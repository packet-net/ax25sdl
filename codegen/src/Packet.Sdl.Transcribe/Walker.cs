using System.Text.RegularExpressions;

namespace Packet.Sdl.Transcribe;

/// <summary>
/// Walks the graphml graph and builds the SDL IR (one SdlPage).
/// </summary>
public static class Walker
{
    private static readonly string[] LineSeparators = ["\r\n", "\n", "\r"];

    public static SdlPage Walk(GraphmlGraph graph, string graphmlFilename)
    {
        var (machine, state, figure) = MachineStateAndFigureFromFilename(graphmlFilename);

        var entry = FindEntryStateNode(graph, state);

        // Triggers are the immediate out-neighbours of the entry State,
        // sorted left-to-right by x position to match the figure's column
        // order.
        var triggerEdges = graph.OutgoingByNodeId[entry.Id]
            .Select(e => new { Edge = e, Node = graph.NodesById[e.Target] })
            .OrderBy(p => p.Node.X)
            .ToList();

        var page = new SdlPage
        {
            Machine = machine,
            State = state,
            Source = new PageSource
            {
                Spec = Packet.Sdl.IR.AnnexC.Spec,
                Figure = figure,
            },
        };

        var decisionsSeen = new Dictionary<string, Decision>();
        var loops = LoopRecovery.FindLoops(graph);
        int triggerIndex = 0;

        foreach (var trigger in triggerEdges)
        {
            triggerIndex++;
            var triggerNode = trigger.Node;
            var triggerEvent = EventResolver.ResolveTriggerEvent(triggerNode.Label, triggerNode.ShapeClass);

            // Save column: the trigger feeds directly into a Save shape with
            // no further outgoing edges. The event is queued, no transition
            // fires.
            if (LeadsToSaveTerminus(graph, triggerNode))
            {
                if (!page.Save.Contains(triggerEvent))
                    page.Save.Add(triggerEvent);
                continue;
            }

            // Decisions are recorded per-trigger so an "Able to Establish?"
            // decision under SABM gets a different id from the same question
            // under SABME — matches the existing yaml convention.
            var triggerPrefix = TriggerPrefixFromEvent(triggerEvent);

            // From the trigger, recursively walk paths. Decisions cause branching.
            var perTriggerTransitions = new List<(string BaseId, string Next, List<PathStep> Path)>();
            foreach (var (path, next, branchLabels) in WalkFromTrigger(graph, triggerNode, triggerPrefix, decisionsSeen, loops))
            {
                perTriggerTransitions.Add((
                    BuildTransitionId(triggerIndex, triggerEvent, branchLabels),
                    next,
                    path));
            }
            // Disambiguate base ids that collide. Collisions only happen when
            // one or more branches in a path is "Undefined" (figc4.5 has
            // decision diamonds with both edges labelled "undefined" — they
            // can't disambiguate at the branch-label level). Walk each
            // colliding group's paths forward and find the first path element
            // that differs; append a sanitised form to the id so consumers
            // can map the id back to a figure-recognisable element.
            foreach (var t in DisambiguateColliding(perTriggerTransitions, triggerEvent, triggerNode.Label))
                page.Transitions.Add(t);
        }

        // Order decisions by their first-encountered transition index.
        page.Decisions.AddRange(decisionsSeen.Values);

        // Drop decisions whose other branch was cycle-skipped. After cycle
        // truncation, the surviving branch's transition is effectively
        // unconditional — by the time the runtime reaches it, the loop
        // predicate has terminated — so the DecisionBranch step is
        // semantically redundant and codegen's bilateral-branch lint trips
        // on the orphaned one-sided decision. Strip both.
        PruneOneSidedDecisions(page);

        // Any transition landing at the UNDEFINED ON DIAGRAM sentinel marks
        // the page's cross-page consistency intentionally incomplete (no page
        // is ever transcribed for the sentinel state). coverage: partial
        // tells the codegen state-target lint to skip checks on this page.
        if (page.Transitions.Any(t => t.Next == UndefinedSentinel))
            page.Coverage = "partial";

        return page;
    }

    /// <summary>Sentinel state name yielded for paths terminating at an
    /// <c>UNDEFINED ON DIAGRAM</c> node in the source SDL.</summary>
    internal const string UndefinedSentinel = "Undefined";

