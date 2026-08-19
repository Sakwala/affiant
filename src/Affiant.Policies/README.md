# Affiant.Policies

Approval policy framework for the [Affiant framework](https://github.com/Sakwala/affiant) — "sworn provenance for every AI write."

Provides the building blocks for deciding what happens to a filed `WriteProposal` before it reaches a human reviewer: Standing Orders (auto-approval rules), Referral routing (escalation rules), and risk-scoring — the layer between "a write was proposed" and "a write sits in the Docket awaiting confirmation."

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

Implement `StandingOrderBase` for an auto-approval rule, `ReferralRuleBase` for an escalation rule, and override `RiskScoreCalculator` (registered by default as `DefaultRiskScoreCalculator`, `TryAddScoped` so a host override always wins) to change how risk feeds into either.

## Package contents

| Namespace | Purpose |
|---|---|
| `Affiant.Policies.StandingOrders` | `StandingOrderBase` — the auto-approval rule contract |
| `Affiant.Policies.Referrals` | `ReferralRuleBase` — the escalation-routing rule contract |
| `Affiant.Policies.Services` | `RiskScoreCalculator` / `DefaultRiskScoreCalculator`, `RiskLevel` |
| `Affiant.Policies.Extensions` | `ServiceCollectionExtensions` — `AddAffiantPolicies`, the `PoliciesBuilder` fluent registration surface |

## Further reading

- [Affiant Framework Specification](https://github.com/Sakwala/affiant/blob/main/docs/affiant-framework-specification.md) — the full design contract, including the review pipeline and approval-policy semantics
- [Tool Authoring Guide](https://github.com/Sakwala/affiant/blob/main/docs/tool-authoring-guide.md) — write your first Affiant plugin pair

---

*Part of the [Affiant Framework](https://github.com/Sakwala/affiant) | Apache-2.0 License*
