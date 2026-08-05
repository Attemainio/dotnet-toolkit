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
    /// <summary>Hard ceiling on one page of get_references items, whatever <c>limit</c> asks for.</summary>
    private const int ReferenceCap = 200;
    private const int ScopedOverfetchCap = 500;
    private const int SummaryCap = 160;

    /// <summary>
    /// The display form behind every default reference and call-hierarchy row: containing type and member
    /// name, with neither the return type nor a parameter list.
    /// </summary>
    /// <remarks>
    /// Emitting the return type put the default within 17% of what <c>fields:"signature"</c> costs while
    /// conveying strictly less, and the empty parens left behind by suppressing only the parameters read
    /// as a zero-argument method when the trailing arity is the real signal.
    /// </remarks>
    private static readonly SymbolDisplayFormat CompactMemberFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeContainingType,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
            | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

[McpServerTool(Name = "get_symbol")]
    [Description("Retrieve one or more C# symbols — look up a class, interface, method, property or field and read its "
        + "signature, XML docs, source text, members, attributes, base type, reference counts and exact "
        + "file location. USE THIS INSTEAD OF READING A .cs FILE — it returns the whole symbol even when "
        + "it is split across partial-class files (Read gives you one fragment and no signal that the "
        + "rest exists), and costs a fraction of the tokens of the file. "
        + "Every response carries declarationSites (file + startLine/endLine, INCLUDING a leading /// doc "
        + "comment when present) and a contentVersion token — these are exactly what validate_patch needs "
        + "as baseVersions and edit spans. search_index's line/endLine EXCLUDE the doc comment, so never "
        + "anchor an edit on a span read off search_index without confirming it here. "
        + "A patch that rewrites a BODY needs a contentVersion from an include that actually served one, "
        + "so fetch with \"all\" (or source/bodyOutline/mechanicalFacts) when about to edit — the default "
        + "include leases the declaration only. "
        + "NEVER build an edit span from a fetch whose -modifiers dropped lines inside it (source:code on "
        + "a type, -comments, -attributes): the patch replaces the span verbatim, so unseen lines are "
        + "deleted. Strip when reading; edit from full source. "
        + "include: \"standard\" (default) | \"all\" | a component list that REPLACES the default — see the "
        + "include parameter. Common calls: include:\"members\" for a type's surface, \"source:code\" to "
        + "READ source without doc comments, \"source:code@120-160\" for one region of a long member (a "
        + "slice still leases the body layer), \"bodyOutline\" to map one before slicing it. Use symbols "
        + "instead of symbol to fetch several at once. "
        + "Full component semantics, the @ and -modifier grammar, the response contract and worked "
        + "examples: docs/tools/get_symbol.md.")]
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
            + "with -tag/-attributes/-comments/-lineNumbers subtracted, and/or an @ line selection returning only those "
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
        if (SharedBatchEnvelope(results) is { } hoisted)
        {
            var hoistedRendered = Formats.Render(hoisted);
            if (hoistedRendered.Length < batchRendered.Length)
                batchRendered = hoistedRendered;
        }

        return Record(telemetry, toolCallId, sessionId, attributedTask, "get_symbol", string.Join(",", targets),
            null, include ?? "standard", null, targets.Length, firstLimitedBy,
            firstErrorKind, batchRendered);
    }

    /// <summary>
    /// The batch envelope with every property the entries repeat verbatim lifted into one <c>shared</c>
    /// block, or null when there is nothing to lift.
    /// </summary>
    /// <remarks>
    /// One include applies to the whole batch, so <c>components</c> is identical per entry by
    /// construction, and entries fetched from one type repeat <c>content.origin</c> and
    /// <c>content.containingType</c> too. That repetition made a three-symbol batch cost 15% MORE than
    /// the same three single calls, inverting the route table's claim that batching is the cheap route.
    /// Lifting reaches one level into <c>content</c> for exactly that reason — an entry-level sweep alone
    /// leaves the two worst repeaters untouched, since they are nested. The caller compares the two
    /// renderings and keeps this one only when it is genuinely smaller.
    /// </remarks>
    /// <param name="results">The per-entry responses, in call order.</param>
    /// <returns>An envelope of <c>shared</c> plus the trimmed entries, or null when nothing is shared.</returns>
    private static object? SharedBatchEnvelope(List<JsonElement> results)
    {
        if (results.Count < 2 || results.Any(r => r.ValueKind != JsonValueKind.Object))
            return null;

        var shared = SharedProperties(results);

        // A content object hoisted whole is already covered; splitting it again would emit it twice.
        var contents = results.Select(r => r.TryGetProperty("content", out var c) ? c : default).ToList();
        var sharedContent = shared.ContainsKey("content") || contents.Any(c => c.ValueKind != JsonValueKind.Object)
            ? []
            : SharedProperties(contents);

        if (shared.Count == 0 && sharedContent.Count == 0)
            return null;

        var trimmed = results.Select(r =>
        {
            var own = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var property in r.EnumerateObject())
            {
                if (shared.ContainsKey(property.Name))
                    continue;

                if (sharedContent.Count > 0 && property.NameEquals("content"))
                {
                    var ownContent = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                    foreach (var field in property.Value.EnumerateObject())
                    {
                        if (!sharedContent.ContainsKey(field.Name))
                            ownContent[field.Name] = field.Value;
                    }

                    if (ownContent.Count > 0)
                        own["content"] = ownContent;
                    continue;
                }

                own[property.Name] = property.Value;
            }

            return own;
        }).ToList();

        var block = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in shared)
            block[key] = value;
        if (sharedContent.Count > 0)
            block["content"] = sharedContent;

        return new { shared = block, results = trimmed };
    }

    /// <summary>Every property these objects all declare with byte-identical value.</summary>
    /// <param name="objects">Objects to compare; must be non-empty and all of kind object.</param>
    /// <returns>The shared properties, in the first object's order; empty when they share none.</returns>
    private static Dictionary<string, JsonElement> SharedProperties(List<JsonElement> objects)
    {
        var shared = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var candidate in objects[0].EnumerateObject())
        {
            var raw = candidate.Value.GetRawText();
            if (objects.All(o => o.TryGetProperty(candidate.Name, out var same) && same.GetRawText() == raw))
                shared[candidate.Name] = candidate.Value;
        }

        return shared;
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
                detail = InvalidComponentDetail(invalidComponent ?? include ?? ""),
            });
            return new SymbolFetchResult(badComponent, symbolId, null, "live", "invalid_component");
        }

        // The token describes only the layers this response's components were derived from, so a caller
        // relying on it to detect drift is never told about a layer it never actually received.
        var version = FullVersionOf(sym, symbolStore).Narrow(parts.RequiredLayers);
        var limitedBy = await LimitedByAsync(workspace, indexBuilder, SourceFilesOf(sym));

        var content = await BuildContent(sym, parts, solution, locator, symbolStore, indexBuilder, featureLog);
        var served = ServedComponents(parts, content);
        var envelope = new
        {
            symbolId,
            contentVersion = version.ToString(),
            limitedBy,
            // Named only when it says something the caller does not already know: a component that was
            // asked for and came back empty. Repeating the request back verbatim is pure restatement.
            components = served.Count == parts.Resolved.Count ? null : served,
            content,
        };

        var json = Formats.ToJson(envelope);
        return new SymbolFetchResult(json, symbolId, version.ToString(), limitedBy, null);
    }

    /// <summary>
    /// Which of the requested components the built content actually carries.
    /// </summary>
    /// <param name="parts">The resolved component selection.</param>
    /// <param name="content">The content object about to be serialized.</param>
    /// <returns>The requested names present in <paramref name="content"/>, in canonical order.</returns>
    /// <remarks>
    /// Every component's name is also its key on the content object, so presence is decided by looking
    /// the name up rather than by a second switch that would drift from <see cref="BuildContent"/>. A
    /// component legitimately returns nothing for the wrong symbol kind - a method has no members and no
    /// base type - and listing it anyway advertised content the response did not contain.
    /// </remarks>
    private static IReadOnlyList<string> ServedComponents(SymbolComponents parts, object content)
    {
        var element = Formats.ToElement(content);
        return [.. parts.Resolved.Where(c => element.TryGetProperty(c, out var value)
            && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))];
    }

    /// <summary>
    /// Explains why one entry of an <c>include</c> list could not be parsed.
    /// </summary>
    /// <param name="component">The offending entry, verbatim.</param>
    /// <returns>A message naming the real mistake when the entry is recognisably a source request.</returns>
    /// <remarks>
    /// A malformed source request is by far the commonest cause, and listing the eleven bare component
    /// names answers a question the caller did not ask: "source:code@70-81-lineNumbers" fails on the
    /// ORDER of its parts, not on the component name, and nothing in the bare list shows that.
    /// </remarks>
    private static string InvalidComponentDetail(string component)
    {
        var separator = component.IndexOfAny([':', '@']);
        var isSourceRequest = separator > 0
            && string.Equals(component[..separator], SymbolComponents.Source, StringComparison.OrdinalIgnoreCase);
        if (!isSourceRequest)
            return $"'{component}' is not a component. Valid: {string.Join(", ", SymbolComponents.All)}.";

        return $"'{component}' is not a usable source request. The form is "
            + "source[:full|:code][-modifier...][@lines]: the -modifiers ("
            + string.Join(", ", SourceQuery.ModifierNames)
            + ") come BEFORE the @ line selection, which runs to the end of the entry, and each range is "
            + "from-to, from- or -to over absolute file lines - e.g. source:code-lineNumbers@70-81;89-109. "
            + "The doc-tag modifiers apply to source:full only, since source:code has already dropped the "
            + "doc comment.";
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
            return (null, Formats.ToJson(new { error = "symbol_not_found", symbol, didYouMean = NearMisses(symbolStore, symbol) }));
        }

        // Every candidate of one ambiguous name shares a prefix by construction -- that shared prefix is
        // what made the name ambiguous in the first place -- so repeating it per row was 29% of a
        // six-candidate payload. This is the same hoist the multi-symbol batch response already takes
        // with its shared: block, finally applied to the shape whose rows share the MOST.
        var named = resolution.Candidates.Take(10)
            .Select(c => (Id: SymbolKey.IdOf(c), Display: SymbolResolver.CompactName(c.ToDisplayString())))
            .ToList();
        var sharedPrefix = named.Count > 1 ? SharedNamePrefix(named.Select(n => n.Display)) : "";
        return (null, Formats.ToJson(new
        {
            error = "ambiguous_symbol",
            sharedPrefix = sharedPrefix.Length > 0 ? sharedPrefix : null,
            candidates = named.Select(n => new
            {
                symbolId = n.Id,
                displayString = n.Display[sharedPrefix.Length..],
            }),
        }));
    }

    /// <summary>
    /// The longest dot-terminated prefix every one of <paramref name="displays"/> begins with.
    /// </summary>
    /// <remarks>
    /// Cut back to the last '.' so what is hoisted is always a whole namespace/type path rather than a
    /// half-identifier: two candidates named <c>Solve</c> and <c>SolveAll</c> share "Solve", which is a
    /// prefix of the text but not of the name.
    /// </remarks>
    /// <param name="displays">The candidate display strings, two or more of them.</param>
    /// <returns>The shared prefix including its trailing '.', or an empty string when there is none.</returns>
    private static string SharedNamePrefix(IEnumerable<string> displays)
    {
        string? common = null;
        foreach (var display in displays)
        {
            if (common is null)
            {
                common = display;
                continue;
            }

            var shared = 0;
            while (shared < common.Length && shared < display.Length && common[shared] == display[shared])
                shared++;
            common = common[..shared];
        }

        var lastDot = common?.LastIndexOf('.') ?? -1;
        return lastDot < 0 ? "" : common![..(lastDot + 1)];
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
    [Description("Callers, implementations or overrides of a C# symbol (who calls it), from the compiler's model. "
        + "USE THIS INSTEAD OF GREP — grep gives wrong caller lists: it cannot see interface, virtual or "
        + "delegate dispatch, counts comment and string matches as hits, and silently drops sites when "
        + "output is truncated. Returns every real call site, no false positives, and reports how many "
        + "text-only matches it excluded as excludedTextMatches (callers direction only). "
        + "A named type (class, record, interface, delegate) has no call sites of its own, so callers on "
        + "one reports the members that REFERENCE it — the field, parameter, return type or construction "
        + "site. Each item carries symbolId, displayString (a compact name/arity form — pass "
        + "fields:\"signature\" for the full "
        + "parameter list instead), and sites, a list of {file, line, snippet} with ONE ROW PER "
        + "{file, line}. XML-doc <see cref=\"...\"/> mentions bind to the symbol but are not code, so "
        + "they are excluded like any other comment match and reported as excludedDocMentions; pass "
        + "fields:\"crefs\" to get them back. isTest is present only when true; content (the inline body) "
        + "only with includeBodies:true. targetSymbolId confirms which overload this answered for, and is "
        + "omitted when the caller already passed a sym_... id. totalItems is always the FULL count "
        + "rather than the page's, so when it exceeds what came back, truncated is set and nextOffset "
        + "names the offset that reaches the rest — a symbol with hundreds of referencing members is "
        + "fully retrievable, one page at a time. truncated, excludedTextMatches and excludedDocMentions "
        + "are present only when they apply. An item's symbolId is a get_symbol target, not an edit "
        + "lease: updating a call site means fetching it with get_symbol first for the contentVersion "
        + "and declarationSites validate_patch needs. Full contract and examples: "
        + "docs/tools/get_references.md.")]
    public static async Task<string> GetReferences(
        WorkspaceHost workspace,
        SolutionLocator locator,
        SymbolStore symbolStore,
        TelemetryRecorder telemetry,
        [Description("Fully-qualified name, unique suffix, or a sym_... id from a previous response.")] string symbol,
        [Description("callers | implementations | overrides (default callers). An unrecognized value falls back to callers rather than erroring.")] string direction = "callers",
        [Description("Max items to return (default 50, cap 200). Lower it when a few worked examples are enough - a high-fan-in symbol's full page is the most expensive response this server produces.")] int limit = 50,
        [Description("Items to skip before limit (default 0). Pass the previous response's nextOffset to reach the references past the page you already have.")] int offset = 0,
        [Description("Include member bodies inline (default false).")] bool includeBodies = false,
        [Description("Comma list of extra per-item fields: contentVersion (this item's own hash, independent "
            + "of the target symbol's — useful only for a caller manually diffing this item against a later "
            + "fetch; almost never used in practice, so it costs real tokens for almost no callers), signature "
            + "(the full parameter-list displayString instead of the default compact name/arity form), crefs "
            + "(also return the XML-doc <see cref=\"...\"/> sites excluded by default, each tagged "
            + "kind:\"cref\"). Omit for the cheaper defaults.")] string? fields = null,
        [Description(ToolTelemetry.TaskIdParam)] string? taskId = null)
    {
        var sessionId = Ids.AmbientSession;
        var attributedTask = Ids.TaskId(taskId);
        var toolCallId = Ids.ToolCall();
        var refLimitedBy = workspace.IsDegraded ? "degraded" : null;
        var wantContentVersion = false;
        var wantSignature = false;
        var wantCrefs = false;
        if (!string.IsNullOrWhiteSpace(fields))
        {
            foreach (var f in fields.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                switch (f.ToLowerInvariant())
                {
                    case "contentversion": wantContentVersion = true; break;
                    case "signature": wantSignature = true; break;
                    case "crefs": wantCrefs = true; break;
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

        // A <see cref="..."/> binds to the symbol, so Roslyn reports doc mentions among the references —
        // but they are the same category as the comment and string matches CountTextOnlyMatches already
        // excludes, and on a heavily documented API they outnumber the real sites. Dropped by default,
        // taking with them any item left with nothing else, and counted so their absence is not silent.
        var docMentions = 0;
        if (!wantCrefs)
        {
            docMentions = items.Sum(i => i.Sites.Count(s => s.IsCref));
            items = [.. items
                .Select(i => i with { Sites = i.Sites.Where(s => !s.IsCref).ToList() })
                .Where(i => i.Sites.Count > 0)];
        }

        var ordered = items.OrderBy(i => i.DisplayString, StringComparer.Ordinal).ToList();

        // Paged rather than hard-capped: at 79 referencing members the old fixed cap returned 50 and left
        // the remaining 29 unreachable by any argument, while a caller who wanted three still paid for 50.
        var pageSize = Math.Clamp(limit, 1, ReferenceCap);
        var skipped = Math.Clamp(offset, 0, ordered.Count);
        var shown = ordered.Skip(skipped).Take(pageSize).ToList();
        var nextOffset = skipped + shown.Count;
        var truncated = nextOffset < ordered.Count;

        var excludedComments = normalized == "callers"
            ? await CountTextOnlyMatches(solution, sym.Name)
            : 0;

        // dispatchKind describes the TARGET symbol (direct/virtual/interface/delegate), computed once here
        // rather than per item — Callers/ToItem already stamp the identical value onto every item, so
        // reporting it per item was pure repetition, never a signal that could vary within one call.
        // A class/record/struct/interface root is excluded: it has no call sites of its own, so this
        // direction reports the members that REFERENCE it and there is no dispatch to describe. Emitting
        // "direct" there stated a fact about nothing, and read as a claim the references are non-virtual.
        // A DELEGATE type is the exception and keeps its kind: "delegate" is a true statement about how
        // the members this direction returns actually invoke it, not a default standing in for silence.
        var dispatchKind = normalized == "callers"
            && sym is not INamedTypeSymbol { TypeKind: not TypeKind.Delegate }
            ? DispatchKindOf(sym)
            : null;

        var envelope = new
        {
            targetSymbolId = symbol.StartsWith("sym_", StringComparison.Ordinal) ? null : SymbolKey.IdOf(sym),
            items = shown.Select(i => new
            {
                symbolId = i.SymbolId,
                contentVersion = wantContentVersion ? i.Version : null,
                displayString = wantSignature ? i.DisplayString : i.CompactDisplayString,
                sites = i.Sites.Select(s => new { file = s.File, line = s.Line, snippet = s.Snippet, kind = s.IsCref ? "cref" : null }),
                isTest = i.IsTest ? true : (bool?)null,
                content = i.Body,
            }),
            dispatchKind,
            totalItems = ordered.Count,
            offset = skipped > 0 ? (int?)skipped : null,
            nextOffset = truncated ? (int?)nextOffset : null,
            truncated = truncated ? true : (bool?)null,
            excludedTextMatches = excludedComments > 0 ? excludedComments : (int?)null,
            excludedDocMentions = docMentions > 0 ? docMentions : (int?)null,
            limitedBy = refLimitedBy,
        };

        var json = Formats.Render(envelope);
        return Record(telemetry, toolCallId, sessionId, attributedTask, "get_references", symbol, SymbolKey.IdOf(sym), null,
            null, shown.Count, refLimitedBy, null, json, normalized);
    }


[McpServerTool(Name = "search_index")]
    [Description("Find C# symbols by name when you don't know the exact name — search, find, locate, "
        + "where is, which class/interface/method/property/field/record/enum. USE THIS INSTEAD OF "
        + "GREP/GLOB over .cs files: it returns ranked symbols with ids and locations, not raw text "
        + "lines, so there is nothing to hand-filter and no truncation to silently lose hits. "
        + "PUT EVERY TERM YOU ARE LOOKING FOR IN ONE CALL: terms are OR-ed and ranked together, so "
        + "query:\"fee ledger TryBuy TrySell\" answers for all four in one round trip rather than four. "
        + "Each term gets a floor share of limit, but that floor is shallow — any term the result never "
        + "covered is named under termsWithNoHits, so raise limit (cap 200) or re-ask that term alone. "
        + "Never read an absent term as an absent symbol. "
        + "camel-case-interior terms match: \"Ledger\" finds FIFOLedger. "
        + "Follow up with get_symbol for the content itself. A hit's line/endLine mark the signature "
        + "line only, EXCLUDING any leading /// doc comment — anchor a validate_patch edit on "
        + "get_symbol's declarationSites span, not this one. "
        + "Each hit's shape column says what fetching it costs, with its legend stated once per "
        + "response: a big L with a big O wants get_symbol include:\"bodyOutline\" then a "
        + "source:code@from-to range rather than a whole fetch, and an edit target wants include:\"all\" "
        + "whatever its shape. "
        + "Filters: kinds, modifiers, implements, xmlDoc, pathPrefix, summary, groupBy, origin. "
        + "Full grammar, the shape legend, worked examples and response shape: "
        + "docs/tools/search_index.md.")]

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
        [Description("Max results (default 10, cap 200).")] int limit = 10,
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
        var sites = index.LocateWithDocs(hits.Select(h => h.FqName).ToHashSet(StringComparer.Ordinal));

        var resolved = hits.Select(h => (Hit: h, Site: sites.GetValueOrDefault(h.FqName)));
        if (scope is not null)
            resolved = resolved.Where(r => WithinPathScope(r.Site?.File, scope));
        if (implementorIds is not null)
            resolved = resolved.Where(r => implementorIds.Contains(r.Hit.SymbolId));
        if (includeDocs is not null || excludeDocs is not null)
            resolved = resolved.Where(r => MatchesXmlDocFilter(r.Site?.DocSections, includeDocs, excludeDocs));
        var limited = resolved.Take(limit).ToList();

        // A ranked OR spends `limit` globally, so a term whose name-matches are far rarer than its
        // neighbours' can be squeezed out of the response altogether - and a caller told that one call
        // answers for every term reads that silence as "no such symbol". Naming the starved terms is what
        // makes the one-call route safe to follow. Skipped only for a single-term query, whose empty
        // result already says the same thing. An EMPTY multi-term result is not skipped: that is the one
        // response carrying no other evidence at all, and "every term" is a materially different answer
        // from "the filters removed what the terms found" - which the caller can then tell apart by
        // seeing every term listed here versus none of them.
        var terms = SearchText.QueryTerms(query);
        var termsWithNoHits = terms.Count > 1
            ? terms.Where(t => !limited.Any(r => r.Hit.FqName.Contains(t, StringComparison.OrdinalIgnoreCase))).ToList()
            : [];

        // Every letter the shape column can carry, gathered from the site the index already resolved. A
        // count left null is one this kind of declaration cannot have, which is what keeps M off a method
        // and P off a field; a hit that never resolved has no facts at all and so shows no column.
        static ShapeFacts ShapeOf(ProjectIndex.DocSite? site) => site is null
            ? default
            : new ShapeFacts(
                ParameterCount: site.ParameterCount,
                MemberCount: site.MemberCount,
                NestedCount: site.NestedCount,
                LineCount: ShapeFacts.LinesBetween(site.Line, site.EndLine),
                LandmarkCount: site.LandmarkCount,
                DocLines: site.DocLines,
                CommentLines: site.CommentLines,
                AttributeCount: site.AttributeCount);

        // Only the flat envelope needs this precomputed: the grouped one derives the same answer from the
        // rows it is handed. Either way the legend is emitted only when some hit actually carries a shape.
        var anyShape = limited.Any(r => SymbolShape.For(ShapeOf(r.Site)) is not null);

        object BuildFlatEnvelope() => new
        {
            limitedBy = searchLimitedBy,
            shape = anyShape ? SymbolShape.Legend : null,
            termsWithNoHits = termsWithNoHits.Count == 0 ? null : termsWithNoHits,
            items = limited.Select(r => new
            {
                symbolId = r.Hit.SymbolId,
                name = SymbolResolver.CompactName(r.Hit.FqName),
                kind = r.Hit.Kind,
                // Why this row has no file/line, when it has none. generated is the same field, spelled
                // the same way, that get_symbol sets on the same symbol; outsideRoot is the other reason
                // the syntax index never saw the declaration - a Compile item from beyond the repo root.
                generated = r.Hit.Placement is DeclarationPlacement.Generated ? true : (bool?)null,
                outsideRoot = r.Hit.Placement is DeclarationPlacement.OutsideRoot ? true : (bool?)null,
                file = r.Site?.File,
                line = r.Site?.Line,
                endLine = r.Site?.EndLine,
                shape = SymbolShape.For(ShapeOf(r.Site)),
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
                    summaryMode == "full" && r.Site?.Doc is { } doc ? CompactFormatter.Truncate(doc, SummaryCap) : null,
                    SymbolShape.For(ShapeOf(r.Site)),
                    r.Hit.Placement);
            }).ToList();
            var grouped = SymbolGrouping.Build(rows, primaryIsNamespace);
            var withLimit = new Dictionary<string, object?>();
            if (searchLimitedBy is not null)
                withLimit["limitedBy"] = searchLimitedBy;
            if (termsWithNoHits.Count > 0)
                withLimit["termsWithNoHits"] = termsWithNoHits;
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

        // A member row states where it is and what it costs, not just what it is called: the whole point
        // of being told a type has M members is to pick one and fetch it, and a row carrying neither a
        // location nor a shape leaves that second hop with nothing to go on. file is emitted only when it
        // differs from the type's own primary declaration file, so only a partial pays for the column.
        var primaryFile = sym.DeclaringSyntaxReferences.FirstOrDefault()?.SyntaxTree.FilePath;
        var memberRows = components.Has(SymbolComponents.Members) && sym is INamedTypeSymbol type
            ? type.GetMembers().Where(IsListable).Select(m => (Symbol: m, Site: MemberSiteOf(m))).ToArray()
            : null;
        var members = memberRows?.Select(row => (object)new
        {
            symbolId = SymbolKey.IdOf(row.Symbol),
            displayString = row.Symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            kind = SymbolKey.KindOf(row.Symbol),
            file = row.Site is { } elsewhere && !string.Equals(elsewhere.File, primaryFile, StringComparison.Ordinal)
                ? locator.RelPath(elsewhere.File)
                : null,
            line = row.Site?.Line,
            shape = row.Site is { } located ? SymbolShape.For(located.Facts) : null,
            // Narrowed to decl, matching what a member ROW actually serves: a name, a location and a shape,
            // never a body. VersionOf computes both layers, so handing its token over unnarrowed leased a
            // body this response never showed - the exact thing unleased_body exists to prevent, and the
            // opposite of what the line above it had claimed since it was written.
            contentVersion = VersionOf(row.Symbol).Narrow(["decl"]).ToString(),
        }).ToArray();
        var memberShapeLegend = memberRows?.Any(row => row.Site is { } s && SymbolShape.For(s.Facts) is not null) == true
            ? SymbolShape.Legend
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
            // A symbol only a source generator declares IS in source by Roslyn's reckoning, so origin says
            // "source" - while DeclarationSites deliberately drops obj/** and hands back an empty array.
            // In source, and nowhere to be found, read together as a lookup bug. Naming it says which of
            // the two it is, and that there is no span to patch because the file is rewritten every build.
            generated = IsGeneratedOnly(sym, locator) ? true : (bool?)null,
            containingType = ContainingType(sym),
            declarationSites = DeclarationSites(sym, locator),
            source = RenderSource(source, components.SourceQuery),
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
            // Stated once beside the member list rather than repeated on every row, same as search_index.
            shape = memberShapeLegend,
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

    /// <summary>
    /// An attribute's constructor and named arguments as one compact string, or null when it carries none
    /// the author actually wrote at the use site.
    /// </summary>
    /// <param name="attribute">The attribute application to render.</param>
    /// <returns>The joined argument list, or null when nothing was written between the brackets.</returns>
    /// <remarks>
    /// Arguments the COMPILER supplied are dropped rather than reported: a caller-info parameter is filled
    /// in from the use site's own location even though nothing is written there, so xUnit v3's [Fact] and
    /// [Theory] - which declare [CallerFilePath]/[CallerLineNumber] optional parameters - rendered their
    /// own source location as their arguments. On a test project that is the most common attribute there
    /// is, and it put an absolute machine path into a response whose every other path is repo-relative.
    /// </remarks>
    private static string? FormatAttributeArguments(AttributeData attribute)
    {
        var parameters = attribute.AttributeConstructor?.Parameters ?? [];
        var parts = new List<string>();
        for (var i = 0; i < attribute.ConstructorArguments.Length; i++)
        {
            if (i < parameters.Length && IsCallerSupplied(parameters[i]))
                continue;
            parts.Add(FormatTypedConstant(attribute.ConstructorArguments[i]));
        }

        parts.AddRange(attribute.NamedArguments.Select(kv => $"{kv.Key} = {FormatTypedConstant(kv.Value)}"));
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    /// <summary>Whether a parameter's argument is supplied by the compiler from the use site rather than written there.</summary>
    /// <param name="parameter">The attribute constructor parameter the argument was bound to.</param>
    /// <returns>True for a caller-info parameter, whose value is a location or an expression the author never typed.</returns>
    private static bool IsCallerSupplied(IParameterSymbol parameter) =>
        parameter.GetAttributes().Any(a => a.AttributeClass?.Name is
            "CallerFilePathAttribute" or "CallerLineNumberAttribute"
            or "CallerMemberNameAttribute" or "CallerArgumentExpressionAttribute");

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
    /// <remarks>
    /// Members that carry nothing are dropped rather than emitted as an empty collection or a null:
    /// absence already means "none", and the six empty members a typical symbol has cost ~18 tokens each
    /// time it is returned — 11% of a three-symbol batch.
    /// </remarks>
    /// <param name="sym">The symbol whose facts are wanted.</param>
    /// <param name="symbolStore">The store holding the extracted facts.</param>
    /// <returns>The non-empty facts, or null when there are none or the stored JSON no longer parses.</returns>
    private static object? MechanicalFactsFor(ISymbol sym, SymbolStore symbolStore)
    {
        var version = VersionOf(sym);
        var json = symbolStore.FactsFor(SymbolKey.IdOf(sym), version.Get("body"));
        if (json is null)
            return null;
        try
        {
            var root = JsonDocument.Parse(json).RootElement.Clone();
            if (root.ValueKind != JsonValueKind.Object)
                return root;

            var carried = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var fact in root.EnumerateObject())
            {
                var isEmpty = fact.Value.ValueKind switch
                {
                    JsonValueKind.Null => true,
                    JsonValueKind.Array => fact.Value.GetArrayLength() == 0,
                    JsonValueKind.Object => !fact.Value.EnumerateObject().Any(),
                    JsonValueKind.String => fact.Value.GetString() is { Length: 0 },
                    _ => false,
                };
                if (!isEmpty)
                    carried[fact.Name] = fact.Value;
            }

            return carried.Count == 0 ? null : carried;
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

    /// <summary>
    /// Whether every one of <paramref name="sym"/>'s declarations is source-generator output, and so
    /// <see cref="DeclarationSites"/> returns an empty array for a symbol that genuinely exists.
    /// </summary>
    /// <param name="sym">The symbol whose declaration sites were filtered.</param>
    /// <param name="locator">Renders each path repo-relative, which is what the generated-path test reads.</param>
    /// <returns>True only when there is at least one declaration and every one of them was excluded.</returns>
    internal static bool IsGeneratedOnly(ISymbol sym, SolutionLocator locator) =>
        sym.DeclaringSyntaxReferences.Length > 0
        && sym.DeclaringSyntaxReferences.All(r =>
            SolutionLocator.IsGeneratedOrBuildPath(locator.RelPath(r.SyntaxTree.FilePath)));


    /// <summary>One line of a symbol's <c>source</c> component — 1-based absolute file line plus its
    /// text, so a multi-line declaration renders as a real per-line table (TOON/JSON alike) instead of
    /// one string carrying literal \n/\" escapes, and each line's number is directly usable as a
    /// validate_patch startLine/endLine without a separate get_symbol round trip.</summary>
    private sealed record SourceLine(int Line, string Text);

    /// <summary>One contiguous run of a symbol's <c>source</c> under <c>-lineNumbers</c>: the run's
    /// absolute <c>start-end</c> file line span, plus its lines as bare text.</summary>
    private sealed record SourceSpan(string Lines, IReadOnlyList<string> Text);

    private static IReadOnlyList<SourceLine> SplitLines(string text, int startLine) =>
        text.Replace("\r\n", "\n").Split('\n')
            .Select((line, i) => new SourceLine(startLine + i, line))
            .ToArray();

    /// <summary>
    /// Renders a symbol's source either as the default per-line <c>{line, text}</c> list or, when
    /// <c>-lineNumbers</c> was requested, as one <c>{lines, text}</c> entry per contiguous run.
    /// </summary>
    /// <returns>Null only when there was no source to render, so an absent component stays absent.</returns>
    /// <remarks>
    /// Grouping into runs is what keeps the numberless form honest: <c>-modifier</c> exclusions and an
    /// <c>@</c> selection both drop lines, and bare text carrying no span headers would read as
    /// contiguous code across a gap that is really there.
    /// </remarks>
    private static object? RenderSource(IReadOnlyList<SourceLine>? lines, SourceQuery query)
    {
        if (lines is null)
            return null;
        if (!query.ExcludeLineNumbers)
            return lines;

        var spans = new List<SourceSpan>();
        var runStart = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            if (i + 1 < lines.Count && lines[i + 1].Line == lines[i].Line + 1)
                continue;

            var text = new string[i - runStart + 1];
            for (var j = 0; j < text.Length; j++)
                text[j] = lines[runStart + j].Text;
            spans.Add(new SourceSpan($"{lines[runStart].Line}-{lines[i].Line}", text));
            runStart = i + 1;
        }

        return spans;
    }

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

        // A long body can still be a bad outline target: the note only ever fired on SHORT declarations, so
        // a 40-150 line member of mostly sequential statements with one branch returned a near-empty outline
        // and said nothing about it. The caller then paid for the outline AND the source fetch it needed
        // anyway. Density, not length, is what decides whether an outline describes a body.
        const int maxLinesPerLandmark = 25;
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
            : rows.Count * maxLinesPerLandmark < lineCount
                ? $"declaration is {lineCount} lines but the outline has only {rows.Count} "
                    + $"entr{(rows.Count == 1 ? "y" : "ies")} - a mostly linear body, so this outline "
                    + "describes little of it and source:code (or a source:code@from-to range) is likely "
                    + "the better answer"
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
        // The span opens at the declaration's first TOKEN, so its first line would arrive with the
        // indentation stripped while every line under it kept its own. Widen back to the start of that
        // line: a caller reconstructing the declaration line from this text - which is exactly what the
        // tool tells it to do before a validate_patch edit - would otherwise write it back misindented.
        start = IndentStartOf(text, start);
        var span = TextSpan.FromBounds(start, end);
        var startLine = tree.GetLineSpan(span).StartLinePosition.Line + 1;
        var lines = SplitLines(text.ToString(span), startLine);

        var excluded = ExcludedLines(node, text, query);
        return excluded.Count == 0 ? lines : lines.Where(l => !excluded.Contains(l.Line)).ToArray();
    }

    /// <summary>
    /// Widens <paramref name="position"/> back to the start of its line when only whitespace separates
    /// the two, so a rendered first line carries the same indentation as the lines beneath it.
    /// </summary>
    /// <param name="text">The file the position indexes into.</param>
    /// <param name="position">An absolute character offset, normally a declaration's first token.</param>
    /// <returns>The widened offset, or <paramref name="position"/> when real code precedes it on the line.</returns>
    private static int IndentStartOf(SourceText text, int position)
    {
        var lineStart = text.Lines.GetLineFromPosition(position).Start;
        for (var i = lineStart; i < position; i++)
        {
            if (!char.IsWhiteSpace(text[i]))
                return position;
        }
        return lineStart;
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

    /// <summary>
    /// Renders a line list's extent as <c>"first-last"</c> when it is contiguous, as every contiguous run
    /// in order (<c>"63-70;146-147"</c>) when it is not, and <c>"none"</c> when empty.
    /// </summary>
    /// <remarks>
    /// The runs are the whole point. A multi-range <c>@</c> selection and a <c>-comments</c> exclusion both
    /// produce a DISJOINT set, and reporting min-to-max claimed the entire envelope for it: a 10-line answer
    /// to <c>source:code@63-70;146-147</c> described itself as <c>63-147</c>, which is precisely the string a
    /// caller reads as "I have the whole member" when it holds 10 of its 85 lines. A contiguous list still
    /// renders as the single span it always did, and every run emitted here is directly usable as a
    /// validate_patch startLine/endLine, the same guarantee the per-line numbers carry.
    /// </remarks>
    /// <param name="lines">The lines to describe.</param>
    /// <returns>A compact span string for the <c>sourceLines</c> field.</returns>
    private static string LineSpan(IReadOnlyList<SourceLine>? lines)
    {
        if (lines is not { Count: > 0 })
            return "none";

        var runs = new List<string>();
        var start = lines[0].Line;
        var previous = start;
        foreach (var line in lines.Skip(1))
        {
            if (line.Line == previous + 1)
            {
                previous = line.Line;
                continue;
            }

            runs.Add($"{start}-{previous}");
            start = previous = line.Line;
        }

        runs.Add($"{start}-{previous}");
        return string.Join(';', runs);
    }

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

    private static async Task<object?> ReferenceCounts(ISymbol sym, Solution solution, SymbolStore symbolStore)
    {
        // Include the interface members this symbol implements: calls made through the interface are
        // recorded against the interface member, and get_references cascades to implementations.
        var equivalentIds = new List<string> { SymbolKey.IdOf(sym) };
        equivalentIds.AddRange(ImplementedInterfaceMembers(sym).Select(SymbolKey.IdOf));

        // Both counts are omitted rather than reported as 0 wherever the symbol's own KIND already makes
        // a non-zero answer impossible: an enum or a static class has no implementers, a non-virtual
        // member has no overriders. Reporting those zeros restated the kind the response already names,
        // and the two fields were the ones surviving on a type while the counts that could actually vary
        // were dropped. Skipping the query is also what makes them free — each is a solution-wide walk.
        var implementations = CanHaveImplementations(sym) ? await CountImplementations(sym, solution) : (int?)null;
        var overrides = CanHaveOverrides(sym) ? await CountOverrides(sym, solution) : (int?)null;

        // Call edges are recorded against MEMBERS, never against named types, so a type's caller count
        // would structurally always be 0 — which reads as "nothing uses this" when the truth is "not
        // measured at this level". Omit those fields for types; implementations/overrides are the
        // meaningful relationships for a type anyway — and where neither of those can apply either, the
        // whole component goes rather than an empty object.
        if (sym is INamedTypeSymbol)
        {
            return implementations is null && overrides is null
                ? null
                : new { callers = (int?)null, implementations, overrides, tests = (int?)null };
        }


        var counts = symbolStore.ReferenceCounts(equivalentIds);

        // A zero from the edge cache is only a fact if the cache covers this symbol's project at all.
        // When the project contributed no edges — typically because it failed to load in MSBuild —
        // omit the counts rather than assert a 0 that get_references will immediately contradict.
        var measured = counts is not null && symbolStore.HasEdgeCoverageFor(SymbolKey.IdOf(sym));
        if (!measured && implementations is null && overrides is null)
            return null;

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

    /// <summary>
    /// Whether an implementation count could ever be non-zero for <paramref name="sym"/>.
    /// </summary>
    /// <param name="sym">The symbol a reference-count block is being built for.</param>
    /// <returns>True when <see cref="CountImplementations"/> has something to search for.</returns>
    /// <remarks>Mirrors <see cref="CountImplementations"/>'s own dispatch: whatever falls to its zero arm
    /// is a structural zero, and is omitted instead of reported.</remarks>
    private static bool CanHaveImplementations(ISymbol sym) => sym switch
    {
        INamedTypeSymbol { TypeKind: TypeKind.Interface } => true,
        // A sealed, static or non-class type has no derived classes to find; static and enum types read
        // as sealed here, which is what excludes them.
        INamedTypeSymbol { TypeKind: TypeKind.Class, IsSealed: false, IsStatic: false } => true,
        INamedTypeSymbol => false,
        _ => sym.ContainingType?.TypeKind == TypeKind.Interface,
    };

    /// <summary>
    /// Whether an override count could ever be non-zero for <paramref name="sym"/>.
    /// </summary>
    /// <param name="sym">The symbol a reference-count block is being built for.</param>
    /// <returns>True when <see cref="CountOverrides"/> has something to search for.</returns>
    /// <remarks>Mirrors <see cref="CountOverrides"/>'s own condition, for the reason
    /// <see cref="CanHaveImplementations"/> mirrors its counterpart.</remarks>
    private static bool CanHaveOverrides(ISymbol sym) =>
        sym is not INamedTypeSymbol && sym is { IsVirtual: true } or { IsAbstract: true };

    // ---- reference directions -------------------------------------------------

private sealed record RefItem(string SymbolId, string Version, string DisplayString, string CompactDisplayString,
        IReadOnlyList<(string File, int Line, string Snippet, bool IsCref)> Sites, string? DispatchKind, IReadOnlyList<SourceLine>? Body,
        bool IsTest = false);


private static async Task<List<RefItem>> Callers(ISymbol sym, Solution solution, SolutionLocator locator, bool includeBodies)
    {
        // A named type is not invocable, so FindCallersAsync answers nothing for one. Delegates are the
        // case that made this visible -- a delegate type is USED by the members that declare, construct or
        // invoke it, and "who uses this type" came back as an empty list instead.
        if (sym is INamedTypeSymbol type)
            return await TypeReferences(type, solution, locator, includeBodies);

        var dispatch = DispatchKindOf(sym);
        var items = new List<RefItem>();
        foreach (var caller in await SymbolFinder.FindCallersAsync(sym, solution))
        {
            if (!caller.Locations.Any(l => l.IsInSource))
                continue;
            var sites = caller.Locations
                .Where(l => l.IsInSource)
                .Select(l =>
                {
                    var span = l.GetLineSpan();
                    return (File: locator.RelPath(span.Path), Line: span.StartLinePosition.Line + 1,
                        Snippet: l.SourceTree?.GetText().Lines[span.StartLinePosition.Line].ToString().Trim() ?? "",
                        IsCref: TypeReferenceScan.IsCrefLocation(l));
                })
                // One row per {file, line}: a signature or tuple naming the symbol several times on one
                // line emitted that many byte-identical rows, and since the snippet is the whole line,
                // everything they carried is still in the single row that survives.
                .DistinctBy(s => (s.File, s.Line))
                .ToList();
            items.Add(new RefItem(
                SymbolKey.IdOf(caller.CallingSymbol),
                VersionOf(caller.CallingSymbol).ToString(),
                caller.CallingSymbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                CompactDisplay(caller.CallingSymbol),
                sites,
                dispatch,
                includeBodies ? SourceOf(caller.CallingSymbol) : null,
                TestAttributes.IsTestMethod(caller.CallingSymbol)));
        }
        return items;
    }

    /// <summary>Members that reference a named type, grouped one item per referencing member.</summary>
    private static async Task<List<RefItem>> TypeReferences(INamedTypeSymbol type, Solution solution, SolutionLocator locator, bool includeBodies)
    {
        var dispatch = DispatchKindOf(type);
        var sitesByOwner = new Dictionary<ISymbol, List<(string File, int Line, string Snippet, bool IsCref)>>(SymbolEqualityComparer.Default);
        foreach (var reference in await SymbolFinder.FindReferencesAsync(type, solution))
        {
            foreach (var location in reference.Locations)
            {
                if (!location.Location.IsInSource)
                    continue;
                var owner = await TypeReferenceScan.OwningMemberAsync(location);
                if (owner is null)
                    continue;

                var span = location.Location.GetLineSpan();
                var lineIndex = span.StartLinePosition.Line;
                var snippet = location.Location.SourceTree?.GetText().Lines[lineIndex].ToString().Trim() ?? "";
                if (!sitesByOwner.TryGetValue(owner, out var sites))
                    sitesByOwner[owner] = sites = [];
                sites.Add((locator.RelPath(span.Path), lineIndex + 1, snippet, TypeReferenceScan.IsCrefLocation(location.Location)));
            }
        }

        return sitesByOwner
            .Select(pair => new RefItem(
                SymbolKey.IdOf(pair.Key),
                VersionOf(pair.Key).ToString(),
                pair.Key.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                CompactDisplay(pair.Key),
                [.. pair.Value.DistinctBy(s => (s.File, s.Line))],
                dispatch,
                includeBodies ? SourceOf(pair.Key) : null,
                TestAttributes.IsTestMethod(pair.Key)))
            .ToList();
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
            return (File: locator.RelPath(span.Path), Line: span.StartLinePosition.Line + 1,
                Snippet: s.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat), IsCref: false);
        }).ToList();
        return new RefItem(SymbolKey.IdOf(s), VersionOf(s).ToString(),
            s.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat), CompactDisplay(s), sites, dispatch,
            includeBodies ? SourceOf(s) : null);
    }

    // The default get_references/get_call_hierarchy displayString: name + arity (e.g.
    // "ContextTools.GetSymbol/13") rather than the full parameter list with types and default values,
    // which answers "who/what is this" for a fraction of the tokens; the full form is still one
    // fields:"signature" away on either tool.
    /// <summary>
    /// The compact name/arity form — <c>ContextTools.GetSymbol/13</c> — that a reference or
    /// call-hierarchy item carries unless the caller asked for the full signature.
    /// </summary>
    /// <param name="s">The symbol to name.</param>
    /// <returns>The containing type and member name, with <c>/N</c> appended for a method's arity.</returns>
    private static string CompactDisplay(ISymbol s)
    {
        var name = s.ToDisplayString(CompactMemberFormat);
        return s is IMethodSymbol method ? $"{name}/{method.Parameters.Length}" : name;
    }


    private static string DispatchKindOf(ISymbol target)
    {
        if (target is INamedTypeSymbol { TypeKind: TypeKind.Delegate })
            return "delegate";

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
    internal static string ResolveHandle(string symbol, SymbolStore symbolStore)
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
        object? source = null;
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
                    source = RenderSource(SourceLinesOf(normalized, parts.SourceQuery), parts.SourceQuery);
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

    // internal: rename_symbol checks a caller's held baseVersion against the same syntax-layer token
    // get_symbol minted, so the two must be computed by one function rather than two.
    internal static ContentVersion VersionOf(ISymbol symbol)
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
    /// Where one member of a type sits and what retrieval shape it has, computed from its own syntax —
    /// null for a member with no declaration to walk (compiler-synthesized, or an external symbol).
    /// </summary>
    /// <param name="member">The member to locate and measure.</param>
    /// <returns>Its absolute file path, signature line, and shape facts; null when it has no declaration.</returns>
    /// <remarks>
    /// Measured with the same <see cref="OutlineBuilder"/> helpers the syntax index uses, so a member's
    /// shape here and the same symbol's shape from search_index cannot disagree — a test asserts it.
    /// <c>Line</c> is the signature line, matching search_index's convention; get_symbol's own
    /// declarationSites span, which starts at the doc comment, stays the anchor for an edit.
    /// </remarks>
    private static (string File, int Line, ShapeFacts Facts)? MemberSiteOf(ISymbol member)
    {
        if (member.DeclaringSyntaxReferences.FirstOrDefault() is not { } reference)
            return null;

        var node = NormalizeDeclNode(reference.GetSyntax());
        var span = node.GetLocation().GetLineSpan();
        var line = span.StartLinePosition.Line + 1;
        var declaration = node as MemberDeclarationSyntax;
        var facts = new ShapeFacts(
            ParameterCount: member is IMethodSymbol method ? method.Parameters.Length : null,
            LineCount: ShapeFacts.LinesBetween(line, span.EndLinePosition.Line + 1),
            LandmarkCount: declaration is null ? null : OutlineBuilder.LandmarkCount(declaration),
            DocLines: OutlineBuilder.DocLines(node),
            CommentLines: OutlineBuilder.CommentLines(node),
            AttributeCount: declaration is null ? 0 : OutlineBuilder.AttributeCount(declaration));

        return (reference.SyntaxTree.FilePath, line, facts);
    }

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

    /// <summary>Whether a member belongs in a type's member listing.</summary>
    /// <param name="member">The member the containing type declares.</param>
    /// <returns>True for a member a reader could go on to open in source.</returns>
    /// <remarks>
    /// PRIVATE members are listed, because search_index's M count includes them: M is read off the syntax
    /// outline, which counts every member a type declares, and filtering the listing by accessibility made
    /// the route the shape column advertises - M9, therefore fetch the member list - return two rows, with
    /// nothing in the response naming the seven it dropped or why. A listing that under-delivers against
    /// its own advertised count is worse than a longer one.
    /// The method kinds mirror <see cref="Indexing.OutlineBuilder"/>'s: accessors, destructors and
    /// conversion operators contribute no outline entry, so listing them would reintroduce the same
    /// disagreement from the opposite side. Two divergences survive by construction and are documented in
    /// docs/tools/get_symbol.md: M is counted per DECLARATION, so a partial type's row counts one file's
    /// share while this listing merges every part, and a nested type is counted by N yet still listed here.
    /// </remarks>
    private static bool IsListable(ISymbol member)
    {
        if (member.IsImplicitlyDeclared)
            return false;
        return member is not IMethodSymbol method
            || method.MethodKind is MethodKind.Ordinary or MethodKind.Constructor
                or MethodKind.StaticConstructor or MethodKind.UserDefinedOperator;
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
            return (null, Formats.Render(new
            {
                error = "symbol_not_found",
                symbol,
                didYouMean = NearMisses(symbolStore, symbol),
            }));
        }
        return (null, AmbiguousSymbol(resolution.Candidates));
    }

    /// <summary>
    /// Ranked index hits for the last segment of a name that did not resolve, so symbol_not_found carries
    /// something to act on. Null when the segment is too short to rank on or nothing came back.
    /// </summary>
    /// <remarks>
    /// A miss is almost always a NEAR miss - a wrong namespace, a dropped containing type, a plural slip.
    /// Echoing the unresolved name back told the caller only what it had just sent, so the next move was
    /// always a search_index round trip for a list the resolver was already positioned to produce.
    /// The ranked search answers a wrong QUALIFICATION; it cannot answer a misspelling, which matches
    /// neither an FTS token nor a substring, so an empty result falls through to an edit-distance scan.
    /// </remarks>
    internal static object? NearMisses(SymbolStore symbolStore, string symbol)
    {
        const int cap = 5;

        var bare = SymbolResolver.NameWithoutParameters(symbol);
        var segment = bare[(bare.LastIndexOf('.') + 1)..];

        // Under three characters the ranking is noise; a sym_/symidx_/symfb_ handle is not a name at all,
        // and a caller holding a stale one needs the id explanation it already gets, not a name guess.
        if (segment.Length < 3 || segment.StartsWith("sym", StringComparison.Ordinal))
            return null;

        var hits = symbolStore.Search(segment, null, null, cap);
        if (hits.Count == 0)
            hits = symbolStore.NearNames(segment, cap);

        return hits.Count == 0
            ? null
            : hits.Select(h => new
            {
                symbolId = h.SymbolId,
                name = SymbolResolver.CompactName(h.FqName),
                kind = h.Kind,
            }).ToList();
    }

    /// <summary>
    /// How many candidates an <c>ambiguous_symbol</c> error lists before it reports a count instead.
    /// </summary>
    private const int MaxAmbiguousCandidates = 10;

    /// <summary>
    /// Renders the shared <c>ambiguous_symbol</c> error payload: a capped candidate list, and the total
    /// it was capped from.
    /// </summary>
    /// <param name="candidates">Every symbol the spec matched, in resolution order.</param>
    /// <param name="message">Guidance to lead with, or null for the candidates alone.</param>
    /// <returns>The rendered error response.</returns>
    /// <remarks>
    /// The cap is what keeps a name like <c>Run</c> - 50+ members across a test tree - an affordable
    /// error rather than a second full response, but it only works if it announces itself. Shown ten
    /// alphabetically-early names and no total, a caller whose target was cut off concludes the symbol
    /// does not exist, or picks the wrong one. totalCandidates/truncated are the same convention
    /// get_references and get_scope report their own caps under.
    /// </remarks>
    internal static string AmbiguousSymbol(IReadOnlyList<ISymbol> candidates, string? message = null)
    {
        var truncated = candidates.Count > MaxAmbiguousCandidates;
        var overflowNote = truncated
            ? $"Only {MaxAmbiguousCandidates} of {candidates.Count} matches are listed; if the intended "
                + "one is not among them, narrow the name with its containing type, its namespace, or a "
                + "parameter list."
            : null;
        return Formats.Render(new
        {
            error = "ambiguous_symbol",
            message = message is null ? overflowNote : (overflowNote is null ? message : message + " " + overflowNote),
            candidates = candidates.Take(MaxAmbiguousCandidates).Select(c => new
            {
                symbolId = SymbolKey.IdOf(c),
                displayString = c.ToDisplayString(),
            }),
            totalCandidates = candidates.Count,
            truncated = truncated ? true : (bool?)null,
        });
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
