using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Eto.Forms;

namespace Dwight.UI
{
  /// <summary>
  /// One conversation in the one window (Brief BR; ADR-048): a verb's result or a Build chat, whose entire UI is the designed HTML shell
  /// rendered in its own WebView2 inside <see cref="DwightDock"/>. It is not a window: the host owns the size, the place and the visibility, and
  /// this owns one page. A NEW PAGE PER CONVERSATION, disposed when replaced, so a message from an old document can never land on a new owner,
  /// such as an approval click (#338): that cannot happen by construction, because the handlers belong to the view. Same
  /// <see cref="IResultPanel"/> the actions drive; falls back to the Eto popup (ADR-045) via <see cref="ResultSurface"/> when there is no host
  /// or no WebView2.
  /// </summary>
  internal sealed class WebPanel : IResultPanel
  {
    // WP-2.1's DisableHtml() pipeline now lives in ModelMarkdown - one definition shared with the chat
    // append (WP-3 Stage 0), which is the second place model text reaches this shell.

    private readonly WebView _web;
    private readonly List<string> _pending = new List<string>();
    private bool _loaded;
    private UITimer _loadWatchdog;   // WP-5.6: fires if the shell never loads (dead WebView) -> close it and toast the user; there is NO fallback here (see ArmLoadWatchdog)
    private string _raw = "";
    private string _action = "Dwight";                   // this panel's action name (for the Ask-Dwight seed)
    private Action _onReady;
    private List<Guid> _sourceGuids;
    private List<List<Guid>> _stepGuids;                 // on-canvas Explain: each step's components
    private List<List<CanvasMark>> _stepMarks;           // on-canvas Explain: each step's annotation marks
    private List<string> _stepHtml;                      // Brief DA: each step's sentence, the snippet the card shows, for the bar
    private int _lensStep = -1;                          // Brief DB: the step the lens's lines sit under, or -1 for above them all
    public Action<string> OnChat;                        // Scaffold: a chat message arrived (set by DwightScaffold)
    public Action<string> OnChatImage;                   // Brief DX: the composer's image: "pick", "clear", "big", "type", "want:SHA", or a paste's or a drop's data URL
    public Action<string> OnConsent;                     // Scaffold WP-3: a mutation-consent card was approved
    public Action<string> OnHeader;                      // Brief AO: a chat-header chip was changed ("model:ID", "approval:MODE", "full:on|off")

    /// <summary>#291: a grounded id in the agent's prose was clicked ("the Loft (42)"), or a message's
    /// whole set was. Carries AGENT IDS, resolved live at click time by the Scaffold shim.
    ///
    /// Deliberately NOT <see cref="OnLocate"/>, which already existed and carries a Simplify CHANGE INDEX:
    /// sharing the command would have sent a click on component (3) to the handler for the third change - a
    /// wrong destination that nothing on either side would have reported.</summary>
    public Action<int[]> OnAnchor;

    /// <summary>WP-4 Stage E: the user asked the agent to explain a built region, by address. It runs
    /// as an ordinary READ-ONLY turn in this thread rather than opening the Explain panel - the agent
    /// already holds every read that answers it, so this needs no new capability and no second consent
    /// model.</summary>
    public Action<string> OnExplainRegion;

    private WebPanel(string action)
    {
      // WP-5.6: probe the WebView2 runtime BEFORE building the panel. If it is absent, WebView2 fails async
      // (a dead blank rectangle) - so throw here and let ResultSurface's catch route to the Eto fallback.
      if (!WebViewSupport.IsWebView2Available())
        throw new InvalidOperationException("WebView2 runtime is not available");

      WebViewSupport.ForceWebView2();
      _web = new WebView { BrowserContextMenuEnabled = false };
      WebViewSupport.HardenNavigation(_web);   // WP-2.2: block external navigation, links open in the browser
      _web.DocumentLoaded += OnLoaded;
      _web.MessageReceived += (s, e) => Handle(e.Message);
      _web.DocumentTitleChanged += (s, e) =>
      {
        var t = e.Title;
        if (!string.IsNullOrEmpty(t) && t.StartsWith("dwight:", StringComparison.Ordinal))
          Handle(PanelTransport.StripTitleNonce(t.Substring(7)));
      };

      _action = action;
      _pending.Add($"dwightSetAction({Js(action)})");
      _pending.Add($"dwightSetTheme({Js(ThemeName())})");

      _html = WebViewSupport.LoadShell();
      if (_html == null) throw new InvalidOperationException("panel shell HTML missing");

      Action<string> onTheme = t => Run($"dwightSetTheme({Js(t)})");  // global theme: update live (spec §6)
      DwightTheme.Changed += onTheme;
      Closed += (s, e) => DwightTheme.Changed -= onTheme;
    }

    public static WebPanel Show(string action)
    {
      // If the host failed, the verbs still work: this throws, and ResultSurface falls to the Eto popup (startup isolation).
      var host = DwightDock.Host();
      if (host == null) throw new InvalidOperationException("the Dwight window is not available");

      var panel = new WebPanel(action);
      _current = panel;
      panel.Closed += (s, e) => { if (_current == panel) _current = null; };

      host.Open(panel);                         // the one window turns to this page: replace, never stack
      panel._web.LoadHtml(panel._html, null);
      panel.ArmLoadWatchdog();                  // WP-5.6: catch a runtime that is present but never loads the shell
      return panel;
    }

    /// <summary>The page's view, for the host to place and show. Nothing else reaches it.</summary>
    internal WebView View => _web;

    /// <summary>What names this page to the window's core.</summary>
    internal string PageId { get; } = Guid.NewGuid().ToString("N");

    /// <summary>The chat this page holds, or null for a verb's result. Set by <see cref="DwightScaffold"/> when it wires the page.</summary>
    internal string ThreadId { get; set; }

    /// <summary>The page the window is showing or would show from the chip. The host keeps this; the harness and the gate tools read it.</summary>
    internal static void SetCurrent(WebPanel panel) => _current = panel;

    /// <summary>Brief ES unit 1: whether this is the page the window shows, so a chat writing behind another page leaves that page's numbers on the canvas.</summary>
    internal bool IsShown => ReferenceEquals(_current, this);

    private readonly string _html;
    private bool _closed;

    /// <summary>This page has been replaced and disposed; anything it would light now has no page to belong to.</summary>
    internal bool IsClosed => _closed;

    /// <summary>This page is finished and replaced. It is removed from the window and disposed, once. Not the same as minimising, which keeps
    /// the page: nothing closes for the user any more.</summary>
    public event EventHandler<EventArgs> Closed;

