// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.OpenApi;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DevProxy.Plugins.Utils;

internal enum SchemaValidationDirection
{
    Request,
    Response
}

internal sealed record SchemaValidationError(string Path, string Message);

/// <summary>
/// Lightweight validator of JSON values against OpenAPI schemas
/// (Microsoft.OpenApi object model). Supports: type (incl. nullable / type null),
/// required, properties, additionalProperties, items, enum, allOf, anyOf, oneOf
/// and $ref. Other keywords (format, min/max, pattern, etc.) are ignored.
/// </summary>
internal static class OpenApiSchemaValidator
{
    private const int MaxDepth = 64;

    public static List<SchemaValidationError> Validate(
        JsonElement value,
        IOpenApiSchema? schema,
        SchemaValidationDirection direction,
        string path = "$")
    {
        var errors = new List<SchemaValidationError>();
        ValidateCore(value, schema, direction, path, errors, 0);
        return errors;
    }

    public static List<SchemaValidationError> Validate(
        JsonNode? value,
        IOpenApiSchema? schema,
        SchemaValidationDirection direction,
        string path = "$")
    {
        using var doc = JsonDocument.Parse(value?.ToJsonString() ?? "null");
        return Validate(doc.RootElement.Clone(), schema, direction, path);
    }

    /// <summary>
    /// Converts a raw (string) parameter value into a JSON value, coercing it
    /// to the primitive type declared by the schema when possible, so that it
    /// can be validated with <see cref="Validate(JsonNode?, IOpenApiSchema?, SchemaValidationDirection, string)"/>.
    /// </summary>
    public static JsonNode? CoerceParameterValue(IReadOnlyList<string> values, IOpenApiSchema? schema)
    {
        var type = schema?.Type;
        if (type is not null && type.Value.HasFlag(JsonSchemaType.Array))
        {
            var items = values.Count == 1
                ? values[0].Split(',')
                : [.. values];
            var array = new JsonArray();
            foreach (var item in items)
            {
                array.Add(CoercePrimitive(item, schema!.Items));
            }
            return array;
        }

        return CoercePrimitive(values.Count > 0 ? values[0] : string.Empty, schema);
    }

