using System;

namespace Dwight.UI
{
  /// <summary>What can happen to the one window.</summary>
  internal enum WindowEvent
  {
    ChipClick,
    Minimise,
    /// <summary>The list. Timeline is the same page, so the window does not know the difference.</summary>
    Home,
    /// <summary>A verb, a new chat or a reopened row: the window turns to this page.</summary>
    Open,
    TurnEnded,
    DocumentSwitch,
    /// <summary>A page closed from outside the window: its load watchdog, or Restart.</summary>
    PageClosed,
    /// <summary>Brief EC unit 5b: a Walk's step asked its chat, or that chat is shown again. The chat is the face, and the Walk's page is kept behind it, on its step.</summary>
    AskOpen,
  }

  /// <summary>What is true of the window now. A page is named by an opaque id; a null <see cref="Face"/> is the home page.</summary>
  internal sealed class WindowFacts
  {
    public bool Open { get; set; }
    public string Face { get; set; }
    /// <summary>The page whose run is going on, showing or hidden; null when nothing runs on a page.</summary>
    public string Running { get; set; }
    /// <summary>The page whose turn finished out of sight, while it is still alive; null otherwise.</summary>
    public string DonePage { get; set; }
    /// <summary>Brief EC unit 5b: the Walk's page, kept behind the chat its step asked; null otherwise.</summary>
    public string Kept { get; set; }
  }

  /// <summary>What the window does next. The host carries it out and decides nothing.</summary>
  internal sealed class WindowStep
  {
    public bool Open { get; set; }
    public string Face { get; set; }
    /// <summary>The page to dispose; null for none. A step never disposes more than one, which is what holds three live pages.</summary>
    public string Dispose { get; set; }
    /// <summary>A flip must focus: toggling visibility leaves the page without focus, and typing goes nowhere (the spike).</summary>
    public bool FocusPage { get; set; }
    /// <summary>Minimise must hand the keyboard back, or focus stays in the hidden page and the canvas loses its keys (the spike).</summary>
    public bool FocusCanvas { get; set; }
    /// <summary>A finished conversation was left without being disposed: clear its glow.</summary>
    public bool ClearGlow { get; set; }
    /// <summary>A finished conversation came back: re-light it, settled.</summary>
    public bool Relight { get; set; }
    /// <summary>A turn finished somewhere the user is not looking.</summary>
    public bool DoneUnseen { get; set; }
    /// <summary>The home page keeps its last view, and the chip opens on History after a document switch whatever that view was (sixth gate).</summary>
    public bool ResetHome { get; set; }
    /// <summary>The user is looking at the conversation that finished, so it is no longer news. Opening the window on anything else is not looking at it.</summary>
    public bool ClearDoneUnseen { get; set; }
    /// <summary>Brief EC unit 5b: the Walk's page kept behind its chat after this step; null when none is.</summary>
    public string Kept { get; set; }
    /// <summary>Brief EC unit 5b: the kept Walk's page, to close because something else opened; its Walk ends with it. The second page a step may close, and only ever this one.</summary>
    public string CloseKept { get; set; }
  }

  /// <summary>What the user asks for while Dwight is working.</summary>
  internal enum RunRequest
  {
    Minimise,
    History,
    Timeline,
    ReadFinished,
    NewChat,
    SendSameChat,
    SendOtherChat,
    VerbFromChord,
    Walk,
    ClickCard,
  }

  internal enum RunAnswer { Allow = 0, Refuse = 1 }

