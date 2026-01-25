// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.State;
using System.CommandLine;
using System.Diagnostics;

namespace DevProxy.Commands;

internal sealed class StopCommand : Command
{
    private readonly Option<bool> _forceOption = new("--force", "-f")
    {
        Description = "Force stop the proxy by killing the process"
    };

    public StopCommand() : base("stop", "Stop running Dev Proxy instance")
    {
        Add(_forceOption);
        SetAction(RunAsync);
    }

    private async Task<int> RunAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var force = parseResult.GetValue(_forceOption);
        var state = await StateManager.LoadStateAsync(cancellationToken);

        if (state == null)
        {
            Console.WriteLine("Dev Proxy is not running.");
            return 1;
        }

        if (force)
        {
            return await ForceStopAsync(state, cancellationToken);
        }

        // Try graceful shutdown via API
        try
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var response = await httpClient.PostAsync($"{state.ApiUrl}/proxy/stopProxy", null, cancellationToken);

            if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.Accepted)
            {
                Console.WriteLine("Stopping Dev Proxy...");

                // Wait for process to exit
                var stopwatch = Stopwatch.StartNew();
                while (stopwatch.Elapsed < TimeSpan.FromSeconds(10))
                {
                    try
                    {
                        var process = Process.GetProcessById(state.Pid);
                        if (process.HasExited)
                        {
                            break;
                        }
                    }
                    catch (ArgumentException)
                    {
                        // Process has exited
                        break;
                    }

                    await Task.Delay(200, cancellationToken);
                }

                await StateManager.DeleteStateAsync(cancellationToken);
                Console.WriteLine("Dev Proxy stopped.");
                return 0;
            }

            Console.WriteLine($"Failed to stop Dev Proxy: {response.StatusCode}");
            Console.WriteLine("Use --force to forcefully terminate the process.");
            return 1;
        }
        catch (HttpRequestException ex)
        {
            Console.WriteLine($"Failed to connect to Dev Proxy API: {ex.Message}");
            Console.WriteLine("Use --force to forcefully terminate the process.");
            return 1;
        }
        catch (TaskCanceledException)
        {
            Console.WriteLine("Timeout waiting for Dev Proxy to respond.");
            Console.WriteLine("Use --force to forcefully terminate the process.");
            return 1;
        }
    }

    private static async Task<int> ForceStopAsync(ProxyInstanceState state, CancellationToken cancellationToken)
    {
        try
        {
            var process = Process.GetProcessById(state.Pid);
            process.Kill();

            Console.WriteLine($"Forcefully terminated Dev Proxy (PID: {state.Pid}).");
        }
        catch (ArgumentException)
        {
            Console.WriteLine("Dev Proxy process not found (already stopped).");
        }
        catch (InvalidOperationException)
        {
            Console.WriteLine("Dev Proxy process has already exited.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to kill process: {ex.Message}");
            return 1;
        }

        await StateManager.DeleteStateAsync(cancellationToken);
        return 0;
    }
}