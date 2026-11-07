using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using Eto.Drawing;
using Eto.Forms;
using Grasshopper;
using Grasshopper.Kernel;
using Dwight.Branding;
using Dwight.Session;

namespace Dwight.UI
{
  /// <summary>THE ONE WINDOW (Brief BR; ADR-046/049): one frameless GH-owned Form holding the home page (dock-shell.html: the chip, History and
  /// Timeline) and at most two conversation pages (<see cref="WebPanel"/>). Collapsed = the SHIRT MARK in a small sharp square pinned to the canvas
  /// corner; drag it left/right to clear other corner widgets. Open = one size, 340 wide, up to 760 tall, inside the canvas. Every decision about
  /// what shows, what survives and what takes focus is <see cref="OneWindowCore"/>'s; this carries them out. GH-owned so it never hides behind
  /// Rhino. Fail-safe; never crashes Rhino.</summary>
  internal sealed class DwightDock : Form
  {
    private static DwightDock _instance;
    private const int ChipW = OneWindowCore.ChipSize, ChipH = OneWindowCore.ChipSize;

    private readonly WebView _web;
    private readonly System.Collections.Generic.List<string> _pending = new System.Collections.Generic.List<string>();
    private readonly UITimer _tick;
    private bool _loaded, _expanded;
    private int _offsetLeft;   // how far left of the default bottom-right corner the user dragged it
    // #179 name fix: refresh on ACTIVE-DOCUMENT IDENTITY, not the DocumentKey. A key-gated refresh missed both a
    // save (id stable, path changes) and a switch between two same-id files (key identical) - so the label went
    // stale. Track the doc instance AND its path; either changing triggers a refresh.
    private GH_Document _lastDoc;
    private string _lastPath;
    private System.Collections.Generic.IReadOnlyList<CommandHistory.CommandView> _view =
      System.Array.Empty<CommandHistory.CommandView>();
    // Brief AP: History's confirmation (while a walk waits) or the last walk's result line, and the document whose undo
    // state the dock listens to, so a row's state follows the stack the moment it moves.
    private string _histConfirm, _histNote;
    private bool _histConfirmBack;
    private GH_Document _undoDoc;

    // Brief BR: the pages. `_expanded` is whether the window is open; a null _faceId is the home page.
    private readonly PixelLayout _layout = new PixelLayout();
    private readonly System.Collections.Generic.Dictionary<string, WebPanel> _pages = new System.Collections.Generic.Dictionary<string, WebPanel>();
    private string _faceId, _runningId, _chipState, _donePage, _doneThread, _liveSignature = "";
    private string _keptId;   // Brief EC unit 5b: a Walk's page, kept behind the chat its step asked
    private bool _asking;     // Brief EC unit 5b: a Walk's Ask is opening its chat now
    private int _quietTicks;
    private bool _doneUnseen;
    private OneWindowCore.Box? _userBox;   // the user's move or resize, kept for the session
    private bool _resizing;
    private string _rEdges = "";
    private int _rMouseX, _rMouseY;
    private OneWindowCore.Box _rStart;

    /// <summary>The one window, attached now if it was not; null when it cannot exist (no WebView2), and then the verbs fall to the Eto popup.</summary>
    internal static DwightDock Host()
    {
      if (_instance == null) Attach();
      return _instance;
    }

    public static void Attach()
    {
      if (_instance != null) return;
      try
      {
        WebViewSupport.ForceWebView2();
        _instance = new DwightDock();
        _instance.Show();
        NativeWindowEffects.ApplyFloating(_instance);
        NativeWindowEffects.SetGhOwner(_instance);  // above GH, hidden from Alt-Tab, never over other apps
        // Runtime round 2: the pre-Show ClientSize is inflated by the host on first creation (the chip
        // appeared at the old wide-rectangle size until the first toggle re-set it). Re-assert the
        // collapsed square AFTER the native handle exists, then pin it back to the corner.
        _instance.ClientSize = new Size(ChipW, ChipH);
        _instance.Reposition();
        CommandHistory.Changed += () => { try { _instance?.Refresh(); } catch { } };
        DwightTheme.Changed += t => { try { _instance?.Run($"dwightSetTheme({Js(t)})"); } catch { } };
        CanvasNotes.ShownChanged += () => { try { _instance?.PushNotes(); } catch { } };
        // Brief ED: the Notes pane follows every note change, and a solution's change of a note's state while the window is open.
        CommandHistory.NotesChanged += () => { try { _instance?.PushNoteList(); } catch { } };
        CanvasNotes.StatesChanged += () => { try { _instance?.OnNoteStates(); } catch { } };
      }
      catch (Exception ex)
      {
        // The dock has no Eto twin, so a WebView2-unavailable ctor throw legitimately means "no dock" (WP-5.6).
        // But the reason USED to be swallowed silently - so a missing chip and a genuine bug looked identical.
        // Log which it is: an InvalidOperationException("WebView2 runtime is not available") is the by-design
        // path; anything else is a real dock bug to chase.
        Rhino.RhinoApp.WriteLine($"Dwight: dock did not attach - {ex.GetType().Name}: {ex.Message}");
        _instance = null;
      }
    }

