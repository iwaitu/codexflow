// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text;

namespace CodexFlow.Core.Agents;

/// <summary>
/// Just enough shell parsing to ask "which program is run, with which subcommand" instead of "does the command line contain these words".
/// Quotes group words, <c>;</c> <c>&amp;&amp;</c> <c>||</c> <c>|</c> <c>&amp;</c> and line breaks start a new simple command, and <c>bash -c "…"</c> /
/// <c>pwsh -Command "…"</c> are looked into. It is not a shell: anything it cannot make sense of yields fewer commands, never more.
/// </summary>
internal static class ShellCommandParser
{
    private static readonly HashSet<string> Wrappers = new(StringComparer.OrdinalIgnoreCase) { "sudo", "env", "time", "nohup", "command", "exec", "call" };
    private static readonly HashSet<string> ShellsWithCommandFlag = new(StringComparer.OrdinalIgnoreCase)
        { "bash", "sh", "zsh", "dash", "pwsh", "powershell", "cmd" };
    private static readonly HashSet<string> CommandFlags = new(StringComparer.OrdinalIgnoreCase) { "-c", "-command", "/c", "-lc", "-ic" };

    /// <summary>Splits a command line into simple commands, each a list of words (the first is the program).</summary>
    public static IReadOnlyList<IReadOnlyList<string>> SimpleCommands(string? commandLine, int depth = 0)
    {
        var result = new List<IReadOnlyList<string>>();
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return result;
        }

        var current = new List<string>();
        var word = new StringBuilder();
        var inWord = false;
        char quote = '\0';

        void EndWord()
        {
            if (inWord)
            {
                current.Add(word.ToString());
                word.Clear();
                inWord = false;
            }
        }

        void EndCommand()
        {
            EndWord();
            if (current.Count > 0)
            {
                result.Add(current);
                current = [];
            }
        }

        for (var i = 0; i < commandLine.Length; i++)
        {
            var c = commandLine[i];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
                else if (c == '\\' && quote == '"' && i + 1 < commandLine.Length && commandLine[i + 1] is '"' or '\\')
                {
                    word.Append(commandLine[++i]);
                }
                else
                {
                    word.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '"':
                case '\'':
                    quote = c;
                    inWord = true;
                    break;
                case ';':
                case '|':
                case '&':
                case '\n':
                case '\r':
                    EndCommand();
                    break;
                case ' ':
                case '\t':
                    EndWord();
                    break;
                default:
                    word.Append(c);
                    inWord = true;
                    break;
            }
        }

        EndCommand();

        // bash -c "dotnet add package X": the program is a shell and the real command is one of its words.
        if (depth < 2)
        {
            var nested = new List<IReadOnlyList<string>>();
            foreach (var command in result.ToList())
            {
                var (program, args) = Split(command);
                if (program is not null && ShellsWithCommandFlag.Contains(program))
                {
                    var flag = args.FindIndex(arg => CommandFlags.Contains(arg));
                    if (flag >= 0 && flag + 1 < args.Count)
                    {
                        nested.AddRange(SimpleCommands(string.Join(" ", args.Skip(flag + 1)), depth + 1));
                    }
                }
            }

            result.AddRange(nested);
        }

        return result;
    }

    /// <summary>The program name (no directory, no .exe, lower-case) and its words, skipping VAR=value prefixes and wrappers like sudo/env/time.</summary>
    public static (string? Program, List<string> Args) Split(IReadOnlyList<string> command)
    {
        var index = 0;
        while (index < command.Count && (IsAssignment(command[index]) || Wrappers.Contains(ProgramName(command[index]))))
        {
            index++;
        }

        return index >= command.Count
            ? (null, [])
            : (ProgramName(command[index]), command.Skip(index + 1).ToList());
    }

    public static string ProgramName(string word)
    {
        var name = word.Replace('\\', '/').Split('/')[^1].ToLowerInvariant();
        return name.EndsWith(".exe", StringComparison.Ordinal) ? name[..^4] : name;
    }

    private static bool IsAssignment(string word)
    {
        var equals = word.IndexOf('=');
        return equals > 0 && word[..equals].All(character => char.IsLetterOrDigit(character) || character == '_') && !char.IsDigit(word[0]);
    }

    /// <summary>The arguments of every <c>dotnet</c> invocation in the command line (lower-case), however it is spelled or wrapped.</summary>
    public static IReadOnlyList<IReadOnlyList<string>> DotnetInvocations(string? commandLine)
    {
        var result = new List<IReadOnlyList<string>>();
        foreach (var command in SimpleCommands(commandLine))
        {
            var (program, args) = Split(command);
            if (program == "dotnet")
            {
                result.Add(args.Select(arg => arg.ToLowerInvariant()).ToList());
            }
        }

        return result;
    }
}
