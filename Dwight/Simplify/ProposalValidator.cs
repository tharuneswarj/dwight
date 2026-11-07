using System;
using System.Collections.Generic;
using System.Linq;

namespace Dwight.Simplify
{
  /// <summary>The validator's outcome. Deconstructs to <c>(ok, error)</c> and converts implicitly from that tuple,
  /// so every existing <c>return (false, "...")</c> / <c>var (ok, err) = Validate(...)</c> keeps compiling
  /// unchanged. The added <see cref="Ids"/> carries the components a failure involves so the caller can render a
  /// HUMANE, component-named user message instead of leaking the raw model-repair instruction (RR5 item 2). The
  /// <see cref="Error"/> stays the model-facing repair instruction fed to the one repair round-trip.</summary>
  internal readonly struct ValidationResult
  {
    public readonly bool Ok;
    public readonly string Error;
    public readonly IReadOnlyList<int> Ids;
    public ValidationResult(bool ok, string error, IReadOnlyList<int> ids = null) { Ok = ok; Error = error; Ids = ids ?? Array.Empty<int>(); }
    public void Deconstruct(out bool ok, out string error) { ok = Ok; error = Error; }
    public static implicit operator ValidationResult((bool ok, string error) t) => new ValidationResult(t.ok, t.error);
  }

  /// <summary>
  /// The minimal-diff proposal validator (WP-2 RR3, ADR-034/091): GH-free, everything it needs injected via
  /// <see cref="SimplifyContext"/>. It guarantees, before any preview renders:
  ///  1. KEEP and REMOVE PARTITION the selection - every original component is exactly one of kept-untouched or
  ///     removed, and nothing outside the selection is named (so a keep can never be a disguised delete-replace).
  ///  2. every ADD type is placeable - already in the selection (by its own ComponentGuid) or a normalised
  ///     whitelist match (the anti-invention guard, tolerant of the selection's own old/plugin components).
  ///  3. the OUTPUT SURFACE is preserved - the proposal exposes at least as many output-producing components as
  ///     the selection did, which kills the "collapse everything to one Panel" class the empty-boundary
  ///     whole-canvas case slipped through (RR3 #4b). The exact output equivalence is layer 2's job at apply.
  ///  4. REWIRE endpoints are well-formed refs into keeps / adds / external ports.
  /// A failure feeds the one repair round-trip; a second is a clean dismissal.
  /// </summary>
  internal static class ProposalValidator
  {
    public static ValidationResult Validate(SimplifyProposal p, SimplifyContext ctx)
    {
      if (p == null) return (false, "empty proposal");
      ctx = ctx ?? new SimplifyContext();

      var keep = new HashSet<int>(p.Keep ?? new List<int>());
      var remove = new HashSet<int>(p.Remove ?? new List<int>());

      // 1. keep + remove partition the selection.
      foreach (var id in keep) if (!ctx.SelectionIds.Contains(id)) return (false, $"kept component {id} is not in the selection");
      foreach (var id in remove) if (!ctx.SelectionIds.Contains(id)) return (false, $"removed component {id} is not in the selection");
      foreach (var id in keep) if (remove.Contains(id)) return (false, $"component {id} is both kept and removed");
      foreach (var id in ctx.SelectionIds) if (!keep.Contains(id) && !remove.Contains(id))
        return (false, $"component {id} is neither kept nor removed - every selected component must be one or the other");
      // RR4: a named relay is a sealed unit (design intent) and can never be removed.
      foreach (var id in remove) if (ctx.SealedFromRemoval.Contains(id))
        return (false, $"component {id} is a named relay - a nickname is expressed design intent, so it cannot be removed");

      // 2. every added component type is placeable (selection-local or normalised whitelist).
      var allow = ctx.TypeAllowed ?? (_ => true);
      for (var i = 0; i < (p.Add?.Count ?? 0); i++)
      {
        var t = p.Add[i]?.Type?.Trim();
        if (string.IsNullOrEmpty(t)) return (false, $"added component {i} has no type");
        if (!allow(t)) return (false, $"unknown component type \"{t}\" - not installed on this machine");
      }

      // 3. output-surface preservation: at least as many output producers survive/are-added as the original had.
      var addCount = p.Add?.Count ?? 0;
      var keptProducers = ctx.OutputProducers.Count(keep.Contains);
      if (keptProducers + addCount < ctx.OutputProducers.Count)
        return (false, "the proposal would collapse the outputs - it exposes fewer output-producing components than the selection");

      // 4. rewire endpoints are well-formed refs.
      foreach (var w in p.Rewires ?? new List<Rewire>())
      {
        if (w == null) continue;
        if (!ValidRef(w.From, p, keep) || !ValidRef(w.To, p, keep)) return (false, "a rewire references a component that is not in the proposal");
      }

      // 5. removal-coverage: a removed component's KEPT consumer must be re-fed by a rewire (or itself removed).
      // Without this, a forgotten rewire silently starves the consumer - predicted and actual agree, so layer 1
      // passes and only layer 2 reverts at the end; the validator rejects it at preview with a repair round.
      var rewiredTargets = new HashSet<int>();
      foreach (var w in p.Rewires ?? new List<Rewire>())
        if (!string.IsNullOrEmpty(w?.To) && w.To.Length > 1 && w.To[0] == 'k' && int.TryParse(w.To.Substring(1), out var tid)) rewiredTargets.Add(tid);
      foreach (var r in remove)
      {
        if (!ctx.Consumers.TryGetValue(r, out var consumers) || consumers == null) continue;
        foreach (var c in consumers)
          if (!remove.Contains(c) && !rewiredTargets.Contains(c))
            // Ids = [removed, starved] so the caller can render "removing the Panel (r) would leave the Number (c)
            // without a source" - the raw text here is the MODEL's repair instruction, never shown to the user (RR5 #2).
            return new ValidationResult(false, $"removing component {r} would leave component {c} unwired - re-wire {c} to a new source, or keep {r}", new[] { r, c });
      }

      // 6. Stage 6: a rename targets a KEPT, non-sealed component. A component with a user nickname is sealed
      // (mirrors the relay seal) - only unnamed/generic-named components are candidates, killing over-rename.
      foreach (var rn in p.Renames ?? new List<Rename>())
      {
        if (rn == null) continue;
        if (!keep.Contains(rn.Id)) return (false, $"cannot rename component {rn.Id} - it is not a kept component");
        if (ctx.SealedFromRename.Contains(rn.Id))
          return new ValidationResult(false, $"component {rn.Id} already has a nickname (expressed intent) - it cannot be renamed", new[] { rn.Id });
        if (string.IsNullOrWhiteSpace(rn.Nick)) return (false, $"the rename of component {rn.Id} has no nickname");
      }

      return (true, null);
    }

    /// <summary>A ref token: "k&lt;id&gt;" a kept original (must be in keep), "a&lt;index&gt;" an add (in range),
    /// "i&lt;n&gt;"/"o&lt;n&gt;" an external port (non-negative index; the exact count is checked at apply).</summary>
    private static bool ValidRef(string r, SimplifyProposal p, HashSet<int> keep)
    {
      if (string.IsNullOrEmpty(r) || r.Length < 2) return false;
      var kind = r[0];
      if (!int.TryParse(r.Substring(1), out var n) || n < 0) return false;
      switch (kind)
      {
        case 'k': return keep.Contains(n);
        case 'a': return n < (p.Add?.Count ?? 0);
        case 'i':
        case 'o': return true;   // external port index; the boundary size is enforced at apply
        default: return false;
      }
    }
  }
}
