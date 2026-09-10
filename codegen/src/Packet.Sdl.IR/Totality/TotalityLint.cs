using System.Text;

namespace Packet.Sdl.IR.Totality;

public enum FindingKind
{
    /// <summary>A feasible atom valuation no transition or path matches.</summary>
    Hole,
    /// <summary>A feasible atom valuation two or more transitions or paths match.</summary>
    Nondeterminism,
}

/// <summary>
/// One totality / determinism finding: a cube of the live atoms (some
/// fixed, the rest free) over which every feasible valuation is a hole, or
/// is matched by the same set of two or more transitions.
/// </summary>
public sealed class TotalityFinding
{
    public required FindingKind Kind { get; init; }
    public required string SourcePath { get; init; }
    public required string Machine { get; init; }
    /// <summary>State-page findings carry the state; subroutine findings carry <see cref="Subroutine"/> instead.</summary>
    public string? State { get; init; }
    public string? Subroutine { get; init; }
    /// <summary>The <c>on:</c> event (state pages only).</summary>
    public string? Event { get; init; }
    /// <summary>The fixed atoms of the cube, in live-atom order.</summary>
    public required IReadOnlyList<(string Atom, bool Value)> When { get; init; }
    /// <summary>Live atoms the finding does not depend on.</summary>
    public required IReadOnlyList<string> FreeAtoms { get; init; }
    /// <summary>For nondeterminism: every transition / path id that matches across the cube (sorted). Empty for a hole.</summary>
    public required IReadOnlyList<string> Transitions { get; init; }
    /// <summary>Concrete underlying-variable assignment that produces a valuation inside the cube.</summary>
    public required string Witness { get; init; }
    /// <summary>The full diagnostic text.</summary>
    public required string Message { get; init; }

    /// <summary>The (state, event) or subroutine arm this finding belongs to, for display.</summary>
    public string Arm => Subroutine is not null
        ? $"subroutine `{Subroutine}`"
        : $"state `{State}`, event `{Event}`";
}

/// <summary>Everything one run of the lint produced.</summary>
public sealed class TotalityReport
{
    public required IReadOnlyList<TotalityFinding> Findings { get; init; }
    public required int ArmsChecked { get; init; }
    public required int SubroutinesChecked { get; init; }
    public required int FeasibleValuations { get; init; }
    /// <summary>Holes over the raw 2^n atom valuations that the domain model classified as infeasible.</summary>
    public required int InfeasibleSyntacticHoles { get; init; }
    /// <summary>Overlaps over the raw 2^n atom valuations that the domain model classified as infeasible.</summary>
    public required int InfeasibleSyntacticOverlaps { get; init; }
    /// <summary>Feasible valuations that only an Undefined-crossing transition covers (the spec leaves them open; not holes).</summary>
    public required int CoveredOnlyByUndefined { get; init; }
    /// <summary>Atoms with no definition in the domain model, treated as independent booleans.</summary>
    public required IReadOnlyList<string> UnknownAtoms { get; init; }
}

/// <summary>
/// Static totality and determinism lint over the resolved SDL tables
/// (docs/lint-totality.md). For every (state, event) arm and every
/// subroutine it enumerates the feasible valuations of the guard atoms the
/// arm mentions (feasibility from <see cref="AtomDomain"/>) and requires
/// exactly one transition or path to match each. Zero matches is a hole in
/// the figure; two or more is nondeterminism. A transition that crosses an
/// Undefined spec branch counts as covering a valuation for totality (the
/// spec deliberately leaves that behaviour open) but never toward
/// nondeterminism.
/// </summary>
public static class TotalityLint
{
    /// <summary>Above this many live atoms the raw 2^n syntactic count is skipped (the feasible check still runs).</summary>
    private const int SyntacticCountMaxAtoms = 20;

    private sealed record Arm(
        string SourcePath, string Machine, string? State, string? Subroutine, string? Event,
        IReadOnlyList<(string Id, string? Guard, bool CrossesUndefined)> Paths);

    private sealed class Counters
    {
        public int Feasible, InfeasibleHoles, InfeasibleOverlaps, UndefinedOnly;
    }

