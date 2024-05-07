﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.DevProxy.Abstractions;
using Microsoft.DevProxy.Plugins.Mocks;
using Titanium.Web.Proxy.EventArguments;
using Microsoft.Extensions.Logging;

namespace Microsoft.DevProxy.Plugins.RequestLogs;

public class MockGeneratorPlugin : BaseProxyPlugin
{
    public MockGeneratorPlugin(IPluginEvents pluginEvents, IProxyContext context, ILogger logger, ISet<UrlToWatch> urlsToWatch, IConfigurationSection? configSection = null) : base(pluginEvents, context, logger, urlsToWatch, configSection)
    {
    }

    public override string Name => nameof(MockGeneratorPlugin);

    public override void Register()
    {
        base.Register();

        PluginEvents.AfterRecordingStop += AfterRecordingStop;
    }

    private Task AfterRecordingStop(object? sender, RecordingArgs e)
    {
        Logger.LogInformation("Creating mocks from recorded requests...");

        if (!e.RequestLogs.Any())
        {
            Logger.LogDebug("No requests to process");
            return Task.CompletedTask;
        }

        var methodAndUrlComparer = new MethodAndUrlComparer();
        var mocks = new List<MockResponse>();

        foreach (var request in e.RequestLogs)
        {
            if (request.MessageType != MessageType.InterceptedResponse ||
              request.Context is null ||
              request.Context.Session is null)
            {
                continue;
            }

            var methodAndUrlString = request.MessageLines.First();
            Logger.LogDebug("Processing request {methodAndUrlString}...", methodAndUrlString);

            var methodAndUrl = GetMethodAndUrl(methodAndUrlString);
            var response = request.Context.Session.HttpClient.Response;

            var newHeaders = new List<MockResponseHeader>();
            newHeaders.AddRange(response.Headers.Select(h => new MockResponseHeader(h.Name, h.Value)));
            var mock = new MockResponse
            {
                Request = new()
                {
                    Method = methodAndUrl.Item1,
                    Url = methodAndUrl.Item2,
                },
                Response = new()
                {
                    StatusCode = response.StatusCode,
                    Headers = newHeaders,
                    Body = GetResponseBody(request.Context.Session).Result
                }
            };
            // skip mock if it's 200 but has no body
            if (mock.Response.StatusCode == 200 && mock.Response.Body is null)
            {
                Logger.LogDebug("Skipping mock with 200 response code and no body");
                continue;
            }

            mocks.Add(mock);
            Logger.LogDebug("Added mock for {method} {url}", mock.Request.Method, mock.Request.Url);
        }

        Logger.LogDebug("Sorting mocks...");
        // sort mocks descending by url length so that most specific mocks are first
        mocks.Sort((a, b) => b.Request!.Url.CompareTo(a.Request!.Url));

        var mocksFile = new MockResponseConfiguration { Mocks = mocks };

        Logger.LogDebug("Serializing mocks...");
        var mocksFileJson = JsonSerializer.Serialize(mocksFile, ProxyUtils.JsonSerializerOptions);
        var fileName = $"mocks-{DateTime.Now:yyyyMMddHHmmss}.json";

        Logger.LogDebug("Writing mocks to {fileName}...", fileName);
        File.WriteAllText(fileName, mocksFileJson);

        Logger.LogInformation("Created mock file {fileName} with {mocksCount} mocks", fileName, mocks.Count);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Returns the body of the response. For binary responses,
    /// saves the binary response as a file on disk and returns @filename
    /// </summary>
    /// <param name="session">Request session</param>
    /// <returns>Response body or @filename for binary responses</returns>
    private async Task<dynamic?> GetResponseBody(SessionEventArgs session)
    {
        Logger.LogDebug("Getting response body...");

        var response = session.HttpClient.Response;
        if (response.ContentType is null || !response.HasBody)
        {
            Logger.LogDebug("Response has no content-type set or has no body. Skipping");
            return null;
        }

        if (response.ContentType.Contains("application/json"))
        {
            Logger.LogDebug("Response is JSON");

            try
            {
                Logger.LogDebug("Reading response body as string...");
                var body = response.IsBodyRead ? response.BodyString : await session.GetResponseBodyAsString();
                Logger.LogDebug("Body: {body}", body);
                Logger.LogDebug("Deserializing response body...");
                return JsonSerializer.Deserialize<dynamic>(body, ProxyUtils.JsonSerializerOptions);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error reading response body");
                return null;
            }
        }

        Logger.LogDebug("Response is binary");
        // assume body is binary
        try
        {
            var filename = $"response-{Guid.NewGuid()}.bin";
            Logger.LogDebug("Reading response body as bytes...");
            var body = await session.GetResponseBody();
            Logger.LogDebug("Writing response body to {filename}...", filename);
            File.WriteAllBytes(filename, body);
            return $"@{filename}";
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error reading response body");
            return null;
        }
    }

    private Tuple<string, string> GetMethodAndUrl(string message)
    {
        var info = message.Split(" ");
        if (info.Length > 2)
        {
            info = [info[0], string.Join(" ", info.Skip(1))];
        }
        return new Tuple<string, string>(info[0], info[1]);
    }
}