    /// <summary>The first-run tour's last button ("Open Dwight") points here: the dock draws an amber ring around
    /// itself, pulses it three times, and stops. Static because the caller is a tour window that has just closed and
    /// holds no reference to the dock; a no-op when there is no dock - it needs WebView2, and so does the tour, so
    /// a machine with no dock never reached the tour's last step. It only DRAWS: nothing about it opens the dock or
    /// runs a verb (a first-run surface must not spend a new user's first money). The ring lives inside the chip's
    /// own window, never outside it - see dock-shell.html for why. Never throws.</summary>
    internal static void DrawAttention()
    {
      try { _instance?.Run("dwightDockAttention()"); } catch { }
    }

    private DwightDock()
    {
      // WP-5.6: probe WebView2 first, so a missing runtime throws here (-> Attach's catch, no dock) instead of
      // an async failure leaving a dead blank dock rectangle. The dock is a convenience surface with no Eto twin.
      if (!WebViewSupport.IsWebView2Available())
        throw new InvalidOperationException("WebView2 runtime is not available");

      WindowStyle = WindowStyle.None;
      ShowInTaskbar = false;
      Topmost = false;
      Resizable = false;
      BackgroundColor = Brand.Surface;

      _web = new WebView { BrowserContextMenuEnabled = false };
      WebViewSupport.HardenNavigation(_web);   // WP-2.2: block external navigation
      _web.DocumentLoaded += (s, e) => { _loaded = true; foreach (var js in _pending) Exec(js); _pending.Clear(); Refresh(); };
      _web.MessageReceived += (s, e) => Handle(e.Message);
      _web.DocumentTitleChanged += (s, e) => { var t = e.Title; if (!string.IsNullOrEmpty(t) && t.StartsWith("dwight:", StringComparison.Ordinal)) Handle(t.Substring(7)); };
      _layout.Add(_web, 0, 0);
      Content = _layout;
      ClientSize = new Size(ChipW, ChipH);
      FitViews();
      SizeChanged += (s, e) => FitViews();
      KeyDown += (s, e) => { if (e.Key == Keys.Escape && _expanded) { Step(WindowEvent.Minimise); e.Handled = true; } };

      var html = ShellAssets.Inline(WebViewSupport.LoadResource("Dwight.DockShell.html"));
      if (html != null) _web.LoadHtml(html, null);

      _tick = new UITimer { Interval = 0.7 };  // keep pinned as the GH window moves/resizes
      _tick.Elapsed += (s, e) => { Reposition(); CheckDocSwitch(); PollRun(); };
      _tick.Start();
      Closed += (s, e) => { try { _tick.Stop(); } catch { } };
    }

    private void Handle(string cmd)
    {
      // title fallback may carry a trailing ":timestamp"; postMessage (forced WebView2) carries the clean cmd.
      if (cmd.StartsWith("toggle", StringComparison.Ordinal)) { Step(_expanded ? WindowEvent.Minimise : WindowEvent.ChipClick); return; }
      if (HandleWindowCommand(cmd)) return;
      // Brief ED: the Notes pane's commands. "note:" is never the start of "notes:", so the switch's door below cannot hear one of these.
      if (cmd.StartsWith("note:", StringComparison.Ordinal)) { HandleNote(cmd.Substring(5)); return; }
      // "+ New chat" on the home page: the same door the menu uses, so a new chat during a run opens and does not disturb the running one.
      if (cmd.StartsWith("notes:", StringComparison.Ordinal)) { CanvasNotes.SetShown(cmd.StartsWith("notes:on", StringComparison.Ordinal)); return; }   // the title fallback may trail a timestamp
      if (cmd == "new" || cmd.StartsWith("new:", StringComparison.Ordinal)) { DwightCanvasPopup.Safe(() => DwightScaffold.Open()); return; }
      if (cmd.StartsWith("dockmove:", StringComparison.Ordinal)) { var p = cmd.Substring(9); var c = p.IndexOf(':'); if (c >= 0) p = p.Substring(0, c); if (int.TryParse(p, out var sx)) DragTo(sx); return; }
      if (cmd.StartsWith("hist:", StringComparison.Ordinal)) { HandleHistory(cmd.Substring(5)); return; }
      if (cmd.StartsWith("chat:", StringComparison.Ordinal)) { var t = cmd.Substring(5); var c = t.IndexOf(':'); if (c >= 0) t = t.Substring(0, c); OpenChat(t); return; }
      if (cmd.StartsWith("open:", StringComparison.Ordinal))
      {
        var rest = cmd.Substring(5);
        var colon = rest.IndexOf(':'); if (colon >= 0) rest = rest.Substring(0, colon);
        if (int.TryParse(rest, out var i)) Open(i);
      }
    }

