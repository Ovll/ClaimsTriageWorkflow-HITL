# How It Works

A developer-oriented walkthrough of the codebase: every file, every function, in the order things actually run.

---

## The complete data flow

```
InboundClaim (raw text)
    │
    ▼
Preprocessor ── PII masking, policy regex, date parse, RedFlagDetector
    │                                                        │
    │  saves PreprocessedClaim to workflow state            │ PreScreenFlags
    ▼                                                        ▼
ClassifierAgent ── LLM call with MaskedText ──► ClaimClassification
    │               attaches PreScreenFlags ◄─────────────────┘
    ▼
Router (passthrough — forwards unchanged)
    │
    ├─[ShouldEscalate]──► adjuster_gate (HITL pause)
    │                          │
    │                ┌─────────┴──────────┐
    │          approve_escalation    override_to_auto_approve
    │                │                    │
    │        [escalation_handler]  [auto_responder_approve]
    │                │
    │           [INBOX] JSON
    │
    ├─[ShouldRequestInfo]──► auto_responder_info
    │
    └─[ShouldAutoApprove]──► auto_responder_approve
                                    │
                                    ▼
                             AuditRecord ──► audit.log
```

---

## `ChatClientFactory.cs` — choosing the LLM

The first thing `Program.cs` does is call `ChatClientFactory.Create()`. This reads one environment variable and returns an `IChatClient` — MAF's standard interface for talking to any LLM.

```csharp
var provider = Environment.GetEnvironmentVariable("LLM_PROVIDER") ?? "azure";
```

**What is a client?** It is an object that knows how to talk to a remote service. Like a phone — you pick it up to make a call; you don't build the phone network yourself.

Three providers are supported:

```csharp
// Direct OpenAI (sk-... key from platform.openai.com)
if (provider == "openai")
{
    return new OpenAIClient(new ApiKeyCredential(apiKey))
        .GetChatClient(model)       // narrow to text chat only
        .AsIChatClient();           // wrap in MAF's standard interface
}

// Local Ollama (same OpenAI API format, different endpoint)
if (provider == "ollama")
{
    return new OpenAIClient(
        new ApiKeyCredential("ollama"),
        new OpenAIClientOptions { Endpoint = new Uri(endpoint) }
    ).GetChatClient(model).AsIChatClient();
}

// Azure OpenAI (default)
return new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(apiKey))
    .GetChatClient(deploymentName)
    .AsIChatClient();
```

`.AsIChatClient()` is the key step — it wraps the provider-specific object in MAF's common interface. Everything downstream receives the same `IChatClient` regardless of which provider is active. Swapping providers is one env var change.

---

## `ClaimsWorkflow.cs` — assembling the graph

`ClaimsWorkflow.Build(client)` runs **once at startup**. It creates all the nodes and wires them together with edges. The result is an immutable `Workflow` object — nothing changes at runtime.

**What is a graph?** Nodes connected by arrows. Every executor/agent is a node. Every `AddEdge` call is an arrow between two nodes.

```csharp
return new WorkflowBuilder(preprocessor)      // entry node
    .BindExecutor(classifier)
    .BindExecutor(router)
    // ...
    .AddEdge(preprocessor, classifier)         // always fires
    .AddEdge(classifier, router)               // always fires
    .AddEdge<ClaimClassification>(router, gate,
        c => RoutingConditions.ShouldEscalate(c))   // fires only when true
    .AddEdge<ClaimClassification>(router, infoResponder,
        c => RoutingConditions.ShouldRequestInfo(c))
    .AddEdge<ClaimClassification>(router, approveResponder,
        c => RoutingConditions.ShouldAutoApprove(c))
    .Build();
```

The `condition=` lambda on each arrow is a C# function that returns `true` or `false`. After each node finishes, MAF evaluates all outgoing edge conditions and follows the ones that return `true`. You declare the structure; MAF drives execution.

---

## `Program.cs` — the entry point and event loop

After building the client and workflow, `Program.cs` resolves claims and runs them one at a time:

```csharp
foreach (var claim in ResolveClaims(args))
{
    await using var run = await env.RunStreamingAsync(workflow, claim, ...);
    await foreach (var evt in run.WatchStreamAsync(...))
    {
        // react to each event as it arrives
    }
}
```

`RunStreamingAsync` kicks off the workflow and returns a stream of events. The loop is **event-driven** — it doesn't call executors directly, it reacts to what MAF emits. Events include `ExecutorInvokedEvent`, `ExecutorCompletedEvent`, `ExecutorFailedEvent`, and `RequestInfoEvent` (the HITL pause).

**Input resolution** (`ResolveClaims`) checks in priority order:
1. `--claim "text"` — single inline claim
2. `--file path.csv` — batch from CSV (TextFieldParser, supports quoted fields with commas)
3. Piped stdin — one claim per line
4. No input — falls back to three hardcoded fixture claims

---

## Executor 1: `Preprocessor.cs` — first node, no LLM

**Runs on:** every claim, always first.  
**Input:** `InboundClaim` (raw text + UUID)  
**Output:** `PreprocessedClaim` (masked text + extracted fields + red flags)

### Regex patterns (compiled once at startup)

```csharp
private static readonly Regex EmailRegex  = new(@"[\w.+-]+@[\w-]+\.[\w.]+", RegexOptions.Compiled);
private static readonly Regex PhoneRegex  = new(@"\+?\d[\d\s\-(). ]{7,15}\d", RegexOptions.Compiled);
private static readonly Regex PolicyRegex = new(@"IL-\d+", RegexOptions.Compiled);
private static readonly Regex NameRegex   = new(@"\b([A-Z][a-z]{1,})\s+([A-Z][a-z]{1,})\b", RegexOptions.Compiled);
```

`static readonly` — created once when the program starts, reused for every claim. `RegexOptions.Compiled` compiles the pattern to machine code on first use, making repeated matching faster.

### Stop words

```csharp
private static readonly HashSet<string> NonNameWords = new(StringComparer.OrdinalIgnoreCase)
{
    "Policy", "Claim", "Small", "Water", "Kitchen", "Total", "Loss", ...
};
```

`HashSet` gives O(1) lookup. Without this, "Water Leak" or "Total Loss" would be masked as `[NAME]` because they match the capitalized-word-pair pattern. These domain words are excluded.

### `Process()` — the pure function

```csharp
var policyMatch  = PolicyRegex.Match(raw);
var policyNumber = policyMatch.Success ? policyMatch.Value : "UNKNOWN";
var claimId      = policyMatch.Success ? policyMatch.Value : claim.ClaimId;
```

Uses the policy number (`IL-2201`) as the claim ID when found — more readable in logs than a UUID.

```csharp
var masked = EmailRegex.Replace(raw, "[EMAIL]");
masked     = PhoneRegex.Replace(masked, "[PHONE]");
masked     = MaskNames(masked);
```

Three passes. Each `.Replace()` returns a new string — `raw` is never modified. Order matters: emails first, then phones, then names.

```csharp
var incidentDate = ExtractDate(raw);   // searches raw, not masked
var flags        = RedFlagDetector.Detect(raw);   // also searches raw
```

Both use the original text — we want keywords and dates before any replacement.

### `Handle()` — the workflow wrapper

```csharp
var result = Process(claim);
await ctx.QueueStateUpdateAsync("preprocessed_claim", result, "run", ct);
return result;
```

Calls `Process()` then saves the result to **workflow state** under key `"preprocessed_claim"` with scope `"run"`. Later nodes (`EscalationHandler`, `AutoResponderAgent`) need the policy number and masked text but only receive `ClaimClassification` from MAF — they read it back from this shared state.

### `MaskNames()` helper

```csharp
NameRegex.Replace(text, m => {
    var first  = m.Groups[1].Value;
    var second = m.Groups[2].Value;
    if (NonNameWords.Contains(first) || NonNameWords.Contains(second))
        return m.Value;   // leave unchanged
    return "[NAME]";
});
```

