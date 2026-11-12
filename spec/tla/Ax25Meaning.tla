---------------------------- MODULE Ax25Meaning ----------------------------
(***************************************************************************)
(* Hand-written. What the action verbs, guard atoms and signals of the     *)
(* generated Data Link tables mean: the default meaning module. It is a    *)
(* port of the reference interpreter, codegen/src/Packet.Sdl.Interpreter/  *)
(* Machine.cs, arm by arm, with the same policies. How the tables are run  *)
(* (guard selection, loops, subroutine calls) is Ax25Runner.tla's, which   *)
(* calls this module only through the interface below. Ax25Semantics.tla   *)
(* puts the two together; docs/tla.md says how to select another meaning.  *)
(*                                                                         *)
(* THE INTERFACE                                                           *)
(*                                                                         *)
(* A replacement meaning module is written from this header and the AX.25  *)
(* v2.2 text alone. It EXTENDS Ax25Values (the shared value shapes: None,  *)
(* Frm, NoFrm, FrameFx, UpperFx, LmFx, Event, Has/Get/Put/Del, Fail) and   *)
(* must define every operator listed under "Operators", with the arity     *)
(* shown. It must not define any name that Ax25Values, Ax25Runner or       *)
(* Ax25DataLinkTables defines (Ax25Semantics.tla would not parse).         *)
(*                                                                         *)
(* Inputs it is given.                                                     *)
(*                                                                         *)
(*  The event e, a record built by the harness (Ax25Values.Event):         *)
(*    ev     the event name, as the tables' page columns spell it          *)
(*           (I_received, RR_received, ..., DL_DATA_request, T1_expiry,    *)
(*           I_frame_pops_off_queue, the catch-alls all_other_commands,    *)
(*           i_or_s_command_received, all_other_primitives__from_lower_    *)
(*           layer and __from_upper_layer, the frame-error inputs          *)
(*           control_field_error, info_not_permitted_in_frame,             *)
(*           u_or_s_frame_length_error);                                   *)
(*    pf     the received frame's P/F bit, 1 or 0; None if not a frame;    *)
(*    cmd    1 command, 0 response, None when the stimulus does not say;   *)
(*    nr, ns the received N(r), N(s), None when the frame carries none;    *)
(*    data   the payload label: of the received I or UI frame, or of the   *)
(*           DL-DATA / DL-UNIT-DATA Request; "" for none;                  *)
(*    able   the environment's answer to able_to_establish (default TRUE); *)
(*    info   the environment's answer to "info field length <= N1 and      *)
(*           octet aligned" (default TRUE);                                *)
(*    ftype, fe  for the harness only; a meaning should not read them.     *)
(*                                                                         *)
(*  Verb and atom spellings are the tables' (Box(kind, verb), Is(atom) in  *)
(*  Ax25DataLinkTables.tla; the catalogues spec-sdl/actions.yaml and       *)
(*  predicates.yaml). A meaning must Fail on a verb or atom it does not    *)
(*  bind, never leave it undefined: the interpreter throws there too.      *)
(*                                                                         *)
(* The station record (what persists between dispatches, and so is part    *)
(* of the model's state). Fields the harness reads or seeds are fixed;     *)
(* their names, values and representations below are part of the           *)
(* interface. A meaning may add fields of its own (this one adds hd, peer, *)
(* rej, ack, ret), but every field is state, so extra fields can change    *)
(* the state count without changing any verdict.                           *)
(*                                                                         *)
(*    st     the Data Link state, a page name of Ax25DataLinkTables:       *)
(*           Disconnected, AwaitingConnection, AwaitingV22Connection,      *)
(*           Connected, TimerRecovery, AwaitingRelease. The runner moves   *)
(*           it; a meaning only reads it.                                  *)
(*    vs     V(S), send state variable          } integers in              *)
(*    vr     V(R), receive state variable       } 0 .. mod - 1             *)
(*    va     V(A), acknowledge state variable   }                          *)
(*    rc     RC, the retry count (an integer)                              *)
(*    mod    the modulus, 8 or 128                                         *)
(*    k      k, the window size                                            *)
(*    n2     N2, the maximum number of retries                             *)
(*    v22    the version 2.2 flag (Set Version 2.2)                        *)
(*    srej   SREJ enabled (Set Selective Reject / Set Implicit Reject)     *)
(*    l3     the Layer 3 Initiated flag                                    *)
(*    own    Own Receiver Busy                                             *)
(*    srejx  Selective Reject Exception, a count (sreject_exception_gt_0)  *)
(*    t1, t3 timers T1 and T3: "stopped", "running" or "expired"           *)
(*    q      the I frame queue: a sequence, head first. Its entries are    *)
(*           the meaning's own; the runner pops the head (see hasPop) and  *)
(*           the harness only asks whether q is empty.                     *)
(*    saved  the receive buffer of out-of-sequence I frames: a set of      *)
(*           <<N(s), label>> pairs, one per N(s) (the harness asks         *)
(*           Has(saved, n)).                                               *)
(*  This module's own fields: hd half duplex, peer Peer Receiver Busy,     *)
(*  rej Reject Exception, ack Acknowledge Pending, ret the retained send   *)
(*  history (pinned semantic 1).                                           *)
(*                                                                         *)
(* The dispatch context c: the station record plus per-dispatch scratch.   *)
(* Fields the runner owns, which Begin must create with these values and   *)
(* which no meaning operator may write (except tid by Preempt):            *)
(*    err    "" or the error the interpreter would have thrown; set only   *)
(*           through Fail, so the first error wins. The runner calls no    *)
(*           meaning operator on a context whose err is set, so operators  *)
(*           may assume err = "" on entry.                                 *)
(*    tid    "", the transition that fired;                                *)
(*    arms   {}, the table rows run (coverage);                            *)
(*    hasPop FALSE; pop: any value. On I_frame_pops_off_queue with a       *)
(*           non-empty q the runner sets hasPop, pop = Head(q), q =        *)
(*           Tail(q) after Arrive and before the table runs.               *)
(*  Fields the harness reads after a dispatch:                             *)
(*    fx     the dispatch's effects in emission order, built only with     *)
(*           FrameFx, UpperFx and LmFx: every frame put on the air         *)
(*           (Frm(type, cmd, pf, nr, ns, data), type one of I RR RNR REJ   *)
(*           SREJ SABM SABME DISC UA DM FRMR UI XID, cmd TRUE for a        *)
(*           command, pf TRUE for P or F = 1, nr/ns None when absent, data *)
(*           the payload label); every signal to layer 3 (UpperFx(verb,    *)
(*           detail), verb as the table spells it, detail the label a      *)
(*           DL_DATA_indication or DL_UNIT_DATA_indication delivers, else  *)
(*           ""); every LM request (LmFx(verb)). The harness checks        *)
(*           delivery by label, so an I frame must carry the label of the  *)
(*           data it sends and an indication the label of what it          *)
(*           delivers.                                                     *)
(*  The rest of the scratch is the meaning's own. This one has pP, pF,     *)
(*  pNr, pNs (the pending P, F, N(r), N(s) of the next frame), x (the X    *)
(*  of figc4.7), qi (where the next head push goes) and dd (the label the  *)
(*  next DL_DATA_indication carries).                                      *)
(*                                                                         *)
(* Operators (each total; c and the result are contexts unless said):      *)
(*                                                                         *)
(*  For the harness:                                                       *)
(*    NewMachine(st)    a station record in state st with the AX.25        *)
(*                      defaults; the harness then sets mod, k, n2, srej,  *)
(*                      v22, rc, l3, t1, t3 with EXCEPT.                   *)
(*    Persist(c)        the station record of a context. Two dispatches    *)
(*                      that leave the station the same must give equal    *)
(*                      records (the harness compares them).               *)
(*  For the runner (Ax25Runner's CONSTANTS):                               *)
(*    Begin(m, e)       a fresh context for event e on station record m.   *)
(*    Arrive(c, e)      what the event itself does before any table row:   *)
(*                      here, a timer expiry marks its timer expired.      *)
(*    AtomError(c, e, a)  "" when guard atom a can be answered now, else   *)
(*                      why not (a string). Called on the live context.    *)
(*    AtomValue(c, e, a)  the truth value of atom a; called only when      *)
(*                      AtomError(c, e, a) = "".                           *)
(*    Processing(c, e, verb)   a processing box (assignments, timers,      *)
(*                      flags, queue and buffer housekeeping). The runner  *)
(*                      also calls it with "F := 1" and "F := 0" to stage  *)
(*                      Enquiry Response's F parameter before the call.    *)
(*    SignalUpper(c, e, verb)  a signal to layer 3 (DL-... Indication,     *)
(*                      Confirm, DL-ERROR Indication (X)).                 *)
(*    SignalLower(c, e, verb)  a frame to the peer, or an LM request.      *)
(*    InternalOut(c, e, verb)  an internal signal: the I frame queue       *)
(*                      pushes, MDL-NEGOTIATE Request.                     *)
(*    Preempts(c, e)    TRUE when the meaning, not the table, handles this *)
(*                      dispatch (checked after the pop); a meaning with   *)
(*                      no such policy defines it FALSE.                   *)
(*    Preempt(c, e)     that handling; it may set tid to name the policy.  *)
(*    AfterTransition(c)  bookkeeping after a transition that did not      *)
(*                      fail (here, pruning the send history).             *)
(*                                                                         *)
(* Pinned semantics (docs/explorer.md "Pinned delegated semantics", docs/  *)
(* golden-traces.md "Interpretation policies"), by who carries them:       *)
(*  Runner policy (Ax25Runner.tla; a meaning cannot change them):          *)
(*    - transition guards are evaluated against the pre-transition         *)
(*      context, subroutine path guards and loop predicates live (3);      *)
(*    - exactly one transition or subroutine path must hold, every guard   *)
(*      is evaluated, terms left to right stopping at the first false;     *)
(*      an atom that cannot be answered is an error only when reached (3); *)
(*    - loop regions are while or do-while, capped at 4096 iterations;     *)
(*    - call-site labels bind through the explicit alias map (3);          *)
(*    - Enquiry Response's F parameter is staged as "F := 1" / "F := 0"    *)
(*      before the call (3);                                               *)
(*    - a processing box "Clear Exception Conditions" calls the subroutine;*)
(*    - I_frame_pops_off_queue consumes the queue head before the table    *)
(*      runs; an empty queue pops nothing (3);                             *)
(*    - no subroutine call-depth limit (docs/tla.md);                      *)
(*    - the first error wins and nothing runs after it;                    *)
(*    - Saves[state] (the figures' Save symbol) is table data the runner   *)
(*      does not consume yet (ax25sdl#97); holding and releasing a saved   *)
(*      input will be runner policy when it does.                          *)
(*  Meaning policy (this module; a replacement chooses its own):           *)
(*    - retransmission (1): a retained copy of every sent I frame, the two *)
(*      push-old verbs re-queue entries carrying the original N(s), and    *)
(*      such an entry popping in Connected or TimerRecovery while the      *)
(*      peer is not busy is re-sent by Preempt with its original N(s),     *)
(*      N(r) = V(r), P = 0, clearing Acknowledge Pending and starting T1   *)
(*      (stopping T3) if T1 is not running; no copy held is an error; the  *)
(*      copies are pruned to [V(a), V(s)) after every dispatch;            *)
(*    - pending P/F/N(r)/N(s) reset every dispatch (P, F to 0, N(r), N(s)  *)
(*      unset); a frame needing an unset N(r) or N(s) is an error (3);     *)
(*    - the queue is a FIFO: DL_DATA_request joins the tail, other pushes  *)
(*      go to the head in their own order, a popped frame pushed back is   *)
(*      unchanged (3);                                                     *)
(*    - Save Contents of I Frame keys the buffer by N(s); Discard Contents *)
(*      of I Frame is a no-op (3);                                         *)
(*    - the environment atoms come from the event, default true (3);       *)
(*    - DL_DATA_indication carries the delivered label; Retrieve Stored    *)
(*      V(r) I Frame sets the label the next one carries (4);              *)
(*    - UI Command carries the DL-UNIT-DATA Request's label and            *)
(*      DL_UNIT_DATA_indication the received UI frame's (16);              *)
(*    - timers are symbolic; an expiry of a timer that is not running is   *)
(*      an error (8);                                                      *)
(*    - the version verbs set the flag only, never the modulus (9);        *)
(*    - vr_lt_ns_lt_vr_plus_k is the open modular interval with the        *)
(*      unclamped k (golden traces, ax25spec#40);                          *)
(*    - N1 and the timer registers SRT, T1V, T2, Next T1 are not modelled; *)
(*      their verbs change nothing.                                        *)
(*  Harness policy (TwoStation.tla; neither module): the catch-all event   *)
(*  routing (5) and the link multiplexer's answer (2), so a meaning only   *)
(*  records an LM request as an effect.                                    *)
(*  The numbers are those of docs/explorer.md.                             *)
(***************************************************************************)
EXTENDS Ax25Values

(***************************************************************************)
(* Values                                                                  *)
(***************************************************************************)

\* An I-frame queue entry (QueueEntry). Fresh data has old = None; a frame
\* re-queued for retransmission carries the N(s) it was first sent with and
\* the retained payload, "" when the station holds no copy.
QEntry(lbl, old, data) == [lbl |-> lbl, old |-> old, data |-> data]
NoQEntry == QEntry("", None, "")

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

\* Forward distance round the sequence-number circle, at the current modulus.
Dist(c, from, to) == (to - from) % c.mod

(***************************************************************************)
(* Frame classification                                                    *)
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

(***************************************************************************)
(* Guard atoms (EvaluateAtom)                                              *)
(***************************************************************************)

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

(***************************************************************************)
(* signal_upper, signal_lower, internal_out                                *)
(***************************************************************************)

SignalUpper(c, e, verb) ==
    [c EXCEPT !.fx = Append(@, UpperFx(verb, IF verb = "DL_DATA_indication" THEN c.dd
                                             ELSE IF verb = "DL_UNIT_DATA_indication" THEN e.data ELSE ""))]

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
      \* The UI frame carries the DL-UNIT-DATA Request's payload label.
      [] verb = "UI Command" -> [c EXCEPT !.fx = Append(@, FrameFx(Frm("UI", TRUE, c.pP = 1, None, None, e.data)))]
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
(* Around the table: the event's own effect, pre-emption, settling         *)
(***************************************************************************)

\* A timer expiry marks its timer expired (a timer that is not running
\* cannot expire); any other event does nothing before the table.
Arrive(c, e) ==
    CASE e.ev = "T1_expiry" ->
            IF c.t1 # "running" THEN Fail(c, "T1_expiry injected but T1 is " \o c.t1 \o ": a stopped timer cannot expire")
            ELSE [c EXCEPT !.t1 = "expired"]
      [] e.ev = "T3_expiry" ->
            IF c.t3 # "running" THEN Fail(c, "T3_expiry injected but T3 is " \o c.t3 \o ": a stopped timer cannot expire")
            ELSE [c EXCEPT !.t3 = "expired"]
      [] OTHER -> c

\* Pinned semantic 1: a re-queued old frame popping in an information-
\* transfer state is re-emitted with its original N(s), not renumbered by
\* the table's fresh-frame path. While the peer is busy it takes the table's
\* peer-busy pop arm, which pushes it back (coverage batch 4).
Preempts(c, e) == c.hasPop /\ c.pop.old # None /\ c.st \in {"Connected", "TimerRecovery"} /\ ~c.peer

\* Keep only the retained frames still outstanding: N(s) in [V(a), V(s)).
Prune(c) == [c EXCEPT !.ret = {pr \in @ : Dist(c, c.va, pr[1]) < Dist(c, c.va, c.vs)}]

Preempt(c, e) ==
    IF c.pop.data = ""
    THEN Fail(c, "retransmission of I(ns=" \o ToString(c.pop.old) \o ") requested, but this station holds no copy of that frame: it was never sent, or it has already been acknowledged (the reject that named it was incoherent)")
    ELSE LET c1 == [c EXCEPT !.fx = Append(@, FrameFx(Frm("I", TRUE, FALSE, c.vr, c.pop.old, c.pop.data))),
                             !.ack = FALSE, !.tid = "pinned:retransmit_old_i_frame"]
             c2 == IF c1.t1 # "running" THEN [c1 EXCEPT !.t3 = "stopped", !.t1 = "running"] ELSE c1
         IN  Prune(c2)

\* After a transition: prune the send history.
AfterTransition(c) == Prune(c)

=============================================================================