    // ---------------------------------------------------------------- Brief BR: the window

    /// <summary>The commands a page and the home view share. The shell's "close" and Esc are minimise: nothing closes any more.</summary>
    internal bool HandleWindowCommand(string cmd)
    {
      if (cmd == "close" || cmd == "minimise") { Step(WindowEvent.Minimise); return true; }
      if (cmd == "nav:home") { Step(WindowEvent.Home); return true; }
      // A verb's Dismiss is not a minimise: it means this proposal is finished with, and the chip must not bring it back. So the page goes, and
      // the window goes to the chip, which is what the button did when closing was a thing.
      if (cmd == "dismiss") { Step(WindowEvent.Home); Step(WindowEvent.Minimise); return true; }
      if (cmd == "nav:running") { GoToRunning(); return true; }
      if (cmd == "nav:walk") { ShowKeptWalk(null); return true; }   // Brief EC unit 5b: the chat's "Back to the walk"
      if (cmd.StartsWith("drag:", StringComparison.Ordinal)) { MoveTo(cmd.Substring(5)); return true; }
      if (cmd.StartsWith("rdown:", StringComparison.Ordinal)) { BeginResize(cmd.Substring(6)); return true; }
      if (cmd.StartsWith("rmove:", StringComparison.Ordinal)) { DoResize(cmd.Substring(6)); return true; }
      if (cmd == "rup") { _resizing = false; return true; }
      return false;
    }

    /// <summary>The page whose run is going on: read live, so a run started a moment ago counts, or the one the poll still holds through the gap
    /// between a turn and its automatic follow-up.</summary>
    private string RunningId() => (DwightSession.RunningPanel as WebPanel)?.PageId ?? _runningId;

    /// <summary>Ask the core what follows, then do it.</summary>
    private void Step(WindowEvent e, string page = null)
    {
      try
      {
        // Brief EC unit 5b: the Walk's Ask opening its chat, or that chat shown again, keeps the Walk's page behind it.
        if (e == WindowEvent.Open && page != null && (_asking || page == WalkSession.AskedPageId)) e = WindowEvent.AskOpen;
        var step = OneWindowCore.Next(new WindowFacts { Open = _expanded, Face = _faceId, Running = RunningId(), DonePage = _donePage, Kept = _keptId }, e, page);
        if (step.DoneUnseen) { _donePage = page; _doneThread = Page(page)?.ThreadId; }   // before the step may dispose the page
        Apply(step);
      }
      catch (Exception ex) { Rhino.RhinoApp.WriteLine("Dwight: the window could not turn - " + ex.Message); }
    }

    private WebPanel Page(string id) => id != null && _pages.TryGetValue(id, out var p) ? p : null;

