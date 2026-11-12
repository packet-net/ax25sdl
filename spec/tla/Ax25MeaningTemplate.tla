------------------------ MODULE Ax25MeaningTemplate ------------------------
(***************************************************************************)
(* Hand-written. A skeleton meaning module that parses: every operator of  *)
(* spec/tla/MeaningInterface.md is defined, the fixed station fields are   *)
(* present, and every verb and atom is still unbound (a dispatch that      *)
(* reaches one fails). Copy it, rename the module and the file, and fill   *)
(* it in.                                                                  *)
(***************************************************************************)
EXTENDS Ax25Values

NewMachine(st) ==
    [st |-> st, vs |-> 0, vr |-> 0, va |-> 0, rc |-> 0, mod |-> 8, k |-> 4, n2 |-> 10,
     v22 |-> FALSE, srej |-> FALSE, l3 |-> FALSE, own |-> FALSE, ack |-> FALSE, srejx |-> 0,
     t1 |-> "stopped", t3 |-> "stopped", q |-> <<>>, saved |-> {}]

Begin(m, e) ==
    [st |-> m.st, vs |-> m.vs, vr |-> m.vr, va |-> m.va, rc |-> m.rc, mod |-> m.mod, k |-> m.k, n2 |-> m.n2,
     v22 |-> m.v22, srej |-> m.srej, l3 |-> m.l3, own |-> m.own, ack |-> m.ack, srejx |-> m.srejx,
     t1 |-> m.t1, t3 |-> m.t3, q |-> m.q, saved |-> m.saved,
     fx |-> <<>>, err |-> "", tid |-> "", arms |-> {}]

Persist(c) ==
    [st |-> c.st, vs |-> c.vs, vr |-> c.vr, va |-> c.va, rc |-> c.rc, mod |-> c.mod, k |-> c.k, n2 |-> c.n2,
     v22 |-> c.v22, srej |-> c.srej, l3 |-> c.l3, own |-> c.own, ack |-> c.ack, srejx |-> c.srejx,
     t1 |-> c.t1, t3 |-> c.t3, q |-> c.q, saved |-> c.saved]

Arrive(c, e) == c

AtomError(c, e, a) == "guard atom '" \o a \o "' has no binding"
AtomValue(c, e, a) == FALSE

Processing(c, e, verb) == Fail(c, "processing verb '" \o verb \o "' has no binding")
SignalUpper(c, e, verb) == Fail(c, "signal_upper verb '" \o verb \o "' has no binding")
SignalLower(c, e, verb) == Fail(c, "signal_lower verb '" \o verb \o "' has no binding")
InternalOut(c, e, verb) == Fail(c, "internal_out verb '" \o verb \o "' has no binding")

Preempts(c, e) == FALSE
Preempt(c, e) == c

AfterTransition(c) == c

=============================================================================
