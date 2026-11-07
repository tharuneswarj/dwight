using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;

namespace Dwight.Agent
{
  /// <summary>Where an approved consent card goes. A pure function of the ENUMERATED KIND - see
  /// <see cref="ConsentCard.Route"/> for why the model's words are not a parameter.</summary>
  internal enum ConsentRoute
  {
    Unknown = 0,
    Solve,
    Reorganise,
    Simplify,
    Scaffold,
    Build,
    /// <summary>Phase-9 U4: a consented edit to an EXISTING slider or panel. The plugin applies the
    /// validated change from the card itself (Q1) and then grants ONE re-read turn (Q2).</summary>
    SetValue,

    /// <summary>
    /// Phase-9 U20: a consented rewrite of an EXISTING script component's source. Like set_value the
    /// change rides the card and the plugin applies it from the card's own record - and here that is not a
    /// convenience but the only way the text shown can be the text applied (#153). Unlike set_value, what
    /// this card carries is CODE THAT WILL RUN on the user's machine, so the surface shows all of it.
    /// </summary>
    EditScript,

    /// <summary>
    /// Phase-9 U6 + U21 (#760): a consented, BATCHED edit of existing wiring - wires added, wires removed, components
    /// deleted, up to forty rows on one card, each row tickable. Like set_value the plugin applies the validated change
    /// from the card's own record on the click; unlike it, WHAT is applied is decided by the ticks that arrive with the
    /// click, so the apply reads the record and the ticks and nothing the model wrote.
    /// </summary>
    Edit,

    /// <summary>
    /// Full control (U90): a consented run of ONE Python 3 script the user has read, with the whole Rhino and Grasshopper API. The
    /// approval grants exactly the shown source to the turn, once; the card asks, except under "Do not ask" (ApprovalMode.Decide).
    /// </summary>
    RunScript,
  }

  /// <summary>
  /// THE CONSENT CARD (Phase-8 WP-3 Stage B; the #235 sharpening).
  ///
  /// A consent surface now carries model-authored text, which inherits ADR-088's injection concern one
  /// layer more dangerous - because this surface's entire job is INFORMED CONSENT. Escaping stops markup.
  /// It does NOT stop a reason that misdescribes what is being approved: "just a routine read-only check"
  /// written beside a kind that reorganises the definition.
  ///
  /// SO THE CARD HAS TWO VOICES, AND ONLY ONE OF THEM HAS AUTHORITY:
  ///   - THE ACT and THE CONSEQUENCE are PLUGIN-AUTHORED, derived from the enumerated kind alone
  ///     (<see cref="Title"/>, <see cref="Authority"/>). This is what the user is agreeing to.
  ///   - THE REASON is the MODEL'S, escaped, visually subordinate, and attributed as such. It explains
  ///     why the model is asking; it never states what will happen.
  ///
  /// The user approves what DWIGHT says will happen, never what the MODEL says will happen. That is
  /// ADR-088's "canvas text is data, never instructions" extended to its natural successor: model text
  /// inside a consent surface is data ABOUT the request, never the request's AUTHORITY.
  ///
  /// GH-free and Eto-free (ADR-091), so both properties are asserted rather than reviewed: markup renders
  /// as text, and a reason claiming a different action cannot alter the rendered act or the route.
  /// </summary>
  internal static class ConsentCard
  {
    /// <summary>
    /// What an approval DOES - a pure function of the enumerated kind.
    ///
    /// THE MODEL'S TEXT IS NOT A PARAMETER, and that is the guarantee rather than an omission: there is
    /// no argument through which a persuasive reason could change where an approval lands, so the granted
    /// capability is decided by what the user was shown, not by what the model wrote beside it.
    /// </summary>
    public static ConsentRoute Route(string kind)
    {
      switch ((kind ?? "").Trim().ToLowerInvariant())
      {
        case "solve": return ConsentRoute.Solve;
        case "reorganise": return ConsentRoute.Reorganise;
        case "simplify": return ConsentRoute.Simplify;
        case "scaffold": return ConsentRoute.Scaffold;
        case "build": return ConsentRoute.Build;
        case "set_value": return ConsentRoute.SetValue;
        case "edit_script": return ConsentRoute.EditScript;
        case "edit": return ConsentRoute.Edit;
        case "run_script": return ConsentRoute.RunScript;
        default: return ConsentRoute.Unknown;
      }
    }

    /// <summary>
    /// The CONSEQUENCE for a SET_VALUE (Phase-9 U4), which names the component, the EXACT change, and the
    /// one continuation the approval grants - all plugin-formatted from the validated state and the live
    /// read (<see cref="SetValueChange"/>), never from the model's text. "And nothing else" is the scope
    /// contract (#338); "once" is the bound on the spend the approval authorises (the Q2 ruling: consent
    /// covers what the card SAYS, so the card says it, and an approval that authorised unbounded spend
    /// would not be an approval).
    /// </summary>
    public static string SetValueAuthority(SetValueChange change)
    {
      if (change == null) return Authority("set_value", null);
      return "Dwight will set " + change.Label + " from " + change.Before + " to " + change.After +
             " - and nothing else. The change is checked and reverts itself if anything but that component " +
             "changed; one Ctrl-Z undoes it. Then Dwight re-reads the result once and continues.";
    }

    /// <summary>
    /// THE CONSEQUENCE FOR AN EDIT_SCRIPT (Phase-9 U20), and the order of its clauses is the design.
    ///
    /// THE DANGEROUS PART COMES FIRST, exactly as a rebuild's removal sentence does: what a reader must
    /// not meet at the end of a paragraph about how safe everything is, is that their existing code is
    /// being replaced and new code is about to run. So: the replacement, then that it will run, then what
    /// the change DOES to the component's behaviour, then what Dwight actually CHECKED, then the ports it
    /// adds, and only then the reassurance.
    ///
    /// EVERY CLAUSE IS PLUGIN-AUTHORED (#235). The claim reaches this sentence as an ENUM and the check
    /// reaches it as an OUTCOME, so there is no argument through which the model's words could describe
    /// what is about to happen - they appear only in the subordinate, attributed "reason for asking".
    ///
    /// AND THE CHECK IS DESCRIBED BY WHAT WAS ACTUALLY COMPARED, never by what was claimed
    /// (<see cref="ScriptEditVerdict.CardLine"/>). A trial that compared nothing says so here, in those
    /// words, because an outcome that softens on its way onto the card is one the user cannot weigh.
    /// </summary>
    public static string EditScriptAuthority(ScriptEditChange change, ScriptEditOutcome outcome, ScriptTrialFacts facts)
    {
      if (change == null) return Authority("edit_script", null);

      // Brief CP: an edit that only changes how the inputs are fed does not replace anything.
      var line = (change.KeepsSource
                   ? "Dwight will keep the source of " + change.Label + " as it is, and change how its inputs are fed - and change "
                   : "Dwight will replace the source of " + change.Label + " with the code below - and change ") +
                 "nothing else on your canvas. " + CodeRunsClause + " ";

      // The CLAIM, from the one shared source so the card and the report cannot describe the same edit
      // differently - and the POINTING WORDS added here, by the surface that actually has a diff on it.
      // A shared sentence may not say "above": it is true on a card and false in a report (#335's
      // sibling - deixis is a property of the destination).
      line += ScriptEditVerdict.ClaimClause(change.Claim) + " ";
      if (change.Claim == ScriptEditClaim.Changes && change.Diff != null && !change.Diff.Identical)
        line += "The difference is in the diff below. ";

      var checked_ = ScriptEditVerdict.CardLine(outcome, facts);
      if (!string.IsNullOrWhiteSpace(checked_)) line += checked_ + " ";

      var ports = Ports(change);
      if (ports != null) line += ports + " ";
      var access = ScriptEditVerdict.AccessSentence(change);
      if (access != null) line += access + " ";

      // THE REASSURANCE DELIBERATELY AVOIDS THE WORD "VERIFIED", and not for style. This sentence sits
      // beside a check clause that may be saying "this did not confirm anything", and the blunt scan that
      // keeps that honest cannot tell a general statement about Dwight's behaviour from a claim about THIS
      // edit. Rather than exempt the sentence and blunt the guard, the sentence says the same thing in
      // plainer words - which reads better to a user anyway.
      return line + "One Ctrl-Z undoes it, and if the change does not check out Dwight puts your original " +
             (change.AccessChanges != null && change.AccessChanges.Count > 0 ? "source and access back" : "source back") + " and tells you so.";
    }