    public void Close()
    {
      if (_closed) return;
      _closed = true;
      try { _loadWatchdog?.Stop(); } catch { }
      try { DwightDock.Host()?.Release(this); } catch { }
      // After this page's own message has returned: the click that replaces a page arrives on that page's view.
      var app = Application.Instance;
      Action dispose = () => { try { _web.Dispose(); } catch { } };
      if (app != null) app.AsyncInvoke(dispose); else dispose();
      try { Closed?.Invoke(this, EventArgs.Empty); } catch { }
    }

    /// <summary>
    /// A flip must focus: the view first, then the composer inside the page. A page with no composer stays focused on itself.
    ///
    /// A NEW PAGE HAS NO COMPOSER YET (sixth gate, 3 of 3): it exists only once <c>dwightChat</c> has drawn it, about 190 ms on, so a call now finds nothing and the
    /// keystrokes land on the body. So the wish is kept and carried out by <see cref="SetChat"/>, right after the composer is drawn. That late call does not insist:
    /// the user may have moved in those 190 ms, so <c>dwightFocusComposer(false)</c> leaves a focus they gave to anything else alone. Only JavaScript runs then, which
    /// cannot take the keyboard from another window. A flip onto a page with its composer is the user's own act, so it insists.
    /// </summary>
    internal void FocusPage()
    {
      if (_closed) return;
      try { _web.Focus(); } catch { }
      if (_composerDrawn) Run("dwightFocusComposer(true)");
      else _focusWhenDrawn = true;
    }

    private bool _composerDrawn, _focusWhenDrawn;

    /// <summary>A finished conversation came back: re-lit, settled.</summary>
    internal void Relight() { if (_sourceGuids == null || _sourceGuids.Count == 0 || WalkSession.OnAStep(this)) return; try { CanvasGlow.Show(_sourceGuids, false, this); } catch { } }

    /// <summary>WP-5.6: if the shell has not loaded a few seconds after Show, the WebView is dead (the probe
    /// covers a missing runtime; this catches a present-but-hung init). Close the blank shell rather than leave
    /// it stuck, and tell the user - never a silent dead rectangle.</summary>
    private void ArmLoadWatchdog()
    {
      try
      {
        _loadWatchdog = new UITimer { Interval = 6.0 };
        _loadWatchdog.Elapsed += (s, e) =>
        {
          try { _loadWatchdog?.Stop(); } catch { }
          if (_loaded) return;
          try { Close(); } catch { }
          try { DwightToast.Show("Dwight's window could not load (WebView2). Try again, or restart Rhino."); } catch { }
        };
        _loadWatchdog.Start();
      }
      catch { }
    }

    // ---- the page the window is showing; the window itself is owned by the GH window (DwightDock) ---------

    private static WebPanel _current;

    /// <summary>Close every conversation (the Restart-Dwight hard reset): the one showing and one left running.</summary>
    public static void CloseCurrent()
    {
      try { _current?.Close(); } catch { }
      try { DwightDock.Host()?.CloseConversations(); } catch { }
    }

    private void OnLoaded(object sender, WebViewLoadedEventArgs e)
    {
      _loaded = true;
      try { _loadWatchdog?.Stop(); } catch { }   // WP-5.6: the shell loaded, disarm the watchdog
      foreach (var js in _pending) Exec(js);
      _pending.Clear();
      try { _onReady?.Invoke(); _onReady = null; } catch { }
    }

    /// <summary>Run <paramref name="a"/> once the WebView has loaded (or now if already loaded). The
    /// coordinator uses this to dismiss the thinking chip the moment the panel can paint.</summary>
    public void OnReady(Action a) { if (a == null) return; if (_loaded) a(); else _onReady = a; }

    /// <summary>The components this panel's result ran on, for the "show on canvas" button.</summary>
    public void SetSourceGuids(List<Guid> guids) => _sourceGuids = guids;

