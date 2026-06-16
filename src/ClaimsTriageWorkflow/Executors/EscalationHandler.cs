using System.Text.Json;
using ClaimsTriageWorkflow.Models;
using Microsoft.Agents.AI.Workflows;

namespace ClaimsTriageWorkflow.Executors;

public static class EscalationHandler
{
    /// <summary>
    /// Determines EscalationReason by priority: high_amount > fraud_flag > high_urgency.
    /// Pure logic, tested directly without a workflow context.
    /// </summary>
    public static string DetermineReason(ClaimClassification c)
    {
        if (c.EstimatedAmount > Constants.AmountThreshold) return "high_amount";
        if (c.FraudIndicators)                             return "fraud_flag";
        return "high_urgency";
    }

    public static async ValueTask<AdjusterDossier> Handle(
        ClaimClassification classification, IWorkflowContext ctx, CancellationToken ct)
    {
        var claim  = await ctx.ReadStateAsync<PreprocessedClaim>("preprocessed_claim", "run", ct)
            ?? throw new InvalidOperationException("preprocessed_claim state not found in workflow context");
        var reason = DetermineReason(classification);

        var dossier = new AdjusterDossier(
            claim.ClaimId,
            claim.PolicyNumber,
            classification,
            claim.MaskedText,
            reason);

        // Mock human inbox: write the dossier as a JSON line prefixed with [INBOX].
        Console.WriteLine($"[INBOX] {JsonSerializer.Serialize(dossier)}");

        return dossier;
    }

    public static ExecutorBinding Build()
        => ((Func<ClaimClassification, IWorkflowContext, CancellationToken, ValueTask<AdjusterDossier>>)Handle)
            .BindAsExecutor("escalation_handler", ExecutorOptions.Default, false);
}
