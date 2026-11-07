using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Dwight.UI
{
  /// <summary>
  /// WHERE ONE WALK IS (Brief DA, Tharun's ruling of 1 November 2026). GH-free and Eto-free (ADR-091). Every control and every key goes through this, so the panel's step click
  /// and the bar's dot are one call. A move that cannot happen returns false, and the host then does nothing and says nothing.
  ///
  /// A WALK ARRIVES BEFORE ITS FIRST STEP (Brief DH): a finished Explain is live with no step, so its controls work and the canvas has not changed. <see cref="Next"/> from there is step 1, and nothing goes back to it.
  /// </summary>
  internal sealed class WalkState
  {
    private readonly IReadOnlyList<IReadOnlyList<Guid>> _steps;

    public WalkState(IReadOnlyList<IReadOnlyList<Guid>> steps, IReadOnlyList<Guid> source)
    {
      _steps = steps ?? Array.Empty<IReadOnlyList<Guid>>();
      Source = source ?? Array.Empty<Guid>();
      Live = _steps.Count > 0;
    }

    /// <summary>The whole selection, which a finished Walk leaves glowing.</summary>
    public IReadOnlyList<Guid> Source { get; }
    public int Count => _steps.Count;

    /// <summary>The step showing, or -1 while the Walk has arrived and no step has been asked for.</summary>
    public int Index { get; private set; } = -1;
    public bool Live { get; private set; }
    public bool Started => Live && Index >= 0;
    public IReadOnlyList<Guid> Current => Started ? _steps[Index] : Array.Empty<Guid>();

    public bool Next() => Move(Index + 1);
    public bool Back() => Move(Index - 1);

    /// <summary>The current step is a valid target: the click means "take me there", and the view may have moved since.</summary>
    public bool GoTo(int i) => Move(i);

    public bool Leave()
    {
      if (!Live) return false;
      Live = false;
      return true;
    }

    private bool Move(int i)
    {
      if (!Live || i < 0 || i >= _steps.Count) return false;
      Index = i;
      return true;
    }
  }

  /// <summary>
  /// STEP ORDER IS THE PLUGIN'S (ADR-056). A step comes before another when any of its components feeds any of the other's through the wires and not the reverse. The model's
  /// order settles ties and cycles, so this never invents an order the wires do not state. A step with no components has no place in the flow, so it stays where the model put it.
  /// </summary>
  internal static class WalkOrder
  {
    /// <summary>The order to walk the steps in, as indices into <paramref name="steps"/>.</summary>
    public static int[] Sort(IReadOnlyList<IReadOnlyCollection<Guid>> steps, IEnumerable<(Guid From, Guid To)> wires)
    {
      var n = steps?.Count ?? 0;
      var order = new int?[n];
      var placed = new List<int>();
      for (var i = 0; i < n; i++)
        if (steps[i] == null || steps[i].Count == 0) order[i] = i;
        else placed.Add(i);

      var next = new Dictionary<Guid, List<Guid>>();
      foreach (var (from, to) in wires ?? Enumerable.Empty<(Guid, Guid)>())
      {
        if (!next.TryGetValue(from, out var list)) next[from] = list = new List<Guid>();
        list.Add(to);
      }

      var feeds = new bool[n, n];
      foreach (var a in placed)
      {
        var reached = Downstream(steps[a], next);
        foreach (var b in placed)
          if (a != b && steps[b].Any(reached.Contains)) feeds[a, b] = true;
      }

      var left = new List<int>(placed);
      var slots = Enumerable.Range(0, n).Where(i => order[i] == null).ToList();
      foreach (var slot in slots)
      {
        // The first step in the model's order that nothing left must precede; in a cycle there is none, and the model's order breaks it.
        var pick = left.FirstOrDefault(r => !left.Any(q => q != r && feeds[q, r] && !feeds[r, q]), left[0]);
        left.Remove(pick);
        order[slot] = pick;
      }
      return order.Select(o => o.Value).ToArray();
    }

    private static HashSet<Guid> Downstream(IEnumerable<Guid> start, Dictionary<Guid, List<Guid>> next)
    {
      var seen = new HashSet<Guid>();
      var todo = new Stack<Guid>(start);
      while (todo.Count > 0)
        if (next.TryGetValue(todo.Pop(), out var list))
          foreach (var to in list)
            if (seen.Add(to)) todo.Push(to);
      return seen;
    }

    /// <summary>The panel's view of the steps, reordered, before it is recorded, so the text recorded is the text walked and the text replayed. A view it cannot read is returned as it came.</summary>
    public static string SortView(string stepsView, IEnumerable<(Guid From, Guid To)> wires)
    {
      try
      {
        var root = JsonNode.Parse(stepsView);
        var steps = root?["steps"]?.AsArray();
        if (steps == null || steps.Count < 2) return stepsView;
        var nodes = steps.ToList();
        var order = Sort(nodes.Select(s => (IReadOnlyCollection<Guid>)IdsOf(s)).ToList(), wires);
        steps.Clear();
        foreach (var i in order) steps.Add(nodes[i]);
        return root.ToJsonString();
      }
      catch { return stepsView; }
    }

    /// <summary>
    /// The view as the panel draws it. The summary is model text the shell shows as escaped text, unlike a step's sentence, which is rendered when the run is read, so a code span's backticks
    /// read as backticks whenever the model writes one there. It may or may not, on a Walk or on Learn alike, so the words are read at this one door: live, replayed, and on a row from an older
    /// build. Read by the reader a note's text goes through, so a summary and the note pinned from it agree. Only the draw changes: the record keeps the marks, and the pin keeps them too.
    /// </summary>
    public static string ForPanel(string stepsView)
    {
      try
      {
        if (!(JsonNode.Parse(stepsView) is JsonObject view)) return stepsView;
        var summary = view["summary"]?.GetValue<string>();
        if (string.IsNullOrEmpty(summary)) return stepsView;
        var words = Dwight.Session.NoteMarkdown.Words(summary);
        if (words == summary) return stepsView;
        view["summary"] = words;
        return view.ToJsonString();
      }
      catch { return stepsView; }
    }

    internal static List<Guid> IdsOf(JsonNode step)
    {
      var ids = new List<Guid>();
      var arr = step?["ids"]?.AsArray();
      if (arr != null)
        foreach (var id in arr)
          if (Guid.TryParse(id?.GetValue<string>(), out var g)) ids.Add(g);
      return ids;
    }
  }

  /// <summary>
  /// A WALK IS THE SELECTION (Brief EB, from the showcase run: six of seventeen components selected, and the Walk lit and timed others). The model is shown each selected component's neighbours as context, so its
  /// steps can name them, and it can give them ids. It is the plugin's job to hold the steps to the selection (ADR-056), here, at the one door both a new run and a replayed row go through: a step's ids and marks are
  /// the selection's, a step only about what lies outside goes, and a neighbour stays only as words in a sentence, in the boundary form.
  /// </summary>
  internal static class WalkScope
  {
    /// <summary>What follows a neighbour's bracketed id in a step's sentence: "feeds `Vase G-code` (15), outside this selection".</summary>
    public const string OutsidePhrase = ", outside this selection";

    /// <summary>What the summary says when no step lies within the selection, so the person reads why nothing lights.</summary>
    public const string NothingWithin = "No step of this run lies within the selection; the sentences name its neighbours.";

    private static readonly Regex Bracketed = new Regex(@"\((\d+)\)(" + Regex.Escape(OutsidePhrase) + ")?", RegexOptions.Compiled);

    /// <summary>Brief EX: the boundary words as a model writes them itself, at a given place: no comma, two spaces, a no-break space, a capital, or "the selection".</summary>
    private static readonly Regex ModelsPhrase = new Regex(@"\G,?[ \u00A0]+outside (?:this|the) selection\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Brief EX: the words twice running, however each is written, kept once as the first is.</summary>
    private static readonly Regex Twice = new Regex(@"(\boutside (?:this|the) selection\b)(?:[ \u00A0]*,?[ \u00A0]*\boutside (?:this|the) selection\b)+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Brief EX addendum: the boundary words as the model writes them later in a sentence, with their leading comma and spaces.</summary>
    private static readonly Regex Again = new Regex(@"[ \u00A0]*,?[ \u00A0]*\boutside (?:this|the) selection\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Where a sentence ends: a full stop, semicolon, exclamation or question mark with no letter or digit after it, so "0.5" goes on.</summary>
    private static readonly Regex SentenceEnd = new Regex(@"[.;!?](?!\w)", RegexOptions.Compiled);

    private static readonly Regex AnyId = new Regex(@"\(\d+\)", RegexOptions.Compiled);

    /// <summary>
    /// The panel's view of the steps, held to <paramref name="selection"/>. A view that needs nothing comes back as it came, so a Walk of a whole selection is byte for byte what it was. No selection known (a row with none
    /// recorded) has nothing to hold it to, and comes back too. If every step were only about what lies outside, the sentences stay with nothing lit, so a paid answer is not lost to an edge case, and the summary says so in
    /// the plugin's words. Their ids are then empty, so a second reading finds nothing to do and says it once.
    /// </summary>
    public static string Within(string stepsView, IReadOnlyCollection<Guid> selection)
    {
      if (selection == null || selection.Count == 0) return stepsView;
      try
      {
        var root = JsonNode.Parse(stepsView);
        var steps = root?["steps"]?.AsArray();
        if (steps == null) return stepsView;

        var inside = new HashSet<Guid>(selection);
        var nodes = steps.ToList();
        var changed = false;
        var kept = new List<JsonNode>();
        foreach (var step in nodes)
        {
          var ids = WalkOrder.IdsOf(step);
          var within = ids.Where(inside.Contains).ToList();
          if (step is JsonObject o)
          {
            if (within.Count != ids.Count)
            {
              o["ids"] = new JsonArray(within.Select(g => (JsonNode)g.ToString()).ToArray());
              changed = true;
            }
            changed |= TrimMarks(o, inside);
          }
          if (ids.Count == 0 || within.Count > 0) kept.Add(step);
        }

        if (nodes.Count == 0) return stepsView;
        if (kept.Count == 0)
        {
          var said = root["summary"]?.GetValue<string>();
          root["summary"] = string.IsNullOrWhiteSpace(said) ? NothingWithin : NothingWithin + " " + said;
          return root.ToJsonString();
        }
        if (kept.Count != nodes.Count)
        {
          changed = true;
          steps.Clear();
          foreach (var step in kept) steps.Add(step);
        }
        return changed ? root.ToJsonString() : stepsView;
      }
      catch { return stepsView; }
    }

    /// <summary>A highlight keeps what is inside; an arrow needs both of its ends inside, because a wire that leaves the selection ends at its edge. True when anything went.</summary>
    private static bool TrimMarks(JsonObject step, HashSet<Guid> inside)
    {
      if (!(step["marks"] is JsonArray marks)) return false;
      var changed = false;
      foreach (var mark in marks.ToList())
      {
        if (!(mark is JsonObject m)) continue;
        if (m["t"]?.GetValue<string>() == "arrow")
        {
          var keep = Guid.TryParse(m["from"]?.GetValue<string>(), out var from) && Guid.TryParse(m["to"]?.GetValue<string>(), out var to) && inside.Contains(from) && inside.Contains(to);
          if (!keep) { marks.Remove(mark); changed = true; }
        }
        else if (m["ids"] is JsonArray)
        {
          var ids = WalkOrder.IdsOf(m);
          var within = ids.Where(inside.Contains).ToList();
          if (within.Count == ids.Count) continue;
          changed = true;
          if (within.Count == 0) marks.Remove(mark);
          else m["ids"] = new JsonArray(within.Select(g => (JsonNode)g.ToString()).ToArray());
        }
      }
      if (marks.Count == 0) step.Remove("marks");
      return changed;
    }

    /// <summary>
    /// A neighbour's bracketed id, as the model writes it, gets the boundary form, so a sentence cannot read as if it were part of this selection. The plugin holds what carries an id; a name with no id it cannot know, and
    /// the prompt asks for the form. Already in the form, it is left alone, so this can be read twice.
    /// </summary>
    public static string MarkOutside(string text, ICollection<int> outside) =>
      MarkOutside(text, outside == null ? null : (IReadOnlyDictionary<int, string>)outside.ToDictionary(id => id, id => (string)null));

    /// <summary>
    /// THE SIXTEENTH GATE: A MODEL CAN WRITE THE NICKNAME AFTER THE ID, and then the mark fell between them, as "(9), outside this selection mul". With each neighbour's nickname known, one written right after its id moves
    /// before it, so it reads "mul (9), outside this selection". One written on both sides is said once.
    /// BRIEF EX: A MODEL CAN WRITE THE WORDS ITSELF, with no comma ("a Division outside this selection", Rhino 2's capture), so after an id the mark put its phrase in front of the model's, and the sweep read
    /// "(15), outside this selection, outside this selection". The model's words after the id, or after its nickname, are taken as the phrase; and whatever the model writes, the words twice running are read once.
    /// BRIEF EX ADDENDUM, THE TWENTY-FIFTH'S GATE: the model's words later in the sentence are dropped too, so a sentence says them once per id (see <see cref="Rest"/>).
    /// </summary>
    public static string MarkOutside(string text, IReadOnlyDictionary<int, string> outside) => Once(Marked(text, outside));

    /// <summary>The words twice running said once; the text itself when they are not, so a sentence with nothing to mark comes back as it came.</summary>
    private static string Once(string text) => string.IsNullOrEmpty(text) || !Twice.IsMatch(text) ? text : Twice.Replace(text, "$1");

    /// <summary>The mark alone, before the backstop, so a test holds each: the model's own words after an id are taken as the phrase here, and <see cref="Once"/> is the net under it.</summary>
    internal static string Marked(string text, IReadOnlyDictionary<int, string> outside)
    {
      if (string.IsNullOrEmpty(text) || outside == null || outside.Count == 0) return text;
      var built = new StringBuilder();
      var at = 0;
      foreach (Match m in Bracketed.Matches(text))
      {
        if (m.Index < at || !int.TryParse(m.Groups[1].Value, out var id) || !outside.TryGetValue(id, out var nick)) continue;
        var next = m.Index + m.Length;
        var after = NameAfter(text, next, nick);
        if (m.Groups[2].Success && after == null)
        {
          var kept = Rest(text, next, out var end);
          if (kept.Length == end - next) continue;   // already in the form and said once, so it is left alone and can be read twice
          built.Append(text, at, next - at).Append(kept);
          at = end;
          continue;
        }

        built.Append(text, at, m.Index - at);
        if (after != null)
        {
          next += after.Length;
          var name = after.TrimStart();
          if (!built.ToString().TrimEnd().EndsWith(name, StringComparison.OrdinalIgnoreCase)) built.Append(name).Append(' ');
        }
        var said = ModelsPhrase.Match(text, next);
        if (said.Success) next += said.Length;
        built.Append('(').Append(m.Groups[1].Value).Append(')').Append(OutsidePhrase);
        var rest = Rest(text, next, out var stop);
        if (rest.Length > 1 && rest[0] == ' ' && char.IsLetterOrDigit(rest[1])) built.Append(',');
        built.Append(rest);
        at = stop;
      }
      return at == 0 && built.Length == 0 ? text : built.Append(text, at, text.Length - at).ToString();
    }

    /// <summary>
    /// BRIEF EX ADDENDUM, THE TWENTY-FIFTH'S GATE: the mark said the words at the id, and the model said them again later in the sentence, so "feeds the Division (11) on its A input, outside this selection." read
    /// "(11), outside this selection, on its A input, outside this selection." The rest of the sentence after a marked id, with the model's own words dropped: it runs to the sentence's end, and never past the next id,
    /// whose words are its own. A sentence with no mark in it keeps the model's words.
    /// </summary>
    private static string Rest(string text, int from, out int end)
    {
      var stop = SentenceEnd.Match(text, from);
      end = stop.Success ? stop.Index : text.Length;
      var id = AnyId.Match(text, from);
      if (id.Success && id.Index < end) end = id.Index;
      return Again.Replace(text.Substring(from, end - from), "");
    }

    /// <summary>The nickname written right after an id, with its space and any code or bold marks, or null. It must end there: "mul" does not match the start of "multiplied".</summary>
    private static string NameAfter(string text, int at, string nick)
    {
      if (string.IsNullOrWhiteSpace(nick) || at >= text.Length || text[at] != ' ') return null;
      foreach (var mark in new[] { "`", "**", "" })
      {
        var token = " " + mark + nick + mark;
        if (string.CompareOrdinal(text, at, token, 0, 0) != 0 || at + token.Length > text.Length) continue;
        if (!string.Equals(text.Substring(at, token.Length), token, StringComparison.OrdinalIgnoreCase)) continue;
        var end = at + token.Length;
        if (end < text.Length && char.IsLetterOrDigit(text[end])) continue;
        return text.Substring(at, token.Length);
      }
      return null;
    }

    /// <summary>The components a Walk's times are read from: the selection, and a row's steps only when it recorded no selection. So the summary and Replay count what the person selected.</summary>
    public static IEnumerable<Guid> Timed(IReadOnlyCollection<Guid> selection, IEnumerable<IReadOnlyList<Guid>> steps) =>
      selection != null && selection.Count > 0
        ? selection.Distinct()
        : (steps ?? Enumerable.Empty<IReadOnlyList<Guid>>()).SelectMany(s => s ?? (IReadOnlyList<Guid>)Array.Empty<Guid>()).Distinct();
  }

  /// <summary>
  /// Where a Walk tells its story (Tharun's ruling of 1 November 2026: Grasshopper is sometimes full screen, sometimes half the screen beside Rhino, sometimes minimised, and resized while a Walk is live).
  /// <see cref="Bar"/> when the bar can be placed honestly on the canvas. <see cref="Panel"/> when it cannot, and then the panel is the Walk: its footer's controls, there for any live Walk, run the same state, so the person always
  /// has one surface that works. <see cref="Hidden"/> when the canvas itself is not showing: the bar is away, and comes back when the canvas does.
  /// </summary>
  internal enum WalkSurface { Bar, Panel, Hidden }

  /// <summary>What a key does to a Walk. <see cref="FocusOff"/> is Esc while the bar shows: the windows go back and the Walk stays on its step (Brief EC unit 1).</summary>
  internal enum WalkKey { None, Next, Back, Leave, FocusOff }

  /// <summary>
  /// THE KEYS, and the law that matters: with no live Walk a key is returned unhandled, so the hook can never eat one. A key with a modifier is never taken either, because that
  /// is where Grasshopper keeps what edits the definition: read from <c>GH_DocumentEditor.EditorKeyDown</c> in Grasshopper 8.32, Alt with an arrow nudges the selection, with an undo record.
  ///
  /// A plain arrow is taken whatever is selected, because a plain arrow never nudges: there it pans the canvas (<c>GH_Canvas.Navigate</c>), at once and without marking the key handled, so the
  /// hook still receives it. Next or Back then overwrites that pan with the step's frame. On step 1 and step N the Walk does nothing and the pan stands. Taking arrows only with nothing selected
  /// would also have killed them in the commonest state, since Explain runs on a selection and it is still there when a Walk starts.
  /// </summary>
  internal static class WalkKeys
  {
    // The virtual-key codes, which System.Windows.Forms.Keys and the host pass as they are.
    public const int Escape = 27, Left = 37, Up = 38, Right = 39, Down = 40;

    public static WalkKey Dispatch(int keyCode, bool modified, bool walkLive)
    {
      if (!walkLive || modified) return WalkKey.None;
      switch (keyCode)
      {
        case Right: case Down: return WalkKey.Next;
        case Left: case Up: return WalkKey.Back;
        case Escape: return WalkKey.Leave;
        default: return WalkKey.None;
      }
    }

    /// <summary>Brief EC unit 1: in Focus, Esc is the bar's X, so it turns Focus off and leaves the Walk on its step. Out of Focus it leaves the Walk, as it always has. Only the panel's Leave ends a Walk from Focus.</summary>
    public static WalkKey InFocus(WalkKey key, bool focusActive) => key == WalkKey.Leave && focusActive ? WalkKey.FocusOff : key;

    /// <summary>A key from a page: <c>KeyboardEvent.key</c>, its modifiers already refused by the page's own line.</summary>
    public static int CodeOf(string domKey)
    {
      switch (domKey)
      {
        case "ArrowRight": return Right;
        case "ArrowDown": return Down;
        case "ArrowLeft": return Left;
        case "ArrowUp": return Up;
        case "Escape": return Escape;
        default: return 0;
      }
    }
  }

  /// <summary>Every coordinate a Walk needs, computed here (ADR-056). Document units unless a name says otherwise; the bar's box is logical points, as the one window's is (#176).</summary>
  internal static class WalkGeometry
  {
    /// <summary>How far a hole outgrows a component, in document units: past the widest ring of the glow, so the halo sits on clean canvas.</summary>
    public const float HoleMargin = CanvasLook.GlowReach + CanvasLook.IslandMargin;

    /// <summary>
    /// How close a step is framed: 1, which is <c>GH_Viewport.ZoomDefault</c>. Measured from Grasshopper's own code rather than chosen: <c>GH_Canvas</c> fades its text in by zoom at
    /// thresholds of 0.6 and 0.8 of that default, so from 0.8 everything it draws is fully in, and closer only makes it bigger. A one-component step therefore stops here, not at the
    /// 1.6 a general framing allows.
    /// </summary>
    public const float ClosestZoom = 1f;

    public const float FurthestZoom = 0.1f;

    /// <summary>The bar's height is the page's to ask for, within these. The sentence scrolls inside the bar past the top one. 420 is the narrowest width it was rendered at and read, with seven steps; below it the panel is the Walk.</summary>
    public const int BarMinH = 78, BarMaxH = 148, BarMinW = 420;

    /// <summary>A canvas too short to hold the tallest bar and still frame a step above it: at least as much canvas left free as the bar takes.</summary>
    public const int BarMinCanvasH = 2 * FrameBottomClear;

    /// <summary>What framing leaves free above the canvas's bottom edge: the bar at its tallest. The page reports its height only after the step is framed, and a step under the bar is the failure this prevents.</summary>
    public const int FrameBottomClear = BarMaxH + OneWindowCore.EdgeMargin;

    /// <summary>One hole per component on the canvas, grown by <see cref="HoleMargin"/>. A step whose components are gone has none, and the wash then covers everything.</summary>
    public static List<RectangleF> Holes(IEnumerable<RectangleF?> bounds)
    {
      var holes = new List<RectangleF>();
      foreach (var b in bounds ?? Enumerable.Empty<RectangleF?>())
        if (b.HasValue) holes.Add(RectangleF.Inflate(b.Value, HoleMargin, HoleMargin));
      return holes;
    }

    /// <summary>The canvas's colour, read and not assumed: what the wash and the edge choose by (<see cref="CanvasLook"/>).</summary>
    public static Color CanvasColour(bool mono, Color monoColor, Color back) => mono ? monoColor : back;

    /// <summary>
    /// Where the view goes for a step: <paramref name="box"/> fitted into the part of the canvas the bar and the window leave free, and centred there. The canvas size and the two
    /// clears are in the canvas's own pixels. The margin is the one <c>CanvasNav.GoTo</c> uses, so a step and the whole selection frame alike.
    /// </summary>
    public static (float Zoom, PointF Mid) Frame(RectangleF box, float canvasW, float canvasH, float bottomClear, float rightClear)
    {
      var freeW = Math.Max(canvasW - rightClear, canvasW * 0.25f);
      var freeH = Math.Max(canvasH - bottomClear, canvasH * 0.25f);
      var r = RectangleF.Inflate(box, box.Width * 0.25f + 60f, box.Height * 0.25f + 60f);
      var zoom = Math.Max(FurthestZoom, Math.Min(Math.Min(freeW / r.Width, freeH / r.Height), ClosestZoom));
      // A viewport's MidPoint is the document point at the canvas's centre; the free part's centre sits up and left of that.
      return (zoom, new PointF(box.X + box.Width / 2f + (canvasW - freeW) / 2f / zoom, box.Y + box.Height / 2f + (canvasH - freeH) / 2f / zoom));
    }

    /// <summary>How much of the canvas's right side an open window takes from framing. A window the user has moved to the left half takes nothing from here.</summary>
    public static int RightClear(OneWindowCore.Box canvas, OneWindowCore.Box? window)
    {
      if (!(window is OneWindowCore.Box w) || w.X + w.W / 2 < canvas.X + canvas.W / 2) return 0;
      return Math.Max(0, canvas.X + canvas.W - w.X);
    }

    /// <summary>Is the canvas on the screen at all? Read from the editor and the canvas, the way the window's own placement reads them, never from the screen.</summary>
    public static bool CanvasShowing(bool editorVisible, bool editorMinimised, bool canvasVisible, int canvasW, int canvasH) =>
      editorVisible && !editorMinimised && canvasVisible && canvasW > 0 && canvasH > 0;

    /// <summary>Does this end the Walk? A different document does, and so does a canvas showing with no document. An editor closed does not: its canvas is away, the document is not, and the Walk waits.</summary>
    public static bool DocumentEnds(bool canvasShowing, bool hasDocument, bool sameDocument) =>
      hasDocument ? !sameDocument : canvasShowing;

    /// <summary>The choice, a pure function of the canvas's rectangle and the one window's box as it stands (see <see cref="WalkSurface"/>).</summary>
    public static WalkSurface Surface(bool canvasShowing, OneWindowCore.Box? canvas, OneWindowCore.Box? other)
    {
      if (!canvasShowing || !(canvas is OneWindowCore.Box box)) return WalkSurface.Hidden;
      var (left, right) = Span(box, other);
      return right - left >= BarMinW && box.H >= BarMinCanvasH ? WalkSurface.Bar : WalkSurface.Panel;
    }

    /// <summary>
    /// THE PANEL IS THE WALK (Brief DH, Tharun's ruling of 1 November 2026). The bar is Focus mode's, and it is asked about only there; a canvas that is not showing is hidden either way. <see cref="Surface"/> stays as
    /// the fallback inside Focus: where the bar cannot be placed, the panel is the Walk there too.
    /// </summary>
    public static WalkSurface SurfaceFor(bool focus, bool canvasShowing, OneWindowCore.Box? canvas, OneWindowCore.Box? other)
    {
      if (!canvasShowing) return WalkSurface.Hidden;
      return focus ? Surface(true, canvas, other) : WalkSurface.Panel;
    }

    /// <summary>
    /// The bar: along the bottom of the canvas, clear of the compass in its corner, and stopping short of the one window or the chip where they would meet. <paramref name="other"/> is that window's box as
    /// it stands, because the user may have moved it. Only asked where <see cref="Surface"/> said <see cref="WalkSurface.Bar"/>, so it is never narrower than its minimum and never over the window.
    /// </summary>
    public static OneWindowCore.Box Bar(OneWindowCore.Box canvas, int wantH, OneWindowCore.Box? other)
    {
      var h = Math.Max(BarMinH, Math.Min(BarMaxH, wantH));
      var (left, right) = Span(canvas, other);
      return new OneWindowCore.Box(left, canvas.Y + canvas.H - h - OneWindowCore.EdgeMargin, right - left, h);
    }

    /// <summary>The row along the bottom the bar may have. The window is tested against the bar at its tallest, so the bar's width does not jump between a short sentence and a long one.</summary>
    private static (int Left, int Right) Span(OneWindowCore.Box canvas, OneWindowCore.Box? other)
    {
      var margin = OneWindowCore.EdgeMargin;
      var left = canvas.X + margin;
      var right = canvas.X + canvas.W - OneWindowCore.CornerClear;
      var bottom = canvas.Y + canvas.H - margin;
      if (other is OneWindowCore.Box w && w.Y < bottom && w.Y + w.H > bottom - BarMaxH)
      {
        if (w.X + w.W / 2 >= (left + right) / 2) right = Math.Min(right, w.X - margin);
        else left = Math.Max(left, w.X + w.W + margin);
      }
      return (left, right);
    }
  }

  /// <summary>The plugin's words on a Walk (#235), and what the bar is told.</summary>
  internal static class WalkText
  {
    public static string StepLine(int index, int count) => "Step " + (index + 1) + " of " + count;

    /// <summary>Brief DD: where Play stops, said where the step's number is.</summary>
    public const string EndOfWalk = "End of the steps";

    /// <summary>Brief EC unit 5b: the chat's way back to the Walk it was asked from, which waits on its step.</summary>
    public const string BackToWalk = "Back to step by step";

    /// <summary>A step whose components have gone since, or were never grounded, says so under its sentence, rather than framing nothing in silence. Null when they are all there.</summary>
    public static string Gone(int total, int found)
    {
      if (total > 0 && found >= total) return null;
      if (found <= 0) return "Its components are no longer on the canvas.";
      return (total - found) + " of its " + total + " components are no longer on the canvas.";
    }

    /// <summary>
    /// What "Ask about this step" leaves in the composer, unsent: the step's sentence, whatever has happened to its components. Brief EC unit 5: its ids are the CHAT's, through each component's
    /// guid (<paramref name="chatIds"/> from the Explain's id to the chat's), because the chat reads a bracketed number as its own; a component the chat cannot place keeps its name and loses its id.
    /// With no translation every bracketed id goes, never the Explain's numbers into a chat that numbers differently.
    /// </summary>
    public static string AskLine(int index, string stepHtml, Func<int, int?> chatIds = null)
    {
      var sentence = PlainText(Dwight.Agent.RunIds.RewriteHtml(stepHtml, chatIds));
      var about = "About step " + (index + 1) + " of this Explain";
      return sentence.Length == 0 ? about + ": " : about + ": " + sentence;
    }

    /// <summary>A step's sentence is the panel's snippet of HTML; the composer holds it as words.</summary>
    public static string PlainText(string html)
    {
      if (string.IsNullOrEmpty(html)) return "";
      var text = WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]*>", ""));
      return Regex.Replace(text, @"\s+", " ").Trim();
    }

    /// <summary>Brief DD: the line under the panel's current card: that Play ran out here, then the step's times. The bar says the first on its step number.</summary>
    public static string PanelLine(bool ended, string time)
    {
      if (!ended) return string.IsNullOrEmpty(time) ? null : time;
      return string.IsNullOrEmpty(time) ? EndOfWalk + "." : EndOfWalk + ". " + time;
    }

    /// <summary>The document the bar draws. The sentence is already through the Markdown pipeline (ADR-088), as the panel's step card is. Brief DD: whether it is playing, whether Play ran out here, and the step's line of times.
    /// Brief EI: on a Learn deck, the card the panel would draw, which the bar draws in the sentence's place, every word the plugin's.</summary>
    public static string View(int index, int count, string stepHtml, string gone, bool playing = false, bool ended = false, string time = null, bool canPlay = true,
      bool? everyWire = null, bool focus = true, bool dim = true, JsonObject deck = null)
    {
      var label = deck != null ? DeckText.Kicker : StepLine(index, count) + (ended ? ". " + EndOfWalk : "");
      var view = new JsonObject { ["label"] = label, ["html"] = stepHtml ?? "", ["i"] = index, ["n"] = count, ["playing"] = playing, ["canplay"] = canPlay, ["focus"] = focus, ["dim"] = dim };
      if (!string.IsNullOrEmpty(gone)) view["gone"] = gone;
      if (!string.IsNullOrEmpty(time)) view["time"] = time;
      if (everyWire.HasValue) view["everywire"] = everyWire.Value;   // Brief DJ: absent with no lens, so the button is disabled, not lying
      if (deck != null) view["deck"] = deck.DeepClone();   // a Walk with no deck sends none of it
      return view.ToJsonString();
    }
  }

  /// <summary>Which Explain a menu with no mode named runs. A Walk needs two or more components (ADR-060); one runs Detailed, as it always has.</summary>
  internal static class ExplainModes
  {
    public const string Walk = "walk", Detailed = "detailed", Short = "short";

    /// <summary>Brief DE (Tharun's ruling of 1 November 2026): Learn is a Walk with practice questions, chosen for this run only. There is no global state: nothing is on or off.</summary>
    public const string Learn = "learn";

    public static string ForCount(int count) => count >= 2 ? Walk : Detailed;

    /// <summary>What the mode is called on a history row.</summary>
    public static string Label(string mode) => mode == Learn ? "learn" : mode == Walk ? Dwight.DwightVerbs.StepByStepName : mode == Short ? "short" : "detailed";
  }
}
