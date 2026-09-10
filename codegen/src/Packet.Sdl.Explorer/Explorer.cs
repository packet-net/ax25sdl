using System.Globalization;
using System.Text;
using Packet.Sdl.Interpreter;

namespace Packet.Sdl.Explorer;

/// <summary>Overall outcome of one exploration.</summary>
public enum Outcome
{
    /// <summary>The whole bounded space was explored and no invariant fired.</summary>
    NoViolation,
    /// <summary>An invariant fired; the shortest counterexample is in the trace.</summary>
    Violation,
    /// <summary>A depth or visited-state bound was hit before the space was exhausted; the liveness checks were skipped.</summary>
    BoundHit,
}

/// <summary>The result of one exploration, with a readable counterexample when there is one.</summary>
public sealed record ExplorationResult(
    Outcome Outcome,
    Violation? Violation,
    IReadOnlyList<StepRecord> Trace,
    int StatesExplored,
    int EdgesExplored,
    int MaxDepthReached,
    bool DepthBoundHit,
    bool StateBoundHit,
    string? Note)
{
    /// <summary>Number of steps in the counterexample (0 when there is none).</summary>
    public int CounterexampleLength => Trace.Count;

    public string Render()
    {
        var sb = new StringBuilder();
        switch (Outcome)
        {
            case Outcome.NoViolation:
                sb.Append("NO VIOLATION: the bounded space was explored exhaustively");
                break;
            case Outcome.Violation:
                sb.Append("VIOLATION: ").Append(Violation!.Kind);
                if (Violation.Station is not null) sb.Append(" at station ").Append(Violation.Station);
                sb.Append('\n').Append(Violation.Message);
                break;
            case Outcome.BoundHit:
                sb.Append("BOUND HIT: ").Append(DepthBoundHit ? "depth bound" : "visited-state bound").Append(" reached; no safety violation seen, liveness not checked");
                break;
            default:
                break;
        }
        sb.Append("\nstates explored: ").Append(StatesExplored.ToString(CultureInfo.InvariantCulture))
          .Append(", edges: ").Append(EdgesExplored.ToString(CultureInfo.InvariantCulture))
          .Append(", max depth: ").Append(MaxDepthReached.ToString(CultureInfo.InvariantCulture));
        if (Note is not null) sb.Append('\n').Append(Note);
        if (Trace.Count > 0)
        {
            const int witnessTail = 12;
            var skip = Outcome == Outcome.BoundHit && Trace.Count > witnessTail ? Trace.Count - witnessTail : 0;
            sb.Append(Outcome == Outcome.BoundHit ? "\nwitness path (" : "\ncounterexample (")
              .Append(Trace.Count.ToString(CultureInfo.InvariantCulture)).Append(" steps");
            if (skip > 0) sb.Append(", first ").Append(skip.ToString(CultureInfo.InvariantCulture)).Append(" omitted");
            sb.Append("):");
            foreach (var step in Trace.Skip(skip)) sb.Append('\n').Append(step.Render());
        }
        return sb.ToString();
    }
}

/// <summary>
/// Breadth-first exploration of the two-station model from the seeded
/// state. Safety invariants are checked on every edge and end the search
/// with the shortest counterexample. After the bounded space is exhausted,
/// the reachable-state graph is analysed for liveness: from every state a
/// fault-free path must reach quiescence (no deadlock, no livelock), and
/// optionally one that takes no T1 expiry (selective-recovery progress).
/// </summary>
public static class Explorer
{
    private sealed class Node
    {
        public int Parent;
        public Move Move;
        public int Depth;
        public bool Quiescent;
        public bool Terminal;
        public bool Expanded;
        public List<(int Target, MoveKind Kind)>? Edges;
    }