    private void Handle(string cmd)
    {
      if (string.IsNullOrEmpty(cmd) || _closed) return;
      if (DwightDock.Host()?.HandleWindowCommand(cmd) == true) return;   // minimise, the list, and the window's move and resize
      if (cmd == "stop") { Dwight.DwightSession.Stop(); return; }
      if (cmd == "copy") { try { new Clipboard().Text = _raw ?? ""; } catch { } return; }
      if (cmd == "askdwight") { GraduateToChat(); return; }
      if (cmd.StartsWith("action:", StringComparison.Ordinal)) { DoAction(cmd.Substring(7)); return; }
      if (cmd == "goto") { CanvasNav.GoTo(_sourceGuids, NavReason.UserClicked); return; }
      if (cmd == "overview") { OnOverview(); return; }
      if (cmd.StartsWith("step:", StringComparison.Ordinal)) { OnStep(cmd.Substring(5)); return; }
      if (cmd.StartsWith("pin:", StringComparison.Ordinal)) { OnPinCommand(cmd.Substring(4)); return; }
      if (cmd.StartsWith("walkkey:", StringComparison.Ordinal)) { WalkSession.PageKey(cmd.Substring(8), false); return; }
      if (cmd == "walk:play") { WalkSession.TogglePlay(); return; }
      if (cmd == "walk:input") { WalkSession.Interrupt(); return; }
      if (cmd.StartsWith("deck:", StringComparison.Ordinal)) { DeckCommand(cmd.Substring(5)); return; }   // Brief EI: the deck's controls, on its card
      if (cmd == "walk:next") { WalkSession.Next(); return; }
      if (cmd == "walk:back") { WalkSession.Back(); return; }
      if (cmd == "walk:ask") { DwightCanvasPopup.Safe(WalkSession.Ask); return; }
      if (cmd == "walk:leave") { WalkSession.Leave(false); return; }
      if (cmd == "lens:all") { TreeLens.Toggle(); return; }
      if (cmd == "walk:focus") { DwightCanvasPopup.Safe(WalkSession.ToggleFocus); return; }
      if (cmd == "walk:dim:on" || cmd == "walk:dim:off") { WalkSession.SetDim(cmd == "walk:dim:on"); return; }
      if (cmd.StartsWith("lens:", StringComparison.Ordinal)) { if (int.TryParse(cmd.Substring(5), out var line)) TreeLens.SelectStop(line); return; }
      if (cmd.StartsWith("glow:", StringComparison.Ordinal)) { OnGlow(cmd.Substring(5)); return; }
      if (cmd.StartsWith("dchain:", StringComparison.Ordinal)) { OnDiagnoseChainClick(cmd.Substring(7)); return; }
      if (cmd.StartsWith("dchainsel:", StringComparison.Ordinal)) { OnChainSelect(cmd.Substring(10)); return; }
      if (cmd.StartsWith("chat:", StringComparison.Ordinal)) { OnChat?.Invoke(cmd.Substring(5)); return; }
      if (cmd.StartsWith("chatimg:", StringComparison.Ordinal)) { OnChatImage?.Invoke(cmd.Substring(8)); return; }
      // WP-3 Stage B: the mutation-consent click. The token is looked up in memory, so a card restored
      // from a persisted thread posts an id that no longer exists and is refused honestly.
      if (cmd.StartsWith("consent:", StringComparison.Ordinal)) { OnConsent?.Invoke(cmd.Substring(8)); return; }
      if (cmd.StartsWith("hdr:", StringComparison.Ordinal)) { OnHeader?.Invoke(cmd.Substring(4)); return; }
      if (cmd.StartsWith("arrange:", StringComparison.Ordinal)) { OnArrange?.Invoke(cmd.Substring(8)); return; }
      if (cmd.StartsWith("simplifyapply:", StringComparison.Ordinal)) { OnSimplifyApplySelected?.Invoke(ParseInts(cmd.Substring(14))); return; }
      if (cmd.StartsWith("toscript:", StringComparison.Ordinal)) { OnToScript?.Invoke(cmd.Substring(9)); return; }   // WP-4: replace | alongside
      if (cmd.StartsWith("locate:", StringComparison.Ordinal)) { if (int.TryParse(cmd.Substring(7), out var li)) OnLocate?.Invoke(li); return; }   // RR8 #21
      if (cmd.StartsWith("anchor:", StringComparison.Ordinal)) { OnAnchor?.Invoke(ParseInts(cmd.Substring(7))); return; }   // #291: agent ids, not a change index
      // WP-4 Stage E: show-and-explain (ADR-054). Its OWN prefix, never borrowed - a shell command
      // prefix is a namespace, and two senders sharing one with different argument semantics is a
      // silent mis-route waiting for a value valid in both (#328).
      if (cmd.StartsWith("explainregion:", StringComparison.Ordinal)) { OnExplainRegion?.Invoke(cmd.Substring(14)); return; }
      if (cmd == "undo") { OnUndo?.Invoke(); return; }   // RR8 #17: the in-panel Undo control
      if (cmd == "gotosel") { OnGotoSelection?.Invoke(); return; }   // RR12 #25: frame the whole affected selection
      if (cmd == "retry") { OnRetry?.Invoke(); return; }   // WP-5: re-run the failed action on the same bound guids
      if (cmd.StartsWith("simplify:", StringComparison.Ordinal)) { OnSimplifyMode?.Invoke(cmd.Substring(9)); return; }
    }

    /// <summary>A state-panel action button (e.g. the no-key "Add your key"): open the relevant dialog
    /// on the UI thread. Best-effort - a dialog failure never crashes the panel.</summary>
    private void DoAction(string what)
    {
      var app = Application.Instance;
      Action act = () =>
      {
        try
        {
          if (what == "addkey") ApiKeyDialog.RunForConfiguredProvider();   // WP-1.9: the configured provider, not always Anthropic
          // Settings opens the WebView panel (ADR-048); the Eto SettingsDialog is the automatic fallback,
          // parallel to ResultPopup - kept for when WebView2 is unavailable (Phase-4 keep/delete call).
          else if (what == "settings") { try { DwightSettingsPanel.Open(); } catch { SettingsDialog.Run(); } }
          // WP-1.7: the whole-canvas Reorganise confirm re-enters (then the WP-3 boundary opt-in asks).
          else if (what == "reorganise-whole") Dwight.LlmActions.ReorganiseWholeConfirmed();
          // WP-3: the boundary opt-in - "Yes, tidy the I/O" applies labelled boundary params; "No" is moves-only.
          else if (what == "reorganise-tidy") Dwight.LlmActions.ReorganiseBoundaries(true);
          else if (what == "reorganise-plain") Dwight.LlmActions.ReorganiseBoundaries(false);
          else if (what == "restore-arrangement") OnRestore?.Invoke();   // WP-1 RR1 #4: dock-reopen restore
          // WP-5's failed-run Retry: the shell posts every ShowState action as "action:<cmd>", so "retry" lands
          // HERE, not at the bare-"retry" branch (which nothing posts). Without this case the button rendered but
          // did nothing - a dead control since the Phase-10 shell rewrite. Phase-8 WP-1 step-4 builds its
          // cross-session "Run again" on this same dispatch seam, so it has to actually deliver the message.
          else if (what == "retry") OnRetry?.Invoke();
          // Brief CS: the size and cost question, answered.
          else if (what == "large-run-continue") AnswerLargeRun(true);
          else if (what == "large-run-cancel") AnswerLargeRun(false);
        }
        catch { /* dialogs are best-effort */ }
      };
      if (app != null) app.AsyncInvoke(act); else act();
    }

    /// <summary>The theme to open in (global, ADR-050 / spec §6): delegated to <see cref="DwightTheme"/>.</summary>
    private static string ThemeName() => DwightTheme.Current();

    // ---- IResultPanel (callable from any thread) ----

    public void SetMarkdown(string markdown)
    {
      _raw = markdown ?? "";
      Run($"dwightSetBody({Js(ModelMarkdown.ToHtml(_raw))})");
    }

    public void SetContent(string text)
    {
      _raw = text ?? "";
      Run($"dwightPlain({Js(_raw)}, false)");
    }

    /// <summary>RR2 item 8: render a Reorganise/flip outcome as three lines (headline / "verified" / Ctrl-Z),
    /// with hierarchy, instead of one paragraph. Copy is preserved for the footer Copy button.</summary>
    public void SetOutcome(string text)
    {
      _raw = text ?? "";
      Run($"dwightOutcome({Js(_raw)})");
    }

    public void SetError(string message)
    {
      _raw = message ?? "Something went wrong.";
      Run($"dwightPlain({Js(_raw)}, true); dwightSetStatus({Js("Error")}, true)");
    }

    public void SetStatus(string text) => Run($"dwightSetStatus({Js(text ?? "")}, false)");

    public void SetThinking(string label, bool withStages) => Run($"dwightThinking({Js(label)}, {(withStages ? "true" : "false")})");
    public void SetRunMeta(string text) => Run($"dwightRunMeta({Js(text ?? "")})");

    public void SetStage(int activeIndex) => Run($"dwightSetStage({activeIndex})");



