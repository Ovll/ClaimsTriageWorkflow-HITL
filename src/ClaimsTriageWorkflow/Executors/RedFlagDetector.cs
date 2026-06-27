using ClaimsTriageWorkflow.Models;

namespace ClaimsTriageWorkflow.Executors;

/// <summary>
/// Scans raw claim text for high-risk keyword signals without calling any LLM or external service.
/// Each flag category covers distinct risk domains; Any is true when at least one fires.
/// </summary>
public static class RedFlagDetector
{
    public static PreScreenFlags Detect(string rawText)
    {
        var t = rawText.ToLowerInvariant();

        return new PreScreenFlags(
            TotalLoss:         t.Contains("total loss") || t.Contains("completely destroyed")
                               || t.Contains("burned down") || t.Contains("fully destroyed"),
            FireOrExplosion:   t.Contains("fire") || t.Contains("explosion") || t.Contains("blast")
                               || t.Contains("burned") || t.Contains("arson"),
            FloodOrWater:      t.Contains("flood") || t.Contains("flooding")
                               || t.Contains("water damage") || t.Contains("submerged"),
            FraudLanguage:     t.Contains("fraud") || t.Contains("staged")
                               || t.Contains("fabricated") || t.Contains("fake claim"),
            HighValueLanguage: t.Contains("millions") || t.Contains("tens of thousands")
                               || t.Contains("enormous loss"),
            LegalLanguage:     t.Contains("lawsuit") || t.Contains("attorney")
                               || t.Contains("legal action") || t.Contains("sue")
        );
    }
}
