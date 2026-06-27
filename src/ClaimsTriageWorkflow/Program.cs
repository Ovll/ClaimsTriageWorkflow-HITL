using ClaimsTriageWorkflow;
using ClaimsTriageWorkflow.Executors;
using ClaimsTriageWorkflow.Middleware;
using ClaimsTriageWorkflow.Models;
using ClaimsTriageWorkflow.Workflow;
using DotNetEnv;
using Microsoft.Agents.AI.Workflows;
using Microsoft.VisualBasic.FileIO;

Env.Load();

var chatClient = ChatClientFactory.Create();
var workflow = ClaimsWorkflow.Build(chatClient);
var env = InProcessExecution.Default;

foreach (var claim in ResolveClaims(args))
{
    Console.WriteLine(new string('=', 60));
    Console.WriteLine($"Input: {claim.RawText}");
    Console.WriteLine(new string('=', 60));

    string? policyNumber = null;
    string? claimId = null;
    string? finalDisposition = null;
    string? maskedText = null;
    ClaimClassification? classification = null;
    string? route = null;
    string? escalationReason = null;

    // await using ensures ownership is released even if the stream errors mid-run.
    await using var run = await env.RunStreamingAsync(workflow, claim, Guid.NewGuid().ToString());
    await foreach (var evt in run.WatchStreamAsync(CancellationToken.None))
    {
        // Track the preprocessor output so later events can refer to the policy-based ID.
        if (evt is ExecutorCompletedEvent { ExecutorId: "preprocessor" } pre
            && pre.Data is PreprocessedClaim pc)
        {
            policyNumber = pc.PolicyNumber;
            claimId = pc.ClaimId;
            maskedText = pc.MaskedText;
        }

        // Failed classifier output must not produce a route.
        // Surfacing to stderr and continuing the event loop skips all routing for this claim.
        if (evt is ExecutorFailedEvent failed)
        {
            Console.Error.WriteLine($"[{failed.ExecutorId}] FAILED: {failed.Data?.Message}");
            continue;
        }

        // ── HITL gate: pause, prompt the human, then resume ─────────────────
        if (evt is RequestInfoEvent hitl)
        {
            var req = hitl.Request;
            req.TryGetDataAs<ClaimClassification>(out var cls);

            Console.WriteLine();
            Console.WriteLine(
                $"[adjuster_gate] HITL prompt — Claim {claimId} (Policy {policyNumber})"
            );
            Console.WriteLine(
                $"  urgency={cls?.Urgency}, fraud={cls?.FraudIndicators}, amount=₪{cls?.EstimatedAmount}"
            );
            Console.WriteLine(
                $"  rationale={cls?.ClassificationRationale} confidence={cls?.ClassificationConfidence:F2}"
            );
            Console.WriteLine(
                $"  Options: {HitlConditions.ApproveEscalation} | {HitlConditions.OverrideToAutoApprove}"
            );
            Console.Write("  > ");

            var decision = Console.ReadLine()?.Trim();

            // Gate response is ClaimClassification so downstream executors receive the correct
            // type. Approve → return original (still satisfies ShouldEscalate → escalation_handler).
            // Override → return a modified classification that satisfies ShouldAutoApprove
            //            → auto_responder_approve sends an approval reply instead.
            ClaimClassification gateResponse;
            if (HitlConditions.ShouldOverrideToAutoApprove(decision))
            {
                gateResponse = HitlConditions.BuildOverrideResponse(cls);
            }
            else
            {
                // approve_escalation or any unrecognised input → keep original classification
                if (decision != HitlConditions.ApproveEscalation)
                    Console.WriteLine(
                        $"  (unrecognised input — defaulting to {HitlConditions.ApproveEscalation})"
                    );
                gateResponse = cls!;
            }

            Console.WriteLine();
            await run.SendResponseAsync(req.CreateResponse(gateResponse));
            // WatchStreamAsync resumes automatically after SendResponseAsync returns.
            continue;
        }

        var invokedLine = LoggingMiddleware.GetInvokedMessage(evt);
        var completedBase = LoggingMiddleware.GetCompletedMessage(evt);

        if (invokedLine is not null)
            Console.WriteLine(invokedLine);

        if (completedBase is not null)
        {
            var suffix = CompletionSuffix(evt);
            Console.WriteLine(completedBase + suffix);

            // Record the outcome so we can print the final disposition once streaming ends.
            if (evt is ExecutorCompletedEvent completed)
            {
                // Capture classifier output for the audit record.
                if (completed.ExecutorId == "classifier" && completed.Data is ClaimClassification cls2)
                    classification = cls2;

                finalDisposition = completed.ExecutorId switch
                {
                    "escalation_handler" =>
                        $"claim {policyNumber} escalated to human adjuster queue",
                    "auto_responder_approve" => $"claim {policyNumber} auto-approved",
                    "auto_responder_info" =>
                        $"claim {policyNumber} pending — additional info requested",
                    _ => finalDisposition,
                };

                // Track route + escalation reason for audit log.
                if (completed.ExecutorId == "escalation_handler"
                    && completed.Data is AdjusterDossier dossier)
                {
                    route = "escalate_to_adjuster";
                    escalationReason = dossier.EscalationReason;
                }
                else if (completed.ExecutorId == "auto_responder_approve")
                    route = "auto_approve";
                else if (completed.ExecutorId == "auto_responder_info")
                    route = "request_more_info";
            }
        }
    }

    if (finalDisposition is not null)
        Console.WriteLine($"Final: {finalDisposition}");

    Console.WriteLine();

    // Append audit record only when a route was determined; failed classifier produces no record.
    if (route is not null && classification is not null)
    {
        var record = new AuditRecord(
            ClaimId: claimId ?? "UNKNOWN",
            PolicyNumber: policyNumber ?? "UNKNOWN",
            MaskedText: maskedText ?? string.Empty,
            Classification: classification,
            PreScreenFlags: classification.PreScreenFlags,
            Route: route,
            EscalationReason: escalationReason,
            Timestamp: DateTimeOffset.UtcNow
        );
        var logPath = Environment.GetEnvironmentVariable("AUDIT_LOG_PATH") ?? "audit.log";
        await AuditLogger.AppendAsync(record, logPath);
    }
}

