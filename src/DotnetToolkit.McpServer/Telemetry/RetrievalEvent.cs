namespace DotnetToolkit.McpServer.Telemetry;

/// <summary>
/// One immutable row for <c>retrieval_events</c> (spec §19.1): call-time facts only.
/// Retroactive judgments (used-for-edit, reread) are computed later in the derived
/// attribution stratum and never belong here.
/// </summary>
public sealed record RetrievalEvent
{
    public required string ToolCallId { get; init; }
    public required string SessionId { get; init; }
    public required string TaskId { get; init; }
    public required string ToolName { get; init; }
    public string? RequestedSymbol { get; init; }
    public string? SymbolId { get; init; }
    public string? Resolution { get; init; }
    public string? Direction { get; init; }
    public string? KnownVersion { get; init; }
    public bool Refetch { get; init; }
    public bool LeaseHit { get; init; }
    public string? ContentVersion { get; init; }
    public int ReturnedSymbols { get; init; }
    public int ReturnedTokens { get; init; }
    public string Staleness { get; init; } = "live";
    public string? ErrorKind { get; init; }
}
