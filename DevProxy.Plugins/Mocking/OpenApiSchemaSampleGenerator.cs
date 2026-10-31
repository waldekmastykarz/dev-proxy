// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.OpenApi;
using System.Globalization;
using System.Text.Json.Nodes;

namespace DevProxy.Plugins.Mocking;

/// <summary>
/// Generates sample values and simplified, reference-free descriptions
/// of OpenAPI schemas.
/// </summary>
internal static class OpenApiSchemaSampleGenerator
{
    private const int MaxDepth = 8;

    public static JsonNode? GenerateSample(IOpenApiSchema? schema) => GenerateSample(schema, 0, []);

    /// <summary>
    /// Returns a simplified JSON Schema-like representation of the schema with
    /// all references resolved, suitable to pass to a language model.
    /// </summary>
    public static JsonObject Describe(IOpenApiSchema? schema) => Describe(schema, 0, []) ?? [];

    private static string? GetReferenceId(IOpenApiSchema schema) =>
        schema is OpenApiSchemaReference reference ? reference.Reference?.ReferenceV3 : null;

    private static JsonNode? GenerateSample(IOpenApiSchema? schema, int depth, HashSet<string> visiting)
    {
        if (schema is null || depth > MaxDepth)
        {
            return null;
        }

        // stop at recursive references to avoid deeply nested samples
        var referenceId = GetReferenceId(schema);
        if (referenceId is not null && !visiting.Add(referenceId))
        {
            return null;
        }

        try
        {
            return GenerateSampleCore(schema, depth, visiting);
        }
        finally
        {
            if (referenceId is not null)
            {
                _ = visiting.Remove(referenceId);
            }
        }
    }

    private static JsonNode? GenerateSampleCore(IOpenApiSchema schema, int depth, HashSet<string> visiting)
    {
        // Example is obsolete in favor of Examples, but OpenAPI 3.0 specs still use it
#pragma warning disable CS0618
        if (schema.Example is not null)
        {
            return schema.Example.DeepClone();
        }
#pragma warning restore CS0618

        var example = schema.Examples?.FirstOrDefault(e => e is not null);
        if (example is not null)
        {
            return example.DeepClone();
        }

        if (schema.Default is not null)
        {
            return schema.Default.DeepClone();
        }

        var enumValue = schema.Enum?.FirstOrDefault(e => e is not null);
        if (enumValue is not null)
        {
            return enumValue.DeepClone();
        }

        if (schema.AllOf is { Count: > 0 })
        {
            return GenerateAllOfSample(schema, depth, visiting);
        }

        var alternative = schema.OneOf?.FirstOrDefault() ?? schema.AnyOf?.FirstOrDefault();
        if (alternative is not null)
        {
            return GenerateSample(alternative, depth + 1, visiting);
        }

        return GetEffectiveType(schema) switch
        {
            JsonSchemaType.Object => GenerateObjectSample(schema, depth, visiting),
            JsonSchemaType.Array => GenerateArraySample(schema, depth, visiting),
            JsonSchemaType.Integer => JsonValue.Create(GetNumber(schema, isInteger: true)),
            JsonSchemaType.Number => JsonValue.Create(GetNumber(schema, isInteger: false)),
            JsonSchemaType.Boolean => JsonValue.Create(true),
            JsonSchemaType.Null => null,
            _ => JsonValue.Create(GetStringSample(schema))
        };
    }

    private static JsonNode? GenerateAllOfSample(IOpenApiSchema schema, int depth, HashSet<string> visiting)
    {
        JsonObject? merged = null;
        JsonNode? firstNonObject = null;

        foreach (var subSchema in schema.AllOf ?? [])
        {
            var sample = GenerateSample(subSchema, depth + 1, visiting);
            if (sample is JsonObject obj)
            {
                merged ??= [];
                MergeInto(merged, obj);
            }
            else if (sample is not null)
            {
                firstNonObject ??= sample;
            }
        }

        if (schema.Properties is { Count: > 0 })
        {
            merged ??= [];
            MergeInto(merged, GenerateObjectSample(schema, depth, visiting));
        }

        return merged ?? firstNonObject;
    }

    private static JsonObject GenerateObjectSample(IOpenApiSchema schema, int depth, HashSet<string> visiting)
    {
        var obj = new JsonObject();
        foreach (var (name, propertySchema) in schema.Properties ?? new Dictionary<string, IOpenApiSchema>())
        {
            if (propertySchema is null || propertySchema.WriteOnly)
            {
                continue;
            }

            obj[name] = GenerateSample(propertySchema, depth + 1, visiting);
        }

        if (obj.Count == 0 && schema.AdditionalProperties is not null)
        {
            obj["additionalProp1"] = GenerateSample(schema.AdditionalProperties, depth + 1, visiting);
        }

        return obj;
    }

    private static JsonArray GenerateArraySample(IOpenApiSchema schema, int depth, HashSet<string> visiting)
    {
        var item = GenerateSample(schema.Items, depth + 1, visiting);
        return item is null ? [] : [item];
    }

    private static string GetStringSample(IOpenApiSchema schema)
    {
        var value = schema.Format?.ToLowerInvariant() switch
        {
            "date-time" => "2024-01-01T12:00:00Z",
            "date" => "2024-01-01",
            "time" => "12:00:00",
            "duration" => "P1D",
            "uuid" => "3fa85f64-5717-4562-b3fc-2c963f66afa6",
            "email" or "idn-email" => "user@example.com",
            "uri" or "url" or "iri" or "uri-reference" or "iri-reference" => "https://example.com",
            "hostname" or "idn-hostname" => "example.com",
            "ipv4" => "192.168.0.1",
            "ipv6" => "2001:db8::1",
            "byte" => "c3RyaW5n",
            "password" => "P@ssw0rd!",
            _ => "string"
        };

        if (schema.MinLength is int minLength && value.Length < minLength)
        {
            value = value.PadRight(minLength, 'x');
        }
        if (schema.MaxLength is int maxLength && value.Length > maxLength)
        {
            value = value[..maxLength];
        }

        return value;
    }

