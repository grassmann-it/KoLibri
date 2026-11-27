#!/usr/bin/env python3
"""Merge KoLmafia's choice spoilers and default options into src/KoLibri/Data/choices.json.

KoLmafia has no choice data file. The spoilers live as Java code in
`src/main/java/net/sourceforge/kolmafia/session/ChoiceAdventures.java`
(`new ChoiceAdventure(...)`, `new ChoiceSpoiler(...)`, `new ChoiceCost(...)`), and
the default option per choice is the user preference `choiceAdventureN` in
`src/main/resources/data/defaults.txt`. This script reads both as TEXT (it never
compiles or runs anything from the KoLmafia tree) and writes KoLibri's own
choices.json format, version 2.

Source: the vendored checkout `external/kolmafia` (or `KOLIBRI_MAFIA_ROOT`).
Without one, clone the pinned version below into the scratchpad and point
KOLIBRI_MAFIA_ROOT at it; the files are only read, never executed:

    git clone https://github.com/kolmafia/kolmafia.git <scratch>/kolmafia
    git -C <scratch>/kolmafia checkout 95ebac8dce63f6532b4b10b339c2f38d6d2dad6f
    KOLIBRI_MAFIA_ROOT=<scratch>/kolmafia python3 -I scripts/import-kolmafia-choices.py

Merge rules:
- Entries whose `confidence` is "live" (hand-verified, e.g. choice 1060) are kept
  untouched; a KoLmafia entry with the same id is dropped. Live always wins.
- Every KoLmafia option gets flags derived from KoLmafia's own data:
  `costsMeat`/`costsItem` (ChoiceCost or spoiler text such as "pay 500 meat"),
  `startsFight` (spoiler mentions fight/combat/battle), `hurts` ("get Beaten Up"), `skipsTurn` ("skip adventure",
  "leave", ...). `safe` is false whenever a cost, fight or hurt flag is set or the option
  has no spoiler text. The resolver only auto-picks a KoLmafia default whose option
  is `safe: true`; everything else asks with the spoilers in the reason.
- Only choices KoLmafia lists in ChoiceAdventures.java are imported (adventure
  choices with spoilers). Dynamic spoilers (computed at runtime) are not imported.

KoLmafia is BSD-3-Clause licensed; see src/KoLibri/Data/README.md.
"""
from __future__ import annotations

import json
import os
import re
import subprocess
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
PINNED_URL = "https://github.com/kolmafia/kolmafia.git"
PINNED_COMMIT = "95ebac8dce63f6532b4b10b339c2f38d6d2dad6f"

MAFIA_ROOT = Path(os.environ.get("KOLIBRI_MAFIA_ROOT", REPO_ROOT / "external" / "kolmafia"))
JAVA = MAFIA_ROOT / "src/main/java/net/sourceforge/kolmafia/session/ChoiceAdventures.java"
DEFAULTS = MAFIA_ROOT / "src/main/resources/data/defaults.txt"
OUT = Path(os.environ.get("KOLIBRI_CHOICES_OUT", REPO_ROOT / "src/KoLibri/Data/choices.json"))

CALL_RE = re.compile(r"new (ChoiceAdventure|ChoiceSpoiler|ChoiceCost)\(")
CONST_RE = re.compile(r'static final ChoiceOption (\w+)\s*=\s*new ChoiceOption\("((?:[^"\\]|\\.)*)"\)')

FIGHT_RE = re.compile(r"(?<![/-])(?<!/ )(?<!per )(?<!after )(?<!non-)\b(fight|combat|battle|duel|wrestle|ambush)\b(?!\s+(item|frequency))", re.I)
HURT_RE = re.compile(r"\bbeaten up\b|\blose (\d+ )?(hp|health)\b|\btake damage\b", re.I)
MEAT_RE = re.compile(r"\b(pay|spend|sink|lose|costs?|bribe|donate|tip)\b[^,;]*\bmeat\b|\b\d[\d,]*\s*meat\s+to\b", re.I)
ITEM_RE = re.compile(r"\b(trade|give|hand over|sacrifice|lose|use up|spend|costs?)\b\s+(an?|your|the|one|\d+)\b", re.I)
SKIP_RE = re.compile(r"^(skip( adventure)?|leave|walk away|nothing|exit|back away|go back)\b", re.I)


def read_call(text: str, start: int) -> tuple[str, int]:
    """Return the argument text of the call whose '(' is at text[start-1], and the end index."""
    depth, i, n = 1, start, len(text)
    while i < n:
        c = text[i]
        if c == '"':
            i += 1
            while text[i] != '"':
                i += 2 if text[i] == "\\" else 1
        elif c == "/" and text[i + 1] == "/":
            i = text.index("\n", i)
        elif c == "(":
            depth += 1
        elif c == ")":
            depth -= 1
            if depth == 0:
                return text[start:i], i + 1
        i += 1
    raise ValueError("unbalanced call at %d" % start)