    private void Apply(WindowStep step)
    {
      if (step.DoneUnseen) _doneUnseen = true;
      if (step.ClearDoneUnseen) ClearDone();
      // Brief EC unit 4 (Tharun's sitting): minimised, the canvas is the person's. A Walk on a step takes its step off the canvas and keeps everything else; Focus's own chip is not a minimise.
      // A Walk that has only arrived is a finished Explain, and clears as one does (Brief DH).
      if (_expanded && !step.Open) WalkSession.WindowMinimised();
      if (step.ClearGlow) { var p = Page(_faceId) ?? Page(step.Face); if (p != null && !WalkSession.OnAStep(p)) CanvasGlow.ClearIfOwner(p); }
      var restoring = !_expanded && step.Open;
      _keptId = step.Kept;

      var opening = step.Open != _expanded;
      var fromPage = _faceId != null && step.Face == null;
      _expanded = step.Open;
      _faceId = step.Face;
      WebPanel.SetCurrent(Page(_faceId));
      CanvasGlow.PutNumbersOf(Page(_faceId));   // Brief ES unit 1: the page a person turns to is the page they read, so the canvas wears its numbers

      // The size first when opening and the views first when shrinking, so a page never shows at the chip's size.
      if (opening && _expanded) Reposition();
      ShowViews();
      if (opening && !_expanded) Reposition();
      if (opening) Run($"dwightDockExpanded({(_expanded ? "true" : "false")})");
      if (opening && _expanded) PushNoteList();   // Brief ED: a state that changed while the window was the chip is read again as it opens
      // Back from a conversation is History, whatever the home page last showed: the three lines say so.
      if (fromPage) Run("dwightDockHome()");
      if (step.ResetHome) Run("dwightDockHome()");   // sixth gate: under the chip, History whatever the home page last showed

      var gone = Page(step.Dispose);
      if (gone != null) { try { gone.Close(); } catch { } }
      var keptGone = Page(step.CloseKept);
      if (keptGone != null) { try { keptGone.Close(); } catch { } }   // its Walk ends as its page goes

      if (restoring) WalkSession.WindowRestored();   // Brief EC unit 4: the same step, lit again
      if (step.Relight) Page(_faceId)?.Relight();
      if (step.FocusPage) FocusFace();
      if (step.FocusCanvas) GiveKeysToCanvas();
      PushChip();
      // A card that came while the window was open must still ring when the window becomes the chip: that is when it could go unseen.
      if (opening && !_expanded && OneWindowCore.ChipRings(null, _chipState, false)) Run("dwightDockAttention()");
    }

    private void ShowViews()
    {
      var face = _expanded ? Page(_faceId) : null;
      foreach (var p in _pages.Values) try { p.View.Visible = ReferenceEquals(p, face); } catch { }
      try { _web.Visible = face == null; } catch { }
    }

    private void FitViews()
    {
      try
      {
        var size = ClientSize;
        _web.Size = size;
        foreach (var p in _pages.Values) p.View.Size = size;
      }
      catch { }
    }

    private void FocusFace()
    {
      var face = Page(_faceId);
      if (face != null) face.FocusPage();
      else try { _web.Focus(); } catch { }
    }

    /// <summary>Minimise must hand the keyboard back to Grasshopper, or focus stays in the page and the canvas loses its keys (the spike).</summary>
    internal static void GiveKeysToCanvas()
    {
      try
      {
        var editor = Instances.DocumentEditor;
        if (editor != null && editor.Visible) editor.Activate();
        Instances.ActiveCanvas?.Focus();
      }
      catch { }
    }

    private void ClearDone() { _doneUnseen = false; _donePage = null; _doneThread = null; }

    /// <summary>A row was reopened. If it is the conversation that finished out of sight, the user is looking at it now.</summary>
    internal void LookedAt(string threadId)
    {
      if (!_doneUnseen || !OneWindowCore.RowClearsDone(_doneThread, threadId)) return;
      ClearDone();
      PushChip();
    }

    /// <summary>A verb, a new chat or a reopened row: the window turns to this new page.</summary>
    internal void Open(WebPanel panel)
    {
      _pages[panel.PageId] = panel;
      _layout.Add(panel.View, 0, 0);
      panel.View.Size = ClientSize;
      Step(WindowEvent.Open, panel.PageId);
      if (_faceId == panel.PageId) panel.View.Size = ClientSize;   // it opened from the chip: the size changed in that step
    }

    /// <summary>A page closed. Named by the host's own step, it is no longer the face; closed from outside (its watchdog, Restart), the window
    /// goes home.</summary>
    internal void Release(WebPanel panel)
    {
      if (panel == null || !_pages.Remove(panel.PageId)) return;
      try { _layout.Remove(panel.View); } catch { }
      if (_runningId == panel.PageId) _runningId = null;
      if (_donePage == panel.PageId) _donePage = null;   // its turn is still news; looking at it now means reopening its row
      if (_faceId == panel.PageId) Step(WindowEvent.PageClosed, panel.PageId);
    }

    /// <summary>The Restart hard reset: no conversation survives it.</summary>
    internal void CloseConversations()
    {
      foreach (var p in new System.Collections.Generic.List<WebPanel>(_pages.Values)) try { p.Close(); } catch { }
    }

