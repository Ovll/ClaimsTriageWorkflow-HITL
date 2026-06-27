using System.Text.Json;
using ClaimsTriageWorkflow.Agents;
using ClaimsTriageWorkflow.Executors;
using ClaimsTriageWorkflow.Models;
using ClaimsTriageWorkflow.Workflow;
using Xunit;

namespace ClaimsTriageWorkflow.Tests;

/// <summary>
/// Asserts routing for every labeled case in eval-cases.json without any LLM calls.
/// Tests priority edge cases: red_flag beats high_amount; low_confidence beats fraud_flag.
/// </summary>
public class EvalSetTests
{
    // Use the same JSON options as the classifier so enum names are deserialized correctly.
    private static readonly JsonSerializerOptions JsonOptions = ClassifierAgent.CreateJsonOptions();

    private sealed class EvalCase
    {
        public string ExpectedRoute { get; init; } = string.Empty;
        public string? ExpectedReason { get; init; }
        public ClaimClassification Input { get; init; } = new();
    }

    [Fact]
    public void All_eval_cases_route_correctly()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Eval", "eval-cases.json");
        var json = File.ReadAllText(path);
        var cases = JsonSerializer.Deserialize<List<EvalCase>>(json, JsonOptions)!;

        Assert.Equal(12, cases.Count);

        foreach (var tc in cases)
        {
            var c = tc.Input;
            var actualRoute = RoutingConditions.ShouldEscalate(c) ? "escalate_to_adjuster"
                : RoutingConditions.ShouldRequestInfo(c) ? "request_more_info"
                : "auto_approve";

            Assert.Equal(tc.ExpectedRoute, actualRoute);

            // Also assert the escalation reason for cases that specify priority ordering.
            if (tc.ExpectedReason is not null)
            {
                var reason = EscalationHandler.DetermineReason(c);
                Assert.Equal(tc.ExpectedReason, reason);
            }
        }
    }
}
