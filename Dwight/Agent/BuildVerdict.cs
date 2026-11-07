using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Dwight.Data;

namespace Dwight.Agent
{
  /// <summary>
  /// WHAT A BUILD PLACED AND REMOVED, WITH OBJECTS AND WIRE SOURCES KEPT APART (#643, Finding B).
  ///
  /// A wire's source is a PARAM: the fingerprint reader records each input's sources by the source param's
  /// own InstanceGuid. For a floating param (a Number Slider, a Panel) that is the object's guid; for a
  /// COMPONENT it is the guid of one of its OUTPUT params, which is a different guid. The verdict used to
  /// compare sources against component guids, so a new Construct Point wired into an existing input read as
  /// "rewired from an existing component" and reverted, and a replaced component's outgoing wire was never
  /// recognised as its own. Two sets, computed by the shim from live params, and never one standing in for
  /// the other.
  /// </summary>
  internal sealed class BuildIdentity
  {
    /// <summary>The top-level objects this build created.</summary>
    public ISet<Guid> CreatedObjects { get; set; } = new HashSet<Guid>();

    /// <summary>Every param a created object can be wired FROM: a component's outputs, or a floating param
    /// itself.</summary>
    public ISet<Guid> CreatedSources { get; set; } = new HashSet<Guid>();

    /// <summary>The top-level objects this rebuild removed - the resolved claim's Remove (#631).</summary>
    public ISet<Guid> ReplacedObjects { get; set; } = new HashSet<Guid>();

    /// <summary>
    /// The DECLARED reconnections, as carried out (#625): the predicted delta of ADR-090 layer 1. On each named
    /// input the verdict expects the cut source at its position replaced, in place, by the declared new source -
    /// and nothing else. A cut the build did not declare is not here, so its loss still reads as rewired.
    /// </summary>
    public IReadOnlyList<PredictedReconnection> Reconnections { get; set; } = new PredictedReconnection[0];
  }

  /// <summary>One reconnection the verdict predicts: on <see cref="InputGuid"/>, the source at
  /// <see cref="Position"/> was <see cref="OldSource"/> and must now be <see cref="NewSource"/>. An empty
  /// <see cref="OldSource"/> is a RESTORE (#625's narrow sibling): <see cref="NewSource"/> is inserted there.</summary>
  internal sealed class PredictedReconnection
  {
    public Guid InputGuid { get; set; }
    public int Position { get; set; }
    public Guid OldSource { get; set; }
    public Guid NewSource { get; set; }
  }

  /// <summary>
  /// THE PRESERVATION CHECK - the first leg of <c>build_region</c>'s verdict trio (#193a), and the one
  /// that needed the most thought.
  ///
  /// WHY IT IS NOT BYTE-IDENTITY. Reorganise's guard is byte-identical-or-abort, because Reorganise
  /// only moves things. A build ADDS, and a useful build wires INTO what is already there - which
  /// necessarily changes those components' input sources. Byte-identity would reject every build that
  /// did anything. So preservation is an EXPECTED-DELTA (the ADR-090 family): the pre-existing graph
  /// must be unchanged EXCEPT for wires arriving from the components this build created.
  ///
  /// Three rules, and rule 3 is the one with teeth:
  ///   1. Nothing pre-existing was DELETED.
  ///   2. No pre-existing object's own properties moved - type, value, locked, tree toggles, persistent
  ///      data. A build has no business editing a slider or flipping a graft.
  ///   3. Every wire that existed still exists, in order, and any NEW source on a pre-existing input
  ///      comes from a component this build created. That is what separates "connected into your
  ///      definition" (asked for) from "rewired your definition" (never asked for) - the failure that
  ///      would be invisible and unrecoverable a hundred components later.
  ///
  /// GH-free: it compares two <see cref="FpDoc"/> snapshots from the existing fingerprint reader, so it
  /// inherits that reader's coverage (values, tree toggles, Locked, ordered sources) rather than
  /// inventing a second, weaker notion of "changed".
  /// </summary>
  internal static class BuildPreservation
  {
    internal sealed class Result
    {
      public bool Preserved => Deleted.Count == 0 && Mutated.Count == 0 && Rewired.Count == 0;
      public List<Guid> Deleted { get; } = new List<Guid>();
      public List<Guid> Mutated { get; } = new List<Guid>();
      public List<Guid> Rewired { get; } = new List<Guid>();