    public static TotalityReport Run(
        IEnumerable<ResolvedPage> pages,
        IEnumerable<ResolvedSubroutinesPage> subroutinePages,
        AtomDomain domain)
    {
        var findings = new List<TotalityFinding>();
        var unknown = new SortedSet<string>(StringComparer.Ordinal);
        var counters = new Counters();
        int arms = 0, subs = 0;

        foreach (var page in pages)
        {
            foreach (var grp in page.Transitions.GroupBy(t => t.On, StringComparer.Ordinal))
            {
                var arm = new Arm(page.SourcePath, page.Machine, page.State, null, grp.Key,
                    grp.Select(t => (t.Id, t.Guard, t.UndefinedBranches.Count > 0)).ToList());
                arms++;
                CheckArm(arm, domain, findings, unknown, counters);
            }
        }

        foreach (var page in subroutinePages)
        {
            foreach (var sub in page.Subroutines)
            {
                var arm = new Arm(page.SourcePath, page.Machine, null, sub.Name, null,
                    sub.Paths.Select(p => (p.Id, p.Guard, false)).ToList());
                subs++;
                CheckArm(arm, domain, findings, unknown, counters);
            }
        }

        return new TotalityReport
        {
            Findings = findings,
            ArmsChecked = arms,
            SubroutinesChecked = subs,
            FeasibleValuations = counters.Feasible,
            InfeasibleSyntacticHoles = counters.InfeasibleHoles,
            InfeasibleSyntacticOverlaps = counters.InfeasibleOverlaps,
            CoveredOnlyByUndefined = counters.UndefinedOnly,
            UnknownAtoms = unknown.ToList(),
        };
    }

    /// <summary>One-line stdout summary so a reader can see the domain model did work.</summary>
    public static string Summarise(TotalityReport r)
    {
        var holes = r.Findings.Count(f => f.Kind == FindingKind.Hole);
        var overlaps = r.Findings.Count(f => f.Kind == FindingKind.Nondeterminism);
        var sb = new StringBuilder();
        sb.Append("  lint  totality/determinism: ")
          .Append(r.ArmsChecked).Append(" (state, event) arm(s) and ")
          .Append(r.SubroutinesChecked).Append(" subroutine(s) checked over ")
          .Append(r.FeasibleValuations).Append(" feasible atom valuation(s): ")
          .Append(holes).Append(" hole(s), ").Append(overlaps).Append(" overlap(s); ")
          .Append(r.InfeasibleSyntacticHoles).Append(" syntactic hole(s) and ")
          .Append(r.InfeasibleSyntacticOverlaps).Append(" syntactic overlap(s) were infeasible under the atom domain model");
        if (r.CoveredOnlyByUndefined > 0)
            sb.Append("; ").Append(r.CoveredOnlyByUndefined).Append(" valuation(s) covered only by an Undefined spec branch (left open by the spec, not counted)");
        sb.Append('.');
        return sb.ToString();
    }

    private static void CheckArm(Arm arm, AtomDomain domain, List<TotalityFinding> findings, SortedSet<string> unknownAtoms, Counters counters)
    {
        // Live atoms, in first-mention order across the arm's guards.
        var live = new List<string>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var parsed = arm.Paths.Select(p => (p.Id, Terms: GuardExpression.Parse(p.Guard), p.CrossesUndefined)).ToList();
        foreach (var (_, terms, _) in parsed)
        {
            foreach (var t in terms)
            {
                if (!index.ContainsKey(t.Atom)) { index[t.Atom] = live.Count; live.Add(t.Atom); }
            }
        }
        var n = live.Count;

        // Per path, one literal per live atom: -1 = indifferent, 0 = needs
        // false, 1 = needs true, -2 = self-contradictory (a and not a).
        var paths = parsed.Select(p =>
        {
            var lits = Enumerable.Repeat(-1, n).ToArray();
            foreach (var t in p.Terms)
            {
                var want = t.Negate ? 0 : 1;
                var i = index[t.Atom];
                lits[i] = lits[i] == -1 || lits[i] == want ? want : -2;
            }
            return (p.Id, Lits: lits, Undefined: p.CrossesUndefined);
        }).ToList();

        var space = domain.Enumerate(live, AtomDomain.ConstraintsForEvent(arm.Event));
        foreach (var u in space.UnknownAtoms) unknownAtoms.Add(u);
        var vals = space.Valuations;
        counters.Feasible += vals.Count;

        // Classify every feasible valuation.
        var holeSet = new HashSet<int>();
        var nondet = new Dictionary<string, (List<string> Ids, HashSet<int> Set)>(StringComparer.Ordinal);
        for (int v = 0; v < vals.Count; v++)
        {
            var (defined, undefinedCount) = Matches(paths, vals[v].Values);
            if (defined.Count == 0 && undefinedCount == 0) holeSet.Add(v);
            else if (defined.Count == 0) counters.UndefinedOnly++;
            else if (defined.Count >= 2)
            {
                var key = string.Join("", defined);
                if (!nondet.TryGetValue(key, out var entry))
                {
                    entry = (defined, new HashSet<int>());
                    nondet[key] = entry;
                }
                entry.Set.Add(v);
            }
        }

        // Syntactic-only counts: the raw 2^n valuations, minus the feasible ones.
        if (n <= SyntacticCountMaxAtoms)
        {
            var feasibleKeys = new HashSet<string>(vals.Select(x => Key(x.Values)), StringComparer.Ordinal);
            var raw = new bool[n];
            for (long bits = 0; bits < (1L << n); bits++)
            {
                for (int i = 0; i < n; i++) raw[i] = ((bits >> i) & 1) == 1;
                if (feasibleKeys.Contains(Key(raw))) continue;
                var (defined, undefinedCount) = Matches(paths, raw);
                if (defined.Count == 0 && undefinedCount == 0) counters.InfeasibleHoles++;
                else if (defined.Count >= 2) counters.InfeasibleOverlaps++;
            }
        }

        // Findings, as minimal cubes.
        var feasibleValues = vals.Select(x => x.Values).ToList();
        foreach (var cube in MinimiseCubes(feasibleValues, holeSet, n))
        {
            findings.Add(Build(arm, FindingKind.Hole, live, cube, Array.Empty<string>(), feasibleValues, vals, domain));
        }
        foreach (var (_, entry) in nondet.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var ids = entry.Ids.OrderBy(s => s, StringComparer.Ordinal).ToList();
            foreach (var cube in MinimiseCubes(feasibleValues, entry.Set, n))
            {
                findings.Add(Build(arm, FindingKind.Nondeterminism, live, cube, ids, feasibleValues, vals, domain));
            }
        }
    }

