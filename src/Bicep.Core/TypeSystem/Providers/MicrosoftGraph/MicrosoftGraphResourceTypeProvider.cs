// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
using System.Collections.Immutable;
using System.Reflection;
using Bicep.Core.Resources;
using Bicep.Core.TypeSystem.Providers.Extensibility;
using Bicep.Core.TypeSystem.Types;

namespace Bicep.Core.TypeSystem.Providers.MicrosoftGraph
{
    public class MicrosoftGraphResourceTypeProvider : ResourceTypeProviderBase, IResourceTypeProvider
    {
        public const string UniqueNamePropertyName = "uniqueName";
        public const string AppIdPropertyName = "appId";
        public const string NamePropertyName = "name";

        public static readonly TypeSymbol Tags = new ObjectType(nameof(Tags), TypeSymbolValidationFlags.Default, [], new TypeProperty(LanguageConstants.String));

        private readonly MicrosoftGraphResourceTypeLoader resourceTypeLoader;
        private readonly ResourceTypeCache definedTypeCache;
        private readonly ResourceTypeCache generatedTypeCache;

        public static readonly ImmutableHashSet<string> UniqueIdentifierProperties =
        [
            UniqueNamePropertyName,
            AppIdPropertyName,
            NamePropertyName,
        ];

        /*
         * The following top-level properties must be set deploy-time constant values,
         * and it is safe to read them at deploy-time because their values cannot be changed.
         */
        public static readonly ImmutableSortedSet<string> ReadWriteDeployTimeConstantPropertyNames
            = ImmutableSortedSet.Create(LanguageConstants.IdentifierComparer,
                UniqueNamePropertyName);

        public MicrosoftGraphResourceTypeProvider(MicrosoftGraphResourceTypeLoader resourceTypeLoader)
            : base([.. resourceTypeLoader.GetAvailableTypes()])
        {
            this.resourceTypeLoader = resourceTypeLoader;
            definedTypeCache = new ResourceTypeCache();
            generatedTypeCache = new ResourceTypeCache();
        }

        private static ResourceTypeComponents SetBicepResourceProperties(ResourceTypeComponents resourceType, ResourceTypeGenerationFlags flags)
        {
            var bodyType = resourceType.Body.Type;

            switch (bodyType)
            {
                case ObjectType bodyObjectType:
                    if (bodyObjectType.Properties.TryGetValue(UniqueNamePropertyName, out var nameProperty) &&
                        nameProperty.TypeReference.Type is not StringType)
                    {
                        // The 'uniqueName' property doesn't support fixed value names (e.g. we're in a top-level child resource declaration).
                        // Best we can do is return a regular 'string' field for it as we have no good way to reliably evaluate complex expressions (e.g. to check whether it terminates with '/<constantType>').
                        // Keep it simple for now - we eventually plan to phase out the 'top-level child' syntax.
                        bodyObjectType = new ObjectType(
                            bodyObjectType.Name,
                            bodyObjectType.ValidationFlags,
                            bodyObjectType.Properties.SetItem(UniqueNamePropertyName, new NamedTypeProperty(nameProperty.Name, LanguageConstants.String, nameProperty.Flags)).Values,
                            bodyObjectType.AdditionalProperties,
                            bodyObjectType.MethodResolver.CopyToObject);

                        bodyType = SetBicepResourceProperties(bodyObjectType, resourceType.ValidParentScopes, resourceType.TypeReference, flags);
                        break;
                    }

                    bodyType = SetBicepResourceProperties(bodyObjectType, resourceType.ValidParentScopes, resourceType.TypeReference, flags);
                    break;
                default:
                    // we exhaustively test deserialization of every resource type during CI, and this happens in a deterministic fashion,
                    // so this exception should never occur in the released product
                    throw new ArgumentException($"Resource {resourceType.TypeReference.FormatName()} has unexpected body type {bodyType.GetType()}");
            }

            return resourceType with { Body = bodyType };
        }