      /// <summary>
      /// EVERY BREACH, WITH WHAT IT WAS (U34, #925). The three lists above say WHICH objects failed the leg; this says
      /// ON WHAT - which input, and what happened to it - so the refusal can name a cause instead of leaving the model
      /// to invent one. Two false "tool limitation" claims in one session were the model explaining a
      /// <c>verification_failed</c> it could not see into. GH-free: guids and plain words; the shim maps the guids to
      /// names and ids.
      /// </summary>
      public List<PreservationBreach> Breaches { get; } = new List<PreservationBreach>();

      public string Describe()
      {
        if (Preserved) return "the existing definition is unchanged apart from the declared new wires";
        var parts = new List<string>();
        if (Deleted.Count > 0) parts.Add(Deleted.Count + " existing component(s) removed");
        if (Mutated.Count > 0) parts.Add(Mutated.Count + " existing component(s) altered");
        if (Rewired.Count > 0) parts.Add(Rewired.Count + " existing input(s) rewired from something other than the new components");
        return string.Join("; ", parts);
      }
    }

    /// <summary>
    /// Judge <paramref name="after"/> against <paramref name="before"/> for a build whose identity is
    /// <paramref name="identity"/>.
    ///
    /// A REBUILD'S BASELINE (Stage B, #352). A rebuild deletes, and rule 1 says nothing is deleted - so the
    /// replaced OBJECTS are removed from `before` (see <see cref="WithoutReplaced"/>) and the three rules run
    /// unchanged against what remains. WHAT BOUNDS THAT SET IS WHAT DWIGHT MAY REMOVE (#631): the resolved
    /// claim's Remove - placed by Dwight, untouched since, and named on the consent card. This check cannot
    /// tell an adopted component from an untouched one, so it trusts the caller for exactly that, which is why
    /// the removal set has one source.
    ///
    /// THE REPLACED OBJECTS' OUTGOING WIRES ARE NO LONGER SPLICED OUT (#625, #643). The splice used to strip
    /// replaced guids from every remaining input's sources, so that a replacement wired into the same input did
    /// not read as rewired. It keyed off OBJECT guids, so it never matched a component's output (that rebuild
    /// always reverted) and it DID match a floating param's (that rebuild silently disconnected its consumer).
    /// Both were wrong. A wire from what a rebuild removes into something it does not remove is a CUT, and a
    /// cut the build did not declare is tampering: it stays in the baseline, so losing it reads as rewired and
    /// the build reverts. Declared reconnections are what make such a rebuild legal.
    /// </summary>
    public static Result Check(FpDoc before, FpDoc after, BuildIdentity identity)
    {
      var result = new Result();
      if (before == null || after == null) return result;
      identity = identity ?? new BuildIdentity();
      var createdSources = identity.CreatedSources ?? new HashSet<Guid>();

      before = WithoutReplaced(before, identity.ReplacedObjects);

      var predictedByInput = (identity.Reconnections ?? new PredictedReconnection[0])
        .Where(r => r != null)
        .GroupBy(r => r.InputGuid)
        .ToDictionary(g => g.Key, g => g.OrderBy(r => r.Position).ToList());

      var afterByGuid = after.Objects.ToDictionary(o => o.Guid);

      foreach (var b in before.Objects)
      {
        if (!afterByGuid.TryGetValue(b.Guid, out var a))
        {
          result.Deleted.Add(b.Guid);
          result.Breaches.Add(PreservationBreach.Deleted(b.Guid));
          continue;
        }

        // 2. the object's own properties - anything the fingerprint reader considers behaviour.
        var changed = new List<string>();
        if (!string.Equals(b.TypeName, a.TypeName, StringComparison.Ordinal)) changed.Add("type");
        if (!string.Equals(b.ValueToken ?? "", a.ValueToken ?? "", StringComparison.Ordinal)) changed.Add("value");
        if (b.Locked != a.Locked) changed.Add("locked state");
        if (b.DataMappingNone != a.DataMappingNone) changed.Add("tree mapping");
        if (b.Simplify != a.Simplify) changed.Add("simplify toggle");
        if (b.Reverse != a.Reverse) changed.Add("reverse toggle");
        if (b.HasPersistentData != a.HasPersistentData) changed.Add("persistent data");
        if (changed.Count > 0)
        {
          result.Mutated.Add(b.Guid);
          result.Breaches.Add(PreservationBreach.Altered(b.Guid, "its " + string.Join(", ", changed) + " changed"));
          continue;
        }

        // 3. wiring: existing sources survive in order; additions may only come from the new components.
        var beforeInputs = b.Inputs ?? new List<FpInput>();
        var afterInputs = a.Inputs ?? new List<FpInput>();
        if (beforeInputs.Count != afterInputs.Count)
        {
          result.Mutated.Add(b.Guid);
          result.Breaches.Add(PreservationBreach.Altered(b.Guid, "its number of inputs changed from " + beforeInputs.Count + " to " + afterInputs.Count));
          continue;
        }

        for (var i = 0; i < beforeInputs.Count; i++)
        {
          var bs = beforeInputs[i].Sources ?? new List<Guid>();
          var as_ = afterInputs[i].Sources ?? new List<Guid>();
          var inputGuid = beforeInputs[i].Guid;

          // #625: a DECLARED reconnection is the predicted delta - the cut source replaced IN PLACE by its new one.
          // A prediction that does not fit the baseline (the old source is not where the plugin recorded it) is not
          // excused: the input reads as rewired, and the build reverts.
          if (predictedByInput.TryGetValue(inputGuid, out var recs))
          {
            bs = new List<Guid>(bs);
            var fits = true;
            foreach (var r in recs)
            {
              // #625's narrow sibling: a RESTORE has no old source in the baseline - the loss already happened - so
              // the new source is INSERTED at the position the lost one held.
              if (r.OldSource == Guid.Empty)
              {
                if (r.Position < 0 || r.Position > bs.Count) { fits = false; break; }
                bs.Insert(r.Position, r.NewSource);
                continue;
              }
              if (r.Position < 0 || r.Position >= bs.Count || bs[r.Position] != r.OldSource) { fits = false; break; }
              bs[r.Position] = r.NewSource;
            }
            if (!fits)
            {
              result.Rewired.Add(b.Guid);
              result.Breaches.Add(PreservationBreach.Rewired(b.Guid, inputGuid,
                "the declared reconnection did not fit - the cut source was not at the position the plugin recorded"));
              break;
            }
          }

          if (!string.Equals(beforeInputs[i].MappingToken ?? "", afterInputs[i].MappingToken ?? "", StringComparison.Ordinal) ||
              !string.Equals(beforeInputs[i].PersistentToken ?? "", afterInputs[i].PersistentToken ?? "", StringComparison.Ordinal))
          {
            result.Mutated.Add(b.Guid);
            result.Breaches.Add(PreservationBreach.Altered(b.Guid, "its tree mapping or persistent data changed", inputGuid));
            break;
          }

          // D1 - PREFIX, not subsequence. New sources may only APPEND; they may never be inserted among
          // the existing ones. GH merges multi-source input data in SOURCE-LIST ORDER and downstream
          // logic indexes into that list (ADR-090; RR20 #66), so [A,B] -> [C,A,B] leaves the wires
          // technically intact while the component now receives C first and every positional index
          // downstream resolves to a different item. A subsequence check passes that; a prefix check
          // does not. Appending is what AddSource does by default, so this pins the natural behaviour
          // rather than constraining it - and it still holds if the spec ever grows an insertion index.
          if (as_.Count < bs.Count)   // a wire was lost
          {
            result.Rewired.Add(b.Guid);
            result.Breaches.Add(PreservationBreach.Rewired(b.Guid, inputGuid,
              "a wire into it was lost (" + bs.Count + " source" + (bs.Count == 1 ? "" : "s") + " before, " + as_.Count + " after)"));
            break;
          }
          if (!as_.Take(bs.Count).SequenceEqual(bs))   // reordered or inserted
          {
            result.Rewired.Add(b.Guid);
            result.Breaches.Add(PreservationBreach.Rewired(b.Guid, inputGuid,
              "its existing sources were reordered, or a new one was inserted before them instead of after"));
            break;
          }
          // #643: a new source is a PARAM guid, so it is judged against the SOURCES the build created - a new
          // component's output included - never against the created objects' own guids.
          if (as_.Skip(bs.Count).Any(g => !createdSources.Contains(g)))   // new wire from an EXISTING component
          {
            result.Rewired.Add(b.Guid);
            result.Breaches.Add(PreservationBreach.Rewired(b.Guid, inputGuid,
              "a new source came from an EXISTING component, not from anything this build placed"));
            break;
          }
        }
      }
      return result;
    }

