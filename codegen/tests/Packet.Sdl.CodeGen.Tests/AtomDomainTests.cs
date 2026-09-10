using Packet.Sdl.IR;
using Packet.Sdl.IR.Totality;

namespace Packet.Sdl.CodeGen.Tests;

/// <summary>
/// Unit tests of the guard-atom domain model behind the totality lint
/// (docs/lint-totality.md). The modular window atoms get the most attention:
/// they are where a wrong definition would silently reclassify a real figure
/// hole as infeasible.
/// </summary>
public class AtomDomainTests
{
    private static readonly AtomDomain D = AtomDomain.Instance;

    private static bool Eval(string atom, params (string Var, int Value)[] vars)
        => D.Evaluate(atom, vars.ToDictionary(v => v.Var, v => v.Value, StringComparer.Ordinal));

    /// <summary>The feasible truth-value combinations of the given atoms, as strings of 1/0 in atom order.</summary>
    private static HashSet<string> FeasibleWith(IReadOnlyDictionary<string, int>? fixedVars, params string[] atoms)
        => D.Enumerate(atoms, fixedVars).Valuations
            .Select(v => new string(v.Values.Select(b => b ? '1' : '0').ToArray()))
            .ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> Feasible(params string[] atoms) => FeasibleWith(null, atoms);

    [Fact]
    public void Receive_window_test_wraps_modulo_8()
    {
        // V(r)=6, k=4: the window is N(s) in {7, 0, 1}.
        Eval("vr_lt_ns_lt_vr_plus_k", (AtomDomain.Vr, 6), (AtomDomain.Ns, 7), (AtomDomain.K, 4)).Should().BeTrue();
        Eval("vr_lt_ns_lt_vr_plus_k", (AtomDomain.Vr, 6), (AtomDomain.Ns, 0), (AtomDomain.K, 4)).Should().BeTrue();
        Eval("vr_lt_ns_lt_vr_plus_k", (AtomDomain.Vr, 6), (AtomDomain.Ns, 1), (AtomDomain.K, 4)).Should().BeTrue();
        // V(r)+k itself is outside the open interval.
        Eval("vr_lt_ns_lt_vr_plus_k", (AtomDomain.Vr, 6), (AtomDomain.Ns, 2), (AtomDomain.K, 4)).Should().BeFalse();
        // N(s) == V(r) is in sequence, not in the out-of-sequence window.
        Eval("vr_lt_ns_lt_vr_plus_k", (AtomDomain.Vr, 6), (AtomDomain.Ns, 6), (AtomDomain.K, 4)).Should().BeFalse();
        // The frame just behind V(r) (a duplicate) is never in the window, even at k=7.
        Eval("vr_lt_ns_lt_vr_plus_k", (AtomDomain.Vr, 0), (AtomDomain.Ns, 7), (AtomDomain.K, 7)).Should().BeFalse();
        // k=1 grants no out-of-sequence window at all.
        Eval("vr_lt_ns_lt_vr_plus_k", (AtomDomain.Vr, 0), (AtomDomain.Ns, 1), (AtomDomain.K, 1)).Should().BeFalse();
    }

    [Fact]
    public void In_sequence_and_inside_the_window_are_mutually_exclusive()
    {
        var f = Feasible("ns_eq_vr", "vr_lt_ns_lt_vr_plus_k");
        f.Should().NotContain("11");
        f.Should().BeEquivalentTo(new[] { "10", "01", "00" });
    }

    [Fact]
    public void Inside_the_window_but_not_more_than_one_past_Vr_means_exactly_the_next_frame()
    {
        var space = D.Enumerate(new[] { "ns_eq_vr", "vr_lt_ns_lt_vr_plus_k", "ns_gt_vr_plus_1" });
        var hits = space.Valuations.Where(v => !v.Values[0] && v.Values[1] && !v.Values[2]).ToList();
        hits.Should().NotBeEmpty();
        foreach (var v in hits)
        {
            ((v.Witness[AtomDomain.Ns] - v.Witness[AtomDomain.Vr] + 8) % 8).Should().Be(1);
        }
    }

