using System.ComponentModel;
using System.Text.Json;
using DotnetToolkit.McpServer.Contracts;
using DotnetToolkit.McpServer.Fingerprint;
using DotnetToolkit.McpServer.Identity;
using DotnetToolkit.McpServer.Indexing;
using DotnetToolkit.McpServer.Output;
using DotnetToolkit.McpServer.Store;
using DotnetToolkit.McpServer.Telemetry;
using DotnetToolkit.McpServer.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol.Server;

namespace DotnetToolkit.McpServer.Tools;

/// <summary>
/// The v2 read surface (spec §9, §10, §16): symbol retrieval, relationship traversal, and ranked
/// discovery. All responses are <see cref="Contract.Id"/> JSON envelopes carrying version tokens so the agent
/// can hold leases and avoid re-transmitting unchanged content.
/// </summary>
[McpServerToolType]
public static class ContextTools
{
    private const int ReferenceCap = 50;
    private const int ScopedOverfetchCap = 500;
    private const int SummaryCap = 160;

[McpServerTool(Name = "get_symbol")]
    [Description("Retrieve one or more C# symbols, choosing exactly which fields you get back via include. "
        + "USE THIS INSTEAD OF READING A .cs FILE — it returns the whole symbol even when it is split across "
        + "partial-class files (Read gives you one fragment and no signal that the rest exists), and costs a "
        + "fraction of the tokens of the file. Returns a version token for leasing; pass knownVersion to get "
        + "changed:false when nothing moved, or refetch:true to force content. "
        + "The response always carries a fixed skeleton — kind, displayString, accessibility, containingType, "
        + "declarationSites (file + startLine/endLine) — regardless of include, so locating a symbol never "
        + "needs an extra argument or a second call, and those line spans feed straight into a validate_patch "
        + "edit. Everything past the skeleton is opt-in and controlled by include:\n"
        + "  source — full declaration source text.\n"
        + "  xmlDoc — {summary, returns, remarks, value, inheritdoc, params, typeParams, exceptions}, each "
        + "XML-stripped to plain text and absent when that tag isn't in the doc comment (xmlDoc itself is "
        + "absent only when none of them are). params/typeParams are arrays of {name, text}, one per "
        + "<param>/<typeparam> tag; exceptions is an array of {type, text}, one per <exception> tag; "
        + "inheritdoc is true when an <inheritdoc/> tag is present (regardless of any other tag).\n"
        + "  mechanicalFacts — server-computed structural facts as opaque JSON; null if the body changed "
        + "since they were computed.\n"
        + "  referenceCounts — {implementations, overrides} always; adds {callers, tests} for a member "
        + "(never present for a type, since call edges are recorded against members only).\n"
        + "  recentLog — the last few development-log entries touching this symbol, each carrying "
        + "current:true/false against the live body so stale history reads as stale, not as fact.\n"
        + "  members — for a type only: [{symbolId, displayString, kind, contentVersion}] per member, "
        + "each independently leasable without fetching its body; null for a non-type symbol.\n"
        + "  attributes — this symbol's own C# attributes (not inherited), as [{name, arguments}]; name "
        + "strips a trailing \"Attribute\" suffix (e.g. [Obsolete] -> \"Obsolete\"); arguments is a compact "
        + "rendering of constructor/named arguments, with a long string argument truncated rather than "
        + "reproduced in full. Absent when the symbol has no attributes.\n"
        + "include takes exactly one of: omitted or \"standard\" (default) for xmlDoc+referenceCounts+recentLog, "
        + "the set meaningful on nearly every call; \"all\" for every component above; or a comma-separated "
        + "list of component names, which REPLACES the default rather than adding to it — a literal query of "
        + "exactly the columns you want, e.g. include:\"source\" for just the text, or "
        + "include:\"xmlDoc,mechanicalFacts,referenceCounts,recentLog\" for everything except source. A "
        + "misspelled name is an invalid_component error, not silently dropped. "
        + "For several symbols in one call, use symbols instead of symbol — one include applies to all of "
        + "them, returning one result per symbol instead of issuing the call several times for the same "
        + "total answer. A result whose symbol did not resolve carries error instead of symbolId/content.")]
    public static async Task<string> GetSymbol(
        WorkspaceHost workspace,
        SolutionLocator locator,
        ProjectIndex index,
        SymbolStore symbolStore,
        FeatureLogStore featureLog,
        SymbolIndexBuilder indexBuilder,
        TelemetryRecorder telemetry,
        [Description("Fully-qualified name (append a parameter list to pick an overload), a unique suffix, or a sym_... id from a previous response. Exactly one of symbol or symbols is required.")] string? symbol = null,
        [Description("\"standard\" (default, omit this) | \"all\" | a comma-separated list of component "
            + "names that replaces the default set exactly: source, xmlDoc, mechanicalFacts, "
            + "referenceCounts, recentLog, members, attributes. See the tool description for what each "
            + "returns.")] string? include = null,
        [Description("Held version token to lease against. Not applied when symbols (batch) is used — "
            + "see symbols.")] string? knownVersion = null,
        [Description("Force full content even if the version matches. Not applied when symbols (batch) is used.")] bool refetch = false,

        [Description("Fetch several symbols in one call instead of symbol. The same include is applied to "
            + "every entry. knownVersion/refetch are ignored here — leasing needs one token per symbol, "
            + "which a single knownVersion cannot express, so every batch result carries full content "
            + "regardless of what the caller already holds. Exactly one of symbol or symbols is required.")]
            string[]? symbols = null)
    {
        var sessionId = Ids.AmbientSession;
        var taskId = sessionId;
        var toolCallId = Ids.ToolCall();

        var targets = symbols is { Length: > 0 } ? symbols : symbol is not null ? [symbol] : null;
        if (targets is not { Length: > 0 })
            return Formats.Render(new { error = "missing_symbol", detail = "Provide exactly one of symbol or symbols." });

        if (targets is [var only] && symbols is null)
        {
            var only1Json = await GetSymbolOne(workspace, locator, index, symbolStore, featureLog, indexBuilder, telemetry,
                toolCallId, sessionId, taskId, only, include, knownVersion, refetch);
            return Formats.Render(JsonSerializer.Deserialize<JsonElement>(only1Json));
        }

        // Batch: each entry is a full, independent fetch (see the knownVersion/refetch note above) sharing
        // one toolCallId, so the telemetry rows this produces read as one tool call over several symbols
        // rather than several unrelated calls. Each result is the same shape GetSymbolOne already returns
        // for a single symbol (parsed back out of its own JSON so the batch can build one array), not
        // hoisted or column-shaped — whichever OutputFormat is active does its own thing with a plain
        // uniform array of these.
        var results = new List<JsonElement>(targets.Length);
        foreach (var target in targets)
        {
            var itemJson = await GetSymbolOne(workspace, locator, index, symbolStore, featureLog, indexBuilder,
                telemetry, toolCallId, sessionId, taskId, target, include,
                knownVersion: null, refetch: false);
            results.Add(JsonSerializer.Deserialize<JsonElement>(itemJson));
        }

        return Formats.Render(new { results });
    }

private static async Task<string> GetSymbolOne(
        WorkspaceHost workspace, SolutionLocator locator, ProjectIndex index, SymbolStore symbolStore,
        FeatureLogStore featureLog, SymbolIndexBuilder indexBuilder, TelemetryRecorder telemetry,
        string toolCallId, string sessionId, string taskId,
        string symbol, string? include, string? knownVersion, bool refetch)
    {
        // Every return in this method is PLAIN JSON, regardless of Formats.Current — its result is
        // always re-parsed and re-rendered by its caller (GetSymbol, for both the single-symbol and
        // batch paths), never returned to an MCP caller directly. Rendering in the active format here
        // (e.g. TOON) would make that re-parse fail outright.
        var solution = await workspace.GetSolutionAsync();
        if (solution is null)
        {
            // Workspace not ready: answer from the syntax index at signature level (Conformance C11).
            await index.EnsureFreshAsync();
            var fallback = IndexSymbol(index, locator, symbol, include);
            if (fallback is { } fb)
            {
                var indexLease = Lease.Evaluate(ContentVersion.Parse(fb.Version), knownVersion, refetch);
                object indexEnvelope = indexLease.OmitContent
                    ? new
                    {
                        contract = Contract.Id, toolCallId, symbolId = fb.SymbolId, contentVersion = fb.Version,
                        changed = false, heldVersion = indexLease.HeldVersion, limitedBy = "index_only",
                        content = (object?)null, refetchHint = Lease.RefetchHint,
                    }
                    : new
                    {
                        contract = Contract.Id, toolCallId, symbolId = fb.SymbolId, contentVersion = fb.Version,
                        changed = true, heldVersion = (string?)null, limitedBy = "index_only", content = fb.Content,
                    };
                var indexJson = Formats.ToJson(indexEnvelope);
                return Record(telemetry, toolCallId, sessionId, taskId, "get_symbol", symbol, fb.SymbolId, include ?? "standard",
                    knownVersion, refetch, indexLease.OmitContent, fb.Version, 1, "index_only", null, indexJson);
            }

            var loading = Formats.ToJson(new { error = workspace.State == WorkspaceState.Loading ? "workspace_loading" : "no_workspace" });
            return Record(telemetry, toolCallId, sessionId, taskId, "get_symbol", symbol, null, include ?? "standard",
                knownVersion, refetch, false, null, 0, "index_only", "workspace_loading", loading);
        }

        var (sym, error) = await ResolveAsPlainJsonAsync(solution, ResolveHandle(symbol, symbolStore));
        if (sym is null)
            return Record(telemetry, toolCallId, sessionId, taskId, "get_symbol", symbol, null, include ?? "standard",
                knownVersion, refetch, false, null, 0, "live", "unresolved", error!);

        var symbolId = SymbolKey.IdOf(sym);

        var components = SymbolComponents.Resolve(include, out var invalidComponent);
        if (components is not { } parts)
        {
            var badComponent = Formats.ToJson(new
            {
                error = "invalid_component",
                detail = $"'{invalidComponent}' is not a component. Valid: {string.Join(", ", SymbolComponents.All)}.",
            });
            return Record(telemetry, toolCallId, sessionId, taskId, "get_symbol", symbol, symbolId, include ?? "standard",
                knownVersion, refetch, false, null, 0, "live", "invalid_component", badComponent);
        }

        // The token describes only the layers this response's components were derived from, so a caller
        // that held it can never be told "unchanged" about content it was never sent.
        var version = FullVersionOf(sym, symbolStore).Narrow(parts.RequiredLayers);
        var limitedBy = await LimitedByAsync(workspace, indexBuilder, SourceFilesOf(sym));
        var lease = Lease.Evaluate(version, knownVersion, refetch, parts.RequiredLayers);

        object envelope;
        if (lease.OmitContent)
        {
            envelope = new
            {
                symbolId,
                contentVersion = version.ToString(),
                changed = false,
                heldVersion = lease.HeldVersion,
                limitedBy,
                refetchHint = Lease.RefetchHint,
            };
        }
        else
        {
            var content = await BuildContent(sym, parts, solution, locator, symbolStore, indexBuilder, featureLog);
            envelope = new
            {
                symbolId,
                contentVersion = version.ToString(),
                limitedBy,
                components = include is null ? null : parts.Resolved,
                content,
            };
        }

        var json = Formats.ToJson(envelope);
        return Record(telemetry, toolCallId, sessionId, taskId, "get_symbol", symbol, symbolId, include ?? "standard",
            knownVersion, refetch, lease.OmitContent, version.ToString(), 1, limitedBy, null, json);
    }

