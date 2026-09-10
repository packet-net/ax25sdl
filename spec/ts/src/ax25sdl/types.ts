// Hand-written runtime types for the ax25sdl package. The .g.ts files
// in this directory import from this module; do not regenerate it.
//
// Exception: the closed action-verb union lives in the generated
// ax25-action-verb.g.ts (data-driven, one member per canonical verb);
// it is imported + re-exported here so ActionStep.verb is strongly typed.
// Each generated closed set also ships a runtime array (AX25_ACTION_VERBS,
// AX25_GUARDS, AX25_EVENTS) that the union is derived from, since a
// string-literal union is erased at runtime and a consumer otherwise has no
// way to enumerate the set. Those are re-exported here as values, not types.
//
// Empty-string and zero-int conventions stand in for nullable fields
// throughout, to keep generated object literals readable. A guard of
// "" means "no guard"; a line of 0 in an ImplementationReference means
// "no line citation"; etc. Consumers should treat empty values as
// absence.

/**
 * Classifies how an SDL action verb interacts with the surrounding
 * system. Mirrors the C# Packet.Ax25.Sdl.ActionKind enum and the kind
 * groups in spec-sdl/actions.yaml.
 */
export type ActionKind =
  | "signal_upper"
  | "signal_lower"
  | "processing"
  | "subroutine"
  | "internal_out";

// The closed set of canonical action verbs (generated from spec-sdl/actions.yaml).
export type { Ax25ActionVerb } from "./ax25-action-verb.g.js";
export { AX25_ACTION_VERBS } from "./ax25-action-verb.g.js";
import type { Ax25ActionVerb } from "./ax25-action-verb.g.js";

// The closed set of canonical guard atoms (generated from spec-sdl/predicates.yaml).
export type { Ax25Guard } from "./ax25-guard.g.js";
export { AX25_GUARDS } from "./ax25-guard.g.js";
import type { Ax25Guard } from "./ax25-guard.g.js";

// The closed set of AX.25 SDL events (generated from spec-sdl/events.yaml).
export type { Ax25Event } from "./ax25-event.g.js";
export { AX25_EVENTS } from "./ax25-event.g.js";
import type { Ax25Event } from "./ax25-event.g.js";

/**
 * One conjunct of a guard: a typed Ax25Guard atom plus whether it is
 * negated. A guard holds when every term holds; an empty guard array
 * means the transition is unguarded (always fires). Carrying the atom as
 * the generated Ax25Guard union member (not a raw string) lets a guard
 * evaluator bind every atom exhaustively — a renamed or typo'd atom is a
 * compile error, not an unbound-identifier throw at runtime.
 */
export interface GuardTerm {
  readonly atom: Ax25Guard;
  readonly negate: boolean;
}

/** Identifies which figure of which specification a page was transcribed from. */
export interface SdlSource {
  readonly spec: string;
  readonly figure: string;
  /** Empty when no URL recorded. */
  readonly url: string;
}

/**
 * One verb + kind pair along a transition or subroutine path. The
 * verb is the canonical spelling from spec-sdl/actions.yaml; aliases
 * are normalised at codegen time.
 */
export interface ActionStep {
  readonly verb: Ax25ActionVerb;
  readonly kind: ActionKind;
}

/**
 * Records a loop_while construct as a slice over the flat actions
 * list. start/length describe the body; predicate is the continue
 * condition (already negated where the figure's continuing edge is the
 * decision's No branch — the negation is carried by the GuardTerm).
 * testAtEnd selects the loop topology: false = test-at-head (while; body
 * may run zero times), true = test-at-tail (do-while; body runs at least
 * once).
 */
export interface LoopRange {
  readonly start: number;
  readonly length: number;
  readonly predicate: GuardTerm;
  readonly testAtEnd: boolean;
}

/**
 * One citation supporting a transition or subroutine path. source is
 * "spec_prose" or the key of a pinned_refs entry. Spec-prose
 * citations populate cite/quote; code citations populate
 * path/function/line.
 */
export interface ImplementationReference {
  readonly source: string;
  readonly cite: string;
  readonly quote: string;
  readonly path: string;
  readonly function: string;
  /** 0 = no line citation. */
  readonly line: number;
  readonly note: string;
}

/** One SDL transition column on a state-machine page. */
export interface TransitionSpec {
  readonly id: string;
  readonly from: string;
  readonly on: Ax25Event;
  /** Conjunction of guard terms; empty when unguarded. */
  readonly guard: readonly GuardTerm[];
  readonly actions: readonly ActionStep[];
  readonly next: string;
  readonly notes: string;
  readonly references: readonly ImplementationReference[];
  readonly loops: readonly LoopRange[];
}

/**
 * One path through a subroutine. Unlike a TransitionSpec there is no
 * incoming event or destination state.
 */
export interface SubroutinePath {
  readonly id: string;
  /** Conjunction of guard terms; empty when unguarded. */
  readonly guard: readonly GuardTerm[];
  readonly actions: readonly ActionStep[];
  readonly notes: string;
  readonly references: readonly ImplementationReference[];
  readonly loops: readonly LoopRange[];
}

/** One subroutine on a subroutine page. */
export interface SubroutineSpec {
  readonly name: string;
  readonly paths: readonly SubroutinePath[];
  readonly notes: string;
  readonly references: readonly ImplementationReference[];
}

/** One generated state-machine page (figc4.1 / 4.2 / 4.3 / 4.4 / 4.6 etc.). */
export interface StatePage {
  readonly machine: string;
  readonly state: string;
  readonly source: SdlSource;
  readonly transitions: readonly TransitionSpec[];
}

/** One generated subroutine page (figc4.7). */
export interface SubroutinesPage {
  readonly machine: string;
  readonly source: SdlSource;
  readonly subroutines: readonly SubroutineSpec[];
}