    [Fact]
    public void Acknowledgement_range_test_wraps_modulo_8()
    {
        // V(a)=6, V(s)=2: outstanding frames 6, 7, 0, 1; N(r) may be 6, 7, 0, 1 or 2.
        Eval("va_le_nr_le_vs", (AtomDomain.Va, 6), (AtomDomain.Nr, 1), (AtomDomain.Vs, 2)).Should().BeTrue();
        Eval("va_le_nr_le_vs", (AtomDomain.Va, 6), (AtomDomain.Nr, 2), (AtomDomain.Vs, 2)).Should().BeTrue();
        Eval("va_le_nr_le_vs", (AtomDomain.Va, 6), (AtomDomain.Nr, 6), (AtomDomain.Vs, 2)).Should().BeTrue();
        Eval("va_le_nr_le_vs", (AtomDomain.Va, 6), (AtomDomain.Nr, 3), (AtomDomain.Vs, 2)).Should().BeFalse();
        Eval("va_le_nr_le_vs", (AtomDomain.Va, 6), (AtomDomain.Nr, 5), (AtomDomain.Vs, 2)).Should().BeFalse();
        // Nothing outstanding: only N(r) == V(a) == V(s) is in range.
        Eval("va_le_nr_le_vs", (AtomDomain.Va, 6), (AtomDomain.Nr, 6), (AtomDomain.Vs, 6)).Should().BeTrue();
        Eval("va_le_nr_le_vs", (AtomDomain.Va, 6), (AtomDomain.Nr, 7), (AtomDomain.Vs, 6)).Should().BeFalse();
    }

    [Fact]
    public void With_nothing_outstanding_an_in_range_Nr_acknowledges_nothing_new()
    {
        // vs_eq_va and va_le_nr_le_vs together force nr_eq_va.
        Feasible("vs_eq_va", "va_le_nr_le_vs", "nr_eq_va").Should().NotContain("110");
        Feasible("vs_eq_va", "va_le_nr_le_vs", "nr_eq_va").Should().Contain("111");
    }

    [Fact]
    public void Window_full_and_nothing_outstanding_are_mutually_exclusive()
    {
        var f = Feasible("vs_eq_va", "vs_eq_va_plus_k");
        f.Should().NotContain("11");
        f.Should().Contain("10");
        f.Should().Contain("01");
        f.Should().Contain("00");
    }

    [Fact]
    public void Window_full_reads_k_frames_outstanding_modulo_8()
    {
        Eval("vs_eq_va_plus_k", (AtomDomain.Va, 5), (AtomDomain.Vs, 1), (AtomDomain.K, 4)).Should().BeTrue();
        Eval("vs_eq_va_plus_k", (AtomDomain.Va, 5), (AtomDomain.Vs, 0), (AtomDomain.K, 4)).Should().BeFalse();
    }

    [Fact]
    public void The_PF_bit_and_the_role_are_shared_by_the_compound_atoms()
    {
        var f = Feasible("P_eq_1", "command_and_P_eq_1", "response_and_F_eq_1");
        // Bit set: exactly one of the compound atoms holds.
        f.Should().NotContain("100");
        f.Should().NotContain("111");
        f.Should().Contain("110");
        f.Should().Contain("101");
        // Bit clear: neither compound atom holds.
        f.Should().Contain("000");
        f.Should().NotContain("010");
        f.Should().NotContain("001");
        // command and response are the two values of one role.
        Feasible("command", "response").Should().BeEquivalentTo(new[] { "10", "01" });
    }

    [Fact]
    public void Modulus_is_exactly_one_of_8_or_128()
    {
        Feasible("mod_8", "mod_128").Should().BeEquivalentTo(new[] { "10", "01" });
    }

    [Fact]
    public void T1_can_be_neither_running_nor_expired_but_not_both()
    {
        var f = Feasible("T1_running", "T1_expired");
        f.Should().NotContain("11");
        f.Should().Contain("00");
        f.Should().Contain("10");
        f.Should().Contain("01");
    }

    [Fact]
    public void RC_eq_0_and_RC_eq_N2_can_coincide_only_when_N2_is_zero()
    {
        // The spec puts no lower bound on N2, so the model keeps N2 = 0 rather
        // than assume it away; the witness must then show N2 = 0.
        var space = D.Enumerate(new[] { "RC_eq_0", "RC_eq_N2" });
        var both = space.Valuations.Where(v => v.Values[0] && v.Values[1]).ToList();
        both.Should().NotBeEmpty();
        both.Should().OnlyContain(v => v.Witness[AtomDomain.N2] == 0 && v.Witness[AtomDomain.Rc] == 0);
    }

