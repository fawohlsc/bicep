﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Azure.Deployments.Expression.Expressions;
using Bicep.Core.Diagnostics;
using Bicep.Core.Extensions;
using Bicep.Core.Parsing;
using Bicep.Core.Semantics;
using Bicep.Core.Syntax;
using Bicep.Core.TypeSystem;
using Bicep.Core.TypeSystem.Az;

namespace Bicep.Core.Emit
{
    public static class ScopeHelper
    {
        public class ScopeData
        {
            public ResourceScope RequestedScope { get; set; }

            public SyntaxBase? ManagementGroupNameProperty { get; set; }

            public SyntaxBase? SubscriptionIdProperty { get; set; }

            public SyntaxBase? ResourceGroupProperty { get; set; }

            public ResourceSymbol? ResourceScopeSymbol { get; set; }
        }

        public delegate void LogInvalidScopeDiagnostic(IPositionable positionable, ResourceScope suppliedScope, ResourceScope supportedScopes);

        private static ScopeData? ValidateScope(SemanticModel semanticModel, LogInvalidScopeDiagnostic logInvalidScopeFunc, ResourceScope supportedScopes, SyntaxBase bodySyntax, ObjectPropertySyntax? scopeProperty)
        {
            if (scopeProperty is null)
            {
                // no scope provided - use the target scope for the file
                if (!supportedScopes.HasFlag(semanticModel.TargetScope))
                {
                    logInvalidScopeFunc(bodySyntax, semanticModel.TargetScope, supportedScopes);
                    return null;
                }

                return null;
            }

            var scopeSymbol = semanticModel.GetSymbolInfo(scopeProperty.Value);
            var scopeType = semanticModel.GetTypeInfo(scopeProperty.Value);

            switch (scopeType)
            {
                case TenantScopeType type:
                    if (!supportedScopes.HasFlag(ResourceScope.Tenant))
                    {
                        logInvalidScopeFunc(scopeProperty.Value, ResourceScope.Tenant, supportedScopes);
                        return null;
                    }

                    return new ScopeData { RequestedScope = ResourceScope.Tenant };
                case ManagementGroupScopeType type:
                    if (!supportedScopes.HasFlag(ResourceScope.ManagementGroup))
                    {
                        logInvalidScopeFunc(scopeProperty.Value, ResourceScope.ManagementGroup, supportedScopes);
                        return null;
                    }

                    return type.Arguments.Length switch {
                        0 => new ScopeData { RequestedScope = ResourceScope.ManagementGroup },
                        _ => new ScopeData { RequestedScope = ResourceScope.ManagementGroup, ManagementGroupNameProperty = type.Arguments[0].Expression },
                    };
                case SubscriptionScopeType type:
                    if (!supportedScopes.HasFlag(ResourceScope.Subscription))
                    {
                        logInvalidScopeFunc(scopeProperty.Value, ResourceScope.Subscription, supportedScopes);
                        return null;
                    }

                    return type.Arguments.Length switch {
                        0 => new ScopeData { RequestedScope = ResourceScope.Subscription },
                        _ => new ScopeData { RequestedScope = ResourceScope.Subscription, SubscriptionIdProperty = type.Arguments[0].Expression },
                    };
                case ResourceGroupScopeType type:
                    if (!supportedScopes.HasFlag(ResourceScope.ResourceGroup))
                    {
                        logInvalidScopeFunc(scopeProperty.Value, ResourceScope.ResourceGroup, supportedScopes);
                        return null;
                    }

                    return type.Arguments.Length switch {
                        0 => new ScopeData { RequestedScope = ResourceScope.ResourceGroup },
                        1 => new ScopeData { RequestedScope = ResourceScope.ResourceGroup, ResourceGroupProperty = type.Arguments[0].Expression },
                        _ => new ScopeData { RequestedScope = ResourceScope.ResourceGroup, SubscriptionIdProperty = type.Arguments[0].Expression, ResourceGroupProperty = type.Arguments[1].Expression },
                    };
                case {} when scopeSymbol is ResourceSymbol targetResourceSymbol:
                    if (targetResourceSymbol.Type is ResourceType resourceType && 
                        StringComparer.OrdinalIgnoreCase.Equals(resourceType.TypeReference.FullyQualifiedType, AzResourceTypeProvider.ResourceTypeResourceGroup))
                    {
                        // special-case 'Microsoft.Resources/resourceGroups' in order to allow it to create a resourceGroup-scope resource
                        var rgScopeProperty = targetResourceSymbol.SafeGetBodyProperty(LanguageConstants.ResourceScopePropertyName);
                        var rgNameProperty = targetResourceSymbol.SafeGetBodyProperty(LanguageConstants.ResourceNamePropertyName);

                        // ignore diagnostics - these will be collected separately in the pass over resources
                        var hasErrors = false;
                        var rgScopeData = ScopeHelper.ValidateScope(semanticModel, (_, _, _) => { hasErrors = true; }, resourceType.ValidParentScopes, targetResourceSymbol.DeclaringResource.Value, rgScopeProperty);
                        if (rgNameProperty is not null && !hasErrors)
                        {
                            if (!supportedScopes.HasFlag(ResourceScope.ResourceGroup))
                            {
                                logInvalidScopeFunc(scopeProperty.Value, ResourceScope.ResourceGroup, supportedScopes);
                                return null;
                            }

                            return new ScopeData { RequestedScope = ResourceScope.ResourceGroup, SubscriptionIdProperty = rgScopeData?.SubscriptionIdProperty, ResourceGroupProperty = rgNameProperty.Value };
                        }
                    }

                    if (!supportedScopes.HasFlag(ResourceScope.Resource))
                    {
                        logInvalidScopeFunc(scopeProperty.Value, ResourceScope.Resource, supportedScopes);
                        return null;
                    }

                    return new ScopeData { RequestedScope = ResourceScope.Resource, ResourceScopeSymbol = targetResourceSymbol };
            }

            // type validation should have already caught this
            return null;
        }

