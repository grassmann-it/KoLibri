#!/usr/bin/env python3
"""Convert KoLmafia data files (under external/kolmafia/src/main/resources/data)
into structured JSON Lines files under external/kolmafia-data/.

This script is the bridge between Mafia's tab/whitespace files and the
typed records the KolArbitrage.Core data layer expects. It is meant to
be re-run only when upstream KoLmafia changes; the JSONL output is
checked into the repository so the rest of the build runs offline.
"""
from __future__ import annotations

import json
import os
import re
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
MAFIA_DATA = REPO_ROOT / "external" / "kolmafia" / "src" / "main" / "resources" / "data"
OUT = REPO_ROOT / "external" / "kolmafia-data"

USE_TOKENS = {
    "weapon", "offhand", "hat", "shirt", "pants", "container", "accessory",
    "familiar", "food", "drink", "spleen", "potion", "avatar", "usable",
    "multiple", "reusable", "message", "grow", "pokepill", "sixgun",
    "sticker", "card", "folder", "bootspur", "bootskin", "zap", "sphere",
    "guardian", "food helper", "drink helper", "none",
}
SECONDARY_TOKENS = {
    "combat", "combat reusable", "single", "solo", "curse", "bounty",
    "package", "candy", "candy1", "candy2", "chocolate", "fancy", "paste",
    "smith", "cook", "mix", "matchable", "perishable", "usable", "multiple",
    "reusable",
}
ACCESS_TOKENS = {"q", "g", "t", "d"}


def parse_items(line: str) -> dict | None:
    """Tab-separated: id\tname\tdescid\timage\tuseflags\taccessflags\tautosell\tplural

    KoLmafia writes short rows for items that have no image/flags yet
    (for example "10881<TAB>11-leaf clover<TAB>477842994"). Those rows are
    valid items and must not be dropped, so the parser fills the missing
    columns with empty defaults instead of rejecting the row.
    """
    parts = line.split("\t")
    if len(parts) < 2 or not parts[0].isdigit():
        return None
    item_id = int(parts[0])
    name = parts[1]
    desc_id = parts[2] if len(parts) > 2 else ""
    image = parts[3] if len(parts) > 3 else ""
    use_field = parts[4] if len(parts) > 4 else ""
    access_field = parts[5] if len(parts) > 5 else ""
    autosell = parts[6] if len(parts) > 6 else ""
    plural = parts[7] if len(parts) > 7 else ""
    use_flags = [t for t in use_field.split(",") if t.strip() and (t.strip() in USE_TOKENS or t.strip() in SECONDARY_TOKENS)]
    secondary = [t for t in use_flags if t in SECONDARY_TOKENS]
    primary = [t for t in use_flags if t in USE_TOKENS and t not in SECONDARY_TOKENS]
    access_flags = [t for t in access_field if t in ACCESS_TOKENS]
    return {
        "id": str(item_id),
        "name": name,
        "descId": desc_id,
        "image": image,
        "primaryUse": primary,
        "secondaryUse": secondary,
        "accessFlags": access_flags,
        "autosell": int(autosell) if autosell.isdigit() else 0,
        "plural": plural,
    }


def parse_zones(line: str) -> dict | None:
    """Tab-separated: zoneId\tparent\tdisplayName\t[accessItem]"""
    parts = line.split("\t")
    if len(parts) < 3:
        return None
    return {
        "zoneId": parts[0],
        "parent": parts[1] if len(parts) > 1 else "",
        "displayName": parts[2] if len(parts) > 2 else "",
        "accessItem": parts[3] if len(parts) > 3 else "",
    }


def parse_adventures(line: str) -> dict | None:
    """Parse zone, route, attributes, location name and optional goals."""
    parts = line.split("\t")
    if len(parts) < 4:
        return None
    zone, route, attributes, name = parts[:4]
    adventure = re.fullmatch(r"adventure=(\d+)", route)
    return {
        "id": f"{route}:{name}",
        "name": name,
        "sniffProperty": route,
        "sniff": int(adventure.group(1)) if adventure else 0,
        "zone": zone,
        "desc": attributes,
        "type": parts[4] if len(parts) > 4 else "",
    }