    /// <summary>
    /// THE ONE SENTENCE THAT SAYS CODE WILL EXECUTE (U19). to-script, U20's edit_script card and U19's placement card
    /// all state the same fact - a script is about to run on the user's machine, in their definition - and a fact stated
    /// in three wordings is three chances for one of them to soften. One constant, so the claim cannot drift between the
    /// surfaces that make it, and a test holds each of them to it.
    /// </summary>
    public const string CodeRunsClause = "THE CODE WILL RUN IN YOUR DEFINITION.";

    /// <summary>The ports this edit ADDS, named - never a count on its own. A user who cannot see which
    /// port is appearing has been given a number rather than a scope (#338's rule about naming).</summary>
    private static string Ports(ScriptEditChange change)
    {
      var added = new List<string>();
      foreach (var n in change.AddInputs ?? new string[0]) added.Add("an input called \"" + n + "\"");
      foreach (var n in change.AddOutputs ?? new string[0]) added.Add("an output called \"" + n + "\"");
      if (added.Count == 0) return null;

      return "It also adds " + string.Join(" and ", added) +
             " - nothing is wired to " + (added.Count == 1 ? "it" : "them") + " yet, so you will need to connect " +
             (added.Count == 1 ? "it" : "them") + " yourself.";
    }

    /// <summary>
    /// The edit_script card: the diff to ORIENT, then the COMPLETE source, which is what is being
    /// consented to.
    ///
    /// THE TWO BLOCKS ARE BOUNDED DIFFERENTLY, ON PURPOSE. The diff is capped
    /// (<see cref="Dwight.Data.SourceDiff.MaxLines"/>) and says what it left out, because a diff long
    /// enough to need scrolling has stopped orienting anyone and the source below it is the authority. THE
    /// SOURCE IS NEVER TRUNCATED - Phase 7 settled that for to-script (as-built §4.5) and the reason is
    /// unchanged: this is code that executes on the user's machine, so the consent surface shows all of
    /// it. The bound that keeps that readable lives at GENERATION time, where a source over
    /// <see cref="EditScriptAsk.MaxSourceChars"/> is refused before a card exists - never here, where
    /// cutting it would mean asking someone to approve code they were not shown.
    /// </summary>
    public static string EditScriptHtml(string why, string token, ScriptEditChange change,
      ScriptEditOutcome outcome, ScriptTrialFacts facts, string autoMode = null)
    {
      var body = new System.Text.StringBuilder();
      body.Append(DiffBlock(change?.Diff));
      body.Append("<p class=\"consent-note\">The complete new source, which is what will run:</p>");
      // Brief AL: FOLDED by default behind a summary the plugin wrote from the record. The whole source is in the DOM either
      // way, so the complete source is still shown before it runs (#48) and the text shown is still the text applied (#153).
      var summary = ScriptSummary.Line(ScriptSummary.LanguageLabel(change?.Language), change?.NewSource,
                                       change?.AddInputs, change?.AddOutputs, portsAreAdditions: true, inputAccess: change?.AddedInputAccess);
      body.Append(ScriptFold(summary, change?.NewSource, ExpandedByDefaultFor("edit_script")));

      return Render("edit_script", EditScriptAuthority(change, outcome, facts), why, token, body.ToString(), autoMode,
                    change == null ? null : change.KeepsSource ? "Change how the inputs of " + change.Label + " are fed." : "Replace the source of " + change.Label + ".");
    }

    /// <summary>
    /// FULL CONTROL: the run_script card. The authority line is the ruling's own words; then the COMPLETE source in the folded script
    /// section - OPEN the first time a thread shows one of these cards and folded after (<paramref name="expanded"/>, decided by the
    /// thread's own record) - then the model's reason. It has its button, except under "Do not ask" (<see cref="ApprovalMode.Decide"/>), where it is a marked receipt that keeps the whole source.
    /// The summary line is the plugin's, and "touches the Rhino document" is a text match, not a promise.
    /// </summary>
    public static string RunScriptHtml(string why, string token, string source, bool expanded, string autoMode = null)
    {
      // U104: the summary says the plugin adds its preamble, and a fixed note under the fold says what it is. The fold stays the approved
      // source byte for byte, which is what the grant compares.
      var summary = ScriptSummary.LineWithoutPorts("Python 3", source) + " " + FullControl.PreambleSummaryClause;
      // Brief BG: the source is the thing read before the click (ADR-100), so it stays on the card's face, outside the fold; the authority
      // and the preamble note go in the fold.
      var visible = ScriptFold(summary, source, expanded);
      var fold = "<p class=\"consent-note\">" + WebUtility.HtmlEncode(FullControl.PreambleNote) + "</p>";
      return Render(FullControl.Kind, FullControl.CardAuthority, why, token, fold, autoMode,
                    "Run a Python 3 script of " + ScriptSummary.LineCountText(source) + ".", visible);
    }

    /// <summary>
    /// Brief AL, completed by Full control: whether a kind's script section opens expanded. FALSE FOR EVERY KIND but run_script, whose card
    /// opens expanded the FIRST time a conversation shows one (<paramref name="firstOfKindInThread"/>, which the caller reads from the
    /// thread's own record) because nothing checks what it runs, and folded after - the whole source is in the card either way.
    /// </summary>
    public static bool ExpandedByDefaultFor(string kind, bool firstOfKindInThread = false) =>
      string.Equals(kind, FullControl.Kind, StringComparison.Ordinal) && firstOfKindInThread;

    /// <summary>
    /// THE FOLD: a native details element, so it works without script. The summary is the plugin's line; the pre holds the
    /// WHOLE source, escaped here (#335) and never cut, whether or not the element is open.
    /// </summary>
    public static string ScriptFold(string summaryLine, string source, bool expanded)
    {
      return "<details class=\"consent-script\"" + (expanded ? " open" : "") + ">" +
             "<summary class=\"consent-script-summary\" title=\"" + WebUtility.HtmlEncode(ScriptSummary.TextTestNote) + "\">" +
             WebUtility.HtmlEncode(summaryLine ?? "") + "</summary>" +
             "<pre class=\"consent-source\">" + WebUtility.HtmlEncode(source ?? "") + "</pre>" +
             "</details>";
    }

    /// <summary>
    /// The diff, rendered. Escaping happens HERE because escaping is a property of the DESTINATION (#335):
    /// the same text is markup-dangerous in HTML and inert in a prompt, so <c>SourceDiff</c> produces
    /// neither and each consumer encodes for itself.
    /// </summary>
    private static string DiffBlock(Dwight.Data.SourceDiff diff)
    {
      if (diff == null || diff.Identical) return "";

      var sb = new System.Text.StringBuilder();
      sb.Append("<p class=\"consent-note\">What changes (").Append(WebUtility.HtmlEncode(diff.Summary())).Append("):</p>");
      sb.Append("<pre class=\"consent-diff\">");
      foreach (var line in diff.Lines)
      {
        var cls = line.Kind == Dwight.Data.DiffKind.Added ? "diff-add"
                : line.Kind == Dwight.Data.DiffKind.Removed ? "diff-del"
                : line.Kind == Dwight.Data.DiffKind.Gap ? "diff-gap" : "diff-ctx";
        var prefix = line.Kind == Dwight.Data.DiffKind.Added ? "+ "
                   : line.Kind == Dwight.Data.DiffKind.Removed ? "- "
                   : line.Kind == Dwight.Data.DiffKind.Gap ? "  " : "  ";
        sb.Append("<span class=\"").Append(cls).Append("\">")
          .Append(WebUtility.HtmlEncode(prefix + line.Text))
          .Append("</span>\n");
      }
      sb.Append("</pre>");
      return sb.ToString();
    }

    /// <summary>
    /// The CONSEQUENCE for a BUILD, which is the only kind whose authority line has to name a SCOPE
    /// (Stage C).
    ///
    /// #338 is why: a consent card is a contract about scope, so the sentence the user agrees to has to
    /// say WHICH regions - and the addresses here are the plugin's own, VALIDATED against the canvas
    /// before the card is built, never the model's text repeated back. #235 is unchanged: the authority
    /// line still contains not one word the model wrote.
    /// </summary>
    public static string BuildAuthority(string kind, IReadOnlyList<string> regions) =>
      BuildAuthority(kind, regions, null);

    /// <summary>
    /// The build card's authority line, and - when a rebuild would REMOVE anything - the sentence naming
    /// exactly what goes and what stays.
    ///
    /// THE REMOVAL SENTENCE IS NOT DECORATION. The rebuild claim outlives the turn that made it, and the
    /// only thing that makes that safe is that the user reads what will be removed at the moment they
    /// consent (#352's worry answered rather than managed). So this line is the safety property, not a
    /// courtesy - and it names components rather than counting them, because a user who cannot recognise
    /// what is about to be removed has been given a figure, not a scope.
    /// </summary>
    public static string BuildAuthority(string kind, IReadOnlyList<string> regions, string removalSentence) =>
      BuildAuthority(kind, regions, removalSentence, null);

