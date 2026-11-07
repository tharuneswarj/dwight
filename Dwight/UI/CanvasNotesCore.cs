using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Dwight.Session;

namespace Dwight.UI
{
  /// <summary>One note's card on the canvas, in document coordinates.</summary>
  internal readonly struct NoteRect
  {
    public NoteRect(string id, double x, double y, double w, double h, bool stale)
    { Id = id; X = x; Y = y; W = w; H = h; Stale = stale; }

    public string Id { get; }
    public double X { get; }
    public double Y { get; }
    public double W { get; }
    public double H { get; }
    public bool Stale { get; }

    public bool Contains(double px, double py) => px >= X && px <= X + W && py >= Y && py <= Y + H;
  }

  /// <summary>
  /// THE DECISIONS OF DRAWING A NOTE (Brief DC, PR 2), pure: which notes are drawn, in what order, whether the canvas is zoomed far enough to draw
  /// any, what a click lands on, and where a new note is anchored. The paint and the mouse hook are the thin shim above this (<c>CanvasNotes</c>), so
  /// the rules that decide anything are tested headlessly and the shim is correct by inspection and read at a sitting (ADR-091).
  ///
  /// <para>BOUNDED. At most <see cref="MaxDrawn"/> cards are drawn, the nearest to the viewport first, and the rest are COUNTED and said ("and N more
  /// notes"), never silently dropped (ADR-035). A card narrower on screen than <see cref="CardMinScreenWidth"/> is drawn as a dot instead (Brief ED,
  /// <see cref="NoteDots"/>), so a zoomed-out canvas shows where every note is and is not carpeted.</para>
  ///
  /// <para>A NOTE NEVER CAPTURES A CLICK GRASSHOPPER WOULD OTHERWISE TAKE. The hit test is the card's own rectangle, or a dot's own circle, and nothing larger:
  /// no halo, no leader line, no padding added for a finger. The one press a note takes is on its own header, which moves it (<see cref="HeaderAt"/>, Brief ED).</para>
  /// </summary>
  internal static class CanvasNotesCore
  {
    public const int MaxDrawn = 40;

    /// <summary>How far below a component a note about it is first placed, in canvas units.</summary>
    public const double FirstGap = 24;

    /// <summary>The narrowest a card is drawn on screen (Brief ED). The plain card is 240 units wide, so it is a card from zoom 0.4, and a wider card's threshold follows from its width.</summary>
    public const double CardMinScreenWidth = 96;

    /// <summary>A card's header, where its tag and its date sit: a press there moves the card.</summary>
    public const double HeaderHeight = 22;

    /// <summary>Whether a card this wide is drawn as a card at this zoom. Below it, the note is a dot.</summary>
    public static bool CardsAtZoom(double zoom, double cardWidth) => cardWidth * zoom >= CardMinScreenWidth;

    /// <summary>The id of the card whose header is under a point, or null. It is the top card under the point and only its header, so a press on one card's body is never a lower card's header.</summary>
    public static string HeaderAt(IReadOnlyList<NoteRect> drawn, double x, double y)
    {
      if (drawn == null) return null;
      foreach (var r in drawn) if (r.Contains(x, y)) return y <= r.Y + HeaderHeight ? r.Id : null;
      return null;
    }

    /// <summary>The chip's words when the cap holds.</summary>
    public static string ChipText(int hidden) => hidden == 1 ? "and 1 more note" : "and " + hidden + " more notes";

    /// <summary>
    /// The cards to draw, nearest to the viewport first, and how many were left out. A card that overlaps the viewport is at distance zero; one
    /// off to the side is as far as its nearest edge; ties break on the card's id, so the choice is the same on every paint.
    /// </summary>
    public static IReadOnlyList<NoteRect> Choose(IEnumerable<NoteRect> all, double viewX, double viewY, double viewW, double viewH, out int hidden, int cap = MaxDrawn)
    {
      var list = (all ?? Enumerable.Empty<NoteRect>()).ToList();
      var ordered = list
        .Select(r => (Rect: r, Distance: DistanceTo(r, viewX, viewY, viewW, viewH)))
        .OrderBy(t => t.Distance)
        .ThenBy(t => t.Rect.Id, StringComparer.Ordinal)
        .Take(Math.Max(0, cap))
        .Select(t => t.Rect)
        .ToList();
      hidden = list.Count - ordered.Count;
      return ordered;
    }

