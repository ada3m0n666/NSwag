﻿//-----------------------------------------------------------------------
// <copyright file="AspNetCoreToSwaggerGenerator.cs" company="NSwag">
//     Copyright (c) Rico Suter. All rights reserved.
// </copyright>
// <license>https://github.com/NSwag/NSwag/blob/master/LICENSE.md</license>
// <author>Rico Suter, mail@rsuter.com</author>
//-----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Namotion.Reflection;
using Newtonsoft.Json;
using NJsonSchema;
using NSwag.SwaggerGeneration.Processors;
using NSwag.SwaggerGeneration.Processors.Contexts;

namespace NSwag.SwaggerGeneration.AspNetCore
{
    /// <summary>Generates a <see cref="OpenApiDocument"/> using <see cref="ApiDescription"/>. </summary>
    public class AspNetCoreOpenApiDocumentGenerator : IOpenApiDocumentGenerator
    {
        /// <summary>Initializes a new instance of the <see cref="AspNetCoreOpenApiDocumentGenerator" /> class.</summary>
        /// <param name="settings">The settings.</param>
        public AspNetCoreOpenApiDocumentGenerator(AspNetCoreOpenApiDocumentGeneratorSettings settings)
        {
            Settings = settings;
        }

        /// <summary>Gets the generator settings.</summary>
        public AspNetCoreOpenApiDocumentGeneratorSettings Settings { get; }

        /// <summary>Generates the <see cref="OpenApiDocument"/> with services from the given service provider.</summary>
        /// <param name="serviceProvider">The service provider.</param>
        /// <returns>The document</returns>
        public async Task<OpenApiDocument> GenerateAsync(object serviceProvider)
        {
            var typedServiceProvider = (IServiceProvider)serviceProvider;

            var mvcOptions = typedServiceProvider.GetRequiredService<IOptions<MvcOptions>>();
            var settings = GetJsonSerializerSettings(typedServiceProvider);

            Settings.ApplySettings(settings, mvcOptions.Value);

            var apiDescriptionGroupCollectionProvider = typedServiceProvider.GetRequiredService<IApiDescriptionGroupCollectionProvider>();
            return await GenerateAsync(apiDescriptionGroupCollectionProvider.ApiDescriptionGroups);
        }

        /// <summary>Loads the <see cref="GetJsonSerializerSettings"/> from the given service provider.</summary>
        /// <param name="serviceProvider">The service provider.</param>
        /// <returns>The settings.</returns>
        public static JsonSerializerSettings GetJsonSerializerSettings(IServiceProvider serviceProvider)
        {
            dynamic options;
            try
            {
                options = new Func<dynamic>(() => serviceProvider?.GetRequiredService(typeof(IOptions<MvcJsonOptions>)) as dynamic)();
            }
            catch
            {
                // Try load ASP.NET Core 3 options
                var optionsAssembly = Assembly.Load(new AssemblyName("Microsoft.AspNetCore.Mvc.NewtonsoftJson"));
                var optionsType = typeof(IOptions<>).MakeGenericType(optionsAssembly.GetType("Microsoft.AspNetCore.Mvc.MvcNewtonsoftJsonOptions", true));
                options = serviceProvider?.GetRequiredService(optionsType) as dynamic;
            }

            var settings = (JsonSerializerSettings)options?.Value?.SerializerSettings ?? JsonConvert.DefaultSettings?.Invoke();
            return settings;
        }

        /// <summary>Generates a Swagger specification for the given <see cref="ApiDescriptionGroupCollection"/>.</summary>
        /// <param name="apiDescriptionGroups">The <see cref="ApiDescriptionGroupCollection"/>.</param>
        /// <returns>The <see cref="OpenApiDocument" />.</returns>
        /// <exception cref="InvalidOperationException">The operation has more than one body parameter.</exception>
        public async Task<OpenApiDocument> GenerateAsync(ApiDescriptionGroupCollection apiDescriptionGroups)
        {
            var apiDescriptions = apiDescriptionGroups.Items
                .Where(group =>
                    Settings.ApiGroupNames == null ||
                    Settings.ApiGroupNames.Length == 0 ||
                    Settings.ApiGroupNames.Contains(group.GroupName))
                .SelectMany(g => g.Items)
                .Where(apiDescription => apiDescription.ActionDescriptor is ControllerActionDescriptor)
                .ToArray();

            var document = await CreateDocumentAsync().ConfigureAwait(false);
            var schemaResolver = new OpenApiSchemaResolver(document, Settings);

            var apiGroups = apiDescriptions
                .Select(apiDescription => new Tuple<ApiDescription, ControllerActionDescriptor>(apiDescription, (ControllerActionDescriptor)apiDescription.ActionDescriptor))
                .GroupBy(item => item.Item2.ControllerTypeInfo.AsType())
                .ToArray();

            var usedControllerTypes = await GenerateForControllersAsync(document, apiGroups, schemaResolver).ConfigureAwait(false);

            document.GenerateOperationIds();

            var controllerTypes = apiGroups.Select(k => k.Key).ToArray();
            foreach (var processor in Settings.DocumentProcessors)
            {
                await processor.ProcessAsync(new DocumentProcessorContext(document, controllerTypes, usedControllerTypes, schemaResolver, Settings.SchemaGenerator, Settings));
            }

            Settings.PostProcess?.Invoke(document);
            return document;
        }

