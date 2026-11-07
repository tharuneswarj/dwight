using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.Json.Nodes;
using Dwight.Branding;
using Grasshopper;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Dwight.Config;
using Dwight.Session;

namespace Dwight.UI
{
  /// <summary>
  /// THE WALK (Brief DA, unit U-A of the Explain-on-canvas plan; Tharun's ruling of 1 November 2026): Explain happens on the canvas, one step at a time. The model call does not change: this is
  /// what the plugin does with the steps it already has. <see cref="WalkState"/> holds where the Walk is, and every control and key comes through here. Everything drawn is ephemeral and
  /// read-only (ADR-065); only one Walk is ever live, and it belongs to one page.
  ///
  /// IT ARRIVES QUIETLY (Brief DH). A finished Explain is the summary and the steps in the panel with the whole selection glowing, as it always was, and the Walk's controls beside them. The wash and the
  /// framing begin on the first step the person asks for, never on arrival, and a History replay is the same.
  ///
  /// THE WAYS IT ENDS. The panel's Leave, Esc out of Focus and the panel's summary keep the whole selection lit, as a finished Explain does (<see cref="Leave"/>). Another run starting, the document
  /// changing and its page going have nothing to restore (<see cref="Drop"/>). Every one of them takes Focus's windows back with it. Brief EC (Tharun's sitting of 2 November 2026): the bar's X, its
  /// Focus switch and Esc in Focus are Focus off, and the Walk stays on its step in the panel (<see cref="BarClosed"/>); "Ask about this step" opens the run's chat beside the Walk and ends nothing
  /// (<see cref="Ask"/>), and a turn in that chat ends nothing either (<see cref="RunStarting"/>). Only Leave ends a Walk a person is on.
  ///
  /// WHO TELLS IT. The panel, whose footer holds the controls, and in Focus the bar as well (<see cref="WalkSurface"/>, <see cref="FocusState"/>), so nothing is lost at any size of Grasshopper.
  ///
  /// IT CAN PLAY ITSELF (Brief DD: Solve Replay is a Walk that happens on its own). <see cref="WalkPlay"/> moves the same <see cref="WalkState"/> on a timer, and any key, click or Esc pauses it. While a Walk is live each
  /// component of the step has a bar for its solve time, and the step's line names the slowest. No model call here: the order and the times are Grasshopper's facts. The times are read at the Walk's start and after
  /// each solve, never inside one (U47, U48).
  /// </summary>
  internal static class WalkSession
  {
    private static WalkState _state;
    private static WebPanel _panel;
    private static GH_Canvas _canvas;
    private static System.Windows.Forms.Form _editor;
    private static GH_Document _doc;
    private static Eto.Forms.UITimer _tick;
    private static readonly WalkCanvas _onCanvas = new WalkCanvas(new Glow());   // Brief EC units 2 and 4: the dim at once, and the step off the canvas while minimised
    private static readonly FocusState _focus = new FocusState();   // remembered for this session and never saved
    private static WebPanel _askedPage;   // Brief EC unit 5b: the run's chat, opened from a step, which the Walk lives beside
    private static string _askedThread;

    /// <summary>The step on the canvas, drawn by CanvasGlow with the step's marks; the cover is the Walk's own (<see cref="UpdateCover"/>).</summary>
    private sealed class Glow : IWalkGlow
    {
      public void ShowStep(IReadOnlyList<Guid> step, bool wash) => CanvasGlow.ShowWalkStep(step, _panel?.WalkMarks(_state?.Index ?? -1), _panel, wash);
      public void SetWash(bool wash) => CanvasGlow.SetWalkWash(wash, _panel);
      public void HideStep() => CanvasGlow.HideWalkStep();
    }

    /// <summary>The chat opened from this Walk's step, which may show while the Walk's page waits behind it; null when there is none.</summary>
    public static string AskedPageId => IsLive ? _askedPage?.PageId : null;

    // Brief DD: the play, and the one read of the selection's times.
    private static WalkPlay _play = new WalkPlay();
    private static Eto.Forms.UITimer _pace;
    private static bool _inputHooked;
    private static System.Windows.Forms.Form _inputForm;
    private static WalkTimes _times;
    private static DateTime? _timesAtLocal;
    private static DateTime? _waitingSinceUtc;
    private static bool _gaveUp;

    public static bool IsLive => _state != null && _state.Live;

    /// <summary>Brief EI: the live page shows its deck, and the Walk's steps are the deck's cards.</summary>
    private static bool IsDeck => _panel?.DeckShown == true;

    /// <summary>The page whose Walk is live, for the bar's commands; null when none is.</summary>
    internal static WebPanel LivePanel => IsLive ? _panel : null;

    /// <summary>The windows are arranged for Focus now, so the bar may show.</summary>
    public static bool FocusActive => _focus.Active;

    /// <summary>Is this page's Walk the live one? Its keys and its controls are the Walk's.</summary>
    public static bool Owns(WebPanel panel) => panel != null && IsLive && ReferenceEquals(panel, _panel);

    /// <summary>Has this page's Walk reached a step? Then the glow is the Walk's, and the window leaves it alone. Before that it is the whole selection's, as on any finished Explain.</summary>
    public static bool OnAStep(WebPanel panel) => Owns(panel) && _state.Started;