    private static double DistanceTo(NoteRect r, double vx, double vy, double vw, double vh)
    {
      var dx = Math.Max(Math.Max(vx - (r.X + r.W), r.X - (vx + vw)), 0);
      var dy = Math.Max(Math.Max(vy - (r.Y + r.H), r.Y - (vy + vh)), 0);
      return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>The id of the card under a point, or null. <paramref name="drawn"/> is nearest-first, which is also top-first (the nearest is painted last).</summary>
    public static string HitTest(IReadOnlyList<NoteRect> drawn, double x, double y)
    {
      if (drawn == null) return null;
      foreach (var r in drawn) if (r.Contains(x, y)) return r.Id;
      return null;
    }

    /// <summary>
    /// WHERE A NEW NOTE ANCHORS (Brief DC ruling 5): to the FIRST selected component, one note per action; with no selection, to a free point at the
    /// chord's own canvas point. The first of the selection is the first in the order the caller lists it, which is the document's.
    /// </summary>
    public static Guid? AnchorFor(IReadOnlyList<Guid> selection) =>
      selection != null && selection.Count > 0 && selection[0] != Guid.Empty ? selection[0] : (Guid?)null;

    /// <summary>
    /// The text of an Explain step or summary pinned as a note: the step's own sentence as plain text (its markup removed, its white space folded), and
    /// never more than a note holds. Null when nothing is left, so a click on an empty step writes no note.
    /// </summary>
    public static string PlainText(string html)
    {
      var text = System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(html ?? "", "<[^>]+>", " "));
      text = System.Text.RegularExpressions.Regex.Replace(text, "\\s+", " ").Trim();
      if (text.Length == 0) return null;
      if (text.Length <= DwightNotes.MaxChars) return text;
      var cut = text.LastIndexOf(' ', DwightNotes.MaxChars - 3);
      return text.Substring(0, cut > 0 ? cut : DwightNotes.MaxChars - 3) + "...";
    }

    /// <summary>
    /// THE SENTENCE A BUILD RECEIPT'S OFFER WRITES (Brief DC ruling 6): the plugin's own facts about what the build did, and never a word the model
    /// wrote. Its inputs are an ADDRESS, which must be a region address the plugin minted (<see cref="Dwight.Agent.ScaffoldAddress.IsRegionAddress"/>,
    /// so no prose can pass for one), a count and a flag; it has no parameter a model's text could travel in, which a test holds by reflection.
    /// Null for anything that is not a real address, so an offer is never made on a line the plugin cannot vouch for.
    /// </summary>
    public static string ReceiptSentence(string address, int components, bool withErrors)
    {
      if (!Dwight.Agent.ScaffoldAddress.IsRegionAddress(address) || components < 1) return null;
      return "Dwight built " + address + ": " + components + (components == 1 ? " component" : " components") + (withErrors ? ", with errors" : "") + ".";
    }
  }

  /// <summary>
  /// WHAT EACH VERB OFFERS TO PIN (Brief DO, item 7), pure: the note's text from what the run already showed. An offer is one click and never silent. Reorganise's and Simplify's are the plugin's
  /// own facts, and an Explain step is the sentence the person just read. Diagnose's holds what the person read, with who wrote each line: the scan's fact, then the model's reading and its suggestion, each under a label that says whose it is.
  /// </summary>
  internal static class NoteOfferText
  {
    public const string PinWhatChanged = "Pin what changed";
    public const string PinTheCause = "Pin the cause";

    /// <summary>A step's text: its Markdown where the run kept it, or its sentence as plain text on a row from an older build. Never more than a note holds.</summary>
    public static string Step(string markdown, string html)
    {
      if (string.IsNullOrWhiteSpace(markdown)) return CanvasNotesCore.PlainText(html);
      return Bound(markdown.Trim());
    }

