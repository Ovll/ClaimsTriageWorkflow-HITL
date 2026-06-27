using ClaimsTriageWorkflow.Agents;
using ClaimsTriageWorkflow.Executors;
using ClaimsTriageWorkflow.Models;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using MafWorkflow = Microsoft.Agents.AI.Workflows.Workflow;

namespace ClaimsTriageWorkflow.Workflow;

/// <summary>
/// Assembles the full claims triage workflow using WorkflowBuilder.
/// All branching logic is expressed as edge condition lambdas — executors contain no if/else.
/// </summary>
public static class ClaimsWorkflow
{
    public static MafWorkflow Build(IChatClient client)
    {
        var preprocessor = Preprocessor.Build();
        var classifier = ClassifierAgent.Build(client);
        var router = Router.Build();
        var escalation = EscalationHandler.Build();
        var approveResponder = AutoResponderAgent.Build(client, "approval");
        var infoResponder = AutoResponderAgent.Build(client, "info_request");

        // HITL gate: pauses the workflow on escalations and waits for a human
        // decision before continuing. The response is a ClaimClassification so that
        // downstream executors receive the correct type — approve returns it unchanged
        // (still satisfies ShouldEscalate), override returns a modified one that satisfies
        // ShouldAutoApprove. Using string as the response type would cause a type mismatch
        // because EscalationHandler and AutoResponderAgent both expect ClaimClassification.
        var gate = RequestPort.Create<ClaimClassification, ClaimClassification>("adjuster_gate");

        return new WorkflowBuilder(preprocessor) // entry point
            .BindExecutor(classifier)
            .BindExecutor(router)
            .BindExecutor(escalation)
            .BindExecutor(approveResponder)
            .BindExecutor(infoResponder)
            .BindExecutor(gate) // implicit RequestPort → ExecutorBinding
            // ── Linear path ─────────────────────────────────────────────────
            .AddEdge(preprocessor, classifier)
            .AddEdge(classifier, router)
            // ── Escalate branch → HITL gate (replaces direct router→escalation edge) ──
            .AddEdge<ClaimClassification>(
                router,
                gate,
                c => c != null && RoutingConditions.ShouldEscalate(c)
            )
            // ── Gate outgoing edges: reuse the same routing predicates on the
            //    returned ClaimClassification. Approve → classification unchanged →
            //    ShouldEscalate still true. Override → classification modified so that
            //    ShouldAutoApprove is true. Both downstream executors receive ClaimClassification.
            .AddEdge<ClaimClassification>(
                gate,
                escalation,
                c => c != null && RoutingConditions.ShouldEscalate(c)
            )
            .AddEdge<ClaimClassification>(
                gate,
                approveResponder,
                c => c != null && RoutingConditions.ShouldAutoApprove(c)
            )
            // ── Non-escalated branches (unchanged) ──────────────────────────
            .AddEdge<ClaimClassification>(
                router,
                infoResponder,
                c => c != null && RoutingConditions.ShouldRequestInfo(c)
            )
            .AddEdge<ClaimClassification>(
                router,
                approveResponder,
                c => c != null && RoutingConditions.ShouldAutoApprove(c)
            )
            // Mark the three terminal executors as workflow output sources.
            .WithOutputFrom(escalation, approveResponder, infoResponder)
            .Build(validateOrphans: true);
    }
}

/// <summary>
/// Pure boolean conditions on edges leading OUT of the HITL adjuster_gate.
/// Exposed as static methods for unit testing.
/// </summary>
public static class HitlConditions
{
    public const string ApproveEscalation = "approve_escalation";
    public const string OverrideToAutoApprove = "override_to_auto_approve";

    public static bool ShouldApproveEscalation(string? response) => response == ApproveEscalation;

    public static bool ShouldOverrideToAutoApprove(string? response) =>
        response == OverrideToAutoApprove;

    // Builds the gate response for override_to_auto_approve. Unknown enum values are
    // normalized to safe defaults so HasUnknownClassification returns false and
    // ShouldAutoApprove can fire. Keeping this logic here instead of inline in Program.cs
    // ensures the test and the entry point stay in sync.
    public static ClaimClassification BuildOverrideResponse(ClaimClassification? cls) =>
        new()
        {
            ClaimType = cls?.ClaimType is null or ClaimType.Unknown ? ClaimType.Other : cls.ClaimType,
            Urgency = UrgencyLevel.Low,
            Sentiment = cls?.Sentiment is null or SentimentType.Unknown ? SentimentType.Neutral : cls.Sentiment,
            EstimatedAmount = 0,
            FraudIndicators = false,
            SafeToAutoApprove = true,
            // Human explicitly overrode to auto-approve; confidence is effectively certain.
            ClassificationConfidence = 1.0,
        };
}

/// <summary>
/// Pure boolean conditions used on WorkflowBuilder edges and exposed for unit testing.
/// Priority: Escalate (1) beats RequestInfo (2) beats AutoApprove (3).
/// </summary>
public static class RoutingConditions
{
    public static bool ShouldEscalate(ClaimClassification c) =>
        HasUnknownClassification(c)
        || c.PreScreenFlags.Any
        || c.ClassificationConfidence < Constants.ConfidenceThreshold
        || c.Urgency == UrgencyLevel.High
        || c.FraudIndicators
        || c.EstimatedAmount > Constants.AmountThreshold;

    // Any Unknown enum value means the model failed to classify — escalate to a human
    // rather than auto-approve or silently misroute the claim.
    public static bool HasUnknownClassification(ClaimClassification c) =>
        c.Urgency == UrgencyLevel.Unknown
        || c.ClaimType == ClaimType.Unknown
        || c.Sentiment == SentimentType.Unknown;

    public static bool ShouldRequestInfo(ClaimClassification c)
    {
        // Only fires when the escalate condition is NOT true.
        bool escalate = ShouldEscalate(c);
        return !escalate && c.MissingInfo.Count > 0 && !c.SafeToAutoApprove;
    }

    public static bool ShouldAutoApprove(ClaimClassification c) =>
        !ShouldEscalate(c) && c.SafeToAutoApprove && c.EstimatedAmount <= Constants.AmountThreshold;
}
