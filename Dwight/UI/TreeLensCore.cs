using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using Dwight.Agent;

namespace Dwight.UI
{
  /// <summary>The live numbers on one wire, read by the host on Rhino's thread, outside a solution (U47, U48).</summary>
  internal sealed class WireCounts
  {
    /// <summary>The items on the wire: Grasshopper's own DataCount on the source output.</summary>
    public long Total;
    /// <summary>False when the source component has not solved yet.</summary>
    public bool Solved = true;
    /// <summary>Grasshopper's own first error on the source component, verbatim, or null.</summary>
    public string SourceError;
    /// <summary>The count on the list a filter takes (<see cref="TreeWords.IsFilter"/>), or null when the source is no filter.</summary>
    public long? OfSource;
  }

  /// <summary>Where a wire's noun can come from: the upstream nickname the person gave it, the output's name, and the output's type hint.</summary>
  internal sealed class WireSource
  {
    public string Nick;
    public string OutputName;
    public string TypeHint;
  }

  /// <summary>The shape a chip's glyph draws: one item, one list, a tree, or no glyph where the wire carries nothing.</summary>
  internal enum ChipShape { None, Item, List, Tree }

  /// <summary>
  /// Brief DB: WHAT TRAVELS ON A WIRE, IN WORDS THE PLUGIN TEMPLATES FROM THE TREE SIGNATURE AND THE LIVE COUNTS. No model call anywhere in this unit, so it is
  /// exact, instant and free. A sentence the facts do not support is not written: the signature lists the first 24 branches only (<c>TreeSig.ItemsCap</c>), so past
  /// that the words fall back on the live total. GH-free (ADR-091).
  /// </summary>
  internal static class TreeWords
  {
    public const string Nothing = "nothing";
    public const string NoDataYet = "no data yet";
    public const string Stopped = "stopped: ";
    public const string ItemWord = "item";
    public const string ItemsWord = "items";

    private const int MaxErrorWords = 6;
    private const int MaxErrorChars = 48;

    /// <summary>The plugin's table of what a wire can be called. A name gives a noun only in the plural, because a singular name is too often a component's own
    /// ("Panel" is the yellow text box); a type hint gives one either way.</summary>
    private static readonly (string One, string Many)[] Nouns =
    {
      ("panel", "panels"), ("point", "points"), ("number", "numbers"), ("curve", "curves"), ("surface", "surfaces"), ("brep", "breps"),
      ("mesh", "meshes"), ("plane", "planes"), ("vector", "vectors"), ("boolean", "booleans"), ("text", "texts"), ("colour", "colours"), ("domain", "domains"),
    };

    /// <summary>Type hints Grasshopper spells another way than the table.</summary>
    private static readonly Dictionary<string, string> HintAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
      ["integer"] = "number",
      ["color"] = "colour",
    };

    /// <summary>Components that pass on part of the list they take, unchanged, so a smaller count on their output is a part "of" that list. The list is their first
    /// input. Bounds and List Length are not here: their output is a new thing, and "1 of 96" would say something false about it.</summary>
    private static readonly HashSet<string> Filters = new HashSet<string>(StringComparer.Ordinal)
    {
      "Dispatch", "Cull Pattern", "Cull Index", "Cull Nth", "List Item", "Shift List", "Sift Pattern", "Split List", "Sub List", "Random Reduce",
    };

    public static bool IsFilter(string type) => type != null && Filters.Contains(type);

    /// <summary>The chip's words for one wire.</summary>
    public static string For(TreeSigDto signature, WireCounts counts, WireSource source)
    {
      counts = counts ?? new WireCounts();
      if (!string.IsNullOrWhiteSpace(counts.SourceError)) return Stopped + FirstWords(counts.SourceError);
      if (!counts.Solved) return NoDataYet;
      if (signature == null || signature.Paths <= 0 || counts.Total <= 0) return Nothing;

      // The count on the wire is the count on the wire; "of 96" is the list the filter took.
      if (counts.OfSource.HasValue && counts.Total < counts.OfSource.Value) return N(counts.Total) + " of " + N(counts.OfSource.Value);

      return signature.Paths == 1 ? Count(counts.Total, source) : Lists(signature, counts.Total);
    }