Lambda-based replace: for every match, run this function to decide what to replace it with. Groups 1 and 2 are the two captured words (the parentheses in the regex).

### `ExtractDate()` helper

```csharp
if (lower.Contains("yesterday") || lower.Contains("last night"))
    return today.AddDays(-1);

// "last [weekday]" — walk backwards until the right weekday
var d = today.AddDays(-1);
while (d.DayOfWeek != target) d = d.AddDays(-1);
return d;
```

Simple string search on lowercased text. Resolves relative references to absolute `DateOnly` values using UTC now as anchor.

---

## Executor 2: `RedFlagDetector.cs` — called inside Preprocessor

**Not a MAF node** — a pure helper function called by `Preprocessor.Process()`.  
**Input:** raw claim text  
**Output:** `PreScreenFlags` record

```csharp
public static PreScreenFlags Detect(string rawText)
{
    var t = rawText.ToLowerInvariant();   // lowercase once, then all Contains() are case-insensitive

    return new PreScreenFlags(
        TotalLoss:         t.Contains("total loss") || t.Contains("burned down") || ...,
        FireOrExplosion:   t.Contains("fire") || t.Contains("explosion") || ...,
        FloodOrWater:      t.Contains("flood") || t.Contains("water damage") || ...,
        FraudLanguage:     t.Contains("fraud") || t.Contains("staged") || ...,
        HighValueLanguage: t.Contains("millions") || t.Contains("tens of thousands") || ...,
        LegalLanguage:     t.Contains("lawsuit") || t.Contains("attorney") || ...
    );
}
```

All six flags are evaluated in a single expression. The result is an immutable record — it cannot be changed after creation. `PreScreenFlags.Any` (defined on the record) returns `true` if any flag is `true` — this is what `ShouldEscalate` checks.

This fires **before** the LLM runs. A `red_flag` result escalates regardless of what the LLM later decides.

---

## Agent 1: `ClassifierAgent.cs` — second node, first LLM call

**Input:** `PreprocessedClaim`  
**Output:** `ClaimClassification` (11 fields, typed enums, structured JSON)

### JSON options

```csharp
new JsonSerializerOptions
{
    Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
};
```

`JsonStringEnumConverter` makes enums serialize as strings (`"High"`) rather than numbers (`2`). `allowIntegerValues: false` means if the LLM returns a number for an enum field, deserialization throws — no silent bad data.

Enums all have `Unknown = 0` as the first value. If the LLM omits a field, C# defaults it to `0 = Unknown`. `RoutingConditions.HasUnknownClassification` detects this and forces escalation — a bad LLM response never silently reaches auto-approval.

### System prompt

Sent to the LLM once per invocation, before the claim text. Defines the persona, exact allowed values for every field, and decision rules (e.g. `SafeToAutoApprove: true ONLY IF MissingInfo is empty AND FraudIndicators is false AND EstimatedAmount <= 10000`). Ends with `Return ONLY valid JSON. No prose, no markdown.` — without this, models wrap the response in markdown code blocks which breaks deserialization.

### The handler lambda

```csharp
Func<PreprocessedClaim, IWorkflowContext, CancellationToken, ValueTask<ClaimClassification>> handler =
    async (claim, ctx, ct) =>
    {
        var prompt = $"""
            Claim ID: {claim.ClaimId}
            Policy: {claim.PolicyNumber}
            Claim text: {claim.MaskedText}    // ← PII already removed
            """;

        var response = await agent.RunAsync<ClaimClassification>(
            prompt, null, JsonOptions, null, ct);
```

`RunAsync<ClaimClassification>` is MAF's structured output call. The generic type tells MAF what schema to deserialize into. Internally it sends system prompt + user message, receives JSON, deserializes using `JsonOptions`.

`null` for the session means no conversation history — each claim is a completely fresh, independent conversation. If history were kept, claim 2 could be influenced by claim 1.

```csharp
        var cls = response.Result;
        cls.PreScreenFlags = claim.PreScreenFlags;   // attach deterministic flags
        return cls;
```

