using System;

namespace Dwight
{
  /// <summary>
  /// The five verbs, in menu order, with the one line each of them gets on a user-facing surface.
  /// THE POINT OF THIS FILE IS THAT THERE IS ONLY ONE OF IT. The names were previously typed out in
  /// three places - the top-bar menu's items, the startup banner, and (had it been written that way)
  /// the first-run tour - which is three strings that happen to agree until somebody renames a verb
  /// and has no reason to open the other two files. #253 is the same class already made real once: the
  /// startup banner read "Phase 1, read path" for six phases after that stopped being true.
  ///
  /// GH-free, Eto-free and dependency-free on purpose (ADR-091), so the tests can link-compile it.
  /// </summary>
  internal static class DwightVerbs
  {
    public const string Explain = "Explain";
    public const string Diagnose = "Diagnose";
    public const string Reorganise = "Reorganise";
    public const string Simplify = "Simplify";
    public const string Build = "Build";

    /// <summary>The name the fifth verb had before the 30 October 2026 rename. Scaffold is now the
    /// MAP TOOL inside Build; records written by an earlier build still carry it as the verb name, so every read of a
    /// persisted or passed-in verb name goes through <see cref="Canonical"/> and nothing writes it back.</summary>
    public const string LegacyBuildName = "Scaffold";

    /// <summary>The verb name to use for any name read from a record: the earlier build's "Scaffold" is "Build".</summary>
    public static string Canonical(string action) =>
      string.Equals(action, LegacyBuildName, StringComparison.OrdinalIgnoreCase) ? Build : action;

    /// <summary>What Explain's walkthrough was called on a history row before Brief DA named it the Walk (1 November 2026).</summary>
    public const string LegacyWalkLabel = "on canvas";

    /// <summary>A row's subtitle as read from a record: an earlier build's "on canvas · 5 components" reads as a Walk, and nothing writes the old name back.</summary>
    public static string CanonicalSubtitle(string action, string subtitle)
    {
      if (subtitle == null || !string.Equals(action, Explain, StringComparison.OrdinalIgnoreCase)) return subtitle;
      foreach (var old in new[] { LegacyWalkLabel, LegacyWalkName })
        if (subtitle.StartsWith(old, StringComparison.OrdinalIgnoreCase) && (subtitle.Length == old.Length || !char.IsLetter(subtitle[old.Length])))
          return StepByStepName + subtitle.Substring(old.Length);
      return subtitle;
    }

    /// <summary>Brief EO: the Walk is called Step by step. A row written as "walk" reads as the new name; nothing writes the old one back.</summary>
    public const string LegacyWalkName = "walk";
    public const string StepByStepName = "step by step";

    /// <summary>True for the chat verb under either name.</summary>
    public static bool IsBuild(string action) =>
      string.Equals(Canonical(action), Build, StringComparison.OrdinalIgnoreCase);

    /// <summary>One verb, as a user-facing surface needs it: the name, its single line, and its tag.</summary>
    public sealed class Verb
    {
      public string Name { get; }
      public string Line { get; }
      /// <summary>The short form of <see cref="Line"/> for a surface with room for a few words: the first-run
      /// tour's verb cards, where each verb gets a cell about 78px wide. Kept to a question or a clause, and
      /// checked by a test against a length bound, because a tag that wraps to a third line at 150% scaling
      /// is a paragraph. Like <see cref="Line"/> it is a claim about what the verb does, so it is written
      /// from the code (each is sourced in the comment beside it below) and never typed into a shell.</summary>
      public string Tag { get; }
      public Verb(string name, string line, string tag) { Name = name; Line = line; Tag = tag; }
    }

    /// <summary>The five, in the order the menu lists them. The lines are deliberately about what the
    /// user gets, not how it works - the tour is somebody's first thirty seconds with Dwight.
    ///
    /// <para>The order is load-bearing beyond the menu: the tour draws an Explain answer card from the FIRST
    /// entry and a Build chat from the LAST, and keys its five icons by position rather than by name (so
    /// no verb name is typed into the shell). A test pins the order, which is what makes that safe.</para>
    ///
    /// <para>A tag is a claim, so each is worth a source. Simplify "same result": boundary-output equivalence,
    /// layer 2 of the ADR-090 verification stack - an apply whose preserved surface differs is reverted.
    /// Diagnose was "Why is it broken, or empty?" (FaultScan.Kind.Silent backs "empty"), and is now the shorter
    /// "Why is it broken?": the longer one wrapped to a THIRD line in the tour's five narrow cells, which the
    /// brief calls too long, and the ruling on it was to shorten and say so (mantis-38, 2026-09-19).</para></summary>
    public static readonly Verb[] All =
    {
      new Verb(Explain,    "Read back what a selection does, in plain language.",
                           "What does this do?"),
      new Verb(Diagnose,   "Find what is broken or suspect, and say why it is.",
                           "Why is it broken?"),
      new Verb(Reorganise, "Lay the definition out in labelled groups. What it computes is guaranteed unchanged.",
                           "Tidy it so it reads."),
      new Verb(Simplify,   "Propose better logic as a minimal diff, and you tick off the parts you want.",
                           "Same result, better built."),
      new Verb(Build,      "Plan and build new work, in a conversation.",
                           "Build it with me."),
    };

    /// <summary>Just the names, in order - for the startup banner and anywhere else a list will do.</summary>
    public static string[] Names => Array.ConvertAll(All, v => v.Name);
  }
}