    /// <summary>Branch label used when a decision's outgoing edge is itself
    /// labelled <c>undefined</c> / <c>Undefined</c> in the source graphml — the
    /// spec's marker for "behaviour undefined on this branch".</summary>
    internal const string UndefinedBranchLabel = "Undefined";

    private static bool IsUndefinedOnDiagramSink(GraphmlNode node) =>
        string.Equals(node.Label.Trim(), "UNDEFINED ON DIAGRAM", StringComparison.Ordinal);

    /// <summary>
    /// Build the final per-trigger transition list, replacing any base ids
    /// that collide with content-derived discriminators. Yes/No-only branch
    /// chains never collide; the only collisions in practice come from
    /// "Undefined" branches that share an edge label with a sibling edge.
    /// </summary>
    private static IEnumerable<Transition> DisambiguateColliding(
        List<(string BaseId, string Next, List<PathStep> Path)> raw,
        string triggerEvent, string triggerLabel)
    {
        // Group by base id, then within each colliding group derive a
        // suffix from the first path element that distinguishes them.
        var groups = raw.GroupBy(r => r.BaseId).ToList();
        foreach (var g in groups)
        {
            var items = g.ToList();
            if (items.Count == 1)
            {
                var only = items[0];
                yield return Build(only.BaseId, only.Next, only.Path);
                continue;
            }
            // Multiple paths share a base id. For each, find its first
            // disambiguating step relative to the others and append a
            // sanitised form to the id. If first-divergence-content still
            // collides (rare — two paths visit identically-named first
            // elements), fall back to a short content hash.
            var assignedIds = AssignDiscriminators(items);
            foreach (var (id, item) in assignedIds)
                yield return Build(id, item.Next, item.Path);
        }

        Transition Build(string id, string next, List<PathStep> path)
        {
            var t = new Transition
            {
                Id = id,
                On = triggerEvent,
                OnLabel = triggerLabel,
                Next = next,
            };
            t.Path.AddRange(path);
            return t;
        }
    }

    private static IEnumerable<(string Id, (string BaseId, string Next, List<PathStep> Path) Item)>
        AssignDiscriminators(List<(string BaseId, string Next, List<PathStep> Path)> items)
    {
        // For each path, derive a primary discriminator from the first step
        // whose textual content differs from at least one other path's same
        // step. If the primary still produces a collision, append a short
        // content hash of the full path for an absolute tie-break.
        var primaries = items.Select(it => FirstDivergingLabel(it.Path, items.Select(o => o.Path).ToList())).ToList();
        var counts = primaries.GroupBy(p => p).ToDictionary(g => g.Key, g => g.Count());
        for (int i = 0; i < items.Count; i++)
        {
            var primary = primaries[i];
            var id = items[i].BaseId + "_via_" + primary;
            if (counts[primary] > 1)
                id += "_" + ShortHash(items[i].Path);
            yield return (id, items[i]);
        }
    }

    /// <summary>
    /// Return a sanitised label drawn from the first path-step at which the
    /// supplied path differs from at least one of the alternatives. Used as
    /// a content-derived id suffix to disambiguate transitions whose branch
    /// chain alone doesn't distinguish them (Undefined branches).
    /// </summary>
    private static string FirstDivergingLabel(List<PathStep> path, List<List<PathStep>> alternatives)
    {
        int maxLen = Math.Max(path.Count, alternatives.Max(p => p.Count));
        for (int i = 0; i < maxLen; i++)
        {
            var mine = i < path.Count ? StepKey(path[i]) : "__end__";
            bool differs = false;
            foreach (var alt in alternatives)
            {
                if (alt == path) continue;
                var theirs = i < alt.Count ? StepKey(alt[i]) : "__end__";
                if (!string.Equals(mine, theirs, StringComparison.Ordinal)) { differs = true; break; }
            }
            if (differs) return SanitiseForId(StepDisplay(path.Count > i ? path[i] : null));
        }
        // Paths are identical at the step level — yield a placeholder; caller
        // will append a content hash below for an absolute tie-break.
        return "tie";
    }

    private static string StepKey(PathStep step) => step switch
    {
        ActionStep a       => "A:" + a.Action + "|" + a.Kind,
        DecisionBranch d   => "D:" + d.DecisionId + "|" + d.Branch,
        _ => "?",
    };

