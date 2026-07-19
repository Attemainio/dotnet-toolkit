namespace DotnetToolkit.McpServer.Store;

/// <summary>
/// Ordered, append-only list of schema migrations. Each entry is applied once, in order,
/// inside a transaction; the applied version is recorded in <c>schema_migrations</c>.
/// Never edit or reorder a shipped migration — add a new one. Phases 1–7 append here.
/// </summary>
internal static class Schema
{
    public sealed record Migration(int Version, string Name, string Sql);

    public static readonly IReadOnlyList<Migration> Migrations =
    [
        new(1, "raw_telemetry", RawTelemetry),
        new(2, "symbol_index", SymbolIndex),
        new(3, "feature_log", FeatureLog),
        new(4, "mechanical_facts", MechanicalFacts),
        new(5, "derived_attribution", DerivedAttribution),
        new(6, "symbol_fts", SymbolFts),
        new(7, "symbol_fts_explicit_writes", SymbolFtsExplicitWrites),
    ];

    // Migration 6 mirrored symbols into symbols_fts with triggers, on the stated assumption that
    // "INSERT OR REPLACE fires delete-then-insert". That is false by default: SQLite fires DELETE
    // triggers for REPLACE-caused deletes only when recursive_triggers is ON, and it is never enabled
    // here. So every incremental re-index fired the INSERT trigger, left the old FTS row in place, and
    // search_index returned the same symbol two or more times. Migration 6 also never backfilled the
    // rows that already existed when it ran, so those symbols had no FTS row at all and were invisible
    // to search entirely — on this repo, 441 of 740 symbols with 69 duplicated among the rest.
    //
    // The triggers go away rather than getting patched: the FTS table is pure derived data, and
    // SymbolStore now writes it explicitly alongside every symbols write, in the same transaction.
    // Clearing the table here is safe precisely because it is derived — RepairSearchIndex rebuilds it.
    private const string SymbolFtsExplicitWrites = """
        DROP TRIGGER IF EXISTS trg_symfts_ins;
        DROP TRIGGER IF EXISTS trg_symfts_del;
        DROP TRIGGER IF EXISTS trg_symfts_upd;

        DELETE FROM symbols_fts;
        """;

    // Spec §20 Phase 7 — ranked discovery. The previous matcher was a single `fq_name LIKE '%q%'`,
    // which cannot match a multi-word query at all: no symbol's name contains "Ledger TryBuy" as a
    // contiguous substring, so such a query returned zero rows rather than the two symbols meant.
    //
    // symbols.search_text holds the name pre-split on both separators and camel-case boundaries
    // (FIFOLedger.TryBuy -> "FIFOLedger TryBuy FIFO Ledger Try Buy"), because FTS5's tokenizer splits
    // on punctuation but not on case.
    //
    // The triggers below were wrong and are dropped by migration 7 — see SymbolFtsExplicitWrites.
    // Left here verbatim because a shipped migration is never edited.
    private const string SymbolFts = """
        ALTER TABLE symbols ADD COLUMN search_text TEXT;

        CREATE VIRTUAL TABLE symbols_fts USING fts5(
            symbol_id UNINDEXED,
            search_text,
            tokenize = 'unicode61'
        );

        CREATE TRIGGER trg_symfts_ins AFTER INSERT ON symbols BEGIN
            INSERT INTO symbols_fts(symbol_id, search_text)
            VALUES (new.symbol_id, COALESCE(new.search_text, new.fq_name));
        END;

        CREATE TRIGGER trg_symfts_del AFTER DELETE ON symbols BEGIN
            DELETE FROM symbols_fts WHERE symbol_id = old.symbol_id;
        END;

        CREATE TRIGGER trg_symfts_upd AFTER UPDATE ON symbols BEGIN
            DELETE FROM symbols_fts WHERE symbol_id = old.symbol_id;
            INSERT INTO symbols_fts(symbol_id, search_text)
            VALUES (new.symbol_id, COALESCE(new.search_text, new.fq_name));
        END;
        """;

