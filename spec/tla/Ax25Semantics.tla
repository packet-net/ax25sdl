--------------------------- MODULE Ax25Semantics ---------------------------
(***************************************************************************)
(* Hand-written. The Data Link tables with a meaning: the runner           *)
(* (Ax25Runner.tla, how a table is run) instantiated with a meaning module *)
(* (what the verbs and atoms mean). This module is the meaning switch: the *)
(* EXTENDS line names the meaning, and the unnamed INSTANCE binds each of  *)
(* the runner's constant operators to the meaning's definition of the      *)
(* same name. The default meaning is Ax25Meaning.tla, a port of the        *)
(* reference interpreter; the interface a meaning implements is in its     *)
(* header. TlaCheck writes this module afresh with another name on the     *)
(* EXTENDS line when given --meaning (docs/tla.md).                        *)
(***************************************************************************)
EXTENDS Ax25Meaning

INSTANCE Ax25Runner

=============================================================================