    /// <summary>Every step's text and the summary's, from the view a run rendered.</summary>
    public static List<string> Steps(string stepsJson, out string summary)
    {
      summary = null;
      var result = new List<string>();
      try
      {
        var root = System.Text.Json.Nodes.JsonNode.Parse(stepsJson);
        summary = root?["summary"]?.GetValue<string>();
        var steps = root?["steps"]?.AsArray();
        if (steps != null) foreach (var s in steps) result.Add(Step(s?["md"]?.GetValue<string>(), s?["text"]?.GetValue<string>()));
      }
      catch { }
      return result;
    }

    /// <summary>What a Reorganise did, in its own summary's words, without the clause about undoing it, which is true today and stale in a note.</summary>
    public static string Reorganise(string summary)
    {
      var text = (summary ?? "").Trim();
      if (text.Length == 0) return null;
      var undo = text.IndexOf("; one Ctrl-Z", StringComparison.Ordinal);
      return Bound(undo >= 0 ? text.Substring(0, undo).TrimEnd() + "." : text);
    }

    /// <summary>What a Simplify applied, by name, from the view it rendered. Null when nothing was applied.</summary>
    public static string Simplify(string appliedJson)
    {
      try
      {
        var root = System.Text.Json.Nodes.JsonNode.Parse(appliedJson);
        var names = (root?["changes"]?.AsArray() ?? new System.Text.Json.Nodes.JsonArray()).Select(c => c?["text"]?.GetValue<string>()).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        if (names.Count == 0) return null;
        var summary = root?["summary"]?.GetValue<string>() ?? "Simplified.";
        return Bound(summary.Trim() + "\n\n" + string.Join("\n", names.Select(n => "- " + n.Trim().Replace("\n", " "))));
      }
      catch { return null; }
    }

    /// <summary>The root cause as the scan found it, the component and Grasshopper's own message, then the model's reading of it and the fix it suggests, each labelled as the model's. From the view Diagnose rendered: the first chain's root. Null when there is none, so a clean run offers nothing.</summary>
    public static string Diagnose(string faultsJson, out Guid? anchor)
    {
      anchor = null;
      try
      {
        var root = System.Text.Json.Nodes.JsonNode.Parse(faultsJson)?["chains"]?.AsArray()?.FirstOrDefault()?["root"];
        var title = root?["title"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(title)) return null;
        if (Guid.TryParse(root["guid"]?.GetValue<string>(), out var g)) anchor = g;
        var message = root["ghMessage"]?.GetValue<string>();
        var parts = new List<string> { "**Root cause (the scan):** " + title.Trim() + (string.IsNullOrWhiteSpace(message) ? "" : ": " + message.Trim()) };
        var reading = root["causeMd"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(reading)) parts.Add("**The model's reading:** " + reading.Trim());
        var fix = root["fixMd"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(fix)) parts.Add("**Suggested fix (the model's):** " + fix.Trim());
        return Bound(string.Join("\n\n", parts));
      }
      catch { return null; }
    }

    private static string Bound(string text)
    {
      if (text.Length <= DwightNotes.MaxChars) return text;
      var cut = text.LastIndexOf(' ', DwightNotes.MaxChars - 3);
      return text.Substring(0, cut > 0 ? cut : DwightNotes.MaxChars - 3) + "...";
    }
  }

  /// <summary>
  /// WHETHER NOTES ARE ON THE CANVAS (Brief DI), pure. One per-machine setting, read by the dock, Settings and the chord. Hidden means not drawn and not
  /// clickable, and it is about the canvas only: nothing here is read by a payload, so what a run sends does not change. The count stays, so a person knows
  /// notes exist, and one new note shows itself, so a person who adds a note sees it land.
  /// </summary>
  internal static class NotesVisibility
  {
    /// <summary>What the switch is called on all three doors. On the chord it is a ticked item under Notes and never a second wording (Brief EU), so a person sees the state.</summary>
    public const string Label = "Show notes";

    /// <summary>The cards there are to draw. <paramref name="revealedId"/> is the one new note, or null.</summary>
    public static IEnumerable<NoteRect> Visible(IEnumerable<NoteRect> all, bool show, string revealedId) =>
      (all ?? Enumerable.Empty<NoteRect>()).Where(r => show || (revealedId != null && string.Equals(r.Id, revealedId, StringComparison.Ordinal)));

