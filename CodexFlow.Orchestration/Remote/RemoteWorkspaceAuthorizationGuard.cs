// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Application;
using CodexFlow.Core.Remote;
using CodexFlow.Orchestration.Hosting;
using CodexFlow.Orchestration.Jobs;
using Microsoft.Extensions.DependencyInjection;

namespace CodexFlow.Orchestration.Remote;

/// <summary>
/// Authoritative remote workspace access guard implementing v1.8 boundary checks:
/// directories under <see cref="IWorkspaceRootProvider.WorkspacesRoot"/> are permitted by default,
/// while external directories and <c>file://</c> repository locations require explicit desktop grant
/// in <see cref="IRemoteWorkspaceAuthorizationStore"/> and reparse-point escape rejection.
/// </summary>
public sealed class RemoteWorkspaceAuthorizationGuard(
    IWorkspaceRootProvider workspaceRootProvider,
    IRemoteWorkspaceAuthorizationStore authStore,
    IServiceScopeFactory? scopeFactory = null) : IRemoteWorkspaceAuthorizationGuard
{
    private readonly IWorkspaceRootProvider _workspaceRootProvider = workspaceRootProvider
        ?? throw new ArgumentNullException(nameof(workspaceRootProvider));
    private readonly IRemoteWorkspaceAuthorizationStore _authStore = authStore
        ?? throw new ArgumentNullException(nameof(authStore));
    private readonly IServiceScopeFactory? _scopeFactory = scopeFactory;

    public async Task<RemoteWorkspaceAuthDecision> AuthorizeWorkspacePathAsync(
        string? workspacePath,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            return new RemoteWorkspaceAuthDecision(
                Allowed: false,
                ReasonCode: "remote_workspace_missing",
                Message: "会话未配置工作区路径，远程控制通道已拒绝访问。");
        }

        if (!RemotePathSecurityHelper.TryNormalizeRealDirectory(workspacePath, out var realPath))
        {
            return new RemoteWorkspaceAuthDecision(
                Allowed: false,
                ReasonCode: "remote_workspace_invalid",
                Message: "无法解析有效的工作区真实目录，远程控制通道已拒绝访问。");
        }

        var defaultRoot = NormalizeDefaultRoot(_workspaceRootProvider.WorkspacesRoot);
        if (RemotePathSecurityHelper.IsSubpathOrEqual(realPath, defaultRoot))
        {
            if (HasReparsePointAlongRelativeSegment(realPath, defaultRoot))
            {
                return new RemoteWorkspaceAuthDecision(
                    Allowed: false,
                    ReasonCode: "remote_workspace_symlink_escape",
                    Message: "工作区路径包含符号链接或重解析点逃逸，远程控制通道已拒绝访问。");
            }

            return new RemoteWorkspaceAuthDecision(
                Allowed: true,
                ReasonCode: "default_workspace_root",
                Message: "Authorized under default WorkspacesRoot.",
                ResolvedPath: realPath);
        }

        if (HasReparsePointOnSelf(realPath))
        {
            return new RemoteWorkspaceAuthDecision(
                Allowed: false,
                ReasonCode: "remote_workspace_symlink_escape",
                Message: "外部工作区目标存在未授权符号链接跳脱，远程控制通道已拒绝访问。");
        }

        var authorized = await _authStore.IsAuthorizedExternalWorkspaceAsync(realPath, ct).ConfigureAwait(false);
        if (!authorized)
        {
            return new RemoteWorkspaceAuthDecision(
                Allowed: false,
                ReasonCode: "remote_workspace_not_authorized",
                Message: "该外部工作区尚未在桌面端「远程控制」设置中显式授权给手机远程控制。");
        }

        return new RemoteWorkspaceAuthDecision(
            Allowed: true,
            ReasonCode: "external_workspace_authorized",
            Message: "Authorized external workspace.",
            ResolvedPath: realPath);
    }

    public async Task<RemoteWorkspaceAuthDecision> AuthorizeSessionAsync(
        string sessionId,
        string? workspacePath,
        string? projectLocation = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return new RemoteWorkspaceAuthDecision(
                Allowed: false,
                ReasonCode: "remote_session_missing",
                Message: "缺少会话标识，远程控制通道已拒绝访问。");
        }

        var resolvedWorkspace = workspacePath;
        var resolvedProjectLocation = projectLocation;

        if ((string.IsNullOrWhiteSpace(resolvedWorkspace) || string.IsNullOrWhiteSpace(resolvedProjectLocation)) &&
            _scopeFactory is not null)
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var chatStore = scope.ServiceProvider.GetService<IChatService>() as IGatewayChatStore
                ?? scope.ServiceProvider.GetService<IGatewayChatStore>();
            if (chatStore is not null)
            {
                var session = await chatStore.GetSession(sessionId).ConfigureAwait(false);
                if (session is not null)
                {
                    if (string.IsNullOrWhiteSpace(resolvedWorkspace))
                    {
                        resolvedWorkspace = session.WorkspacePath;
                    }

                    if (string.IsNullOrWhiteSpace(resolvedProjectLocation))
                    {
                        resolvedProjectLocation = session.ProjectUrl;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(resolvedWorkspace))
            {
                var sessionDir = scope.ServiceProvider.GetService<ISessionDirectory>();
                if (sessionDir is not null)
                {
                    var rec = await sessionDir.FindAsync(sessionId, ct).ConfigureAwait(false);
                    if (rec is not null && !string.IsNullOrWhiteSpace(rec.WorkspacePath))
                    {
                        resolvedWorkspace = rec.WorkspacePath;
                    }
                }
            }
        }

        var workspaceDecision = await AuthorizeWorkspacePathAsync(resolvedWorkspace, ct).ConfigureAwait(false);
        if (!workspaceDecision.Allowed)
        {
            return workspaceDecision;
        }

        if (TryExtractLocalRepositoryPath(resolvedProjectLocation, out var localRepoPath))
        {
            var repoDecision = await AuthorizeWorkspacePathAsync(localRepoPath, ct).ConfigureAwait(false);
            if (!repoDecision.Allowed)
            {
                return new RemoteWorkspaceAuthDecision(
                    Allowed: false,
                    ReasonCode: "remote_project_location_not_authorized",
                    Message: "会话绑定的本地仓库位置未获远程控制授权。");
            }
        }

        return workspaceDecision;
    }

    private static string NormalizeDefaultRoot(string workspacesRoot)
        => RemotePathSecurityHelper.TryNormalizeRealDirectory(workspacesRoot, out var normalized)
            ? normalized
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspacesRoot));

    private static bool TryExtractLocalRepositoryPath(string? projectLocation, out string localPath)
    {
        localPath = string.Empty;
        if (string.IsNullOrWhiteSpace(projectLocation))
        {
            return false;
        }

        var trimmed = projectLocation.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            if (uri.IsFile && !string.IsNullOrWhiteSpace(uri.LocalPath))
            {
                localPath = uri.LocalPath;
                return true;
            }

            return false;
        }

        if (Path.IsPathRooted(trimmed) || (trimmed.Length >= 2 && trimmed[1] == ':'))
        {
            localPath = trimmed;
            return true;
        }

        return false;
    }

    private static bool HasReparsePointAlongRelativeSegment(string realPath, string defaultRoot)
    {
        try
        {
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var current = new DirectoryInfo(Path.TrimEndingDirectorySeparator(Path.GetFullPath(realPath)));
            var stop = Path.TrimEndingDirectorySeparator(Path.GetFullPath(defaultRoot));

            while (current is not null)
            {
                if (string.Equals(Path.TrimEndingDirectorySeparator(current.FullName), stop, comparison))
                {
                    break;
                }

                if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    return true;
                }

                current = current.Parent;
            }
        }
        catch
        {
            return true;
        }

        return false;
    }

    private static bool HasReparsePointOnSelf(string path)
    {
        try
        {
            var info = new DirectoryInfo(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)));
            return info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch
        {
            return true;
        }
    }
}
