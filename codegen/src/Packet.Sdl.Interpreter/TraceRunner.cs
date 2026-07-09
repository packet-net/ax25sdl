using System.Globalization;
using System.Text;

namespace Packet.Sdl.Interpreter;

/// <summary>Outcome of running one trace: pass, or fail with a message pinpointing the first divergence.</summary>
public sealed record TraceResult(bool Passed, string? Message)
{
    public static TraceResult Pass() => new(true, null);
    public static TraceResult Fail(string message) => new(false, message);
}

/// <summary>
/// Replays a golden trace against a <see cref="DataLinkMachine"/> built from
/// the JSON tables and reports the first divergence. The
/// <see cref="TraceDoc.ExpectedFailure"/> (xfail) policy is applied by the
/// caller (the test glue), not here — this runner just says pass/fail.
/// </summary>
public static class TraceRunner
{
    public static TraceResult Run(TraceDoc trace, TableSet tables)
    {
        ArgumentNullException.ThrowIfNull(trace);
        ArgumentNullException.ThrowIfNull(tables);

        if (!string.Equals(trace.Machine, tables.Machine, StringComparison.Ordinal))
            return TraceResult.Fail($"trace targets machine `{trace.Machine}` but the loaded tables are `{tables.Machine}`");

        DataLinkMachine machine;
        try
        {
            machine = new DataLinkMachine(tables, trace.Initial.State);
            foreach (var (key, value) in trace.Initial.Variables ?? new Dictionary<string, string>())
                machine.SetVariable(key, value);
            foreach (var (timer, status) in trace.Initial.Timers ?? new Dictionary<string, string>())
                machine.SetTimer(timer, ParseTimerStatus(timer, status));
        }
        catch (InvalidOperationException ex)
        {
            return TraceResult.Fail($"initial state: {ex.Message}");
        }

        for (var i = 0; i < trace.Steps.Count; i++)
        {
            var step = trace.Steps[i];
            var label = $"step {i + 1}" + (step.Name is null ? "" : $" ({step.Name})") + $" [{step.Inject.Event}]";

            DispatchResult result;
            try
            {
                result = machine.Dispatch(new EventInput(
                    Event: step.Inject.Event,
                    Pf: step.Inject.Pf,
                    Command: step.Inject.Command,
                    Nr: step.Inject.Nr,
                    Ns: step.Inject.Ns,
                    Data: step.Inject.Data,
                    Atoms: step.Inject.Atoms));
            }
            catch (InvalidOperationException ex)
            {
                return TraceResult.Fail($"{label}: interpreter error: {ex.Message}");
            }

            var failure = CheckStep(step.Expect, result, machine);
            if (failure is not null)
                return TraceResult.Fail($"{label}: {failure}");
        }

        return TraceResult.Pass();
    }

    private static string? CheckStep(ExpectDoc expect, DispatchResult result, DataLinkMachine machine)
    {
        if (expect.Transition is not null && !string.Equals(expect.Transition, result.TransitionId, StringComparison.Ordinal))
            return $"expected transition {expect.Transition}, took {result.TransitionId}";

        if (expect.Effects is not null)
        {
            var failure = CheckEffects(expect.Effects, result.Effects);
            if (failure is not null) return failure;
        }

        if (expect.State is not null && !string.Equals(expect.State, machine.State, StringComparison.Ordinal))
            return $"expected state {expect.State}, machine is in {machine.State} (via {result.TransitionId})";

        foreach (var (key, expected) in expect.Variables ?? new Dictionary<string, string>())
        {
            string actual;
            try
            {
                actual = machine.GetVariable(key);
            }
            catch (InvalidOperationException ex)
            {
                return ex.Message;
            }
            if (!string.Equals(Normalise(expected), actual, StringComparison.Ordinal))
                return $"variable {key}: expected {expected}, actual {actual}";
        }

        foreach (var (timer, expected) in expect.Timers ?? new Dictionary<string, string>())
        {
            TimerStatus expectedStatus;
            TimerStatus actual;
            try
            {
                expectedStatus = ParseTimerStatus(timer, expected);
                actual = machine.GetTimer(timer);
            }
            catch (InvalidOperationException ex)
            {
                return ex.Message;
            }
            if (actual != expectedStatus)
                return $"timer {timer}: expected {expected}, actual {actual.ToString().ToLowerInvariant()}";
        }

        if (expect.Queue is not null && !expect.Queue.SequenceEqual(machine.Queue, StringComparer.Ordinal))
            return $"queue: expected [{string.Join(", ", expect.Queue)}], actual [{string.Join(", ", machine.Queue)}]";

        return null;
    }