    /// <param name="buildStyle">U63: the user's build style (<see cref="BuildStyle"/>), named after the regions so the card says
    /// how the build will go. Plugin-authored from the setting; null leaves the line exactly as it was.</param>
    public static string BuildAuthority(string kind, IReadOnlyList<string> regions, string removalSentence, string buildStyle)
    {
      if (Route(kind) != ConsentRoute.Build || regions == null || regions.Count == 0)
        return Authority(kind, null);

      var named = regions.Count == 1
        ? "region " + regions[0]
        : "regions " + string.Join(", ", regions);

      var line = "Dwight will place and wire components in " + named +
                 (buildStyle == null ? "" : BuildStyle.CardClause(buildStyle)) + " - and nowhere else. ";

      // Before the reassurance, never after it: what is about to be DELETED is the part a reader must
      // not reach at the end of a paragraph about how safe everything is.
      if (!string.IsNullOrWhiteSpace(removalSentence)) line += removalSentence + " ";

      return line + "Every region is checked after it is built and reverts itself if it cannot be " +
             "verified, each one is a single Ctrl-Z, and Stop is live throughout.";
    }

    /// <summary>
    /// THE SPOT CARD'S CONSEQUENCE - one of TWO sentences, and the user picks which (U18, #770).
    ///
    /// Tharun's question was "why can't we leave the choice to the user?", and the answer is that here the
    /// guarantee does not depend on the choice: THE RECORD IS THE ENFORCEMENT, THE GROUP IS A VIEW OF IT.
    /// The spot is minted, the ledger entry written, the attempt cap applied and the scope enforced
    /// identically either way, so there is nothing to protect by deciding for them.
    ///
    /// BOTH SENTENCES ARE PLUGIN-AUTHORED AND BOTH STATE THE SCOPE (#235, and #338's scope rule): a spot has
    /// no region name to stand in for its contents, so the components ARE the scope. Up to five, the sentence
    /// names every one and that is the total. Past five it names the first five and "and N more" and then says
    /// the total as a number (Brief CV, <see cref="FoldThem"/>), and the card's body lists every one
    /// (<see cref="SpotComponentRows"/>). A placement past the bound is refused before a card is minted.
    ///
    /// THE LOOSE SENTENCE STATES WHAT IT COSTS, in plain words. Without a box there is no boundary, so a
    /// later rebuild has nothing to "leave alone" - it can only ever remove what Dwight itself placed.
    /// That is a true and slightly surprising consequence, and the worst moment to discover it is during
    /// a rebuild, so the user reads it while choosing (#763's habit: state the consequence at the point
    /// of consent, never at the point of damage).
    ///
    /// The model supplies only `why`, as on every other card. It does not choose which option is offered,
    /// which is default, or which the user takes - see the #346 note on <c>TurnPermission.SpotGrouped</c>.
    /// </summary>
    public static string SpotAuthority(SpotPlacement placement, bool grouped)
    {
      if (placement == null || placement.Components == null || placement.Components.Count == 0)
        return Authority("build", null);

      // Brief CV: a sentence that names every component has said the total. One that folds has not, so it says it as a number (#338).
      var count = placement.Components.Count;
      var what = FoldThem(placement.Components) + (count > RebuildPreview.MaxNamed ? ": " + count + " components" : "");

      // A card whose grammar stumbles reads as machine-generated exactly where it needs to read as
      // considered - and more than one component is the ORDINARY case for a spot, not the exception.
      var connect = placement.Components.Count == 1 ? "it connects to" : "they connect to";
      var where = string.IsNullOrWhiteSpace(placement.Beside)
        ? ClearPlace
        : "near the " + placement.Beside + " " + connect + ", clear of your work";
      if (!string.IsNullOrWhiteSpace(placement.NotIn)) where += ", not in " + placement.NotIn;

      // Brief CM: a place the request named has one consequence, so it has one paragraph and one button.
      if (!string.IsNullOrWhiteSpace(placement.Into))
        return "Dwight will place " + what + " inside " + placement.Into + ", which grows to hold " + (placement.Components.Count == 1 ? "it" : "them") +
               ", and what it already holds stays - and nothing else. " + SpotScriptClause(placement) +
               "The build is checked and reverts itself if it cannot be verified, and one Ctrl-Z removes all of it.";

      var line = grouped
        ? "Dwight will place " + what + " in a new group called " + placement.Address + ", " + where +
          " - and nothing else. "
        : "Dwight will place " + what + " loose on the canvas, " + where + ", with no group - and nothing " +
          "else. Dwight will only ever remove what it placed here itself; without a box there is nothing " +
          "for it to leave alone. ";

      // THE THING YOU PRESS IS THE THING YOU READ. Two paragraphs both opening "Dwight will place..." over
      // two buttons leaves the reader to work out the mapping, and a reader who gets it wrong approves the
      // other option. So each consequence is LABELLED with its own option, in the same words as its button.
      return SpotOptionLabel(grouped) + ": " + line + SpotScriptClause(placement) +
             "The build is checked and reverts itself if it cannot be verified, and one Ctrl-Z " +
             "removes all of it.";
    }

    /// <summary>
    /// WHAT A PLACEMENT THAT CARRIES CODE ADDS TO ITS CONSEQUENCE (U19, #898): that the code will run - the one sentence
    /// every code-running card shares (<see cref="CodeRunsClause"/>) - what happens to the build if the script fails, and,
    /// per script, which of its inputs nothing is wired to. The complete source is on the card below the consequence;
    /// this is the sentence the user agrees to, and it contains not one word the model wrote (#235) - the names are the
    /// plugin's, read off the validated spec.
    ///
    /// THE UNWIRED-INPUT SENTENCES ARE COMPUTED FROM THE SPEC, at the ask. They are the same fact the buildline states
    /// afterwards ("not exercised"), told while the user can still decide: a script that will run with nothing fed to it
    /// proves nothing by being empty, and a user who approves it should have read that before, not after. Empty string
    /// when the placement carries no code, so every card without a script reads exactly as it did.
    /// </summary>
    public static string SpotScriptClause(SpotPlacement placement)
    {
      var scripts = placement?.Scripts;
      if (scripts == null || scripts.Count == 0) return "";

      var sb = new System.Text.StringBuilder();
      sb.Append(CodeRunsClause).Append(' ');
      sb.Append(scripts.Count == 1 ? "Its complete source is shown below. " : "Their complete sources are shown below. ");
      foreach (var s in scripts)
      {
        if (s.UnwiredInputs == null || s.UnwiredInputs.Count == 0) continue;
        var ports = string.Join(", ", s.UnwiredInputs);
        sb.Append("Nothing is wired to ").Append(s.Name).Append("'s input").Append(s.UnwiredInputs.Count == 1 ? " " : "s ")
          .Append(ports).Append(s.AllInputsUnwired
            ? ", so it will run with nothing fed to it. "
            : ", so it will run without " + (s.UnwiredInputs.Count == 1 ? "it" : "them") + ". ");
      }
      sb.Append("A script that does not compile or run, or that produces nothing although it was fed, takes the whole build back with it. ");
      return sb.ToString();
    }

    /// <summary>
    /// What one option is CALLED - and the single source the button text is derived from, so the label on
    /// the consequence and the words on the button cannot drift apart in a later edit. Making them match
    /// structurally is the point; making them match today would only be a coincidence waiting to lapse.
    /// </summary>
    public static string SpotOptionLabel(bool grouped) => grouped ? "In a group" : "Loose on the canvas";

    /// <summary>Brief CM: where a placement with no anchor goes. True wherever the search lands it, which is why it names no direction.</summary>
    public const string ClearPlace = "in clear space on the canvas";

    /// <summary>The button, built FROM the label: "In a group" -> "Yes, in a group".</summary>
    public static string SpotOptionButton(bool grouped) => grouped ? SpotOptionLabel(true) : "Loose";

    /// <summary>"a Panel", "a Panel and a Number Slider", "a Panel, a Number Slider and a Curve" - every
    /// one of them, never a count.</summary>
    private static string NameThem(IReadOnlyList<string> items)
    {
      if (items.Count == 1) return items[0];
      return string.Join(", ", items.Take(items.Count - 1)) + " and " + items[items.Count - 1];
    }

    /// <summary>
    /// Brief CV: A SPOT'S COMPONENTS AS ITS SENTENCES SAY THEM. Every one up to <see cref="RebuildPreview.MaxNamed"/>, and past it the first
    /// that many and "and N more": "a Panel, a Curve, a Circle, a Line, a Point and 4 more". Only the sentences fold:
    /// <see cref="SpotComponentRows"/> lists every component in the card's body, and the authority line states the total once it has folded.
    /// </summary>
    private static string FoldThem(IReadOnlyList<string> items) =>
      items.Count <= RebuildPreview.MaxNamed
        ? NameThem(items)
        : string.Join(", ", items.Take(RebuildPreview.MaxNamed)) + " and " + (items.Count - RebuildPreview.MaxNamed) + " more";