    /// <summary>
    /// The baseline a REBUILD is judged against: the definition without the objects it removed. Their
    /// OBJECTS only - every remaining input keeps its sources exactly, so a wire cut from a removed object
    /// into one that stays is still expected, and its loss is caught (#625, #643).
    ///
    /// A pure function over the snapshot, so the three rules above never learn about rebuilding at all.
    /// </summary>
    private static FpDoc WithoutReplaced(FpDoc before, ISet<Guid> replaced)
    {
      if (replaced == null || replaced.Count == 0) return before;

      var spliced = new FpDoc();
      foreach (var o in before.Objects)
      {
        if (replaced.Contains(o.Guid)) continue;

        var copy = new FpObject
        {
          Guid = o.Guid,
          TypeName = o.TypeName,
          ValueToken = o.ValueToken,
          Locked = o.Locked,
          IsGenericParam = o.IsGenericParam,
          SourceCount = o.SourceCount,
          DataMappingNone = o.DataMappingNone,
          Simplify = o.Simplify,
          Reverse = o.Reverse,
          HasPersistentData = o.HasPersistentData,
        };
        foreach (var input in o.Inputs ?? new List<FpInput>())
          copy.Inputs.Add(new FpInput
          {
            Guid = input.Guid,
            MappingToken = input.MappingToken,
            PersistentToken = input.PersistentToken,
            Sources = new List<Guid>(input.Sources ?? new List<Guid>()),
          });
        spliced.Objects.Add(copy);
      }
      return spliced;
    }
  }

