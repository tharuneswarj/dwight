using System.Collections.Generic;
using System.Linq;

namespace Dwight.Simplify
{
  /// <summary>WP-3 (GH-free): deterministic, zero-token best-practice SUGGESTIONS for Simplify's advisory tier -
  /// never auto-applied. The design lever is RESTRAINT (Tharun): fire only where it genuinely aids legibility, and
  /// SUMMARIZE into one line per category ("5 inputs merge implicitly - consider explicit Merge components"), never
  /// one item per input. Under-suggesting beats nagging - a wall of suggestions destroys the sharp advisory tier.
  /// The GH read that gathers <see cref="InputFacts"/> is a thin shim; this decision is headless and unit-tested.</summary>
  internal static class BestPracticeAdvisor
  {
    /// <summary>One selection input worth noting: how many wires feed it, and any inline data-tree toggle it
    /// carries ("flatten" | "graft" | "simplify" | "reverse", else null). The shim only emits inputs that already
    /// clear a bar (fan-in &gt;= 3 or a toggle), so this list is small.</summary>
    public readonly struct InputFacts
    {
      public readonly int FanIn;
      public readonly string Toggle;
      public InputFacts(int fanIn, string toggle) { FanIn = fanIn; Toggle = toggle; }
    }

    public static List<Advice> Suggest(IReadOnlyList<InputFacts> inputs)
    {
      var advice = new List<Advice>();
      if (inputs == null || inputs.Count == 0) return advice;

      // Merge: only HIGH fan-in (3+), summarized to one line. A 2-source input is ordinary and never fires.
      var highFanIn = inputs.Count(i => i.FanIn >= 3);
      if (highFanIn > 0)
        advice.Add(new Advice
        {
          Observation = highFanIn == 1 ? "One input merges 3 or more wires implicitly." : $"{highFanIn} inputs merge 3 or more wires implicitly.",
          Suggestion = "An explicit Merge component at each would document exactly what is being combined.",
        });

      // Tree toggles: only when there are SEVERAL (>= 2), summarized. A single inline Flatten is fine and never fires.
      var toggled = inputs.Count(i => i.Toggle != null);
      if (toggled >= 2)
      {
        var flat = inputs.Count(i => i.Toggle == "flatten");
        var lead = flat >= 4
          ? $"You have flattened {flat} inputs inline"
          : $"{toggled} inputs reshape their data tree inline (flatten / graft / simplify / reverse)";
        advice.Add(new Advice
        {
          Observation = lead + ".",
          Suggestion = "Pulling those into explicit tree components (Flatten / Graft / Simplify / Reverse) would make the data handling visible on the canvas.",
        });
      }
      return advice;
    }
  }
}
