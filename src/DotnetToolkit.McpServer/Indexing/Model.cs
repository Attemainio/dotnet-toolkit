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
    string? DocSections = null,
    int DocLines = 0,
    int CommentLines = 0);

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
    string? DocSections = null,
    int DocLines = 0,
    int CommentLines = 0);

/// <summary>A cached, syntax-only outline of one source file's namespaces and types.</summary>
public sealed record FileEntry(
    long MtimeTicks,
    long Length,
    List<string> Namespaces,
    List<TypeEntry> Types);

/// <summary>The on-disk cache of every file's <see cref="Indexing.FileEntry"/>, keyed by absolute path.</summary>
public sealed class IndexDocument
{
    /// <summary>Schema/content stamp for the on-disk index cache; a mismatch makes LoadCache discard it.</summary>
    /// <remarks>
    /// Bump this for a change to what <see cref="OutlineBuilder"/> PRODUCES, not just to the record shapes
    /// it produces them into. Cache entries are keyed on each file's mtime and length, so an indexer that
    /// starts emitting something new for an unchanged file keeps serving the old entry forever — the new
    /// behavior then appears to work in unit tests and to do nothing at all through the server.
    ///
    /// 4 added the synthesized Program.Main of a top-level-statements file, which every earlier cache
    /// records as an empty type list. 5 scoped that to files a project actually compiles, so a cache
    /// written by 4 holds entries for a test fixture's and a loose script's Program too. 6 added
    /// DocLines/CommentLines, which a cache written by 5 deserializes as 0 — indistinguishable from a
    /// genuinely undocumented, uncommented symbol, and unfixed until each file's mtime changes.
    /// </remarks>
    public const int CurrentVersion = 6;

    public int Version { get; set; } = CurrentVersion;
    public string Root { get; set; } = "";
    public Dictionary<string, FileEntry> Files { get; set; } = new(StringComparer.Ordinal);
}
