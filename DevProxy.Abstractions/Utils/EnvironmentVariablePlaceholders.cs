// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Extensions.Logging;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DevProxy.Abstractions.Utils;

/// <summary>
/// Resolves environment variable placeholders in user-authored files.
/// Supports <c>${NAME}</c> and <c>${env:NAME}</c>. Use <c>$${NAME}</c> to
/// output a literal <c>${NAME}</c>. Placeholders that reference variables
/// that aren't set are left as-is.
/// </summary>
public static partial class EnvironmentVariablePlaceholders
{
    private static readonly JsonSerializerOptions _writeOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// Returns true if the value contains something that looks like a placeholder or an escaped placeholder.
    /// </summary>
    public static bool ContainsPlaceholder(string? value) =>
        !string.IsNullOrEmpty(value) && PlaceholderRegex().IsMatch(value);

    /// <summary>
    /// Resolves placeholders in a string value using process environment variables.
    /// </summary>
    public static string Resolve(string value, ICollection<string>? unresolvedVariables = null) =>
        Resolve(value, Environment.GetEnvironmentVariable, unresolvedVariables);

    /// <summary>
    /// Resolves placeholders in a string value using the specified lookup.
    /// </summary>
    /// <param name="value">Value to resolve.</param>
    /// <param name="lookup">Function that returns the value of a variable or null if it isn't set.</param>
    /// <param name="unresolvedVariables">Collects names of variables that couldn't be resolved.</param>
    public static string Resolve(string value, Func<string, string?> lookup, ICollection<string>? unresolvedVariables = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(lookup);

        if (!value.Contains("${", StringComparison.Ordinal))
        {
            return value;
        }

        return PlaceholderRegex().Replace(value, match =>
        {
            var name = match.Groups["name"].Value;
            if (match.Groups["escape"].Success)
            {
                // $${NAME} -> literal ${NAME}
                return match.Value[1..];
            }

            var variableValue = lookup(name);
            if (variableValue is null)
            {
                if (unresolvedVariables is not null && !unresolvedVariables.Contains(name))
                {
                    unresolvedVariables.Add(name);
                }
                return match.Value;
            }

            return variableValue;
        });
    }

    /// <summary>
    /// Resolves placeholders in all string values of a JSON document. Property names aren't changed.
    /// Returns the original string if it doesn't contain any placeholders.
    /// </summary>
    public static string ResolveInJson(string json, Func<string, string?> lookup, ICollection<string>? unresolvedVariables = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(lookup);

        if (!ContainsPlaceholder(json))
        {
            return json;
        }

        var node = JsonNode.Parse(json, documentOptions: ProxyUtils.JsonDocumentOptions);
        if (node is null)
        {
            return json;
        }

        node = ResolveInJsonNode(node, lookup, unresolvedVariables);
        return node.ToJsonString(_writeOptions);
    }

    /// <summary>
    /// Resolves placeholders in all string values of a JSON document using process environment variables.
    /// </summary>
    public static string ResolveInJson(string json, ICollection<string>? unresolvedVariables = null) =>
        ResolveInJson(json, Environment.GetEnvironmentVariable, unresolvedVariables);

    /// <summary>
    /// Logs a warning for each environment variable that couldn't be resolved.
    /// </summary>
    public static void LogUnresolvedVariables(ILogger logger, IEnumerable<string> unresolvedVariables, string source)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(unresolvedVariables);

        foreach (var name in unresolvedVariables)
        {
            logger.LogWarning(
                "Environment variable {Name} referenced in {Source} isn't set. Leaving the placeholder as-is",
                name, source);
        }
    }

    private static JsonNode ResolveInJsonNode(JsonNode node, Func<string, string?> lookup, ICollection<string>? unresolvedVariables)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var property in obj.ToArray())
                {
                    if (property.Value is not null)
                    {
                        var resolved = ResolveInJsonNode(property.Value, lookup, unresolvedVariables);
                        if (!ReferenceEquals(resolved, property.Value))
                        {
                            obj[property.Key] = resolved;
                        }
                    }
                }
                return obj;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    var item = array[i];
                    if (item is not null)
                    {
                        var resolved = ResolveInJsonNode(item, lookup, unresolvedVariables);
                        if (!ReferenceEquals(resolved, item))
                        {
                            array[i] = resolved;
                        }
                    }
                }
                return array;
            case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                var s = value.GetValue<string>();
                var r = Resolve(s, lookup, unresolvedVariables);
                return string.Equals(s, r, StringComparison.Ordinal) ? value : JsonValue.Create(r);
            default:
                return node;
        }
    }

    [GeneratedRegex(@"(?<escape>\$)?\$\{(?:env:)?(?<name>[A-Za-z_][A-Za-z0-9_]*)\}")]
    private static partial Regex PlaceholderRegex();
}
