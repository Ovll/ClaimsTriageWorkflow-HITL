namespace ClaimsTriageWorkflow.Models;

/// <summary>Deterministic pre-screen flags produced by RedFlagDetector before LLM classification.</summary>
public record PreScreenFlags(
    bool TotalLoss,
    bool FireOrExplosion,
    bool FloodOrWater,
    bool FraudLanguage,
    bool HighValueLanguage,
    bool LegalLanguage)
{
    public static PreScreenFlags None { get; } =
        new(false, false, false, false, false, false);

    public bool Any =>
        TotalLoss || FireOrExplosion || FloodOrWater ||
        FraudLanguage || HighValueLanguage || LegalLanguage;
}