        private async Task<List<Type>> GenerateForControllersAsync(
            OpenApiDocument document,
            IGrouping<Type, Tuple<ApiDescription, ControllerActionDescriptor>>[] apiGroups,
            OpenApiSchemaResolver schemaResolver)
        {
            var usedControllerTypes = new List<Type>();
            var swaggerGenerator = new OpenApiDocumentGenerator(Settings, schemaResolver);

            var allOperations = new List<Tuple<OpenApiOperationDescription, ApiDescription, MethodInfo>>();
            foreach (var controllerApiDescriptionGroup in apiGroups)
            {
                var controllerType = controllerApiDescriptionGroup.Key;

                var hasIgnoreAttribute = controllerType.GetTypeInfo()
                    .GetCustomAttributes()
                    .GetAssignableToTypeName("SwaggerIgnoreAttribute", TypeNameStyle.Name)
                    .Any();

                if (!hasIgnoreAttribute)
                {
                    var operations = new List<Tuple<OpenApiOperationDescription, ApiDescription, MethodInfo>>();
                    foreach (var item in controllerApiDescriptionGroup)
                    {
                        var apiDescription = item.Item1;
                        var method = item.Item2.MethodInfo;

                        var actionHasIgnoreAttribute = method.GetCustomAttributes().GetAssignableToTypeName("SwaggerIgnoreAttribute", TypeNameStyle.Name).Any();
                        if (actionHasIgnoreAttribute)
                        {
                            continue;
                        }

                        var path = apiDescription.RelativePath;
                        if (!path.StartsWith("/", StringComparison.Ordinal))
                        {
                            path = "/" + path;
                        }

                        var controllerActionDescriptor = (ControllerActionDescriptor)apiDescription.ActionDescriptor;
                        var httpMethod = apiDescription.HttpMethod?.ToLowerInvariant() ?? OpenApiOperationMethod.Get;

                        var operationDescription = new OpenApiOperationDescription
                        {
                            Path = path,
                            Method = httpMethod,
                            Operation = new OpenApiOperation
                            {
                                IsDeprecated = method.GetCustomAttribute<ObsoleteAttribute>() != null,
                                OperationId = GetOperationId(document, controllerActionDescriptor, method),
                                Consumes = apiDescription.SupportedRequestFormats
                                   .Select(f => f.MediaType)
                                   .Distinct()
                                   .ToList(),
                                Produces = apiDescription.SupportedResponseTypes
                                   .SelectMany(t => t.ApiResponseFormats.Select(f => f.MediaType))
                                   .Distinct()
                                   .ToList()
                            }
                        };

                        operations.Add(new Tuple<OpenApiOperationDescription, ApiDescription, MethodInfo>(operationDescription, apiDescription, method));
                    }

                    var addedOperations = await AddOperationDescriptionsToDocumentAsync(document, controllerType, operations, swaggerGenerator, schemaResolver).ConfigureAwait(false);
                    if (addedOperations.Any())
                    {
                        usedControllerTypes.Add(controllerApiDescriptionGroup.Key);
                    }

                    allOperations.AddRange(addedOperations);
                }
            }

            UpdateConsumesAndProduces(document, allOperations);
            return usedControllerTypes;
        }

        private async Task<List<Tuple<OpenApiOperationDescription, ApiDescription, MethodInfo>>> AddOperationDescriptionsToDocumentAsync(
            OpenApiDocument document, Type controllerType,
            List<Tuple<OpenApiOperationDescription, ApiDescription, MethodInfo>> operations,
            OpenApiDocumentGenerator swaggerGenerator, OpenApiSchemaResolver schemaResolver)
        {
            var addedOperations = new List<Tuple<OpenApiOperationDescription, ApiDescription, MethodInfo>>();
            var allOperations = operations.Select(t => t.Item1).ToList();
            foreach (var tuple in operations)
            {
                var operation = tuple.Item1;
                var apiDescription = tuple.Item2;
                var method = tuple.Item3;

                var addOperation = await RunOperationProcessorsAsync(document, apiDescription, controllerType, method, operation,
                    allOperations, swaggerGenerator, schemaResolver).ConfigureAwait(false);
                if (addOperation)
                {
                    var path = operation.Path.Replace("//", "/");
                    if (!document.Paths.ContainsKey(path))
                    {
                        document.Paths[path] = new OpenApiPathItem();
                    }

                    if (document.Paths[path].ContainsKey(operation.Method))
                    {
                        throw new InvalidOperationException($"The method '{operation.Method}' on path '{path}' is registered multiple times.");
                    }

                    document.Paths[path][operation.Method] = operation.Operation;
                    addedOperations.Add(tuple);
                }
            }

            return addedOperations;
        }