    /// <summary>The chip's words. Hidden, the count of the document's notes; shown, what the cap left out. Null when there is nothing to say.</summary>
    public static string Chip(bool show, int total, int capped)
    {
      if (!show) return total <= 0 ? null : total == 1 ? "1 note" : total + " notes";
      return capped > 0 ? CanvasNotesCore.ChipText(capped) : null;
    }

    /// <summary>A click on the chip shows the notes only when it is the count of hidden ones. The cap's chip says a number and does nothing.</summary>
    public static bool ChipClickShows(bool show, int total) => !show && total > 0;
  }

  /// <summary>
  /// THE ONE NEW NOTE (Brief DI, ruling 4). Added while notes are hidden, it is drawn with the toggle unchanged until the next redraw at least
  /// <see cref="MinSeconds"/> after it first drew. The floor is there because closing the note's own window redraws the canvas, so "the next redraw" alone
  /// could take it away before anyone saw it. There is no timer: with no redraw it simply stays.
  /// </summary>
  internal sealed class NoteReveal
  {
    public const double MinSeconds = 3;

    private string _id;
    private DateTime? _first;

    public void Set(string id) { _id = id; _first = null; }

    public void Clear() { _id = null; _first = null; }

    /// <summary>A paint asks: which note is revealed now? The first paint starts the clock, and a paint past the floor ends it.</summary>
    public string At(DateTime nowUtc)
    {
      if (_id == null) return null;
      if (_first == null) { _first = nowUtc; return _id; }
      if ((nowUtc - _first.Value).TotalSeconds < MinSeconds) return _id;
      Clear();
      return null;
    }
  }

  /// <summary>
  /// THE OFFERS THAT ARE WAITING FOR A CLICK (Brief DC ruling 6): "Pin as a note" on an Explain step and "Pin a note on this" on a Build receipt. An offer
  /// is registered with the exact text a click will write, and the click names it by an opaque token, so the page never carries note text back to the
  /// host and a note is never written without a click. Bounded; the oldest offer is forgotten first.
  /// </summary>
  internal static class NoteOffers
  {
    private const int Max = 200;
    private static readonly object Gate = new object();
    private static readonly List<string> Order = new List<string>();
    private static readonly Dictionary<string, (string Text, Guid? Anchor, string By, string FromRun)> Book = new Dictionary<string, (string, Guid?, string, string)>(StringComparer.Ordinal);

    /// <param name="fromRun">The history row the offer came from (Brief DO, item 8): a run's id, or a chat's thread.</param>
    public static string Register(string text, Guid? anchor, string by, string fromRun)
    {
      if (string.IsNullOrWhiteSpace(text)) return null;
      var token = Guid.NewGuid().ToString("N").Substring(0, 12);
      lock (Gate)
      {
        Book[token] = (text, anchor, DwightNotes.NormaliseBy(by), fromRun);
        Order.Add(token);
        while (Order.Count > Max) { Book.Remove(Order[0]); Order.RemoveAt(0); }
      }
      return token;
    }

    /// <summary>The offer for a token, or false when it is unknown or already forgotten. It is left in the book: a second click writes a second note, which is the person's choice.</summary>
    public static bool TryGet(string token, out string text, out Guid? anchor, out string by, out string fromRun)
    {
      text = null; anchor = null; by = null; fromRun = null;
      lock (Gate)
      {
        if (token == null || !Book.TryGetValue(token, out var o)) return false;
        text = o.Text; anchor = o.Anchor; by = o.By; fromRun = o.FromRun;
        return true;
      }
    }
  }

  /// <summary>One note that draws as a dot: its card's rectangle in document coordinates, its card's top-left corner on screen, and its state.</summary>
  internal readonly struct DotSource
  {
    public DotSource(NoteRect rect, double screenX, double screenY, NoteStatus status) { Rect = rect; ScreenX = screenX; ScreenY = screenY; Status = status; }
    public NoteRect Rect { get; }
    public double ScreenX { get; }
    public double ScreenY { get; }
    public NoteStatus Status { get; }
  }