    private static string StepDisplay(PathStep? step) => step switch
    {
        ActionStep a       => a.Action,
        DecisionBranch d   => d.DecisionId + "_" + d.Branch.ToLowerInvariant(),
        null               => "end",
        _ => "?",
    };

    private static string SanitiseForId(string s)
    {
        // Keep ascii alphanumerics + underscore; collapse runs of anything
        // else; lowercase.
        var sb = new System.Text.StringBuilder();
        bool lastWasSep = true;
        foreach (var ch in s)
        {
            if (char.IsAsciiLetterOrDigit(ch)) { sb.Append(char.ToLowerInvariant(ch)); lastWasSep = false; }
            else if (!lastWasSep) { sb.Append('_'); lastWasSep = true; }
        }
        var trimmed = sb.ToString().Trim('_');
        // Cap length to keep ids readable in error messages.
        return trimmed.Length > 40 ? trimmed[..40] : trimmed;
    }

    private static string ShortHash(List<PathStep> path)
    {
        var canonical = string.Join("|", path.Select(StepKey));
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(bytes)[..8].ToLowerInvariant();
    }

    private static void PruneOneSidedDecisions(SdlPage page)
    {
        var branchesByDecision = new Dictionary<string, HashSet<string>>();
        foreach (var t in page.Transitions)
            foreach (var step in t.Path.OfType<DecisionBranch>())
            {
                if (!branchesByDecision.TryGetValue(step.DecisionId, out var set))
                    branchesByDecision[step.DecisionId] = set = new HashSet<string>(StringComparer.Ordinal);
                set.Add(step.Branch);
            }
        // A decision is "one-sided" only when exactly one Yes/No branch
        // was reached (the other was cycle-skipped). A decision whose
        // single observed branch is `Undefined` is NOT one-sided — it
        // represents the AX.25 spec marker where both edges share the
        // `Undefined` label deliberately, and pruning would strip the
        // decision-branch step the codegen relies on to emit a runtime
        // throw.
        var oneSided = branchesByDecision
            .Where(kv => kv.Value.Count < 2
                      && !kv.Value.Contains(UndefinedBranchLabel))
            .Select(kv => kv.Key)
            .ToHashSet(StringComparer.Ordinal);
        if (oneSided.Count == 0) return;

        page.Decisions.RemoveAll(d => oneSided.Contains(d.Id));
        foreach (var t in page.Transitions)
        {
            for (int i = t.Path.Count - 1; i >= 0; i--)
                if (t.Path[i] is DecisionBranch db && oneSided.Contains(db.DecisionId))
                    t.Path.RemoveAt(i);
        }
    }

    /// <summary>
    /// Resolves a graphml filename to its machine id, state and figure id
    /// through the Annex C table (<see cref="Packet.Sdl.IR.AnnexC"/>):
    /// "DataLink_Disconnected.graphml" gives ("data_link", "Disconnected",
    /// "figc4.1"); "LinkMux_Idle.graphml" gives ("link_mux", "Idle",
    /// "figc3.1"). The figure is looked up per machine and page, so a state
    /// name two machines share (Ready, Receiving) gets the right figure. An
    /// unknown prefix or state, or a subroutines page, throws.
    /// </summary>
    private static (string Machine, string State, string Figure) MachineStateAndFigureFromFilename(string filename)
    {
        var (machine, page) = Packet.Sdl.IR.AnnexC.ResolveGraphmlFilename(filename);
        if (page.IsSubroutines)
            throw new InvalidDataException(
                $"graphml filename '{filename}' names the {machine.Title} subroutines page, but the graphml has no Subroutine start node.");
        return (machine.Id, page.Key, page.Figure);
    }

    /// <summary>
    /// Finds the State node that's the entry to this page. There are
    /// usually many State-shape-class nodes (one per transition terminus),
    /// but only one has outgoing edges to triggers — the entry.
    /// </summary>
    private static GraphmlNode FindEntryStateNode(GraphmlGraph graph, string expectedState)
    {
        var stateNodes = graph.Nodes.Where(n => n.ShapeClass == "State").ToList();
        var candidates = stateNodes
            .Where(n => graph.OutgoingByNodeId[n.Id].Count > 0)
            .ToList();
        if (candidates.Count == 1) return candidates[0];
        if (candidates.Count == 0)
            throw new InvalidDataException(
                $"no State node with outgoing edges found — can't identify entry state.");
        // Multiple candidates: prefer the one whose label matches expectedState.
        var byLabel = candidates.FirstOrDefault(n => StateLabelMatches(n.Label, expectedState));
        if (byLabel is not null) return byLabel;
        throw new InvalidDataException(
            $"multiple State nodes with outgoing edges ({string.Join(", ", candidates.Select(c => c.Id))}); none match expected state '{expectedState}'.");
    }

