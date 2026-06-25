using ClaimsTriageWorkflow.Models;
using ClaimsTriageWorkflow.Workflow;
using Xunit;

namespace ClaimsTriageWorkflow.Tests;

public class RoutingConditionTests
{
    // ── Unknown classification (model failure) → fail-safe escalation ────────

    [Fact]
    public void Unknown_classification_escalates_for_human_review()
    {
        // Default-constructed ClaimClassification has all enum fields at Unknown (= 0).
        // Previously they would default to the first real value (High/Vehicle/Positive)
        // silently; Unknown sentinel makes the failure state explicit and routes to HITL.
        var c = new ClaimClassification();
        Assert.True(RoutingConditions.HasUnknownClassification(c));
        Assert.True(RoutingConditions.ShouldEscalate(c));
        Assert.False(RoutingConditions.ShouldAutoApprove(c));
    }

    [Fact]
    public void Unknown_urgency_alone_escalates()
    {
        var c = C(urgency: UrgencyLevel.Unknown, amount: 100m, fraud: false, safe: false, missing: 0);
        Assert.True(RoutingConditions.ShouldEscalate(c));
    }

    [Fact]
    public void Unknown_claim_type_alone_escalates()
    {
        var c = C(urgency: UrgencyLevel.Low, amount: 100m, fraud: false, safe: false, missing: 0);
        c.ClaimType = ClaimType.Unknown;
        Assert.True(RoutingConditions.ShouldEscalate(c));
    }

    [Fact]
    public void Unknown_sentiment_alone_escalates()
    {
        var c = C(urgency: UrgencyLevel.Low, amount: 100m, fraud: false, safe: false, missing: 0);
        c.Sentiment = SentimentType.Unknown;
        Assert.True(RoutingConditions.ShouldEscalate(c));
    }

    // ── Escalate branch ───────────────────────────────────────────────────────

    [Fact]
    public void Escalates_when_urgency_is_high()
    {
        var c = C(urgency: UrgencyLevel.High, amount: 100m, fraud: false, safe: false, missing: 0);
        Assert.True(RoutingConditions.ShouldEscalate(c));
    }

    [Fact]
    public void Escalates_when_fraud_is_true()
    {
        var c = C(urgency: UrgencyLevel.Low, amount: 100m, fraud: true, safe: false, missing: 0);
        Assert.True(RoutingConditions.ShouldEscalate(c));
    }

    [Fact]
    public void Escalates_when_amount_exceeds_threshold()
    {
        var c = C(urgency: UrgencyLevel.Low, amount: Constants.AmountThreshold + 1, fraud: false, safe: true, missing: 0);
        Assert.True(RoutingConditions.ShouldEscalate(c));
    }

    [Fact]
    public void High_amount_escalates_even_when_SafeToAutoApprove_is_true()
    {
        // Acceptance test item 8
        var c = C(urgency: UrgencyLevel.Low, amount: Constants.AmountThreshold + 1, fraud: false, safe: true, missing: 0);
        Assert.True(RoutingConditions.ShouldEscalate(c));
        Assert.False(RoutingConditions.ShouldAutoApprove(c));
    }

    [Fact]
    public void Fraud_escalates_regardless_of_amount_or_missing_info()
    {
        // Acceptance test item 9
        var c = C(urgency: UrgencyLevel.Low, amount: 100m, fraud: true, safe: false, missing: 2);
        Assert.True(RoutingConditions.ShouldEscalate(c));
        Assert.False(RoutingConditions.ShouldRequestInfo(c));
    }

    // ── Escalate priority over RequestInfo ────────────────────────────────────

    [Fact]
    public void Escalate_wins_over_request_info_when_both_conditions_match()
    {
        // High urgency + missing info → should still escalate, not request info
        var c = C(urgency: UrgencyLevel.High, amount: 100m, fraud: false, safe: false, missing: 2);
        Assert.True(RoutingConditions.ShouldEscalate(c));
        Assert.False(RoutingConditions.ShouldRequestInfo(c));
    }

    // ── RequestInfo branch ────────────────────────────────────────────────────

    [Fact]
    public void Requests_info_when_missing_items_and_not_safe()
    {
        var c = C(urgency: UrgencyLevel.Medium, amount: 500m, fraud: false, safe: false, missing: 2);
        Assert.False(RoutingConditions.ShouldEscalate(c));
        Assert.True(RoutingConditions.ShouldRequestInfo(c));
    }

    [Fact]
    public void Does_not_request_info_when_nothing_is_missing()
    {
        var c = C(urgency: UrgencyLevel.Low, amount: 800m, fraud: false, safe: true, missing: 0);
        Assert.False(RoutingConditions.ShouldRequestInfo(c));
    }

    // ── AutoApprove branch ────────────────────────────────────────────────────

    [Fact]
    public void Auto_approves_when_safe_and_within_threshold()
    {
        var c = C(urgency: UrgencyLevel.Low, amount: 800m, fraud: false, safe: true, missing: 0);
        Assert.True(RoutingConditions.ShouldAutoApprove(c));
    }

    [Fact]
    public void Does_not_auto_approve_when_amount_exceeds_threshold()
    {
        var c = C(urgency: UrgencyLevel.Low, amount: Constants.AmountThreshold + 1, fraud: false, safe: true, missing: 0);
        Assert.False(RoutingConditions.ShouldAutoApprove(c));
    }

    [Fact]
    public void Does_not_auto_approve_unknown_classification_even_when_safe_flag_is_true()
    {
        var c = C(urgency: UrgencyLevel.Low, amount: 800m, fraud: false, safe: true, missing: 0);
        c.ClaimType = ClaimType.Unknown;

        Assert.True(RoutingConditions.ShouldEscalate(c));
        Assert.False(RoutingConditions.ShouldAutoApprove(c));
    }

    [Fact]
    public void Does_not_auto_approve_fraud_even_when_safe_flag_is_true()
    {
        var c = C(urgency: UrgencyLevel.Low, amount: 800m, fraud: true, safe: true, missing: 0);

        Assert.True(RoutingConditions.ShouldEscalate(c));
        Assert.False(RoutingConditions.ShouldAutoApprove(c));
    }

    [Fact]
    public void Does_not_auto_approve_high_urgency_even_when_safe_flag_is_true()
    {
        var c = C(urgency: UrgencyLevel.High, amount: 800m, fraud: false, safe: true, missing: 0);

        Assert.True(RoutingConditions.ShouldEscalate(c));
        Assert.False(RoutingConditions.ShouldAutoApprove(c));
    }

    // ── Helper ────────────────────────────────────────────────────────────────

    private static ClaimClassification C(UrgencyLevel urgency, decimal amount, bool fraud, bool safe, int missing)
    {
        // Set ClaimType and Sentiment to known non-Unknown values so routing tests
        // isolate the variable under test rather than triggering HasUnknownClassification.
        var c = new ClaimClassification
        {
            ClaimType = ClaimType.Vehicle,
            Urgency = urgency,
            Sentiment = SentimentType.Neutral,
            EstimatedAmount = amount,
            FraudIndicators = fraud,
            SafeToAutoApprove = safe,
        };
        for (int i = 0; i < missing; i++) c.MissingInfo.Add($"item_{i}");
        return c;
    }
}
