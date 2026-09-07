# Codex guidance

[`CLAUDE.md`](CLAUDE.md) is this repository's canonical operating contract. Read and follow it; this
file intentionally does not duplicate those instructions.

Before the first C# read or edit, also read [`.claude/rules/dotnet-index.md`](.claude/rules/dotnet-index.md).
Claude Code loads that router automatically; Codex does not scan `.claude/rules/`.

The Markdown files under [`agents/`](agents/) are the canonical role briefs, and the adapter below is
the supported way to run them outside Claude Code — there are deliberately no generated
`.codex/agents/*.toml` equivalents to drift from them. When a skill asks for a named agent and that
custom agent is unavailable in the current host, launch the closest built-in subagent and tell it to
read the matching `agents/<name>.md` before starting. Treat the YAML frontmatter fields that name
Claude-specific tools, models, memory, or colors as host configuration; the Markdown body remains the
shared role contract.

That frontmatter's `tools:` list is host configuration in one further respect: it spells every MCP
tool with Claude Code's plugin-qualified prefix, `mcp__plugin_dotnet-toolkit_dotnet__<tool>`. Codex
registers the same server under the bare name `dotnet` and namespaces by that instead. Resolve the
prefix once by probing for one known tool rather than assuming either form, and apply what comes back
to the rest — a qualified name that misses means the label differs, never that the tools are absent.

## Checking the installation

`codex mcp list --json` reports configuration, not health: it launches nothing, so it shows the
server enabled whether or not the command it names can run. To verify for real —
executor, effective `CODEX_HOME`, whether each stored path is openable from this process, the SDK the
workspace loads with, and a live handshake — run:

```bash
dotnet dist/DotnetToolkit.McpServer.dll doctor --repo .
```

It exits nonzero on any failure. `doctor --no-probe` additionally fails when `dist/` has fallen
behind `src/`, which is the pre-commit gate for the rule that `dist/` is committed alongside the
change that required it.
