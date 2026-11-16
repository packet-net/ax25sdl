# Grid runs of 14 to 16 November 2026: raw results

Raw output of the runs that finished the 13 November leftovers and the uncapped local-busy and inj-srej grids. These are the records as TlaCheck wrote them, copied off the machines before they went away. They are not yet merged per grid; that is the next step, and it replaces this folder with a tidy one next to `docs/grid-runs/2026-11-13`.

## Where they ran

- `spot-r6i/` and `spot-r6i-coverage/`: an r6i.32xlarge spot instance, 14 Nov 13:06 to 16 Nov 12:13 UTC, when AWS reclaimed it. Results copied at 11:46 on 16 Nov; nothing it held finished between then and the reclaim.
- `spot-r7a/` and `spot-r7a-coverage/`: an r7a.12xlarge spot instance from 15 Nov 00:10, running to 23:35 on 16 Nov. This copy was taken on 16 Nov at about 20:00 and will be refreshed at the end.
- A replacement r7a.24xlarge ran for 3.7 hours on 16 Nov before it too was reclaimed. Nothing it ran finished, so it has no folder.

## How to read the files

- `*.jsonl`: one record per cell per run, as `--results` writes them. A cell can appear in several files when it was rerun or run twice by mistake; prefer a finished record that agrees, and ignore `error` records from duplicate runs that were stopped on purpose.
- `*.md`: the same results as TlaCheck prints them.
- `coverage-only*.jsonl`: `--coverage-only` reruns (#130) for cells whose Coverage pass was cut (below). Fold them in with `--merge-coverage`.
- `coverage-cut-*.log`: every cell whose Coverage pass was stopped. The Coverage pass of the build in use then wrote its whole state dump to disk, 50 to 70 GB per large cell, and filled the disks. Stopping it keeps the verdict from the first three passes; only the list of rows fired is partial until a coverage-only rerun replaces it.
- `open-local-busy-cells.txt`: the 17 local-busy cells with no verdict yet.

## State at about 20:00 UTC on 16 November

- inj-srej: 380 of 384 agree. The other 4 are the srej-command cells below.
- local-busy: 367 of 384 finished, all agree (31 of them explorer-capped, compared on verdict kind). The 17 hardest are open; see below.
- Leftovers of 13 November: 14 of the 19 non-srej-command cells agree; the other 5 are local-busy cells counted above. Of the 4 srej-command cells, 3 agree fully once their coverage-only arms are folded in (two first showed "transitions fired 11 vs 9", which was only the cut Coverage pass); the 4th was still running.
- No DISAGREE, no capped verdict difference and no finding in any finished cell.

## What remains, and where it is tracked

- The 17 open local-busy cells, the 4th srej-command cell and the unfinished coverage-only reruns: these need passes of a day or more each, and two spot boxes were reclaimed mid-run. They now run on local machines (proxmox0's tla-grid container and a WSL machine) with checkpoint and resume (#131, #132) and live progress reporting. Tracked in #112.
- Merging these raw files per grid with the 13 November records: tracked in #112.
