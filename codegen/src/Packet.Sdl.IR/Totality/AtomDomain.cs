namespace Packet.Sdl.IR.Totality;

/// <summary>
/// The guard-atom domain model behind <see cref="TotalityLint"/>: the
/// protocol variables the atoms of <c>spec-sdl/predicates.yaml</c> are
/// defined over, and every atom as a function of those variables.
/// </summary>
/// <remarks>
/// <para>
/// The atoms are not independent booleans. <c>ns_eq_vr</c> and
/// <c>vr_lt_ns_lt_vr_plus_k</c> both talk about N(s) against V(r);
/// <c>command_and_P_eq_1</c> and <c>response_and_F_eq_1</c> both read the
/// one P/F bit and the one command/response role; <c>mod_8</c> and
/// <c>mod_128</c> are the two values of one modulus. A hole in a figure at an
/// atom valuation that no protocol state can produce is not a figure defect,
/// so the lint asks this model which valuations are feasible before it
/// reports anything. Feasibility is decided by enumerating the underlying
/// variables concretely, never by hand-written pairwise exclusion rules.
/// </para>
/// <para>
/// Everything in here must be a definition the spec gives (the section
/// citation sits next to each atom), never a behaviour some runtime chose;
/// a model that quietly encodes packet.net's choices would be testing
/// packet.net again and calling the result a spec finding
/// (ax25spec docs/fuzzing-the-figures.md, "Laundering implementation
/// choices as spec findings"). Where the spec settles nothing, the atom is a
/// free boolean and says so. State-space invariants (for example that the
/// number of outstanding I frames never exceeds k) are deliberately NOT
/// encoded: they are reachability facts, not definitions, and leaving them
/// out can only make the lint over-report, which is the visible failure
/// mode.
/// </para>
/// <para>
/// Sequence numbers are enumerated modulo 8 with k in 1..7 regardless of the
/// <c>mod_8</c> / <c>mod_128</c> selection. Every atom that reads a sequence
/// number compares an offset (N(s) against V(r), N(r) against V(a) and V(s),
/// V(s) against V(a) and k) and the qualitative configurations of those
/// offsets (equal, inside the window, just past the window, wrapped) are all
/// present at modulo 8 with k ranging over 1..7; modulo 128 adds no new
/// relationship between any pair of atoms, only more room. The modulus
/// variable is kept for the two atoms that read it directly.
/// </para>
/// </remarks>
public sealed class AtomDomain
{
    /// <summary>One underlying protocol variable and its finite domain.</summary>
    public sealed record Variable(string Name, IReadOnlyList<int> Domain, Func<int, string> Render);

    /// <summary>One atom: the variables it reads, its truth function, and where the spec defines it.</summary>
    public sealed record Atom(string Name, IReadOnlyList<string> Variables, Func<Assignment, bool> Eval, string Citation);

    /// <summary>A concrete assignment of values to variables.</summary>
    public sealed class Assignment
    {
        private readonly Dictionary<string, int> _values;
        public Assignment(Dictionary<string, int> values) { _values = values; }
        public int this[string variable] => _values[variable];
        public bool Has(string variable) => _values.ContainsKey(variable);
        public IReadOnlyDictionary<string, int> Values => _values;
    }

    /// <summary>One feasible valuation of the live atoms plus the assignment that produced it.</summary>
    public sealed class Valuation
    {
        public Valuation(IReadOnlyList<bool> values, IReadOnlyDictionary<string, int> witness)
        {
            Values = values;
            Witness = witness;
        }
        /// <summary>Atom truth values, in the order of the live-atom list passed to <see cref="Enumerate"/>.</summary>
        public IReadOnlyList<bool> Values { get; }
        /// <summary>Variable assignment that produces <see cref="Values"/> (only the variables the live atoms read).</summary>
        public IReadOnlyDictionary<string, int> Witness { get; }
    }

    /// <summary>The feasible valuations of a live-atom set.</summary>
    public sealed class FeasibleSpace
    {
        public FeasibleSpace(IReadOnlyList<string> atoms, IReadOnlyList<Valuation> valuations, IReadOnlyList<string> unknownAtoms)
        {
            Atoms = atoms;
            Valuations = valuations;
            UnknownAtoms = unknownAtoms;
        }
        public IReadOnlyList<string> Atoms { get; }
        public IReadOnlyList<Valuation> Valuations { get; }
        /// <summary>Atoms with no definition here, treated as independent free booleans.</summary>
        public IReadOnlyList<string> UnknownAtoms { get; }
    }