def parse_classskills(line: str) -> dict | None:
    """Tab-separated: id\tname\timage\ttags\tmp\tduration\t[attributes]"""
    parts = line.split("\t")
    if len(parts) < 6 or not parts[0].isdigit():
        return None
    return {
        "id": int(parts[0]),
        "name": parts[1],
        "image": parts[2] if len(parts) > 2 else "",
        "tags": parts[3].split(",") if len(parts) > 3 else [],
        "mpCost": int(parts[4]) if parts[4].lstrip("-").isdigit() else 0,
        "duration": parts[5] if len(parts) > 5 else "",
        "attributes": parts[6] if len(parts) > 6 else "",
    }


def parse_statuseffects(line: str) -> dict | None:
    """Tab-separated: id\tname\timage\tdescid\tquality\tattributes\taction"""
    parts = line.split("\t")
    if len(parts) < 4 or not parts[0].isdigit():
        return None
    return {
        "id": int(parts[0]),
        "name": parts[1],
        "image": parts[2],
        "descId": parts[3] if len(parts) > 3 else "",
        "quality": parts[4] if len(parts) > 4 else "",
        "attributes": parts[5].split(",") if len(parts) > 5 else [],
        "defaultAction": parts[6] if len(parts) > 6 else "",
    }


MONSTER_LINE_TWO = re.compile(
    r"^(?P<name>.+?)\t(?P<id>\d+)\t(?P<image>\S+)\t(?P<params>.+?)(?:\t(?P<drops>.+))?$"
)
MONSTER_LINE_ONE = re.compile(
    r"^(?P<numeric_id>\d+)\s+(?P<name>.+?)\s+(?P<id>\d+)\s+(?P<image>\S+)\s+(?P<params>.+?)(?:\s+(?P<drops>.+))?$"
)
PARAM_TOKEN = re.compile(r"([A-Za-z][A-Za-z]*):\s*(\S+)")
DROP_LINE = re.compile(r"^(?P<name>.+?)\s*\((?P<flags>[a-zA-Z]*)(?P<rate>\d+)\)\s*$")
QUOTED_RE = re.compile(r'"([^"]*)"')


def parse_monsters(line: str) -> dict | None:
    """Parse a tab-separated monster row.

    The very first monster in the file uses a legacy shape:
    ``<numeric-id> <Name> <id> <image> ...``. Every subsequent monster uses
    the canonical ``Name <tab> id <tab> image <tab> params <tab> drops`` shape.
    Both shapes are accepted and the legacyId is preserved when present.
    """
    line = line.strip()
    if not line:
        return None
    match = MONSTER_LINE_TWO.match(line) or MONSTER_LINE_ONE.match(line)
    if not match:
        return None
    params = match.group("params")
    parsed = {}
    cleaned = QUOTED_RE.sub(lambda q: q.group(1).replace(" ", "_"), params)
    for key, value in PARAM_TOKEN.findall(cleaned):
        if key not in parsed:
            parsed[key] = value.replace("_", " ")
    drops_blob = match.group("drops")
    drops = []
    if drops_blob:
        for drop in drops_blob.split("\t"):
            d = DROP_LINE.match(drop.strip())
            if d:
                drops.append({
                    "name": d.group("name").strip(),
                    "rate": int(d.group("rate")),
                    "flags": d.group("flags"),
                })
            else:
                drops.append({"name": drop.strip(), "rate": 0, "flags": ""})
    record = {
        "id": match.group("id"),
        "name": match.group("name"),
        "image": match.group("image"),
        "parameters": parsed,
        "drops": drops,
    }
    if "numeric_id" in match.groupdict():
        record["legacyId"] = match.group("numeric_id")
    return record


def parse_modifiers(line: str) -> dict | None:
    """Tab-separated: <group/header>\t<description with embedded modifiers>."""
    parts = line.split("\t")
    if len(parts) < 2:
        return None
    return {
        "header": parts[0].strip(),
        "description": parts[1].strip(),
        "extras": [p for p in parts[2:] if p.strip()],
    }