  /// <summary>
  /// ONE WINDOW (Brief BR), GH-free and Eto-free (ADR-091), so every cell of it is tested. <see cref="DwightDock"/> only carries out what this
  /// says. A verb, a new chat and a reopened row are one event, <see cref="WindowEvent.Open"/>: the window turns to that page.
  ///
  /// <code>
  /// event           Open after    Face after          disposes                       focus    glow
  /// ChipClick       yes           unchanged           nothing                        page     re-light a finished face
  /// Minimise        no            unchanged           nothing                        canvas   clear a finished face
  /// Home            yes           home                the old face, unless running   page     (cleared by its disposal)
  /// Open X          yes           X                   the old face, unless X/running page     (cleared by its disposal)
  /// TurnEnded X     unchanged     unchanged           X, if it is not the face       none     clear X, if it is a minimised face
  /// DocumentSwitch  unchanged     open: unchanged     open: nothing                  none     -
  ///                               chip: home          chip: the old face, unless running
  /// PageClosed X    unchanged     X was face: home    nothing (it is already gone)   none     -
  /// AskOpen X       yes           X                   the old face, unless X/running/kept   page     -
  /// </code>
  ///
  /// THE KEPT WALK (Brief EC unit 5b). A Walk's step asking its chat keeps the Walk's page behind the chat, on its step, so the person can read the answer beside the step and walk on. Opening
  /// the kept page again brings it back and it is no longer kept; going home keeps it, because History is a way back; opening anything else closes it, and its Walk ends with it.
  ///
  /// THE LAW IT HOLDS: at most four live pages, home, the one showing, one left running, and a Walk's page kept behind its chat. One thing runs at a time, so a page that is none of those
  /// cannot exist after a step. A document switch never jumps an open window; the chip then opens on History, because the last conversation may belong to another file.
  /// </summary>
  internal static class OneWindowCore
  {
    public static WindowStep Next(WindowFacts now, WindowEvent e, string page = null)
    {
      var step = new WindowStep { Open = now.Open, Face = now.Face, Kept = now.Kept };
      switch (e)
      {
        case WindowEvent.AskOpen:
          step.Open = true;
          step.Face = page;
          step.Dispose = Leaving(now, page);
          step.FocusPage = true;
          if (page != null && page == now.Kept) step.Kept = null;
          break;
        case WindowEvent.ChipClick:
          step.Open = true;
          step.FocusPage = true;
          step.Relight = !now.Open && now.Face != null && now.Face != now.Running;
          break;
        case WindowEvent.Minimise:
          if (!now.Open) break;
          step.Open = false;
          step.FocusCanvas = true;
          step.ClearGlow = now.Face != null && now.Face != now.Running;
          break;
        case WindowEvent.Home:
          step.Open = true;
          step.Face = null;
          step.Dispose = Leaving(now, null);
          step.FocusPage = true;
          break;
        case WindowEvent.Open:
          step.Open = true;
          step.Face = page;
          step.Dispose = Leaving(now, page);
          step.FocusPage = true;
          if (now.Kept != null)
          {
            step.Kept = null;
            if (page != now.Kept) step.CloseKept = now.Kept;   // something else opened: the Walk behind the chat ends
          }
          break;
        case WindowEvent.TurnEnded:
          if (page == null) break;
          if (page != now.Face) step.Dispose = page;
          else if (!now.Open) step.ClearGlow = true;
          step.DoneUnseen = !(now.Open && page == now.Face);
          break;
        case WindowEvent.DocumentSwitch:
          if (now.Open) break;
          step.Face = null;
          step.Dispose = Leaving(now, null);
          step.ResetHome = true;
          break;
        case WindowEvent.PageClosed:
          if (page != null && page == now.Face) step.Face = null;
          if (page != null && page == now.Kept) step.Kept = null;
          break;
      }
      step.ClearDoneUnseen = step.Open && step.Face != null && step.Face == now.DonePage;
      return step;
    }

    /// <summary>A hidden page that finished is disposed, so looking at it again is reopening its row: the same chat, or for a verb, which has no chat
    /// id, a verb's row.</summary>
    public static bool RowClearsDone(string doneThread, string rowThread) =>
      string.Equals(doneThread ?? "", rowThread ?? "", StringComparison.Ordinal);

    private static string Leaving(WindowFacts now, string target) =>
      now.Face == null || now.Face == target || now.Face == now.Running || now.Face == now.Kept ? null : now.Face;

    // ---------------------------------------------------------------- while Dwight is working