    private static string? CheckEffects(List<EffectDoc> expected, IReadOnlyList<Effect> actual)
    {
        var count = Math.Min(expected.Count, actual.Count);
        for (var i = 0; i < count; i++)
        {
            var mismatch = MatchEffect(expected[i], actual[i]);
            if (mismatch is not null)
                return $"effect {i + 1}: {mismatch}\n{RenderEffects(actual)}";
        }

        if (expected.Count != actual.Count)
            return $"expected {expected.Count} effect(s), machine emitted {actual.Count}\n{RenderEffects(actual)}";

        return null;
    }

    private static string? MatchEffect(EffectDoc expected, Effect actual)
    {
        if (expected.Dl is not null)
        {
            return actual is UpperEffect upper
                ? (string.Equals(upper.Primitive, expected.Dl, StringComparison.Ordinal)
                    ? null
                    : $"expected dl `{expected.Dl}`, actual {actual.Render()}")
                : $"expected dl `{expected.Dl}`, actual {actual.Render()}";
        }

        if (expected.Lm is not null)
        {
            return actual is LmEffect lm
                ? (string.Equals(lm.Primitive, expected.Lm, StringComparison.Ordinal)
                    ? null
                    : $"expected lm `{expected.Lm}`, actual {actual.Render()}")
                : $"expected lm `{expected.Lm}`, actual {actual.Render()}";
        }

        if (expected.Internal is not null)
        {
            if (actual is not InternalEffect internalEffect
                || !string.Equals(internalEffect.Verb, expected.Internal, StringComparison.Ordinal))
                return $"expected internal `{expected.Internal}`, actual {actual.Render()}";
            if (expected.Detail is not null && !string.Equals(internalEffect.Detail, expected.Detail, StringComparison.Ordinal))
                return $"internal `{expected.Internal}`: expected detail `{expected.Detail}`, actual `{internalEffect.Detail}`";
            return null;
        }

        // Frame expectation.
        if (actual is not FrameEffect frame || !string.Equals(frame.Frame, expected.Frame, StringComparison.Ordinal))
            return $"expected frame {expected.Frame}, actual {actual.Render()}";
        if (expected.Command is not null && frame.Command != expected.Command)
            return $"frame {frame.Frame}: expected {(expected.Command.Value ? "command" : "response")}, actual {frame.Render()}";
        if (expected.Pf is not null && frame.Pf != expected.Pf)
            return $"frame {frame.Frame}: expected pf={(expected.Pf.Value ? 1 : 0)}, actual {frame.Render()}";
        if (expected.Nr is not null && frame.Nr != expected.Nr)
            return $"frame {frame.Frame}: expected nr={expected.Nr.Value.ToString(CultureInfo.InvariantCulture)}, actual {frame.Render()}";
        if (expected.Ns is not null && frame.Ns != expected.Ns)
            return $"frame {frame.Frame}: expected ns={expected.Ns.Value.ToString(CultureInfo.InvariantCulture)}, actual {frame.Render()}";
        if (expected.Expedited is not null && frame.Expedited != expected.Expedited)
            return $"frame {frame.Frame}: expected expedited={expected.Expedited}, actual {frame.Render()}";
        return null;
    }

    private static string RenderEffects(IReadOnlyList<Effect> effects)
    {
        if (effects.Count == 0) return "  (machine emitted no effects)";
        var sb = new StringBuilder("  machine emitted:");
        foreach (var effect in effects) sb.Append("\n    ").Append(effect.Render());
        return sb.ToString();
    }

    /// <summary>YAML booleans arrive as True/False from some emitters; normalise for ordinal compare.</summary>
    private static string Normalise(string value) => value switch
    {
        "True" => "true",
        "False" => "false",
        _ => value,
    };

    private static TimerStatus ParseTimerStatus(string timer, string value) => value switch
    {
        "stopped" => TimerStatus.Stopped,
        "running" => TimerStatus.Running,
        "expired" => TimerStatus.Expired,
        _ => throw new InvalidOperationException($"timer {timer}: unknown status `{value}` (stopped | running | expired)"),
    };
}
