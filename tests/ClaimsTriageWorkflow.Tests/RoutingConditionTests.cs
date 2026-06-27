using ClaimsTriageWorkflow.Executors;
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

    // ── Confidence threshold routing ──────────────────────────────────────────

    [Fact]
    public void Low_confidence_escalates()
    {
        var c = C(urgency: UrgencyLevel.Low, amount: 100m, fraud: false, safe: false, missing: 0);
        c.ClassificationConfidence = 0.3; // below default 0.65 threshold
        Assert.True(RoutingConditions.ShouldEscalate(c));
    }

    [Fact]
    public void Confidence_at_threshold_does_not_escalate()
    {
        // Boundary: exactly at threshold is NOT < threshold, so it should not escalate.
        var c = C(urgency: UrgencyLevel.Low, amount: 100m, fraud: false, safe: true, missing: 0);
        c.ClassificationConfidence = Constants.ConfidenceThreshold;
        Assert.False(RoutingConditions.ShouldEscalate(c));
        Assert.True(RoutingConditions.ShouldAutoApprove(c));
    }

    [Fact]
    public void Confidence_above_threshold_does_not_escalate()
    {
        var c = C(urgency: UrgencyLevel.Low, amount: 800m, fraud: false, safe: true, missing: 0);
        c.ClassificationConfidence = 0.9;
        Assert.False(RoutingConditions.ShouldEscalate(c));
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

    // ── Red-flag pre-screening ────────────────────────────────────────────────

    [Fact]
    public void TotalLoss_fires_on_burned_down()
    {
        var flags = RedFlagDetector.Detect("The warehouse burned down completely.");
        Assert.True(flags.TotalLoss);
        Assert.True(flags.Any);
    }

    [Fact]
    public void FireOrExplosion_fires_on_fire_keyword()
    {
        var flags = RedFlagDetector.Detect("There was a fire at the premises.");
        Assert.True(flags.FireOrExplosion);
        Assert.True(flags.Any);
    }

    [Fact]
    public void FloodOrWater_fires_on_flooding()
    {
        var flags = RedFlagDetector.Detect("The basement had flooding.");
        Assert.True(flags.FloodOrWater);
        Assert.True(flags.Any);
    }

    [Fact]
    public void FraudLanguage_fires_on_staged_keyword()
    {
        var flags = RedFlagDetector.Detect("The accident was staged.");
        Assert.True(flags.FraudLanguage);
        Assert.True(flags.Any);
    }

    [Fact]
    public void HighValueLanguage_fires_on_millions()
    {
        var flags = RedFlagDetector.Detect("Loss in the millions.");
        Assert.True(flags.HighValueLanguage);
        Assert.True(flags.Any);
    }

    [Fact]
    public void LegalLanguage_fires_on_lawsuit()
    {
        var flags = RedFlagDetector.Detect("I will file a lawsuit.");
        Assert.True(flags.LegalLanguage);
        Assert.True(flags.Any);
    }

    [Fact]
    public void Case_insensitive_matching()
    {
        var flags = RedFlagDetector.Detect("FIRE and EXPLOSION damage.");
        Assert.True(flags.FireOrExplosion);
    }

    [Fact]
    public void Routine_claim_produces_None()
    {
        var flags = RedFlagDetector.Detect("Small water leak, receipt for ₪800 attached.");
        Assert.False(flags.Any);
    }

    [Fact]
    public void Multiple_flags_detected_simultaneously()
    {
        var flags = RedFlagDetector.Detect("There was fraud and a lawsuit after the fire.");
        Assert.True(flags.FraudLanguage);
        Assert.True(flags.LegalLanguage);
        Assert.True(flags.FireOrExplosion);
        Assert.True(flags.Any);
    }

    [Fact]
    public void Red_flag_any_triggers_escalation()
    {
        var c = C(urgency: UrgencyLevel.Low, amount: 100m, fraud: false, safe: false, missing: 0);
        c.PreScreenFlags = new PreScreenFlags(TotalLoss: true, FireOrExplosion: false, FloodOrWater: false, FraudLanguage: false, HighValueLanguage: false, LegalLanguage: false);
        Assert.True(c.PreScreenFlags.Any);
        Assert.True(RoutingConditions.ShouldEscalate(c));
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
        // Set ClassificationConfidence = 1.0 so the confidence threshold check does not
        // fire accidentally and mask the condition being tested.
        var c = new ClaimClassification
        {
            ClaimType = ClaimType.Vehicle,
            Urgency = urgency,
            Sentiment = SentimentType.Neutral,
            EstimatedAmount = amount,
            FraudIndicators = fraud,
            SafeToAutoApprove = safe,
            ClassificationConfidence = 1.0,
        };
        for (int i = 0; i < missing; i++) c.MissingInfo.Add($"item_{i}");
        return c;
    }
}
