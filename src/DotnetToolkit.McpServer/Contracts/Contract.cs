namespace DotnetToolkit.McpServer.Contracts;

/// <summary>
/// Shared constants for the response contract. Every v2 tool response envelope carries
/// <see cref="Id"/> and a <c>toolCallId</c> (spec Part III preamble).
/// </summary>
public static class Contract
{
    /// <summary>
    /// 2.1 — get_symbol gained include/exclude, and a response that serves a narrowed component set
    /// now returns a token narrowed to the layers it actually served. The lease additionally requires
    /// the held token to cover the layers a request needs, so escalating resolution against a narrower
    /// token returns content instead of a false changed:false. The change is minor rather than major
    /// because it only ever sends more than before, never less: a consumer that ignores the new
    /// parameters sees identical responses, and one that leases sees content it would previously have
    /// been denied.
    /// </summary>
    public const string Id = "ctx-contract/2.1";
}
