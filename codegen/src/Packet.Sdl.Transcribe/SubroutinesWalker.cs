namespace Packet.Sdl.Transcribe;

/// <summary>
/// Walks an SDL subroutines page (one with multiple Subroutine-start →
/// Return-from-Subroutine chains; no entry State node). Distinct from
/// the state-page walker because:
///   - there are no triggers (subroutines are CALLED, not event-driven)
///   - each subroutine has its own start node and one+ return terminus
///   - the yaml shape is sdl-subroutines.schema.json, not sdl-machine
/// </summary>
public static class SubroutinesWalker
{
    public static bool IsSubroutinesPage(GraphmlGraph graph) =>
        graph.Nodes.Any(n => n.ShapeClass == "Subroutine start");

    public static SubroutinePage Walk(GraphmlGraph graph, string graphmlFilename)
    {
        var (machine, figure) = MachineAndFigureFromFilename(graphmlFilename);
        var page = new SubroutinePage
        {
            Machine = machine,
            Source = new PageSource
            {
                Spec = Packet.Sdl.IR.AnnexC.Spec,
                Figure = figure,
            },
        };

        var subroutineStarts = graph.Nodes
            .Where(n => n.ShapeClass == "Subroutine start")
            .OrderBy(n => n.Y).ThenBy(n => n.X)
            .ToList();

        var loops = LoopRecovery.FindLoops(graph);

        foreach (var start in subroutineStarts)
        {
            var name = NormaliseSubroutineName(start.Label);
            var sub = new Subroutine { Name = name };
            // Each subroutine has its own decisions dictionary — per the
            // sdl-subroutines schema, decisions are nested under the
            // subroutine, not at page level.
            var decisionsSeen = new Dictionary<string, Decision>();
            int pathIndex = 0;
            var startEdges = graph.OutgoingByNodeId[start.Id];
            // A start node with no edge, or a path that can't be walked
            // (typically a missing edge), is a hard error: skipping the
            // subroutine would silently drop it from the page.
            if (startEdges.Count == 0)
                throw new InvalidDataException(
                    $"subroutine '{name}' (start node {start.Id}) has no outgoing edge.");

            try
            {
                foreach (var (steps, branchLabels) in WalkFromSubroutineStart(graph, start, name, decisionsSeen, loops))
                {
                    pathIndex++;
                    var pathId = BuildSubroutinePathId(pathIndex, name, branchLabels);
                    var p = new SubroutinePath { Id = pathId };
                    p.Steps.AddRange(steps);
                    sub.Paths.Add(p);
                }
            }
            catch (InvalidDataException ex)
            {
                throw new InvalidDataException($"subroutine '{name}' (start node {start.Id}): {ex.Message}", ex);
            }

            sub.Decisions.AddRange(decisionsSeen.Values);
            page.Subroutines.Add(sub);
        }

        return page;
    }

    /// <summary>
    /// "DataLink_Subroutines.graphml" gives ("data_link", "figc4.7");
    /// "ManagementDataLink_T1NegotiationSubroutines.graphml" gives
    /// ("management_data_link", "figc5.8"). Looked up in the Annex C table;
    /// an unknown prefix or page, or a state page, throws.
    /// </summary>
    private static (string Machine, string Figure) MachineAndFigureFromFilename(string filename)
    {
        var (machine, page) = Packet.Sdl.IR.AnnexC.ResolveGraphmlFilename(filename);
        if (!page.IsSubroutines)
            throw new InvalidDataException(
                $"graphml filename '{filename}' names the {machine.Title} {page.Key} state, but the graphml is drawn as a subroutines page.");
        return (machine.Id, page.Figure);
    }