def parse_concoctions(line: str) -> dict | None:
    """Tab-separated: name\tmixMethod\tdescription."""
    parts = line.split("\t")
    if len(parts) < 3:
        return None
    return {
        "name": parts[0],
        "mixMethod": parts[1],
        "description": parts[2],
        "extras": [p for p in parts[3:] if p],
    }


def parse_monsterparts(line: str) -> dict | None:
    """Tab-separated: monsterName\tpartName\tpartId"""
    parts = line.split("\t")
    if len(parts) < 3 or not parts[1]:
        return None
    return {
        "monster": parts[0],
        "part": parts[1],
        "partId": int(parts[2]) if parts[2].isdigit() else 0,
    }


def parse_restores(line: str) -> dict | None:
    """Tab-separated: name\teffectType\tstat\tdelta"""
    parts = line.split("\t")
    if len(parts) < 4:
        return None
    return {
        "name": parts[0],
        "effect": parts[1],
        "stat": parts[2],
        "delta": parts[3],
    }


def parse_familiar(line: str) -> dict | None:
    fields = line.split("\t")
    if len(fields) < 2 or not fields[0].isdigit():
        return None
    return {"id": fields[0], "name": fields[1]}


PARSERS = {
    "familiars.txt": (parse_familiar, "familiars.jsonl"),
    "items.txt": (parse_items, "items.jsonl"),
    "zonelist.txt": (parse_zones, "zones.jsonl"),
    "adventures.txt": (parse_adventures, "adventures.jsonl"),
    "classskills.txt": (parse_classskills, "skills.jsonl"),
    "statuseffects.txt": (parse_statuseffects, "effects.jsonl"),
    "monsters.txt": (parse_monsters, "monsters.jsonl"),
    "modifiers.txt": (parse_modifiers, "modifiers.jsonl"),
    "concoctions.txt": (parse_concoctions, "concoctions.jsonl"),
    "monsterparts.txt": (parse_monsterparts, "monsterparts.jsonl"),
    "restores.txt": (parse_restores, "restores.jsonl"),
}


def strip_comments(path: Path):
    """Yield non-comment, non-blank lines, skipping the leading version
    row (a bare integer in line 1 followed by a #-comment in line 2)."""
    with path.open("r", encoding="utf-8", errors="replace") as fh:
        lines = fh.readlines()
    skip_first = False
    if lines and lines[0].strip().isdigit() and len(lines) > 1 and lines[1].lstrip().startswith("#"):
        skip_first = True
    for line in lines[1 if skip_first else 0:]:
        line = line.rstrip("\n")
        if not line.strip():
            continue
        if line.lstrip().startswith("#"):
            continue
        yield line


def load_area_monsters(path: Path) -> dict[str, list[str]]:
    areas = {}
    for line in strip_comments(path):
        fields = line.split("\t")
        if len(fields) < 2:
            continue
        areas[fields[0]] = [entry.partition(":")[0].strip()
                            for entry in fields[2:] if entry.strip()]
    return areas


def main() -> int:
    if not MAFIA_DATA.exists():
        print(f"KoLmafia data directory not found: {MAFIA_DATA}", file=sys.stderr)
        return 1
    OUT.mkdir(parents=True, exist_ok=True)
    summary = []
    area_monsters = load_area_monsters(MAFIA_DATA / "combats.txt")
    for filename, (parser, outname) in PARSERS.items():
        src = MAFIA_DATA / filename
        dst = OUT / outname
        if not src.exists():
            print(f"  skip {filename} (not found)", file=sys.stderr)
            continue
        count = 0
        with dst.open("w", encoding="utf-8") as out:
            for raw in strip_comments(src):
                record = parser(raw)
                if record is None:
                    continue
                if filename == "adventures.txt":
                    record["monsters"] = area_monsters.get(record["name"], [])
                out.write(json.dumps(record, ensure_ascii=False))
                out.write("\n")
                count += 1
        summary.append((outname, count))
    for name, count in summary:
        print(f"  {count:>6}  {name}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
