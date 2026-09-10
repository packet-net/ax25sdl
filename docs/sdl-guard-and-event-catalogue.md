# SDL guard-atom + event catalogues

The guard/event counterparts of `docs/sdl-verb-catalogue.md`. Where that
document covers action **verbs** (`spec-sdl/actions.yaml` → `Ax25ActionVerb`),
this one covers decision **predicates** (`spec-sdl/predicates.yaml` →
`Ax25Guard`) and transition **events** (`spec-sdl/events.yaml` → `Ax25Event`).

See `docs/adr/0002-typed-guard-and-event-closed-sets.md` for the why.

## Guard atoms — `spec-sdl/predicates.yaml`

A transition's `guard:` is a boolean expression. The `Resolver` builds it from
the `predicate:` of each decision on the transition's path, negating the No
branch and joining multiple decisions with `and`:

```
not peer_receiver_busy and vs_eq_va_plus_k
```

Each decision `predicate:` field is a **single atom** — `predicates.yaml` is
the vocabulary of those atoms. It is the guard analogue of `actions.yaml` and
has the identical shape (groups → list of `{ name, aliases }`):

```yaml
flags:
  - name: peer_receiver_busy   # canonical — what .g.* guards carry
    # legacy binding: peer_receiver_busy
    aliases:
      - peer_busy              # figc4.4 figure spelling (drops 'receiver')
```

The group key (`flags`, `sequence_variables`, `timers`, …) is **documentation
only** — predicates have no `kind:` the way actions do; the codegen flattens
every group into one namespace. Group by what the predicate tests.

### Canonical vs. legacy-binding names

`name:` is the spelling the generated tables carry **and** the spelling a
consumer binds to after the runtime alias layers are deleted (Part B). Where
the historic packet.net binding name differs (the pre-normalisation
hand-authored spelling — `V_s_eq_V_a`, `acknowledge_pending`, `srej_enabled`),
it's recorded in a `# legacy binding:` comment. Those legacy names are **not
aliases**: no `*.sdl.yaml` page spells a predicate that way, so declaring them
as aliases would trip the unused-alias lint. The comment is purely a map for
Part B's exhaustive re-binding.

### What triggers an error

Unlike `actions.yaml` (soft passthrough), once `predicates.yaml` is present the
catalog is **authoritative**:

1. **Uncatalogued predicate.** A decision `predicate:` that is neither a
   canonical nor a declared alias is a hard error (catalog-completeness lint) —
   add it as a canonical, or as an alias if it's an alternate figure spelling.
   This is what keeps the emitted `Ax25Guard` closed set complete.
2. **Unused alias.** A declared alias no decision references is dead weight
   (same lint as `actions.yaml`).
3. **Malformed catalog.** Duplicate canonical, alias claimed by two canonicals,
   empty alias.

### The closed set is gathered from the resolved IR, not the raw YAML

`Ax25Guard.g.cs` / `ax25-guard.g.ts` enumerate every atom that actually appears
in emitted guard/predicate output. That set is the catalog canonicals **plus**
atoms the `Resolver` synthesises that never appear as a raw `predicate:` —
notably `vs_eq_nr`, produced by the ax25sdl#53 stale-read substitution when a
`vs_eq_va` decision sits after a `V(a) := N(r)` assignment. `vs_eq_nr` is a
catalog canonical with no YAML decision (the completeness lint only checks YAML
decisions, so this is fine).

### The typed representation

`Ax25Guard` is one member per canonical atom (PascalCased, collision-checked,
ordered by canonical string). The composed guard becomes a conjunction of
typed terms — a hand-written `GuardTerm(Ax25Guard Atom, bool Negate)`:

- `TransitionSpec.Guard` / `SubroutinePath.Guard` → `GuardTerm[]?` (conjunction;
  null/empty = unguarded).
- `LoopRange.Predicate` → a single `GuardTerm` (carries the loop's `not`).
- `UndefinedSpecBranch.Predicate` → `Ax25Guard` (bare atom).

C#, TS, **and Rust** get the typed closed set + tightened field types
(`Ax25Guard` enum/union + `GuardTerm`); Rust was brought to parity in
[ADR-0003](adr/0003-rust-typed-closed-sets-and-no-std.md) for an embedded
(`no_std`) consumer. Go/C/Python/JSON keep the canonical guard string.

#### The TS closed sets also ship as runtime arrays

A C# consumer enumerates a closed set with `Enum.GetValues<Ax25Guard>()`, which
is how packet.net's `Binding_Table_Is_Exhaustive_Over_Ax25Guard` proves its
table covers every atom. Rust gets the same from its enum. A TypeScript
string-literal union is erased at compile time, so a TS consumer given only the
union has no way to walk the members and no way to write that test.

Each of the three generated TS closed sets therefore emits a runtime array
alongside the union, with the union derived from the array so the two cannot
drift:

```ts
export const AX25_GUARDS = ["F_eq_1", /* ... */] as const;
export type Ax25Guard = (typeof AX25_GUARDS)[number];
```

`ax25-action-verb.g.ts` and `ax25-event.g.ts` carry `AX25_ACTION_VERBS` and
`AX25_EVENTS` the same way. All three are re-exported as values from the
hand-written `types.ts`, so `import { AX25_GUARDS } from "ax25sdl"` works. The
derived type is identical in shape to the literal union it replaced; the
emitted `.d.ts` still spells out every member, so nothing changes for existing
consumers of the type.

The composed guard string is parsed back into terms by `GuardExpression` (in
`Packet.Sdl.IR`). It accepts only a conjunction of optionally-negated atoms and
**throws on a top-level `or`** — that's the trigger to extend `GuardTerm` to a
disjunctive shape rather than silently mis-encode. (No spec page emits a
top-level `or` today; atoms whose *name* contains `_or_` are single opaque
atoms.)

## Events — `spec-sdl/events.yaml`

Every transition's `on:` field must already appear in `events.yaml` (the
codegen has long refused an uncatalogued event). The event catalogue therefore
needs no new file: `Ax25Event` simply enumerates the existing `events.yaml`
groups (`primitives_upper`, `frames_received`, `catchalls`, `internal`,
`timers`, …). The group key is documentation only.

`On` is always a single atom, so `TransitionSpec.On` tightens `string` →
`Ax25Event` directly — the exact same trivial retype SP-010 applied to
`ActionStep.Verb`. (Delivered in the follow-on events PR.)

## Workflow when you spot a cross-page predicate variant

1. Confirm both spellings exist in the figures (the graphml `<y:NodeLabel>`
   text the walker normalises — see `docs/sdl-primer.md`).
2. Pick the canonical: prefer the fuller/correct spelling
   (`peer_receiver_busy` over `peer_busy`, `own_receiver_busy` over the
   `own_receive_busy` typo).
3. Add an entry under the most descriptive group with the canonical `name:`
   and the variants as `aliases:`; record the packet.net binding name in a
   `# legacy binding:` comment if it differs.
4. Re-run codegen — the `.g.*` guards and the `Ax25Guard` set show the
   canonical atom everywhere.

Once an atom is canonicalised it's load-bearing for the consumers' exhaustive
binding. When in doubt, ask.
