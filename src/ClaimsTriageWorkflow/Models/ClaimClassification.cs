namespace ClaimsTriageWorkflow.Models;

/// <summary>Typed classifier output produced by the LLM classifier agent.</summary>
public class ClaimClassification
{
    /// <summary>Type of claim: Vehicle | Property | Health | Liability | Other</summary>
    public ClaimType ClaimType { get; set; }

    /// <summary>Urgency level: High | Medium | Low</summary>
    public UrgencyLevel Urgency { get; set; }

    /// <summary>Customer sentiment: Positive | Neutral | Frustrated | Distressed</summary>
    public SentimentType Sentiment { get; set; }

    /// <summary>Estimated claim amount in NIS; 0 if not stated in claim text</summary>
    public decimal EstimatedAmount { get; set; }

    /// <summary>Missing items required for this claim type, e.g. police_report, photos, repair_estimate</summary>
    public List<string> MissingInfo { get; set; } = new();

    /// <summary>True if claim exhibits suspicious patterns: implausible amounts, timeline gaps, inconsistent details</summary>
    public bool FraudIndicators { get; set; }

    /// <summary>True only when MissingInfo is empty, FraudIndicators is false, and EstimatedAmount is within threshold</summary>
    public bool SafeToAutoApprove { get; set; }

    /// <summary>One-sentence model explanation of the key signals that drove this classification</summary>
    public string ClassificationRationale { get; set; } = string.Empty;

    /// <summary>Model self-assessed certainty from 0.0 (completely uncertain) to 1.0 (unambiguous). Below Constants.ConfidenceThreshold triggers escalation.</summary>
    public double ClassificationConfidence { get; set; }

    /// <summary>Deterministic pre-screen flags attached by the classifier handler after LLM deserialization. The LLM does not populate this field.</summary>
    public PreScreenFlags PreScreenFlags { get; set; } = PreScreenFlags.None;
}
