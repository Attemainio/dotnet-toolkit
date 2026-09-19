using System.Text.Json;
using System.Text.Json.Nodes;

namespace DotnetToolkit.McpServer.Hooks;

/// <summary>
/// <c>PostToolUse</c> hook on every tool that can write a <c>.cs</c> file: asks the running server to
/// absorb the written paths — syntax index, live workspace and symbol store — before the agent's next
/// query, and says something only when there is something the agent has to act on.
/// </summary>
/// <remarks>
/// Change detection is mtime-polling rather than a filesystem watcher, so without this a written
/// file reaches the index on the next debounced sweep and the symbol store only on the pass that
/// sweep queues. The sweep is what guarantees convergence for a write this hook does not recognise; the
/// hook is what makes the common case immediate, so <c>search_index</c> right after <c>Edit</c> sees
/// the edit.
/// <para>
/// Silent on success by design. A refactor is thirty edits, and thirty "refreshed" notes would be
/// thirty lines of context spent saying that nothing needs doing. The note is injected only when the
/// agent must wait — a new or deleted file starts a workspace reload — or when the server could not
/// be reached for a new file, in which case the manual <c>reload_workspace</c> is the only route.
/// </para>
/// <para>
/// A <c>git</c> command that rewrites the working tree (<c>checkout</c>, <c>pull</c>, <c>merge</c>,
/// <c>rebase</c>, …) names no file, so it sends the existing <c>rescan</c>, the full sweep, whose
/// notifications do the rest.
/// </para>
/// </remarks>
internal static class CsFileRefresh
{
    /// <summary>
    /// Longer than the server's own refresh budget, so a slow pass answers "queued" through the channel
    /// rather than timing out the client and reading as an unreachable server.
    /// </summary>
    private static readonly TimeSpan RefreshResponseTimeout = TimeSpan.FromSeconds(12);

    private static readonly HashSet<string> TreeRewritingGitVerbs = new(StringComparer.Ordinal)
    {
        "checkout", "switch", "pull", "merge", "rebase", "reset", "stash", "apply", "cherry-pick", "revert", "restore",
    };

    /// <summary>Extracts the files the tool call wrote and refreshes the server's view of them.</summary>
    /// <param name="payload">The parsed hook payload.</param>
    /// <param name="context">The repo root whose control channel to talk to, and the working directory
    /// relative paths resolve against.</param>
    /// <returns>
    /// A plain allow in the common case; an allow carrying <c>additionalContext</c> when a reload was
    /// started or the channel was unreachable for a new file.
    /// </returns>
    public static async Task<HookOutcome> EvaluateAsync(HookPayload payload, HookContext context)
    {
        var (files, treeRewrite) = WrittenFiles(payload, context);
        if (treeRewrite)
        {
            await ControlClient.SendAsync(context.Root, "rescan");
            return HookOutcome.Allow;
        }

        var csharp = files
            .Where(file => file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || IsProjectFile(file))
            .Select(file => Path.GetFullPath(Path.Combine(context.WorkingDirectory, file)))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (csharp.Length == 0)
        {
            return HookOutcome.Allow;
        }

        var response = await ControlClient.SendAsync(
            context.Root,
            $"refresh {JsonSerializer.Serialize(csharp)}",
            RefreshResponseTimeout);

        var shown = string.Join(", ", csharp.Select(path => Path.GetRelativePath(context.Root, path).Replace('\\', '/')));
        if (response is null)
        {
            // Unreachable server. An edited file converges on the next sweep; a NEW file never does
            // without a reload, and only the agent can ask for one.
            var created = csharp.Where(path => IsNewToIndex(path, context)).ToArray();
            if (created.Length == 0)
            {
                return HookOutcome.Allow;
            }
            return Note(
                $"{shown}: the dotnet-toolkit server could not be reached to index this new file. Call "
                + "reload_workspace(scope: \"all\") and wait for workspace_status to report loaded before "
                + "get_symbol or search_index on it.");
        }

        if (response.StartsWith("ok:reload started", StringComparison.Ordinal))
        {
            return Note(
                $"{shown}: {response[3..]} - call workspace_status and wait for it to report loaded before "
                + "get_symbol, search_index or get_references on this file.");
        }
        if (response.StartsWith("ok:refresh queued", StringComparison.Ordinal)
            || response.StartsWith("ok:refreshed, but", StringComparison.Ordinal))
        {
            return Note($"{shown}: {response[3..]}");
        }

        return HookOutcome.Allow;
    }

