using ClaimsTriageWorkflow.Executors;
using ClaimsTriageWorkflow.Models;
using Xunit;

namespace ClaimsTriageWorkflow.Tests;

public class RouterTests
{
    [Fact]
    public void Router_output_is_same_object_as_input()
    {
        var classification = new ClaimClassification { ClaimType = "vehicle", Urgency = "medium" };
        var result = Router.Passthrough(classification);
        Assert.Same(classification, result);
    }

    [Fact]
    public void Router_does_not_modify_fields()
    {
        var classification = new ClaimClassification
        {
            ClaimType = "property",
            Urgency = "high",
            Sentiment = "distressed",
            EstimatedAmount = 50_000m,
            FraudIndicators = false,
            SafeToAutoApprove = false,
        };
        classification.MissingInfo.Add("police_report");

        var result = Router.Passthrough(classification);

        Assert.Equal("property", result.ClaimType);
        Assert.Equal("high", result.Urgency);
        Assert.Equal("distressed", result.Sentiment);
        Assert.Equal(50_000m, result.EstimatedAmount);
        Assert.False(result.FraudIndicators);
        Assert.False(result.SafeToAutoApprove);
        Assert.Single(result.MissingInfo);
    }

    [Fact]
    public async Task Handle_returns_same_classification_via_context()
    {
        var classification = new ClaimClassification { ClaimType = "health" };
        var ctx = new FakeWorkflowContext();
        var result = await Router.Handle(classification, ctx, CancellationToken.None);
        Assert.Same(classification, result);
    }
}
