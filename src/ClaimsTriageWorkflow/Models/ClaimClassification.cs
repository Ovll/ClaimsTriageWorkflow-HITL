namespace ClaimsTriageWorkflow.Models;

/// <summary>Typed classifier output produced by the LLM classifier agent.</summary>
public class ClaimClassification
{
    /// <summary>Type of claim: vehicle | property | health | liability | other</summary>
    public string ClaimType { get; set; } = string.Empty;

    /// <summary>Urgency level: high | medium | low</summary>
    public string Urgency { get; set; } = string.Empty;

    /// <summary>Customer sentiment: positive | neutral | frustrated | distressed</summary>
    public string Sentiment { get; set; } = string.Empty;

    /// <summary>Estimated claim amount in NIS; 0 if not stated in claim text</summary>
    public decimal EstimatedAmount { get; set; }

    /// <summary>Missing items required for this claim type, e.g. police_report, photos, repair_estimate</summary>
    public List<string> MissingInfo { get; set; } = new();

    /// <summary>True if claim exhibits suspicious patterns: implausible amounts, timeline gaps, inconsistent details</summary>
    public bool FraudIndicators { get; set; }

    /// <summary>True only when MissingInfo is empty, FraudIndicators is false, and EstimatedAmount is within threshold</summary>
    public bool SafeToAutoApprove { get; set; }
}
