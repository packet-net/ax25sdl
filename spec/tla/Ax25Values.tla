---------------------------- MODULE Ax25Values -----------------------------
(***************************************************************************)
(* Hand-written. The value shapes that cross the interface between the     *)
(* harness, the runner (Ax25Runner.tla) and a meaning module: what an      *)
(* event, a frame on the air and an effect look like, the error            *)
(* convention, and helpers for finite maps kept as sets of pairs (the      *)
(* harness reads the receive buffer with Has). Every meaning module        *)
(* EXTENDS this one and must not redefine anything in it. The interface is *)
(* spec/tla/MeaningInterface.md.                                           *)
(***************************************************************************)
EXTENDS Integers, Sequences, FiniteSets, TLC

\* An absent value: the N(r) or N(s) of a frame that carries none, or a pf
\* or cmd of a stimulus that is not a frame.
None == -1

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

\* One injected stimulus (EventInput). pf and cmd are 1 or 0 for a frame,
\* None otherwise; able and info are the environment's answers to the two
\* environment atoms.
\* ftype is the received frame's type, "" when the stimulus is not a frame
\* (the harness's UI check keys on it). fe is the corrupted form when the
\* harness injected a frame error, "" otherwise (its FrameError check and
\* reset rule key on it).
Event(ev) == [ev |-> ev, pf |-> None, cmd |-> None, nr |-> None, ns |-> None, data |-> "", able |-> TRUE, info |-> TRUE, ftype |-> "", fe |-> ""]

\* Finite maps from a sequence number to a payload label, kept as sets of
\* pairs so that equal maps are equal values whatever built them.
Has(map, key) == \E pr \in map : pr[1] = key
Get(map, key) == (CHOOSE pr \in map : pr[1] = key)[2]
Put(map, key, val) == {pr \in map : pr[1] # key} \cup {<<key, val>>}
Del(map, key) == {pr \in map : pr[1] # key}

\* Record an error on a dispatch context; the first one wins and every later
\* step passes through.
Fail(c, msg) == IF c.err = "" THEN [c EXCEPT !.err = msg] ELSE c

=============================================================================