    /// <summary>The run finished, and the person is still on that result. Its Walk has arrived: the controls are there and the canvas has not moved.</summary>
    public static void RunFinished(WebPanel panel)
    {
      if (panel == null || DwightDock.Host()?.IsFace(panel) != true) return;
      Arrive(panel);
    }

    /// <summary>A finished Explain reopened from History arrives the same way. During a run it is only being read, and a step card lights its step as it always has.</summary>
    public static void Reopened(WebPanel panel)
    {
      if (panel == null || DwightSession.IsBusy) return;
      Arrive(panel);
    }

    private static void Arrive(WebPanel panel)
    {
      Drop();
      // Brief EI: a deck read back from history, or one whose every card is answered, shows its result and opens no card: a reopened run never offers what it cannot honour.
      if (panel.DeckShown && (panel.Deck.Reopened || panel.Deck.AllAnswered || !panel.Deck.Dealt)) { panel.ShowDeckResult(); return; }
      if (!Attach(panel)) return;
      _panel.MarkWalkArrived(_state.Count);
      PushDeck();   // Brief EI: card 0, the idea, and nothing on the canvas changes until Start
      TellPanel();
      PushSwitches();
    }

    /// <summary>A step card or a dot. On a finished Explain, the card is the way back in.</summary>
    public static void GoTo(WebPanel panel, int i)
    {
      if (panel?.DeckShown == true && !panel.Deck.MayOpen(i)) return;   // Brief EI: a deck is walked in order; a card opens once those before it are answered
      if (!Owns(panel))
      {
        // Reading a finished Explain while something else runs: one thing at a time, so its card lights its step as it always has, with no bar and no wash over the running work.
        if (DwightSession.IsBusy) { LightOnly(panel, i); return; }
        Begin(panel, i, NavReason.UserClicked);
        return;
      }
      Interrupt();
      if (_state.GoTo(i)) { _play.UserMoved(); Step(NavReason.UserClicked); }
    }

    private static void LightOnly(WebPanel panel, int i)
    {
      var steps = panel?.WalkSteps;
      if (steps == null || i < 0 || i >= steps.Count) return;
      CanvasGlow.ShowMarks(steps[i], panel.WalkMarks(i), panel);
      CanvasNav.GoTo(steps[i], NavReason.UserClicked);
      panel.SetLensStep(i);
      TreeLens.SetCurrent(steps[i]);
    }

    public static void GoTo(int i) { if (IsLive) GoTo(_panel, i); }
    public static void Next()
    {
      Interrupt();
      if (!IsLive) return;
      if (IsDeck && _state.Started)
      {
        if (!_panel.Deck.IsAnswered(_state.Index)) return;   // Brief EI: the right arrow opens the next card once this one is answered
        if (_state.Index >= _state.Count - 1) { Leave(false); return; }   // and after the last, the result card
      }
      if (_state.Next()) { _play.UserMoved(); Step(NavReason.UserClicked); }
    }
    public static void Back() { Interrupt(); if (IsLive && _state.Back()) { _play.UserMoved(); Step(NavReason.UserClicked); } }

    /// <summary>
    /// The Focus control. On a Walk that has only arrived it starts step 1 as well, because the bar has to have a step to show, and pressing Focus is as plain an ask as pressing Next. Off, the Walk carries
    /// on in the panel on the same step.
    /// </summary>
    public static void ToggleFocus()
    {
      switch (_focus.Toggle(IsLive))
      {
        case FocusDo.Engage:
          if (!_state.Started) _state.Next();
          Engage();
          Step(NavReason.UserClicked);
          break;
        case FocusDo.Restore:
          WalkFocus.Restore();
          if (_state?.Started == true) Tell(NavReason.UserClicked);
          break;
      }
      PushSwitches();
    }

    /// <summary>
    /// The bar's X, its Focus switch, and Esc while the bar shows (Brief EC unit 1). Focus off, the windows back, and the panel on the same step with its footer, the step lit and washed as it was.
    /// Turning Focus off is a choice; ending the Walk is the panel's Leave, named and one click on. The bar had the keyboard, so it hands it back.
    /// </summary>
    public static void BarClosed()
    {
      if (_focus.BarClosed() != FocusDo.Restore) { PushSwitches(); return; }
      WalkFocus.Restore();
      try
      {
        if (_state?.Started == true) Tell(NavReason.UserClicked);
      }
      catch (Exception ex)
      {
        try { Rhino.RhinoApp.WriteLine("Dwight: the walk stopped - " + ex.Message); } catch { }
        Drop();
      }
      PushSwitches();
      DwightDock.GiveKeysToCanvas();
    }

    /// <summary>The Dim switch, from the panel, the bar or Settings (Brief EC unit 2): saved, and the step showing takes it in the same call. The next step reads it too.</summary>
    public static void SetDim(bool on)
    {
      var settings = DwightSettings.Current;
      settings.WalkDim = on;
      settings.Save();
      if (IsLive) _onCanvas.DimChanged(on);
      PushSwitches();
    }