  /// <summary>How a dot is drawn. The card says a changed or a gone note in amber ink on its own surface, so the dot does: filled for a fresh note, the card's surface ringed in that ink for a changed one, and the ring dashed for a gone one, as the card says "gone".</summary>
  internal enum NoteDotLook { Filled, Hollow, HollowDashed }

  /// <summary>A dot on the overlay, in screen pixels: one note, or several within <see cref="NoteDots.MergeDistance"/> of each other, merged and carrying their count.</summary>
  internal sealed class NoteDot
  {
    public NoteDot(IReadOnlyList<DotSource> members, double x, double y, double diameter) { Members = members; X = x; Y = y; Diameter = diameter; }
    public IReadOnlyList<DotSource> Members { get; }
    public double X { get; }
    public double Y { get; }
    public double Diameter { get; }
    public int Count => Members.Count;
    public bool Merged => Members.Count > 1;
    public IEnumerable<string> Ids => Members.Select(m => m.Rect.Id);
    public NoteDotLook Look => Merged ? NoteDotLook.Filled : NoteDots.LookOf(Members[0].Status);

    /// <summary>Its own circle and nothing larger.</summary>
    public bool Contains(double px, double py) => (px - X) * (px - X) + (py - Y) * (py - Y) <= Diameter * Diameter / 4;
  }

  /// <summary>
  /// A NOTE TOO SMALL TO READ IS A DOT (Brief ED, Tharun: "like Figma's comments"), pure. Every note that is not a card gets one, on the overlay at a constant size at every
  /// zoom, at its card's top-left corner on screen. Dots that would touch merge into one that says how many, and a click on that one zooms in until they part. Bounded at
  /// <see cref="MaxDots"/>; the rest are counted on the chip, as the cards' cap is.
  /// </summary>
  internal static class NoteDots
  {
    public const double Diameter = 10;
    public const double MergeDistance = 14;
    public const int MaxDots = 400;
    /// <summary>A merged dot's count needs this much room either side of its numerals.</summary>
    public const double CountPad = 3;
    public const double MergedMinDiameter = 16;
    /// <summary>A click on a merged dot never zooms past Grasshopper's default zoom.</summary>
    public const double MaxSeparateZoom = 1;

    public static NoteDotLook LookOf(NoteStatus status) =>
      status == NoteStatus.Gone ? NoteDotLook.HollowDashed : status == NoteStatus.Stale ? NoteDotLook.Hollow : NoteDotLook.Filled;

    /// <summary>A merged dot is wide enough for its count.</summary>
    public static double MergedDiameter(double countWidth) => Math.Max(MergedMinDiameter, countWidth + 2 * CountPad);

    /// <summary>
    /// The dots on a screen <paramref name="width"/> by <paramref name="height"/>, from the notes that draw as dots, in the order given. Only those on the screen count, so a
    /// count says how many are here. Two within <see cref="MergeDistance"/> of each other are one dot, and so is a chain of them: the merge is the same whatever the order.
    /// A merged dot sits at its members' middle and is as wide as <paramref name="countWidth"/> says its numerals need.
    /// </summary>
    public static IReadOnlyList<NoteDot> Merge(IEnumerable<DotSource> sources, double width, double height, Func<string, double> countWidth)
    {
      var on = (sources ?? Enumerable.Empty<DotSource>())
        .Where(s => s.ScreenX >= -Diameter && s.ScreenX <= width + Diameter && s.ScreenY >= -Diameter && s.ScreenY <= height + Diameter)
        .ToList();
      var parent = Enumerable.Range(0, on.Count).ToArray();
      int Root(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
      for (var i = 0; i < on.Count; i++)
        for (var j = i + 1; j < on.Count; j++)
        {
          var dx = on[i].ScreenX - on[j].ScreenX;
          var dy = on[i].ScreenY - on[j].ScreenY;
          if (dx * dx + dy * dy > MergeDistance * MergeDistance) continue;
          var a = Root(i); var b = Root(j);
          if (a != b) parent[Math.Max(a, b)] = Math.Min(a, b);
        }

      var dots = new List<NoteDot>();
      foreach (var group in Enumerable.Range(0, on.Count).GroupBy(Root).OrderBy(g => g.Key))
      {
        var members = group.Select(i => on[i]).ToList();
        var x = members.Average(m => m.ScreenX);
        var y = members.Average(m => m.ScreenY);
        var diameter = members.Count == 1 ? Diameter : MergedDiameter(countWidth?.Invoke(members.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)) ?? 0);
        dots.Add(new NoteDot(members, x, y, diameter));
      }
      return dots;
    }

