--------------------------- MODULE Ax25Semantics ---------------------------
(***************************************************************************)
(* Hand-written. What the verbs and guard atoms of the generated Data Link *)
(* tables (Ax25DataLinkTables.tla) mean, and how the tables are run.       *)
(*                                                                         *)
(* The catalogues (spec-sdl/actions.yaml, predicates.yaml, events.yaml)    *)
(* name the verbs, atoms and events but do not define them, so this module *)
(* is a port of the one place that does: the reference interpreter,        *)
(* codegen/src/Packet.Sdl.Interpreter/Machine.cs. Each operator below      *)
(* mirrors one arm of its switches, with the same policies (docs/          *)
(* golden-traces.md, docs/explorer.md pinned semantics 1, 3 and 4). A verb *)
(* or atom the tables use and this module does not bind is an error at     *)
(* the dispatch that reaches it, exactly as in the interpreter. The last   *)
(* section runs the table data as the interpreter's Dispatch,              *)
(* ExecuteActions, RunLoop and InvokeSubroutine do.                        *)
(*                                                                         *)
(* Not modelled, as in the explorer's state fingerprint: N1 and the        *)
(* symbolic timer registers (SRT, T1V, T2, Next T1). Their verbs are       *)
(* accepted and change nothing; no guard reads them.                       *)
(***************************************************************************)
EXTENDS Integers, Sequences, FiniteSets, TLC, Ax25DataLinkTables

\* An absent number: the N(r)/N(s) of a frame that carries none, an unstaged
\* register, an unassigned X.
None == -1

\* The interpreter's cap on one loop region's iterations.
MaxLoopIterations == 4096

(***************************************************************************)
(* Values                                                                  *)
(***************************************************************************)

\* An I-frame queue entry (QueueEntry). Fresh data has old = None; a frame
\* re-queued for retransmission carries the N(s) it was first sent with and
\* the retained payload, "" when the station holds no copy.
QEntry(lbl, old, data) == [lbl |-> lbl, old |-> old, data |-> data]
NoQEntry == QEntry("", None, "")

\* A frame on the air (Frame.cs): type, command (else response), P/F, N(r),
\* N(s) and the payload label ("" when it carries none).
Frm(type, cmd, pf, nr, ns, data) == [type |-> type, cmd |-> cmd, pf |-> pf, nr |-> nr, ns |-> ns, data |-> data]
NoFrm == Frm("", FALSE, FALSE, None, None, "")

\* One effect of a dispatch, in emission order: a frame, a primitive to
\* layer 3 (DL_DATA_indication carries the delivered label), or a request
\* to the link multiplexer. Internal effects are not recorded: nothing reads
\* them.
FrameFx(f) == [kind |-> "frame", frame |-> f, prim |-> "", detail |-> ""]
UpperFx(p, d) == [kind |-> "dl", frame |-> NoFrm, prim |-> p, detail |-> d]
LmFx(p) == [kind |-> "lm", frame |-> NoFrm, prim |-> p, detail |-> ""]

\* One injected stimulus (EventInput). pf and cmd are 1, 0 or None (the
\* stimulus is not a frame); able and info are the two environment atoms.
Event(ev) == [ev |-> ev, pf |-> None, cmd |-> None, nr |-> None, ns |-> None, data |-> "", able |-> TRUE, info |-> TRUE]

\* Finite maps from a sequence number to a payload label, kept as sets of
\* pairs so that equal maps are equal values whatever built them.
Has(map, key) == \E pr \in map : pr[1] = key
Get(map, key) == (CHOOSE pr \in map : pr[1] = key)[2]
Put(map, key, val) == {pr \in map : pr[1] # key} \cup {<<key, val>>}
Del(map, key) == {pr \in map : pr[1] # key}

\* The label of a re-queued or saved frame that has none of its own.
NsLabel(num) == "I(ns=" \o ToString(num) \o ")"

\* The persistent machine state: DataLinkMachine's modelled fields (the
\* explorer's machine fingerprint). q is the I-frame queue, head first;
\* saved the out-of-sequence receive buffer; ret the retained send history.
NewMachine(st) ==
    [st |-> st, vs |-> 0, vr |-> 0, va |-> 0, rc |-> 0, mod |-> 8, k |-> 4, n2 |-> 10,
     v22 |-> FALSE, srej |-> FALSE, hd |-> FALSE, l3 |-> FALSE, own |-> FALSE, peer |-> FALSE,
     rej |-> FALSE, srejx |-> 0, ack |-> FALSE, t1 |-> "stopped", t3 |-> "stopped",
     q |-> <<>>, saved |-> {}, ret |-> {}]

\* The dispatch context: the machine plus the per-dispatch scratch the
\* interpreter resets on every Dispatch. pP/pF/pNr/pNs are the pending TX
\* registers, x the figc4.7 X, qi the head-insert index, pop the popped
\* queue head (hasPop), dd the label the next DL_DATA_indication carries,
\* fx the effects, err the error (the interpreter would have thrown), tid
\* the transition that fired, arms every table row the dispatch ran (the
\* transition as "State/id" and each subroutine path as "Subroutine:id"),
\* for coverage.
Begin(m, e) ==
    [st |-> m.st, vs |-> m.vs, vr |-> m.vr, va |-> m.va, rc |-> m.rc, mod |-> m.mod, k |-> m.k, n2 |-> m.n2,
     v22 |-> m.v22, srej |-> m.srej, hd |-> m.hd, l3 |-> m.l3, own |-> m.own, peer |-> m.peer,
     rej |-> m.rej, srejx |-> m.srejx, ack |-> m.ack, t1 |-> m.t1, t3 |-> m.t3,
     q |-> m.q, saved |-> m.saved, ret |-> m.ret,
     pP |-> 0, pF |-> 0, pNr |-> None, pNs |-> None, x |-> None, qi |-> 0,
     hasPop |-> FALSE, pop |-> NoQEntry, dd |-> e.data, fx |-> <<>>, err |-> "", tid |-> "", arms |-> {}]

Persist(c) ==
    [st |-> c.st, vs |-> c.vs, vr |-> c.vr, va |-> c.va, rc |-> c.rc, mod |-> c.mod, k |-> c.k, n2 |-> c.n2,
     v22 |-> c.v22, srej |-> c.srej, hd |-> c.hd, l3 |-> c.l3, own |-> c.own, peer |-> c.peer,
     rej |-> c.rej, srejx |-> c.srejx, ack |-> c.ack, t1 |-> c.t1, t3 |-> c.t3,
     q |-> c.q, saved |-> c.saved, ret |-> c.ret]

\* Record an error; the first one wins and every later step passes through.
Fail(c, msg) == IF c.err = "" THEN [c EXCEPT !.err = msg] ELSE c

\* Forward distance round the sequence-number circle, at the current modulus.
Dist(c, from, to) == (to - from) % c.mod

(***************************************************************************)
(* Guard atoms (EvaluateAtom)                                              *)
(***************************************************************************)

FrameType(e) ==
    CASE e.ev = "I_received" -> "I"
      [] e.ev = "RR_received" -> "RR"
      [] e.ev = "RNR_received" -> "RNR"
      [] e.ev = "REJ_received" -> "REJ"
      [] e.ev = "SREJ_received" -> "SREJ"
      [] e.ev = "UA_received" -> "UA"
      [] e.ev = "DM_received" -> "DM"
      [] e.ev = "DISC_received" -> "DISC"
      [] e.ev = "SABM_received" -> "SABM"
      [] e.ev = "SABME_received" -> "SABME"
      [] e.ev = "FRMR_received" -> "FRMR"
      [] e.ev = "UI_received" -> "UI"
      [] e.ev = "XID_response_received" -> "XID"
      [] OTHER -> ""

\* Command (1) or response (0); the fixed role of a frame type when the
\* event does not say; None when neither applies.
Cmd(e) ==
    IF e.cmd # None THEN e.cmd
    ELSE IF FrameType(e) \in {"SABM", "SABME", "DISC", "I"} THEN 1
    ELSE IF FrameType(e) \in {"UA", "DM", "FRMR"} THEN 0
    ELSE None

BoundAtoms ==
    {"F_eq_1", "P_eq_1", "P_or_F_eq_1", "command", "response", "command_and_P_eq_1", "response_and_F_eq_1",
     "F_eq_1_and_frame_eq_RR_or_frame_eq_RNR_or_frame_eq_I",
     "nr_eq_va", "nr_eq_vs", "ns_eq_vr", "ns_gt_vr_plus_1", "vr_lt_ns_lt_vr_plus_k", "va_le_nr_le_vs",
     "vs_eq_nr", "vs_eq_va", "vs_eq_va_plus_k", "vs_eq_X",
     "RC_eq_0", "RC_eq_N2", "T1_expired", "T1_running", "ack_pending", "layer_3_initiated", "mod_128", "mod_8",
     "own_receiver_busy", "peer_receiver_busy", "reject_exception", "sreject_exception_gt_0", "SREJ_enabled",
     "version_2_2", "out_of_sequence_frames_in_receive_buffer", "vr_I_frame_stored",
     "able_to_establish", "info_field_length_le_N1_and_content_is_octet_aligned"}

\* Why an atom cannot be answered for this event, or "" when it can (the
\* interpreter's Require* throws). Mirrors its short-circuits: the compound
\* role atoms consult P/F only when the role matches.
AtomError(c, e, a) ==
    LET need(what) == "guard atom '" \o a \o "' consulted, but the event " \o e.ev \o " does not set " \o what
    IN  CASE a = "RC_eq_NM201" -> "atom RC_eq_NM201 belongs to the management_data_link machine, which is not modelled"
          [] a \notin BoundAtoms -> "guard atom '" \o a \o "' has no binding"
          [] a \in {"F_eq_1", "P_eq_1", "P_or_F_eq_1"} /\ e.pf = None -> need("pf")
          [] a \in {"command", "response", "command_and_P_eq_1", "response_and_F_eq_1"} /\ Cmd(e) = None -> need("command")
          [] a = "command_and_P_eq_1" /\ Cmd(e) = 1 /\ e.pf = None -> need("pf")
          [] a = "response_and_F_eq_1" /\ Cmd(e) = 0 /\ e.pf = None -> need("pf")
          [] a \in {"nr_eq_va", "nr_eq_vs", "va_le_nr_le_vs", "vs_eq_nr"} /\ e.nr = None -> need("nr")
          [] a \in {"ns_eq_vr", "ns_gt_vr_plus_1", "vr_lt_ns_lt_vr_plus_k"} /\ e.ns = None -> need("ns")
          [] a = "vs_eq_X" /\ c.x = None -> "vs_eq_X consulted but X was never assigned"
          [] OTHER -> ""

\* The value of an atom that AtomError says can be answered.
AtomValue(c, e, a) ==
    CASE a \in {"F_eq_1", "P_eq_1", "P_or_F_eq_1"} -> e.pf = 1
      [] a = "command" -> Cmd(e) = 1
      [] a = "response" -> Cmd(e) = 0
      [] a = "command_and_P_eq_1" -> Cmd(e) = 1 /\ e.pf = 1
      [] a = "response_and_F_eq_1" -> Cmd(e) = 0 /\ e.pf = 1
      \* Enquiry_Response's head decision: the F parameter the caller staged
      \* and the received frame's type.
      [] a = "F_eq_1_and_frame_eq_RR_or_frame_eq_RNR_or_frame_eq_I" -> c.pF = 1 /\ FrameType(e) \in {"RR", "RNR", "I"}
      [] a = "nr_eq_va" -> e.nr = c.va
      [] a = "nr_eq_vs" -> e.nr = c.vs
      [] a = "ns_eq_vr" -> e.ns = c.vr
      [] a = "ns_gt_vr_plus_1" -> Dist(c, c.vr, e.ns) > 1
      \* packethacking/ax25spec#40: V(r) < N(s) < V(r)+k, open, modular.
      [] a = "vr_lt_ns_lt_vr_plus_k" -> Dist(c, c.vr, e.ns) > 0 /\ Dist(c, c.vr, e.ns) < c.k
      [] a = "va_le_nr_le_vs" -> Dist(c, c.va, e.nr) <= Dist(c, c.va, c.vs)
      \* The Resolver's stale-read substitution: V(s) = the received N(r).
      [] a = "vs_eq_nr" -> c.vs = e.nr
      [] a = "vs_eq_va" -> c.vs = c.va
      [] a = "vs_eq_va_plus_k" -> c.vs = (c.va + c.k) % c.mod
      [] a = "vs_eq_X" -> c.vs = c.x
      [] a = "RC_eq_0" -> c.rc = 0
      [] a = "RC_eq_N2" -> c.rc = c.n2
      [] a = "T1_expired" -> c.t1 = "expired"
      [] a = "T1_running" -> c.t1 = "running"
      [] a = "ack_pending" -> c.ack
      [] a = "layer_3_initiated" -> c.l3
      [] a = "mod_128" -> c.mod = 128
      [] a = "mod_8" -> c.mod = 8
      [] a = "own_receiver_busy" -> c.own
      [] a = "peer_receiver_busy" -> c.peer
      [] a = "reject_exception" -> c.rej
      [] a = "sreject_exception_gt_0" -> c.srejx > 0
      [] a = "SREJ_enabled" -> c.srej
      [] a = "version_2_2" -> c.v22
      [] a = "out_of_sequence_frames_in_receive_buffer" -> c.saved # {}
      [] a = "vr_I_frame_stored" -> Has(c.saved, c.vr)
      \* The environment atoms: the event supplies them (default true).
      [] a = "able_to_establish" -> e.able
      [] a = "info_field_length_le_N1_and_content_is_octet_aligned" -> e.info

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
(* signal_upper, signal_lower, internal_out                                *)
(***************************************************************************)

SignalUpper(c, e, verb) ==
    [c EXCEPT !.fx = Append(@, UpperFx(verb, IF verb = "DL_DATA_indication" THEN c.dd ELSE ""))]

EmitU(c, type, cmd, pf) == [c EXCEPT !.fx = Append(@, FrameFx(Frm(type, cmd, pf, None, None, "")))]

\* A supervisory frame consumes the staged N(r); none staged means the table
\* omitted the staging box (pinned semantic 3).
EmitS(c, type, cmd, pf, verb) ==
    IF c.pNr = None
    THEN Fail(c, "'" \o verb \o "' emitted without a preceding N(r) staging assignment: the table omits 'N(r) := ...' on this path")
    ELSE [c EXCEPT !.fx = Append(@, FrameFx(Frm(type, cmd, pf, c.pNr, None, "")))]

SignalLower(c, e, verb) ==
    CASE verb \in {"LM_seize_request", "LM_release_request", "LM_data_request"} -> [c EXCEPT !.fx = Append(@, LmFx(verb))]
      [] verb = "UA" -> EmitU(c, "UA", FALSE, c.pF = 1)
      [] verb = "Expedited UA" -> EmitU(c, "UA", FALSE, c.pF = 1)
      [] verb = "DM" -> EmitU(c, "DM", FALSE, c.pF = 1)
      [] verb = "DM (F = 1)" -> EmitU(c, "DM", FALSE, TRUE)
      [] verb = "DM Response (F = 0)" -> EmitU(c, "DM", FALSE, FALSE)
      [] verb = "Expedited DM" -> EmitU(c, "DM", FALSE, c.pF = 1)
      [] verb = "SABM" -> EmitU(c, "SABM", TRUE, c.pP = 1)
      [] verb = "SABM (P == 1)" -> EmitU(c, "SABM", TRUE, TRUE)
      [] verb = "SABME" -> EmitU(c, "SABME", TRUE, c.pP = 1)
      [] verb = "SABME (P = 1)" -> EmitU(c, "SABME", TRUE, TRUE)
      [] verb = "DISC (P = 1)" -> EmitU(c, "DISC", TRUE, TRUE)
      [] verb = "UI Command" -> EmitU(c, "UI", TRUE, c.pP = 1)
      [] verb = "XID_command" -> EmitU(c, "XID", TRUE, c.pP = 1)
      \* A fresh I frame: staged N(s) and N(r), the popped payload; the copy
      \* is retained for any later retransmission (pinned semantic 1).
      [] verb = "I Command" ->
            IF c.pNs = None
            THEN Fail(c, "'I Command' emitted without a preceding N(s) staging assignment: the table omits 'N(s) := ...' on this path")
            ELSE LET data == IF c.hasPop /\ c.pop.data # "" THEN c.pop.data ELSE IF e.data # "" THEN e.data ELSE "frame"
                 IN  IF c.pNr = None
                     THEN Fail(c, "'I Command' emitted without a preceding N(r) staging assignment: the table omits 'N(r) := ...' on this path")
                     ELSE [c EXCEPT !.fx = Append(@, FrameFx(Frm("I", TRUE, c.pP = 1, c.pNr, c.pNs, data))),
                                    !.ret = Put(@, c.pNs, data)]
      [] verb \in {"RR", "RR Response"} -> EmitS(c, "RR", FALSE, c.pF = 1, verb)
      [] verb = "RR Command" -> EmitS(c, "RR", TRUE, c.pP = 1, verb)
      [] verb = "RR Command (P = 0)" -> EmitS(c, "RR", TRUE, FALSE, verb)
      [] verb \in {"RNR", "RNR Response"} -> EmitS(c, "RNR", FALSE, c.pF = 1, verb)
      [] verb = "RNR Response (F = 0)" -> EmitS(c, "RNR", FALSE, FALSE, verb)
      [] verb = "RNR Command" -> EmitS(c, "RNR", TRUE, c.pP = 1, verb)
      [] verb = "REJ" -> EmitS(c, "REJ", FALSE, c.pF = 1, verb)
      [] verb = "SREJ" -> EmitS(c, "SREJ", FALSE, c.pF = 1, verb)
      [] OTHER -> Fail(c, "signal_lower verb '" \o verb \o "' has no frame semantics")

\* Pushes within one dispatch land at the head of the FIFO in their own
\* order, ahead of anything already queued.
QueueInsert(c, entry) ==
    [c EXCEPT !.q = SubSeq(@, 1, c.qi) \o <<entry>> \o SubSeq(@, c.qi + 1, Len(@)), !.qi = @ + 1]

\* Re-queue the retained copy of I(num) for retransmission ("" if none held).
QueueInsertOld(c, num) == QueueInsert(c, QEntry(NsLabel(num), num, IF Has(c.ret, num) THEN Get(c.ret, num) ELSE ""))

PushVerbs == {"Push on I Frame Queue", "Push on I Frame Queue (note: word order?)", "Push I Frame on I Queue", "push_frame_on_queue"}

InternalOut(c, e, verb) ==
    CASE verb \in PushVerbs ->
            \* Fresh data from layer 3 joins the tail; a popped frame pushed
            \* back (peer busy, window full) returns to the head unchanged.
            LET data == IF e.data # "" THEN e.data ELSE "frame"
                entry == IF e.data = "" /\ c.hasPop THEN c.pop ELSE QEntry(data, None, data)
            IN  IF e.ev = "DL_DATA_request" THEN [c EXCEPT !.q = Append(@, entry)] ELSE QueueInsert(c, entry)
      \* Invoke_Retransmission's loop body: the frame numbered by the rewound V(s).
      [] verb = "Push Old I Frame onto Queue" -> QueueInsertOld(c, c.vs)
      [] verb = "Push Old I Frame N(r) on Queue" ->
            IF e.nr = None THEN Fail(c, "'" \o verb \o "' needs N(r), but the event does not set nr") ELSE QueueInsertOld(c, e.nr)
      [] verb = "MDL-NEGOTIATE Request" -> c
      [] OTHER -> Fail(c, "internal_out verb '" \o verb \o "' has no semantics")

(***************************************************************************)
(* processing                                                              *)
(***************************************************************************)

NeedNr(c, e, verb, upd) == IF e.nr = None THEN Fail(c, "'" \o verb \o "' needs N(r), but the event does not set nr") ELSE upd
NeedNs(c, e, verb, upd) == IF e.ns = None THEN Fail(c, "'" \o verb \o "' needs N(s), but the event does not set ns") ELSE upd

\* Verbs with no effect on the modelled state: N1 and the symbolic timer
\* registers, which nothing reads, and the boxes the figures draw for a
\* frame or primitive that is simply not acted on. "Backtrack" is the
\* send-history rewind, whose observable behaviour is the queue pushes.
NoOpVerbs ==
    {"N1 := 2048", "SRT := Initial Default",
     "SRT := 7(SRT)/8 + (T1)/8 - (Remaining Time on T1 When Last Stopped)/8",
     "T1V := 2 * SRT", "T2 := 3000", "Next T1 := 2 * SRT", "Next T1 := (RC*0.25)+SRT*2",
     "Discard Contents of I Frame", "Discard I Frame", "Discard Primitive", "Backtrack"}

Processing(c, e, verb) ==
    CASE verb \in NoOpVerbs -> c
      \* Pending TX registers.
      [] verb = "F := 0" -> [c EXCEPT !.pF = 0]
      [] verb = "F := 1" -> [c EXCEPT !.pF = 1]
      [] verb = "F := P" -> IF e.pf = None THEN Fail(c, "'F := P' needs pf, but the event does not set it") ELSE [c EXCEPT !.pF = e.pf]
      [] verb = "P := 0" -> [c EXCEPT !.pP = 0]
      [] verb = "P := 1" -> [c EXCEPT !.pP = 1]
      [] verb = "N(r) := V(r)" -> [c EXCEPT !.pNr = c.vr]
      [] verb = "N(r) := N(s)" -> NeedNs(c, e, verb, [c EXCEPT !.pNr = e.ns])
      [] verb = "N(s) := V(s)" -> [c EXCEPT !.pNs = c.vs]
      \* Sequence variables, modulo the current modulus.
      [] verb = "V(s) := 0" -> [c EXCEPT !.vs = 0]
      [] verb = "V(s) := N(r)" -> NeedNr(c, e, verb, [c EXCEPT !.vs = e.nr])
      [] verb = "V(s) := V(s) + 1" -> [c EXCEPT !.vs = (@ + 1) % c.mod]
      [] verb = "V(r) := 0" -> [c EXCEPT !.vr = 0]
      [] verb = "V(r) := V(r) + 1" -> [c EXCEPT !.vr = (@ + 1) % c.mod]
      [] verb = "V(r) := V(r) - 1" -> [c EXCEPT !.vr = (@ - 1) % c.mod]
      [] verb = "V(a) := 0" -> [c EXCEPT !.va = 0]
      [] verb = "V(a) := N(r)" -> NeedNr(c, e, verb, [c EXCEPT !.va = e.nr])
      [] verb = "X := V(s)" -> [c EXCEPT !.x = c.vs]
      \* Retry counter.
      [] verb = "RC := 0" -> [c EXCEPT !.rc = 0]
      [] verb = "RC := 1" -> [c EXCEPT !.rc = 1]
      [] verb = "RC := RC + 1" -> [c EXCEPT !.rc = @ + 1]
      \* Timers, symbolic (pinned semantic 8).
      [] verb = "Start T1" -> [c EXCEPT !.t1 = "running"]
      [] verb = "Stop T1" -> [c EXCEPT !.t1 = "stopped"]
      [] verb = "Start T3" -> [c EXCEPT !.t3 = "running"]
      [] verb = "Stop T3" -> [c EXCEPT !.t3 = "stopped"]
      \* Flags.
      [] verb = "Set Layer 3 Initiated" -> [c EXCEPT !.l3 = TRUE]
      [] verb = "Clear Layer 3 Initiated" -> [c EXCEPT !.l3 = FALSE]
      [] verb = "set_peer_receiver_busy" -> [c EXCEPT !.peer = TRUE]
      [] verb = "clear_peer_receiver_busy" -> [c EXCEPT !.peer = FALSE]
      [] verb = "Set Own Receiver Busy" -> [c EXCEPT !.own = TRUE]
      [] verb = "Clear Own Receiver Busy" -> [c EXCEPT !.own = FALSE]
      [] verb = "Set Reject Exception" -> [c EXCEPT !.rej = TRUE]
      [] verb \in {"Clear Reject Exception", "Clear Reject Condition"} -> [c EXCEPT !.rej = FALSE]
      [] verb = "Clear Sreject Condition" -> [c EXCEPT !.srejx = 0]
      [] verb \in {"Increment Sreject Exception", "Sreject := Sreject + 1"} -> [c EXCEPT !.srejx = @ + 1]
      [] verb = "Decrement Sreject Exception if > 0" -> [c EXCEPT !.srejx = IF @ > 0 THEN @ - 1 ELSE @]
      [] verb = "set_acknowledge_pending" -> [c EXCEPT !.ack = TRUE]
      [] verb = "Clear Acknowledge Pending" -> [c EXCEPT !.ack = FALSE]
      \* Version and parameters.
      [] verb = "Set Version 2.2" -> [c EXCEPT !.v22 = TRUE]
      [] verb = "set_version_2_0" -> [c EXCEPT !.v22 = FALSE]
      [] verb = "Set Half Duplex" -> [c EXCEPT !.hd = TRUE]
      [] verb = "Set Selective Reject" -> [c EXCEPT !.srej = TRUE]
      [] verb = "Set Implicit Reject" -> [c EXCEPT !.srej = FALSE]
      [] verb = "Modulo := 8" -> [c EXCEPT !.mod = 8]
      [] verb = "Modulo := 128" -> [c EXCEPT !.mod = 128]
      [] verb = "k := 8" -> [c EXCEPT !.k = 8]
      [] verb = "k := 32" -> [c EXCEPT !.k = 32]
      [] verb = "N2 := 10" -> [c EXCEPT !.n2 = 10]
      \* Queue and receive buffer.
      [] verb \in {"discard_I_frame_queue", "discard_frame_queue", "Discard Queue", "Discard I Queue Entries"} -> [c EXCEPT !.q = <<>>]
      [] verb = "Save Contents of I Frame" ->
            NeedNs(c, e, verb, [c EXCEPT !.saved = Put(@, e.ns, IF e.data # "" THEN e.data ELSE NsLabel(e.ns))])
      \* The retrieved frame is what the next DL_DATA_indication delivers.
      [] verb = "Retrieve Stored V(r) I Frame" ->
            IF ~Has(c.saved, c.vr)
            THEN Fail(c, "Retrieve Stored V(r) I Frame: no stored frame with ns=" \o ToString(c.vr))
            ELSE [c EXCEPT !.dd = Get(c.saved, c.vr), !.saved = Del(@, c.vr)]
      [] OTHER -> Fail(c, "processing verb '" \o verb \o "' has no semantics")

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

\* Enquiry Response takes an F parameter, carried by the call-site label and
\* staged in the pending F register before the call.
BeforeCall(c, label) ==
    CASE label = "Enquiry_Response_F_1" -> [c EXCEPT !.pF = 1]
      [] label = "Enquiry Response (F = 0)" -> [c EXCEPT !.pF = 0]
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

Invoke(c, e, label) == CallSubroutine(BeforeCall(c, label), e, SubroutineFor(label))

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
(* Around the table (Dispatch)                                             *)
(***************************************************************************)

\* Reset the scratch, expire the timer the event names (a timer that is not
\* running cannot expire), and pop the queue head for I_frame_pops_off_queue.
Prepare(m, e) ==
    LET c0 == Begin(m, e)
        c1 == CASE e.ev = "T1_expiry" ->
                      IF c0.t1 # "running" THEN Fail(c0, "T1_expiry injected but T1 is " \o c0.t1 \o ": a stopped timer cannot expire")
                      ELSE [c0 EXCEPT !.t1 = "expired"]
                [] e.ev = "T3_expiry" ->
                      IF c0.t3 # "running" THEN Fail(c0, "T3_expiry injected but T3 is " \o c0.t3 \o ": a stopped timer cannot expire")
                      ELSE [c0 EXCEPT !.t3 = "expired"]
                [] OTHER -> c0
    IN  IF c1.err = "" /\ e.ev = "I_frame_pops_off_queue" /\ c1.q # <<>>
        THEN [c1 EXCEPT !.hasPop = TRUE, !.pop = Head(c1.q), !.q = Tail(c1.q)]
        ELSE c1

\* Pinned semantic 1: a re-queued old frame popping in an information-
\* transfer state is re-emitted with its original N(s), not renumbered by
\* the table's fresh-frame path.
RetransmitsOldFrame(c) == c.hasPop /\ c.pop.old # None /\ c.st \in {"Connected", "TimerRecovery"}

\* Keep only the retained frames still outstanding: N(s) in [V(a), V(s)).
Prune(c) == [c EXCEPT !.ret = {pr \in @ : Dist(c, c.va, pr[1]) < Dist(c, c.va, c.vs)}]

RetransmitOldFrame(c) ==
    IF c.pop.data = ""
    THEN Fail(c, "retransmission of I(ns=" \o ToString(c.pop.old) \o ") requested, but this station holds no copy of that frame: it was never sent, or it has already been acknowledged (the reject that named it was incoherent)")
    ELSE LET c1 == [c EXCEPT !.fx = Append(@, FrameFx(Frm("I", TRUE, FALSE, c.vr, c.pop.old, c.pop.data))),
                             !.ack = FALSE, !.tid = "pinned:retransmit_old_i_frame"]
             c2 == IF c1.t1 # "running" THEN [c1 EXCEPT !.t3 = "stopped", !.t1 = "running"] ELSE c1
         IN  Prune(c2)

\* After the transition: prune the send history (unless the dispatch failed).
Settle(c) == IF c.err # "" THEN c ELSE Prune(c)

\* The context after dispatching event e on machine m: Persist(...) is the
\* machine after, fx its effects in order, tid the transition that fired,
\* and err non-empty when the interpreter would have thrown.
Dispatch(m, e) ==
    LET c0 == Prepare(m, e)
    IN  IF c0.err # "" THEN c0
        ELSE IF RetransmitsOldFrame(c0) THEN RetransmitOldFrame(c0)
        ELSE Settle(DispatchState(c0, e))

=============================================================================