    /// <summary>
    /// Brief EC unit 4: the window went to its chip with a step lit. The step comes off the canvas and the Walk, its step, its page and its chat stay as they were; it does not play on to a canvas
    /// that does not show it. Focus's own collapse is not this: in Focus the bar is the Walk (<see cref="WalkCanvas"/>).
    /// </summary>
    public static void WindowMinimised()
    {
      if (!IsLive || !_state.Started) return;
      if (_focus.Active) return;
      Interrupt();
      _onCanvas.WindowMinimised(false);
    }

    /// <summary>Brief EC unit 4: the window came back. The same step is lit again as <c>Show</c> lights it, with no model call, no move of the step and no framing. Focus is not entered by this.</summary>
    public static void WindowRestored()
    {
      if (!IsLive || !_state.Started) return;
      if (_onCanvas.Relight(DwightSettings.Current.WalkDim)) UpdateCover();
    }

    /// <summary>Brief EC unit 5b: the Walk's page is in front again after its chat. Anything the chat lit is replaced by the step, and the lens comes back if a glow of the chat's ended it.</summary>
    public static void PageShownAgain()
    {
      if (!IsLive || !_state.Started) return;
      var i = _state.Index;
      _onCanvas.Relight(DwightSettings.Current.WalkDim, force: true);
      UpdateCover();
      try { _panel.ResumeLens(); } catch { }   // a lens begun again sits above the steps until it is told which one
      _panel.SetLensStep(i);
      TreeLens.SetCurrent(_state.Current);
      TellPanel();
      PushSwitches();
    }

    /// <summary>
    /// A run is starting (Brief EC unit 5b). A turn in the chat this Walk's step opened leaves the Walk on its step, with the step lit, so the person can read the answer beside it and walk on;
    /// returns true then, and the run does not light the canvas. Anything else ends the Walk, as it always has: its glow is about to be replaced.
    /// </summary>
    public static bool RunStarting(WebPanel adopt)
    {
      LlmActions.StopDeckCall();   // Brief EO: a new run stops a deck's call in flight (a judge, Play again)
      if (IsLive && _state.Started && adopt != null && !string.IsNullOrEmpty(_askedThread) && string.Equals(adopt.ThreadId, _askedThread, StringComparison.Ordinal)) return true;
      Drop();
      return false;
    }

    /// <summary>Brief DD: the bar's Play, which is Pause while playing. Starting is the person's click, so a restart from the last step frames as a click does.</summary>
    public static void TogglePlay()
    {
      if (!IsLive) return;
      if (Interrupt()) return;
      if (!CanPlay()) return;
      if (_play.Start(_state)) Step(NavReason.UserClicked); else Redraw();
      Arm();
    }

    /// <summary>Any input pauses a Walk that is playing: a key, a click on the canvas or the bar, a move of the wheel. Returns whether it was playing. It never eats what paused it.</summary>
    public static bool Interrupt()
    {
      if (!_play.Pause()) return false;
      Arm();
      Redraw();
      return true;
    }

    /// <summary>The panel's half of the same state: Play or Pause on its footer, and the line under its current card.</summary>
    private static void TellPanel()
    {
      if (!IsLive || _panel == null) return;
      try
      {
        _panel.SetWalkPlaying(_play.Playing, CanPlay());
        _panel.SetWalkLine(WalkText.PanelLine(_play.Ended, TimeLine()));
      }
      catch { }
    }

    private static string TimeLine() => WalkTimeLine.For(_times, _state.Current, _timesAtLocal, _waitingSinceUtc != null, _gaveUp);

    /// <summary>Set the timer for this step's pace, or stop it. The canvas's mouse is only hooked while playing, so it can pause nothing and cost nothing otherwise.</summary>
    private static void Arm()
    {
      try { _pace?.Stop(); } catch { }
      if (!IsLive || !_play.Playing) { HookInput(false); return; }
      HookInput(true);
      if (_pace == null) { _pace = new Eto.Forms.UITimer(); _pace.Elapsed += OnPace; }
      _pace.Interval = WalkPace.Seconds(_state.Current.Count, WalkText.PlainText(_panel.WalkHtml(_state.Index)).Length);
      _pace.Start();
    }

    private static void OnPace(object sender, EventArgs e)
    {
      try { _pace?.Stop(); } catch { }
      if (!IsLive || !_play.Playing) return;
      if (_play.Advance(_state)) Step(NavReason.UserClicked); else Redraw();
      Arm();
    }

    /// <summary>The lens's toggle moved, from the canvas, the panel or the bar. The bar draws what it is told, so it is told.</summary>
    public static void LensChanged() { if (IsLive) Redraw(); }

    private static void HookInput(bool on)
    {
      if (on == _inputHooked || _canvas == null) { if (_canvas == null) _inputHooked = false; return; }
      _inputHooked = on;
      try
      {
        if (on) { _canvas.MouseDown += OnCanvasInput; _canvas.MouseWheel += OnCanvasInput; }
        else { _canvas.MouseDown -= OnCanvasInput; _canvas.MouseWheel -= OnCanvasInput; }
        if (_inputForm != null) _inputForm.KeyDown -= OnFormKey;
        _inputForm = on ? _canvas.FindForm() : null;
        if (_inputForm != null) _inputForm.KeyDown += OnFormKey;
      }
      catch { }
    }

    private static void OnCanvasInput(object sender, System.Windows.Forms.MouseEventArgs e) => Interrupt();

