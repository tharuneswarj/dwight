using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;

namespace Dwight.UI
{
  /// <summary>
  /// The first-run tour's decisions and its payload, GH-free and Eto-free so they can be tested
  /// headlessly (ADR-091). <see cref="FirstRunTour"/> is the window; everything here is the part that
  /// can be wrong in a way a test can see: whether the tour shows at all, what the plugin tells it,
  /// whether the documentation link exists, and what a machine with no WebView2 gets instead.
  /// </summary>
  internal static class FirstRunCore
  {
    /// <summary>
    /// Where the Settings "Documentation" entry and the tour's last step point: the site's live /docs.
    ///
    /// It was DELIBERATELY EMPTY until that section existed, because an affordance is an assertion that its
    /// target exists (#321, the rule that makes a retired component id refuse rather than pan somewhere
    /// plausible - the same thing one level out) and a "Documentation" button that 404s is worse than no button.
    /// It was flipped on 2026-09-20, when the page returned 200 (checked at the time, not assumed), so both
    /// surfaces now offer it. The Phase-10 constraint that the docs be live before the build is cut is met by it.
    ///
    /// THIS IS A COMPILE-TIME CONSTANT ON PURPOSE. The surfaces' CSP is connect-src 'none' (ADR-088), so nothing can
    /// fetch the docs' address or check it at runtime; if the page ever moves, this string is the one place that
    /// changes, and the test below is what makes that change a deliberate one. The hidden state (no URL pushed, no
    /// button) is still held by the harness fixtures for both shells, so it cannot rot while this is set.
    /// </summary>
    public const string DocsUrl = "https://dwight.tha.run/docs";

    /// <summary>True when the documentation affordance may be offered at all.</summary>
    public static bool DocsLinkVisible => !string.IsNullOrWhiteSpace(DocsUrl);

    /// <summary>
    /// Brief EN: the site's keys page, for the person who reaches "Add your API key" not knowing what a key is, where one
    /// comes from, or what it costs. The tour's last step offers it while no key is stored ("How to get a key") and
    /// Settings offers it under the key input. A compile-time constant for the same reasons as <see cref="DocsUrl"/>, with
    /// the same visibility rule: empty means no link anywhere, because an affordance is an assertion that its target exists.
    /// </summary>
    public const string KeysUrl = "https://dwight.tha.run/docs/keys";

    /// <summary>True when the keys page may be offered at all.</summary>
    public static bool KeysLinkVisible => !string.IsNullOrWhiteSpace(KeysUrl);

    /// <summary>
    /// The tour's and Settings' command table for the links they offer: the bare command a shell sends, and the one
    /// address the plugin launches for it. A page never sends an address; it sends "docs" or "keys" and this decides, so
    /// no string from a page can become something the process launches. Exact match only, and null for anything else,
    /// including a hidden link's command.
    /// </summary>
    public static string LinkFor(string command)
    {
      switch (command)
      {
        case "docs": return DocsLinkVisible ? DocsUrl : null;
        case "keys": return KeysLinkVisible ? KeysUrl : null;
        default: return null;
      }
    }

    /// <summary>
    /// Does the tour open by itself this session? Only when it has never been seen on this profile AND
    /// it has not already opened since Grasshopper started.
    ///
    /// <para><b>"Seen" means shown, not read.</b> The flag is written the moment the tour has actually
    /// rendered, so every way out is equivalent - the close button, Escape, the last card, or Rhino
    /// falling over midway. The alternative (mark it seen only at the end) makes a first-run surface one
    /// that will not take no for an answer, and the costs are not symmetric: re-opening a tour someone
    /// has already dismissed is worse than a dismissal they can undo from Settings, which is exactly
    /// what "Show the tour again" is for.</para>
    ///
    /// <para>An upgrade is treated as a first run, and that is the intended reading of "once": a user
    /// upgrading has not seen the tour either. There is deliberately NO version-gated re-show - a tour
    /// that comes back after an upgrade is the thing users hate, and nobody asked for the mechanism.</para>
    /// </summary>
    public static bool ShouldShowTour(bool tourSeen, bool shownThisSession) => !tourSeen && !shownThisSession;

    /// <summary>
    /// The trust statement a new user gets once, as three short badges on the tour's first step, said this
    /// way because "less text" does not mean dropping the only first-run statement of where their data goes
    /// (the architect's ruling, 2026-09-19). Each is a claim about the product, so each is pushed from here
    /// and never typed into the shell, and each holds today: the user's own API key (the OS credential
    /// store), calls that go from their machine to the provider they chose with nobody's server between, and
    /// a fully-local provider that sends nothing off the machine (LlmProviders enforces the egress).
    /// TWO badges, not three (Tharun, 2026-10-25, his second ruling on this line): "Nothing routed through anyone"
    /// overstated it - the calls DO go to the provider the user chose - and its replacement was one claim too many
    /// for a first screen. What a new user needs on the first step is the key and the local option; the fuller
    /// statement of where data goes is the privacy page, linked from Settings.
    /// </summary>
    public static readonly string[] Trust =
    {
      "Bring your own key",
      "A fully-local option",
    };

