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
        new(8, "test_flag_on_symbol", TestFlagOnSymbol),
        new(9, "drop_unread_derived_columns", DropUnreadDerivedColumns),
        new(10, "rename_chain_on_feature_log_symbols", RenameChainOnFeatureLogSymbols),
        new(11, "symbol_modifiers_column", SymbolModifiersColumn),
        new(12, "symbol_origin_column", SymbolOriginColumn),
        new(13, "symbol_namespace_column", SymbolNamespaceColumn),
    ];

    // A rename/arity change gives the same logical member a new symbolId (SymbolKey.IdOf hashes the
    // fully-qualified name), so a log entry recorded under the pre-rename id was orphaned: nothing
    // linked it to the id the symbol carries after the rename, and get_symbol's recentLog only ever
    // queried the current id. old_symbol_id lets a query walk backward through however many renames a
    // symbol has been through -- see FeatureLogStore.ResolveIdChain.
// search_index's modifiers/implements filters (get_symbol's matching modifiers/baseType/interfaces
    // components are declaration-only reads and need no storage of their own). modifiers is a
    // space-separated, space-padded tag set — literal C# modifier keywords plus a few cheap derived
    // tags (extension, indexer, initonly, disposable, asyncdisposable) — populated by
    // ModifierText.Tags at index-build time; existing rows read back NULL until the next full rebuild
    // recomputes them, same as any other newly-added derived column.
    // External-reference indexing: a symbol discovered only as an edge target (a BCL/NuGet API this
    // repo calls, implements, or extends), never as a declaration this repo's own solution walks.
    // origin distinguishes the two; documentation_id is the raw doc-comment id symbolId is hashed from,
    // stored so an external symbol can be re-resolved into a live ISymbol via
    // DocumentationCommentId.GetSymbolsForDeclarationId without reverse-engineering it from the hash.
    // decl_hash/project have no ALTER-TABLE-friendly way to become nullable in SQLite without a full
    // table rebuild, so an external row uses "" (empty string, not NULL) as its sentinel for both —
    // "origin='source'" is what actually gates whether decl_hash is meaningful, not its nullability.
    private const string SymbolOriginColumn = """
        ALTER TABLE symbols ADD COLUMN origin TEXT NOT NULL DEFAULT 'source';
        ALTER TABLE symbols ADD COLUMN documentation_id TEXT;
        """;

private const string SymbolModifiersColumn = """
        ALTER TABLE symbols ADD COLUMN modifiers TEXT;
        """;

    // External rows predating this column have no namespace and, per Moved()'s external-origin
    // exemption, are never reconsidered "changed" by content — so a plain ALTER TABLE would leave
    // every already-recorded external symbol's namespace NULL forever. Dropping them instead makes
    // the next SymbolIndexBuilder rebuild treat them as new rows, which populates the column.
    private const string SymbolNamespaceColumn = """
        ALTER TABLE symbols ADD COLUMN namespace TEXT;
        DELETE FROM symbols WHERE origin = 'external';
        """;

    private const string RenameChainOnFeatureLogSymbols = """
        ALTER TABLE feature_log_symbols ADD COLUMN old_symbol_id TEXT;
        """;

    // Derived data that was written on every index pass and read by nothing. Each of these was a second
    // copy of something the read path recomputes live from Roslyn, so none could ever be consulted
    // without risking the answer the live path already gives correctly:
    //
    //   declaration_sites        — every FROM was a DELETE; reads use sym.DeclaringSyntaxReferences
    //   symbols.search_text      — the FTS table holds the copy that MATCH actually queries
    //   symbols.xml_doc          — reads use sym.GetDocumentationCommentXml(), or ProjectIndex when
    //                              the workspace is not loaded
    //   symbols.accessibility    — reads use sym.DeclaredAccessibility
    //   symbols.updated_at       — never read at all
    //   reference_edges.dispatch_kind — reads compute DispatchKindOf(sym) at the call site
    //
    // xml_doc is the instructive one: a doc comment is trivia, and SyntaxFingerprint is trivia-blind by
    // design, so editing only a doc comment moves no version layer and the incremental pass rewrites
    // nothing. The stored copy would have gone stale and stayed stale — the same shape of defect as the
    // test attribution in migration 8, where a value's real dependency was not what invalidation keyed on.
    //
    // symbols.embedding is deliberately kept: it is an unfilled placeholder for planned vector search,
    // not a duplicate of anything.
    private const string DropUnreadDerivedColumns = """
        DROP TABLE declaration_sites;

        ALTER TABLE symbols DROP COLUMN search_text;
        ALTER TABLE symbols DROP COLUMN xml_doc;
        ALTER TABLE symbols DROP COLUMN accessibility;
        ALTER TABLE symbols DROP COLUMN updated_at;

        ALTER TABLE reference_edges DROP COLUMN dispatch_kind;
        """;

    // test_reference edges duplicated every call edge originating in a test project, and decided
    // test-ness from Project.MetadataReferences — i.e. from how well MSBuild happened to load that
    // project on that pass. A degraded load wrote unmarked edges, and nothing ever recomputed them:
    // ApplyIncremental rewrites edges only where a CONTENT hash moved, and content cannot express
    // "the environment that produced this row was wrong". Measured on this repo, 53 of 113 calling
    // members carried no test attribution while a clean index of the same source attributed all of
    // them — and the resulting tests:0 is read by the validation ladder as "no tests to run".
    //
    // The flag now lives on the symbol and is derived from its own attributes ([Fact], [Test], ...).
    // That makes content-based invalidation correct rather than merely cheap: changing the attribute
    // changes the declaration hash, so the row is rewritten exactly when the answer changes.
    //
    // The derived symbol index is dropped so the next pass rebuilds it from source with the flag set.
    // Everything here is rebuildable by construction; the development log and telemetry are untouched.
    private const string TestFlagOnSymbol = """
        ALTER TABLE symbols ADD COLUMN is_test INTEGER NOT NULL DEFAULT 0;

        DELETE FROM mechanical_facts;
        DELETE FROM declaration_sites;
        DELETE FROM reference_edges;
        DELETE FROM symbols_fts;
        DELETE FROM symbols;
        """;

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
