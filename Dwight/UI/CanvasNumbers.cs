using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;

namespace Dwight.UI
{
  /// <summary>
  /// Brief ES unit 1: ONE NUMBER PER COMPONENT. A chat names a component by its registry's id (#191), a verb's result by its run's (ADR-058, left to right by
  /// pivot), and the canvas badge used to show whichever map was pushed last, so a person read "add1 (5)" in the chat and found a "7" on add1 (the
  /// twenty-first's gate, the twenty-third's). Each surface keeps its own numbering, and the canvas wears the numbering of the surface that last wrote words
  /// naming components, lit them, or was turned to. A push replaces, never merges: a number another surface left on the canvas is a number the words a person
  /// is reading do not hold. Drawing only, so there is nothing to undo. GH-free (ADR-091): <see cref="CanvasGlow"/> hands in the lit components and their bounds.
  /// </summary>
  internal static class CanvasNumbers
  {
    private static Func<Guid, int?> _wearing;

    /// <summary>A surface wrote words, or lit components, naming them in <paramref name="numbers"/>: from now the badges wear its numbers, and only its. Null leaves the canvas unnumbered.</summary>
    public static void Wrote(Func<Guid, int?> numbers) => _wearing = numbers;

    /// <summary>A run's numbering, as its labels hold it.</summary>
    public static void Wrote(IReadOnlyDictionary<Guid, int> labels) => Wrote(Of(labels));

    public static void Clear() => _wearing = null;

    /// <summary>Whether the canvas wears any numbering at all.</summary>
    public static bool Numbered => _wearing != null;

    /// <summary>The number the canvas shows on this component, if it shows one.</summary>
    public static int? Number(Guid guid) => _wearing?.Invoke(guid);

    /// <summary>The numbers the canvas wears, for a sentence written against the canvas itself (the Lens's chips).</summary>
    public static Func<Guid, int?> Wearing => _wearing;

    /// <summary>A run's numbering. Copied, so a map the run changes afterwards cannot move a badge.</summary>
    public static Func<Guid, int?> Of(IReadOnlyDictionary<Guid, int> labels)
    {
      if (labels == null) return null;
      var copy = new Dictionary<Guid, int>();
      foreach (var kv in labels) copy[kv.Key] = kv.Value;
      return g => copy.TryGetValue(g, out var n) ? n : (int?)null;
    }

    /// <summary>A chat's numbering: the ids its registry issued, which are the ids its words use. Read, never issued: a component the registry has not numbered has no number here.</summary>
    public static Func<Guid, int?> Of(Dwight.Agent.AgentIdRegistry ids) =>
      ids == null ? null : g => ids.TryGetId(g, out var n) ? n : (int?)null;

    /// <summary>
    /// The composer: "add1 (5)" in <paramref name="numbers"/>, or the bare name where that numbering holds no number for the component. Never a number from
    /// another numbering, and never one made up.
    /// </summary>
    public static string Name(Func<Guid, int?> numbers, string name, Guid guid)
    {
      var n = numbers?.Invoke(guid);
      var tag = n.HasValue ? "(" + n.Value.ToString(CultureInfo.InvariantCulture) + ")" : "";
      if (string.IsNullOrWhiteSpace(name)) return tag;
      return n.HasValue ? name + " " + tag : name;
    }

    /// <summary>
    /// Each lit component's badge, its number and where it is drawn, measured: at the component's top-left corner, above it. One function, so what the Lens
    /// keeps its chips off is what is drawn. A component the canvas's numbering does not hold, or whose bounds cannot be read, has no badge.
    /// </summary>
    public static List<(string Text, RectangleF Rect)> Badges(IEnumerable<Guid> lit, Func<Guid, RectangleF?> boundsOf, Func<string, SizeF> measure)
    {
      var badges = new List<(string, RectangleF)>();
      var seen = new HashSet<Guid>();
      foreach (var id in lit ?? Enumerable.Empty<Guid>())
      {
        if (!seen.Add(id)) continue;
        var num = Number(id);
        if (!num.HasValue) continue;
        var b = boundsOf?.Invoke(id);
        if (b == null) continue;
        var text = num.Value.ToString(CultureInfo.InvariantCulture);
        var size = measure(text);
        var w = Math.Max(size.Width + 6f, 14f);
        var h = size.Height + 2f;
        badges.Add((text, new RectangleF(b.Value.Left - 2f, b.Value.Top - h - 1f, w, h)));
      }
      return badges;
    }
  }
}
