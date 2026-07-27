namespace DotnetToolkit.McpServer.Indexing;

/// <summary>
/// Kind codes are single letters, shared by the index and the compact output format:
/// C class, I interface, S struct, R record, E enum, D delegate,
/// M method, K constructor, P property/indexer, F field/enum-member, V event.
/// </summary>
public sealed record MemberEntry(
    string Kind,
    string Name,
    string Signature,
    string? Doc,
    int Line,
    int EndLine,
    bool IsPublic,
    string? DocSections = null);

/// <summary>A syntax-only outline of one type declaration, produced by <see cref="OutlineBuilder"/>.</summary>
public sealed record TypeEntry(
    string Kind,
    string Name,
    string FqName,
    string Namespace,
    string? Doc,
    string[] Bases,
    string Modifiers,
    int Line,
    int EndLine,
    List<MemberEntry> Members,
    List<TypeEntry> Nested,
    bool IsPublic,
    string? DocSections = null);

/// <summary>A cached, syntax-only outline of one source file's namespaces and types.</summary>
public sealed record FileEntry(
    long MtimeTicks,
    long Length,
    List<string> Namespaces,
    List<TypeEntry> Types);

/// <summary>The on-disk cache of every file's <see cref="Indexing.FileEntry"/>, keyed by absolute path.</summary>
public sealed class IndexDocument
{
    public const int CurrentVersion = 3;

    public int Version { get; set; } = CurrentVersion;
    public string Root { get; set; } = "";
    public Dictionary<string, FileEntry> Files { get; set; } = new(StringComparer.Ordinal);
}
