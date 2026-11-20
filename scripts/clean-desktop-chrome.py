#!/usr/bin/env python3
"""Cleans the KoLibri desktop code-behind after the sidebar-to-menu-bar
rewrite.

The shell used to have a sidebar, a context rail and a compact
navigation variant. All three are gone from the AXAML now, so the fields,
the FindControl calls and the methods that only served them have to go
too. This script removes them and reports what it touched.
"""

import re
import sys
from pathlib import Path

CS = Path("KoLibri.Desktop/MainWindow.axaml.cs")

# Fields that no longer have a counterpart in the AXAML.
DEAD_FIELDS = [
    "_sidebar",
    "_contextRail",
    "_compactNavigation",
    "_compactThemeToggleButton",
    "_sidebarThemeLabel",
    "_contextRailTitle",
    "_contextRailKind",
    "_contextRailDetails",
]

# Methods that only existed for the removed chrome.
DEAD_METHODS = [
    "UpdateContextRail",
    "SetActiveNavigation",
    "RefreshAccountStatusBrushes",
]


def strip_field_declarations(text: str) -> tuple[str, list[str]]:
    removed = []
    for field in DEAD_FIELDS:
        pattern = re.compile(
            rf"^\s*private\s+[\w<>?\[\]]+\s+{re.escape(field)};\n",
            re.MULTILINE)
        text, count = pattern.subn("", text)
        if count:
            removed.append(f"{field} (declaration)")
    return text, removed


def strip_find_controls(text: str) -> tuple[str, list[str]]:
    removed = []
    for field in DEAD_FIELDS:
        pattern = re.compile(
            rf"^\s*{re.escape(field)}\s*=\s*this\.FindControl<[^>]+>\(\"[^\"]+\"\);\n",
            re.MULTILINE)
        text, count = pattern.subn("", text)
        if count:
            removed.append(f"{field} (FindControl)")
    return text, removed


def strip_method_bodies(text: str, name: str) -> tuple[str, bool]:
    """Removes a whole method (signature through its closing brace)."""
    match = re.search(rf"^\s*(?:private|public|internal)\s+[\w<>?\[\], ]+\s+{name}\s*\(",
                      text, re.MULTILINE)
    if not match:
        return text, False
    start = match.start()
    # Walk forward to the matching closing brace of the method body.
    index = text.index("{", match.end())
    depth = 0
    while index < len(text):
        if text[index] == "{":
            depth += 1
        elif text[index] == "}":
            depth -= 1
            if depth == 0:
                break
        index += 1
    end = index + 1
    while end < len(text) and text[end] == "\n":
        end += 1
    return text[:start] + text[end:], True


def main() -> int:
    text = CS.read_text(encoding="utf-8")
    before = len(text)

    text, removed_fields = strip_field_declarations(text)
    text, removed_find = strip_find_controls(text)

    removed_methods = []
    for method in DEAD_METHODS:
        text, ok = strip_method_bodies(text, method)
        if ok:
            removed_methods.append(method)

    # Any remaining reference to a dead field is now a compile error; report
    # them so the caller knows what still needs a human decision.
    leftover = []
    for field in DEAD_FIELDS:
        hits = len(re.findall(rf"\b{re.escape(field)}\b", text))
        if hits:
            leftover.append(f"{field}: {hits} reference(s)")

    CS.write_text(text, encoding="utf-8")
    print(f"removed {before - len(text)} characters")
    for item in removed_fields + removed_find:
        print(f"  - {item}")
    for item in removed_methods:
        print(f"  - method {item}")
    if leftover:
        print("still referenced:")
        for item in leftover:
            print(f"  ! {item}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
