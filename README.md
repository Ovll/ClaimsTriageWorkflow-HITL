# Claims Triage Workflow

An insurance claims triage system built with the **Microsoft Agent Framework (MAF)** and **Azure OpenAI / Ollama**. Incoming claims are preprocessed, classified by an LLM, and automatically routed to one of three outcomes — auto-approval, information request, or human escalation.

---

## Architecture

```
InboundClaim
     │
     ▼
[preprocessor]          PII masking · policy extraction · date normalisation
     │
     ▼
[classifier]            LLM → ClaimClassification (9 fields, typed enums, structured JSON)
     │
     ▼
[router]                Passthrough — branching is on the edges, not here
     │
     ├─ unknown || urgency==High || fraud || amount>10k ─► [adjuster_gate] ──► [escalation_handler] ──► [INBOX]
     │                                                  │
     │                                                  └── override ──► [auto_responder_approve]
     │
     ├─ missingInfo.Count>0 && !safeToAutoApprove ────────────────────────► [auto_responder_info]
     │
     └─ safeToAutoApprove && amount<=10k ─────────────────────────────────► [auto_responder_approve]
```

### Executors

| Node | Type | Responsibility |
|------|------|----------------|
| `preprocessor` | Deterministic executor | Regex PII masking, policy extraction, date normalisation |
| `classifier` | `ChatClientAgent` | Produces structured `ClaimClassification` (9 fields, typed enums) via LLM |
| `router` | Deterministic executor | Forwards `ClaimClassification` unchanged; edges branch |
| `adjuster_gate` | `RequestPort` (HITL) | Pauses workflow, prompts operator for a decision |
| `escalation_handler` | Deterministic executor | Builds `AdjusterDossier`, writes to `[INBOX]` |
| `auto_responder_approve` | `ChatClientAgent` | Drafts approval letter |
| `auto_responder_info` | `ChatClientAgent` | Drafts missing-information request |