        public static LanguageExpression FormatFullyQualifiedResourceId(EmitterContext context, ExpressionConverter converter, ScopeData scopeData, string fullyQualifiedType, IEnumerable<LanguageExpression> nameSegments)
        {
            switch (scopeData.RequestedScope)
            {
                case ResourceScope.Tenant:
                    return ExpressionConverter.GenerateTenantResourceId(fullyQualifiedType, nameSegments);
                case ResourceScope.Subscription:
                    var arguments = new List<LanguageExpression>();
                    if (scopeData.SubscriptionIdProperty != null)
                    {
                        arguments.Add(converter.ConvertExpression(scopeData.SubscriptionIdProperty));
                    }
                    arguments.Add(new JTokenExpression(fullyQualifiedType));
                    arguments.AddRange(nameSegments);

                    return new FunctionExpression("subscriptionResourceId", arguments.ToArray(), Array.Empty<LanguageExpression>());
                case ResourceScope.ResourceGroup:
                    // We avoid using the 'resourceId' function at all here, because its behavior differs depending on the scope that it is called FROM.
                    LanguageExpression scope;
                    if (scopeData.SubscriptionIdProperty == null)
                    {
                        if (scopeData.ResourceGroupProperty == null)
                        {
                            return ExpressionConverter.GenerateResourceGroupResourceId(fullyQualifiedType, nameSegments);
                        }
                        else
                        {
                            var subscriptionId = new FunctionExpression("subscription", Array.Empty<LanguageExpression>(), new LanguageExpression[] { new JTokenExpression("subscriptionId") });
                            var resourceGroup = converter.ConvertExpression(scopeData.ResourceGroupProperty);
                            scope = ExpressionConverter.GenerateResourceGroupScope(subscriptionId, resourceGroup);
                        }
                    }
                    else
                    {
                        if (scopeData.ResourceGroupProperty == null)
                        {
                            throw new NotImplementedException($"Cannot format resourceId with non-null subscription and null resourceGroup");
                        }

                        var subscriptionId = converter.ConvertExpression(scopeData.SubscriptionIdProperty);
                        var resourceGroup = converter.ConvertExpression(scopeData.ResourceGroupProperty);
                        scope = ExpressionConverter.GenerateResourceGroupScope(subscriptionId, resourceGroup);
                    }

                    // We've got to DIY it, unfortunately. The resourceId() function behaves differently when used at different scopes, so is unsuitable here.
                    return ExpressionConverter.GenerateExtensionResourceId(scope, fullyQualifiedType, nameSegments);
                case ResourceScope.ManagementGroup:
                    if (scopeData.ManagementGroupNameProperty != null)
                    {
                        var managementGroupScope = converter.GenerateManagementGroupResourceId(scopeData.ManagementGroupNameProperty, true);

                        return ExpressionConverter.GenerateExtensionResourceId(managementGroupScope, fullyQualifiedType, nameSegments);
                    }

                    // We need to do things slightly differently for Management Groups, because there is no IL to output for "Give me a fully-qualified resource id at the current scope",
                    // and we don't even have a mechanism for reliably getting the current scope (e.g. something like 'deployment().scope'). There are plans to add a managementGroupResourceId function,
                    // but until we have it, we should generate unqualified resource Ids. There should not be a risk of collision, because we do not allow mixing of resource scopes in a single bicep file.
                    return ExpressionConverter.GenerateUnqualifiedResourceId(fullyQualifiedType, nameSegments);
                case ResourceScope.Resource:
                    if (scopeData.ResourceScopeSymbol is null)
                    {
                        throw new InvalidOperationException($"Cannot format resourceId with non-null resource scope symbol");
                    }

                    var parentTypeReference = EmitHelpers.GetTypeReference(scopeData.ResourceScopeSymbol);
                    var parentResourceId = FormatFullyQualifiedResourceId(
                        context,
                        converter,
                        context.ResourceScopeData[scopeData.ResourceScopeSymbol],
                        parentTypeReference.FullyQualifiedType,
                        converter.GetResourceNameSegments(scopeData.ResourceScopeSymbol, parentTypeReference));

                    return ExpressionConverter.GenerateExtensionResourceId(
                        parentResourceId,
                        fullyQualifiedType,
                        nameSegments);
                default:
                    throw new InvalidOperationException($"Cannot format resourceId for scope {scopeData.RequestedScope}");
            }
        }