    public void ShowState(string kind, string title, string message, string actionLabel, string actionCmd,
      string actionLabel2 = null, string actionCmd2 = null) =>
      Run($"dwightShowState({Js(kind)}, {Js(title)}, {Js(message)}, {Js(actionLabel ?? "")}, {Js(actionCmd ?? "")}, {Js(actionLabel2 ?? "")}, {Js(actionCmd2 ?? "")})");

    public void SetSteps(string stepsJson) { stepsJson = WalkScope.Within(stepsJson, _sourceGuids); _stepsJson = stepsJson; _stepGuids = ParseSteps(stepsJson); _stepMarks = ParseStepMarks(stepsJson); _stepHtml = ParseStepHtml(stepsJson); _stepTexts = ParseStepTexts(stepsJson, out _stepSummary); Run($"dwightSteps({Js(WalkOrder.ForPanel(stepsJson))})"); BeginLens(); }

    // ---- Brief DA: what the Walk reads from this page, and marks on it ----
    /// <summary>
    /// Brief EI: this Learn run's deck, or null (Learn not chosen, nothing the plugin could ask about, or a call that failed). <see cref="TeachCost"/> is the ledger's figure
    /// for the extra call, and <see cref="TeachNote"/> the plugin's words when the call failed and the Walk runs without questions. A live run sets it before its script
    /// plays; a reopened run reads it from the record's <c>SetDeck</c>, with its answers, and it is then <see cref="DeckState.Reopened"/>.
    /// </summary>
    internal DeckState Deck { get; private set; }
    internal string TeachCost { get; private set; }
    internal string TeachNote { get; private set; }
    /// <summary>The history row this run wrote, so the deck's answers and score can be put on it.</summary>
    internal Dwight.Session.DwightCommand RunRow { get; set; }
    internal void SetTeaching(DeckState deck, string cost, string note, Dwight.ModelInfo model = null, int quizChars = 0)
    {
      Deck = deck; TeachCost = cost; TeachNote = note; _deckShown = deck != null; LearnModel = model; LearnChars = quizChars;
      if (deck != null) deck.JudgeOff = LlmActions.JudgeOffLine();
    }

    /// <summary>Brief EO: the model the run's quiz was asked on and its request in characters, so Play again is priced before it is sent. Null on a reopened run, which cannot play again.</summary>
    internal Dwight.ModelInfo LearnModel { get; private set; }
    internal int LearnChars { get; private set; }
    /// <summary>The Walk's steps as this page holds them, for a fresh deck's call.</summary>
    internal string StepsJson => _stepsJson;
    private string _stepsJson;

    /// <summary>Brief EO: Play again's fresh deck takes the old one's place on this page, with the cost of its call; the record is rewritten by the caller.</summary>
    internal void ReplaceDeck(DeckState deck, string cost, Dwight.ModelInfo model, int quizChars)
    {
      if (deck == null) return;
      SetTeaching(deck, cost, null, model, quizChars);
    }

    /// <summary>Brief EI: the page shows its deck rather than its steps. "Step by step" turns it off and "Back to the quiz" on.</summary>
    internal bool DeckShown => _deckShown && Deck != null;
    private bool _deckShown;

    /// <summary>
    /// Brief EI: the record's deck, played after its steps. The live run's own deck (the same id) is kept as it is; any other is read back as a reopened deck, which shows
    /// its result and offers nothing that needs the live definition. A deck that cannot be read leaves the steps, which is what a reopened Explain always showed.
    /// </summary>
    public void SetDeck(string deckJson)
    {
      var read = DeckState.FromJson(deckJson);
      if (read == null) return;
      if (Deck == null || Deck.Id != read.Id) Deck = read;
      _deckShown = true;
      if (Deck.Reopened || Deck.AllAnswered || !Deck.Dealt) ShowDeckResult(); else ShowDeck(DeckStart());
    }

    /// <summary>Card 0, the idea: the Explain's summary as its title, what the deck holds, and what its questions cost.</summary>
    internal System.Text.Json.Nodes.JsonObject DeckStart() => Deck?.StartView(_stepSummary, _sourceGuids?.Count ?? 0, _stepGuids?.Count ?? 0, TeachCost);

    /// <summary>The deck as it stands: a view from <see cref="DeckState"/>, every word the plugin's; null takes the deck away and the steps show.</summary>
    internal void ShowDeck(System.Text.Json.Nodes.JsonObject view) => Run($"dwightDeck({Js(view?.ToJsonString() ?? "")})");
    internal void ShowDeckResult() { if (Deck == null) return; Deck.UnderstandGo(-1); Deck.AgainEstimate = LlmActions.AgainEstimate(this); ShowDeck(Deck.ResultView()); }

    /// <summary>A Walk on the deck ended (the last card's arrow, Esc, a key): the result once a card is answered, else card 0, which opens the first card again.</summary>
    internal void ShowDeckAfterWalk() { if (!DeckShown) return; if (Deck.Answered > 0 || Deck.Reopened || !Deck.Dealt) ShowDeckResult(); else ShowDeck(DeckStart()); }

    /// <summary>Brief EI: the deck's controls, from its card here or on the Focus bar. Every one is the host's to act on: the page only says which was pressed.</summary>
    internal void DeckCommand(string c)
    {
      if (Deck == null || string.IsNullOrEmpty(c)) return;
      var colon = c.IndexOf(':');
      var verb = colon < 0 ? c : c.Substring(0, colon);
      var arg = colon < 0 ? "" : c.Substring(colon + 1);
      switch (verb)
      {
        case "start": if (DeckShown) WalkSession.GoTo(this, Deck.FirstOpen); break;
        case "next": WalkSession.DeckNext(this); break;
        case "back": WalkSession.DeckBack(this); break;
        case "answer": try { WalkSession.DeckAnswer(this, Uri.UnescapeDataString(arg)); } catch (UriFormatException) { } break;
        case "goto": if (int.TryParse(arg, out var k)) WalkSession.GoTo(this, k); break;
        case "miss": if (int.TryParse(arg, out var m)) WalkSession.DeckMissed(this, m); break;
        case "walk": DeckWalkThis(); break;
        case "quiz": DeckBackToQuiz(); break;
        case "ask": GraduateToChat(); break;   // the run's chat, seeded with the questions, the answers and the score
        case "again": WalkSession.DeckTryAgain(this); break;
        // Brief EO: a name on a card, the judge's Stop, Understand and its way back, and Play again with its "Continue?".
        case "look": if (int.TryParse(arg, out var look)) WalkSession.DeckLook(this, look); break;
        case "stop": LlmActions.StopDeckCall(); break;
        case "understand": WalkSession.DeckUnderstand(this, int.TryParse(arg, out var u) ? u : 0); break;
        case "score": WalkSession.DeckUnderstand(this, -1); break;
        case "playagain": LlmActions.PlayAgain(this); break;
        case "againgo": LlmActions.PlayAgain(this, true); break;
      }
    }