    /// <summary>The ACT, in Dwight's own words. Plugin-authored from the kind.</summary>
    public static string Title(string kind)
    {
      switch (Route(kind))
      {
        case ConsentRoute.Reorganise: return "Reorganise this definition?";
        case ConsentRoute.Simplify: return "Simplify this selection?";
        case ConsentRoute.Solve: return "Re-run the definition?";
        case ConsentRoute.Scaffold: return "Lay down a scaffold map?";
        case ConsentRoute.Build: return "Build these regions?";
        case ConsentRoute.SetValue: return "Set a value?";
        case ConsentRoute.EditScript: return "Change this script?";
        case ConsentRoute.Edit: return "Make these changes?";
        case ConsentRoute.RunScript: return FullControl.CardTitle;
        default: return "Dwight cannot offer that.";
      }
    }

    /// <summary>
    /// WHAT REORGANISE DOES TO THE CANVAS - in ONE place, because two surfaces state it: the card the user
    /// approves, and the notice the click posts as Reorganise opens (#248). Each true on every path:
    ///   - it APPLIES; there is no step between planning and the canvas changing;
    ///   - it MOVES components into its own labelled groups, and REMOVES any existing group whose components all
    ///     sit inside what it arranges, with that group's label scribbles (CanvasWriter.ReplaceContainedGroups, on
    ///     both the plain and the tidy-I/O path; a group that spills outside is left alone) - the consequence a
    ///     user with hand-made groups needs to read before approving (#763's habit: at the point of consent, never
    ///     at the point of damage);
    ///   - it CHANGES NOTHING THEY COMPUTE - the behaviour fingerprint aborts and rolls back otherwise;
    ///   - it is ONE UNDO RECORD.
    /// The labelled-inputs-and-outputs option is not here because the verb asks it separately, before doing it.
    /// </summary>
    public const string ReorganiseConsequence =
      "It rearranges components on your canvas - moving them into its own labelled groups, and removing any " +
      "existing group that sits wholly inside what it arranges - and changes nothing they compute. One Ctrl-Z " +
      "undoes it.";

    /// <summary>
    /// The notice the consent click posts as it opens a verb: WHAT it opens ON, because "opening Simplify" on a
    /// seeded set and on the live selection are different acts and the user is entitled to know which one they
    /// approved - and what the verb will then do, in the same words its card used.
    /// </summary>
    public static string OpeningNotice(string verb, int ontoCount, string mode)
    {
      var on = ontoCount > 0
        ? "on the " + ontoCount + " component" + (ontoCount == 1 ? "" : "s") + " I described"
        : "on your current selection";
      var kind = (verb ?? "").ToLowerInvariant();
      var doing = SeedPlan.ModeLabel(kind, mode);
      var opening = "Opening " + verb + " " + on + (doing == null ? "" : ", to " + doing);

      switch (Route(kind))
      {
        case ConsentRoute.Reorganise:
          return opening + ". " + ReorganiseConsequence;
        case ConsentRoute.Simplify:
          // True: Simplify shows its proposal and waits for acceptance before it touches the canvas.
          return opening + " - it will preview before it changes anything.";
        default:
          // A verb nobody enumerated here gets no claim about what it will do (#613).
          return opening + ".";
      }
    }

    /// <summary>The CONSEQUENCE, in Dwight's own words. This sentence is the thing being agreed to, so it
    /// is never assembled from anything the model wrote.</summary>
    public static string Authority(string kind) => Authority(kind, null);

    /// <param name="mode">The VALIDATED mode id (SeedPlan.PreFillMode), or null. Named through
    /// <see cref="SeedPlan.ModeLabel"/>, so the sentence the user agrees to still contains not one word
    /// the model wrote - #235 unchanged, now covering what will happen as well as that it will (#332).</param>
    public static string Authority(string kind, string mode)
    {
      var doing = SeedPlan.ModeLabel(kind, mode);
      var willAlso = doing == null ? "" : " and " + doing;

      switch (Route(kind))
      {
        case ConsentRoute.Solve:
          return "Dwight will re-run the definition and read the result. Nothing on the canvas is changed.";
        case ConsentRoute.RunScript:
          return FullControl.CardAuthority;
        case ConsentRoute.Scaffold:
          return "Dwight will draw a map of the work - named, numbered regions with their intent written " +
                 "on the canvas, clear of your existing definition. NOTHING IS BUILT, nothing you have is " +
                 "touched, and one Ctrl-Z removes the whole map.";
        case ConsentRoute.Build:
          // The REGIONS are named in Authority(kind, mode, regions) below - this overload is reached only
          // where a caller has none to state, and it must not imply a scope it cannot name.
          return "Dwight will place and wire components on your canvas. Every step is checked and reverts " +
                 "itself if it cannot be verified, each one is a single Ctrl-Z, and Stop is live throughout.";
        case ConsentRoute.Reorganise:
          // #890: THIS SENTENCE USED TO SAY "which previews before it changes anything". IT DOES NOT: Reorganise
          // plans, then applies (LlmActions.RunReorganise goes from planning straight to SetStage(2), "Applying").
          // A consent sentence stating a safety property that does not exist is the most serious class there is
          // (#235, #338) - the user approves on the strength of it. It now says what DOES happen, from one shared
          // clause, and avoids the preview vocabulary altogether rather than negating it.
          return "This opens Reorganise" + (doing == null ? "" : ", to " + doing) + ". " + ReorganiseConsequence +
                 " It replaces this chat in the window - your conversation stays in History and reopens from there.";
        case ConsentRoute.Simplify:
          return "This opens Simplify" + willAlso + ", which previews before it changes anything. It " +
                 "replaces this chat in the window - your conversation stays in History and reopens from there.";
        case ConsentRoute.SetValue:
          // The CHANGE is named in SetValueAuthority - this overload is reached only where a caller has
          // none to state, and it must not imply a change it cannot name.
          return "Dwight will set one value on one existing component - and nothing else. The change is " +
                 "checked and reverts itself if anything but that component changed; one Ctrl-Z undoes it.";
        case ConsentRoute.EditScript:
          // #851: THE CASE THIS FAMILY WAS MISSING. Its two siblings in exactly this position - Build and
          // SetValue - each carry one deliberately, so this overload "must not imply a scope it cannot name".
          // Without it, a caller with no change to state fell to the default and was told "Nothing will
          // happen" about a kind whose whole point is that code WILL run. A hole in a family is not neutral:
          // the next kind's author copies the shape they find, not the shape that was intended.
          //
          // It keeps EditScriptAuthority's clause order - the dangerous part first, before any reassurance -
          // and, like it, never says "verified": this sentence can sit beside a check that confirmed nothing.
          return "Dwight will replace the source of one existing script component - and change nothing else on " +
                 "your canvas. THE NEW CODE WILL RUN IN YOUR DEFINITION. One Ctrl-Z undoes it, and if the change " +
                 "does not check out Dwight puts your original source back and tells you so.";
        case ConsentRoute.Edit:
          // This overload is reached only where a caller has no plan to state, and it must not imply a scope it cannot
          // name (Build's and SetValue's rule). The plan's own sentence is EditAuthority.
          return "Dwight will add wires, remove wires or delete existing components - only the changes you tick - " +
                 "and nothing else, apart from a group Dwight made that a delete leaves empty, which goes too. Every change is " +
                 "checked after it is made and all of them are put back if anything else differs; one Ctrl-Z undoes it.";
        default:
          return "Nothing will happen.";
      }
    }

    // ==== THE BATCH CARD (Phase-9 U6 + U21, ruled at #760) ================================================================

    /// <summary>
    /// ABOUT TWELVE ROWS ARE SHOWN; the rest collapse (#760 Q1, the soft bound). It is a starting value to be measured
    /// against real cards, not a constant to defend - and it changes only what is SHOWN: every row is validated whether
    /// it is visible or not, and the authority line states the TOTAL either way.
    /// </summary>
    public const int MaxEditRowsShown = 12;

    /// <summary>How many "this leaves X with no source" sentences one delete row shows before it says how many more.</summary>
    public const int MaxOrphansShown = 8;

    public const string EditButton = "Make these changes";
    public const string NothingTickedLabel = "Tick at least one change";

