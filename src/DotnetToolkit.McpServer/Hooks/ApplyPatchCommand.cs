namespace DotnetToolkit.McpServer.Hooks;

/// <summary>Extracts file operations from the stable header lines in an <c>apply_patch</c> command.</summary>
internal static class ApplyPatchCommand
{
    internal enum ChangeKind
    {
        Add,
        Update,
        Delete,
        Move,
    }

    internal readonly record struct Change(ChangeKind Kind, string Path);

    /// <summary>Returns the files named by patch operation headers, ignoring diff content.</summary>
    public static IReadOnlyList<Change> Parse(string? command)
    {
        if (string.IsNullOrEmpty(command))
        {
            return [];
        }

        var changes = new List<Change>();
        using var reader = new StringReader(command);
        while (reader.ReadLine() is { } line)
        {
            Add(line, "*** Add File: ", ChangeKind.Add, changes);
            Add(line, "*** Update File: ", ChangeKind.Update, changes);
            Add(line, "*** Delete File: ", ChangeKind.Delete, changes);
            Add(line, "*** Move to: ", ChangeKind.Move, changes);
        }

        return changes;
    }

    private static void Add(string line, string prefix, ChangeKind kind, ICollection<Change> changes)
    {
        if (line.StartsWith(prefix, StringComparison.Ordinal)
            && line[prefix.Length..].Trim() is { Length: > 0 } path)
        {
            changes.Add(new Change(kind, path));
        }
    }
}
