"""
Rich output rendering for the AI Engineering Command Center CLI.

All display logic lives here. cli/main.py calls these functions
and stays free of formatting concerns.
"""

from __future__ import annotations

import json

from rich import box
from rich.console import Console
from rich.panel import Panel
from rich.syntax import Syntax
from rich.table import Table
from rich.text import Text

from core.orchestrator import OrchestratorResult

_STATUS_COLOR = {"success": "green", "partial": "yellow", "error": "red"}
_HEALTH_COLOR = {"green": "green", "yellow": "yellow", "red": "red"}


# ---------------------------------------------------------------------------
# Top-level dispatcher
# ---------------------------------------------------------------------------

def render_result(
    result: OrchestratorResult,
    console: Console,
    *,
    json_output: bool = False,
) -> None:
    """Render an OrchestratorResult to the terminal."""
    if json_output:
        console.print_json(json.dumps(result.to_dict()))
        return

    _print_header(result, console)
    _print_agent_table(result, console)
    _print_issues(result, console)
    _print_recommendations(result, console)


def render_test_code(result: OrchestratorResult, console: Console) -> None:
    """
    After a PR-review or generate-tests run, display the generated test code
    from the test-generation agent with Python syntax highlighting.
    """
    for out in result.agent_outputs:
        if out.agent == "test-generation" and out.status == "success":
            code = out.raw.get("test_code", "").strip()
            if code:
                console.print(
                    Panel(
                        Syntax(code, "python", theme="monokai", line_numbers=True),
                        title="[bold cyan]Generated Tests[/bold cyan]",
                        border_style="cyan",
                    )
                )
            covered = out.raw.get("functions_covered", [])
            if covered:
                console.print(
                    f"[dim]Functions covered: {', '.join(covered)}[/dim]"
                )


def render_incident_detail(result: OrchestratorResult, console: Console) -> None:
    """
    For the incident workflow, highlight root cause and affected services
    prominently above the standard issues/recommendations panels.
    """
    for out in result.agent_outputs:
        if out.agent == "incident" and out.status == "success":
            root_cause = out.raw.get("root_cause", "")
            severity   = out.raw.get("severity", "")
            services   = out.raw.get("affected_services", [])

            if root_cause:
                sev_badge = f"  [bold red][{severity.upper()}][/bold red]" if severity else ""
                console.print(
                    Panel(
                        f"[bold]{root_cause}[/bold]{sev_badge}",
                        title="[bold red]Root Cause[/bold red]",
                        border_style="red",
                    )
                )
            if services:
                console.print(
                    f"[dim]Affected services:[/dim] "
                    + ", ".join(f"[yellow]{s}[/yellow]" for s in services)
                )
        if out.agent == "sre" and out.status == "success":
            actions = out.raw.get("immediate_actions", [])
            if actions:
                content = "\n".join(f"  [bold]->[/bold] {a}" for a in actions)
                console.print(
                    Panel(content, title="[bold yellow]Immediate Actions[/bold yellow]",
                          border_style="yellow")
                )


def render_backlog_detail(result: OrchestratorResult, console: Console) -> None:
    """
    For the backlog workflow, show a priority order table and a
    duplicates panel.
    """
    for out in result.agent_outputs:
        if out.agent == "backlog" and out.status == "success":
            priority_order = out.raw.get("priority_order", [])
            duplicates     = out.raw.get("duplicates", [])

            if priority_order:
                table = Table(title="Prioritized Backlog", box=box.SIMPLE_HEAVY,
                              show_header=True)
                table.add_column("#", style="dim", width=4)
                table.add_column("Ticket ID", style="cyan")
                for rank, ticket_id in enumerate(priority_order, 1):
                    table.add_row(str(rank), ticket_id)
                console.print(table)

            if duplicates:
                lines = "\n".join(
                    f"  [yellow]{a}[/yellow] == [yellow]{b}[/yellow]"
                    for a, b in duplicates
                )
                console.print(
                    Panel(lines,
                          title=f"[bold yellow]Duplicate Tickets ({len(duplicates)})[/bold yellow]",
                          border_style="yellow")
                )


def render_knowledge_answer(result: OrchestratorResult, console: Console) -> None:
    """
    For the knowledge workflow, show the answer prominently with
    source attributions and a context-sufficiency indicator.
    """
    for out in result.agent_outputs:
        if out.agent == "knowledge" and out.status == "success":
            answer   = out.raw.get("answer", "")
            sources  = out.raw.get("sources_used", [])
            sufficient = out.raw.get("context_was_sufficient", True)

            confidence_color = (
                "green" if out.confidence >= 0.7
                else "yellow" if out.confidence >= 0.4
                else "red"
            )

            footer_lines = []
            if sources:
                footer_lines.append(
                    "[dim]Sources: " + ", ".join(f"[cyan]{s}[/cyan]" for s in sources) + "[/dim]"
                )
            if not sufficient:
                footer_lines.append(
                    "[yellow][!] Context was insufficient - answer may be incomplete.[/yellow]"
                )

            footer = "\n" + "\n".join(footer_lines) if footer_lines else ""
            console.print(
                Panel(
                    (answer or "[dim]No answer returned.[/dim]") + footer,
                    title=(
                        f"[bold]Answer[/bold]  "
                        f"[{confidence_color}]confidence: {out.confidence:.0%}[/{confidence_color}]"
                    ),
                    border_style=confidence_color,
                )
            )


