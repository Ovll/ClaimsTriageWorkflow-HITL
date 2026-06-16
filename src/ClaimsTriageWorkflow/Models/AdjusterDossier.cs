namespace ClaimsTriageWorkflow.Models;

/// <summary>Payload pushed to the human adjuster inbox by EscalationHandler.</summary>
public record AdjusterDossier(
    string ClaimId,
    string PolicyNumber,
    ClaimClassification Classification,
    string MaskedText,
    string EscalationReason   // "high_urgency" | "fraud_flag" | "high_amount"
);
