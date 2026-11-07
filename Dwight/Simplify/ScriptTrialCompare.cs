using System;
using System.Collections.Generic;
using System.Linq;

namespace Dwight.Simplify
{
  /// <summary>One compared output, on either side of the beside-verify: WHICH parameter it was read from, what
  /// the user calls it, and what it held.</summary>
  internal sealed class ComparedOutput
  {
    /// <summary>The parameter's identity - its instance guid on the live canvas. It is the field the whole
    /// compare turns on: two sides read from the SAME identity have not been compared.</summary>
    public Guid Identity { get; set; }

    /// <summary>The port's name as the user sees it, for the mismatch list. Names only, never values (#71).</summary>
    public string Name { get; set; }

    public OutputSample Sample { get; set; }

    /// <summary>At least one NON-NULL item on this output (<c>Data.OutputItems</c>'s rule).</summary>
    public bool HasData { get; set; }
  }

  /// <summary>What the compare concluded - FACTS, which <c>ScriptEditVerdict</c> judges. Null <see cref="Refused"/>
  /// means the compare happened; otherwise it did not, for the reason given, and nothing else is meaningful.</summary>
  internal sealed class TrialComparison
  {
    public string Refused { get; set; }
    public int OutputsCompared { get; set; }
    public int OutputsWithData { get; set; }
    public List<string> Mismatched { get; set; } = new List<string>();
  }

  /// <summary>
  /// PHASE-9 U35 - THE BESIDE-VERIFY'S COMPARE, WITH THE GUARD THAT MAKES IT A COMPARE (#928).
  ///
  /// THE DEFECT THIS EXISTS TO MAKE UNREPRESENTABLE. The trial duplicates the script component through
  /// Grasshopper's own serialisation, and that serialisation carries every PARAMETER's instance guid. The copy's
  /// component guid was renewed; its parameters' were not. The compare then resolved both sides BY GUID through
  /// the document, so "before" (the original's output) and "after" (the copy's output, same guid) resolved to the
  /// SAME parameter, and every "they are identical" the card ever showed was the original compared with itself -
  /// true by construction for a correct edit and for one that doubled the result alike (#109 inside the proof).
  ///
  /// SO THE IDENTITY IS AN INPUT TO THE COMPARE, AND THE COMPARE REFUSES WHEN THE TWO SIDES SHARE ONE. Not a
  /// check at the call site, which is where it would be forgotten: the shim refuses to hand over a copy whose
  /// parameters share an identity with the original's (<c>ScriptComponentShim.TryDuplicate</c>), and this
  /// compare refuses again when handed two sides that do. A refusal here is a <c>CouldNotCheck</c>, never a pass,
  /// and never a mismatch either - nothing was measured.
  ///
  /// GH-free (ADR-091), so the property is asserted rather than reviewed: the test that fails when a copy shares
  /// a parameter guid with its original, and the POSITIVE CONTROL (#212) - two different identities holding
  /// different data report a mismatch - both live beside this type.
  /// </summary>
  internal static class ScriptTrialCompare
  {
    /// <summary>
    /// Every identity that appears on BOTH sides, in the original's order. Empty means the two sets of parameters
    /// are genuinely two sets. The shim asks this of ALL the copy's parameters (inputs and outputs) against all
    /// the original's; the compare asks it of the outputs it is about to read.
    /// </summary>
    public static List<Guid> SharedIdentities(IEnumerable<Guid> original, IEnumerable<Guid> copy)
    {
      var mine = new HashSet<Guid>(copy ?? Enumerable.Empty<Guid>());
      var shared = new List<Guid>();
      foreach (var g in original ?? Enumerable.Empty<Guid>())
        if (mine.Contains(g) && !shared.Contains(g)) shared.Add(g);
      return shared;
    }

    /// <summary>
    /// Compare the original's outputs against the copy's, index by index. The sides must be two DIFFERENT sets of
    /// parameters, or the comparison is refused with the port named - a compare of a parameter against itself is
    /// not a comparison, whatever its result says.
    /// </summary>
    public static TrialComparison Compare(IReadOnlyList<ComparedOutput> original, IReadOnlyList<ComparedOutput> copy)
    {
      original = original ?? new ComparedOutput[0];
      copy = copy ?? new ComparedOutput[0];

      var shared = SharedIdentities(original.Select(o => o.Identity), copy.Select(o => o.Identity));
      if (shared.Count > 0)
      {
        var names = new List<string>();
        for (var i = 0; i < original.Count; i++)
          if (shared.Contains(original[i].Identity)) names.Add(NameOf(original[i], i));
        return new TrialComparison
        {
          Refused = "Dwight's test copy of the script shares the identity of the original's output" +
                    (names.Count == 1 ? " " : "s ") + string.Join(", ", names) +
                    ", so a comparison would have read the original twice; Dwight did not compare them",
        };
      }

      var result = new TrialComparison { OutputsCompared = Math.Min(original.Count, copy.Count) };

      // HOW MANY COMPARED OUTPUTS CARRIED DATA ON EITHER SIDE - counted per INDEX, never summed across the two
      // sides (the trial's own rule, kept: a field whose number is wrong is a field that will be rendered wrongly).
      for (var i = 0; i < result.OutputsCompared; i++)
        if (original[i].HasData || copy[i].HasData) result.OutputsWithData++;

      var before = original.Take(result.OutputsCompared).Select(o => o.Sample).ToList();
      var after = copy.Take(result.OutputsCompared).Select(o => o.Sample).ToList();
      foreach (var i in OutputEquivalence.Mismatches(before, after))
        result.Mismatched.Add(i >= 0 && i < original.Count ? NameOf(original[i], i) : "output" + (i + 1));

      return result;
    }

    private static string NameOf(ComparedOutput o, int index) =>
      string.IsNullOrWhiteSpace(o?.Name) ? "output" + (index + 1) : o.Name.Trim();
  }
}
