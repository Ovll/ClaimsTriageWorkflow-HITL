# Claims Triage Workflow

![CI](https://github.com/Ovll/ClaimsTriageWorkflow-HITL/actions/workflows/ci.yml/badge.svg?branch=main)

An insurance claims triage system built with the **Microsoft Agent Framework (MAF)** and **Azure OpenAI / Ollama**. Incoming claims are preprocessed deterministically, classified by an LLM into a typed structured output, and automatically routed to one of three outcomes — auto-approval, information request, or human escalation via an interactive HITL gate.

---

## Quick start

### Docker (recommended)

```bash
git clone https://github.com/Ovll/ClaimsTriageWorkflow-HITL.git
cd ClaimsTriageWorkflow-HITL
cp .env.example .env        # fill in your credentials
docker compose run triage
```

The image builds and runs tests before starting. The HITL gate for escalated claims reads from stdin — keep the terminal interactive.

### Local (.NET 8 required)

```bash
cp .env.example .env        # fill in your credentials

# Run the three built-in fixture claims
dotnet run --project src/ClaimsTriageWorkflow/

# Single claim from the command line
dotnet run --project src/ClaimsTriageWorkflow/ -- --claim "My roof leaked, policy IL-3301, receipt attached, ₪800."

# Batch from a CSV file (columns: claimId,text; supports quoted text with commas)
dotnet run --project src/ClaimsTriageWorkflow/ -- --file claims.csv

# Pipe claims from stdin (one claim text per line)
echo "Policy IL-5540, car accident yesterday, no details yet." | dotnet run --project src/ClaimsTriageWorkflow/
```

---

## Configuration

Copy `.env.example` to `.env` and choose a provider:

**Azure OpenAI**
```env
LLM_PROVIDER=azure
AZURE_OPENAI_ENDPOINT=https://your-resource.openai.azure.com/
AZURE_OPENAI_API_KEY=your-api-key
AZURE_OPENAI_DEPLOYMENT_NAME=gpt-4o
AMOUNT_THRESHOLD=10000
```

**Local Ollama** (`ollama pull qwen2.5:7b` first)
```env
LLM_PROVIDER=ollama
OLLAMA_MODEL=qwen2.5:7b
AMOUNT_THRESHOLD=10000
# dotnet run  → http://localhost:11434/v1
# docker compose run → http://host.docker.internal:11434/v1
OLLAMA_ENDPOINT=http://localhost:11434/v1
```

---

## Architecture

```
InboundClaim
     │
     ▼
[preprocessor]      deterministic — PII masking, policy extraction, date normalisation
     │
     ▼
[classifier]        LLM → ClaimClassification (9 fields, typed enums, structured JSON)
     │
     ▼
[router]            passthrough — all branching is on edges, not inside executors
     │
     ├─ escalate ──► [adjuster_gate] ──approve──► [escalation_handler] ──► [INBOX]
     │                    (HITL)      └─override──► [auto_responder_approve]
     │
     ├─ missing info ───────────────────────────► [auto_responder_info]
     │
     └─ safe to approve ────────────────────────► [auto_responder_approve]
```

Escalate fires when any of: `preScreenFlags.Any == true` (deterministic red-flag), `classificationConfidence < threshold` (default 0.65), `urgency == High`, `fraudIndicators == true`, `estimatedAmount > threshold`, or the LLM returned an unrecognised enum value (`Unknown` sentinel). Escalation reason priority: `red_flag → low_confidence → high_amount → fraud_flag → high_urgency`.

### Nodes

| Node | Type | Role |
|------|------|------|
| `preprocessor` | Deterministic executor | Regex PII masking, policy extraction, date normalisation |
| `classifier` | `ChatClientAgent` | Structured `ClaimClassification` via LLM |
| `router` | Deterministic executor | Forwards classification unchanged; edges branch |
| `adjuster_gate` | `RequestPort` (HITL) | Pauses workflow, prompts operator for a decision |
| `escalation_handler` | Deterministic executor | Builds `AdjusterDossier`, writes to `[INBOX]` |
| `auto_responder_approve` | `ChatClientAgent` | Drafts approval letter |
| `auto_responder_info` | `ChatClientAgent` | Drafts missing-information request |

---

## Key technical decisions

**Branching on edges, not inside executors.** The Router executor is a passthrough — it forwards `ClaimClassification` unchanged. All conditional logic lives in `WorkflowBuilder` edge condition lambdas (`RoutingConditions.ShouldEscalate`, `ShouldRequestInfo`, `ShouldAutoApprove`). This keeps executor code free of routing concerns and makes the graph easy to read and test independently.

**Typed enums with an `Unknown = 0` sentinel.** `ClaimType`, `UrgencyLevel`, and `SentimentType` are C# enums rather than strings. If the LLM omits a field it stays at `Unknown = 0`; `RoutingConditions.HasUnknownClassification` detects this and forces escalation — a malformed response never silently reaches auto-approval. `JsonStringEnumConverter(allowIntegerValues: false)` is applied only to the classifier's deserialization path so that the `[INBOX]` JSON output uses the default converter.

**HITL gate response is `ClaimClassification`, not a string.** The gate's `RequestPort<ClaimClassification, ClaimClassification>` returns the same type that feeds downstream executors. On `approve_escalation` the original classification is returned unchanged (it still satisfies `ShouldEscalate`). On `override_to_auto_approve`, `HitlConditions.BuildOverrideResponse` builds a modified classification that satisfies `ShouldAutoApprove`, so the workflow continues to `auto_responder_approve` without a type mismatch.

**Escalation reason priority: `red_flag → low_confidence → high_amount → fraud_flag → high_urgency`.** The `EscalationHandler` applies a deterministic priority so the primary reason recorded in the dossier is always the most actionable one, regardless of which combination of flags is set.

**Deterministic red-flag pre-screening.** `RedFlagDetector` scans raw claim text for six high-risk keyword categories (total loss, fire/explosion, flood, fraud language, high-value language, legal language) before the LLM runs. Flags are attached to `ClaimClassification` by the classifier handler after LLM deserialization — the Router stays a true passthrough. Any fired flag immediately triggers escalation, independently of LLM output.

**Confidence threshold routing.** `ClassificationConfidence < Constants.ConfidenceThreshold` (default 0.65, env-configurable) triggers escalation. This ensures that low-certainty LLM output never silently reaches auto-approval.

**Append-only audit log.** After each completed claim run, an `AuditRecord` is serialised as one JSON line and appended to `audit.log` (path configurable via `AUDIT_LOG_PATH`). No record is written when the classifier fails.

**Flexible CLI input.** `--claim "text"` for a single inline claim, `--file claims.csv` for batch (supports quoted fields with embedded commas via `TextFieldParser`), piped stdin for shell pipelines, and a fixture fallback when no arguments are given.

---

## Expected output

**Claim A — auto-approve** (₪800 water leak, receipt attached)
```
[preprocessor] completed
[classifier] completed — Property / Low / Neutral / ₪800 / auto_approve
[router] completed — route: auto_approve
[auto_responder_approve] completed
Final: claim IL-2201 auto-approved
```

**Claim B — request info** (car accident, no details)
```
[classifier] completed — Vehicle / Medium / Neutral / ₪0 / request_more_info
[router] completed — route: request_more_info
[auto_responder_info] completed
Final: claim IL-5540 pending — additional info requested
```

**Claim C — escalate + HITL** (warehouse fire, millions)
```
[classifier] completed — Property / High / Distressed / ₪0 / escalate_to_adjuster
[router] completed — route: escalate_to_adjuster

[adjuster_gate] HITL prompt — Claim IL-9910 (Policy IL-9910)
  urgency=High, fraud=False, amount=₪0
  rationale=Total loss warehouse fire with extreme distress language.  confidence=0.97
  Options: approve_escalation | override_to_auto_approve
  > approve_escalation

[escalation_handler] completed
Final: claim IL-9910 escalated to human adjuster queue
```

---

## Tests

```bash
dotnet test
```

Unit tests covering PII masking, routing conditions, red-flag detection, confidence threshold routing, escalation reason priority, enum sentinel fail-safe, HITL response parsing, and a labeled 12-case eval set. No LLM or network required.

---

## Project structure

```
src/ClaimsTriageWorkflow/
├── Program.cs                  Entry point — runs 3 sample claims
├── Constants.cs                AmountThreshold + ConfidenceThreshold (env-configurable)
├── ChatClientFactory.cs        IChatClient builder (Azure or Ollama)
├── Models/
│   ├── InboundClaim.cs
│   ├── PreprocessedClaim.cs
│   ├── ClaimClassification.cs  11 fields; typed enums, PreScreenFlags, ClassificationConfidence
│   ├── PreScreenFlags.cs       6 deterministic keyword flags + Any computed property
│   ├── AuditRecord.cs          per-claim decision record for the JSONL audit log
│   ├── ClaimType.cs            Vehicle | Property | Health | Liability | Other
│   ├── UrgencyLevel.cs         High | Medium | Low  (Unknown = 0 sentinel)
│   ├── SentimentType.cs        Positive | Neutral | Frustrated | Distressed
│   └── AdjusterDossier.cs
├── Executors/
│   ├── Preprocessor.cs
│   ├── RedFlagDetector.cs      keyword-based PreScreenFlags detection (no LLM)
│   ├── Router.cs
│   └── EscalationHandler.cs
├── AuditLogger.cs              appends AuditRecord as JSONL line
├── Agents/
│   ├── ClassifierAgent.cs
│   └── AutoResponderAgent.cs
├── Middleware/
│   └── LoggingMiddleware.cs
└── Workflow/
    └── ClaimsWorkflow.cs       WorkflowBuilder + RoutingConditions + HitlConditions

tests/ClaimsTriageWorkflow.Tests/
├── PreprocessorTests.cs        12 tests
├── RouterTests.cs               3 tests
├── EscalationHandlerTests.cs    8 tests  (incl. priority chain)
├── RoutingConditionTests.cs    29 tests  (incl. confidence + red-flag)
├── HitlRoutingTests.cs          9 tests
├── ClassifierJsonOptionsTests.cs 3 tests
├── EvalSetTests.cs              1 test   (12 labeled cases)
└── Eval/eval-cases.json        12 labeled routing cases
```