        public static LanguageExpression FormatUnqualifiedResourceId(EmitterContext context, ExpressionConverter converter, ScopeData scopeData, string fullyQualifiedType, IEnumerable<LanguageExpression> nameSegments)
        {
            switch (scopeData.RequestedScope)
            {
                case ResourceScope.Tenant:
                case ResourceScope.Subscription:
                case ResourceScope.ResourceGroup:
                case ResourceScope.ManagementGroup:
                    return ExpressionConverter.GenerateUnqualifiedResourceId(fullyQualifiedType, nameSegments);
                case ResourceScope.Resource:
                    if (scopeData.ResourceScopeSymbol is null)
                    {
                        throw new InvalidOperationException($"Cannot format resourceId with non-null resource scope symbol");
                    }

                    var parentTypeReference = EmitHelpers.GetTypeReference(scopeData.ResourceScopeSymbol);
                    var parentResourceId = FormatUnqualifiedResourceId(
                        context,
                        converter,
                        context.ResourceScopeData[scopeData.ResourceScopeSymbol],
                        parentTypeReference.FullyQualifiedType,
                        converter.GetResourceNameSegments(scopeData.ResourceScopeSymbol, parentTypeReference));

                    return ExpressionConverter.GenerateExtensionResourceId(
                        parentResourceId,
                        fullyQualifiedType,
                        nameSegments);
                default:
                    throw new InvalidOperationException($"Cannot format resourceId for scope {scopeData.RequestedScope}");
            }
        }

        public static void EmitModuleScopeProperties(ResourceScope targetScope, ScopeData scopeData, ExpressionEmitter expressionEmitter)
        {
            switch (scopeData.RequestedScope)
            {
                case ResourceScope.Tenant:
                    expressionEmitter.EmitProperty("scope", new JTokenExpression("/"));
                    return;
                case ResourceScope.ManagementGroup:
                    if (scopeData.ManagementGroupNameProperty != null)
                    {
                        // The template engine expects an unqualified resourceId for the management group scope if deploying at tenant scope
                        var useFullyQualifiedResourceId = targetScope != ResourceScope.Tenant;
                        expressionEmitter.EmitProperty("scope", expressionEmitter.GetManagementGroupResourceId(scopeData.ManagementGroupNameProperty, useFullyQualifiedResourceId));
                    }
                    return;
                case ResourceScope.Subscription:
                    if (scopeData.SubscriptionIdProperty != null)
                    {
                        expressionEmitter.EmitProperty("subscriptionId", scopeData.SubscriptionIdProperty);
                    }
                    else if (targetScope == ResourceScope.ResourceGroup)
                    {
                        expressionEmitter.EmitProperty("subscriptionId", new FunctionExpression("subscription", Array.Empty<LanguageExpression>(), new LanguageExpression[] { new JTokenExpression("subscriptionId") }));
                    }
                    return;
                case ResourceScope.ResourceGroup:
                    if (scopeData.SubscriptionIdProperty != null)
                    {
                        expressionEmitter.EmitProperty("subscriptionId", scopeData.SubscriptionIdProperty);
                    }
                    if (scopeData.ResourceGroupProperty != null)
                    {
                        expressionEmitter.EmitProperty("resourceGroup", scopeData.ResourceGroupProperty);
                    }
                    return;
                default:
                    throw new InvalidOperationException($"Cannot format resourceId for scope {scopeData.RequestedScope}");
            }
        }

