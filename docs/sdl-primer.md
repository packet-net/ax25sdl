# A minimal SDL primer for AX.25 transcribers

This is a working reference for reading the SDL diagrams in the AX.25 v2.2
specification. It is **not** an introduction to SDL as a language — just
enough vocabulary to transcribe AX.25 diagrams into our YAML DSL
(see `docs/adr/0001-sdl-dsl.md`) without misreading shapes.

If you're staring at one of the figures in
`https://github.com/packethacking/ax25spec/blob/main/doc/ax.25.2.2.4_Oct_25.md`
and wondering whether a particular box is meaningful — start here.

## Telling the shapes apart

The figures use the 13 shapes of figure C1.1. The palette `spec-sdl/ax25-sdl-all-shapes.graphml` (in ax25spec) has one of each, and every node in a transcription graphml is a copy of one of them. Each node's `<data key="d5">` text names its shape class, and that text is the only thing the transcriber reads to decide what the node is. The table gives the d5 text exactly, what it looks like on the page, and where it ends up in the yaml.

| d5 shape class | Looks like | Means | In the yaml |
| --- | --- | --- | --- |
| `State` | rounded box (stadium), text such as "0 Idle" | where the machine rests; one at the top of the page, one at the end of every column | `state:` (entry) and `next:` (end), leading number dropped |
| `Signal reception from upper layer` | rectangle with a **notch cut into its left side** | an input from the layer above: DL-, LM-, PH-, MDL- requests, "All Other Primitives" on the request side | `on:` |
| `Signal reception from lower layer` | rectangle with a **notch cut into its right side** | an input from below: received frames, PH- and HW- indications and confirms, "All Other Primitives" on the indication side | `on:` |
| `Internal Signal Reception` | rectangle with a **double bar on the left and a notch on the right** | an input from inside the station: queue events ("I Frame Pops Off I Frame Queue", "Queue Empty"), and on C2a and C2b the timer expiries | `on:` |
| `Signal generation to upper layer` | rectangle with a **point on its left side** | an output to the layer above: indications and confirms | action, `kind: signal_upper` |
| `Signal generation to lower layer` | rectangle with a **point on its right side** | an output to the layer below: frames to send, PH- and LM- requests | action, `kind: signal_lower` |
| `Internal Signal Generation` | rectangle with a **point on the left and a double bar on the right** | an output to inside the station: queue moves such as "Move all events in Served Queue to Awaiting Queue" | action, `kind: internal_out` |
| `Processing description` | plain rectangle | a task: assignments, flags, timers, queue operations | action, `kind: processing` |
| `Subroutine call` | rectangle with a **double bar at each side** | a call to a named subroutine on the machine's subroutines page | action, `kind: subroutine` |
| `Test or decision` | diamond, question inside, answers on the arrows | a branch; each out-edge is labelled Yes or No | `decisions:` plus `{ decision, branch }` steps |
| `Save a signal until a new state is reached` | parallelogram | the input is kept until the state changes | page-level `save:` |
| `Subroutine start` | rectangle with **curved (bracket) ends** | the entry of a subroutine, on a subroutines page only | subroutine `name:` |
| `Return from Subroutine` | circle with an X | the end of a subroutine path | end of a subroutine `path:` |

Reading tips:

- **Notch means in, point means out.** A notch cut into the box is a reception; a point sticking out is a generation. Left is the upper layer, right the lower layer. The double bar marks the internal kinds.
- **Timers are not drawn the same way everywhere.** Data Link draws "Timer T1 Expiry" as a reception from the lower layer; C2a and C2b draw "T102 Expiry" with the internal reception shape; C5 and C6 use a plain left or right notch. Copy the shape the page uses. The event id is the same whichever shape it has.
- **The same label can be two shapes.** Data Link's Disconnected page has "All Other Primitives" from both sides; they are two different events (`__from_upper_layer`, `__from_lower_layer`).
- **Multi-line boxes.** Each line of an action box is one action, in order. See the runbook's "Labels" section before typing a label with a line break.
- **Connectors.** Some figures continue a column on another page through a small labelled circle. The palette has no connector shape: draw the column as one continuous path in the graphml.

