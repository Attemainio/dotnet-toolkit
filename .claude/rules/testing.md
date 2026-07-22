---
paths:
  - "**/*.cs"
---

# .NET testing

Canonical testing standard: how tests are structured, written, and run. Loaded on demand per
`csharp-standards.md`'s index; read it before writing or modifying tests. `dotnet-code-review`
validates against it (aspect `[testing]`). A consuming repo overrides it via
`.claude/dotnet-toolkit/testing.md`.

## Project layout & running

- Tests live in a **separate test project** named `<Project>.Tests` (e.g. `OrderService.Tests`), never
  inside production source folders. The test tree mirrors the source tree: a type at
  `src/Orders/Pricing/DiscountCalculator.cs` is tested from
  `tests/OrderService.Tests/Pricing/DiscountCalculatorTests.cs`.
- One test class per type under test, named `<TypeName>Tests`.
- Run everything with `dotnet test`; run one class with
  `dotnet test --filter FullyQualifiedName~DiscountCalculatorTests`; run one test with
  `--filter "FullyQualifiedName~DiscountCalculatorTests.Apply_NegativeQuantity_Throws"`. A repo's
  CLAUDE.md may state additional project-specific commands (a slower integration suite, a fixture
  solution) — check it before assuming `dotnet test` alone is the whole story.
- New test files follow the repo's established framework (xUnit unless the repo says otherwise) — don't
  introduce a second test framework into a solution that already has one.

## Structure: Arrange / Act / Assert

One behavior per test, three visible phases separated by blank lines:

```csharp
[Fact]
public void Apply_QuantityAboveThreshold_AppliesBulkDiscount()
{
    var calculator = new DiscountCalculator(bulkThreshold: 10, bulkRate: 0.1m);
    var order = new Order(quantity: 12, unitPrice: 5m);

    var total = calculator.Apply(order);

    Assert.Equal(54m, total);
}
```

- Multiple assertions on the same behavior (several properties of one result) are fine; asserting two
  unrelated behaviors in one test means a failure doesn't say which broke — split it.
- **Test names state scenario and expected outcome**: `MethodName_Scenario_ExpectedResult` (or the
  project's own established equivalent — check existing tests before assuming the default). `TestApply`
  or `Apply_Works` tells a failing build nothing.

## Real dependencies over mocks

- **Don't mock types the project itself owns** — that couples the test to implementation details and
  breaks on refactors that don't change behavior. Use the real implementation, or an in-memory test
  implementation the project already provides.
- **Reserve mocks for genuine external boundaries** — third-party HTTP APIs, cloud SDKs, anything whose
  interface the project doesn't control. A mock there is correct.
- **An in-memory database provider is not the real database.** EF Core's in-memory provider diverges from
  a relational provider on constraint enforcement, transactions, and query translation — tests asserting
  any of those behaviors run against the real provider (Testcontainers or an equivalent). Simple CRUD
  shape that behaves identically either way may use the substitute.
- Shared expensive resources (a container, a seeded database) go in a fixture
  (`IClassFixture<T>`/collection fixture), not rebuilt per test.

## Behavior over implementation

- **Assert observable outcomes** — a returned value, persisted state, a raised event/HTTP response — not
  which private method got called or how many times an internal collaborator was invoked, unless the call
  count itself is the contract (e.g. a cache populated exactly once).
- A test whose assertions read private/internal state of the system under test is fragile by
  construction — it fails on refactors that preserve behavior. Test through the public contract.

## Determinism

No real wall-clock dependence (`DateTime.Now`, `Task.Delay`-based timing races), no dependence on test
execution order, no shared mutable fixture state without isolation. A test that fails once a week trains
everyone to ignore the suite.

## Review calibration — coverage claims need evidence

**A missing-test finding needs a stated `get_references` check, never a guess.** For a changed/scoped
public symbol, run `get_references` (`direction: "callers"`) and look for a caller under a test project
(name/path contains `Tests`, `.Test`, or the repo's own convention). Zero test-project callers on a
non-trivial public symbol (not a one-line pass-through, not a DTO) is the finding — cite the zero-hit
result. A symbol with only non-test callers may still be covered through an integration test's
higher-level entry point: if `get_call_slice` from a plausible test entry point reaches it, drop the
finding. Also `search_index` for a test method matching the symbol's name before assuming none exists.
Don't assert a coverage percentage — this plugin has no tool to compute one; report presence/absence per
symbol. A "this test looks flaky" finding needs a stated cause from reading it (non-deterministic
ordering, wall-clock time, shared fixture state), not a hunch. Structure/naming issues are 🔵 unless the
structure obscures what's being tested; in-memory-provider misuse against
constraint/transaction-asserting tests is 🟡.