    /// <summary>
    /// "N(r) Error Recovery" → "N_r_Error_Recovery"
    /// "UI Check"            → "UI_Check"
    /// "Set Version 2.0"     → "Set_Version_2_0"
    /// Matches sdl-subroutines.schema.json's `name` pattern
    /// `^[A-Z][A-Za-z0-9_]*$` (PascalCase with underscores).
    /// Parens become word separators so V(r), N(r), etc. tokenise into
    /// their subscripts (V_r, N_r) rather than being collapsed (Vr, Nr).
    /// </summary>
    private static string NormaliseSubroutineName(string label)
    {
        var stripped = label.Trim()
                            .Replace("(", " ")
                            .Replace(")", " ")
                            .Replace(".", "_");
        return string.Join('_', stripped.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// Walks every path from a subroutine-start node to a Return-from-Subroutine
    /// terminus. Decision branches produce multiple paths just like state-page
    /// transitions. Cycle-back edges are skipped (same convention).
    /// </summary>
    private static IEnumerable<(List<PathStep> Steps, List<string> BranchLabels)>
        WalkFromSubroutineStart(GraphmlGraph graph, GraphmlNode start, string subroutineName,
                                Dictionary<string, Decision> decisionsSeen,
                                IReadOnlyDictionary<string, LoopRecovery.LoopInfo> loops)
    {
        // Prefix decisions with the subroutine name so the same question text
        // gets a different id in different subroutines (mirrors the state-page
        // convention with trigger prefix).
        var triggerPrefix = subroutineName.ToLowerInvariant();
        return WalkFrom(graph, start, [], [], [start.Id], triggerPrefix, decisionsSeen, loops);
    }

    private static IEnumerable<(List<PathStep>, List<string>)>
        WalkFrom(GraphmlGraph graph, GraphmlNode current, List<PathStep> stepsSoFar, List<string> branchLabelsSoFar,
                 HashSet<string> visited, string triggerPrefix, Dictionary<string, Decision> decisionsSeen,
                 IReadOnlyDictionary<string, LoopRecovery.LoopInfo> loops)
    {
        // Reached a Return → emit the path.
        if (current.ShapeClass == "Return from Subroutine")
        {
            yield return (stepsSoFar, branchLabelsSoFar);
            yield break;
        }

        var outEdges = graph.OutgoingByNodeId[current.Id];
        if (outEdges.Count == 0)
            throw new InvalidDataException(
                $"dead end at non-Return node {current.Id} ('{current.Label}', d5='{current.ShapeClass}')");

        // Loop header: emit one loop_while step and continue from the exit edge
        // (see LoopRecovery / m0lte/ax25sdl#44 — Invoke_Retransmission is the
        // tail-test do-while case that lives on a subroutine page).
        if (loops.TryGetValue(current.Id, out var loop))
        {
            var loopDecisionId = Walker.ContextualDecisionIdPublic(triggerPrefix, loop.TestNode.Label);
            Walker.RecordDecisionPublic(loop.TestNode, loopDecisionId, decisionsSeen);
            var loopStep = new LoopWhileStep(
                loopDecisionId, loop.ContinueBranch, loop.TestAtEnd,
                LoopRecovery.BuildBodySteps(loop.BodyNodes));
            var loopSteps = new List<PathStep>(stepsSoFar) { loopStep };
            var loopVisited = new HashSet<string>(visited);
            foreach (var id in loop.LoopNodeIds) loopVisited.Add(id);
            loopVisited.Add(loop.ExitTargetId);
            foreach (var r in WalkFrom(graph, graph.NodesById[loop.ExitTargetId], loopSteps, branchLabelsSoFar, loopVisited, triggerPrefix, decisionsSeen, loops))
                yield return r;
            yield break;
        }

        // Skip the start node itself in the steps (just like state-page triggers).
        var stepsToAppend = current.ShapeClass == "Subroutine start"
            ? Enumerable.Empty<PathStep>()
            : NodeToPathSteps(current);

        if (current.ShapeClass == "Test or decision")
        {
            var decisionId = Walker.ContextualDecisionIdPublic(triggerPrefix, current.Label);
            Walker.RecordDecisionPublic(current, decisionId, decisionsSeen);
            var (orderedEdges, resolvedLabels) = Walker.OrderBranches(outEdges, ResolveBranchLabels(outEdges));
            for (int i = 0; i < orderedEdges.Count; i++)
            {
                var edge = orderedEdges[i];
                if (visited.Contains(edge.Target))
                    throw new InvalidDataException(
                        $"{Path.GetFileName(graph.SourcePath)}: decision {current.Id} ('{current.Label}') edge {edge.Id} " +
                        $"leads back to already-visited {edge.Target} but was not recovered as a loop. Unhandled cycle.");
                var branchLabel = resolvedLabels[i];
                if (string.IsNullOrWhiteSpace(branchLabel))
                    throw new InvalidDataException(
                        $"decision edge {edge.Id} (from {current.Id} '{current.Label}') has no Yes/No label");
                var newSteps = new List<PathStep>(stepsSoFar) { new DecisionBranch(decisionId, branchLabel) };
                var newBranchLabels = new List<string>(branchLabelsSoFar) { branchLabel };
                var newVisited = new HashSet<string>(visited) { edge.Target };
                foreach (var r in WalkFrom(graph, graph.NodesById[edge.Target], newSteps, newBranchLabels, newVisited, triggerPrefix, decisionsSeen, loops))
                    yield return r;
            }
            yield break;
        }

        if (outEdges.Count > 1)
            throw new InvalidDataException(
                $"non-decision node {current.Id} ('{current.Label}', d5='{current.ShapeClass}') has {outEdges.Count} outgoing edges");

        var newSteps2 = new List<PathStep>(stepsSoFar);
        newSteps2.AddRange(stepsToAppend);
        var nextTarget = outEdges[0].Target;
        if (visited.Contains(nextTarget))
            throw new InvalidDataException(
                $"{Path.GetFileName(graph.SourcePath)}: node {current.Id} ('{current.Label}') edge leads back to " +
                $"already-visited {nextTarget} but was not recovered as a loop. Unhandled cycle.");
        var newVisited2 = new HashSet<string>(visited) { nextTarget };
        foreach (var r in WalkFrom(graph, graph.NodesById[nextTarget], newSteps2, branchLabelsSoFar, newVisited2, triggerPrefix, decisionsSeen, loops))
            yield return r;
    }

    private static IEnumerable<PathStep> NodeToPathSteps(GraphmlNode node)
    {
        if (node.ShapeClass == "Test or decision") yield break;
        var kind = EventResolver.ShapeClassToActionKind(node.ShapeClass);
        if (kind is null) yield break;
        // One action per line of the label, as on state pages (see
        // Walker.NodeToPathSteps).
        var lines = node.Label
            .Split(["\r\n", "\n", "\r"], StringSplitOptions.RemoveEmptyEntries)
            .Select(EventResolver.NormaliseActionLabel)
            .Where(s => !string.IsNullOrEmpty(s));
        foreach (var line in lines)
            yield return new ActionStep(line, kind);
    }

    private static List<string> ResolveBranchLabels(List<GraphmlEdge> outEdges)
    {
        var labels = outEdges.Select(e => e.Label).ToList();
        if (outEdges.Count != 2) return labels;
        var blanks = labels.Count(string.IsNullOrWhiteSpace);
        if (blanks != 1) return labels;
        var labelled = labels.First(l => !string.IsNullOrWhiteSpace(l));
        var opposite = labelled.Equals("Yes", StringComparison.OrdinalIgnoreCase) ? "No"
                     : labelled.Equals("No",  StringComparison.OrdinalIgnoreCase) ? "Yes"
                     : "";
        if (string.IsNullOrEmpty(opposite)) return labels;
        return labels.Select(l => string.IsNullOrWhiteSpace(l) ? opposite : l).ToList();
    }

    private static string BuildSubroutinePathId(int pathIndex, string subroutineName, List<string> branchLabels)
    {
        var idx = pathIndex.ToString("D2", System.Globalization.CultureInfo.InvariantCulture);
        var nameLower = subroutineName.ToLowerInvariant();
        if (branchLabels.Count == 0) return $"t{idx}_{nameLower}";
        return $"t{idx}_{nameLower}_" + string.Join('_', branchLabels.Select(b => b.ToLowerInvariant()));
    }
}
