namespace DotnetToolkit.McpServer.Contracts;

/// <summary>
/// Shared constants for the response contract. Every v2 tool response envelope carries
/// <see cref="Id"/> and a <c>toolCallId</c> (spec Part III preamble).
/// </summary>
public static class Contract
{
    /// <summary>
    /// The response contract version.
    /// <list type="bullet">
    /// <item><description>
    /// <b>3.4</b> — search_index's <c>items</c> becomes a <see cref="Output.CompactTable"/>:
    /// <c>{"columns":[...],"rows":[[...],...]}</c> instead of one object per hit. BREAKING: a consumer
    /// reading <c>items[i].name</c> sees nothing; it now reads <c>items.columns.IndexOf("name")</c> and
    /// <c>items.rows[i][that]</c>. Still plain JSON arrays, so no delimiter or escaping rule was
    /// introduced — the saving is only that column names are no longer repeated once per row.
    /// </description></item>
    /// <item><description>
    /// <b>3.3</b> — search_index hits carry <c>file</c> and <c>line</c>. Additive, and both are absent
    /// when the name maps to several declarations: the syntax index they are resolved from keys members
    /// without parameter lists, so overloads cannot be separated, and absent is what a caller already
    /// handled. Resolved per response rather than stored beside the symbol row, because a stored line is
    /// invalidated by that symbol's own hashes and an edit *above* a declaration moves its line without
    /// touching one of them.
    /// </description></item>
    /// <item><description>
    /// <b>3.2</b> — <c>limitedBy</c> gained the value <c>stale</c>: the files an answer was served from
    /// have moved on disk since the workspace read them. The previous markers described the tier, which
    /// a loaded, undegraded workspace holding a file that changed underneath it satisfies while still
    /// serving content that no longer exists.
    /// </description></item>
    /// <item><description>
    /// <b>3.1</b> — validate_patch gained the <c>stale_workspace</c> error. Additive: it occupies a case
    /// that previously produced a silent, successful, wrong apply. An apply writes the whole document
    /// text, so a patch built on a workspace copy behind disk reverted everything else in that file;
    /// <c>baseVersions</c> never covered it, guarding only the symbols detected as changed.
    /// </description></item>
    /// <item><description>
    /// <b>3.0</b> — the per-response tier marker is renamed <c>staleness</c> to <c>limitedBy</c>.
    /// BREAKING: a consumer reading <c>staleness</c> sees nothing. The old name claimed the field was
    /// about content freshness, which it never was — freshness is mtime-polled before every query.
    /// It names what the answer could not draw on: absent (nothing), <c>index_only</c> (the semantic
    /// tier was unavailable) or <c>degraded</c> (the workspace loaded with failed projects, so results
    /// may be wrong rather than merely thin). The values are unchanged; only the key moved.
    /// </description></item>
    /// <item><description>
    /// <b>2.2</b> — added the <c>degraded</c> value. That state was previously indistinguishable from
    /// a healthy one, because the marker asked only whether the index had finished a pass, not whether
    /// the model it indexed was sound.
    /// </description></item>
    /// <item><description>
    /// <b>2.1</b> — get_symbol gained include/exclude, and a response serving a narrowed component set
    /// returns a token narrowed to the layers it actually served. The lease additionally requires the
    /// held token to cover the layers a request needs, so escalating resolution against a narrower
    /// token returns content instead of a false changed:false.
    /// </description></item>
    /// </list>
    /// </summary>
    public const string Id = "ctx-contract/3.4";
}