    private static string Key(IReadOnlyList<bool> values)
    {
        var c = new char[values.Count];
        for (int i = 0; i < c.Length; i++) c[i] = values[i] ? '1' : '0';
        return new string(c);
    }

    /// <summary>Ids of the defined (non-Undefined) paths that match, plus how many Undefined-crossing paths match.</summary>
    private static (List<string> Defined, int UndefinedCount) Matches(
        IReadOnlyList<(string Id, int[] Lits, bool Undefined)> paths, IReadOnlyList<bool> values)
    {
        var defined = new List<string>();
        int undefinedCount = 0;
        foreach (var (id, lits, undefined) in paths)
        {
            bool ok = true;
            for (int i = 0; i < lits.Length && ok; i++)
            {
                if (lits[i] == -1) continue;
                if (lits[i] == -2 || (lits[i] == 1) != values[i]) ok = false;
            }
            if (!ok) continue;
            if (undefined) undefinedCount++; else defined.Add(id);
        }
        return (defined, undefinedCount);
    }

    private static bool Contains(int[] cube, IReadOnlyList<bool> values)
    {
        for (int i = 0; i < cube.Length; i++)
        {
            if (cube[i] == -1) continue;
            if ((cube[i] == 1) != values[i]) return false;
        }
        return true;
    }

