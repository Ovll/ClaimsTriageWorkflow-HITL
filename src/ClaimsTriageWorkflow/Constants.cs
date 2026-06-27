namespace ClaimsTriageWorkflow;

public static class Constants
{
    // Computed properties re-read env vars on each access so tests can set/unset them without AppDomain reload.
    public static decimal AmountThreshold =>
        decimal.TryParse(Environment.GetEnvironmentVariable("AMOUNT_THRESHOLD"), out var v) ? v : 10_000m;

    public static double ConfidenceThreshold =>
        double.TryParse(Environment.GetEnvironmentVariable("CONFIDENCE_THRESHOLD"), out var v) ? v : 0.65;
}