  /// <summary>One thing the preservation leg found changed that a build must never change (U34). GH-free: guids and
  /// plain words; <c>GhBuildRegion</c> maps the guids to the names and ids a model can relay.</summary>
  internal sealed class PreservationBreach
  {
    public const string KindDeleted = "deleted";
    public const string KindAltered = "altered";
    public const string KindRewired = "rewired";

    public Guid ObjectGuid { get; private set; }
    public string Kind { get; private set; }
    /// <summary>The input the breach is on, when it is on one - null for the object as a whole.</summary>
    public Guid? InputGuid { get; private set; }
    /// <summary>What happened, in words that need no guid to read.</summary>
    public string Detail { get; private set; }

    public static PreservationBreach Deleted(Guid obj) =>
      new PreservationBreach { ObjectGuid = obj, Kind = KindDeleted, Detail = "it was removed" };

    public static PreservationBreach Altered(Guid obj, string detail, Guid? input = null) =>
      new PreservationBreach { ObjectGuid = obj, Kind = KindAltered, Detail = detail, InputGuid = input };

    public static PreservationBreach Rewired(Guid obj, Guid input, string detail) =>
      new PreservationBreach { ObjectGuid = obj, Kind = KindRewired, Detail = detail, InputGuid = input };
  }

  /// <summary>Which leg a reverted build failed, and on what - the cause the refusal carries (U34, #925).</summary>
  internal sealed class BuildFailure
  {
    /// <summary>"preservation" or "completeness" - the two legs that revert. Solve-health never reverts; its count
    /// rides the verdict instead.</summary>
    public string Leg { get; set; }
    /// <summary>On what, naming the component and input where there is one.</summary>
    public string What { get; set; }
    /// <summary>The other leg's failure too, when both failed - never dropped, so the model sees the whole picture.</summary>
    public string Also { get; set; }
  }

  /// <summary>
  /// WHICH LEG FAILED AND ON WHAT, SAID (U34, #925).
  ///
  /// THE DEFECT. A one-wire patch came back <c>verification_failed</c> and the model told the user *"Line SDL has an
  /// input and an output both named L, and build_region's wire resolver can't disambiguate... a genuine tool
  /// limitation"* - false (the resolver looks up inputs and outputs on their own sides). A second failure drew
  /// *"build_region cannot remove or replace an existing wire"* - also false. Both were #315's class spoken by the
  /// model on our behalf: a claim about what the product cannot do, invented to explain an outcome it could not see
  /// into. The data existed in the verdict; it never reached the sentence.
  ///
  /// So the refusal names the LEG (preservation / completeness) and the SUBJECT (which component, which input, what
  /// happened to it), from the verdict's own structure. GH-free: the labels arrive through a delegate the shim
  /// supplies, so the sentence is tested per leg here and only the name lookup is left to a sitting.
  /// </summary>
  internal static class BuildFailureDiagnosis
  {
    public const string Preservation = "preservation";
    public const string Completeness = "completeness";
    public const string SolveHealth = "solve-health";

