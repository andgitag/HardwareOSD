"""Builds the HardwareOSD tile-design resources from the saved CapFrameX 1.9.2 design library.

Input : CapFrameX portable profiles.json (46 MB original, 9.5 MB without images? no: ~9.5 MB with
        embedded PNG data URIs). Only the design documents are read; no CapFrameX binary is used.
Output: src/HardwareOSD.Core/TileOsd/Designs/<id>.json and catalog.json.

The saved documents carry authoring `editor` metadata. This tool lifts the absolute tile frames
(x/y/frameWidth/frameHeight) onto the nodes and removes the rest of the authoring block, so the
HardwareOSD renderer consumes a clean, self-contained scene with the geometry the CapFrameX editor
created. CapFrameX itself strips editor metadata and lays flow panels out at runtime; HardwareOSD
uses the saved frames because they are the measured layout contract in the research.
"""
from __future__ import annotations

import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PROFILES = Path(sys.argv[1]) if len(sys.argv) > 1 else (
    ROOT / "reference" / "CapFrameX-1.9.2-portable" / "Portable" / "Config" / "OverlayDesigns" / "profiles.json"
)
OUT = ROOT / "src" / "HardwareOSD.Core" / "TileOsd" / "Designs"

FAMILIES = [
    ("Everyday", {"Benchmark", "Compact", "Minimal", "OLED Minimal", "Frame Pacing",
                  "Performance Strip", "GPU Dashboard", "Memory Watch"}),
    ("Vertical", {"Vertical Thread Strip", "Vertical Clock Deck", "Vertical Thermal Panel"}),
    ("Hybrid", {"Hybrid Benchmark", "Hybrid Compact", "Hybrid Thread Deck"}),
    ("Core Types", {"Core Types Overview", "Core Types Compact", "Core Types Thermal"}),
    ("Turquoise", {"Turquoise Reference", "Turquoise Compact", "Turquoise Wide"}),
    ("Anime", {"CHERRY BLOSSOM - Anime", "NEON NIGHT - Anime", "LUNAR - Anime"}),
    ("Slick", {"Slick Overlay - Compact 2x2", "Slick Overlay - Single Line",
               "Slick Overlay - Vertical Panel", "Slick Overlay - Wide 3 Rows"}),
]

FRAME_FIELDS = ("x", "y", "frameWidth", "frameHeight")


def slug(name: str) -> str:
    text = name.lower()
    text = text.replace("&", "and").replace("+", "-plus-").replace("×", "x")
    text = re.sub(r"[^a-z0-9]+", "-", text)
    return "cfx-" + text.strip("-")


def family_for(name: str) -> str:
    for family, names in FAMILIES:
        if name in names:
            return family
    return "Hardware"


def walk(node: dict, stats: dict) -> dict:
    if not isinstance(node, dict):
        return node
    editor = node.get("editor") or {}
    for field in FRAME_FIELDS:
        if node.get(field) is None and editor.get(field) is not None:
            node[field] = editor[field]
    node.pop("editor", None)
    node.pop("locked", None)
    stats["nodes"] += 1
    stats[node.get("type", "?")] = stats.get(node.get("type", "?"), 0) + 1
    children = node.get("children")
    if children:
        node["children"] = [walk(child, stats) for child in children]
    return node


def has_images(node: dict) -> int:
    count = 1 if node.get("type") == "image" else 0
    for child in node.get("children") or []:
        count += has_images(child)
    return count


def main() -> int:
    if not PROFILES.exists():
        print(f"missing {PROFILES}", file=sys.stderr)
        return 2
    doc = json.loads(PROFILES.read_text(encoding="utf-8-sig"))
    OUT.mkdir(parents=True, exist_ok=True)
    for old in OUT.glob("*.json"):
        old.unlink()

    catalog = []
    for profile in doc["Profiles"]:
        design = json.loads(profile["DesignJson"])
        design.pop("editor", None)
        stats: dict = {"nodes": 0}
        design["root"] = walk(design["root"], stats)
        name = design.get("name") or profile["Name"]
        design["name"] = name
        design["id"] = slug(name)
        design["family"] = family_for(name)
        design.setdefault("version", 1)
        root = design["root"]
        width = root.get("width") or root.get("frameWidth") or 0
        height = root.get("height") or root.get("frameHeight") or 0
        path = OUT / f"{design['id']}.json"
        path.write_text(json.dumps(design, ensure_ascii=False, separators=(",", ":")),
                        encoding="utf-8")
        theme = design.get("theme") or {}
        catalog.append({
            "id": design["id"],
            "name": name,
            "family": design["family"],
            "file": path.name,
            "width": width,
            "height": height,
            "fontFamily": theme.get("fontFamily", "Segoe UI"),
            "fontSize": theme.get("fontSize", 14),
            "titleSize": theme.get("titleSize", 12),
            "panel": theme.get("panel", "#00000000"),
            "accent": theme.get("accent", "#FFFFFFFF"),
            "text": theme.get("text", "#FFFFFFFF"),
            "topTiles": len(root.get("children") or []),
            "nodes": stats["nodes"],
            "images": has_images(root),
            "metrics": stats.get("metric", 0),
            "bars": stats.get("bar", 0),
            "charts": stats.get("chart", 0),
            "gauges": stats.get("gauge", 0),
            "heatmaps": stats.get("heatmap", 0),
            "texts": stats.get("text", 0),
        })

    catalog.sort(key=lambda item: item["name"].lower())
    (OUT.parent / "catalog.json").write_text(
        json.dumps({"version": 1, "designs": catalog}, ensure_ascii=False, indent=1),
        encoding="utf-8")
    total = sum(p.stat().st_size for p in OUT.glob("*.json"))
    print(f"wrote {len(catalog)} designs, {total / 1024 / 1024:.2f} MB")
    print(f"images {sum(item['images'] for item in catalog)}, "
          f"metrics {sum(item['metrics'] for item in catalog)}, "
          f"bars {sum(item['bars'] for item in catalog)}, "
          f"charts {sum(item['charts'] for item in catalog)}, "
          f"gauges {sum(item['gauges'] for item in catalog)}, "
          f"heatmaps {sum(item['heatmaps'] for item in catalog)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