    /// <summary>
    /// ONE THING AT A TIME STAYS, AND BEING BUSY NEVER COSTS THE WINDOW (Tharun's question, #1157). There is one canvas and one undo stack, and a
    /// verdict compares a before with an after, so nothing else may write during a run. Everything that only reads, or opens a page that has not
    /// asked for anything yet, carries on.
    /// </summary>
    public static RunAnswer DuringRun(RunRequest request)
    {
      switch (request)
      {
        case RunRequest.Minimise:
        case RunRequest.History:
        case RunRequest.Timeline:
        case RunRequest.ReadFinished:
        case RunRequest.NewChat:
          return RunAnswer.Allow;
        default:
          return RunAnswer.Refuse;   // a send, a verb, a walk, a card: each would start something; a request this table has not been told about too
      }
    }

    public const string BusySameChat = "Dwight is still working on the previous message. Wait for it to finish, or press Stop.";
    public const string BusyOtherChat = "Dwight is working in another chat. Wait for it to finish, or stop it there.";
    public const string BusyOtherTask = "Dwight is working on another task. Wait for it to finish, or stop it there.";
    public const string GoToRunning = "Go to it";
    public const string RunFinishedLine = "That has finished.";

    /// <summary>The refusal a chat gives when something else is running, by what is running. "Previous message" is true only of this chat.</summary>
    public static string BusyLine(string thisThread, string runningThread)
    {
      if (string.IsNullOrEmpty(runningThread)) return BusyOtherTask;
      return string.Equals(thisThread, runningThread, StringComparison.Ordinal) ? BusySameChat : BusyOtherChat;
    }

    /// <summary>Whether the refusal carries the way to what is running. In the same chat, Stop is already on the page.</summary>
    public static bool BusyHasWayThere(string thisThread, string runningThread) =>
      !string.Equals(thisThread, runningThread, StringComparison.Ordinal);

    // ---------------------------------------------------------------- what the shells are told

    public const string LiveRunning = "running";
    public const string LiveWaiting = "waiting";

    /// <summary>A History row's live state. A card waiting is not a run, and a card minted during a turn is not shown until the turn ends, so
    /// running comes first.</summary>
    public static string LiveWord(string thread, string runningThread, bool hasPendingCard)
    {
      if (string.IsNullOrEmpty(thread)) return null;
      if (string.Equals(thread, runningThread, StringComparison.Ordinal)) return LiveRunning;
      return hasPendingCard ? LiveWaiting : null;
    }

    /// <summary>The chip's state. A waiting card comes first: it is the one thing that must never go unseen, and only the user can end it. It is
    /// computed from the live store, so it holds until the card is answered, and it does not rest on the ring.</summary>
    public static string ChipState(bool thinking, bool waiting, bool doneUnseen) =>
      waiting ? "waiting" : thinking ? "thinking" : doneUnseen ? "done" : null;

    /// <summary>The ring means only "you are needed" (#1159): a waiting card, never a finished turn. The look holds it for as long as the state
    /// holds; this is only when the host draws today's ring. Lavender stays the thinking state.</summary>
    public static bool ChipRings(string before, string after, bool windowOpen) =>
      !windowOpen && after != before && after == "waiting";

    // ---------------------------------------------------------------- placement (#176: everything here is LOGICAL points)

    internal readonly struct Box
    {
      public readonly int X, Y, W, H;
      public Box(int x, int y, int w, int h) { X = x; Y = y; W = w; H = h; }
      public override string ToString() => "(" + X + "," + Y + " " + W + "x" + H + ")";
    }

    public const int ChipSize = 48, WindowW = 340, WindowMaxH = 760, WindowMinH = 170;
    public const int CornerClear = 56;     // the chip's gap from the corner: it clears the Grasshopper compass
    public const int EdgeMargin = 12;
    public const int WindowBottomClear = 80;   // the bottom edge the open dock has today, above the compass
    public const int MaxNudge = 150;

    /// <summary>The canvas is a WinForms control, so its rectangle is PHYSICAL device pixels; a window's location is LOGICAL points. At 100% they
    /// coincide, and at 150% they diverge and a window lands off the screen (incident #176).</summary>
    public static Box CanvasLogical(int physicalX, int physicalY, int physicalW, int physicalH, double dpi)
    {
      var s = dpi > 0 ? dpi / 96.0 : 1.0;
      return new Box(Round(physicalX / s), Round(physicalY / s), Round(physicalW / s), Round(physicalH / s));
    }