## How a transition chain reads

A typical chain in an AX.25 SDL reads top-to-bottom, like so:

```
   [State]          ← we sit here
       │
   ⟩[Input]         ← this is what woke us up
       │
   ⟨Decision?⟩      ← maybe a branch
   No│      │Yes
     │      │
  [Task]  [Task]    ← actions on each branch
     │      │
  [Out⟩  [Out⟩      ← frames or primitives we emit
     │      │
   [State][State]   ← where we end up
```

When encoding into our YAML DSL, each chain from start-state through
inputs/decisions/tasks/outputs to end-state becomes **one transition
entry**. A decision with N outgoing branches becomes N entries on the
same input, each carrying a mutually-exclusive guard.

## When the figure surprises you

The figures are the source of truth. Several of the AX.25 transitions are
counter-intuitive on first read — especially around busy-state handling
and the timer-recovery state — and most of those surprises are deliberate.
The spec author's notes describe these as having "taken quite a bit of
study to understand" even for experienced practitioners.

**Encode what the figure shows, even if it surprises you.** Do not flip a
branch label, swap a Yes/No, or substitute a "correct-looking" action just
because the figure looks wrong to you. The figure is almost certainly
right and you (or I, or any of the agents helping with this) are almost
certainly mis-reading the SDL convention, mis-inferring the semantics of
the variables, or applying intuition from a different protocol.

When you do encounter genuine surprise, the workflow is:

1. **Re-read the shapes.** Look at this primer's shape table and confirm
   you've identified each box correctly. Most "spec bugs" are
   shape-confusion in disguise.
2. **Transcribe faithfully.** Encode what the figure literally shows.
3. **Flag for review.** Add a `verification_pending:` block in the
   transition's `notes:` that captures *what* surprised you, *why*, and
   the alternative interpretation you considered. The PR review picks it
   up; the upstream maintainers can confirm.
4. **Only after explicit upstream confirmation of a spec bug** would we
   consider deviating from the figure. That is the rare path, not the
   default.

We need our implementation to work against real LinBPQ / Xrouter peers,
*and* those peers were themselves built against this spec, so faithful
transcription is the path to interoperability — not "fixing" the figures.

[upstream]: https://github.com/packethacking/ax25spec/issues

## Shape direction and the d5 shape class

figc1.1 defines four "signal" shape-classes that are visually
direction-coded:

- **Left-notch** input = "Signal reception from upper layer" (DL-* primitives from Layer 3)
- **Right-notch** input = "Signal reception from lower layer" (frames from the peer)
- **Left-tab** output = "Signal generation to upper layer" (indications/confirms to Layer 3)
- **Right-tab** output = "Signal generation to lower layer" (frames to transmit)

Each node in the GraphML files carries a `d5` description that records
the shape class. The d5 value and the visual direction are consistent
and both correctly identify which layer a signal comes from or goes to.

The **text label** on a shape identifies the specific event or action;
the **d5 shape class** identifies the layer direction.

## YAML schema (lossless encoding)

The YAML DSL is designed so a figure can be **redrawn from the YAML
alone** (minus physical layout). That requires preserving more than just
"event → guard → actions → next state".

Worked structure:

```yaml
machine: data_link
state: Connected
coverage: complete
source:
  spec: ax.25.2.2.4_Oct_25
  figure: figc4.4a
  url: https://raw.githubusercontent.com/packethacking/ax25spec/main/doc/media/figc4.4a.png
variables: [V(S), V(R), RC]

# Optional: events that should be deferred ("saved") in this state
# until a new state is reached. Corresponds to the SDL save parallelogram.
save: []

# Decision-diamond catalogue. Each diamond has a stable id, the question
# as drawn in the figure (lossless), and the canonical predicate evaluated
# at runtime. Transitions reference diamonds by id from path[].
decisions:
  - id: own_receiver_busy_test
    question: "Own Receiver Busy?"
    predicate: own_receiver_busy
  - id: t1_running_test
    question: "T1 Running?"
    predicate: T1_running

transitions:
  - id: t04_dl_flow_on_when_busy_and_T1_not_running
    on: DL_FLOW_ON_request
    # Ordered interleaving of decision-branch steps and action steps,
    # exactly as they appear in the column. The codegen compiles this
    # into a flat (guard, actions[]) pair for the runtime; the original
    # ordering is preserved so the figure can be redrawn.
    path:
      - { decision: own_receiver_busy_test, branch: "Yes" }
      - { decision: t1_running_test,         branch: "No"  }
      - { action: clear_own_receiver_busy, kind: processing }
      - { action: RR command,               kind: signal_lower }
      - { action: clear_acknowledge_pending, kind: processing }
      - { action: stop_T3,                  kind: processing }
      - { action: start_T1,                 kind: processing }
    next: Connected
```

`kind:` is one of:

| `kind`         | Shape (figc1.1)                            | What lives here                              |
|----------------|--------------------------------------------|----------------------------------------------|
| `signal_upper` | Signal generation to upper layer           | DL-* indications/confirms                    |
| `signal_lower` | Signal generation to lower layer           | frames to transmit (UI, SABM, RR, …)         |
| `processing`   | Processing description (plain rectangle)   | internal tasks (`V(S):=0`, `start_T1`)       |
| `subroutine`   | Subroutine call (rect with sidebars)       | named ref into a subroutine page             |
| `internal_out` | Internal Signal Generation                 | posts to internal queues                     |

The other figc1.1 shapes route to different places in the schema:

| Shape                                | Routes to                                  |
|--------------------------------------|--------------------------------------------|
| State (rounded rect)                 | `state:` (focal) / `next:` (target)         |
| Signal reception from Lower/upper     | `on:` event (catalog implicit)             |
| Internal Signal Reception            | `on:` event (catalog implicit)             |
| Test or decision (diamond)           | entry in `decisions:` + `path:` reference  |
| Save a signal until a new state      | state-level `save: [...]`                  |
| Subroutine start / Return            | only on subroutine pages (different schema)|

### Loops in the figure

When a diamond's Yes branch loops back to the same diamond (e.g.
figc4.4's n148-n149-n150-n151-n148 stored-frame drain), use the
`loop_while` path step:

```yaml
path:
  - { decision: vr_i_frame_stored, branch: "Yes" }   # entry gate
  - loop_while: vr_i_frame_stored                     # iterate body while predicate stays true
    body:
      - { action: retrieve_stored_V_r_I_frame, kind: processing }
      - { action: DL_DATA_indication,          kind: signal_upper }
      - { action: "V(r) := V(r) + 1",          kind: processing }
  - { decision: p_eq_1, branch: "Yes" }              # continues here when loop exits
  - { action: "F := 1", kind: processing }
```

Semantics:

- The `decision: ..., branch: "Yes"` step that PRECEDES the loop gates
  entry — the transition only fires if the predicate is true on
  arrival, mirroring the figure's diamond at the loop head.
- `loop_while` re-runs the body while the same predicate stays true,
  and exits when it becomes false (the body's actions are expected to
  change something that may flip the predicate — here, advancing V(r)
  past the stored frame).
- Body steps are **action-only** today; nested decisions or nested
  loops aren't supported by the codegen. Refactor as a subroutine if
  you need them.

The codegen compiles `loop_while` into the runtime's flat
`Actions[]` (one iteration of the body inlined) plus a
`Loops[]` metadata entry recording `(startIndex, length, predicate)`.
Loop-aware dispatchers consult `Loops[]` to re-execute the body;
loop-unaware dispatchers run `Actions[]` linearly (which executes
each loop body once — close enough for the common one-stored-frame
case and matches the smoke tests).

## Where this primer is invoked

- `CONTRIBUTING.md` references this file under "Working with SDL diagrams".
- `docs/adr/0001-sdl-dsl.md` references it under "Worked example".
- Every SDL transcription PR description should link here.