    /// <summary>Plain-JSON variant of <see cref="ResolveAsync"/> for use inside <see cref="GetSymbolOne"/> only.</summary>
    private static async Task<(ISymbol? Symbol, string? Error)> ResolveAsPlainJsonAsync(Solution solution, string symbol)
    {
        var resolution = await SymbolResolver.ResolveAsync(solution, symbol);
        if (resolution.Symbol is not null)
            return (resolution.Symbol, null);
        if (resolution.Candidates.Count == 0)
            return (null, Formats.ToJson(new { error = "symbol_not_found", symbol }));
        return (null, Formats.ToJson(new
        {
            error = "ambiguous_symbol",
            candidates = resolution.Candidates.Take(10).Select(c => new
            {
                symbolId = SymbolKey.IdOf(c),
                displayString = c.ToDisplayString(),
            }),
        }));
    }

[McpServerTool(Name = "get_references")]
    [Description("Callers, implementations or overrides of a C# symbol, from the compiler's own model. "
        + "USE THIS INSTEAD OF GREP — grep gives wrong caller lists: it cannot see interface, virtual or delegate "
        + "dispatch, counts comment and string matches as hits, and silently drops sites when output is truncated. "
        + "Returns every real call site, no false positives, and reports how many text-only matches it excluded "
        + "as excludedTextMatches (callers direction only). "
        + "Each item carries symbolId, contentVersion (independent of the target symbol's own version, so an "
        + "item can be leased on its own), displayString, dispatchKind, and sites — a list of {file, line, "
        + "snippet}, one entry per call site for that symbol. isTest is present only when true; content (the "
        + "inline body) only with includeBodies:true. targetSymbolId confirms which overload this answered "
        + "for; truncated and excludedTextMatches are present only when they apply.")]
    public static async Task<string> GetReferences(
        WorkspaceHost workspace,
        SolutionLocator locator,
        SymbolStore symbolStore,
        TelemetryRecorder telemetry,
        [Description("Fully-qualified name, unique suffix, or a sym_... id from a previous response.")] string symbol,
        [Description("callers | implementations | overrides (default callers).")] string direction = "callers",
        [Description("Include member bodies inline (default false).")] bool includeBodies = false)
    {
        var sessionId = Ids.AmbientSession;
        var taskId = sessionId;
        var toolCallId = Ids.ToolCall();
        var refLimitedBy = workspace.IsDegraded ? "degraded" : null;
        var solution = await workspace.GetSolutionAsync();
        if (solution is null)
        {
            var loading = Error(toolCallId, "workspace_loading");
            return Record(telemetry, toolCallId, sessionId, taskId, "get_references", symbol, null, null,
                null, false, false, null, 0, "index_only", "workspace_loading", loading, direction);
        }

        var (sym, error) = await ResolveAsync(solution, ResolveHandle(symbol, symbolStore), toolCallId);
        if (sym is null)
            return Record(telemetry, toolCallId, sessionId, taskId, "get_references", symbol, null, null,
                null, false, false, null, 0, "live", "unresolved", error!, direction);

        var normalized = direction.Trim().ToLowerInvariant();
        var items = normalized switch
        {
            "implementations" => await Implementations(sym, solution, locator, includeBodies),
            "overrides" => await Overrides(sym, solution, locator, includeBodies),
            _ => await Callers(sym, solution, locator, includeBodies),
        };

        var ordered = items.OrderBy(i => i.DisplayString, StringComparer.Ordinal).ToList();
        var truncated = ordered.Count > ReferenceCap;
        var shown = ordered.Take(ReferenceCap).ToList();

        var excludedComments = normalized == "callers"
            ? await CountTextOnlyMatches(solution, sym.Name)
            : 0;

        var envelope = new
        {
            targetSymbolId = SymbolKey.IdOf(sym),
            items = shown.Select(i => new
            {
                symbolId = i.SymbolId,
                // Per-item version so leases accumulate before any body is fetched (§10).
                contentVersion = i.Version,
                displayString = i.DisplayString,
                sites = i.Sites.Select(s => new { file = s.File, line = s.Line, snippet = s.Snippet }),
                dispatchKind = i.DispatchKind,
                isTest = i.IsTest ? true : (bool?)null,
                content = i.Body,
            }),
            totalItems = ordered.Count,
            truncated = truncated ? true : (bool?)null,
            excludedTextMatches = excludedComments > 0 ? excludedComments : (int?)null,
            limitedBy = refLimitedBy,
        };

        var json = Formats.Render(envelope);
        return Record(telemetry, toolCallId, sessionId, taskId, "get_references", symbol, SymbolKey.IdOf(sym), null,
            null, false, false, null, shown.Count, refLimitedBy, null, json, normalized);
    }

[McpServerTool(Name = "search_index")]
    [Description("Find C# symbols when you don't know their exact names. "
        + "USE THIS INSTEAD OF GREP/GLOB over .cs files — it returns ranked symbols with ids and locations, not "
        + "raw text lines, so there is nothing to hand-filter and no truncation to silently lose hits. "
        + "PUT EVERY TERM YOU ARE LOOKING FOR IN ONE CALL: terms are OR-ed and ranked, so "
        + "query:\"fee ledger TryBuy TrySell\" returns the symbols for all four in a single response. Do NOT "
        + "issue one call per word — that is several times the tokens for a worse-ranked result. Partial and "
        + "camel-case-interior terms work too: \"Ledger\" finds FIFOLedger. "
        + "Each hit carries symbolId, name (the fully-qualified name, directly usable as get_symbol's symbol "
        + "argument), kind, and the file/line it was found at, so going straight there needs no second call; "
        + "a hit whose name maps to several declarations (overloads) has file and line as null rather than "
        + "pointing at the wrong one — follow up with get_symbol, which separates overloads by parameter list. "
        + "Pass pathPrefix to narrow the ranked results to one folder or file. Pass summary:\"has\" or "
        + "summary:\"full\" to check or read each hit's XML doc <summary> without a follow-up get_symbol call — "
        + "full text is capped short, get_symbol's xmlDoc.summary for the untruncated version. "
        + "Follow up with get_symbol when you want the content itself.")]
    public static async Task<string> SearchIndex(
        SymbolStore symbolStore,
        ProjectIndex index,
        WorkspaceHost workspace,
        TelemetryRecorder telemetry,
        [Description("Free-text query over symbol names.")] string query,
        [Description("Optional kind filter, case-insensitive, space- or comma-separated. Valid values: class "
            + "(alias for type), interface, struct, record, enum, delegate, method, property, field, event. "
            + "Bare tokens restrict the search to only those kinds, e.g. \"method property\". Prefix a token "
            + "with '-' to exclude it instead, e.g. \"-struct -enum\" searches every kind except those two. "
            + "If a call mixes both forms, the bare (include) tokens win and the '-' tokens are ignored rather "
            + "than combined with them. An unrecognized value is passed through as-is rather than rejected, so "
            + "a typo silently matches nothing instead of erroring. Omit to search every kind.")] string? kinds = null,
        [Description("Max results (default 10, cap 50).")] int limit = 10,
        [Description("Optional path prefix to narrow results to a folder or file, e.g. \"src/Tools\" or "
            + "\"src/Tools/ContextTools.cs\" (relative to the repo root, forward slashes, matched on a full "
            + "path-segment boundary so \"Tools\" cannot match \"ToolsFoo\"). A hit whose file cannot be "
            + "resolved (an overloaded name — see the file/line note above) is dropped rather than guessed at, "
            + "so scoped results can undercount for a very overload-heavy query. Ranking still runs over the "
            + "whole index first, so a query with many more hits outside the prefix than the internal overfetch "
            + "cap can return fewer than limit even though more in-scope matches exist — narrow the query text "
            + "itself if that happens. Omit to search the whole index.")] string? pathPrefix = null,
        [Description("Include XML doc <summary> info per hit. \"has\": adds hasSummary (bool) per item — cheap "
            + "presence check, no text. \"full\": adds summary (string, the extracted <summary> text, capped at "
            + "160 chars with an ellipsis — fetch get_symbol's xmlDoc.summary for the untruncated text; absent if "
            + "none) per item. Omit for today's behavior (no summary fields, no extra cost). An unrecognized "
            + "value is treated as omitted.")] string? summary = null)
    {
        var sessionId = Ids.AmbientSession;
        var taskId = sessionId;
        var toolCallId = Ids.ToolCall();
        limit = Math.Clamp(limit, 1, ReferenceCap);
        var (includeKindTokens, excludeKindTokens) = ParseKindFilter(kinds);
        var includeKinds = NormalizeKinds(includeKindTokens);
        var excludeKinds = includeKindTokens.Length == 0 ? NormalizeKinds(excludeKindTokens) : null;
        var scope = string.IsNullOrWhiteSpace(pathPrefix) ? null : NormalizePathPrefix(pathPrefix);
        var fetchLimit = scope is null ? limit : ScopedOverfetchCap;
        var summaryMode = summary is "has" or "full" ? summary : null;

        var hits = symbolStore.Search(query, includeKinds, excludeKinds, fetchLimit);
        var searchLimitedBy = workspace.IsDegraded ? "degraded"
            : symbolStore.SymbolCount() > 0 ? null
            : "index_only";

        await index.EnsureFreshAsync();
        var sites = index.LocateWithDocs(hits
            .Select(h => SymbolResolver.NameWithoutParameters(h.FqName))
            .ToHashSet(StringComparer.Ordinal));

        var resolved = hits.Select(h =>
            (Hit: h, Site: sites.GetValueOrDefault(SymbolResolver.NameWithoutParameters(h.FqName))));
        if (scope is not null)
            resolved = resolved.Where(r => WithinPathScope(r.Site?.File, scope));
        var limited = resolved.Take(limit).ToList();

        object envelope = new
        {
            limitedBy = searchLimitedBy,
            items = limited.Select(r => new
            {
                symbolId = r.Hit.SymbolId,
                name = SymbolResolver.CompactName(r.Hit.FqName),
                kind = r.Hit.Kind,
                file = r.Site?.File,
                line = r.Site?.Line,
                hasSummary = summaryMode == "has" ? (bool?)!string.IsNullOrWhiteSpace(r.Site?.Doc) : null,
                summary = summaryMode == "full" && r.Site?.Doc is { } doc ? CompactFormatter.Truncate(doc, SummaryCap) : null,
            }),
        };

        var json = Formats.Render(envelope);
        return Record(telemetry, toolCallId, sessionId, taskId, "search_index", query, null, null,
            null, false, false, null, limited.Count, searchLimitedBy, null, json);
    }