    private static bool StateLabelMatches(string label, string expectedState)
    {
        // State labels are formatted like "0 Disconnected" (number + state name).
        // The number is the figc4.x table-column index.
        var trimmed = label.Trim();
        var spaceAt = trimmed.IndexOf(' ');
        if (spaceAt < 0) return trimmed == expectedState;
        return trimmed[(spaceAt + 1)..] == expectedState;
    }

    /// <summary>
    /// Walks every path from a trigger to a terminal state, enumerating
    /// decision branches into separate paths. Returns one tuple per path.
    /// </summary>
    private static IEnumerable<(List<PathStep> Path, string Next, List<string> BranchLabels)>
        WalkFromTrigger(GraphmlGraph graph, GraphmlNode triggerNode, string triggerPrefix,
                        Dictionary<string, Decision> decisionsSeen,
                        IReadOnlyDictionary<string, LoopRecovery.LoopInfo> loops)
    {
        return WalkFrom(graph, triggerNode, [], [], [triggerNode.Id], triggerPrefix, decisionsSeen, loops);
    }

    private static IEnumerable<(List<PathStep>, string, List<string>)>
        WalkFrom(GraphmlGraph graph, GraphmlNode current, List<PathStep> pathSoFar, List<string> branchLabelsSoFar,
                 HashSet<string> visitedInThisWalk, string triggerPrefix,
                 Dictionary<string, Decision> decisionsSeen,
                 IReadOnlyDictionary<string, LoopRecovery.LoopInfo> loops)
    {
        var outEdges = graph.OutgoingByNodeId[current.Id];

        // Reached a State terminus → yield the path.
        if (outEdges.Count == 0 && current.ShapeClass == "State")
        {
            var nextState = ParseStateName(current.Label);
            yield return (pathSoFar, nextState, branchLabelsSoFar);
            yield break;
        }

        // Spec-level "UNDEFINED ON DIAGRAM" sink — AX.25 v2.2 figc4.5 has a
        // node labelled this verbatim, marking a path the spec authors
        // deliberately left undefined. Yield with a sentinel state name and
        // let the caller mark the page coverage: partial.
        if (outEdges.Count == 0 && IsUndefinedOnDiagramSink(current))
        {
            yield return (pathSoFar, UndefinedSentinel, branchLabelsSoFar);
            yield break;
        }

        if (outEdges.Count == 0)
            throw new InvalidDataException(
                $"dead end at non-terminal node {current.Id} ('{current.Label}', d5='{current.ShapeClass}')");

        // Loop header: emit one loop_while step (its body + controlling test)
        // and continue the walk from the loop's exit edge. The body nodes and
        // the test decision are consumed here — never traversed by the normal
        // walk — so the back-edge is recovered as a loop rather than silently
        // skipped. See LoopRecovery / m0lte/ax25sdl#44.
        if (loops.TryGetValue(current.Id, out var loop))
        {
            var loopDecisionId = ContextualDecisionId(triggerPrefix, loop.TestNode.Label);
            RecordDecision(loop.TestNode, loopDecisionId, decisionsSeen);
            var loopStep = new LoopWhileStep(
                loopDecisionId, loop.ContinueBranch, loop.TestAtEnd,
                LoopRecovery.BuildBodySteps(loop.BodyNodes));
            var loopPath = new List<PathStep>(pathSoFar) { loopStep };
            var loopVisited = new HashSet<string>(visitedInThisWalk);
            foreach (var id in loop.LoopNodeIds) loopVisited.Add(id);
            loopVisited.Add(loop.ExitTargetId);
            foreach (var result in WalkFrom(graph, graph.NodesById[loop.ExitTargetId], loopPath, branchLabelsSoFar, loopVisited, triggerPrefix, decisionsSeen, loops))
                yield return result;
            yield break;
        }

        // Skip the trigger itself in the path (it becomes the `on:` field instead).
        // For every other node, append step(s) before recursing.
        var stepsToAppend = NodeToPathSteps(current, decisionsSeen);

        if (current.ShapeClass == "Test or decision")
        {
            // Each outgoing edge carries a branch label (Yes / No / sometimes other).
            // Append the decision-branch step then recurse along each edge. A
            // decision that is a loop header is intercepted above (LoopRecovery),
            // so any edge back to an already-visited node here is an unrecovered
            // cycle — fail loudly rather than silently drop it.
            var decisionId = ContextualDecisionId(triggerPrefix, current.Label);
            RecordDecision(current, decisionId, decisionsSeen);
            var (orderedEdges, resolvedBranchLabels) = OrderBranches(outEdges, ResolveBranchLabels(current, outEdges));
            for (int i = 0; i < orderedEdges.Count; i++)
            {
                var edge = orderedEdges[i];
                if (visitedInThisWalk.Contains(edge.Target))
                    throw new InvalidDataException(
                        $"{Path.GetFileName(graph.SourcePath)}: decision {current.Id} ('{current.Label}') edge {edge.Id} " +
                        $"leads back to already-visited {edge.Target} but was not recovered as a loop. Unhandled cycle.");
                var branchLabel = resolvedBranchLabels[i];
                if (string.IsNullOrWhiteSpace(branchLabel))
                    throw new InvalidDataException(
                        $"decision edge {edge.Id} (from {current.Id} '{current.Label}') has no branch label — every out-edge of a Test-or-decision must be labelled explicitly (Yes/No, or another figure-verbatim value).");
                // Strip annotations like "Yes (Note: assumed; missing from spec)"
                // down to bare "Yes" / "No" — the codegen validator requires
                // exactly these two values for branch labels.
                var canonicalBranch = NormaliseBranchLabel(branchLabel);
                var newPath = new List<PathStep>(pathSoFar);
                newPath.Add(new DecisionBranch(decisionId, canonicalBranch));
                var newBranchLabels = new List<string>(branchLabelsSoFar) { canonicalBranch };
                var newVisited = new HashSet<string>(visitedInThisWalk) { edge.Target };
                foreach (var result in WalkFrom(graph, graph.NodesById[edge.Target], newPath, newBranchLabels, newVisited, triggerPrefix, decisionsSeen, loops))
                    yield return result;
            }
            yield break;
        }

        // Non-decision: append this node's steps and continue along the
        // single outgoing edge (the SDL convention — every non-decision
        // node has exactly one out-edge).
        if (outEdges.Count > 1 && current.ShapeClass is not ("State" or "Subroutine start"))
            throw new InvalidDataException(
                $"non-decision node {current.Id} ('{current.Label}', d5='{current.ShapeClass}') has {outEdges.Count} outgoing edges — only Test-or-decision shapes are allowed to fan out.");

        var newPathLinear = new List<PathStep>(pathSoFar);
        newPathLinear.AddRange(stepsToAppend);

        if (current.ShapeClass == "State")
        {
            // State node mid-path — that means we're walking THROUGH another
            // state machine's state, which doesn't happen in well-formed pages.
            // Defensive: treat as a terminus.
            yield return (newPathLinear, ParseStateName(current.Label), branchLabelsSoFar);
            yield break;
        }

        var nextTarget = outEdges[0].Target;
        if (visitedInThisWalk.Contains(nextTarget))
            // A non-decision node forming a cycle. Loop bodies are recovered at
            // the header above, so reaching this point means an unguarded /
            // unrecovered back-edge — fail loudly rather than truncate silently.
            throw new InvalidDataException(
                $"{Path.GetFileName(graph.SourcePath)}: node {current.Id} ('{current.Label}') edge leads back to " +
                $"already-visited {nextTarget} but was not recovered as a loop. Unhandled cycle.");
        var nextNode = graph.NodesById[nextTarget];
        var newVisitedLinear = new HashSet<string>(visitedInThisWalk) { nextTarget };
        foreach (var result in WalkFrom(graph, nextNode, newPathLinear, branchLabelsSoFar, newVisitedLinear, triggerPrefix, decisionsSeen, loops))
            yield return result;
    }