    private static int Round(double v) => (int)Math.Round(v);

    /// <summary>The chip: the bottom right of the canvas, nudged left by the user, never past the canvas's left edge.</summary>
    public static Box Chip(Box canvas, int nudgeLeft)
    {
      var x = canvas.X + canvas.W - ChipSize - CornerClear - nudgeLeft;
      if (x < canvas.X + EdgeMargin) x = canvas.X + EdgeMargin;
      var y = canvas.Y + canvas.H - ChipSize - EdgeMargin;
      if (y < canvas.Y + EdgeMargin) y = canvas.Y + EdgeMargin;
      return new Box(x, y, ChipSize, ChipSize);
    }

    /// <summary>The window has one size: 340 wide, up to 760 tall, inside the canvas, right-pinned above the compass. On a canvas shorter than
    /// that it keeps its head on the canvas.</summary>
    public static Box Window(Box canvas)
    {
      var w = Math.Min(WindowW, Math.Max(canvas.W - EdgeMargin * 2, 0));
      var h = Math.Max(WindowMinH, Math.Min(WindowMaxH, canvas.H - WindowBottomClear - EdgeMargin));
      var x = canvas.X + canvas.W - w - EdgeMargin;
      if (x < canvas.X + EdgeMargin) x = canvas.X + EdgeMargin;
      var y = canvas.Y + canvas.H - h - WindowBottomClear;
      if (y < canvas.Y + EdgeMargin) y = canvas.Y + EdgeMargin;
      return new Box(x, y, w, h);
    }

    /// <summary>How far the chip's drag nudges it left of its corner: not back over the compass, and bounded rather than across the canvas.</summary>
    public static int ChipNudge(Box canvas, int pointerX)
    {
      var defaultLeft = canvas.X + canvas.W - ChipSize - CornerClear;
      var offset = defaultLeft - (pointerX - ChipSize / 2);
      return Math.Min(MaxNudge, Math.Max(-(CornerClear - EdgeMargin), offset));
    }

    /// <summary>The host's move: wherever it is asked, the window stays inside the canvas, so no value puts it off the screen (sixth gate). A window larger than the canvas
    /// keeps its head and its left edge on it.</summary>
    public static Box Move(int x, int y, int w, int h, Box canvas)
    {
      int left = canvas.X + EdgeMargin, top = canvas.Y + EdgeMargin, right = canvas.X + canvas.W - EdgeMargin, bottom = canvas.Y + canvas.H - EdgeMargin;
      x = Math.Max(left, Math.Min(x, right - w));
      y = Math.Max(top, Math.Min(y, bottom - h));
      return new Box(x, y, w, h);
    }

    /// <summary>The frameless resize: the HTML drives it through the left, top and bottom edges, clamped to the canvas and the minimum size.</summary>
    public static Box Resize(string edges, Box start, int dx, int dy, Box canvas)
    {
      edges = edges ?? "";
      int x = start.X, y = start.Y, w = start.W, h = start.H;
      if (edges.Contains("l")) { x = start.X + dx; w = start.W - dx; }
      if (edges.Contains("t")) { y = start.Y + dy; h = start.H - dy; }
      if (edges.Contains("b")) h = start.H + dy;

      if (w < WindowW) { x = start.X + (start.W - WindowW); w = WindowW; }
      if (h < WindowMinH) { if (edges.Contains("t")) y = start.Y + (start.H - WindowMinH); h = WindowMinH; }

      int left = canvas.X + EdgeMargin, top = canvas.Y + EdgeMargin, right = canvas.X + canvas.W - EdgeMargin, bottom = canvas.Y + canvas.H - EdgeMargin;
      if (x < left) { w -= left - x; x = left; }
      if (x + w > right) w = right - x;
      if (y < top) { h -= top - y; y = top; }
      if (y + h > bottom) h = bottom - y;
      if (w < WindowW) w = WindowW;
      if (h < WindowMinH) h = WindowMinH;
      return new Box(x, y, w, h);
    }
  }
}
