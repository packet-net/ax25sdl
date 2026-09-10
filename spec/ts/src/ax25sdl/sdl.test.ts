import { describe, it, expect } from "vitest";

import {
  AX25_ACTION_VERBS,
  AX25_EVENTS,
  AX25_GUARDS,
  DataLinkAwaitingConnection,
  DataLinkAwaitingV22Connection,
  DataLinkAwaitingRelease,
  DataLinkConnected,
  DataLinkDisconnected,
  DataLinkSubroutines,
} from "./index.js";
import type { StatePage, ActionKind, Ax25Guard } from "./index.js";

// State pages must declare at least one transition. The codegen
// validator rejects YAML pages with zero transitions before they
// reach the emitter, so an empty page is a generator bug.
describe("state pages", () => {
  const pages: readonly StatePage[] = [
    DataLinkAwaitingConnection,
    DataLinkAwaitingV22Connection,
    DataLinkAwaitingRelease,
    DataLinkConnected,
    DataLinkDisconnected,
  ];

  for (const p of pages) {
    it(`${p.machine}/${p.state} has transitions`, () => {
      expect(p.transitions.length).toBeGreaterThan(0);
    });
  }
});

// figc4.7 declares thirteen subroutines in the spec. The graphml edge
// fix for #11 (SABM out-edge from n50) restored Establish_Data_Link
// and Establish_Extended_Data_Link, so all 13 transcribe.
describe("figc4.7 subroutines", () => {
  it("has 13 entries", () => {
    expect(DataLinkSubroutines.subroutines).toHaveLength(13);
  });

  it("UI_Check appears", () => {
    const names = DataLinkSubroutines.subroutines.map((s) => s.name);
    expect(names).toContain("UI_Check");
  });
});

// ActionKind is a string-literal union; spot-check that generated
// pages use values from the declared set. The compile-time check is
// already enforced by the type system; this is belt-and-braces.
describe("action kinds", () => {
  const allowed = new Set<ActionKind>([
    "signal_upper", "signal_lower", "processing", "subroutine", "internal_out",
  ]);

  it("every generated verb has a known kind", () => {
    for (const p of [DataLinkAwaitingConnection, DataLinkConnected, DataLinkDisconnected]) {
      for (const t of p.transitions) {
        for (const a of t.actions) {
          expect(allowed.has(a.kind)).toBe(true);
        }
      }
    }
  });
});

// The three closed sets ship as runtime arrays with the union types derived
// from them. C# and Rust consumers enumerate their closed sets from the enum
// type; a TypeScript string-literal union is erased, so without these arrays a
// TS consumer cannot walk the members and cannot write the counterpart of
// packet.net's Binding_Table_Is_Exhaustive_Over_Ax25Guard.
describe("closed-set runtime arrays", () => {
  const sets = [
    ["AX25_ACTION_VERBS", AX25_ACTION_VERBS],
    ["AX25_GUARDS", AX25_GUARDS],
    ["AX25_EVENTS", AX25_EVENTS],
  ] as const;

  for (const [name, members] of sets) {
    it(`${name} is a non-empty array of unique strings`, () => {
      expect(Array.isArray(members)).toBe(true);
      expect(members.length).toBeGreaterThan(0);
      expect(new Set<string>(members).size).toBe(members.length);
      for (const m of members) expect(typeof m).toBe("string");
    });

    it(`${name} is sorted by ordinal`, () => {
      const sorted = [...members].sort();
      expect([...members]).toEqual(sorted);
    });
  }

  // Spot-check that the array carries the atoms the generated pages use, so a
  // consumer building an exhaustive binding table off it covers real guards.
  it("AX25_GUARDS covers every atom the generated pages reference", () => {
    const referenced = new Set<Ax25Guard>();
    for (const p of [DataLinkAwaitingConnection, DataLinkConnected, DataLinkDisconnected]) {
      for (const t of p.transitions) {
        for (const term of t.guard) referenced.add(term.atom);
      }
    }
    expect(referenced.size).toBeGreaterThan(0);
    const known = new Set<string>(AX25_GUARDS);
    for (const atom of referenced) expect(known.has(atom)).toBe(true);
  });

  // The union is `(typeof AX25_GUARDS)[number]`, so this only compiles while
  // the two agree. A member removed from the array stops type-checking here.
  it("the derived union accepts a member of the array", () => {
    const first: Ax25Guard = AX25_GUARDS[0];
    expect(AX25_GUARDS).toContain(first);
  });
});
