namespace Dwight.Data
{
  /// <summary>
  /// WHAT A PLUGIN IS CALLED, in every payload that names one - one rule, used by get_component's
  /// <c>plugin.library</c> and by the installed-plugins inventory (U23).
  ///
  /// It was inline in <c>ScriptSourceReader.LibraryOf</c>. A second copy beside the inventory is how one plugin
  /// ends up with two names - the model reading "(unnamed plugin)" in one answer and an empty string in the next,
  /// and treating them as two things. GH-free (ADR-091).
  /// </summary>
  internal static class PluginName
  {
    public const string Unnamed = "(unnamed plugin)";

    /// <summary>The library's own name, or <see cref="Unnamed"/> when it gives none.</summary>
    public static string Of(string libraryName) =>
      string.IsNullOrWhiteSpace(libraryName) ? Unnamed : libraryName;
  }
}
