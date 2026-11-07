using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Dwight.Data;

namespace Dwight.Agent
{
  /// <summary>
  /// WHAT THE MODEL CLAIMS ITS EDIT DOES (Phase-9 U20). An ENUM, never prose, because the claim decides
  /// WHICH PROOF RUNS - and a decision the model could phrase its way into is not a decision the plugin
  /// controls (#235).
  /// </summary>
  internal enum ScriptEditClaim
  {
    Unknown = 0,

    /// <summary>The edit does not change what the component produces. Checked by BESIDE-VERIFY against the
    /// original's own outputs, in the same solve - so a model that declares this and changes behaviour is
    /// caught by the proof rather than trusted.</summary>
    Preserves,

    /// <summary>The edit changes what the component produces, deliberately. Checked as a PREDICTED DELTA:
    /// exactly this component's source changes, the definition still solves, and the user reads the diff.
    /// Adding a feature is not a hole in the proof; it is a different claim.</summary>
    Changes,
  }

  /// <summary>What the plugin read off the target script component, live.</summary>
  internal sealed class LiveScript
  {
    public string Type { get; set; }
    public string Nick { get; set; }

    /// <summary>
    /// The script's language ("csharp", "python", "ironpython", "vb", or "script"), DERIVED from <see cref="Type"/>
    /// and never stored beside it - so a component cannot be described as one language and repaired as another.
    ///
    /// It exists because that happened: the refusal chose its compile-repair header from the edit's display LABEL
    /// (a nickname, or a friendly word like "C# Script"), which the language rule reads as "script", so every C#
    /// edit that failed to compile was told "FAILED TO RUN. The interpreter said:". The label was never a type
    /// name; the edit simply carried no language, so the call site had nothing correct to pass.
    /// </summary>
    public string Language => SourceBound.LanguageOf(Type);

    /// <summary>The source as it stands NOW. Null when it could not be read at all - which is a refusal,
    /// never an empty string, because Dwight must not overwrite what it cannot show the user a diff of.</summary>
    public string Source { get; set; }

    /// <summary>Did a read actually happen? Separates "an empty script" from "a locked one" - the same
    /// distinction #779 turns on, and for the same reason.</summary>
    public bool SourceReadable { get; set; }

    /// <summary>
    /// Is this component reporting an ERROR right now? It decides whether "preserves" has a referent at all.
    ///
    /// An errored script STILL EMITS A NULL ITEM ON EVERY OUTPUT (the Phase-7 live finding), so a broken
    /// original and a broken edit COMPARE EQUAL - "preserves" would pass by matching nulls against nulls.
    /// That is the same false pass that made errors gate the compare in the first place, arriving from the
    /// other side.
    /// </summary>
    public bool Errored { get; set; }

    /// <summary>Is it LOCKED (disabled)? A locked component computes nothing, so it has the same
    /// no-referent problem as an errored one, for a different reason - and the two are refused separately,
    /// because "it is broken" and "you switched it off" call for different corrections (#208).</summary>
    public bool Locked { get; set; }

    /// <summary>The component's existing port names, in order. The model may not touch these: they are
    /// read, never declared (the Phase-7 law held as tightly as it can be on an existing component).</summary>
    public IReadOnlyList<string> Inputs { get; set; } = new string[0];
    public IReadOnlyList<string> Outputs { get; set; } = new string[0];

    /// <summary>Brief CP: each existing input's access as it stands, where it is not item.</summary>
    public IReadOnlyDictionary<string, string> InputAccess { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
  }

  /// <summary>Brief CP: one existing input whose access an edit changes, in the words the card and the report use.</summary>
  internal sealed class AccessChange
  {
    public string Name { get; set; }
    public string From { get; set; }
    public string To { get; set; }
  }

  /// <summary>The exact edit the card names and the click applies.</summary>
  internal sealed class ScriptEditChange
  {
    public int TargetId { get; set; }

    /// <summary>The component in the user's words - a nickname, or a friendly word like "C# Script". For
    /// SENTENCES only: it is not a type name and no rule may be read off it (see <see cref="Language"/>).</summary>
    public string Label { get; set; }

    /// <summary>The target's language, copied from <see cref="LiveScript.Language"/> when the ask is validated -
    /// the one definition, carried to every call site that needs it.</summary>
    public string Language { get; set; }

