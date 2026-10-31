// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Proxy.Http;
using DevProxy.Abstractions.Utils;
using DevProxy.Plugins.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DevProxy.Plugins.Reporting;

public sealed class OpenApiValidationPluginConfiguration
{
    public bool ReportUnmatchedRequests { get; set; }
    public IEnumerable<string>? SpecFiles { get; set; }
    public string? SpecsFolder { get; set; }
}

public sealed class OpenApiValidationPlugin(
    HttpClient httpClient,
    ILogger<OpenApiValidationPlugin> logger,
    ISet<UrlToWatch> urlsToWatch,
    IProxyConfiguration proxyConfiguration,
    IConfigurationSection pluginConfigurationSection) :
    BaseReportingPlugin<OpenApiValidationPluginConfiguration>(
        httpClient,
        logger,
        urlsToWatch,
        proxyConfiguration,
        pluginConfigurationSection)
{
    // headers that OpenAPI says to ignore when defined as header parameters
    private static readonly string[] _ignoredHeaderParameters = ["Accept", "Content-Type", "Authorization"];

    private readonly ConditionalWeakTable<IProxySession, ValidationOutcome> _outcomes = [];
    private List<LoadedSpec> _specs = [];

    public override string Name => nameof(OpenApiValidationPlugin);

    public override async Task InitializeAsync(InitArgs e, CancellationToken cancellationToken)
    {
        await base.InitializeAsync(e, cancellationToken);

        var specFiles = new List<string>();

        foreach (var specFile in Configuration.SpecFiles ?? [])
        {
            if (string.IsNullOrWhiteSpace(specFile))
            {
                continue;
            }

            var fullPath = ProxyUtils.GetFullPath(specFile, ProxyConfiguration.ConfigFile);
            if (!File.Exists(fullPath))
            {
                Enabled = false;
                throw new InvalidOperationException($"Spec file '{fullPath}' does not exist.");
            }
            specFiles.Add(fullPath);
        }

        if (!string.IsNullOrWhiteSpace(Configuration.SpecsFolder))
        {
            var folderPath = ProxyUtils.GetFullPath(Configuration.SpecsFolder, ProxyConfiguration.ConfigFile);
            if (!Directory.Exists(folderPath))
            {
                Enabled = false;
                throw new InvalidOperationException($"Specs folder '{folderPath}' does not exist.");
            }

            specFiles.AddRange(Directory
                .EnumerateFiles(folderPath, "*.*", SearchOption.AllDirectories)
                .Where(IsSpecFile)
                .Order(StringComparer.Ordinal));
        }

        if (Configuration.SpecFiles?.Any() != true && string.IsNullOrWhiteSpace(Configuration.SpecsFolder))
        {
            Enabled = false;
            throw new InvalidOperationException("Specify at least one OpenAPI spec using specFiles or specsFolder.");
        }

        _specs = await LoadSpecsAsync(specFiles.Distinct(StringComparer.Ordinal), cancellationToken);
        if (_specs.Count == 0)
        {
            Logger.LogWarning("No valid OpenAPI specs with absolute server URLs found. Requests won't be validated.");
        }
    }

    public override Task AfterResponseAsync(ProxyResponseArgs e, CancellationToken cancellationToken)
    {
        Logger.LogTrace("{Method} called", nameof(AfterResponseAsync));

        ArgumentNullException.ThrowIfNull(e);

        var loggingContext = new LoggingContext(e.ProxySession);
        if (!e.HasRequestUrlMatch(UrlsToWatch))
        {
            Logger.LogRequest("URL not matched", MessageType.Skipped, loggingContext);
            return Task.CompletedTask;
        }
        if (IsCorsPreflight(e.ProxySession.Request))
        {
            Logger.LogRequest("Skipping CORS preflight request", MessageType.Skipped, loggingContext);
            return Task.CompletedTask;
        }

        var outcome = GetOrValidate(e.ProxySession);
        if (outcome.Result is null)
        {
            Logger.LogRequest("No matching OpenAPI spec", MessageType.Skipped, loggingContext);
        }
        else if (outcome.Result.Issues.Any())
        {
            foreach (var issue in outcome.Result.Issues)
            {
                Logger.LogRequest(
                    $"OpenAPI {issue.Location.ToString().ToLowerInvariant()} validation: {issue}",
                    MessageType.Warning,
                    loggingContext);
            }
        }
        else
        {
            Logger.LogRequest($"Request and response match {outcome.Result.Operation}", MessageType.Processed, loggingContext);
        }

        Logger.LogTrace("Left {Name}", nameof(AfterResponseAsync));
        return Task.CompletedTask;
    }

    public override Task AfterRecordingStopAsync(RecordingArgs e, CancellationToken cancellationToken)
    {
        Logger.LogTrace("{Method} called", nameof(AfterRecordingStopAsync));

        ArgumentNullException.ThrowIfNull(e);

        var sessions = e.RequestLogs
            .Where(l =>
                l.MessageType == MessageType.InterceptedResponse &&
                l.Context?.Session is not null &&
                l.Context.Session.HasResponse &&
                ProxyUtils.MatchesUrlToWatch(UrlsToWatch, l.Context.Session.Request.RequestUri.AbsoluteUri) &&
                !IsCorsPreflight(l.Context.Session.Request))
            .Select(l => l.Context!.Session)
            .Distinct();

        if (!sessions.Any())
        {
            Logger.LogRequest("No requests to process", MessageType.Skipped);
            return Task.CompletedTask;
        }

        var validated = 0;
        var results = new List<OpenApiValidationResult>();
        var unmatched = new List<string>();

        foreach (var session in sessions)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var outcome = GetOrValidate(session);
            if (outcome.Result is null)
            {
                unmatched.Add($"{session.Request.Method} {session.Request.Url}");
                continue;
            }

            validated++;
            if (outcome.Result.Issues.Any())
            {
                results.Add(outcome.Result);
            }
        }

        if (Logger.IsEnabled(LogLevel.Information))
        {
            Logger.LogInformation(
                "Validated {Validated} request(s) against OpenAPI specs. {WithIssues} request(s) with issues.",
                validated,
                results.Count);
        }

        var report = new OpenApiValidationPluginReport
        {
            ValidatedRequests = validated,
            Results = results,
            UnmatchedRequests = Configuration.ReportUnmatchedRequests ? [.. unmatched.Distinct()] : null
        };
        StoreReport(report, e);

        Logger.LogTrace("Left {Name}", nameof(AfterRecordingStopAsync));
        return Task.CompletedTask;
    }

    internal OpenApiValidationResult? Validate(IProxySession session) => GetOrValidate(session).Result;

    private ValidationOutcome GetOrValidate(IProxySession session) =>
        _outcomes.GetValue(session, s => new ValidationOutcome(ValidateSession(s)));

    private OpenApiValidationResult? ValidateSession(IProxySession session)
    {
        var request = session.Request;
        var response = session.Response;
        var requestText = $"{request.Method} {request.Url}";
        int? statusCode = response is null ? null : (int)response.StatusCode;

        var match = FindOperation(request);
        if (match is null)
        {
            return null;
        }

        var issues = new List<OpenApiValidationIssue>();
        var specFile = match.Spec.FilePath;

        if (match.PathTemplate is null)
        {
            issues.Add(new()
            {
                Location = OpenApiValidationIssueLocation.Request,
                Message = $"Path '{match.RelativePath}' isn't documented in the spec"
            });
            return new()
            {
                Request = requestText,
                StatusCode = statusCode,
                SpecFile = specFile,
                Issues = issues
            };
        }

        if (match.Operation is null)
        {
            issues.Add(new()
            {
                Location = OpenApiValidationIssueLocation.Request,
                Message = $"Method {request.Method} isn't documented for path '{match.PathTemplate}'"
            });
            return new()
            {
                Request = requestText,
                StatusCode = statusCode,
                SpecFile = specFile,
                Issues = issues
            };
        }

        ValidateParameters(request, match, issues);
        ValidateRequestBody(request, match.Operation, issues);
        if (response is not null)
        {
            ValidateResponse(response, match.Operation, issues);
        }

        return new()
        {
            Request = requestText,
            StatusCode = statusCode,
            Operation = $"{request.Method} {match.PathTemplate}",
            SpecFile = specFile,
            Issues = issues
        };
    }

    private OperationMatch? FindOperation(IHttpRequest request)
    {
        var requestUrl = request.RequestUri.GetLeftPart(UriPartial.Path);
        OperationMatch? firstServerMatch = null;

        foreach (var spec in _specs)
        {
            foreach (var serverRegex in spec.ServerRegexes)
            {
                var serverMatch = serverRegex.Match(requestUrl);
                if (!serverMatch.Success)
                {
                    continue;
                }

                var relativePath = requestUrl[serverMatch.Length..];
                if (relativePath.Length > 0 && relativePath[0] != '/')
                {
                    // server URL matched only part of a path segment
                    continue;
                }
                if (relativePath.Length == 0)
                {
                    relativePath = "/";
                }

                var pathMatch = FindPath(spec, relativePath);
                if (pathMatch is null)
                {
                    firstServerMatch ??= new OperationMatch(spec, relativePath, null, null, null, []);
                    continue;
                }

                var (template, pathItem, pathValues) = pathMatch.Value;
                OpenApiOperation? operation = null;
                _ = pathItem.Operations?.TryGetValue(new HttpMethod(request.Method), out operation);
                return new OperationMatch(spec, relativePath, template, pathItem, operation, pathValues);
            }
        }

        return firstServerMatch;
    }

    private static (string Template, IOpenApiPathItem PathItem, Dictionary<string, string> PathValues)? FindPath(LoadedSpec spec, string relativePath)
    {
        // prefer literal paths over templated paths (e.g. /users/me over /users/{id}),
        // and among templated paths, those with fewer parameters
        foreach (var path in spec.Paths)
        {
            var m = path.Regex.Match(relativePath);
            if (!m.Success)
            {
                continue;
            }

            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < path.ParameterNames.Count; i++)
            {
                values[path.ParameterNames[i]] = Uri.UnescapeDataString(m.Groups[i + 1].Value);
            }
            return (path.Template, path.PathItem, values);
        }

        return null;
    }

    private static void ValidateParameters(IHttpRequest request, OperationMatch match, List<OpenApiValidationIssue> issues)
    {
        var parameters = GetEffectiveParameters(match.PathItem!, match.Operation!);
        var query = ParseQueryString(request.RequestUri.Query);

        foreach (var parameter in parameters)
        {
            if (parameter.Name is null || parameter.In is null)
            {
                continue;
            }

            List<string>? values = null;
            switch (parameter.In)
            {
                case ParameterLocation.Path:
                    if (match.PathValues.TryGetValue(parameter.Name, out var pathValue))
                    {
                        values = [pathValue];
                    }
                    break;
                case ParameterLocation.Query:
                    if (query.TryGetValue(parameter.Name, out var queryValues))
                    {
                        values = queryValues;
                    }
                    break;
                case ParameterLocation.Header:
                    if (_ignoredHeaderParameters.Contains(parameter.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    var headerValues = request.Headers.GetAll(parameter.Name).Select(h => h.Value).ToList();
                    if (headerValues.Count > 0)
                    {
                        values = headerValues;
                    }
                    break;
                default:
                    // cookie and querystring parameters aren't validated
                    continue;
            }

            var locationName = parameter.In.Value.ToString().ToLowerInvariant();
            if (values is null)
            {
                if (parameter.Required)
                {
                    issues.Add(new()
                    {
                        Location = OpenApiValidationIssueLocation.Request,
                        Path = parameter.Name,
                        Message = $"Missing required {locationName} parameter '{parameter.Name}'"
                    });
                }
                continue;
            }

            ValidateParameterValue(parameter.Name, $"{locationName} parameter", values, parameter.Schema,
                OpenApiValidationIssueLocation.Request, issues);
        }

        // unknown query parameters
        var hasQueryStringParameter = parameters.Any(p => p.In == ParameterLocation.QueryString);
        if (hasQueryStringParameter)
        {
            return;
        }

        var knownQueryParameters = parameters
            .Where(p => p.In == ParameterLocation.Query && p.Name is not null)
            .Select(p => p.Name!)
            .ToArray();
        foreach (var name in query.Keys)
        {
            if (knownQueryParameters.Contains(name, StringComparer.Ordinal) ||
                // deepObject style, e.g. filter[name]=value
                knownQueryParameters.Any(p => name.StartsWith($"{p}[", StringComparison.Ordinal)))
            {
                continue;
            }

            issues.Add(new()
            {
                Location = OpenApiValidationIssueLocation.Request,
                Path = name,
                Message = $"Query parameter '{name}' isn't documented"
            });
        }
    }

    private static void ValidateParameterValue(string name, string kind, List<string> values, IOpenApiSchema? schema,
        OpenApiValidationIssueLocation location, List<OpenApiValidationIssue> issues)
    {
        if (schema is null || schema.Type == JsonSchemaType.Object)
        {
            return;
        }

        var value = OpenApiSchemaValidator.CoerceParameterValue(values, schema);
        var direction = location == OpenApiValidationIssueLocation.Request
            ? SchemaValidationDirection.Request
            : SchemaValidationDirection.Response;
        foreach (var error in OpenApiSchemaValidator.Validate(value, schema, direction, name))
        {
            issues.Add(new()
            {
                Location = location,
                Path = error.Path,
                Message = $"Invalid {kind} value '{string.Join(",", values)}': {error.Message}"
            });
        }
    }

    private static void ValidateRequestBody(IHttpRequest request, OpenApiOperation operation, List<OpenApiValidationIssue> issues)
    {
        var requestBody = operation.RequestBody;
        var hasBody = request.Body.Length > 0;

        if (!hasBody)
        {
            if (requestBody?.Required == true && !request.HasBody)
            {
                issues.Add(new()
                {
                    Location = OpenApiValidationIssueLocation.Request,
                    Message = "Missing required request body"
                });
            }
            return;
        }

        if (requestBody is null)
        {
            issues.Add(new()
            {
                Location = OpenApiValidationIssueLocation.Request,
                Message = "Request body isn't documented for this operation"
            });
            return;
        }

        ValidateBody(request, requestBody.Content, OpenApiValidationIssueLocation.Request, issues);
    }

    private static void ValidateResponse(IHttpResponse response, OpenApiOperation operation, List<OpenApiValidationIssue> issues)
    {
        var statusCode = ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);
        var responses = operation.Responses;
        IOpenApiResponse? specResponse = null;
        if (responses is not null)
        {
            specResponse =
                responses.FirstOrDefault(r => r.Key == statusCode).Value ??
                responses.FirstOrDefault(r => string.Equals(r.Key, $"{statusCode[0]}XX", StringComparison.OrdinalIgnoreCase)).Value ??
                responses.FirstOrDefault(r => string.Equals(r.Key, "default", StringComparison.OrdinalIgnoreCase)).Value;
        }

        if (specResponse is null)
        {
            issues.Add(new()
            {
                Location = OpenApiValidationIssueLocation.Response,
                Message = $"Status code {statusCode} isn't documented for this operation"
            });
            return;
        }

        foreach (var (headerName, header) in specResponse.Headers ?? new Dictionary<string, IOpenApiHeader>())
        {
            if (string.Equals(headerName, "Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var values = response.Headers.GetAll(headerName).Select(h => h.Value).ToList();
            if (values.Count == 0)
            {
                if (header.Required)
                {
                    issues.Add(new()
                    {
                        Location = OpenApiValidationIssueLocation.Response,
                        Path = headerName,
                        Message = $"Missing required response header '{headerName}'"
                    });
                }
                continue;
            }

            ValidateParameterValue(headerName, "response header", values, header.Schema,
                OpenApiValidationIssueLocation.Response, issues);
        }

        if (response.Body.Length == 0)
        {
            return;
        }

        if (specResponse.Content is not { Count: > 0 })
        {
            issues.Add(new()
            {
                Location = OpenApiValidationIssueLocation.Response,
                Message = $"Response body isn't documented for status code {statusCode}"
            });
            return;
        }

        ValidateBody(response, specResponse.Content, OpenApiValidationIssueLocation.Response, issues);
    }

    private static void ValidateBody(IHttpMessage message, IDictionary<string, IOpenApiMediaType>? content,
        OpenApiValidationIssueLocation location, List<OpenApiValidationIssue> issues)
    {
        if (content is not { Count: > 0 })
        {
            return;
        }

        var locationName = location.ToString().ToLowerInvariant();
        var contentType = GetMediaType(message.ContentType);
        if (contentType is null)
        {
            issues.Add(new()
            {
                Location = location,
                Message = $"The {locationName} has a body but no Content-Type header"
            });
            return;
        }

        var mediaType = FindMediaType(content, contentType);
        if (mediaType is null)
        {
            issues.Add(new()
            {
                Location = location,
                Message = $"Content type '{contentType}' isn't documented. Documented: {string.Join(", ", content.Keys)}"
            });
            return;
        }

        if (mediaType.Schema is null || !IsJsonMediaType(contentType))
        {
            return;
        }

        JsonDocument json;
        try
        {
            json = JsonDocument.Parse(message.Body);
        }
        catch (JsonException ex)
        {
            issues.Add(new()
            {
                Location = location,
                Message = $"The {locationName} body isn't valid JSON: {ex.Message}"
            });
            return;
        }

        using (json)
        {
            var direction = location == OpenApiValidationIssueLocation.Request
                ? SchemaValidationDirection.Request
                : SchemaValidationDirection.Response;
            foreach (var error in OpenApiSchemaValidator.Validate(json.RootElement, mediaType.Schema, direction))
            {
                issues.Add(new()
                {
                    Location = location,
                    Path = error.Path,
                    Message = error.Message
                });
            }
        }
    }

    private static IOpenApiMediaType? FindMediaType(IDictionary<string, IOpenApiMediaType> content, string contentType)
    {
        var entries = content
            .Select(c => (MediaType: GetMediaType(c.Key) ?? c.Key, c.Value))
            .ToArray();

        var exact = entries.FirstOrDefault(c => string.Equals(c.MediaType, contentType, StringComparison.OrdinalIgnoreCase));
        if (exact.Value is not null)
        {
            return exact.Value;
        }

        var typeWildcard = $"{contentType.Split('/')[0]}/*";
        var wildcard = entries.FirstOrDefault(c => string.Equals(c.MediaType, typeWildcard, StringComparison.OrdinalIgnoreCase));
        if (wildcard.Value is not null)
        {
            return wildcard.Value;
        }

        return entries.FirstOrDefault(c => c.MediaType == "*/*").Value;
    }

    private static string? GetMediaType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return null;
        }

        return contentType.Split(';')[0].Trim().ToLowerInvariant();
    }

    private static bool IsJsonMediaType(string mediaType) =>
        mediaType == "application/json" ||
        mediaType.EndsWith("+json", StringComparison.Ordinal);

    private static List<IOpenApiParameter> GetEffectiveParameters(IOpenApiPathItem pathItem, OpenApiOperation operation)
    {
        var parameters = new List<IOpenApiParameter>();
        parameters.AddRange(pathItem.Parameters ?? []);

        // operation-level parameters override path-level parameters with the same name and location
        foreach (var parameter in operation.Parameters ?? [])
        {
            _ = parameters.RemoveAll(p => p.Name == parameter.Name && p.In == parameter.In);
            parameters.Add(parameter);
        }

        return parameters;
    }

    private static Dictionary<string, List<string>> ParseQueryString(string query)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(query))
        {
            return result;
        }

        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separatorIndex = pair.IndexOf('=', StringComparison.Ordinal);
            var name = Unescape(separatorIndex < 0 ? pair : pair[..separatorIndex]);
            var value = separatorIndex < 0 ? string.Empty : Unescape(pair[(separatorIndex + 1)..]);

            if (!result.TryGetValue(name, out var values))
            {
                values = [];
                result[name] = values;
            }
            values.Add(value);
        }

        return result;
    }

    private static string Unescape(string value) =>
        Uri.UnescapeDataString(value.Replace('+', ' '));

    private static bool IsCorsPreflight(IHttpRequest request) =>
        string.Equals(request.Method, "OPTIONS", StringComparison.OrdinalIgnoreCase) &&
        request.Headers.Contains("Access-Control-Request-Method");

    private static bool IsSpecFile(string file)
    {
        var extension = Path.GetExtension(file);
        return extension.Equals(".json", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".yml", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<List<LoadedSpec>> LoadSpecsAsync(IEnumerable<string> files, CancellationToken cancellationToken)
    {
        var specs = new List<LoadedSpec>();

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Logger.LogDebug("Loading OpenAPI spec from '{File}'...", file);
            OpenApiDocument? document;
            string? diagnostics;
            try
            {
                var fileContents = await File.ReadAllTextAsync(file, cancellationToken);
                fileContents = ProxyUtils.ReplaceVariables(fileContents, ProxyConfiguration.Env, v => $"{{{v}}}");

                var settings = new OpenApiReaderSettings();
                settings.AddYamlReader();
                var readResult = OpenApiDocument.Parse(fileContents, null, settings);
                document = readResult.Document;
                diagnostics = readResult.Diagnostic?.Errors is { Count: > 0 } errors
                    ? string.Join("; ", errors.Select(e => e.ToString()))
                    : null;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to load OpenAPI spec from '{File}'", file);
                continue;
            }

            if (document is null)
            {
                Logger.LogWarning("File '{File}' isn't a valid OpenAPI spec. Skipping. {Diagnostics}", file, diagnostics);
                continue;
            }

            var serverRegexes = new List<Regex>();
            foreach (var server in document.Servers ?? [])
            {
                if (string.IsNullOrWhiteSpace(server.Url) ||
                    !server.Url.Contains("://", StringComparison.Ordinal))
                {
                    Logger.LogDebug("Skipping server '{ServerUrl}' in '{File}' because it isn't an absolute URL", server.Url, file);
                    continue;
                }

                var pattern = Regex.Replace(Regex.Escape(server.Url.TrimEnd('/')), @"\\\{[^}]+\}", "[^/]*");
                serverRegexes.Add(new Regex($"^{pattern}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
            }

            if (serverRegexes.Count == 0)
            {
                Logger.LogWarning("OpenAPI spec '{File}' doesn't define any absolute server URLs. Skipping", file);
                continue;
            }

            var paths = (document.Paths ?? [])
                .Select(p => CompilePath(p.Key, p.Value))
                .OrderBy(p => p.ParameterNames.Count)
                .ToList();

            specs.Add(new LoadedSpec(file, document, serverRegexes, paths));
            Logger.LogInformation("Loaded OpenAPI spec '{File}'", file);
        }

        return specs;
    }

    private static CompiledPath CompilePath(string template, IOpenApiPathItem pathItem)
    {
        var parameterNames = new List<string>();
        var pattern = Regex.Replace(Regex.Escape(template), @"\\\{([^}]+)\}", m =>
        {
            parameterNames.Add(Regex.Unescape(m.Groups[1].Value));
            return "([^/]+)";
        });
        var regex = new Regex($"^{pattern}/?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return new CompiledPath(template, pathItem, regex, parameterNames);
    }

    private sealed record CompiledPath(string Template, IOpenApiPathItem PathItem, Regex Regex, List<string> ParameterNames);

    private sealed record LoadedSpec(string FilePath, OpenApiDocument Document, List<Regex> ServerRegexes, List<CompiledPath> Paths);

    private sealed record OperationMatch(
        LoadedSpec Spec,
        string RelativePath,
        string? PathTemplate,
        IOpenApiPathItem? PathItem,
        OpenApiOperation? Operation,
        Dictionary<string, string> PathValues);

    private sealed record ValidationOutcome(OpenApiValidationResult? Result);
}