    /// <summary>
    /// THE CONSEQUENCE FOR AN EDIT. THE TOTAL IS THE CONTRACT; THE ROWS ARE THE EVIDENCE (#760) - so the count of every
    /// change on the card is stated here whether or not every row is on screen, in the plugin's own numbers. The ticks
    /// are the user's: an unticked row is left exactly as it is, and the check afterwards is computed on what was ticked
    /// alone. Every word is plugin-authored (#235) - counts, never the model's description of them.
    /// </summary>
    public static string EditAuthority(EditPlan plan)
    {
      if (plan == null || plan.Steps.Count == 0) return Authority("edit", null);

      var parts = new List<string>();
      if (plan.Wires > 0) parts.Add(EditOpsAsk.Plural(plan.Wires, "wire") + " added");
      if (plan.Unwires > 0) parts.Add(EditOpsAsk.Plural(plan.Unwires, "wire") + " removed");
      if (plan.Deletes > 0) parts.Add(EditOpsAsk.Plural(plan.Deletes, "component") + " deleted");

      var line = "This card holds " + EditOpsAsk.Plural(plan.Steps.Count, "change") + " in all to components that already " +
                 "exist (" + string.Join(", ", parts) + "). Dwight applies only the rows you leave ticked - an unticked row " +
                 "is left exactly as it is - and changes nothing else on your canvas. ";
      if (plan.Deletes > 0)
        line += "A deleted component takes every wire on it with it, and a group Dwight made goes too if the delete leaves it empty. ";
      return line + "After the changes are made Dwight checks the result and puts all of them back if anything other than " +
             "the ticked changes differs; one Ctrl-Z undoes them. Then Dwight re-reads the result once and continues.";
    }

    /// <summary>
    /// THE BATCH CARD. Rows are tickable, all ticked to start; the first <see cref="MaxEditRowsShown"/> are on screen
    /// and the remainder sit under "and N more, grouped by region" - plugin-computed from the canvas's own groups, never
    /// the model's summary of itself (#056/#235).
    ///
    /// THE CLICK CARRIES THE TICKS. The button posts one token plus the indices of the ticked rows
    /// (<c>consent:TOKEN:0,2,5</c>, read by <see cref="ConsentTokens.Split"/>), gathered in the browser from the card's own
    /// checkboxes; nothing about WHICH rows is in the token, so the same card cannot be redeemed for a different set than
    /// the user ticked. With nothing ticked the button says so and sends nothing (an affordance asserts its target
    /// exists, #321 - a click that silently did nothing would be a dead button).
    ///
    /// Escaped HERE (#335): the rows are canvas labels and ids, but a label is user text and the markup is ours.
    /// </summary>
    public static string EditHtml(string why, string token, EditPlan plan, string autoMode = null)
    {
      var body = new System.Text.StringBuilder();
      var steps = plan?.Steps ?? new List<EditStep>();
      var shown = steps.Take(MaxEditRowsShown).ToList();
      var rest = steps.Skip(MaxEditRowsShown).ToList();

      body.Append("<div class=\"edit-rows\">");
      foreach (var s in shown) body.Append(EditRow(s));
      body.Append("</div>");

      if (rest.Count > 0)
      {
        body.Append("<details class=\"edit-more\"><summary>and ")
            .Append(rest.Count).Append(" more, grouped by region</summary>");
        foreach (var g in rest.GroupBy(s => s.Region ?? "").OrderBy(g => rest.IndexOf(g.First())))
        {
          var name = string.IsNullOrEmpty(g.Key) ? "Not in a group" : "Region " + g.Key;
          body.Append("<p class=\"consent-note edit-region\">").Append(WebUtility.HtmlEncode(name))
              .Append(" (").Append(g.Count()).Append(")</p>");
          foreach (var s in g) body.Append(EditRow(s));
        }
        body.Append("</details>");
      }

      return RenderEdit(EditAuthority(plan), why, token, body.ToString(), autoMode, EditAct(plan));
    }

    /// <summary>A batch card's act: "Make 3 changes to existing components: 2 wires added, 1 component deleted." - the plugin's counts.</summary>
    public static string EditAct(EditPlan plan)
    {
      if (plan == null || plan.Steps.Count == 0) return ActFor("edit");
      var parts = new List<string>();
      if (plan.Wires > 0) parts.Add(EditOpsAsk.Plural(plan.Wires, "wire") + " added");
      if (plan.Unwires > 0) parts.Add(EditOpsAsk.Plural(plan.Unwires, "wire") + " removed");
      if (plan.Deletes > 0) parts.Add(EditOpsAsk.Plural(plan.Deletes, "component") + " deleted");
      return "Make " + EditOpsAsk.Plural(plan.Steps.Count, "change") + " to existing components: " + string.Join(", ", parts) + ".";
    }

    /// <summary>One tickable row: the number the refusals will cite, the change, and - for a delete - what it leaves
    /// without a source. The orphan lines belong to THEIR row, so unticking the delete takes its warning with it.</summary>
    private static string EditRow(EditStep s)
    {
      var sb = new System.Text.StringBuilder();
      sb.Append("<label class=\"edit-row\"><input type=\"checkbox\" class=\"edit-tick\" value=\"")
        .Append(s.Row - 1).Append("\" checked=\"checked\"/>")
        .Append("<span class=\"edit-n\">").Append(s.Row).Append("</span>")
        .Append("<span class=\"edit-text\">").Append(WebUtility.HtmlEncode(s.Text));
      foreach (var o in s.Orphans.Take(MaxOrphansShown))
        sb.Append("<span class=\"edit-orphan\">").Append(WebUtility.HtmlEncode(o)).Append("</span>");
      if (s.Orphans.Count > MaxOrphansShown)
        sb.Append("<span class=\"edit-orphan\">").Append(WebUtility.HtmlEncode(
          "and " + (s.Orphans.Count - MaxOrphansShown) + " more inputs lose a source")).Append("</span>");
      sb.Append("</span></label>");
      return sb.ToString();
    }

    /// <summary>The edit card's frame - the standard card, with a button that gathers the ticks.</summary>
    private static string RenderEdit(string authority, string why, string token, string body, string autoMode = null, string act = null)
    {
      // The ticks are what the user chooses, so the rows stay on the card's face; the authority sentence goes in the fold.
      var button =
        "<button class=\"btn primary\" onclick=\"var x=[];this.closest('.consent').querySelectorAll('.edit-tick')" +
        ".forEach(function(b){if(b.checked)x.push(b.value)});if(!x.length){this.textContent='" + NothingTickedLabel +
        "';return;}dwightSend('consent:" + WebUtility.HtmlEncode(token ?? "") + ":'+x.join(','))\">" +
        WebUtility.HtmlEncode(EditButton) + "</button>";
      return Card("edit", act ?? ActFor("edit"), "<p class=\"consent-note\">" + WebUtility.HtmlEncode(authority) + "</p>",
                  why, button, autoMode, body);
    }

    /// <summary>The set_value card: the validated change in the authority line, the model's reason
    /// escaped and subordinate, exactly as every other card.</summary>
    public static string Html(string kind, string why, string token, SetValueChange change, string autoMode = null) =>
      Render(kind, SetValueAuthority(change), why, token, null, autoMode,
             change == null ? null : "Set " + change.Label + " from " + change.Before + " to " + change.After + ".");

    /// <summary>
    /// The card. Dwight's act and consequence first, then the model's reason - escaped, labelled, and
    /// subordinate.
    /// </summary>
    public static string Html(string kind, string why, string token) => Html(kind, why, token, (string)null);

    /// <param name="mode">The validated mode, named in the authority line so the user can see WHICH
    /// variant they are approving rather than only which verb.</param>
    public static string Html(string kind, string why, string token, string mode) =>
      Html(kind, why, token, mode, null);

    /// <param name="regions">For a BUILD: the VALIDATED region addresses this card approves. The
    /// authority line names them, because #338 makes the card a contract about scope and a scope the
    /// sentence does not state is not one the user agreed to. Plugin-computed - never the model's text.</param>
    public static string Html(string kind, string why, string token, string mode, IReadOnlyList<string> regions) =>
      Html(kind, why, token, mode, regions, null);

    /// <param name="removalSentence">For a REBUILD: what will be removed, named, and what will be left.
    /// Plugin-computed from the ledger and the live canvas. This is the sentence that makes a
    /// document-lived delete-claim safe, so it belongs on the card rather than in the reply.</param>
    public static string Html(string kind, string why, string token, string mode,
      IReadOnlyList<string> regions, string removalSentence) =>
      Html(kind, why, token, mode, regions, removalSentence, null);