def split_args(s: str) -> list[str]:
    """Split on top-level commas, skipping strings, comments and nested brackets."""
    out, depth, cur, i = [], 0, [], 0
    while i < len(s):
        c = s[i]
        if c == '"':
            j = i + 1
            while s[j] != '"':
                j += 2 if s[j] == "\\" else 1
            cur.append(s[i:j + 1])
            i = j + 1
            continue
        if c == "/" and s[i:i + 2] == "//":
            i = s.index("\n", i) if "\n" in s[i:] else len(s)
            continue
        if c in "([{":
            depth += 1
        elif c in ")]}":
            depth -= 1
        if c == "," and depth == 0:
            out.append("".join(cur).strip())
            cur = []
        else:
            cur.append(c)
        i += 1
    if "".join(cur).strip():
        out.append("".join(cur).strip())
    return out


def string_literal(arg: str) -> str | None:
    m = re.fullmatch(r'"((?:[^"\\]|\\.)*)"', arg.strip())
    return bytes(m.group(1), "utf-8").decode("unicode_escape") if m else None


def preceding_comment(text: str, pos: int) -> str | None:
    """The `// Title` comment block directly above a call (KoLmafia names each choice this way).

    The first line of the block is the title; later lines are notes ("Dynamically calculate ...").
    """
    block = []
    for line in reversed(text[:pos].rstrip().splitlines()):
        s = line.strip()
        if not s.startswith("//"):
            break
        block.append(s[2:].strip())
    for title in reversed(block):
        title = re.sub(r"^Choice \d+ is (an? |the )?(?=\S)", lambda m: m.group(1).capitalize() if m.group(1) else "", title)
        if title and not title.lower().startswith(("option", "choice ")):
            return title
    return None


def parse_option(arg: str, consts: dict[str, str]) -> tuple[str | None, int, list[str]]:
    """(spoiler text or None, explicit decision number or 0, item names)."""
    arg = arg.strip()
    if arg in consts:
        return consts[arg], 0, []
    if arg == "null":
        return None, 0, []
    m = re.fullmatch(r"new ChoiceOption\((.*)\)", arg, re.S)
    if not m:
        return None, 0, []
    parts = split_args(m.group(1))
    if not parts:
        return None, 0, []
    name = string_literal(parts[0])
    if name is None and (c := re.fullmatch(r"AscensionClass\.(\w+)\.toString\(\)", parts[0])):
        name = c.group(1).replace("_", " ").title()
    decision, items = 0, []
    for p in parts[1:]:
        if re.fullmatch(r"\d+", p):
            decision = int(p)
        elif (s := string_literal(p)) is not None:
            items.append(s)
    return name, decision, items


def parse_cost(arg: str) -> tuple[int, str] | None:
    m = re.fullmatch(r"new Cost\(\s*(\d+)\s*,(.*)\)", arg.strip(), re.S)
    if not m:
        return None
    body = m.group(2)
    if "AdventureResult.MEAT" in body:
        kind = "meat"
    elif "ItemPool.get" in body:
        kind = "item"
    else:
        kind = "other"  # MP, HP, ... still a cost
    return int(m.group(1)), kind


def flags_for(spoiler: str | None, cost: str | None) -> dict:
    f: dict = {}
    text = spoiler or ""
    if cost == "meat" or MEAT_RE.search(text):
        f["costsMeat"] = True
    if cost in ("item", "other") or (ITEM_RE.search(text) and not f.get("costsMeat")):
        f["costsItem"] = True
    if FIGHT_RE.search(text):
        f["startsFight"] = True
    if HURT_RE.search(text):
        f["hurts"] = True
    if SKIP_RE.search(text.strip()):
        f["skipsTurn"] = True
    f["safe"] = bool(text.strip()) and not any(f.get(k) for k in ("costsMeat", "costsItem", "startsFight", "hurts"))
    return f


def parse_java(text: str) -> tuple[dict[int, dict], dict[int, dict[int, str]]]:
    consts = {m.group(1): m.group(2) for m in CONST_RE.finditer(text)}
    choices: dict[int, dict] = {}
    costs: dict[int, dict[int, str]] = {}
    for m in CALL_RE.finditer(text):
        kind = m.group(1)
        body, _ = read_call(text, m.end())
        args = split_args(body)
        if not args or not re.fullmatch(r"\d+", args[0]):
            continue  # a constructor definition or a computed id
        cid = int(args[0])
        if kind == "ChoiceCost":
            for a in args[1:]:
                if (c := parse_cost(a)) is not None:
                    costs.setdefault(cid, {})[c[0]] = c[1]
            continue
        rest = args[1:]
        strings = []
        while rest and (s := string_literal(rest[0])) is not None:
            strings.append(s)
            rest = rest[1:]
        if rest and re.fullmatch(r"\d+", rest[0]):  # ChoiceAdventure(id, zone, name, ordering, ...)
            rest = rest[1:]
        if kind == "ChoiceAdventure":
            zone, location = (strings + [None, None])[:2]
        else:
            zone, location = (strings[0], strings[1]) if len(strings) >= 2 else (None, strings[0] if strings else None)
        options: dict[int, dict] = {}
        for pos, a in enumerate(rest, start=1):
            spoiler, decision, items = parse_option(a, consts)
            if spoiler is None and a.strip() == "null":
                continue
            n = decision or pos
            opt: dict = {}
            if spoiler is not None:
                opt["label"] = spoiler
            if items:
                opt["items"] = items
            options[n] = opt
        if cid in choices:
            continue  # KoLmafia warns about duplicates; first one wins as in its map
        choices[cid] = {
            "title": preceding_comment(text, m.start()),
            "zone": zone if zone not in (None, "Unsorted") else None,
            "location": location,
            "options": options,
        }
    return choices, costs


