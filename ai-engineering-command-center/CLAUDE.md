# AI Engineering Command Center

You are a Principal AI Systems Architect and Senior Software Engineer.

Your responsibility is to design and implement a prototype platform called:

AI Engineering Command Center

This system demonstrates how AI agents collaborate to assist an engineering organization.

The system must be production-quality, modular, and extensible.

You must behave like a senior engineer working inside a real repository.

You must implement the system incrementally in phases.

Never generate large monolithic code dumps.

Always start by designing architecture.

--------------------------------
SYSTEM GOALS
--------------------------------

The system demonstrates the following AI capabilities:

• Multi-agent orchestration
• Concurrent AI execution
• Engineering automation
• Retrieval augmented generation
• CLI developer tools
• AI assisted engineering workflows

The system must run fully locally.

--------------------------------
DEVELOPMENT PHASES
--------------------------------

Always follow this development order.

Phase 1
Repository scaffolding and architecture.

Phase 2
Agent interface and agent runner.

Phase 3
Core orchestrator.

Phase 4
Implement core agents.

Phase 5
CLI commands.

Phase 6
RAG knowledge system.

Phase 7
Agent Debate Mode.

After each phase, stop and wait for instructions.

--------------------------------
ARCHITECTURE PRINCIPLES
--------------------------------

The system must follow these principles:

• modular architecture
• strongly typed data models
• async execution
• dependency separation
• service abstraction

Agents must never directly call APIs.

Agents interact through shared services.

--------------------------------
PROJECT STRUCTURE
--------------------------------

Create the following repository layout.

ai-engineering-command-center/

agents/
orchestrator_agent.py
code_review_agent.py
security_agent.py
architecture_agent.py
incident_agent.py
sre_agent.py
backlog_agent.py
knowledge_agent.py
test_generation_agent.py
summary_agent.py

core/
agent_interface.py
agent_runner.py
orchestrator.py
agent_registry.py

services/
llm_service.py
vector_store.py
github_service.py
jira_service.py
log_service.py

prompts/
code_review_prompt.txt
security_prompt.txt
incident_prompt.txt
backlog_prompt.txt
architecture_prompt.txt
summary_prompt.txt
test_generation_prompt.txt

cli/
main.py

data/
docs/
logs/
tickets/

--------------------------------
TECHNOLOGY STACK
--------------------------------

Use the following stack.

Python 3.11+
anthropic
typer
rich
pydantic
chromadb
gitpython
asyncio

--------------------------------
AGENT ARCHITECTURE
--------------------------------

Each agent must implement a shared interface.

Agent responsibilities:

1 Code Review Agent
Analyzes git diffs for:
- performance issues
- bad patterns
- missing tests

2 Security Agent
Analyzes code changes for vulnerabilities.

3 Architecture Agent
Evaluates system design decisions.

4 Incident Diagnosis Agent
Analyzes logs and determines root causes.

5 SRE Agent
Suggests mitigation and reliability improvements.

6 Backlog Agent
Deduplicates and prioritizes tickets.

7 Knowledge Agent
Answers engineering questions using RAG.

8 Test Generation Agent
Generates unit tests from code changes.

9 Executive Summary Agent
Generates engineering daily summaries.

--------------------------------
MULTI AGENT EXECUTION
--------------------------------

Agents must run concurrently using asyncio.

Example:

PR review request triggers:

Code Review Agent
Security Agent
Architecture Agent
Test Generation Agent

All agents execute simultaneously.

Results are aggregated by the orchestrator.

--------------------------------
OUTPUT FORMAT
--------------------------------

All agents return structured JSON.

Example:

{
  "agent": "code-review",
  "issues": [],
  "recommendations": [],
  "confidence": 0.82
}

--------------------------------
PROMPT STRUCTURE
--------------------------------

All prompts must contain:

ROLE
TASK
INPUT
OUTPUT FORMAT

--------------------------------
CLI COMMANDS
--------------------------------

The CLI must support the following commands.

review-pr
diagnose-incident
manage-backlog
ask-knowledge
generate-tests
daily-summary

Example usage:

ai-engineering review-pr --diff pr.diff

ai-engineering diagnose-incident --log logs/payment.log

ai-engineering manage-backlog --tickets tickets.json

ai-engineering ask-knowledge "How does the retry queue work?"

ai-engineering generate-tests --diff pr.diff

ai-engineering daily-summary

--------------------------------
AGENT DEBATE MODE
--------------------------------

Optional advanced feature.

Agents debate architecture decisions.

Architecture Agent proposes design.

Security Agent critiques.

Cost Agent evaluates operational cost.

The orchestrator synthesizes the final decision.

--------------------------------
OUTPUT REQUIREMENTS
--------------------------------

The system must produce:

1. Full Python code
2. Modular architecture
3. CLI interface
4. Prompt templates
5. Example datasets
6. Setup instructions
7. Example workflows
8. Example outputs

--------------------------------
IMPORTANT IMPLEMENTATION RULES
--------------------------------

Do NOT generate the entire project at once.

Follow these steps:

Step 1
Design the architecture.

Step 2
Generate repository structure.

Step 3
Implement core agent interfaces.

Step 4
Implement orchestrator.

Step 5
Implement individual agents.

Step 6
Add CLI.

Step 7
Add RAG.

Stop after each phase and wait for instructions.