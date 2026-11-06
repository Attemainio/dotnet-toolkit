# The official Claude documentation this plugin is built against

```
last-verified:  2026-10-18
refresh-window: 30 days
```

Read by `SKILL.md` Step 1. This file is the **external anchor**: `harness-compliance.md` states
thresholds, and every one of them cites a row here. Without it, "what good looks like" is folklore
carried in our own prose, and our own prose is exactly what the audit is supposed to be checking.

Two roots, both maintained by Anthropic:

- **Platform / API** — <https://platform.claude.com/docs/en/home>
- **Claude Code** — <https://code.claude.com/docs/en/overview>

Machine-readable indexes, useful when a path has moved: `https://code.claude.com/docs/llms.txt` and
`https://platform.claude.com/llms.txt`. Appending `.md` to a docs URL returns the raw Markdown, which
is what `WebFetch` should ask for.

## Refresh procedure — the "occasionally"

Do **not** hit the network on every audit run. Refresh when either is true:

- `last-verified` above is older than `refresh-window`, **or**
- the user asks for it ("refresh the Claude docs", "check the guideline links", "did the guidance
  change").

Then, for every URL in the table: `WebFetch` it, record **resolves / moved / 404**, and diff its
shortlist against what is written here.

- **A dead or moved link is a finding in this file** — fix the URL, don't delete the row.
- **A changed bullet is a finding against the plugin.** Update the bullet here, then re-run the
  `harness-compliance.md` sections that cite it. A guidance change is the one thing that can
  invalidate a previously-clean audit, which is why this step comes first rather than last.
- Bump `last-verified` **only after the whole sweep**, never per-URL — a partial date makes the next
  run skip the URLs that were never checked.

## The URL table

| Topic | URL |
| --- | --- |
| Tool search (deferred tool loading) | `https://code.claude.com/docs/en/agent-sdk/tool-search.md` |
| Tool search tool (API) | `https://platform.claude.com/docs/en/agents-and-tools/tool-use/tool-search-tool.md` |
| Tool use overview | `https://platform.claude.com/docs/en/agents-and-tools/tool-use/overview.md` |
| Defining tools | `https://platform.claude.com/docs/en/agents-and-tools/tool-use/define-tools.md` |
| Skills authoring | `https://code.claude.com/docs/en/skills.md` |
| Subagents | `https://code.claude.com/docs/en/sub-agents.md` |
| CLAUDE.md and memory | `https://code.claude.com/docs/en/memory.md` |
| Context window | `https://code.claude.com/docs/en/context-window.md` |
| Hooks reference | `https://code.claude.com/docs/en/hooks.md` |
| Hooks guide | `https://code.claude.com/docs/en/hooks-guide.md` |
| MCP in Claude Code | `https://code.claude.com/docs/en/mcp.md` |
| Plugins | `https://code.claude.com/docs/en/plugins/overview.md` |
| Plugin manifest reference | `https://code.claude.com/docs/en/plugins/manifest-reference.md` |
| Plugin components (skills, agents, hooks in a plugin) | `https://code.claude.com/docs/en/plugins/components.md` |
| Settings | `https://code.claude.com/docs/en/settings.md` |
| Prompt caching | `https://code.claude.com/docs/en/prompt-caching.md` |

All resolved on `last-verified`. On 2026-10-18 the two plugin pages had moved (the old
`plugins.md` / `plugins-reference.md` paths still answered with identical content but were no longer
in `llms.txt`), and the API tool-search page and the plugin components page were added.

## Shortlists — what each one mandates that we must remember

### Tool search — deferred tool loading

- **Names, descriptions, argument names and argument descriptions are the searchable index** (the API
  tool-search page; the Claude Code page says "tool names and descriptions"). Nothing else about a
  tool is visible until it is fetched.
- **Load just in time.** Defer specialist tools; keep only what a caller needs unconditionally
  always-available. An MCP server's `alwaysLoad`, or a tool's `_meta` `"anthropic/alwaysLoad"`, exempts
  it from deferral; with fewer than ~10 small tools, loading everything upfront is typically faster.
- **A search returns at most ~5 matches.** A tool that ranks sixth for its own intent is unreachable
  in practice — this is the mechanism behind our router rule, not a theoretical concern.
- **Design for discovery from natural-language intent**, not from the exact technical name.
- On by default for Opus 4 / Sonnet 4 / Haiku 4.5 and later (4.5 and later on Google Cloud's Agent
  Platform). `ENABLE_TOOL_SEARCH` = `true` / `false` / `auto` (activates when definitions reach 10% of
  the window) / `auto:N`.
- Catalogue cap is 10,000 tools. We ship 17 — the cap is not our constraint; **ranking** is.
- Cost model: one extra round-trip the first time a tool is discovered, repaid by a smaller context on
  every turn. A fetched tool stays available until compaction.

### Tool use overview / defining tools

- `name` must match `^[a-zA-Z0-9_-]{1,128}$`.
- `description` is **required, plaintext, and detailed** — "at least 3–4 sentences". **No maximum on
  the API**; Claude Code's own cut is under *MCP* below.
- Say what the tool does, **when it should and should not be used**, what the parameters mean, and the
  caveats.
- **Return high-signal responses.** Trim bloat; use semantic ids rather than opaque internal handles.
- Prefer **consolidating related operations** into one tool with a selector parameter over
  near-duplicate tools that compete in search.
- Prefix names by service when a catalogue spans services — the harness does this for us via
  `mcp__plugin_dotnet-toolkit_dotnet__`.
- `input_examples` is optional and costs roughly 20–50 tokens for simple, 100–200 for nested objects.

### Skills authoring

- A skill is `<dir>/SKILL.md` with YAML frontmatter. **All fields are optional**: `name` defaults to
  the directory name (set it in a plugin, or a marketplace install names the skill after its cache
  directory); `description` is recommended and **decides whether the skill is invoked**.
- **`description` + `when_to_use` are cut at 1,536 characters** in the skill listing. The listing's
  budget is 1% of the context window; on overflow the least-invoked skills lose their descriptions
  first.
- Optional frontmatter: `when_to_use`, `argument-hint`, `arguments`, `disable-model-invocation`
  (removes the skill from Claude's context entirely), `user-invocable`, `allowed-tools`
  (pre-approves for one turn; does **not** restrict), `disallowed-tools`, `model`, `effort`,
  `context: fork` with `agent` and `background` (**a skill can launch a subagent itself**), `hooks`,
  `paths`, `shell`, `metadata`, `license`, `compatibility`. Unknown fields are ignored silently.
- **Bodies load on invocation and then stay in context on every later turn** — a recurring cost. The
  official tip is **`SKILL.md` under 500 lines**; after compaction only the first 5,000 tokens of each
  invoked skill return (25,000 in total), so critical instructions go first.
- Supporting files may sit alongside `SKILL.md` and be read when needed — the mechanism this file
  uses, and the remedy for a long skill.
- `$ARGUMENTS` captures user input; `@path` references a file; `` !`cmd` `` substitutes a command.

### Subagents

- `agents/<name>.md` with frontmatter: `name` (≤ 256 characters, no `:`) and `description` required;
  `tools`, `disallowedTools`, `model`, `effort`, `maxTurns`, `autoCompactWindow`, `skills`, `memory`,
  `background`, `omitClaudeMd`, `isolation`, `color`, `experimental.cacheTtl` optional. **Plugin
  agents ignore `permissionMode`, `hooks`, `mcpServers` and `initialPrompt`.** A subfolder joins the
  name (`agents/review/x.md` → `plugin:review:x`). Unknown fields are ignored silently.
- **The `description` is always in context; the body is not.** Selection is description-driven.
  Combined descriptions over 15,000 tokens draw a startup warning.
- Omitting `tools` inherits every tool available to subagents. Background subagents (the default) keep
  every MCP tool but only a fixed built-in set (`Read`, `Grep`, `Glob`, `Bash`, `Edit`, `Write`,
  `Skill`, `ToolSearch`, …). MCP wildcards work. A `tools` list in which nothing resolves usually fails
  the launch.
- A subagent receives its definition, the task, the `CLAUDE.md` hierarchy and rules, a git status
  snapshot and any preloaded `skills`. **There is an opt-out now**: `omitClaudeMd: true` drops user,
  project and local `CLAUDE.md` (managed policy still loads); Explore and Plan skip them too.
- `memory: user | project | local` gives a persistent directory, injects the first 200 lines / 25 KB of
  its `MEMORY.md`, and **automatically enables `Read`, `Write` and `Edit`**. It does nothing when auto
  memory is off.
- Subagents can spawn subagents, up to three layers below the main conversation, unless `Agent` is
  withheld from `tools`.

### CLAUDE.md and memory

- Load order, broadest first, **concatenated rather than overriding**: managed policy → user
  `~/.claude/CLAUDE.md` → project `CLAUDE.md` → `CLAUDE.local.md`; closer directories are read later.
- **Target under 200 lines per always-loaded file.** A startup warning fires per oversized file and for
  a combined total across `CLAUDE.md`, rules and imports; a file over 4 MiB is skipped.
- Auto-memory `MEMORY.md` is read only to **200 lines or 25 KB, whichever comes first**.
- `@path` imports resolve relative to the importing file, max 4 hops; backticked `` `@path` `` stays
  literal. Block-level HTML comments are stripped before injection.
- `.claude/rules/*.md`: **`paths` is the only field read**; a rule without `paths` (or with
  unparseable frontmatter) loads at launch with the priority of `.claude/CLAUDE.md`.

### Context window

- Load order: system prompt and tool definitions → project context (`CLAUDE.md`, rules, auto-memory)
  → conversation.
- **Tool definitions load upfront unless tool search is on**, in which case only names (and MCP server
  instructions) are present until a fetch. Everything in `harness-compliance.md` §C and §F follows.
- `paths:`-scoped rules inject when a matching file is read and do not re-inject after compaction
  until re-triggered. An unconditional rule is reloaded from disk. The skill listing is not
  re-injected after `/compact`; invoked skill bodies come back capped (see *Skills*).

### Hooks

- Configured in `hooks/hooks.json` for a plugin. **Matcher**: only letters, digits, `_`, `-`, spaces,
  `,` and `|` → an exact name or list; anything else → an unanchored JavaScript regex; `"*"`, `""` or
  omitted → match all. A partial MCP name like `mcp__memory` matches nothing; plugin MCP tools are
  `mcp__plugin_<plugin>_<server>__<tool>`.
- Types include `command`, `http`, `mcp_tool`, `prompt`, `agent`.
- **Output cap is 10,000 characters** per `additionalContext`, `systemMessage` or plain stdout; over
  it, the output goes to a file with a 2,000-character preview Claude is not asked to read. Exit 0 =
  success, exit 2 = blocking (stderr to the model), other = non-blocking error — unless stdout is valid
  JSON, in which case the fields decide.
- `PreToolUse` decides via `permissionDecision`: `allow` / `deny` / `ask` / `defer` (precedence
  deny > defer > ask > allow). The reason reaches **Claude on `deny`**, the user on `ask`, and **only
  the debug log on `allow`/`defer`** — text the model must read on an allowed call goes in
  `hookSpecificOutput.additionalContext` (our read guards and checklist hint). `allow` skips the
  prompt but never overrides deny/ask rules; never emit it from a hook that only advises.
- Default timeout 600 s for `command`; our `hooks.json` pins 10 s, the right instinct on a hot path.
- Hooks are **deterministic**; `CLAUDE.md` is advisory. A `deny` blocks even in `bypassPermissions`.

### MCP in Claude Code

- Project servers live in `.mcp.json`; tools are `mcp__<server>__<tool>` (plugin:
  `mcp__plugin_<plugin>_<server>__<tool>`), and a full name over **128 characters** is left out of the
  tool list.
- **Descriptions and server instructions are cut at 4,096 characters for a tool loaded upfront and
  16,384 for one loaded through tool search, and search reads only the first 4,096** — put the words a
  caller would search for first. (Before v2.1.296 the cut was 2,048.)
  `CLAUDE_CODE_MAX_MCP_DESCRIPTION_LENGTH` overrides both.
- Output: a warning above 10,000 tokens, a default cap of 25,000 (`MAX_MCP_OUTPUT_TOKENS`), text over
  50,000 characters saved to a file.
- *(Unverified 2026-10-18 — `mcp.md` no longer states it; re-check on the next refresh)* stdio servers
  are spawned directly, not through a shell — the reason nothing we ship at runtime may be a
  `.sh`/`.ps1`/shebang entry point. The rule stands on Windows grounds regardless.

### Plugins / manifest reference / components

- `.claude-plugin/plugin.json` is optional; **`name` is the only required key** (non-empty, no
  spaces, `@`, `:` or path separators; kebab-case; `claude-` and `anthropic-` prefixes reserved).
  `description`, `version` and `author` are recommended — the validator warns without them.
- **Everything else sits at the plugin root**: `skills/`, `agents/`, `hooks/hooks.json`, `commands/`,
  `.mcp.json`, `.lsp.json`, `bin/`, `settings.json`. The `agents` / `commands` manifest keys replace
  the default scan; `skills` adds to it.
- **A `CLAUDE.md` at the plugin root is not loaded as context** — a plugin cannot ship always-loaded
  context; it must be installed into the repo (what `dotnet-init` does with the router).
- Skills namespace as `/plugin-name:skill-name`; agents as `plugin-name:agent-name`.
- `claude plugin validate <dir>` checks the layout locally; `/reload-plugins` picks up skills, agents
  and hooks, but holds back a reload that adds or removes a plugin MCP server until `--force`.

### Settings

- Precedence: managed → `--settings` → `.claude/settings.local.json` → `.claude/settings.json` →
  `~/.claude/settings.json`; lists merge across scopes.
- `permissions` and `hooks` reload live; `model`, `effortLevel` and `modelSettings` are read at
  startup.
- Relevant to us because `dotnet-init` merges the read-only MCP tools into the project allowlist —
  it must add, never clobber — and **a project `permissions.allow` applies only after the folder is
  trusted** (`deny`/`ask` apply immediately).

### Prompt caching

- The cached prefix is the system prompt plus project context. Editing `CLAUDE.md` mid-session does
  **not** take effect until `/clear`, `/compact`, or a restart.
- Connecting an MCP server invalidates the cache when its tools are in the prefix — with tool search
  on, Claude Code keeps the first request's tool list for the conversation, so our deferred surface is
  cheap to attach. Plugin skills, agents and hooks never invalidate the cache.
- TTL is 5 minutes by default; 1 hour only for the main conversation on a subscription within plan
  usage. **Subagents get 5 minutes** unless `experimental.cacheTtl` says otherwise.

## Facts cited by `harness-compliance.md`

Kept together so a refresh can check them in one pass.

| Fact | Value | Cited by |
| --- | --- | --- |
| Tool name regex | `^[a-zA-Z0-9_-]{1,128}$` | §C |
| MCP qualified tool name | ≤ 128 characters, else dropped | §C |
| Tool-search results per query | ≤ ~5 | §C |
| Tool catalogue cap | 10,000 | §C |
| Tool search default-on | Opus 4 / Sonnet 4 / Haiku 4.5+ (4.5+ on Agent Platform) | §C, §F |
| Tool description length | API: 3–4 sentences minimum, no maximum. Claude Code: cut at 4,096 (upfront) / 16,384 (deferred); search reads the first 4,096 | §B |
| Skill description cap | 1,536 characters (`description` + `when_to_use`) | §F |
| `SKILL.md` length | official tip: under 500 lines; first 5,000 tokens survive compaction | §D |
| Always-loaded file target | under 200 lines | §D |
| `MEMORY.md` read limit | 200 lines or 25 KB, whichever first | §D |
| Hook output cap | 10,000 characters | §G |
| `paths:` frontmatter | `.claude/rules/` and skill frontmatter | §G |
| Plugin asset location | plugin root, not `.claude-plugin/`; a root `CLAUDE.md` is not loaded | §G |
| Subagent always-loaded inheritance | inherited by default; opt-out via `omitClaudeMd` (managed policy still loads) | §D, §F |
| `memory:` on an agent | grants `Read`, `Write`, `Edit` automatically | §G |