    /// <param name="scopeLine">For a REORGANISE or SIMPLIFY: the scope resolved AT THE ASK, with its count
    /// (<see cref="VerbScope.CardScopeLine"/>, U28) - the components the run will open on, so the card states the scope
    /// it is a contract about (#338). Plugin-authored; null leaves the card exactly as it was.</param>
    public static string Html(string kind, string why, string token, string mode,
      IReadOnlyList<string> regions, string removalSentence, string scopeLine, string buildStyle = null,
      IReadOnlyList<ApprovedScript> scripts = null, string autoMode = null)
    {
      var authority = regions != null && regions.Count > 0
        ? BuildAuthority(kind, regions, removalSentence, buildStyle)
        : Authority(kind, mode);
      return Render(kind, WithScope(authority, scopeLine), why, token,
                    regions != null && regions.Count > 0 ? ScriptsSection(scripts, ExpandedByDefaultFor(kind)) : null, autoMode,
                    regions != null && regions.Count > 0 ? BuildAct(regions) : ScopedAct(kind, scopeLine));
    }

    public const string ScriptsHeading = "Scripts in this build";
    public const string ScriptsWarning = "These run on your machine when you approve. Read them.";

    /// <summary>
    /// U65: THE COMPLETE SOURCE OF EVERY SCRIPT A REGION BUILD CARRIES, UNTRUNCATED - the consent surface for code that is about to
    /// run on the user's machine, as <see cref="SpotSourceBlocks"/> is for a placement. After the authority line, headed, with a
    /// line in Dwight's voice above the code; per script the region, the language and any ports it adds, then the whole source in
    /// a code block. No collapse, no cut: a card that hides text by default has not shown it. Escaped HERE, because escaping is a
    /// property of the destination (#335). Empty for a build that carries no script, so that card is byte-identical to before.
    /// </summary>
    public static string ScriptsSection(IReadOnlyList<ApprovedScript> scripts, bool expandedByDefault = false)
    {
      var list = (scripts ?? new ApprovedScript[0]).Where(s => s != null).ToList();
      if (list.Count == 0) return "";
      var sb = new System.Text.StringBuilder();
      sb.Append("<p class=\"consent-note\"><strong>").Append(WebUtility.HtmlEncode(ScriptsHeading)).Append("</strong></p>");
      sb.Append("<p class=\"consent-note\">").Append(WebUtility.HtmlEncode(ScriptsWarning)).Append("</p>");
      foreach (var s in list)
      {
        // Brief AL: one folded section per script - "Region B1. Python, 84 lines. Inputs: ... Touches the Rhino document: no." -
        // with the whole source inside it. The line is the plugin's, from the record.
        var summary = "Region " + (s.Region ?? "") + ". " +
                      ScriptSummary.Line(ScriptSummary.LanguageLabel(s.Language), s.Source, s.Inputs, s.Outputs, inputAccess: s.InputAccess);
        sb.Append(ScriptFold(summary, s.Source, expandedByDefault));
      }
      return sb.ToString();
    }

    /// <summary>
    /// U28: the authority line with the resolved scope FIRST - what the act runs on, then what it does - so the count
    /// the user approves is read before the consequence it qualifies. A null or blank scope line returns the authority
    /// unchanged, byte for byte, so every card without one reads exactly as before.
    /// </summary>
    /// <summary>A Reorganise or Simplify card's act carries its resolved scope when there is one: "Reorganise this definition. On the 4 components ..."</summary>
    public static string ScopedAct(string kind, string scopeLine) =>
      string.IsNullOrWhiteSpace(scopeLine) ? ActFor(kind) : ActFor(kind) + " " + scopeLine.Trim();

    public static string WithScope(string authority, string scopeLine) =>
      string.IsNullOrWhiteSpace(scopeLine) ? authority : scopeLine.Trim() + " " + (authority ?? "");

    /// <summary>
    /// WHY THIS PATH AND NOT THE OTHER, in one clause (§9.5).
    ///
    /// The route decision is not the user's and not the model's - it is a COMPUTED FACT, and #146 governs:
    /// the plugin knows the whole proposal at card time, so it decides and then SAYS what it decided. This
    /// is that sentence, and it is derived from the very fact that chose the route: the placement is within
    /// <see cref="SpotPlacement.MaxComponents"/>, which is the product's own definition of small.
    ///
    /// It is a statement, never a question. A choice offered before its consequences are in view is not a
    /// choice, so the card shows what will happen and states why this path - it does not ask first.
    /// </summary>
    public static string SpotRouteStatement(SpotPlacement placement) =>
      placement == null || placement.Components == null || placement.Components.Count == 0
        ? null
        : "This is small enough to build directly.";

    /// <summary>
    /// THE SPOT CARD: two consequences, two buttons, one act (U18, #770).
    ///
    /// EACH BUTTON CARRIES ITS OWN TOKEN, pre-bound at mint time to the option it belongs to. The click is
    /// therefore a LOOKUP and never a parse: there is no string a caller could mangle into the other
    /// option, and an unknown token simply does not resolve. That is #208's shape - resolve exactly or
    /// refuse - applied to a choice rather than to an address.
    ///
    /// ================================================================================================
    /// A KNOWN EXCEPTION, WITH ITS REPLACEMENT NAMED (#773). THIS IS NOT THE SETTLED CARD GRAMMAR.
    ///
    /// §9.5 rules AT MOST ONE ALTERNATIVE ACTION PER CARD, and this card carries TWO "yes" buttons, which
    /// that count forbids. It ships anyway, for one round, because #770 landed four hours before §9.5 and
    /// the two did not see each other.
    ///
    /// The diagnosis, which is the part worth keeping: THE GROUPING WAS NEVER AN ALTERNATIVE ACTION. It is
    /// a PROPERTY OF THE ONE ACTION - same components, same scope, same record, same undo, differing only
    /// in whether a box is drawn - and two "yes" buttons is what that mis-framing looks like on screen.
    /// The ambiguity is the symptom: a user should not have to work out which "yes" is the default.
    ///
    /// U18b REPLACES THIS with one primary action, one alternative action (the route button), and the
    /// grouping as a TICKABLE OPTION ticked by default - converging with the batch card's shape (#760 Q1).
    /// Its hard condition: THE AUTHORITY LINE MUST RE-RENDER WHEN THE TICK CHANGES, because a line reading
    /// "in a new group called Spot 1" above an unticked box would be a card asserting something untrue at
    /// the moment of approval, which is the one thing a card may never do.
    ///
    /// Do not read this card as the pattern to copy.
    /// ================================================================================================
    /// </summary>
    public static string SpotHtml(string why, SpotPlacement placement, string groupedToken, string looseToken, string autoMode = null)
    {
      var route = SpotRouteStatement(placement);
      var into = !string.IsNullOrWhiteSpace(placement?.Into);
      var fold =
        "<p class=\"consent-note\">" + WebUtility.HtmlEncode(SpotAuthority(placement, grouped: true)) + "</p>" +
        (into ? "" : "<p class=\"consent-note\">" + WebUtility.HtmlEncode(SpotAuthority(placement, grouped: false)) + "</p>") +
        (route == null ? "" : "<p class=\"consent-route\"><em>" + WebUtility.HtmlEncode(route) + "</em></p>") +
        SpotComponentRows(placement) +
        SpotSourceBlocks(placement);
      var buttons = into
        ? "<button class=\"btn primary\" onclick=\"dwightSend('consent:" + WebUtility.HtmlEncode(groupedToken ?? "") + "')\">" +
          WebUtility.HtmlEncode("Place in " + placement.Into) + "</button>"
        :
        "<button class=\"btn primary\" onclick=\"dwightSend('consent:" +
        WebUtility.HtmlEncode(groupedToken ?? "") + "')\">" +
        WebUtility.HtmlEncode(SpotOptionButton(grouped: true)) + "</button>" +
        "<button class=\"btn\" onclick=\"dwightSend('consent:" +
        WebUtility.HtmlEncode(looseToken ?? "") + "')\">" +
        WebUtility.HtmlEncode(SpotOptionButton(grouped: false)) + "</button>";
      return Card("build", SpotAct(placement), fold, why, buttons, autoMode, null, SpotTitle(placement));
    }

    /// <summary>
    /// Brief CV: EVERY COMPONENT ON ITS OWN ROW, once the sentences have folded, so the whole scope is one read away (#338). The names are the
    /// plugin's, and are escaped here because escaping is a property of the destination (#335). Empty while the sentences name every one, because
    /// the same list twice is noise: a card of up to <see cref="RebuildPreview.MaxNamed"/> has no rows to add.
    /// </summary>
    public static string SpotComponentRows(SpotPlacement placement)
    {
      var items = placement?.Components;
      if (items == null || items.Count <= RebuildPreview.MaxNamed) return "";
      var sb = new System.Text.StringBuilder();
      sb.Append("<p class=\"consent-note\">All ").Append(items.Count).Append(" components:</p><ul class=\"consent-names\">");
      foreach (var item in items) sb.Append("<li>").Append(WebUtility.HtmlEncode(item)).Append("</li>");
      return sb.Append("</ul>").ToString();
    }

