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
    [Description("Retrieve one or more C# symbols — a class, interface, method, property or field: its "
        + "signature, XML docs, source text, members, attributes, base type, reference counts and exact "
        + "file location. Read, inspect, show, view, look at, open, what does this look like. "
        + "USE THIS INSTEAD OF READING A .cs FILE — it returns the whole symbol even when it is split "
        + "across partial-class files (Read gives you one fragment and no signal that the rest exists), "
        + "and costs a fraction of the tokens of the file. "
        + "Every response carries declarationSites (file + startLine/endLine, INCLUDING a leading /// doc "
        + "comment when present) and a contentVersion token — these are exactly what validate_patch needs "
        + "as baseVersions and edit spans. search_index's line/endLine EXCLUDE the doc comment, so never "
        + "anchor an edit on a span read off search_index without confirming it here. "
        + "include selects what comes back, in one of three forms: omitted or \"standard\" (default) for "
        + "xmlDoc+referenceCounts+recentLog; \"all\" for everything; or a comma-separated component list "
        + "that REPLACES the default rather than adding to it. Components: source, xmlDoc, "
        + "mechanicalFacts, bodyOutline, referenceCounts, recentLog, members, attributes, baseType, "
        + "interfaces, usings. "
        + "source:code drops the leading /// doc comment; appending @ plus absolute file line ranges "
        + "returns only those lines (source:code@46-76;79-83) — the way to read one region of a long "
        + "member instead of all of it. Use symbols instead of symbol to fetch several at once. "
        + "Full component semantics, the @ and -modifier grammar, and worked examples: "
        + "docs/tools/get_symbol.md.")]
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
            + "names that replaces the default set exactly: source (optionally source:code, either mode "
            + "with -tag/-attributes/-comments subtracted, and/or an @ line selection returning only those "
            + "absolute file lines, e.g. source:code@46-76;79-83), xmlDoc, mechanicalFacts, bodyOutline, "
            + "referenceCounts, recentLog, members, attributes, baseType, interfaces, usings. See "
            + "docs/tools/get_symbol.md for what each returns, the full @ grammar, and why source "
            + "suppresses several of these.")] string? include = null,

        [Description("Fetch several symbols in one call instead of symbol. The same include is applied to "
            + "every entry, and each entry is a full, independent fetch. A source @line selection is rejected here, "
            + "since one span of file lines cannot apply to several symbols. Exactly one of symbol or "
            + "symbols is required.")]
            string[]? symbols = null,
        [Description(ToolTelemetry.TaskIdParam)] string? taskId = null)
    {
        var sessionId = Ids.AmbientSession;
        var attributedTask = Ids.TaskId(taskId);
        var toolCallId = Ids.ToolCall();

        var targets = symbols is { Length: > 0 } ? symbols : symbol is not null ? [symbol] : null;
        if (targets is not { Length: > 0 })
            return Formats.Render(new { error = "missing_symbol", detail = "Provide exactly one of symbol or symbols." });

        // A line selection names a span in one specific file, so applying the same include unchanged to
        // every entry would slice each symbol by another one's line numbers. Rejected rather than
        // silently returning fragments that look like real answers.
        if (symbols is { Length: > 0 } && SymbolComponents.Resolve(include, out _) is { HasSlicedSource: true })
        {
            return Formats.Render(new
            {
                error = "lines_with_batch",
                detail = "A source @line selection applies to one symbol's own file lines; fetch it with symbol, not symbols.",
            });
        }

        if (targets is [var only] && symbols is null)
        {
            var one = await GetSymbolOne(workspace, locator, index, symbolStore, featureLog, indexBuilder,
                toolCallId, sessionId, attributedTask, only, include);
            var rendered = Formats.Render(JsonSerializer.Deserialize<JsonElement>(one.Json));
            return Record(telemetry, toolCallId, sessionId, attributedTask, "get_symbol", only, one.SymbolId,
                include ?? "standard", one.ContentVersion, 1,
                one.LimitedBy, one.ErrorKind, rendered);
        }

        // Batch: each entry is a full, independent fetch sharing one toolCallId. Telemetry is recorded
        // ONCE for the whole batch, against the final rendered text (not each entry's intermediate JSON)
        // — recording per-entry would both miss the wrapping results[] overhead this response actually
        // costs, and double-count tokens across the batch's shared toolCallId.
        var results = new List<JsonElement>(targets.Length);
        string? firstErrorKind = null;
        string? firstLimitedBy = null;
        foreach (var target in targets)
        {
            var one = await GetSymbolOne(workspace, locator, index, symbolStore, featureLog, indexBuilder,
                toolCallId, sessionId, attributedTask, target, include);
            results.Add(JsonSerializer.Deserialize<JsonElement>(one.Json));
            firstErrorKind ??= one.ErrorKind;
            firstLimitedBy ??= one.LimitedBy;
        }

        var batchRendered = Formats.Render(new { results });
        return Record(telemetry, toolCallId, sessionId, attributedTask, "get_symbol", string.Join(",", targets),
            null, include ?? "standard", null, targets.Length, firstLimitedBy,
            firstErrorKind, batchRendered);
    }


