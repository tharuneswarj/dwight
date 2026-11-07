using System.Collections.Generic;
using System.Linq;

namespace Dwight.Agent
{
  /// <summary>
  /// WHAT AN APPROVED TURN WAS APPROVED FOR, STATED AS A FACT (#611c).
  ///
  /// THE FIELD FAILURE. Tharun approved "Build A1". The turn that followed was told, by rule 6, that changes
  /// are requested by raising a card, and it saw "Yes, go ahead." in the transcript - but nothing told it
  /// that THIS turn was the approval being carried out, or which regions it covered. The approved regions
  /// went to the ledger and the closing report, never to the model. It said "Raising the build now for A1"
  /// and the build never ran (#609 link 1).
  ///
  /// #573's lesson, applied again: an instruction the model must remember does not bind, and a fact the
  /// plugin states does not need remembering. So the approval is in the situation block, generated from the
  /// same approved set the ledger is seeded with, naming the tool that carries it out.
  ///
  /// The region addresses are the plugin's own, validated against the canvas before the card was drawn and
  /// re-resolved at the click - nothing the model wrote reaches this sentence.
  /// </summary>
  internal static class ApprovedTurnFact
  {
    public const string Heading = "APPROVED - THIS TURN CARRIES IT OUT:";

    private const string AlreadyGiven = "The approval is already given, so do not ask for it again.";

    /// <summary>The fact for a turn granted <paramref name="capability"/>, or null for a turn that holds no
    /// approval (a typed message is always read-only).</summary>
    public static string For(TurnCapability capability, IReadOnlyList<string> approvedRegions)
    {
      var kind = AgentHostTools.KindApprovedBy(capability);
      var tool = AgentHostTools.ActingToolFor(kind);
      if (kind == null || tool == null) return null;

      switch (capability)
      {
        case TurnCapability.Build:
        {
          var regions = (approvedRegions ?? new string[0]).Where(r => !string.IsNullOrWhiteSpace(r)).ToList();
          if (regions.Count == 0) return null;   // RunTurn refuses an unscoped build before it gets here
          var one = regions.Count == 1;
          return Heading + " the user has approved building " + string.Join(", ", regions) + ". Build " +
                 (one ? "it" : "them") + " now with " + tool + ". " + AlreadyGiven +
                 " Build no region outside " + (one ? "that one" : "that list") + ".";
        }
        case TurnCapability.ReadAndSolve:
          return Heading + " the user has approved a solve. Call " + tool + " now and read the fresh result. " +
                 AlreadyGiven;
        case TurnCapability.Scaffold:
          return Heading + " the user has approved laying down a scaffold. Call " + tool + " now with the plan " +
                 "you proposed. " + AlreadyGiven;
        case TurnCapability.RunScript:
          return Heading + " the user has read and approved running the script you asked for. Call " + tool + " now with that identical " +
                 "source and your one-sentence purpose. " + AlreadyGiven + " Dwight runs the approved text once and reports what changed; it " +
                 "does not check what the script does, so do not call the result checked, verified or proven.";
        default:
          return null;
      }
    }

    public const string ScriptsHeading = "SCRIPTS THE USER READ:";

    /// <summary>
    /// U65: WHAT THE APPROVED CARD SHOWED, as a fact the approved build turn is told - which scripts the user read whole and
    /// approved, per region. The build holds each script it sends to exactly these (<see cref="BuildScriptGate"/>), so the model
    /// is told to send them unchanged rather than learning it from a refusal. Plugin-authored from the granted record; null when
    /// the card showed none.
    /// </summary>
    public static string ScriptsFact(IReadOnlyList<ApprovedScript> scripts)
    {
      var list = (scripts ?? new ApprovedScript[0]).Where(s => s != null).ToList();
      if (list.Count == 0) return null;
      // Brief CA: the source and the ports, because a turn starts from chat history and this is the only place they can be read. Told what it may leave out,
      // the model need not retype the text at all, and the claim is held on the behaviour's side (BuildScriptGate.FillFromCard, #315).
      return ScriptsHeading + " the card showed " + (list.Count == 1 ? "this script" : "these scripts") + " whole and the user approved " +
             (list.Count == 1 ? "it" : "them") + ". In build_region, give a component of the script's type in its region and leave \"source\", \"inputs\" and \"outputs\" out: " +
             "Dwight builds the card's text with these ports, unchanged, so wire to these port names. Different code needs its own card.\n" + ScriptBlocks(list);
    }

    /// <summary>The sources a fact repeats, in total. A card can carry ten scripts of 12,000 characters, and past this the fact says what it left out (ADR-035).</summary>
    public const int MaxFactSourceChars = 30_000;

