# Affiant Compliance Test Helper

Verify that your `Affiant.Core` task inference strategies conform to their specifications before shipping to production. This package provides `ComplianceHarness.Verify()`, which enumerates all registered write strategies, checks that each has a paired compliance fixture, and executes every fixture case — reporting missing fixtures and assertion failures in a structured `ComplianceVerificationResult`.

## Quick Start

Register your inference strategy and a compliance fixture, then call `ComplianceHarness.Verify()`:

```csharp
var services = new ServiceCollection()
    .AddAffiantCore()
    .AddAffiantInferenceOrchestration()
    .AddSingleton(recordedPort) // IInferenceCompletionPort for test doubles
    .AddAffiantTool<MyStrategy>(
        functionName: "CreateOrder",
        operation: Operation.WriteCreate,
        entityType: "Order",
        pluginName: null
    )
    .AddSingleton<ITaskInferenceComplianceFixture>(new MyStrategyComplianceFixture());

var result = ComplianceHarness.Verify(services);
Assert.True(result.Passed,
    $"Compliance check failed.\n" +
    $"Missing fixtures: {string.Join(", ", result.MissingFixtures)}\n" +
    $"Fixture failures: {string.Join(", ", result.FixtureFailures)}");
```

If `result.Passed` is `false`, either:
- A write strategy is missing a compliance fixture (`MissingFixtures`), or
- A fixture case's assertion failed (`FixtureFailures`)

## API Reference

| Type | Role |
|------|------|
| `ComplianceHarness` | Static entry point — `Verify(IServiceCollection)` returns a `ComplianceVerificationResult` |
| `ComplianceVerificationResult` | `Passed`, `MissingFixtures`, `FixtureFailures` |
| `ITaskInferenceComplianceFixture` | Implement per strategy; provides `Cases` (input/expected pairs) |
| `InferenceFixtureCase` | A single fixture case: `Name`, a `ChatHistory` input, `Arguments`, and an `Assertion` predicate over the produced `Affidavit` |

## Further Reading

- [Affiant Framework Specification](https://github.com/affiant-dev/affiant/blob/main/packages/docs/affiant-framework-specification.md) — full framework guide including the seven normative rules and tool authoring patterns
- [L2 Inference Orchestration PRD](https://github.com/affiant-dev/affiant/blob/main/docs/architecture/phase-3-prd-l2-inference-orchestration.md) — L2 design rationale and acceptance criteria (AC #7 specifies this package as a v1.0 deliverable)

---

*Version 1.0.0-alpha.1 | Apache-2.0 License | Part of the [Affiant Framework](https://github.com/affiant-dev/affiant)*
