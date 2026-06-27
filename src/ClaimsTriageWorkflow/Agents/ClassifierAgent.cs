using System.Text.Json;
using System.Text.Json.Serialization;
using ClaimsTriageWorkflow.Models;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace ClaimsTriageWorkflow.Agents;

public static class ClassifierAgent
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public static JsonSerializerOptions CreateJsonOptions()
    {
        // allowIntegerValues: false — reject numeric enum values so only named strings are valid.
        return new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
        };
    }

    private const string SystemPrompt = """
        You are an insurance claims classification specialist. You receive a pre-processed,
        PII-masked insurance claim and return a structured JSON assessment.

        Field rules:
        - ClaimType: exactly one of — Vehicle | Property | Health | Liability | Other
        - Urgency:
            High   → life/safety risk, total loss, fraud suspected, extreme distress
            Medium → significant damage, partial info missing, elevated emotion
            Low    → minor/routine damage, all info present, calm tone
        - Sentiment: exactly one of — Positive | Neutral | Frustrated | Distressed
        - EstimatedAmount: numeric NIS value extracted from ₪/NIS expressions; 0 if absent
        - MissingInfo: list items required for this claim type that are absent.
            Common items: police_report, photos, repair_estimate, medical_records,
                          receipts, witness_statement, damage_assessment
        - FraudIndicators: true if the claim shows implausible amounts, timeline gaps,
            vague descriptions of expensive losses, or pressure language
        - SafeToAutoApprove: true ONLY IF MissingInfo is empty AND FraudIndicators is false
            AND EstimatedAmount > 0 AND EstimatedAmount <= 10000
        - ClassificationRationale: exactly one sentence naming the key signal(s) that drove
            the classification (e.g. "Total loss warehouse fire with extreme distress language.")
        - ClassificationConfidence: a number between 0.0 and 1.0 where 1.0 means the claim
            text unambiguously supports all field values and 0.0 means the text is too vague
            to classify with any certainty

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
                    prompt, null, JsonOptions, (ChatClientAgentRunOptions?)null, ct);

                var cls = response.Result;
                // LLM does not populate PreScreenFlags; attach deterministic flags from the preprocessor.
                cls.PreScreenFlags = claim.PreScreenFlags;
                return cls;
            };

        return handler.BindAsExecutor("classifier", ExecutorOptions.Default, false);
    }
}