    /// <summary>Breaches named on the line before the rest are counted (ADR-035).</summary>
    public const int MaxNamed = 3;

    /// <summary>
    /// The failure a reverted verdict carries, or null when nothing failed - a composer that could describe a failure
    /// of a passing build would be the instrument inventing the defect it exists to stop.
    /// </summary>
    /// <param name="label">What a guid is called on the canvas: (object, input or null) to "Line SDL (23), input L".
    /// Null, or a null result, falls back to "an existing component" - never to a guid.</param>
    public static BuildFailure Diagnose(BuildPreservation.Result preservation, Func<Guid, Guid?, string> label,
      int placed, int componentsAsked, int wired, int wiresAsked, int reconnectDone, int reconnectPlanned,
      string stateMismatch)
    {
      var preserved = preservation?.Preserved ?? true;
      var completenessWhat = CompletenessWhat(placed, componentsAsked, wired, wiresAsked, reconnectDone, reconnectPlanned, stateMismatch);

      if (preserved && completenessWhat == null) return null;

      if (!preserved)
      {
        return new BuildFailure
        {
          Leg = Preservation,
          What = PreservationWhat(preservation, label),
          Also = completenessWhat == null ? null : "Completeness failed too: " + completenessWhat + ".",
        };
      }
      return new BuildFailure { Leg = Completeness, What = completenessWhat };
    }

    /// <summary>"Preservation failed on Line SDL (23), input L: a new source came from an EXISTING component, not
    /// from anything this build placed" - the first <see cref="MaxNamed"/> breaches, then a count.</summary>
    private static string PreservationWhat(BuildPreservation.Result p, Func<Guid, Guid?, string> label)
    {
      var breaches = p.Breaches ?? new List<PreservationBreach>();
      if (breaches.Count == 0)
      {
        // The lists say it failed and nothing recorded why: say that, never invent a cause.
        return "an existing component was changed (" + p.Describe() + ")";
      }
      var named = breaches.Take(MaxNamed).Select(b => Name(label, b.ObjectGuid, b.InputGuid) + ": " + b.Detail).ToList();
      var text = string.Join("; ", named);
      if (breaches.Count > MaxNamed) text += "; and " + (breaches.Count - MaxNamed) + " more";
      return text;
    }

    private static string CompletenessWhat(int placed, int componentsAsked, int wired, int wiresAsked,
      int reconnectDone, int reconnectPlanned, string stateMismatch)
    {
      var parts = new List<string>();
      if (placed < componentsAsked) parts.Add(placed + " of " + componentsAsked + " declared components were placed");
      if (wired < wiresAsked) parts.Add(wired + " of " + wiresAsked + " declared wires landed");
      if (reconnectDone < reconnectPlanned) parts.Add(reconnectDone + " of " + reconnectPlanned + " declared reconnections were carried out");
      if (!string.IsNullOrWhiteSpace(stateMismatch)) parts.Add("component " + stateMismatch.Trim());
      return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    private static string Name(Func<Guid, Guid?, string> label, Guid obj, Guid? input)
    {
      string name = null;
      try { name = label?.Invoke(obj, input); } catch { name = null; }
      return string.IsNullOrWhiteSpace(name) ? "an existing component" : name.Trim();
    }

    /// <summary>The verdict's note: the leg, capitalised, then the subject.</summary>
    public static string Note(BuildFailure f) =>
      f == null ? null : Cap(f.Leg) + " failed on " + f.What + "." + (f.Also == null ? "" : " " + f.Also);

    /// <summary>The refusal's message, for the model: the leg in capitals so it cannot be skimmed past, then the cause.</summary>
    public static string Message(BuildFailure f) =>
      "The build was applied, failed its " + (f?.Leg ?? "own").ToUpperInvariant() + " check, and was reverted - the canvas " +
      "is as it was. " + Note(f) + " Fix that and build again; this is not a limit of the tool.";

    /// <summary>A build that threw BEFORE its check could run (a port or reconnection that did not resolve, a type that
    /// would not instantiate): completeness could not be met, and the canvas was rolled back.</summary>
    public static string StoppedBefore(string reason) =>
      "The build stopped before its check and the canvas is as it was. COMPLETENESS could not be met: " + (reason ?? "unknown") +
      ". Fix that and build again.";

    /// <summary>The solve did not finish inside the watchdog: solve-health could not be judged, so the build reverted.</summary>
    public static string SolveTimedOut(int ms) =>
      "SOLVE-HEALTH could not be judged: the definition did not finish solving within " + (ms / 1000) + " s, so the build " +
      "could not be checked and was reverted - the canvas is as it was.";

    /// <summary>The check itself threw: no leg was judged, and the build reverted.</summary>
    public static string CheckThrew(string reason) =>
      "The build's check itself failed before any leg could be judged (" + (reason ?? "unknown") + "), so it was reverted - " +
      "the canvas is as it was.";

    private static string Cap(string s) => string.IsNullOrEmpty(s) ? "" : char.ToUpperInvariant(s[0]) + s.Substring(1);
  }

