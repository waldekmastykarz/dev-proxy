// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using DevProxy.Abstractions.Proxy;
using DevProxy.Abstractions.Utils;
using DevProxy.Plugins.Reporting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevProxy.Integration.Tests;

/// <summary>
/// Coverage for <see cref="OpenApiValidationPlugin"/>, which validates intercepted
/// requests and responses against local OpenAPI specs.
/// </summary>
public sealed class OpenApiValidationPluginTests : IDisposable
{
    private const string Spec = """
    {
      "openapi": "3.0.3",
      "info": { "title": "Contoso", "version": "1.0" },
      "servers": [ { "url": "https://api.contoso.com/v1" } ],
      "paths": {
        "/users": {
          "get": {
            "parameters": [
              { "name": "top", "in": "query", "schema": { "type": "integer" } },
              { "name": "status", "in": "query", "schema": { "type": "string", "enum": [ "active", "inactive" ] } },
              { "name": "x-tenant", "in": "header", "required": true, "schema": { "type": "string" } }
            ],
            "responses": {
              "200": {
                "description": "ok",
                "headers": {
                  "x-total-count": { "required": true, "schema": { "type": "integer" } }
                },
                "content": {
                  "application/json": {
                    "schema": { "type": "array", "items": { "$ref": "#/components/schemas/User" } }
                  }
                }
              },
              "4XX": {
                "description": "client error",
                "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Error" } } }
              }
            }
          },
          "post": {
            "requestBody": {
              "required": true,
              "content": {
                "application/json": { "schema": { "$ref": "#/components/schemas/User" } }
              }
            },
            "responses": {
              "201": {
                "description": "created",
                "content": { "application/json": { "schema": { "$ref": "#/components/schemas/User" } } }
              }
            }
          }
        },
        "/users/{id}": {
          "parameters": [ { "name": "id", "in": "path", "required": true, "schema": { "type": "integer" } } ],
          "get": {
            "responses": {
              "200": {
                "description": "ok",
                "content": { "application/json": { "schema": { "$ref": "#/components/schemas/User" } } }
              }
            }
          }
        },
        "/users/me": {
          "get": {
            "responses": {
              "200": {
                "description": "ok",
                "content": { "application/json": { "schema": { "$ref": "#/components/schemas/User" } } }
              }
            }
          }
        },
        "/pets": {
          "get": {
            "responses": {
              "default": {
                "description": "pets",
                "content": {
                  "application/json": {
                    "schema": {
                      "type": "array",
                      "items": {
                        "oneOf": [ { "$ref": "#/components/schemas/Cat" }, { "$ref": "#/components/schemas/Dog" } ]
                      }
                    }
                  }
                }
              }
            }
          }
        }
      },
      "components": {
        "schemas": {
          "User": {
            "type": "object",
            "required": [ "id", "name" ],
            "additionalProperties": false,
            "properties": {
              "id": { "type": "integer", "readOnly": true },
              "name": { "type": "string" },
              "nickname": { "type": "string", "nullable": true },
              "role": { "type": "string", "enum": [ "admin", "user" ] },
              "tags": { "type": "array", "items": { "type": "string" } }
            }
          },
          "Error": {
            "type": "object",
            "required": [ "message" ],
            "properties": { "message": { "type": "string" } }
          },
          "Cat": {
            "type": "object",
            "required": [ "meows" ],
            "additionalProperties": false,
            "properties": { "meows": { "type": "boolean" } }
          },
          "Dog": {
            "type": "object",
            "required": [ "barks" ],
            "additionalProperties": false,
            "properties": { "barks": { "type": "boolean" } }
          }
        }
      }
    }
    """;

    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("devproxy-oasvalidation-");

    public void Dispose() => _dir.Delete(recursive: true);

    private static RecordingArgs Recording(IEnumerable<RequestLog> logs) =>
        new(logs)
        {
            GlobalData = new() { [ProxyUtils.ReportsKey] = new Dictionary<string, object>() },
        };