The LLM never knows about `PreScreenFlags` — it doesn't appear in the prompt and is not part of the output schema. After deserialization, we copy the flags that `RedFlagDetector` computed earlier. From this point `ClaimClassification` carries both LLM output and deterministic keyword flags in one object.

---

## Executor 3: `Router.cs` — third node, pure passthrough

**Input:** `ClaimClassification`  
**Output:** same `ClaimClassification` object, unchanged

```csharp
public static ClaimClassification Passthrough(ClaimClassification c) => c;
```

That is the entire logic. Returns the exact same object reference — not a copy. The Router exists solely as a **hub** in the graph — the node from which three conditional edges diverge. All branching logic lives on those edges, expressed as lambdas in `ClaimsWorkflow.cs`.

Why not put if/else here? Because then the Router would need to know about all downstream nodes. By putting conditions on edges, you can add a new branch without touching the Router.

---

## `RoutingConditions` (in `ClaimsWorkflow.cs`) — the decision logic

Three pure static methods, each returning a boolean:

```csharp
public static bool ShouldEscalate(ClaimClassification c) =>
    HasUnknownClassification(c)     // LLM returned an Unknown enum value
    || c.PreScreenFlags.Any         // keyword rule fired (deterministic)
    || c.ClassificationConfidence < Constants.ConfidenceThreshold   // LLM is uncertain
    || c.Urgency == UrgencyLevel.High
    || c.FraudIndicators
    || c.EstimatedAmount > Constants.AmountThreshold;

public static bool ShouldRequestInfo(ClaimClassification c) =>
    !ShouldEscalate(c) && c.MissingInfo.Count > 0 && !c.SafeToAutoApprove;

public static bool ShouldAutoApprove(ClaimClassification c) =>
    !ShouldEscalate(c) && c.SafeToAutoApprove && c.EstimatedAmount <= Constants.AmountThreshold;
```

Priority is enforced structurally — `ShouldRequestInfo` and `ShouldAutoApprove` both start with `!ShouldEscalate(c)`, so a claim that escalates can never reach either of them.

`Constants.ConfidenceThreshold` and `Constants.AmountThreshold` are computed properties (not `static readonly`) so tests can set environment variables and observe the updated values without restarting the process.

---

## HITL gate (`adjuster_gate`) — the pause

When `ShouldEscalate` is true, the edge leads to a `RequestPort` — a special MAF node that pauses the workflow and emits a `RequestInfoEvent`. Back in `Program.cs`:

```csharp
if (evt is RequestInfoEvent hitl)
{
    // print claim details to terminal
    var decision = Console.ReadLine()?.Trim();   // wait for human input

    if (HitlConditions.ShouldOverrideToAutoApprove(decision))
    {
        gateResponse = HitlConditions.BuildOverrideResponse(cls);
        classification = gateResponse;   // sync for audit record
    }
    else
        gateResponse = cls!;   // approve_escalation — keep original

    await run.SendResponseAsync(req.CreateResponse(gateResponse));
    // workflow resumes automatically
}
```

On `override_to_auto_approve`, `BuildOverrideResponse` creates a new `ClaimClassification` with `SafeToAutoApprove=true` and `ClassificationConfidence=1.0`. Now `ShouldAutoApprove` returns `true` for it, so the workflow continues to `auto_responder_approve` instead of `escalation_handler`. The `classification = gateResponse` assignment ensures the audit record reflects the classification that actually drove the route.

---

## Executor 4: `EscalationHandler.cs` — escalate path only

**Input:** `ClaimClassification` + `PreprocessedClaim` (from workflow state)  
**Output:** `AdjusterDossier`

### Priority chain

```csharp
public static string DetermineReason(ClaimClassification c)
{
    if (c.PreScreenFlags.Any)                                        return "red_flag";
    if (c.ClassificationConfidence < Constants.ConfidenceThreshold) return "low_confidence";
    if (c.EstimatedAmount > Constants.AmountThreshold)              return "high_amount";
    if (c.FraudIndicators)                                          return "fraud_flag";
    return "high_urgency";   // fallback — must be true if ShouldEscalate fired
}
```

