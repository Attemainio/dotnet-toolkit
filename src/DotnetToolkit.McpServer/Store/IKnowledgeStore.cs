using Microsoft.Data.Sqlite;

namespace DotnetToolkit.McpServer.Store;

/// <summary>
/// The pooled-connection surface of the SQLite knowledge store: exactly the two members every
/// consumer (<see cref="SymbolStore"/>, <see cref="FeatureLogStore"/>, and the telemetry readers)
/// actually calls through the concrete <see cref="KnowledgeStore"/> type.
/// </summary>
public interface IKnowledgeStore
{
    /// <value>Whether the store initialized successfully; false if the underlying file could not be opened or migrated.</value>
    bool Available { get; }

    /// <summary>Opens a pooled connection. Caller disposes.</summary>
    SqliteConnection Connect();
}
