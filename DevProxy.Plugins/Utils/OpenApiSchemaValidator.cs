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
    // bounds total work, e.g. for schemas that reference the same subschema repeatedly
    private const int MaxEvaluations = 1_000_000;

    private sealed class ValidationState
    {
        public int Evaluations { get; set; }
        public bool BudgetExceeded { get; set; }
        public Dictionary<(IOpenApiSchema Schema, string Path), List<SchemaValidationError>> Memo { get; } = [];
    }

    public static List<SchemaValidationError> Validate(
        JsonElement value,
        IOpenApiSchema? schema,
        SchemaValidationDirection direction,
        string path = "$")
    {
        var errors = new List<SchemaValidationError>();
        var state = new ValidationState();
        ValidateCore(value, schema, direction, path, errors, state, 0);
        if (state.BudgetExceeded)
        {
            errors.Add(new(path, $"Validation stopped after {MaxEvaluations} schema evaluations, so the value couldn't be fully validated"));
        }
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
    /// Converts raw (string) parameter values into a JSON value, decoding them
    /// according to the parameter's serialization style and coercing them
    /// to the primitive type declared by the schema when possible, so that they
    /// can be validated with <see cref="Validate(JsonNode?, IOpenApiSchema?, SchemaValidationDirection, string)"/>.
    /// </summary>
    public static JsonNode? CoerceParameterValue(
        IReadOnlyList<string> values,
        IOpenApiSchema? schema,
        string name,
        ParameterStyle style,
        bool explode)
    {
        var type = schema?.Type;
        if (type is not null && type.Value.HasFlag(JsonSchemaType.Array))
        {
            var array = new JsonArray();
            foreach (var item in SplitArrayValues(values, name, style, explode))
            {
                array.Add(CoercePrimitive(item, schema!.Items));
            }
            return array;
        }

        var value = values.Count > 0 ? values[0] : string.Empty;
        return CoercePrimitive(StripStylePrefix(value, name, style), schema);
    }

    private static IEnumerable<string> SplitArrayValues(IReadOnlyList<string> values, string name, ParameterStyle style, bool explode)
    {
        switch (style)
        {
            case ParameterStyle.Form:
            case ParameterStyle.SpaceDelimited:
            case ParameterStyle.PipeDelimited:
                // exploded arrays are sent as repeated parameters, e.g. ids=1&ids=2
                if (explode)
                {
                    return values;
                }
                var delimiter = style switch
                {
                    ParameterStyle.SpaceDelimited => ' ',
                    ParameterStyle.PipeDelimited => '|',
                    _ => ','
                };
                return values.SelectMany(v => v.Split(delimiter));
            case ParameterStyle.Label:
                return values.SelectMany(v => StripStylePrefix(v, name, style).Split(explode ? '.' : ','));
            case ParameterStyle.Matrix:
                if (explode)
                {
                    // ;id=1;id=2
                    var prefix = $";{name}=";
                    return values.SelectMany(v => v.Split(prefix, StringSplitOptions.RemoveEmptyEntries));
                }
                return values.SelectMany(v => StripStylePrefix(v, name, style).Split(','));
            case ParameterStyle.Simple:
            default:
                return values.SelectMany(v => v.Split(',').Select(i => i.Trim()));
        }
    }

    private static string StripStylePrefix(string value, string name, ParameterStyle style) => style switch
    {
        ParameterStyle.Label when value.StartsWith('.') => value[1..],
        ParameterStyle.Matrix when value.StartsWith($";{name}=", StringComparison.Ordinal) => value[(name.Length + 2)..],
        ParameterStyle.Matrix when value == $";{name}" => string.Empty,
        _ => value
    };

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
            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) &&
            double.IsFinite(d))
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
        ValidationState state,
        int depth)
    {
        if (schema is null || state.BudgetExceeded)
        {
            return;
        }
        if (++state.Evaluations > MaxEvaluations)
        {
            state.BudgetExceeded = true;
            return;
        }
        if (depth > MaxDepth)
        {
            errors.Add(new(path, $"Schema is nested deeper than the supported maximum of {MaxDepth} levels, so the value couldn't be validated"));
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
            ValidateObject(value, schema, direction, path, errors, state, depth);
        }
        else if (value.ValueKind == JsonValueKind.Array && schema.Items is not null)
        {
            var i = 0;
            foreach (var item in value.EnumerateArray())
            {
                ValidateCore(item, schema.Items, direction, $"{path}[{i}]", errors, state, depth + 1);
                i++;
            }
        }

        if (schema.AllOf is { Count: > 0 })
        {
            foreach (var subSchema in schema.AllOf)
            {
                foreach (var error in ValidateComposed(value, subSchema, direction, path, state, depth + 1))
                {
                    // subschemas can report the same error, e.g. when they reference the same schema
                    if (!errors.Contains(error))
                    {
                        errors.Add(error);
                    }
                }
            }
        }

        if (schema.AnyOf is { Count: > 0 } &&
            !schema.AnyOf.Any(s => IsValid(value, s, direction, path, state, depth)))
        {
            errors.Add(new(path, "Value doesn't match any of the schemas in 'anyOf'"));
        }

        if (schema.OneOf is { Count: > 0 })
        {
            var matches = schema.OneOf.Count(s => IsValid(value, s, direction, path, state, depth));
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

    private static bool IsValid(JsonElement value, IOpenApiSchema schema, SchemaValidationDirection direction, string path, ValidationState state, int depth) =>
        ValidateComposed(value, schema, direction, path, state, depth + 1).Count == 0;

    // Composition keywords can reference the same subschema many times for the
    // same value. Within a single validation, a path identifies a value, so
    // results are memoized per (resolved schema, path) to avoid exponential work.
    private static List<SchemaValidationError> ValidateComposed(JsonElement value, IOpenApiSchema schema, SchemaValidationDirection direction, string path, ValidationState state, int depth)
    {
        var target = schema is OpenApiSchemaReference reference ? (IOpenApiSchema?)reference.Target ?? schema : schema;
        if (state.Memo.TryGetValue((target, path), out var cached))
        {
            return cached;
        }

        var subErrors = new List<SchemaValidationError>();
        ValidateCore(value, schema, direction, path, subErrors, state, depth);
        state.Memo[(target, path)] = subErrors;
        return subErrors;
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
        ValidationState state,
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
                ValidateCore(property.Value, propertySchema, direction, propertyPath, errors, state, depth + 1);
            }
            else if (schema.AdditionalProperties is not null)
            {
                ValidateCore(property.Value, schema.AdditionalProperties, direction, propertyPath, errors, state, depth + 1);
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
