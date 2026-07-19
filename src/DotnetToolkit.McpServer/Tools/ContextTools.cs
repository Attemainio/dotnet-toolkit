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
using ModelContextProtocol.Server;

namespace DotnetToolkit.McpServer.Tools;

/// <summary>
/// The v2 read surface (spec §9, §10, §16): symbol retrieval, relationship traversal, and ranked
/// discovery. All responses are ctx-contract/2.0 JSON envelopes carrying version tokens so the agent
/// can hold leases and avoid re-transmitting unchanged content.
/// </summary>
[McpServerToolType]
public static class ContextTools
{
    private const int ReferenceCap = 50;

    [McpServerTool(Name = "get_symbol")]
    [Description("Retrieve one C# symbol at the resolution you need (signature|outline|full). "
        + "USE THIS INSTEAD OF READING A .cs FILE — it returns the whole symbol even when it is split across "
        + "partial-class files (Read gives you one fragment and no signal that the rest exists), and costs a "
        + "fraction of the tokens of the file. Returns a version token for leasing; pass knownVersion to get "
        + "changed:false when nothing moved, or refetch:true to force content. "
        + "For a targeted fetch, adjust the resolution's default set with include/exclude, naming components "
        + "exactly as they appear in the response: source, xmlDoc, mechanicalFacts, referenceCounts, "
        + "recentLog, members. e.g. resolution:\"full\" exclude:\"source\" for everything known about a symbol "
        + "except its text, or resolution:\"signature\" include:\"members\" to list a type's API without bodies.")]
    public static async Task<string> GetSymbol(
        WorkspaceHost workspace,
        SolutionLocator locator,
        ProjectIndex index,
        SymbolStore symbolStore,
        FeatureLogStore featureLog,
        SymbolIndexBuilder indexBuilder,
        TelemetryRecorder telemetry,
        [Description("Fully-qualified name (append a parameter list to pick an overload), a unique suffix, or a sym_... id from a previous response.")] string symbol,
        [Description("signature | outline | full (default signature).")] string resolution = "signature",
        [Description("Comma-separated components to ADD to the resolution's default set: "
            + "source, xmlDoc, mechanicalFacts, referenceCounts, recentLog, members.")] string? include = null,
        [Description("Comma-separated components to REMOVE from the resolution's default set. "
            + "Same names as include.")] string? exclude = null,
        [Description("Held version token to lease against.")] string? knownVersion = null,
        [Description("Force full content even if the version matches.")] bool refetch = false,
        [Description("Optional agent conversation id (ses_...) for telemetry grouping.")] string? sessionId = null,
        [Description("Optional user task id (tsk_...) for telemetry grouping.")] string? taskId = null)
    {
        sessionId ??= Ids.AmbientSession;
        taskId ??= Ids.UnattributedTask;
        var toolCallId = Ids.ToolCall();
        var solution = await workspace.GetSolutionAsync();
        if (solution is null)
        {
            // Workspace not ready: answer from the syntax index at signature level (Conformance C11).
            await index.EnsureFreshAsync();
            var fallback = IndexSymbol(index, locator, symbol, resolution);
            if (fallback is { } fb)
            {
                var indexLease = Lease.Evaluate(ContentVersion.Parse(fb.Version), knownVersion, refetch);
                object indexEnvelope = indexLease.OmitContent
                    ? new
                    {
                        contract = Contract.Id, toolCallId, symbolId = fb.SymbolId, contentVersion = fb.Version,
                        changed = false, heldVersion = indexLease.HeldVersion, staleness = "index_only",
                        content = (object?)null, refetchHint = Lease.RefetchHint,
                    }
                    : new
                    {
                        contract = Contract.Id, toolCallId, symbolId = fb.SymbolId, contentVersion = fb.Version,
                        changed = true, heldVersion = (string?)null, staleness = "index_only", content = fb.Content,
                    };
                var indexJson = Formats.ToJson(indexEnvelope);
                return Record(telemetry, toolCallId, sessionId, taskId, "get_symbol", symbol, fb.SymbolId, resolution,
                    knownVersion, refetch, indexLease.OmitContent, fb.Version, 1, "index_only", null, indexJson);
            }

            var loading = Error(toolCallId, workspace.State == WorkspaceState.Loading ? "workspace_loading" : "no_workspace");
            return Record(telemetry, toolCallId, sessionId, taskId, "get_symbol", symbol, null, resolution,
                knownVersion, refetch, false, null, 0, "index_only", "workspace_loading", loading);
        }

        var (sym, error) = await ResolveAsync(solution, ResolveHandle(symbol, symbolStore), toolCallId);
        if (sym is null)
            return Record(telemetry, toolCallId, sessionId, taskId, "get_symbol", symbol, null, resolution,
                knownVersion, refetch, false, null, 0, "live", "unresolved", error!);

        var symbolId = SymbolKey.IdOf(sym);

        var components = SymbolComponents.Resolve(
            resolution, include, exclude, sym is INamedTypeSymbol, out var invalidComponent);
        if (components is not { } parts)
        {
            var badComponent = Error(toolCallId, "invalid_component",
                $"'{invalidComponent}' is not a component. Valid: {string.Join(", ", SymbolComponents.All)}.");
            return Record(telemetry, toolCallId, sessionId, taskId, "get_symbol", symbol, symbolId, resolution,
                knownVersion, refetch, false, null, 0, "live", "invalid_component", badComponent);
        }

        // The token describes only the layers this response's components were derived from, so a caller
        // that held it can never be told "unchanged" about content it was never sent.
        var version = FullVersionOf(sym, symbolStore).Narrow(parts.RequiredLayers);
        var staleness = indexBuilder.Ready ? "live" : "index_only";
        var lease = Lease.Evaluate(version, knownVersion, refetch, parts.RequiredLayers);

        object envelope;
        if (lease.OmitContent)
        {
            // Lease hit: content is omitted entirely. changed:false is the signal; heldVersion and the
            // hint are required by the lease contract (§8).
            envelope = new
            {
                symbolId,
                contentVersion = version.ToString(),
                changed = false,
                heldVersion = lease.HeldVersion,
                staleness = staleness == "live" ? null : staleness,
                refetchHint = Lease.RefetchHint,
            };
        }
        else
        {
            var content = await BuildContent(sym, parts, solution, locator, symbolStore, indexBuilder, featureLog);
            // "changed" is omitted here: content being present already says it.
            envelope = new
            {
                symbolId,
                contentVersion = version.ToString(),
                staleness = staleness == "live" ? null : staleness,
                // Echoed only when the caller narrowed the set: on a plain resolution it would repeat
                // what the resolution name already said, on every single call.
                components = include is null && exclude is null ? null : parts.Resolved,
                content,
            };
        }

        var json = Formats.ToJson(envelope);
        return Record(telemetry, toolCallId, sessionId, taskId, "get_symbol", symbol, symbolId, resolution,
            knownVersion, refetch, lease.OmitContent, version.ToString(), 1, staleness, null, json);
    }

