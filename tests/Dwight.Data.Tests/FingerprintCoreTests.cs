using System;
using System.Collections.Generic;
using Dwight.Data;
using Xunit;

namespace Dwight.Data.Tests
{
  /// <summary>
  /// Headless tests for the behaviour-fingerprint core (WP-1.8). The fingerprint is the safety-critical
  /// guard that Reorganise never changes what a definition computes (ADR-086); before Phase 7.0 it had zero
  /// tests because it was GH-coupled. The token/splice/hash logic is now in the GH-free
  /// <see cref="BehaviourFingerprintCore"/>; these tests pin its contract - relay splicing, multi-source
  /// order sensitivity, the locked-relay case, and persistent-value sensitivity.
  /// </summary>
  public class FingerprintCoreTests
  {
    // ---- builders (keep the tests readable) ----

    private static readonly Guid A = new Guid("11111111-0000-0000-0000-000000000001");
    private static readonly Guid Aout = new Guid("11111111-0000-0000-0000-0000000000a1");
    private static readonly Guid B = new Guid("22222222-0000-0000-0000-000000000002");
    private static readonly Guid Bin = new Guid("22222222-0000-0000-0000-0000000000b1");
    private static readonly Guid C = new Guid("33333333-0000-0000-0000-000000000003");
    private static readonly Guid Cout = new Guid("33333333-0000-0000-0000-0000000000c1");
    private static readonly Guid Relay = new Guid("44444444-0000-0000-0000-000000000004");

    /// <summary>A plain component node with the given input params.</summary>
    private static FpObject Comp(Guid id, string type, params FpInput[] inputs)
      => new FpObject { Guid = id, TypeName = type, Inputs = new List<FpInput>(inputs) };

    /// <summary>An input param fed by the given ordered source-param guids.</summary>
    private static FpInput In(Guid id, params Guid[] sources)
      => new FpInput { Guid = id, MappingToken = "None/False/False", Sources = new List<Guid>(sources) };

    /// <summary>A pass-through generic relay: one source, no toggle, no data, unlocked.</summary>
    private static FpObject RelayNode(Guid id, Guid source)
      => new FpObject
      {
        Guid = id, TypeName = "Param_GenericObject", IsGenericParam = true, SourceCount = 1,
        Inputs = new List<FpInput> { In(id, source) },
      };

    /// <summary>The same relay but LOCKED - it blocks all data, so it is not transparent.</summary>
    private static FpObject LockedRelayNode(Guid id, Guid source)
    { var r = RelayNode(id, source); r.Locked = true; return r; }

    // ---- tests ----

    [Fact]
    public void Identical_docs_hash_equal()
    {
      var d1 = new FpDoc { Objects = { Comp(A, "Number"), Comp(B, "Curve", In(Bin, Aout)) } };
      var d2 = new FpDoc { Objects = { Comp(A, "Number"), Comp(B, "Curve", In(Bin, Aout)) } };
      Assert.Equal(BehaviourFingerprintCore.Compute(d1), BehaviourFingerprintCore.Compute(d2));
    }

    [Fact]
    public void A_pass_through_relay_hashes_identically_to_the_direct_wire()
    {
      // A.out -> B.in  (direct)
      var direct = new FpDoc { Objects = { Comp(A, "Number"), Comp(B, "Curve", In(Bin, Aout)) } };
      // A.out -> relay -> B.in  (the relay must splice out, so the two are behaviour-identical)
      var relayed = new FpDoc
      {
        Objects = { Comp(A, "Number"), RelayNode(Relay, Aout), Comp(B, "Curve", In(Bin, Relay)) },
      };
      Assert.Equal(BehaviourFingerprintCore.Compute(direct), BehaviourFingerprintCore.Compute(relayed));
    }

    [Fact]
    public void Reordering_a_multi_source_inputs_sources_flips_the_hash()
    {
      // GH merges a param's sources in wire order, so [A, C] and [C, A] compute different data - the
      // fingerprint must be sensitive to source ORDER (WP-1.8), not just the source SET.
      var order1 = new FpDoc { Objects = { Comp(A, "Number"), Comp(C, "Number"), Comp(B, "Merge", In(Bin, Aout, Cout)) } };
      var order2 = new FpDoc { Objects = { Comp(A, "Number"), Comp(C, "Number"), Comp(B, "Merge", In(Bin, Cout, Aout)) } };
      Assert.NotEqual(BehaviourFingerprintCore.Compute(order1), BehaviourFingerprintCore.Compute(order2));
    }

