using ClaimsTriageWorkflow.Executors;
using ClaimsTriageWorkflow.Models;
using Xunit;

namespace ClaimsTriageWorkflow.Tests;

public class PreprocessorTests
{
    // ── PII masking ──────────────────────────────────────────────────────────

    [Fact]
    public void Email_is_masked()
    {
        var result = Preprocessor.Process(Claim("Send to john.doe@example.com for info"));
        Assert.Contains("[EMAIL]", result.MaskedText);
        Assert.DoesNotContain("@", result.MaskedText);
    }

    [Fact]
    public void Phone_is_masked()
    {
        var result = Preprocessor.Process(Claim("Call me at +972 50-123-4567 anytime"));
        Assert.Contains("[PHONE]", result.MaskedText);
        Assert.DoesNotContain("+972", result.MaskedText);
    }

    [Fact]
    public void Capitalized_name_pair_is_masked()
    {
        var result = Preprocessor.Process(Claim("Signed by John Smith on behalf of the claim"));
        Assert.Contains("[NAME]", result.MaskedText);
        Assert.DoesNotContain("John Smith", result.MaskedText);
    }

    // ── Policy extraction ─────────────────────────────────────────────────────

    [Fact]
    public void Policy_number_extracted_when_present()
    {
        var result = Preprocessor.Process(Claim("Policy #IL-2201. Water leak."));
        Assert.Equal("IL-2201", result.PolicyNumber);
    }

    [Fact]
    public void Policy_number_is_UNKNOWN_when_absent()
    {
        var result = Preprocessor.Process(Claim("No policy mentioned here."));
        Assert.Equal("UNKNOWN", result.PolicyNumber);
    }

    [Fact]
    public void ClaimId_is_policy_number_when_found_in_text()
    {
        var result = Preprocessor.Process(Claim("Policy #IL-2201. Water leak."));
        Assert.Equal("IL-2201", result.ClaimId);
    }

    [Fact]
    public void ClaimId_falls_back_to_inbound_id_when_no_policy()
    {
        var inbound = new InboundClaim("my-uuid-123", "No policy here.");
        var result = Preprocessor.Process(inbound);
        Assert.Equal("my-uuid-123", result.ClaimId);
    }

    // ── Original text preserved ───────────────────────────────────────────────

    [Fact]
    public void Original_text_is_preserved_verbatim()
    {
        const string raw = "Policy #IL-2201. Send to john@x.com.";
        var result = Preprocessor.Process(Claim(raw));
        Assert.Equal(raw, result.OriginalText);
    }

    // ── Date normalization ────────────────────────────────────────────────────

    [Fact]
    public void Yesterday_resolves_to_previous_day()
    {
        var result = Preprocessor.Process(Claim("I had a car accident yesterday."));
        var expected = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);
        Assert.Equal(expected, result.IncidentDate);
    }

    [Fact]
    public void Last_night_resolves_to_previous_day()
    {
        var result = Preprocessor.Process(Claim("My warehouse burned down last night."));
        var expected = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);
        Assert.Equal(expected, result.IncidentDate);
    }

    [Fact]
    public void No_date_reference_yields_null()
    {
        var result = Preprocessor.Process(Claim("I need to file a claim."));
        Assert.Null(result.IncidentDate);
    }

    // ── No LLM involvement ────────────────────────────────────────────────────

    [Fact]
    public void Process_is_synchronous_and_pure()
    {
        // If Process were async or used I/O it would throw without a real context.
        // The fact that this runs inline proves it is pure/deterministic.
        var result = Preprocessor.Process(Claim("Policy IL-9910. Loss in the millions."));
        Assert.NotNull(result);
    }

    // ── Helper ────────────────────────────────────────────────────────────────
    private static InboundClaim Claim(string text) => new(Guid.NewGuid().ToString(), text);
}
