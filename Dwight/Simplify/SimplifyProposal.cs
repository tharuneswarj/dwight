using System;
using System.Collections.Generic;

namespace Dwight.Simplify
{
  /// <summary>
  /// The LLM's better-logic proposal as a MINIMAL DIFF (WP-2 RR3): the original selection is partitioned into
  /// KEEP (untouched by construction - identity, internalized data, panel text, and slider values all preserved,
  /// the ADR-031 rule) and REMOVE (deleted); ADD introduces new components; REWIRES connects the result; CHANGES
  /// is the classified, human-facing rationale. Only genuine changes ever touch the canvas - keeps are never
  /// instantiated, so the Stage-5 apply's deltas and predictions shrink to exactly what moved.
  ///
  /// The objective is BEST-PRACTICE ROBUSTNESS, never component-count reduction (the gate lesson): externalise a
  /// hardcoded value into a slider/param, remove a dead relay with reasoning, clarify tree handling, reduce only
  /// where it serves legibility. Old / unknown / locked components are SEALED UNITS - kept and rewired around,
  /// never internally rewritten.
  /// </summary>
  internal sealed class SimplifyProposal
  {
    public List<int> Keep { get; } = new List<int>();     // original selection ids, UNTOUCHED
    public List<int> Remove { get; } = new List<int>();   // original selection ids, deleted
    public List<AddNode> Add { get; } = new List<AddNode>();
    public List<Rewire> Rewires { get; } = new List<Rewire>();
    /// <summary>Stage 6: behaviour-NEUTRAL nickname edits on kept components (applied as GH_NickNameActions in the
    /// same undo record, outside the fingerprint - no prediction/verification needed). The first member of the
    /// bounded safe op class.</summary>
    public List<Rename> Renames { get; } = new List<Rename>();
    public List<Change> Changes { get; } = new List<Change>();
  }

  /// <summary>Stage 6: a nickname edit on a kept component (id -> new nickname). Behaviour-neutral - the
  /// fingerprint does not hash nicknames.</summary>
  internal sealed class Rename { public int Id; public string Nick; }

  /// <summary>Stage 7: a read-only ADVISORY insight the model surfaces but does NOT auto-apply - a
  /// performance / scalability / robustness / hardcoding observation and its suggestion. Rendered as the "Dwight
  /// also noticed" section; never validated, never verified, zero risk. The deep-refinement value ships as
  /// narration while applied changes stay gated by exact equivalence.</summary>
  internal sealed class Advice { public string Observation; public string Suggestion; }

  /// <summary>
  /// Stage 6: the LLM's diff as a list of CHANGE GROUPS - the op-addressable wire form. Each classified change
  /// OWNS the low-level ops that realise it, so ticking a change in the preview includes/excludes its whole op
  /// group (selective accept). Refs inside a group are change-LOCAL (<c>a&lt;i&gt;</c> is this group's i-th add;
  /// <c>k</c>/<c>i</c>/<c>o</c> stay global); <see cref="PlanFilter"/> flattens the ticked groups into a
  /// <see cref="SimplifyProposal"/>, remapping each group's local add indices to the global add list. The KIND
  /// split de-risks it: remove/add/rewire ride the predict -> apply -> verify -> revert net (a mis-grouped filter
  /// can only revert); rename is behaviour-neutral.
  /// </summary>
  internal sealed class ChangeGroup
  {
    public string Class;
    public string Text;
    public bool Confirm;
    public List<int> Remove { get; } = new List<int>();
    public List<AddNode> Add { get; } = new List<AddNode>();
    public List<Rewire> Rewire { get; } = new List<Rewire>();
    public List<Rename> Rename { get; } = new List<Rename>();
  }

  internal sealed class AddNode
  {
    public string Type;
    public string Nick;
    /// <summary>RR4 #6 / RR7: set by the PLUGIN (never the model) when this add is a slider externalising a value -
    /// the value + integer/float read from the live target, so the slider is born holding it. RR7 extends this to
    /// value+type+wiring: the model NOMINATES the target (below) and supplies none of these.</summary>
    public ExternalizeSpec Externalize;
    /// <summary>RR7: for an externalize slider, the target the model NOMINATED - the component whose value to
    /// externalize (+ optional input param NAME, never a port index). The plugin reads value, type, and the exact
    /// consumer port(s) from the live graph and wires the slider itself. Null for a non-externalize add.</summary>
    public int? ExternalizeTargetId;
    public string ExternalizeTargetParam;
    /// <summary>RR8 #21: the index of the CHANGE GROUP this add came from (matches the preview/applied list's
    /// change index), set by the parser and carried through <see cref="PlanFilter"/>'s flatten. Lets the apply map
    /// each created guid back to its change so the applied "done" list is click-to-locate. -1 if unassigned.</summary>
    public int OriginChange = -1;
  }

  internal sealed class ExternalizeSpec { public double Value; public bool Integer; }

  /// <summary>One classified change for the preview (the consent surface). <see cref="Class"/> is one of
  /// remove-dead / externalize / robustify / reduce / rename; <see cref="Confirm"/> flags a change the model is
  /// unsure about, rendered for the user to confirm.</summary>
  internal sealed class Change { public string Class; public string Text; public bool Confirm; }

  /// <summary>A wire in the resulting graph. Each endpoint is a ref token: "k&lt;id&gt;" a kept original component,
  /// "a&lt;index&gt;" an added component, "i&lt;ext&gt;" an external input, "o&lt;ext&gt;" an external output.</summary>
  internal sealed class Rewire { public string From; public int FromPort; public string To; public int ToPort; }

  /// <summary>What the validator needs to check a proposal, GH-free (all injected by the caller): the original
  /// selection ids the diff must partition, the ids that PRODUCE an output (boundary or terminal) so a collapse
  /// of the output surface is caught, and whether a proposed ADD type is placeable (selection-local by its own
  /// ComponentGuid, or a normalised whitelist match - RR3 #3).</summary>
  internal sealed class SimplifyContext
  {
    public HashSet<int> SelectionIds = new HashSet<int>();
    public HashSet<int> OutputProducers = new HashSet<int>();
    public Func<string, bool> TypeAllowed = _ => true;
    /// <summary>RR4: ids that must never be removed - a NAMED relay (a nickname is expressed design intent, and
    /// relays are legitimate wire organisation). Deterministic, not model-judged.</summary>
    public HashSet<int> SealedFromRemoval = new HashSet<int>();
    /// <summary>Stage 6: ids that must never be RENAMED - a component that already carries a user nickname (the
    /// nickname is expressed intent). Mirrors the relay seal; only unnamed/generic-named components are rename
    /// candidates. Deterministic, kills over-rename before any preview.</summary>
    public HashSet<int> SealedFromRename = new HashSet<int>();
    /// <summary>RR4 review completion: for each selection component id, the KEPT consumer component ids that read
    /// from its outputs - so removing a component whose output feeds a kept consumer WITHOUT a rewire is rejected
    /// at preview ("removal of X leaves consumer Y unwired") rather than silently starving Y (predicted and actual
    /// agree, layer 1 passes, and only layer 2 reverts at the end of a full apply cycle).</summary>
    public Dictionary<int, HashSet<int>> Consumers = new Dictionary<int, HashSet<int>>();
  }
}
