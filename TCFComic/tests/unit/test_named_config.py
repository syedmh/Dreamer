import json

import pytest
import yaml

from conftest import write_config
from tcfcomic.config import DEFAULT_PROMPT, load_config
from tcfcomic.domain import AppError


@pytest.mark.parametrize("prompts", [
    {}, None, [], "text", {"": "text"}, {"../x": "text"}, {"a/b": "text"},
    {"a\\b": "text"}, {"a.b": "text"}, {"a_b": "text"}, {"A": "text"},
    {"a--b": "text"}, {"1abc": "text"}, {"a-": "text"}, {"a" * 33: "text"},
    {"con": "text"}, {"com1": "text"}, {"a\u00e9": "text"},
    {"a\n": "text"}, {"a": ""}, {"a": "  "}, {"a": None}, {"a": True},
    {1: "text"}, {"a": ["text"]},
])
def test_invalid_named_prompts(tmp_path, prompts):
    path, _ = write_config(tmp_path)
    data = yaml.safe_load(path.read_text())
    data["provider"]["prompts"] = prompts
    path.write_text(yaml.safe_dump(data), encoding="utf-8")
    with pytest.raises(AppError):
        load_config(path)


def test_prompt_modes_exclusive_and_duplicates_rejected(tmp_path):
    path, _ = write_config(tmp_path)
    original = path.read_text()
    for extra in (
        "  prompt: single\n  prompts:\n    one: text\n",
        "  prompts:\n    one: text\n    one: other\n",
        "  prompts:\n    one: text\n  prompts:\n    two: other\n",
    ):
        path.write_text(original.replace("  name: fake\n", "  name: fake\n" + extra))
        with pytest.raises(AppError):
            load_config(path)


def test_named_order_exact_whitespace_and_legacy_default(tmp_path):
    path, original = write_config(tmp_path)
    assert original.provider.prompt == DEFAULT_PROMPT and original.provider.prompts is None
    data = yaml.safe_load(path.read_text())
    values = {"z-first": " first\n\nparagraph \u2019 ", "a-second": "second"}
    data["provider"]["prompts"] = values
    path.write_text(yaml.safe_dump(data, sort_keys=False), encoding="utf-8")
    config = load_config(path)
    assert config.provider.variants == ("z-first", "a-second")
    assert dict(config.provider.prompts) == values
    assert config.provider.prompt_for("z-first") == values["z-first"]
    with pytest.raises(AppError):
        config.provider.prompt_for("")


@pytest.mark.parametrize("style", ["literal", "folded", "quoted"])
def test_named_yaml_preserves_exact_synthetic_paragraphs(tmp_path, style):
    path, _ = write_config(tmp_path)
    if style == "literal":
        scalars = (
            "    tcf-school: |-\n"
            "      First synthetic paragraph.\n\n"
            "      Second synthetic paragraph.\n"
            "    pakistani-80s: |+\n"
            "      Other synthetic paragraph.\n\n"
            "      Final synthetic paragraph.\n\n"
        )
        expected = {
            "tcf-school": "First synthetic paragraph.\n\nSecond synthetic paragraph.",
            "pakistani-80s": "Other synthetic paragraph.\n\nFinal synthetic paragraph.\n\n",
        }
    elif style == "folded":
        scalars = (
            "    tcf-school: >-\n"
            "      First synthetic\n"
            "      paragraph.\n\n"
            "      Second paragraph.\n"
            "    pakistani-80s: >\n"
            "      Other synthetic\n"
            "      paragraph.\n\n"
            "      Final paragraph.\n"
        )
        expected = {
            "tcf-school": "First synthetic paragraph.\nSecond paragraph.",
            "pakistani-80s": "Other synthetic paragraph.\nFinal paragraph.\n",
        }
    else:
        expected = {
            "tcf-school": "  Synthetic \u201cfirst\u201d paragraph.\n\nSecond paragraph.  ",
            "pakistani-80s": "\tOther synthetic paragraph.\n\nFinal \u2019 paragraph.\n\n",
        }
        scalars = "".join(f"    {name}: {json.dumps(value)}\n" for name, value in expected.items())
    path.write_text(
        path.read_text(encoding="utf-8").replace(
            "  name: fake\n", "  name: fake\n  prompts:\n" + scalars,
        ),
        encoding="utf-8",
    )
    before = path.read_bytes()
    config = load_config(path)
    assert config.provider.variants == tuple(expected)
    assert dict(config.provider.prompts) == expected
    for name, text in expected.items():
        assert config.provider.prompt_for(name) == text
    assert path.read_bytes() == before
