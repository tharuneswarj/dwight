using System.Linq;

namespace Dwight.Agent
{
  /// <summary>
  /// Brief DX unit 8, Tharun's ruling of 2 November: DWIGHT CANNOT SEE THE SCREEN, AND SAYS SO. The prompt tells the model; this makes the sentence true in every state (#297), because a model
  /// can forget and a plugin line cannot. A short list of phrases, the mechanism of the announcement catch (U97). It reads the person's words before the turn runs and stops nothing: a model
  /// asked to look at the canvas still reads it with its tools, and the person learns what those reads are not.
  ///
  /// GH-free (ADR-091).
  /// </summary>
  internal static class ScreenAsk
  {
    /// <summary>The person's line. The same words are in known-limitations.md, and a test holds the two together.</summary>
    public const string Line = "I cannot see your screen. Take a screenshot and attach it with the paper-clip or Ctrl+V and I will read it.";

    /// <summary>The model's half: the report door puts it in the user turn, so the model does not ask again what the plugin just asked.</summary>
    public const string Fact = "[Dwight told the person, in its own voice: " + Line + "]";

    /// <summary>The person asking to be looked at, by the words they use for it. "look at this component" is not here: a tool reads a component.</summary>
    private static readonly string[] Phrases =
    {
      "look at my screen", "look at the screen", "look at my canvas", "look at the canvas", "look at my viewport", "look at the viewport", "look at my rhino", "look at rhino",
      "see my screen", "see the screen", "see my canvas", "see the canvas", "see my viewport", "see the viewport",
      "what's on my screen", "what is on my screen", "what's on the screen", "what is on the screen", "what is in the viewport", "what's in the viewport",
      "what do you see", "take a screenshot", "take a look at my screen", "take a look at the canvas", "take a look at the viewport", "screenshot the",
    };

    /// <summary>The same small list the announcement catch reads a sentence against: "don't look at the canvas" is not asking for a look.</summary>
    private static readonly string[] Negations = { "don't", "do not", "dont", "no need to", "without looking" };

    /// <summary>Does this turn ask Dwight to look, with nothing attached to look at?</summary>
    public static bool Asks(string text, bool imageAttached)
    {
      if (imageAttached || string.IsNullOrWhiteSpace(text)) return false;
      foreach (var sentence in text.ToLowerInvariant().Replace((char)0x2019, '\'').Split('.', '!', '?', ';', '\n'))
      {
        if (!Phrases.Any(p => sentence.Contains(p))) continue;
        if (Negations.Any(n => sentence.Contains(n))) continue;
        return true;
      }
      return false;
    }
  }
}
