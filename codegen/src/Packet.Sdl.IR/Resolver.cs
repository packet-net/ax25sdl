using System.Text.RegularExpressions;

namespace Packet.Sdl.IR;

/// <summary>
/// Converts loaded-and-validated YAML pages into the language-neutral
/// <see cref="ResolvedPage"/> / <see cref="ResolvedSubroutinesPage"/> IR
/// that emitters consume. Run after <see cref="Validation"/>; assumes the
/// page structure is well-formed (every decision id referenced exists,
/// every action has a kind, etc.).
/// </summary>
public static class Resolver
{
    public static ResolvedPage Resolve(SdlPage page)
    {
        var decisionsById = page.Decisions!.ToDictionary(d => d.Id, d => d, StringComparer.Ordinal);
        return new ResolvedPage
        {
            Machine      = page.Machine,
            State        = page.State,
            Coverage     = page.Coverage,
            SourcePath   = page.SourcePath,
            SourceSpec   = page.Source!.Spec,
            SourceFigure = page.Source.Figure,
            SourceUrl    = page.Source.Url,
            Transitions  = page.Transitions!.Select(t => ResolveTransition(t, decisionsById)).ToList(),
            Saves        = page.Save is null ? Array.Empty<string>() : page.Save.ToList(),
        };
    }

    public static ResolvedSubroutinesPage Resolve(SubroutinePage page) => new()
    {
        Machine      = page.Machine,
        SourcePath   = page.SourcePath,
        SourceSpec   = page.Source!.Spec,
        SourceFigure = page.Source.Figure,
        SourceUrl    = page.Source.Url,
        Subroutines  = page.Subroutines.Select(ResolveSubroutine).ToList(),
    };

    private static ResolvedSubroutine ResolveSubroutine(SubroutineYamlEntry s)
    {
        var decisionsById = s.Decisions.ToDictionary(d => d.Id, d => d, StringComparer.Ordinal);
        return new ResolvedSubroutine
        {
            Name       = s.Name,
            Notes      = s.Notes,
            Paths      = s.Paths.Select(p => ResolveSubroutinePath(p, decisionsById)).ToList(),
            References = Array.Empty<ResolvedReference>(),  // subroutine-level refs not currently surfaced (path-level only)
        };
    }

    private static ResolvedSubroutinePath ResolveSubroutinePath(
        SubroutinePathYaml p,
        IReadOnlyDictionary<string, SdlDecision> decisionsById)
    {
        var predicates = new List<string>();
        var actions    = new List<ResolvedAction>();
        var loops      = new List<ResolvedLoop>();
        WalkPath(p.Path, decisionsById, predicates, actions, loops);
        return new ResolvedSubroutinePath
        {
            Id         = p.Id,
            Guard      = predicates.Count == 0 ? null : string.Join(" and ", predicates),
            Notes      = p.Notes,
            Actions    = actions,
            Loops      = loops,
            References = MapReferences(p.References),
        };
    }

    private static ResolvedTransition ResolveTransition(
        SdlTransition t,
        IReadOnlyDictionary<string, SdlDecision> decisionsById)
    {
        var predicates = new List<string>();
        var actions    = new List<ResolvedAction>();
        var loops      = new List<ResolvedLoop>();
        var undefined  = new List<ResolvedUndefinedBranch>();
        WalkPath(t.Path, decisionsById, predicates, actions, loops, undefined);
        return new ResolvedTransition
        {
            Id                = t.Id,
            On                = t.On,
            OnLabel           = t.OnLabel ?? "",
            Guard             = predicates.Count == 0 ? null : string.Join(" and ", predicates),
            Next              = t.Next,
            Notes             = t.Notes,
            Actions           = actions,
            Loops             = loops,
            References        = MapReferences(t.References),
            UndefinedBranches = undefined,
        };
    }

    private static IReadOnlyList<ResolvedReference> MapReferences(IEnumerable<SdlReference>? refs)
    {
        if (refs is null) return Array.Empty<ResolvedReference>();
        return refs.Select(r => new ResolvedReference(
            r.Source, r.Cite, r.Quote, r.Path, r.Function, r.Line, r.Note)).ToList();
    }