  /// <summary>
  /// The verdict a mutating tool returns (ADR-098: every mutating tool returns its verification verdict).
  ///
  /// #193a - WHAT IT DOES NOT CLAIM, stated in the payload itself. The trio proves the build LANDED AS
  /// SPECIFIED and broke nothing that was already there. It does NOT prove the region computes what the
  /// user actually wants: no machine oracle can know that, and "applied" must never be read as "correct".
  /// Plan-level rightness belongs to the human and the chat (WP-3/WP-4). Putting the limit in the schema
  /// rather than in a comment is the 7.0 rule - the panel never shows a state the canvas contradicts -
  /// applied to a machine-readable result.
  /// </summary>
  internal static class BuildVerdict
  {
    public static JsonObject Build(bool applied, IReadOnlyList<int> placed, int wired, int wiresAsked,
      BuildPreservation.Result preservation, int componentsAsked, int newErrors, JsonArray errorList,
      string note = null, string region = null, int replaced = 0, int? attemptsLeft = null,
      JsonArray initialState = null, JsonArray reconnected = null,
      JsonArray placedFacts = null, string units = null, string failedLeg = null, string userMessage = null)
    {
      var complete = placed.Count == componentsAsked && wired == wiresAsked;

      var verdict = new JsonObject
      {
        ["status"] = applied ? "applied" : "reverted",
        // #206b: on a revert those components no longer exist, so no id here may look addressable -
        // the structured field has to say what the prose says. A count, never ids the caller could use.
        ["placed"] = applied
          ? new JsonArray(placed.Select(i => (JsonNode)i).ToArray())
          : new JsonArray(),
        ["wired"] = applied ? wired : 0,

        // leg 1 - nothing pre-existing was broken
        ["preserved"] = preservation?.Preserved ?? false,
        ["preservation"] = preservation?.Describe() ?? "not checked",

        // leg 2 - the intended change actually HAPPENED (a silent no-op is not a success)
        ["complete"] = complete,
        ["asked"] = new JsonObject { ["components"] = componentsAsked, ["wires"] = wiresAsked },

        // leg 3 - what Grasshopper says about it now
        ["newErrors"] = newErrors,
        ["errors"] = errorList ?? new JsonArray(),

        ["undo"] = applied ? "one Ctrl-Z reverts this whole build" : "nothing to undo - the canvas is as it was",

        // The honest ceiling, in the payload rather than a comment - the reader who most needs it is
        // the one who cannot check for themselves.
        ["proves"] = "that the build landed as specified, and that no existing component was deleted, " +
                     "edited, or rewired apart from the new wires and the reconnections this build declared",
        ["doesNotProve"] = "that this region computes what you actually want - check the result yourself. " +
                           "Note also that where this build fed an existing component, that component's " +
                           "own output legitimately CHANGES; 'preserved' means its wiring and settings " +
                           "were not tampered with, not that its data is the same as before.",
      };
      if (!applied) verdict["revertedComponents"] = placed?.Count ?? 0;

      // U34 (#925): WHICH LEG FAILED, as a field a model can key on - "preservation" or "completeness". The note
      // beside it says on WHAT. A model that can read the cause does not invent a capability limit to explain it.
      if (!applied && !string.IsNullOrEmpty(failedLeg)) verdict["failedLeg"] = failedLeg;

      // U46: the USER'S reading of the refusal, when it differs from the model's (the reflected signatures are repair
      // material for the model and not for the user's line). The click reads this; the model reads the message.
      if (!applied && !string.IsNullOrEmpty(userMessage)) verdict["userMessage"] = userMessage;

      // U26 (#916, #923): WHAT WAS CHECKED, SAID - never the word "verified" - and the post-solve facts about the
      // components this build PLACED: their own warnings and errors, their empty outputs, and one geometry fact per
      // output that holds geometry (count, and the total length, total area or bounding size the type has). Read by
      // the geometry reads' own reader after the solve, so "486 lines, total length 0 m" reaches the model and the
      // user on the same line that reports the build. A fact, not a judgement: nothing here reverts.
      if (applied)
      {
        verdict["checked"] = BuildPlacedFacts.Checked(newErrors, wiresAsked);
        verdict["placedFacts"] = placedFacts ?? new JsonArray();
        if (!string.IsNullOrEmpty(units)) verdict["units"] = units;
      }

      // #394: the values this build actually SET, read back off the components rather than echoed from
      // the spec. The user-facing row is the reason it exists - #377 was a value nobody could see - but
      // the model gets it too, and for the model it is confirmation rather than instruction: it asked for
      // a number, and this is the number that is now on the canvas.
      if (applied && initialState != null && initialState.Count > 0)
        verdict["initialState"] = initialState;

      // #625: the wires this rebuild cut and put back, in place, from its new components - plugin-authored, and
      // only on a build that stood (a reverted one reconnected nothing that still exists).
      if (applied && reconnected != null && reconnected.Count > 0)
        verdict["reconnected"] = reconnected;

      // WP-4 Stage B: where this landed, and what it cost. The region is stated because a build is now
      // AIMED, and a verdict that did not say where it landed would be answering a different question
      // from the one asked.
      if (!string.IsNullOrEmpty(region))
      {
        verdict["region"] = region;
        if (attemptsLeft.HasValue) verdict["attemptsLeftForRegion"] = attemptsLeft.Value;
        if (replaced > 0)
        {
          verdict["replaced"] = replaced;
          // Said plainly, because a delete is the one thing here a reader must never be surprised by -
          // and the bound on WHAT was deleted is the claim that makes it safe.
          // #625 commit 7: "THIS conversation placed" went stale at F1(b) - what Dwight placed is a document-scoped
          // fact, and the removal is the resolved claim (#631). And "nothing else was touched" is false for a rebuild
          // that reconnected what it cut, so the exception is named rather than the claim left standing.
          var reconnectedCount = applied ? reconnected?.Count ?? 0 : 0;
          verdict["replacedNote"] = "this rebuilt the region: " + replaced + " component(s) Dwight placed there " +
                                    "and the user had not edited since were removed first. Nothing else was touched" +
                                    (reconnectedCount == 0 ? ""
                                      : " apart from the " + reconnectedCount + " wire" + (reconnectedCount == 1 ? "" : "s") +
                                        " it reconnected, each back in its own place") +
                                    ", and one Ctrl-Z restores the previous attempt.";

          // #363: THE PAYLOAD NAMES ITS REFERENCE FRAME. `preserved` above is not measured against the
          // canvas as it stood a moment ago - a rebuild is judged against the definition WITHOUT this
          // region's previous attempt, which is the only frame in which "nothing was deleted" is both
          // true and meaningful. The claim changed frames, so the payload says so: #193's rule that the
          // verdict states what it proves reaches the basis it proves it against, and both readers of
          // this payload - the model and the thread summary - need it.
          verdict["judgedAgainst"] = "the definition WITHOUT this region's previous attempt. 'preserved' " +
                                     "is measured from before those " + replaced + " component(s) existed, " +
                                     "so removing them is not counted as a deletion - and nothing outside " +
                                     "that set was allowed to change" +
                                     (reconnectedCount == 0 ? ""
                                       : " except the reconnections this build declared, each checked in place") + ".";
        }
      }

      if (note != null) verdict["note"] = note;
      return verdict;
    }
  }
}