    // ─── Variables ─────────────────────────────────────────────────────

    // Sequence numbers, modulo 8 (see the class remarks for why 8 suffices).
    // §4.2.2.1: "an I frame is assigned a sequential number from 0 to 7".
    private static readonly int[] Seq = Enumerable.Range(0, 8).ToArray();
    // The window size k. §6.7.2.3: "The maximum number of I frames
    // outstanding at a time is seven (modulo 8)"; §4.2.2.1: "up to seven
    // outstanding I frames". k = 0 would forbid sending anything and is not
    // a configuration the figures contemplate. The §6.3.2 default for
    // modulo 8 (k = 4) is listed first only so that the first witness a
    // finding quotes uses the everyday window size; the feasible set does
    // not depend on the order.
    private static readonly int[] WindowSizes = { 4, 1, 2, 3, 5, 6, 7 };
    private static readonly int[] Bit = { 0, 1 };
    private static readonly int[] Three = { 0, 1, 2 };
    private static readonly int[] Four = { 0, 1, 2, 3 };
    private const int Modulus = 8;

    public const string Vs = "V(s)";
    public const string Va = "V(a)";
    public const string Vr = "V(r)";
    public const string Ns = "N(s)";
    public const string Nr = "N(r)";
    public const string X = "X";
    public const string K = "k";
    public const string Pf = "P/F";
    public const string Role = "role";
    public const string Frame = "frame";
    public const string Mod = "modulus";
    public const string T1 = "T1";
    public const string Rc = "RC";
    public const string N2 = "N2";
    public const string Nm201 = "NM201";

    public const int RoleCommand = 0, RoleResponse = 1;
    public const int FrameI = 0, FrameRR = 1, FrameRNR = 2, FrameOther = 3;
    public const int Mod8 = 0, Mod128 = 1;
    public const int T1Stopped = 0, T1Running = 1, T1Expired = 2;