    /// <summary>
    /// Which tier answered, and therefore what the answer cannot be trusted to know. Emitted only when
    /// it is NOT the healthy case, so silence means "fully informed" and costs nothing.
    ///
    /// - <c>degraded</c>: the workspace loaded but projects failed, so results may be silently WRONG,
    ///   not merely thin. It outranks index_only because a missing answer is safer than a false one.
    /// - <c>index_only</c>: answered from the syntax tier, or before the semantic index finished its
    ///   first pass. Reference counts and semantic resolution are unavailable, not zero.
    ///
    /// This is not about content freshness: change detection is mtime-polling and runs before every
    /// query. It reports what the answer could not draw on.
    /// </summary>
    private static string? LimitedBy(WorkspaceHost workspace, SymbolIndexBuilder indexBuilder) =>
        workspace.IsDegraded ? "degraded"
        : indexBuilder.Ready ? null
        : "index_only";

    /// <summary>
    /// As <see cref="LimitedBy(WorkspaceHost, SymbolIndexBuilder)"/>, plus the check that the files this
    /// answer was actually served from still match disk.
    ///
    /// The cheap markers describe the tier; this one describes the answer. A workspace can be fully
    /// loaded, undegraded and still holding a file that moved underneath it, and without this the
    /// response asserts content that no longer exists on disk while looking perfectly healthy.
    ///
    /// Checked after the tier markers because they subsume it: content from the syntax index is
    /// mtime-swept before every query, so <c>index_only</c> is already fresh by construction.
    /// </summary>
    private static async Task<string?> LimitedByAsync(
        WorkspaceHost workspace, SymbolIndexBuilder indexBuilder, IEnumerable<string> servedFromAbsPaths)
    {
        var tier = LimitedBy(workspace, indexBuilder);
        if (tier is not null)
            return tier;
        return await workspace.IsBehindDiskAsync(servedFromAbsPaths) ? "stale" : null;
    }

