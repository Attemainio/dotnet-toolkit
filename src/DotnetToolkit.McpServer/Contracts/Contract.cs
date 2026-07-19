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
    public const string Id = "ctx-contract/3.0";
}