    /// <summary>One block per script: where it goes, its ports, and its source while the budget lasts.</summary>
    public static string ScriptBlocks(IReadOnlyList<ApprovedScript> scripts)
    {
      var left = MaxFactSourceChars;
      var blocks = new List<string>();
      foreach (var s in scripts)
      {
        var head = s.Region + " - " + s.TypeName + ". Inputs: " + PortList(s.Inputs) + ". Outputs: " + PortList(s.Outputs) + ".";
        var length = (s.Source ?? "").Length;
        if (length <= left)
        {
          left -= length;
          blocks.Add(head + "\n```" + (s.Language == BuildScriptTypes.CSharp ? "csharp" : "python") + "\n" + s.Source + "\n```");
        }
        else blocks.Add(head + " Its source, " + length + " characters, is not repeated here; it is on the card, and Dwight builds it from there.");
      }
      return string.Join("\n", blocks);
    }

    private static string PortList(IReadOnlyList<string> ports) => ports == null || ports.Count == 0 ? "the script's defaults" : string.Join(", ", ports);

    public const string OnFileHeading = "SCRIPTS ALREADY SHOWN AND NOT YET BUILT:";

    /// <summary>Brief CA: what a later turn is told about scripts a card showed and no build has landed. Null when there are none.</summary>
    public static string ScriptsOnFileFact(IReadOnlyList<ApprovedScript> scripts)
    {
      var list = (scripts ?? new ApprovedScript[0]).Where(s => s != null).ToList();
      if (list.Count == 0) return null;
      return OnFileHeading + " the user has read these on a card. To build one, raise kind=build naming its region and leave \"scripts\" out: the new card shows the same " +
             "text again, and nothing needs to be written out or pasted. To change one, send the new script in \"scripts\".\n" + ScriptBlocks(list);
    }

    public const string KeptHeading = "KEPT ON REBUILD:";

    /// <summary>Kept components named per region before the rest are counted (ADR-035) - the duplicate refusal's bound.</summary>
    public const int MaxKeptNamed = RebuildDuplicates.MaxNamed;

    /// <summary>
    /// WHAT AN APPROVED BUILD TURN'S REBUILDS KEEP, told as a fact at turn start (U33's panel half, #954) - so the model
    /// wires from a kept component when it writes the spec, instead of placing a second copy and learning from
    /// <see cref="RebuildDuplicates.Check"/>'s refusal. The #922 field case: two orphaned, intact params and a user told
    /// "your bindings were lost". Read through the same claim the build runs (GhBuildRegion.KeptForRebuild); the labels
    /// are the plugin's own (type, nickname, id). Null when no approved region keeps anything.
    /// </summary>
    public static string KeptFact(IReadOnlyDictionary<string, IReadOnlyList<KeptComponent>> keptByRegion)
    {
      var regions = (keptByRegion ?? new Dictionary<string, IReadOnlyList<KeptComponent>>())
        .Select(kv => new { Region = kv.Key, Kept = (kv.Value ?? new KeptComponent[0]).Where(k => k != null).ToList() })
        .Where(r => !string.IsNullOrWhiteSpace(r.Region) && r.Kept.Count > 0)
        .OrderBy(r => ScaffoldAddress.SortKey(r.Region).Letters, System.StringComparer.Ordinal)
        .ThenBy(r => ScaffoldAddress.SortKey(r.Region).Number)
        .ToList();
      if (regions.Count == 0) return null;

      var lines = regions.Select(r =>
      {
        var labels = r.Kept.Select(KeptLabel).ToList();
        var named = labels.Count <= MaxKeptNamed
          ? string.Join(", ", labels)
          : string.Join(", ", labels.Take(MaxKeptNamed)) + ", and " + (labels.Count - MaxKeptNamed) + " more";
        return "rebuilding " + r.Region + " keeps " + named + ".";
      });
      var total = regions.Sum(r => r.Kept.Count);
      var many = total > 1;
      var earlier = regions.Sum(r => r.Kept.Count(k => k.PlacedEarlier));
      var whatKept = earlier == 0 ? (many ? "These are components the user has edited since Dwight placed them" : "This is a component the user has edited since Dwight placed it")
        : earlier == total ? (many ? "These are components Dwight placed earlier" : "This is a component Dwight placed earlier")
        : "These are components Dwight placed earlier, some of them edited by the user since";
      return KeptHeading + " " + string.Join(" ", lines) + " " + whatKept +
             ", so the rebuild leaves " + (many ? "them" : "it") +
             " exactly as " + (many ? "they are" : "it is") + ": wire from " + (many ? "them" : "it") +
             " by id, and do not place " + (many ? "them" : "it") + " again.";
    }