    /// <summary>Grasshopper's form sends a bare letter or digit to Rhino's command line and marks it handled, so the canvas's own KeyDown never hears one; a second handler on the form still does, and the letter goes on to Rhino as it always did. Only a key it kept from the canvas: the rest reach <see cref="OnCanvasKey"/>, where Esc only pauses, and pausing here first would make that Esc leave.</summary>
    private static void OnFormKey(object sender, System.Windows.Forms.KeyEventArgs e) { if (e.Handled) Interrupt(); }

    /// <summary>Both surfaces again, with no move and no frame: the play's state or the times have changed.</summary>
    private static void Redraw()
    {
      try { if (IsLive && _focus.Active) WalkBar.Get()?.Present(ViewNow()); } catch { }
      TellPanel();
    }

    /// <summary>A lesson stops on each card for its answer, and a timer would walk past it. So Play is not offered during one.</summary>
    private static bool CanPlay() => WalkPlay.CanPlay(_state.Count, _panel?.Deck != null);

    /// <summary>Read every component's time once. Never inside a solution: while one runs the read waits for its end, on a clock (<see cref="WalkTimeLine"/>), and the line says so.</summary>
    private static void ReadTimes()
    {
      var doc = _doc;
      if (doc == null || _state == null) return;
      try
      {
        if (doc.SolutionState == GH_ProcessStep.Process)
        {
          if (_times == null && _waitingSinceUtc == null) _waitingSinceUtc = DateTime.UtcNow;
          return;
        }
        var ids = WalkScope.Timed(_state.Source, _panel?.WalkSteps);
        var facts = new List<TimeFact>();
        foreach (var id in ids)
          if (doc.FindObject(id, true) is IGH_ActiveObject active)
            facts.Add(new TimeFact { Guid = id, Label = TreeLens.LabelOf(active), Recorded = Dwight.Agent.GhSolveTiming.HasComputed(active), Ms = Dwight.Data.Payloads.ProcessorMs(doc, id) });
        _times = new WalkTimes(facts);
        _timesAtLocal = DateTime.Now;
        _waitingSinceUtc = null;
        _gaveUp = false;
        _panel?.SetWalkTotal(_times.SummaryLine());
      }
      catch { }
    }

    private static void OnSolutionEnd(object sender, GH_SolutionEventArgs e)
    {
      try
      {
        var canvas = _canvas;
        if (IsLive && canvas != null && canvas.IsHandleCreated)
          canvas.BeginInvoke((Action)(() => { if (!IsLive) return; ReadTimes(); Redraw(); try { canvas.Invalidate(); } catch { } }));
      }
      catch { }
    }

    /// <summary>The tick holds the wait's clock: a solve that ended unseen is read now, and one that has outlasted the clock is said to have.</summary>
    private static void CheckWait()
    {
      if (_waitingSinceUtc == null || _doc == null) return;
      if (_doc.SolutionState != GH_ProcessStep.Process) { ReadTimes(); Redraw(); return; }
      if (_gaveUp || !WalkTimeLine.WaitIsOver(_waitingSinceUtc.Value, DateTime.UtcNow)) return;
      _gaveUp = true;
      Redraw();
    }

    /// <summary>The bars under the step's components, and a dot under one that has never run, from live bounds at every paint.</summary>
    internal static void PaintTimes(Graphics g, GH_Document doc)
    {
      if (!IsLive || _times == null || !ReferenceEquals(doc, _doc) || IsDeck) return;   // Brief EI: a card is a question, not a timing
      var step = _state.Current.Select(id => (id, doc.FindObject(id, true)?.Attributes?.Bounds)).ToList();
      using (var ink = new SolidBrush(Color.FromArgb(TimeBars.InkAlpha, UiTokens.TealWire)))
      {
        foreach (var bar in TimeBars.Bars(_times, step)) g.FillRectangle(ink, bar);
        foreach (var dot in TimeBars.Dots(_times, step)) g.FillEllipse(ink, dot);
      }
    }

    /// <summary>Esc, the close, the summary. The whole selection stays lit, with its badges, as a finished Explain leaves it. A bar that had the keyboard hands it back, or it goes with the hidden window.</summary>
    public static void Leave(bool fromBar)
    {
      var panel = _panel;
      var source = _state?.Source;
      var deck = IsDeck;
      if (!End()) return;
      if (panel != null && !panel.IsClosed && source != null && source.Count > 0) CanvasGlow.Show(source, false, panel);   // a page kept behind its chat went with the Walk, and lights nothing
      if (deck && panel != null && !panel.IsClosed) panel.ShowDeckAfterWalk();   // Brief EI: the result once a card is answered, else card 0 again; never a card with no Walk behind it
      if (fromBar) DwightDock.GiveKeysToCanvas();
    }

    /// <summary>Another run, another document, or the page has gone: the layer goes, and whatever comes next lights its own.</summary>
    public static void Drop()
    {
      if (End()) CanvasGlow.ClearWalkLayer();
    }

    /// <summary>A key from the bar or the panel. A key the dispatcher does not take does nothing.</summary>
    public static void PageKey(string domKey, bool fromBar)
    {
      var key = WalkKeys.Dispatch(WalkKeys.CodeOf(domKey), false, IsLive);
      Do(Interrupt() ? WalkPlay.WhilePlaying(key) : key, fromBar);
    }

