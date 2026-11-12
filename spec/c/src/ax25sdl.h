// Hand-written runtime types for the generated SDL tables.
//
// The `*.g.c` / `*.g.h` files in this backend are produced by
// `codegen/src/Packet.Sdl.CodeGen.C` and use the structs defined here. This
// header is **not** generated — it lives outside the `*.g.{c,h}` codegen
// globs (the C mirror of `spec/csharp/TransitionSpec.cs`,
// `spec/go/ax25sdl/types.go`, `spec/rust/src/types.rs` and
// `spec/python/ax25sdl/types.py`). Keep it in sync with those.

#ifndef AX25SDL_H
#define AX25SDL_H

#include <stdbool.h>
#include <stddef.h>

// figc1.1 shape class that produced an action verb.
typedef enum {
  AX25SDL_KIND_SIGNAL_UPPER,
  AX25SDL_KIND_SIGNAL_LOWER,
  AX25SDL_KIND_PROCESSING,
  AX25SDL_KIND_SUBROUTINE,
  AX25SDL_KIND_INTERNAL_OUT,
} ActionKind;

// Which spec figure a page was transcribed from.
typedef struct {
  const char *spec;
  const char *figure;
  const char *url;
} SdlSource;

// One verb in an action chain plus its SDL shape class.
typedef struct {
  const char *verb;
  ActionKind kind;
} ActionStep;

// A loop_while construct as a slice over the flat action list. start/length
// describe the body; predicate is the continue condition (already negated
// where the figure's continuing edge is the decision's No branch);
// test_at_end is false for test-at-head (while, zero-or-more iterations),
// true for test-at-tail (do-while, one-or-more).
typedef struct {
  int start;
  int length;
  const char *predicate;
  bool test_at_end;
} LoopRange;

// One cross-reference citation supporting a transition or subroutine path.
typedef struct {
  const char *source;
  const char *cite;
  const char *quote;
  const char *path;
  const char *function;
  int line;
  const char *note;
} ImplementationReference;

// One SDL transition.
typedef struct {
  const char *id;
  const char *from;
  const char *on;
  const char *guard;
  const ActionStep *actions;
  size_t actions_len;
  const char *next;
  const char *notes;
  const ImplementationReference *references;
  size_t references_len;
  const LoopRange *loops;
  size_t loops_len;
} TransitionSpec;

// A resolved state-machine page (figc4.1 / 4.2 / 4.3 / 4.4 / 4.6 / ...).
typedef struct {
  const char *machine;
  const char *state;
  SdlSource source;
  const TransitionSpec *transitions;
  size_t transitions_len;
  // Events this state saves (the figure's Save symbol, "Requeue"), in page
  // order; NULL + 0 when none. A saved event has no transition here: keep it
  // in the input queue, in arrival order, and each time the machine enters a
  // state (a nextstate back into the same state included) consume the first
  // queued event that state does not save (ITU-T Z.100 save semantics).
  const char *const *saves;
  size_t saves_len;
} StatePage;

// One path through a subroutine (one combination of decision outcomes).
typedef struct {
  const char *id;
  const char *guard;
  const ActionStep *actions;
  size_t actions_len;
  const char *notes;
  const ImplementationReference *references;
  size_t references_len;
  const LoopRange *loops;
  size_t loops_len;
} SubroutinePath;

// One subroutine on a figc4.7-style subroutine page.
typedef struct {
  const char *name;
  const SubroutinePath *paths;
  size_t paths_len;
  const char *notes;
  const ImplementationReference *references;
  size_t references_len;
} SubroutineSpec;

// A resolved subroutine page (figc4.7).
typedef struct {
  const char *machine;
  SdlSource source;
  const SubroutineSpec *subroutines;
  size_t subroutines_len;
} SubroutinesPage;

#endif // AX25SDL_H
