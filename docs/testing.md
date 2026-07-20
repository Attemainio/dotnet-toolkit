# .NET testing guide

Default reference for `dotnet-code-review`'s `testing` dimension. Overridable per-repo via
`.claude/dotnet-toolkit/testing.md` (see `CLAUDE.md`).

## Coverage signal — verify, don't assume

**A missing-test finding needs a stated `get_references` check, the same discipline the `cleanup`
dimension applies to dead code.** For a changed/scoped public type or method, use `get_references` and
look at whether any caller lives under a test project (a project whose name/path contains `Tests`,
`.Test`, or the repo's own test-project convention if `search_log`/prior findings have established one).
Zero test-project callers on a public symbol that isn't trivial (not a one-line pass-through, not a DTO)
is the finding — cite the zero-hit result rather than asserting "this looks untested" from reading alone.
A symbol with only non-test callers may still be covered indirectly through an integration test that
exercises it via a higher-level entry point; if `get_call_slice` from a plausible test entry point reaches
it, say so and drop the finding rather than double-counting.

## Test structure

- **Arrange/Act/Assert, visibly separated.** A test that interleaves setup, the call under test, and
  assertions without any structure is harder to verify at a glance — flag it as a 🔵 readability
  suggestion, not a correctness issue, unless the interleaving actually obscures what's being tested.
- **One behavior per test.** Multiple assertions covering the same behavior (several properties of one
  result) are fine; asserting two unrelated behaviors in one test method means a failure doesn't say which
  one broke — flag the split.
- **Test naming should state the scenario and expected outcome**, not just the method under test
  (`MethodName_Scenario_ExpectedResult` or an equivalent convention already established in the project —
  check `search_log` and existing test files for the project's own pattern before assuming the default is
  wrong).

## Real dependencies over mocks, with a stated exception

- **Mocking a type the project itself owns** (not a third-party boundary) couples the test to
  implementation details and usually means the test breaks on refactors that don't change behavior. Prefer
  a real or in-memory test implementation the project already has, or flag the absence of one as a 🔵
  suggestion rather than accepting the mock silently.
- **Reserve mocks for genuine external boundaries** — third-party HTTP APIs, cloud SDKs, anything the
  project doesn't control the interface of. A mock here is correct, not a finding.
- **An in-memory-provider substitute for a real database** (e.g. EF Core's in-memory provider standing in
  for the actual relational provider) diverges in constraint enforcement, transaction behavior, and query
  translation — flag it as 🟡 when the test is asserting behavior that provider actually differs on
  (constraint violations, specific SQL translation, transaction rollback), not when it's asserting simple
  CRUD shape that behaves the same either way.

## Behavior over implementation

- **Assert on the observable outcome** — a returned value, a persisted state change, a raised event/HTTP
  response — not on which private method got called or how many times an internal collaborator was
  invoked, unless the call count itself is the contract being tested (e.g. verifying a cache is only
  populated once).
- **A test whose assertions read the internal fields/private state of the system under test** rather than
  its public contract is fragile by construction — flag it, since it will fail on refactors that preserve
  behavior.

## What this dimension does not do

This plugin has no dedicated flakiness/mutation-testing tool — a "this test looks flaky" finding needs a
stated reason from reading the test (non-deterministic ordering, real wall-clock time, shared mutable
fixture state across tests without isolation), not a guess. Don't assert a coverage percentage; this
dimension reports presence/absence of tests for specific symbols via `get_references`, not an aggregate
number this plugin has no tool to compute.