// ── Helpers ──────────────────────────────────────────────────────────────────

static string CompletionSuffix(WorkflowEvent evt)
{
    if (evt is not ExecutorCompletedEvent completed)
        return string.Empty;

    return completed.ExecutorId switch
    {
        "classifier" when completed.Data is ClaimClassification cls1 => FormatClassifierSummary(
            cls1
        ),

        "router" when completed.Data is ClaimClassification cls2 =>
            $" — route: {ComputeRoute(cls2)}",

        _ => string.Empty,
    };
}

static string FormatClassifierSummary(ClaimClassification c)
{
    var route = ComputeRoute(c);
    return $" — {c.ClaimType} / {c.Urgency} / {c.Sentiment} / ₪{c.EstimatedAmount} / {route}";
}

static string ComputeRoute(ClaimClassification c) =>
    RoutingConditions.ShouldEscalate(c) ? "escalate_to_adjuster"
    : RoutingConditions.ShouldRequestInfo(c) ? "request_more_info"
    : "auto_approve";

/// <summary>
/// Resolves claims from CLI args, CSV file, piped stdin, or fixture — evaluated in priority order.
/// </summary>
static IEnumerable<InboundClaim> ResolveClaims(string[] args)
{
    // --claim "text" — single inline claim
    var claimIdx = Array.IndexOf(args, "--claim");
    if (claimIdx >= 0 && claimIdx + 1 < args.Length)
    {
        yield return new InboundClaim(Guid.NewGuid().ToString(), args[claimIdx + 1]);
        yield break;
    }

    // --file path.csv — batch from CSV (TextFieldParser supports quoted fields with embedded commas)
    var fileIdx = Array.IndexOf(args, "--file");
    if (fileIdx >= 0 && fileIdx + 1 < args.Length)
    {
        var filePath = args[fileIdx + 1];
        using var parser = new TextFieldParser(filePath);
        parser.TextFieldType = FieldType.Delimited;
        parser.SetDelimiters(",");
        parser.HasFieldsEnclosedInQuotes = true;

        bool isFirstRow = true;
        while (!parser.EndOfData)
        {
            var fields = parser.ReadFields();
            if (fields is null) continue;

            // Skip header row when first field is "claimId" or "id" (case-insensitive).
            if (isFirstRow)
            {
                isFirstRow = false;
                if (fields.Length > 0 && (
                    string.Equals(fields[0], "claimId", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(fields[0], "id", StringComparison.OrdinalIgnoreCase)))
                    continue;
            }

            if (fields.Length >= 2)
                yield return new InboundClaim(fields[0], fields[1]);
        }
        yield break;
    }

    // Piped stdin — each non-empty line is one claim text
    if (Console.IsInputRedirected)
    {
        string? line;
        while ((line = Console.ReadLine()) is not null)
        {
            if (!string.IsNullOrWhiteSpace(line))
                yield return new InboundClaim(Guid.NewGuid().ToString(), line);
        }
        yield break;
    }

    // Fallback: hardcoded three-claim fixture
    foreach (var c in FixtureClaims())
        yield return c;
}

static IEnumerable<InboundClaim> FixtureClaims()
{
    yield return new InboundClaim(
        Guid.NewGuid().ToString(),
        "Policy #IL-2201. Small water leak under the kitchen sink, fixed already, receipt for ₪800 attached."
    );
    yield return new InboundClaim(
        Guid.NewGuid().ToString(),
        "Hi, I had a car accident yesterday. Policy is IL-5540. I need to file a claim."
    );
    yield return new InboundClaim(
        Guid.NewGuid().ToString(),
        "My entire warehouse burned down last night. Policy IL-9910. Loss is in the millions. I'm desperate, please help immediately."
    );
}
