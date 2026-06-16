namespace ClaimsTriageWorkflow.Models;

/// <summary>PII-masked, structured output of the Preprocessor executor.</summary>
public record PreprocessedClaim(
    string ClaimId,          // extracted from text (policy number) or carried from InboundClaim
    string PolicyNumber,     // regex-extracted, e.g. "IL-8821"; "UNKNOWN" if absent
    string MaskedText,       // RawText with PII replaced by tokens
    DateOnly? IncidentDate,  // normalized from relative references; null if not extractable
    string OriginalText      // preserved verbatim for audit trail
);