def render_debate_result(result, console: Console) -> None:
    """
    Render a DebateResult from the Agent Debate Engine.

    Shows each round's position and a prominent final-decision panel.
    """
    from core.debate_engine import DebateResult  # avoid circular at module level

    _DECISION_COLOR = {"proceed": "green", "reject": "red", "revise": "yellow"}
    _ROLE_LABEL = {
        "proposal":        "[cyan]Proposal[/cyan]",
        "critique":        "[red]Security Critique[/red]",
        "cost_evaluation": "[yellow]Cost Evaluation[/yellow]",
        "rebuttal":        "[blue]Rebuttal[/blue]",
        "synthesis":       "[bold magenta]Synthesis[/bold magenta]",
    }
    _PARTICIPANT_COLOR = {
        "architecture": "cyan",
        "security":     "red",
        "cost":         "yellow",
        "orchestrator": "magenta",
    }

    # Header
    color = _DECISION_COLOR.get(result.final_decision, "white")
    console.print(
        Panel(
            f"Topic: [bold white]{result.topic}[/bold white]\n"
            f"Status: [bold {color}]{result.status.upper()}[/bold {color}]   "
            f"Decision: [bold {color}]{result.final_decision.upper()}[/bold {color}]   "
            f"Confidence: [yellow]{result.overall_confidence:.0%}[/yellow]   "
            f"Time: [dim]{result.duration_seconds:.1f}s[/dim]",
            title="[bold]Agent Debate Mode[/bold]",
            border_style=color,
        )
    )

    # Rounds table
    table = Table(title="Debate Transcript", box=box.ROUNDED)
    table.add_column("Round", width=6, justify="center")
    table.add_column("Participant", style="bold", min_width=14)
    table.add_column("Role", min_width=18)
    table.add_column("Position", min_width=60)
    table.add_column("Conf.", justify="right", width=6)

    for r in result.rounds:
        pcol  = _PARTICIPANT_COLOR.get(r.participant, "white")
        label = _ROLE_LABEL.get(r.role, r.role)
        summary = r.summary[:120] + "..." if len(r.summary) > 120 else r.summary
        table.add_row(
            str(r.round_number),
            f"[{pcol}]{r.participant}[/{pcol}]",
            label,
            summary,
            f"{r.confidence:.0%}",
        )
    console.print(table)

    # Final decision panel
    decision_color = _DECISION_COLOR.get(result.final_decision, "white")
    rec_text = result.recommendation or "[dim]No recommendation returned.[/dim]"

    if result.dissenting_views:
        dissent = "\n\n[dim]Dissenting views:[/dim]\n" + "\n".join(
            f"  [dim]* {v}[/dim]" for v in result.dissenting_views
        )
    else:
        dissent = ""

    console.print(
        Panel(
            rec_text + dissent,
            title=f"[bold]Final Decision: {result.final_decision.upper()}[/bold]",
            border_style=decision_color,
        )
    )


def render_daily_summary(result, console: Console) -> None:
    """
    For the daily-summary workflow, show the executive summary text
    with a health-status badge.
    """
    for out in result.agent_outputs:
        if out.agent == "summary" and out.status == "success":
            health       = out.raw.get("health_status", "unknown")
            summary_text = out.raw.get("summary_text", "")
            color        = _HEALTH_COLOR.get(health, "white")
            badge        = f"[bold {color}][{health.upper()}][/bold {color}]"

            console.print(
                Panel(
                    summary_text or "[dim]No summary text returned.[/dim]",
                    title=f"[bold]Engineering Daily Summary[/bold]  {badge}",
                    border_style=color,
                )
            )


# ---------------------------------------------------------------------------
# Internal helpers
# ---------------------------------------------------------------------------

def _print_header(result: OrchestratorResult, console: Console) -> None:
    color  = _STATUS_COLOR.get(result.status, "white")
    status = Text(result.status.upper(), style=f"bold {color}")

    content = (
        f"Workflow: [cyan]{result.workflow}[/cyan]   "
        f"Status: [{color}]{result.status.upper()}[/{color}]   "
        f"Agents: [white]{result.success_count}/{result.agent_count}[/white]   "
        f"Confidence: [yellow]{result.overall_confidence:.0%}[/yellow]   "
        f"Time: [dim]{result.duration_seconds:.1f}s[/dim]"
    )
    console.print(
        Panel(content, title="[bold]AI Engineering Command Center[/bold]",
              border_style=color)
    )


def _print_agent_table(result: OrchestratorResult, console: Console) -> None:
    table = Table(box=box.ROUNDED, show_header=True, header_style="bold")
    table.add_column("Agent",      style="cyan", min_width=18)
    table.add_column("Status",     min_width=9)
    table.add_column("Confidence", justify="right", min_width=10)
    table.add_column("Issues",     justify="right", min_width=7)
    table.add_column("Recs",       justify="right", min_width=5)

    for out in result.agent_outputs:
        c = _STATUS_COLOR.get(out.status, "white")
        table.add_row(
            out.agent,
            f"[{c}]{out.status}[/{c}]",
            f"{out.confidence:.0%}",
            str(len(out.issues)),
            str(len(out.recommendations)),
        )
    console.print(table)


def _print_issues(result: OrchestratorResult, console: Console) -> None:
    if not result.all_issues:
        console.print("[dim]No issues found.[/dim]")
        return
    lines = "\n".join(f"  [red]*[/red] {i}" for i in result.all_issues)
    console.print(
        Panel(lines,
              title=f"[bold red]Issues ({len(result.all_issues)})[/bold red]",
              border_style="red")
    )


def _print_recommendations(result: OrchestratorResult, console: Console) -> None:
    if not result.all_recommendations:
        return
    lines = "\n".join(f"  [green]*[/green] {r}" for r in result.all_recommendations)
    console.print(
        Panel(lines,
              title=f"[bold green]Recommendations ({len(result.all_recommendations)})[/bold green]",
              border_style="green")
    )
