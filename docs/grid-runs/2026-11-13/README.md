# Grid runs, 13 November 2026

Results of every grid run on 13 November 2026 at ax25sdl main ec72cd6 (TlaCheck from #126), one JSON line per cell as `TlaCheck --results` writes it, merged from the split and helper part files (deduplicated by cell; a finished verdict wins over a timeout). Run on two AWS spot boxes (a c8i.32xlarge in eu-west-2, reclaimed after 3 hours, then a c7a.32xlarge in eu-north-1 that resumed from the saved results), `--timeout 3600`, `--heap 2g` or `3g`. The runbook is in docs/tla.md ("Grid runs"); the grid options per file name are in the table below. Follow-ups are tracked in #112.

| Grid (`--grid-with`) | Cells | Result |
|---|---|---|
| base, flow, stub, give-up, abandon, inject rej-poll, reconnect | 576 each (stub 72) | all agree |
| release, release + give-up, t3, t3 + give-up | 576 each | all agree except the single largest cell, which timed out in TLC |
| frame errors, reconnect + data after it | 576 each | all agree; the largest cell is a cap artefact (below) |
| flow with the #43 checks off | 576 | all agree; the largest cell is capped, and TLC finds a 28-step Deadlock past the explorer's cap |
| inject i-poll | 576 | **103 disagree**: explorer clean, TLC a Quiescence lasso, same state counts; 1 timeout |
| inject frmr, b0/b1 cells | 384 | all agree; 2 cap artefacts |
| unit-data, b0/b1 cells | 384 | all agree; 4 timeouts |
| inject srej-command, local-busy, b0/b1 | 10 each | stopped after 10 cells each (all budget 1): srej-command 6 agree, 4 timed out in TLC; local-busy 5 agree, 5 timed out in TLC (one of those, `g-ab3-ba3-b1-k2-nosrej-drop@local-busy=both`, is an explorer Deadlock at 20 steps that TLC did not reach in time) |
| lockstep against Ax25MeaningIndependent | 576 | 336 same, 239 differ (the known D1 to D6), 1 timeout |

File names: `base` (--grid), `flow` (--grid-flow), `flow-behind43` (--grid-flow --grid-with disable=busy-tracks-flow+flow-off-delivery), `stub` (--grid-stub), `t3`, `give-up` (accept-give-up), `t3-give-up`, `abandon` (reconnect=A,abandon=A), `inj-rej-poll`, `inj-i-poll`, `inj-frmr-b01`, `inj-srej-b01` (inject=both,inject-frame=...), `release` (release=both), `release-give-up`, `reconnect` (reconnect=both), `reconnect-data` (reconnect=both,data-after-reconnect), `frame-errors` (frame-errors=control+info+length+n1+i-response), `unit-data-b01` (unit-data=both), `local-busy-b01` (local-busy=both), `lockstep` (--grid --lockstep --meaning Ax25MeaningIndependent --lockstep-iterate 8). A `-b01` suffix means only the budget 0 and 1 cells were run.

**Note added after the run (packet-net/ax25sdl#128):** `inj-i-poll.jsonl` records 103 cells where the explorer said clean and TLC reported Quiescence. That was an explorer bug: its liveness check counted a station's own unused injected frame as a way out of the #91 (H1) Timer Recovery state. With #128 the explorer reports Quiescence on those 103 cells and on the one that timed out in TLC, so the two tools agree; the other 422 clean cells stay clean. The file is left as it was recorded. Re-running the frame-errors, inj-rej-poll and inj-frmr-b01 grids with the fix changed no explorer verdict.