    private async Task<OpenApiValidationPlugin> CreatePluginAsync(string configJson = """{ "specFiles": [ "contoso.json" ] }""")
    {
        await File.WriteAllTextAsync(Path.Combine(_dir.FullName, "contoso.json"), Spec);

        var proxyConfig = new TestProxyConfiguration
        {
            ConfigFile = Path.Combine(_dir.FullName, "devproxyrc.json"),
        };
        var plugin = new OpenApiValidationPlugin(
            TestDefaults.HttpClient,
            NullLogger<OpenApiValidationPlugin>.Instance,
            KestrelProxyHarness.BuildUrlsToWatch("api.contoso.com"),
            proxyConfig,
            PluginConfig.FromJson(configJson));

        using var host = new PluginTestHost(proxyConfig);
        await plugin.InitializeAsync(host.CreateInitArgs(), CancellationToken.None);
        return plugin;
    }

    private static TestExchange Get(string url, HttpStatusCode status, string? body,
        IEnumerable<(string, string)>? requestHeaders = null,
        IEnumerable<(string, string)>? responseHeaders = null) =>
        TestExchange
            .Request("GET", url, headers: requestHeaders)
            .WithResponse(
                status,
                headers: [("Content-Type", "application/json"), .. responseHeaders ?? []],
                body: body);

    private static string[] Messages(OpenApiValidationResult? result) =>
        [.. Assert.IsType<OpenApiValidationResult>(result).Issues.Select(i => i.ToString())];