    public static ExplorationResult Run(ExplorerOptions options, TableSet? tables = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        tables ??= TableLoader.Load(options.TablesDir);
        var stepper = new Stepper(options, tables);
        var seed = stepper.Seed();

        var nodes = new List<Node> { new() { Parent = -1, Depth = 0, Quiescent = stepper.IsQuiescent(seed) } };
        var index = new Dictionary<string, int>(StringComparer.Ordinal) { [seed.Fingerprint()] = 0 };
        var frontier = new Queue<(int Index, SystemState State)>();
        frontier.Enqueue((0, seed));

        var edges = 0;
        var maxDepth = 0;
        var depthBoundHit = false;
        var stateBoundHit = false;

        while (frontier.Count > 0)
        {
            var (idx, state) = frontier.Dequeue();
            var node = nodes[idx];
            if (node.Depth >= options.MaxDepth)
            {
                depthBoundHit = true;
                continue;
            }

            var moves = stepper.EnabledMoves(state);
            node.Terminal = moves.Count == 0;
            node.Expanded = true;
            node.Edges = new List<(int, MoveKind)>(moves.Count);

            foreach (var move in moves)
            {
                var outcome = stepper.Apply(state, move, node.Depth + 1);
                edges++;
                if (outcome.Violation is not null)
                {
                    var path = PathTo(nodes, idx);
                    path.Add(move);
                    var (trace, _) = Replay(stepper, path);
                    return new ExplorationResult(Outcome.Violation, outcome.Violation, trace, nodes.Count, edges, Math.Max(maxDepth, node.Depth + 1), depthBoundHit, stateBoundHit, null);
                }

                var fp = outcome.Next.Fingerprint();
                if (!index.TryGetValue(fp, out var target))
                {
                    target = nodes.Count;
                    nodes.Add(new Node { Parent = idx, Move = move, Depth = node.Depth + 1, Quiescent = stepper.IsQuiescent(outcome.Next) });
                    index[fp] = target;
                    maxDepth = Math.Max(maxDepth, node.Depth + 1);
                    frontier.Enqueue((target, outcome.Next));
                }
                node.Edges.Add((target, move.Kind));

                if (nodes.Count >= options.MaxStates)
                {
                    stateBoundHit = true;
                    break;
                }
            }

            if (stateBoundHit) break;
        }

        // Deadlock: a terminal, non-quiescent state. Sound even under a bound
        // (the state is real and has no move), so it is checked first.
        if (options.Invariants.HasFlag(Invariants.Deadlock))
        {
            for (var i = 0; i < nodes.Count; i++)
            {
                if (nodes[i].Expanded && nodes[i].Terminal && !nodes[i].Quiescent)
                {
                    var (trace, final) = Replay(stepper, PathTo(nodes, i));
                    var v = new Violation(Invariants.Deadlock, null,
                        "deadlock: this state has no enabled move and is not quiescent (" + Describe(stepper, final) + ")");
                    return new ExplorationResult(Outcome.Violation, v, trace, nodes.Count, edges, maxDepth, depthBoundHit, stateBoundHit, null);
                }
            }
        }

        if (depthBoundHit || stateBoundHit)
        {
            // Witness: the path to the first state at the depth bound, so a
            // runaway (a chain that never repeats a state) can be read off.
            IReadOnlyList<StepRecord> witness = Array.Empty<StepRecord>();
            if (depthBoundHit)
            {
                var at = nodes.FindIndex(n => n.Depth >= options.MaxDepth);
                if (at >= 0) witness = Replay(stepper, PathTo(nodes, at)).Trace;
            }
            return new ExplorationResult(Outcome.BoundHit, null, witness, nodes.Count, edges, maxDepth, depthBoundHit, stateBoundHit,
                depthBoundHit
                    ? "some states at the depth bound were not expanded; raise --max-depth or shrink the scenario (the trace below is a witness path to one such state)"
                    : "the visited-state cap was reached; raise --max-states or shrink the scenario");
        }

        // Liveness over the whole reachable graph, following non-fault edges
        // only ("the channel eventually stops faulting"): every state must be
        // able to reach a quiescent state.
        if (options.Invariants.HasFlag(Invariants.Quiescence))
        {
            var reach = CanReachQuiescence(nodes, excludeT1: false);
            var witness = PickWitness(nodes, reach, excludeT1: false);
            if (witness >= 0)
            {
                var (trace, final) = Replay(stepper, PathTo(nodes, witness));
                var v = new Violation(Invariants.Quiescence, null,
                    "quiescence unreachable: from this state no fault-free path leads to a quiescent state (both Connected, nothing in flight or queued, V(s)=V(a) on both, all data delivered). " +
                    Describe(stepper, final));
                return new ExplorationResult(Outcome.Violation, v, trace, nodes.Count, edges, maxDepth, false, false, null);
            }

            if (options.Invariants.HasFlag(Invariants.SelectiveProgress))
            {
                var reachNoT1 = CanReachQuiescence(nodes, excludeT1: true);
                witness = PickWitness(nodes, reachNoT1, excludeT1: true);
                if (witness >= 0)
                {
                    var (trace, final) = Replay(stepper, PathTo(nodes, witness));
                    var v = new Violation(Invariants.SelectiveProgress, null,
                        "timer-free progress: from this state quiescence is reachable only through a timer expiry (T1, or T3 when enabled); no fault-free, timer-free path completes the recovery (data phase) or the connection (connect phase). " +
                        Describe(stepper, final));
                    return new ExplorationResult(Outcome.Violation, v, trace, nodes.Count, edges, maxDepth, false, false, null);
                }
            }
        }

        return new ExplorationResult(Outcome.NoViolation, null, Array.Empty<StepRecord>(), nodes.Count, edges, maxDepth, false, false, null);
    }

