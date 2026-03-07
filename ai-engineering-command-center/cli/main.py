"""
AI Engineering Command Center — CLI entry point.

Usage:
    ai-engineering review-pr --diff pr.diff
    ai-engineering diagnose-incident --log logs/payment.log
    ai-engineering manage-backlog --tickets tickets.json
    ai-engineering ask-knowledge "How does the retry queue work?"
    ai-engineering generate-tests --diff pr.diff
    ai-engineering daily-summary --prs 5 --incidents 1 --deploys 3
"""

from __future__ import annotations

import asyncio
import json as json_lib
import sys
from pathlib import Path

import typer
from rich.console import Console

from cli.output import (
    render_result,
    render_test_code,
    render_incident_detail,
    render_backlog_detail,
    render_daily_summary,
    render_knowledge_answer,
    render_debate_result,
)
from core.agent_registry import (
    WORKFLOW_PR_REVIEW,
    WORKFLOW_INCIDENT,
    WORKFLOW_BACKLOG,
    WORKFLOW_TESTS,
    WORKFLOW_SUMMARY,
    WORKFLOW_KNOWLEDGE,
)

app = typer.Typer(
    name="ai-engineering",
    help="AI Engineering Command Center — multi-agent engineering automation.",
    no_args_is_help=True,
    rich_markup_mode="rich",
)
console = Console()
err_console = Console(stderr=True, style="bold red")


# ---------------------------------------------------------------------------
# Shared helpers
# ---------------------------------------------------------------------------

def _build_orch(show_progress: bool = True):
    from core.container import build_orchestrator
    orchestrator, _ = build_orchestrator(show_progress=show_progress)
    return orchestrator


def _read_file(path: str, label: str) -> str:
    """Read a file, print a friendly error and exit on failure."""
    try:
        return Path(path).read_text(encoding="utf-8")
    except FileNotFoundError:
        err_console.print(f"{label} not found: {path}")
        raise typer.Exit(1)
    except Exception as exc:
        err_console.print(f"Could not read {label}: {exc}")
        raise typer.Exit(1)


def _run_workflow(workflow: str, context: dict, json_output: bool) -> None:
    """Execute a workflow and handle top-level errors."""
    orchestrator = _build_orch(show_progress=not json_output)
    try:
        result = asyncio.run(orchestrator.execute(workflow, context))
    except ValueError as exc:
        err_console.print(str(exc))
        raise typer.Exit(1)
    except Exception as exc:
        err_console.print(f"Workflow failed: {exc}")
        raise typer.Exit(1)
    return result


# ---------------------------------------------------------------------------
# Commands
# ---------------------------------------------------------------------------

@app.command("review-pr")
def review_pr(
    diff: str = typer.Option(..., "--diff", "-d",
                             help="Path to a unified diff file"),
    json_output: bool = typer.Option(False, "--json",
                                     help="Emit raw JSON instead of Rich output"),
) -> None:
    """
    Run [cyan]Code Review[/cyan], [cyan]Security[/cyan], [cyan]Architecture[/cyan],
    and [cyan]Test Generation[/cyan] agents concurrently on a pull request diff.
    """
    diff_text = _read_file(diff, "diff file")
    result = _run_workflow(WORKFLOW_PR_REVIEW, {"diff": diff_text}, json_output)

    if json_output:
        print(json_lib.dumps(result.to_dict(), indent=2))
    else:
        render_result(result, console)
        render_test_code(result, console)


@app.command("diagnose-incident")
def diagnose_incident(
    log: str = typer.Option(..., "--log", "-l",
                            help="Path to a log file"),
    json_output: bool = typer.Option(False, "--json",
                                     help="Emit raw JSON instead of Rich output"),
) -> None:
    """
    Run [cyan]Incident Diagnosis[/cyan] and [cyan]SRE[/cyan] agents on a log file.
    """
    # Pass the path; agents read the file themselves (supports large logs)
    log_path = str(Path(log).resolve())
    if not Path(log_path).exists():
        err_console.print(f"Log file not found: {log}")
        raise typer.Exit(1)

    result = _run_workflow(WORKFLOW_INCIDENT, {"log_path": log_path}, json_output)

    if json_output:
        print(json_lib.dumps(result.to_dict(), indent=2))
    else:
        render_result(result, console)
        render_incident_detail(result, console)