    /// <summary>
    /// THE COMPLETE SOURCE OF EVERY SCRIPT THIS PLACEMENT CARRIES, UNTRUNCATED (U19) - the consent surface for code that is
    /// about to run on the user's machine. Same classes as the edit_script card (<c>consent-note</c> and
    /// <c>consent-source</c>), so it renders the way the one already gated does. Escaped HERE, because escaping is a
    /// property of the destination (#335): the same text is inert in a prompt and markup in HTML. NEVER cut: the bound that
    /// keeps it readable lives at the parse (<see cref="EditScriptAsk.MaxSourceChars"/>), where an over-long script is
    /// refused before a card exists - cutting it here would mean asking someone to approve code they were not shown.
    /// </summary>
    public static string SpotSourceBlocks(SpotPlacement placement)
    {
      var scripts = placement?.Scripts;
      if (scripts == null || scripts.Count == 0) return "";
      var sb = new System.Text.StringBuilder();
      foreach (var s in scripts)
      {
        sb.Append("<p class=\"consent-note\">The complete source of ").Append(WebUtility.HtmlEncode(s.Name))
          .Append(" (").Append(WebUtility.HtmlEncode(s.LanguageLabel)).Append("), which is what will run:</p>");
        // U98: THE SAME FOLD AS THE REGION CARD'S (Brief AL's rule: one fold everywhere except the first run_script card in a thread).
        // The plugin's summary line - language, lines, declared ports, the document test - and the whole source inside it, either way.
        var summary = s.Name + ". " + ScriptSummary.Line(s.LanguageLabel, s.Source, s.Inputs, s.Outputs, inputAccess: s.InputAccess);
        sb.Append(ScriptFold(summary, s.Source ?? "", ExpandedByDefaultFor("build")));
      }
      return sb.ToString();
    }

    /// <summary>The spot card's ACT. It does not presume either option - the consequences below it do.</summary>
    public static string SpotTitle(SpotPlacement placement) =>
      placement == null || placement.Components == null || placement.Components.Count <= 1
        ? "Place this component?"
        : "Place these components?";

    /// <summary>The one renderer: Dwight's act and consequence first, then the model's reason - escaped,
    /// labelled, and subordinate. Every card overload arrives here with its authority already decided.</summary>
    private static string Render(string kind, string authority, string why, string token) =>
      Render(kind, authority, why, token, null);

    /// <param name="body">Plugin-authored content between the authority line and the model's reason - the
    /// diff and the source for an edit_script. It is ALREADY ENCODED by its builder, because only the
    /// builder knows which parts are markup and which are the user's text; the reason below is encoded
    /// here, where it arrives as raw model output.</param>
    private static string Render(string kind, string authority, string why, string token, string body, string autoMode = null,
                                 string act = null, string visible = null)
    {
      var buttons = "<button class=\"btn primary\" onclick=\"dwightSend('consent:" +
                    WebUtility.HtmlEncode(token ?? "") + "')\">Yes, go ahead</button>";
      return Card(kind, act ?? ActFor(kind), "<p class=\"consent-note\">" + WebUtility.HtmlEncode(authority) + "</p>" + (body ?? ""),
                  why, buttons, autoMode, visible);
    }

    // ==== BRIEF BQ: WHAT RAN, IN THE PAST TENSE. The acts above are what a card offers; these are what History says happened. ==============

    /// <summary>The past tense of <see cref="ActFor"/>, for a site with nothing more exact to say.</summary>
    public static string DoneFor(string kind)
    {
      switch (Route(kind))
      {
        case ConsentRoute.Reorganise: return "Reorganised this definition.";
        case ConsentRoute.Simplify: return "Simplified this selection.";
        case ConsentRoute.Solve: return "Re-ran the definition.";
        case ConsentRoute.Scaffold: return "Laid down a scaffold map.";
        case ConsentRoute.Build: return "Built on your canvas.";
        case ConsentRoute.SetValue: return "Set one value.";
        case ConsentRoute.EditScript: return "Changed a script.";
        case ConsentRoute.Edit: return "Changed existing components.";
        case ConsentRoute.RunScript: return "Ran a Python 3 script.";
        default: return "";
      }
    }

    /// <summary>One record is one region, so this names the region that ran, not every region the card offered.</summary>
    public static string BuildDone(string region, bool rebuilt) =>
      string.IsNullOrWhiteSpace(region) ? DoneFor("build") : (rebuilt ? "Rebuilt " : "Built ") + region.Trim() + ".";

    /// <summary>"Placed a Square, Spot 5, near empty_number." A spot's card record IS what runs, so its sentence is exact. Near, not beside: the search takes the nearest clear place, and that may be further than beside.</summary>
    public static string SpotDone(SpotPlacement placement)
    {
      if (placement == null || placement.Components == null || placement.Components.Count == 0) return DoneFor("build");
      if (!string.IsNullOrWhiteSpace(placement.Into)) return "Placed " + FoldThem(placement.Components) + " in " + placement.Into + ".";
      return "Placed " + FoldThem(placement.Components) + ", " + placement.Address + ", " + SpotPlace(placement) + ".";
    }

    /// <summary>What the rows left ticked did: <paramref name="plan"/> is validated from the ticks (GhEditOps.RunApproved), so an unticked row is
    /// not in it and is not counted (#338).</summary>
    public static string EditDone(EditPlan plan)
    {
      if (plan == null || plan.Steps.Count == 0) return DoneFor("edit");
      var parts = new List<string>();
      if (plan.Wires > 0) parts.Add("added " + EditOpsAsk.Plural(plan.Wires, "wire"));
      if (plan.Unwires > 0) parts.Add("removed " + EditOpsAsk.Plural(plan.Unwires, "wire"));
      if (plan.Deletes > 0) parts.Add("deleted " + EditOpsAsk.Plural(plan.Deletes, "component"));
      var text = string.Join(", ", parts);
      return char.ToUpperInvariant(text[0]) + text.Substring(1) + ".";
    }

    /// <summary>"Set Radius.", by the name the user gave it; "Set the Number Slider." when it has none.</summary>
    public static string SetValueDone(SetValueChange change) =>
      change == null ? DoneFor("set_value") : "Set " + (string.IsNullOrWhiteSpace(change.Nick) ? "the " + change.Type : change.Nick.Trim()) + ".";

    public static string EditScriptDone(ScriptEditChange change) =>
      change == null || string.IsNullOrWhiteSpace(change.Label) ? DoneFor("edit_script") : "Edited " + change.Label.Trim() + ".";

    /// <summary>A build card's act: "Build A1 and B2." - the approved addresses, plugin-validated, never the model's words.</summary>
    public static string BuildAct(IReadOnlyList<string> regions) =>
      regions == null || regions.Count == 0 ? ActFor("build") : "Build " + NameThem(regions.ToList()) + ".";

    /// <summary>A spot card's act: "Place a Square, Spot 5, near empty_number." It names the components, folded past five (Brief CV), and the address, and does
    /// not presume either option: the buttons choose. One named into a group has no address of its own to give and no option to choose: "Place a Square in A1."</summary>
    public static string SpotAct(SpotPlacement placement)
    {
      if (placement == null || placement.Components == null || placement.Components.Count == 0) return ActFor("build");
      if (!string.IsNullOrWhiteSpace(placement.Into)) return "Place " + FoldThem(placement.Components) + " in " + placement.Into + ".";
      return "Place " + FoldThem(placement.Components) + ", " + placement.Address + ", " + SpotPlace(placement) + ".";
    }

    private static string SpotPlace(SpotPlacement placement)
    {
      var where = string.IsNullOrWhiteSpace(placement.Beside) ? ClearPlace : "near " + placement.Beside;
      return string.IsNullOrWhiteSpace(placement.NotIn) ? where : where + ", not in " + placement.NotIn;
    }

    // ==== BRIEF BG: A CARD THAT WAITS IS OPEN AND TRIMMED; A CARD THAT WAS ANSWERED IS A RECEIPT ROW ==========================

    /// <summary>The label of the one fold that holds everything a waiting card does not show on its face.</summary>
    public const string MoreLabel = "What this does and what it cannot";

    /// <summary>The one plain line under the buttons, standing in for the consequence sentences that live in the fold.</summary>
    public const string CheckedLine = "Checked after it runs; reverts itself if the check fails; one Ctrl-Z.";
    public const string EditScriptLine = "Checked after it runs; puts your source back if the check fails; one Ctrl-Z.";
    public const string RunScriptLine = "Not checked; one Ctrl-Z for what Grasshopper recorded.";
    public const string ScaffoldLine = "Nothing is built; one Ctrl-Z removes the map.";
    public const string SolveLine = "Nothing on the canvas changes.";
    public const string ReorganiseLine = "Opens Reorganise; changes nothing the definition computes; one Ctrl-Z.";
    public const string SimplifyLine = "Opens Simplify, which previews before it changes anything.";