    /// <summary>The glyph for a wire that carries something.</summary>
    public static ChipShape ShapeOf(TreeSigDto signature, long total)
    {
      if (signature == null || signature.Paths <= 0 || total <= 0) return ChipShape.None;
      if (signature.Paths > 1) return ChipShape.Tree;
      return total == 1 ? ChipShape.Item : ChipShape.List;
    }

    /// <summary>"96 panels", "1 surface", or "96 items" where nothing gives a noun.</summary>
    public static string Count(long total, WireSource source)
    {
      var noun = NounOf(source);
      if (noun == null) return N(total) + " " + (total == 1 ? ItemWord : ItemsWord);
      return N(total) + " " + (total == 1 ? noun.Value.One : noun.Value.Many);
    }

    private static string Lists(TreeSigDto s, long total)
    {
      var known = s.Items ?? Array.Empty<int>();
      if (known.Length == 0) return N(s.Paths) + " lists, " + N(total) + " " + ItemsWord;

      var min = known.Min();
      var max = known.Max();
      var everyBranch = known.Length == s.Paths;

      // Past the first 24 branches the signature says nothing, so equal counts are only claimed where the live total agrees with them.
      if (min == max && (everyBranch || total == s.Paths * (long)min)) return N(s.Paths) + " lists of " + N(min);
      if (everyBranch) return N(s.Paths) + " lists, " + N(min) + " to " + N(max) + " each";
      return N(s.Paths) + " lists, " + N(total) + " " + ItemsWord;
    }

    private static (string One, string Many)? NounOf(WireSource source)
    {
      if (source == null) return null;
      return Plural(source.Nick) ?? Plural(source.OutputName) ?? FromHint(source.TypeHint);
    }

    private static (string One, string Many)? Plural(string name)
    {
      foreach (var word in Words(name))
        foreach (var n in Nouns)
          if (string.Equals(word, n.Many, StringComparison.OrdinalIgnoreCase)) return n;
      return null;
    }

    private static (string One, string Many)? FromHint(string hint)
    {
      foreach (var word in Words(hint))
      {
        var one = HintAliases.TryGetValue(word, out var alias) ? alias : word;
        foreach (var n in Nouns)
          if (string.Equals(one, n.One, StringComparison.OrdinalIgnoreCase) || string.Equals(one, n.Many, StringComparison.OrdinalIgnoreCase)) return n;
      }
      return null;
    }

    private static IEnumerable<string> Words(string text)
    {
      if (string.IsNullOrWhiteSpace(text)) yield break;
      var start = -1;
      for (var i = 0; i <= text.Length; i++)
      {
        var letter = i < text.Length && char.IsLetter(text[i]);
        if (letter && start < 0) start = i;
        if (!letter && start >= 0) { yield return text.Substring(start, i - start); start = -1; }
      }
    }