    [Fact]
    public void A_frame_typed_event_pins_the_frame_type_of_the_enquiry_response_atom()
    {
        const string enquiry = "F_eq_1_and_frame_eq_RR_or_frame_eq_RNR_or_frame_eq_I";
        // Inside a subroutine the frame type is free: F set on some other frame type is possible.
        FeasibleWith(null, "F_eq_1", enquiry).Should().Contain("10");
        // On an RR_received arm the atom collapses to the F bit.
        FeasibleWith(AtomDomain.ConstraintsForEvent("RR_received"), "F_eq_1", enquiry)
            .Should().BeEquivalentTo(new[] { "11", "00" });
        // On a DISC_received arm it can never hold.
        FeasibleWith(AtomDomain.ConstraintsForEvent("DISC_received"), "F_eq_1", enquiry)
            .Should().BeEquivalentTo(new[] { "10", "00" });
        // A primitive pins nothing.
        AtomDomain.ConstraintsForEvent("DL_DATA_request").Should().BeEmpty();
        AtomDomain.ConstraintsForEvent(null).Should().BeEmpty();
    }

    [Fact]
    public void An_atom_the_model_does_not_know_is_an_independent_boolean_and_is_reported()
    {
        var space = D.Enumerate(new[] { "something_true", "ack_pending" });
        space.UnknownAtoms.Should().BeEquivalentTo(new[] { "something_true" });
        Feasible("something_true", "ack_pending").Should().BeEquivalentTo(new[] { "00", "01", "10", "11" });
    }

    [Fact]
    public void Independent_atoms_are_enumerated_per_component_so_the_biggest_real_arm_stays_small()
    {
        // The figc4.4 I_received arm's live atoms.
        var live = new[]
        {
            "command", "info_field_length_le_N1_and_content_is_octet_aligned", "va_le_nr_le_vs", "own_receiver_busy",
            "ns_eq_vr", "P_eq_1", "ack_pending", "vr_lt_ns_lt_vr_plus_k", "reject_exception", "SREJ_enabled",
            "sreject_exception_gt_0", "ns_gt_vr_plus_1",
        };
        var space = D.Enumerate(live, AtomDomain.ConstraintsForEvent("I_received"));
        space.Valuations.Count.Should().BeLessThan(1 << 12);
        space.Valuations.Count.Should().BeGreaterThan(0);
        // Every valuation's witness reproduces its values.
        foreach (var v in space.Valuations)
        {
            for (int i = 0; i < live.Length; i++)
            {
                D.Evaluate(live[i], v.Witness).Should().Be(v.Values[i], $"atom {live[i]} under witness");
            }
        }
    }

    [Fact]
    public void Every_canonical_atom_in_the_predicate_catalogue_has_a_domain_definition()
    {
        // A new atom added to spec-sdl/predicates.yaml without a definition
        // here would be treated as a free boolean, which is conservative but
        // silent. This test makes it loud.
        var repoRoot = FindRepoRoot();
        var path = Path.Combine(repoRoot, "..", "spec-sdl", "predicates.yaml");
        File.Exists(path).Should().BeTrue($"the ax25spec submodule must be checked out for this test ({path})");
        var catalog = PredicateCatalog.Load(path);
        catalog.Canonicals.Should().NotBeEmpty();
        var missing = catalog.Canonicals.Where(c => !D.IsKnown(c)).OrderBy(c => c, StringComparer.Ordinal).ToList();
        missing.Should().BeEmpty("every catalogued atom needs a definition (or an explicit Free() entry) in AtomDomain.cs");
        var extra = D.Atoms.Select(a => a.Name).Where(a => !catalog.Canonicals.Contains(a)).OrderBy(a => a, StringComparer.Ordinal).ToList();
        extra.Should().BeEmpty("the domain model should not define atoms the catalogue does not have");
    }

    private static string FindRepoRoot()
    {
        var d = new DirectoryInfo(Path.GetDirectoryName(typeof(AtomDomainTests).Assembly.Location)!);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "ax25sdl.slnx"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("codegen root (ax25sdl.slnx) not found");
    }
}