    /// <summary>"Curve "Site" (41)", or "Panel (43)" when the nickname is empty or just the type - a default identifies
    /// nothing, so it is not quoted as if it did.</summary>
    private static string KeptLabel(KeptComponent k)
    {
      var type = string.IsNullOrWhiteSpace(k.Type) ? "component" : k.Type.Trim();
      var nick = (k.Nick ?? "").Trim();
      return nick.Length == 0 || string.Equals(nick, type, System.StringComparison.OrdinalIgnoreCase)
        ? type + " (" + k.Id + ")"
        : type + " \"" + nick + "\" (" + k.Id + ")";
    }
  }

  /// <summary>
  /// THE SYSTEM TEXT ONE AGENT TURN IS SENT, assembled in one GH-free place so a test reads the text the
  /// model reads rather than a copy of how it is built. The situation itself is read off the live document
  /// by the caller; everything appended to it is decided here.
  /// </summary>
  internal static class TurnSystemText
  {
    public static string Compose(string situation, string canvasFact, string buildFact, TurnCapability capability,
                                 IReadOnlyList<string> approvedRegions) =>
      Compose(situation, canvasFact, buildFact, capability, approvedRegions, null);

    /// <param name="cutsByRegion">For an approved BUILD: what each approved region's rebuild would cut, read at turn
    /// start (#625). Told inside the approval, after it, so it is part of the last thing the model reads.</param>
    public static string Compose(string situation, string canvasFact, string buildFact, TurnCapability capability,
                                 IReadOnlyList<string> approvedRegions,
                                 IReadOnlyDictionary<string, IReadOnlyList<CutWire>> cutsByRegion) =>
      Compose(situation, canvasFact, buildFact, capability, approvedRegions, cutsByRegion, null);

    /// <param name="keptByRegion">For an approved BUILD: what each approved region's rebuild KEEPS, read at turn start
    /// (U33, #954). Told beside the cut list, inside the approval, for the same reason.</param>
    public static string Compose(string situation, string canvasFact, string buildFact, TurnCapability capability,
                                 IReadOnlyList<string> approvedRegions,
                                 IReadOnlyDictionary<string, IReadOnlyList<CutWire>> cutsByRegion,
                                 IReadOnlyDictionary<string, IReadOnlyList<KeptComponent>> keptByRegion,
                                 string staleReadsFact = null, string buildStyleFact = null,
                                 IReadOnlyList<ApprovedScript> approvedScripts = null,
                                 string webSearchFact = null)
    {
      var text = Dwight.Prompts.Prompts.AgentMasterWith(AgentHostTools.CapabilitySentence()) + (situation ?? "");

      // #573: whether the canvas changed since the model's last turn.
      if (canvasFact != null) text += "\n\n" + canvasFact;

      // U22: which of the components the model has READ differ from the reading now - the canvas fact's per-object half.
      // Directly after it, so the two read as one account of what changed.
      if (staleReadsFact != null) text += "\n\n" + staleReadsFact;

      // Brief V: whether the model may search the web, said as a fact the plugin states - and ONLY when the user turned
      // search on. Null leaves the text byte-identical to what it was, and no sentence anywhere then mentions search.
      if (webSearchFact != null) text += "\n\n" + webSearchFact;

      // #624/#634: what the last approved build actually did, until a newer one supersedes it.
      if (buildFact != null) text += "\n\n" + buildFact;

      // U63: the user's build style, as a fact the plugin states every turn - the model proposes the build, so it must know the
      // style before any card is raised, not only on the approved turn.
      if (buildStyleFact != null) text += "\n\n" + buildStyleFact;

      // #611c: what this turn was approved for, last, so it is the most recent thing the model reads.
      var approved = ApprovedTurnFact.For(capability, approvedRegions);
      if (approved != null)
      {
        text += "\n\n" + approved;

        // #625/#647: an approved build is told what its rebuilds cut, with the way out, as part of the approval.
        var boundary = capability == TurnCapability.Build ? RebuildBoundary.TurnFact(cutsByRegion) : null;
        if (boundary != null) text += " " + boundary;

        // U33 (#954): and what its rebuilds keep, so a kept component is wired from rather than placed again.
        var kept = capability == TurnCapability.Build ? ApprovedTurnFact.KeptFact(keptByRegion) : null;
        if (kept != null) text += " " + kept;

        // U65: and the scripts the card showed, which the build must send unchanged.
        var scripts = ApprovedTurnFact.ScriptsFact(approvedScripts);
        if (scripts != null) text += " " + scripts;
      }

      return text;
    }
  }
}