        private static ResourceSymbol? GetRootResourceSymbol(IReadOnlyDictionary<ResourceSymbol, ScopeData> scopeInfo, ResourceSymbol resourceSymbol)
        {
            if (!scopeInfo.TryGetValue(resourceSymbol, out var scopeData))
            {
                return null;
            }

            if (scopeData.ResourceScopeSymbol is not null)
            {
                return GetRootResourceSymbol(scopeInfo, scopeData.ResourceScopeSymbol);
            }

            return resourceSymbol;
        }

        private static void ValidateResourceScopeRestrictions(SemanticModel semanticModel, IReadOnlyDictionary<ResourceSymbol, ScopeData> scopeInfo, ResourceSymbol resourceSymbol, Action<DiagnosticBuilder.DiagnosticBuilderDelegate> writeScopeDiagnostic)
        {
            if (resourceSymbol.DeclaringResource.IsExistingResource())
            {
                // we don't have any cross-scope restrictions on 'existing' resource declarations
                return;
            }

            if (semanticModel.Binder.TryGetCycle(resourceSymbol) is not null)
            {
                return;
            }
            
            var rootResourceSymbol = GetRootResourceSymbol(scopeInfo, resourceSymbol);
            if (rootResourceSymbol is null ||
                !scopeInfo.TryGetValue(rootResourceSymbol, out var scopeData))
            {
                // invalid scope should have already generated errors
                return;
            }

            // we only allow resources to be deployed at the target scope
            var matchesTargetScope = (scopeData.RequestedScope == semanticModel.TargetScope &&
                scopeData.ManagementGroupNameProperty is null  &&
                scopeData.SubscriptionIdProperty is null &&
                scopeData.ResourceGroupProperty is null &&
                scopeData.ResourceScopeSymbol is null);

            if (!matchesTargetScope)
            {
                writeScopeDiagnostic(x => x.InvalidCrossResourceScope());
            }
        }

        public static ImmutableDictionary<ResourceSymbol, ScopeData> GetResoureScopeInfo(SemanticModel semanticModel, IDiagnosticWriter diagnosticWriter)
        {
            void logInvalidScopeDiagnostic(IPositionable positionable, ResourceScope suppliedScope, ResourceScope supportedScopes)
                => diagnosticWriter.Write(positionable, x => x.UnsupportedResourceScope(suppliedScope, supportedScopes));

            var scopeInfo = new Dictionary<ResourceSymbol, ScopeData>();

            foreach (var resourceSymbol in semanticModel.Root.ResourceDeclarations)
            {
                if (resourceSymbol.Type is not ResourceType resourceType)
                {
                    // missing type should be caught during type validation
                    continue;
                }

                var scopeProperty = resourceSymbol.SafeGetBodyProperty(LanguageConstants.ResourceScopePropertyName);
                var scopeData = ScopeHelper.ValidateScope(semanticModel, logInvalidScopeDiagnostic, resourceType.ValidParentScopes, resourceSymbol.DeclaringResource.Value, scopeProperty);

                if (scopeData is null)
                {
                    scopeData = new ScopeData { RequestedScope = semanticModel.TargetScope };
                }

                scopeInfo[resourceSymbol] = scopeData;
            }

            foreach (var resourceSymbol in semanticModel.Root.ResourceDeclarations)
            {
                var scopeProperty = resourceSymbol.SafeGetBodyProperty(LanguageConstants.ResourceScopePropertyName);
                
                ValidateResourceScopeRestrictions(
                    semanticModel,
                    scopeInfo,
                    resourceSymbol,
                    buildDiagnostic => diagnosticWriter.Write(scopeProperty?.Value ?? resourceSymbol.DeclaringResource.Value, buildDiagnostic));
            }

            return scopeInfo.ToImmutableDictionary();
        }