    /// <summary>
    /// "Ask about this step" (Brief EC units 5 and 5b): its components selected, and the RUN's chat open, the one chat every door from this run reaches, carrying the run (the first Ask makes it,
    /// seeded through the model door), with the step's sentence in the composer, unsent, in the chat's own ids. A second Ask from another step changes the draft and nothing else.
    ///
    /// THE WALK STAYS ON ITS STEP. The chat shows in the one window and the Walk's page is kept behind it; "Back to the walk" in the chat, or the run's row in History, brings it back. Only Leave
    /// ends a Walk (Rhino 2's measurement of the seventeenth: the Ask ended it).
    /// </summary>
    public static void Ask()
    {
      if (_state?.Started != true || _panel == null) return;
      var panel = _panel;
      var index = _state.Index;
      var deck = IsDeck ? panel.Deck : null;
      Select(_state.Current);
      // Brief EI: on a card, the question and, once it is turned, the answers; on a step, its sentence. Either way in the chat's own ids.
      Func<WebPanel> door = () => DwightScaffold.GraduateFrom(panel.HandOff(), ids => deck != null ? Dwight.Agent.RunIds.RewriteMarkdown(deck.AskDraft(index), ids) : WalkText.AskLine(index, panel.WalkHtml(index), ids));
      var host = DwightDock.Host();
      var chat = host != null ? host.KeepingWalk(panel, door) : door();
      if (!IsLive || !ReferenceEquals(panel, _panel)) return;   // the door ended the Walk (a page that would not load)
      _askedPage = chat;
      _askedThread = chat?.ThreadId;
      try { chat?.SetWalkBack(WalkText.BackToWalk); } catch { }
    }

    /// <summary>The surface moved, the bar having been told first. The card takes the gone line or gives it back. The bar stopping fitting is the one moment the window opens by itself: otherwise a person whose Grasshopper just got narrow would have the wash and nothing to read.</summary>
    public static void SurfaceChanged(WalkSurface was, WalkSurface now)
    {
      if (!IsLive) return;
      if (now == WalkSurface.Hidden) Interrupt();   // Brief DD: a Walk does not go on playing to a canvas nobody can see
      Mark(now);
      PushDeck();   // Brief EI: the deck goes with the Walk to wherever it is told
      if (was == WalkSurface.Bar && now == WalkSurface.Panel) DwightDock.Host()?.OpenIfClosed();
    }

    /// <summary>The tick: a Walk belongs to its document, and in Focus the one window moves without an event here.</summary>
    public static void Tick()
    {
      if (!IsLive) return;
      var doc = Instances.ActiveCanvas?.Document;
      if (WalkGeometry.DocumentEnds(WalkBar.CanvasShowingNow(), doc != null, ReferenceEquals(doc, _doc))) { Drop(); return; }
      CheckWait();
      if (_play.Playing && !WalkBar.CanvasShowingNow()) Interrupt();   // Brief DD's law with no bar to report it: a Walk does not go on playing to a canvas nobody can see
      if (_focus.Active && WalkFocus.ViewClosedByHand()) FocusViewClosed();
      if (_focus.Active) Replace();
    }

    /// <summary>
    /// The person closed Focus's view by hand (the twelfth gate). Focus goes off as the switch would turn it off, the switch follows, and the Walk carries on in the panel on the same step, still playing if it was: nothing in Play reads the view.
    /// The windows moving is the person's act answered, so it says why. Rhino's modified mark from that close stays, because the view was not ours to close.
    /// </summary>
    private static void FocusViewClosed()
    {
      if (_focus.ViewClosed() != FocusDo.Restore) return;
      WalkFocus.Restore();
      try
      {
        if (_state?.Started == true) Tell(NavReason.UserClicked);
      }
      catch (Exception ex)
      {
        try { Rhino.RhinoApp.WriteLine("Dwight: the walk stopped - " + ex.Message); } catch { }
        Drop();
      }
      PushSwitches();
      try { DwightToast.Show(FocusText.ViewClosed); } catch { }
    }

    private static void Begin(WebPanel panel, int i, NavReason why)
    {
      Drop();
      var steps = panel?.WalkSteps;
      if (steps == null || i < 0 || i >= steps.Count) return;
      if (!Attach(panel)) return;
      _state.GoTo(i);
      PushSwitches();
      Step(why);
    }

    /// <summary>This page's Walk is the live one, with no step yet: the hooks go on for its life only, so they can never eat a key outside one.</summary>
    private static bool Attach(WebPanel panel)
    {
      var steps = panel?.WalkSteps;
      if (steps == null) return false;
      var state = new WalkState(steps, panel.WalkSource);
      if (!state.Live) return false;
      _state = state;
      _panel = panel;
      _onCanvas.Ended();
      _canvas = Instances.ActiveCanvas;
      _editor = Instances.DocumentEditor;
      _doc = _canvas?.Document;
      _play = new WalkPlay();
      _times = null;
      _timesAtLocal = null;
      _waitingSinceUtc = null;
      _gaveUp = false;
      panel.Closed += OnPageClosed;
      if (_doc != null) { try { _doc.SolutionEnd += OnSolutionEnd; } catch { } }
      ReadTimes();
      if (_canvas != null)
      {
        _canvas.KeyDown += OnCanvasKey;
        _canvas.Move += OnPlaceChanged;
        _canvas.SizeChanged += OnPlaceChanged;
        _canvas.VisibleChanged += OnPlaceChanged;
      }
      if (_editor != null)
      {
        _editor.Move += OnPlaceChanged;
        _editor.Resize += OnPlaceChanged;
        _editor.VisibleChanged += OnPlaceChanged;
      }
      if (_tick == null)
      {
        _tick = new Eto.Forms.UITimer { Interval = 0.7 };
        _tick.Elapsed += (s, e) => Tick();
      }
      _tick.Start();
      return true;
    }

