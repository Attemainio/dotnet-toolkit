# Changelog

Each entry is what a consumer sees change: tools, response contract, hooks, skills, install. The
response contract version (`ctx-contract/<n>`, reported by `ping`) is listed where it moved.

## 2.0.0 — 2026-09-20

**dotnet-toolkit is now navigation-only.** It locates symbols, references, implementations and call
paths; `Read` fetches the text, `Edit`/`Write` change it, `dotnet build` is the gate. The one tool-made
edit that remains is `rename_symbol`, because a solution-wide semantic rename is the one edit a string
replacement cannot make safely. The full reasoning — what the 1.x write path was measured to do on a
real repo, and the principles the cut rests on — is in
[docs/design/navigation-only.md](docs/design/navigation-only.md).

### Removed

- **`validate_patch`** and everything that existed only for it: drafts, `baseVersion` leases, the
  compile ladder, analyzer runs, the two write guards (`guard-cs-edit`, `guard-cs-bash-write`), and
  the patch-specific bullets of the write-time checklist. Measured on FluidScript: fewer than half of
  382 calls did what they were sent to do; 33 % were refused pre-flight by the lease protocol working
  as designed; line-anchored ranges failed at the closing brace in a way no fix short of string
  anchoring removes — and `Edit` already is string-anchored.
- **Source text from every tool.** `get_symbol`'s `source` component, its `:code`/`@lines` grammar and
  `contentVersion`; `get_references`' `includeBodies`; `search_index`'s `read` column and `intent`.
  `get_symbol` is a locator: `declarationSites` rows carry `file`, `signatureLine`, `startLine` (doc
  comment included) and `endLine`, one per part of a partial, and a ranged `Read` of that span is the
  text.
- **The development log**: `search_log`, `recentLog`, `feature_log`. Nothing writes it any more.
- **`dotnet-guard-redteam`** and `dotnet-guard-audit`: there is no enforcement left to bypass.

### Added

- **The index follows every edit.** A `PostToolUse` hook (`refresh-cs-files`) sends the files an
  `Edit`, `Write`, `NotebookEdit`, `apply_patch` or `Bash` call wrote to the running server, which
  re-indexes just those documents; the mtime sweep catches the rest. `search_index`, `sym_` ids,
  `get_references` and the hierarchies see the change before the next call. Previously only a full
  rebuild (a patch apply, `reload_workspace`, or a restart) updated them.
- **`get_references` names dispatch.** Rows a call reaches only through an interface or base member
  are marked `indirect: true` and carry `via` (the member the call binds to); the envelope splits
  `directItems`/`indirectItems` and explains once in `dispatchNote`.
- **`search_index(query: "IFoo", implements: "IFoo")` lists `IFoo`'s direct implementers** — the
  obvious call for "who implements this" used to return nothing, since ranking the interface's own
  name never scores a type that implements it. An empty page under `implements:` always carries
  `implementsHint` saying which zero it is.
- **`get_project_graph` separates declared from inherited edges**: `.csproj` references in
  `references`/`referencedBy`, SDK-inherited ones in `transitiveReferences`/`transitivelyReferencedBy`.
- **`entryPointHint` on a routed action** (`[HttpGet]`, `[HttpPost]`, `[Route]`, …) says its callers
  are HTTP clients outside the compilation and to grep the route template over the client code before
  treating it as unused.
- **The read advisory reaches the model, and states its cost.** A whole-file `Read`/`cat` of a
  compiled `.cs` file now draws "this read injects all N lines (about T tokens); a ranged Read of one
  declaration's span returns only those lines", plus a running per-session tally. Before 2.0.0 the
  note went to the user's transcript only. Ranged reads, `grep`, and write-shaped commands (`sed -i`)
  draw nothing.
- **`get_retrieval_metrics` reports `wholeFileCsReads`** per agent and per agent/tool, from the same
  predicate the advisory uses.
- `search_index` accepts list-shaped filters (`kinds: ["method","property"]`), explains a zero-hit
  query (names and doc summaries only — literals and comments are a `Grep`), and `get_symbol` on a
  single symbol always carries `xmlDoc`.
- `get_call_hierarchy`'s description steers "how does A reach B" to `get_call_slice` (measured: 18k
  tokens against a few hundred).

### Changed

- **`dotnet-performance` runs without switching guard state.** Claude Code's auto-mode classifier
  denies `set_hook_guards` — and any agent prompt mentioning suspended or blocked guards — as
  self-modification, which stopped every auto-mode run at pass 2. The three probes now run at the
  default state and the meter's `wholeFileCsReads` is the discipline check; suspend/block survives
  only as an opt-in strict variant outside auto mode. Both 2026-09-20 runs completed in auto mode.
- **`dotnet-init` adds only read-only tools to a repo's allowlist** (15 entries); `rename_symbol`
  and `set_hook_guards` are never allowlisted.
- Old quarantined cache files are capped at the newest two.
- Response contract **`ctx-contract/4.0` → `4.2`**. 4.0: the removals above. 4.1: `signatureLine`
  always present and first after `file`; `via`/`dispatchNote`; single-symbol `xmlDoc`;
  `get_project_graph` edge split. 4.2: the `implements` listing shape and `implementsHint` cases;
  `wholeFileCsReads`.

### Measured

First runs on the navigation-only surface (both tool families available, the shipped default):
FluidScript (316 files, 649 types) 1.53× and PandaAI (329 files, 551 types) 1.77× less context
injected than the raw route, 19/19 scored questions correct against the raw route's 16/19, and the
MCP route reached for on every question without a nudge. Full tables, per-question detail and where
the raw route won: [docs/benchmarks.md](docs/benchmarks.md).

## 1.0.1 — 2026-08-23

- Read guards became **advisory**: a whole-file `.cs` read is allowed with a note, never denied.
- `guard-cs-bash-write` added for write-shaped shell commands on compiled files (removed in 2.0.0).
- Codex support on Windows and WSL2 (2026-09-07).

## 1.0.0 — 2026-08-19

Initial marketplace submission: Roslyn MCP server with symbol search, references, call and type
hierarchies, semantic diff, `validate_patch` write path with compile ladder, seven hooks, and the
`dotnet-read`/`dotnet-write`/`dotnet-explore`/`dotnet-review` skills.