    [Fact]
    public async Task ValidRequestAndResponse_HasNoIssues()
    {
        var plugin = await CreatePluginAsync();
        var exchange = Get("https://api.contoso.com/v1/users?top=10&status=active", HttpStatusCode.OK,
            """[ { "id": 1, "name": "Alex", "nickname": null, "role": "admin", "tags": [ "a" ] } ]""",
            requestHeaders: [("x-tenant", "contoso")],
            responseHeaders: [("x-total-count", "1")]);

        var result = plugin.Validate(exchange.Session);

        Assert.NotNull(result);
        Assert.Equal("GET /users", result.Operation);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public async Task ReportsParameterIssues()
    {
        var plugin = await CreatePluginAsync();
        var exchange = Get("https://api.contoso.com/v1/users?top=abc&status=deleted&foo=bar", HttpStatusCode.OK,
            "[]", responseHeaders: [("x-total-count", "0")]);

        var messages = Messages(plugin.Validate(exchange.Session));

        Assert.Contains(messages, m => m.Contains("Missing required header parameter 'x-tenant'", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.StartsWith("top:", StringComparison.Ordinal) && m.Contains("Expected integer", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.StartsWith("status:", StringComparison.Ordinal) && m.Contains("allowed values", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("Query parameter 'foo' isn't documented", StringComparison.Ordinal));
        Assert.Equal(4, messages.Length);
    }

    [Fact]
    public async Task ReportsInvalidPathParameter()
    {
        var plugin = await CreatePluginAsync();
        var exchange = Get("https://api.contoso.com/v1/users/abc", HttpStatusCode.OK, """{ "id": 1, "name": "Alex" }""");

        var result = plugin.Validate(exchange.Session);

        Assert.Equal("GET /users/{id}", result?.Operation);
        var issue = Assert.Single(result!.Issues);
        Assert.Equal("id", issue.Path);
        Assert.Contains("Expected integer", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrefersLiteralPathOverTemplate()
    {
        var plugin = await CreatePluginAsync();
        var exchange = Get("https://api.contoso.com/v1/users/me", HttpStatusCode.OK, """{ "id": 1, "name": "Alex" }""");

        var result = plugin.Validate(exchange.Session);

        Assert.Equal("GET /users/me", result?.Operation);
        Assert.Empty(result!.Issues);
    }

    [Fact]
    public async Task ReportsUndocumentedPathAndMethod()
    {
        var plugin = await CreatePluginAsync();

        var path = plugin.Validate(Get("https://api.contoso.com/v1/orders", HttpStatusCode.OK, "[]").Session);
        var method = plugin.Validate(TestExchange.Request("DELETE", "https://api.contoso.com/v1/users").WithResponse(HttpStatusCode.NoContent).Session);

        Assert.Contains("Path '/orders' isn't documented", Assert.Single(Messages(path)), StringComparison.Ordinal);
        Assert.Contains("Method DELETE isn't documented for path '/users'", Assert.Single(Messages(method)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportsRequestBodyIssues()
    {
        var plugin = await CreatePluginAsync();
        var exchange = TestExchange
            .Request("POST", "https://api.contoso.com/v1/users",
                headers: [("Content-Type", "application/json")],
                body: """{ "name": 42, "role": "guest", "unknown": true }""")
            .WithResponse(HttpStatusCode.Created, headers: [("Content-Type", "application/json")],
                body: """{ "id": 1, "name": "Alex" }""");

        var result = plugin.Validate(exchange.Session);
        var messages = Messages(result);

        // id is readOnly so it's not required in the request
        Assert.DoesNotContain(messages, m => m.Contains("'id'", StringComparison.Ordinal));
        Assert.Contains("$.name: Expected string but got integer", messages);
        Assert.Contains(messages, m => m.StartsWith("$.role:", StringComparison.Ordinal));
        Assert.Contains("$.unknown: Property 'unknown' isn't defined in the schema", messages);
        Assert.All(result!.Issues, i => Assert.Equal(OpenApiValidationIssueLocation.Request, i.Location));
    }

    [Fact]
    public async Task ReportsMissingRequiredRequestBodyAndUndocumentedContentType()
    {
        var plugin = await CreatePluginAsync();

        var missing = plugin.Validate(TestExchange.Request("POST", "https://api.contoso.com/v1/users")
            .WithResponse(HttpStatusCode.Created).Session);
        var xml = plugin.Validate(TestExchange.Request("POST", "https://api.contoso.com/v1/users",
                headers: [("Content-Type", "application/xml")], body: "<user />")
            .WithResponse(HttpStatusCode.Created).Session);

        Assert.Equal("Missing required request body", Assert.Single(Messages(missing)));
        Assert.Contains("Content type 'application/xml' isn't documented", Assert.Single(Messages(xml)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportsResponseIssues()
    {
        var plugin = await CreatePluginAsync();
        var headers = new[] { ("x-tenant", "contoso") };

        var undocumentedStatus = plugin.Validate(Get("https://api.contoso.com/v1/users", HttpStatusCode.InternalServerError, "{}", headers).Session);
        var badBody = plugin.Validate(Get("https://api.contoso.com/v1/users", HttpStatusCode.OK, """[ { "id": "1" } ]""", headers,
            responseHeaders: [("x-total-count", "1")]).Session);
        var missingHeader = plugin.Validate(Get("https://api.contoso.com/v1/users", HttpStatusCode.OK, "[]", headers).Session);
        var range = plugin.Validate(Get("https://api.contoso.com/v1/users", HttpStatusCode.NotFound, """{ "message": "nope" }""", headers).Session);

        Assert.Contains("Status code 500 isn't documented", Assert.Single(Messages(undocumentedStatus)), StringComparison.Ordinal);
        var badBodyMessages = Messages(badBody);
        Assert.Contains("$[0].id: Expected integer but got string", badBodyMessages);
        Assert.Contains("$[0]: Missing required property 'name'", badBodyMessages);
        Assert.Contains("Missing required response header 'x-total-count'", Assert.Single(Messages(missingHeader)), StringComparison.Ordinal);
        Assert.Empty(range!.Issues);
    }

    [Fact]
    public async Task ValidatesOneOf()
    {
        var plugin = await CreatePluginAsync();

        var valid = plugin.Validate(Get("https://api.contoso.com/v1/pets", HttpStatusCode.OK, """[ { "meows": true }, { "barks": false } ]""").Session);
        var invalid = plugin.Validate(Get("https://api.contoso.com/v1/pets", HttpStatusCode.OK, """[ { "quacks": true } ]""").Session);

        Assert.Empty(valid!.Issues);
        Assert.Contains("$[0]: Value doesn't match any of the schemas in 'oneOf'", Messages(invalid));
    }

    [Fact]
    public async Task IgnoresRequestsNotMatchingAnySpec()
    {
        var plugin = await CreatePluginAsync();

        Assert.Null(plugin.Validate(Get("https://api.contoso.com/v2/users", HttpStatusCode.OK, "[]").Session));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StoresReportOnRecordingStop(bool reportUnmatchedRequests)
    {
        var plugin = await CreatePluginAsync($$"""{ "specsFolder": ".", "reportUnmatchedRequests": {{(reportUnmatchedRequests ? "true" : "false")}} }""");
        var args = Recording([
            Get("https://api.contoso.com/v1/users/1", HttpStatusCode.OK, """{ "id": 1, "name": "Alex" }""").AsRequestLog(MessageType.InterceptedResponse),
            Get("https://api.contoso.com/v1/users/1", HttpStatusCode.OK, """{ "id": 1 }""").AsRequestLog(MessageType.InterceptedResponse),
            Get("https://api.contoso.com/other", HttpStatusCode.OK, "{}").AsRequestLog(MessageType.InterceptedResponse),
        ]);

        await plugin.AfterRecordingStopAsync(args, CancellationToken.None);

        var report = Assert.IsType<OpenApiValidationPluginReport>(
            ((Dictionary<string, object>)args.GlobalData[ProxyUtils.ReportsKey])[plugin.Name]);
        Assert.Equal(2, report.ValidatedRequests);
        var result = Assert.Single(report.Results);
        Assert.Equal("GET /users/{id}", result.Operation);
        if (reportUnmatchedRequests)
        {
            Assert.Equal(["GET https://api.contoso.com/other"], report.UnmatchedRequests);
        }
        else
        {
            Assert.Null(report.UnmatchedRequests);
        }
        Assert.Contains("Missing required property 'name'", report.ToMarkdown(), StringComparison.Ordinal);
        Assert.Contains("Missing required property 'name'", report.ToPlainText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadsYamlOpenApi31SpecsFromFolder()
    {
        var specsDir = Directory.CreateDirectory(Path.Combine(_dir.FullName, "specs", "nested"));
        await File.WriteAllTextAsync(Path.Combine(specsDir.FullName, "fabrikam.yaml"), """
        openapi: 3.1.0
        info:
          title: Fabrikam
          version: '1.0'
        servers:
          - url: https://{region}.fabrikam.com
            variables:
              region:
                default: eu
        paths:
          /items:
            get:
              responses:
                '200':
                  description: ok
                  content:
                    application/json:
                      schema:
                        type: object
                        properties:
                          name:
                            type: [string, 'null']
        """);
        var plugin = await CreatePluginAsync("""{ "specsFolder": "specs" }""");

        var valid = plugin.Validate(TestExchange.Request("GET", "https://eu.fabrikam.com/items")
            .WithResponse(HttpStatusCode.OK, headers: [("Content-Type", "application/json; charset=utf-8")], body: """{ "name": null }""").Session);
        var invalid = plugin.Validate(TestExchange.Request("GET", "https://eu.fabrikam.com/items")
            .WithResponse(HttpStatusCode.OK, headers: [("Content-Type", "application/json")], body: """{ "name": 1 }""").Session);

        Assert.Equal("GET /items", valid?.Operation);
        Assert.Empty(valid!.Issues);
        Assert.Contains("$.name: Expected string or null but got integer", Messages(invalid));
    }

    [Fact]
    public async Task ThrowsWhenNoSpecsConfigured()
    {
        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => CreatePluginAsync("{}"));
    }
}
