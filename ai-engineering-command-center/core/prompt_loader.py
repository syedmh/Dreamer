"""
Prompt loader utility.

Loads prompt templates from the prompts/ directory and supports
simple {variable} substitution.
"""

from pathlib import Path

_PROMPTS_DIR = Path(__file__).parent.parent / "prompts"


def load_prompt(name: str, **variables: str) -> str:
    """
    Load a prompt template by agent name and substitute variables.

    Args:
        name: Agent name, e.g. "code_review" → loads prompts/code_review_prompt.txt
        **variables: Key/value pairs to substitute into the template.

    Returns:
        Prompt text with all {key} placeholders replaced.

    Raises:
        FileNotFoundError: If the prompt file does not exist.
    """
    path = _PROMPTS_DIR / f"{name}_prompt.txt"
    if not path.exists():
        raise FileNotFoundError(f"Prompt template not found: {path}")
    text = path.read_text(encoding="utf-8")
    if variables:
        text = text.format(**variables)
    return text
