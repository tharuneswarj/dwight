using System;
using System.Collections.Generic;
using System.Linq;

namespace Dwight.Models
{
  /// <summary>The action whose model is being resolved (ADR-082 task-tier policy).</summary>
  internal enum DwightTask { Explain, Diagnose, Reorganise, Simplify, Scaffold, Label }

  /// <summary>The size band of an Explain (other tasks use <see cref="Any"/>).</summary>
  internal enum TaskScope { Single, Small, Large, Any }

  /// <summary>
  /// The smart resolver (ADR-082): resolves <c>(task, scope, profile) -> concrete model + thinking</c>,
  /// deterministically. The TASK sets a capability band - a floor (the weakest model that does it well)
  /// and a ceiling (the strongest it can benefit from); the user-facing PROFILE only slides the model
  /// within that band. So on a task with no headroom (floor == ceiling) every profile resolves to the same
  /// model - Ultra cannot put Top on a one-component Explain, and Economy cannot under-power a Scaffold.
  /// Two guarantees ride on top regardless of profile: a structured task never resolves to a model lacking
  /// reliable structured output, and thinking stays off for comprehension (it turns on only for Top-tier
  /// agentic work, Scaffold). The size bands and floors are tunable constants the eval harness refines.
  /// </summary>
  internal static class ModelResolver
  {
    // User-facing profiles (the dial). Stored in settings as the "posture" value for back-compat.
    public const string Economy = "economy", Balanced = "balanced", Quality = "quality", Ultra = "ultra";
    public static readonly string[] Profiles = { Economy, Balanced, Quality, Ultra };

    // Internal capability bands (match ModelInfo.Tier strings).
    private static readonly string[] TierOrder = { "Fast", "Mid", "Strong", "Top" };

    internal sealed class Resolved
    {
      public ModelInfo Model;
      public bool Thinking;
      /// <summary>Phase-8 WP-2: reasoning depth for a thinking-on task, from the profile (ADR-082:
      /// "Ultra = strongest, thinking maxed where the task allows"). Null when thinking is off, or when
      /// the profile takes the API default.</summary>
      public string Effort;
    }

    public static Resolved Resolve(DwightTask task, TaskScope scope, bool structured, string profile, string provider) =>
      Resolve(task, scope, structured, profile, ModelCatalogue.ForProvider(provider));

    /// <summary>The same resolution over a provider's rows handed in, so a test can read it on the shipped file rather than on a copy of these lines.</summary>
    internal static Resolved Resolve(DwightTask task, TaskScope scope, bool structured, string profile, IReadOnlyList<ModelInfo> rows)
    {
      var (floor, ceiling, mayThink) = Policy(task, scope);

      var all = rows;
      // U72: the Scaffold chat is the agent loop, so it is chosen among the models that can run it (see ModelPick.AgentPool).
      if (task == DwightTask.Scaffold) all = ModelPick.AgentPool(all, profile == Economy, ref floor);
      var target = TargetTier(profile, floor, ceiling);
      // Prefer reconcile-usable models (green or not-yet-checked); never pick a known-bad (red/amber) one.
      var usable = all.Where(m => m.Reconcile != "red" && m.Reconcile != "amber").ToList();

      var model =
        Pick(usable, target, floor, ceiling, structured) ??           // usable + (structured if needed)
        Pick(all, target, floor, ceiling, structured) ??              // ignore reconcile if nothing usable
        Pick(all, target, floor, ceiling, false) ??                   // last resort: drop the structured filter
        all.FirstOrDefault() ?? DwightConfig.Haiku45;

      var thinking = mayThink && profile != Economy && (model.Reasoning || model.ReasoningKnob);
      return new Resolved
      {
        Model = model,
        Thinking = thinking,
        Effort = thinking ? Dwight.Llm.ThinkingWire.EffortForProfile(profile) : null,
      };
    }

    /// <summary>
    /// Brief EO unit 2: the model a Learn answer typed in the person's own words is judged on, the provider's Fast band, resolved through the catalogue and never by id:
    /// the cheapest fast model the key can call, preferring one with structured output. Null when the provider has no fast model, and then nothing is judged.
    /// </summary>
    public static ModelInfo Judge(string provider) => Judge(ModelCatalogue.ForProvider(provider));

    internal static ModelInfo Judge(IReadOnlyList<ModelInfo> rows)
    {
      var usable = (rows ?? Array.Empty<ModelInfo>()).Where(m => m != null && Idx(m.Tier) == 0 && m.Reconcile != "red" && m.Reconcile != "amber").ToList();
      return ModelPick.Choose(usable, "Fast", "Fast", "Fast", true) ?? ModelPick.Choose(usable, "Fast", "Fast", "Fast", false);
    }

    /// <summary>The cheapest model nearest <paramref name="target"/> within [floor, ceiling]. The
    /// comparison itself lives in <see cref="ModelPick"/> - GH-free, disk-free, and a TOTAL order, so a
    /// tie can never be settled by catalogue file position (#199).</summary>
    private static ModelInfo Pick(IReadOnlyList<ModelInfo> models, string target, string floor, string ceiling, bool structuredOnly) =>
      ModelPick.Choose(models, target, floor, ceiling, structuredOnly);

    private static int Idx(string tier) => ModelPick.Idx(tier);

    /// <summary>The tier the profile targets within the task's band (ADR-082): Economy/Balanced sit at the
    /// floor, Quality/Ultra reach the ceiling. With no headroom (floor == ceiling) all four coincide.</summary>
    private static string TargetTier(string profile, string floor, string ceiling)
    {
      switch (profile)
      {
        case Quality:
        case Ultra: return ceiling;
        default: return floor;   // economy, balanced
      }
    }

    /// <summary>The task-tier policy (floors + ceilings, in code, ADR-082). <c>mayThink</c> = the task may
    /// use a thinking pass (only Scaffold today); comprehension tasks keep it off.</summary>
    private static (string floor, string ceiling, bool mayThink) Policy(DwightTask task, TaskScope scope)
    {
      switch (task)
      {
        case DwightTask.Explain:
          if (scope == TaskScope.Single) return ("Fast", "Fast", false);
          if (scope == TaskScope.Large) return ("Mid", "Strong", false);
          return ("Fast", "Mid", false);                 // small group (default)
        case DwightTask.Label: return ("Mid", "Top", false);   // the most visible comprehension output; Balanced->Sonnet, Quality/Ultra->Opus; payload tiny so cost is trivial (ADR-087 WP-C). Thinking off - it is classification.
        case DwightTask.Diagnose: return ("Strong", "Strong", false);
        // WP-3.5: the DwightTask.Reorganise branch is removed - Reorganise resolves its ONE model via the
        // Label task (the WP-C labelling call), so this case was dead. Simplify/Scaffold stay as forward
        // placeholders wired in Phases 7/8.
        case DwightTask.Simplify: return ("Strong", "Top", false);
        case DwightTask.Scaffold: return ("Strong", "Top", true);
        default: return ("Fast", "Strong", false);
      }
    }

    /// <summary>Map an Explain selection size to a scope band (tunable; eval-harness refined, ADR-066).</summary>
    public static TaskScope ScopeForCount(int count)
    {
      if (count <= 1) return TaskScope.Single;
      if (count <= SmallGroupMax) return TaskScope.Small;
      return TaskScope.Large;
    }

    public const int SmallGroupMax = 12;
  }
}