    // Spec §19.2 — derived attribution. Unlike raw telemetry this stratum is DROPPED AND REBUILT, and
    // every row records the ruleset version that produced it, so heuristics can evolve without
    // corrupting history and a rebuild stays idempotent for a fixed version (Conformance C9).
    private const string DerivedAttribution = """
        CREATE TABLE derived_retrieval_attribution (
            event_id                TEXT PRIMARY KEY REFERENCES retrieval_events(event_id),
            computed_at             TEXT NOT NULL,
            attribution_version     TEXT NOT NULL,
            used_for_edit           INTEGER,
            used_for_navigation     INTEGER,
            reread                  INTEGER,
            reread_after_compaction INTEGER,
            hops_to_edit            INTEGER,
            verdict                 TEXT NOT NULL
        );

        CREATE TABLE derived_task_summary (
            task_id                       TEXT PRIMARY KEY,
            computed_at                   TEXT NOT NULL,
            attribution_version           TEXT NOT NULL,
            total_tokens                  INTEGER NOT NULL,
            tokens_contributing           INTEGER NOT NULL,
            tokens_navigational           INTEGER NOT NULL,
            tokens_unused                 INTEGER NOT NULL,
            tokens_wasted_rereads         INTEGER NOT NULL,
            tokens_saved_by_leases        INTEGER NOT NULL,
            validation_attempts           INTEGER NOT NULL,
            attempts_to_first_success     INTEGER,
            insufficient_green_lights     INTEGER NOT NULL,
            suggested_inspection_followed REAL,
            outcome                       TEXT NOT NULL
        );
        """;

    // Spec §18 — body-derived facts. Valid only while body_hash matches the symbol's current body
    // layer; a moved body invalidates the row rather than silently serving stale facts.
    private const string MechanicalFacts = """
        CREATE TABLE mechanical_facts (
            symbol_id  TEXT PRIMARY KEY REFERENCES symbols(symbol_id),
            facts_json TEXT NOT NULL,
            body_hash  TEXT NOT NULL
        );
        """;

    // Spec §18 — development log (append-only, a source of truth; never rebuilt from source).
    private const string FeatureLog = """
        CREATE TABLE feature_log (
            log_id          TEXT PRIMARY KEY,
            task_id         TEXT NOT NULL,
            patch_id        TEXT,
            commit_sha      TEXT,
            intent          TEXT NOT NULL,
            tags            TEXT NOT NULL,
            validation_json TEXT,
            created_at      TEXT NOT NULL
        );

        CREATE TABLE feature_log_symbols (
            log_id       TEXT NOT NULL REFERENCES feature_log(log_id),
            symbol_id    TEXT NOT NULL,
            change_kinds TEXT NOT NULL,
            detail       TEXT,
            old_version  TEXT,
            new_version  TEXT,
            api_impact   TEXT,
            PRIMARY KEY (log_id, symbol_id)
        );
        CREATE INDEX ix_logsym_symbol ON feature_log_symbols(symbol_id);
        """;

    // Spec §18 — symbol index + reference edge cache. Rebuildable from source at any time;
    // refs_hash/api_hash stay NULL until Phase 3 materializes the semantic layers.
    private const string SymbolIndex = """
        CREATE TABLE symbols (
            symbol_id       TEXT PRIMARY KEY,
            fq_name         TEXT NOT NULL,
            kind            TEXT NOT NULL,
            accessibility   TEXT NOT NULL,
            project         TEXT NOT NULL,
            decl_hash       TEXT NOT NULL,
            body_hash       TEXT,
            refs_hash       TEXT,
            api_hash        TEXT,
            display_string  TEXT NOT NULL,
            xml_doc         TEXT,
            embedding       BLOB,
            updated_at      TEXT NOT NULL
        );
        CREATE INDEX ix_symbols_fq ON symbols(fq_name);

        CREATE TABLE declaration_sites (
            symbol_id  TEXT NOT NULL REFERENCES symbols(symbol_id),
            file       TEXT NOT NULL,
            start_line INTEGER NOT NULL,
            end_line   INTEGER NOT NULL,
            region     TEXT
        );
        CREATE INDEX ix_declsites_symbol ON declaration_sites(symbol_id);

        CREATE TABLE reference_edges (
            from_symbol   TEXT NOT NULL,
            to_symbol     TEXT NOT NULL,
            edge_kind     TEXT NOT NULL,
            dispatch_kind TEXT,
            file          TEXT,
            line          INTEGER,
            PRIMARY KEY (from_symbol, to_symbol, edge_kind, file, line)
        );
        CREATE INDEX ix_edges_to   ON reference_edges(to_symbol, edge_kind);
        CREATE INDEX ix_edges_from ON reference_edges(from_symbol, edge_kind);
        """;