    /// <summary>Grasshopper's own first words, verbatim, one line, short enough for a chip. Only the length is ours.</summary>
    public static string FirstWords(string message)
    {
      var line = (message ?? "").Trim();
      var cut = line.IndexOfAny(new[] { '\r', '\n' });
      if (cut >= 0) line = line.Substring(0, cut).TrimEnd();
      var words = string.Join(" ", line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).Take(MaxErrorWords));
      return words.Length <= MaxErrorChars ? words : words.Substring(0, MaxErrorChars).TrimEnd();
    }

    private static string N(long n) => n.ToString("n0", CultureInfo.InvariantCulture);
  }

  // ---- where it stops ---------------------------------------------------------------------------------------------------------------------------

  /// <summary>One output of a component, as the read found it.</summary>
  internal sealed class LensOutput
  {
    public Guid Guid;
    public string Port;
    public long Count;
    public int Recipients;
    public WireSource Source;
  }

  /// <summary>What feeds one input: the component it comes from and the items on that wire.</summary>
  internal sealed class LensFeed
  {
    public Guid Node;
    public string Label;
    public long Count;
  }

  /// <summary>One input of a component, as the read found it.</summary>
  internal sealed class LensInput
  {
    public string Port;
    public string Name;
    public bool Optional;
    public long Count;
    public List<LensFeed> Feeds = new List<LensFeed>();
  }

  /// <summary>One component the lens read. <see cref="Label"/> is what a person finds on the canvas: its nickname or type, and its badge number where it has one.</summary>
  internal sealed class LensNode
  {
    public Guid Guid;
    public string Type;
    public string Label;
    public List<LensOutput> Outputs = new List<LensOutput>();
    public List<LensInput> Inputs = new List<LensInput>();
    public List<string> Errors = new List<string>();
    public List<string> Warnings = new List<string>();
  }

  /// <summary>One line in the plugin's voice, and what a click on it selects: the components it names. The port is ringed on the canvas, because items themselves cannot be selected.</summary>
  internal sealed class LensStop
  {
    public string Text;
    public List<Guid> Select = new List<Guid>();
    public Guid Port;
    /// <summary>What a click does, in words: "Select Dispatch (23)". Null on a line with nothing to select.</summary>
    public string Action;
  }

  /// <summary>
  /// Brief DB: WHERE DATA STOPS, READ OFF THE FACTS AND SAID IN THE PLUGIN'S WORDS, with Grasshopper's own words forwarded where it said something (#235, as Diagnose forwards them).
  ///
  /// A DEAD-END OUTPUT IS ONLY SAID OF A SPLITTER. Nearly every component has an output nothing reads: Move's transform, Area's centroid, the last component of every chain. A line
  /// about each would be true and useless, and a lens that cries wolf gets ignored. On a component that divides its list between its outputs, an unread output is items that go nowhere.
  /// </summary>
  internal static class TreeStops
  {
    public const int MaxLines = 5;

    /// <summary>Components that divide what they take between their outputs, so an output nothing reads is part of the list going nowhere.</summary>
    private static readonly HashSet<string> Splitters = new HashSet<string>(StringComparer.Ordinal)
    {
      "Dispatch", "Sift Pattern", "Split List", "Split Tree",
    };

    public static bool IsSplitter(string type) => type != null && Splitters.Contains(type);

    /// <summary>Every line for these components, in their order, bounded, with the last saying how many it did not name.</summary>
    public static List<LensStop> Find(IEnumerable<LensNode> nodes)
    {
      var all = new List<LensStop>();
      foreach (var n in nodes ?? Enumerable.Empty<LensNode>())
      {
        if (n == null) continue;
        foreach (var e in n.Errors.Take(1)) all.Add(Line(n.Label + " stopped: " + TreeWords.FirstWords(e), n));
        DeadEnds(n, all);
        Starved(n, all);
      }
      if (all.Count <= MaxLines) return all;

      var kept = all.Take(MaxLines).ToList();
      var rest = all.Count - MaxLines;
      kept.Add(new LensStop { Text = "And " + rest + " more " + (rest == 1 ? "line" : "lines") + " like these in what is lit." });
      return kept;
    }

    private static void DeadEnds(LensNode n, List<LensStop> into)
    {
      if (!IsSplitter(n.Type)) return;
      foreach (var o in n.Outputs)
      {
        if (o.Count <= 0 || o.Recipients > 0) continue;
        into.Add(new LensStop
        {
          Text = TreeWords.Count(o.Count, o.Source) + " leave port " + o.Port + " of " + n.Label + ", and port " + o.Port + " connects to nothing.",
          Select = new List<Guid> { n.Guid },
          Port = o.Guid,
          Action = "Select " + n.Label,
        });
      }
    }

    private static void Starved(LensNode n, List<LensStop> into)
    {
      if (n.Warnings.Count == 0) return;
      foreach (var i in n.Inputs)
      {
        var says = " Grasshopper says: " + WarningFor(n, i);
        if (i.Feeds.Count == 0)
        {
          if (!i.Optional && i.Count <= 0) into.Add(Line(n.Label + ", input " + i.Port + " has no source." + says, n));
          continue;
        }
        foreach (var f in i.Feeds)
          if (f.Count <= 0)
            into.Add(new LensStop
            {
              Text = "Nothing reaches input " + i.Port + " of " + n.Label + " from " + f.Label + "." + says,
              Select = new List<Guid> { f.Node, n.Guid },
              Action = "Select " + f.Label + " and " + n.Label,
            });
      }
    }

    /// <summary>The warning that names this input if one does, or the component's first. Forwarded whole.</summary>
    private static string WarningFor(LensNode n, LensInput i) =>
      (n.Warnings.FirstOrDefault(w => Names(w, i.Name) || Names(w, i.Port)) ?? n.Warnings[0]).Trim();

    /// <summary>The whole word only: a port called D is not named by the d in "failed".</summary>
    private static bool Names(string text, string word)
    {
      if (string.IsNullOrEmpty(word)) return false;
      for (var at = text.IndexOf(word, StringComparison.OrdinalIgnoreCase); at >= 0; at = text.IndexOf(word, at + 1, StringComparison.OrdinalIgnoreCase))
      {
        var end = at + word.Length;
        if ((at == 0 || !char.IsLetterOrDigit(text[at - 1])) && (end == text.Length || !char.IsLetterOrDigit(text[end]))) return true;
      }
      return false;
    }

    private static LensStop Line(string text, LensNode node) => new LensStop { Text = text, Select = new List<Guid> { node.Guid }, Action = "Select " + node.Label };
  }

  /// <summary>What a surface shows of the lens: the lines under the current step, or above the steps when none is current, and the cap's sentence. Every word is already written, so the shell only places it.</summary>
  internal static class LensView
  {
    /// <param name="live">A lens is live, so the surfaces offer Every wire, the same toggle as T. A key is a shortcut and never the only door. With no lens the field is absent and the button goes.</param>
    public static string Json(int step, IReadOnlyList<LensStop> stops, string note, bool live = false, bool showsAll = false)
    {
      var lines = new JsonArray();
      foreach (var s in stops ?? new List<LensStop>())
      {
        var o = new JsonObject { ["text"] = s.Text };
        if (!string.IsNullOrEmpty(s.Action)) o["action"] = s.Action;
        lines.Add(o);
      }
      var view = new JsonObject { ["step"] = step, ["lines"] = lines };
      if (live) view["everywire"] = showsAll;
      if (!string.IsNullOrEmpty(note)) view["note"] = note;
      return view.ToJsonString();
    }
  }

  // ---- where a chip goes -------------------------------------------------------------------------------------------------------------------------

  /// <summary>A rectangle in document units, free of System.Drawing so its rules can be tested without it.</summary>
  internal readonly struct LensRect
  {
    public readonly double X, Y, W, H;
    public LensRect(double x, double y, double w, double h) { X = x; Y = y; W = w; H = h; }
    public double Right => X + W;
    public double Bottom => Y + H;
    public bool Overlaps(LensRect o) => X < o.Right && o.X < Right && Y < o.Bottom && o.Y < Bottom;
  }

  /// <summary>Brief EB: which wires end at the selection's edge. Anything with a far end outside it: its chip may say what the wire carries, and it is never lit and never leaves a band through the wash.</summary>
  internal static class LensEdge
  {
    public static bool Leaves(ICollection<Guid> selection, Guid fromOwner, Guid toOwner) =>
      selection != null && selection.Count > 0 && !(selection.Contains(fromOwner) && selection.Contains(toOwner));
  }

  /// <summary>
  /// A chip sits at its wire's midpoint when the wire can hold it. THE FIFTEENTH GATE: A CHIP NEVER COVERS A STEP'S BADGE, A PORT'S LABEL OR ANOTHER CHIP, AND A CHIP BELONGS TO ITS WIRE. Four short wires into one component
  /// stacked over its badge, on one trunk that hid which wire was which, so <see cref="Seat"/> starts from the wire's own ends. <see cref="ClearOf"/>'s stack is the last resort. What to keep off is measured bounds: the plugin
  /// computes this, and the search always ends somewhere clear.
  /// </summary>
  internal sealed class ChipWire
  {
    public double FromX, FromY, ToX, ToY;
    public LensRect FromBox, ToBox;
    /// <summary>Is this end in the selection? A chip never sits at an end that is not: a wire that leaves the selection ends at its edge.</summary>
    public bool FromSelected, ToSelected;
    /// <summary>Points along the wire as it is drawn, source to destination, from <see cref="ChipPlace.WirePoints"/>.</summary>
    public IReadOnlyList<(double X, double Y)> Points;
  }

  internal static class ChipPlace
  {
    public const double Margin = 6;
    public const double Lift = 10;
    public const double StackGap = 3;
    public const int MaxStack = 6;

    /// <summary>How far a chip beside a wire's end may lie from that end. A tall component's top is no place for a chip about a port halfway down it.</summary>
    public const double Reach = 40;

    private const int WireSteps = 16;

    /// <summary>Along the wire, the places tried when neither end has room: off the middle, where short wires share a trunk.</summary>
    private static readonly int[] AlongSteps = { 4, 12, 2, 14, 6, 10 };

    /// <summary>Points along the Bezier the wire is drawn with, source to destination.</summary>
    public static IReadOnlyList<(double X, double Y)> WirePoints(double ax, double ay, double c1x, double c1y, double c2x, double c2y, double bx, double by)
    {
      var points = new List<(double, double)>(WireSteps + 1);
      for (var i = 0; i <= WireSteps; i++)
      {
        var t = i / (double)WireSteps;
        var u = 1 - t;
        double On(double p0, double p1, double p2, double p3) => u * u * u * p0 + 3 * u * u * t * p1 + 3 * u * t * t * p2 + t * t * t * p3;
        points.Add((On(ax, c1x, c2x, bx), On(ay, c1y, c2y, by)));
      }
      return points;
    }

    /// <summary>
    /// WHERE A CHIP GOES. On its wire's middle if the wire holds it. Else beside the wire's source grip, when the source is in the selection. Where the destination's own body stands against that grip, the far side of the
    /// destination, level with the wire's row (the sixteenth gate), and its line crosses the body to reach the wire. Else beside its destination grip, when that is in the selection; else along the wire, off the middle. Only
    /// for a wire whose ends and length are all crowded does it fall back on <see cref="ClearOf"/>'s stack. Never covers what is placed or <paramref name="keepOff"/>, whichever way it ends.
    /// </summary>
    public static LensRect Seat(ChipWire wire, double w, double h, IEnumerable<LensRect> placed, IEnumerable<LensRect> keepOff)
    {
      var chips = (placed ?? Enumerable.Empty<LensRect>()).ToList();
      var off = (keepOff ?? Enumerable.Empty<LensRect>()).ToList();
      bool Free(LensRect r) => !chips.Any(o => o.Overlaps(r)) && !off.Any(o => o.Overlaps(r));

      var inside = wire.FromSelected && wire.ToSelected;
      var lifted = false;
      var start = inside
        ? Place(wire.FromX, wire.FromY, wire.ToX, wire.ToY, wire.FromBox, wire.ToBox, w, h, out lifted)
        : AtEdge(wire.FromSelected ? wire.FromX : wire.ToX, wire.FromSelected ? wire.FromY : wire.ToY, wire.FromSelected, w, h);
      if (inside && !lifted && Free(start)) return start;

      if (wire.FromSelected)
        foreach (var r in BesideSource(wire, w, h))
          if (Distance(r, wire.FromX, wire.FromY) <= Reach && Free(r)) return r;
      if (inside && AtEdge(wire.FromX, wire.FromY, true, w, h).Overlaps(wire.ToBox))
      {
        var far = new LensRect(wire.ToBox.Right + Margin, wire.FromY - h / 2, w, h);
        if (Free(far)) return far;
      }
      if (wire.ToSelected)
        foreach (var r in BesideDestination(wire, w, h))
          if (Distance(r, wire.ToX, wire.ToY) <= Reach && Free(r)) return r;
      if (inside && wire.Points != null && wire.Points.Count > WireSteps)
        foreach (var i in AlongSteps)
        {
          var r = new LensRect(wire.Points[i].X - w / 2, wire.Points[i].Y - h / 2, w, h);
          if (Free(r)) return r;
        }

      return ClearOf(start, chips, off, inside ? OtherSide(start, lifted, wire.FromBox, wire.ToBox) : (double?)null);
    }

    private static IEnumerable<LensRect> BesideSource(ChipWire wire, double w, double h)
    {
      yield return AtEdge(wire.FromX, wire.FromY, true, w, h);
      yield return new LensRect(wire.FromX - w, wire.FromBox.Y - StackGap - h, w, h);
      yield return new LensRect(wire.FromX - w, wire.FromBox.Bottom + StackGap, w, h);
    }

    private static IEnumerable<LensRect> BesideDestination(ChipWire wire, double w, double h)
    {
      yield return AtEdge(wire.ToX, wire.ToY, false, w, h);
      yield return new LensRect(wire.ToX, wire.ToBox.Y - StackGap - h, w, h);
      yield return new LensRect(wire.ToX, wire.ToBox.Bottom + StackGap, w, h);
    }

    /// <summary>How far a point lies from a chip, 0 when it is on it.</summary>
    public static double Distance(LensRect chip, double x, double y)
    {
      var (ex, ey) = LeaderEnd(chip, x, y);
      return Math.Sqrt((x - ex) * (x - ex) + (y - ey) * (y - ey));
    }

    /// <summary>How far a bend stands off the corner it goes round.</summary>
    private const double BendClear = 3;

    /// <summary>
    /// THE LINE FROM A CHIP TO ITS OWN WIRE, as the points to draw through. A chip without a line is a chip without a meaning (the sixteenth gate), so every chip not already on or beside its wire has one, and null means only
    /// that it needs none. Straight to the wire's nearest point it can reach without crossing another chip or a port's label; it may cross a component's body. Failing that, the shortest route with one bend. A crowd that leaves
    /// no such route still gets the straight line, because a line that crosses something tells the person more than none.
    /// </summary>
    public static IReadOnlyList<(double X, double Y)> Leader(LensRect chip, IReadOnlyList<(double X, double Y)> wire, IEnumerable<LensRect> otherChips, IEnumerable<LensRect> labels = null)
    {
      if (wire == null || wire.Count == 0) return null;
      var avoid = (otherChips ?? Enumerable.Empty<LensRect>()).Concat(labels ?? Enumerable.Empty<LensRect>()).ToList();
      bool Clear((double X, double Y) a, (double X, double Y) b) => !avoid.Any(o => Crosses(o, a.X, a.Y, b.X, b.Y));

      var nearest = wire.OrderBy(q => Distance(chip, q.X, q.Y)).ToList();
      if (Distance(chip, nearest[0].X, nearest[0].Y) <= Margin) return null;

      foreach (var p in nearest)
      {
        var from = LeaderEnd(chip, p.X, p.Y);
        if (Clear(from, p)) return new[] { from, p };
      }

      IReadOnlyList<(double X, double Y)> best = null;
      var bestLength = double.MaxValue;
      foreach (var p in nearest)
      {
        var from = LeaderEnd(chip, p.X, p.Y);
        var bends = new List<(double X, double Y)> { (p.X, from.Y), (from.X, p.Y) };
        foreach (var o in avoid.Where(q => Crosses(q, from.X, from.Y, p.X, p.Y)))
        {
          bends.Add((o.X - BendClear, o.Y - BendClear));
          bends.Add((o.Right + BendClear, o.Y - BendClear));
          bends.Add((o.X - BendClear, o.Bottom + BendClear));
          bends.Add((o.Right + BendClear, o.Bottom + BendClear));
        }
        foreach (var bend in bends)
        {
          var start = LeaderEnd(chip, bend.X, bend.Y);
          if (!Clear(start, bend) || !Clear(bend, p)) continue;
          var length = Length(start, bend) + Length(bend, p);
          if (length < bestLength) { bestLength = length; best = new[] { start, bend, p }; }
        }
      }
      return best ?? new[] { LeaderEnd(chip, nearest[0].X, nearest[0].Y), nearest[0] };
    }

    private static double Length((double X, double Y) a, (double X, double Y) b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>Where a port's label is drawn: its words, centred in the port's own row, as Grasshopper draws them.</summary>
    public static LensRect LabelIn(LensRect row, double textWidth, double textHeight) =>
      new LensRect(row.X + (row.W - textWidth) / 2, row.Y + (row.H - textHeight) / 2, textWidth, textHeight);

    /// <summary>Does this straight line pass through the rectangle? Clipped against each side in turn.</summary>
    public static bool Crosses(LensRect r, double x1, double y1, double x2, double y2)
    {
      double t0 = 0, t1 = 1, dx = x2 - x1, dy = y2 - y1;
      bool Clip(double p, double q)
      {
        if (p == 0) return q >= 0;
        var t = q / p;
        if (p < 0) { if (t > t1) return false; if (t > t0) t0 = t; }
        else { if (t < t0) return false; if (t < t1) t1 = t; }
        return true;
      }
      return Clip(-dx, x1 - r.X) && Clip(dx, r.Right - x1) && Clip(-dy, y1 - r.Y) && Clip(dy, r.Bottom - y1);
    }

    public static LensRect Place(double fromX, double fromY, double toX, double toY, LensRect fromBox, LensRect toBox, double w, double h, out bool lifted)
    {
      var midX = (fromX + toX) / 2;
      var midY = (fromY + toY) / 2;
      var atMid = new LensRect(midX - w / 2, midY - h / 2, w, h);
      lifted = Math.Abs(toX - fromX) < w + 2 * Margin || atMid.Overlaps(fromBox) || atMid.Overlaps(toBox);
      return lifted ? new LensRect(midX - w / 2, Math.Min(fromBox.Y, toBox.Y) - Lift - h, w, h) : atMid;
    }

    /// <summary>Brief EB: a wire that leaves the selection ends at its edge, so its chip sits beside the selected component's own grip, on the side the wire leaves by, and nowhere near the far end.</summary>
    public static LensRect AtEdge(double gripX, double gripY, bool outgoing, double w, double h) =>
      new LensRect(outgoing ? gripX + Margin : gripX - Margin - w, gripY - h / 2, w, h);

    /// <summary>Where the other side of the wire starts for this chip: under the two components when it had lifted over them, else the next row down.</summary>
    public static double OtherSide(LensRect at, bool lifted, LensRect fromBox, LensRect toBox) =>
      lifted ? Math.Max(fromBox.Bottom, toBox.Bottom) + Lift : at.Bottom + StackGap;

    /// <summary>The point on a chip nearest its wire, where the line from the chip to the wire starts.</summary>
    public static (double X, double Y) LeaderEnd(LensRect chip, double wireX, double wireY) =>
      (Math.Max(chip.X, Math.Min(wireX, chip.Right)), Math.Max(chip.Y, Math.Min(wireY, chip.Bottom)));

    /// <summary>
    /// Where the chip goes, clear of the chips already placed and of <paramref name="keepOff"/>. It stacks upward. A chip that starts on something it must keep off moves up past it; a stack of chips that reaches such a thing stops
    /// there and goes on from <paramref name="otherSide"/>, downward. Then along the wire, out to each side. A crowd past all of that goes above everything here, which nothing can be on. Bounded, so a paint can never loop.
    /// </summary>
    public static LensRect ClearOf(LensRect chip, IEnumerable<LensRect> placed, IEnumerable<LensRect> keepOff = null, double? otherSide = null)
    {
      var chips = (placed ?? Enumerable.Empty<LensRect>()).ToList();
      var off = (keepOff ?? Enumerable.Empty<LensRect>()).ToList();
      bool Free(LensRect r) => !chips.Any(o => o.Overlaps(r)) && !off.Any(o => o.Overlaps(r));
      LensRect At(double x, double y) => new LensRect(x, y, chip.W, chip.H);

      var row = chip.H + StackGap;
      var onChips = false;
      var under = new List<LensRect>();
      for (var k = 0; k < MaxStack; k++)
      {
        var up = At(chip.X, chip.Y - k * row);
        if (Free(up)) return up;
        var hit = off.Where(o => o.Overlaps(up)).ToList();
        if (onChips && hit.Any(o => !under.Contains(o))) break;   // climbing over chips, this row has just reached it
        onChips |= chips.Any(o => o.Overlaps(up));
        under = hit;
      }

      var below = otherSide ?? chip.Y + row;
      for (var k = 0; k < MaxStack; k++)
      {
        var down = At(chip.X, below + k * row);
        if (Free(down)) return down;
      }

      var column = chip.W + StackGap;
      for (var j = 1; j <= MaxStack; j++)
        foreach (var x in new[] { chip.X - j * column, chip.X + j * column })
          foreach (var y in new[] { chip.Y, below })
          {
            var along = At(x, y);
            if (Free(along)) return along;
          }

      return At(chip.X, chips.Concat(off).Min(o => o.Y) - row);
    }
  }

  // ---- how many chips, and the keys ---------------------------------------------------------------------------------------------------------------

  /// <summary>Which chips are asked for: the current step's, or every wire of the selection.</summary>
  internal enum LensMode { Step, All }

  /// <summary>What a key on the canvas does to a live lens.</summary>
  internal enum LensKeyAction { None, Toggle, Hide }

  /// <summary>
  /// HOW MANY CHIPS. More than <see cref="MaxInView"/> wires in view have no room for words, so past it only the current step's draw, whatever was asked for.
  /// The sentence says what would change it, which is zooming in: the count is of the wires in view, and pressing T again would only go back.
  /// </summary>
  internal static class LensCap
  {
    public const int MaxInView = 40;

    public const string StepOnlyLine = "Chips on this step only: more than 40 wires are in view. Zoom in for the rest.";
    public const string NoChipsLine = "No chips: more than 40 wires are in view. Zoom in, or pick a step.";

    /// <summary>All were asked for and the cap holds.</summary>
    public static bool Capped(LensMode mode, int wiresInView) => mode == LensMode.All && wiresInView > MaxInView;

    /// <summary>Whether a wire draws. A step's own wire always does; another only when all were asked for and the cap does not hold.</summary>
    public static bool Draws(bool inStep, LensMode mode, int wiresInView) => inStep || (mode == LensMode.All && wiresInView <= MaxInView);

    /// <summary>The sentence when the cap holds, true whether or not a step is current. Null when it does not hold.</summary>
    public static string Line(LensMode mode, int wiresInView, bool hasStep) =>
      !Capped(mode, wiresInView) ? null : hasStep ? StepOnlyLine : NoChipsLine;
  }

  /// <summary>
  /// THE KEYS, a pure decision. T toggles and Esc hides, on a bare key, while a lens is live and nothing else claims the key. With no live result a key is returned unhandled, whatever it
  /// is: the hook is attached only while a result is live, and this holds it even if a detach were ever missed. Esc is only taken while chips are showing, so it stays free for everything else.
  /// </summary>
  internal static class LensKeys
  {
    public const string ToggleKey = "T";
    public const string HideKey = "Escape";

    /// <param name="canvasActive">The canvas holds the keys. With a text box active the letter is the person's typing, and it changes nothing here.</param>
    public static LensKeyAction Dispatch(string key, bool live, bool modifiers, bool claimed, bool chipsShowing, bool canvasActive)
    {
      if (!live || !canvasActive || modifiers || claimed) return LensKeyAction.None;
      if (string.Equals(key, ToggleKey, StringComparison.Ordinal)) return LensKeyAction.Toggle;
      if (string.Equals(key, HideKey, StringComparison.Ordinal) && chipsShowing) return LensKeyAction.Hide;
      return LensKeyAction.None;
    }

    /// <summary>T goes between the step's chips and all of them.</summary>
    public static LensMode Toggled(LensMode mode) => mode == LensMode.All ? LensMode.Step : LensMode.All;
  }
}
