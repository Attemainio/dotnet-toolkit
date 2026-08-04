# Verifying an installed dotnet-toolkit

The detail behind `dotnet-toolkit-init`'s Step 8. The skill carries the staleness decision table —
the part that decides what gets rewritten; this file carries the checklists it runs afterwards. Read
it when that step fires.

Report every check as checked-and-clean rather than omitting it. A silent check reads as a skipped one.

## Is it installed correctly?

```bash
ls .claude/rules/ .claude/dotnet-toolkit/ 2>/dev/null
```

- Every file in init's "What gets written" table is present.
- Both always-loaded rules have **no `paths:` frontmatter**. With one they would almost never load:
  path-scoped rules fire only when the built-in `Read` touches a matching file, and `.cs` contact in
  a repo with this plugin goes through the MCP tools or is blocked by the read/edit guards.
- The standards copies **do** carry `paths: ["**/*.cs"]`, which is what keeps them out of the launch
  context; they are read on demand through `csharp-standards.md`'s index.
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
for f in .claude/rules/tool-protocol.md .claude/rules/csharp-standards.md CLAUDE.md; do
    [ -f "$f" ] && printf "%-46s %6d B  ~%.1fk tok\n" "$f" $(wc -c < "$f") $(echo "$(wc -c < "$f")/3800" | bc -l)
done
```

Budget: **≤ 6 KB each** for the two always-loaded rules.

**Never fix an overage by deleting guidance** — move it behind a pointer, or the consumer simply
loses the rule. And an overage is a finding against the *plugin*, not this repo: the copies are
verbatim, so report it and say `dotnet-toolkit-consistency` owns the fix upstream.

The consumer's `CLAUDE.md` must contain no dotnet-toolkit content, and specifically no
`<!-- dotnet-toolkit:start -->`/`<!-- dotnet-toolkit:end -->` marker block — an artifact from before
init stopped writing there. Propose removing it alongside the refresh; the rule files alone are
sufficient.

## Would uninstalling leave anything?

Take init's "Undoing this later" list literally as a dry run, and ask what would remain.

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