    private static bool End()
    {
      LlmActions.StopDeckCall();   // Brief EO: a judge still reading an answer is stopped when the Walk ends, and its card says it could not judge
      if (_state == null || !_state.Leave()) return false;
      try
      {
        try { _tick?.Stop(); } catch { }
        _play.Pause();
        try { _pace?.Stop(); } catch { }
        HookInput(false);
        if (_doc != null) { try { _doc.SolutionEnd -= OnSolutionEnd; } catch { } }
        _times = null;
        _waitingSinceUtc = null;
        if (_canvas != null)
        {
          try
          {
            _canvas.KeyDown -= OnCanvasKey;
            _canvas.Move -= OnPlaceChanged;
            _canvas.SizeChanged -= OnPlaceChanged;
            _canvas.VisibleChanged -= OnPlaceChanged;
          }
          catch { }
        }
        if (_editor != null)
        {
          try
          {
            _editor.Move -= OnPlaceChanged;
            _editor.Resize -= OnPlaceChanged;
            _editor.VisibleChanged -= OnPlaceChanged;
          }
          catch { }
        }
        if (_panel != null) { _panel.Closed -= OnPageClosed; try { _panel.MarkWalkStep(-1); _panel.SetLensStep(-1); _panel.SetWalkTotal(null); _panel.SetWalkPlaying(false); _panel.SetWalkLine(null); } catch { } }
        try { CanvasGlow.SetHatch(null); } catch { }   // Brief DE: the cover goes with the Walk
        try { TreeLens.SetCurrent(null); } catch { }   // Brief DB: the step's chips go with the Walk, and T stays available
        try { WalkBar.Existing?.Dismiss(); } catch { }
        try { _askedPage?.SetWalkBack(null); } catch { }   // Brief EC: the chat has no Walk to go back to
      }
      finally
      {
        // Brief DH: A FOCUS ON ALWAYS HAS ITS FOCUS OFF, however the Walk ended. After the state has gone, so nothing the windows' moving sets off reads a Walk that has left.
        var page = _panel;
        _state = null; _panel = null; _canvas = null; _editor = null; _doc = null;
        _askedPage = null; _askedThread = null;
        _onCanvas.Ended();
        if (_focus.WalkEnded() == FocusDo.Restore) WalkFocus.Restore();
        try { DwightDock.Host()?.WalkEnded(page); } catch { }   // Brief EC unit 5b: a page kept behind its chat goes with its Walk
      }
      return true;
    }

    /// <summary>Show the current step, and if that fails end the Walk, whose ending takes Focus's windows back: a Walk that broke must not leave Grasshopper rearranged.</summary>
    private static void Step(NavReason why)
    {
      LlmActions.StopDeckCall(_state.Index);   // Brief EO: leaving a card stops the judge reading its answer
      try { Show(why); }
      catch (Exception ex)
      {
        try { Rhino.RhinoApp.WriteLine("Dwight: the walk stopped - " + ex.Message); } catch { }
        Drop();
      }
    }

    private static void Show(NavReason why)
    {
      if (_focus.StepShown() == FocusDo.Engage) Engage();   // before the bar is asked, because it reads Focus; the first step of a Walk in a session that wanted it
      var i = _state.Index;
      var guids = _state.Current;
      _onCanvas.StepShown(guids, DwightSettings.Current.WalkDim);
      Tell(why);
      PushSwitches();   // Settings may have changed the dim, and a decline may have turned Focus off
      _panel.SetLensStep(i);   // before the lens is told, because telling it redraws its lines under this step
      TreeLens.SetCurrent(IsDeck && !_panel.Deck.IsAnswered(i) ? null : _state.Current);   // Brief DB: the Tree Lens rides the current step; Brief EI: a card's wires wait until it is answered
    }

    /// <summary>The surfaces say the step, and the view goes to it clear of whatever they take. No glow here: the step is lit as it was, and the dim already follows its switch (Brief EC).</summary>
    private static void Tell(NavReason why)
    {
      var surface = Present();
      UpdateCover();
      var (bottom, right) = WalkBar.Clears(surface);
      CanvasNav.FrameStep(_state.Current, why, bottom, right);
      Mark(surface);
      TellPanel();   // after the mark, because the line goes under the card it has just made current
      PushDeck();   // Brief EI: the card the Walk is on, after Mark
    }

    /// <summary>The bar only in Focus. By default it is not even made: the panel is the Walk.</summary>
    private static WalkSurface Present()
    {
      if (_focus.Active && !IsDeck) return WalkBar.Get()?.Present(ViewNow()) ?? WalkSurface.Panel;   // Brief EO unit 6: in Learn, Focus keeps the panel as the card and opens no bar
      return WalkGeometry.SurfaceFor(false, WalkBar.CanvasShowingNow(), null, null);
    }