        private static ObjectType SetBicepResourceProperties(ObjectType objectType, ResourceScope validParentScopes, ResourceTypeReference typeReference, ResourceTypeGenerationFlags flags)
        {
            // Local function.
            static NamedTypeProperty UpdateFlags(NamedTypeProperty typeProperty, TypePropertyFlags flags) =>
                new(typeProperty.Name, typeProperty.TypeReference, flags, typeProperty.Description);

            var properties = objectType.Properties;
            var isExistingResource = flags.HasFlag(ResourceTypeGenerationFlags.ExistingResource);

            if (isExistingResource)
            {
            }
            else
            {
                // TODO: remove 'dependsOn' from the type library
                properties = properties.SetItem(LanguageConstants.ResourceDependsOnPropertyName, new NamedTypeProperty(LanguageConstants.ResourceDependsOnPropertyName, LanguageConstants.ResourceOrResourceCollectionRefArray, TypePropertyFlags.WriteOnly | TypePropertyFlags.DisallowAny));
            }

            // add the loop variant flag to the uniqueName property (if it exists)
            if (properties.TryGetValue(UniqueNamePropertyName, out var uniqueNameProperty))
            {
                // TODO apply this to all unique properties
                properties = properties.SetItem(UniqueNamePropertyName, UpdateFlags(uniqueNameProperty, uniqueNameProperty.Flags | TypePropertyFlags.LoopVariant));
            }

            // add the loop variant flag to the name property (if it exists)
            if (properties.TryGetValue(NamePropertyName, out var nameProperty))
            {
                // TODO apply this to all unique properties
                properties = properties.SetItem(NamePropertyName, UpdateFlags(nameProperty, nameProperty.Flags | TypePropertyFlags.LoopVariant));
            }

            foreach (var identifier in ReadWriteDeployTimeConstantPropertyNames)
            {
                if (properties.TryGetValue(identifier, out var typeProperty))
                {
                    properties = properties.SetItem(identifier, UpdateFlags(typeProperty, typeProperty.Flags | TypePropertyFlags.ReadableAtDeployTime));
                }
            }

            return new ObjectType(
                objectType.Name,
                objectType.ValidationFlags,
                isExistingResource ? ConvertToReadOnly(properties.Values) : properties.Values,
                isExistingResource && objectType.AdditionalProperties is not null
                    ? objectType.AdditionalProperties with { Flags = ConvertToReadOnly(objectType.AdditionalProperties.Flags) }
                    : objectType.AdditionalProperties,
                functions: null);
        }

        private static IEnumerable<NamedTypeProperty> ConvertToReadOnly(IEnumerable<NamedTypeProperty> properties)
        {
            foreach (var property in properties)
            {
                // "uniqueName", "name", "appId", "scope" & "parent" can be set for existing resources - everything else should be read-only
                if (UniqueIdentifierProperties.Contains(property.Name) || property.Flags.HasFlag(TypePropertyFlags.ResourceIdentifier))
                {
                    yield return property;
                }
                else
                {
                    yield return new NamedTypeProperty(property.Name, property.TypeReference, ConvertToReadOnly(property.Flags));
                }
            }
        }

        private static TypePropertyFlags ConvertToReadOnly(TypePropertyFlags typePropertyFlags)
            => (typePropertyFlags | TypePropertyFlags.ReadOnly) & ~TypePropertyFlags.Required;

        public ResourceType? TryGetDefinedType(NamespaceType declaringNamespace, ResourceTypeReference typeReference, ResourceTypeGenerationFlags flags)
        {
            if (!HasDefinedType(typeReference))
            {
                return null;
            }

            // It's important to cache this result because generating the resource type is an expensive operation
            var resourceType = definedTypeCache.GetOrAdd(flags, typeReference, () =>
            {
                var resourceType = resourceTypeLoader.LoadType(typeReference);

                return SetBicepResourceProperties(resourceType, flags);
            });

            return new(
                declaringNamespace,
                resourceType.TypeReference,
                resourceType.ValidParentScopes,
                resourceType.ReadOnlyScopes,
                resourceType.Flags,
                resourceType.Body,
                UniqueIdentifierProperties);
        }

        public ResourceType? TryGenerateFallbackType(NamespaceType declaringNamespace, ResourceTypeReference typeReference, ResourceTypeGenerationFlags flags)
            => null;

        public bool HasDefinedType(ResourceTypeReference typeReference)
            => availableResourceTypes.Contains(typeReference);

        public IEnumerable<ResourceTypeReference> GetAvailableTypes()
            => availableResourceTypes;

        public ExtensionResourceTypeLoader.NamespaceConfiguration? GetNamespaceConfiguration()
        {
            return resourceTypeLoader.LoadNamespaceConfiguration();
        }
    }
}