---

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download)
- **One** of:
  - Azure OpenAI resource with a `gpt-4o` deployment, **or**
  - [Ollama](https://ollama.ai) running locally with `qwen2.5:7b` pulled

---

## Setup

```bash
git clone https://github.com/Ovll/ClaimsTriageWorkflow-HITL.git
cd ClaimsTriageWorkflow-HITL
cp .env.example .env
# edit .env with your credentials (see below)
```

### .env — Azure OpenAI (default)

```env
LLM_PROVIDER=azure
AZURE_OPENAI_ENDPOINT=https://your-resource.openai.azure.com/
AZURE_OPENAI_API_KEY=your-api-key
AZURE_OPENAI_DEPLOYMENT_NAME=gpt-4o
AMOUNT_THRESHOLD=10000
```

### .env — Local Ollama

```env
LLM_PROVIDER=ollama
OLLAMA_ENDPOINT=http://localhost:11434/v1
OLLAMA_MODEL=qwen2.5:7b
AMOUNT_THRESHOLD=10000
```

---

## Run

```bash
dotnet run --project src/ClaimsTriageWorkflow/
```

### Expected output

```
============================================================
Input: Policy #IL-2201. Small water leak under the kitchen sink, fixed already, receipt for ₪800 attached.
============================================================
[preprocessor] invoked
[preprocessor] completed
[classifier] invoked
[classifier] completed — Property / Low / Neutral / ₪800 / auto_approve
[router] invoked
[router] completed — route: auto_approve
[auto_responder_approve] invoked
[auto_responder_approve] completed
Final: claim IL-2201 auto-approved

============================================================
Input: Hi, I had a car accident yesterday. Policy is IL-5540. I need to file a claim.
============================================================
[preprocessor] invoked
[preprocessor] completed
[classifier] invoked
[classifier] completed — Vehicle / Medium / Neutral / ₪0 / request_more_info
[router] invoked
[router] completed — route: request_more_info
[auto_responder_info] invoked
[auto_responder_info] completed
Final: claim IL-5540 pending — additional info requested

============================================================
Input: My entire warehouse burned down last night. Policy IL-9910. Loss is in the millions. I'm desperate, please help immediately.
============================================================
[preprocessor] invoked
[preprocessor] completed
[classifier] invoked
[classifier] completed — Property / High / Distressed / ₪0 / escalate_to_adjuster
[router] invoked
[router] completed — route: escalate_to_adjuster

[adjuster_gate] HITL prompt — Claim IL-9910 (Policy IL-9910)
  urgency=High, fraud=False, amount=₪0
  rationale=Total loss warehouse fire with extreme distress language and no stated amount.  confidence=0.97
  Options: approve_escalation | override_to_auto_approve
  > approve_escalation

[escalation_handler] invoked
[INBOX] {"ClaimId":"IL-9910","PolicyNumber":"IL-9910",...}
[escalation_handler] completed
Final: claim IL-9910 escalated to human adjuster queue
```

---

## Classifier output

`ClaimClassification` has 9 fields. The three categorical fields are C# enums. The classifier deserialization path uses `JsonStringEnumConverter(allowIntegerValues: false)` via `ClassifierAgent.CreateJsonOptions()` — the type-level `[JsonConverter]` attributes use the default converter for other serialization contexts such as the `[INBOX]` JSON output. Two distinct failure modes are handled on the classifier path:

- **Omitted field** — the property stays at its C# default, which is `Unknown = 0`. `RoutingConditions.HasUnknownClassification` detects this and escalates to the HITL gate as a fail-safe.
- **Invalid string or integer value** — `JsonStringEnumConverter(allowIntegerValues: false)` throws `JsonException` at deserialization so the malformed response never reaches routing logic.

| Field | Type | Notes |
|-------|------|-------|
| `ClaimType` | `ClaimType` enum | Vehicle / Property / Health / Liability / Other |
| `Urgency` | `UrgencyLevel` enum | High / Medium / Low |
| `Sentiment` | `SentimentType` enum | Positive / Neutral / Frustrated / Distressed |
| `EstimatedAmount` | `decimal` | NIS value; 0 if not stated |
| `MissingInfo` | `List<string>` | Items required but absent from the claim |
| `FraudIndicators` | `bool` | True if suspicious patterns detected |
| `SafeToAutoApprove` | `bool` | True only when all three safety conditions pass |
| `ClassificationRationale` | `string` | One-sentence model explanation of key signals |
| `ClassificationConfidence` | `double` | 0.0–1.0 model self-assessment. **Informational only** — not used as a routing input. Routing uses deterministic signals (urgency, fraud flag, amount threshold) which are more reliable than raw LLM confidence until calibration evals establish a threshold. |

---

## Human-in-the-Loop (HITL)

Escalated claims pause at `adjuster_gate` before reaching the adjuster inbox. The operator is prompted on the console and must choose one of two options:

| Input | Outcome |
|-------|---------|
| `approve_escalation` | Workflow continues to `escalation_handler` → dossier pushed to `[INBOX]` |
| `override_to_auto_approve` | Workflow continues to `auto_responder_approve` → approval reply sent to customer |

Any unrecognised input defaults to `approve_escalation`.

### Example console interaction (Claim C)

```
[adjuster_gate] invoked

[adjuster_gate] HITL prompt — Claim IL-9910 (Policy IL-9910)
  urgency=High, fraud=False, amount=₪0
  rationale=Total loss warehouse fire with extreme distress language.  confidence=0.97
  Options: approve_escalation | override_to_auto_approve
  > approve_escalation

[adjuster_gate] completed
[escalation_handler] invoked
[INBOX] {"ClaimId":"IL-9910", ...}
[escalation_handler] completed
Final: claim IL-9910 escalated to human adjuster queue
```

---

## Tests

```bash
dotnet test
```

47 unit tests covering:
- PII masking (email, phone, name)
- Policy number extraction
- Date normalisation
- Escalation reason priority (`high_amount` > `fraud_flag` > `high_urgency`)
- All three routing conditions and their priority order
- Unknown enum sentinel fail-safe (omitted classifier fields default to `Unknown` and escalate; invalid strings/integers throw at deserialization)
- HITL gate response parsing (`approve_escalation` / `override_to_auto_approve`)

---

## Project structure

```
ClaimsTriageWorkflow-HITL/
├── src/ClaimsTriageWorkflow/
│   ├── Program.cs                  Entry point — runs 3 sample claims
│   ├── Constants.cs                AmountThreshold (env-configurable)
│   ├── ChatClientFactory.cs        Builds IChatClient (Azure or Ollama)
│   ├── Models/
│   │   ├── InboundClaim.cs
│   │   ├── PreprocessedClaim.cs
│   │   ├── ClaimClassification.cs  9 fields; typed enums for ClaimType/Urgency/Sentiment
│   │   ├── ClaimType.cs            enum — Vehicle | Property | Health | Liability | Other
│   │   ├── UrgencyLevel.cs         enum — High | Medium | Low  (Unknown=0 sentinel)
│   │   ├── SentimentType.cs        enum — Positive | Neutral | Frustrated | Distressed
│   │   └── AdjusterDossier.cs
│   ├── Executors/
│   │   ├── Preprocessor.cs
│   │   ├── Router.cs
│   │   └── EscalationHandler.cs
│   ├── Agents/
│   │   ├── ClassifierAgent.cs
│   │   └── AutoResponderAgent.cs
│   ├── Middleware/
│   │   └── LoggingMiddleware.cs
│   └── Workflow/
│       └── ClaimsWorkflow.cs       WorkflowBuilder assembly + RoutingConditions
└── tests/ClaimsTriageWorkflow.Tests/
    ├── PreprocessorTests.cs
    ├── RouterTests.cs
    ├── EscalationHandlerTests.cs
    ├── RoutingConditionTests.cs
    ├── HitlRoutingTests.cs
    └── ClassifierJsonOptionsTests.cs
```
