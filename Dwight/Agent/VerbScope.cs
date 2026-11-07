using System;
using System.Collections.Generic;
using System.Linq;

namespace Dwight.Agent
{
  /// <summary>What a verb should run on, once the question has actually been asked properly.</summary>
  internal enum ScopeOutcome
  {
    /// <summary>Run on the components the agent nominated and the user approved.</summary>
    Seeded = 0,
    /// <summary>Run on what the user has selected by hand.</summary>
    Selection = 1,
    /// <summary>Nothing selected on a hand-invoked run: ASK before touching the whole definition.</summary>
    AskWholeCanvas = 2,
    /// <summary>Nothing selected, and the user has explicitly confirmed the whole definition.</summary>
    WholeCanvas = 3,
    /// <summary>An approved scope that no longer resolves to anything. Do nothing and say so.</summary>
    Refuse = 4,
  }

  internal sealed class ScopeResolution
  {
    public ScopeOutcome Outcome { get; set; }
    public IReadOnlyList<Guid> Guids { get; set; } = new Guid[0];
    public string Message { get; set; } = "";
  }

  /// <summary>
  /// WHAT SCOPE A VERB RUNS ON - and the reason it is a separate, tested function rather than two lines at
  /// the top of each verb.
  ///
  /// THE DEFECT THAT PRODUCED IT (#338). A card named N components. The user approved it, answered one more
  /// question in the verb's own flow ("also tidy the I/O?"), and the verb reorganised all five hundred
  /// components on the canvas. Every guard held - the run was byte-identical-checked and one Ctrl-Z removed
  /// it - but the user approved "these N" and got "all of them", which is a mutation outside the scope the
  /// card stated, and no amount of correct execution makes that acceptable.
  ///
  /// The mechanism was mundane and is the reason this type exists: the verb's flow is a CONVERSATION - it
  /// asks, returns, and is re-entered by a shell command that carries no state - and the re-entry
  /// reconstructed that state from defaults. The approved scope was not dropped by a bug in the scope logic;
  /// it was dropped because there was no scope logic, only a null-coalesce repeated at each entry point.
  ///
  /// TWO RULES, and the first is the one that makes the second unnecessary to remember:
  ///
  ///   1. A SEEDED ROUTE CAN NEVER REACH <see cref="ScopeOutcome.WholeCanvas"/>. Not "should not" - the
  ///      value is unreachable from <c>seeded: true</c> in the code below. A card that named N components
  ///      cannot produce a whole-canvas run even if every other piece of state is lost.
  ///   2. AN APPROVED SCOPE THAT RESOLVES TO NOTHING REFUSES. It never falls through to the selection and
  ///      never falls through to the canvas. Scope is IDENTITY, and #208's rule for identity is that
  ///      resolution either succeeds exactly or refuses - falling back is how you end up somewhere
  ///      plausible instead of somewhere right.
  ///
  /// A consent card is a contract about scope. The verb runs on the scope the card stated, or not at all.
  /// </summary>
  internal static class VerbScope
  {
    /// <summary>
    /// <paramref name="verb"/> is the verb the refusal speaks for ("reorganise", "simplify"); it defaults to reorganise,
    /// the verb every caller before U28 was. <paramref name="named"/> is how many components the approved CARD named -
    /// before any of them was found missing - or -1 when the caller does not know. It only chooses the refusal's words
    /// (U28, #918): "named none" and "named retired" are different facts, and one sentence for both was false for one.
    /// </summary>
    public static ScopeResolution Resolve(bool seeded, IReadOnlyList<Guid> seed,
                                          IReadOnlyList<Guid> selection, bool confirmedWhole,
                                          string verb = null, int named = -1)
    {
      var approved = Clean(seed);

      // ---- THE SEEDED BRANCH. WholeCanvas is not reachable from here, by construction. ----
      if (seeded)
      {
        if (approved.Count > 0)
          return new ScopeResolution { Outcome = ScopeOutcome.Seeded, Guids = approved };

        return new ScopeResolution
        {
          Outcome = ScopeOutcome.Refuse,
          // #347: the copy is the REFUSAL'S OWN, not the manual route's. The first version advised
          // "deselect everything to reorganise the whole definition" - borrowed from the hand-invoked
          // path, and on a seeded route it advises an action that is impossible by construction, because
          // VerbScope makes whole-canvas unreachable from here. A refusal that suggests a way out must
          // suggest one that exists (#260's class, at a refusal).
          // U28 (#918): and it must be TRUE in every state that reaches it. "The components I named are no longer on
          // the canvas" was written for the stale-ids case and was shown over a card that named none.
          Message = SeededRefusal(verb, named),
        };
      }

      // ---- The hand-invoked branch, unchanged in behaviour from every shipped version. ----
      var live = Clean(selection);
      if (live.Count > 0) return new ScopeResolution { Outcome = ScopeOutcome.Selection, Guids = live };

      return new ScopeResolution
      {
        Outcome = confirmedWhole ? ScopeOutcome.WholeCanvas : ScopeOutcome.AskWholeCanvas,
      };
    }

    private static List<Guid> Clean(IReadOnlyList<Guid> ids) =>
      (ids ?? new Guid[0]).Where(g => g != Guid.Empty).Distinct().ToList();

    // ---- U28 (#918): the refusal's words, chosen by what the card NAMED ----------------------------------------------