    /// <summary>a subsumes b when every valuation in b is in a.</summary>
    private static bool Subsumes(int[] a, int[] b)
    {
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] == -1) continue;
            if (a[i] != b[i]) return false;
        }
        return true;
    }

    /// <summary>
    /// Greedy prime-implicant style cover of <paramref name="target"/> (indices into
    /// <paramref name="feasible"/>): start from each uncovered target valuation
    /// and free atoms one at a time, deepest-mentioned first, as long as every
    /// feasible valuation inside the cube is still in the target. Deterministic;
    /// not guaranteed minimal in count, always sound (a cube never claims a
    /// feasible valuation outside the target).
    /// </summary>
    private static List<int[]> MinimiseCubes(List<IReadOnlyList<bool>> feasible, HashSet<int> target, int n)
    {
        var cubes = new List<int[]>();
        foreach (var vi in target.OrderBy(i => i))
        {
            if (cubes.Any(c => Contains(c, feasible[vi]))) continue;
            var cube = feasible[vi].Select(b => b ? 1 : 0).ToArray();
            for (int a = n - 1; a >= 0; a--)
            {
                var saved = cube[a];
                cube[a] = -1;
                bool ok = true;
                for (int j = 0; j < feasible.Count && ok; j++)
                {
                    if (Contains(cube, feasible[j]) && !target.Contains(j)) ok = false;
                }
                if (!ok) cube[a] = saved;
            }
            cubes.Add(cube);
        }
        return cubes.Where(c => !cubes.Any(o => !ReferenceEquals(o, c) && Subsumes(o, c))).ToList();
    }

    private static TotalityFinding Build(
        Arm arm, FindingKind kind, List<string> live, int[] cube, IReadOnlyList<string> ids,
        List<IReadOnlyList<bool>> feasibleValues, IReadOnlyList<AtomDomain.Valuation> vals, AtomDomain domain)
    {
        var when = new List<(string Atom, bool Value)>();
        var free = new List<string>();
        for (int i = 0; i < cube.Length; i++)
        {
            if (cube[i] == -1) free.Add(live[i]); else when.Add((live[i], cube[i] == 1));
        }

        // Witness: the first feasible valuation inside the cube, restricted to
        // the variables the fixed atoms read.
        var witnessVars = new HashSet<string>(when.SelectMany(w => domain.VariablesOf(w.Atom)), StringComparer.Ordinal);
        string witness = "";
        for (int j = 0; j < feasibleValues.Count; j++)
        {
            if (!Contains(cube, feasibleValues[j])) continue;
            var w = vals[j].Witness;
            var declared = domain.Variables.Select(v => v.Name).ToList();
            var ordered = declared.Where(w.ContainsKey)
                .Concat(w.Keys.Where(k => !declared.Contains(k)).OrderBy(k => k, StringComparer.Ordinal))
                .Where(witnessVars.Contains)
                .Select(k => $"{k}={domain.Render(k, w[k])}");
            witness = string.Join(", ", ordered);
            break;
        }

        var draft = new TotalityFinding
        {
            Kind = kind,
            SourcePath = arm.SourcePath,
            Machine = arm.Machine,
            State = arm.State,
            Subroutine = arm.Subroutine,
            Event = arm.Event,
            When = when,
            FreeAtoms = free,
            Transitions = ids,
            Witness = witness,
            Message = "",
        };
        return new TotalityFinding
        {
            Kind = draft.Kind,
            SourcePath = draft.SourcePath,
            Machine = draft.Machine,
            State = draft.State,
            Subroutine = draft.Subroutine,
            Event = draft.Event,
            When = draft.When,
            FreeAtoms = draft.FreeAtoms,
            Transitions = draft.Transitions,
            Witness = draft.Witness,
            Message = Render(draft),
        };
    }

    /// <summary>The cube as text: <c>when a=true, b=false (c, d: either)</c>.</summary>
    public static string RenderWhen(TotalityFinding f)
    {
        if (f.When.Count == 0 && f.FreeAtoms.Count == 0) return "with no guard atom in play";
        var sb = new StringBuilder();
        if (f.When.Count == 0)
        {
            sb.Append("for every valuation of ").Append(string.Join(", ", f.FreeAtoms));
            return sb.ToString();
        }
        sb.Append("when ").Append(string.Join(", ", f.When.Select(w => $"{w.Atom}={(w.Value ? "true" : "false")}")));
        if (f.FreeAtoms.Count > 0)
        {
            sb.Append(" (").Append(string.Join(", ", f.FreeAtoms)).Append(": either)");
        }
        return sb.ToString();
    }

    private static string Render(TotalityFinding f)
    {
        var what = f.Subroutine is null ? "transition" : "path";
        var sb = new StringBuilder();
        sb.Append(f.SourcePath).Append(": ");
        sb.Append(f.Kind == FindingKind.Hole ? "totality" : "determinism").Append(": ");
        sb.Append(f.Arm).Append(": ");
        if (f.Kind == FindingKind.Hole)
        {
            sb.Append("no ").Append(what).Append(" matches ").Append(RenderWhen(f));
        }
        else
        {
            sb.Append(what).Append("s ")
              .Append(string.Join(", ", f.Transitions.Select(t => $"`{t}`")))
              .Append(" all match ").Append(RenderWhen(f));
        }
        if (f.Witness.Length > 0) sb.Append("; witness ").Append(f.Witness);
        sb.Append(". ");
        sb.Append(f.Kind == FindingKind.Hole
            ? "The figure has no branch for this case. Fix the figure in packethacking/ax25spec (or mark the branch Undefined if the spec really leaves it open), or record it in codegen/lint-known-findings.yaml with an issue URL."
            : "The runtime would silently take the first match. Make the guards exclusive in the figure in packethacking/ax25spec, or record it in codegen/lint-known-findings.yaml with an issue URL.");
        return sb.ToString();
    }
}