    private static IEnumerable<PathStep> NodeToPathSteps(GraphmlNode node, Dictionary<string, Decision> decisionsSeen)
    {
        if (node.ShapeClass == "Test or decision")
        {
            // Decisions are handled by the caller (it emits one branch step
            // per outgoing edge AND records the decision via its contextual id).
            yield break;
        }

        var kind = EventResolver.ShapeClassToActionKind(node.ShapeClass);
        if (kind is null)
        {
            // State / trigger / subroutine-start / return — these aren't action nodes.
            // Walker handles them as path boundaries; nothing to emit.
            yield break;
        }

        // Multi-line node = one path step per line, in order. A figure box
        // that lists several statements one per line ("V(s) <- 0" over
        // "V(a) <- 0") becomes that many actions, so a graphml label must
        // only contain a hard line break where the figure starts a new
        // statement. A long single statement is typed on one line and left
        // for yEd to wrap. docs/sdl-transcription-runbook.md, "Labels".
        var lines = node.Label
            .Split(LineSeparators, StringSplitOptions.RemoveEmptyEntries)
            .Select(EventResolver.NormaliseActionLabel)
            .Where(s => !string.IsNullOrEmpty(s));
        foreach (var line in lines)
            yield return new ActionStep(line, kind);
    }

    /// <summary>Shared with SubroutinesWalker.</summary>
    internal static void RecordDecisionPublic(GraphmlNode node, string contextualId, Dictionary<string, Decision> decisionsSeen)
        => RecordDecision(node, contextualId, decisionsSeen);

