using System;
using System.Security.Cryptography;
using System.Text;

namespace Dwight.Data
{
  /// <summary>
  /// A BOUNDED, NORMALISED DIGEST OF A SCRIPT COMPONENT'S SOURCE (#779).
  ///
  /// THE DEFECT IT CLOSES, and it was live on `main` rather than waiting on a future unit.
  /// <see cref="BehaviourFingerprint"/>'s value token recognised sliders, panels, toggles, swatches and
  /// value lists and returned null for everything else - so a script component contributed NOTHING to it,
  /// and therefore nothing to <c>AsLeftRecord</c>, which composes that token. <c>build_region</c> already
  /// places any installed type, script components included; it simply cannot give them source. So Dwight
  /// could place an empty Python component, the user could write two hundred lines into it, and a rebuild
  /// would read that component as OURS AND UNTOUCHED and put it in the removal set. The removal is not
  /// silent - <c>RebuildPreview</c> names the component on the card - which makes it a CONSENT defect
  /// rather than a silent deletion: the card is true and unhelpful at the one moment it matters, because
  /// it names "Python 3 Script" and not that it holds the user's afternoon.
  ///
  /// A SOURCE EDIT IS A CHANGE IN WHAT THE DEFINITION COMPUTES, so the blindness was a defect in the
  /// fingerprint's OWN question, not only in the as-left record's. That is why the digest lives in the
  /// token set rather than in a check beside it: a guard that cannot see a change blesses every future
  /// writer that forgets to look elsewhere (#208's class).
  ///
  /// THE NORMALISATION IS STATED BECAUSE IT IS LOAD-BEARING. Line endings are collapsed to \n and the
  /// whole text is trimmed BEFORE hashing, so a CRLF round-trip - a save, a copy between editors, a git
  /// checkout with different autocrlf - does not read as a behaviour change. A guard that cries wolf on
  /// whitespace is a guard people learn to override.
  ///
  /// THE DIGEST IS BOUNDED AND THE SOURCE NEVER TRAVELS. The output is a fixed-width hash whatever the
  /// script's length, so a 12,000-character script costs the token set the same as a one-liner, and the
  /// user's code is never carried in a token that ends up in a payload, a record or a log.
  ///
  /// GH-free (ADR-091): a string in, a string out.
  /// </summary>
  internal static class SourceToken
  {
    /// <summary>
    /// The token for a component that IS a script but whose source could not be read - a locked or
    /// encrypted script, or a Rhino whose script API has moved.
    ///
    /// IT IS DELIBERATELY STABLE, and that is a decision about the FINGERPRINT, not about the as-left
    /// record. The fingerprint asks "does this definition still compute the same thing?", and Reorganise
    /// answers it by requiring a BYTE-IDENTICAL token before and after. An unstable marker here would
    /// abort Reorganise on any definition containing one locked script - breaking a working feature to
    /// report a change nobody made. So the fingerprint keeps the honest, stable answer "a script whose
    /// source we could not read", exactly as blind as it was before and no blinder.
    ///
    /// The as-left record's question is the opposite one and gets the opposite answer: see
    /// <c>FpObject.ScriptSourceUnreadable</c> and <c>AsLeftRecord.Of</c>, where an unreadable script
    /// yields a NULL token that compares as CHANGED, so the component is left alone. Two questions, two
    /// failure directions, one read - which is the shape #680 earned.
    /// </summary>
    public const string Unreadable = "script:?";

    /// <summary>
    /// THE WHOLE DECISION, for a component already known to be a script: what token does this read
    /// deserve? It exists as its own function because the alternative - the same two lines inline in the
    /// Grasshopper reader - is untestable, and this project has been bitten by exactly that (#612): the
    /// first version of these tests asserted the digest beside the reader and stayed green when the
    /// reader was reverted.
    ///
    /// An empty script is a READ, and gets a real token. A failed read is not, and gets the stable marker.
    /// </summary>
    /// <param name="readSucceeded">Did a read actually happen, whatever it returned.</param>
    /// <param name="source">What it returned; may be empty, and is ignored when the read failed.</param>
    public static string ForScript(bool readSucceeded, string source) =>
      readSucceeded ? Of(source ?? "") : Unreadable;

    /// <summary>The token for a source we could read. Null in, null out - a caller that has no source is
    /// not a caller with an empty one.</summary>
    public static string Of(string source)
    {
      if (source == null) return null;
      var normalised = Normalise(source);
      using (var sha = SHA256.Create())
        return "script:" + Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(normalised)));
    }

    /// <summary>The normalisation, in one place so the rule can be asserted rather than described: CRLF
    /// and lone CR become LF, then the whole text is trimmed. Nothing else - indentation inside the
    /// script is meaningful in Python and is never touched.</summary>
    public static string Normalise(string source) =>
      (source ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim();
  }
}
