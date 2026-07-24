namespace DotnetToolkit.McpServer.Contracts;

/// <summary>
/// Shared constants for the response contract. Every v2 tool response envelope carries
/// <see cref="Id"/> and a <c>toolCallId</c> (spec Part III preamble).
/// </summary>
public static class Contract
{
    /// <summary>The response contract version — current: 3.24.</summary>
    /// <remarks>
    /// Bump this whenever a tool's request/response shape changes, so a caller can react to a
    /// changed version string without diffing every field itself. Through 3.18, this doc comment also
    /// carried a growing bullet list of version-by-version rationale — removed, since it duplicated
    /// the development log for no benefit and grew this one field's <c>xmlDoc</c> without bound on
    /// every fetch that touches it, exactly the tag-separation mistake <c>docs/xml-documentation.md</c>
    /// tells every other symbol in this codebase to avoid. Query <c>search_log(query: "contract")</c>
    /// for why each bump from 3.6 onward happened (the development log's era); <c>git log -p --
    /// src/DotnetToolkit.McpServer/Contracts/Contract.cs</c> for the exact wording and full history
    /// back to 2.1, including the versions that predate the log.
    /// </remarks>
    public const string Id = "ctx-contract/3.24";
}
