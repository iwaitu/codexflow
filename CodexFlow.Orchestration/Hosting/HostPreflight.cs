// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using CodexFlow.Core.Execution;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CodexFlow.Orchestration.Hosting;

public enum LocalPreflightMode
{
    Fail = 0,
    Report = 1,
    Off = 2
}

/// <summary>One thing that has to be fixed before a host can do its work.</summary>
/// <param name="Component">What is wrong: <c>Model</c> or <c>Docker</c>.</param>
/// <param name="Message">What the user sees, with what to do about it.</param>
public sealed record PreflightIssue(string Component, string Message);

/// <summary>
/// The start-up check of a host that depends on nothing but Docker and a model interface: the model settings are complete and the
/// interface answers, and the Docker daemon is reachable. It reports every problem at once, so one start tells the user all there is
/// to fix instead of one thing per attempt.
/// </summary>
public static class HostPreflight
{
    private static readonly TimeSpan ModelTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DockerTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Resolves <c>Local:PreflightMode</c> (<c>Fail</c> | <c>Report</c> | <c>Off</c>), honoring the legacy
    /// <c>Local:SkipPreflight=true</c> switch with a deprecation warning.
    /// </summary>
    public static LocalPreflightMode ResolvePreflightMode(IConfiguration configuration, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var rawMode = configuration["Local:PreflightMode"];
        if (!string.IsNullOrWhiteSpace(rawMode) && Enum.TryParse<LocalPreflightMode>(rawMode, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        if (configuration.GetValue<bool>("Local:SkipPreflight"))
        {
            logger?.LogWarning("Local:SkipPreflight is deprecated; use Local:PreflightMode=Off instead.");
            return LocalPreflightMode.Off;
        }

        return LocalPreflightMode.Fail;
    }

    /// <summary>Runs the start-up checks required by the configured execution backend.</summary>
    /// <param name="configuration">Host configuration; the model settings are in the <c>VllmAgent</c> section.</param>
    /// <param name="handler">Replaces the HTTP stack for the model call; tests only.</param>
    /// <param name="dockerProbe">Replaces the real Docker call; returns <c>null</c> when the daemon answers, else the reason. Tests only.</param>
    public static async Task<IReadOnlyList<PreflightIssue>> RunAsync(
        IConfiguration configuration,
        HttpMessageHandler? handler = null,
        Func<CancellationToken, Task<string?>>? dockerProbe = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var issues = new List<PreflightIssue>();
        await CheckModelAsync(configuration.GetSection("VllmAgent"), handler, issues, cancellationToken).ConfigureAwait(false);

        var backendRaw = configuration[$"{ExecutionOptions.SectionName}:Backend"];
        var backend = !string.IsNullOrWhiteSpace(backendRaw) && Enum.TryParse<ExecutionBackend>(backendRaw, ignoreCase: true, out var parsedBackend)
            ? parsedBackend
            : ExecutionBackend.Local;

        if (backend == ExecutionBackend.Docker)
        {
            var dockerFailure = await (dockerProbe ?? ProbeDockerAsync)(cancellationToken).ConfigureAwait(false);
            if (dockerFailure is not null)
            {
                issues.Add(new PreflightIssue("Docker", $"Docker is not available: {dockerFailure} Start Docker Desktop (or the Docker daemon) and try again."));
            }
        }

        return issues;
    }

    /// <summary>
    /// The <c>/models</c> address of a model interface configured the way <c>VllmAgent:ApiUrl</c> is: a base URL, possibly with the
    /// <c>{1}</c> placeholder or a trailing <c>/chat/completions</c> or <c>/models</c>.
    /// </summary>
    public static Uri ModelsUri(string apiUrl)
    {
        var normalized = (apiUrl ?? string.Empty).Trim().Replace("{0}", string.Empty, StringComparison.Ordinal).Replace("{1}", string.Empty, StringComparison.Ordinal).TrimEnd('/');
        foreach (var suffix in new[] { "/chat/completions", "/models" })
        {
            if (normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized[..^suffix.Length];
                break;
            }
        }

        return new Uri(new Uri(normalized + "/", UriKind.Absolute), "models");
    }

    private static async Task CheckModelAsync(IConfigurationSection model, HttpMessageHandler? handler, List<PreflightIssue> issues, CancellationToken ct)
    {
        var apiUrl = model["ApiUrl"];
        var apiKey = model["ApiKey"];
        var name = model["Model"];
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(apiUrl)) missing.Add("VllmAgent:ApiUrl");
        if (string.IsNullOrWhiteSpace(apiKey)) missing.Add("VllmAgent:ApiKey");
        if (string.IsNullOrWhiteSpace(name)) missing.Add("VllmAgent:Model");
        if (missing.Count > 0)
        {
            issues.Add(new PreflightIssue("Model", $"The model interface is not configured: {string.Join(", ", missing)} missing. Set them in the configuration or as environment variables (VllmAgent__ApiUrl, VllmAgent__ApiKey, VllmAgent__Model)."));
            return;
        }

        // Only the OpenAI-compatible chat-completions interface has a /models list that costs nothing to ask for; the other modes are
        // not probed (a request would spend tokens), their first real call reports a problem.
        var mode = model["ApiMode"];
        if (!string.IsNullOrWhiteSpace(mode) && !string.Equals(mode, "ChatCompletions", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Uri uri;
        try
        {
            uri = ModelsUri(apiUrl!);
        }
        catch (UriFormatException)
        {
            issues.Add(new PreflightIssue("Model", $"VllmAgent:ApiUrl is not a valid absolute URL: {apiUrl}"));
            return;
        }

        try
        {
            using var client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
            client.Timeout = ModelTimeout;
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);

            // 404 / 405: this interface does not list models, which says nothing about whether chat works.
            if (response.IsSuccessStatusCode || response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
            {
                return;
            }

            issues.Add(new PreflightIssue("Model", response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? $"The model interface at {uri.GetLeftPart(UriPartial.Authority)} rejected the API key ({(int)response.StatusCode}). Check VllmAgent:ApiKey."
                : $"The model interface at {uri.GetLeftPart(UriPartial.Authority)} answered {(int)response.StatusCode} {response.ReasonPhrase}."));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            if (ct.IsCancellationRequested) throw;
            issues.Add(new PreflightIssue("Model", $"The model interface at {uri.GetLeftPart(UriPartial.Authority)} cannot be reached ({ex.GetBaseException().Message}). Check VllmAgent:ApiUrl and your network or VPN."));
        }
    }

    private static async Task<string?> ProbeDockerAsync(CancellationToken ct)
    {
        try
        {
            var info = new ProcessStartInfo("docker")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            info.ArgumentList.Add("info");
            info.ArgumentList.Add("--format");
            info.ArgumentList.Add("{{.ServerVersion}}");

            using var process = Process.Start(info) ?? throw new InvalidOperationException("the docker command did not start.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(DockerTimeout);
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already gone */ }
                return "the daemon did not answer within 15 seconds.";
            }

            if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(await stdout.ConfigureAwait(false)))
            {
                return null;
            }

            var reason = (await stderr.ConfigureAwait(false)).Trim();
            return reason.Length == 0 ? $"docker info exited with code {process.ExitCode}." : reason.Split('\n')[0].Trim();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return "the docker command was not found on PATH.";
        }
    }
}