    /// <summary>A conversation left while its turn runs is shown again, live. True when it found one.</summary>
    internal bool ShowLive(string threadId)
    {
      var p = LivePage(threadId);
      if (p == null) return false;
      Step(WindowEvent.Open, p.PageId);
      return true;
    }

    /// <summary>The live page holding this chat, or null.</summary>
    internal WebPanel LivePage(string threadId)
    {
      if (string.IsNullOrEmpty(threadId)) return null;
      foreach (var p in _pages.Values)
        if (p.ThreadId == threadId) return p;
      return null;
    }

    /// <summary>
    /// Brief EC unit 5b: a Walk's Ask opens its chat through <paramref name="door"/>, and the Walk's page stays behind the chat on its step, where "Back to the walk" or the run's History row
    /// finds it. Returns the chat's page.
    /// </summary>
    internal WebPanel KeepingWalk(WebPanel walkPage, Func<WebPanel> door)
    {
      if (walkPage != null && _pages.ContainsKey(walkPage.PageId)) _keptId = walkPage.PageId;
      _asking = true;
      try { return door(); }
      finally
      {
        _asking = false;
        if (_keptId != null && _keptId == _faceId) _keptId = null;   // nothing opened in front of it, so nothing is kept
      }
    }

    /// <summary>Brief EC unit 5b: the kept Walk's page in front again, its step lit as it was. <paramref name="runId"/> null is the chat's own button; otherwise the History row of that run. True when it was shown.</summary>
    internal bool ShowKeptWalk(string runId)
    {
      var kept = Page(_keptId);
      if (kept == null || (runId != null && !string.Equals(kept.RunId, runId, StringComparison.Ordinal))) return false;
      Step(WindowEvent.Open, kept.PageId);
      WalkSession.PageShownAgain();
      return true;
    }

    /// <summary>Brief EC unit 5b: a Walk has ended. Its page, kept behind its chat, goes with it.</summary>
    internal void WalkEnded(WebPanel walkPage)
    {
      if (walkPage == null || walkPage.PageId != _keptId) return;
      _keptId = null;
      if (_faceId != walkPage.PageId) { try { walkPage.Close(); } catch { } }
    }

    private void GoToRunning()
    {
      var running = Page(RunningId());
      if (running == null) { try { DwightToast.Show(OneWindowCore.RunFinishedLine); } catch { } return; }
      Step(WindowEvent.Open, running.PageId);
    }

    /// <summary>A turn's end, seen on the tick. Two quiet ticks, because an automatic follow-up starts a new run just after the last one ended, and
    /// disposing the page in that gap would leave the follow-up writing to nothing.</summary>
    private void PollRun()
    {
      try
      {
        var live = (DwightSession.RunningPanel as WebPanel)?.PageId;
        if (live != null) { _runningId = live; _quietTicks = 0; }
        else if (_runningId != null && ++_quietTicks >= 2)
        {
          var ended = _runningId;
          _runningId = null;
          _quietTicks = 0;
          Step(WindowEvent.TurnEnded, ended);
        }
        var signature = LiveSignature();
        if (signature != _liveSignature) { _liveSignature = signature; Refresh(); }
        PushChip();
      }
      catch { }
    }

    private string RowLive(DwightCommand c) =>
      OneWindowCore.LiveWord(c.ThreadId, (Page(RunningId()))?.ThreadId, DwightScaffold.ThreadHasPendingCard(c.ThreadId));

    private string LiveSignature()
    {
      var sb = new System.Text.StringBuilder();
      foreach (var v in _view) if (!string.IsNullOrEmpty(v.Cmd.ThreadId)) sb.Append(RowLive(v.Cmd)).Append(',');
      return sb.ToString();
    }

    /// <summary>A CARD WAITING MUST NEVER GO UNSEEN. It, or a turn finished out of sight, draws on the chip with the attention ring, and marks its
    /// row (`live`). This reads the active file's rows, because a card belongs to its file.</summary>
    private void PushChip()
    {
      var waiting = false;
      foreach (var v in _view) if (RowLive(v.Cmd) == OneWindowCore.LiveWaiting) { waiting = true; break; }
      var state = OneWindowCore.ChipState(DwightSession.IsBusy, waiting, _doneUnseen);
      if (state == _chipState) return;
      var rings = OneWindowCore.ChipRings(_chipState, state, _expanded);
      _chipState = state;
      Run($"dwightChipState({Js(state)})");
      if (rings) Run("dwightDockAttention()");
    }

    private OneWindowCore.Box? CanvasBox() => CanvasLogical();

