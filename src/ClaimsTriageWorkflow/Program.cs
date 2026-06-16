using ClaimsTriageWorkflow;
using ClaimsTriageWorkflow.Middleware;
using ClaimsTriageWorkflow.Models;
using ClaimsTriageWorkflow.Workflow;
using DotNetEnv;
using Microsoft.Agents.AI.Workflows;

Env.Load();

var chatClient = ChatClientFactory.Create();
var workflow   = ClaimsWorkflow.Build(chatClient);
var env        = InProcessExecution.Default;

var claims = new[]
{
    new InboundClaim(
        Guid.NewGuid().ToString(),
        "Policy #IL-2201. Small water leak under the kitchen sink, fixed already, receipt for ₪800 attached."
    ),
    new InboundClaim(
        Guid.NewGuid().ToString(),
        "Hi, I had a car accident yesterday. Policy is IL-5540. I need to file a claim."
    ),
    new InboundClaim(
        Guid.NewGuid().ToString(),
        "My entire warehouse burned down last night. Policy IL-9910. Loss is in the millions. I'm desperate, please help immediately."
    ),
};

foreach (var claim in claims)
{
    Console.WriteLine(new string('=', 60));
    Console.WriteLine($"Input: {claim.RawText}");
    Console.WriteLine(new string('=', 60));

    string? policyNumber    = null;
    string? finalDisposition = null;

    // await using ensures ownership is released even if the stream errors mid-run.
    await using var run = await env.RunStreamingAsync(workflow, claim, Guid.NewGuid().ToString());
    await foreach (var evt in run.WatchStreamAsync(CancellationToken.None))
    {
        // Track the preprocessor output so later events can refer to the policy-based ID.
        if (evt is ExecutorCompletedEvent { ExecutorId: "preprocessor" } pre
            && pre.Data is PreprocessedClaim pc)
        {
            policyNumber = pc.PolicyNumber;
        }

        // Surface any executor-level failures so they don't silently swallow errors.
        if (evt is ExecutorFailedEvent failed)
        {
            Console.Error.WriteLine($"[{failed.ExecutorId}] FAILED: {failed.Data?.Message}");
            continue;
        }

        var invokedLine    = LoggingMiddleware.GetInvokedMessage(evt);
        var completedBase  = LoggingMiddleware.GetCompletedMessage(evt);

        if (invokedLine is not null)
            Console.WriteLine(invokedLine);

        if (completedBase is not null)
        {
            var suffix = CompletionSuffix(evt);
            Console.WriteLine(completedBase + suffix);

            // Record the outcome so we can print the final disposition once streaming ends.
            if (evt is ExecutorCompletedEvent completed)
            {
                finalDisposition = completed.ExecutorId switch
                {
                    "escalation_handler"    => $"claim {policyNumber} escalated to human adjuster queue",
                    "auto_responder_approve" => $"claim {policyNumber} auto-approved",
                    "auto_responder_info"   => $"claim {policyNumber} pending — additional info requested",
                    _                       => finalDisposition,
                };
            }
        }
    }

    if (finalDisposition is not null)
        Console.WriteLine($"Final: {finalDisposition}");

    Console.WriteLine();
}

// ── Helpers ──────────────────────────────────────────────────────────────────

static string CompletionSuffix(WorkflowEvent evt)
{
    if (evt is not ExecutorCompletedEvent completed) return string.Empty;

    return completed.ExecutorId switch
    {
        "classifier" when completed.Data is ClaimClassification cls1
            => FormatClassifierSummary(cls1),

        "router" when completed.Data is ClaimClassification cls2
            => $" — route: {ComputeRoute(cls2)}",

        _ => string.Empty,
    };
}

static string FormatClassifierSummary(ClaimClassification c)
{
    var route = ComputeRoute(c);
    return $" — {c.ClaimType} / {c.Urgency} / {c.Sentiment} / ₪{c.EstimatedAmount} / {route}";
}

static string ComputeRoute(ClaimClassification c)
    => RoutingConditions.ShouldEscalate(c)    ? "escalate_to_adjuster"
     : RoutingConditions.ShouldRequestInfo(c) ? "request_more_info"
     : "auto_approve";