def parse_defaults(text: str) -> dict[int, int]:
    out = {}
    for line in text.splitlines():
        p = line.split("\t")
        if len(p) >= 3 and p[0] == "user" and (m := re.fullmatch(r"choiceAdventure(\d+)", p[1])):
            if re.fullmatch(r"\d+", p[2].strip()) and int(p[2]) > 0:
                out[int(m.group(1))] = int(p[2])
    return out


def mafia_commit() -> str:
    try:
        return subprocess.run(["git", "-C", str(MAFIA_ROOT), "rev-parse", "HEAD"],
                              capture_output=True, text=True, check=True).stdout.strip()
    except (OSError, subprocess.CalledProcessError):
        return PINNED_COMMIT


def build_entries(choices, costs, defaults, source: str) -> list[dict]:
    entries = []
    for cid in sorted(choices):
        c = choices[cid]
        options = {}
        for n, opt in sorted(c["options"].items()):
            o = dict(opt)
            o.update(flags_for(opt.get("label"), costs.get(cid, {}).get(n)))
            options[str(n)] = o
        entry: dict = {"id": cid}
        if c["title"]:
            entry["name"] = c["title"]
        if c["location"]:
            entry["location"] = c["location"]
        if c["zone"]:
            entry["zone"] = c["zone"]
        d = defaults.get(cid)
        # A default KoLmafia handles specially (beyond the listed options) is not taken over.
        if d is not None and str(d) in options:
            entry["default"] = d
        entry["options"] = options
        entry["source"] = source
        entry["confidence"] = "kolmafia-default"
        entries.append(entry)
    return entries


def main() -> int:
    if not JAVA.is_file() or not DEFAULTS.is_file():
        print(f"KoLmafia sources not found under {MAFIA_ROOT}.\n"
              f"Clone {PINNED_URL} at {PINNED_COMMIT} and set KOLIBRI_MAFIA_ROOT.", file=sys.stderr)
        return 1
    commit = mafia_commit()
    source = f"kolmafia {commit[:12]}"
    choices, costs = parse_java(JAVA.read_text(encoding="utf-8"))
    defaults = parse_defaults(DEFAULTS.read_text(encoding="utf-8"))

    current = json.loads(OUT.read_text(encoding="utf-8")) if OUT.is_file() else {"choices": []}
    live = [e for e in current.get("choices", []) if e.get("confidence", "live") == "live"]
    for e in live:
        e["confidence"] = "live"
    live_ids = {e["id"] for e in live}
    imported = [e for e in build_entries(choices, costs, defaults, source) if e["id"] not in live_ids]

    doc = {
        "version": 2,
        "_turtle": current.get("_turtle"),
        "_kolmafia": (f"Optionstexte und Defaults aus KoLmafia {commit} (ChoiceAdventures.java, defaults.txt; "
                      f"BSD-3-Clause, siehe Data/README.md). Erzeugt von scripts/import-kolmafia-choices.py. "
                      f"Einträge mit confidence 'live' pflegt man von Hand; sie schlagen KoLmafia."),
        "choices": sorted(live + imported, key=lambda e: e["id"]),
    }
    if doc["_turtle"] is None:
        del doc["_turtle"]
    # One entry per line keeps the diff readable and the file small.
    lines = [json.dumps(e, ensure_ascii=False, separators=(",", ":")) for e in doc["choices"]]
    head = {k: v for k, v in doc.items() if k != "choices"}
    body = json.dumps(head, ensure_ascii=False, indent=2)[:-2]
    OUT.write_text(body + ',\n  "choices": [\n    ' + ",\n    ".join(lines) + "\n  ]\n}\n", encoding="utf-8")

    with_default = sum(1 for e in imported if "default" in e)
    safe_default = sum(1 for e in imported if "default" in e and e["options"][str(e["default"])]["safe"])
    print(f"{len(imported)} KoLmafia choices ({with_default} with default, {safe_default} safe), "
          f"{len(live)} live kept, {OUT.stat().st_size // 1024} KB -> {OUT}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
