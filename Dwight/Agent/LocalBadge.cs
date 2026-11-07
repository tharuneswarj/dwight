namespace Dwight.Agent
{
  /// <summary>
  /// WHAT THE SETTINGS PANEL'S PRIVACY BADGE SAYS (U63/U64 follow-up). It was a static "Fully local. Nothing leaves your
  /// machine.", which stops being true the moment web search is on with Ollama local: the query goes to ollama.com with the
  /// user's Ollama key. A badge is an assertion about the current state, so it is computed from both facts. GH-free (ADR-091).
  ///
  /// The first-run tour's "A fully-local option" badge describes the option, not the current state, and is unchanged.
  /// </summary>
  internal static class LocalBadge
  {
    public const string FullyLocal = "Fully local. Nothing leaves your machine.";
    public const string LocalExceptSearch = "Fully local, except web search: queries go to ollama.com with your Ollama key.";

    /// <summary>The badge text, or "" when the provider is not local (no badge).</summary>
    public static string Text(bool isLocal, bool webSearchEnabled) =>
      !isLocal ? "" : webSearchEnabled ? LocalExceptSearch : FullyLocal;

    public const string ModelsOnKey = "Models on your key";
    public const string ModelsOnMachine = "Models on this machine";

    /// <summary>
    /// Brief FN: the heading over Connection's list of models. It said "Models on your key" for every provider, and Ollama on this machine has no
    /// key: its models are the ones pulled here. The heading is a claim about the provider in force, so it is chosen from the same fact as the badge.
    /// </summary>
    public static string ModelsHeading(bool isLocal) => isLocal ? ModelsOnMachine : ModelsOnKey;
  }
}