private static async Task<SymbolFetchResult> GetSymbolOne(
        WorkspaceHost workspace, SolutionLocator locator, ProjectIndex index, SymbolStore symbolStore,
        FeatureLogStore featureLog, SymbolIndexBuilder indexBuilder,
        string toolCallId, string sessionId, string taskId,
        string symbol, string? include)
    {
        // Every return in this method is PLAIN JSON, regardless of Formats.Current — its result is
        // always re-parsed and re-rendered by its caller (GetSymbol, for both the single-symbol and
        // batch paths), which also records telemetry against that final rendered text rather than this
        // intermediate JSON. Rendering in the active format here (e.g. TOON) would make that re-parse
        // fail outright.
        var solution = await workspace.GetSolutionAsync();
        if (solution is null)
        {
            // A reload in progress after a previous successful load means a live answer is imminent (or
            // already timed out waiting for one) -- minting an index-only id here would only diverge
            // from the live one moments later (see Ids.IndexOnlySymbolId). Only fall back to the syntax
            // index when no live answer has ever existed yet.
            var reloadInProgress = workspace.State == WorkspaceState.Loading && workspace.HasLoadedOnce;
            if (!reloadInProgress)
            {
                // Workspace not ready: answer from the syntax index at signature level (Conformance C11).
                await index.EnsureFreshAsync();
                var fallback = IndexSymbol(index, locator, symbol, include);
                if (fallback is { } fb)
                {
                    var indexEnvelope = new
                    {
                        contract = Contract.Id, toolCallId, symbolId = fb.SymbolId, contentVersion = fb.Version,
                        limitedBy = "index_only", content = fb.Content,
                    };
                    var indexJson = Formats.ToJson(indexEnvelope);
                    return new SymbolFetchResult(indexJson, fb.SymbolId, fb.Version, "index_only", null);
                }
            }

            var loading = Formats.ToJson(new { error = workspace.State == WorkspaceState.Loading ? "workspace_loading" : "no_workspace" });
            return new SymbolFetchResult(loading, null, null, "index_only", "workspace_loading");
        }

        var (sym, error) = await ResolveAsPlainJsonAsync(solution, ResolveHandle(symbol, symbolStore), symbolStore);
        if (sym is null)
            return new SymbolFetchResult(error!, null, null, "live", "unresolved");

        var symbolId = SymbolKey.IdOf(sym);

        var components = SymbolComponents.Resolve(include, out var invalidComponent);
        if (components is not { } parts)
        {
            var badComponent = Formats.ToJson(new
            {
                error = "invalid_component",
                detail = $"'{invalidComponent}' is not a component. Valid: {string.Join(", ", SymbolComponents.All)}.",
            });
            return new SymbolFetchResult(badComponent, symbolId, null, "live", "invalid_component");
        }

        // The token describes only the layers this response's components were derived from, so a caller
        // relying on it to detect drift is never told about a layer it never actually received.
        var version = FullVersionOf(sym, symbolStore).Narrow(parts.RequiredLayers);
        var limitedBy = await LimitedByAsync(workspace, indexBuilder, SourceFilesOf(sym));

        var content = await BuildContent(sym, parts, solution, locator, symbolStore, indexBuilder, featureLog);
        var envelope = new
        {
            symbolId,
            contentVersion = version.ToString(),
            limitedBy,
            components = include is null ? null : parts.Resolved,
            content,
        };

        var json = Formats.ToJson(envelope);
        return new SymbolFetchResult(json, symbolId, version.ToString(), limitedBy, null);
    }

    // Carries GetSymbolOne's result plus the telemetry fields its caller needs to record against the
    // FINAL rendered text (GetSymbol re-renders this Json in the active OutputFormat before recording),
    // not this intermediate JSON.
    private readonly record struct SymbolFetchResult(
        string Json,
        string? SymbolId,
        string? ContentVersion,
        string? LimitedBy,
        string? ErrorKind);


/// <summary>Plain-JSON variant of <see cref="ResolveAsync"/> for use inside <see cref="GetSymbolOne"/> only.</summary>
    private static async Task<(ISymbol? Symbol, string? Error)> ResolveAsPlainJsonAsync(
        Solution solution, string symbol, SymbolStore symbolStore)
    {
        var resolution = await SymbolResolver.ResolveAsync(solution, symbol);
        if (resolution.Symbol is not null)
            return (resolution.Symbol, null);
        if (resolution.Candidates.Count == 0)
        {
            if (await ResolveExternalAsync(solution, symbol, symbolStore) is { } external)
                return (external, null);
            if (await ResolveEntryPointAsync(solution, symbol) is { } entryPoint)
                return (entryPoint, null);
            return (null, Formats.ToJson(new { error = "symbol_not_found", symbol }));
        }
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

/// <summary>
    /// Resolves a name/id that only matches a previously-discovered external symbol row (BCL/NuGet code
    /// this repo's own source calls, implements, or extends) into a live <c>ISymbol</c> via its stored
    /// documentation-comment id, so it flows through the same BuildContent/DeclarationSites/VersionOf
    /// path a source symbol does — those already tolerate a symbol with no source locations. Only ever
    /// finds symbols SymbolIndexBuilder's edge walk already surfaced; not a general external-library
    /// browser.
    /// </summary>
    private static async Task<ISymbol?> ResolveExternalAsync(Solution solution, string symbol, SymbolStore symbolStore)
    {
        var docId = symbolStore.ExternalDocumentationId(symbol);
        if (docId is null)
            return null;
        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync();
            if (compilation is null)
                continue;
            var matches = DocumentationCommentId.GetSymbolsForDeclarationId(docId, compilation);
            if (matches.Length > 0)
                return matches[0];
        }
        return null;
    }

    /// <summary>
    /// Resolves a handle to a project's synthesized top-level-statements entry point when no other path
    /// finds it. The entry point has no ordinary declaration syntax for SymbolFinder-based resolution to
    /// find (SymbolResolver.ResolveAsync's FindSourceDeclarationsAsync misses it, since it has no
    /// ClassDeclarationSyntax/MethodDeclarationSyntax to walk to), so it may reach here as the raw
    /// <c>sym_…</c> id, as SymbolIndexBuilder's indexed row text ("{ContainingType}.Main"), or as a bare
    /// "Program"/"Main"-shaped guess a caller typed without having seen either — matched case-insensitively
    /// against all of those forms of the live entry-point symbol rather than assuming which one arrived.
    /// </summary>
    private static async Task<ISymbol?> ResolveEntryPointAsync(Solution solution, string handle)
    {
        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync();
            var entryPoint = compilation?.GetEntryPoint(CancellationToken.None);
            if (entryPoint is null)
                continue;
            var typeName = entryPoint.ContainingType?.Name;
            if (SymbolKey.IdOf(entryPoint) == handle
                || entryPoint.ToDisplayString() == handle
                || entryPoint.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == handle
                || (typeName is { Length: > 0 } && (
                    string.Equals(typeName, handle, StringComparison.OrdinalIgnoreCase)
                    || string.Equals($"{typeName}.Main", handle, StringComparison.OrdinalIgnoreCase))))
                return entryPoint;
        }
        return null;
    }

