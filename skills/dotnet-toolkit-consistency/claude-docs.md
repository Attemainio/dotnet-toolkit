# The official Claude documentation this plugin is built against

```
last-verified:  2026-08-06
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
| Tool use overview | `https://platform.claude.com/docs/en/agents-and-tools/tool-use/overview.md` |
| Defining tools | `https://platform.claude.com/docs/en/agents-and-tools/tool-use/define-tools.md` |
| Skills authoring | `https://code.claude.com/docs/en/skills.md` |
| Subagents | `https://code.claude.com/docs/en/sub-agents.md` |
| CLAUDE.md and memory | `https://code.claude.com/docs/en/memory.md` |
| Context window | `https://code.claude.com/docs/en/context-window.md` |
| Hooks reference | `https://code.claude.com/docs/en/hooks.md` |
| Hooks guide | `https://code.claude.com/docs/en/hooks-guide.md` |
| MCP in Claude Code | `https://code.claude.com/docs/en/mcp.md` |
| Plugins | `https://code.claude.com/docs/en/plugins.md` |
| Plugins reference | `https://code.claude.com/docs/en/plugins-reference.md` |
| Settings | `https://code.claude.com/docs/en/settings.md` |
| Prompt caching | `https://code.claude.com/docs/en/prompt-caching.md` |

All fourteen resolved on `last-verified`.

## Shortlists — what each one mandates that we must remember

### Tool search — deferred tool loading

- **Names, descriptions, argument names and argument descriptions are the searchable index.** They
  are what the match runs against; nothing else about the tool is visible until it is fetched.
- **Load just in time.** Defer specialist tools; keep only the handful a caller needs unconditionally
  always-available.
- **A search returns at most ~5 matches.** A tool that ranks sixth for its own intent is unreachable
  in practice — this is the mechanism behind our router rule, not a theoretical concern.
- **Design for discovery from natural-language intent**, not from the exact technical name. A caller
  who knows the name doesn't need search.
- Enabled by default on Opus / Sonnet / Haiku 4.5 and later; `ENABLE_TOOL_SEARCH` = `true` / `false` /
  `auto` / `auto:N` (load upfront when the definitions are under N% of the context window).
- Catalogue cap is 10,000 tools. We ship 18 — the cap is not our constraint; **ranking** is.
- Cost model: one extra round-trip the first time a tool is discovered, repaid by a smaller context on
  every turn. A fetched tool stays available until compaction.

### Tool use overview / defining tools

- `name` must match `^[a-zA-Z0-9_-]{1,64}$`.
- `description` is **required, plaintext, and should be detailed** — "3–4 sentences minimum" for a
  simple tool, more for a complex one. Description quality is called out as critical to selection
  accuracy. **There is no official upper character cap.** Our 2 KB ceiling is a house rule; see
  `harness-compliance.md` §B for the argument and the honest label.
- Say what the tool does, **when it should and should not be used**, what the parameters mean, and the
  caveats.
- **Return high-signal responses.** Trim bloat; use semantic ids (slugs, symbol ids) rather than
  opaque internal handles, so the model can act on what it gets back.
- Prefer **consolidating related operations** into one tool with an action/selector parameter over
  splitting them into near-duplicate tools that then compete in search.
- Prefix names by service when a catalogue spans services — which is what the harness already does
  for us via `mcp__plugin_dotnet-toolkit_dotnet__`.
- `input_examples` is optional and costs roughly 20–200 tokens; worth it for format-sensitive or
  deeply nested arguments.

### Skills authoring

- A skill is `<dir>/SKILL.md` with YAML frontmatter. `name` and `description` are required; the
  **`description` is what decides whether the skill gets invoked**, and it is always in context.
- Optional frontmatter: `allowed-tools`, `user-invocable`, `disable-model-invocation`,
  `argument-hint`, `model`, `effort`.
- **Bodies load on demand**, so body length costs nothing until invocation — the basis for our
  "skills have no size budget" carve-out.
- Supporting files may sit alongside `SKILL.md` in the same directory and be read when needed. This is
  the mechanism this very file uses.
- `$ARGUMENTS` captures user input; `@path` references a file; `` !`cmd` `` substitutes a command.

### Subagents

- `agents/<name>.md` with frontmatter: `name` and `description` required; `prompt`, `tools`, `model`,
  `memory`, `mcpServers` optional.
- **The `description` is always in context; the body is not.** Selection is description-driven —
  which is why our router only needs to carry the *mandate* to delegate, never a summary of what the
  agent does.
- Omitting `tools` inherits everything; supplying it restricts. MCP wildcards (`mcp__server__*`) work.
- A subagent receives its invocation prompt, its own definition, and the project's always-loaded
  context (`CLAUDE.md` + rules). **There is no opt-out from the always-loaded files** — the multiplier
  behind §D's budget.
- `memory:` grants a memory scope; note that granting it can carry tool grants with it (our
  `dotnet-code-review` notes this explicitly).