    /// <summary>Is this a COMPILED language? It decides whether a failure is reported to the model as the
    /// compiler's words or the interpreter's. C# and VB compile; Python and IronPython are interpreted; and a
    /// language that could not be determined is NULL - not defaulted to either, because a header naming the wrong
    /// one is a guess stated as a fact (#208).</summary>
    public bool? Compiled
    {
      get
      {
        switch (Language)
        {
          case "csharp": case "vb": return true;
          case "python": case "ironpython": return false;
          default: return null;
        }
      }
    }

    public ScriptEditClaim Claim { get; set; }

    /// <summary>The source as it will be applied - already normalised, so the text shown IS the text
    /// tested IS the text applied (#153).</summary>
    public string NewSource { get; set; }

    /// <summary>The source the diff was taken against. Re-read at click time and required to still match,
    /// or the card was describing a file that has since moved (see <see cref="BaseMoved"/>).</summary>
    public string BaseSource { get; set; }

    public SourceDiff Diff { get; set; }

    /// <summary>Ports to APPEND, never to remove, reorder or rename. Empty for a body-only edit.</summary>
    public IReadOnlyList<string> AddInputs { get; set; } = new string[0];
    public IReadOnlyList<string> AddOutputs { get; set; } = new string[0];

    /// <summary>Brief CP: the existing inputs whose access changes. Empty for an edit that leaves them.</summary>
    public IReadOnlyList<AccessChange> AccessChanges { get; set; } = new AccessChange[0];

    /// <summary>Brief CP: each appended input whose declared access is not item.</summary>
    public IReadOnlyDictionary<string, string> AddedInputAccess { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every input the apply sets the access of, by name: the changes and the appended ones.</summary>
    public IReadOnlyDictionary<string, string> AccessToSet
    {
      get
      {
        var all = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in AddedInputAccess ?? new Dictionary<string, string>()) all[kv.Key] = kv.Value;
        foreach (var c in AccessChanges ?? new AccessChange[0]) all[c.Name] = c.To;
        return all;
      }
    }

    /// <summary>Does the edit leave the source as it was? It then only changes how the inputs are fed, and no sentence may say the source was replaced.</summary>
    public bool KeepsSource => Diff != null && Diff.Identical;

    /// <summary>Has the target's source moved since the diff was taken? The card promised a change from a
    /// specific starting point; if that point has moved the diff is no longer true, and a card asserting
    /// something untrue at the moment of approval is the one thing a card may never be (#775).</summary>
    public bool BaseMoved(string sourceNow) =>
      !string.Equals(SourceToken.Normalise(BaseSource), SourceToken.Normalise(sourceNow), StringComparison.Ordinal);
  }

  /// <summary>
  /// THE ASK, VALIDATED (Phase-9 U20) - run at ask time, before the card exists, and again at click time,
  /// because the canvas may have moved between them. GH-free (ADR-091): the live facts arrive as functions,
  /// so every refusal below is asserted by a test rather than reviewed.
  ///
  /// THE UNIT'S ORIGIN, because it decides what "good" means here. Tharun asked for an offset feature on a
  /// Python script that computes area and centroid. Dwight could not do it, and said so honestly, and that
  /// honesty was the product failing him (#721). Equivalence has nothing to say about work that is NEW, so
  /// every request for something new hit a wall that was never a safety decision - it was the shape of the
  /// only proof we had.
  ///
  /// SO THE MODEL DECLARES THE CLAIM AND THE PLUGIN VERIFIES THE ONE IT DECLARED. "Preserves" runs
  /// beside-verify against the original's own outputs; "changes" runs the predicted delta and shows the
  /// user the diff. The claim is an enum, so which proof runs is a pure function of what the user was
  /// SHOWN - not of anything the model wrote beside it.
  ///
  /// INTERFACE CHANGES ARE APPEND-ONLY IN v1, and the reason is structural rather than cautious. Appending
  /// a port cuts NOTHING - existing ports keep their index and every existing wire keeps its param object -
  /// so the predicted delta stays "this component and nothing else". Removing or reordering a port severs
  /// or re-binds existing wires, which is a BOUNDARY CUT: the class #625 already governs, with its own
  /// declare-or-refuse machinery pointed at a different case. Serving the field case does not need it - an
  /// offset feature wants an offset INPUT, which appends.
  /// </summary>
  internal static class EditScriptAsk
  {
    /// <summary>
    /// THE CONSENT BOUND: the longest script Dwight will put on a card.
    ///
    /// It is the same NUMBER as <c>ScriptSourceReader.MaxChars</c> and a DIFFERENT QUESTION, so it is its
    /// own constant rather than a reference to that one. That reader's cap answers "past this, to-script
    /// cannot reproduce a component and must treat it as opaque"; this one answers "past this, a card
    /// cannot carry the code in a form a person will read to the end" (#766 - a card nobody finishes
    /// reading is a card nobody has consented to). Borrowing the other constant would be the #680 defect
    /// exactly: an instrument inheriting a question it was not asked. They are free to diverge, and if one
    /// moves the other should be considered rather than dragged.
    /// </summary>
    public const int MaxSourceChars = 12000;