    private static decimal GetNumber(IOpenApiSchema schema, bool isInteger)
    {
        // use 0 unless it's outside the allowed range
        if (TryParseBound(schema.Minimum?.ToString(), out var minimum) && minimum > 0)
        {
            return isInteger ? decimal.Ceiling(minimum) : minimum;
        }
        if (TryParseBound(schema.Maximum?.ToString(), out var maximum) && maximum < 0)
        {
            return isInteger ? decimal.Floor(maximum) : maximum;
        }

        return 0;
    }

    private static bool TryParseBound(string? bound, out decimal value) =>
        decimal.TryParse(bound, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static JsonSchemaType? GetEffectiveType(IOpenApiSchema schema)
    {
        if (schema.Type is JsonSchemaType type)
        {
            // pick the first non-null type when multiple types are allowed
            foreach (var candidate in new[]
            {
                JsonSchemaType.Object, JsonSchemaType.Array, JsonSchemaType.String,
                JsonSchemaType.Integer, JsonSchemaType.Number, JsonSchemaType.Boolean
            })
            {
                if (type.HasFlag(candidate))
                {
                    return candidate;
                }
            }

            if (type.HasFlag(JsonSchemaType.Null))
            {
                return JsonSchemaType.Null;
            }
        }

        if (schema.Properties is { Count: > 0 } || schema.AdditionalProperties is not null)
        {
            return JsonSchemaType.Object;
        }

        if (schema.Items is not null)
        {
            return JsonSchemaType.Array;
        }

        return null;
    }

    private static void MergeInto(JsonObject target, JsonObject source)
    {
        foreach (var (key, value) in source)
        {
            target[key] = value?.DeepClone();
        }
    }

    private static JsonObject? Describe(IOpenApiSchema? schema, int depth, HashSet<string> visiting)
    {
        if (schema is null || depth > MaxDepth)
        {
            return null;
        }

        var referenceId = GetReferenceId(schema);
        if (referenceId is not null && !visiting.Add(referenceId))
        {
            return null;
        }

        try
        {
            return DescribeCore(schema, depth, visiting);
        }
        finally
        {
            if (referenceId is not null)
            {
                _ = visiting.Remove(referenceId);
            }
        }
    }

    private static JsonObject DescribeCore(IOpenApiSchema schema, int depth, HashSet<string> visiting)
    {
        var description = new JsonObject();

        var allOfSchemas = schema.AllOf?.Where(s => s is not null).ToArray() ?? [];
        if (allOfSchemas.Length > 0)
        {
            foreach (var subSchema in allOfSchemas)
            {
                MergeDescription(description, Describe(subSchema, depth + 1, visiting));
            }
        }

        var alternative = schema.OneOf?.FirstOrDefault() ?? schema.AnyOf?.FirstOrDefault();
        if (alternative is not null)
        {
            MergeDescription(description, Describe(alternative, depth + 1, visiting));
        }

        var type = GetEffectiveType(schema);
        if (type is not null)
        {
            description["type"] = type.Value.ToString().ToLowerInvariant();
        }
        if (!string.IsNullOrEmpty(schema.Format))
        {
            description["format"] = schema.Format;
        }
        if (!string.IsNullOrEmpty(schema.Description))
        {
            description["description"] = schema.Description;
        }
        if (schema.Minimum is not null)
        {
            description["minimum"] = ToNumberNode(schema.Minimum.ToString());
        }
        if (schema.Maximum is not null)
        {
            description["maximum"] = ToNumberNode(schema.Maximum.ToString());
        }
        if (schema.Enum is { Count: > 0 })
        {
            description["enum"] = new JsonArray([.. schema.Enum.Select(e => e?.DeepClone())]);
        }
#pragma warning disable CS0618
        if (schema.Example is not null)
        {
            description["example"] = schema.Example.DeepClone();
        }
#pragma warning restore CS0618

        if (schema.Properties is { Count: > 0 })
        {
            var properties = description["properties"] as JsonObject ?? [];
            foreach (var (name, propertySchema) in schema.Properties)
            {
                if (propertySchema is null || propertySchema.WriteOnly)
                {
                    continue;
                }
                var propertyDescription = Describe(propertySchema, depth + 1, visiting);
                if (propertyDescription is not null)
                {
                    properties[name] = propertyDescription;
                }
            }
            description["properties"] = properties;
        }
        if (schema.Required is { Count: > 0 })
        {
            description["required"] = new JsonArray([.. schema.Required.Select(r => JsonValue.Create(r))]);
        }
        if (schema.Items is not null)
        {
            description["items"] = Describe(schema.Items, depth + 1, visiting) ?? [];
        }
        if (schema.AdditionalProperties is not null)
        {
            description["additionalProperties"] = Describe(schema.AdditionalProperties, depth + 1, visiting) ?? [];
        }

        return description;
    }

    private static JsonValue? ToNumberNode(string? value) =>
        decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ?
            JsonValue.Create(number) :
            JsonValue.Create(value);

    private static void MergeDescription(JsonObject target, JsonObject? source)
    {
        if (source is null)
        {
            return;
        }

        foreach (var (key, value) in source)
        {
            if (key == "properties" && value is JsonObject sourceProperties &&
                target["properties"] is JsonObject targetProperties)
            {
                MergeInto(targetProperties, sourceProperties);
                continue;
            }

            target[key] = value?.DeepClone();
        }
    }
}
