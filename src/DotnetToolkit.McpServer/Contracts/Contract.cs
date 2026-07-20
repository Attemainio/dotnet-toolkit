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
    public const string Id = "ctx-contract/3.9";
}