    /// <summary>The claim, from the model's word. Anything unrecognised is <see cref="ScriptEditClaim.Unknown"/>
    /// and refused BY NAME with the two that exist - never defaulted to the weaker one, which would let a
    /// typo buy a model the proof it did not ask for.</summary>
    public static ScriptEditClaim ClaimOf(string word)
    {
      switch ((word ?? "").Trim().ToLowerInvariant())
      {
        case "preserves": return ScriptEditClaim.Preserves;
        case "changes": return ScriptEditClaim.Changes;
        default: return ScriptEditClaim.Unknown;
      }
    }

    public static bool Validate(HandoffSeed seed, Func<int, IdState> state, Func<int, LiveScript> live,
      out ScriptEditChange change, out ToolResult error)
    {
      change = null;
      error = null;

      // ---- the target exists, and is still what it was (SetValueAsk's shape, #338/#208) --------------
      if (seed?.Target == null)
      {
        error = ToolResult.Fail(ToolErrors.InvalidArgs,
          "edit_script needs \"target\" - the id of the EXISTING script component to edit, from a previous read.");
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
      if (target == null)
      {
        error = ToolResult.Fail(ToolErrors.UnknownId,
          "Dwight could not read component " + id + " to edit it.", new JsonObject { ["id"] = id });
        return false;
      }

      // ---- it is a script, and one whose source Dwight can actually SHOW the user ---------------------
      if (!ScriptSourceReflect.IsScriptTypeName(target.Type))
      {
        error = ToolResult.Fail(ToolErrors.InvalidArgs,
          "Component " + id + " is a " + Describe(target) + ", not a script component, so there is no source to edit. " +
          "edit_script works on a C# Script or a Python 3 Script. To change a slider or a panel, use set_value.",
          new JsonObject { ["id"] = id, ["type"] = target.Type ?? "" });
        return false;
      }

      // A source Dwight cannot READ is one it must not OVERWRITE. The user would be approving a diff
      // against a base nobody could show them, which is not consent - it is a blank cheque with a
      // syntax-highlighted background.
      if (!target.SourceReadable || target.Source == null)
      {
        error = ToolResult.Fail(ToolErrors.InvalidArgs,
          "Dwight cannot read the source of " + Describe(target) + " (it may be locked or encrypted), so it will not " +
          "replace it: you would be approving a change without being able to see what it changes FROM.",
          new JsonObject { ["id"] = id });
        return false;
      }

      // ---- the claim: an enum, or a refusal naming the two that exist ---------------------------------
      var claim = ClaimOf(seed.Claim);
      if (claim == ScriptEditClaim.Unknown)
      {
        error = ToolResult.Fail(ToolErrors.InvalidArgs,
          "edit_script needs \"claim\", and it must be exactly one of: \"preserves\" (the component still produces " +
          "the same outputs - Dwight checks this by running your version beside the original and comparing) or " +
          "\"changes\" (it deliberately produces something different - Dwight checks the definition still solves and " +
          "shows the user the diff). Say which one you mean; Dwight verifies the one you declare.");
        return false;
      }

      // ---- "preserves" needs something to preserve ------------------------------------------------------
      //
      // THE FALSE PASS THIS STOPS, and it is the worst shape a verification can take: an errored script
      // still emits a null item on EVERY output, so a broken original and a broken edit compare EQUAL. A
      // "preserves" claim would be certified by matching nulls against nulls - the proof passing precisely
      // because neither side computed anything.
      //
      // It is refused as a CLAIM rather than as a capability, and the door is the one the user actually
      // wants: a broken script somebody wants fixed is the common case, and this routes it to the right
      // claim instead of into a wall (#613/#626).
      if (claim == ScriptEditClaim.Preserves && (target.Errored || target.Locked))
      {
        error = ToolResult.Fail(ToolErrors.InvalidArgs,
          target.Errored
            ? Describe(target) + " is reporting an error at the moment, so there is no working behaviour to " +
              "preserve - Dwight would be comparing one broken result against another. If you mean to FIX it, " +
              "declare \"changes\": Dwight will check the definition still solves, say whether that error is gone, " +
              "and show the user the diff."
            : Describe(target) + " is disabled, so it is not computing anything for Dwight to compare against. " +
              "Enable it if you want the outputs checked, or declare \"changes\" and the user will read the diff.",
          new JsonObject { ["id"] = id });
        return false;
      }

      // ---- the source ---------------------------------------------------------------------------------
      if (string.IsNullOrWhiteSpace(seed.Source))
      {
        error = ToolResult.Fail(ToolErrors.InvalidArgs,
          "edit_script needs \"source\" - the COMPLETE new source for the component, not a fragment and not a patch. " +
          "The user is shown the whole thing before it runs, so send the whole thing.");
        return false;
      }

      var newSource = SourceToken.Normalise(seed.Source);
      if (newSource.Length > MaxSourceChars)
      {
        error = ToolResult.Fail(ToolErrors.InvalidArgs,
          "That script is " + newSource.Length + " characters and Dwight's limit is " + MaxSourceChars +
          ". The whole source is shown on the card before it runs, and a card nobody finishes reading is a card " +
          "nobody has consented to - so write a smaller script. If the logic already exists as components on the " +
          "canvas, Simplify's to-script can collapse those instead.");
        return false;
      }

      var diff = SourceDiff.Between(target.Source, newSource);

      // Brief CP: what the edit declares about access, read against the inputs that exist and the ones it adds.
      if (!ReadAccess(seed, target, id, out var accessChanges, out var addedAccess, out error)) return false;

      // AN EDIT THAT CHANGES NOTHING IS NOT A CHEAP EDIT, IT IS AN EMPTY CARD. RR14's law: an accepted
      // change that no-ops is a FAILED change. Here it is caught before the user is asked, because the
      // alternative is a card that promises a change and applies none - and then a verdict saying it
      // worked. An access change is a change, so the same source with one is an edit.
      if (diff.Identical && accessChanges.Count == 0)
      {
        error = ToolResult.Fail(ToolErrors.InvalidArgs,
          "That source is identical to what " + Describe(target) + " already holds, so there is nothing to approve. " +
          "If you meant to change something, send the changed source; if the component is already right, say so " +
          "rather than asking the user to approve a no-op.",
          new JsonObject { ["id"] = id });
        return false;
      }

      // ---- the interface: append-only ------------------------------------------------------------------
      if (!AppendOnly(seed.AddInputs, target.Inputs, "input", out var inputError) ||
          !AppendOnly(seed.AddOutputs, target.Outputs, "output", out inputError))
      {
        error = ToolResult.Fail(ToolErrors.InvalidArgs, inputError, new JsonObject { ["id"] = id });
        return false;
      }

      change = new ScriptEditChange
      {
        TargetId = id,
        Label = Describe(target),
        Language = target.Language,
        Claim = claim,
        NewSource = newSource,
        BaseSource = SourceToken.Normalise(target.Source),
        Diff = diff,
        AddInputs = (seed.AddInputs ?? new List<string>()).ToList(),
        AddOutputs = (seed.AddOutputs ?? new List<string>()).ToList(),
        AccessChanges = accessChanges,
        AddedInputAccess = addedAccess,
      };
      return true;
    }

    /// <summary>
    /// BRIEF CP: AN ACCESS CHANGE ON AN INPUT THAT EXISTS, OR THE ACCESS OF ONE BEING ADDED. An input not there is refused with the ones that are, and a word not known with the three there are. An input
    /// declared as what it already is, is no change, and does not make a card.
    /// </summary>
    private static bool ReadAccess(HandoffSeed seed, LiveScript target, int id, out List<AccessChange> changes, out Dictionary<string, string> added, out ToolResult error)
    {
      changes = new List<AccessChange>();
      added = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
      error = null;

      var adding = new HashSet<string>((seed.AddInputs ?? new List<string>()).Where(n => n != null).Select(n => n.Trim()), StringComparer.OrdinalIgnoreCase);
      var existing = target.Inputs ?? new string[0];
      foreach (var kv in seed.InputAccess ?? new Dictionary<string, string>())
      {
        if (!PortAccess.TryRead(kv.Value, out var word))
        {
          error = ToolResult.Fail(ToolErrors.InvalidArgs,
            "\"" + kv.Value + "\" is not an access for the input \"" + kv.Key + "\". Use " + PortAccess.Words + ". " + PortAccess.Rule, new JsonObject { ["id"] = id });
          return false;
        }
        if (adding.Contains(kv.Key))
        {
          if (word != PortAccess.Item) added[kv.Key] = word;
          continue;
        }

        var name = existing.FirstOrDefault(n => string.Equals(n, kv.Key, StringComparison.OrdinalIgnoreCase));
        if (name == null)
        {
          error = ToolResult.Fail(ToolErrors.InvalidArgs,
            Describe(target) + " has no input called \"" + kv.Key + "\", so its access cannot change. Its inputs are: " +
            (existing.Count == 0 ? "none" : string.Join(", ", existing)) + ".", new JsonObject { ["id"] = id });
          return false;
        }
        var now = PortAccess.Of(target.InputAccess, name);
        if (now != word) changes.Add(new AccessChange { Name = name, From = now, To = word });
      }
      return true;
    }

    /// <summary>
    /// A port may be ADDED. It may not collide with one that exists, and it must be a usable identifier.
    ///
    /// The refusal names the existing ports, because "that name is taken" without the list is a wall, and
    /// a refusal with a door behind it is the house rule (#613/#626).
    /// </summary>
    private static bool AppendOnly(IReadOnlyList<string> adding, IReadOnlyList<string> existing, string side, out string error)
    {
      error = null;
      var have = new HashSet<string>(existing ?? new string[0], StringComparer.OrdinalIgnoreCase);
      var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

      foreach (var name in adding ?? new string[0])
      {
        var n = (name ?? "").Trim();
        if (n.Length == 0)
        {
          error = "A new " + side + " needs a name. Dwight computes the port's type and how it receives data; " +
                  "the name is the part only you can supply.";
          return false;
        }
        if (!IsIdentifier(n))
        {
          error = "\"" + n + "\" cannot be a " + side + " name: it has to be usable as a variable in the script, so " +
                  "it must start with a letter or underscore and contain only letters, digits and underscores.";
          return false;
        }
        if (have.Contains(n))
        {
          error = "This component already has an " + side + " called \"" + n + "\". Dwight can ADD " + side + "s to a " +
                  "script but never rename, reorder or remove the ones that are there - those carry the user's existing " +
                  "wires. Its " + side + "s are: " + string.Join(", ", existing) + ".";
          return false;
        }
        if (!seen.Add(n))
        {
          error = "\"" + n + "\" is named twice in the new " + side + "s. Each port needs its own name.";
          return false;
        }
      }
      return true;
    }

    private static bool IsIdentifier(string s)
    {
      if (s.Length == 0 || !(char.IsLetter(s[0]) || s[0] == '_')) return false;
      return s.All(c => char.IsLetterOrDigit(c) || c == '_');
    }

    /// <summary>The component in the user's terms: its nickname when it has one, else its type.</summary>
    private static string Describe(LiveScript target)
    {
      if (target == null) return "that component";
      if (!string.IsNullOrWhiteSpace(target.Nick)) return "\"" + target.Nick.Trim() + "\"";
      switch (target.Language)
      {
        case "csharp": return "C# Script";
        case "python": return "Python 3 Script";
        case "ironpython": return "IronPython script";
        case "vb": return "VB script";
        default: return "script component";
      }
    }
  }
}
