using System.Text.RegularExpressions;
using ClaimsTriageWorkflow.Models;
using Microsoft.Agents.AI.Workflows;

namespace ClaimsTriageWorkflow.Executors;

public static class Preprocessor
{
    private static readonly Regex EmailRegex  = new(@"[\w.+-]+@[\w-]+\.[\w.]+",         RegexOptions.Compiled);
    private static readonly Regex PhoneRegex  = new(@"\+?\d[\d\s\-(). ]{7,15}\d",       RegexOptions.Compiled);
    private static readonly Regex PolicyRegex = new(@"IL-\d+",                           RegexOptions.Compiled);
    // Capitalized word pairs that are likely person names.
    private static readonly Regex NameRegex   = new(@"\b([A-Z][a-z]{1,})\s+([A-Z][a-z]{1,})\b", RegexOptions.Compiled);

    // Domain terms that should NOT be treated as person names.
    private static readonly HashSet<string> NonNameWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "Policy", "Claim", "Small", "Water", "Kitchen", "Total", "Loss",
        "My", "Hi", "The", "This", "That", "Fire", "Last", "Next",
    };

    /// <summary>
    /// Pure processing logic: PII masking, policy extraction, date normalization.
    /// Called directly by unit tests so it must not touch any I/O or external services.
    /// </summary>
    public static PreprocessedClaim Process(InboundClaim claim)
    {
        var raw = claim.RawText;

        var policyMatch  = PolicyRegex.Match(raw);
        var policyNumber = policyMatch.Success ? policyMatch.Value : "UNKNOWN";

        // Use the policy number as the claim ID when it's found in the text; this
        // makes the final disposition reference the human-readable policy ID, not a UUID.
        var claimId = policyMatch.Success ? policyMatch.Value : claim.ClaimId;

        var masked = EmailRegex.Replace(raw, "[EMAIL]");
        masked     = PhoneRegex.Replace(masked, "[PHONE]");
        masked     = MaskNames(masked);

        var incidentDate = ExtractDate(raw);

        return new PreprocessedClaim(claimId, policyNumber, masked, incidentDate, raw);
    }

    /// <summary>
    /// Workflow integration: stores PreprocessedClaim in workflow state so later
    /// executors (EscalationHandler, AutoResponder) can read it without re-processing.
    /// </summary>
    public static async ValueTask<PreprocessedClaim> Handle(
        InboundClaim claim, IWorkflowContext ctx, CancellationToken ct)
    {
        var result = Process(claim);
        // Use explicit scope "run" so downstream executors can read it from the same scope.
        // The default scope is executor-specific; a named scope is shared across the workflow run.
        await ctx.QueueStateUpdateAsync("preprocessed_claim", result, "run", ct);
        return result;
    }

    public static ExecutorBinding Build()
        => ((Func<InboundClaim, IWorkflowContext, CancellationToken, ValueTask<PreprocessedClaim>>)Handle)
            .BindAsExecutor("preprocessor", ExecutorOptions.Default, false);

    // ── Private helpers ───────────────────────────────────────────────────────

    private static string MaskNames(string text)
        => NameRegex.Replace(text, m =>
        {
            var first  = m.Groups[1].Value;
            var second = m.Groups[2].Value;
            // Skip pairs where either word is a known domain/stop term.
            if (NonNameWords.Contains(first) || NonNameWords.Contains(second))
                return m.Value;
            return "[NAME]";
        });

    private static DateOnly? ExtractDate(string text)
    {
        var lower = text.ToLowerInvariant();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        if (lower.Contains("yesterday") || lower.Contains("last night"))
            return today.AddDays(-1);
        if (lower.Contains("today"))
            return today;

        // "last [weekday]"
        string[] weekdays = { "monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday" };
        foreach (var day in weekdays)
        {
            if (!lower.Contains($"last {day}")) continue;
            var target = Enum.Parse<DayOfWeek>(day, ignoreCase: true);
            var d = today.AddDays(-1);
            while (d.DayOfWeek != target) d = d.AddDays(-1);
            return d;
        }

        return null;
    }
}