    /// <summary>Absolute paths of the source files a symbol was read from.</summary>
    private static IEnumerable<string> SourceFilesOf(ISymbol sym) =>
        sym.DeclaringSyntaxReferences
            .Select(r => r.SyntaxTree.FilePath)
            .Where(p => !string.IsNullOrEmpty(p));

    // ---- content builder -----------------------------------------------------

    /// <summary>
    /// Builds the response body for exactly the requested components. One path regardless of how
    /// <c>include</c> resolved: an earlier version special-cased outline with an early return from its
    /// own object literal, which is why it silently lacked containingType and recentLog — a divergence,
    /// not a decision.
    ///
    /// Every optional field is null when not requested, and <c>WhenWritingNull</c> drops it from the
    /// JSON — so an unrequested component costs nothing, not even an empty key.
    /// </summary>
    private static async Task<object> BuildContent(
        ISymbol sym, SymbolComponents components, Solution solution, SolutionLocator locator,
        SymbolStore symbolStore, SymbolIndexBuilder indexBuilder, FeatureLogStore featureLog)
    {
        // referenceCounts is the one component with a real latency cost — it awaits the semantic model —
        // so it is computed only when asked for, rather than computed and then thrown away.
        var counts = components.Has(SymbolComponents.ReferenceCounts) && indexBuilder.Ready
            ? await ReferenceCounts(sym, solution, symbolStore)
            : null;

        var members = components.Has(SymbolComponents.Members) && sym is INamedTypeSymbol type
            ? type.GetMembers().Where(IsListable).Select(m => (object)new
            {
                symbolId = SymbolKey.IdOf(m),
                displayString = m.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                kind = SymbolKey.KindOf(m),
                // decl-layer version so members can be leased without ever fetching their bodies.
                contentVersion = VersionOf(m).ToString(),
            }).ToArray()
            : null;

        // attachedContracts (P4) is deliberately absent rather than emitted as null/empty — an
        // unpopulated field is pure overhead until it carries data.
        return new
        {
            kind = SymbolKey.KindOf(sym),
            displayString = sym.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            accessibility = sym.DeclaredAccessibility.ToString().ToLowerInvariant(),
            containingType = ContainingType(sym),
            declarationSites = DeclarationSites(sym, locator),
            source = components.Has(SymbolComponents.Source) ? SourceOf(sym) : null,
            xmlDoc = components.Has(SymbolComponents.XmlDoc)
                ? OutlineBuilder.SectionsFromXml(sym.GetDocumentationCommentXml())
                : null,
            // Body-derived facts, served only while the body hash they were computed from still holds.
            mechanicalFacts = components.Has(SymbolComponents.MechanicalFacts)
                ? MechanicalFactsFor(sym, symbolStore)
                : null,
            referenceCounts = counts,
            members,
            attributes = components.Has(SymbolComponents.Attributes) ? AttributesOf(sym) : null,
            // Why this code is the way it is. Entries describing a superseded body are flagged rather
            // than presented as current truth.
            recentLog = components.Has(SymbolComponents.RecentLog) ? RecentLogFor(sym, featureLog) : null,
        };
    }

