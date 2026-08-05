# Verifying an installed dotnet-toolkit

The verify-and-refresh path of `dotnet-toolkit-init`. The skill carries the staleness decision table —
the part that decides what gets rewritten; this file carries the checklists it runs afterwards. Read
it when that step fires.

Report every check as checked-and-clean rather than omitting it. A silent check reads as a skipped one.

## Is it installed correctly?

```bash
ls .claude/rules/ .claude/dotnet-toolkit/ 2>/dev/null
```

- Every file in `docs/install/install.md`'s "What gets written" table is present — all three of them.
- **`.claude/rules/` contains exactly one file, `index.md`, and it has no frontmatter.** A second
  unfrontmattered file is a second always-loaded rule nobody costed. Any of `tool-protocol.md`,
  `csharp-standards.md`, or a standards filename still sitting there is a **pre-migration leftover**:
  it auto-loads, contradicts `index.md`, and in the standards' case keeps a `paths:` trigger that can
  fire on any `.cs` file no project compiles. Report it and re-run the install path, which cleans it.
- **No standards files are installed, and that is correct.** They are read from
  `<pluginRoot>/standards/` on demand — `workspace_status` reports `pluginRoot` — so they cannot go
  stale here. Their absence is not a
  broken install — it is the design. There is no per-repo override tier, so a
  `.claude/dotnet-toolkit/standards/` directory left over from an older install is **dead text**
  nothing reads: report it for deletion.
- `.claude/settings.json`'s `permissions.allow` covers the read-only tools and **not**
  `validate_patch`/`rename_symbol` — writers must keep prompting. Their presence is a finding.
- The plugin is actually connected: `workspace_status` answers, and the solution it resolved is the
  one the repo expects. A rule installed against a plugin that is not loaded is worse than no rule —
  it tells the session to call tools that will not answer.
- If `.claude/dotnet-toolkit/config.json` exists, its `solution`/`excludeGlobs`/`defaultFormat` still
  describe this repo.

## Is the always-loaded footprint within budget?

This is the cost every session in the repo pays regardless of task, so it has a hard number.

```bash
for f in .claude/rules/index.md CLAUDE.md; do
    [ -f "$f" ] && printf "%-46s %6d B  ~%.1fk tok\n" "$f" $(wc -c < "$f") $(echo "$(wc -c < "$f")/3800" | bc -l)
done
```

Budget: **≤ 6 KB** for `index.md`, the one always-loaded rule this plugin installs. Note this cost is
paid again by **every subagent** — the harness injects always-loaded rules into them with no opt-out,
so a parallel review multiplies it by the number of instances.

**Never fix an overage by deleting guidance** — move it behind a pointer, or the consumer simply
loses the rule. And an overage is a finding against the *plugin*, not this repo: the copies are
verbatim, so report it and say `dotnet-toolkit-consistency` owns the fix upstream.

The consumer's `CLAUDE.md` must contain no dotnet-toolkit content, and specifically no
`<!-- dotnet-toolkit:start -->`/`<!-- dotnet-toolkit:end -->` marker block — an artifact from before
init stopped writing there. Propose removing it alongside the refresh; the rule files alone are
sufficient.

## Would uninstalling leave anything?

Take `docs/install/uninstall.md`'s "Delete these" list literally as a dry run, and ask what would
remain.

- Every copied file deleted or restored from `.claude/dotnet-toolkit/backups/`.
- The `mcp__plugin_dotnet-toolkit_dotnet__*` entries gone from `.claude/settings.json`, with the rest
  of that file untouched.
- `.claude/dotnet-toolkit/install.json` deleted.
- Runtime paths explicitly dispositioned. `cache/` is rebuildable and self-gitignored, so "safe to
  leave, delete for a clean removal" is a fine disposition — an *unmentioned* directory is not. The
  backups directory survives deliberately; the report says where it is.
- Nothing left instructing a session to call MCP tools that are gone: no remaining file naming
  `search_index`, `get_symbol`, or `validate_patch`. The repo's own `CLAUDE.md` may name them — that
  is the repo's own text, which is fine, but the report must say so rather than leave a dangling
  instruction unexplained.