    /// <summary>Shared with SubroutinesWalker.</summary>
    internal static string ContextualDecisionIdPublic(string triggerPrefix, string questionLabel)
        => ContextualDecisionId(triggerPrefix, questionLabel);

    /// <summary>Test entry point for the predicate-name normaliser. Returns the
    /// runtime-identifier form (preserves spec-variable casing like P / F / V / I /
    /// RR / SREJ) used as the <c>predicate:</c> field of an emitted decision.</summary>
    public static string DecisionPredicateForTests(string questionLabel)
        => DecisionPredicate(questionLabel);

    /// <summary>Test entry point for the decision-id normaliser. Returns the
    /// lower-case-everywhere form used as the <c>id:</c> field of an emitted
    /// decision (prefixed by trigger context at call sites).</summary>
    public static string NormaliseQuestionToIdForTests(string questionLabel)
        => NormaliseQuestionToId(questionLabel);

    private static void RecordDecision(GraphmlNode node, string contextualId, Dictionary<string, Decision> decisionsSeen)
    {
        if (decisionsSeen.ContainsKey(contextualId)) return;
        decisionsSeen[contextualId] = new Decision
        {
            Id = contextualId,
            Question = node.Label.Trim(),
            Predicate = DecisionPredicate(node.Label),
        };
    }

    private static string ContextualDecisionId(string triggerPrefix, string questionLabel)
    {
        // "SABM" + "Able to Establish?" → "sabm_able_to_establish"
        var bare = NormaliseQuestionToId(questionLabel);
        return string.IsNullOrEmpty(triggerPrefix) ? bare : $"{triggerPrefix}_{bare}";
    }

    private static string NormaliseQuestionToId(string label)
    {
        // "P == 1?" → "p_eq_1"   |   "Able to Establish?" → "able_to_establish"
        var snake = NormaliseQuestionShared(label).ToLowerInvariant();
        return Regex.Replace(snake, @"_+", "_").Trim('_');
    }

    private static string DecisionPredicate(string label)
    {
        // Predicates preserve original case for spec-variable tokens
        // (single-letter or all-caps — P, F, V, I) and lowercase everything
        // else. Matches the existing yaml convention: P_eq_1 (keep P), but
        // able_to_establish (lowercase the regular words).
        var snake = NormaliseQuestionShared(label);
        var collapsed = Regex.Replace(snake, @"_+", "_").Trim('_');
        var tokens = collapsed.Split('_', StringSplitOptions.RemoveEmptyEntries)
                              .Select(PreserveSpecVariable);
        return string.Join('_', tokens);
    }

