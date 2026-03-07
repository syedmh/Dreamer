"""
Service container — composition root.

Single place where all services are constructed and all agents are
wired into the registry. The CLI and tests call `build_orchestrator()`
to get a ready-to-use Orchestrator.

Mock mode:
    When ANTHROPIC_API_KEY is not set, MockLLMService is used automatically.
    All commands work with realistic pre-written responses — no API key needed.
"""

from __future__ import annotations

import os

from services.github_service import GitHubService
from services.jira_service import JiraService
from services.log_service import LogService
from services.vector_store import VectorStoreService

from core.agent_registry import (
    AgentRegistry,
    WORKFLOW_PR_REVIEW,
    WORKFLOW_INCIDENT,
    WORKFLOW_BACKLOG,
    WORKFLOW_TESTS,
    WORKFLOW_SUMMARY,
    WORKFLOW_KNOWLEDGE,
)
from core.agent_runner import AgentRunner
from core.orchestrator import Orchestrator


def _make_llm(model: str):
    """
    Return a real LLMService if ANTHROPIC_API_KEY is set,
    otherwise return MockLLMService with pre-written responses.
    """
    if os.environ.get("ANTHROPIC_API_KEY"):
        from services.llm_service import LLMService
        return LLMService(model=model)
    else:
        from services.mock_llm_service import MockLLMService
        from rich.console import Console
        Console().print(
            "[dim yellow][DEMO] Demo mode - ANTHROPIC_API_KEY not set. "
            "Using mock responses.[/dim yellow]"
        )
        return MockLLMService()


class ServiceContainer:
    """Holds all shared service singletons."""

    def __init__(self, model: str = "claude-sonnet-4-6") -> None:
        self.llm     = _make_llm(model)
        self.github  = GitHubService()
        self.jira    = JiraService()
        self.log     = LogService()
        self.vectors = VectorStoreService()


def build_orchestrator(
    model: str = "claude-sonnet-4-6",
    show_progress: bool = True,
    agent_timeout: float = 120.0,
) -> tuple[Orchestrator, ServiceContainer]:
    """
    Composition root — builds and wires the full system.

    Returns:
        (orchestrator, services)
    """
    services = ServiceContainer(model=model)
    registry = AgentRegistry()
    runner   = AgentRunner(timeout=agent_timeout, show_progress=show_progress)

    _register_agents(registry, services)

    return Orchestrator(registry, runner), services


def _register_agents(registry: AgentRegistry, svc: ServiceContainer) -> None:
    """Instantiate every agent with injected services and register it."""

    # -- PR Review workflow ------------------------------------------
    from agents.code_review_agent import CodeReviewAgent
    from agents.security_agent import SecurityAgent
    from agents.architecture_agent import ArchitectureAgent
    from agents.test_generation_agent import TestGenerationAgent

    registry.register_many(WORKFLOW_PR_REVIEW, [
        CodeReviewAgent(llm_service=svc.llm, github_service=svc.github),
        SecurityAgent(llm_service=svc.llm, github_service=svc.github),
        ArchitectureAgent(llm_service=svc.llm),
        TestGenerationAgent(llm_service=svc.llm),
    ])

    # -- Incident workflow -------------------------------------------
    from agents.incident_agent import IncidentAgent
    from agents.sre_agent import SREAgent

    registry.register_many(WORKFLOW_INCIDENT, [
        IncidentAgent(llm_service=svc.llm, log_service=svc.log),
        SREAgent(llm_service=svc.llm, log_service=svc.log),
    ])

    # -- Backlog workflow --------------------------------------------
    from agents.backlog_agent import BacklogAgent

    registry.register(WORKFLOW_BACKLOG,
        BacklogAgent(llm_service=svc.llm, jira_service=svc.jira))

    # -- Test generation workflow ------------------------------------
    registry.register(WORKFLOW_TESTS,
        TestGenerationAgent(llm_service=svc.llm))

    # -- Daily summary workflow --------------------------------------
    from agents.summary_agent import SummaryAgent

    registry.register(WORKFLOW_SUMMARY,
        SummaryAgent(llm_service=svc.llm))

    # -- Knowledge / RAG workflow ------------------------------------
    from agents.knowledge_agent import KnowledgeAgent

    registry.register(WORKFLOW_KNOWLEDGE,
        KnowledgeAgent(llm_service=svc.llm, vector_store=svc.vectors))
