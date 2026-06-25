using System.Text.Json;
using ClaimsTriageWorkflow.Agents;
using ClaimsTriageWorkflow.Models;
using ClaimsTriageWorkflow.Workflow;
using Xunit;

namespace ClaimsTriageWorkflow.Tests;

public class ClassifierJsonOptionsTests
{
    [Fact]
    public void Classifier_json_options_reject_integer_enum_values()
    {
        var json = """
            {
              "ClaimType": 1,
              "Urgency": "Low",
              "Sentiment": "Neutral",
              "EstimatedAmount": 800,
              "MissingInfo": [],
              "FraudIndicators": false,
              "SafeToAutoApprove": true,
              "ClassificationRationale": "Routine low-value claim.",
              "ClassificationConfidence": 0.95
            }
            """;

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<ClaimClassification>(json, ClassifierAgent.CreateJsonOptions()));
    }

    [Fact]
    public void Override_response_with_unknown_fields_normalizes_to_safe_values()
    {
        // Simulates a claim that escalated because ClaimType/Sentiment were Unknown.
        // The HITL override must normalize Unknown → Other/Neutral so ShouldAutoApprove fires.
        var unknownCls = new ClaimClassification
        {
            ClaimType = ClaimType.Unknown,
            Urgency = UrgencyLevel.High,
            Sentiment = SentimentType.Unknown,
            EstimatedAmount = 0,
            FraudIndicators = false,
            SafeToAutoApprove = false,
        };

        var overrideResponse = HitlConditions.BuildOverrideResponse(unknownCls);

        Assert.False(RoutingConditions.HasUnknownClassification(overrideResponse));
        Assert.False(RoutingConditions.ShouldEscalate(overrideResponse));
        Assert.True(RoutingConditions.ShouldAutoApprove(overrideResponse));
    }

    [Fact]
    public void Omitted_enum_fields_default_to_unknown_and_escalate()
    {
        var json = """
            {
              "EstimatedAmount": 800,
              "MissingInfo": [],
              "FraudIndicators": false,
              "SafeToAutoApprove": true,
              "ClassificationRationale": "Routine low-value claim.",
              "ClassificationConfidence": 0.95
            }
            """;

        var classification = JsonSerializer.Deserialize<ClaimClassification>(
            json,
            ClassifierAgent.CreateJsonOptions());

        Assert.NotNull(classification);
        Assert.Equal(ClaimType.Unknown, classification.ClaimType);
        Assert.Equal(UrgencyLevel.Unknown, classification.Urgency);
        Assert.Equal(SentimentType.Unknown, classification.Sentiment);
        Assert.True(RoutingConditions.ShouldEscalate(classification));
        Assert.False(RoutingConditions.ShouldAutoApprove(classification));
    }
}