    internal static OneWindowCore.Box? CanvasLogical()
    {
      var canvas = Instances.ActiveCanvas;
      if (canvas == null || canvas.Width <= 0) return null;
      var tl = canvas.PointToScreen(System.Drawing.Point.Empty);
      return OneWindowCore.CanvasLogical(tl.X, tl.Y, canvas.Width, canvas.Height, canvas.DeviceDpi);
    }

    // ---- Brief DA: what the Walk's bar lays itself out against ----
    /// <summary>Where this window is now, chip or open, in logical points; null when it does not exist.</summary>
    internal static OneWindowCore.Box? BoxNow()
    {
      var d = _instance;
      if (d == null) return null;
      return new OneWindowCore.Box(d.Location.X, d.Location.Y, d.ClientSize.Width, d.ClientSize.Height);
    }

    internal static bool IsOpenNow => _instance != null && _instance._expanded;

    /// <summary>The bar has just stopped fitting and the panel is now the Walk, so it must be showing. Only that moment opens it: a person who then minimises it again is obeyed.</summary>
    internal void OpenIfClosed() { if (!_expanded) Step(WindowEvent.ChipClick); }

    /// <summary>Brief DH: Focus collapses the window to the chip, the same step the Minimise button takes, so the keyboard goes to the canvas with it.</summary>
    internal void Collapse() { if (_expanded) Step(WindowEvent.Minimise); }

    /// <summary>Brief DH: the chip follows the canvas on this window's tick, and Focus has just maximised the editor and is about to read where the chip is.</summary>
    internal void FollowCanvas() => Reposition();

    /// <summary>Is this the page the window shows, or would show from the chip?</summary>
    internal bool IsFace(WebPanel panel) => panel != null && panel.PageId == _faceId;

    private void MoveTo(string xy)
    {
      var p = xy.Split(':');
      var canvas = CanvasBox();
      if (!_expanded || canvas == null || p.Length != 2 || !int.TryParse(p[0], out var x) || !int.TryParse(p[1], out var y)) return;
      _userBox = OneWindowCore.Move(x, y, ClientSize.Width, ClientSize.Height, canvas.Value);
      Reposition();
    }

    private void BeginResize(string arg)
    {
      var p = arg.Split(':');
      if (!_expanded || p.Length < 3 || !int.TryParse(p[1], out var mx) || !int.TryParse(p[2], out var my)) return;
      _rEdges = p[0];
      _rMouseX = mx;
      _rMouseY = my;
      _rStart = new OneWindowCore.Box(Location.X, Location.Y, ClientSize.Width, ClientSize.Height);
      _resizing = true;
    }

    private void DoResize(string arg)
    {
      if (!_resizing) return;
      var p = arg.Split(':');
      var canvas = CanvasBox();
      if (canvas == null || p.Length < 2 || !int.TryParse(p[0], out var mx) || !int.TryParse(p[1], out var my)) return;
      _userBox = OneWindowCore.Resize(_rEdges, _rStart, mx - _rMouseX, my - _rMouseY, canvas.Value);
      Reposition();
    }

    private void Open(int index)
    {
      if (index < 0 || index >= _view.Count) return;   // index into the SAME provenance view the dock rendered
      DwightSession.Reopen(_view[index].Cmd);
    }

    /// <summary>Brief BQ: History's "Open chat". The id must name a chat row the dock is showing now; anything else opens nothing.</summary>
    private void OpenChat(string threadId)
    {
      if (string.IsNullOrEmpty(threadId)) return;
      foreach (var v in _view)
        if (v.Cmd.ThreadId == threadId) { DwightSession.Reopen(v.Cmd); return; }
    }

    /// <summary>Runtime round 1: the dock lists the ACTIVE file's commands (ADR-049), but nothing told it the
    /// active document changed. #179 name fix: the trigger is the active-document IDENTITY (instance + path),
    /// not the DocumentKey - a save (id stable) and a switch between two same-id files (key identical) both
    /// slipped past a key-gated check. CommandHistory.Changed still covers records / removals / a save re-stamp.</summary>
    private void CheckDocSwitch()
    {
      try
      {
        var doc = Instances.ActiveCanvas?.Document;
        var path = doc?.FilePath ?? "";
        if (ReferenceEquals(doc, _lastDoc) && path == _lastPath) return;
        var switched = !ReferenceEquals(doc, _lastDoc);
        if (switched) { HistoryWalk.Cancel(); _histConfirm = null; _histNote = null; }   // a walk belongs to its document
        _lastDoc = doc; _lastPath = path;
        Refresh();
        if (switched) Step(WindowEvent.DocumentSwitch);   // never jumps an open window; the chip then opens on History
      }
      catch { }
    }

