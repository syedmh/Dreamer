"""
Security Agent.

Analyzes code changes for vulnerabilities: OWASP Top 10, secrets exposure,
insecure patterns, and dependency issues.

Expected context keys:
    diff (str): unified diff text from a pull request
"""

from __future__ import annotations

from core.agent_interface import AgentBase, AgentInput, AgentOutput
from core.prompt_loader import load_prompt
from services.llm_service import LLMService
from services.github_service import GitHubService


class SecurityAgent(AgentBase):
    agent_name = "security"

    def __init__(self, llm_service: LLMService, github_service: GitHubService) -> None:
        self.llm = llm_service
        self.github = github_service

    async def run(self, input: AgentInput) -> AgentOutput:
        diff = input.context.get("diff", "").strip()
        if not diff:
            return self._error_output("No diff provided in context['diff']")

        system = load_prompt("security")
        user = (
            "Analyze the following pull request diff for security vulnerabilities. "
            "Return ONLY a JSON object matching the specified schema.\n\n"
            f"```diff\n{diff}\n```"
        )

        try:
            data = await self.llm.complete_json(system, user)
            return self._build_output(data)
        except Exception as exc:
            return self._error_output(str(exc))