    /// <summary>
    /// This symbol's own C# attributes (not inherited ones — Roslyn's GetAttributes() only returns what
    /// is declared directly on this symbol), as {name, arguments}. name strips a trailing "Attribute"
    /// suffix to match how it reads at the use site (e.g. [Obsolete] -> "Obsolete"). arguments renders
    /// constructor and named arguments as a compact string rather than the raw attribute syntax text,
    /// since some attributes here carry multi-hundred-character Description strings that would otherwise
    /// dominate the response; a long string argument is truncated rather than reproduced in full.
    /// </summary>
    private static object[]? AttributesOf(ISymbol sym)
    {
        var attrs = sym.GetAttributes();
        if (attrs.Length == 0)
            return null;

        return attrs.Select(a => (object)new
        {
            name = a.AttributeClass?.Name is { } n && n.EndsWith("Attribute", StringComparison.Ordinal)
                ? n[..^"Attribute".Length]
                : a.AttributeClass?.Name,
            arguments = FormatAttributeArguments(a),
        }).ToArray();
    }

    private static string? FormatAttributeArguments(AttributeData attribute)
    {
        var parts = new List<string>(attribute.ConstructorArguments.Select(FormatTypedConstant));
        parts.AddRange(attribute.NamedArguments.Select(kv => $"{kv.Key} = {FormatTypedConstant(kv.Value)}"));
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    private static string FormatTypedConstant(TypedConstant constant)
    {
        if (constant.Kind == TypedConstantKind.Array)
            return "[" + string.Join(", ", constant.Values.Select(FormatTypedConstant)) + "]";

        var text = constant.Value?.ToString() ?? "null";
        // Cap rather than reproduce in full: some attributes here carry a multi-hundred-character
        // [Description] string that would otherwise dominate the response on its own.
        const int cap = 120;
        return text.Length > cap ? text[..cap] + "…" : text;
    }

    /// <summary>
    /// The last few development-log entries touching this symbol (spec §9). An entry whose recorded
    /// new version no longer matches the symbol's current body layer is marked <c>current: false</c> —
    /// stale history is surfaced as stale, never silently as fact.
    /// </summary>
    private static object? RecentLogFor(ISymbol sym, FeatureLogStore featureLog)
    {
        var currentId = SymbolKey.IdOf(sym);
        var entries = featureLog.RecentForSymbolWithChain(currentId);
        if (entries.Count == 0)
            return null;

        var currentBody = VersionOf(sym).Get("body");
        return entries.Select(e => new
        {
            logId = e.LogId,
            date = e.CreatedAt.Length >= 10 ? e.CreatedAt[..10] : e.CreatedAt,
            intent = e.Intent,
            detail = e.Detail,
            apiImpact = e.ApiImpact,
            current = currentBody is null || e.NewVersion is null
                || ContentVersion.Parse(e.NewVersion).Get("body") == currentBody,
        }).ToList();
    }

    /// <summary>
    /// Facts are stored as JSON; returning the parsed element keeps them structured in the response
    /// without re-modelling every field here. Null when the body moved since they were computed.
    /// </summary>
    private static object? MechanicalFactsFor(ISymbol sym, SymbolStore symbolStore)
    {
        var version = VersionOf(sym);
        var json = symbolStore.FactsFor(SymbolKey.IdOf(sym), version.Get("body"));
        if (json is null)
            return null;
        try
        {
            return JsonDocument.Parse(json).RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static object? ContainingType(ISymbol sym)
    {
        if (sym.ContainingType is not { } ct)
            return null;
        // No contentVersion here: the containing type is a navigation pointer, not something being leased.
        return new
        {
            symbolId = SymbolKey.IdOf(ct),
            displayString = ct.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
        };
    }

private static object[] DeclarationSites(ISymbol sym, SolutionLocator locator) =>
        sym.DeclaringSyntaxReferences.Select(r =>
        {
            var node = NormalizeDeclNode(r.GetSyntax());
            var (start, end) = DeclarationBoundsIncludingDocComment(node);
            var span = r.SyntaxTree.GetLineSpan(TextSpan.FromBounds(start, end));
            // Flat file/startLine/endLine — these feed straight into a validate_patch edit. Start
            // includes a leading /// doc comment when present, so an edit targeting this span can
            // rewrite the comment along with the declaration, not just the declaration alone.
            return (object)new
            {
                file = locator.RelPath(span.Path),
                startLine = span.StartLinePosition.Line + 1,
                endLine = span.EndLinePosition.Line + 1,
            };
        }).ToArray();

private static string? SourceOf(ISymbol sym)
    {
        var reference = sym.DeclaringSyntaxReferences.FirstOrDefault();
        if (reference is null)
            return null;
        var node = NormalizeDeclNode(reference.GetSyntax());
        var (start, end) = DeclarationBoundsIncludingDocComment(node);
        var text = node.SyntaxTree!.GetText().ToString(TextSpan.FromBounds(start, end));
        var header = sym.ContainingType?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        return header is null ? text : $"// in {header}\n{text}";
    }

    private static async Task<object> ReferenceCounts(ISymbol sym, Solution solution, SymbolStore symbolStore)
    {
        // Include the interface members this symbol implements: calls made through the interface are
        // recorded against the interface member, and get_references cascades to implementations.
        var equivalentIds = new List<string> { SymbolKey.IdOf(sym) };
        equivalentIds.AddRange(ImplementedInterfaceMembers(sym).Select(SymbolKey.IdOf));
        var implementations = await CountImplementations(sym, solution);
        var overrides = await CountOverrides(sym, solution);

        // Call edges are recorded against MEMBERS, never against named types, so a type's caller count
        // would structurally always be 0 — which reads as "nothing uses this" when the truth is "not
        // measured at this level". Omit those fields for types; implementations/overrides are the
        // meaningful relationships for a type anyway.
        if (sym is INamedTypeSymbol)
            return new { callers = (int?)null, implementations, overrides, tests = (int?)null };

        var counts = symbolStore.ReferenceCounts(equivalentIds);

        // A zero from the edge cache is only a fact if the cache covers this symbol's project at all.
        // When the project contributed no edges — typically because it failed to load in MSBuild —
        // omit the counts rather than assert a 0 that get_references will immediately contradict.
        var measured = counts is not null && symbolStore.HasEdgeCoverageFor(SymbolKey.IdOf(sym));
        return new
        {
            callers = measured ? counts!.Value.Callers : (int?)null,
            implementations,
            overrides,
            tests = measured ? counts!.Value.Tests : (int?)null,
        };
    }

    /// <summary>Interface members this symbol implements, so counts can span the interface boundary.</summary>
    private static IEnumerable<ISymbol> ImplementedInterfaceMembers(ISymbol sym)
    {
        if (sym.ContainingType is not { } type)
            yield break;
        foreach (var iface in type.AllInterfaces)
        {
            foreach (var member in iface.GetMembers())
            {
                if (SymbolEqualityComparer.Default.Equals(type.FindImplementationForInterfaceMember(member), sym))
                    yield return member;
            }
        }
    }

    private static async Task<int> CountImplementations(ISymbol sym, Solution solution) => sym switch
    {
        INamedTypeSymbol { TypeKind: TypeKind.Interface } nt => (await SymbolFinder.FindImplementationsAsync(nt, solution)).Count(),
        INamedTypeSymbol nt => (await SymbolFinder.FindDerivedClassesAsync(nt, solution)).Count(),
        _ when sym.ContainingType?.TypeKind == TypeKind.Interface => (await SymbolFinder.FindImplementationsAsync(sym, solution)).Count(),
        _ => 0,
    };

    private static async Task<int> CountOverrides(ISymbol sym, Solution solution) =>
        sym is { IsVirtual: true } or { IsAbstract: true } && sym is not INamedTypeSymbol
            ? (await SymbolFinder.FindOverridesAsync(sym, solution)).Count()
            : 0;

    // ---- reference directions -------------------------------------------------

    private sealed record RefItem(string SymbolId, string Version, string DisplayString,
        IReadOnlyList<(string File, int Line, string Snippet)> Sites, string? DispatchKind, string? Body,
        bool IsTest = false);

    private static async Task<List<RefItem>> Callers(ISymbol sym, Solution solution, SolutionLocator locator, bool includeBodies)
    {
        var dispatch = DispatchKindOf(sym);
        var items = new List<RefItem>();
        foreach (var caller in await SymbolFinder.FindCallersAsync(sym, solution))
        {
            if (!caller.Locations.Any(l => l.IsInSource))
                continue;
            var sites = caller.Locations.Select(l =>
            {
                var span = l.GetLineSpan();
                return (locator.RelPath(span.Path), span.StartLinePosition.Line + 1, l.SourceTree?.GetText().Lines[span.StartLinePosition.Line].ToString().Trim() ?? "");
            }).ToList();
            items.Add(new RefItem(
                SymbolKey.IdOf(caller.CallingSymbol),
                VersionOf(caller.CallingSymbol).ToString(),
                caller.CallingSymbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                sites!,
                dispatch,
                includeBodies ? SourceOf(caller.CallingSymbol) : null,
                TestAttributes.IsTestMethod(caller.CallingSymbol)));
        }
        return items;
    }

    private static async Task<List<RefItem>> Implementations(ISymbol sym, Solution solution, SolutionLocator locator, bool includeBodies)
    {
        IEnumerable<ISymbol> results = sym switch
        {
            INamedTypeSymbol { TypeKind: TypeKind.Interface } nt => await SymbolFinder.FindImplementationsAsync(nt, solution),
            INamedTypeSymbol nt => await SymbolFinder.FindDerivedClassesAsync(nt, solution),
            _ => await SymbolFinder.FindImplementationsAsync(sym, solution),
        };
        return results.Select(s => ToItem(s, locator, null, includeBodies)).ToList();
    }

    private static async Task<List<RefItem>> Overrides(ISymbol sym, Solution solution, SolutionLocator locator, bool includeBodies)
    {
        var results = await SymbolFinder.FindOverridesAsync(sym, solution);
        return results.Select(s => ToItem(s, locator, null, includeBodies)).ToList();
    }

    private static RefItem ToItem(ISymbol s, SolutionLocator locator, string? dispatch, bool includeBodies)
    {
        var sites = s.Locations.Where(l => l.IsInSource).Select(l =>
        {
            var span = l.GetLineSpan();
            return (locator.RelPath(span.Path), span.StartLinePosition.Line + 1, s.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
        }).ToList();
        return new RefItem(SymbolKey.IdOf(s), VersionOf(s).ToString(),
            s.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat), sites!, dispatch,
            includeBodies ? SourceOf(s) : null);
    }

    private static string DispatchKindOf(ISymbol target)
    {
        if (target.ContainingType?.TypeKind == TypeKind.Interface)
            return "interface";
        if (target is IMethodSymbol { MethodKind: MethodKind.DelegateInvoke })
            return "delegate";
        if (target.IsVirtual || target.IsAbstract || target.IsOverride)
            return "virtual";
        return "direct";
    }

    /// <summary>
    /// Counts occurrences of the identifier in comment trivia and string literals across the solution.
    /// These are exactly the matches a text search would surface and get_references must NOT return as
    /// items (Conformance C7); they are reported only under excludedKinds.
    /// </summary>
    private static async Task<int> CountTextOnlyMatches(Solution solution, string name)
    {
        var count = 0;
        foreach (var project in solution.Projects)
        {
            foreach (var document in project.Documents)
            {
                var root = await document.GetSyntaxRootAsync();
                if (root is null)
                    continue;
                foreach (var trivia in root.DescendantTrivia())
                {
                    if (trivia.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.SingleLineCommentTrivia)
                        || trivia.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.MultiLineCommentTrivia))
                    {
                        if (trivia.ToString().Contains(name, StringComparison.Ordinal))
                            count++;
                    }
                }
                foreach (var literal in root.DescendantTokens()
                    .Where(t => t.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.StringLiteralToken)))
                {
                    if (literal.ValueText.Contains(name, StringComparison.Ordinal))
                        count++;
                }
            }
        }
        return count;
    }

