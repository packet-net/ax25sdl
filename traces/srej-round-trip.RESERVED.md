# RESERVED: srej-round-trip (clean-room authoring in progress)

**Do not author an SREJ trace on this branch.**

The SREJ round-trip golden trace is deliberately reserved for a separate,
clean-room author who writes it from the AX.25 v2.2 spec prose alone
(packethacking/ax25spec `doc/ax.25.2.2.4_Oct_25.md` §4.3.2.4, §6.4.4.2,
§6.4.8), **without reading the current tables or any pending SREJ fixes**.

Rationale: a trace written by someone who has already read the tables tends
to encode the tables' own behaviour back at them. An independently derived
trace is a far stronger conformance check — this reservation is an
independence control for the SREJ paths specifically, because several SREJ
figure defects are under active upstream discussion
(packethacking/ax25spec#38, #42, #47, #51) and an SREJ-touching fix PR is
open against this repo.

Tracking issue: https://github.com/packet-net/ax25sdl/issues/74

When the clean-room trace lands, this file is replaced by
`srej-round-trip.trace.yaml` (and any `expected_failure:` markers it needs,
e.g. for #38/#42/#47, are added by that author from the prose, not from the
tables).
