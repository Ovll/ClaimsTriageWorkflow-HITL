using ClaimsTriageWorkflow.Executors;
using ClaimsTriageWorkflow.Models;
using Xunit;

namespace ClaimsTriageWorkflow.Tests;

public class RouterTests
{
    [Fact]
    public void Router_output_is_same_object_as_input()
    {
        var classification = new ClaimClassification { ClaimType = ClaimType.Vehicle, Urgency = UrgencyLevel.Medium };
        var result = Router.Passthrough(classification);
        Assert.Same(classification, result);
    }

    [Fact]
    public void Router_does_not_modify_fields()
    {
        var classification = new ClaimClassification
        {
            ClaimType = ClaimType.Property,
            Urgency = UrgencyLevel.High,
            Sentiment = SentimentType.Distressed,
            EstimatedAmount = 50_000m,
            FraudIndicators = false,
            SafeToAutoApprove = false,
        };
        classification.MissingInfo.Add("police_report");

        var result = Router.Passthrough(classification);

        Assert.Equal(ClaimType.Property, result.ClaimType);
        Assert.Equal(UrgencyLevel.High, result.Urgency);
        Assert.Equal(SentimentType.Distressed, result.Sentiment);
        Assert.Equal(50_000m, result.EstimatedAmount);
        Assert.False(result.FraudIndicators);
        Assert.False(result.SafeToAutoApprove);
        Assert.Single(result.MissingInfo);
    }

    [Fact]
    public async Task Handle_returns_same_classification_via_context()
    {
        var classification = new ClaimClassification { ClaimType = ClaimType.Health };
        var ctx = new FakeWorkflowContext();
        var result = await Router.Handle(classification, ctx, CancellationToken.None);
        Assert.Same(classification, result);
    }
}
