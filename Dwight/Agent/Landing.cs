using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Dwight.Agent
{
  /// <summary>What the search for a place reads: the footprint of what is landing, every box on the canvas, and what it is anchored to.</summary>
  internal sealed class LandingAsk
  {
    public double W, H;

    /// <summary>Every object on the canvas as it stands, measured by <c>GhRegionTidy.CanvasBoxes</c>. A named group's own box is left out when landing in it, and its members are not.</summary>
    public IReadOnlyList<SpaceBox> Canvas = new SpaceBox[0];

    /// <summary>The group this lands in, or null. It is the first anchor: the user named it.</summary>
    public SpaceBox Into;

    /// <summary>Brief DK: what a rebuild of this place left standing. They keep their places, and it starts beside them, because that is where the person left this work.</summary>
    public IReadOnlyList<SpaceBox> Kept = new SpaceBox[0];

    /// <summary>Existing components that feed what is landing. It starts to their right, as a definition reads.</summary>
    public IReadOnlyList<SpaceBox> FedBy = new SpaceBox[0];

    /// <summary>Existing components what is landing feeds. With nothing feeding it, it starts to their left.</summary>
    public IReadOnlyList<SpaceBox> Feeding = new SpaceBox[0];

    /// <summary>The centre of the user's view, or null when there is no canvas to read.</summary>
    public TidyPoint? ViewCentre;
  }

  /// <summary>
  /// WHERE A PLACEMENT LANDS, AND THE WORDS FOR IT (Brief CM; ADR-056: the plugin computes where things go).
  ///
  /// Tharun's metric: on a pre-existing file, can the user go and find what was built? The night's real files said no: a slider 153 units past the
  /// drawing's edge at 5 percent zoom with the view unmoved, a three-component check on top of existing components, 3 of 3 overlapping, and a sentence that
  /// said "Spot N" with no direction. The old rule took the rightmost pivot and added 220, which measured no box.
  ///
  /// THE ANCHOR ORDER: a group the user named, then what a rebuild kept (Brief DK), then the components it wires to, then the user's view, then the drawing's right edge. From there the search is
  /// <see cref="RegionSpacing.TryFindClear"/>, the same search and the same margin a grown region takes, over every box on the canvas. If nothing is clear
  /// within its reach, past the drawing's right edge is clear by construction, so a placement never lands on existing work.
  ///
  /// GH-free (ADR-091): the shim measures, this decides.
  /// </summary>
  internal static class Landing
  {
    /// <summary>Where a landing starts inside a named group's box, clear of its edge.</summary>
    private const double Inset = 20;

    /// <summary>The verdict's field for <see cref="Where"/>'s sentence.</summary>
    public const string VerdictKey = "landed";

    /// <summary>What <see cref="Where"/> calls <see cref="LandingAsk.Kept"/>.</summary>
    public const string KeptLabel = "what this spot kept";

    /// <summary>Where the top-left of the footprint goes. Never over a box on <see cref="LandingAsk.Canvas"/>, and never within <see cref="RegionSpacing.Gap"/> of one.</summary>
    public static TidyPoint Find(LandingAsk ask)
    {
      var canvas = ask.Canvas ?? new SpaceBox[0];
      var start = Start(ask, canvas);
      var box = new SpaceBox("", start.X, start.Y, ask.W, ask.H, SpaceRole.Fixed);

      // Inside the group it was named into if there is room, however near a place outside it is. Only then does the group grow.
      if (ask.Into != null && RegionSpacing.TryFindClear(box, canvas, out var inside, ask.Into)) return new TidyPoint(start.X + inside.X, start.Y + inside.Y);
      if (RegionSpacing.TryFindClear(box, canvas, out var offset)) return new TidyPoint(start.X + offset.X, start.Y + offset.Y);

      var drawing = Extent(canvas);
      return drawing == null ? start : new TidyPoint(drawing.Right + RegionSpacing.Gap, start.Y);
    }

    private static TidyPoint Start(LandingAsk ask, IReadOnlyList<SpaceBox> canvas)
    {
      if (ask.Into != null) return new TidyPoint(ask.Into.X + Inset, ask.Into.Y + Inset);

      if (ask.Kept != null && ask.Kept.Count > 0)
      {
        var kept = Extent(ask.Kept);
        return new TidyPoint(kept.Right + RegionSpacing.Gap, kept.Y);
      }

      if (ask.FedBy != null && ask.FedBy.Count > 0)
        return new TidyPoint(ask.FedBy.Max(b => b.Right) + RegionSpacing.Gap, ask.FedBy.Min(b => b.Y));
      if (ask.Feeding != null && ask.Feeding.Count > 0)
        return new TidyPoint(ask.Feeding.Min(b => b.X) - RegionSpacing.Gap - ask.W, ask.Feeding.Min(b => b.Y));

      if (ask.ViewCentre.HasValue) return new TidyPoint(ask.ViewCentre.Value.X - ask.W / 2, ask.ViewCentre.Value.Y - ask.H / 2);

      var drawing = Extent(canvas);
      return drawing == null ? new TidyPoint(0, 0) : new TidyPoint(drawing.Right + RegionSpacing.Gap, drawing.Y);
    }

    /// <summary>The box round a set of boxes, or null for none.</summary>
    public static SpaceBox Extent(IEnumerable<SpaceBox> boxes)
    {
      var all = (boxes ?? new SpaceBox[0]).Where(b => b != null).ToList();
      if (all.Count == 0) return null;
      var x = all.Min(b => b.X);
      var y = all.Min(b => b.Y);
      return new SpaceBox("", x, y, all.Max(b => b.Right) - x, all.Max(b => b.Bottom) - y, SpaceRole.Fixed);
    }

    /// <summary>
    /// WHERE IT WENT, IN PLAIN WORDS WITH A NUMBER: "60 units to the right of Radius", "to the right of the drawing, 150 units past its edge", "inside the view".
    /// It reads the view as it was before the landing was framed, because after that every landing is inside the view and the words would say nothing.
    /// </summary>
    public static string Where(SpaceBox landed, IReadOnlyList<SpaceBox> canvas, SpaceBox view = null, string anchorLabel = null, SpaceBox anchor = null)
    {
      if (anchor != null && !string.IsNullOrWhiteSpace(anchorLabel))
      {
        var near = Clearance(landed, anchor);
        if (near.Gap > 0) return Units(near.Gap) + " units " + near.Side + " " + anchorLabel.Trim();
      }

      var inView = view != null && Inside(landed, view);
      var drawing = Extent(canvas);
      if (drawing == null) return inView ? "inside the view" : "with nothing else on the canvas";

      var past = Clearance(landed, drawing);
      if (past.Gap > 0) return past.Side + " the drawing, " + Units(past.Gap) + " units past its edge";
      if (inView) return "inside the view";
      return "inside the drawing, " + Units(landed.X - drawing.X) + " units from its left edge";
    }

    /// <summary>Where it went in a group it was named into, against the box the group had before it grew.</summary>
    public static string InGroup(SpaceBox landed, SpaceBox group)
    {
      var over = new[] { group.X - landed.X, group.Y - landed.Y, landed.Right - group.Right, landed.Bottom - group.Bottom }.Max();
      return over <= 0 ? "inside its bounds" : "reaching " + Units(over) + " units past its old edge, so the group grew to hold it";
    }

    /// <summary>The line for a run's built regions, after the tidy has moved them: "Built A1 inside the view. Built A2 to the right of the drawing, 150 units past its edge."</summary>
    public static string RunLine(IEnumerable<(string Address, string Where)> regions)
    {
      var lines = (regions ?? new (string, string)[0]).Select(r => "Built " + r.Address + " " + r.Where + ".").ToList();
      return lines.Count == 0 ? null : string.Join(" ", lines);
    }

    /// <summary>The verdict carries the sentence, so what the report says is what the build measured.</summary>
    public static JsonObject Stamped(JsonObject verdict, string where)
    {
      if (verdict != null && !string.IsNullOrWhiteSpace(where)) verdict[VerdictKey] = where;
      return verdict;
    }

    public static string Read(JsonNode verdict)
    {
      try { return verdict?[VerdictKey]?.GetValue<string>(); }
      catch { return null; }
    }

    private static (string Side, double Gap) Clearance(SpaceBox box, SpaceBox from) => new[]
    {
      (Side: "to the right of", Gap: box.X - from.Right),
      (Side: "to the left of", Gap: from.X - box.Right),
      (Side: "below", Gap: box.Y - from.Bottom),
      (Side: "above", Gap: from.Y - box.Bottom),
    }.OrderByDescending(s => s.Gap).First();

    private static bool Inside(SpaceBox box, SpaceBox outer) =>
      box.X >= outer.X && box.Y >= outer.Y && box.Right <= outer.Right && box.Bottom <= outer.Bottom;

    private static string Units(double value) => Math.Round(value).ToString("N0", CultureInfo.InvariantCulture);
  }

  /// <summary>A group on the canvas, as a request can name it: by its address, by the name after the address, or by its whole name.</summary>
  internal sealed class NamedGroup
  {
    public Guid Guid;
    public string NickName;
    public string Address;
    public string Label;

    /// <summary>What a card and a report call it.</summary>
    public string Display => !string.IsNullOrEmpty(Address) ? Address : Label;

    /// <summary>Null for a group nobody named: an empty name, or Grasshopper's own default.</summary>
    public static NamedGroup Of(Guid guid, string nickName)
    {
      var nick = (nickName ?? "").Trim();
      if (nick.Length == 0 || string.Equals(nick, "Group", StringComparison.OrdinalIgnoreCase)) return null;
      return ScaffoldAddress.FromGroupName(nick, out var address, out var label)
        ? new NamedGroup { Guid = guid, NickName = nick, Address = address, Label = label }
        : new NamedGroup { Guid = guid, NickName = nick, Label = nick };
    }
  }

  /// <summary>
  /// A NAMED DESTINATION LANDS THERE OR IS REFUSED (Brief CM). On the teaching file a request to add to "my answer key region" was built as a new spot group,
  /// and the sentence said where it went as if that were what was asked. <see cref="Resolve"/> is the model naming one, <see cref="NamedIn"/> is the user doing so.
  /// </summary>
  internal static class RegionNames
  {
    /// <summary>How many names a refusal lists. A real file can hold hundreds of groups.</summary>
    private const int MaxListed = 12;

    /// <summary>Exactly one group by that name, or the refusal that says why not, with the names that exist. Never another group, and never a new one (#208).</summary>
    public static bool Resolve(string asked, IReadOnlyList<NamedGroup> groups, out NamedGroup group, out ToolResult error)
    {
      group = null;
      error = null;
      var name = (asked ?? "").Trim();
      var all = groups ?? new NamedGroup[0];
      var matches = name.Length == 0 ? new List<NamedGroup>() : all.Where(g => Answers(g, name)).ToList();
      if (matches.Count == 1) { group = matches[0]; return true; }

      if (matches.Count > 1)
      {
        error = ToolResult.Fail(ToolErrors.UnknownRegion,
          "More than one group on this canvas is called \"" + name + "\", so it does not name one place. Nothing was offered. Ask the user which one they mean.");
        return false;
      }

      var names = all.Select(g => g.Display).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList();
      var listed = names.Count == 0
        ? "This canvas has no named groups."
        : "The ones here are: " + string.Join(", ", names.Take(MaxListed)) + (names.Count > MaxListed ? ", and " + (names.Count - MaxListed) + " more" : "") + ".";
      error = ToolResult.Fail(ToolErrors.UnknownRegion,
        "\"" + name + "\" is not a region or group on this canvas, so nothing can land in it. " + listed +
        " Name one of them in \"region\" to add to it, or leave \"region\" out and Dwight will pick a clear place.");
      return false;
    }

    /// <summary>
    /// The group the user's own words name as a destination, or null. Only ever a name that is on the canvas, and only where the words can mean nothing else: a name
    /// of two words or more, or an address or a one-word name right beside "region", "group" or "section", or straight after "into" or "inside". A bare address or a
    /// common word is not enough: "put a1 beside it" and "add a slider to the grid" are for the model to judge, and a refusal here would cost a build the person wanted.
    /// </summary>
    public static NamedGroup NamedIn(string text, IReadOnlyList<NamedGroup> groups)
    {
      if (string.IsNullOrWhiteSpace(text) || groups == null) return null;
      foreach (var g in groups.Where(g => g != null).OrderByDescending(g => (g.Label ?? "").Length))
      {
        if (IsPhrase(g.Label) && HasPhrase(text, g.Label)) return g;
        if (HasDestination(text, g.Label) || HasDestination(text, g.Address)) return g;
      }
      return null;
    }

    /// <summary>The refusal when the user named a place and the model did not. It holds both doors: the model judges which the user meant, and the card says so either way.</summary>
    public static ToolResult UserNamedRefusal(NamedGroup group) =>
      ToolResult.Fail(ToolErrors.InvalidArgs,
        "The user's message names \"" + group.Display + "\", which is on this canvas, and this placement would make a new place instead. To add to it, set " +
        "\"region\": \"" + group.Display + "\" in \"place\". If they do want something apart from it, set \"separate\": true, and the card will say it is not in " + group.Display + ".");

    private static bool Answers(NamedGroup g, string name) =>
      string.Equals(g.Address, name, StringComparison.OrdinalIgnoreCase) ||
      string.Equals(g.Label, name, StringComparison.OrdinalIgnoreCase) ||
      string.Equals(g.NickName, name, StringComparison.OrdinalIgnoreCase);

    private static bool IsPhrase(string name) => !string.IsNullOrWhiteSpace(name) && name.Trim().IndexOf(' ') > 0;

    private static bool HasPhrase(string text, string name) =>
      Regex.IsMatch(text, @"(?<![\w])" + Regex.Escape(name.Trim()) + @"(?![\w])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool HasDestination(string text, string name)
    {
      if (string.IsNullOrWhiteSpace(name)) return false;
      var n = Regex.Escape(name.Trim());
      return Regex.IsMatch(text,
        @"\b(?:region|group|section)\s+" + n + @"(?![\w])|(?<![\w])" + n + @"\s+(?:region|group|section)\b|\b(?:into|inside)\s+(?:the\s+|my\s+|our\s+)?" + n + @"(?![\w])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
  }
}