    /// <summary>"Step by step": the same run's steps, as a Walk, with no new call; the way back to the deck sits above them.</summary>
    private void DeckWalkThis()
    {
      if (!DeckShown) return;
      _deckShown = false;
      ShowDeck(null);
      Run($"dwightDeckBack({Js(DeckText.BackToQuiz)})");
      WalkSession.PageChanged(this);
    }

    private void DeckBackToQuiz()
    {
      if (Deck == null || _deckShown) return;
      _deckShown = true;
      Run("dwightDeckBack('')");
      ShowDeckAfterWalk();
      WalkSession.PageChanged(this);
    }

    /// <summary>Brief EI: on a deck the Walk's steps are its cards, each lighting what it asks about; otherwise the Explain's steps.</summary>
    internal IReadOnlyList<IReadOnlyList<Guid>> WalkSteps => DeckShown ? Deck.Cards.Select(c => (IReadOnlyList<Guid>)c.Lights).ToList() : _stepGuids;
    internal IReadOnlyList<Guid> WalkSource => _sourceGuids;
    internal IEnumerable<CanvasMark> WalkMarks(int i) => DeckShown ? null : _stepMarks != null && i >= 0 && i < _stepMarks.Count ? _stepMarks[i] : null;
    internal string WalkHtml(int i) => DeckShown ? (i >= 0 && i < Deck.Count ? System.Net.WebUtility.HtmlEncode(Deck.Cards[i].Question ?? "") : "") : _stepHtml != null && i >= 0 && i < _stepHtml.Count ? _stepHtml[i] : "";
    /// <summary>The step card that is current, and the footer's controls for it; below zero, none, and the page stops passing its arrow keys on. <paramref name="panelIsWalk"/>: the bar cannot be placed, so the card carries the gone line.</summary>
    internal void MarkWalkStep(int i, bool panelIsWalk = false, string gone = null) => Run($"dwightStepCurrent({i},{(panelIsWalk ? "true" : "false")},{Js(gone)})");
    /// <summary>Brief DH: a finished Explain has arrived. Its controls are there and no card is current, because the canvas has not moved.</summary>
    internal void MarkWalkArrived(int count) => Run($"dwightWalkArrived({count})");
    /// <summary>Brief DH: what the footer's two switches and the title bar's Focus say.</summary>
    internal void MarkWalkSwitches(bool focus, bool dim) => Run($"dwightWalkSwitches({(focus ? "true" : "false")},{(dim ? "true" : "false")})");
    /// <summary>"Ask about this step": the composer holds the words, unsent.</summary>
    internal void SetComposerDraft(string text) => Run($"dwightComposerDraft({Js(text)})");
    /// <summary>Brief DB through the Walk: which step the lens's lines sit under. The Walk owns the step now, so it says, and -1 puts them above the steps.</summary>
    internal void SetLensStep(int i) => _lensStep = i;

    /// <summary>Brief DD: Play on the footer is Pause while the Walk plays.</summary>
    internal void SetWalkPlaying(bool on, bool canPlay = true) => Run($"dwightWalkPlaying({(on ? "true" : "false")},{(canPlay ? "true" : "false")})");

    /// <summary>Brief DD: the step's line of times, under the current card, or null to take it away.</summary>
    internal void SetWalkLine(string line) => Run($"dwightWalkLine({Js(line)})");

    /// <summary>Brief DD: the selection's total and its slowest, under the summary, or null to take it away.</summary>
    internal void SetWalkTotal(string line) => Run($"dwightWalkTotal({Js(line)})");

    /// <summary>Brief DB: an Explain's steps are on the panel, so its lens is live: T shows the wires of all it covers, and what stops is said above the steps until one is picked.</summary>
    private void BeginLens()
    {
      var selection = (_sourceGuids ?? new List<Guid>()).Concat((_stepGuids ?? new List<List<Guid>>()).SelectMany(s => s)).Distinct().ToList();
      if (selection.Count == 0) return;
      _lensStep = -1;
      TreeLens.Begin(selection, this);
      TreeLens.Changed += PushLens;
      PushLens();
    }

    /// <summary>The lines and the cap's sentence, under the step they are about. Every word is already written.</summary>
    private void PushLens() => Run($"dwightLens({Js(LensView.Json(TreeLens.HasStep ? _lensStep : -1, TreeLens.Stops, TreeLens.Note, TreeLens.IsLive, TreeLens.ShowsAll))})");

    /// <summary>"pin:summary" and "pin:step:N" are an Explain step's offer; any other token is a Build receipt's, whose text the plugin registered.</summary>
    private void OnPinCommand(string arg)
    {
      if (arg == "summary" || arg.StartsWith("step:", StringComparison.Ordinal)) { OnPin(arg); return; }
      if (!NoteOffers.TryGet(arg, out var text, out var anchor, out var by, out var fromRun)) return;
      var doc = Grasshopper.Instances.ActiveCanvas?.Document;
      if (doc != null) PinNote(doc, anchor, text, fromRun);
    }

    private List<string> _stepTexts;
    private string _stepSummary;

    /// <summary>The steps' sentences and the summary, as the model wrote them, for "Pin as a note". The model's words are the note's text here BY DESIGN (the
    /// note is the person's to keep, and it is read back to a model as context only, never as an instruction); a click writes it, nothing else does.</summary>
    private static List<string> ParseStepTexts(string stepsJson, out string summary) => NoteOfferText.Steps(stepsJson, out summary);

    /// <summary>The run this panel shows, or a chat's thread, so a note it offers remembers where it came from (Brief DO, item 8). Null on a row from an older build.</summary>
    public string RunId { get; set; }

    /// <summary>Brief DO, item 7: a result's one offer, a click from a note. Shown, never recorded: a reopened row has no token to carry, and a note is never written without the click.</summary>
    internal void OfferNote(string text, Guid? anchor, string title)
    {
      if (string.IsNullOrWhiteSpace(text)) return;
      var token = NoteOffers.Register(text, anchor, Dwight.Session.DwightNotes.ByDwight, RunId);
      if (token != null) Run($"dwightPinOffer({Js(token)},{Js(title)})");
    }