    /// <summary>
    /// Shared question-text → identifier normalisation. Replaces comparison
    /// operators and boolean connectives with word forms, strips remaining
    /// non-alphanumeric / non-underscore characters, and leaves casing for
    /// the caller (id form lowercases everything; predicate form preserves
    /// spec-variable tokens P / F / V / I etc.).
    /// </summary>
    /// <remarks>
    /// The graphml decision diamonds use mathematical notation verbatim:
    /// <c>V(s) == V(a)?</c>, <c>N(s) &gt; V(r)+1?</c>, <c>P/F == 1?</c>,
    /// <c>F == 1 &amp; (frame=RR || frame=RNR || frame=I)?</c>, <c>Version 2.2?</c>.
    /// All of these need to become valid identifier strings so the
    /// downstream guard evaluator can tokenise them. Replacements applied in
    /// fixed order, longest-match first within each category so e.g.
    /// <c>&lt;=</c> doesn't get partially matched by the <c>&lt;</c> rule.
    /// </remarks>
    private static string NormaliseQuestionShared(string label)
    {
        var stripped = label.TrimEnd('?').Trim();
        var snake = Regex.Replace(stripped, @"\s+", "_")
                         // Comparison operators — two-char before one-char.
                         .Replace("==", "_eq_")
                         .Replace("!=", "_ne_")
                         .Replace(">=", "_ge_")
                         .Replace("<=", "_le_")
                         .Replace(">",  "_gt_")
                         .Replace("<",  "_lt_")
                         // Single '=' is comparison too (e.g. `frame=RR` inside
                         // a composite). Applied AFTER the two-char operators
                         // above so >=, <=, !=, == aren't double-replaced.
                         .Replace("=",  "_eq_")
                         // Boolean connectives — two-char before one-char.
                         .Replace("&&", "_and_")
                         .Replace("||", "_or_")
                         .Replace("&",  "_and_")
                         .Replace("|",  "_or_")
                         // Slash is P/F-style "or" in spec prose.
                         .Replace("/",  "_or_")
                         // Arithmetic / structural.
                         .Replace("+",  "_plus_")
                         .Replace(".",  "_")
                         .Replace("(",  "")
                         .Replace(")",  "")
                         .Replace(",",  "_");
        // Strip any remaining non-identifier characters as a backstop.
        snake = Regex.Replace(snake, @"[^A-Za-z0-9_]+", "_");
        return snake;
    }

    private static string PreserveSpecVariable(string token)
    {
        // All-caps token → leave as-is. Otherwise lowercase.
        return Regex.IsMatch(token, "^[A-Z]+[0-9]*$") ? token : token.ToLowerInvariant();
    }

    /// <summary>
    /// Turns an event name (DL_DISCONNECT_request, SABM_received) into a
    /// short prefix used for contextual decision ids (dl_disconnect, sabm).
    /// </summary>
    private static string TriggerPrefixFromEvent(string triggerEvent)
    {
        var lower = triggerEvent.ToLowerInvariant();
        // Strip common suffixes that don't add disambiguating value.
        foreach (var suffix in new[] { "_received", "_request", "_indication", "_confirm" })
        {
            if (lower.EndsWith(suffix, StringComparison.Ordinal))
            {
                lower = lower[..^suffix.Length];
                break;
            }
        }
        return lower;
    }

