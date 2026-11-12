------------------------- MODULE LockstepSemantics -------------------------
(***************************************************************************)
(* Hand-written. Two meanings side by side (TlaCheck --lockstep,           *)
(* docs/tla.md "Lockstep"). TlaCheck writes this module into the model     *)
(* directory as Ax25Semantics.tla, with the MB line naming the second      *)
(* meaning, so the harness (TwoStation.tla) extends it unchanged.          *)
(*                                                                         *)
(* The runner is instanced twice: RA with the default meaning (MA), which  *)
(* drives the system, and RB with the other (MB), which shadows it. The    *)
(* station record is MA's with one extra field, lsB, holding MB's record.  *)
(* Every dispatch runs through both. Before it, MB's fixed fields other    *)
(* than q are set from MA's: they are equal after every dispatch that      *)
(* agreed, so this only carries over what the harness wrote itself (the    *)
(* seed, the local busy condition). After it, an observable projection of  *)
(* the two results is compared:                                            *)
(*   - the error flag (did the dispatch fail), and if neither failed       *)
(*   - the effects fx (frames with all their fields, primitives to layer 3 *)
(*     with their labels, LM requests), in order;                          *)
(*   - every fixed field of MeaningInterface.md except q, the next state   *)
(*     included;                                                           *)
(*   - the length of q (its entries are each meaning's own).               *)
(* Not compared: tid, arms, and fields private to a meaning.               *)
(*                                                                         *)
(* A difference fails the dispatch with an error that starts               *)
(* "lockstep{SIG}: ", SIG naming the station's state, the event and the    *)
(* components that differ; the harness reports it as a MachineError and    *)
(* Lockstep.tla's invariant stops TLC on it. A SIG in LockstepKnown is not *)
(* an error: the dispatch goes on with MA's result, and MB's fixed fields  *)
(* (q aside) are reset to MA's, so the search can look past it. TlaCheck   *)
(* fills LockstepKnown only while it iterates one row; the committed       *)
(* default is empty.                                                       *)
(***************************************************************************)
EXTENDS Ax25Values

CONSTANT LockstepKnown

MA == INSTANCE Ax25Meaning
MB == INSTANCE Ax25MeaningIndependent

RA == INSTANCE Ax25Runner WITH
    Begin <- MA!Begin, Arrive <- MA!Arrive, AtomError <- MA!AtomError, AtomValue <- MA!AtomValue,
    Processing <- MA!Processing, SignalUpper <- MA!SignalUpper, SignalLower <- MA!SignalLower,
    InternalOut <- MA!InternalOut, Preempts <- MA!Preempts, Preempt <- MA!Preempt,
    AfterTransition <- MA!AfterTransition
RB == INSTANCE Ax25Runner WITH
    Begin <- MB!Begin, Arrive <- MB!Arrive, AtomError <- MB!AtomError, AtomValue <- MB!AtomValue,
    Processing <- MB!Processing, SignalUpper <- MB!SignalUpper, SignalLower <- MB!SignalLower,
    InternalOut <- MB!InternalOut, Preempts <- MB!Preempts, Preempt <- MB!Preempt,
    AfterTransition <- MB!AfterTransition

\* The table and save policy is the runner's, the same in both instances.
Handles(st, ev) == RA!Handles(st, ev)
SavedIn(st, ev) == RA!SavedIn(st, ev)
ReleaseIndex(st, held) == RA!ReleaseIndex(st, held)
Unheld(held, i) == RA!Unheld(held, i)

\* The fixed fields compared one by one, in a fixed order for the report:
\* all of FixedFields but q.
LsFields == <<"st", "vs", "vr", "va", "rc", "mod", "k", "n2", "v22", "srej", "l3", "own", "ack", "srejx",
              "t1", "t3", "saved">>
ASSUME {LsFields[i] : i \in 1..Len(LsFields)} = FixedFields \ {"q"}
LsCopied == FixedFields \ {"q"}

\* MB's record (or context) r with the fixed fields other than q taken from a.
LsFrom(r, a) == [f \in DOMAIN r |-> IF f \in LsCopied THEN a[f] ELSE r[f]]

NewMachine(st) == MA!NewMachine(st) @@ [lsB |-> MB!NewMachine(st)]
Begin(m, e) == MA!Begin(m, e) @@ [lsB |-> MB!Begin(LsFrom(m.lsB, m), e)]
Persist(c) == MA!Persist(c) @@ [lsB |-> MB!Persist(c.lsB)]

RECURSIVE LsJoin(_, _)
LsJoin(s, sep) == IF s = <<>> THEN "" ELSE IF Len(s) = 1 THEN Head(s) ELSE Head(s) \o sep \o LsJoin(Tail(s), sep)

LsEvent(e) ==
    e.ev \o (IF e.pf # None THEN " pf=" \o ToString(e.pf) ELSE "") \o (IF e.cmd # None THEN " cmd=" \o ToString(e.cmd) ELSE "")
    \o (IF e.nr # None THEN " nr=" \o ToString(e.nr) ELSE "") \o (IF e.ns # None THEN " ns=" \o ToString(e.ns) ELSE "")
    \o (IF e.data # "" THEN " data=" \o e.data ELSE "")

Dispatch(m, e) ==
    LET ca == RA!Dispatch(m, e)
        cb == RB!Dispatch(LsFrom(m.lsB, m), e)
        ea == ca.err # ""
        eb == cb.err # ""
        pa == MA!Persist(ca)
        pb == MB!Persist(cb)
        fields == SelectSeq(LsFields, LAMBDA f : pa[f] # pb[f])
        comps == IF ea # eb THEN <<"error">>
                 ELSE IF ea THEN <<>>
                 ELSE (IF ca.fx # cb.fx THEN <<"fx">> ELSE <<>>) \o fields
                      \o (IF Len(pa.q) # Len(pb.q) THEN <<"q length">> ELSE <<>>)
        sig == m.st \o " / " \o e.ev \o " / " \o LsJoin(comps, ", ")
        show(f) == f \o ": A " \o ToString(pa[f]) \o ", B " \o ToString(pb[f])
        detail ==
            IF ea # eb THEN "error: A " \o (IF ea THEN ca.err ELSE "none") \o "; B " \o (IF eb THEN cb.err ELSE "none")
            ELSE LsJoin((IF ca.fx # cb.fx THEN <<"fx: A " \o ToString(ca.fx) \o ", B " \o ToString(cb.fx)>> ELSE <<>>)
                        \o [i \in 1..Len(fields) |-> show(fields[i])]
                        \o (IF Len(pa.q) # Len(pb.q) THEN <<"q length: A " \o ToString(Len(pa.q)) \o ", B " \o ToString(Len(pb.q))>> ELSE <<>>),
                        "; ")
        both == ca @@ [lsB |-> cb]
    IN  IF comps = <<>> THEN both
        ELSE IF sig \in LockstepKnown THEN ca @@ [lsB |-> LsFrom(cb, ca)]
        ELSE [both EXCEPT !.err = "lockstep{" \o sig \o "}: in " \o m.st \o " on " \o LsEvent(e)
                                  \o " (A took " \o ca.tid \o ", B took " \o cb.tid \o "): " \o detail]

\* A harness message that reports a lockstep difference (it may be wrapped,
\* as a replayed save's is).
LsIsDiff(msg) ==
    \E i \in 1..(Len(msg) - 8) : SubSeq(msg, i, i + 8) = "lockstep{"

=============================================================================