    /// <summary>"Pin as a note" on a step or the summary (Brief DC ruling 6). A click writes a note with by: dwight, anchored to the step's first component (the
    /// summary's to the first selected one), text the sentence as plain text. Nothing is pinned without this click.</summary>
    private void OnPin(string what)
    {
      var doc = Grasshopper.Instances.ActiveCanvas?.Document;
      if (doc == null) return;
      string text; Guid? anchor;
      if (what == "summary") { text = _stepSummary; anchor = _sourceGuids != null && _sourceGuids.Count > 0 ? _sourceGuids[0] : (Guid?)null; }
      else if (what.StartsWith("step:", StringComparison.Ordinal) && int.TryParse(what.Substring(5), out var i) && _stepTexts != null && i >= 0 && i < _stepTexts.Count)
      { text = _stepTexts[i]; anchor = _stepGuids != null && i < _stepGuids.Count && _stepGuids[i].Count > 0 ? _stepGuids[i][0] : (Guid?)null; }
      else return;
      text = NoteOfferText.Step(text, null);
      if (text == null) return;
      PinNote(doc, anchor, text, RunId);
    }

    /// <summary>Write a note Dwight drafted, on a click: anchored to a component when there is one, else at the canvas's centre.</summary>
    internal static void PinNote(Grasshopper.Kernel.GH_Document doc, Guid? anchor, string text, string fromRun)
    {
      var centre = default(System.Drawing.PointF);
      try { var v = Grasshopper.Instances.ActiveCanvas.Viewport.VisibleRegion; centre = new System.Drawing.PointF(v.X + v.Width / 2f, v.Y + v.Height / 2f); } catch { }
      var note = DwightNotePanel.MakeNew(doc, anchor, centre, text, Dwight.Session.DwightNotes.ByDwight, fromRun, out _);
      if (note == null) return;
      CanvasNotes.Reveal(note.Id);
      Dwight.Session.CommandHistory.AddNote(doc, note);
    }
    public void SetFaults(string faultsJson) { _faultsJson = faultsJson; Run($"dwightFaults({Js(faultsJson)})"); }

    /// <summary>WP-2: the Simplify mode chooser (a card per mode); picking one posts "simplify:&lt;id&gt;" back to
    /// <see cref="OnSimplifyMode"/>, which the action runs on the bound selection.</summary>
    public Action<string> OnSimplifyMode;
    public void SetSimplifyChooser(string modesJson) => Run($"dwightSimplifyChooser({Js(modesJson)})");
    /// <summary>WP-2: the better-logic preview (Stage 6: a checkbox per change). "Apply selected" posts
    /// "simplifyapply:&lt;csv of ticked indices&gt;" back to <see cref="OnSimplifyApplySelected"/>; Dismiss is always live.</summary>
    public void SetSimplifyPreview(string previewJson) { _simplifyPreview = previewJson; Run($"dwightSimplifyPreview({Js(previewJson)})"); }
    /// <summary>WP-2 Stage 6: the user ticked a subset of changes and pressed Apply selected - the indices to apply.</summary>
    public Action<int[]> OnSimplifyApplySelected;
    /// <summary>WP-4: the to-script preview - the CONSENT surface. It shows the COMPLETE source (this is the first
    /// Dwight output that EXECUTES on the user's machine, so they read all of it before it runs), the
    /// plugin-computed param spec, and the two accept choices. Picking one posts "toscript:replace" or
    /// "toscript:alongside"; BOTH verify the script beside the originals before anything is replaced.</summary>
    public void SetToScriptPreview(string json) => Run($"dwightToScriptPreview({Js(json)})");
    /// <summary>WP-4: the user's accept choice - "replace" or "alongside".</summary>
    public Action<string> OnToScript;
    /// <summary>RR8 #21: click-to-locate - the user clicked a change item (preview or applied) to highlight exactly
    /// its components on the canvas. The int is the change index.</summary>
    public Action<int> OnLocate;
    /// <summary>WP-5: the Retry control on a failed / stopped panel - re-runs the same action on the same bound
    /// guids (set from <see cref="Dwight.DwightSession.Context.Rerun"/>, so it survives a changed selection).</summary>
    public Action OnRetry;

    private Action _largeRunContinue, _largeRunCancel;

    /// <summary>Brief CS: the verb's question, in the notice state. The answers are closures over the verb's own run (#344), and each
    /// is taken once, so a second click on a button that has not yet gone cannot start a second paid call.</summary>
    public void AskToContinue(string title, string sentence, Action onContinue, Action onCancel)
    {
      _largeRunContinue = onContinue;
      _largeRunCancel = onCancel;
      ShowState("notice", title, sentence, Dwight.Session.LargeRun.ContinueLabel, "large-run-continue", Dwight.Session.LargeRun.CancelLabel, "large-run-cancel");
    }

    private void AnswerLargeRun(bool go)
    {
      var answer = go ? _largeRunContinue : _largeRunCancel;
      _largeRunContinue = null;
      _largeRunCancel = null;
      answer?.Invoke();
    }
    /// <summary>RR8 #17: the applied panel's in-panel Undo control.</summary>
    public Action OnUndo;
    /// <summary>RR12 #25: the header "take me to the selection on canvas" - frame + glow the whole affected set.</summary>
    public Action OnGotoSelection;
    /// <summary>RR8 #17: the itemized applied "done" list (change items + skips + honest summary + advisory + the
    /// Undo/Re-apply controls) - replaces the bare count outcome for a successful apply.</summary>
    public void SetSimplifyApplied(string json) { _simplifyApplied = json; Run($"dwightSimplifyApplied({Js(json)})"); }
    /// <summary>WP-2 Stage 7: the applied/reverted outcome + the read-only "Dwight also noticed" advice (so a
    /// revert still teaches). <paramref name="title"/> null for a plain success headline.</summary>
    public void SetSimplifyOutcome(string kind, string title, string message, string statusText, string adviceJson, string retryLabel = null)
    {
      _outcomeTitle = title; _outcomeMessage = message; _outcomeAdvice = adviceJson;
      Run($"dwightSimplifyOutcome({Js(kind ?? "ok")}, {Js(title ?? "")}, {Js(message ?? "")}, {Js(statusText ?? "")}, {Js(adviceJson ?? "[]")}, {Js(retryLabel ?? "")})");
    }