@app.command("manage-backlog")
def manage_backlog(
    tickets: str = typer.Option(..., "--tickets", "-t",
                                help="Path to a JSON tickets file"),
    json_output: bool = typer.Option(False, "--json",
                                     help="Emit raw JSON instead of Rich output"),
) -> None:
    """
    Run the [cyan]Backlog[/cyan] agent to deduplicate and prioritize tickets.
    """
    tickets_path = str(Path(tickets).resolve())
    if not Path(tickets_path).exists():
        err_console.print(f"Tickets file not found: {tickets}")
        raise typer.Exit(1)

    result = _run_workflow(WORKFLOW_BACKLOG, {"tickets_path": tickets_path}, json_output)

    if json_output:
        print(json_lib.dumps(result.to_dict(), indent=2))
    else:
        render_result(result, console)
        render_backlog_detail(result, console)


@app.command("ask-knowledge")
def ask_knowledge(
    question: str = typer.Argument(..., help="Engineering question to answer"),
    json_output: bool = typer.Option(False, "--json",
                                     help="Emit raw JSON instead of Rich output"),
) -> None:
    """
    Answer an engineering question using [cyan]RAG[/cyan] over internal docs.

    [dim]Requires Phase 6 (RAG knowledge system) to be complete.[/dim]
    """
    result = _run_workflow(WORKFLOW_KNOWLEDGE, {"question": question}, json_output)
    if json_output:
        print(json_lib.dumps(result.to_dict(), indent=2))
    else:
        render_result(result, console)
        render_knowledge_answer(result, console)


@app.command("generate-tests")
def generate_tests(
    diff: str = typer.Option(..., "--diff", "-d",
                             help="Path to a unified diff file"),
    json_output: bool = typer.Option(False, "--json",
                                     help="Emit raw JSON instead of Rich output"),
) -> None:
    """
    Generate [cyan]pytest[/cyan] unit tests for code changes in a diff.
    """
    diff_text = _read_file(diff, "diff file")
    result = _run_workflow(WORKFLOW_TESTS, {"diff": diff_text}, json_output)

    if json_output:
        print(json_lib.dumps(result.to_dict(), indent=2))
    else:
        render_result(result, console)
        render_test_code(result, console)


@app.command("daily-summary")
def daily_summary(
    prs: int      = typer.Option(0, "--prs",       help="PRs merged today"),
    incidents: int = typer.Option(0, "--incidents", help="Open incidents"),
    deploys: int  = typer.Option(0, "--deploys",   help="Deployments today"),
    changes: int  = typer.Option(0, "--changes",   help="Backlog ticket changes"),
    notes: str    = typer.Option("", "--notes",    help="Free-text team notes"),
    json_output: bool = typer.Option(False, "--json",
                                     help="Emit raw JSON instead of Rich output"),
) -> None:
    """
    Generate an [cyan]executive engineering daily summary[/cyan].
    """
    context = {
        "merged_prs":      prs,
        "open_incidents":  incidents,
        "deploy_count":    deploys,
        "backlog_changes": changes,
        "notes":           notes,
    }
    result = _run_workflow(WORKFLOW_SUMMARY, context, json_output)

    if json_output:
        print(json_lib.dumps(result.to_dict(), indent=2))
    else:
        render_result(result, console)
        render_daily_summary(result, console)


@app.command("debate")
def debate(
    topic: str = typer.Argument(..., help="Architecture decision to debate"),
    context: str = typer.Option("", "--context", "-c",
                                help="Background context (metrics, constraints)"),
    json_output: bool = typer.Option(False, "--json",
                                     help="Emit raw JSON instead of Rich output"),
) -> None:
    """
    Run a [bold]4-round Agent Debate[/bold] on an architecture decision.

    [dim]Round 1:[/dim] Architecture Agent proposes a design
    [dim]Round 2:[/dim] Security + Cost Agents critique in parallel
    [dim]Round 3:[/dim] Architecture Agent rebuts
    [dim]Round 4:[/dim] Orchestrator synthesizes the final decision
    """
    from core.container import ServiceContainer, _make_llm
    from core.debate_engine import DebateEngine

    llm = _make_llm("claude-sonnet-4-6")
    engine = DebateEngine(llm_service=llm, show_progress=not json_output)

    try:
        result = asyncio.run(engine.run(topic=topic, context=context))
    except Exception as exc:
        err_console.print(f"Debate failed: {exc}")
        raise typer.Exit(1)

    if json_output:
        print(json_lib.dumps(result.to_dict(), indent=2))
    else:
        render_debate_result(result, console)


if __name__ == "__main__":
    app()