    /// <summary>
    /// Canonicalizes caller-supplied kind filters: accepts "Class" as an alias for the stored "Type",
    /// and normalizes casing so "method" behaves like "Method".
    /// </summary>
    private static string[]? NormalizeKinds(string[]? kinds)
    {
        if (kinds is null || kinds.Length == 0)
            return null;
        return [.. kinds.Select(k => k.Trim().ToLowerInvariant() switch
        {
            "class" => "Type",
            "type" => "Type",
            "interface" => "Interface",
            "struct" => "Struct",
            "enum" => "Enum",
            "delegate" => "Delegate",
            "record" => "Record",
            "method" => "Method",
            "property" => "Property",
            "field" => "Field",
            "event" => "Event",
            _ => k.Trim(),
        })];
    }

    /// <summary>
    /// Splits search_index's <c>kinds</c> argument into include/exclude token arrays. A bare token is an
    /// include; a token prefixed with '-' is an exclude, e.g. "method property -struct" (mixing the two
    /// is legal to parse — SearchIndex decides above whether to honor the exclude side or drop it once
    /// include is non-empty; this method only splits). Space, comma, tab, and newline all separate
    /// tokens, so "method,property" and "method property" parse the same way.
    /// </summary>
    private static (string[] Include, string[] Exclude) ParseKindFilter(string? kinds)
    {
        if (string.IsNullOrWhiteSpace(kinds))
            return ([], []);
        var tokens = kinds.Split([' ', ',', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var include = tokens.Where(t => t[0] != '-').ToArray();
        var exclude = tokens.Where(t => t[0] == '-' && t.Length > 1).Select(t => t[1..]).ToArray();
        return (include, exclude);
    }

    // pathPrefix is relative to the repo root (SolutionLocator.RelPath already yields forward
    // slashes), so normalizing here only needs to fold backslashes and strip a leading "./" - never
    // an absolute-path translation.
    private static string NormalizePathPrefix(string pathPrefix)
    {
        var normalized = pathPrefix.Replace('\\', '/').Trim('/');
        return normalized.StartsWith("./", StringComparison.Ordinal) ? normalized[2..] : normalized;
    }

    // Segment-boundary match, not a raw StartsWith: a prefix of "Tools" must not match a sibling
    // folder named "ToolsFoo". A null file (an overload site.Locate could not disambiguate) is out
    // of scope rather than guessed into it.
    private static bool WithinPathScope(string? file, string normalizedPrefix)
    {
        if (file is null)
            return false;
        if (file.Equals(normalizedPrefix, StringComparison.Ordinal))
            return true;
        return file.StartsWith(normalizedPrefix + "/", StringComparison.Ordinal);
    }

    /// <summary>
    /// Accepts either a name spec or a <c>sym_…</c> identifier handed out by a previous response,
    /// mapping the latter back to a resolvable name via the symbol index.
    /// </summary>
    private static string ResolveHandle(string symbol, SymbolStore symbolStore)
    {
        if (!symbol.StartsWith("sym_", StringComparison.Ordinal))
            return symbol;
        return symbolStore.FqNameFor(symbol) ?? symbol;
    }

    // ---- syntax-index fallback (index_only mode) ------------------------------

    /// <summary>
    /// Signature-level get_symbol served from the syntax index when the semantic workspace is not yet
    /// ready (spec §Startup, Conformance C11). The version token is computed by re-parsing the single
    /// declaring file, so leases remain valid across the index_only → live transition for unchanged
    /// declarations. referenceCounts is null here — counts require the edge cache.
    /// </summary>
private static (object Content, string Version, string SymbolId)? IndexSymbol(
        ProjectIndex index, SolutionLocator locator, string symbol, string? include)
    {
        var namePart = symbol;
        var paren = namePart.IndexOf('(');
        if (paren >= 0)
            namePart = namePart[..paren];
        var simple = namePart[(namePart.LastIndexOf('.') + 1)..];
        var lt = simple.IndexOf('<');
        if (lt >= 0)
            simple = simple[..lt];
        if (simple.Length == 0)
            return null;

        var (hits, _) = index.FindSymbol(simple, null, 10);
        var hit = hits.FirstOrDefault(h =>
                      h.FqName.Equals(namePart, StringComparison.OrdinalIgnoreCase)
                      || h.FqName.EndsWith("." + namePart, StringComparison.OrdinalIgnoreCase))
                  ?? hits.FirstOrDefault(h => h.Name.Equals(simple, StringComparison.OrdinalIgnoreCase));
        if (hit is null)
            return null;

        var version = "decl:index";
        string? source = null;
        try
        {
            var text = File.ReadAllText(locator.AbsPath(hit.File));
            var root = CSharpSyntaxTree.ParseText(text).GetRoot();
            var node = root.DescendantNodes()
                .Where(IsIndexableDeclaration)
                .FirstOrDefault(n => n.SyntaxTree.GetLineSpan(n.Span).StartLinePosition.Line + 1 == hit.Line);
            if (node is not null)
            {
                var (decl, body) = SyntaxFingerprint.Compute(NormalizeDeclNode(node));
                version = ContentVersion.Of(decl, body).ToString();
                if (SymbolComponents.Resolve(include, out _) is { } parts && parts.Has(SymbolComponents.Source))
                    source = node.ToString();
            }
        }
        catch
        {
            // A parse/IO failure just yields the placeholder version; still index_only, still honest.
        }

        var content = new
        {
            kind = MapKindCode(hit.Kind),
            displayString = hit.FqName,
            declarationSites = new object[] { new { file = hit.File, span = new { startLine = hit.Line, endLine = hit.Line } } },
            source,
            xmlDoc = hit.Doc,
            referenceCounts = (object?)null,
        };
        return (content, version, Ids.SymbolId(hit.FqName, ""));
    }

    private static bool IsIndexableDeclaration(SyntaxNode node) => node
        is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax or BaseMethodDeclarationSyntax
        or PropertyDeclarationSyntax or EventDeclarationSyntax or BaseFieldDeclarationSyntax;

    private static string MapKindCode(string code) => code switch
    {
        "C" => "Type", "I" => "Interface", "S" => "Struct", "R" => "Record", "E" => "Enum", "D" => "Delegate",
        "M" => "Method", "K" => "Method", "P" => "Property", "F" => "Field", "V" => "Event",
        _ => code,
    };

    // ---- shared helpers -------------------------------------------------------

    private static ContentVersion VersionOf(ISymbol symbol)
    {
        var reference = symbol.DeclaringSyntaxReferences.FirstOrDefault();
        if (reference is null)
            return ContentVersion.Of(decl: "external");
        var (decl, body) = SyntaxFingerprint.Compute(NormalizeDeclNode(reference.GetSyntax()));
        return ContentVersion.Of(decl, body);
    }

    /// <summary>
    /// The full four-layer token: syntax layers computed on demand, plus the semantic refs/api layers
    /// from the index when it has them. Comparison is per supplied layer, so a caller holding only
    /// decl+body still leases correctly against this.
    /// </summary>
    private static ContentVersion FullVersionOf(ISymbol symbol, SymbolStore symbolStore)
    {
        var syntax = VersionOf(symbol);
        var (refs, api) = symbolStore.LayersFor(SymbolKey.IdOf(symbol));
        return ContentVersion.Of(syntax.Get("decl"), syntax.Get("body"), refs, api);
    }

private static SyntaxNode NormalizeDeclNode(SyntaxNode node) =>
        node is VariableDeclaratorSyntax && node.FirstAncestorOrSelf<BaseFieldDeclarationSyntax>() is { } field
            ? field
            : node;

    /// <summary>
    /// A declaration's real boundary for display/editing purposes: <paramref name="node"/>'s own Span
    /// (Roslyn's node.Span/ToString() exclude the outermost leading trivia, so a /// doc comment sitting
    /// there is invisible to both) widened to start at that doc comment when one is present. The end is
    /// unchanged — trailing trivia is never part of "the declaration" the way a comment ABOVE it is.
    /// </summary>
private static (int Start, int End) DeclarationBoundsIncludingDocComment(SyntaxNode node)
    {
        var doc = node.GetLeadingTrivia().FirstOrDefault(t =>
            t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) ||
            t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia));
        // FullSpan, not Span: a documentation comment trivia's OWN Span excludes the "///" exterior
        // marker on its first line (Roslyn attaches it as leading trivia of the structure's first
        // token), so Span alone would start one line right but three characters short.
        var start = doc.IsKind(SyntaxKind.None) ? node.SpanStart : doc.FullSpan.Start;
        return (start, node.Span.End);
    }