        private static void ValidateNestedTemplateScopeRestrictions(SemanticModel semanticModel, ScopeData scopeData, Action<DiagnosticBuilder.DiagnosticBuilderDelegate> writeScopeDiagnostic)
        {
            bool checkScopes(params ResourceScope[] scopes)
                => scopes.Contains(semanticModel.TargetScope);

            var isValid = scopeData.RequestedScope switch {
                // If you update this switch block to add new supported nested template scope combinations,
                // please ensure you update the wording of error messages BCP113, BCP114, BCP115 & BCP116 to reflect this!
                ResourceScope.Tenant 
                    => checkScopes(ResourceScope.Tenant, ResourceScope.ManagementGroup, ResourceScope.Subscription, ResourceScope.ResourceGroup),
                ResourceScope.ManagementGroup when scopeData.ManagementGroupNameProperty is not null 
                    => checkScopes(ResourceScope.Tenant, ResourceScope.ManagementGroup),
                ResourceScope.ManagementGroup
                    => checkScopes(ResourceScope.Tenant, ResourceScope.ManagementGroup),
                ResourceScope.Subscription when scopeData.SubscriptionIdProperty is not null
                    => checkScopes(ResourceScope.Tenant, ResourceScope.ManagementGroup, ResourceScope.Subscription, ResourceScope.ResourceGroup),
                ResourceScope.Subscription
                    => checkScopes(ResourceScope.Subscription, ResourceScope.ResourceGroup),
                ResourceScope.ResourceGroup when scopeData.SubscriptionIdProperty is not null && scopeData.ResourceGroupProperty is not null
                    => checkScopes(ResourceScope.Tenant, ResourceScope.ManagementGroup, ResourceScope.Subscription, ResourceScope.ResourceGroup),
                ResourceScope.ResourceGroup when scopeData.ResourceGroupProperty is not null
                    => checkScopes(ResourceScope.Subscription, ResourceScope.ResourceGroup),
                ResourceScope.ResourceGroup
                    => checkScopes(ResourceScope.ResourceGroup),
                _ => false,
            };
            
            if (isValid)
            {
                return;
            }

            switch (semanticModel.TargetScope)
            {
                case ResourceScope.Tenant:
                    writeScopeDiagnostic(x => x.InvalidModuleScopeForTenantScope());
                    break;
                case ResourceScope.ManagementGroup:
                    writeScopeDiagnostic(x => x.InvalidModuleScopeForManagementScope());
                    break;
                case ResourceScope.Subscription:
                    writeScopeDiagnostic(x => x.InvalidModuleScopeForSubscriptionScope());
                    break;
                case ResourceScope.ResourceGroup:
                    writeScopeDiagnostic(x => x.InvalidModuleScopeForResourceGroup());
                    break;
            }
        }

        public static ImmutableDictionary<ModuleSymbol, ScopeData> GetModuleScopeInfo(SemanticModel semanticModel, IDiagnosticWriter diagnosticWriter)
        {
            void logInvalidScopeDiagnostic(IPositionable positionable, ResourceScope suppliedScope, ResourceScope supportedScopes)
                => diagnosticWriter.Write(positionable, x => x.UnsupportedModuleScope(suppliedScope, supportedScopes));

            var scopeInfo = new Dictionary<ModuleSymbol, ScopeData>();

            foreach (var moduleSymbol in semanticModel.Root.ModuleDeclarations)
            {
                if (moduleSymbol.Type is not ModuleType moduleType)
                {
                    // missing type should be caught during type validation
                    continue;
                }

                var scopeProperty = moduleSymbol.SafeGetBodyProperty(LanguageConstants.ResourceScopePropertyName);
                var scopeData = ScopeHelper.ValidateScope(semanticModel, logInvalidScopeDiagnostic, moduleType.ValidParentScopes, moduleSymbol.DeclaringModule.Value, scopeProperty);

                if (scopeData is null)
                {
                    scopeData = new ScopeData { RequestedScope = semanticModel.TargetScope };
                }

                ValidateNestedTemplateScopeRestrictions(
                    semanticModel,
                    scopeData,
                    buildDiagnostic => diagnosticWriter.Write(scopeProperty?.Value ?? moduleSymbol.DeclaringModule.Value, buildDiagnostic));

                scopeInfo[moduleSymbol] = scopeData;
            }

            return scopeInfo.ToImmutableDictionary();
        }
    }
}