//! Hand-written runtime types for the generated SDL pages.
//!
//! The `.g.rs` files in this crate are produced by
//! `codegen/src/Packet.Sdl.CodeGen.Rust` and reference the types
//! defined here via `use crate::types::*`. This file is **not**
//! generated — it's the analogue of `spec/go/ax25sdl/types.go`
//! and `spec/csharp/`'s hand-written shape, providing the language-
//! idiomatic structs the codegen targets.
//!
//! Empty-string / zero-int conventions are used in place of
//! `Option<…>` throughout, to keep the static initialisers
//! readable: a `Line` of `0` in an `ImplementationReference` means
//! "no line citation"; etc. A guard is an `&'static [GuardTerm]`
//! conjunction whose empty slice (`&[]`) means "no guard".
//!
//! This module is `no_std`-clean: every type is `Copy`, holds only
//! `&'static` borrows + scalars + the closed-set enums, and touches
//! no allocator. The crate is `#![no_std]` unless the default-on
//! `std` feature is enabled (which lights up the test harness).

// The closed typed sets (SP-010 / ADR-0002), generated from the spec
// catalogues and re-exported here so `ActionStep` / `TransitionSpec` /
// `GuardTerm` can name them. Carrying a typed enum member (not a raw
// `&str`) lets a consumer `match` exhaustively — a renamed or typo'd
// verb / atom / event becomes a compile error, not a runtime "unknown"
// throw. (Generated alongside this module; see crate::ax25_action_verb
// etc.)
pub use crate::ax25_action_verb::Ax25ActionVerb;
pub use crate::ax25_event::Ax25Event;
pub use crate::ax25_guard::Ax25Guard;

/// SDL action-kind classifier — mirrors `spec-sdl/actions.yaml`
/// kind groups and the C# / Go equivalents.
#[derive(Debug, PartialEq, Eq, Clone, Copy, Hash)]
pub enum ActionKind {
    SignalUpper,
    SignalLower,
    Processing,
    Subroutine,
    InternalOut,
}

/// One conjunct of a guard: a typed [`Ax25Guard`] atom plus whether it
/// is negated. A guard holds when every term holds; an empty guard
/// slice (`&[]`) means the transition is unguarded (always fires).
/// Carrying the atom as the generated [`Ax25Guard`] member (not a raw
/// string) lets a guard evaluator bind every atom exhaustively — a
/// renamed or typo'd atom is a compile error, not an unbound-identifier
/// throw at runtime. The Rust counterpart of the C# `GuardTerm` record /
/// TS `GuardTerm` interface (ADR-0002).
#[derive(Debug, PartialEq, Eq, Clone, Copy, Hash)]
pub struct GuardTerm {
    pub atom: Ax25Guard,
    pub negate: bool,
}

/// One verb + kind pair along a transition or subroutine path.
/// `verb` is the canonical action verb from `spec-sdl/actions.yaml`
/// (aliases are normalised at codegen time), carried as a typed
/// [`Ax25ActionVerb`] member.
#[derive(Debug, PartialEq, Eq, Clone, Copy, Hash)]
pub struct ActionStep {
    pub verb: Ax25ActionVerb,
    pub kind: ActionKind,
}

/// Identifies which figure of which specification a page was
/// transcribed from.
#[derive(Debug, PartialEq, Eq, Clone, Copy, Hash)]
pub struct SdlSource {
    pub spec: &'static str,
    pub figure: &'static str,
    /// Empty = no URL recorded.
    pub url: &'static str,
}

/// A `loop_while` construct rendered as a slice over the flat
/// `actions` list. `start` + `length` describe the body; `predicate`
/// is the continue condition (already negated where the figure's
/// continuing edge is the decision's No branch — the negation is
/// carried by the [`GuardTerm`]). `test_at_end` selects the loop
/// topology: `false` = test-at-head (while; body may run zero times),
/// `true` = test-at-tail (do-while; body runs at least once).
#[derive(Debug, PartialEq, Eq, Clone, Copy, Hash)]
pub struct LoopRange {
    pub start: usize,
    pub length: usize,
    pub predicate: GuardTerm,
    pub test_at_end: bool,
}

/// One citation supporting a transition or subroutine path.
/// `source` is `"spec_prose"` or the key of a `pinned_refs` entry.
/// Spec-prose citations populate `cite` / `quote`; code citations
/// populate `path` / `function` / `line`.
#[derive(Debug, PartialEq, Eq, Clone, Copy, Hash)]
pub struct ImplementationReference {
    pub source: &'static str,
    pub cite: &'static str,
    pub quote: &'static str,
    pub path: &'static str,
    pub function: &'static str,
    /// `0` = no line citation.
    pub line: u32,
    pub note: &'static str,
}

/// One SDL transition column on a state-machine page.
#[derive(Debug, PartialEq, Eq, Clone, Copy)]
pub struct TransitionSpec {
    pub id: &'static str,
    pub from: &'static str,
    pub on: Ax25Event,
    /// Conjunction of guard terms; empty (`&[]`) when unguarded.
    pub guard: &'static [GuardTerm],
    pub actions: &'static [ActionStep],
    pub next: &'static str,
    /// Empty = no notes.
    pub notes: &'static str,
    pub references: &'static [ImplementationReference],
    pub loops: &'static [LoopRange],
}

/// One path through a subroutine. Unlike a `TransitionSpec` there
/// is no incoming event or destination state.
#[derive(Debug, PartialEq, Eq, Clone, Copy)]
pub struct SubroutinePath {
    pub id: &'static str,
    /// Conjunction of guard terms; empty (`&[]`) when unguarded.
    pub guard: &'static [GuardTerm],
    pub actions: &'static [ActionStep],
    pub notes: &'static str,
    pub references: &'static [ImplementationReference],
    pub loops: &'static [LoopRange],
}

/// One subroutine on a subroutine page.
#[derive(Debug, PartialEq, Eq, Clone, Copy)]
pub struct SubroutineSpec {
    pub name: &'static str,
    pub paths: &'static [SubroutinePath],
    pub notes: &'static str,
    pub references: &'static [ImplementationReference],
}

/// One generated state-machine page (figc4.1 / 4.2 / 4.3 / 4.4 /
/// 4.6 / etc.).
#[derive(Debug, PartialEq, Eq, Clone, Copy)]
pub struct StatePage {
    pub machine: &'static str,
    pub state: &'static str,
    pub source: SdlSource,
    pub transitions: &'static [TransitionSpec],
    /// Events this state saves (the figure's Save symbol, "Requeue"), in
    /// page order. A saved event has no transition here: keep it in the
    /// input queue, in arrival order, and each time the machine enters a
    /// state (a nextstate back into the same state included) consume the
    /// first queued event that state does not save (ITU-T Z.100 save
    /// semantics). Empty for most states.
    pub saves: &'static [Ax25Event],
}

/// One generated subroutine page (figc4.7).
#[derive(Debug, PartialEq, Eq, Clone, Copy)]
pub struct SubroutinesPage {
    pub machine: &'static str,
    pub source: SdlSource,
    pub subroutines: &'static [SubroutineSpec],
}
