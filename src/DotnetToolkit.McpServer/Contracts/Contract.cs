namespace DotnetToolkit.McpServer.Contracts;

/// <summary>
/// Shared constants for the response contract. Every v2 tool response envelope carries
/// <see cref="Id"/> and a <c>toolCallId</c> (spec Part III preamble).
/// </summary>
public static class Contract
{
    /// <summary>The response contract version — current: 4.0.</summary>

    /// <remarks>
    /// Bump this whenever a tool's request/response shape changes, so a caller can react to a
    /// changed version string without diffing every field itself. The rationale for each bump lives
    /// in git history (<c>git log -p -- src/DotnetToolkit.McpServer/Contracts/Contract.cs</c>), not
    /// here: a growing bullet list in this comment once grew the field's own <c>xmlDoc</c> without
    /// bound on every fetch that touched it. 4.0 (2026-09-19) removed validate_patch, search_log, the
    /// source component and contentVersion of get_symbol, includeBodies of get_references, and the
    /// read column and intent of search_index; rename_symbol lost baseVersion/intent/requestedLevel and
    /// its ladder block, and applyOnSuccess became apply. 4.1 (2026-09-20): declarationSites always
    /// carries signatureLine, first after file, on the live and index-only paths alike; get_references
    /// rows marked indirect name the member they bind to in via, and the envelope carries dispatchNote
    /// whenever any row is indirect; a single-symbol get_symbol always carries xmlDoc;
    /// get_project_graph lists SDK-inherited edges under transitiveReferences/transitivelyReferencedBy
    /// and keeps only declared ones in references/referencedBy.
    /// </remarks>
    public const string Id = "ctx-contract/4.1";

}
