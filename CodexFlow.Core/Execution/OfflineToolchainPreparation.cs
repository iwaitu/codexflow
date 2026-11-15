// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CodexFlow.Core.Execution;

/// <summary>
/// Installs a Node or Python project's dependencies from the host's offline caches before a worker's own command runs, in the same
/// container, so <c>npm test</c> and <c>python -m pytest</c> work there the way they do in the host verification.
/// </summary>
/// <remarks>
/// Live run (local Qwen, bugfix-node-pager and bugfix-python-pager): every command gets a fresh container without network, so
/// <c>npm test</c> answered "jest: not found" and <c>python -m pytest</c> "No module named pytest". The workers could not run the
/// regression test they had just written, reported it as unverified, and the host verification then failed on an assertion that was
/// wrong from the start (<c>r.items</c> on a function that returns an array). The host verification restores from
/// <c>/npm-cache</c> and <c>/pip-wheels</c>; the worker's commands now do the same first.
/// </remarks>
public static class OfflineToolchainPreparation
{
    private static readonly HashSet<string> NodeExecutables = new(StringComparer.OrdinalIgnoreCase) { "node", "npm", "npx" };
    private static readonly HashSet<string> PythonExecutables = new(StringComparer.OrdinalIgnoreCase) { "python", "python3", "pip", "pip3" };

    // Exit code 3 = nothing to prepare here (no cache mounted, or not a project of this kind): not a failure, and nothing to tell the model.
    internal const int NotApplicableExitCode = 3;

    // The cache mount is read-only and npm writes to its cache, so it works on a copy in the tmpfs (same recipe as NodeProjectVerifier).
    internal const string NodeScript =
        "[ -d /npm-cache ] && [ -f package-lock.json ] || exit 3; " +
        "[ -d node_modules ] && exit 0; " +
        "cp -r /npm-cache /tmp/npm-cache && npm ci --offline --ignore-scripts --no-audit --no-fund --cache /tmp/npm-cache";

    // --user installs under $HOME (/tmp/home), outside the workspace, and the image's own python finds it without a virtualenv.
    internal const string PythonScript =
        "[ -d /pip-wheels ] || exit 3; " +
        "[ -f requirements.txt ] || [ -f pyproject.toml ] || [ -f setup.py ] || [ -f setup.cfg ] || exit 3; " +
        "pip=\"python -m pip install --user --no-index --find-links /pip-wheels --no-warn-script-location -q\"; " +
        "$pip pytest && if [ -f requirements.txt ]; then $pip -r requirements.txt; fi";

    // Bytecode and pytest's cache would land in the workspace and be exported as if the worker had written them.
    private static readonly IReadOnlyDictionary<string, string> PythonEnv = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["PYTHONDONTWRITEBYTECODE"] = "1",
        ["PYTEST_ADDOPTS"] = "-p no:cacheprovider"
    };

    /// <summary>The script that prepares the toolchain of <paramref name="executable"/>, or null when it has none.</summary>
    public static string? ScriptFor(string? executable)
        => executable is null ? null
            : NodeExecutables.Contains(executable) ? NodeScript
            : PythonExecutables.Contains(executable) ? PythonScript
            : null;

    /// <summary>Environment the worker's own command needs after the preparation, or null.</summary>
    public static IReadOnlyDictionary<string, string>? EnvFor(string? executable)
        => executable is not null && PythonExecutables.Contains(executable) ? PythonEnv : null;

    /// <summary>
    /// Runs the preparation in <paramref name="session"/>. Returns null when there was nothing to do, otherwise one line for the model:
    /// what is now installed, or why it could not be installed (the worker's command still runs afterwards).
    /// </summary>
    public static async Task<string?> PrepareAsync(IIsolatedExecutionSession session, string? executable, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (ScriptFor(executable) is not { } script) return null;

        var run = await session.RunAsync(new IsolatedCommandRequest(["sh", "-c", script], Timeout: TimeSpan.FromMinutes(5)), ct).ConfigureAwait(false);
        return Describe(executable!, run.Status, run.ExitCode, run.Stderr, run.Stdout);
    }

    internal static string? Describe(string executable, IsolatedCommandStatus status, int? exitCode, string? stderr, string? stdout)
    {
        var node = NodeExecutables.Contains(executable);
        if (status == IsolatedCommandStatus.Completed && exitCode == 0)
        {
            return node
                ? "[environment] 项目依赖已由宿主从离线缓存装好（npm ci --offline），可以直接运行 `npm test` / `npx --no-install jest`；容器没有网络，不要再联网安装。"
                : "[environment] pytest 和 requirements.txt 里的依赖已由宿主从离线 wheel 目录装好，可以直接运行 `python -m pytest`；容器没有网络，不要再 pip install。";
        }

        if (exitCode == NotApplicableExitCode) return null;

        var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
        detail = string.IsNullOrWhiteSpace(detail) ? "(no output)" : detail.Trim();
        if (detail.Length > 600) detail = detail[^600..];
        return "[environment] 宿主没能从离线缓存装好项目依赖（" + (node ? "npm ci --offline" : "pip --no-index") +
               $"，status {status}, exit {exitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "n/a"}），测试命令可能因此找不到依赖：{detail}";
    }
}
