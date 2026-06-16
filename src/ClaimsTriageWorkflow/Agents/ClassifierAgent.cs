using System.Text.Json;
using ClaimsTriageWorkflow.Models;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace ClaimsTriageWorkflow.Agents;

public static class ClassifierAgent
{
    private const string SystemPrompt = """
        You are an insurance claims classification specialist. You receive a pre-processed,
        PII-masked insurance claim and return a structured JSON assessment.

        Field rules:
        - ClaimType: exactly one of — vehicle | property | health | liability | other
        - Urgency:
            high   → life/safety risk, total loss, fraud suspected, extreme distress
            medium → significant damage, partial info missing, elevated emotion
            low    → minor/routine damage, all info present, calm tone
        - Sentiment: exactly one of — positive | neutral | frustrated | distressed
        - EstimatedAmount: numeric NIS value extracted from ₪/NIS expressions; 0 if absent
        - MissingInfo: list items required for this claim type that are absent.
            Common items: police_report, photos, repair_estimate, medical_records,
                          receipts, witness_statement, damage_assessment
        - FraudIndicators: true if the claim shows implausible amounts, timeline gaps,
            vague descriptions of expensive losses, or pressure language
        - SafeToAutoApprove: true ONLY IF MissingInfo is empty AND FraudIndicators is false
            AND EstimatedAmount > 0 AND EstimatedAmount <= 10000

        Return ONLY valid JSON matching the ClaimClassification schema. No prose, no markdown.
        """;

    /// <summary>
    /// Builds an executor binding that wraps a ChatClientAgent for structured classification.
    /// Each invocation creates a fresh session so claims are independent.
    /// </summary>
    public static ExecutorBinding Build(IChatClient client)
    {
        // Constructor: (chatClient, instructions, name, description, tools, loggerFactory, services)
        var agent = new ChatClientAgent(client, SystemPrompt, "classifier", null, null, null, null);

        Func<PreprocessedClaim, IWorkflowContext, CancellationToken, ValueTask<ClaimClassification>> handler =
            async (claim, ctx, ct) =>
            {
                var prompt = $"""
                    Claim ID: {claim.ClaimId}
                    Policy: {claim.PolicyNumber}
                    Claim text: {claim.MaskedText}
                    """;

                // Pass null session so MAF creates a fresh stateless session per invocation.
                // CreateSessionAsync requires service-managed history which Ollama does not support.
                var response = await agent.RunAsync<ClaimClassification>(
                    prompt, null, (JsonSerializerOptions?)null, (ChatClientAgentRunOptions?)null, ct);

                return response.Result;
            };

        return handler.BindAsExecutor("classifier", ExecutorOptions.Default, false);
    }
}
