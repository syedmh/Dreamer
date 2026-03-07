"""
Code Review Agent.

Analyzes git diffs for performance issues, bad patterns, and missing tests.
Runs as part of the PR review workflow.

Expected context keys:
    diff (str): unified diff text from a pull request
"""

from __future__ import annotations

from core.agent_interface import AgentBase, AgentInput, AgentOutput
from core.prompt_loader import load_prompt
from services.llm_service import LLMService
from services.github_service import GitHubService


class CodeReviewAgent(AgentBase):
    agent_name = "code-review"

    def __init__(self, llm_service: LLMService, github_service: GitHubService) -> None:
        self.llm = llm_service
        self.github = github_service

    async def run(self, input: AgentInput) -> AgentOutput:
        diff = input.context.get("diff", "").strip()
        if not diff:
            return self._error_output("No diff provided in context['diff']")

        system = load_prompt("code_review")
        user = (
            "Review the following pull request diff. "
            "Return ONLY a JSON object matching the specified schema.\n\n"
            f"```diff\n{diff}\n```"
        )

        try:
            data = await self.llm.complete_json(system, user)
            # Augment raw payload with parsed changed-file list
            data.setdefault("raw", {})
            data["raw"]["changed_files"] = self.github.get_changed_files(diff)
            return self._build_output(data)
        except Exception as exc:
            return self._error_output(str(exc))