    private static string Num(int v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private static string Bool(int v) => v == 1 ? "true" : "false";

    private static readonly Variable[] BuiltinVariables =
    {
        new(Vs, Seq, Num),          // §4.2.2.2 send state variable
        new(Va, Seq, Num),          // §4.2.2.6 acknowledge state variable
        new(Vr, Seq, Num),          // §4.2.2.4 receive state variable
        new(Ns, Seq, Num),          // §4.2.2.3 send sequence number of the received I frame
        new(Nr, Seq, Num),          // §4.2.2.5 receive sequence number of the received frame
        new(X, Seq, Num),           // figc4.7 Invoke Retransmission: "X := V(s)", a saved copy
        new(K, WindowSizes, Num),   // §6.7.2.3 window size
        new(Pf, Bit, Num),          // §4.2.1 / §6.2: the one Poll/Final bit every frame carries
        new(Role, Bit, v => v == RoleCommand ? "command" : "response"),                  // §6.1.2
        new(Frame, Four, v => v switch { FrameI => "I", FrameRR => "RR", FrameRNR => "RNR", _ => "other" }), // §4.3
        new(Mod, Bit, v => v == Mod8 ? "8" : "128"),                                       // §4.3.3.1 / §4.3.3.2
        new(T1, Three, v => v switch { T1Stopped => "stopped", T1Running => "running", _ => "expired" }),   // §6.7.1.1
        new(Rc, Four, Num),         // retry count RC (figc4.x); counts T1 retries against N2 (§6.7.2.2)
        new(N2, Four, Num),         // §6.7.2.2 maximum number of retries; the spec gives no lower bound, so 0 is kept
        new(Nm201, Four, Num),      // figc4.8 / figc4.9 management retry limit; same treatment as N2
    };

    // ─── Atoms ─────────────────────────────────────────────────────────

    /// <summary>Offset of <paramref name="to"/> ahead of <paramref name="from"/>, modulo 8. The spec's own reading of "greater than" for sequence numbers: how far past <paramref name="from"/> the number <paramref name="to"/> lies, wrapping (§4.4.2's window "greater-than V(r) and less-than V(r)+k").</summary>
    private static int Offset(int from, int to) => ((to - from) % Modulus + Modulus) % Modulus;

    private static Atom Def(string name, string citation, Func<Assignment, bool> eval, params string[] variables)
        => new(name, variables, eval, citation);

    /// <summary>An atom the spec does not define in terms of any other variable: an independent boolean.</summary>
    private static Atom Free(string name, string citation)
        => new(name, new[] { name }, a => a[name] == 1, citation);

    private static readonly Atom[] BuiltinAtoms =
    {
        // ── Sequence-variable comparisons ──────────────────────────────
        Def("vs_eq_va",
            "§4.2.2.2 V(s), §4.2.2.6 V(a): V(s) = V(a), no I frame outstanding",
            a => a[Vs] == a[Va], Vs, Va),
        Def("vs_eq_va_plus_k",
            "§6.4.1: a TNC stops sending I frames when V(s) equals the last received N(r) [= V(a)] plus k; §6.7.2.3 k",
            a => Offset(a[Va], a[Vs]) == a[K], Vs, Va, K),
        Def("vs_eq_X",
            "figc4.7 Invoke Retransmission: X := V(s) is a saved copy of V(s); V(s) = X ends the retransmission loop (no prose definition)",
            a => a[Vs] == a[X], Vs, X),
        Def("vs_eq_nr",
            "§4.2.2.2 V(s), §4.2.2.5 N(r): V(s) = N(r). Synthesised by the Resolver (ax25sdl#53) from the figure's V(s) = V(a)? read after V(a) := N(r); the same comparison as nr_eq_vs",
            a => a[Vs] == a[Nr], Vs, Nr),
        Def("ns_eq_vr",
            "§4.2.2.4: V(r) is the N(s) of the next expected I frame; §6.4.2: in-sequence means N(s) equals V(r)",
            a => a[Ns] == a[Vr], Ns, Vr),
        Def("ns_gt_vr_plus_1",
            "figc4.5 \"N(s) > V(r)+1?\": the received N(s) lies more than one past V(r), read with §4.4.2's offset convention (the figure only asks it inside the receive window, where that is the only reading)",
            a => Offset(a[Vr], a[Ns]) > 1, Ns, Vr),
        Def("nr_eq_va",
            "§4.2.2.5 N(r), §4.2.2.6 V(a): N(r) = V(a), the frame acknowledges nothing new",
            a => a[Nr] == a[Va], Nr, Va),
        Def("nr_eq_vs",
            "§4.2.2.5 N(r), §4.2.2.2 V(s): N(r) = V(s), the frame acknowledges everything sent",
            a => a[Nr] == a[Vs], Nr, Vs),
        Def("va_le_nr_le_vs",
            "§6.4.11 / §4.4.5.1: N(r) \"within the range from the last N(R) received [V(a)] to the last N(S) sent plus one [V(s)]\", i.e. V(a) <= N(r) <= V(s) taken as offsets from V(a)",
            a => Offset(a[Va], a[Nr]) <= Offset(a[Va], a[Vs]), Va, Nr, Vs),
        Def("vr_lt_ns_lt_vr_plus_k",
            "§4.4.2: N(s) \"in the range greater-than V(r) and less-than V(r)+k\" (open interval, ax25spec#40; predicates.yaml comment): 1 <= (N(s) - V(r)) mod M <= k - 1",
            a => Offset(a[Vr], a[Ns]) is var d && d >= 1 && d <= a[K] - 1, Ns, Vr, K),

        // ── Frame role and the P/F bit ─────────────────────────────────
        Def("command", "§6.1.2: every frame is a command or a response (the C bits)", a => a[Role] == RoleCommand, Role),
        Def("response", "§6.1.2: every frame is a command or a response (the C bits)", a => a[Role] == RoleResponse, Role),
        Def("P_eq_1", "§4.2.1 / §6.2: the P/F bit is one bit present in every frame; it is read as P on a command", a => a[Pf] == 1, Pf),
        Def("F_eq_1", "§4.2.1 / §6.2: the same P/F bit, read as F on a response", a => a[Pf] == 1, Pf),
        Def("P_or_F_eq_1", "§4.2.1 / §6.2: the P/F bit, whichever way it is read", a => a[Pf] == 1, Pf),
        Def("command_and_P_eq_1", "§6.1.2 + §6.2: a command frame with the P/F bit set", a => a[Role] == RoleCommand && a[Pf] == 1, Role, Pf),
        Def("response_and_F_eq_1", "§6.1.2 + §6.2: a response frame with the P/F bit set", a => a[Role] == RoleResponse && a[Pf] == 1, Role, Pf),
        Def("F_eq_1_and_frame_eq_RR_or_frame_eq_RNR_or_frame_eq_I",
            "figc4.7 Enquiry Response: P/F bit set and the frame is an RR, RNR (§4.3.2.1, §4.3.2.2) or I (§4.3.1) frame. On a state page the on: event fixes the frame type (see ConstraintsForEvent); inside a subroutine it is free",
            a => a[Pf] == 1 && a[Frame] is FrameI or FrameRR or FrameRNR, Pf, Frame),

        // ── Session flags: independent booleans ────────────────────────
        Free("ack_pending", "figc4.4 acknowledge-pending flag (§6.4.3 / T2 delayed acknowledgement); the spec defines it in terms of nothing else here"),
        Free("own_receiver_busy", "§4.4.1 / §6.4.10: this TNC's own busy condition; independent state"),
        Free("peer_receiver_busy", "§6.4.9: the peer's busy condition as last reported by RNR; independent state"),
        Free("layer_3_initiated", "figc4.1 / figc4.2 flag: whether layer 3 asked for this connection (§5.3 DL-CONNECT); independent state"),
        Free("able_to_establish", "figc4.1 \"Able to establish?\": an environment decision the spec leaves to the implementation"),

        // ── Exception conditions: independent booleans ─────────────────
        Free("reject_exception", "§4.4.3: an outstanding sent-REJ condition; the spec defines it only by when it is set and cleared"),
        Free("SREJ_enabled", "§6.3.2: selective reject is negotiated per link; independent of everything else here"),
        Free("sreject_exception_gt_0", "§4.4.4: one or more outstanding sent-SREJ conditions; defined only by set/clear behaviour"),
        Free("out_of_sequence_frames_in_receive_buffer", "§4.4.2: information fields saved while out of sequence; buffer state the spec does not relate to any other variable"),
        Free("vr_I_frame_stored", "figc4.4 / figc4.5 drain loop: an I frame numbered V(r) is held in the receive buffer (§4.4.2); buffer state"),

        // ── Timer T1 ───────────────────────────────────────────────────
        Def("T1_running",
            "§6.7.1.1 / §6.4.1: T1 is running (started or restarted). A T1 cancelled by an acknowledgement (§6.4.6) is neither running nor expired, so the timer has three states",
            a => a[T1] == T1Running, T1),
        Def("T1_expired",
            "§4.4.5.1 / §6.4.11: T1 has run to expiry and not been restarted since",
            a => a[T1] == T1Expired, T1),

        // ── Retry counter ──────────────────────────────────────────────
        Def("RC_eq_0", "figc4.x retry count RC (§6.7.2.2 pairs it with T1): RC = 0", a => a[Rc] == 0, Rc),
        Def("RC_eq_N2", "§6.7.2.2 maximum number of retries: RC = N2. The spec puts no lower bound on N2, so RC = 0 and RC = N2 can coincide", a => a[Rc] == a[N2], Rc, N2),
        Def("RC_eq_NM201", "figc4.8 / figc4.9 management data-link retry limit NM201: RC = NM201", a => a[Rc] == a[Nm201], Rc, Nm201),

        // ── Modulus and version ────────────────────────────────────────
        Def("mod_8", "§4.3.3.1: SABM selects modulo 8; §4.2.2.1", a => a[Mod] == Mod8, Mod),
        Def("mod_128", "§4.3.3.2: SABME selects modulo 128; §4.2.2.1", a => a[Mod] == Mod128, Mod),
        Free("version_2_2", "§6.3.2: whether the peer negotiated as v2.2. The spec relates it to nothing else by definition (the modulus is set by behaviour, figc4.7 Set Version 2.2, not by definition)"),

        // ── Frame content ──────────────────────────────────────────────
        Free("info_field_length_le_N1_and_content_is_octet_aligned", "§6.7.2.1 N1 and §3.x octet alignment: a property of the received frame the spec relates to no state variable"),
    };

    // ─── Event-derived constraints ─────────────────────────────────────

    private static readonly Dictionary<string, int> FrameByEvent = new(StringComparer.Ordinal)
    {
        ["I_received"]     = FrameI,
        ["RR_received"]    = FrameRR,
        ["RNR_received"]   = FrameRNR,
        ["REJ_received"]   = FrameOther,
        ["SREJ_received"]  = FrameOther,
        ["UI_received"]    = FrameOther,
        ["UA_received"]    = FrameOther,
        ["DM_received"]    = FrameOther,
        ["DISC_received"]  = FrameOther,
        ["SABM_received"]  = FrameOther,
        ["SABME_received"] = FrameOther,
        ["FRMR_received"]  = FrameOther,
        ["XID_response_received"] = FrameOther,
        ["XID_command_received"]  = FrameOther,
    };

    private static readonly Dictionary<string, int> RoleByEvent = new(StringComparer.Ordinal)
    {
        // Events whose figure input box names the role. Frame-type events
        // (I_received etc.) do NOT fix the role: the figures test
        // "Command?" on them explicitly (figc4.4 I frame received), so the
        // role stays a free variable there.
        ["i_or_s_command_received"] = RoleCommand,
        ["all_other_commands"]      = RoleCommand,
        ["XID_command_received"]    = RoleCommand,
        ["XID_response_received"]   = RoleResponse,
    };

    /// <summary>
    /// Variables an <c>on:</c> event pins on a state page: a frame-typed
    /// event fixes <see cref="Frame"/>, and an event whose figure input
    /// names the role fixes <see cref="Role"/>. Null event (a subroutine)
    /// pins nothing.
    /// </summary>
    public static IReadOnlyDictionary<string, int> ConstraintsForEvent(string? eventName)
    {
        var fixedVars = new Dictionary<string, int>(StringComparer.Ordinal);
        if (eventName is null) return fixedVars;
        if (FrameByEvent.TryGetValue(eventName, out var frame)) fixedVars[Frame] = frame;
        if (RoleByEvent.TryGetValue(eventName, out var role)) fixedVars[Role] = role;
        return fixedVars;
    }

    // ─── Instance ──────────────────────────────────────────────────────

    public static AtomDomain Instance { get; } = new();

    private readonly Dictionary<string, Variable> _variables = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Atom> _atoms = new(StringComparer.Ordinal);

    private AtomDomain()
    {
        foreach (var v in BuiltinVariables) _variables.Add(v.Name, v);
        foreach (var a in BuiltinAtoms)
        {
            _atoms.Add(a.Name, a);
            // A Free() atom declares a variable named after itself.
            if (!_variables.ContainsKey(a.Name) && a.Variables.Count == 1 && a.Variables[0] == a.Name)
                _variables.Add(a.Name, new Variable(a.Name, Bit, Bool));
            foreach (var vn in a.Variables)
            {
                if (!_variables.ContainsKey(vn))
                    throw new InvalidOperationException($"atom `{a.Name}` reads undeclared variable `{vn}`");
            }
        }
    }

    /// <summary>The variables, in declaration order (sequence variables first, then the free booleans in atom order).</summary>
    public IReadOnlyList<Variable> Variables => _variables.Values.ToList();

    /// <summary>The atoms, in declaration order.</summary>
    public IReadOnlyList<Atom> Atoms => _atoms.Values.ToList();

    public bool IsKnown(string atom) => _atoms.ContainsKey(atom);

    /// <summary>Variables an atom reads (an unknown atom reads a free boolean named after itself).</summary>
    public IReadOnlyList<string> VariablesOf(string atom)
        => _atoms.TryGetValue(atom, out var a) ? a.Variables : new[] { atom };

    /// <summary>Render a variable value the way the witness text does.</summary>
    public string Render(string variable, int value)
        => _variables.TryGetValue(variable, out var v) ? v.Render(value) : Bool(value);

    /// <summary>Evaluate one atom under a concrete assignment (unit-test surface).</summary>
    public bool Evaluate(string atom, IReadOnlyDictionary<string, int> assignment)
    {
        var a = ResolveAtom(atom);
        return a.Eval(new Assignment(new Dictionary<string, int>(assignment, StringComparer.Ordinal)));
    }

    private Atom ResolveAtom(string name)
        => _atoms.TryGetValue(name, out var a)
            ? a
            : new Atom(name, new[] { name }, asg => asg[name] == 1, "not in the atom domain model; treated as an independent boolean");

    private Variable ResolveVariable(string name)
        => _variables.TryGetValue(name, out var v) ? v : new Variable(name, Bit, Bool);

    /// <summary>
    /// The feasible valuations of <paramref name="liveAtoms"/>: every
    /// combination of truth values some concrete assignment of the underlying
    /// variables produces, each with one such assignment as its witness.
    /// <paramref name="fixedVars"/> pins variables (the event-derived frame
    /// type and role) to one value before enumeration.
    /// </summary>
    /// <remarks>
    /// Atoms that share no variable, directly or transitively, are
    /// independent, so the enumeration works per connected component of the
    /// atom/variable graph and takes the Cartesian product of the components'
    /// feasible sets. That keeps the biggest real arm (a dozen live atoms over
    /// five sequence variables, k, the role, the P/F bit and a handful of free
    /// booleans) at a few thousand valuations instead of tens of millions.
    /// </remarks>
    public FeasibleSpace Enumerate(IReadOnlyList<string> liveAtoms, IReadOnlyDictionary<string, int>? fixedVars = null)
    {
        fixedVars ??= new Dictionary<string, int>(StringComparer.Ordinal);
        var atoms = liveAtoms.Select(ResolveAtom).ToList();
        var unknown = liveAtoms.Where(n => !_atoms.ContainsKey(n)).ToList();

        // Union-find over atoms that share a variable.
        var parent = Enumerable.Range(0, atoms.Count).ToArray();
        int Find(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
        var firstAtomByVar = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < atoms.Count; i++)
        {
            foreach (var v in atoms[i].Variables)
            {
                if (firstAtomByVar.TryGetValue(v, out var j)) parent[Find(i)] = Find(j);
                else firstAtomByVar[v] = i;
            }
        }

        var components = Enumerable.Range(0, atoms.Count)
            .GroupBy(Find)
            .OrderBy(g => g.Min())
            .Select(g => g.OrderBy(i => i).ToList())
            .ToList();

        // Per component: enumerate the product of its variables' domains and
        // collect the distinct sub-valuations with a first witness each.
        var perComponent = new List<List<(bool[] Values, Dictionary<string, int> Witness)>>();
        foreach (var comp in components)
        {
            var vars = comp.SelectMany(i => atoms[i].Variables).Distinct(StringComparer.Ordinal).ToList();
            var domains = vars.Select(v => fixedVars.TryGetValue(v, out var fx) ? new[] { fx } : ResolveVariable(v).Domain.ToArray()).ToList();
            var found = new Dictionary<string, (bool[] Values, Dictionary<string, int> Witness)>(StringComparer.Ordinal);
            var order = new List<string>();
            var idx = new int[vars.Count];
            var current = new Dictionary<string, int>(StringComparer.Ordinal);
            while (true)
            {
                for (int v = 0; v < vars.Count; v++) current[vars[v]] = domains[v][idx[v]];
                var asg = new Assignment(current);
                var values = new bool[comp.Count];
                var key = new char[comp.Count];
                for (int c = 0; c < comp.Count; c++)
                {
                    values[c] = atoms[comp[c]].Eval(asg);
                    key[c] = values[c] ? '1' : '0';
                }
                var k = new string(key);
                if (!found.ContainsKey(k))
                {
                    found[k] = (values, new Dictionary<string, int>(current, StringComparer.Ordinal));
                    order.Add(k);
                }
                // Odometer step.
                int p = vars.Count - 1;
                while (p >= 0 && ++idx[p] == domains[p].Length) { idx[p] = 0; p--; }
                if (p < 0) break;
            }
            perComponent.Add(order.Select(k => found[k]).ToList());
        }

        // Cartesian product of the components' feasible sets, mapped back to
        // the live-atom order.
        long total = 1;
        foreach (var pc in perComponent)
        {
            total *= pc.Count;
            if (total > 1 << 20)
                throw new InvalidOperationException(
                    $"the live atom set [{string.Join(", ", liveAtoms)}] has more than 2^20 feasible valuations; " +
                    "the totality lint cannot enumerate it. Split the arm or extend the domain model.");
        }

        var result = new List<Valuation>((int)total);
        var pick = new int[perComponent.Count];
        if (perComponent.Count == 0)
        {
            result.Add(new Valuation(Array.Empty<bool>(), new Dictionary<string, int>(StringComparer.Ordinal)));
        }
        else
        {
            while (true)
            {
                var values = new bool[atoms.Count];
                var witness = new Dictionary<string, int>(StringComparer.Ordinal);
                for (int c = 0; c < components.Count; c++)
                {
                    var (vals, wit) = perComponent[c][pick[c]];
                    for (int j = 0; j < components[c].Count; j++) values[components[c][j]] = vals[j];
                    foreach (var (vn, vv) in wit) witness[vn] = vv;
                }
                result.Add(new Valuation(values, witness));
                int p = perComponent.Count - 1;
                while (p >= 0 && ++pick[p] == perComponent[p].Count) { pick[p] = 0; p--; }
                if (p < 0) break;
            }
        }

        return new FeasibleSpace(liveAtoms, result, unknown);
    }
}