    private static string ParseStateName(string label)
    {
        // "0 Disconnected"          → "Disconnected"
        // "1 Awaiting Connection"   → "AwaitingConnection"
        // "5 Awaiting V2.2 Connection" → "AwaitingV22Connection" (drop dots)
        // "Awaiting 2.2 Connection" (LLM-era typo: missing V) → "AwaitingV22Connection"
        //
        // The source graphmls use multiple inconsistent labels for the same
        // SDL state. We normalise: strip leading figure-column number, fuse
        // spaces into PascalCase, drop dots, then apply a small alias map
        // for known typo variants. The aliases will go away when Tom
        // redraws the graphmls with consistent labels.
        var trimmed = label.Trim();
        var firstSpace = trimmed.IndexOf(' ');
        var stateText = firstSpace < 0 ? trimmed : trimmed[(firstSpace + 1)..];
        var raw = string.Concat(stateText.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Replace(".", "");
        return StateNameAliases.TryGetValue(raw, out var canon) ? canon : raw;
    }

    /// <summary>
    /// Maps inconsistent state-name labels in the current graphmls to their
    /// canonical form. Empty once the graphmls are consistent.
    /// </summary>
    private static readonly Dictionary<string, string> StateNameAliases = new(StringComparer.Ordinal)
    {
        ["Awaiting22Connection"] = "AwaitingV22Connection",  // missing "V" in some graphmls
    };

    /// <summary>
    /// Returns the raw labels for each out-edge of a decision. Both edges
    /// must be labelled — the walker no longer infers a missing label as
    /// the opposite of the labelled one, because the inference silently
    /// papered over a polarity ambiguity in figc4.4 (see #24, #29). The
    /// caller throws on any blank label, so unlabelled edges surface at
    /// transcribe time rather than at runtime.
    /// </summary>
    /// <summary>
    /// Puts a decision's out-edges in a fixed order, so the paths (and the
    /// transition order and subroutine path ids numbered from them) don't
    /// depend on the order the edges happen to be stored in the graphml, or
    /// on how the page is laid out: Yes first, then No, then any other label
    /// (such as Undefined). Edges with the same rank keep their file order.
    /// Shared with SubroutinesWalker.
    /// </summary>
    internal static (List<GraphmlEdge> Edges, List<string> Labels) OrderBranches(
        List<GraphmlEdge> outEdges, List<string> labels)
    {
        var ordered = outEdges.Zip(labels)
            .Select((pair, index) => (Edge: pair.First, Label: pair.Second, Index: index))
            .OrderBy(p => BranchRank(p.Label))
            .ThenBy(p => p.Index)
            .ToList();
        return (ordered.Select(p => p.Edge).ToList(), ordered.Select(p => p.Label).ToList());
    }

    private static int BranchRank(string? label) => NormaliseBranchLabel(label ?? "") switch
    {
        "Yes" => 0,
        "No" => 1,
        _ => 2,
    };

    private static List<string> ResolveBranchLabels(GraphmlNode decision, List<GraphmlEdge> outEdges)
        => outEdges.Select(e => e.Label).ToList();

    /// <summary>
    /// True if walking from the trigger reaches a "Save a signal until a
    /// new state is reached" shape with no further outgoing edges, optionally
    /// after passing through (single-out-edge) intermediate nodes.
    /// </summary>
    private static bool LeadsToSaveTerminus(GraphmlGraph graph, GraphmlNode trigger)
    {
        var cur = trigger;
        var guard = 0;
        while (true)
        {
            if (++guard > 50) return false;  // pathological case, fall through to normal walk
            var outs = graph.OutgoingByNodeId[cur.Id];
            if (outs.Count != 1) return false;
            cur = graph.NodesById[outs[0].Target];
            if (cur.ShapeClass == "Save a signal until a new state is reached")
                return graph.OutgoingByNodeId[cur.Id].Count == 0;
        }
    }

    private static string BuildTransitionId(int triggerIndex, string triggerEvent, List<string> branchLabels)
    {
        // "t01_dl_disconnect_request" or "t12_sabm_received_yes" / "_no" if branched.
        // Branch labels are sanitised to their leading word — authors sometimes
        // annotate a branch with parenthetical notes (e.g. "Yes (Note: assumed)")
        // which would break the id otherwise.
        var prefix = $"t{triggerIndex:D2}_{triggerEvent.ToLowerInvariant()}";
        if (branchLabels.Count == 0) return prefix;
        var sanitised = branchLabels.Select(SanitiseBranchLabelForId);
        return prefix + "_" + string.Join('_', sanitised);
    }

    private static string SanitiseBranchLabelForId(string label)
        => NormaliseBranchLabel(label).ToLowerInvariant();

    /// <summary>
    /// Reduces a free-form branch label to its canonical "Yes"/"No" form by
    /// taking the leading word. Authors sometimes annotate a branch with
    /// parenthetical context ("Yes (Note: assumed; missing from spec)") —
    /// those notes belong in commentary, not in the branch label itself.
    /// </summary>
    private static string NormaliseBranchLabel(string label)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in label.Trim())
        {
            if (char.IsLetter(c)) sb.Append(c);
            else break;
        }
        var bare = sb.ToString();
        // Title-case it: "yes" → "Yes" / "no" → "No".
        if (bare.Length == 0) return "";
        return char.ToUpperInvariant(bare[0]) + bare[1..].ToLowerInvariant();
    }
}