    /// <summary>The paths a tool call wrote, per tool shape, plus whether it rewrote the whole tree.</summary>
    internal static (IReadOnlyList<string> Files, bool TreeRewrite) WrittenFiles(HookPayload payload, HookContext context)
    {
        switch (payload.ToolName)
        {
            case "Edit":
            case "Write":
            case "NotebookEdit":
                return (payload.FilePath is { Length: > 0 } path ? [path] : [], false);

            case "apply_patch":
                return (ApplyPatchCommand.Parse(payload.Command).Select(change => change.Path).ToArray(), false);

            case "Bash":
                return BashWrites(payload.Command, context);

            default:
                return ([], false);
        }
    }

    private static (IReadOnlyList<string> Files, bool TreeRewrite) BashWrites(string? command, HookContext context)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return ([], false);
        }

        var files = new List<string>();
        var workingDirectory = context.WorkingDirectory;
        foreach (var segment in BashCommandScanner.Segments(command))
        {
            if (BashCommandScanner.CdTarget(segment) is { } moved)
            {
                workingDirectory = Path.GetFullPath(Path.Combine(workingDirectory, moved));
                continue;
            }

            if (IsTreeRewritingGit(segment))
            {
                return ([], true);
            }

            var target = BashCommandScanner.FindWriteTargetCsArgument(segment);
            if (target is not null)
            {
                files.Add(Path.GetFullPath(Path.Combine(workingDirectory, target)));
            }
        }
        return (files, false);
    }

    private static bool IsTreeRewritingGit(string segment)
    {
        if (!string.Equals(BashCommandScanner.CommandName(segment), "git", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        // The verb is the first token after "git" that is not an option (git -C dir pull).
        var tokens = segment.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 1; i < tokens.Length; i++)
        {
            if (tokens[i].StartsWith('-'))
            {
                if (tokens[i] is "-C" or "-c")
                    i++;
                continue;
            }
            return TreeRewritingGitVerbs.Contains(tokens[i]);
        }
        return false;
    }

    private static bool IsProjectFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".csproj" or ".props" or ".targets" or ".sln" or ".slnx";

    /// <summary>
    /// Whether the server's syntax index cache has no entry for this file — the cheap static proxy
    /// for "new file" a hook process can afford, since it cannot ask the server that just failed to
    /// answer.
    /// </summary>
    private static bool IsNewToIndex(string absolute, HookContext context)
    {
        try
        {
            var cache = Path.Combine(context.Root, ".claude", "dotnet-toolkit", "cache", "index.json");
            if (!File.Exists(cache))
                return true;
            var relative = Path.GetRelativePath(context.Root, absolute).Replace('\\', '/');
            using var doc = JsonDocument.Parse(File.ReadAllText(cache));
            return !doc.RootElement.TryGetProperty("Files", out var files)
                || !files.TryGetProperty(relative, out _);
        }
        catch (Exception)
        {
            return true;
        }
    }

    private static HookOutcome Note(string message)
    {
        var output = new JsonObject
        {
            ["hookSpecificOutput"] = new JsonObject
            {
                ["hookEventName"] = "PostToolUse",
                ["additionalContext"] = message,
            },
        };
        return HookOutcome.AllowWith(output.ToJsonString(JsonSerializerOptions.Default));
    }
}
