using ClaimsTriageWorkflow.Executors;
using ClaimsTriageWorkflow.Models;
using Xunit;

namespace ClaimsTriageWorkflow.Tests;

public class EscalationHandlerTests
{
    // ── EscalationReason priority: red_flag → low_confidence → high_amount → fraud_flag → high_urgency ──

    [Fact]
    public void Red_flag_beats_high_amount_and_fraud()
    {
        var c = Classification(amount: 50_000m, fraud: true, urgency: UrgencyLevel.High);
        c.PreScreenFlags = new PreScreenFlags(TotalLoss: true, false, false, false, false, false);
        Assert.Equal("red_flag", EscalationHandler.DetermineReason(c));
    }

    [Fact]
    public void Low_confidence_beats_fraud_flag()
    {
        var c = Classification(amount: 1_000m, fraud: true, urgency: UrgencyLevel.Low);
        c.ClassificationConfidence = 0.3; // below default 0.65 threshold
        Assert.Equal("low_confidence", EscalationHandler.DetermineReason(c));
    }

    [Fact]
    public void High_amount_beats_fraud_and_urgency()
    {
        // red_flag and low_confidence are both absent; high_amount fires first.
        var c = Classification(amount: 50_000m, fraud: true, urgency: UrgencyLevel.High);
        Assert.Equal("high_amount", EscalationHandler.DetermineReason(c));
    }

    [Fact]
    public void Fraud_flag_beats_high_urgency()
    {
        var c = Classification(amount: 1_000m, fraud: true, urgency: UrgencyLevel.High);
        Assert.Equal("fraud_flag", EscalationHandler.DetermineReason(c));
    }

    [Fact]
    public void High_urgency_is_fallback_reason()
    {
        var c = Classification(amount: 500m, fraud: false, urgency: UrgencyLevel.High);
        Assert.Equal("high_urgency", EscalationHandler.DetermineReason(c));
    }

    [Fact]
    public void Full_priority_chain_red_flag_wins_over_all()
    {
        var c = Classification(amount: 50_000m, fraud: true, urgency: UrgencyLevel.High);
        c.PreScreenFlags = new PreScreenFlags(true, false, false, false, false, false);
        c.ClassificationConfidence = 0.3;
        Assert.Equal("red_flag", EscalationHandler.DetermineReason(c));
    }

    // ── AdjusterDossier construction ──────────────────────────────────────────

    [Fact]
    public async Task Dossier_fields_are_populated_correctly()
    {
        var claim = new PreprocessedClaim("IL-9910", "IL-9910", "masked text", null, "original", PreScreenFlags.None);
        var classification = Classification(amount: 0m, fraud: false, urgency: UrgencyLevel.High);

        var ctx = new FakeWorkflowContext();
        ctx.SetState("preprocessed_claim", claim);

        var dossier = await EscalationHandler.Handle(classification, ctx, CancellationToken.None);

        Assert.Equal("IL-9910", dossier.ClaimId);
        Assert.Equal("IL-9910", dossier.PolicyNumber);
        Assert.Equal("high_urgency", dossier.EscalationReason);
        Assert.Equal("masked text", dossier.MaskedText);
        Assert.Same(classification, dossier.Classification);
    }

    [Fact]
    public async Task Inbox_line_is_written_to_console()
    {
        var claim = new PreprocessedClaim("IL-9910", "IL-9910", "text", null, "original", PreScreenFlags.None);
        var ctx = new FakeWorkflowContext();
        ctx.SetState("preprocessed_claim", claim);

        var output = new System.Text.StringBuilder();
        var original = Console.Out;
        Console.SetOut(new System.IO.StringWriter(output));
        try
        {
            await EscalationHandler.Handle(Classification(0m, false, UrgencyLevel.High), ctx, CancellationToken.None);
        }
        finally
        {
            Console.SetOut(original);
        }

        Assert.Contains("[INBOX]", output.ToString());
    }

    // ── Helper ────────────────────────────────────────────────────────────────

    // ClassificationConfidence = 1.0 prevents the confidence threshold from firing
    // accidentally and masking the escalation reason being tested.
    private static ClaimClassification Classification(decimal amount, bool fraud, UrgencyLevel urgency)
        => new()
        {
            EstimatedAmount = amount,
            FraudIndicators = fraud,
            Urgency = urgency,
            ClassificationConfidence = 1.0,
        };
}