    [Fact]
    public void A_locked_relay_is_not_spliced_out()
    {
      // A LOCKED generic param blocks all data (its output stays empty), so it is NOT transparent - splicing
      // it out would hash a broken definition as identical to a working one (WP-1.8). The locked-relay doc
      // must therefore differ from the unlocked-relay doc (which does splice to the direct wire).
      var unlocked = new FpDoc { Objects = { Comp(A, "Number"), RelayNode(Relay, Aout), Comp(B, "Curve", In(Bin, Relay)) } };
      var locked = new FpDoc { Objects = { Comp(A, "Number"), LockedRelayNode(Relay, Aout), Comp(B, "Curve", In(Bin, Relay)) } };
      Assert.False(BehaviourFingerprintCore.IsPassThrough(LockedRelayNode(Relay, Aout)));
      Assert.NotEqual(BehaviourFingerprintCore.Compute(unlocked), BehaviourFingerprintCore.Compute(locked));
    }

    [Fact]
    public void Editing_an_internalized_params_value_flips_the_hash()
    {
      // A param with internalized (persistent) data: editing a value in place changes what it computes but
      // leaves the topology and counts identical, so the fingerprint must depend on the value DIGEST (WP-1.8),
      // not just the tree shape.
      FpObject Internalized(string digest) => new FpObject
      {
        Guid = A, TypeName = "Param_Number", HasPersistentData = true,
        Inputs = new List<FpInput> { new FpInput { Guid = A, MappingToken = "None/False/False", PersistentToken = digest } },
      };
      var before = new FpDoc { Objects = { Internalized("3:1.0,2.0,3.0") } };
      var after = new FpDoc { Objects = { Internalized("3:1.0,2.0,9.0") } };   // same count/paths, one value changed
      Assert.NotEqual(BehaviourFingerprintCore.Compute(before), BehaviourFingerprintCore.Compute(after));
    }

    /// <summary>
    /// #779: A SCRIPT'S SOURCE REACHES THE HASH. Until this change a script component's value token was
    /// null, so two definitions differing by two hundred lines of Python hashed IDENTICALLY - the
    /// fingerprint answering "nothing changed" about the one edit that changes everything. The reader now
    /// puts <c>SourceToken.Of(source)</c> in the value token; this is the core's half of that claim.
    /// </summary>
    [Fact]
    public void A_changed_script_source_changes_the_hash()
    {
      FpObject Script(string source) => new FpObject
      {
        Guid = A, TypeName = "RhinoCodePluginGH.Components.Python3Component", ValueToken = SourceToken.Of(source),
      };

      var empty = new FpDoc { Objects = { Script("") } };
      var written = new FpDoc { Objects = { Script("a = x * 2\n") } };
      var edited = new FpDoc { Objects = { Script("a = x * 3\n") } };

      Assert.NotEqual(BehaviourFingerprintCore.Compute(empty), BehaviourFingerprintCore.Compute(written));
      Assert.NotEqual(BehaviourFingerprintCore.Compute(written), BehaviourFingerprintCore.Compute(edited));
    }

    /// <summary>
    /// And the direction that protects Reorganise: a script nobody touched hashes the same across two
    /// reads, an UNREADABLE one included. An unstable marker for "we could not read it" would abort the
    /// byte-identical guard on any definition holding one locked script - breaking a working feature to
    /// report a change nobody made.
    /// </summary>
    [Fact]
    public void An_unreadable_script_hashes_stably_so_one_locked_node_cannot_abort_Reorganise()
    {
      FpDoc Doc() => new FpDoc
      {
        Objects =
        {
          new FpObject { Guid = A, TypeName = "GhPython.Component.ZuiPythonComponent", ValueToken = SourceToken.Unreadable },
        },
      };
      Assert.Equal(BehaviourFingerprintCore.Compute(Doc()), BehaviourFingerprintCore.Compute(Doc()));
    }

    [Fact]
    public void A_pass_through_cycle_terminates()
    {
      // Two relays feeding each other (a degenerate cycle) must not hang the splice walk - the visited set
      // guarantees termination. A stable hash is enough; correctness of a genuine cycle is undefined but safe.
      var r1 = new Guid("55555555-0000-0000-0000-000000000005");
      var r2 = new Guid("66666666-0000-0000-0000-000000000006");
      var doc = new FpDoc { Objects = { RelayNode(r1, r2), RelayNode(r2, r1), Comp(B, "Curve", In(Bin, r1)) } };
      var h = BehaviourFingerprintCore.Compute(doc);   // must return, not spin
      Assert.Equal(h, BehaviourFingerprintCore.Compute(doc));   // and be deterministic
    }
  }
}
