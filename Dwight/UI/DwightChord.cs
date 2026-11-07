using System.Collections.Generic;
using System.Windows.Forms;

namespace Dwight.UI
{
  /// <summary>
  /// The invocation chord (ADR-047): Ctrl+Shift+Right-click, and nothing else.
  ///
  /// The modifiers are declared ONCE and the printed label is DERIVED from them, so a surface that
  /// teaches the user the chord cannot drift from the handler that tests it. Before this, the chord
  /// existed as a <c>const Keys</c> inside the mouse handler and as English prose in a doc comment;
  /// a first-run tour that taught a third copy of the prose would have been the one a rename forgets.
  /// Deriving it makes the agreement structural instead of a coincidence someone has to maintain.
  /// </summary>
  internal static class DwightChord
  {
    /// <summary>The modifier keys that must be held. The single source for both the guard and the label.</summary>
    public const Keys Modifiers = Keys.Control | Keys.Shift;

    /// <summary>True when the chord's modifiers are all held in <paramref name="current"/>.</summary>
    public static bool Held(Keys current) => (current & Modifiers) == Modifiers;

    /// <summary>The chord as a user reads it, e.g. "Ctrl+Shift+Right-click" - computed from
    /// <see cref="Modifiers"/>, never typed out beside it.</summary>
    public static string Label => Describe(Modifiers) + "+Right-click";

    private static string Describe(Keys m)
    {
      var parts = new List<string>();
      if ((m & Keys.Control) == Keys.Control) parts.Add("Ctrl");
      if ((m & Keys.Shift) == Keys.Shift) parts.Add("Shift");
      if ((m & Keys.Alt) == Keys.Alt) parts.Add("Alt");
      return string.Join("+", parts);
    }
  }
}
