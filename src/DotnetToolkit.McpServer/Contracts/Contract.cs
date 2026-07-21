namespace DotnetToolkit.McpServer.Contracts;

/// <summary>
/// Shared constants for the response contract. Every v2 tool response envelope carries
/// <see cref="Id"/> and a <c>toolCallId</c> (spec Part III preamble).
/// </summary>
public static class Contract
{
    /// <summary>
    /// The response contract version.
    /// <list type="bullet">
    /// <item><description>
    /// <b>3.15</b> — <c>sessionId</c>/<c>taskId</c> are removed as caller-facing arguments from
    /// <c>search_index</c>, <c>get_symbol</c>, <c>get_references</c>, and <c>validate_patch</c>. Every
    /// call in a server process now shares one ambient session id automatically (unchanged mechanism,
    /// just no longer overridable) and the task concept is retired entirely — nothing read it back
    /// through any tool, so it bought attribution granularity nobody consumed. BREAKING for a caller
    /// that passed either argument: it is now rejected as an unrecognized parameter.
    /// <c>get_retrieval_metrics</c> changes to match: <c>scope: "task"</c>/<c>taskId</c> are gone;
    /// <c>sessionId</c> becomes <c>sessionIds</c> (an array, merged together when scope="session");
    /// <c>since</c>/<c>until</c> (ISO <c>yyyy-MM-dd</c>) filter any scope by <c>created_at</c>; and
    /// <c>groupBy</c> gains <c>"session"</c> (with <c>firstSeen</c>/<c>lastSeen</c>) — since there is no
    /// session directory, this is how a caller discovers past session ids for a date range before
    /// merging them via <c>sessionIds</c>.
    /// </description></item>
    /// <item><description>
    /// <b>3.14</b> — <c>get_symbol</c>'s <c>xmlDoc</c> (3.13) gains <c>value</c>, <c>inheritdoc</c>,
    /// <c>params</c>, and <c>typeParams</c> alongside <c>summary</c>/<c>returns</c>/<c>remarks</c>/
    /// <c>exceptions</c> — <c>value</c> from <c>&lt;value&gt;</c> (properties), <c>params</c>/
    /// <c>typeParams</c> as arrays of <c>{name, text}</c> from <c>&lt;param&gt;</c>/<c>&lt;typeparam&gt;</c>,
    /// and <c>inheritdoc</c> (<c>true</c>) when an <c>&lt;inheritdoc/&gt;</c> tag is present. Additive to
    /// 3.13's object shape: existing consumers reading <c>xmlDoc.summary</c> etc. are unaffected; new keys
    /// only appear when their tag is present. Also new: an <c>attributes</c> include component —
    /// <c>[{name, arguments}]</c> of this symbol's own (non-inherited) C# attributes, absent when there are
    /// none; a long string argument is truncated rather than reproduced in full. Both changes together mean
    /// <c>xmlDoc</c> is no longer absent-or-summary-only as the missing-doc signal: a doc comment with only
    /// a <c>&lt;returns&gt;</c> or a bare <c>&lt;inheritdoc/&gt;</c> now surfaces as a populated <c>xmlDoc</c>
    /// with <c>summary</c> specifically absent, distinguishable from no doc comment at all (see
    /// <c>docs/xml-documentation.md</c> and <c>agents/dotnet-code-review.md</c>'s <c>docs</c> dimension).
    /// </description></item>
    /// <item><description>
    /// <b>3.13</b> — <c>get_symbol</c>'s <c>xmlDoc</c> becomes a structured
    /// <c>{summary, returns, remarks, exceptions}</c> breakdown of the doc comment instead of the plain
    /// <c>&lt;summary&gt;</c> string it was through 3.12. Each field is XML-stripped to plain text and
    /// absent when that tag isn't in the doc comment; <c>exceptions</c> is an array of
    /// <c>{type, text}</c>, one per <c>&lt;exception&gt;</c> tag. <c>xmlDoc</c> itself is still absent
    /// only when NONE of these tags are present — a doc comment with only a <c>&lt;returns&gt;</c> and no
    /// <c>&lt;summary&gt;</c> now surfaces that (previously indistinguishable from no doc comment at all,
    /// since the old extractor only ever looked at <c>&lt;summary&gt;</c>). BREAKING: a consumer reading
    /// <c>content.xmlDoc</c> as a string sees an object; it now reads <c>content.xmlDoc.summary</c> for
    /// the equivalent value, and must check for its presence separately from <c>xmlDoc</c>'s own presence.
    /// <c>&lt;param&gt;</c> is still not extracted, since it duplicates the parameter list already in
    /// <c>displayString</c>.
    /// </description></item>
    /// <item><description>
    /// <b>3.12</b> — <c>get_retrieval_metrics</c>'s <c>totals.toolCalls</c>/<c>totals.tokensReturned</c>
    /// now include <c>validate_patch</c> activity, and the default <c>groupBy: "tool"</c> gains a
    /// <c>validate_patch</c> bucket. <c>validate_patch</c> writes <c>patch_events</c>, not
    /// <c>retrieval_events</c> like every other tool; <c>ReadTotals</c>/<c>ReadGroups</c> previously
    /// queried <c>retrieval_events</c> only, so validate_patch's own recorded <c>returned_tokens</c>
    /// were silently excluded from every total and never appeared in any <c>tool</c>-grouped row.
    /// Additive/fix: field shapes are unchanged, but reported totals and the group list both grow to
    /// include activity that was always recorded but never surfaced.
    /// </description></item>
    /// <item><description>
    /// <b>3.11</b> — <c>search_index</c> gained <c>pathPrefix</c>, an optional folder/file scope
    /// (repo-root-relative, forward slashes, matched on a path-segment boundary). Additive: omitted,
    /// behavior is byte-for-byte unchanged. Location isn't stored beside the symbol row (same reasoning
    /// as 3.3's <c>file</c>/<c>line</c>), so scoping is done by ranking the full index first and then
    /// filtering the resolved sites — a hit whose file can't be resolved (an overloaded name) is excluded
    /// rather than guessed into scope, and a query with far more out-of-scope hits than fit the internal
    /// overfetch cap can return fewer than <c>limit</c> even though more in-scope matches exist.
    /// </description></item>
    /// <item><description>
    /// <b>3.10</b> — <c>CompactTable</c>/<c>JsonHoist</c> (the <c>{columns,rows}</c> table shape
    /// introduced across 3.4–3.8) are removed entirely. <c>search_index</c>'s/<c>get_references</c>'/
    /// <c>get_symbol</c> batch's/<c>validate_patch</c>'s multi-item fields (<c>items</c>, <c>results</c>,
    /// <c>detectedChanges</c>, <c>diagnostics.rootCauses</c>) are plain arrays of objects again — one
    /// object per item, field names repeated per item, no <c>columns</c>/<c>rows</c> indirection and no
    /// field hoisting into a <c>rest</c>/shared column set. BREAKING for a <c>compact</c>/<c>json</c>
    /// consumer that read <c>.columns</c>/<c>.rows</c>: it now reads <c>items[i].fieldName</c> directly.
    /// introduced across 3.4–3.8) are removed entirely. <c>search_index</c>'s/<c>get_references</c>'/
    /// <c>get_symbol</c> batch's/<c>validate_patch</c>'s multi-item fields (<c>items</c>, <c>results</c>,
    /// <c>detectedChanges</c>, <c>diagnostics.rootCauses</c>) are plain arrays of objects again — one
    /// object per item, field names repeated per item, no <c>columns</c>/<c>rows</c> indirection and no
    /// field hoisting into a <c>rest</c>/shared column set. BREAKING for a <c>compact</c>/<c>json</c>
    /// consumer that read <c>.columns</c>/<c>.rows</c>: it now reads <c>items[i].fieldName</c> directly.
    /// Reversed because TOON (3.9) already tabulates a uniform array of plain objects natively — encoding
    /// our own pre-hoisted <c>{columns,rows}</c> wrapper through TOON cost MORE tokens than handing TOON
    /// the plain array and letting its own uniform-array detection do the job, since the wrapper's
    /// <c>columns:</c>/<c>rows:</c>/per-row <c>- [N]:</c> markers are pure overhead TOON's native encoding
    /// does not need. <c>compact</c>/<c>json</c> lose the old column-name deduplication as a result —
    /// accepted, since TOON is the token-optimized path now and JSON is the fallback/debugging one.
    /// Also: <c>OutputFormat</c> is no longer read from <c>ToolkitConfig.DefaultFormat</c> on every call.
    /// <c>Formats.Current</c> is a runtime-mutable static, seeded once at startup from
    /// <c>defaultFormat</c>, changeable for the rest of the session via the new <c>set_output_format</c>
    /// tool. A field's absence now uniformly means "does not apply" in every format, including nested
    /// objects inside a plain array (e.g. an item's <c>isTest</c>/<c>content</c>) — previously only true
    /// for top-level envelope fields; a JSON consumer must check key presence, not compare against
    /// <c>null</c>.
    /// </description></item>
    /// <item><description>
    /// <b>3.9</b> — every tool response is now rendered through one of three <c>OutputFormat</c>s
    /// instead of always being plain JSON: <c>toon</c> (TOON — Token-Oriented Object Notation, the new
    /// default), <c>compact</c> (the exact minified JSON every tool already produced through 3.8), or
    /// <c>json</c> (the same data, pretty-printed for a human reader). Selected per repo via
    /// <c>defaultFormat</c> in <c>.claude/dotnet-toolkit/config.json</c> — not a per-call argument. This
    /// is additive to the response CONTRACT documented by every other entry in this list: the JSON shape
    /// versioned here (the field names, the <see cref="Output.CompactTable"/>/<see cref="Output.JsonHoist"/>
    /// layouts) is unchanged in all three formats — only how that same structure is spelled on the wire
    /// changes. A consumer parsing every response as JSON breaks under the new default; set
    /// <c>defaultFormat: "compact"</c> to keep the pre-3.9 wire format exactly.
    /// </description></item>
    /// <item><description>
    /// <b>3.8</b> — validate_patch's <c>detectedChanges</c> and <c>diagnostics.rootCauses</c> become
    /// <see cref="Output.CompactTable"/>s, the same treatment as search_index/get_references/get_symbol's
    /// batch mode/search_log — the last multi-item response still using a plain array. BREAKING: a
    /// consumer reading <c>detectedChanges[i].symbolId</c> sees nothing; it now reads
    /// <c>detectedChanges.columns.IndexOf("symbolId")</c> and <c>detectedChanges.rows[i][that]</c>, same
    /// for <c>rootCauses</c>. <c>rootCauses</c>' <c>suggestedInspection</c> stays a nested array of
    /// <c>{symbolId, displayString}</c> objects inside its row, not hoisted further — the same pattern
    /// get_references already uses for <c>sites</c>.
    /// </description></item>
    /// <item><description>
    /// <b>3.7</b> — get_symbol drops <c>resolution</c> and <c>exclude</c>. <c>include</c> is now the sole
    /// selector, with three forms: omitted/<c>"standard"</c> (default) for <c>xmlDoc, referenceCounts,
    /// recentLog</c> — the set meaningful on nearly every call; <c>"all"</c> for every component
    /// (<c>source, xmlDoc, mechanicalFacts, referenceCounts, recentLog, members</c>); or a comma list of
    /// component names, which now REPLACES the default set rather than adding to it — a literal query of
    /// exactly the columns wanted. BREAKING: <c>resolution:"full"</c> becomes <c>include:"all"</c>;
    /// <c>resolution:"full", exclude:"source"</c> becomes spelling out the remaining names explicitly,
    /// e.g. <c>include:"xmlDoc,mechanicalFacts,referenceCounts,recentLog"</c>; a bare
    /// <c>include:"members"</c> that previously ADDED members to the signature default now returns ONLY
    /// members (plus the unconditional skeleton) — callers relying on the old additive behavior must
    /// list every component they still want.
    /// </description></item>
    /// <item><description>
    /// <b>3.6</b> — get_references' <c>items</c> becomes a <see cref="Output.CompactTable"/> combined with
    /// <see cref="Output.JsonHoist"/>, the same treatment as get_symbol's batch <c>results</c>: whatever
    /// fields this call's actual items share (typically <c>symbolId, contentVersion, displayString, sites,
    /// dispatchKind</c>) become their own columns, and a trailing <c>rest</c> column carries whatever was
    /// not common — <c>isTest</c> and <c>content</c> (the inline body, only present with
    /// <c>includeBodies:true</c>) are the fields most likely to end up there, since neither is present on
    /// every caller. BREAKING: a consumer reading <c>items[i].displayString</c> sees nothing; it now reads
    /// <c>items.columns.IndexOf("displayString")</c> and <c>items.rows[i][that]</c>, or checks <c>rest</c>
    /// for a field that did not make it into <c>columns</c> this call.
    /// </description></item>
    /// <item><description>
    /// <b>3.5</b> — get_symbol gained <c>symbols</c>, an alternative to <c>symbol</c> that fetches several
    /// symbols in one call under one resolution/include/exclude. Additive: single-symbol calls are
    /// untouched, byte-for-byte the same envelope as before. A batch response's <c>results</c> is a
    /// <see cref="Output.CompactTable"/> whose fixed columns are <c>symbolId, contentVersion, limitedBy,
    /// error</c> — that outer envelope IS uniform across every entry, on success or failure alike — plus
    /// whatever <see cref="Output.JsonHoist"/> finds common to every requested symbol's own content THIS
    /// call (e.g. <c>kind, displayString, accessibility</c> when every symbol has them, sometimes more:
    /// <c>xmlDoc</c> joins them when every requested symbol happens to have a doc comment, and drops back
    /// out the moment one does not), and finally <c>content</c> holding whatever was not common. This set
    /// is genuinely call-dependent, not a fixed contract — which is why it is always reported in
    /// <c>columns</c> rather than needing to be memorized. A row whose symbol did not resolve has
    /// <c>symbolId</c>/<c>contentVersion</c> null, <c>error</c> holding the short error string, and
    /// <c>content</c> holding that error's own envelope (its <c>detail</c> or <c>candidates</c>) rather
    /// than dropping it; such a row shares no keys with a real symbol's content, so it never contributes
    /// to what gets hoisted. <c>knownVersion</c> and <c>refetch</c> do not apply to a batch; each result
    /// always carries full content, because leasing needs one token per symbol and a single
    /// <c>knownVersion</c> cannot express that.
    /// </description></item>
    /// <item><description>
    /// <b>3.4</b> — search_index's <c>items</c> becomes a <see cref="Output.CompactTable"/>:
    /// <c>{"columns":[...],"rows":[[...],...]}</c> instead of one object per hit. BREAKING: a consumer
    /// reading <c>items[i].name</c> sees nothing; it now reads <c>items.columns.IndexOf("name")</c> and
    /// <c>items.rows[i][that]</c>. Still plain JSON arrays, so no delimiter or escaping rule was
    /// introduced — the saving is only that column names are no longer repeated once per row.
    /// </description></item>
    /// <item><description>
    /// <b>3.3</b> — search_index hits carry <c>file</c> and <c>line</c>. Additive, and both are absent
    /// when the name maps to several declarations: the syntax index they are resolved from keys members
    /// without parameter lists, so overloads cannot be separated, and absent is what a caller already
    /// handled. Resolved per response rather than stored beside the symbol row, because a stored line is
    /// invalidated by that symbol's own hashes and an edit *above* a declaration moves its line without
    /// touching one of them.
    /// </description></item>
    /// <item><description>
    /// <b>3.2</b> — <c>limitedBy</c> gained the value <c>stale</c>: the files an answer was served from
    /// have moved on disk since the workspace read them. The previous markers described the tier, which
    /// a loaded, undegraded workspace holding a file that changed underneath it satisfies while still
    /// serving content that no longer exists.
    /// </description></item>
    /// <item><description>
    /// <b>3.1</b> — validate_patch gained the <c>stale_workspace</c> error. Additive: it occupies a case
    /// that previously produced a silent, successful, wrong apply. An apply writes the whole document
    /// text, so a patch built on a workspace copy behind disk reverted everything else in that file;
    /// <c>baseVersions</c> never covered it, guarding only the symbols detected as changed.
    /// </description></item>
    /// <item><description>
    /// <b>3.0</b> — the per-response tier marker is renamed <c>staleness</c> to <c>limitedBy</c>.
    /// BREAKING: a consumer reading <c>staleness</c> sees nothing. The old name claimed the field was
    /// about content freshness, which it never was — freshness is mtime-polled before every query.
    /// It names what the answer could not draw on: absent (nothing), <c>index_only</c> (the semantic
    /// tier was unavailable) or <c>degraded</c> (the workspace loaded with failed projects, so results
    /// may be wrong rather than merely thin). The values are unchanged; only the key moved.
    /// </description></item>
    /// <item><description>
    /// <b>2.2</b> — added the <c>degraded</c> value. That state was previously indistinguishable from
    /// a healthy one, because the marker asked only whether the index had finished a pass, not whether
    /// the model it indexed was sound.
    /// </description></item>
    /// <item><description>
    /// <b>2.1</b> — get_symbol gained include/exclude, and a response serving a narrowed component set
    /// returns a token narrowed to the layers it actually served. The lease additionally requires the
    /// held token to cover the layers a request needs, so escalating resolution against a narrower
    /// token returns content instead of a false changed:false.
    /// </description></item>
    /// </list>
    /// </summary>
    public const string Id = "ctx-contract/3.15";
}