    /// <summary>
    /// Walks a path, accumulating guard predicates, flattening loop bodies
    /// into the action list, and recording loop ranges. Decision branches
    /// contribute a predicate (negated for the No branch). Loop bodies are
    /// action-only (validator enforces).
    /// </summary>
    private static void WalkPath(
        List<SdlPathStep> path,
        IReadOnlyDictionary<string, SdlDecision> decisionsById,
        List<string> predicates,
        List<ResolvedAction> actions,
        List<ResolvedLoop> loops,
        List<ResolvedUndefinedBranch>? undefined = null)
    {
        // Variables assigned by processing actions earlier in this path, mapped
        // to the bare-variable value assigned (or null when the right-hand side
        // is not a bare variable). The figure may draw a decision *after* such
        // an assignment — SDL semantics say it reads the post-assignment value —
        // but the guard is evaluated before any action runs, so we substitute
        // the assigned value into the guard predicate to preserve the figure's
        // meaning. (ax25sdl#53 — figc4.5 recovery-complete read stale V(a).)
        var assigned = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var step in path)
        {
            if (!string.IsNullOrWhiteSpace(step.Decision))
            {
                var decision = decisionsById[step.Decision!];
                if (step.Branch == "Undefined")
                {
                    // Spec-level undefined branch — don't contribute to the
                    // guard expression (no truth-value applies; the codegen
                    // emits a runtime throw for this transition). Record the
                    // decision so the emitter can quote it in the error.
                    undefined?.Add(new ResolvedUndefinedBranch(decision.Id, decision.Question, decision.Predicate));
                }
                else
                {
                    var pred = ResolveStaleReads(decision.Predicate, assigned, decision.Id);
                    predicates.Add(step.Branch == "Yes" ? pred : "not " + pred);
                }
            }
            else if (!string.IsNullOrWhiteSpace(step.LoopWhile))
            {
                var loopGuard = decisionsById[step.LoopWhile!];
                var startIndex = actions.Count;
                foreach (var body in step.Body!)
                {
                    if (!string.IsNullOrWhiteSpace(body.Action))
                    {
                        actions.Add(new ResolvedAction(body.Action!, ParseKind(body.Kind!)));
                        RecordAssignment(body.Action!, assigned);
                    }
                }
                // The continuing edge is the figure branch that loops back. Yes
                // (default) means "keep looping while the predicate holds"; No
                // means the predicate is the exit test, so negate it to get the
                // continue condition — same convention as decision-branch guards.
                // NOTE: a loop continue-predicate is re-evaluated by the runtime
                // each iteration against current state, so — unlike a decision
                // guard, which is hoisted ahead of the actions — it is never
                // stale and must NOT be substituted. (ax25sdl#53)
                var continueBranch = string.IsNullOrWhiteSpace(step.Branch) ? "Yes" : step.Branch!;
                var continuePredicate = continueBranch == "No" ? "not " + loopGuard.Predicate : loopGuard.Predicate;
                var testAtEnd = string.Equals(step.Test, "tail", StringComparison.Ordinal);
                loops.Add(new ResolvedLoop(startIndex, actions.Count - startIndex, continuePredicate, testAtEnd));
            }
            else
            {
                actions.Add(new ResolvedAction(step.Action!, ParseKind(step.Kind!)));
                RecordAssignment(step.Action!, assigned);
            }
        }
    }

    /// <summary>Spec variable spelling → guard-predicate token.</summary>
    private static readonly IReadOnlyDictionary<string, string> SpecToToken =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["V(s)"] = "vs", ["V(a)"] = "va", ["V(r)"] = "vr", ["N(r)"] = "nr", ["N(s)"] = "ns",
        };

    /// <summary>
    /// Records a processing action of the form "X := Y" / "X &lt;- Y" into the
    /// per-path assignment map: token → assigned bare-variable token, or null
    /// when the right-hand side is not a bare variable (e.g. "V(s) := 0").
    /// Non-assignment actions are ignored.
    /// </summary>
    private static void RecordAssignment(string action, Dictionary<string, string?> assigned)
    {
        var m = Regex.Match(action.Trim(),
            @"^(V\(s\)|V\(a\)|V\(r\)|N\(r\)|N\(s\))\s*(?::=|<-|=)\s*(.+)$");
        if (!m.Success) return;
        var lhs = SpecToToken[m.Groups[1].Value];
        var rhsRaw = m.Groups[2].Value.Trim();
        assigned[lhs] = SpecToToken.TryGetValue(rhsRaw, out var rhsTok) ? rhsTok : null;
    }

    /// <summary>
    /// Substitutes any variable assigned earlier in the path into a guard
    /// predicate, so a decision the figure draws after an assignment reads the
    /// post-assignment value even though the guard is evaluated before actions
    /// run. Throws if a stale read cannot be resolved to a bare-variable value
    /// (assignment from a non-variable expression, or an assignment cycle): such
    /// a table cannot be faithfully flattened to a pre-action guard and must not
    /// be emitted silently. (ax25sdl#53.)
    /// </summary>
    private static string ResolveStaleReads(
        string predicate,
        Dictionary<string, string?> assigned,
        string decisionId)
    {
        if (assigned.Count == 0) return predicate;
        var pred = predicate;
        for (var iter = 0; iter <= SpecToToken.Count; iter++)
        {
            var stale = ReadVars(pred).Where(assigned.ContainsKey).ToList();
            if (stale.Count == 0) return pred;
            foreach (var tok in stale)
            {
                var rhs = assigned[tok];
                if (rhs is null)
                    throw new InvalidOperationException(
                        $"decision '{decisionId}' guard '{predicate}' reads variable '{tok}' " +
                        $"that an earlier action in the same path assigns from a non-variable " +
                        $"expression; cannot derive a pre-action guard (ax25sdl#53).");
                pred = ReplaceToken(pred, tok, rhs);
            }
        }
        throw new InvalidOperationException(
            $"decision '{decisionId}' guard '{predicate}': unresolved stale variable read " +
            $"(possible assignment cycle) (ax25sdl#53).");
    }

    /// <summary>Bare-variable tokens a guard predicate reads (whole-token match).</summary>
    private static IEnumerable<string> ReadVars(string predicate) =>
        SpecToToken.Values.Where(tok => Regex.IsMatch(predicate, $@"(?:^|_){tok}(?:$|_)"));

    private static string ReplaceToken(string predicate, string tok, string rhs) =>
        Regex.Replace(predicate, $@"(?<=^|_){tok}(?=$|_)", rhs);

    public static ResolvedActionKind ParseKind(string kind) => kind switch
    {
        "signal_upper" => ResolvedActionKind.SignalUpper,
        "signal_lower" => ResolvedActionKind.SignalLower,
        "processing"   => ResolvedActionKind.Processing,
        "subroutine"   => ResolvedActionKind.Subroutine,
        "internal_out" => ResolvedActionKind.InternalOut,
        _ => throw new InvalidOperationException($"unknown action kind '{kind}'"),
    };
}