    // Spec §19.1 — raw events. Only call-time facts; append-only and immutable.
    // Retroactive judgments live in the derived attribution stratum (Phase 5), never here.
    private const string RawTelemetry = """
        CREATE TABLE retrieval_events (
            event_id         TEXT PRIMARY KEY,
            tool_call_id     TEXT NOT NULL UNIQUE,
            session_id       TEXT NOT NULL,
            task_id          TEXT NOT NULL,
            tool_name        TEXT NOT NULL,
            requested_symbol TEXT,
            symbol_id        TEXT,
            resolution       TEXT,
            direction        TEXT,
            known_version    TEXT,
            refetch          INTEGER NOT NULL DEFAULT 0,
            lease_hit        INTEGER NOT NULL DEFAULT 0,
            content_version  TEXT,
            returned_symbols INTEGER NOT NULL DEFAULT 0,
            returned_tokens  INTEGER NOT NULL,
            staleness        TEXT NOT NULL DEFAULT 'live',
            error_kind       TEXT,
            created_at       TEXT NOT NULL
        );
        CREATE INDEX ix_re_session ON retrieval_events(session_id, created_at);
        CREATE INDEX ix_re_symbol  ON retrieval_events(symbol_id, created_at);

        CREATE TABLE patch_events (
            event_id              TEXT PRIMARY KEY,
            tool_call_id          TEXT NOT NULL UNIQUE,
            patch_id              TEXT NOT NULL,
            validation_attempt_id TEXT NOT NULL UNIQUE,
            session_id            TEXT NOT NULL,
            task_id               TEXT NOT NULL,
            attempt_ordinal       INTEGER NOT NULL,
            changed_symbol_ids    TEXT NOT NULL,
            change_kinds          TEXT NOT NULL,
            base_versions         TEXT NOT NULL,
            completed_level       TEXT NOT NULL,
            required_level        TEXT NOT NULL,
            is_sufficient         INTEGER NOT NULL,
            succeeded             INTEGER NOT NULL,
            applied               INTEGER NOT NULL,
            intent                TEXT,
            raw_diagnostics       INTEGER NOT NULL,
            distilled_diagnostics INTEGER NOT NULL,
            returned_tokens       INTEGER NOT NULL,
            duration_ms           INTEGER NOT NULL,
            created_at            TEXT NOT NULL
        );

        CREATE TABLE surfaced_symbols (
            event_id        TEXT NOT NULL REFERENCES retrieval_events(event_id),
            symbol_id       TEXT NOT NULL,
            content_version TEXT,
            surface_kind    TEXT NOT NULL,
            PRIMARY KEY (event_id, symbol_id, surface_kind)
        );

        CREATE TABLE session_events (
            event_id   TEXT PRIMARY KEY,
            session_id TEXT NOT NULL,
            task_id    TEXT,
            kind       TEXT NOT NULL,
            detail     TEXT,
            created_at TEXT NOT NULL
        );

        -- Immutability: UPDATE on any raw telemetry table raises; append succeeds (Conformance C6).
        CREATE TRIGGER trg_re_immutable BEFORE UPDATE ON retrieval_events BEGIN
            SELECT RAISE(ABORT, 'raw telemetry is immutable');
        END;
        CREATE TRIGGER trg_pe_immutable BEFORE UPDATE ON patch_events BEGIN
            SELECT RAISE(ABORT, 'raw telemetry is immutable');
        END;
        CREATE TRIGGER trg_ss_immutable BEFORE UPDATE ON surfaced_symbols BEGIN
            SELECT RAISE(ABORT, 'raw telemetry is immutable');
        END;
        CREATE TRIGGER trg_se_immutable BEFORE UPDATE ON session_events BEGIN
            SELECT RAISE(ABORT, 'raw telemetry is immutable');
        END;
        """;
}