    private void PushNotes() => Run($"dwightDockNotes({(CanvasNotes.Shown ? "true" : "false")})");

    // ---------------------------------------------------------------- Brief ED: the Notes pane

    private string _noteList;

    /// <summary>The active file's notes, sent when they differ from what the page holds. The page is loaded once, so what was last sent is what it shows.</summary>
    private void PushNoteList()
    {
      var json = CanvasNotes.PaneJson(Instances.ActiveCanvas?.Document);
      if (json == _noteList) return;
      _noteList = json;
      Run($"dwightDockNoteList({Js(json)})");
    }

    /// <summary>A solution ended. While the window is the chip nothing shows the pane, so it waits for the window to open.</summary>
    private void OnNoteStates()
    {
      var app = Application.Instance;
      Action a = () => { try { if (_expanded) PushNoteList(); } catch { } };
      if (app != null) app.AsyncInvoke(a); else a();
    }

    /// <summary>The pane's three commands: read it again, go to a note, and New note, which is the chord's Note here at the viewport's centre.</summary>
    private void HandleNote(string rest)
    {
      var colon = rest.IndexOf(':');
      var verb = colon < 0 ? rest : rest.Substring(0, colon);
      var arg = colon < 0 ? "" : rest.Substring(colon + 1);
      var cut = arg.IndexOf(':'); if (cut >= 0) arg = arg.Substring(0, cut);   // the title fallback's ":timestamp"
      if (verb == "list") { PushNoteList(); return; }
      if (verb == "new") { DwightCanvasPopup.Safe(() => { var canvas = Instances.ActiveCanvas; if (canvas?.Document != null) DwightCanvasPopup.NoteHere(canvas.Viewport.MidPoint); }); return; }
      if (verb == "go") CanvasNotes.GoTo(Instances.ActiveCanvas?.Document, arg);
    }

    public void Refresh()
    {
      var doc = Instances.ActiveCanvas?.Document;
      PushNotes();
      PushNoteList();
      Run($"dwightDockFile({Js(FileName(doc))})");
      Run($"dwightChipState({Js(_chipState)})");   // the state holds: a reloaded home page is told again, since it starts with none

      _view = CommandHistory.ForActiveFileView();   // provenance-aware (#179); Open indexes this same list
      var arr = new JsonArray();
      foreach (var v in _view)
      {
        var c = v.Cmd;
        // Brief DG: the row is written by DockRows, the one writer the shell's fixtures are rendered from. A chat's row says what the user asked in it (a thread
        // gone from the store keeps the row's own word, Brief BQ), and the live word is the plugin's. Brief BQ section 9: no cost or tokens in the list, on any row;
        // a stopped or failed run's cost shows in its status line when it is reopened.
        arr.Add(DockRows.Row(c.Action,
          string.IsNullOrEmpty(c.ThreadId) ? c.Subtitle ?? "" : CommandHistory.ThreadTitle(doc, c.ThreadId) ?? c.Subtitle ?? "",
          !string.IsNullOrEmpty(c.ThreadId), RowLive(c), c.State, Ago(c.Time), v.From));
      }
      Run($"dwightDockList({Js(arr.ToJsonString())})");
      RefreshHistory(doc);
      Run($"dwightSetTheme({Js(DwightTheme.Current())})");
    }

    // ---------------------------------------------------------------- Brief AP: History

    /// <summary>History's timeline for the active document, with each change's state read from the live stack now.</summary>
    private void RefreshHistory(GH_Document doc)
    {
      try
      {
        ListenToUndo(doc);
        var snap = ChangeLog.Snapshot(doc);
        var changes = CommandHistory.Changes(doc);
        var waiting = _histConfirm != null && HistoryWalk.Waiting(doc);
        // Brief BQ: the timeline, built GH-free from the changes, the stack and the clock.
        var open = new System.Collections.Generic.HashSet<string>();
        foreach (var v in _view)
          if (!string.IsNullOrEmpty(v.Cmd.ThreadId) && CommandHistory.ThreadTitle(doc, v.Cmd.ThreadId) != null) open.Add(v.Cmd.ThreadId);
        var view = HistoryCore.TimelineView(new TimelineInput
        {
          Changes = changes,
          Stack = snap,
          NowUtc = DateTime.UtcNow,
          TitleOf = id => CommandHistory.ThreadTitle(doc, id),
          CanOpen = open.Contains,
        }, waiting ? _histConfirm : null, HistoryWalk.WaitingIds(doc), _histConfirmBack, _histNote);
        Run($"dwightDockHistory({Js(view.ToJsonString())})");
      }
      catch { }
    }

