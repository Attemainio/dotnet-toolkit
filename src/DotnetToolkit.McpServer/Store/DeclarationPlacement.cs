namespace DotnetToolkit.McpServer.Store;

/// <summary>
/// Where a symbol's declarations sit relative to the tree the syntax index scans, which is what decides
/// whether a search hit can carry a file and line at all.
/// </summary>
/// <remarks>
/// The symbol rows come from the semantic tier, which sees the whole compilation; the file locations on
/// those rows come from <c>ProjectIndex</c>, which scans the repo tree and prunes <c>bin</c>/<c>obj</c>/
/// <c>dist</c> first. Anything the two disagree about reaches a caller as a row with an empty file, line
/// and endLine — indistinguishable from an indexing failure unless the row says which case it is. Stored
/// as the integer value in the <c>generated</c> column, so the numbers are part of the on-disk format.
/// </remarks>
public enum DeclarationPlacement
{
    /// <summary>Declared in this repo's own scanned tree; the syntax index has its location.</summary>
    InTree = 0,

    /// <summary>Source-generator or build output, under a directory the index prunes — regenerated on
    /// every build, so there is no span worth patching.</summary>
    Generated = 1,

    /// <summary>Declared outside the repo root entirely — a <c>Compile</c> item a package contributes,
    /// such as the test SDK's synthesized entry point. Real source, but not this repo's to edit.</summary>
    OutsideRoot = 2,
}
