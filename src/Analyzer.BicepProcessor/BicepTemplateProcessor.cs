﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Threading.Tasks;
using Bicep.Core.Analyzers.Linter;
using Bicep.Core.Configuration;
using Bicep.Core.Diagnostics;
using Bicep.Core.Emit;
using Bicep.Core.Features;
using Bicep.Core.FileSystem;
using Bicep.Core.Registry;
using Bicep.Core.Registry.Auth;
using Bicep.Core.Semantics;
using Bicep.Core.Semantics.Namespaces;
using Bicep.Core.TypeSystem.Az;
using Bicep.Core.Workspaces;

namespace Microsoft.Azure.Templates.Analyzer.BicepProcessor
{
    /// <summary>
    /// Contains functionality to process Bicep templates.
    /// </summary>
    public class BicepTemplateProcessor
    {
        private static readonly IConfigurationManager configurationManager = new ConfigurationManager(new FileSystem());
        private static readonly IFileResolver fileResolver = new FileResolver();
        private static readonly IFeatureProvider featureProvider = new FeatureProvider();

        private static readonly EmitterSettings emitterSettings = new(featureProvider);
        private static readonly IModuleDispatcher moduleDispatcher = new ModuleDispatcher(
            new DefaultModuleRegistryProvider(
                fileResolver,
                new ContainerRegistryClientFactory(new TokenCredentialFactory()),
                new TemplateSpecRepositoryFactory(new TokenCredentialFactory()),
                featureProvider));
        private static readonly INamespaceProvider namespaceProvider = new DefaultNamespaceProvider(new AzResourceTypeLoader(), featureProvider);

        /// <summary>
        /// Converts Bicep template into JSON template and returns it as a string and its source map
        /// </summary>
        /// <param name="bicepPath">The Bicep template file path.</param>
        /// <returns>The compiled template as a <c>JSON</c> string and its source map.</returns>
        public static (string, SourceMap) ConvertBicepToJson(string bicepPath)
        {
            using var stringWriter = new StringWriter();

            Environment.SetEnvironmentVariable("BICEP_SOURCEMAPPING_ENABLED", "true");

            var configuration = configurationManager.GetConfiguration(new Uri(bicepPath));
            var workspace = new Workspace();
            var sourceFileGrouping = SourceFileGroupingBuilder.Build(fileResolver, moduleDispatcher, workspace, PathHelper.FilePathToFileUrl(bicepPath), configuration);

            // pull modules optimistically
            if (moduleDispatcher.RestoreModules(configuration, moduleDispatcher.GetValidModuleReferences(sourceFileGrouping.ModulesToRestore, configuration)).Result)
            {
                // modules had to be restored - recompile
                sourceFileGrouping = SourceFileGroupingBuilder.Rebuild(moduleDispatcher, workspace, sourceFileGrouping, configuration);
            }

            var compilation = new Compilation(featureProvider, namespaceProvider, sourceFileGrouping, configuration, new LinterAnalyzer(configuration));
            var emitter = new TemplateEmitter(compilation.GetEntrypointSemanticModel(), emitterSettings);
            var emitResult = emitter.Emit(stringWriter);

            if (emitResult.Status == EmitStatus.Failed)
            {
                var bicepIssues = emitResult.Diagnostics
                    .Where(diag => diag.Level == DiagnosticLevel.Error)
                    .Select(diag => diag.Message);
                throw new Exception($"Bicep issues found:{Environment.NewLine}{string.Join(Environment.NewLine, bicepIssues)}");
            }

            return (stringWriter.ToString(), emitResult.SourceMap);
        }
    }
}
