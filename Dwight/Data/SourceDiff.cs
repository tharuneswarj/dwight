using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Dwight.Data
{
  internal enum DiffKind { Context, Added, Removed, Gap }

  /// <summary>One rendered line of a diff. The numbers are the line's position in the OLD and NEW texts -
  /// null on the side where the line does not exist.</summary>
  internal sealed class DiffLine
  {
    public DiffKind Kind;
    public string Text;
    public int? OldNo;
    public int? NewNo;

    public DiffLine(DiffKind kind, string text, int? oldNo, int? newNo)
    { Kind = kind; Text = text; OldNo = oldNo; NewNo = newNo; }
  }

  /// <summary>
  /// THE DIFF A USER READS BEFORE APPROVING AN EDIT TO THEIR SCRIPT (Phase-9 U20).
  ///
  /// WHAT IT IS FOR, and it is not the consent surface. The card shows the COMPLETE new source, untruncated,
  /// because this is code that will RUN on the user's machine and Phase 7 settled that a script's consent
  /// surface shows everything (the Phase 7 to-script ruling, §4.5). The diff sits above it to ORIENT: which three lines
  /// moved, in a file of ninety. A reader who has the diff knows where to look in the source; a reader with
  /// only the source has to find the change themselves, which on an edit is most of the work.
  ///
  /// SO THE TWO ARE BOUNDED DIFFERENTLY, ON PURPOSE. The source is never cut - the refusal that keeps it
  /// readable lives at generation time, not here. The DIFF is cut, because a diff long enough to need
  /// scrolling has stopped orienting anyone and the source below it is the authority anyway. Every cut says
  /// what it left out (ADR-035), and the counts above it are of the WHOLE diff, never of the shown part -
  /// a summary that counted only what survived truncation would understate the change being approved.
  ///
  /// GH-free (ADR-091): two strings in, a list of lines out.
  /// </summary>
  internal sealed class SourceDiff
  {
    /// <summary>Unchanged lines kept either side of a change, so a hunk can be read in place.</summary>
    public const int Context = 3;

    /// <summary>The default bound on RENDERED lines. Chosen to be a screenful rather than a file: past this
    /// the diff has stopped being an orientation aid, and the full source is on the same card.</summary>
    public const int MaxLines = 120;

    public IReadOnlyList<DiffLine> Lines { get; private set; } = new DiffLine[0];

    /// <summary>Lines added and removed across the WHOLE diff, never only the shown part.</summary>
    public int Added { get; private set; }
    public int Removed { get; private set; }

    /// <summary>True when the two texts are identical after normalisation. An edit that changes nothing is
    /// not a small edit - it is a card with nothing to approve, and the ask refuses it (RR14's inert-no-op
    /// law: an accepted change that no-ops is a FAILED change, not a cheap one).</summary>
    public bool Identical => Added == 0 && Removed == 0;

    public bool Truncated { get; private set; }

    /// <summary>What the cut left out, in words, or null when nothing was cut.</summary>
    public string Omitted { get; private set; }

    /// <summary>
    /// The diff between two sources, line by line.
    ///
    /// BOTH SIDES ARE NORMALISED FIRST, through the same rule the source digest uses, so a line-ending
    /// round-trip does not render as "every line changed". A diff that showed a whole file rewritten
    /// because the user's editor saved CRLF would be worse than no diff at all: it would make the card
    /// unreadable at exactly the moment the card is the safety property.
    /// </summary>
    public static SourceDiff Between(string oldSource, string newSource) =>
      Between(oldSource, newSource, MaxLines);

    public static SourceDiff Between(string oldSource, string newSource, int maxLines)
    {
      var a = Split(SourceToken.Normalise(oldSource));
      var b = Split(SourceToken.Normalise(newSource));

      var all = Walk(a, b);
      var diff = new SourceDiff
      {
        Added = all.Count(l => l.Kind == DiffKind.Added),
        Removed = all.Count(l => l.Kind == DiffKind.Removed),
      };

      var hunked = Hunks(all);
      if (maxLines > 0 && hunked.Count > maxLines)
      {
        var shown = hunked.Take(maxLines).ToList();
        var cut = hunked.Count - shown.Count;
        shown.Add(new DiffLine(DiffKind.Gap, cut + " more diff line" + (cut == 1 ? "" : "s") + " not shown - the full source is below", null, null));
        diff.Lines = shown;
        diff.Truncated = true;
        diff.Omitted = cut + " diff line" + (cut == 1 ? "" : "s") + " not shown";
      }
      else
      {
        diff.Lines = hunked;
      }

      return diff;
    }

    private static string[] Split(string s) =>
      string.IsNullOrEmpty(s) ? new string[0] : s.Split('\n');

    /// <summary>
    /// A longest-common-subsequence walk, which is the standard shape and the right one here: it reports a
    /// change where the change IS, rather than declaring everything after an inserted line to be new. The
    /// table is bounded by the source cap (12,000 characters, so a few hundred lines a side) and this runs
    /// once per card, so the quadratic table is the cheap, obvious implementation rather than a risk.
    /// </summary>
    private static List<DiffLine> Walk(string[] a, string[] b)
    {
      var lcs = new int[a.Length + 1, b.Length + 1];
      for (var i = a.Length - 1; i >= 0; i--)
        for (var j = b.Length - 1; j >= 0; j--)
          lcs[i, j] = string.Equals(a[i], b[j], StringComparison.Ordinal)
            ? lcs[i + 1, j + 1] + 1
            : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

      var lines = new List<DiffLine>();
      int x = 0, y = 0;
      while (x < a.Length && y < b.Length)
      {
        if (string.Equals(a[x], b[y], StringComparison.Ordinal))
        {
          lines.Add(new DiffLine(DiffKind.Context, a[x], x + 1, y + 1));
          x++; y++;
        }
        else if (lcs[x + 1, y] >= lcs[x, y + 1])
        {
          lines.Add(new DiffLine(DiffKind.Removed, a[x], x + 1, null));
          x++;
        }
        else
        {
          lines.Add(new DiffLine(DiffKind.Added, b[y], null, y + 1));
          y++;
        }
      }
      while (x < a.Length) { lines.Add(new DiffLine(DiffKind.Removed, a[x], x + 1, null)); x++; }
      while (y < b.Length) { lines.Add(new DiffLine(DiffKind.Added, b[y], null, y + 1)); y++; }
      return lines;
    }

    /// <summary>
    /// Keep every changed line and <see cref="Context"/> unchanged lines around it; replace each stretch of
    /// untouched code between hunks with one GAP line saying how much was skipped.
    ///
    /// The gap is a LINE rather than a silence, because the alternative - simply not showing those lines -
    /// makes two distant hunks look adjacent, and a user reading a card has no other way to tell.
    /// </summary>
    private static List<DiffLine> Hunks(List<DiffLine> all)
    {
      var keep = new bool[all.Count];
      for (var i = 0; i < all.Count; i++)
      {
        if (all[i].Kind == DiffKind.Context) continue;
        for (var j = Math.Max(0, i - Context); j <= Math.Min(all.Count - 1, i + Context); j++) keep[j] = true;
      }

      var outLines = new List<DiffLine>();
      var skipped = 0;
      for (var i = 0; i < all.Count; i++)
      {
        if (keep[i])
        {
          if (skipped > 0)
          {
            outLines.Add(new DiffLine(DiffKind.Gap, skipped + " unchanged line" + (skipped == 1 ? "" : "s"), null, null));
            skipped = 0;
          }
          outLines.Add(all[i]);
        }
        else skipped++;
      }
      // A trailing run of untouched lines needs no marker: nothing follows it that could be misread as adjacent.
      return outLines;
    }

    /// <summary>The diff as plain text, for a prompt or a log - never for HTML, where the destination does
    /// its own escaping (#335: escaping is a property of the destination, not of the producer).</summary>
    public string ToText()
    {
      var sb = new StringBuilder();
      foreach (var l in Lines)
      {
        switch (l.Kind)
        {
          case DiffKind.Added: sb.Append("+ ").AppendLine(l.Text); break;
          case DiffKind.Removed: sb.Append("- ").AppendLine(l.Text); break;
          case DiffKind.Gap: sb.Append("  ... ").AppendLine(l.Text); break;
          default: sb.Append("  ").AppendLine(l.Text); break;
        }
      }
      return sb.ToString();
    }

    /// <summary>The one-line summary the card's authority sentence carries. Counts the WHOLE diff.</summary>
    public string Summary()
    {
      if (Identical) return "no change";
      var parts = new List<string>();
      if (Added > 0) parts.Add(Added + " line" + (Added == 1 ? "" : "s") + " added");
      if (Removed > 0) parts.Add(Removed + " line" + (Removed == 1 ? "" : "s") + " removed");
      return string.Join(", ", parts);
    }
  }
}
