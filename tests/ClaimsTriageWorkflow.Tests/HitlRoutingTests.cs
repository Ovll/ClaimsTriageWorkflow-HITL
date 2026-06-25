using ClaimsTriageWorkflow.Models;
using ClaimsTriageWorkflow.Workflow;
using Xunit;

namespace ClaimsTriageWorkflow.Tests;

public class HitlRoutingTests
{
    // ── Gate → escalation_handler ─────────────────────────────────────────────

    [Fact]
    public void ApproveEscalation_routes_to_escalation_handler()
    {
        Assert.True(HitlConditions.ShouldApproveEscalation("approve_escalation"));
    }

    [Fact]
    public void ApproveEscalation_does_not_route_to_auto_approve()
    {
        Assert.False(HitlConditions.ShouldOverrideToAutoApprove("approve_escalation"));
    }

    // ── Gate → auto_responder_approve ─────────────────────────────────────────

    [Fact]
    public void OverrideToAutoApprove_routes_to_auto_responder()
    {
        Assert.True(HitlConditions.ShouldOverrideToAutoApprove("override_to_auto_approve"));
    }

    [Fact]
    public void OverrideToAutoApprove_does_not_route_to_escalation()
    {
        Assert.False(HitlConditions.ShouldApproveEscalation("override_to_auto_approve"));
    }

    // ── Edge cases ────────────────────────────────────────────────────────────

    [Fact]
    public void Null_response_does_not_route_to_either_branch()
    {
        Assert.False(HitlConditions.ShouldApproveEscalation(null));
        Assert.False(HitlConditions.ShouldOverrideToAutoApprove(null));
    }

    [Fact]
    public void Unknown_response_does_not_route_to_either_branch()
    {
        Assert.False(HitlConditions.ShouldApproveEscalation("something_else"));
        Assert.False(HitlConditions.ShouldOverrideToAutoApprove("something_else"));
    }

    [Fact]
    public void Override_response_from_null_classification_routes_to_auto_approve()
    {
        var response = HitlConditions.BuildOverrideResponse(null);

        Assert.Equal(ClaimType.Other, response.ClaimType);
        Assert.Equal(UrgencyLevel.Low, response.Urgency);
        Assert.Equal(SentimentType.Neutral, response.Sentiment);
        Assert.False(response.FraudIndicators);
        Assert.True(response.SafeToAutoApprove);
        Assert.True(RoutingConditions.ShouldAutoApprove(response));
    }
}