[McpServerTool(Name = "get_references")]
    [Description("Callers, implementations or overrides of a C# symbol, from the compiler's own model. "
        + "USE THIS INSTEAD OF GREP — grep gives wrong caller lists: it cannot see interface, virtual or delegate "
        + "dispatch, counts comment and string matches as hits, and silently drops sites when output is truncated. "
        + "Returns every real call site, no false positives, and reports how many text-only matches it excluded "
        + "as excludedTextMatches (callers direction only). "
        + "dispatchKind is reported once at the top level, not per item — it describes the TARGET symbol's own "
        + "dispatch (direct/virtual/interface/delegate), which is identical for every item in one call by "
        + "construction. Each item carries symbolId, displayString (a compact name/arity form, e.g. "
        + "\"ContextTools.GetSymbol/13\" — pass fields:\"signature\" for the full parameter list instead), and "
        + "sites — a list of {file, line, snippet}, one entry per call site for that symbol. isTest is present "
        + "only when true; content (the inline body) only with includeBodies:true. targetSymbolId confirms which "
        + "overload this answered for; omitted when the caller already passed a sym_... id, since it would only "
        + "restate the input. truncated and excludedTextMatches are present only when they apply.")]
    public static async Task<string> GetReferences(
        WorkspaceHost workspace,
        SolutionLocator locator,
        SymbolStore symbolStore,
        TelemetryRecorder telemetry,
        [Description("Fully-qualified name, unique suffix, or a sym_... id from a previous response.")] string symbol,
        [Description("callers | implementations | overrides (default callers). An unrecognized value falls back to callers rather than erroring.")] string direction = "callers",
        [Description("Include member bodies inline (default false).")] bool includeBodies = false,
        [Description("Comma list of extra per-item fields: contentVersion (this item's own hash, independent "
            + "of the target symbol's — useful only for a caller manually diffing this item against a later "
            + "fetch; almost never used in practice, so it costs real tokens for almost no callers), signature "
            + "(the full parameter-list displayString instead of the default compact name/arity form). Omit for "
            + "the cheaper defaults.")] string? fields = null,
        [Description(ToolTelemetry.TaskIdParam)] string? taskId = null)
    {
        var sessionId = Ids.AmbientSession;
        var attributedTask = Ids.TaskId(taskId);
        var toolCallId = Ids.ToolCall();
        var refLimitedBy = workspace.IsDegraded ? "degraded" : null;
        var wantContentVersion = false;
        var wantSignature = false;
        if (!string.IsNullOrWhiteSpace(fields))
        {
            foreach (var f in fields.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                switch (f.ToLowerInvariant())
                {
                    case "contentversion": wantContentVersion = true; break;
                    case "signature": wantSignature = true; break;
                }
            }
        }
        var solution = await workspace.GetSolutionAsync();
        if (solution is null)
        {
            var loading = Error(toolCallId, "workspace_loading");
            return Record(telemetry, toolCallId, sessionId, attributedTask, "get_references", symbol, null, null,
                null, 0, "index_only", "workspace_loading", loading, direction);
        }

        var (sym, error) = await ResolveAsync(solution, ResolveHandle(symbol, symbolStore), toolCallId, symbolStore);
        if (sym is null)
            return Record(telemetry, toolCallId, sessionId, attributedTask, "get_references", symbol, null, null,
                null, 0, "live", "unresolved", error!, direction);

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

        // dispatchKind describes the TARGET symbol (direct/virtual/interface/delegate), computed once here
        // rather than per item — Callers/ToItem already stamp the identical value onto every item, so
        // reporting it per item was pure repetition, never a signal that could vary within one call.
        var dispatchKind = normalized == "callers" ? DispatchKindOf(sym) : null;

        var envelope = new
        {
            targetSymbolId = symbol.StartsWith("sym_", StringComparison.Ordinal) ? null : SymbolKey.IdOf(sym),
            items = shown.Select(i => new
            {
                symbolId = i.SymbolId,
                contentVersion = wantContentVersion ? i.Version : null,
                displayString = wantSignature ? i.DisplayString : i.CompactDisplayString,
                sites = i.Sites.Select(s => new { file = s.File, line = s.Line, snippet = s.Snippet }),
                isTest = i.IsTest ? true : (bool?)null,
                content = i.Body,
            }),
            dispatchKind,
            totalItems = ordered.Count,
            truncated = truncated ? true : (bool?)null,
            excludedTextMatches = excludedComments > 0 ? excludedComments : (int?)null,
            limitedBy = refLimitedBy,
        };

        var json = Formats.Render(envelope);
        return Record(telemetry, toolCallId, sessionId, attributedTask, "get_references", symbol, SymbolKey.IdOf(sym), null,
            null, shown.Count, refLimitedBy, null, json, normalized);
    }


