---------------------------- MODULE Ax25Runner -----------------------------
(***************************************************************************)
(* Hand-written. How the generated Data Link tables (Ax25DataLinkTables)   *)
(* are run, independent of what any verb or guard atom means: selecting    *)
(* the one transition whose guard holds, running its action chain box by   *)
(* box with its loop regions, calling subroutines through the call-site    *)
(* binding map, moving to the next state, and the frame around one         *)
(* dispatch (the event's own effect, a pre-emption the meaning may claim,  *)
(* the settle step). A port of the reference interpreter's Dispatch,       *)
(* ExecuteActions, RunLoop and InvokeSubroutine                            *)
(* (codegen/src/Packet.Sdl.Interpreter/Machine.cs).                        *)
(*                                                                         *)
(* What each verb, atom and signal means comes from a meaning module,      *)
(* through the constant operators declared below. Ax25Semantics.tla binds  *)
(* them to a meaning by instantiating this module. The interface a meaning *)
(* implements is spec/tla/MeaningInterface.md.                             *)
(*                                                                         *)
(* The runner reads and writes these fields of the dispatch context and    *)
(* nothing else: st (the Data Link state, a page name), err (the error,    *)
(* "" for none), tid (the transition that fired) and arms (every table row *)
(* the dispatch ran, for coverage).                                        *)
(*                                                                         *)
(* Saves (the figures' Save symbol, Saves in the tables) are runner policy *)
(* too, and need nothing from a meaning: SavedIn says which inputs a state *)
(* holds instead of dispatching, ReleaseIndex which held input is consumed *)
(* next after a transition has entered a state (pinned semantic 19 in      *)
(* docs/explorer.md). The held inputs themselves are not part of the       *)
(* station record: the harness keeps them beside it and dispatches each    *)
(* released one through Dispatch, exactly as a fresh input.                *)
(***************************************************************************)
EXTENDS Ax25DataLinkTables

LOCAL INSTANCE Integers
LOCAL INSTANCE Sequences
LOCAL INSTANCE FiniteSets
LOCAL INSTANCE Ax25Values

CONSTANTS
    Begin(_, _),           \* Begin(m, e): the dispatch context for event e on station record m
    Arrive(_, _),          \* Arrive(c, e): what the event itself does before any table row runs
    AtomError(_, _, _),    \* AtomError(c, e, atom): why atom cannot be answered here, "" when it can
    AtomValue(_, _, _),    \* AtomValue(c, e, atom): the atom's truth value, when AtomError is ""
    Processing(_, _, _),   \* Processing(c, e, verb): a processing box
    SignalUpper(_, _, _),  \* SignalUpper(c, e, verb): a signal to layer 3
    SignalLower(_, _, _),  \* SignalLower(c, e, verb): a signal to the lower layer or the link multiplexer
    InternalOut(_, _, _),  \* InternalOut(c, e, verb): an internal signal (the I frame queue)
    Preempts(_, _),        \* Preempts(c, e): the meaning handles this dispatch itself, not the table
    Preempt(_, _),         \* Preempt(c, e): that handling
    AfterTransition(_)     \* AfterTransition(c): bookkeeping after a transition that did not fail

\* The interpreter's cap on one loop region's iterations.
MaxLoopIterations == 4096

(***************************************************************************)
(* Guards                                                                  *)
(***************************************************************************)

\* A guard is a conjunction of <<atom, negated>> terms, evaluated left to
\* right and stopping at the first false term, as LINQ All does; an atom
\* that cannot be answered is an error only if it is reached.
RECURSIVE EvalTerms(_, _, _, _)
EvalTerms(c, e, terms, i) ==
    IF i > Len(terms) THEN [v |-> TRUE, err |-> ""]
    ELSE LET a == terms[i][1]
             why == AtomError(c, e, a)
         IN  IF why # "" THEN [v |-> FALSE, err |-> why]
             ELSE IF AtomValue(c, e, a) = terms[i][2] THEN [v |-> FALSE, err |-> ""]
             ELSE EvalTerms(c, e, terms, i + 1)

\* Select the one guard of gs that holds. Every guard is evaluated (an error
\* in any is an error); none or several holding is an error: the table is
\* not total or not deterministic here.
SelectGuard(c, e, gs) ==
    LET rs == [j \in 1..Len(gs) |-> EvalTerms(c, e, gs[j], 1)]
        bad == {j \in 1..Len(gs) : rs[j].err # ""}
        hit == {j \in 1..Len(gs) : rs[j].err = "" /\ rs[j].v}
    IN  IF bad # {} THEN [err |-> rs[CHOOSE j \in bad : \A j2 \in bad : j <= j2].err, i |-> 0]
        ELSE IF hit = {} THEN [err |-> "no guard matched", i |-> 0]
        ELSE IF Cardinality(hit) > 1
             THEN [err |-> ToString(Cardinality(hit)) \o " transitions match simultaneously: the table is non-deterministic here", i |-> 0]
        ELSE [err |-> "", i |-> CHOOSE j \in hit : TRUE]

(***************************************************************************)
(* Subroutine call sites (InvokeSubroutine)                                *)
(***************************************************************************)

\* Call-site spelling to subroutine table name: the interpreter's explicit
\* binding map. A label not listed binds to the table of the same name.
SubroutineBindings ==
    ("Check Need For Response" :> "Check_Need_for_Response") @@
    ("Clear Exception Conditions" :> "Clear_Exception_Conditions") @@
    ("Enquiry Response (F = 0)" :> "Enquiry_Response") @@
    ("Enquiry_Response_F_1" :> "Enquiry_Response") @@
    ("Invoke Retransmission" :> "Invoke_Retransmission") @@
    ("N(r) Error Recovery" :> "N_r_Error_Recovery") @@
    ("N(r) Recovery" :> "N_r_Error_Recovery") @@
    ("Select_T1_Value" :> "Select_T1") @@
    ("Transmit Enquery" :> "Transmit_Enquiry") @@
    ("Transmit Enquiry" :> "Transmit_Enquiry") @@
    ("UI Check" :> "UI_Check")

SubroutineFor(label) == IF label \in DOMAIN SubroutineBindings THEN SubroutineBindings[label] ELSE label

\* Enquiry Response takes an F parameter, carried by the call-site label. It
\* is staged before the call by what the processing box of the same text
\* means.
BeforeCall(c, e, label) ==
    CASE label = "Enquiry_Response_F_1" -> Processing(c, e, "F := 1")
      [] label = "Enquiry Response (F = 0)" -> Processing(c, e, "F := 0")
      [] OTHER -> c

\* Some pages draw "Clear Exception Conditions" as a processing box; it
\* means the figc4.7 subroutine.
ProcessingCallsSubroutine(verb) == verb = "Clear Exception Conditions"

(***************************************************************************)
(* Running the tables (Dispatch, ExecuteActions, RunLoop, InvokeSubroutine)*)
(***************************************************************************)

DataLinkStates == DOMAIN Pages

\* The page for st has an arm for ev.
Handles(st, ev) == st \in DOMAIN Pages /\ ev \in DOMAIN Pages[st]

RECURSIVE Act(_, _, _), RunRange(_, _, _, _, _), RunLoop(_, _, _, _, _), RunActions(_, _, _, _, _), CallSubroutine(_, _, _)

Invoke(c, e, label) == CallSubroutine(BeforeCall(c, e, label), e, SubroutineFor(label))

\* One action box, by shape class. Once err is set nothing more runs (the
\* interpreter has thrown).
Act(c, e, box) ==
    IF c.err # "" THEN c
    ELSE CASE box.kind = "signal_upper" -> SignalUpper(c, e, box.verb)
           [] box.kind = "signal_lower" -> SignalLower(c, e, box.verb)
           [] box.kind = "internal_out" -> InternalOut(c, e, box.verb)
           [] box.kind = "subroutine" -> Invoke(c, e, box.verb)
           [] box.kind = "processing" ->
                 IF ProcessingCallsSubroutine(box.verb) THEN Invoke(c, e, box.verb) ELSE Processing(c, e, box.verb)
           [] OTHER -> Fail(c, "unknown action kind '" \o box.kind \o "' for verb '" \o box.verb \o "'")

\* actions[from..to], straight (a loop region's body never nests a loop).
RunRange(c, e, acts, from, to) ==
    IF from > to \/ c.err # "" THEN c ELSE RunRange(Act(c, e, acts[from]), e, acts, from + 1, to)

\* A loop region: while (test first) or do-while (test last); pred is the
\* continue condition; the iteration cap is the interpreter's.
RunLoop(c, e, acts, lp, n) ==
    LET body(c1) == RunRange(c1, e, acts, lp.start, lp.start + lp.len - 1)
        cap == "loop over actions[" \o ToString(lp.start - 1) \o ".." \o ToString(lp.start + lp.len - 2)
               \o "] exceeded the iteration cap: diverging predicate '" \o lp.pred[1] \o "'"
    IN  IF c.err # "" THEN c
        ELSE IF lp.atEnd THEN
            IF n >= MaxLoopIterations THEN Fail(c, cap)
            ELSE LET b == body(c)
                     g == EvalTerms(b, e, <<lp.pred>>, 1)
                 IN  IF b.err # "" THEN b
                     ELSE IF g.err # "" THEN Fail(b, g.err)
                     ELSE IF g.v THEN RunLoop(b, e, acts, lp, n + 1) ELSE b
        ELSE LET g == EvalTerms(c, e, <<lp.pred>>, 1)
             IN  IF g.err # "" THEN Fail(c, g.err)
                 ELSE IF ~g.v THEN c
                 ELSE IF n >= MaxLoopIterations THEN Fail(c, cap)
                 ELSE RunLoop(body(c), e, acts, lp, n + 1)

\* An action chain from position i (ExecuteActions): a loop region that
\* starts at i runs as a loop and the chain resumes after it.
RunActions(c, e, acts, loops, i) ==
    IF i > Len(acts) \/ c.err # "" THEN c
    ELSE LET at == {j \in 1..Len(loops) : loops[j].start = i /\ loops[j].len > 0}
         IN  IF at = {} THEN RunActions(Act(c, e, acts[i]), e, acts, loops, i + 1)
             ELSE LET lp == loops[CHOOSE j \in at : \A j2 \in at : j <= j2]
                  IN  RunActions(RunLoop(c, e, acts, lp, 0), e, acts, loops, lp.start + lp.len)

\* A subroutine: its path guards are evaluated live and exactly one must hold.
CallSubroutine(c, e, name) ==
    IF name \notin DOMAIN Subroutines
    THEN Fail(c, "subroutine call '" \o name \o "' does not bind to any subroutine table")
    ELSE LET paths == Subroutines[name]
             sel == SelectGuard(c, e, [j \in 1..Len(paths) |-> paths[j].guard])
         IN  IF sel.err # "" THEN Fail(c, "subroutine " \o name \o ": " \o sel.err)
             ELSE RunActions([c EXCEPT !.arms = @ \cup {name \o ":" \o paths[sel.i].id}], e, paths[sel.i].actions, paths[sel.i].loops, 1)

\* Leave a transition: move to its next state, recording which one fired.
Goto(c, next, tid) ==
    IF c.err # "" THEN c
    ELSE IF next \notin DataLinkStates THEN Fail(c, "transition " \o tid \o " targets unknown state '" \o next \o "'")
    ELSE [c EXCEPT !.st = next, !.tid = tid, !.arms = @ \cup {c.st \o "/" \o tid}]

\* Select the one transition of the current page on this event whose guard
\* holds (evaluated against the pre-transition state) and run it.
DispatchState(c, e) ==
    IF c.st \notin DataLinkStates THEN Fail(c, "unknown state '" \o c.st \o "'")
    ELSE IF ~Handles(c.st, e.ev) THEN Fail(c, "state " \o c.st \o " has no transition for event '" \o e.ev \o "'")
    ELSE LET ts == Pages[c.st][e.ev]
             sel == SelectGuard(c, e, [j \in 1..Len(ts) |-> ts[j].guard])
         IN  IF sel.err # "" THEN Fail(c, "state " \o c.st \o ", event '" \o e.ev \o "': " \o sel.err)
             ELSE Goto(RunActions(c, e, ts[sel.i].actions, ts[sel.i].loops, 1), ts[sel.i].next, ts[sel.i].id)

(***************************************************************************)
(* Saves (Dispatch's save and ReplayHeld)                                  *)
(***************************************************************************)

\* State st saves event ev: an input that arrives there causes nothing and
\* is held, after any already held. An explicit save takes precedence over
\* the page's "all other" column, so a harness that routes unhandled inputs
\* there asks this first.
SavedIn(st, ev) == st \in DOMAIN Saves /\ \E i \in 1..Len(Saves[st]) : Saves[st][i] = ev

\* After every transition (into the same state included): the position of
\* the first held input, from the head, that state st does not save, or 0
\* when it saves them all. That input is consumed next, as if it had just
\* arrived, and the scan repeats in the state its transition enters; inputs
\* still saved keep their place and order.
ReleaseIndex(st, held) ==
    LET free == {i \in 1..Len(held) : ~SavedIn(st, held[i].ev)}
    IN  IF free = {} THEN 0 ELSE CHOOSE i \in free : \A j \in free : i <= j

\* The held inputs without the one at position i.
Unheld(held, i) == SubSeq(held, 1, i - 1) \o SubSeq(held, i + 1, Len(held))

(***************************************************************************)
(* Around the table (Dispatch)                                             *)
(***************************************************************************)

\* Open the context and let the event itself act (Arrive) before the table.
Prepare(m, e) == Arrive(Begin(m, e), e)

\* After the transition: the meaning's bookkeeping, unless the dispatch failed.
Settle(c) == IF c.err # "" THEN c ELSE AfterTransition(c)

\* The context after dispatching event e on station record m: fx its
\* effects in order, tid the transition that fired, arms the rows run, and
\* err non-empty when the interpreter would have thrown. The meaning's
\* Persist turns the context back into a station record.
Dispatch(m, e) ==
    LET c0 == Prepare(m, e)
    IN  IF c0.err # "" THEN c0
        ELSE IF Preempts(c0, e) THEN Preempt(c0, e)
        ELSE Settle(DispatchState(c0, e))

=============================================================================
