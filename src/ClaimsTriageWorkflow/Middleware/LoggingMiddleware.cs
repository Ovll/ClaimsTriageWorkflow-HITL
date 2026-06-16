using Microsoft.Agents.AI.Workflows;

namespace ClaimsTriageWorkflow.Middleware;

/// <summary>
/// Formats WorkflowEvents for console logging. Called from the Program.cs event loop;
/// the MAF runtime emits ExecutorInvokedEvent / ExecutorCompletedEvent for every node
/// automatically — no per-executor wiring required.
/// </summary>
public static class LoggingMiddleware
{
    /// <summary>Returns the log line for an ExecutorInvokedEvent, or null for other event types.</summary>
    public static string? GetInvokedMessage(WorkflowEvent evt)
        => evt is ExecutorInvokedEvent invoked ? $"[{invoked.ExecutorId}] invoked" : null;

    /// <summary>Returns the base log line for an ExecutorCompletedEvent (without enrichment suffix).</summary>
    public static string? GetCompletedMessage(WorkflowEvent evt)
        => evt is ExecutorCompletedEvent completed ? $"[{completed.ExecutorId}] completed" : null;
}