    /// <summary>A History button: ask (count the walk and show the confirmation), confirm, or cancel.</summary>
    private void HandleHistory(string rest)
    {
      var colon = rest.IndexOf(':');
      var verb = colon < 0 ? rest : rest.Substring(0, colon);
      var arg = colon < 0 ? "" : rest.Substring(colon + 1);
      var cut = arg.IndexOf(':'); if (cut >= 0) arg = arg.Substring(0, cut);   // the title fallback's ":timestamp"
      var doc = Instances.ActiveCanvas?.Document;
      try
      {
        if (verb == "back" || verb == "redo" || verb == "before")
        {
          _histNote = null; _histConfirm = null;
          if (!Guid.TryParse(arg, out var id)) return;
          var back = verb != "redo";
          var line = HistoryWalk.Ask(doc, id, back, verb == "before");
          if (HistoryWalk.Waiting(doc)) { _histConfirm = line; _histConfirmBack = back; } else _histNote = line;
          RefreshHistory(doc);
          return;
        }
        if (verb == "yes")
        {
          _histConfirm = null;
          HistoryWalk.Confirm(doc, line => { _histNote = line; Refresh(); });
          RefreshHistory(doc);
          return;
        }
        if (verb == "no") { HistoryWalk.Cancel(); _histConfirm = null; _histNote = null; RefreshHistory(doc); }
      }
      catch { }
    }

    /// <summary>Follow the active document's undo state, so a row flips to undone the moment the user presses Ctrl-Z.</summary>
    private void ListenToUndo(GH_Document doc)
    {
      if (ReferenceEquals(doc, _undoDoc)) return;
      try { if (_undoDoc != null) _undoDoc.UndoStateChanged -= OnUndoStateChanged; } catch { }
      _undoDoc = doc;
      try { if (doc != null) doc.UndoStateChanged += OnUndoStateChanged; } catch { }
    }

    private void OnUndoStateChanged(object sender, GH_DocUndoEventArgs e)
    {
      var app = Application.Instance;
      Action a = () => { try { RefreshHistory(Instances.ActiveCanvas?.Document); } catch { } };
      if (app != null) app.AsyncInvoke(a); else a();
    }

    /// <summary>Drag the collapsed chip horizontally within a small band so it clears corner widgets;
    /// vertical stays pinned to the bottom. Ignored while open.</summary>
    private void DragTo(int screenX)
    {
      if (_expanded) return;
      try
      {
        var canvas = CanvasBox();
        if (canvas == null) return;
        _offsetLeft = OneWindowCore.ChipNudge(canvas.Value, screenX);   // screenX arrives as a LOGICAL (CSS-pixel) coordinate from the shell
        Reposition();
      }
      catch { }
    }

    /// <summary>The one place the window is placed, on every tick: the chip follows the canvas, and so does the window until the user moves or
    /// resizes it, which holds for the session.</summary>
    private void Reposition()
    {
      try
      {
        OneWindowCore.Box box;
        if (_expanded && _userBox != null) box = _userBox.Value;
        else
        {
          var canvas = CanvasBox();
          if (canvas == null) return;
          box = _expanded ? OneWindowCore.Window(canvas.Value) : OneWindowCore.Chip(canvas.Value, _offsetLeft);
        }
        var size = new Size(box.W, box.H);
        if (ClientSize != size) ClientSize = size;
        var at = new Point(box.X, box.Y);
        if (Location != at) Location = at;
      }
      catch { }
    }

    private static string FileName(GH_Document doc)
    {
      try { var p = doc?.FilePath; if (!string.IsNullOrEmpty(p)) return System.IO.Path.GetFileName(p); } catch { }
      return "Unsaved file";
    }

    private static string Ago(DateTime t)
    {
      var d = DateTime.Now - t;
      if (d.TotalSeconds < 60) return "just now";
      if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes}m ago";
      return $"{(int)d.TotalHours}h ago";
    }

    private void Run(string js) { var app = Application.Instance; Action a = () => { if (_loaded) Exec(js); else _pending.Add(js); }; if (app != null) app.AsyncInvoke(a); else a(); }
    private void Exec(string js) { try { _web.ExecuteScript(js); } catch { } }
    private static string Js(string s) => JsonSerializer.Serialize(s ?? "");
  }
}