    private static bool IsListable(ISymbol member)
    {
        if (member.IsImplicitlyDeclared)
            return false;
        if (member is IMethodSymbol { MethodKind: not (MethodKind.Ordinary or MethodKind.Constructor) })
            return false;
        return member.DeclaredAccessibility is Accessibility.Public or Accessibility.Protected or Accessibility.Internal;
    }

    private static async Task<(ISymbol? Symbol, string? Error)> ResolveAsync(Solution solution, string symbol, string toolCallId)
    {
        var resolution = await SymbolResolver.ResolveAsync(solution, symbol);
        if (resolution.Symbol is not null)
            return (resolution.Symbol, null);
        if (resolution.Candidates.Count == 0)
            return (null, Formats.Render(new { error = "symbol_not_found", symbol }));
        return (null, Formats.Render(new
        {
            error = "ambiguous_symbol",
            candidates = resolution.Candidates.Take(10).Select(c => new
            {
                symbolId = SymbolKey.IdOf(c),
                displayString = c.ToDisplayString(),
            }),
        }));
    }

    // detail carries what the caller needs to correct the call — omitted when the kind says it all.
    private static string Error(string toolCallId, string kind, string? detail = null) =>
        Formats.Render(new { error = kind, detail });

    private static string Record(
        TelemetryRecorder telemetry, string toolCallId, string sessionId, string taskId, string tool,
        string requestedSymbol, string? symbolId, string? resolution, string? knownVersion, bool refetch,
        bool leaseHit, string? contentVersion, int returnedSymbols, string? limitedBy, string? errorKind, string result,
        string? direction = null)
    {
        telemetry.RecordRetrieval(new RetrievalEvent
        {
            ToolCallId = toolCallId,
            SessionId = sessionId,
            TaskId = taskId,
            ToolName = tool,
            RequestedSymbol = requestedSymbol,
            SymbolId = symbolId,
            Resolution = resolution,
            Direction = direction,
            KnownVersion = knownVersion,
            Refetch = refetch,
            LeaseHit = leaseHit,
            ContentVersion = contentVersion,
            ReturnedSymbols = returnedSymbols,
            ReturnedTokens = TelemetryRecorder.EstimateTokens(result),
            // Telemetry keeps the pre-3.0 column name: retrieval_events is immutable raw history and
            // its rows cannot be rewritten, so renaming the column would split one signal across two.
            Staleness = limitedBy ?? "live",
            ErrorKind = errorKind,
        });
        return result;
    }
}