    /// <summary>The dot under a point, or null. The last drawn is on top, so it is asked first.</summary>
    public static NoteDot At(IReadOnlyList<NoteDot> dots, double x, double y)
    {
      if (dots == null) return null;
      for (var i = dots.Count - 1; i >= 0; i--) if (dots[i].Contains(x, y)) return dots[i];
      return null;
    }

    /// <summary>
    /// WHERE A CLICK ON A MERGED DOT TAKES THE CANVAS: the zoom at which the group's two nearest corners are a card's narrowest width apart, so their cards stand side by side
    /// and no longer merge, and never less than the zoom at which every one of them is a card. Capped at <see cref="MaxSeparateZoom"/>, which notes sharing one corner reach,
    /// and never further out than the canvas is now. The point is the middle of the group's cards, so the canvas pans to all of them.
    /// </summary>
    public static (double Zoom, double X, double Y) Separate(IReadOnlyList<NoteRect> group, double zoomNow)
    {
      if (group == null || group.Count == 0) return (zoomNow, 0, 0);
      var nearest = double.PositiveInfinity;
      for (var i = 0; i < group.Count; i++)
        for (var j = i + 1; j < group.Count; j++)
        {
          var dx = group[i].X - group[j].X;
          var dy = group[i].Y - group[j].Y;
          nearest = Math.Min(nearest, Math.Sqrt(dx * dx + dy * dy));
        }
      var cards = group.Max(r => r.W > 0 ? CanvasNotesCore.CardMinScreenWidth / r.W : MaxSeparateZoom);
      var apart = nearest > 0 && !double.IsInfinity(nearest) ? CanvasNotesCore.CardMinScreenWidth / nearest : MaxSeparateZoom;
      var zoom = Math.Max(zoomNow, Math.Min(MaxSeparateZoom, Math.Max(cards, apart)));
      var left = group.Min(r => r.X); var top = group.Min(r => r.Y);
      var right = group.Max(r => r.X + r.W); var bottom = group.Max(r => r.Y + r.H);
      return (zoom, (left + right) / 2, (top + bottom) / 2);
    }
  }

  /// <summary>
  /// THE DOCK'S NOTES PANE (Brief ED, unit 2; Tharun: "like Figma's comments tab"), pure: the file's notes, newest first, each a row the dock draws and a click on which goes
  /// there. Every word is the plugin's, so the shell writes none.
  /// </summary>
  internal static class NotesPane
  {
    public const string Heading = "Notes";
    public const string NewNote = "New note";
    public const string Empty = "No notes in this file yet. Select a component and choose Note here from the chord, or press New note.";
    public const int WordsMax = 80;

    /// <summary>A row's words: the note's first line, its marks read, cut at <see cref="WordsMax"/> with an ellipsis. A table's first row reads as its cells.</summary>
    public static string Words(string text)
    {
      var first = NoteMarkdown.ForModel(text).Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
      var words = NoteMarkdown.Words(first).Trim();
      if (words.Length <= WordsMax) return words;
      var cut = words.LastIndexOf(' ', WordsMax - 3);
      return words.Substring(0, cut > 0 ? cut : WordsMax - 3).TrimEnd() + "...";
    }

    public static string Who(string by) => DwightNotes.NormaliseBy(by) == DwightNotes.ByDwight ? "Dwight" : "you";

    /// <summary>A note's state in the card's words.</summary>
    public static string StateWord(NoteStatus status) => status == NoteStatus.Stale ? "changed since" : status == NoteStatus.Gone ? "gone" : "fresh";

