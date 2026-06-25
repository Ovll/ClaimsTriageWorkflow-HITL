namespace ClaimsTriageWorkflow;

public static class Constants
{
    // Read threshold from env so it can be changed without recompiling.
    public static readonly decimal AmountThreshold = decimal.TryParse(
        Environment.GetEnvironmentVariable("AMOUNT_THRESHOLD"),
        out var v
    )
        ? v
        : 10_000m;
}
