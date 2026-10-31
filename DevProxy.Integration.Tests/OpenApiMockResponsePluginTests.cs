// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using System.Text.Json.Nodes;
using DevProxy.Plugins.Mocking;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;
using Xunit;

namespace DevProxy.Integration.Tests;

/// <summary>
/// Coverage for <see cref="OpenApiMockResponsePlugin"/> served through the Kestrel engine
/// (with the language model disabled, so responses come from spec examples or the
/// schema-based fallback) and for <see cref="OpenApiSchemaSampleGenerator"/>.
/// </summary>
public sealed class OpenApiMockResponsePluginTests
{
    [Fact]
    public async Task TextResponseWithStringSchema_IsMocked()
    {
        var (status, body, received) = await GetAsync("/text-string", """
            content:
              text/plain:
                schema: { type: string }
            """);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("string", body);
        Assert.False(received);
    }

    [Fact]
    public async Task TextResponseWithObjectSchema_PassesThrough()
    {
        var (_, _, received) = await GetAsync("/text-object", """
            content:
              text/plain:
                schema: { type: object, properties: { a: { type: string } } }
            """);

        Assert.True(received);
    }

    [Fact]
    public async Task TextResponseWithNonStringExample_PassesThrough()
    {
        var (_, _, received) = await GetAsync("/text-number", """
            content:
              text/plain:
                example: 42
            """);

        Assert.True(received);
    }

    [Fact]
    public async Task XmlResponseWithStringExample_IsMocked()
    {
        var (status, body, received) = await GetAsync("/xml", """
            content:
              application/xml:
                example: '<a>1</a>'
            """);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("<a>1</a>", body);
        Assert.False(received);
    }

    [Fact]
    public async Task JsonResponseWithObjectSchema_IsMocked()
    {
        var (status, body, received) = await GetAsync("/json", """
            content:
              application/json:
                schema: { type: object, properties: { name: { type: string } } }
            """);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("string", JsonNode.Parse(body)!["name"]!.GetValue<string>());
        Assert.False(received);
    }

    [Theory]
    [InlineData("{ type: array, minItems: 3, items: { type: integer } }", 3)]
    [InlineData("{ type: array, items: { type: integer } }", 1)]
    [InlineData("{ type: array, minItems: 5, maxItems: 2, items: { type: integer } }", 2)]
    [InlineData("{ type: array, maxItems: 0, items: { type: integer } }", 0)]
    public void GenerateSample_HonorsMinItems(string schema, int expectedCount)
    {
        var sample = OpenApiSchemaSampleGenerator.GenerateSample(ParseSchema(schema));

        Assert.Equal(expectedCount, Assert.IsType<JsonArray>(sample).Count);
    }

    [Theory]
    [InlineData("{ type: integer, minimum: 7, multipleOf: 5 }", "10")]
    [InlineData("{ type: integer, maximum: -7, multipleOf: 5 }", "-10")]
    [InlineData("{ type: number, minimum: 0.3, multipleOf: 0.25 }", "0.5")]
    [InlineData("{ type: integer, minimum: 1, multipleOf: 0.5 }", "1")]
    [InlineData("{ type: integer, minimum: 1.2, multipleOf: 1.5 }", "3")]
    [InlineData("{ type: integer, multipleOf: 3 }", "0")]
    public void GenerateSample_HonorsMultipleOf(string schema, string expected)
    {
        var sample = OpenApiSchemaSampleGenerator.GenerateSample(ParseSchema(schema));

        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), sample!.GetValue<decimal>());
    }

    [Fact]
    public void Describe_IncludesConstraints()
    {
        var description = OpenApiSchemaSampleGenerator.Describe(ParseSchema("""
            type: object
            properties:
              code: { type: string, minLength: 2, maxLength: 4, pattern: '^[A-Z]+$' }
              amount: { type: number, multipleOf: 0.5 }
              tags: { type: array, minItems: 1, maxItems: 3, uniqueItems: true, items: { type: string } }
            """));

        var properties = description["properties"]!;
        Assert.Equal(2, properties["code"]!["minLength"]!.GetValue<int>());
        Assert.Equal(4, properties["code"]!["maxLength"]!.GetValue<int>());
        Assert.Equal("^[A-Z]+$", properties["code"]!["pattern"]!.GetValue<string>());
        Assert.Equal(0.5m, properties["amount"]!["multipleOf"]!.GetValue<decimal>());
        Assert.Equal(1, properties["tags"]!["minItems"]!.GetValue<int>());
        Assert.Equal(3, properties["tags"]!["maxItems"]!.GetValue<int>());
        Assert.True(properties["tags"]!["uniqueItems"]!.GetValue<bool>());
    }

    private static IOpenApiSchema ParseSchema(string schemaYaml)
    {
        var spec = string.Join('\n', [
            "openapi: 3.0.1",
            "info: { title: Test, version: '1.0' }",
            "paths: {}",
            "components:",
            "  schemas:",
            "    Test:",
            .. Indent(schemaYaml, 6)
        ]);
        var settings = new OpenApiReaderSettings();
        settings.AddYamlReader();
        var document = OpenApiDocument.Parse(spec, null, settings).Document!;
        return document.Components!.Schemas!["Test"];
    }

    private static IEnumerable<string> Indent(string yaml, int spaces) =>
        yaml.Split('\n').Select(l => new string(' ', spaces) + l.TrimEnd('\r'));

    private static async Task<(HttpStatusCode Status, string Body, bool OriginReceived)> GetAsync(string path, string responseYaml)
    {
        await using var origin = await FakeOrigin.StartAsync();

        var dir = Directory.CreateTempSubdirectory("devproxy-openapimock-");
        try
        {
            var specFile = Path.Combine(dir.FullName, "spec.yaml");
            await File.WriteAllTextAsync(specFile, string.Join('\n', [
                "openapi: 3.0.1",
                "info: { title: Test, version: '1.0' }",
                "servers:",
                $"  - url: http://{origin.Host}",
                "paths:",
                $"  {path}:",
                "    get:",
                "      responses:",
                "        '200':",
                "          description: OK",
                .. Indent(responseYaml, 10)
            ]));

            var proxyConfig = new TestProxyConfiguration
            {
                ConfigFile = Path.Combine(dir.FullName, "devproxyrc.json"),
            };
            var config = PluginConfig.FromJson($$"""{ "specFile": "{{specFile.Replace("\\", "\\\\", StringComparison.Ordinal)}}" }""");

            using var plugin = new OpenApiMockResponsePlugin(
                TestDefaults.HttpClient,
                NullLogger<OpenApiMockResponsePlugin>.Instance,
                KestrelProxyHarness.BuildUrlsToWatch(origin.Host),
                new DisabledLanguageModelClient(),
                proxyConfig,
                config);

            using var host = new PluginTestHost(proxyConfig);
            await plugin.InitializeAsync(host.CreateInitArgs(), CancellationToken.None);

            await using var proxy = await KestrelProxyHarness.StartAsync(origin.Host, [plugin]);
            using var client = proxy.CreateHttpClient();

            using var httpResponse = await client.GetAsync(new Uri($"http://{origin.Host}{path}"));
            var body = await httpResponse.Content.ReadAsStringAsync();

            return (httpResponse.StatusCode, body, origin.ReceivedRequests.Any(r => r.PathAndQuery == path));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