    /// <summary>Arrange the windows, or be told why not. The sentence reaches the person on a toast, because the footer's status line gives way during a Walk and the window may have just collapsed.</summary>
    private static void Engage()
    {
      var arranged = WalkFocus.Engage(IsDeck, out var say);
      if (!arranged) _focus.Declined();
      if (!string.IsNullOrEmpty(say)) { try { DwightToast.Show(say); } catch { } }
    }

    /// <summary>The canvas or the one window moved while Focus is arranged: the bar and the viewport follow.</summary>
    private static void Replace()
    {
      if (!IsDeck) { try { WalkBar.Get()?.Sync(); } catch { } }
      WalkFocus.Place();
    }

    /// <summary>The panel's switches and the bar's, from the same two facts, so the two cannot disagree (Brief EC unit 3).</summary>
    private static void PushSwitches()
    {
      try { _panel?.MarkWalkSwitches(_focus.Wanted, DwightSettings.Current.WalkDim); } catch { }
      try { WalkBar.Existing?.MarkSwitches(_focus.Active, DwightSettings.Current.WalkDim); } catch { }
    }

    /// <summary>The bar's document for the current step. Brief EI: on a deck, the card, drawn by the bar as the panel draws it.</summary>
    private static string ViewNow()
    {
      var i = _state.Index;
      var deck = IsDeck ? _panel.Deck.CardView(i) : null;
      return WalkText.View(i, _state.Count, deck != null ? "" : _panel.WalkHtml(i), GoneNow(), _play.Playing, _play.Ended, TimeLine(), CanPlay(),
        TreeLens.IsLive ? TreeLens.ShowsAll : (bool?)null, _focus.Active, DwightSettings.Current.WalkDim, deck);
    }

    /// <summary>Brief EI: the panel draws the deck where the Walk is: card 0 before a card is opened, else the card. Both surfaces draw from the one state, so they cannot disagree.</summary>
    private static void PushDeck()
    {
      if (!IsDeck) return;
      try { _panel.ShowDeck(_state.Started ? _panel.Deck.CardView(_state.Index) : _panel.DeckStart()); } catch { }
    }

    /// <summary>The cover over the step's value, while its question is open; none otherwise.</summary>
    private static void UpdateCover()
    {
      var cover = IsDeck ? _panel.Deck.CoverOf(_state.Index) : null;
      CanvasGlow.SetHatch(cover.HasValue ? new[] { cover.Value } : null);
    }

    /// <summary>
    /// Brief EI: an answer to the card the Walk is on, from the panel or the bar. The deck grades it; both surfaces are drawn again without moving the canvas, the cover lifts
    /// and the card's wires may show in the Lens. The answers go into the run's record at once, and the score onto its row when the last card is answered: numbers and the
    /// answers given, never in a payload. A typed answer that is not a number turns nothing and is said on the card.
    /// </summary>
    public static void DeckAnswer(WebPanel panel, string answer)
    {
      if (!Owns(panel) || !IsDeck || !_state.Started) return;
      var deck = _panel.Deck;
      var outcome = deck.Answer(_state.Index, answer);
      if (outcome == DeckOutcome.None) return;
      if (outcome == DeckOutcome.Judging) LlmActions.JudgeAnswer(_panel, _state.Index);   // Brief EO unit 2: own words go to the judge; the card shows it is judging
      else if (outcome != DeckOutcome.NotANumber)
      {
        try { CommandHistory.SetDeck(_doc, _panel.RunRow, deck.ToJson(), deck.Right, deck.Count, deck.AllAnswered); } catch { }
        UpdateCover();
        try { TreeLens.SetCurrent(_state.Current); } catch { }
      }
      Redraw();
      PushDeck();
    }

    /// <summary>Brief EO unit 2: the judge has answered, failed or been stopped, and the deck holds what it said. The answer goes into the record when it turned the card, and both surfaces draw it.</summary>
    public static void DeckJudged(WebPanel panel)
    {
      var deck = panel?.Deck;
      if (deck == null) return;
      try { CommandHistory.SetDeck(Instances.ActiveCanvas?.Document, panel.RunRow, deck.ToJson(), deck.Right, deck.Count, deck.AllAnswered); } catch { }
      if (!Owns(panel)) return;
      UpdateCover();
      try { TreeLens.SetCurrent(_state.Current); } catch { }
      Redraw();
      PushDeck();
    }

    /// <summary>
    /// Brief EO unit 5: a name on a card, clicked. The canvas goes to that component and it is lit, glow only: it never selects (#322), so the person's next action still operates on their selection.
    /// On an open card the component is already lit, because a card lights every component its words name, so the view only moves; with no Walk live it is lit as a missed row is.
    /// </summary>
    public static void DeckLook(WebPanel panel, int id)
    {
      var deck = panel?.Deck;
      if (deck == null || !deck.TryGuid(id, out var guid)) return;
      var doc = Instances.ActiveCanvas?.Document;
      if (doc?.FindObject(guid, true) == null) return;   // a component deleted since: nothing moves, as a retired anchor in the chat
      var one = new[] { guid };
      if (!Owns(panel)) CanvasGlow.ShowMarks(one, null, panel);
      CanvasNav.GoTo(one, NavReason.UserClicked);
    }

