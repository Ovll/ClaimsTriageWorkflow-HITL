namespace ClaimsTriageWorkflow.Models;

/// <summary>Immutable record capturing the complete routing decision for one claim run.</summary>
public record AuditRecord(
    string ClaimId,
    string PolicyNumber,
    string MaskedText,
    ClaimClassification Classification,
    PreScreenFlags PreScreenFlags,
    string Route,              // "auto_approve" | "request_more_info" | "escalate_to_adjuster"
    string? EscalationReason,  // "red_flag" | "low_confidence" | "high_amount" | "fraud_flag" | "high_urgency" | null
    DateTimeOffset Timestamp
);