    /// <summary>The kind's one-liner (Brief BG): what the check is, in one line, with the sentences in the fold.</summary>
    public static string OneLiner(string kind)
    {
      switch (Route(kind))
      {
        case ConsentRoute.RunScript: return RunScriptLine;
        case ConsentRoute.EditScript: return EditScriptLine;
        case ConsentRoute.Scaffold: return ScaffoldLine;
        case ConsentRoute.Solve: return SolveLine;
        case ConsentRoute.Reorganise: return ReorganiseLine;
        case ConsentRoute.Simplify: return SimplifyLine;
        default: return CheckedLine;
      }
    }

    /// <summary>The act in one plain sentence when a caller has nothing more specific to name: the title, as a statement.</summary>
    public static string ActFor(string kind)
    {
      switch (Route(kind))
      {
        case ConsentRoute.Reorganise: return "Reorganise this definition.";
        case ConsentRoute.Simplify: return "Simplify this selection.";
        case ConsentRoute.Solve: return "Re-run the definition.";
        case ConsentRoute.Scaffold: return "Lay down a scaffold map.";
        case ConsentRoute.Build: return "Build on your canvas.";
        case ConsentRoute.SetValue: return "Set one value.";
        case ConsentRoute.EditScript: return "Change this script.";
        case ConsentRoute.Edit: return "Make these changes.";
        case ConsentRoute.RunScript: return "Run a Python 3 script.";
        default: return "Dwight cannot offer that.";
      }
    }

    /// <summary>
    /// THE ONE CARD FRAME (Brief BG). Waiting: the act and its scope in one sentence, then the model's reason, labelled, then the buttons,
    /// then one plain line, then ONE fold labelled <see cref="MoreLabel"/> holding everything else - the authority sentences, the scripts,
    /// the diff, the route. <paramref name="visible"/> is plugin markup that must be read before the click and so stays outside the fold
    /// (a run_script card's source, ADR-100; a batch card's ticks). Approved automatically: the same card, arriving as a receipt row,
    /// collapsed, since nobody reads it to decide. The complete text is in the DOM in every state (#338).
    /// </summary>
    internal static string Card(string kind, string act, string foldBody, string why, string buttons, string autoMode, string visible = null,
                                string title = null)
    {
      var reason = WebUtility.HtmlEncode(why ?? "");
      var auto = AutoStateHtml(autoMode);
      // The card's question title heads the fold, so the full card as it was - title, consequence, everything - is still one read away.
      foldBody = "<p class=\"consent-note consent-title\"><strong>" + WebUtility.HtmlEncode(title ?? Title(kind)) + "</strong></p>" + (foldBody ?? "");
      var html =
        "<div class=\"consent\" data-kind=\"" + WebUtility.HtmlEncode(kind ?? "") + "\" data-act=\"" + WebUtility.HtmlEncode(act ?? "") + "\">" +
        "<p class=\"consent-act\"><strong>" + WebUtility.HtmlEncode(act ?? "") + "</strong></p>" + auto +
        "<div class=\"consent-why\"><span class=\"consent-why-label\">" + ReasonLabel + "</span>" + reason + "</div>" +
        (visible ?? "") +
        (auto.Length > 0 ? "" : "<div class=\"consent-btns\">" + buttons + "</div>") +
        "<p class=\"consent-line\">" + WebUtility.HtmlEncode(OneLiner(kind)) + "</p>" +
        "<details class=\"consent-more\"><summary>" + WebUtility.HtmlEncode(MoreLabel) + "</summary>" + (foldBody ?? "") + "</details>" +
        "</div>";
      return auto.Length > 0 ? CardReceipt.Of(html, CardReceipt.AutoState(ApprovalMode.AutoStateLine(autoMode))) : html;
    }

    /// <summary>The label over the model's reason, as it has been since #235.</summary>
    public const string ReasonLabel = "Dwight's reason for asking";

    /// <summary>
    /// Brief AL: the state line a card approved automatically carries under its title, in the card's note style, verbatim
    /// (<see cref="ApprovalMode.AutoStateLine"/>); the button is gone. Empty for a card that waits, which is therefore
    /// byte-identical to what it was before modes existed.
    /// </summary>
    internal static string AutoStateHtml(string autoMode)
    {
      var line = autoMode == null ? null : ApprovalMode.AutoStateLine(autoMode);
      return line == null ? "" : "<p class=\"consent-note consent-auto\">" + WebUtility.HtmlEncode(line) + "</p>";
    }
  }

  /// <summary>
  /// ONE SMALL PLACEMENT, as the card describes it (U18). Every field is PLUGIN-COMPUTED: the address is
  /// minted, the component labels come from the validated spec, and "beside" is read off the live canvas.
  /// Nothing the model wrote reaches this type, which is what keeps the card's sentence #235-clean.
  /// </summary>
  internal sealed class SpotPlacement
  {
    /// <summary>The minted address - "Spot 1". Reserved when the card is minted so a second card cannot
    /// mint the same one.</summary>
    public string Address { get; set; }

    /// <summary>Every component, named - never a count. For a spot the components ARE the scope, so the card's body lists them all and its
    /// sentences name the first five and state the total (Brief CV). A placement past <see cref="MaxComponents"/> is refused before a card exists.</summary>
    public List<string> Components { get; set; } = new List<string>();

    /// <summary>What it will sit near, in words a person can check by looking, or null for <see cref="ConsentCard.ClearPlace"/>.
    /// Computed from what the build actually wires into.</summary>
    public string Beside { get; set; }

    /// <summary>Brief CM: the existing group this lands in, as the card names it, or null for a place of its own.</summary>
    public string Into { get; set; }

    /// <summary>Brief CM: a group the user's message named and this is NOT in, because the model said so. The card states it: a new place is never the silent answer to a named destination.</summary>
    public string NotIn { get; set; }

    /// <summary>The scripts in this placement that carry source (U19), as the card names them. Plugin-computed from the
    /// validated spec; empty for every placement without code, which leaves the card exactly as it was.</summary>
    public List<SpotScript> Scripts { get; set; } = new List<SpotScript>();

    /// <summary>
    /// THE BOUND ON A PLACEMENT WITHOUT A MAP: TWELVE (Tharun's ruling and the architect's, 1 November 2026, Brief CV).
    ///
    /// It was five, borrowed from <see cref="RebuildPreview.MaxNamed"/> on the reasoning that a card must name every component and a
    /// person stops reading a long card. Tharun ruled that the limit should not be a bottleneck. The card now stays readable by folding its
    /// sentences, so THIS NUMBER IS THE PLACEMENT'S, NOT THE CARD'S: a placement without a map lands as one block in clear space, its inputs and outputs
    /// in columns and the rest in rows, packed from measured bounds with no tidy after it (SpotLayout), and past about a dozen that block has no layout worth the name. Work of
    /// that size wants the map, which lays its regions down with room.
    /// </summary>
    public const int MaxComponents = 12;

    /// <summary>
    /// THE REFUSAL AT THE BOUND NAMES THE DOOR. Work too large to place as one block is work that wants a map,
    /// and propose_scaffold is that door - so this refuses toward it rather than simply saying no
    /// (#613/#626: a refusal is only honest when there was nothing else to do, and here there is
    /// something else to do). That is also what keeps Scaffold's reason to exist intact: U18 removes the
    /// friction of needing a map for ONE panel, and does not become a way to build a definition without
    /// one.
    /// </summary>
    public static bool TryDescribe(IReadOnlyList<string> components, string address, string beside,
                                   out SpotPlacement placement, out ToolResult error)
    {
      placement = null;
      error = null;

      var named = (components ?? new List<string>()).Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
      if (named.Count == 0)
      {
        error = ToolResult.Fail(ToolErrors.InvalidArgs,
          "That build places nothing, so there is nothing to ask the user to approve.");
        return false;
      }

      if (named.Count > MaxComponents)
      {
        error = ToolResult.Fail(ToolErrors.PreconditionRefused,
          "That is " + named.Count + " components to place at once. Without a map they land as one block, and " +
          "past " + MaxComponents + " a block stops being something anyone can follow. Work this " +
          "size wants a map: call propose_scaffold, and build the regions it lays down.",
          new JsonObject { ["asked"] = named.Count, ["mostThatCanBeNamed"] = MaxComponents });
        return false;
      }

      placement = new SpotPlacement { Address = address, Beside = beside, Components = named };
      return true;
    }
  }
}
