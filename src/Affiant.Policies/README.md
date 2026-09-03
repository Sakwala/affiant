# Affiant.Policies

Approval policy framework for the [Affiant framework](https://github.com/Sakwala/affiant) — "sworn provenance for every AI write."

Provides the building blocks for deciding what happens to a filed `WriteProposal` before it reaches a human reviewer: Standing Orders (auto-approval rules), Referral routing (escalation rules), and the risk-scoring seam — the layer between "a write was proposed" and "a write sits in the Docket awaiting confirmation."

## Quick start

```csharp
builder.Services.AddAffiantCore();
builder.Services.AddAffiantPolicies(policies =>
{
    policies
        .AddStandingOrder<LowValueAutoApproval>()
        .AddReferralRule<HighValueEscalation>()
        .AddDefaultReviewerConfirmation();
});
```

Implement `StandingOrderBase` for an auto-approval rule and `ReferralRuleBase` for an escalation rule. A Standing Order is complete once `MatchesAsync` describes when it applies — matching is then the whole test, and the order auto-approves.

## Risk thresholds are opt-in, and the score is yours

A Standing Order may add a risk ceiling on top of its conditions by overriding `RiskThreshold` (an `int?`, `null` by default, on the `RiskLevel` scale where 1 is low and 3 is high). It then auto-approves only when the computed score is at or below that ceiling.

The framework ships no scoring formula. What counts as risky is a property of your domain, not of the evidence layer, so you subclass `RiskScoreCalculatorBase`, implement `ComputeAsync`, and register it:

```csharp
builder.Services.AddAffiantPolicies(policies =>
{
    policies
        .SetRiskScoreCalculator<InvoiceRiskCalculator>()
        .AddStandingOrder<LowValueAutoApproval>()   // declares RiskThreshold => (int)RiskLevel.Low
        .AddDefaultReviewerConfirmation();
});
```

A Standing Order that declares a `RiskThreshold` with no calculator registered throws `InvalidOperationException` when the container builds it, naming `SetRiskScoreCalculator<T>()` — a misconfigured host fails at startup rather than quietly deferring every write the order was written to approve.

## Package contents

| Namespace | Purpose |
|---|---|
| `Affiant.Policies.StandingOrders` | `StandingOrderBase` — the auto-approval rule contract |
| `Affiant.Policies.Referrals` | `ReferralRuleBase` — the escalation-routing rule contract |
| `Affiant.Policies.Services` | `RiskScoreCalculatorBase` — the host-implemented scoring seam — and `RiskLevel` |
| `Affiant.Policies.Extensions` | `ServiceCollectionExtensions` — `AddAffiantPolicies`, the `PoliciesBuilder` fluent registration surface |

## Further reading

- [Affiant Framework Specification](https://github.com/Sakwala/affiant/blob/main/docs/affiant-framework-specification.md) — the full design contract, including the review pipeline and approval-policy semantics
- [Tool Authoring Guide](https://github.com/Sakwala/affiant/blob/main/docs/tool-authoring-guide.md) — write your first Affiant plugin pair

---

*Part of the [Affiant Framework](https://github.com/Sakwala/affiant) | Apache-2.0 License*
