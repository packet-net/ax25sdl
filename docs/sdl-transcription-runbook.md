# SDL transcription runbook

How to transcribe one page of AX.25 v2.2 Annex C, from the figure in the PDF to a reviewed pull request, starting from nothing. It covers every machine: Simplex Physical (C2a), Duplex Physical (C2b), Link Multiplexer (C3), Data Link (C4), Management Data Link (C5) and Segmenter/Reassembler (C6).

Read [`sdl-primer.md`](sdl-primer.md) first for the shapes. This page is the workflow.

## How it fits together

The figures live in [`packethacking/ax25spec`](https://github.com/packethacking/ax25spec). A transcription is a PR **there**. This repo (ax25sdl) holds the tooling and, later, picks the change up with a submodule pin bump.

| Thing | Where (in ax25spec unless noted) | Made by |
| --- | --- | --- |
| Source figures | `spec-sdl/v2.2-errata/ax.25 v2.2 rev 4 Oct 2025.pdf` | the working group |
| Shape palette | `spec-sdl/ax25-sdl-all-shapes.graphml` (13 shapes) | fixed |
| **Figure source** | `spec-sdl/v2.2-errata/<machine-dir>/sdl/<Prefix>_<Page>.graphml` | **you**: this is the canonical, normative artefact |
| Transcription | `.../<machine-dir>/yaml/<page>.sdl.yaml` | `Packet.Sdl.Transcribe` (in ax25sdl), never by hand |
| Render | `.../<machine-dir>/svg/<Prefix>_<Page>.svg` | `python3 tools/render/render_all.py` |
| Citations | `.../<machine-dir>/yaml/<page>.citations.yaml` | humans, later (see "After the transcription") |
| Catalogues | `spec-sdl/events.yaml`, `predicates.yaml`, `actions.yaml` | you, by hand |
| Machine and figure table | `codegen/src/Packet.Sdl.IR/AnnexC.cs` (ax25sdl) | fixed; every Annex C page is already listed |

CI in ax25spec regenerates every `*.sdl.yaml` from its graphml with the ax25sdl commit in `.github/ax25sdl-tooling-ref`, and every svg with `tools/render`, and fails on any difference. So the graphml is the only thing you really author; the yaml and svg are regenerated, never edited.

## Rules

1. **Trust the figure.** Draw what the page shows, including its spelling ("SIEZE", "Sized") and anything that looks wrong. Don't fix a label, swap a Yes/No, or add a missing arrow. If a column surprises you, re-read the shapes, then draw it as drawn.
2. **Contradictions become issues, not fixes.** If a figure contradicts the prose, or contradicts itself (a state drawn "1 Sieze Pending" on the Seized page), confirm it against both sources, then raise it as its own issue in ax25spec quoting the figure (page and column) and the prose (section and sentence). Link the issue from the PR. The transcription still records what is drawn.
3. **Every page is transcribed twice.** Two transcribers work independently, in separate worktrees, and neither looks at the other's graphml or yaml until both are finished. Then compare the two generated yaml files (`cmp a/idle.sdl.yaml b/idle.sdl.yaml`; the graphml layouts will differ, the yaml must not). Identical means done. Every difference is resolved against the source figure, never by picking one side, and the PR says what differed and how it was settled. Only one graphml is committed.
4. **Stay on the page.** One PR per page (a state drawn over several figures, such as C4.4a to C4.4c, is one page). Anything else you notice gets its own issue.

## Naming

The graphml filename picks the machine, the state and the figure, through the Annex C table in [`AnnexC.cs`](../codegen/src/Packet.Sdl.IR/AnnexC.cs). Use it exactly: an unknown prefix or page is rejected with the list of valid ones.

| Machine (Annex C) | Filename prefix | `machine:` | Directory | Figures |
| --- | --- | --- | --- | --- |
| Simplex Physical | `SimplexPhy_` | `simplex_phy` | `simplex-physical` | C2a.1 to C2a.7, C2a.s |
| Duplex Physical | `DuplexPhy_` | `duplex_phy` | `duplex-physical` | C2b.1 to C2b.5 |
| Link Multiplexer | `LinkMux_` | `link_mux` | `link-multiplexer` | C3.1 to C3.4 |
| Data Link | `DataLink_` | `data_link` | `data-link` | C4.1 to C4.7 |
| Management Data Link | `ManagementDataLink_` | `management_data_link` | `management-data-link` | C5.1 to C5.8 |
| Segmenter | `Segmenter_` | `segmenter` | `segmenter` | C6.1 |
| Reassembler | `Reassembler_` | `reassembler` | `reassembler` | C6.2 to C6.4 |

Each machine has its own directory, and a graphml filed under another machine's directory is rejected. Segmenter and Reassembler are separate even though Annex C6 draws them together: both have a Ready state.

The part after the underscore is the page key from the table: the state name in PascalCase as the figures draw it (`LinkMux_SiezePending.graphml`, figure C3.2), or for a subroutines page its caption ending in `Subroutines` (`LinkMux_Subroutines.graphml`, `ManagementDataLink_T1NegotiationSubroutines.graphml`). The yaml filename is derived from it (`sieze_pending.sdl.yaml`).

## Step by step

### 1. Set up

```sh
# ax25spec: a worktree on a fresh branch from origin/main
git -C <ax25spec> fetch origin
git -C <ax25spec> worktree add -b sdl/<machine>-<page> <ax25spec>/.claude/worktrees/<page> origin/main

# ax25sdl: the tooling, at the commit ax25spec pins
git -C <ax25sdl> fetch origin
git -C <ax25sdl> worktree add --detach <ax25sdl>/.claude/worktrees/tooling $(cat <ax25spec>/.github/ax25sdl-tooling-ref)
dotnet build <ax25sdl>/.claude/worktrees/tooling/codegen/ax25sdl.slnx
```

Use absolute paths throughout. The physical PDF page of each figure is in the `PdfPages` column of `AnnexC.cs`.

### 2. Read the figure

```sh
mkdir -p /tmp/fig && pdftoppm -f 81 -l 81 -r 300 -png "<ax25spec>/spec-sdl/v2.2-errata/ax.25 v2.2 rev 4 Oct 2025.pdf" /tmp/fig/p
```

Render at 300 dpi or more, and zoom into strips (crop with Python PIL) until every label, arrow and branch is legible. Before drawing anything, write an inventory of the page:

- the entry state symbol (top left) and its exact text;
- every input column, left to right, with its exact label and its shape class (primer, "Telling the shapes apart");
- every box down each column, with its shape class and its exact text, line by line;
- every decision, with both branch labels and where each goes;
- where columns merge (an arrow joining another column), loop back, or end in a Save;
- every terminal state symbol's exact text.

Never guess. If something genuinely cannot be read, draw your best reading, and record it as `verification_pending:` in the PR body and in your report: the figure, the column, what you read and what else it could be. The yaml has no field for it, because it is regenerated from the graphml.

### 3. Draw the graphml

Start from the palette, `spec-sdl/ax25-sdl-all-shapes.graphml`. It holds one node of each of the 13 shape classes; every node you draw is a copy of one of them, and its `<data key="d5">` text is what the walker reads as the shape class. The walker ignores how a node looks; the d5 text is what counts.

You can draw in yEd, or generate the file with a short script that copies the palette's node blocks and sets each node's id, geometry and label (one transcriber of figure C3.1 did this). Either way:

- **Columns run left to right in the figure's order.** The walker numbers transitions by the x position of each input symbol (`t01_...`, `t02_...`), so the order is part of the output. See "Order in the yaml".
- **One entry state** with an arrow to each input symbol. Every column ends in a State symbol whose label is the figure's text (`0 Idle`, `1 Sieze Pending`). The leading number is dropped and the rest becomes the `next:` state.
- **Labels are typed verbatim.** See "Labels" below for line breaks.
- **Decisions:** each out-edge of a Test or decision carries a label, `Yes` or `No` (or `undefined` where the figure says so). An unlabelled decision edge is an error.
- **Merges:** where the figure joins two columns (C3.1's LM-EXPEDITED-DATA Request joins the LM-DATA Request column), draw the edge into the shared node. The walker gives each input its own transition with the shared tail.
- **Loops:** draw the back-edge into the decision the figure loops to. The walker turns it into a `loop_while` step; a back-edge that doesn't come through a decision is rejected.
- **Save:** a column ending in the Save symbol becomes a page-level `save:` entry, not a transition.

### 4. Generate the yaml

```sh
dotnet run --project <ax25sdl>/.claude/worktrees/tooling/codegen/src/Packet.Sdl.Transcribe -- \
  -i <ax25spec-wt>/spec-sdl/v2.2-errata/<machine-dir>/sdl/<Prefix>_<Page>.graphml \
  -o <ax25spec-wt>/spec-sdl/v2.2-errata/<machine-dir>/yaml/
```

A non-zero exit means the page was not written. The common causes:

- *not an Annex C machine* or *not a page of*: fix the filename (see "Naming").
- *must be under ...*: the graphml is in another machine's directory.
- *would both write*: two graphmls map to one yaml file. Nothing is written.
- *has no graphml*: a yaml in the output directory that no graphml produces (its graphml was deleted or renamed). Delete it, or restore the graphml.
- *not a valid event id ... Add a rule*, or *primitive label ... is not*: an input label that no rule in `EventResolver.cs` covers, or a primitive typed with a space or a line break inside its name. Retype it on one line as the figure spells it. If the figure itself draws a new form, that is a tooling change: add the rule and a test in ax25sdl first, get it merged, and bump `.github/ax25sdl-tooling-ref`. Don't reword the label to fit.
- *no branch label*: a decision edge is unlabelled.
- *Unhandled cycle* or *guarded*: a back-edge that is not a loop through a decision; check the drawing against the figure.
- *subroutine '...'*: one subroutine could not be walked (often a dead end where an edge is missing, or a start with no edge). The whole page fails; a subroutine is never silently dropped.

Read the generated yaml against your inventory, column by column.

### 5. Update the catalogues

Every `on:` event must be in `spec-sdl/events.yaml`. Add the page's new events to a group that fits, with the figure label as a comment. Event ids come from the walker; the conventions are in "Event names" below. Every decision `predicate:` must be in `spec-sdl/predicates.yaml` (see [`sdl-guard-and-event-catalogue.md`](sdl-guard-and-event-catalogue.md)). `actions.yaml` entries are optional ([`sdl-verb-catalogue.md`](sdl-verb-catalogue.md)).

### 6. Render and compare with the figure

```sh
cd <ax25spec-wt> && python3 tools/render/render_all.py
```

Open the new svg next to the PDF page and check every column, box and label. The render is laid out automatically, so compare content, not position. Commit the svg; CI checks it is current.

### 7. Check it with codegen

Run from the ax25sdl tooling worktree, so the totality lint finds its known-findings list:

```sh
cd <ax25sdl>/.claude/worktrees/tooling && dotnet run --project codegen/src/Packet.Sdl.CodeGen -- \
  --in <ax25spec-wt>/spec-sdl --json --json-out /tmp/codegen-check
```

This runs every lint over every page: unknown events and predicates, decision completeness, guard overlap, state targets and totality. For a machine whose other pages are not done yet you will see `withheld <machine>: partly transcribed`; that is expected (see "Partly transcribed machines"). A `next:` state that is not an Annex C state of the machine is an error: either a drawing slip, or a figure defect for rule 2.

### 8. Compare with the second transcription

Both transcribers finish steps 1 to 7 on their own. Then compare the yaml (rule 3) and resolve any difference against the figure.

### 9. Open the PR in ax25spec

Commit the graphml, yaml, svg and catalogue changes. The PR body says:

- which figure and PDF page;
- who transcribed it, human or agent, for both transcriptions;
- that the two were compared, what differed and how each difference was settled against the figure;
- any `verification_pending:` readings (step 2) and any issues raised under rule 2.

Link the epic the page belongs to. Human review of the ax25spec PR is the sign-off.

## Order in the yaml

The yaml must not depend on how a page happens to be drawn or stored, so two correct transcriptions give the same file:

- **Transitions** follow the input symbols left to right by x position, so draw the columns in the figure's order.
- **Decision branches** are walked Yes first, then No, then any other label (such as Undefined), whatever order the edges are stored in the graphml. Transitions of one input, and subroutine path ids (`t01_queue_event_yes`, `t02_queue_event_no`), are numbered in that order.
- **Subroutines** follow their start symbols top to bottom by y position, then left to right by x. Lay the starts out in the figure's reading order.

## Labels

- **Input labels are one line.** An input names one event, so type it on a single line and let yEd wrap it. Line breaks and runs of spaces are read as single spaces, so a primitive with a break or a space inside its name (`LM-` then `DATA Request`, or `PH-EXPEDITED DATA Request`) is rejected rather than given a different id.
- **Each line of an action box is one action.** A box that lists statements one per line (`V(s) <- 0` then `V(a) <- 0`) becomes that many path steps, in order. So put a hard line break only where the figure starts a new statement; type a long single statement (`Move all events submitted by current DL to Current Queue`) on one line and let yEd wrap it.
- Assignment arrows can be typed `<-` or as the arrow character; both become `:=` in the yaml.
- A decision's question is copied into `question:` verbatim; its `predicate:` is derived from it.

## Event names

The walker derives `on:` from the input label and shape class. Spelling is kept; case is normalised.

| Figure label | Event id | Rule |
| --- | --- | --- |
| `DL-DATA Request`, `LM-SEIZE Confirm`, `HW-AOS Indication`, `MDL-NEGOTIATE Request` | `DL_DATA_request`, `LM_SEIZE_confirm`, `HW_AOS_indication`, `MDL_NEGOTIATE_request` | `<PREFIX>-<NAME> <Kind>`: name in upper case with underscores, then the kind (Request, Indication, Indicate, Confirm) in lower case. An underscore drawn for the dash (`PH_SIEZE Confirm`) is accepted; `PH-Quiet` becomes `PH_QUIET` |
| `LM-SIEZE Request`, `PH-SIEZE Confirm` | `LM_SEIZE_request`, `PH_SEIZE_confirm` | a known misspelling maps to the canonical name (below) |
| `SABM`, `I` | `SABM_received`, `I_received` | bare frame name |
| `XID Response`, `FRMR Response` | `XID_response_received`, `FRMR_response_received` | frame with its role |
| `Timer T1 Expiry`, `T102 Expiry`, `TM 201 Expiry`, `TR210 Expiry` | `T1_expiry`, `T102_expiry`, `TM201_expiry`, `TR210_expiry` | timer name, spaces removed |
| `All Other Primitives` (upper or lower) | `all_other_primitives__from_upper_layer` or `__from_lower_layer` | catch-alls record their side; `All Other DL Primitives` likewise |
| `Control Field Error`, `Queue Empty` | `control_field_error`, `queue_empty` | anything else: lower case, underscores |

A label that would give anything other than a plain identifier is rejected with a pointer to `EventResolver.cs`, so a new label family needs a rule (and a test) before its page can be generated.

**One primitive, one id.** Where figures spell the same primitive differently, a small alias table in `EventResolver.cs` maps the figure spelling to the canonical name, so the primitive has one event id on every machine. `on_label` still records the label as drawn. Today it holds only SIEZE to SEIZE for LM- and PH- (C3.1 and C3.2, ax25spec#61). Add an alias only when a page being transcribed needs it, with its issue and a test.

## Partly transcribed machines

Machines are transcribed a page at a time, so a page often leads to a state that has no page yet (C3.1 Idle leads to Sieze Pending). Codegen in ax25sdl handles this without breaking the build:

- If any transition targets a state that `AnnexC.cs` lists for that machine but that has no page, the machine is **withheld**. Its pages still go through every lint, but nothing is generated from them: no tables in any language, no verbs or guard atoms, no totality run, and none of the events only it uses appear in the generated event enums. Every other machine's output is unaffected.
- When the last missing page lands, the machine starts generating and the closed sets (`Ax25Event` and the rest) grow. That shows up as a visible change in the ax25sdl pin-bump PR, as it should.
- A target that is not an Annex C state of the machine at all is an error.
- `coverage: partial` (used by the prose-derived Management Data Link bootstrap) still skips the state-target check for that page.
- Data Link and Management Data Link are already published, so they are never withheld: a transition of theirs that leads to a missing page is an error. A deleted yaml can't quietly remove their generated code.
- Generated files are named per machine (`link_mux_idle.g.go`, `segmenter_ready.g.json`), because states such as Ready recur across machines. The published machines keep their existing names (`connected.g.go`, `ready.g.json`). Two pages that would still share a name fail codegen.

## After the transcription

Once a page is merged, the evidence work follows in separate PRs:

- **Citations:** `<page>.citations.yaml` holds per-transition notes and references (spec prose, plus implementation code at pinned commits; schema `spec-sdl/schema/sdl-citations.schema.json`). It survives regeneration because the walker never writes it.
- **Behaviour:** golden traces ([`golden-traces.md`](golden-traces.md)), the totality lint and its known-findings list ([`lint-totality.md`](lint-totality.md)), and the two-station explorer ([`explorer.md`](explorer.md)). A finding on a real figure is filed as an ax25spec issue and recorded, never fixed in the transcription.
