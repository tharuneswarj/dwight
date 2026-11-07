using System;
using System.Collections.Generic;
using System.Linq;
using Dwight.Simplify;
using Xunit;

namespace Dwight.Simplify.Tests
{
  /// <summary>
  /// Phase-9 U35 (#928) - THE BESIDE-VERIFY COMPARED THE ORIGINAL WITH ITSELF, and the compare that cannot.
  ///
  /// The copy's parameters carried the original's guids; the trial captured both sides by guid through the
  /// document; both resolved to the same parameter; "they are identical" was true for a doubled result. These
  /// tests hold the two properties the fix rests on: a compare handed two sides that share an identity REFUSES
  /// (never passes, never mismatches - nothing was measured), and - THE POSITIVE CONTROL (#212) - two different
  /// identities holding different data report a mismatch by name. Without the second the first cannot be told
  /// from the defect: a compare that always refuses would pass the first.
  /// </summary>
  public class ScriptTrialCompareTests
  {
    private static ComparedOutput Out(Guid id, string name, string tree, string value, bool hasData = true) =>
      new ComparedOutput { Identity = id, Name = name, Sample = new OutputSample(tree, value), HasData = hasData };

    private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid(), C = Guid.NewGuid(), D = Guid.NewGuid();

    // ---- the defect, made unrepresentable --------------------------------------------------------------

    /// <summary>THE DEFECT. Both sides read from the same parameter: whatever the data says, nothing was compared.
    /// The result must be a refusal that names the port - not "identical", which is what it used to say.</summary>
    [Fact]
    public void Two_sides_sharing_a_parameter_identity_are_refused_not_compared()
    {
      var original = new[] { Out(A, "a", "1:1", "12") };
      var copy = new[] { Out(A, "a", "1:1", "12") };   // the same guid: the copy IS the original, by identity

      var r = ScriptTrialCompare.Compare(original, copy);

      Assert.NotNull(r.Refused);
      Assert.Contains("output a,", r.Refused);
      Assert.Contains("read the original twice", r.Refused);
      Assert.Equal(0, r.OutputsCompared);
      Assert.Empty(r.Mismatched);
    }

    /// <summary>One shared identity among several is enough: a compare that is half a compare is not one.</summary>
    [Fact]
    public void One_shared_identity_among_many_is_enough_to_refuse()
    {
      var original = new[] { Out(A, "area", "1:1", "12"), Out(B, "centroid", "1:1", "p") };
      var copy = new[] { Out(C, "area", "1:1", "12"), Out(B, "centroid", "1:1", "p") };

      var r = ScriptTrialCompare.Compare(original, copy);

      Assert.NotNull(r.Refused);
      Assert.Contains("centroid", r.Refused);
      Assert.DoesNotContain("area", r.Refused);
    }

    /// <summary>The shim's question, on every parameter of both components: which identities does the copy still
    /// share with its original? Empty is the only acceptable answer, and the test that fails when a copy shares one.</summary>
    [Fact]
    public void Shared_identities_are_listed_in_the_originals_order_and_once_each()
    {
      var shared = ScriptTrialCompare.SharedIdentities(new[] { A, B, C, B }, new[] { C, B, D });
      Assert.Equal(new[] { B, C }, shared);
      Assert.Empty(ScriptTrialCompare.SharedIdentities(new[] { A, B }, new[] { C, D }));
      Assert.Empty(ScriptTrialCompare.SharedIdentities(null, new[] { A }));
    }

    // ---- THE POSITIVE CONTROL (#212) --------------------------------------------------------------------

    /// <summary>A changed source MUST report a mismatch. Two different parameters, different data: the compare
    /// names the port that differs. This is the row the gate called its stop condition (3c), GH-free.</summary>
    [Fact]
    public void Different_identities_with_different_data_report_the_mismatch_by_name()
    {
      var original = new[] { Out(A, "a", "1:1", "12") };
      var copy = new[] { Out(B, "a", "1:1", "24") };   // the doubled result of row 3c

      var r = ScriptTrialCompare.Compare(original, copy);

      Assert.Null(r.Refused);
      Assert.Equal(1, r.OutputsCompared);
      Assert.Equal(1, r.OutputsWithData);
      Assert.Equal(new[] { "a" }, r.Mismatched);
    }

    /// <summary>And "identical" now has to be EARNED: identical data read through two DIFFERENT identities, and
    /// only then no mismatch. Row 3b's pass, as it should have been measured.</summary>
    [Fact]
    public void Identical_data_through_two_different_identities_is_a_real_pass()
    {
      var original = new[] { Out(A, "a", "1:1", "12"), Out(B, "b", "2:1", "xy") };
      var copy = new[] { Out(C, "a", "1:1", "12"), Out(D, "b", "2:1", "xy") };

      var r = ScriptTrialCompare.Compare(original, copy);

      Assert.Null(r.Refused);
      Assert.Equal(2, r.OutputsCompared);
      Assert.Equal(2, r.OutputsWithData);
      Assert.Empty(r.Mismatched);
    }

    /// <summary>The per-index rule for "carried data" is kept from the trial: counted once per index whichever side
    /// had it, never summed across the two sides.</summary>
    [Fact]
    public void Outputs_with_data_are_counted_per_index_not_per_side()
    {
      var original = new[] { Out(A, "a", "1:1", "1", hasData: true), Out(B, "b", "0", "", hasData: false) };
      var copy = new[] { Out(C, "a", "1:1", "1", hasData: true), Out(D, "b", "0", "", hasData: false) };

      var r = ScriptTrialCompare.Compare(original, copy);

      Assert.Equal(2, r.OutputsCompared);
      Assert.Equal(1, r.OutputsWithData);
    }

    /// <summary>A tree-shape difference is a mismatch too - the sample token is tree + value.</summary>
    [Fact]
    public void A_changed_tree_shape_is_a_mismatch()
    {
      var r = ScriptTrialCompare.Compare(new[] { Out(A, "a", "1:3", "v") }, new[] { Out(B, "a", "3:1", "v") });
      Assert.Null(r.Refused);
      Assert.Equal(new[] { "a" }, r.Mismatched);
    }

    /// <summary>A port with no readable name is still named - by its REAL position - never left blank in a refusal
    /// or a mismatch list, and never by its position among the ports that happened to be listed.</summary>
    [Fact]
    public void A_nameless_port_is_named_by_position()
    {
      var r = ScriptTrialCompare.Compare(new[] { Out(A, null, "1:1", "1") }, new[] { Out(B, "", "1:1", "2") });
      Assert.Equal(new[] { "output1" }, r.Mismatched);

      // the second port shares an identity and has no name: it is "output2", not "output1"
      var refused = ScriptTrialCompare.Compare(
        new[] { Out(A, "a", "1:1", "1"), Out(B, null, "1:1", "1") },
        new[] { Out(C, "a", "1:1", "1"), Out(B, null, "1:1", "1") });
      Assert.Contains("output2", refused.Refused);
      Assert.DoesNotContain("output1", refused.Refused);
    }
  }
}
