// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.RegularExpressions;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;

namespace CodexFlow.Core.Agents;

public sealed class TaskFileScopeDescriptor
{
    public HashSet<string> AllowedFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> AllowedFileNames { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> AllowedTestFileNames { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> AllowedDirectories { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Files the task's structured Outputs name. They are writable, but they never switch the scope on (<see cref="HasConstraints"/>):
    /// planners guess output names ("WebApi.csproj" for src/CleanApp/CleanApp.csproj, seen live), and a guessed name as the scope's only
    /// allowed file refused the real one.
    /// </summary>
    public HashSet<string> DeclaredOutputFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> DisallowedDirectories { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool AllowProjectFileEdits { get; set; }
    public bool AllowDiRegistrationFiles { get; set; }
    public bool HasConstraints => AllowedFiles.Count > 0;
}

public static class TaskFileScopeGuard
{
    private static readonly string[] SiblingLayers =
    [
        "Infrastructure",
        "Persistence",
        "Application",
        "Domain",
        "Core"
    ];

    private static readonly Dictionary<string, string> LayerAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["core"] = "Core",
        ["infrastructure"] = "Infrastructure",
        ["application"] = "Application",
        ["domain"] = "Domain",
        ["persistence"] = "Persistence",
        ["api"] = "Api",
        ["web"] = "Web"
    };

    /// <param name="workspaceRoot">The project root on the host. Planners sometimes write a declared output as an absolute host path
    /// ("D:\work\app\package-lock.json", seen live); under this root it is the same file as its relative path, anywhere else it grants nothing.</param>
    public static TaskFileScopeDescriptor BuildTaskFileScope(CodexTask task, string? workspaceRoot = null)
    {
        ArgumentNullException.ThrowIfNull(task);

        var scope = new TaskFileScopeDescriptor();
        var text = string.Join("\n", new[]
        {
            task.Title ?? string.Empty,
            task.Description ?? string.Empty,
            string.Join("\n", task.Outputs)
        });

        // Only the parts of the task that can GRANT write access feed the allow-lists: a file the task forbids touching, or only asks to
        // read, must not become writable just because its name appears in the text (ExportScope is built from these lists alone).
        var (grantText, forbidden) = SplitGrantableText(text);

        // A plan that says what may not be touched, as data, is the authority on it. The prose still decides what is GRANTED (a clause that
        // prohibits a file grants nothing), but it no longer takes files away: six live failures came from reading write permission out of
        // sentences. Without the field (an older plan) the prose decides, as before.
        if (task.ForbiddenPaths is { } declaredForbidden)
        {
            forbidden = new ForbiddenMentions();
            foreach (var entry in declaredForbidden)
            {
                AddDeclaredForbidden(scope, forbidden, entry);
            }
        }

        var seedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pathRegex = new Regex(@"(?:[A-Za-z0-9_.-]+[\\/])+[A-Za-z0-9_.-]+\.(?:csproj|cs|json|md)", RegexOptions.IgnoreCase);
        foreach (Match m in pathRegex.Matches(grantText))
        {
            var normalized = NormalizePathLike(m.Value);
            if (string.IsNullOrWhiteSpace(normalized) || forbidden.IsForbidden(normalized) ||
                normalized.Split('/').Any(static part => part is "." or ".."))   // a path that climbs out of the workspace grants nothing
            {
                continue;
            }

            scope.AllowedFiles.Add(normalized);
            var fileName = Path.GetFileNameWithoutExtension(normalized);
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                scope.AllowedTestFileNames.Add($"{fileName}Tests.cs");
                scope.AllowedTestFileNames.Add($"{fileName}IntegrationTests.cs");
                scope.AllowedTestFileNames.Add($"{fileName}ArchitectureTests.cs");
            }

            var parentDir = Path.GetDirectoryName(normalized)?.Replace('\\', '/');
            if (!string.IsNullOrWhiteSpace(parentDir))
            {
                scope.AllowedDirectories.Add(parentDir);
                seedDirectories.Add(parentDir);
            }
        }

        // What the task declares it produces (the plan's structured Outputs, not its prose) is writable as declared. The prose rules above
        // only know .cs/.csproj/.json/.md paths that have a directory, so a file in the repository root (package-lock.json,
        // requirements.txt, pager.py) was in no list: seen live, `npm install --package-lock-only` regenerated the lockfile the task was
        // for, the export refused it as out of scope, and the worker then wrote the lockfile by hand with an invented integrity hash.
        foreach (var output in task.Outputs)
        {
            // Judged as written, before any normalisation: normalising would turn "../x" or "/etc/x" into an innocent-looking relative path.
            var written = (output ?? string.Empty).Trim().Replace('\\', '/');
            var root = (workspaceRoot ?? string.Empty).Trim().Replace('\\', '/').TrimEnd('/');
            if (root.Length > 0 && written.Length > root.Length + 1 && written[root.Length] == '/' &&
                written.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                written = written[(root.Length + 1)..];   // an absolute path under the project root is that file's relative path
            }

            if (!DeclaredOutputFile.IsMatch(written) || written.Split('/').Any(static part => part is "." or ".."))
            {
                continue;
            }

            var declared = NormalizePathLike(written);
            if (!string.IsNullOrWhiteSpace(declared) && !forbidden.IsForbidden(declared))
            {
                scope.DeclaredOutputFiles.Add(declared);
            }
        }

        ExpandBareFileMentionsIntoSeedDirectories(grantText, scope, seedDirectories);
        AddMentionedTypeFileNames(grantText, scope);
        forbidden.RemoveFrom(scope);

        var taskText = text.ToLowerInvariant();
        var exclusiveLayers = ExtractExplicitExclusiveLayers(taskText);
        var excludedLayers = ExtractExplicitExcludedLayers(taskText);

        var isCreationTask = taskText.Contains("创建", StringComparison.Ordinal) ||
                             taskText.Contains("新建", StringComparison.Ordinal) ||
                             taskText.Contains("引入接口", StringComparison.Ordinal) ||
                             taskText.Contains("添加类", StringComparison.Ordinal) ||
                             taskText.Contains("introduce", StringComparison.Ordinal) ||
                             taskText.Contains("create interface", StringComparison.Ordinal);

        if (isCreationTask)
        {
            var relatedDirs = new[] { "Interfaces", "Abstractions", "Repositories" };
            foreach (var dir in scope.AllowedDirectories.ToList())
            {
                var projectDir = ResolveProjectDirectory(dir) ?? dir;
                foreach (var related in relatedDirs)
                {
                    scope.AllowedDirectories.Add($"{projectDir}/{related}");
                }
            }
        }

        var isArchRefactor = taskText.Contains("重构", StringComparison.Ordinal) ||
                             taskText.Contains("架构", StringComparison.Ordinal) ||
                             taskText.Contains("refactor", StringComparison.Ordinal) ||
                             taskText.Contains("restructur", StringComparison.Ordinal) ||
                             taskText.Contains("引入仓储", StringComparison.Ordinal) ||
                             taskText.Contains("引入生成器", StringComparison.Ordinal) ||
                             taskText.Contains("introduce repository", StringComparison.Ordinal) ||
                             taskText.Contains("introduce generator", StringComparison.Ordinal) ||
                             taskText.Contains("hexagonal", StringComparison.Ordinal) ||
                             taskText.Contains("clean architecture", StringComparison.Ordinal) ||
                             taskText.Contains("六边形", StringComparison.Ordinal) ||
                             taskText.Contains("洋葱架构", StringComparison.Ordinal);

        var excludesDiRegistration =
            Regex.IsMatch(taskText, @"(不处理|不涉及|不修改|不要改|不得修改|不需要)[^。\n]*(依赖注入|di\s*注册|di registration|dependency injection)", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(taskText, @"(do not|don't|without|no need to)[^\n]*(dependency injection|di registration)", RegexOptions.IgnoreCase);

        var needsDiRegistration = !excludesDiRegistration &&
                                  (taskText.Contains("依赖注入", StringComparison.Ordinal) ||
                                   taskText.Contains("dependency injection", StringComparison.Ordinal) ||
                                   taskText.Contains("di registration", StringComparison.Ordinal) ||
                                   taskText.Contains("di 注册", StringComparison.Ordinal));

        if (needsDiRegistration)
        {
            scope.AllowDiRegistrationFiles = true;
        }

        if (isArchRefactor || isCreationTask || needsDiRegistration)
        {
            foreach (var dir in scope.AllowedDirectories.ToList())
            {
                if (string.IsNullOrWhiteSpace(dir))
                {
                    continue;
                }

                var projectDir = ResolveProjectDirectory(dir) ?? dir;
                var inProjectPatterns = new[]
                {
                    "Generators",
                    "Extensions",
                    "DependencyInjection",
                    "Implementations",
                    "Interfaces",
                    "Abstractions",
                    "Repositories"
                };

                foreach (var sub in inProjectPatterns)
                {
                    scope.AllowedDirectories.Add($"{projectDir}/{sub}");
                }

                var projectName = Path.GetFileName(projectDir);
                var solutionRoot = Path.GetDirectoryName(projectDir)?.Replace('\\', '/');
                if (string.IsNullOrWhiteSpace(solutionRoot) || string.IsNullOrWhiteSpace(projectName))
                {
                    continue;
                }

                var dotIdx = projectName.IndexOf('.', StringComparison.Ordinal);
                var projectPrefix = dotIdx > 0 ? projectName[..dotIdx] : projectName;

                foreach (var layer in SiblingLayers)
                {
                    scope.AllowedDirectories.Add($"{solutionRoot}/{projectPrefix}.{layer}");
                }

                scope.AllowedDirectories.Add($"{solutionRoot}/{projectPrefix}");
            }
        }

        if (Regex.IsMatch(taskText, @"(所有|全部)\s*(项目文件|工程文件|csproj)", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(taskText, @"\b(all|every)\s+(\.csproj|csproj|project files?)\b", RegexOptions.IgnoreCase))
        {
            scope.AllowProjectFileEdits = true;
        }

        ApplyExplicitLayerRestrictions(scope, exclusiveLayers, excludedLayers);
        return scope;
    }

    public static IReadOnlyList<string> GetOutOfScopeChanges(
        IReadOnlyList<GitFileChange> changes,
        TaskFileScopeDescriptor scope)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(scope);

        var violations = new List<string>();
        foreach (var change in changes)
        {
            var normalized = NormalizePathLike(change.FilePath);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            var isNewFile = string.Equals(change.Status, "A", StringComparison.OrdinalIgnoreCase);
            if (!IsPathInScope(normalized, scope, isNewFile))
            {
                violations.Add(normalized);
            }
        }

        return violations;
    }

    // A clause is one sentence/line/comma-separated piece; its "、" items inherit the polarity of the item before them (see SplitGrantableText).
    private static readonly Regex ClauseSplit = new(@"[。\n;；,，]+|(?<=[.!?])\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private static readonly Regex SentenceSplit = new(@"[。\n;；]+|(?<=[.!?])\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    // A clause that opens a contingency: "如果命令执行失败", "3. 验证 X：若还原失败", "If the restore fails".
    private static readonly Regex ContingencyLead = new(@"^\s*(?:\d+[.、)]\s*)?(?:[^,，]*[:：]\s*)?(?:如果|如若|若|假如|一旦|万一|倘若|if\b|in case\b|should\b)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private static readonly Regex NegationCue = new(
        @"禁止|不得|不要|不可|不能|不允许|不处理|不涉及|不修改|不改动|不更改|不改|不做|不加|不碰|不触碰|不动|不新增|不新建|不引入|切勿|无需修改|\b(?:do not|don't|must not|should not|never|without (?:modifying|changing|touching))\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private static readonly Regex ReadOnlyCue = new(@"读取|查看|阅读|\b(?:read|inspect|examine|look at)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private static readonly Regex WriteVerb = new(
        @"修改|添加|追加|更新|修复|编写|新增|替换|重构|实现|删除|创建|新建|\b(?:modify|add|update|fix|write|edit|replace|implement|delete|create)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private static readonly Regex MentionedPath = new(@"(?:[A-Za-z0-9_.-]+[\\/])+[A-Za-z0-9_.-]+\.(?:csproj|cs|json|md)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private static readonly Regex MentionedBareFile = new(@"(?<![\\/])\b(?<name>[A-Za-z0-9_.-]+\.(?:csproj|cs|json|md))\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private static readonly Regex MentionedType = new(@"\b(?<name>[A-Z][A-Za-z0-9]{2,})\b", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

    // A relative file path and nothing else: no spaces or prose, no wildcard, no drive or leading slash, with a file extension.
    // The extension is lower case on purpose: "tests/Rounding.Tests" is a .NET project DIRECTORY, not a file with the extension "Tests".
    // Seen live: that directory, declared as the task's output, became the scope's only "file", which switched the scope on
    // (HasConstraints) and refused every write to the test file inside it.
    private static readonly Regex DeclaredOutputFile = new(@"^(?![\\/])[A-Za-z0-9_.-]+(?:/[A-Za-z0-9_.-]+)*\.[a-z0-9]{1,8}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

    private sealed class ForbiddenMentions
    {
        public HashSet<string> Paths { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> FileNames { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> TypeFileNames { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool IsForbidden(string normalizedPath) =>
            Paths.Contains(normalizedPath) || FileNames.Contains(Path.GetFileName(normalizedPath));

        /// <summary>Forbidden wins over a positive mention of the same file elsewhere in the task.</summary>
        public void RemoveFrom(TaskFileScopeDescriptor scope)
        {
            scope.AllowedFiles.RemoveWhere(file => IsForbidden(file));
            scope.DeclaredOutputFiles.RemoveWhere(file => IsForbidden(file));
            scope.AllowedFileNames.RemoveWhere(name => FileNames.Contains(name) || TypeFileNames.Contains(name));
        }
    }

    /// <summary>
    /// Splits the task text into the part that can grant write access and the files it forbids. In a clause with a negation cue
    /// ("禁止修改 X", "do not touch X") everything from the cue on is forbidden; in a read-only clause ("读取 X、Y") everything from the
    /// cue on grants nothing, unless the clause also names a write verb. Text before the cue still grants.
    /// </summary>
    private static readonly Regex MannerOnlyProhibition = new(
        @"^(?:禁止|不得|不要|不可|不能|不允许|切勿|do not|don't|must not|should not|never)\s*(?:手动|手工|手写|人工|manually|hand[- ]?(?:edit|writ)|edit\w*\s+\S+\s+by hand)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

    // A prohibition that limits WHAT in a file may change, not the file: "禁止修改 package.json 或 package-lock.json 中的其他依赖版本",
    // "do not change other dependency versions in package-lock.json". The qualifier follows the file in Chinese ("X 中的其他…") and
    // precedes it in English ("other … in X"); a bare "other files" names no file and is not matched.
    private static readonly Regex ContentQualifiedProhibition = new(
        @"[A-Za-z0-9_.-]+\.[A-Za-z0-9]{1,8}\s*(?:中|里|内)的?其[他它]|\bother\s+(?:[\w-]+\s+){0,3}(?:in|of|within)\s+[A-Za-z0-9_./\\-]+\.[A-Za-z0-9]{1,8}\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

    private static (string GrantText, ForbiddenMentions Forbidden) SplitGrantableText(string text)
    {
        var forbidden = new ForbiddenMentions();
        var grant = new System.Text.StringBuilder();

        // What a sentence says to do IF something goes wrong ("如果命令执行失败，如实记录原因，不修改 package.json 或 package-lock.json") is a
        // contingency, not the task's scope: from the "if" clause to the end of that sentence a prohibition forbids nothing. Seen live: that
        // sentence took both files out of the scope of the task whose whole purpose was to change them.
        var clauses = new List<(string Clause, bool Contingency)>();
        foreach (var sentence in SentenceSplit.Split(text ?? string.Empty))
        {
            var contingency = false;
            foreach (var part in ClauseSplit.Split(sentence))
            {
                contingency |= ContingencyLead.IsMatch(part);
                clauses.Add((part, contingency));
            }
        }

        foreach (var (clause, contingency) in clauses)
        {
            // A clause is an enumeration ("A、B、C"). Every item answers for itself: its own negation, read or write verb sets the polarity;
            // an item without one inherits the polarity of the item before it ("读取 A、B" reads both, "不修改 A、B" forbids both), and
            // a cue never reaches past the "、" that ends its item ("(仅读不改)" of one item says nothing about the next).
            var polarity = Polarity.Grant;
            foreach (var item in clause.Split('、'))
            {
                var negation = NegationCue.Match(item);
                if (negation.Success && contingency)
                {
                    continue;
                }

                if (negation.Success && MannerOnlyProhibition.IsMatch(item[negation.Index..]))
                {
                    // "禁止手动编辑 package-lock.json" rules out a way of producing the file (it must come from the tool), not the file:
                    // it neither grants nor forbids. Seen live: this sentence took the lockfile out of the scope of the very task that
                    // had to regenerate it, so npm's result could not be written back.
                    continue;
                }

                if (negation.Success && ContentQualifiedProhibition.IsMatch(item[negation.Index..]))
                {
                    // The task may still write the file; it must only leave the other entries in it alone. Seen live (dependency-upgrade-node,
                    // platform host): this sentence forbade the declared lockfile and npm's regenerated result was refused at export.
                    continue;
                }

                var read = ReadOnlyCue.Match(item);
                var readFirst = read.Success && (!negation.Success || read.Index < negation.Index);

                if (readFirst && !WriteVerb.IsMatch(item[read.Index..]))
                {
                    // "读取 X 确认 ... (仅读不改)": X is only read; the trailing "不改" forbids nothing the item mentions before it.
                    grant.Append(item, 0, read.Index).Append('\n');
                    if (negation.Success) AddForbidden(forbidden, item[negation.Index..]);
                    polarity = Polarity.ReadOnly;
                }
                else if (negation.Success)
                {
                    grant.Append(item, 0, negation.Index).Append('\n');
                    AddForbidden(forbidden, item[negation.Index..]);
                    polarity = Polarity.Forbidden;
                }
                else if (WriteVerb.IsMatch(item))
                {
                    grant.Append(item).Append('\n');
                    polarity = Polarity.Grant;
                }
                else
                {
                    switch (polarity)
                    {
                        case Polarity.Grant:
                            grant.Append(item).Append('\n');
                            break;
                        case Polarity.Forbidden:
                            AddForbidden(forbidden, item);
                            break;
                    }
                }
            }
        }

        return (grant.ToString(), forbidden);
    }

    /// <summary>
    /// What a dependency task may write is a fact the host holds, not what the plan's author guessed: its scan says which projects each package
    /// reaches. For every package the task names, the manifests of those projects become writable (never anything else in them, never a path that
    /// climbs out or is absolute, never a FILE the plan forbids by name; a forbidden directory does not take the manifest away). Seen live: a plan declared its outputs as "OtherProject1/*.csproj" without having
    /// read the repository, and the csproj files the task really had to change were refused. Returns how many files were added.
    /// </summary>
    public static int AddHostKnownManifests(
        TaskFileScopeDescriptor scope,
        CodexTask task,
        IEnumerable<(string Package, IReadOnlyList<string> Projects)> upgrades)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(upgrades);

        var text = string.Join("\n", task.Title ?? string.Empty, task.Description ?? string.Empty, string.Join("\n", task.Outputs));
        var added = 0;
        foreach (var (package, projects) in upgrades)
        {
            if (string.IsNullOrWhiteSpace(package) || !MentionsPackage(text, package))
            {
                continue;
            }

            foreach (var project in projects ?? [])
            {
                if (GrantHostKnownManifest(scope, project))
                {
                    added++;
                }
            }
        }

        return added;
    }

    /// <summary>
    /// What a RETRY must change is what the host's own contract check found unmet: it names the package and the project that declares it, and the
    /// retry's feedback tells the worker to change exactly those. The retry belongs to a task whose text does not name those packages (the last
    /// code task checks every package of the upgrade), so the scan-based grant above cannot reach them and the scope guard refused every edit the
    /// host's feedback asked for. The manifests are granted on the same terms: never an absolute or climbing path, never a file the plan forbids
    /// by name. Returns how many files were added.
    /// </summary>
    public static int AddHostGrantedManifests(TaskFileScopeDescriptor scope, IEnumerable<string> projectManifests)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(projectManifests);

        return projectManifests.Count(project => GrantHostKnownManifest(scope, project));
    }

    private static bool GrantHostKnownManifest(TaskFileScopeDescriptor scope, string? project)
    {
        var path = (project ?? string.Empty).Trim().Replace('\\', '/');
        while (path.StartsWith("./", StringComparison.Ordinal))
        {
            path = path[2..];
        }

        if (path.Length == 0 || path.StartsWith('/') || Regex.IsMatch(path, @"^[A-Za-z]:") ||
            path.Split('/').Any(static part => part is ".." or "."))
        {
            return false;
        }

        // A file the plan forbids by name stays forbidden. A DIRECTORY it forbids does not: the plan's author guessed where the package
        // lives, the host's scan knows. Taking the directory out of the forbidden set opens nothing else in it, because a file that is
        // not on the allow-list is refused anyway; only this manifest is added below.
        if (scope.DisallowedDirectories.Contains(path))
        {
            return false;
        }

        foreach (var dir in scope.DisallowedDirectories
                     .Where(dir => path.StartsWith(dir + "/", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            scope.DisallowedDirectories.Remove(dir);
        }

        return scope.DeclaredOutputFiles.Add(path);
    }

    // The package id as a whole: not part of a longer id ("OpenTelemetry.Api" is not "OpenTelemetry.Api.ProviderBuilderExtensions"), and a sentence
    // end after it is not a longer id.
    private static bool MentionsPackage(string text, string package)
    {
        try
        {
            return Regex.IsMatch(
                text,
                $@"(?<![A-Za-z0-9_.]){Regex.Escape(package)}(?![A-Za-z0-9_]|\.[A-Za-z0-9_])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(2));
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private enum Polarity { Grant, ReadOnly, Forbidden }

    /// <summary>
    /// One entry of the plan's ForbiddenPaths: a file or a directory relative to the project root. It is closed to the task whatever else
    /// allows it (the export's disallowed list always wins, and matches an exact path as well as everything below a directory). An entry that
    /// is empty, absolute, or climbs out of the project is ignored: it names nothing the task could reach.
    /// </summary>
    private static void AddDeclaredForbidden(TaskFileScopeDescriptor scope, ForbiddenMentions forbidden, string? entry)
    {
        var written = (entry ?? string.Empty).Trim().Replace('\\', '/');
        while (written.Contains("//", StringComparison.Ordinal))
        {
            written = written.Replace("//", "/", StringComparison.Ordinal);
        }

        while (written.StartsWith("./", StringComparison.Ordinal))
        {
            written = written[2..];
        }

        written = written.TrimEnd('/');
        if (written.Length == 0 || written.StartsWith('/') || Regex.IsMatch(written, @"^[A-Za-z]:") ||
            written.Split('/').Any(static part => part is ".." or "."))
        {
            return;
        }

        scope.DisallowedDirectories.Add(written);
        forbidden.Paths.Add(written);
    }

    private static void AddForbidden(ForbiddenMentions forbidden, string forbiddenPart)
    {
        foreach (Match m in MentionedPath.Matches(forbiddenPart))
        {
            var normalized = NormalizePathLike(m.Value);
            if (!string.IsNullOrWhiteSpace(normalized)) forbidden.Paths.Add(normalized);
        }

        foreach (Match m in MentionedBareFile.Matches(forbiddenPart))
            forbidden.FileNames.Add(m.Groups["name"].Value);

        foreach (Match m in MentionedType.Matches(forbiddenPart))
            if (m.Groups["name"].Value.Any(char.IsLower)) forbidden.TypeFileNames.Add(m.Groups["name"].Value + ".cs");
    }

    private static void ApplyExplicitLayerRestrictions(
        TaskFileScopeDescriptor scope,
        HashSet<string> exclusiveLayers,
        HashSet<string> excludedLayers)
    {
        if (exclusiveLayers.Count == 0 && excludedLayers.Count == 0)
        {
            return;
        }

        var seeds = scope.AllowedDirectories
            .Concat(scope.AllowedFiles.Select(file => Path.GetDirectoryName(file)?.Replace('\\', '/')))
            .Where(dir => !string.IsNullOrWhiteSpace(dir))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var seed in seeds)
        {
            var projectDir = ResolveProjectDirectory(seed) ?? seed!;
            var projectName = Path.GetFileName(projectDir);
            var solutionRoot = Path.GetDirectoryName(projectDir)?.Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(projectName) || string.IsNullOrWhiteSpace(solutionRoot))
            {
                continue;
            }

            var dotIdx = projectName.IndexOf('.', StringComparison.Ordinal);
            var projectPrefix = dotIdx > 0 ? projectName[..dotIdx] : projectName;

            if (exclusiveLayers.Count > 0)
            {
                foreach (var layer in SiblingLayers)
                {
                    if (exclusiveLayers.Contains(layer))
                    {
                        continue;
                    }

                    scope.DisallowedDirectories.Add($"{solutionRoot}/{projectPrefix}.{layer}");
                }
            }

            foreach (var excludedLayer in excludedLayers)
            {
                scope.DisallowedDirectories.Add($"{solutionRoot}/{projectPrefix}.{excludedLayer}");
            }
        }
    }

    private static void ExpandBareFileMentionsIntoSeedDirectories(
        string text,
        TaskFileScopeDescriptor scope,
        HashSet<string> seedDirectories)
    {
        if (string.IsNullOrWhiteSpace(text) || seedDirectories.Count == 0)
        {
            return;
        }

        var bareFileRegex = new Regex(
            @"(?<![\\/])\b(?<name>[A-Za-z0-9_.-]+\.(?:csproj|cs|json|md))\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        foreach (Match match in bareFileRegex.Matches(text))
        {
            var bareFileName = match.Groups["name"].Value;
            if (string.IsNullOrWhiteSpace(bareFileName))
            {
                continue;
            }

            // Skip entries that already arrived through explicit path extraction.
            if (scope.AllowedFiles.Any(path =>
                    string.Equals(Path.GetFileName(path), bareFileName, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            foreach (var directory in seedDirectories)
            {
                scope.AllowedFiles.Add($"{directory.TrimEnd('/')}/{bareFileName}");
            }

            var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(bareFileName);
            if (!string.IsNullOrWhiteSpace(fileNameWithoutExtension))
            {
                scope.AllowedTestFileNames.Add($"{fileNameWithoutExtension}Tests.cs");
                scope.AllowedTestFileNames.Add($"{fileNameWithoutExtension}IntegrationTests.cs");
                scope.AllowedTestFileNames.Add($"{fileNameWithoutExtension}ArchitectureTests.cs");
            }
        }
    }

    private static void AddMentionedTypeFileNames(string text, TaskFileScopeDescriptor scope)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var typeNameRegex = new Regex(
            @"\b(?<name>[A-Z][A-Za-z0-9]{2,})\b",
            RegexOptions.CultureInvariant);

        foreach (Match match in typeNameRegex.Matches(text))
        {
            var name = match.Groups["name"].Value;
            if (string.IsNullOrWhiteSpace(name) ||
                !name.Any(char.IsLower) ||
                name.EndsWith("Tests", StringComparison.Ordinal) ||
                name.EndsWith("Test", StringComparison.Ordinal))
            {
                continue;
            }

            var fileName = $"{name}.cs";
            scope.AllowedFileNames.Add(fileName);
            scope.AllowedTestFileNames.Add($"{name}Tests.cs");
            scope.AllowedTestFileNames.Add($"{name}IntegrationTests.cs");
            scope.AllowedTestFileNames.Add($"{name}ArchitectureTests.cs");
        }
    }

    private static HashSet<string> ExtractExplicitExclusiveLayers(string taskText)
    {
        var layers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (alias, canonical) in LayerAliases)
        {
            if (Regex.IsMatch(taskText, $@"(仅修改|只修改|仅限|只限|only modify|only touch)\s*{Regex.Escape(alias)}(\s*(layer|层))?",
                    RegexOptions.IgnoreCase) ||
                Regex.IsMatch(taskText, $@"{Regex.Escape(alias)}\s*(layer|层)\s*only",
                    RegexOptions.IgnoreCase))
            {
                layers.Add(canonical);
            }
        }

        return layers;
    }

    private static HashSet<string> ExtractExplicitExcludedLayers(string taskText)
    {
        var layers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (alias, canonical) in LayerAliases)
        {
            if (Regex.IsMatch(taskText, $@"(不涉及|不修改|不要改|不得修改|不触碰)\s*{Regex.Escape(alias)}(\s*层)?",
                    RegexOptions.IgnoreCase) ||
                Regex.IsMatch(taskText, $@"(do not touch|do not modify|without touching)\s+{Regex.Escape(alias)}(\s+layer)?",
                    RegexOptions.IgnoreCase))
            {
                layers.Add(canonical);
            }
        }

        return layers;
    }

    private static string? ResolveProjectDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        var current = directory.Replace('\\', '/').TrimEnd('/');
        while (!string.IsNullOrWhiteSpace(current))
        {
            var segment = Path.GetFileName(current);
            if (segment.Contains('.', StringComparison.Ordinal) &&
                !segment.EndsWith('.'))
            {
                return current;
            }

            var parent = Path.GetDirectoryName(current)?.Replace('\\', '/');
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = parent ?? string.Empty;
        }

        return null;
    }

    private static bool IsSystemManagedTaskSideEffect(string normalizedPath)
    {
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return false;
        }

        var fileName = Path.GetFileName(normalizedPath);
        return string.Equals(fileName, "PROJECT_SUMMARY.md", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Checks whether a single normalized path is within the allowed scope.
    /// Used by both GetOutOfScopeChanges (post-execution) and TaskScopeInterventionHook (pre-execution).
    /// </summary>
    /// <param name="normalizedPath">Workspace-relative normalized path (forward slashes, no leading slash)</param>
    /// <param name="scope">The task file scope descriptor</param>
    /// <param name="isNewFile">Whether this path represents a file being created (not yet existing)</param>
    /// <returns>true if the path is in scope; false if it is out of scope</returns>
    public static bool IsPathInScope(string normalizedPath, TaskFileScopeDescriptor scope, bool isNewFile = false)
    {
        if (string.IsNullOrWhiteSpace(normalizedPath) || scope == null)
        {
            return true; // Conservative: allow if path or scope is invalid
        }

        if (IsSystemManagedTaskSideEffect(normalizedPath))
        {
            return true;
        }

        // Explicit exclusion: disallowed directories block regardless of other rules
        var blockedByExplicitRestriction = scope.DisallowedDirectories.Any(dir =>
            normalizedPath.StartsWith(dir + "/", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalizedPath, dir, StringComparison.OrdinalIgnoreCase));
        if (blockedByExplicitRestriction)
        {
            return false;
        }

        // Explicit inclusion: exact match in allowed files
        if (scope.AllowedFiles.Contains(normalizedPath) || scope.DeclaredOutputFiles.Contains(normalizedPath))
        {
            return true;
        }

        var fileName = Path.GetFileName(normalizedPath);
        if (scope.AllowedFileNames.Contains(fileName))
        {
            return true;
        }

        // Suffix match: partial path overlap (e.g., "Interfaces/IUnitOfWork.cs" matches "src/Core/Interfaces/IUnitOfWork.cs")
        var isSuffixMatch = scope.AllowedFiles.Any(allowed =>
            normalizedPath.EndsWith("/" + allowed, StringComparison.OrdinalIgnoreCase) ||
            allowed.EndsWith("/" + normalizedPath, StringComparison.OrdinalIgnoreCase));
        if (isSuffixMatch)
        {
            return true;
        }

        // New file in allowed directory
        if (isNewFile)
        {
            var isInAllowedDirectory = scope.AllowedDirectories.Any(dir =>
                normalizedPath.StartsWith(dir + "/", StringComparison.OrdinalIgnoreCase));
            if (isInAllowedDirectory)
            {
                return true;
            }
        }

        // Project file edits (.csproj)
        if (scope.AllowProjectFileEdits &&
            normalizedPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // DI registration files
        if (scope.AllowDiRegistrationFiles)
        {
            var fn = Path.GetFileName(normalizedPath);
            if (string.Equals(fn, "Program.cs", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fn, "Startup.cs", StringComparison.OrdinalIgnoreCase) ||
                fn.EndsWith("ServiceCollectionExtensions.cs", StringComparison.OrdinalIgnoreCase) ||
                fn.EndsWith("DependencyInjection.cs", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (fn.StartsWith("Base", StringComparison.OrdinalIgnoreCase) &&
                fn.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // Scoped test files
        var isScopedTest =
            (normalizedPath.StartsWith("test/", StringComparison.OrdinalIgnoreCase) ||
             normalizedPath.Contains("/test/", StringComparison.OrdinalIgnoreCase)) &&
            scope.AllowedTestFileNames.Contains(fileName);
        if (isScopedTest)
        {
            return true;
        }

        // Not in scope
        return false;
    }

    internal static string NormalizePathLike(string path)
    {
        var normalized = (path ?? string.Empty).Trim().Replace('\\', '/');
        while (normalized.Contains("//", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
        }

        var firstSlash = normalized.IndexOf('/', StringComparison.Ordinal);
        if (firstSlash > 0 && normalized.Contains("/src/", StringComparison.OrdinalIgnoreCase))
        {
            var srcIndex = normalized.IndexOf("/src/", StringComparison.OrdinalIgnoreCase);
            normalized = normalized[(srcIndex + 1)..];
        }
        else if (firstSlash > 0 && normalized.Contains("/test/", StringComparison.OrdinalIgnoreCase))
        {
            var testIndex = normalized.IndexOf("/test/", StringComparison.OrdinalIgnoreCase);
            normalized = normalized[(testIndex + 1)..];
        }

        return normalized.TrimStart('/');
    }
}