    /// <summary>
    /// The state to report for a liveness failure: the shallowest state that
    /// cannot reach quiescence AND has no non-fault (and, when timers are
    /// excluded, non-timer) edge to a different state, i.e. where the model
    /// is actually stuck; failing that (a livelock cycle), the shallowest
    /// non-reaching state. Returns -1 when every state can reach quiescence.
    /// The seed itself is usually non-reaching too, but the stuck state is
    /// the one that reads as a counterexample.
    /// </summary>
    private static int PickWitness(List<Node> nodes, bool[] reach, bool excludeT1)
    {
        var first = -1;
        for (var i = 0; i < nodes.Count; i++)
        {
            if (reach[i]) continue;
            if (first < 0) first = i;
            var e = nodes[i].Edges;
            var stuck = true;
            if (e is not null)
            {
                foreach (var (target, kind) in e)
                {
                    if (kind is MoveKind.Drop or MoveKind.Duplicate or MoveKind.Reorder) continue;
                    if (excludeT1 && kind is MoveKind.T1Expiry or MoveKind.T3Expiry) continue;
                    if (target != i) { stuck = false; break; }
                }
            }
            if (stuck) return i;
        }
        return first;
    }

    /// <summary>Reverse reachability from the quiescent states over the non-fault edges; with <paramref name="excludeT1"/> the timer edges (T1, and T3 when enabled) are left out too.</summary>
    private static bool[] CanReachQuiescence(List<Node> nodes, bool excludeT1)
    {
        var reverse = new List<int>[nodes.Count];
        for (var i = 0; i < nodes.Count; i++) reverse[i] = new List<int>();
        for (var i = 0; i < nodes.Count; i++)
        {
            var e = nodes[i].Edges;
            if (e is null) continue;
            foreach (var (target, kind) in e)
            {
                if (kind is MoveKind.Drop or MoveKind.Duplicate or MoveKind.Reorder) continue;
                if (excludeT1 && kind is MoveKind.T1Expiry or MoveKind.T3Expiry) continue;
                reverse[target].Add(i);
            }
        }

        var reach = new bool[nodes.Count];
        var work = new Queue<int>();
        for (var i = 0; i < nodes.Count; i++)
        {
            if (nodes[i].Quiescent)
            {
                reach[i] = true;
                work.Enqueue(i);
            }
        }
        while (work.Count > 0)
        {
            var n = work.Dequeue();
            foreach (var p in reverse[n])
            {
                if (reach[p]) continue;
                reach[p] = true;
                work.Enqueue(p);
            }
        }
        return reach;
    }

    private static List<Move> PathTo(List<Node> nodes, int idx)
    {
        var path = new List<Move>();
        for (var i = idx; nodes[i].Parent >= 0; i = nodes[i].Parent) path.Add(nodes[i].Move);
        path.Reverse();
        return path;
    }

    private static (IReadOnlyList<StepRecord> Trace, SystemState Final) Replay(Stepper stepper, List<Move> path)
    {
        var state = stepper.Seed();
        var trace = new List<StepRecord>(path.Count);
        var n = 1;
        foreach (var move in path)
        {
            var outcome = stepper.Apply(state, move, n++);
            trace.Add(outcome.Record);
            state = outcome.Next;
        }
        return (trace, state);
    }

    private static string Describe(Stepper stepper, SystemState s)
    {
        var moves = stepper.EnabledMoves(s);
        var sb = new StringBuilder();
        sb.Append("in flight A->B: [").Append(string.Join(", ", s.ToB.Select(f => f.Render()))).Append("], B->A: [")
          .Append(string.Join(", ", s.ToA.Select(f => f.Render()))).Append("]; delivered at A: ")
          .Append(s.DeliveredAtA.ToString(CultureInfo.InvariantCulture)).Append('/').Append(stepper.SubmittedB.Count.ToString(CultureInfo.InvariantCulture))
          .Append(", at B: ").Append(s.DeliveredAtB.ToString(CultureInfo.InvariantCulture)).Append('/').Append(stepper.SubmittedA.Count.ToString(CultureInfo.InvariantCulture))
          .Append("; enabled moves: ").Append(moves.Count == 0 ? "(none)" : string.Join(", ", moves.Select(m => m.ToString())));
        return sb.ToString();
    }
}
