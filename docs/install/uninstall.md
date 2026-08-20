# Removing dotnet-toolkit from a repo

The procedure behind `dotnet-init`'s uninstall path. Also read as a **dry run** during verify
(`docs/install/verify.md`) — "what would a clean removal touch, and what would it leave?"

Almost nothing needs removing, and that is the design: only what init actually wrote into the repo is
repo-local. Everything else leaves with the plugin.

## Delete these

- `.claude/rules/dotnet-index.md`, **and `.claude/rules/index.md`** — the same rule under the name it
  carried before the rename. A repo that installed once, refreshed, and never re-ran init can still
  have the old one; deleting only the new name leaves an always-loaded rule pointing at a plugin that
  is gone.
- **Legacy, from an install predating the standards move** — delete these too if present, since
  nothing else will: `.claude/rules/tool-protocol.md`, `.claude/rules/csharp-standards.md`, and any of
  `naming.md`, `styling.md`, `best-practices.md`, `antipatterns.md`, `architecture.md`,
  `api-design.md`, `error-handling.md`, `resource-management.md`, `performance.md`, `concurrency.md`,
  `security.md`, `testing.md`, `xml-documentation.md` under `.claude/rules/`. Check the manifest and
  the backups before deleting one the repo may have edited.
- The `mcp__plugin_dotnet-toolkit_dotnet__*` entries in `.claude/settings.json`'s `permissions.allow`
  — **leaving the rest of that file untouched**. It is the repo's own file; init only merged into it.
- `.claude/dotnet-toolkit/install.json`.
- `.claude/dotnet-toolkit/.gitignore` — but only together with the run output it covers. Deleting it
  while `review/`, `eval/`, `perf/` or `backups/` still hold files un-ignores all of them at once,
  and the next `git add -A` commits a pile of stale reports. Delete those directories first, or
  leave the file.

That is the complete list of what init ever writes outside `.claude/dotnet-toolkit/`. Any of it can
be restored instead from `.claude/dotnet-toolkit/backups/` if the repo had its own version before.

## Disposition these, don't guess

Runtime paths are not deleted silently. Each gets a stated disposition, even when the disposition is
"safe to leave" — an *unmentioned* directory is the bug this rule prevents.

- `.claude/dotnet-toolkit/cache/` — the server's rebuildable SQLite store, self-gitignored. Safe to
  leave; delete the directory for a fully clean removal.
- `.claude/dotnet-toolkit/config.json` — if the repo wrote one, it is the repo's own. Leave it.
- `.claude/dotnet-toolkit/backups/` — kept deliberately. Say where it is when reporting.

## Leaves with the plugin — nothing repo-local

The MCP server, the hooks, the skills, the agents, the `standards/` files, and every `docs/` file
those skills open are gone the moment the plugin is uninstalled. No repo-local cleanup, and nothing left instructing a session to
call tools that no longer exist.

**If an old `CLAUDE.md` marker block exists** from a prior version of this skill, delete everything
from `<!-- dotnet-toolkit:start -->` to `<!-- dotnet-toolkit:end -->` inclusive, or restore the newest
backup over `CLAUDE.md`. Current versions never write there, so on a fresh install there is nothing to
check.

## Confirm, don't assert

After the deletions, nothing remaining in the repo should name `search_index`, `get_symbol`, or
`validate_patch` — except whatever the repo wrote itself, which is theirs to keep. Check rather than
claim it, and if the repo's own `CLAUDE.md` names the tools, say so explicitly rather than leaving a
dangling instruction unexplained.
