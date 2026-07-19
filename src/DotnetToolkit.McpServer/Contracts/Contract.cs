namespace DotnetToolkit.McpServer.Contracts;

/// <summary>
/// Shared constants for the response contract. Every v2 tool response envelope carries
/// <see cref="Id"/> and a <c>toolCallId</c> (spec Part III preamble).
/// </summary>
public static class Contract
{
    /// <summary>
    /// The response contract version. Both increments below are minor because each only ever sends
    /// more than before: a consumer that ignores the new fields sees what it always saw.
    /// <list type="bullet">
    /// <item><description>
    /// <b>2.1</b> — get_symbol gained include/exclude, and a response serving a narrowed component set
    /// returns a token narrowed to the layers it actually served. The lease additionally requires the
    /// held token to cover the layers a request needs, so escalating resolution against a narrower
    /// token returns content instead of a false changed:false.
    /// </description></item>
    /// <item><description>
    /// <b>2.2</b> — responses can report <c>staleness: "degraded"</c>: the workspace loaded but
    /// projects failed, so results may be silently wrong rather than merely thin. That state was
    /// previously indistinguishable from a healthy one, because the marker asked only whether the
    /// index had finished a pass, not whether the model it indexed was sound. The healthy case is
    /// still silence.
    /// </description></item>
    /// </list>
    /// </summary>
    public const string Id = "ctx-contract/2.2";
}
