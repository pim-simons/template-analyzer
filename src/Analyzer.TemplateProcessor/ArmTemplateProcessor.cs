﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using Azure.Deployments.Core.Collections;
using Azure.Deployments.Core.Extensions;
using Azure.Deployments.Core.Resources;
using Azure.Deployments.Expression.Engines;
using Azure.Deployments.Templates.Configuration;
using Azure.Deployments.Templates.Engines;
using Azure.Deployments.Templates.Expressions;
using Azure.Deployments.Templates.Extensions;
using Azure.Deployments.Templates.Schema;
using Microsoft.Azure.Templates.Analyzer.Utilities;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Microsoft.Azure.Templates.Analyzer.TemplateProcessor
{
    /// <summary>
    /// Contains functionality to process all language expressions in ARM templates. 
    /// Generates placeholder values when parameter values are not provided.
    /// </summary>
    public class ArmTemplateProcessor
    {
        private readonly string armTemplate;
        private readonly string apiVersion;
        private Dictionary<string, List<string>> originalToExpandedMapping = new Dictionary<string, List<string>>();
        private Dictionary<string, TemplateResource> flattenedResources = new Dictionary<string, TemplateResource>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Mapping between resources in the expanded template to their original resource in 
        /// the original template. Used to get line numbers.
        /// The key is the path in the expanded template with value being the path
        /// in the original template.
        /// </summary>
        public Dictionary<string, string> ResourceMappings = new Dictionary<string, string>();

        /// <summary>
        ///  Constructor for the ARM Template Processing library
        /// </summary>
        /// <param name="armTemplate">The ARM Template <c>JSON</c>. Must follow this schema: https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#</param>
        /// <param name="apiVersion">The deployment API version. Must be a valid version from the deploymetns list here: https://docs.microsoft.com/en-us/azure/templates/microsoft.resources/allversions</param>
        public ArmTemplateProcessor(string armTemplate, string apiVersion = "2020-01-01")
        {
            this.armTemplate = armTemplate;
            this.apiVersion = apiVersion;

            AnalyzerDeploymentsInterop.Initialize();
        }

        /// <summary>
        /// Processes the ARM template with placeholder parameters and deployment metadata.
        /// </summary>
        /// <returns>The processed template as a <c>JSON</c> object.</returns>
        public JToken ProcessTemplate()
        {
            return ProcessTemplate(null, null);
        }

        /// <summary>
        /// Processes the ARM template with provided parameters and placeholder deployment metadata.
        /// </summary>
        /// <param name="parameters">The template parameters and their values <c>JSON</c>. Must follow this schema: https://schema.management.azure.com/schemas/2015-01-01/deploymentParameters.json#</param>
        /// <returns>The processed template as a <c>JSON</c> object.</returns>
        public JToken ProcessTemplate(string parameters)
        {
            return ProcessTemplate(parameters, null);
        }

        /// <summary>
        /// Processes the ARM template with provided parameters and deployment metadata.
        /// </summary>
        /// <param name="parameters">The template parameters and their values <c>JSON</c>. Must follow this schema: https://schema.management.azure.com/schemas/2015-01-01/deploymentParameters.json#</param>
        /// <param name="metadata">The deployment metadata <c>JSON</c>.</param>
        /// <returns>The processed template as a <c>JSON</c> object.</returns>
        public JToken ProcessTemplate(string parameters, string metadata)
        {
            InsensitiveDictionary<JToken> parametersDictionary = PopulateParameters(string.IsNullOrEmpty(parameters) ? PlaceholderInputGenerator.GeneratePlaceholderParameters(armTemplate) : parameters);
            InsensitiveDictionary<JToken> metadataDictionary = string.IsNullOrEmpty(metadata) ? PlaceholderInputGenerator.GeneratePlaceholderDeploymentMetadata() : PopulateDeploymentMetadata(metadata);

            var template = ParseAndValidateTemplate(parametersDictionary, metadataDictionary);

            return template.ToJToken();
        }

        /// <summary>
        /// Parses and validates the template.
        /// </summary>
        /// <param name="parameters">The template parameters</param>
        /// <param name="metadata">The deployment metadata</param>
        /// <returns>The processed template as a Template object.</returns>
        internal Template ParseAndValidateTemplate(InsensitiveDictionary<JToken> parameters, InsensitiveDictionary<JToken> metadata)
        {
            Dictionary<string, (string, int)> copyNameMap = new Dictionary<string, (string, int)>();

            Template template = TemplateEngine.ParseTemplate(armTemplate);

            TemplateEngine.ValidateTemplate(template, apiVersion, TemplateDeploymentScope.NotSpecified);

            TemplateEngine.ParameterizeTemplate(
                inputParameters: parameters,
                template: template,
                metadata: metadata);

            SetOriginalResourceNames(template);

            // If there are resources using copies, the original resource will
            // be removed from template.Resources and the copies will be added instead,
            // to the end of the array. This means that OriginalName will be lost
            // in the resource and will be out of order.
            // To work around this, build a map of copy name to OriginalName and index
            // so OriginalName can be updated and the order fixed after copies are finished
            for (int i = 0; i < template.Resources.Length; i++)
            {
                var resource = template.Resources[i];
                if (resource.Copy != null) copyNameMap[resource.Copy.Name.Value] = (resource.OriginalName, i);
            }

            try
            {
                TemplateEngine.ProcessTemplateLanguageExpressions(template, apiVersion);
            }
            catch
            {
                // Do not throw if there was an issue with evaluating language expressions
            }

            MapResources(template, copyNameMap);

            TemplateEngine.ValidateProcessedTemplate(template, apiVersion, TemplateDeploymentScope.NotSpecified);

            template = ProcessResourcesAndOutputs(template);

            return template;
        }

        /// <summary>
        /// Processes each resource for language expressions and parent resources as well
        /// as processes language expressions for outputs.
        /// </summary>
        /// <param name="template">Template being processed.</param>
        /// <returns>Template after processing resources and outputs.</returns>
        internal Template ProcessResourcesAndOutputs(Template template)
        {
            var evaluationHelper = GetTemplateFunctionEvaluationHelper(template);
            SaveFlattenedResources(template.Resources);

            foreach (var resourcePair in flattenedResources)
            {
                var resource = resourcePair.Value;

                ProcessTemplateResourceLanguageExpressions(resource, evaluationHelper);

                CopyResourceDependants(resource, flattenedResources);

                if (!ResourceMappings.ContainsKey(resource.Path))
                {
                    AddResourceMapping(resource.Path, resource.Path);
                }
            }

            if ((template.Outputs?.Count ?? 0) > 0)
            {
                // Recreate evaluation helper with newly parsed properties
                evaluationHelper = GetTemplateFunctionEvaluationHelper(template);

                foreach (var outputKey in template.Outputs.Keys.ToList())
                {
                    try
                    {
                        template.Outputs[outputKey].Value.Value = ExpressionsEngine.EvaluateLanguageExpressionsRecursive(
                            root: template.Outputs[outputKey].Value.Value,
                            evaluationContext: evaluationHelper.EvaluationContext);
                    }
                    catch (Exception)
                    {
                        template.Outputs[outputKey].Value.Value = new JValue("NOT_PARSED");
                    }
                }
            }

            return template;
        }

        /// <summary>
        /// Copies child resources into sub resources for parent resources.
        /// </summary>
        /// <param name="templateResource">The child resource.</param>
        /// <param name="resources">A collection of all resources with a mapping to their processed name.</param>
        internal void CopyResourceDependants(TemplateResource templateResource, Dictionary<string, TemplateResource> resources)
        {
            if (templateResource.DependsOn == null)
            {
                return;
            }

            foreach (var parentResourceIds in templateResource.DependsOn)
            {
                string parentResourceName;
                TemplateResource parentResource = null;
                // If the dependsOn references the resourceId
                if (parentResourceIds.Value.StartsWith("/subscriptions"))
                {
                    string parentResourceId = IResourceIdentifiableExtensions.GetUnqualifiedResourceId(parentResourceIds.Value);
                    parentResourceName = IResourceIdentifiableExtensions.GetResourceName(parentResourceId);
                    string parentResourceType = IResourceIdentifiableExtensions.GetFullyQualifiedResourceType(parentResourceId);

                    resources.TryGetValue($"{parentResourceName} {parentResourceType}", out parentResource);
                }
                // If the dependsOn references the resource name
                else
                {
                    parentResourceName = parentResourceIds.Value;
                    var matchingResources = resources.Where(k => k.Key.StartsWith($"{parentResourceName} ", StringComparison.OrdinalIgnoreCase)).ToList();
                    if (matchingResources.Count == 1)
                    {
                        parentResource = matchingResources.First().Value;
                    }
                }

                // Parent resouce is not in the template
                if (parentResource == null)
                {
                    continue;
                }

                // Add this resource as a child of its parent resource
                if (parentResource.Resources == null)
                {
                    parentResource.Resources = new TemplateResource[] { templateResource };

                    AddResourceMapping($"{parentResource.Path}.resources[0]", templateResource.Path);
                }
                else
                {
                    var childResources = parentResource.Resources;
                    parentResource.Resources = childResources.ConcatArray(new TemplateResource[] { templateResource });
                    int resourceIndex = parentResource.Resources.Length - 1;

                    AddResourceMapping($"{parentResource.Path}.resources[{resourceIndex}]", templateResource.Path);
                }
            }

            return;
        }

        private void AddResourceMapping(string expandedTemplatePath, string originalTemplatePath)
        {
            // Save all permutations of the resource path based off values already present 
            // in the dictionary with mapping. This is necessary to report an issue in
            // a copied nth grandchild resource.
            var tokens = expandedTemplatePath.Split('.');

            for (int i = 0; i < tokens.Length - 1; i++)
            {
                string possibleOriginalPathOfAnotherResource = string.Join('.', tokens[..(i + 1)]);
                if (originalToExpandedMapping.TryGetValue(possibleOriginalPathOfAnotherResource, out List<string> copiedLocationsOfAnotherResource))
                {
                    foreach (string copiedLocationOfAnotherResource in copiedLocationsOfAnotherResource)
                    {
                        if (!copiedLocationOfAnotherResource.Equals(possibleOriginalPathOfAnotherResource, StringComparison.OrdinalIgnoreCase))
                        {
                            ResourceMappings.TryAdd($"{copiedLocationOfAnotherResource}.{string.Join('.', tokens[(i + 1)..])}", originalTemplatePath);
                        }
                    }
                }
            }

            if (!ResourceMappings.TryAdd(expandedTemplatePath, originalTemplatePath))
            {
                throw new Exception($"Error mapping resource at path {originalTemplatePath}");
            }

            if(!originalToExpandedMapping.TryAdd(originalTemplatePath, new List<string> { expandedTemplatePath }))
            {
                originalToExpandedMapping[originalTemplatePath].Add(expandedTemplatePath);
            }
        }

        /// <summary>
        /// Flattens resources that are defined inside other resources.
        /// </summary>
        /// <param name="resources">Resources in the template.</param>
        /// <param name="parentName">Name of the parent resource. Used during recursive call.</param>
        /// <param name="parentType">Type of the parent resource. Used during recursive call.</param>
        private void SaveFlattenedResources(TemplateResource[] resources, string parentName = null, string parentType = null)
        {
            for (int i = 0; i < resources.Length; i++)
            {
                string dictionaryKey;
                var resource = resources[i];

                if (parentName != null && parentType != null)
                {
                    resource.Path = $"{flattenedResources[$"{parentName} {parentType}"].Path}.resources[{i}]";

                    dictionaryKey = $"{parentName}/{resource.Name.Value} {parentType}/{resource.Type.Value}";
                }
                else
                {
                    resource.Path = $"resources[{i}]";

                    dictionaryKey = $"{resource.Name.Value} {resource.Type.Value}";
                }

                flattenedResources.Add(dictionaryKey, resource);

                if (resource.Resources != null)
                {
                    string resourceNamePrefix = parentName == null ? "" : $"{parentName}/";
                    string resourceTypePrefix = parentType == null ? "" : $"{parentType}/";

                    SaveFlattenedResources(resource.Resources, $"{resourceNamePrefix}{resource.Name.Value}", $"{resourceTypePrefix}{resource.Type.Value}");
                }
            }
        }

        /// <summary>
        /// Processes language expressions in the properties property of the resources.
        /// </summary>
        /// <param name="templateResource">The template resource to process language expressions for.</param>
        /// <param name="evaluationHelper">Evaluation helper to evaluate expressions</param>
        private void ProcessTemplateResourceLanguageExpressions(TemplateResource templateResource, TemplateExpressionEvaluationHelper evaluationHelper)
        {
            try
            {
                if (templateResource.Properties != null)
                {
                    evaluationHelper.OnGetCopyContext = () => templateResource.CopyContext;
                    templateResource.Properties.Value = ExpressionsEngine.EvaluateLanguageExpressionsRecursive(
                        root: templateResource.Properties.Value,
                        evaluationContext: evaluationHelper.EvaluationContext);
                }
            }
            catch
            {
                // Do not throw if there was an issue with evaluating language expressions
                return;
            }

            return;
        }

        /// <summary>
        /// Gets the template expression evaluation helper.
        /// </summary>
        /// <param name="template">The template.</param>
        /// <returns>The template expression evaluation helper</returns>
        private TemplateExpressionEvaluationHelper GetTemplateFunctionEvaluationHelper(Template template)
        {
            var helper = new TemplateExpressionEvaluationHelper();

            var functionsLookup = template.GetFunctionDefinitions().ToInsensitiveDictionary(keySelector: function => function.Key, elementSelector: function => function.Function);

            var parametersLookup = template.Parameters.CoalesceEnumerable().ToInsensitiveDictionary(
                keySelector: parameter => parameter.Key,
                elementSelector: parameter => parameter.Value.Value);

            var variablesLookup = template.Variables.CoalesceEnumerable().ToInsensitiveDictionary(
                keySelector: variable => variable.Key,
                elementSelector: variable => variable.Value);

            helper.Initialize(
                metadata: template.Metadata,
                functionsLookup: functionsLookup,
                parametersLookup: parametersLookup,
                variablesLookup: variablesLookup);

            // Set reference lookup
            helper.OnGetTemplateReference = (TemplateReference templateReference) =>
            {
                foreach (var resource in template.Resources)
                {
                    if (new[] { resource.Name.Value, resource.OriginalName }.Contains(templateReference.ResourceId))
                    {
                        return resource.Properties?.Value;
                    }
                }
                return ExpressionsEngine.EvaluateLanguageExpressionsRecursive(
                    root: templateReference.ResourceId,
                    evaluationContext: helper.EvaluationContext);
            };

            return helper;
        }

        /// <summary>
        /// Maps the resources to their original location.
        /// </summary>
        /// <param name="template">The template</param>
        /// <param name="copyNameMap">Mapping of the copy name, the original name of the resource, and index of resource in resource list.</param>
        private void MapResources(Template template, Dictionary<string, (string, int)> copyNameMap)
        {
            // Set OriginalName back on resources that were copied and map them
            // to their original resource
            for (int i = 0; i < template.Resources.Length; i++)
            {
                var resource = template.Resources[i];
                if (resource.Copy != null)
                {
                    // Copied resource.  Update OriginalName and
                    // add maping to original resource
                    if (copyNameMap.TryGetValue(resource.Copy.Name.Value, out (string, int) originalValues))
                    {
                        resource.OriginalName = originalValues.Item1;

                        resource.Path = $"resources[{i}]";
                        AddResourceMapping(resource.Path, $"resources[{originalValues.Item2}]");

                        continue;
                    }
                }
                else
                {
                    AddResourceMapping($"resources[{i}]", resource.Path);
                }
            }
            
            return;
        }

        /// <summary>
        /// Set the original name property for each resource before processing language expressions in the template.
        /// This is used to help map to the original resource after processing.
        /// </summary>
        /// <param name="template">The template</param>
        private void SetOriginalResourceNames(Template template)
        {
            foreach (var resource in template.Resources)
            {
                resource.OriginalName = resource.Name.Value;
            }
        }

        /// <summary>
        /// Populates the deployment metadata data object.
        /// </summary>
        /// <param name="metadata">The deployment metadata <c>JSON</c>.</param>
        /// <returns>A dictionary with the metadata.</returns>
        internal InsensitiveDictionary<JToken> PopulateDeploymentMetadata(string metadata)
        {
            try
            {
                var metadataAsJObject = JObject.Parse(metadata);
                InsensitiveDictionary<JToken> metadataDictionary = new InsensitiveDictionary<JToken>();

                foreach (var property in metadataAsJObject.Properties())
                {
                    metadataDictionary.Add(property.Name, property.Value.ToObject<JToken>());
                }

                return metadataDictionary;
            }
            catch (JsonReaderException ex)
            {
                throw new Exception($"Error parsing metadata: {ex}");
            }
            catch (Exception ex)
            {
                throw new Exception($"Error populating metadata: {ex}");
            }
        }

        /// <summary>
        /// Populates the parameters data object.
        /// </summary>
        /// <param name="parameters">The required input parameters and their values <c>JSON</c>.</param>
        /// <returns>A dictionary with required parameters.</returns>
        internal InsensitiveDictionary<JToken> PopulateParameters(string parameters)
        {
            // Create the minimum parameters needed
            JObject parametersObject = JObject.Parse(parameters);
            InsensitiveDictionary<JToken> parametersDictionary = new InsensitiveDictionary<JToken>();

            if (parametersObject["parameters"] == null)
            {
                throw new Exception("Parameteres property is not specified in the ARM Template parameters provided. Please ensure ARM Template parameters follows the following JSON schema https://schema.management.azure.com/schemas/2015-01-01/deploymentParameters.json#");
            }

            foreach (var parameter in parametersObject.InsensitiveToken("parameters").Value<JObject>()?.Properties() ?? Enumerable.Empty<JProperty>())
            {
                JToken parameterValueAsJToken = parameter.Value.ToObject<JObject>().Property("value")?.Value;

                // See if "reference" was specified instead of "value"
                bool isReference = false;
                if (parameterValueAsJToken == null)
                {
                    parameterValueAsJToken = parameter.Value.ToObject<JObject>().Property("reference")?.Value;
                    if (parameterValueAsJToken != null) isReference = true;
                }

                parametersDictionary.Add(parameter.Name, isReference ? $"REF_NOT_AVAIL_{parameter.Name}" : parameterValueAsJToken ?? string.Empty);
            }

            return parametersDictionary;
        }
    }
}
