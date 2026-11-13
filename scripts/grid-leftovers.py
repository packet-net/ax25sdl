#!/usr/bin/env python3
"""List the grid cells a run did not finish, for TlaCheck --grid-cells-from. Stdlib only.

Usage: python3 scripts/grid-leftovers.py DIR [--cap N]

Reads every DIR/*.jsonl that TlaCheck --results wrote and prints a leftover
when its last record for a cell is a timeout (timedOut), a failure (done
false), or capped: the explorer's search cut short by its state cap or depth
limit (capped set, or explorerStates at the cap, which also catches a
violation the explorer found past the cap, recorded before
packet-net/ax25sdl#112's cap fix as a disagreement on the state count).
Cross-check cells go to DIR/leftovers.txt and lockstep cells to
DIR/leftovers-lockstep.txt, grouped by results file, each line a full cell
name and a comment saying why. Cells the run never reached are not listed:
resume those from their own results file. --cap is the explorer's state cap
the run used (default 1200000, the data-phase grids' cap).
"""
import argparse
import glob
import json
import os
import sys


def reason(r, cap):
    if not r.get("done", False):
        return "failed"
    if r.get("mode") == "lockstep":
        return "timed out" if r.get("timedOut") else None
    capped = r.get("capped") or ("states" if r.get("explorerStates", 0) >= cap else "")
    why = []
    if r.get("timedOut"):
        why.append("TLC timed out")
    if capped:
        why.append(f"explorer {'state cap' if capped == 'states' else 'depth limit'} ({r.get('explorerKind', '')})")
    return ", ".join(why) or None


def main():
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("dir")
    ap.add_argument("--cap", type=int, default=1_200_000)
    a = ap.parse_args()
    out = {"cross": [], "lockstep": []}
    for path in sorted(glob.glob(os.path.join(a.dir, "*.jsonl"))):
        last = {}
        with open(path, encoding="utf-8") as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                try:
                    r = json.loads(line)
                except json.JSONDecodeError:
                    continue
                last[r["cell"]] = r
        rows = {"cross": [], "lockstep": []}
        for cell, r in sorted(last.items(), key=lambda kv: kv[1].get("index", 0)):
            why = reason(r, a.cap)
            if why:
                rows["lockstep" if r.get("mode") == "lockstep" else "cross"].append(f"{cell}  # {why}")
        for mode, lines in rows.items():
            if lines:
                out[mode].append(f"# {os.path.basename(path)}: {len(lines)}")
                out[mode].extend(lines)
    for mode, name in (("cross", "leftovers.txt"), ("lockstep", "leftovers-lockstep.txt")):
        cells = [l for l in out[mode] if not l.startswith("#")]
        target = os.path.join(a.dir, name)
        if not cells:
            if os.path.exists(target):
                os.remove(target)
            continue
        with open(target, "w", encoding="utf-8") as f:
            f.write(f"# Unfinished {mode} grid cells in {os.path.basename(os.path.normpath(a.dir))}, written by scripts/grid-leftovers.py: {len(cells)}.\n")
            f.write("\n".join(out[mode]) + "\n")
        print(f"{target}: {len(cells)}", file=sys.stderr)


if __name__ == "__main__":
    main()
