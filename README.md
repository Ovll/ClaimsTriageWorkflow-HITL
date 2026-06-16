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
[classifier]            LLM → ClaimClassification (7 fields, structured JSON)
     │
     ▼
[router]                Passthrough — branching is on the edges, not here
     │
     ├─ urgency==high || fraud || amount>10k ──► [escalation_handler] ──► [INBOX]
     │
     ├─ missingInfo.Count>0 && !safeToAutoApprove ──────────────────► [auto_responder_info]
     │
     └─ safeToAutoApprove && amount<=10k ───────────────────────────► [auto_responder_approve]
```

### Executors

| Node | Type | Responsibility |
|------|------|----------------|
| `preprocessor` | Deterministic executor | Regex PII masking, policy extraction, date normalisation |
| `classifier` | `ChatClientAgent` | Produces structured `ClaimClassification` via LLM |
| `router` | Deterministic executor | Forwards `ClaimClassification` unchanged; edges branch |
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
git clone https://github.com/Ovll/ClaimsTriageWorkflow.git
cd ClaimsTriageWorkflow
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
Input: Policy #IL-2201. Small water leak under the kitchen sink...
============================================================
[preprocessor] invoked
[preprocessor] completed
[classifier] invoked
[classifier] completed — property / low / neutral / ₪800 / auto_approve
[router] invoked
[router] completed — route: auto_approve
[auto_responder_approve] invoked
[auto_responder_approve] completed
Final: claim IL-2201 auto-approved

...

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

30 unit tests covering:
- PII masking (email, phone, name)
- Policy number extraction
- Date normalisation
- Escalation reason priority (`high_amount` > `fraud_flag` > `high_urgency`)
- All three routing conditions and their priority order

---

## Project structure

```
ClaimsTriageWorkflow/
├── src/ClaimsTriageWorkflow/
│   ├── Program.cs                  Entry point — runs 3 sample claims
│   ├── Constants.cs                AmountThreshold (env-configurable)
│   ├── ChatClientFactory.cs        Builds IChatClient (Azure or Ollama)
│   ├── Models/
│   │   ├── InboundClaim.cs
│   │   ├── PreprocessedClaim.cs
│   │   ├── ClaimClassification.cs
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
    └── RoutingConditionTests.cs
```