    [McpServerTool(Name = "get_references")]
    [Description("Callers, implementations or overrides of a C# symbol, from the compiler's own model. "
        + "USE THIS INSTEAD OF GREP — grep gives wrong caller lists: it cannot see interface, virtual or delegate "
        + "dispatch, counts comment and string matches as hits, and silently drops sites when output is truncated. "
        + "Returns every real call site, no false positives, and reports how many text-only matches it excluded. "
        + "Each item carries a version token.")]
    public static async Task<string> GetReferences(
        WorkspaceHost workspace,
        SolutionLocator locator,
        SymbolStore symbolStore,
        TelemetryRecorder telemetry,
        [Description("Fully-qualified name, unique suffix, or a sym_... id from a previous response.")] string symbol,
        [Description("callers | implementations | overrides (default callers).")] string direction = "callers",
        [Description("Include member bodies inline (default false).")] bool includeBodies = false,
        [Description("Optional agent conversation id (ses_...) for telemetry grouping.")] string? sessionId = null,
        [Description("Optional user task id (tsk_...) for telemetry grouping.")] string? taskId = null)
    {
        sessionId ??= Ids.AmbientSession;
        taskId ??= Ids.UnattributedTask;
        var toolCallId = Ids.ToolCall();
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
            // The resolved target, so the caller can confirm which overload this answered for.
            // direction/targetContentVersion are omitted: the first echoes the request, the second is
            // only useful from get_symbol where the target is actually being leased.
            targetSymbolId = SymbolKey.IdOf(sym),
            items = shown.Select(i => new
            {
                symbolId = i.SymbolId,
                // Per-item version so leases accumulate before any body is fetched (§10).
                contentVersion = i.Version,
                displayString = i.DisplayString,
                sites = i.Sites.Select(s => new { file = s.File, line = s.Line, snippet = s.Snippet }),
                dispatchKind = i.DispatchKind,
                content = i.Body,
            }),
            totalItems = ordered.Count,
            truncated = truncated ? true : (bool?)null,
            // Reported only when text-only matches actually existed; generatedCode is omitted until it
            // is genuinely computed rather than hardcoded to zero.
            excludedTextMatches = excludedComments > 0 ? excludedComments : (int?)null,
        };

