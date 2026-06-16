using ClaimsTriageWorkflow.Executors;
using ClaimsTriageWorkflow.Models;
using Xunit;

namespace ClaimsTriageWorkflow.Tests;

public class EscalationHandlerTests
{
    // ── EscalationReason priority ─────────────────────────────────────────────

    [Fact]
    public void High_amount_is_first_priority()
    {
        // Amount > threshold beats fraud and urgency
        var c = Classification(amount: 50_000m, fraud: true, urgency: "high");
        Assert.Equal("high_amount", EscalationHandler.DetermineReason(c));
    }

    [Fact]
    public void Fraud_flag_is_second_priority()
    {
        // Fraud beats urgency when amount is within threshold
        var c = Classification(amount: 1_000m, fraud: true, urgency: "high");
        Assert.Equal("fraud_flag", EscalationHandler.DetermineReason(c));
    }

    [Fact]
    public void High_urgency_is_fallback_reason()
    {
        var c = Classification(amount: 500m, fraud: false, urgency: "high");
        Assert.Equal("high_urgency", EscalationHandler.DetermineReason(c));
    }

    // ── AdjusterDossier construction ──────────────────────────────────────────

    [Fact]
    public async Task Dossier_fields_are_populated_correctly()
    {
        var claim = new PreprocessedClaim("IL-9910", "IL-9910", "masked text", null, "original");
        var classification = Classification(amount: 0m, fraud: false, urgency: "high");

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
        var claim = new PreprocessedClaim("IL-9910", "IL-9910", "text", null, "original");
        var ctx = new FakeWorkflowContext();
        ctx.SetState("preprocessed_claim", claim);

        var output = new System.Text.StringBuilder();
        var original = Console.Out;
        Console.SetOut(new System.IO.StringWriter(output));
        try
        {
            await EscalationHandler.Handle(Classification(0m, false, "high"), ctx, CancellationToken.None);
        }
        finally
        {
            Console.SetOut(original);
        }

        Assert.Contains("[INBOX]", output.ToString());
    }

    // ── Helper ────────────────────────────────────────────────────────────────

    private static ClaimClassification Classification(decimal amount, bool fraud, string urgency)
        => new() { EstimatedAmount = amount, FraudIndicators = fraud, Urgency = urgency };
}