### CLAUDE.md and memory

- Precedence: managed policy → project `CLAUDE.md` → `CLAUDE.local.md` → user `~/.claude/CLAUDE.md`.
- **Target under 200 lines per always-loaded file.** This is the one *official* size number that
  applies to us.
- Auto-memory `MEMORY.md` is read only to **200 lines or 25 KB, whichever comes first**; past that it
  is silently truncated.
- `@path` imports resolve relative to the importing file, max 4 hops; backticked `` `@path` `` stays
  literal.
- `.claude/rules/*.md` may carry `paths:` frontmatter for conditional loading; **a rule file with no
  frontmatter is unconditionally always-loaded.**

### Context window

- Load order: system prompt and tool definitions → project context (`CLAUDE.md`, rules, auto-memory)
  → conversation.
- **Tool definitions load upfront unless tool search is on**, in which case only names are present
  until a fetch. Everything in `harness-compliance.md` §C and §F follows from this one sentence.
- `paths:`-scoped rules inject when a matching file is read, and **do not re-inject after compaction**
  until re-triggered. An unconditional rule is reloaded from disk.
- Compaction rebuilds from a summary; the always-loaded layer survives, conversation detail does not.

### Hooks

- Configured in `hooks/hooks.json` for a plugin; matcher is a tool name, a `|`-separated list, or a
  regex when it contains non-alphanumerics.
- Types include `command`, `http`, `mcp_tool`, `prompt`, `agent`. Matching an **MCP tool name** is
  supported — our `hint-write-checklist` relies on it.
- **Output cap is 10,000 characters.** Exit 0 = success (JSON on stdout is parsed), exit 2 = blocking
  error (stderr shown to the model, action blocked), anything else = non-blocking error.
- `PreToolUse` decides via `permissionDecision`: `allow` / `deny` / `ask` / `defer`.
- Default timeout is generous (600 s for `command`), but per-event caps are much tighter — our
  `hooks.json` pins 10 s, which is the right instinct for a hook on a hot path.
- Hooks are **deterministic**; `CLAUDE.md` is advisory. Anything that must always happen belongs in a
  hook, not in prose.

### MCP in Claude Code

- Project servers live in `.mcp.json`; tools are exposed as `mcp__<server>__<tool>`, and the full
  qualified name is subject to the same 64-character limit.
- stdio servers are **spawned directly, not through a shell** — the reason nothing we ship at runtime
  may be a `.sh`/`.ps1`/shebang entry point.
- Tool descriptions are called out here too as critical for performance.

### Plugins / plugins reference

- `.claude-plugin/plugin.json` holds the manifest (`name` required, matching `^[a-z0-9][a-z0-9-_]*$`;
  `description` required; `version`, `author`, `repository`, `license`, `keywords` optional).
- **Everything else sits at the plugin root, not inside `.claude-plugin/`**: `skills/`, `agents/`,
  `hooks/`, `commands/`, `.mcp.json`, `.lsp.json`, `scripts`.
- Skills namespace as `/plugin-name:skill-name`.
- `claude plugin validate <dir>` checks the layout locally; `/reload-plugins` picks up skill, agent,
  hook and MCP changes without a restart.

### Settings

- `~/.claude/settings.json` (user) → `.claude/settings.json` (project) → `.claude/settings.local.json`
  (local); managed policy outranks all. Arrays merge and de-duplicate across scopes.
- `permissions` and `hooks` reload live; `model` and `outputStyle` are read once at startup.
- Relevant to us because `dotnet-toolkit-init` merges the read-only MCP tools into the project
  allowlist — merge semantics mean it must not clobber, only add.

### Prompt caching

- The cached prefix is the system prompt plus project context. Editing `CLAUDE.md` mid-session does
  **not** take effect until `/clear`, `/compact`, or a restart.
- Connecting or disconnecting an MCP server invalidates the cache **when its tools are in the prefix**
  — with tool search on, they are not, so our deferred surface is cheap to attach.
- TTL is 5 minutes by default, 1 hour on a subscription.

## Facts cited by `harness-compliance.md`

Kept together so a refresh can check them in one pass.

| Fact | Value | Cited by |
| --- | --- | --- |
| Tool name regex | `^[a-zA-Z0-9_-]{1,64}$` | §C |
| Tool-search results per query | ≤ ~5 | §C |
| Tool catalogue cap | 10,000 | §C |
| Tool search default-on | Opus / Sonnet / Haiku 4.5+ | §C, §F |
| Tool description length | detailed, 3–4 sentences minimum; **no official maximum** | §B |
| Always-loaded file target | under 200 lines | §D |
| `MEMORY.md` read limit | 200 lines or 25 KB, whichever first | §D |
| Hook output cap | 10,000 characters | §G |
| `paths:` frontmatter | only meaningful inside `.claude/rules/` | §G |
| Plugin asset location | plugin root, not `.claude-plugin/` | §G |
| Subagent always-loaded inheritance | no opt-out | §D, §F |
