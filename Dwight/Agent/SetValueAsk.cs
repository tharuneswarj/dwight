using System;
using System.Text.Json.Nodes;

namespace Dwight.Agent
{
  /// <summary>What the plugin read off the target, live: its type, the user's nickname, and what it holds.</summary>
  internal sealed class LiveTarget
  {
    public string Type { get; set; }
    public string Nick { get; set; }
    /// <summary>The current slider or panel state, in the shared vocabulary; null when the type holds neither.</summary>
    public BuildState Current { get; set; }
  }

  /// <summary>
  /// THE ASK, VALIDATED (Phase-9 U4) - run at ask time (before the card exists) and again at click time
  /// (the canvas may have moved). GH-free: the live facts arrive as functions, so every decision here is
  /// tested headless and the panel's shim only reads.
  ///
  /// In order: the seed carries a target and a state; the target is a live id (never resolved to something
  /// else - #338, #208); the state parses through stream A's parser for the target's live type (its refusals
  /// arrive unchanged - inverted domain, out-of-domain, off-grid, a type that takes no state); the rounding
  /// change is restorable by one undo (<see cref="SetValueCore.MayChangeRounding"/>). What comes out is the
  /// EXACT change the card names and the click applies: what the user approved is what executes.
  /// </summary>
  internal static class SetValueAsk
  {
    public static bool Validate(HandoffSeed seed, Func<int, IdState> state, Func<int, LiveTarget> live,
      out SetValueChange change, out BuildState declared, out ToolResult error)
    {
      change = null;
      declared = null;
      error = null;

      if (seed?.Target == null)
      {
        error = ToolResult.Fail(ToolErrors.InvalidArgs,
          "set_value needs \"target\" - the id of the EXISTING slider or panel to change, from a previous read.");
        return false;
      }
      if (seed.State == null)
      {
        error = ToolResult.Fail(ToolErrors.InvalidArgs,
          "set_value needs \"state\" - {min, max, value, rounding?} for a Number Slider, {text} for a Panel.");
        return false;
      }

      var id = seed.Target.Value;
      var s = state == null ? IdState.Unknown : state(id);
      if (s == IdState.Unknown)
      {
        error = ToolResult.Fail(ToolErrors.UnknownId,
          "No component with id " + id + " exists. Ids come from this session's reads - do not invent them.",
          new JsonObject { ["id"] = id });
        return false;
      }
      if (s == IdState.Retired)
      {
        error = ToolResult.Fail(ToolErrors.RetiredId,
          "Component " + id + " is no longer on the canvas (it was deleted, or an undo removed it).",
          new JsonObject { ["id"] = id });
        return false;
      }

      var target = live == null ? null : live(id);
      if (target == null || string.IsNullOrWhiteSpace(target.Type))
      {
        error = ToolResult.Fail(ToolErrors.RetiredId,
          "Component " + id + " could not be read from the canvas, so nothing can be promised about it.",
          new JsonObject { ["id"] = id });
        return false;
      }

      // Stream A's parser, unforked: refuses by type name with the real list, and every measured trap.
      if (!BuildStateParser.TryParse(seed.State, target.Type, "component " + id, out declared, out error))
        return false;
      if (declared == null)
      {
        error = ToolResult.Fail(ToolErrors.InvalidArgs, "set_value needs a non-empty \"state\".");
        return false;
      }

      if (!SetValueCore.MayChangeRounding(declared, target.Current, out var refusal))
      {
        error = ToolResult.Fail(ToolErrors.PreconditionRefused, refusal, new JsonObject { ["id"] = id });
        return false;
      }

      change = new SetValueChange
      {
        Id = id,
        Type = target.Type,
        Nick = target.Nick,
        Before = target.Current == null ? "(unread)" : SetValueCore.Describe(target.Current),
        After = SetValueCore.Describe(declared),
      };
      return true;
    }
  }
}