    /// <summary>The canvas point a row's click centres: the card's middle when it draws as a card at this zoom, and its dot's place, the card's corner, when it draws as one.</summary>
    public static (double X, double Y) GoTarget(double x, double y, double w, double h, double zoom) =>
      CanvasNotesCore.CardsAtZoom(zoom, w) ? (x + w / 2, y + h / 2) : (x, y);

    /// <summary>The pane, from the file's notes and what the canvas says of each: the anchor's nickname (null for a free note or a gone one) and its state.</summary>
    public static JsonObject View(IEnumerable<StoredNote> notes, Func<StoredNote, (string Name, NoteStatus Status)> read, DateTime nowUtc)
    {
      var rows = new JsonArray();
      foreach (var n in (notes ?? Enumerable.Empty<StoredNote>()).Where(n => n != null)
                 .OrderByDescending(n => n.WhenUtc ?? "", StringComparer.Ordinal).ThenBy(n => n.Id ?? "", StringComparer.Ordinal))
      {
        var (name, status) = read?.Invoke(n) ?? (null, NoteStatus.Fresh);
        rows.Add(new JsonObject
        {
          ["id"] = n.Id ?? "",
          ["words"] = Words(n.Text),
          ["who"] = Who(n.By),
          ["when"] = HistoryCore.ShortAgo(n.WhenUtc, nowUtc),
          ["about"] = string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
          ["state"] = status == NoteStatus.Stale ? "stale" : status == NoteStatus.Gone ? "gone" : "fresh",
          ["stateWord"] = StateWord(status),
        });
      }
      return new JsonObject
      {
        ["heading"] = Heading,
        ["newNote"] = NewNote,
        ["empty"] = Empty,
        ["show"] = NotesVisibility.Label,
        ["count"] = rows.Count,
        ["rows"] = rows,
      };
    }
  }

  /// <summary>Where a note is put: the fields of its record a move or a pin changes, and nothing else, so it stays the same note (same id, text, author and time).</summary>
  internal sealed class NotePlacement
  {
    public string Anchor { get; set; }
    public double[] At { get; set; }
    public double[] Last { get; set; }
    public string Stamp { get; set; }
  }

  /// <summary>
  /// WHERE A NOTE IS, AND WHERE IT GOES WHEN IT MOVES OR IS PINNED (Brief ED, unit 3; Tharun: "either pin it, or the user can move it"), pure. A pinned note's place is an offset
  /// from its component's corner, so it follows the component; a free note's is its own point. Unpinning and pinning recompute it so the card does not jump.
  ///
  /// <para>THE STAMP MOVES WITH THE ANCHOR, which is the one field past the brief's three. A note re-pinned to another component would otherwise compare that component with the
  /// old one's stamp and say "written before this component changed" of a note pinned a moment ago, a sentence that is false (#297).</para>
  /// </summary>
  internal static class NotePlace
  {
    public const string PinRefusal = "Select the component to pin to.";
    public const string Free = "Free";
    public const string PinnedGone = "Pinned to a component no longer on the canvas";

    public static bool Pinned(StoredNote note) => Guid.TryParse(note?.Anchor, out _);

    /// <summary>The switch's words: "Pinned to" its component's nickname, or "Free".</summary>
    public static string PinLabel(bool pinned, string nickname) =>
      !pinned ? Free : string.IsNullOrWhiteSpace(nickname) ? PinnedGone : "Pinned to " + nickname.Trim();

    private static bool Two(double[] p) => p != null && p.Length == 2 && !double.IsNaN(p[0]) && !double.IsNaN(p[1]);

    /// <summary>The top-left of a note's card: its component's corner (<paramref name="corner"/>, null when the component is not on the canvas) plus its offset, or its last
    /// known point when the component is gone, or its own point when it is free.</summary>
    public static (double X, double Y)? Origin(StoredNote note, (double X, double Y)? corner)
    {
      if (note == null) return null;
      if (Pinned(note))
      {
        if (corner != null && Two(note.At)) return (corner.Value.X + note.At[0], corner.Value.Y + note.At[1]);
        var last = note.Last ?? note.At;
        return Two(last) ? (last[0], last[1]) : ((double, double)?)null;
      }
      return Two(note.At) ? (note.At[0], note.At[1]) : ((double, double)?)null;
    }

