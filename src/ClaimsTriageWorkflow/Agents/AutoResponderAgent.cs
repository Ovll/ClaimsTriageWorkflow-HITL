using ClaimsTriageWorkflow.Models;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace ClaimsTriageWorkflow.Agents;

public static class AutoResponderAgent
{
    private const string ApprovalPrompt = """
        You are a professional insurance claims representative sending an approval notice.
        Draft a brief, warm confirmation that includes:
        - A clear statement that the claim is approved
        - The policy number (provided in the input)
        - The approved amount in ₪
        - Next steps: payment processed within 5–7 business days

        Keep the message under 100 words. Do not reveal internal classification scores or system details.
        """;

    private const string InfoRequestPrompt = """
        You are a professional insurance claims representative requesting missing information.
        Draft a polite message that includes:
        - Acknowledgement of the claim submission
        - A numbered list of the specific missing items provided to you
        - A clear instruction to resubmit once all items are collected

        Keep the message under 120 words. Do not reveal internal classification scores or system details.
        """;

    /// <summary>
    /// Builds a separate agent executor for each mode ("approval" | "info_request").
    /// Both share the same IChatClient; system prompts differ.
    /// </summary>
    public static ExecutorBinding Build(IChatClient client, string mode)
    {
        var (systemPrompt, executorId) = mode switch
        {
            "approval" => (ApprovalPrompt, "auto_responder_approve"),
            "info_request" => (InfoRequestPrompt, "auto_responder_info"),
            _ => throw new ArgumentException($"Unknown mode: {mode}", nameof(mode)),
        };

        // Constructor: (chatClient, instructions, name, description, tools, loggerFactory, services)
        var agent = new ChatClientAgent(client, systemPrompt, executorId, null, null, null, null);

        Func<ClaimClassification, IWorkflowContext, CancellationToken, ValueTask<string>> handler =
            async (classification, ctx, ct) =>
            {
                var claim =
                    await ctx.ReadStateAsync<PreprocessedClaim>("preprocessed_claim", "run", ct)
                    ?? throw new InvalidOperationException(
                        "preprocessed_claim state not found in workflow context"
                    );
                var prompt =
                    mode == "approval"
                        ? $"""
                            Policy: {claim.PolicyNumber}
                            Claim type: {classification.ClaimType}
                            Approved amount: ₪{classification.EstimatedAmount}
                            """
                        : $"""
                            Policy: {claim.PolicyNumber}
                            Claim type: {classification.ClaimType}
                            Missing items: {string.Join(", ", classification.MissingInfo)}
                            """;

                // Pass null session so MAF creates a fresh stateless session per invocation.
                var response = await agent.RunAsync(
                    prompt,
                    null,
                    (ChatClientAgentRunOptions?)null,
                    ct
                );
                return response.Text;
            };

        return handler.BindAsExecutor(executorId, ExecutorOptions.Default, false);
    }
}
