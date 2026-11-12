----------------------------- MODULE Lockstep ------------------------------
(***************************************************************************)
(* Hand-written. The root module of a lockstep run (TlaCheck --lockstep):  *)
(* the two-station harness over LockstepSemantics.tla (written into the    *)
(* model directory as Ax25Semantics.tla), checked for one thing only:      *)
(* that the two meanings never differ on a dispatch. Other violations are  *)
(* not reported, but a state reached by one is not explored further, so   *)
(* the search covers what the plain safety search would before it stops.  *)
(***************************************************************************)
EXTENDS TwoStation

\* No dispatch so far found the two meanings different.
LockstepAgree == ~(viol.kind = "MachineError" /\ LsIsDiff(viol.msg))

\* Explore only from states no check has fired on (a CONSTRAINT).
LockstepGo == viol = NoViol

=============================================================================