Five `if` statements, each returning immediately when true. The first match wins. Order encodes business logic: deterministic keyword evidence (`red_flag`) is more trustworthy than LLM uncertainty (`low_confidence`), which is more trustworthy than LLM content signals.

### Handle

```csharp
var claim = await ctx.ReadStateAsync<PreprocessedClaim>("preprocessed_claim", "run", ct)
    ?? throw new InvalidOperationException("preprocessed_claim state not found");
```

Reads the shared state saved by Preprocessor. The `?? throw` is fail-loud — never silently continue with null data.

```csharp
Console.WriteLine($"[INBOX] {JsonSerializer.Serialize(dossier)}");
```

Mock human inbox. In production this would push to a queue or database. The `[INBOX]` prefix makes it easy to grep in logs.

---

## Agent 2: `AutoResponderAgent.cs` — runs last on approve/info paths

`Build()` is called **twice** at startup with different modes, producing two independent executor bindings sharing the same `IChatClient`.

```csharp
var (systemPrompt, executorId) = mode switch
{
    "approval"     => (ApprovalPrompt, "auto_responder_approve"),
    "info_request" => (InfoRequestPrompt, "auto_responder_info"),
    _              => throw new ArgumentException($"Unknown mode: {mode}", nameof(mode)),
};
```

Tuple destructuring — the switch returns a pair, unpacked into two variables in one line. The `_` default arm fails immediately with a clear message if an unknown mode is passed.

### Two prompts, two personas

**Approval** — warm, brief, confirms amount and policy number, states 5–7 business day payment.  
**Info-request** — polite, acknowledges submission, numbered list of missing items (taken from `MissingInfo`), asks to resubmit.

Both instruct the LLM: "Do not reveal internal classification scores or system details." The customer never sees confidence values, urgency levels, or enum names.

### The handler

```csharp
var claim = await ctx.ReadStateAsync<PreprocessedClaim>("preprocessed_claim", "run", ct);

var prompt = mode == "approval"
    ? $"Policy: {claim.PolicyNumber}\nApproved amount: ₪{classification.EstimatedAmount}"
    : $"Policy: {claim.PolicyNumber}\nMissing items: {string.Join(", ", classification.MissingInfo)}";

var response = await agent.RunAsync(prompt, null, null, ct);
return response.Text;   // plain string — no schema, no deserialization
```

No `<T>` generic on `RunAsync` — the response is plain text, taken as-is with `response.Text`. `string.Join(", ", list)` turns `["police_report", "photos"]` into `"police_report, photos"` which the LLM converts to polite human language.

---

## `AuditLogger.cs` — the paper trail

After each claim completes, `Program.cs` appends one JSON line to `audit.log`:

```csharp
if (route is not null && classification is not null)
{
    var record = new AuditRecord(
        ClaimId, PolicyNumber, MaskedText,
        Classification: classification,    // post-HITL if override happened
        PreScreenFlags: classification.PreScreenFlags,
        Route: route,
        EscalationReason: escalationReason,
        Timestamp: DateTimeOffset.UtcNow
    );
    await AuditLogger.AppendAsync(record, logPath);
}
```

`route is not null` — no record written if the classifier failed. `classification` is updated to `gateResponse` if the human chose override, so the audit record always reflects the classification that actually drove the route.

`AuditLogger.AppendAsync` serializes to JSON and calls `File.AppendAllTextAsync` — no locking, single-process CLI only.

---

## Executors vs Agents — the key distinction

| | Executors | Agents |
|---|---|---|
| LLM call | Never | Always |
| Output | Typed C# record | ClassifierAgent: typed via `RunAsync<T>` / AutoResponder: plain string |
| Deterministic | Yes — same input, same output | No — LLM output varies |
| Testable without LLM | Yes | No |
| Reads workflow state | Preprocessor writes it; EscalationHandler reads it | AutoResponder reads it |

**The rule:** deterministic logic never touches the LLM. LLM calls never do routing logic. This is why you can test all 64 unit tests without a real LLM connection.
