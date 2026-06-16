using ClaimsTriageWorkflow.Models;
using Microsoft.Agents.AI.Workflows;

namespace ClaimsTriageWorkflow.Executors;

/// <summary>
/// Passes ClaimClassification downstream unchanged.
/// All routing logic lives on WorkflowBuilder edge conditions — NOT here.
/// </summary>
public static class Router
{
    // Pure passthrough — testable without any workflow context.
    public static ClaimClassification Passthrough(ClaimClassification c) => c;

    public static ValueTask<ClaimClassification> Handle(
        ClaimClassification c, IWorkflowContext ctx, CancellationToken ct)
        => ValueTask.FromResult(Passthrough(c));

    public static ExecutorBinding Build()
        => ((Func<ClaimClassification, IWorkflowContext, CancellationToken, ValueTask<ClaimClassification>>)Handle)
            .BindAsExecutor("router", ExecutorOptions.Default, false);
}
