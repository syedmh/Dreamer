"""
LLM service.

Wraps the Anthropic client and exposes async completion methods.
All agents use this service — never the SDK directly.

Key design decisions:
- Anthropic's Python SDK is synchronous; we use asyncio.to_thread to
  run it in a thread pool so it never blocks the event loop.
- complete_json() extracts and parses JSON from the response text,
  handling the common case where the LLM wraps JSON in prose or fences.
- Exponential backoff retries on rate-limit (429) and overload (529) errors.
"""

from __future__ import annotations

import asyncio
import json
import logging
import re
import time
from typing import Any

import anthropic
from anthropic import APIStatusError

logger = logging.getLogger(__name__)

_RETRYABLE_STATUS_CODES = {429, 529}
_MAX_RETRIES = 3
_BASE_DELAY = 1.0   # seconds


class LLMService:
    """
    Async wrapper around the Anthropic Messages API.

    Args:
        model:      Claude model ID.
        max_tokens: Default token budget per completion.
    """

    def __init__(
        self,
        model: str = "claude-sonnet-4-6",
        max_tokens: int = 4096,
    ) -> None:
        self.model = model
        self.max_tokens = max_tokens
        self._client = anthropic.Anthropic()

    # ------------------------------------------------------------------
    # Public API
    # ------------------------------------------------------------------

    async def complete(self, system: str, user: str) -> str:
        """
        Send a system + user prompt and return the response text.

        Runs the synchronous Anthropic SDK call in a thread pool to
        avoid blocking the asyncio event loop.
        """
        return await asyncio.to_thread(self._complete_sync, system, user)

    async def complete_json(self, system: str, user: str) -> dict[str, Any]:
        """
        Send a prompt and parse the JSON object from the response.

        Raises:
            ValueError: If no valid JSON is found in the response.
        """
        text = await self.complete(system, user)
        return self._extract_json(text)

    # ------------------------------------------------------------------
    # Internal helpers
    # ------------------------------------------------------------------

    def _complete_sync(self, system: str, user: str) -> str:
        """Synchronous completion with retry on transient errors."""
        delay = _BASE_DELAY
        for attempt in range(1, _MAX_RETRIES + 1):
            try:
                message = self._client.messages.create(
                    model=self.model,
                    max_tokens=self.max_tokens,
                    system=system,
                    messages=[{"role": "user", "content": user}],
                )
                return message.content[0].text
            except APIStatusError as exc:
                if exc.status_code in _RETRYABLE_STATUS_CODES and attempt < _MAX_RETRIES:
                    logger.warning(
                        "LLM rate limited (attempt %d/%d), retrying in %.1fs",
                        attempt, _MAX_RETRIES, delay,
                    )
                    time.sleep(delay)
                    delay *= 2
                else:
                    raise

        raise RuntimeError("LLM retries exhausted")  # unreachable but satisfies type checker

    def _extract_json(self, text: str) -> dict[str, Any]:
        """Extract and parse a JSON object from LLM response text."""
        text = text.strip()

        # Attempt 1: direct parse
        try:
            return json.loads(text)
        except json.JSONDecodeError:
            pass

        # Attempt 2: JSON inside ``` fences
        fenced = re.search(r"```(?:json)?\s*(\{.*?\})\s*```", text, re.DOTALL)
        if fenced:
            return json.loads(fenced.group(1))

        # Attempt 3: first { ... } block in prose
        embedded = re.search(r"\{.*\}", text, re.DOTALL)
        if embedded:
            return json.loads(embedded.group(0))

        raise ValueError(
            f"No JSON object found in LLM response. First 300 chars: {text[:300]}"
        )