[McpServerTool(Name = "search_index")]
    [Description("Find C# symbols by name when you don't know the exact name — search, find, locate, "
        + "look up, where is, which class/interface/method/property/field/record/enum. "
        + "USE THIS INSTEAD OF GREP/GLOB over .cs files: it returns ranked symbols with ids and "
        + "locations, not raw text lines, so there is nothing to hand-filter and no truncation to "
        + "silently lose hits. "
        + "PUT EVERY TERM YOU ARE LOOKING FOR IN ONE CALL: terms are OR-ed and ranked, so "
        + "query:\"fee ledger TryBuy TrySell\" answers for all four in one response. One call per word "
        + "costs several times the tokens for a worse-ranked result. Partial and camel-case-interior "
        + "terms match: \"Ledger\" finds FIFOLedger. "
        + "Follow up with get_symbol for the content itself. A hit's line/endLine mark the signature "
        + "line only, EXCLUDING any leading /// doc comment — anchor a validate_patch edit on "
        + "get_symbol's declarationSites span, not this one. "
        + "Filters: kinds, modifiers, implements, xmlDoc, pathPrefix, summary, groupBy, origin. "
        + "Full grammar, worked examples and response shape: docs/tools/search_index.md.")]

    public static async Task<string> SearchIndex(
        SymbolStore symbolStore,
        ProjectIndex index,
        WorkspaceHost workspace,
        TelemetryRecorder telemetry,
        [Description("Free-text query over symbol names.")] string query,
        [Description("Optional kind filter, space/comma-separated: class (alias for type), interface, "
            + "struct, record, enum, delegate, method, property, field, event. Bare tokens restrict to "
            + "those kinds (OR); '-' tokens exclude instead. Mixing both forms lets the bare tokens win. "
            + "An unrecognized value matches nothing rather than erroring. Omit to search every kind.")] string? kinds = null,
        [Description("Optional modifier filter, space/comma-separated: the literal C# keywords (public, "
            + "private, protected, internal, static, const, readonly, volatile, virtual, abstract, sealed, "
            + "override, async, extern, partial) plus derived tags extension, indexer, initonly, "
            + "disposable, asyncdisposable. UNLIKE kinds, bare tokens are AND-ed (a symbol has several "
            + "modifiers at once), and '-' tokens exclude and COMBINE with them: \"public -sealed\" is "
            + "public AND NOT sealed. See docs/tools/search_index.md. Omit for no modifier filtering.")] string? modifiers = null,
        [Description("Optional interface name to filter to its DIRECT implementers only. Narrows the "
            + "ranked query hits the same way pathPrefix does, so query still needs a real search term. "
            + "An unresolvable name yields an empty result rather than an error.")] string? implements = null,
        [Description("Optional filter on which XML doc sections a hit has beyond plain <summary> (use the "
            + "summary parameter for that). Tokens: summary, returns, remarks, value, inheritdoc, params, "
            + "typeparams, exceptions. Same AND/exclude grammar as modifiers. Narrows the ranked query "
            + "hits, so query still needs a real search term. Omit for no doc-section filtering.")] string? xmlDoc = null,
        [Description("Max results (default 10, cap 50).")] int limit = 10,
        [Description("Optional path prefix narrowing results to a folder or file, e.g. \"src/Tools\" "
            + "(repo-root-relative, forward slashes, matched on a full path-segment boundary). Ranking runs "
            + "over the whole index before scoping, so a query with far more hits outside the prefix can "
            + "return fewer than limit — narrow the query text itself if that happens. See "
            + "docs/tools/search_index.md. Omit to search the whole index.")] string? pathPrefix = null,
        [Description("Include XML doc <summary> info per hit without a follow-up get_symbol call. \"has\": "
            + "adds hasSummary (bool). \"full\": adds summary (text, capped at 160 chars — get_symbol's "
            + "xmlDoc.summary for the untruncated version). An unrecognized value is treated as omitted.")] string? summary = null,
        [Description("How to group results: \"namespace\" nests namespace -> file -> symbols; "
            + "\"file\" nests file -> namespace -> symbols; \"none\" returns the flat items[] list from before "
            + "grouping existed, with file/name repeated per row and no namespace field. Omit this parameter "
            + "entirely (rather than passing \"namespace\" explicitly) to let the server render both the flat "
            + "and namespace-grouped shapes from the same data and keep whichever actually costs fewer tokens — "
            + "grouping only pays for itself when hits concentrate onto few namespaces/files; scattered results "
            + "make the nesting overhead a net loss. An explicit value is always honored as given (no "
            + "comparison). Whichever axis the whole result set collapses to a single value on additionally "
            + "collapses its wrapper array to a flat namespace/file header field instead of a nested array, and "
            + "a leaf's kind column is dropped whenever every hit in that leaf shares one kind. An unrecognized "
            + "non-null value is treated as \"namespace\".")] string? groupBy = null,

        [Description("\"source\" (default) searches only symbols this repo's own solution declares. "
            + "\"external\" searches only BCL/NuGet symbols already discovered as a call/construction/"
            + "implements target from this repo's source — not a general library browser, only what this "
            + "repo's own code already references. \"all\" searches both. An unrecognized value is treated as "
            + "\"source\".")] string? origin = null,
        [Description(ToolTelemetry.TaskIdParam)] string? taskId = null)
    {
        var sessionId = Ids.AmbientSession;
        var attributedTask = Ids.TaskId(taskId);
        var toolCallId = Ids.ToolCall();
        limit = Math.Clamp(limit, 1, ReferenceCap);
        var originFilter = origin is "source" or "external" or "all" ? origin : "source";
        var (includeKindTokens, excludeKindTokens) = ParseKindFilter(kinds);
        var includeKinds = NormalizeKinds(includeKindTokens);
        var excludeKinds = includeKindTokens.Length == 0 ? NormalizeKinds(excludeKindTokens) : null;

        // Reuses the same bare/'-'-prefixed token grammar as kinds, but include and exclude are both
        // honored together here (not one replacing the other) — see the modifiers parameter description.
        var (includeModTokens, excludeModTokens) = ParseKindFilter(modifiers);
        var includeMods = includeModTokens.Length == 0 ? null : includeModTokens.Select(t => t.ToLowerInvariant()).ToArray();
        var excludeMods = excludeModTokens.Length == 0 ? null : excludeModTokens.Select(t => t.ToLowerInvariant()).ToArray();

        var (includeDocTokens, excludeDocTokens) = ParseKindFilter(xmlDoc);
        var includeDocs = includeDocTokens.Length == 0 ? null : includeDocTokens.Select(t => t.ToLowerInvariant()).ToArray();
        var excludeDocs = excludeDocTokens.Length == 0 ? null : excludeDocTokens.Select(t => t.ToLowerInvariant()).ToArray();

        HashSet<string>? implementorIds = null;
        if (!string.IsNullOrWhiteSpace(implements))
        {
            var interfaceHit = symbolStore.Search(implements, ["Interface"], null, 1, origin: "all").FirstOrDefault();
            implementorIds = interfaceHit is null
                ? new HashSet<string>(StringComparer.Ordinal)
                : new HashSet<string>(symbolStore.ImplementorsOf(interfaceHit.SymbolId), StringComparer.Ordinal);
        }

        var scope = string.IsNullOrWhiteSpace(pathPrefix) ? null : NormalizePathPrefix(pathPrefix);
        var fetchLimit = scope is null ? limit : ScopedOverfetchCap;
        var summaryMode = summary is "has" or "full" ? summary : null;

        var hits = symbolStore.Search(query, includeKinds, excludeKinds, fetchLimit, includeMods, excludeMods, originFilter);
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
        if (implementorIds is not null)
            resolved = resolved.Where(r => implementorIds.Contains(r.Hit.SymbolId));
        if (includeDocs is not null || excludeDocs is not null)
            resolved = resolved.Where(r => MatchesXmlDocFilter(r.Site?.DocSections, includeDocs, excludeDocs));
        var limited = resolved.Take(limit).ToList();

        object BuildFlatEnvelope() => new
        {
            limitedBy = searchLimitedBy,
            items = limited.Select(r => new
            {
                symbolId = r.Hit.SymbolId,
                name = SymbolResolver.CompactName(r.Hit.FqName),
                kind = r.Hit.Kind,
                file = r.Site?.File,
                line = r.Site?.Line,
                endLine = r.Site?.EndLine,
                hasSummary = summaryMode == "has" ? (bool?)!string.IsNullOrWhiteSpace(r.Site?.Doc) : null,
                summary = summaryMode == "full" && r.Site?.Doc is { } doc ? CompactFormatter.Truncate(doc, SummaryCap) : null,
            }),
        };

        object BuildGroupedEnvelope(bool primaryIsNamespace)
        {
            var rows = limited.Select(r =>
            {
                var ns = r.Site?.Namespace ?? r.Hit.Namespace ?? "(unresolved)";
                var file = r.Site?.File ?? "(unresolved)";
                var compact = SymbolResolver.CompactName(r.Hit.FqName);
                var leafName = ns.Length > 0 && compact.StartsWith(ns + ".", StringComparison.Ordinal)
                    ? compact[(ns.Length + 1)..]
                    : compact;
                return new SymbolGrouping.Row(
                    r.Hit.SymbolId, r.Hit.Kind, leafName, file, ns, r.Site?.Line, r.Site?.EndLine,
                    summaryMode == "has" ? (bool?)!string.IsNullOrWhiteSpace(r.Site?.Doc) : null,
                    summaryMode == "full" && r.Site?.Doc is { } doc ? CompactFormatter.Truncate(doc, SummaryCap) : null);
            }).ToList();
            var grouped = SymbolGrouping.Build(rows, primaryIsNamespace);
            var withLimit = new Dictionary<string, object?>();
            if (searchLimitedBy is not null)
                withLimit["limitedBy"] = searchLimitedBy;
            foreach (var (key, value) in grouped)
                withLimit[key] = value;
            return withLimit;
        }

        string json;
        if (groupBy is null)
        {
            // No explicit request: render both the flat list and the default namespace grouping from the
            // same data and keep whichever actually costs fewer tokens. Grouping only pays for itself when
            // hits concentrate onto few namespaces/files; on scattered results the nesting overhead is a
            // net loss (measured +10% on a 10-hit/4-namespace query).
            var flatJson = Formats.Render(BuildFlatEnvelope());
            var groupedJson = Formats.Render(BuildGroupedEnvelope(primaryIsNamespace: true));
            json = TelemetryRecorder.EstimateTokens(groupedJson) <= TelemetryRecorder.EstimateTokens(flatJson) ? groupedJson : flatJson;
        }
        else if (groupBy == "none")
        {
            json = Formats.Render(BuildFlatEnvelope());
        }
        else
        {
            json = Formats.Render(BuildGroupedEnvelope(primaryIsNamespace: groupBy != "file"));
        }

        return Record(telemetry, toolCallId, sessionId, attributedTask, "search_index", query, null, null,
            null, limited.Count, searchLimitedBy, null, json);
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

private static async Task<object> BuildContent(
        ISymbol sym, SymbolComponents components, Solution solution, SolutionLocator locator,
        SymbolStore symbolStore, SymbolIndexBuilder indexBuilder, FeatureLogStore featureLog)
    {
        // source already prints the declaration's own signature line as text, so anything that would
        // just restate what that line already says gets suppressed rather than duplicated: displayString,
        // modifiers (accessibility included), xmlDoc, attributes, baseType, interfaces.
        var hasSource = components.Has(SymbolComponents.Source);

        // ...except when source was narrowed to specific lines, which usually cut the signature line out
        // of the response entirely. Restating it then is not duplication, it is the only thing saying
        // what member the fragment belongs to, so displayString/modifiers come back.
        var restatesSignature = hasSource && !components.HasSlicedSource;

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

        // baseType/interfaces are type-only, same as members — null for anything else rather than an
        // empty array, so a member's response carries no trace of a component that cannot apply to it.
        // Direct only (BaseType/Interfaces, not AllInterfaces): a one-hop pointer, not a hierarchy walk —
        // get_type_hierarchy already owns the transitive chain.
        var namedType = sym as INamedTypeSymbol;
        var baseType = !hasSource && components.Has(SymbolComponents.BaseType) && namedType?.BaseType is { } bt
            ? TypeRef(bt)
            : null;
        var interfaces = !hasSource && components.Has(SymbolComponents.Interfaces) && namedType is not null
            ? namedType.Interfaces.Select(TypeRef).ToArray()
            : null;

        // The whole declaration first, then the caller's line selection over it: the unsliced list is
        // what the reported span is measured against, so both come from one render rather than two.
        var declarationSource = hasSource ? SourceOf(sym, components.SourceQuery) : null;
        var source = declarationSource is null ? null : SelectLines(declarationSource, components.SourceQuery);

        var outline = components.Has(SymbolComponents.BodyOutline) ? BodyOutlineFor(sym) : null;

        // attachedContracts (P4) is deliberately absent rather than emitted as null/empty — an
        // unpopulated field is pure overhead until it carries data.
        return new
        {
            kind = SymbolKey.KindOf(sym),
            displayString = restatesSignature ? null : sym.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            // "external": no location in this repo's own solution — a BCL/NuGet symbol resolved via its
            // stored documentation-comment id (see ResolveExternalAsync), never a declaration this repo
            // walked. Unconditional: cheap, and callers need it to know why declarationSites/source/xmlDoc
            // came back empty rather than assuming a lookup bug.
            origin = sym.Locations.Any(l => l.IsInSource) ? "source" : "external",
            containingType = ContainingType(sym),
            declarationSites = DeclarationSites(sym, locator),
            source,
            // Emitted only for a slice, because only a slice can mislead: contentVersion is fingerprinted
            // over the whole symbol, so a caller leasing off a fragment would otherwise hold a token for
            // content it never saw. "kept/whole", or "none/whole" when the ranges missed the declaration
            // entirely — which also states the range that would have worked.
            sourceLines = components.HasSlicedSource && declarationSource is { Count: > 0 }
                ? $"{LineSpan(source)}/{declarationSource[0].Line}-{declarationSource[^1].Line}"
                : null,
            xmlDoc = !hasSource && components.Has(SymbolComponents.XmlDoc)
                ? OutlineBuilder.SectionsFromXml(sym.GetDocumentationCommentXml())
                : null,
            // Body-derived facts, served only while the body hash they were computed from still holds. Not
            // source-suppressed: this is server-computed analysis, not something visible by reading source.
            mechanicalFacts = components.Has(SymbolComponents.MechanicalFacts)
                ? MechanicalFactsFor(sym, symbolStore)
                : null,
            // Control-flow landmarks, computed purely from syntax like source rather than the semantic
            // model mechanicalFacts needs — absent (with an explanatory bodyOutlineNote) for a symbol with
            // no executable body of its own (a type, a field, an auto-property), or fully absent (both
            // fields) when there's no declaration to walk at all. bodyOutlineNote is otherwise an
            // advisory, not an error, for a declaration short enough that source:code would likely cost
            // fewer tokens than this.
            bodyOutline = outline?.Rows?.Select(r => (object)new
            {
                text = r.Text,
                startLine = r.StartLine,
                endLine = r.EndLine,
                depth = r.Depth,
            }).ToArray(),
            bodyOutlineNote = outline?.Note,
            referenceCounts = counts,
            members,
            attributes = !hasSource && components.Has(SymbolComponents.Attributes) ? AttributesOf(sym) : null,
            // Unconditional like displayString, not an opt-in include component: the literal modifier
            // phrase already subsumes accessibility ("public sealed" states both), so there is no separate
            // accessibility field at all. Suppressed when source is also requested, since source's own
            // signature line already states the modifiers as text — unless that line was sliced away.
            modifiers = restatesSignature ? null : DotnetToolkit.McpServer.Fingerprint.ModifierText.Render(sym),
            baseType,
            interfaces,
            usings = components.Has(SymbolComponents.Usings) ? UsingsOf(sym) : null,
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

    /// <summary>The same navigation-pointer shape as <see cref="ContainingType"/>, for baseType/interfaces.</summary>
    private static object TypeRef(ITypeSymbol type) => new
    {
        symbolId = SymbolKey.IdOf(type),
        displayString = type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
    };

    /// <summary>Flat file/startLine/endLine sites for a symbol's declarations, one per partial part.</summary>
    /// <param name="sym">The symbol whose declaration spans are wanted.</param>
    /// <param name="locator">Renders each site's path repo-relative.</param>
    /// <returns>One entry per declaring syntax reference, source-generator output excluded.</returns>
    /// <remarks>
    /// Internal rather than private because <c>validate_patch</c> returns the same shape for the symbols a
    /// patch changed. Both paths must produce byte-identical spans -- a caller is meant to feed either one
    /// straight back into an edit -- so they share this method rather than each computing bounds.
    /// </remarks>
    internal static object[] DeclarationSites(ISymbol sym, SolutionLocator locator) =>
        sym.DeclaringSyntaxReferences
            // Exclude source-generator output (obj/**): it is regenerated on every build and not an
            // editable declaration site, so surfacing it alongside the hand-written partial only offers
            // a validate_patch caller a span that will be overwritten out from under it.
            .Where(r => !SolutionLocator.IsGeneratedOrBuildPath(locator.RelPath(r.SyntaxTree.FilePath)))
            .Select(r =>
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


    /// <summary>One line of a symbol's <c>source</c> component — 1-based absolute file line plus its
    /// text, so a multi-line declaration renders as a real per-line table (TOON/JSON alike) instead of
    /// one string carrying literal \n/\" escapes, and each line's number is directly usable as a
    /// validate_patch startLine/endLine without a separate get_symbol round trip.</summary>
    private sealed record SourceLine(int Line, string Text);

    private static IReadOnlyList<SourceLine> SplitLines(string text, int startLine) =>
        text.Replace("\r\n", "\n").Split('\n')
            .Select((line, i) => new SourceLine(startLine + i, line))
            .ToArray();

    private static IReadOnlyList<SourceLine>? SourceOf(ISymbol sym, SourceQuery? query = null)
    {
        var reference = sym.DeclaringSyntaxReferences.FirstOrDefault();
        if (reference is null)
            return null;
        var node = NormalizeDeclNode(reference.GetSyntax());
        return SourceLinesOf(node, query ?? SourceQuery.Full);
    }

    /// <summary>
    /// Control-flow landmarks for one member's body (spec §9 <c>bodyOutline</c>), plus an advisory note
    /// when the declaration is short enough that <c>source:code</c> is likely cheaper than the outline.
    /// Rows is null (bodyOutline stays absent) for a symbol with no executable body of its own (a type,
    /// a field, an auto-property) or with no syntax reference at all (external/no declaration to walk);
    /// Note still explains the former case rather than leaving the caller to guess why both fields are
    /// missing. Rows is an empty (not null) list when the symbol IS a method but its body simply has no
    /// control-flow landmarks to report — bodyOutline still renders as [] there, same as before.
    /// </summary>
    private static (IReadOnlyList<OutlineRow>? Rows, string? Note)? BodyOutlineFor(ISymbol sym)
    {
        const int minWorthwhileLines = 40;
        if (sym is not IMethodSymbol)
            return (null, $"bodyOutline is not applicable to a {SymbolKey.KindOf(sym)} symbol - only a method has an executable body to outline");
        var reference = sym.DeclaringSyntaxReferences.FirstOrDefault();
        if (reference is null)
            return null;

        var node = NormalizeDeclNode(reference.GetSyntax());
        var rows = BodyOutlineExtractor.Extract(node);
        // Doc-comment-inclusive bounds, matching declarationSites/SourceLinesOf's SourceMode.Full span —
        // node.Span alone excludes leading trivia and would undercount a well-documented short method as
        // shorter than the source fetch its own declarationSites promises.
        var (start, end) = DeclarationBoundsIncludingDocComment(node);
        var lineSpan = node.SyntaxTree!.GetLineSpan(TextSpan.FromBounds(start, end));
        var lineCount = lineSpan.EndLinePosition.Line - lineSpan.StartLinePosition.Line + 1;
        var note = lineCount < minWorthwhileLines
            ? $"declaration is {lineCount} lines (<{minWorthwhileLines}) - source:code is likely cheaper than this outline"
            : null;
        return (rows, note);
    }

    /// <summary>
    /// Slices <paramref name="node"/>'s own source text for the <c>source</c> component per
    /// <paramref name="query"/> — <see cref="SourceMode.Full"/> widens the start to include a leading doc
    /// comment (see <see cref="DeclarationBoundsIncludingDocComment"/>); <see cref="SourceMode.Code"/> uses
    /// the node's own <see cref="SyntaxNode.Span"/>, which already excludes all leading trivia including
    /// that comment. <see cref="ExcludedLines"/> then drops whatever <paramref name="query"/> subtracts on
    /// top of that: every nested doc comment under <see cref="SourceMode.Code"/>, specific doc-comment tags
    /// under <see cref="SourceMode.Full"/>, and attributes/<c>//</c> comments under either, whenever they
    /// occupy a whole standalone line.
    /// </summary>
    private static IReadOnlyList<SourceLine> SourceLinesOf(SyntaxNode node, SourceQuery query)
    {
        var (start, end) = query.Mode == SourceMode.Code
            ? (node.SpanStart, node.Span.End)
            : DeclarationBoundsIncludingDocComment(node);
        var tree = node.SyntaxTree!;
        var text = tree.GetText();
        var span = TextSpan.FromBounds(start, end);
        var startLine = tree.GetLineSpan(span).StartLinePosition.Line + 1;
        var lines = SplitLines(text.ToString(span), startLine);

        var excluded = ExcludedLines(node, text, query);
        return excluded.Count == 0 ? lines : lines.Where(l => !excluded.Contains(l.Line)).ToArray();
    }

    /// <summary>
    /// Narrows already-rendered source lines to the query's <c>@</c> ranges, or returns them untouched
    /// when the query selected none.
    /// </summary>
    /// <remarks>
    /// Deliberately a filter over the absolute line numbers <see cref="SourceLinesOf"/> already assigned,
    /// never a re-slice of the text: that is what lets a range and a <c>-modifier</c> exclusion compose
    /// in either order, and what keeps every surviving line's number directly usable as a
    /// validate_patch startLine/endLine. A range reaching past the declaration therefore clamps to it on
    /// its own, and one entirely outside it yields no lines rather than an error — the <c>sourceLines</c>
    /// span reported alongside says which happened.
    /// </remarks>
    /// <param name="lines">The declaration's lines, already stripped per the query's modifiers.</param>
    /// <param name="query">The resolved source query whose <see cref="SourceQuery.Lines"/> to apply.</param>
    /// <returns>The lines falling inside at least one requested range, in their original order.</returns>
    private static IReadOnlyList<SourceLine> SelectLines(IReadOnlyList<SourceLine> lines, SourceQuery query) =>
        query.Lines.Count == 0
            ? lines
            : lines.Where(l => query.Lines.Any(r => r.Contains(l.Line))).ToArray();

    /// <summary>Renders a line list's own extent as <c>"first-last"</c>, or <c>"none"</c> when empty.</summary>
    /// <param name="lines">The lines to describe.</param>
    /// <returns>A compact span string for the <c>sourceLines</c> field.</returns>
    private static string LineSpan(IReadOnlyList<SourceLine>? lines) =>
        lines is { Count: > 0 } ? $"{lines[0].Line}-{lines[^1].Line}" : "none";

    /// <summary>
    /// Absolute 1-based file lines to drop from <paramref name="node"/>'s rendered source per
    /// <paramref name="query"/>: the whole doc comment under <see cref="SourceMode.Code"/> (a member's own
    /// <c>///</c> block, not just the requested symbol's leading one — already excluded via its span
    /// start), specific tags under <see cref="SourceMode.Full"/>, and attributes/<c>//</c> comments under
    /// either — always whole standalone lines (<see cref="IsWholeLine"/>), never a line shared with code.
    /// </summary>
    private static HashSet<int> ExcludedLines(SyntaxNode node, SourceText text, SourceQuery query)
    {
        var lines = new HashSet<int>();

        foreach (var trivia in node.DescendantTrivia())
        {
            if (trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) ||
                trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
            {
                if (query.Mode == SourceMode.Code)
                    AddSpan(lines, trivia.Span, text);
                else if (query.ExcludedTags.Count > 0 && trivia.GetStructure() is DocumentationCommentTriviaSyntax doc)
                    foreach (var xmlNode in doc.Content)
                        if (TagNameOf(xmlNode) is { } tagName && query.ExcludedTags.Contains(tagName))
                            AddSpan(lines, xmlNode.Span, text);
                continue;
            }

            if (query.ExcludeComments &&
                (trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia)) &&
                IsWholeLine(trivia.Span, text))
            {
                AddSpan(lines, trivia.Span, text);
            }
        }

        if (query.ExcludeAttributes)
            foreach (var attributeList in node.DescendantNodes().OfType<AttributeListSyntax>())
                if (IsWholeLine(attributeList.Span, text))
                    AddSpan(lines, attributeList.Span, text);

        return lines;
    }

    /// <summary>The doc-comment element's own XML local tag name (e.g. <c>"remarks"</c>, <c>"exception"</c>).</summary>
    private static string? TagNameOf(XmlNodeSyntax xmlNode) => xmlNode switch
    {
        XmlElementSyntax element => element.StartTag.Name.LocalName.Text,
        XmlEmptyElementSyntax empty => empty.Name.LocalName.Text,
        _ => null,
    };

    /// <summary>
    /// True when nothing but whitespace shares <paramref name="span"/>'s first and last line with it — the
    /// bar for a whole-line removal. An attribute or comment inline with real code (e.g.
    /// <c>[Fact] public void Foo()</c>, or a trailing <c>// comment</c>) is left untouched rather than
    /// partially rewriting that line's text.
    /// </summary>
    private static bool IsWholeLine(TextSpan span, SourceText text)
    {
        var startLine = text.Lines.GetLineFromPosition(span.Start);
        if (!string.IsNullOrWhiteSpace(text.ToString(TextSpan.FromBounds(startLine.Start, span.Start))))
            return false;

        var endLine = text.Lines.GetLineFromPosition(Math.Max(span.Start, span.End - 1));
        return string.IsNullOrWhiteSpace(text.ToString(TextSpan.FromBounds(span.End, endLine.End)));
    }

    /// <summary>
    /// Adds every absolute 1-based line <paramref name="span"/> touches to <paramref name="lines"/>. Uses
    /// <c>Span.End - 1</c>, not the raw end position, since a trivia span's end often lands at column 0 of
    /// the FOLLOWING line (its trailing newline is part of its own span) — the raw end would then wrongly
    /// mark that next line (real code) as covered too.
    /// </summary>
    private static void AddSpan(HashSet<int> lines, TextSpan span, SourceText text)
    {
        if (span.IsEmpty)
            return;
        var startLine = text.Lines.GetLineFromPosition(span.Start).LineNumber;
        var endLine = text.Lines.GetLineFromPosition(Math.Max(span.Start, span.End - 1)).LineNumber;
        for (var line = startLine; line <= endLine; line++)
            lines.Add(line + 1);
    }

    /// <summary>
    /// This symbol's file-level <c>using</c> directives: the compilation unit's own, plus any declared
    /// inside an enclosing classic (block-scoped) namespace. A file-scoped namespace cannot carry its
    /// own usings — C# requires them before the namespace declaration — so the compilation-unit pass
    /// already covers that case. Order follows source position.
    /// </summary>
    private static string[]? UsingsOf(ISymbol sym)
    {
        var reference = sym.DeclaringSyntaxReferences.FirstOrDefault();
        if (reference is null)
            return null;
        var node = NormalizeDeclNode(reference.GetSyntax());
        var usings = new List<string>();
        if (node.SyntaxTree.GetRoot() is CompilationUnitSyntax root)
            usings.AddRange(root.Usings.Select(u => u.ToString().Trim()));
        for (var ancestor = node.Parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (ancestor is BaseNamespaceDeclarationSyntax ns)
                usings.AddRange(ns.Usings.Select(u => u.ToString().Trim()));
        }
        return usings.Count > 0 ? [.. usings] : null;
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

private sealed record RefItem(string SymbolId, string Version, string DisplayString, string CompactDisplayString,
        IReadOnlyList<(string File, int Line, string Snippet)> Sites, string? DispatchKind, IReadOnlyList<SourceLine>? Body,
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
                CompactDisplay(caller.CallingSymbol),
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
            s.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat), CompactDisplay(s), sites!, dispatch,
            includeBodies ? SourceOf(s) : null);
    }

    // The default get_references/get_call_hierarchy displayString: name + arity (e.g.
    // "ContextTools.GetSymbol/13") rather than the full parameter list with types and default values,
    // which answers "who/what is this" for a fraction of the tokens; the full form is still one
    // fields:"signature" away on either tool.
    private static string CompactDisplay(ISymbol s)
    {
        var name = s.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat
            .WithParameterOptions(SymbolDisplayParameterOptions.None));
        return s is IMethodSymbol method ? $"{name}/{method.Parameters.Length}" : name;
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
    /// Checks a hit's comma-joined <see cref="Indexing.ProjectIndex.DocSite"/>-style DocSections tags
    /// against search_index's xmlDoc include/exclude token arrays — same AND-include/exclude-combine
    /// semantics as the modifiers filter. A null docSections (no doc comment, or none of the recognized
    /// tags) matches only when include is also null.
    /// </summary>
    private static bool MatchesXmlDocFilter(string? docSections, string[]? include, string[]? exclude)
    {
        string[] tags = docSections is null
            ? []
            : docSections.Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (include is not null && !include.All(t => tags.Contains(t, StringComparer.Ordinal)))
            return false;
        if (exclude is not null && exclude.Any(t => tags.Contains(t, StringComparer.Ordinal)))
            return false;
        return true;
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
        IReadOnlyList<SourceLine>? source = null;
        try
        {
            var text = File.ReadAllText(locator.AbsPath(hit.File));
            var root = CSharpSyntaxTree.ParseText(text).GetRoot();
            var node = root.DescendantNodes()
                .Where(IsIndexableDeclaration)
                .FirstOrDefault(n => n.SyntaxTree.GetLineSpan(n.Span).StartLinePosition.Line + 1 == hit.Line);
            if (node is not null)
            {
                var normalized = NormalizeDeclNode(node);
                var (decl, body) = SyntaxFingerprint.Compute(normalized);
                version = ContentVersion.Of(decl, body).ToString();
                if (SymbolComponents.Resolve(include, out _) is { } parts && parts.Has(SymbolComponents.Source))
                    source = SourceLinesOf(normalized, parts.SourceQuery);
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
        return (content, version, Ids.IndexOnlySymbolId(hit.FqName));
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

        // start still lands on the first non-trivia character, not the start of ITS OWN line, so a
        // caller reading the span back line-by-line (SourceOf) would see the first line missing its own
        // leading indentation while every later line keeps its real formatting. Snap back to the line's
        // start whenever everything before `start` on that line is pure whitespace (an indented
        // declaration, the overwhelmingly common case) — never when something else precedes it on the
        // same physical line (e.g. a prior sibling declaration), since that would fold unrelated code
        // into this declaration's own span. GetLineSpan()'s reported line NUMBER is unaffected either
        // way, so DeclarationSites' startLine/endLine (which never look at the column) don't change.
        var text = node.SyntaxTree!.GetText();
        var line = text.Lines.GetLineFromPosition(start);
        if (string.IsNullOrWhiteSpace(text.ToString(TextSpan.FromBounds(line.Start, start))))
            start = line.Start;

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

private static async Task<(ISymbol? Symbol, string? Error)> ResolveAsync(
        Solution solution, string symbol, string toolCallId, SymbolStore symbolStore)
    {
        var resolution = await SymbolResolver.ResolveAsync(solution, symbol);
        if (resolution.Symbol is not null)
            return (resolution.Symbol, null);
        if (resolution.Candidates.Count == 0)
        {
            if (await ResolveExternalAsync(solution, symbol, symbolStore) is { } external)
                return (external, null);
            if (await ResolveEntryPointAsync(solution, symbol) is { } entryPoint)
                return (entryPoint, null);
            return (null, Formats.Render(new { error = "symbol_not_found", symbol }));
        }
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
        string requestedSymbol, string? symbolId, string? resolution, string? contentVersion,
        int returnedSymbols, string? limitedBy, string? errorKind, string result,
        string? direction = null)
    {
        // Kept as a positional forwarder rather than folded into ToolTelemetry.Record directly: this
        // signature has six call sites in this file, all positional, whose argument order would
        // otherwise have to move for no behavioural gain.
        return ToolTelemetry.Record(
            telemetry, toolCallId, sessionId, taskId, tool, requestedSymbol, result,
            symbolId: symbolId,
            resolution: resolution,
            contentVersion: contentVersion,
            returnedSymbols: returnedSymbols,
            limitedBy: limitedBy,
            errorKind: errorKind,
            direction: direction);
    }
}
