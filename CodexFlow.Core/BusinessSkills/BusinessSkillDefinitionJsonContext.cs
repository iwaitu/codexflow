// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using CodexFlow.Contracts.BusinessSkills;

namespace CodexFlow.Core.BusinessSkills;

internal static class BusinessSkillDefinitionJsonReader
{
    private static readonly BusinessSkillDefinitionJsonContext Json = new(new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        AllowOutOfOrderMetadataProperties = true
    });

    // For source-generated init-only properties, absent parameters otherwise take default(T).
    // Serialize the model initializers so an omitted property has the same value as in the original reader.
    private static readonly Dictionary<Type, JsonElement> InitializerDefaults = new()
    {
        [typeof(SkillDefinitionV1)] = Defaults(new SkillDefinitionV1()),
        [typeof(SkillConnectionBinding)] = Defaults(new SkillConnectionBinding()),
        [typeof(SkillInputSchemaDto)] = Defaults(new SkillInputSchemaDto()),
        [typeof(SkillInputPropertyDto)] = Defaults(new SkillInputPropertyDto()),
        [typeof(SkillOutputSchemaDto)] = Defaults(new SkillOutputSchemaDto()),
        [typeof(SkillSuccessCriteriaDto)] = Defaults(new SkillSuccessCriteriaDto()),
        [typeof(SkillDataPolicyDto)] = Defaults(new SkillDataPolicyDto()),
        [typeof(SkillBudgetPolicyDto)] = Defaults(new SkillBudgetPolicyDto()),
        [typeof(SkillValidationEvidenceDto)] = Defaults(new SkillValidationEvidenceDto()),
        [typeof(SkillPaginationConfig)] = Defaults(new SkillPaginationConfig()),
        [typeof(SkillResponseContract)] = Defaults(new SkillResponseContract()),
        [typeof(SkillConditionExpr)] = Defaults(new SkillConditionExpr()),
        [typeof(SkillJoinSpec)] = Defaults(new SkillJoinSpec()),
        [typeof(SkillToolStep)] = Defaults(new SkillToolStep()),
        [typeof(SkillTransformStep)] = Defaults(new SkillTransformStep()),
        [typeof(SkillBranchStep)] = Defaults(new SkillBranchStep()),
        [typeof(SkillForeachStep)] = Defaults(new SkillForeachStep()),
        [typeof(SkillGenerateTextStep)] = Defaults(new SkillGenerateTextStep()),
        [typeof(SkillApprovalStep)] = Defaults(new SkillApprovalStep()),
        [typeof(SkillOutputStep)] = Defaults(new SkillOutputStep())
    };

    public static SkillDefinitionV1? Read(string json)
    {
        using var document = JsonDocument.Parse(json);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteWithDefaults(writer, document.RootElement, Json.SkillDefinitionV1);
        }

        return JsonSerializer.Deserialize(buffer.WrittenSpan, Json.SkillDefinitionV1);
    }

    private static JsonElement Defaults<T>(T value)
        => JsonSerializer.SerializeToElement(value, (JsonTypeInfo<T>)Json.GetTypeInfo(typeof(T))!);

    private static void WriteWithDefaults(Utf8JsonWriter writer, JsonElement value, JsonTypeInfo? contract)
    {
        if (value.ValueKind == JsonValueKind.Object && contract?.Kind == JsonTypeInfoKind.Object)
        {
            WriteObjectWithDefaults(writer, value, ResolveDerivedType(value, contract));
        }
        else if (value.ValueKind == JsonValueKind.Object && contract?.Kind == JsonTypeInfoKind.Dictionary)
        {
            var itemContract = Json.GetTypeInfo(contract.ElementType!);
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject())
            {
                writer.WritePropertyName(property.Name);
                WriteWithDefaults(writer, property.Value, itemContract);
            }

            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array && contract?.Kind == JsonTypeInfoKind.Enumerable)
        {
            var itemContract = Json.GetTypeInfo(contract.ElementType!);
            writer.WriteStartArray();
            foreach (var item in value.EnumerateArray())
            {
                WriteWithDefaults(writer, item, itemContract);
            }

            writer.WriteEndArray();
        }
        else
        {
            // An opaque JsonElement binding keeps the original number, escaping and whitespace inside its token.
            writer.WriteRawValue(value.GetRawText(), skipInputValidation: true);
        }
    }

    private static void WriteObjectWithDefaults(Utf8JsonWriter writer, JsonElement value, JsonTypeInfo contract)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        writer.WriteStartObject();
        foreach (var property in value.EnumerateObject())
        {
            writer.WritePropertyName(property.Name);
            names.Add(property.Name);
            var descriptor = contract.Properties.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, property.Name, StringComparison.OrdinalIgnoreCase));
            WriteWithDefaults(writer, property.Value, descriptor is null ? null : Json.GetTypeInfo(descriptor.PropertyType));
        }

        if (InitializerDefaults.TryGetValue(contract.Type, out var defaults))
        {
            foreach (var property in defaults.EnumerateObject())
            {
                if (!names.Contains(property.Name))
                {
                    property.WriteTo(writer);
                }
            }
        }

        writer.WriteEndObject();
    }

    private static JsonTypeInfo ResolveDerivedType(JsonElement value, JsonTypeInfo contract)
    {
        if (contract.PolymorphismOptions is { } polymorphism &&
            value.TryGetProperty(polymorphism.TypeDiscriminatorPropertyName, out var discriminator) &&
            discriminator.ValueKind == JsonValueKind.String)
        {
            var name = discriminator.GetString();
            foreach (var derived in polymorphism.DerivedTypes)
            {
                if (derived.TypeDiscriminator is string label && string.Equals(label, name, StringComparison.Ordinal))
                {
                    return Json.GetTypeInfo(derived.DerivedType)!;
                }
            }
        }

        return contract;
    }
}

internal sealed record CompositeOpenApiInputSchema(
    [property: JsonPropertyName("parameters")] IReadOnlyList<CapabilityParameterSpecDto> Parameters,
    [property: JsonPropertyName("hasJsonBody")] bool HasJsonBody);

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(SkillDefinitionV1))]
[JsonSerializable(typeof(CompositeOpenApiInputSchema))]
internal sealed partial class BusinessSkillDefinitionJsonContext : JsonSerializerContext;
