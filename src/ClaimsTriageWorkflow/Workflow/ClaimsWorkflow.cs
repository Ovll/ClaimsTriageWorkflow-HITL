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
        var preprocessor    = Preprocessor.Build();
        var classifier      = ClassifierAgent.Build(client);
        var router          = Router.Build();
        var escalation      = EscalationHandler.Build();
        var approveResponder = AutoResponderAgent.Build(client, "approval");
        var infoResponder   = AutoResponderAgent.Build(client, "info_request");

        return new WorkflowBuilder(preprocessor)         // entry point
            .BindExecutor(classifier)
            .BindExecutor(router)
            .BindExecutor(escalation)
            .BindExecutor(approveResponder)
            .BindExecutor(infoResponder)

            // ── Linear path ─────────────────────────────────────────────────
            .AddEdge(preprocessor, classifier)
            .AddEdge(classifier,   router)

            // ── Conditional branches (priority order: Escalate > Info > Approve) ──
            // Wrap in nullable-accepting lambdas to satisfy WorkflowBuilder's Func<T?, bool> delegate.
            .AddEdge<ClaimClassification>(router, escalation,
                c => c != null && RoutingConditions.ShouldEscalate(c))

            .AddEdge<ClaimClassification>(router, infoResponder,
                c => c != null && RoutingConditions.ShouldRequestInfo(c))

            .AddEdge<ClaimClassification>(router, approveResponder,
                c => c != null && RoutingConditions.ShouldAutoApprove(c))

            // Mark the three terminal executors as workflow output sources.
            .WithOutputFrom(escalation, approveResponder, infoResponder)

            .Build(validateOrphans: true);
    }
}

/// <summary>
/// Pure boolean conditions used on WorkflowBuilder edges and exposed for unit testing.
/// Priority: Escalate (1) beats RequestInfo (2) beats AutoApprove (3).
/// </summary>
public static class RoutingConditions
{
    public static bool ShouldEscalate(ClaimClassification c)
        => c.Urgency == "high"
        || c.FraudIndicators
        || c.EstimatedAmount > Constants.AmountThreshold;

    public static bool ShouldRequestInfo(ClaimClassification c)
    {
        // Only fires when the escalate condition is NOT true.
        bool escalate = ShouldEscalate(c);
        return !escalate && c.MissingInfo.Count > 0 && !c.SafeToAutoApprove;
    }

    public static bool ShouldAutoApprove(ClaimClassification c)
        => c.SafeToAutoApprove && c.EstimatedAmount <= Constants.AmountThreshold;
}