    /// <summary>
    /// The seeded refusal, in Dwight's voice, for the three states that reach it:
    ///   named 0   - the card named NO components (#918's field case: a reorganise asked with an empty list);
    ///   named &gt; 0 - it named some and none is on the canvas any more (#347's stale-ids case);
    ///   unknown   - the caller cannot say which, so the sentence claims only what holds in both: there was nothing on the
    ///               canvas for the verb to run on, and nothing was changed.
    /// Every one says nothing was changed - true, because a refusal here runs on nothing - and offers a way out that exists.
    /// </summary>
    public static string SeededRefusal(string verb, int named)
    {
      var v = Verb(verb);
      if (named == 0)
        return "The card named no components, so there was nothing to " + v + " and nothing was changed. " +
               "Ask me again and I will name the components I mean.";
      if (named > 0)
        return (named == 1 ? "The component the card named is" : "The " + named + " components the card named are") +
               " no longer on the canvas, so there was nothing to " + v + " and nothing was changed. " +
               "Ask me again and I will look at what is there now.";
      return "There was nothing on the canvas for this " + Capital(v) + " to run on, so nothing was changed. " +
             "Ask me again and I will look at what is there now.";
    }

    /// <summary>The kinds whose card is a scope of EXISTING components - the two this rule governs.</summary>
    public static bool IsComponentScoped(string kind) =>
      string.Equals(kind, "reorganise", StringComparison.OrdinalIgnoreCase) ||
      string.Equals(kind, "simplify", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// U28: NO CARD OVER NOTHING, FOR REORGANISE AND SIMPLIFY - decided AT THE ASK, before a card exists (#892's rule for
    /// the build kind, given to the other two). Returns null when the ask may mint, with <paramref name="scope"/> holding
    /// the ids the card is for and where they came from; otherwise the refusal, addressed to the MODEL.
    ///
    /// THREE STATES:
    ///   - It NAMED components: the card is for the ones still on the canvas. If none is, it is REFUSED - never widened to
    ///     the selection, because scope is identity (#208, #338) and the selection is not what the model asked about.
    ///   - It named NONE and the user HAS a selection: the card is for that selection, resolved NOW, and says how many - so
    ///     the scope approved is the scope seen at the ask, not whatever is selected at the click. This is what the
    ///     published schema already promised ("so the flow opens on them instead of on whatever the user happens to have
    ///     selected"): leaving the field out means the selection. The plugin keeps that promise here.
    ///   - It named none and NOTHING is selected: refused, by name, with the field to fill.
    /// </summary>
    public static ToolResult ResolveAsk(string kind, IReadOnlyList<int> named, Func<int, IdState> state,
                                        IReadOnlyList<int> selected, out AskScope scope)
    {
      scope = null;
      var verb = Verb(kind);
      var asked = (named ?? new int[0]).Distinct().ToList();

      if (asked.Count > 0)
      {
        var alive = asked.Where(id => (state?.Invoke(id) ?? IdState.Unknown) == IdState.Alive).ToList();
        if (alive.Count > 0)
        {
          scope = new AskScope { Ids = alive, FromSelection = false };
          return null;
        }

        var retired = asked.Where(id => state?.Invoke(id) == IdState.Retired).ToList();
        var never = asked.Except(retired).ToList();
        var parts = new List<string>();
        if (retired.Count > 0) parts.Add(Ids(retired) + (retired.Count == 1 ? " was" : " were") + " deleted from the canvas");
        if (never.Count > 0) parts.Add(Ids(never) + (never.Count == 1 ? " was" : " were") + " never issued in this session");

        return ToolResult.Fail(retired.Count > 0 ? ToolErrors.RetiredId : ToolErrors.UnknownId,
          "None of the components you named for this " + verb + " is on the canvas: " + string.Join("; ", parts) +
          ". So there is nothing to ask the user to approve. Read the canvas again (get_overview) and name components " +
          "that exist - the user's selection is not used in place of the ones you named.");
      }

      var live = (selected ?? new int[0]).Distinct().ToList();
      if (live.Count > 0)
      {
        scope = new AskScope { Ids = live, FromSelection = true };
        return null;
      }

      return ToolResult.Fail(ToolErrors.InvalidArgs,
        "This " + verb + " named no components in \"components\" and nothing is selected on the canvas, so there is " +
        "nothing for it to run on and nothing to ask the user to approve. Name the components it is for, by id from " +
        "what you have read.");
    }

    /// <summary>
    /// The card's scope line, from the same resolution the card is minted with - so the count the user approves is the
    /// count that runs. Dwight's own words; nothing from the model.
    /// </summary>
    public static string CardScopeLine(AskScope scope)
    {
      if (scope == null || scope.Ids == null || scope.Ids.Count == 0) return null;
      var n = scope.Ids.Count;
      var what = n == 1 ? "1 component" : n + " components";
      return scope.FromSelection
        ? "On the " + what + (n == 1 ? " that was" : " that were") + " selected when Dwight asked."
        : "On the " + what + " Dwight named.";
    }

    private static string Verb(string verb)
    {
      var v = (verb ?? "").Trim().ToLowerInvariant();
      return v == "simplify" ? "simplify" : "reorganise";
    }

    private static string Capital(string v) => v.Length == 0 ? v : char.ToUpperInvariant(v[0]) + v.Substring(1);

    private static string Ids(List<int> ids) =>
      (ids.Count == 1 ? "component " : "components ") + string.Join(", ", ids);
  }

  /// <summary>What a reorganise/simplify ask resolved to at the mint (U28): the ids the card is for, and whether they are
  /// the user's selection (the ask named none) or the components the model named.</summary>
  internal sealed class AskScope
  {
    public IReadOnlyList<int> Ids { get; set; } = new int[0];
    public bool FromSelection { get; set; }
  }
}