    /// <summary>The one button on the real consent card (ConsentCard.Render). The tour draws that card, so the
    /// label is typed here - and held equal to the real render by a test in the Agent tests, because two
    /// copies held equal by a test is the shape the architect ruled for it (ConsentCard.cs is another lane's
    /// file and is not touched). DwightScaffold types the same words into the transcript on approval.</summary>
    public const string ApproveLabel = "Yes, go ahead";

    /// <summary>The label on the model's reason, as ConsentCard.Render prints it. Held equal by the same test.</summary>
    public const string ReasonLabel = "Dwight's reason for asking";

    /// <summary>What the tour's last step draws of the waiting card (Brief BG's frame): the act in one sentence, the one plain line and the fold's label,
    /// handed over by the caller from ConsentCard so the picture is the card and cannot drift from it (#315). Plugin-authored: the model's reason is never
    /// part of it.</summary>
    public sealed class ConsentPicture
    {
      public string Act { get; }
      public string Line { get; }
      public string More { get; }
      public ConsentPicture(string act, string line, string more) { Act = act ?? ""; Line = line ?? ""; More = more ?? ""; }
    }

    /// <summary>
    /// Everything the tour surface is allowed to know, as JSON. The shell holds no verb list, no verb
    /// tagline, no trust statement, no consent-card wording and no chord string of its own - a second copy
    /// would be a coincidence that lapses the first time one of them changes - so all of it is pushed from
    /// here. Plugin-authored throughout: no user text and no model text can reach this payload (ADR-088),
    /// which is why the shell can render it directly.
    /// </summary>
    public static string InitJson(IReadOnlyList<DwightVerbs.Verb> verbs, string chord, bool needsKey, string docsUrl, string keysUrl,
                                  ConsentPicture consent = null)
    {
      var list = new JsonArray();
      foreach (var v in verbs ?? Array.Empty<DwightVerbs.Verb>())
        list.Add(new JsonObject { ["name"] = v.Name, ["desc"] = v.Line, ["tag"] = v.Tag ?? "" });

      var trust = new JsonArray();
      foreach (var t in Trust) trust.Add(t);

      return new JsonObject
      {
        ["verbs"] = list,
        ["chord"] = chord ?? "",
        ["needsKey"] = needsKey,
        ["docs"] = docsUrl ?? "",
        ["keys"] = keysUrl ?? "",
        ["trust"] = trust,
        ["consent"] = new JsonObject
        {
          ["act"] = consent?.Act ?? "",
          ["line"] = consent?.Line ?? "",
          ["more"] = consent?.More ?? "",
          ["reasonLabel"] = ReasonLabel,
          ["approve"] = ApproveLabel,
        },
      }.ToJsonString();
    }

    /// <summary>
    /// The tour for a machine with no WebView2 runtime. The Settings panel can fall back to its Eto
    /// twin; the tour has none, and a machine without WebView2 today will not have it next launch
    /// either - so the content goes to the Rhino command line and the tour still counts as shown.
    /// Silence would be the one outcome that is both useless and repeated forever.
    /// </summary>
    public static string CommandLineText(IReadOnlyList<DwightVerbs.Verb> verbs, string chord, bool needsKey)
    {
      var sb = new StringBuilder();
      sb.AppendLine("Dwight - a quick tour (shown once; reopen it any time from Dwight > Settings).");
      sb.AppendLine("  Dwight reads the definition you point it at and answers beside it. The model supplies");
      sb.AppendLine("  judgement; Dwight itself computes every id, coordinate and check - so what it builds can be");
      sb.AppendLine("  verified rather than trusted. Your own API key, your canvas, nobody's server in between.");
      foreach (var v in verbs ?? Array.Empty<DwightVerbs.Verb>())
        sb.AppendLine("  " + v.Name + " - " + v.Line);
      sb.AppendLine("  Reach it with " + chord + " on the canvas, the Dwight top-bar menu, or the Dwight icon (bottom right).");
      // SCOPED, because the universal was false (mantis-38, 2026-09-19): a chord-invoked Reorganise applies with
      // no card and no preview, and a multi-region build is one Ctrl-Z per region. These two sentences hold for
      // every verb; the card promise holds in the chat, and says so.
      sb.AppendLine("  In the chat, nothing changes your canvas without a card naming what it will do.");
      sb.AppendLine("  Dwight checks what it builds, and Ctrl-Z undoes it. Full control, off by default, is the one exception.");
      if (needsKey)
        sb.AppendLine("  Dwight needs an API key before it can answer anything: Dwight > Settings.");
      return sb.ToString();
    }
  }
}
