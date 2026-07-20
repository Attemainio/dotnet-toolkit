---
name: docs-findings-pattern
description: Recurring docs-dimension finding class in dotnet-toolkit's own MCP tool surface — [Description]/example drift after a response-shape refactor
metadata:
  type: project
---

[docs] When a tool's response shape changes (e.g. array -> CompactTable {columns,rows}), check
every place that shape is described, not just the [Description] attribute on the changed method
itself: (1) other prose in the *same* [Description] string can lag behind a shape change made
elsewhere in the same edit (found: ContextTools.GetSymbol's own batch-mode sentence still said
`{"results":[...]}` after the type moved to a CompactTable, while the surrounding text was
correct); (2) docs/tool-reference.md's "real call and response" examples are literal captured
output and go stale silently — verify by actually invoking the tool live and diffing, don't trust
the doc's own internal consistency; (3) skills/dotnet-code-query/SKILL.md can retain terminology
from a superseded API (found: "signature"/"full" resolution-ladder language surviving the
resolution/exclude -> include-selector rewrite, still present as an isolated example sentence
elsewhere in an otherwise-updated file).

Why: these three files independently describe the same tool surface (CLAUDE.md's "Changing the
tool surface" table), so a shape change updates unevenly across them — grep for old
value/terminology names across skills/ and docs/ after any response-shape or arg-shape change,
not just the touched Tools/*.cs file.

How to apply: for a `dimension: docs` review touching Tools/*.cs, always (a) live-call each
changed tool and diff against docs/tool-reference.md's example, (b) grep skills/*.md and docs/*.md
for now-invalid argument/value names from the old API shape, (c) re-read the full [Description]
string end-to-end rather than just the part that obviously changed — a stale sentence can sit
right next to a correctly-updated one in the same attribute.