    private static JsonValue? CoercePrimitive(string value, IOpenApiSchema? schema)
    {
        var type = schema?.Type;
        if (type is null)
        {
            return JsonValue.Create(value);
        }

        if (type.Value.HasFlag(JsonSchemaType.Integer) &&
            long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
        {
            return JsonValue.Create(l);
        }
        if (type.Value.HasFlag(JsonSchemaType.Number) &&
            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
        {
            return JsonValue.Create(d);
        }
        if (type.Value.HasFlag(JsonSchemaType.Boolean) && bool.TryParse(value, out var b))
        {
            return JsonValue.Create(b);
        }
        if (type.Value.HasFlag(JsonSchemaType.Null) && value.Length == 0)
        {
            return null;
        }

        return JsonValue.Create(value);
    }

    private static void ValidateCore(
        JsonElement value,
        IOpenApiSchema? schema,
        SchemaValidationDirection direction,
        string path,
        List<SchemaValidationError> errors,
        int depth)
    {
        if (schema is null || depth > MaxDepth)
        {
            return;
        }

        if (!ValidateType(value, schema, path, errors))
        {
            // no point validating further if the type doesn't match
            return;
        }

        ValidateEnum(value, schema, path, errors);

        if (value.ValueKind == JsonValueKind.Object)
        {
            ValidateObject(value, schema, direction, path, errors, depth);
        }
        else if (value.ValueKind == JsonValueKind.Array && schema.Items is not null)
        {
            var i = 0;
            foreach (var item in value.EnumerateArray())
            {
                ValidateCore(item, schema.Items, direction, $"{path}[{i}]", errors, depth + 1);
                i++;
            }
        }

        if (schema.AllOf is { Count: > 0 })
        {
            foreach (var subSchema in schema.AllOf)
            {
                ValidateCore(value, subSchema, direction, path, errors, depth + 1);
            }
        }

        if (schema.AnyOf is { Count: > 0 } &&
            !schema.AnyOf.Any(s => IsValid(value, s, direction, path, depth)))
        {
            errors.Add(new(path, "Value doesn't match any of the schemas in 'anyOf'"));
        }

        if (schema.OneOf is { Count: > 0 })
        {
            var matches = schema.OneOf.Count(s => IsValid(value, s, direction, path, depth));
            if (matches == 0)
            {
                errors.Add(new(path, "Value doesn't match any of the schemas in 'oneOf'"));
            }
            else if (matches > 1)
            {
                errors.Add(new(path, $"Value matches {matches} schemas in 'oneOf' but must match exactly one"));
            }
        }
    }

    private static bool IsValid(JsonElement value, IOpenApiSchema schema, SchemaValidationDirection direction, string path, int depth)
    {
        var subErrors = new List<SchemaValidationError>();
        ValidateCore(value, schema, direction, path, subErrors, depth + 1);
        return subErrors.Count == 0;
    }

    private static bool ValidateType(JsonElement value, IOpenApiSchema schema, string path, List<SchemaValidationError> errors)
    {
        if (schema.Type is not { } type || type == 0)
        {
            return true;
        }

        var matches = value.ValueKind switch
        {
            JsonValueKind.Null => type.HasFlag(JsonSchemaType.Null),
            JsonValueKind.True or JsonValueKind.False => type.HasFlag(JsonSchemaType.Boolean),
            JsonValueKind.String => type.HasFlag(JsonSchemaType.String),
            JsonValueKind.Object => type.HasFlag(JsonSchemaType.Object),
            JsonValueKind.Array => type.HasFlag(JsonSchemaType.Array),
            JsonValueKind.Number => type.HasFlag(JsonSchemaType.Number) ||
                (type.HasFlag(JsonSchemaType.Integer) && IsInteger(value)),
            JsonValueKind.Undefined => true,
            _ => true
        };

        if (!matches)
        {
            errors.Add(new(path, $"Expected {DescribeType(type)} but got {DescribeValueKind(value)}"));
        }

        return matches;
    }

    private static bool IsInteger(JsonElement value) =>
        value.TryGetInt64(out _) ||
        (value.TryGetDecimal(out var d) && decimal.Truncate(d) == d);

    private static void ValidateEnum(JsonElement value, IOpenApiSchema schema, string path, List<SchemaValidationError> errors)
    {
        if (schema.Enum is not { Count: > 0 })
        {
            return;
        }

        var node = JsonNode.Parse(value.GetRawText());
        if (schema.Enum.Any(e => JsonNode.DeepEquals(e, node)))
        {
            return;
        }

        // OAS 3.0 nullable enums: null is allowed if the schema allows null
        if (node is null && schema.Type is { } type && type.HasFlag(JsonSchemaType.Null))
        {
            return;
        }

        var allowed = string.Join(", ", schema.Enum.Select(e => e?.ToJsonString() ?? "null"));
        errors.Add(new(path, $"Value {value.GetRawText()} isn't one of the allowed values: {allowed}"));
    }

    private static void ValidateObject(
        JsonElement value,
        IOpenApiSchema schema,
        SchemaValidationDirection direction,
        string path,
        List<SchemaValidationError> errors,
        int depth)
    {
        if (schema.Required is { Count: > 0 })
        {
            foreach (var requiredProperty in schema.Required)
            {
                if (value.TryGetProperty(requiredProperty, out _))
                {
                    continue;
                }

                // readOnly properties aren't sent in requests, writeOnly
                // properties aren't returned in responses
                IOpenApiSchema? propertySchema = null;
                _ = schema.Properties?.TryGetValue(requiredProperty, out propertySchema);
                if (propertySchema is not null &&
                    ((direction == SchemaValidationDirection.Request && propertySchema.ReadOnly) ||
                     (direction == SchemaValidationDirection.Response && propertySchema.WriteOnly)))
                {
                    continue;
                }

                errors.Add(new(path, $"Missing required property '{requiredProperty}'"));
            }
        }

        foreach (var property in value.EnumerateObject())
        {
            var propertyPath = $"{path}.{property.Name}";
            if (schema.Properties is not null &&
                schema.Properties.TryGetValue(property.Name, out var propertySchema))
            {
                ValidateCore(property.Value, propertySchema, direction, propertyPath, errors, depth + 1);
            }
            else if (schema.AdditionalProperties is not null)
            {
                ValidateCore(property.Value, schema.AdditionalProperties, direction, propertyPath, errors, depth + 1);
            }
            else if (!schema.AdditionalPropertiesAllowed)
            {
                errors.Add(new(propertyPath, $"Property '{property.Name}' isn't defined in the schema"));
            }
        }
    }

    private static string DescribeType(JsonSchemaType type)
    {
        var names = Enum.GetValues<JsonSchemaType>()
            .Where(t => type.HasFlag(t))
            .OrderBy(t => t == JsonSchemaType.Null)
            .Select(t => t.ToString().ToLowerInvariant());
        return string.Join(" or ", names);
    }

    private static string DescribeValueKind(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => "null",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.String => "string",
        JsonValueKind.Object => "object",
        JsonValueKind.Array => "array",
        JsonValueKind.Number => IsInteger(value) ? "integer" : "number",
        _ => value.ValueKind.ToString().ToLowerInvariant()
    };
}