        var json = Formats.ToJson(envelope);
        return Record(telemetry, toolCallId, sessionId, taskId, "get_references", symbol, SymbolKey.IdOf(sym), null,
            null, false, false, null, shown.Count, "live", null, json, normalized);
    }

    [McpServerTool(Name = "search_index")]
    [Description("Find C# symbols when you don't know their exact names. "
        + "USE THIS INSTEAD OF GREP/GLOB over .cs files — it returns ranked symbols with ids and locations, not "
        + "raw text lines, so there is nothing to hand-filter and no truncation to silently lose hits. "
        + "PUT EVERY TERM YOU ARE LOOKING FOR IN ONE CALL: terms are OR-ed and ranked, so "
        + "query:\"fee ledger TryBuy TrySell\" returns the symbols for all four in a single response. Do NOT "
        + "issue one call per word — that is several times the tokens for a worse-ranked result. Partial and "
        + "camel-case-interior terms work too: \"Ledger\" finds FIFOLedger. "
        + "Follow up with get_symbol on the ones you want.")]
    public static string SearchIndex(
        SymbolStore symbolStore,
        ProjectIndex index,
        TelemetryRecorder telemetry,
        [Description("Free-text query over symbol names.")] string query,
        [Description("Optional kind filter, e.g. Method,Type.")] string? kinds = null,
        [Description("Max results (default 10, cap 50).")] int limit = 10,
        [Description("Optional agent conversation id (ses_...) for telemetry grouping.")] string? sessionId = null,
        [Description("Optional user task id (tsk_...) for telemetry grouping.")] string? taskId = null)
    {
        sessionId ??= Ids.AmbientSession;
        taskId ??= Ids.UnattributedTask;
        var toolCallId = Ids.ToolCall();
        limit = Math.Clamp(limit, 1, ReferenceCap);
        var kindList = string.IsNullOrWhiteSpace(kinds)
            ? null
            : kinds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var hits = symbolStore.Search(query, NormalizeKinds(kindList), limit);
        var searchStaleness = symbolStore.SymbolCount() > 0 ? "live" : "index_only";
        object envelope = new
        {
            // staleness is emitted only when it is NOT the default "live" — silence means live.
            staleness = searchStaleness == "live" ? null : searchStaleness,
            items = hits.Select(h => new
            {
                symbolId = h.SymbolId,
                // Fully qualified up to the member — unambiguous across namespaces and directly usable
                // as a retrieval target — but with parameter types shortened. A 15-parameter method spent
                // ~500 characters here repeating the same namespace once per parameter, and the resolver
                // strips those prefixes before matching anyway, so the long form bought nothing.
                // Rank/score and matchedOn are omitted — the list is already ordered.
                name = SymbolResolver.CompactName(h.FqName),
                kind = h.Kind,
            }),
        };

        var json = Formats.ToJson(envelope);
        return Record(telemetry, toolCallId, sessionId, taskId, "search_index", query, null, null,
            null, false, false, null, hits.Count, searchStaleness, null, json);
    }

    // ---- content builder -----------------------------------------------------

    /// <summary>
    /// Builds the response body for exactly the requested components. One path for every resolution:
    /// the outline case used to return early from its own object literal, which is why an outline
    /// silently lacked containingType and recentLog — a divergence, not a decision.
    ///
    /// Every optional field is null when not requested, and <c>WhenWritingNull</c> drops it from the
    /// JSON — so an excluded component costs nothing, not even an empty key.
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
                ? OutlineBuilder.SummaryFromXml(sym.GetDocumentationCommentXml())
                : null,
            // Body-derived facts, served only while the body hash they were computed from still holds.
            mechanicalFacts = components.Has(SymbolComponents.MechanicalFacts)
                ? MechanicalFactsFor(sym, symbolStore)
                : null,
            referenceCounts = counts,
            members,
            // Why this code is the way it is. Entries describing a superseded body are flagged rather
            // than presented as current truth.
            recentLog = components.Has(SymbolComponents.RecentLog) ? RecentLogFor(sym, featureLog) : null,
        };
    }

    /// <summary>
    /// The last few development-log entries touching this symbol (spec §9). An entry whose recorded
    /// new version no longer matches the symbol's current body layer is marked <c>current: false</c> —
    /// stale history is surfaced as stale, never silently as fact.
    /// </summary>
    private static object? RecentLogFor(ISymbol sym, FeatureLogStore featureLog)
    {
        var entries = featureLog.RecentForSymbol([SymbolKey.IdOf(sym)]);
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
            var span = r.SyntaxTree.GetLineSpan(r.Span);
            // Flat file/startLine/endLine — these feed straight into a validate_patch edit.
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
        var header = sym.ContainingType?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        return header is null ? node.ToString() : $"// in {header}\n{node}";
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
        IReadOnlyList<(string File, int Line, string Snippet)> Sites, string? DispatchKind, string? Body);

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
                includeBodies ? SourceOf(caller.CallingSymbol) : null));
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
        ProjectIndex index, SolutionLocator locator, string symbol, string resolution)
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
                if (resolution.Trim().Equals("full", StringComparison.OrdinalIgnoreCase))
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

    // detail carries what the caller needs to correct the call — omitted when the kind says it all.
    private static string Error(string toolCallId, string kind, string? detail = null) =>
        Formats.ToJson(new { error = kind, detail });

    private static string Record(
        TelemetryRecorder telemetry, string toolCallId, string sessionId, string taskId, string tool,
        string requestedSymbol, string? symbolId, string? resolution, string? knownVersion, bool refetch,
        bool leaseHit, string? contentVersion, int returnedSymbols, string staleness, string? errorKind, string result,
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
            Staleness = staleness,
            ErrorKind = errorKind,
        });
        return result;
    }
}