    /// <summary>Brief EO unit 4: Understand, a missed card at a time, with no new call; -1 is back to the score. The card's components lit and in view, glow only, as a missed row's are.</summary>
    public static void DeckUnderstand(WebPanel panel, int to)
    {
      var deck = panel?.DeckShown == true ? panel.Deck : null;
      if (deck == null || Owns(panel)) return;
      var at = deck.UnderstandGo(to);
      if (at < 0) { panel.ShowDeckResult(); return; }
      var view = deck.UnderstandView();
      panel.ShowDeck(view);
      var lights = deck.Cards[deck.MissedCards[at]].Lights;
      if (lights.Count > 0) { CanvasGlow.ShowMarks(lights, null, panel); CanvasNav.GoTo(lights, NavReason.UserClicked); }
    }

    /// <summary>Brief EI: the right arrow, from the panel, which may hold card 0 or the result with no Walk live.</summary>
    public static void DeckNext(WebPanel panel)
    {
      if (panel?.DeckShown != true) return;
      if (Owns(panel)) { Next(); return; }
      if (!panel.Deck.AllAnswered && !panel.Deck.Reopened) GoTo(panel, panel.Deck.FirstOpen);   // card 0's arrow is its Start
    }

    /// <summary>Brief EI: the left arrow. From the result it reopens the last card, unless the deck was read back from history.</summary>
    public static void DeckBack(WebPanel panel)
    {
      if (panel?.DeckShown != true) return;
      if (Owns(panel)) { Back(); return; }
      if (panel.Deck.Answered > 0 && !panel.Deck.Reopened) GoTo(panel, panel.Deck.Count - 1);
    }

    /// <summary>Brief EI: Try again. The same cards, their options in a new order, every answer cleared, and the first card opened. No model call, so no ledger line.</summary>
    public static void DeckTryAgain(WebPanel panel)
    {
      if (panel?.DeckShown != true || !panel.Deck.TryAgain()) return;
      try { CommandHistory.SetDeck(Instances.ActiveCanvas?.Document, panel.RunRow, panel.Deck.ToJson(), 0, panel.Deck.Count, false); } catch { }
      GoTo(panel, 0);
    }

    /// <summary>Brief EI: a missed card's row on the result. Its components lit and in view, glow only, the selection untouched (#322); no Walk is live behind a result, so none is started.</summary>
    public static void DeckMissed(WebPanel panel, int k)
    {
      var lights = panel?.DeckShown == true && k >= 0 && k < panel.Deck.Count ? panel.Deck.Cards[k].Lights : null;
      if (lights == null || lights.Count == 0 || Owns(panel)) return;
      CanvasGlow.ShowMarks(lights, null, panel);
      CanvasNav.GoTo(lights, NavReason.UserClicked);
    }

    /// <summary>Brief EI: "Walk this" and "Back to the quiz". The page has changed what it shows, so the Walk it had goes and the one it shows now arrives.</summary>
    public static void PageChanged(WebPanel panel)
    {
      if (panel == null || DwightSession.IsBusy) return;
      Arrive(panel);
    }

    private static string GoneNow()
    {
      var guids = _state.Current;
      return WalkText.Gone(guids.Count, guids.Count(id => _doc?.FindObject(id, true) != null));
    }

    private static void Mark(WalkSurface surface)
    {
      var gone = GoneNow();
      _panel.MarkWalkStep(_state.Index, surface != WalkSurface.Bar, gone);
    }

    private static void OnPageClosed(object sender, EventArgs e) => Drop();

    private static void OnPlaceChanged(object sender, EventArgs e)
    {
      try { if (IsLive && _focus.Active) Replace(); } catch { }
    }

    private static void OnCanvasKey(object sender, System.Windows.Forms.KeyEventArgs e)
    {
      if (_onCanvas.Hidden) return;   // Brief EC unit 4: minimised, the canvas is the person's, and its keys are Grasshopper's
      var key = WalkKeys.Dispatch((int)e.KeyCode, e.Modifiers != System.Windows.Forms.Keys.None, IsLive);
      var wasPlaying = Interrupt();   // Brief DD: any key pauses, and one the Walk does not take goes on to Grasshopper as it always did
      if (key == WalkKey.None) return;
      e.Handled = true;
      Do(wasPlaying ? WalkPlay.WhilePlaying(key) : key, false);
    }

    private static void Do(WalkKey key, bool fromBar)
    {
      switch (WalkKeys.InFocus(key, _focus.Active))
      {
        case WalkKey.Next: Next(); break;
        case WalkKey.Back: Back(); break;
        case WalkKey.Leave: Leave(fromBar); break;
        case WalkKey.FocusOff: BarClosed(); break;   // Brief EC unit 1: Esc in Focus is the bar's X
      }
    }

    private static void Select(IReadOnlyList<Guid> guids)
    {
      if (_doc == null) return;
      var wanted = new HashSet<Guid>(guids);
      foreach (var o in _doc.Objects)
        if (o?.Attributes != null) o.Attributes.Selected = wanted.Contains(o.InstanceGuid);
      try { _canvas?.Refresh(); } catch { }
    }
  }
}