        private void UpdateConsumesAndProduces(OpenApiDocument document,
            List<Tuple<OpenApiOperationDescription, ApiDescription, MethodInfo>> allOperations)
        {
            // TODO: Move to SwaggerGenerator class?

            document.Consumes = allOperations
                .SelectMany(s => s.Item1.Operation.Consumes)
                .Where(m => allOperations.All(o => o.Item1.Operation.Consumes.Contains(m)))
                .Distinct()
                .ToArray();

            document.Produces = allOperations
                .SelectMany(s => s.Item1.Operation.Produces)
                .Where(m => allOperations.All(o => o.Item1.Operation.Produces.Contains(m)))
                .Distinct()
                .ToArray();

            foreach (var tuple in allOperations)
            {
                var consumes = tuple.Item1.Operation.Consumes.Distinct().ToArray();
                tuple.Item1.Operation.Consumes = consumes.Any(c => !document.Consumes.Contains(c)) ? consumes.ToList() : null;

                var produces = tuple.Item1.Operation.Produces.Distinct().ToArray();
                tuple.Item1.Operation.Produces = produces.Any(c => !document.Produces.Contains(c)) ? produces.ToList() : null;
            }
        }

        private async Task<OpenApiDocument> CreateDocumentAsync()
        {
            var document = !string.IsNullOrEmpty(Settings.DocumentTemplate) ?
                await OpenApiDocument.FromJsonAsync(Settings.DocumentTemplate).ConfigureAwait(false) :
                new OpenApiDocument();

            document.Generator = $"NSwag v{OpenApiDocument.ToolchainVersion} (NJsonSchema v{JsonSchema.ToolchainVersion})";
            document.SchemaType = Settings.SchemaType;

            if (document.Info == null)
            {
                document.Info = new OpenApiInfo();
            }

            if (string.IsNullOrEmpty(Settings.DocumentTemplate))
            {
                if (!string.IsNullOrEmpty(Settings.Title))
                {
                    document.Info.Title = Settings.Title;
                }

                if (!string.IsNullOrEmpty(Settings.Description))
                {
                    document.Info.Description = Settings.Description;
                }

                if (!string.IsNullOrEmpty(Settings.Version))
                {
                    document.Info.Version = Settings.Version;
                }
            }

            return document;
        }

        private async Task<bool> RunOperationProcessorsAsync(OpenApiDocument document, ApiDescription apiDescription, Type controllerType, MethodInfo methodInfo, OpenApiOperationDescription operationDescription, List<OpenApiOperationDescription> allOperations, OpenApiDocumentGenerator swaggerGenerator, OpenApiSchemaResolver schemaResolver)
        {
            // 1. Run from settings
            var operationProcessorContext = new AspNetCoreOperationProcessorContext(document, operationDescription, controllerType, methodInfo, swaggerGenerator, Settings.SchemaGenerator, schemaResolver, Settings, allOperations)
            {
                ApiDescription = apiDescription,
            };

            foreach (var operationProcessor in Settings.OperationProcessors)
            {
                if (await operationProcessor.ProcessAsync(operationProcessorContext).ConfigureAwait(false) == false)
                {
                    return false;
                }
            }

            // 2. Run from class attributes
            var operationProcessorAttribute = methodInfo.DeclaringType.GetTypeInfo()
                .GetCustomAttributes()
            // 3. Run from method attributes
                .Concat(methodInfo.GetCustomAttributes())
                .Where(a => a.GetType().IsAssignableToTypeName("SwaggerOperationProcessorAttribute", TypeNameStyle.Name));

            foreach (dynamic attribute in operationProcessorAttribute)
            {
                var operationProcessor = ObjectExtensions.HasProperty(attribute, "Parameters") ?
                    (IOperationProcessor)Activator.CreateInstance(attribute.Type, attribute.Parameters) :
                    (IOperationProcessor)Activator.CreateInstance(attribute.Type);

                if (await operationProcessor.ProcessAsync(operationProcessorContext) == false)
                {
                    return false;
                }
            }

            return true;
        }

        private string GetOperationId(OpenApiDocument document, ControllerActionDescriptor actionDescriptor, MethodInfo method)
        {
            string operationId;

            dynamic swaggerOperationAttribute = method
                .GetCustomAttributes()
                .FirstAssignableToTypeNameOrDefault("SwaggerOperationAttribute", TypeNameStyle.Name);

            if (swaggerOperationAttribute != null && !string.IsNullOrEmpty(swaggerOperationAttribute.OperationId))
            {
                operationId = swaggerOperationAttribute.OperationId;
            }
            else
            {
                operationId = actionDescriptor.ControllerName + "_" + GetActionName(actionDescriptor.ActionName);
            }

            var number = 1;
            while (document.Operations.Any(o => o.Operation.OperationId == operationId + (number > 1 ? "_" + number : string.Empty)))
            {
                number++;
            }

            return operationId + (number > 1 ? number.ToString() : string.Empty);
        }

        private static string GetActionName(string actionName)
        {
            if (actionName.EndsWith("Async"))
            {
                actionName = actionName.Substring(0, actionName.Length - 5);
            }

            return actionName;
        }
    }
}