    // ---- Brief EC units 5 and 5b: what this run hands its chat, from the data it rendered, never from the page or the copy text ----
    private string _faultsJson, _simplifyPreview, _simplifyApplied, _outcomeTitle, _outcomeMessage, _outcomeAdvice;
    private IReadOnlyList<Dwight.Agent.RunRegion> _regions;

    /// <summary>The numbering this run's words use, and the run's time. The chat it opens translates the first and is named for the second.</summary>
    internal IReadOnlyDictionary<Guid, int> RunLabels { get; private set; }
    internal DateTime RunAt { get; private set; } = DateTime.Now;
    internal void SetRun(IReadOnlyDictionary<Guid, int> labels, DateTime at) { if (labels != null) RunLabels = labels; RunAt = at; }

    /// <summary>Brief ES unit 1: a fresh deck on the same run is written in its own read of the selection, so the page's numbering follows it and the run's time stays.</summary>
    internal void SetRunLabels(IReadOnlyDictionary<Guid, int> labels) { if (labels != null) RunLabels = labels; }

    /// <summary>Brief ES unit 1: a chat page's numbering, its registry's, read when it is asked for. Set by <see cref="DwightScaffold"/> when it wires the thread.</summary>
    internal Func<Func<Guid, int?>> ChatNumbers { get; set; }

    /// <summary>Brief ES unit 1: the numbers this page's words name components by, which the canvas wears while it is the page a person reads: a chat's registry, a verb's run. Null before a run has any.</summary>
    internal Func<Guid, int?> Numbers() => ChatNumbers != null ? ChatNumbers() : CanvasNumbers.Of(RunLabels);

    /// <summary>A Reorganise's regions, for its chat.</summary>
    internal void SetRunRegions(IReadOnlyList<Dwight.Agent.RunRegion> regions) => _regions = regions;

    /// <summary>The run, as its chat is seeded with it: the verb, its key and time, its sources and numbering, and whichever result it rendered.</summary>
    internal Dwight.Agent.RunResult HandOff() => new Dwight.Agent.RunResult
    {
      Verb = _action,
      RunKey = RunId,
      At = RunAt,
      Sources = _sourceGuids,
      Labels = RunLabels,
      Prose = _raw,
      StepsSummary = _stepSummary,
      Steps = _stepTexts,
      Lesson = Deck?.Lines(),
      LessonQuestions = Deck?.Count ?? 0,
      FaultsJson = _faultsJson,
      SimplifyPreviewJson = _simplifyPreview,
      SimplifyAppliedJson = _simplifyApplied,
      OutcomeTitle = _outcomeTitle,
      OutcomeMessage = _outcomeMessage,
      OutcomeAdviceJson = _outcomeAdvice,
      Regions = _regions,
    };

    /// <summary>Brief EC unit 5b: in a chat opened from a Walk's step, the way back to that Walk, which waits on its step; null takes it away.</summary>
    internal void SetWalkBack(string label) => Run($"dwightChatWalkBack({Js(label ?? "")})");

    /// <summary>Brief EC unit 5b: the Walk's page is in front again, and a glow of its chat's may have ended its lens.</summary>
    internal void ResumeLens() { if (!TreeLens.IsLive) { TreeLens.Changed -= PushLens; BeginLens(); } }