    /// <summary>A note dragged so its card's corner is at (<paramref name="x"/>, <paramref name="y"/>). A pinned note keeps its component and takes a new offset from it; a
    /// free note takes the point; a note whose component is gone keeps its offset, so it goes back to its component if the component comes back, and is drawn here meanwhile.</summary>
    public static NotePlacement Moved(StoredNote note, double x, double y, (double X, double Y)? corner)
    {
      var at = !Pinned(note) ? new[] { x, y }
             : corner != null ? new[] { x - corner.Value.X, y - corner.Value.Y }
             : note.At == null ? null : (double[])note.At.Clone();
      return new NotePlacement { Anchor = note.Anchor, At = at, Last = new[] { x, y }, Stamp = note.Stamp };
    }

    /// <summary>A note made free where it stands.</summary>
    public static NotePlacement Unpinned(double x, double y) => new NotePlacement { Anchor = null, At = new[] { x, y }, Last = new[] { x, y }, Stamp = null };

    /// <summary>A note pinned to a component whose corner is <paramref name="corner"/>, where it stands, taking that component's stamp now.</summary>
    public static NotePlacement PinnedTo(Guid anchor, (double X, double Y) corner, double x, double y, string stamp) =>
      new NotePlacement { Anchor = anchor.ToString(), At = new[] { x - corner.X, y - corner.Y }, Last = new[] { x, y }, Stamp = string.IsNullOrEmpty(stamp) ? null : stamp };

    public static void Apply(StoredNote note, NotePlacement place)
    {
      if (note == null || place == null) return;
      note.Anchor = place.Anchor;
      note.At = place.At;
      note.Last = place.Last;
      note.Stamp = place.Stamp;
    }
  }

  /// <summary>
  /// A NOTE DRAGGED BY ITS HEADER (Brief ED, unit 3), pure. Grasshopper's canvas owns the mouse, so the shim hands this the press, the moves and the release, and this decides
  /// where the card is and whether anything is written: nothing until the pointer has gone past <see cref="Slop"/> pixels, the same as a click's, and then one write when it is
  /// let go, never one per move and never one for a drag that was abandoned.
  /// </summary>
  internal sealed class NoteDrag
  {
    /// <summary>A press and release within this many pixels is a click.</summary>
    public const int Slop = 3;

    public static bool IsDrag(int dx, int dy) => Math.Abs(dx) > Slop || Math.Abs(dy) > Slop;

    private readonly Action<string, double, double> _write;
    private readonly double _originX, _originY, _pressX, _pressY;
    private bool _done;

    public NoteDrag(string noteId, double originX, double originY, double pressX, double pressY, Action<string, double, double> write)
    {
      NoteId = noteId; _originX = originX; _originY = originY; _pressX = pressX; _pressY = pressY; _write = write;
      X = originX; Y = originY;
    }

    public string NoteId { get; }
    /// <summary>Where the card's corner is now, in document coordinates.</summary>
    public double X { get; private set; }
    public double Y { get; private set; }
    public bool Moved { get; private set; }
    public bool Done => _done;

    /// <param name="canvasX">The pointer, in document coordinates.</param>
    /// <param name="pixelsX">How far it is from the press, in screen pixels.</param>
    public void Move(double canvasX, double canvasY, int pixelsX, int pixelsY)
    {
      if (_done) return;
      if (!Moved && !IsDrag(pixelsX, pixelsY)) return;
      Moved = true;
      X = _originX + canvasX - _pressX;
      Y = _originY + canvasY - _pressY;
    }

    /// <summary>Let go: the one write, when it moved. True when it wrote.</summary>
    public bool Drop()
    {
      if (_done) return false;
      _done = true;
      if (!Moved) return false;
      _write?.Invoke(NoteId, X, Y);
      return true;
    }

    /// <summary>Ended without a release (the canvas lost the mouse): nothing is written and the card goes back.</summary>
    public void Abandon() => _done = true;
  }
}
