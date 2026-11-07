namespace Dwight.Agent
{
  /// <summary>
  /// The chat's own text - the greeting and the two notices. CHROME, in the #228 sense: it carries
  /// <c>Src = null</c>, never enters the model transcript, and is the surface speaking as itself.
  ///
  /// WHY IT LIVES HERE RATHER THAN BESIDE THE PANEL THAT SHOWS IT (Defect 3, the WP-4 gate). The
  /// greeting told every user that "the build agent arrives in a later Phase-8 work-package" - and went
  /// on saying it through the whole work-package that shipped the build agent, so the first person to
  /// finish a real build had been told to their face, one message earlier, that it did not exist yet.
  ///
  /// That is #316's class pointed at the HUMAN instead of the model: a claim about our own behaviour
  /// which can go stale in silence, with nothing at the far end to notice. The fix is the same fix -
  /// put the claim where a test can reach it. <see cref="DwightScaffold"/> is GH-coupled and is not
  /// compiled into the test project; this file is GH-free and is, which is the whole point of the move.
  ///
  /// THE RULE FOR ANYONE ADDING TO THIS FILE: a deferral is a dated claim. If a string here promises
  /// that something is coming later, it acquires an owner the day the thing arrives, and nobody is
  /// watching. Say what the surface does today, or say nothing.
  /// </summary>
  internal static class ChatChrome
  {
    /// <summary>
    /// The first thing a new thread says. It names what this surface can actually do now: read the
    /// definition (the seven read tools), sketch a scaffold (<c>propose_scaffold</c>), and build the
    /// regions the user approves (<c>build_region</c>, bounded by <see cref="BuildScope"/>).
    /// </summary>
    public const string Greeting =
      "Tell me what you'd like to build. I can read your definition, sketch a scaffold, and build the " +
      "regions you approve.";

    /// <summary>#303: every agent tool refuses <c>no_document</c> without one, so a chat here can hold a
    /// conversation but cannot do its job. Said at the surface BEFORE the user composes a message, and
    /// again in place of spending a call if they send one anyway.</summary>
    public const string NoDocument =
      "I need an open Grasshopper definition to work on - every one of my tools reads the canvas, so " +
      "there is nothing for me to look at yet. Open an existing definition or create a new file and ask " +
      "me again.";

    public const string NoKey =
      "Add an API key in Dwight &gt; Settings and I can read your definition and answer properly.";
  }
}