    private static int[] ParseInts(string csv)
    {
      var list = new List<int>();
      foreach (var s in (csv ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
        if (int.TryParse(s.Trim(), out var n)) list.Add(n);
      return list.ToArray();
    }
    public void SetChat(string messagesJson)
    {
      Run($"dwightChat({Js(messagesJson)})");
      _composerDrawn = true;
      if (!_focusWhenDrawn) return;
      _focusWhenDrawn = false;
      Run("dwightFocusComposer(false)");   // queued behind the chat, so the composer exists when it runs
    }

    public void AppendChat(string who, string html) => Run($"dwightChatAppend({Js(who)}, {Js(html)})");
    /// <summary>Brief DX: the person's own message with its image, by hash. The shell then asks for the bytes, as it does on a reopened thread, so the two draw one way.</summary>
    public void AppendChatWithImage(string html, string imageJson) => Run($"dwightChatAppend('me', {Js(html)}, false, {Js(imageJson)})");
    /// <summary>Brief DX: the chip above the text box, or none when <paramref name="json"/> is null.</summary>
    public void SetChatHeld(string json) => Run($"dwightChatHeld({Js(json ?? "")})");
    /// <summary>Brief DX: the bytes of an image in the thread, as a data URL, or the words to show where a file is gone.</summary>
    public void SetChatImageBytes(string sha, string dataUrl, string goneText) => Run($"dwightChatImageBytes({Js(sha)}, {Js(dataUrl ?? "")}, {Js(goneText ?? "")})");
    /// <summary>U111: the plugin's echo of an approval, marked, so the shell can leave it out after a stated receipt. A typed line never comes through here.</summary>
    public void AppendApprovalEcho(string html) => Run($"dwightChatAppend('me', {Js(html)}, true)");
    /// <summary>WP-3 (#259a): show the chat's Stop control and disable the composer while a turn runs.
    /// ADR-089 is canon at every other surface; a multi-step billed loop is where it matters most.</summary>
    /// <summary>Brief AO: draw the chat header from the host's own document (<see cref="Dwight.Agent.ChatHeader.Json"/>). The shell holds no value of its own.</summary>
    public void SetChatHeader(string json) => Run($"dwightChatHeader({Js(json)})");

    /// <summary>Brief BG: the card minted with this token was answered; the shell swaps it for its receipt in place, so the thread does not
    /// re-render and the composer keeps what is typed. The record holds the same receipt (CommandHistory.RewriteThreadPluginHtml).</summary>
    public void SetCardAnswered(string token, string receiptHtml) => Run($"dwightCardAnswered({Js(token)},{Js(receiptHtml)})");

    public void SetChatBusy(bool busy) => Run($"dwightChatBusy({(busy ? "true" : "false")})");

    /// <summary>"Ask Dwight" (spec §3): turn this result into a chat thread carrying it as context.
    /// SEAM - the echo reply stands in for the real agent (Phase 8, with ADR-067 persistence).</summary>
    /// <summary>
    /// ADR-071's graduation, realised (#365).
    ///
    /// This was a Phase-3b ECHO: it answered every message with "the agent that answers lands in Phase 8"
    /// - while the agent shipped. A surface stating a version of itself the system had left behind (#253),
    /// on a button the user is invited to press. The false line went with the stub.
    ///
    /// It now opens the REAL agent thread, seeded with this run's verb, its components (as registry ids,
    /// so they linkify and stay honest across time) and its result. The turn that follows is READ-ONLY
    /// like any typed message - graduating a panel carries CONTEXT, never authority (#220).
    /// </summary>
    private void GraduateToChat() => DwightScaffold.GraduateFrom(HandOff());   // Brief EC unit 5: the run's one chat, made by the first door and found by every later one

    /// <summary>On-canvas Explain: clicking a step frames its components (glow, under) and draws that
    /// step's annotation marks (over) on the canvas, then pans to them (ADR-065).</summary>
    private void OnStep(string idx)
    {
      if (_stepGuids == null || !int.TryParse(idx, out var i) || i < 0 || i >= _stepGuids.Count) return;
      WalkSession.GoTo(this, i);   // Brief DA: the card and the bar's dot are one call, and on a finished Explain this brings the bar back
    }

    /// <summary>Diagnose / stray: clicking a row frames + pans to that component (by guid).</summary>
    private void OnGlow(string guidStr)
    {
      if (!Guid.TryParse(guidStr, out var id)) return;
      var g = new List<Guid> { id };
      CanvasGlow.Show(g, false, this);
      CanvasNav.GoTo(g, NavReason.UserClicked);
    }

    /// <summary>On-canvas Explain: clicking the summary returns to the whole selection - frame all of it
    /// with its number badges, clearing the per-step marks (the "back to the overview" affordance).</summary>
    private void OnOverview()
    {
      if (_sourceGuids == null || _sourceGuids.Count == 0) return;
      if (WalkSession.OnAStep(this)) WalkSession.Leave(false);   // Brief DA: back to the whole selection is the end of the Walk; Brief DH: on one that has only arrived it is already there
      CanvasGlow.Show(_sourceGuids, false, this);   // clears step marks; the id labels persist
      CanvasNav.GoTo(_sourceGuids, NavReason.UserClicked);
      _lensStep = -1;
      TreeLens.SetCurrent(null);
    }

    /// <summary>Whole-canvas Diagnose chain-picker (D6): selecting a chain row highlights it; the
    /// "Diagnose this chain" button runs the LLM for that one chain. Both are set by the Diagnose action.</summary>
    public Action<int> OnChainExplain;
    public Action<int> OnChainHighlight;
    private void OnDiagnoseChainClick(string idx) { if (int.TryParse(idx, out var i)) OnChainExplain?.Invoke(i); }
    private void OnChainSelect(string idx) { if (int.TryParse(idx, out var i)) OnChainHighlight?.Invoke(i); }

    /// <summary>Render the deterministic chain list (no LLM yet).</summary>
    public void SetChains(string chainsJson) => Run($"dwightChains({Js(chainsJson)})");

    /// <summary>WP-1: the in-panel arrangement switcher (Reorganise result only). <paramref name="active"/> is
    /// "compact" | "hchain" | "grid"; clicking a mode posts "arrange:&lt;mode&gt;" back to <see cref="OnArrange"/>,
    /// which the Reorganise action wires to a layout-only re-run (no re-segment, no LLM). Set by the action and
    /// re-wired on dock reopen, exactly like the chain-picker callbacks.</summary>
    public Action<string> OnArrange;
    public void SetArrangeSwitcher(string active) => Run($"dwightArrange({Js(active ?? "compact")})");

    /// <summary>WP-1 RR1 #4: the dock-reopen "Restore this arrangement" button (set by the Reorganise render when
    /// the arrangement is no longer in place). Routed via the state-card action button, like the reorganise-whole
    /// confirm.</summary>
    public Action OnRestore;

    private static List<List<Guid>> ParseSteps(string stepsJson)
    {
      var result = new List<List<Guid>>();
      try
      {
        var steps = System.Text.Json.Nodes.JsonNode.Parse(stepsJson)?["steps"]?.AsArray();
        if (steps == null) return result;
        foreach (var s in steps)
        {
          var ids = new List<Guid>();
          var arr = s?["ids"]?.AsArray();
          if (arr != null) foreach (var id in arr) if (Guid.TryParse(id?.GetValue<string>(), out var g)) ids.Add(g);
          result.Add(ids);
        }
      }
      catch { }
      return result;
    }

    private static List<string> ParseStepHtml(string stepsJson)
    {
      var result = new List<string>();
      try
      {
        var steps = System.Text.Json.Nodes.JsonNode.Parse(stepsJson)?["steps"]?.AsArray();
        if (steps != null) foreach (var s in steps) result.Add(s?["text"]?.GetValue<string>() ?? "");
      }
      catch { }
      return result;
    }

    /// <summary>Per-step annotation marks (guid-resolved by the action). Parallel to <see cref="ParseSteps"/>.</summary>
    private static List<List<CanvasMark>> ParseStepMarks(string stepsJson)
    {
      var result = new List<List<CanvasMark>>();
      try
      {
        var steps = System.Text.Json.Nodes.JsonNode.Parse(stepsJson)?["steps"]?.AsArray();
        if (steps == null) return result;
        foreach (var s in steps)
        {
          var marks = new List<CanvasMark>();
          var arr = s?["marks"]?.AsArray();
          if (arr != null)
            foreach (var m in arr)
            {
              if (!(m is System.Text.Json.Nodes.JsonObject o)) continue;
              var t = o["t"]?.GetValue<string>();
              if (t == "arrow")
              {
                if (Guid.TryParse(o["from"]?.GetValue<string>(), out var f) && Guid.TryParse(o["to"]?.GetValue<string>(), out var to))
                  marks.Add(new CanvasMark { T = "arrow", From = f, To = to, Label = o["label"]?.GetValue<string>() });
              }
              else if (t == "highlight")
              {
                var mark = new CanvasMark { T = t };
                var ids = o["ids"]?.AsArray();
                if (ids != null) foreach (var id in ids) if (Guid.TryParse(id?.GetValue<string>(), out var g)) mark.Ids.Add(g);
                if (mark.Ids.Count > 0) marks.Add(mark);
              }
            }
          result.Add(marks);
        }
      }
      catch { }
      return result;
    }

    // ---- helpers ----

    private void Run(string js)
    {
      var app = Application.Instance;
      Action act = () => { if (_closed) return; if (_loaded) Exec(js); else _pending.Add(js); };   // a turn may outlive its page
      if (app != null) app.AsyncInvoke(act); else act();
    }

    private void Exec(string js) { try { _web.ExecuteScript(js); } catch { /* page may be navigating */ } }

    /// <summary>Encode a string as a safe JS string literal (quotes + escapes handled).</summary>
    private static string Js(string s) => JsonSerializer.Serialize(s ?? "");
  }
}
