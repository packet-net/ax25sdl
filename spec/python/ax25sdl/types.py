"""Hand-written runtime types for the generated SDL pages.

The ``*.g.py`` files in this package are produced by
``codegen/src/Packet.Sdl.CodeGen.Python`` and construct the dataclasses
defined here. This file is **not** generated — it lives outside the
``*.g.py`` / ``*_g_test.py`` codegen globs (the mirror of
``spec/csharp/TransitionSpec.cs``, ``spec/go/ax25sdl/types.go`` and
``spec/rust/src/types.rs``). Keep it in sync with those type definitions.
"""

from __future__ import annotations

from dataclasses import dataclass
from enum import Enum


class ActionKind(Enum):
    """The figc1.1 shape class that produced an action verb."""

    SIGNAL_UPPER = "signal_upper"
    SIGNAL_LOWER = "signal_lower"
    PROCESSING = "processing"
    SUBROUTINE = "subroutine"
    INTERNAL_OUT = "internal_out"


@dataclass(frozen=True)
class SdlSource:
    """Which spec figure a page was transcribed from."""

    spec: str
    figure: str
    url: str = ""


@dataclass(frozen=True)
class ActionStep:
    """One verb in a transition's action chain plus its SDL shape class."""

    verb: str
    kind: ActionKind


@dataclass(frozen=True)
class LoopRange:
    """A ``loop_while`` construct as a slice over the flat action list.

    ``start``/``length`` describe the body; ``predicate`` is the *continue*
    condition (already negated where the figure's continuing edge is the
    decision's No branch). ``test_at_end`` selects the topology: ``False`` =
    test-at-head (while; body may run zero times), ``True`` = test-at-tail
    (do-while; body runs at least once).
    """

    start: int
    length: int
    predicate: str
    test_at_end: bool


@dataclass(frozen=True)
class ImplementationReference:
    """One cross-reference citation supporting a transition or path."""

    source: str
    cite: str = ""
    quote: str = ""
    path: str = ""
    function: str = ""
    line: int = 0
    note: str = ""


@dataclass(frozen=True)
class TransitionSpec:
    """One SDL transition: in ``from_`` on ``on`` while ``guard`` holds, run
    ``actions`` (expanding any ``loops``) and move to ``next``."""

    id: str
    from_: str
    on: str
    guard: str
    actions: tuple[ActionStep, ...]
    next: str
    notes: str
    references: tuple[ImplementationReference, ...]
    loops: tuple[LoopRange, ...]


@dataclass(frozen=True)
class StatePage:
    """A resolved state-machine page (figc4.1 / 4.2 / 4.3 / 4.4 / 4.6 / …)."""

    machine: str
    state: str
    source: SdlSource
    transitions: tuple[TransitionSpec, ...]
    # Events this state saves (the figure's Save symbol, "Requeue"), in page
    # order. A saved event has no transition here: keep it in the input
    # queue, in arrival order, and each time the machine enters a state (a
    # nextstate back into the same state included) consume the first queued
    # event that state does not save (ITU-T Z.100 save semantics). Empty for
    # most states.
    saves: tuple[str, ...] = ()


@dataclass(frozen=True)
class SubroutinePath:
    """One path through a subroutine (one combination of decision outcomes)."""

    id: str
    guard: str
    actions: tuple[ActionStep, ...]
    notes: str
    references: tuple[ImplementationReference, ...]
    loops: tuple[LoopRange, ...]


@dataclass(frozen=True)
class SubroutineSpec:
    """One subroutine on a figc4.7-style subroutine page."""

    name: str
    paths: tuple[SubroutinePath, ...]
    notes: str = ""
    references: tuple[ImplementationReference, ...] = ()


@dataclass(frozen=True)
class SubroutinesPage:
    """A resolved subroutine page (figc4.7)."""

    machine: str
    source: SdlSource
    subroutines: tuple[SubroutineSpec, ...]
