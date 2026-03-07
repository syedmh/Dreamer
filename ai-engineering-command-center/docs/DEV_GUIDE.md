# AI Engineering Command Center - Developer Documentation

## Table of Contents

1. [Overview](#overview)
2. [Architecture](#architecture)
3. [Project Structure](#project-structure)
4. [Setup & Installation](#setup--installation)
5. [Running the CLI](#running-the-cli)
6. [Core Modules](#core-modules)
7. [Agents](#agents)
8. [Services](#services)
9. [Workflows](#workflows)
10. [Debate Engine](#debate-engine)
11. [RAG Knowledge System](#rag-knowledge-system)
12. [Prompt Templates](#prompt-templates)
13. [CLI Output Rendering](#cli-output-rendering)
14. [Testing](#testing)
15. [Adding a New Agent](#adding-a-new-agent)
16. [Adding a New Workflow](#adding-a-new-workflow)
17. [Configuration Reference](#configuration-reference)
18. [Data Models](#data-models)
19. [Error Handling](#error-handling)
20. [Troubleshooting](#troubleshooting)

---

## Overview

The AI Engineering Command Center is a multi-agent AI platform that demonstrates how specialized AI agents collaborate to assist engineering teams. It provides a CLI interface for tasks like PR review, incident diagnosis, backlog management, knowledge retrieval, test generation, and architecture debate.

### Key Capabilities

- **Multi-agent orchestration** - Multiple agents execute concurrently via asyncio
- **Concurrent AI execution** - Agents run in parallel with per-agent timeout isolation
- **Engineering automation** - Automated code review, security analysis, test generation
- **Retrieval Augmented Generation (RAG)** - Answer questions using indexed engineering docs
- **Agent Debate Mode** - Agents debate architecture decisions in a structured 4-round format
- **CLI developer tools** - Rich terminal UI with structured output

### Operating Modes

| Mode | Trigger | Description |
|------|---------|-------------|
| **Demo** | No `ANTHROPIC_API_KEY` env var | Uses `MockLLMService` with realistic pre-written responses. No network required. |
| **Live** | `ANTHROPIC_API_KEY` is set | Uses real Anthropic Claude API via `LLMService`. |

---

## Architecture

```
CLI Layer (cli/main.py)
    |
    v
Service Container (core/container.py)         <-- composition root
    |
    v
Orchestrator (core/orchestrator.py)
    |
    +---> Agent Registry (core/agent_registry.py)    <-- workflow -> agents mapping
    +---> Agent Runner (core/agent_runner.py)         <-- asyncio.gather execution
              |
              v
         Agent Layer (agents/*.py)                    <-- implements AgentBase
              |
              v
         Service Layer (services/*.py)                <-- LLM, VectorStore, GitHub, etc.
              |
              v
         Prompt Templates (prompts/*.txt)             <-- ROLE/TASK/INPUT/OUTPUT FORMAT
```

### Design Principles

- **Modular architecture** - Each component has a single responsibility
- **Strongly typed data models** - Pydantic models for all inputs/outputs
- **Async execution** - `asyncio.gather` for concurrent agent runs
- **Dependency injection** - Services injected at construction, never imported directly by agents
- **Service abstraction** - Agents interact through shared services, never call external APIs directly

---

## Project Structure

```
ai-engineering-command-center/
|
+-- agents/                      # Agent implementations
|   +-- code_review_agent.py     # PR code quality analysis
|   +-- security_agent.py        # Security vulnerability detection
|   +-- architecture_agent.py    # System design evaluation
|   +-- incident_agent.py        # Log-based root cause analysis
|   +-- sre_agent.py             # Reliability improvement suggestions
|   +-- backlog_agent.py         # Ticket deduplication & prioritization
|   +-- knowledge_agent.py       # RAG-powered Q&A
|   +-- test_generation_agent.py # Unit test generation from diffs
|   +-- summary_agent.py         # Executive daily summary
|   +-- cost_agent.py            # Operational cost evaluation (debate)
|   +-- orchestrator_agent.py    # Debate synthesis agent
|
+-- core/                        # Core framework
|   +-- agent_interface.py       # AgentBase ABC, AgentInput, AgentOutput
|   +-- agent_runner.py          # Concurrent agent execution with timeouts
|   +-- agent_registry.py        # Workflow-to-agent mapping
|   +-- orchestrator.py          # Workflow execution & result aggregation
|   +-- container.py             # Composition root (dependency wiring)
|   +-- prompt_loader.py         # Prompt template loader with variable substitution
|   +-- debate_engine.py         # 4-round structured debate coordinator
|
+-- services/                    # Shared services
|   +-- llm_service.py           # Anthropic Claude API wrapper (async)
|   +-- mock_llm_service.py      # Mock LLM with canned responses (no API key)
|   +-- vector_store.py          # ChromaDB vector store for RAG
|   +-- github_service.py        # GitHub/git utilities
|   +-- jira_service.py          # Jira ticket utilities
|   +-- log_service.py           # Log file parsing
|
+-- cli/                         # CLI interface
|   +-- main.py                  # Typer CLI commands
|   +-- output.py                # Rich output rendering functions
|
+-- prompts/                     # Prompt templates (14 files)
|   +-- code_review_prompt.txt
|   +-- security_prompt.txt
|   +-- architecture_prompt.txt
|   +-- incident_prompt.txt
|   +-- sre_prompt.txt
|   +-- backlog_prompt.txt
|   +-- test_generation_prompt.txt
|   +-- summary_prompt.txt
|   +-- knowledge_prompt.txt
|   +-- debate_propose_prompt.txt
|   +-- debate_critique_prompt.txt
|   +-- debate_cost_prompt.txt
|   +-- debate_rebuttal_prompt.txt
|   +-- debate_synthesize_prompt.txt
|
+-- scripts/
|   +-- ingest_docs.py           # Document ingestion into ChromaDB
|
+-- data/                        # Sample data & persistence
|   +-- diffs/sample_pr.diff     # Sample PR diff with intentional issues
|   +-- logs/payment.log         # Sample incident log
|   +-- tickets/tickets.json     # Sample backlog tickets
|   +-- docs/                    # Engineering docs for RAG
|   |   +-- retry_queue.md
|   |   +-- deployment_guide.md
|   |   +-- incident_response.md
|   |   +-- architecture_overview.md
|   +-- chroma_db/               # ChromaDB persistent storage (auto-created)
|
+-- tests/                       # Test suite (128+ tests)
|   +-- test_phase2.py           # Agent interface, runner, LLM service
|   +-- test_phase3.py           # Registry, orchestrator
|   +-- test_phase4.py           # All agents, container wiring
|   +-- test_phase5.py           # CLI commands
|   +-- test_phase6.py           # RAG knowledge system
|   +-- test_phase7.py           # Debate engine
|
+-- pyproject.toml               # Project config & dependencies
+-- CLAUDE.md                    # AI assistant instructions
```

---

## Setup & Installation

### Prerequisites

- Python 3.11+
- pip

### Install

```bash
# Clone the repository
git clone <repo-url>
cd ai-engineering-command-center

# Install in editable mode (installs all dependencies)
pip install -e .
```

### Dependencies

| Package | Purpose |
|---------|---------|
| `anthropic>=0.26.0` | Claude API client |
| `typer>=0.12.0` | CLI framework |
| `rich>=13.7.0` | Terminal UI (panels, tables, syntax highlighting) |
| `pydantic>=2.7.0` | Typed data models |
| `chromadb>=0.5.0` | Vector store for RAG |
| `gitpython>=3.1.43` | Git diff parsing |

### Optional: Set API Key for Live Mode

```bash
# Windows PowerShell
$env:ANTHROPIC_API_KEY = "sk-ant-..."

# Linux/macOS
export ANTHROPIC_API_KEY="sk-ant-..."
```

Without the API key, the system runs in **demo mode** with pre-written mock responses.

---

## Running the CLI

All commands are available via `python -m cli.main` or `ai-engineering` (if installed).

### Commands

```bash
# PR Review - runs 4 agents concurrently
python -m cli.main review-pr --diff data/diffs/sample_pr.diff

# Incident Diagnosis - runs 2 agents
python -m cli.main diagnose-incident --log data/logs/payment.log

# Backlog Management - runs 1 agent
python -m cli.main manage-backlog --tickets data/tickets/tickets.json

# Knowledge Q&A (RAG) - runs 1 agent
python -m cli.main ask-knowledge "How does the retry queue work?"

# Test Generation - runs 1 agent
python -m cli.main generate-tests --diff data/diffs/sample_pr.diff

# Daily Summary - runs 1 agent
python -m cli.main daily-summary --prs 5 --incidents 1 --deploys 3

# Agent Debate - 4-round structured debate
python -m cli.main debate "Should we add a Redis caching layer?"
```

### Global Flags

| Flag | Description |
|------|-------------|
| `--json` | Output raw JSON instead of Rich-formatted tables |
| `--help` | Show help for any command |

### JSON Output Example

```bash
python -m cli.main review-pr --diff data/diffs/sample_pr.diff --json
```

Returns a serialized `OrchestratorResult` with all agent outputs.

---

## Core Modules

### agent_interface.py

Defines the contract all agents must follow.

**`AgentInput`** - Pydantic model wrapping a `context: dict` passed to every agent.

**`AgentOutput`** - Pydantic model returned by every agent:
```python
class AgentOutput(BaseModel):
    agent: str                    # canonical agent name
    status: str = "success"       # "success" | "error"
    confidence: float = 0.0       # 0.0 - 1.0
    issues: list[str] = []        # problems found
    recommendations: list[str] = []  # suggested fixes
    raw: dict[str, Any] = {}      # agent-specific payload
```

**`AgentBase`** - Abstract base class:
```python
class AgentBase(ABC):
    agent_name: str = "base"

    @abstractmethod
    async def run(self, input: AgentInput) -> AgentOutput: ...

    # Helpers:
    def _parse_llm_json(self, text: str) -> dict      # Extract JSON from LLM text
    def _build_output(self, data: dict) -> AgentOutput  # Filter & construct output
    def _error_output(self, message: str) -> AgentOutput # Error response
```

### agent_runner.py

Executes agents concurrently using `asyncio.gather`.

- **Per-agent timeout** via `asyncio.wait_for` (default: 120s)
- **Error isolation** - one failing agent never crashes others
- **Rich live progress** - real-time status table (pending/running/success/error)
- Failed agents return an `AgentOutput` with `status="error"` instead of raising

### agent_registry.py

Maps workflow names to lists of agent instances.

```python
# Workflow constants
WORKFLOW_PR_REVIEW    = "pr_review"
WORKFLOW_INCIDENT     = "incident"
WORKFLOW_BACKLOG      = "backlog"
WORKFLOW_TESTS        = "generate_tests"
WORKFLOW_SUMMARY      = "daily_summary"
WORKFLOW_KNOWLEDGE    = "knowledge"
WORKFLOW_DEBATE       = "debate"
```

Key methods:
- `register(workflow, agent)` - Add one agent to a workflow
- `register_many(workflow, agents)` - Add multiple agents
- `get_agents_for_workflow(workflow)` - Returns agent list (copy)
- `summary()` - Returns `{workflow: [agent_names]}` for diagnostics

### orchestrator.py

Coordinates multi-agent workflows.

**`OrchestratorResult`** - Aggregated output:
```python
class OrchestratorResult(BaseModel):
    workflow: str
    status: str               # "success" | "partial" | "error"
    agent_count: int
    success_count: int
    error_count: int
    overall_confidence: float  # mean of successful agents
    all_issues: list[str]      # deduplicated, insertion-ordered
    all_recommendations: list[str]
    agent_outputs: list[AgentOutput]
    duration_seconds: float
```

Status logic:
- `"success"` - all agents succeeded
- `"partial"` - some agents succeeded, some failed
- `"error"` - all agents failed

### container.py

**Composition root** - the single place where all dependencies are wired together.

```python
def build_orchestrator(
    model: str = "claude-sonnet-4-6",
    show_progress: bool = True,
    agent_timeout: float = 120.0,
) -> tuple[Orchestrator, ServiceContainer]:
```

- Auto-detects `ANTHROPIC_API_KEY` to choose between `LLMService` and `MockLLMService`
- Creates all services (`LLM`, `GitHub`, `Jira`, `Log`, `VectorStore`)
- Instantiates all agents with injected services
- Registers agents to their workflows via `AgentRegistry`
- Returns a ready-to-use `Orchestrator`

### prompt_loader.py

Loads prompt templates from `prompts/` directory.

```python
load_prompt("code_review")                    # loads prompts/code_review_prompt.txt
load_prompt("knowledge", context="...")       # loads + substitutes {context}
```

---

## Agents

All agents inherit `AgentBase` and implement `async run(input: AgentInput) -> AgentOutput`.

### Agent Summary

| Agent | Class | File | Context Keys | Workflows |
|-------|-------|------|-------------|-----------|
| `code-review` | `CodeReviewAgent` | `agents/code_review_agent.py` | `diff` | `pr_review` |
| `security` | `SecurityAgent` | `agents/security_agent.py` | `diff` | `pr_review` |
| `architecture` | `ArchitectureAgent` | `agents/architecture_agent.py` | `diff` or `description` | `pr_review` |
| `incident` | `IncidentAgent` | `agents/incident_agent.py` | `log` or `log_path` | `incident` |
| `sre` | `SREAgent` | `agents/sre_agent.py` | `log` or `log_path` | `incident` |
| `backlog` | `BacklogAgent` | `agents/backlog_agent.py` | `tickets` or `tickets_path` | `backlog` |
| `knowledge` | `KnowledgeAgent` | `agents/knowledge_agent.py` | `question` | `knowledge` |
| `test-generation` | `TestGenerationAgent` | `agents/test_generation_agent.py` | `diff` | `pr_review`, `generate_tests` |
| `summary` | `SummaryAgent` | `agents/summary_agent.py` | `merged_prs`, `open_incidents`, `deploy_count`, `backlog_changes` | `daily_summary` |
| `cost` | `CostAgent` | `agents/cost_agent.py` | `topic`, `proposal` | debate only |
| `debate-synthesizer` | `DebateSynthesizerAgent` | `agents/orchestrator_agent.py` | `topic`, `transcript` | debate only |

### Agent Execution Pattern

Every agent follows the same pattern:

```python
class MyAgent(AgentBase):
    agent_name = "my-agent"

    def __init__(self, llm_service, other_service) -> None:
        self.llm = llm_service
        self.other = other_service

    async def run(self, input: AgentInput) -> AgentOutput:
        # 1. Extract context
        data = input.context.get("key", "")
        if not data:
            return self._error_output("Missing required context key")

        # 2. Load prompt
        system = load_prompt("my_agent")

        # 3. Call LLM
        try:
            result = await self.llm.complete_json(system, user_message)
            return self._build_output(result)
        except Exception as exc:
            return self._error_output(str(exc))
```

---

## Services

### LLMService (`services/llm_service.py`)

Async wrapper around the Anthropic Messages API.

- `complete(system, user) -> str` - Returns raw text response
- `complete_json(system, user) -> dict` - Returns parsed JSON from response
- Uses `asyncio.to_thread()` to wrap synchronous SDK calls
- Exponential backoff retry on HTTP 429 (rate limit) and 529 (overload)
- JSON extraction handles: raw JSON, fenced code blocks, embedded JSON in prose

### MockLLMService (`services/mock_llm_service.py`)

Drop-in replacement for `LLMService` that returns pre-written responses.

- No API key or network required
- Detects which agent is calling by matching keywords in the system prompt
- Returns realistic canned responses for all 14 agent/debate contexts
- Same interface as `LLMService` (`complete`, `complete_json`)

### VectorStoreService (`services/vector_store.py`)

ChromaDB-backed semantic retrieval for the RAG knowledge system.

- Uses `PersistentClient` at `data/chroma_db/` (survives restarts)
- Default embedding: `all-MiniLM-L6-v2` via onnxruntime (local, no API key)
- `add_documents(documents, ids, metadatas)` - Upsert chunks
- `query(text, n_results=5)` - Semantic similarity search (L2 distance)
- `reset()` - Wipe and recreate the collection
- `count()` - Total chunks stored

### GitHubService (`services/github_service.py`)

Utilities for parsing git diffs and extracting changed file lists.

### JiraService (`services/jira_service.py`)

Utilities for loading and parsing ticket data from JSON files.

### LogService (`services/log_service.py`)

Utilities for reading and parsing log files (supports both raw text and file paths).

---

## Workflows

A workflow is a named group of agents that run together for a task.

### Workflow Execution Flow

```
1. CLI command invoked (e.g., `review-pr --diff pr.diff`)
2. CLI calls _run_workflow("pr_review", {"diff": "..."})
3. build_orchestrator() wires all dependencies
4. orchestrator.execute("pr_review", context) called
5. AgentRegistry resolves "pr_review" -> [CodeReview, Security, Architecture, TestGen]
6. AgentRunner.run_all() executes 4 agents via asyncio.gather
7. Orchestrator._aggregate() merges results:
   - Deduplicates issues and recommendations
   - Computes mean confidence of successful agents
   - Determines overall status (success/partial/error)
8. CLI renders OrchestratorResult with Rich panels
```

### Workflow-Agent Mapping

| Workflow | CLI Command | Agents | Concurrency |
|----------|-------------|--------|-------------|
| `pr_review` | `review-pr` | CodeReview, Security, Architecture, TestGeneration | 4 parallel |
| `incident` | `diagnose-incident` | Incident, SRE | 2 parallel |
| `backlog` | `manage-backlog` | Backlog | 1 |
| `generate_tests` | `generate-tests` | TestGeneration | 1 |
| `daily_summary` | `daily-summary` | Summary | 1 |
| `knowledge` | `ask-knowledge` | Knowledge | 1 |

---

## Debate Engine

The Agent Debate Mode (`core/debate_engine.py`) is a separate coordination system that runs a structured 4-round debate between agents.

### Debate Flow

```
Round 1: Architecture Agent proposes a design solution
    |
    v
Round 2: Security Agent + Cost Agent critique in parallel (asyncio.gather)
    |
    v
Round 3: Architecture Agent rebuts critiques and refines proposal
    |
    v
Round 4: Orchestrator Agent synthesizes all positions -> final decision
```

### DebateResult Model

```python
class DebateResult(BaseModel):
    topic: str
    status: str                   # "complete" | "error"
    rounds: list[DebateRound]     # all round contributions
    final_decision: str           # "proceed" | "reject" | "revise"
    recommendation: str           # full recommendation text
    overall_confidence: float
    dissenting_views: list[str]
    duration_seconds: float
```

### Usage

```bash
python -m cli.main debate "Should we add a Redis caching layer?" \
    --context "p99 latency is 450ms, Postgres CPU at 70%"
```

---

## RAG Knowledge System

The knowledge system uses ChromaDB for semantic search over engineering documentation.

### Ingesting Documents

```bash
# Ingest all docs from data/docs/
python scripts/ingest_docs.py

# Ingest from custom directory
python scripts/ingest_docs.py --docs-dir path/to/docs

# Reset knowledge base before ingesting
python scripts/ingest_docs.py --reset

# Custom chunk settings
python scripts/ingest_docs.py --chunk-size 500 --overlap 50
```

### How RAG Works

1. **Ingestion**: `scripts/ingest_docs.py` reads files from `data/docs/`, splits into 500-char chunks with 50-char overlap, and upserts into ChromaDB
2. **Query**: `KnowledgeAgent` receives a question, queries ChromaDB for top-5 similar chunks
3. **Synthesis**: Retrieved chunks are formatted as context blocks and sent to the LLM
4. **Response**: LLM synthesizes an answer with source attributions

### Supported File Types

`.md`, `.txt`, `.py`, `.rst`, `.yaml`, `.json`

### Storage

ChromaDB data persists at `data/chroma_db/`. Uses the `all-MiniLM-L6-v2` embedding model (runs locally via onnxruntime).

---

## Prompt Templates

All prompts live in `prompts/` and follow a consistent structure:

```
ROLE
<who the agent is>

TASK
<what it should do>

INPUT
<what it will receive>

OUTPUT FORMAT
<exact JSON schema to return>
```

### Prompt Files

| File | Agent |
|------|-------|
| `code_review_prompt.txt` | Code Review |
| `security_prompt.txt` | Security |
| `architecture_prompt.txt` | Architecture |
| `incident_prompt.txt` | Incident |
| `sre_prompt.txt` | SRE |
| `backlog_prompt.txt` | Backlog |
| `test_generation_prompt.txt` | Test Generation |
| `summary_prompt.txt` | Summary |
| `knowledge_prompt.txt` | Knowledge |
| `debate_propose_prompt.txt` | Debate: Proposal |
| `debate_critique_prompt.txt` | Debate: Security Critique |
| `debate_cost_prompt.txt` | Debate: Cost Evaluation |
| `debate_rebuttal_prompt.txt` | Debate: Rebuttal |
| `debate_synthesize_prompt.txt` | Debate: Synthesis |

### Loading Prompts

```python
from core.prompt_loader import load_prompt

# Simple load
system = load_prompt("code_review")

# With variable substitution
system = load_prompt("knowledge", context="retrieved chunks here")
```

---

## CLI Output Rendering

All display logic is in `cli/output.py`, keeping `cli/main.py` free of formatting.

### Render Functions

| Function | Used By | Purpose |
|----------|---------|---------|
| `render_result()` | All commands | Header panel + agent table + issues + recommendations |
| `render_test_code()` | `review-pr`, `generate-tests` | Syntax-highlighted Python test code |
| `render_incident_detail()` | `diagnose-incident` | Root cause panel + immediate actions |
| `render_backlog_detail()` | `manage-backlog` | Priority table + duplicates panel |
| `render_knowledge_answer()` | `ask-knowledge` | Confidence-colored answer + sources |
| `render_debate_result()` | `debate` | Transcript table + final decision |
| `render_daily_summary()` | `daily-summary` | Health badge + summary text |

---

## Testing

### Running Tests

```bash
# Run all tests
python -m pytest tests/ -v

# Run specific phase
python -m pytest tests/test_phase4.py -v

# Run with coverage
python -m pytest tests/ --cov=core --cov=agents --cov=services --cov=cli
```

### Test Organization

| File | Tests | Coverage |
|------|-------|----------|
| `test_phase2.py` | 14 | AgentBase, AgentRunner, LLMService JSON extraction, PromptLoader |
| `test_phase3.py` | 20 | AgentRegistry, Orchestrator (success/partial/error scenarios) |
| `test_phase4.py` | 28 | All agents with MockLLM, container wiring |
| `test_phase5.py` | 20 | CLI via Typer CliRunner with MockOrchestrator |
| `test_phase6.py` | 22 | KnowledgeAgent, chunking, MockVectorStore, CLI integration |
| `test_phase7.py` | 24 | DebateEngine, DebateResult, CostAgent, CLI integration |

### Testing Strategy

- **Unit tests**: Each agent tested with a mock LLM that returns controlled JSON
- **Integration tests**: CLI tested via Typer's `CliRunner` with mock orchestrator
- **Isolation**: Tests use mock services; no real API calls or database needed
- **No flaky tests**: All tests are deterministic (mock responses, no network)

---

## Adding a New Agent

### Step 1: Create the Agent

Create `agents/my_agent.py`:

```python
from core.agent_interface import AgentBase, AgentInput, AgentOutput
from core.prompt_loader import load_prompt
from services.llm_service import LLMService


class MyAgent(AgentBase):
    agent_name = "my-agent"

    def __init__(self, llm_service: LLMService) -> None:
        self.llm = llm_service

    async def run(self, input: AgentInput) -> AgentOutput:
        data = input.context.get("my_key", "")
        if not data:
            return self._error_output("Missing context key: my_key")

        system = load_prompt("my_agent")
        user = f"Analyze this:\n\n{data}"

        try:
            result = await self.llm.complete_json(system, user)
            return self._build_output(result)
        except Exception as exc:
            return self._error_output(str(exc))
```

### Step 2: Create the Prompt

Create `prompts/my_agent_prompt.txt`:

```
ROLE
You are an expert in <domain>.

TASK
Analyze the provided input and identify <things>.

INPUT
<description of what the agent receives>

OUTPUT FORMAT
Return a JSON object with this exact schema:
{
  "agent": "my-agent",
  "status": "success",
  "confidence": <float 0.0-1.0>,
  "issues": [<string>, ...],
  "recommendations": [<string>, ...],
  "raw": { <agent-specific fields> }
}
```

### Step 3: Register in a Workflow

Edit `core/container.py` in `_register_agents()`:

```python
from agents.my_agent import MyAgent

registry.register(WORKFLOW_MY_WORKFLOW,
    MyAgent(llm_service=svc.llm))
```

### Step 4: Add Mock Response (optional, for demo mode)

Edit `services/mock_llm_service.py`:

1. Add a canned response to `_RESPONSES`:
```python
"my_agent": {
    "agent": "my-agent",
    "status": "success",
    "confidence": 0.85,
    "issues": ["Issue 1"],
    "recommendations": ["Fix 1"],
    "raw": {},
},
```

2. Add keyword detection to `_KEYWORD_MAP`:
```python
(["unique", "keywords", "from_prompt"], "my_agent"),
```

---

## Adding a New Workflow

### Step 1: Define the Workflow Constant

Edit `core/agent_registry.py`:

```python
WORKFLOW_MY_WORKFLOW = "my_workflow"
```

### Step 2: Register Agents

Edit `core/container.py` in `_register_agents()`:

```python
from core.agent_registry import WORKFLOW_MY_WORKFLOW

registry.register_many(WORKFLOW_MY_WORKFLOW, [
    AgentA(llm_service=svc.llm),
    AgentB(llm_service=svc.llm, log_service=svc.log),
])
```

### Step 3: Add CLI Command

Edit `cli/main.py`:

```python
from core.agent_registry import WORKFLOW_MY_WORKFLOW

@app.command("my-command")
def my_command(
    input_file: str = typer.Option(..., "--input", "-i", help="Input file"),
    json_output: bool = typer.Option(False, "--json"),
) -> None:
    """Run my workflow."""
    data = _read_file(input_file, "input file")
    result = _run_workflow(WORKFLOW_MY_WORKFLOW, {"my_key": data}, json_output)

    if json_output:
        print(json_lib.dumps(result.to_dict(), indent=2))
    else:
        render_result(result, console)
```

---

## Configuration Reference

### Environment Variables

| Variable | Required | Description |
|----------|----------|-------------|
| `ANTHROPIC_API_KEY` | No | Anthropic API key. If unset, demo mode is used. |

### Default Values

| Setting | Value | Location |
|---------|-------|----------|
| Claude model | `claude-sonnet-4-6` | `core/container.py:56` |
| Agent timeout | 120 seconds | `core/agent_runner.py:26` |
| LLM max tokens | 4096 | `services/llm_service.py:49` |
| LLM retry attempts | 3 | `services/llm_service.py:30` |
| LLM retry base delay | 1.0s | `services/llm_service.py:31` |
| ChromaDB path | `data/chroma_db/` | `services/vector_store.py:23` |
| ChromaDB collection | `engineering_kb` | `services/vector_store.py:24` |
| RAG chunk size | 500 characters | `scripts/ingest_docs.py:33` |
| RAG chunk overlap | 50 characters | `scripts/ingest_docs.py:34` |

---

## Data Models

### AgentInput

```python
class AgentInput(BaseModel):
    context: dict[str, Any] = {}
```

### AgentOutput

```python
class AgentOutput(BaseModel):
    agent: str                      # e.g., "code-review"
    status: str = "success"         # "success" | "error"
    confidence: float = 0.0         # 0.0 - 1.0
    issues: list[str] = []
    recommendations: list[str] = []
    raw: dict[str, Any] = {}        # agent-specific payload
```

### OrchestratorResult

```python
class OrchestratorResult(BaseModel):
    workflow: str                    # e.g., "pr_review"
    status: str                     # "success" | "partial" | "error"
    agent_count: int
    success_count: int
    error_count: int
    overall_confidence: float       # mean of successful agents
    all_issues: list[str]           # deduplicated
    all_recommendations: list[str]  # deduplicated
    agent_outputs: list[AgentOutput]
    duration_seconds: float
```

### DebateRound

```python
class DebateRound(BaseModel):
    round_number: int               # 1-4
    participant: str                 # "architecture" | "security" | "cost" | "orchestrator"
    role: str                       # "proposal" | "critique" | "cost_evaluation" | "rebuttal" | "synthesis"
    summary: str
    confidence: float = 0.0
    raw: dict[str, Any] = {}
```

### DebateResult

```python
class DebateResult(BaseModel):
    topic: str
    status: str                     # "complete" | "error"
    rounds: list[DebateRound]
    final_decision: str             # "proceed" | "reject" | "revise"
    recommendation: str
    overall_confidence: float
    dissenting_views: list[str] = []
    duration_seconds: float
```

---

## Error Handling

### Agent-Level Isolation

The `AgentRunner._safe_run()` method wraps every agent execution:

- **Timeout**: `asyncio.wait_for(agent.run(), timeout=120)` - returns error `AgentOutput` on timeout
- **Exceptions**: Any unhandled exception is caught and converted to an error `AgentOutput`
- **Result**: A failing agent never crashes other agents or the orchestrator

### LLM Retry Logic

`LLMService._complete_sync()` retries on transient errors:

- HTTP 429 (rate limit) and 529 (overload) trigger exponential backoff
- Up to 3 attempts with delays of 1s, 2s, 4s
- Non-retryable errors propagate immediately

### JSON Parsing Resilience

Both `LLMService._extract_json()` and `AgentBase._parse_llm_json()` handle:

1. Raw JSON strings
2. JSON inside ` ```json ... ``` ` code fences
3. JSON embedded in surrounding prose text

### Orchestrator Status

- `"success"` - zero errors
- `"partial"` - at least one success and at least one error
- `"error"` - all agents failed

---

## Troubleshooting

### UnicodeEncodeError on Windows

If you see encoding errors with special characters, ensure the CLI uses ASCII-safe characters. The codebase has been sanitized for Windows cp1252 compatibility.

### "No module named 'cli'" when running commands

Run from the project root directory, or install with `pip install -e .`

### ChromaDB "less than n_results" warning

This is handled automatically. The `VectorStoreService.query()` clamps `n_results` to `min(n_results, collection.count())`.

### pip not recognized (Windows)

Use the full Python path:
```bash
C:\Users\<user>\AppData\Local\Programs\Python\Python311\python.exe -m pip install -e .
```

### Empty knowledge base responses

Run the ingestion script first:
```bash
python scripts/ingest_docs.py
```

In demo mode, the knowledge agent bypasses the vector store entirely and returns a canned response.
