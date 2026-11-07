using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Grasshopper;
using Grasshopper.Kernel;
using Dwight.Agent;
using Dwight.Config;
using Dwight.Data;
using Dwight.Llm;
using Dwight.Models;
using Dwight.Session;

namespace Dwight.UI
{
  /// <summary>The Scaffold agentic surface (ADR-054; spec §5): a file-linked chat thread, durable per file
  /// (ADR-067). WP-3 Stage A replaces the echo with a REAL agent turn - the multi-tool loop over WP-2's tool
  /// surface - running READ-ONLY: comprehension and planning, no capability to touch the canvas (#220). The
  /// consent card that can grant more is Stage B; Plan -> Scaffold is Stages C/D.</summary>
  internal static class DwightScaffold
  {
    /// <summary>Shown when a turn cannot start because Dwight is already working (ADR-072 - one operation
    /// at a time). Refused with an inline notice rather than queued (#222 Q4): queueing hides spend, and a
    /// message the user thinks was sent is worse than one they can see was not.</summary>
    private const string BusyNotice = OneWindowCore.BusySameChat;

    /// <summary>Brief BR: the refusal when something is running, in this chat's words. "Previous message" is true only of the chat that is
    /// running, so another chat is told so, with the way to it.</summary>
    private static string BusyHtml(string threadId)
    {
      var running = (DwightSession.RunningPanel as WebPanel)?.ThreadId;
      var html = System.Net.WebUtility.HtmlEncode(OneWindowCore.BusyLine(threadId, running));
      return OneWindowCore.BusyHasWayThere(threadId, running)
        ? html + " <button class=\"btn sm\" onclick=\"dwightSend('nav:running')\">" + System.Net.WebUtility.HtmlEncode(OneWindowCore.GoToRunning) + "</button>"
        : html;
    }

    /// <summary>Brief BR: is a card waiting in this chat? The token store is the one record: a card is pending from its mint until it is used
    /// or a later card retires it, it holds no clock, and it does not survive a restart, where the card refuses honestly.</summary>
    internal static bool ThreadHasPendingCard(string threadId) =>
      !string.IsNullOrEmpty(threadId) && _consents.PendingFor(threadId).Count > 0;

    // Defect 3 (the WP-4 gate): every user-facing claim about what this surface can do moved to
    // ChatChrome, which is GH-free and therefore reachable by a test. The greeting had promised the
    // build agent "in a later work-package" for the whole of the work-package that shipped it. These
    // aliases keep the call sites below reading exactly as they did.
    private const string NoDocumentNotice = ChatChrome.NoDocument;

    private const string NoKeyNotice = ChatChrome.NoKey;

    public static void Open() { OpenThread(); }

    /// <summary>A chat with its composer holding the words, unsent (a note's "Ask the chat"). No model call until the person sends. A Walk's step asks through <see cref="GraduateFrom"/>.</summary>
    internal static void OpenWithDraft(string draft)
    {
      if (OpenThread().Panel is WebPanel wp) wp.SetComposerDraft(draft);
    }

    /// <summary>Open a Scaffold thread and hand back its parts. <see cref="Open"/> discards them; the
    /// WP-4 full-path drill needs them, so it can post a message through the same door a user does
    /// rather than through a reimplementation of it (#268 - a drill that exercises its own copy of the
    /// path measures the copy).</summary>
    private static (IResultPanel Panel, GH_Document Doc, string ThreadId) OpenThread(string runKey = null, string runTitle = null)
    {
      // Read the canvas BEFORE showing a window. Not a fix for a named mechanism - it is simply the state we
      // were invoked in, and capturing it before we change the window state is the order that cannot be
      // wrong. The document we bind here is the document for this panel's life (AgentScope.Document checks
      // it is still the active one on every tool call).
      var canvas = Instances.ActiveCanvas;
      var doc = canvas?.Document;

      // #298's discriminator: WHICH of the two nulls it was. One line, and it separates "Grasshopper has no
      // active canvas" from "the canvas has no document" - a distinction no amount of reading settles.
      if (doc == null)
        TurnLog("opened with NO DOCUMENT (activeCanvas=" + (canvas == null ? "null" : "present") + ")");

      var panel = ResultSurface.Show("Dwight - " + DwightVerbs.Build);
      var threadId = Guid.NewGuid().ToString("N");

      // Seed the thread with the greeting, then render the SCREEN from the thread - so the on-screen chat and
      // the persisted thread are one shape from the first frame (#153 applied to chat).
      CommandHistory.StartThread(doc, threadId, "dw",
                                 doc == null ? NoDocumentNotice : GreetingHtml() + ChatHeader.OpeningScriptsHtml(ApprovalMode.Normalise(DwightSettings.Current.ApprovalMode), DwightSettings.Current.FullControl),
                                 // Brief AL: the Settings default is copied onto the thread ONCE, here; the thread keeps its own from now on.
                                 ApprovalMode.Normalise(DwightSettings.Current.ApprovalMode),
                                 DwightSettings.Current.FullControl);   // U90: copied once; the thread keeps its own
      if (!string.IsNullOrEmpty(runKey)) CommandHistory.SetThreadRun(doc, threadId, runKey, runTitle);   // Brief EC: before the row, so the list names it for its run from the first draw
      Wire(panel, doc, threadId);

      try
      {
        CommandHistory.Record(doc, new DwightCommand
        {
          Action = DwightVerbs.Build,
          Subtitle = "thread",
          SourceGuids = LlmActions.SelectedGuids(),
          CostLine = " - ",
          ThreadId = threadId,
          // Fallback only: reopen routes through ReopenThread on the ThreadId; this greeting snapshot renders if
          // the thread was ever cap-evicted (honest degradation).
          Script = PanelScript.New().Op(PanelScript.SetChat, RenderThread(doc, threadId)).Build(),
        });
      }
      catch { }

      return (panel, doc, threadId);
    }

    /// <summary>
    /// PANEL GRADUATION (#365, ADR-071): the Chat button on a verb's result panel opens the REAL agent
    /// thread, seeded with what that run was about.
    ///
    /// WHAT IT REPLACES. This door was a Phase-3b stub: an in-panel echo that answered every message with
    /// "the agent that answers lands in Phase 8" - while the agent shipped. A surface stating a version of
    /// itself the system has left behind (#253), on a button the user is invited to press.
    ///
    /// WHAT THE SEED IS, and each piece is here for a reason:
    ///   - THE VERB and THE SCOPE, as a plugin-authored line. The components are named by ids issued in
    ///     THIS thread's registry, so they linkify, they can be clicked to, and they stay honest across
    ///     time - a component deleted later says so rather than panning somewhere plausible (#291/#321).
    ///   - THE RESULT, through the MODEL door, because that is what it is: the verb's own answer, which
    ///     the user has just read. It enters the transcript as Dwight's prior words - attributed context
    ///     that reaches the PROMPT and nothing else, the #332(3) ApprovedIntent pattern. No verification
    ///     reads it, because nothing here verifies anything.
    ///
    /// WHAT IT DOES NOT GRANT. The turn that follows is READ-ONLY like any typed message: capability
    /// arrives on the call stack from a consent click and nowhere else (#220), so graduating a panel
    /// carries context, never authority. Anything more goes through request_mutation like everything else.
    ///
    /// This is the DOOR only. Running verbs inside the thread is #340's study, not this.
    ///
    /// ONE RUN, ONE CHAT (Brief EC unit 5, Tharun's sitting of 2 November 2026: "it opens a new chat instead of going to the same Explain chat"). Every door from one run, the footer's Chat and
    /// every step's Ask, reaches the one thread keyed to the run: the first door makes it and seeds it, a later door re-opens it and changes the composer's draft and nothing else.
    ///
    /// WHAT THE SEED IS (unit 5b, Rhino 2's measurement of the seventeenth). One message through the MODEL door: the context line naming the verb, the run's time and every source in the chat's
    /// numbering, then the result in the plugin's Markdown from the data the panel rendered (<see cref="RunHandOff"/>). The context line was chrome, and asked, the model could not name the action;
    /// Simplify and Diagnose carried nothing at all. Every id in it and in the draft is the CHAT's, through the guid, because the chat reads a bracketed number as its own (ADR-058).
    /// </summary>
    internal static WebPanel GraduateFrom(RunResult run, Func<Func<int, int?>, string> draft = null)
    {
      run = run ?? new RunResult();
      var doc = Instances.ActiveCanvas?.Document;
      var threadId = CommandHistory.ThreadForRun(doc, run.RunKey);
      WebPanel wp;
      if (threadId != null)
        wp = ReopenRunThread(doc, threadId);
      else
      {
        var verb = string.IsNullOrWhiteSpace(run.Verb) ? "run" : run.Verb.Trim();
        var opened = OpenThread(run.RunKey, HistoryCore.RunThreadTitle(verb, run.At));
        wp = opened.Panel as WebPanel;
        if (wp == null) return null;   // the Eto fallback has no chat to graduate into
        doc = opened.Doc;
        threadId = opened.ThreadId;
        try
        {
          var scope = ScopeFor(threadId, doc);
          var seed = RunHandOff.Seed(run, g => NameOf(doc, g), g => ChatId(doc, scope, g));
          SaveIdSpace(doc, scope);
          AppendModelMarkdown(wp, doc, threadId, seed);
        }
        catch (Exception ex)
        {
          // Fail safe: a graduation that cannot carry its context is still a working chat, and says so
          // rather than pretending it carried something.
          TurnLog("graduation context not carried: " + ex.Message);
          AppendPluginHtml(wp, doc, threadId,
            "I could not carry that result across, so ask me about it and I will read the canvas again.");
        }
      }
      if (draft != null && wp != null)
      {
        try
        {
          var scope = ScopeFor(threadId, doc);
          var text = draft(RunIds.Through(run.Labels, g => ChatId(doc, scope, g)));
          SaveIdSpace(doc, scope);
          wp.SetComposerDraft(text);
        }
        catch (Exception ex) { TurnLog("the step's question not drafted: " + ex.Message); }
      }
      return wp;
    }

    /// <summary>The chat already made for a run: in front if its page is alive, its turn included, or re-opened from the record as a History row opens it.</summary>
    private static WebPanel ReopenRunThread(GH_Document doc, string threadId)
    {
      var host = DwightDock.Host();
      host?.LookedAt(threadId);
      var live = host?.LivePage(threadId);
      if (live != null && host.ShowLive(threadId)) return live;
      var panel = ResultSurface.Show("Dwight - " + DwightVerbs.Build);
      ReopenThread(panel, doc, threadId);
      return panel as WebPanel;
    }

    /// <summary>The chat's id for a component still on the canvas, issued in this thread's registry; null for one that has gone.</summary>
    private static int? ChatId(GH_Document doc, AgentScope scope, Guid g) =>
      g != Guid.Empty && doc?.FindObject(g, false) != null ? scope.Ids.Issue(g) : (int?)null;

    /// <summary>What a person calls a component: its nickname where they gave one, otherwise its type. Never its badge, which is the run's number.</summary>
    private static string NameOf(GH_Document doc, Guid g)
    {
      var o = doc?.FindObject(g, false);
      if (o == null) return null;
      return string.IsNullOrWhiteSpace(o.NickName) || o.NickName == o.Name ? o.Name : o.NickName;
    }

    /// <summary>Reopen a Scaffold row from the dock (routed here by <see cref="Dwight.DwightSession.Reopen"/> on
    /// its ThreadId). The thread comes back whole and the input is LIVE again - a chat message is neither a
    /// canvas mutation nor a paid call, so re-wiring it is consistent with #169/#161 (per-message consent, not
    /// session age, governs a chat; #182).</summary>
    internal static void ReopenThread(IResultPanel panel, GH_Document doc, string threadId) =>
      Wire(panel, doc, threadId);

    /// <summary>Render the whole thread onto the panel and wire the (live) input. Used by both open and reopen.</summary>
    private static void Wire(IResultPanel panel, GH_Document doc, string threadId)
    {
      var json = RenderThread(doc, threadId);
      if (panel is WebPanel page) page.ThreadId = threadId;   // Brief BR: the window finds a live chat by this
      if (panel is WebPanel wp)
      {
        wp.ChatNumbers = () => CanvasNumbers.Of(ScopeFor(threadId, doc).Ids);   // Brief ES unit 1: the chat's words number by its registry, so its badges do
        wp.OnChat = text => OnUserMessage(wp, doc, threadId, text);
        wp.OnChatImage = what => OnChatImage(wp, doc, threadId, what);
        wp.OnConsent = token => OnConsentClick(wp, doc, threadId, token);
        wp.OnHeader = cmd => OnHeaderCommand(wp, doc, threadId, cmd);
        wp.OnAnchor = ids => OnAnchorClick(wp, doc, threadId, ids);
        wp.OnExplainRegion = address => OnExplainRegionClick(wp, doc, threadId, address);
        wp.SetChat(json);
        // Brief DX: the chip is this thread's. The shell keeps one per page, and a chip left from another chat would say a picture goes with a message it does not go with.
        RedrawChip(wp, doc, threadId);
        RefreshHeader(wp, doc, threadId);   // Brief AO: the header, from the thread's own record. The cost is on its line, so a reopened chat shows it at once.
      }
      else panel.SetChat(json);
    }

    /// <summary>
    /// A user message arrives: record it, then run ONE agent turn (WP-3 Stage A).
    ///
    /// The turn is one <see cref="DwightSession"/> operation over however many tool calls it takes
    /// (#193d/#223), adopted onto THIS panel so it is not replaced mid-conversation - so Stop covers the
    /// whole loop, and a second message while it runs is refused with an inline notice rather than
    /// queued (#222 Q4).
    ///
    /// The capability is <see cref="TurnCapability.ReadOnly"/>, passed in memory and never read from the
    /// persisted thread (#220): this turn has no tool with which to change anything.
    /// </summary>
    private static void OnUserMessage(WebPanel wp, GH_Document doc, string threadId, string text)
    {
      // Brief DX: the image held for this turn goes with it, kept first. A refusal here sends nothing, so the words go back in the box and the chip stays.
      StoredImage image = null;
      if (_heldImages.TryGetValue(threadId ?? "", out var held))
      {
        var model = ChatModel(doc, threadId);
        var refusal = model != null && !model.Vision ? ChatImage.NoVisionAtSendLine(model.DisplayName, VisionAdviceFor()) : null;
        if (refusal == null)
        {
          image = ChatImageStore.Save(ImagesDir(doc), held);
          if (image == null) refusal = ChatImage.NotKeptLine;
        }
        if (refusal != null)
        {
          AppendPluginHtml(wp, doc, threadId, System.Net.WebUtility.HtmlEncode(refusal));
          try { wp.SetComposerDraft(text); } catch { }
          return;
        }
        _heldImages.Remove(threadId ?? "");
        try { wp.SetChatHeld(null); } catch { }
      }
      try { CanvasGlow.ClearIfOwner(ScopeFor(threadId, doc)); } catch { }   // Brief EE: a glow the chat's show_on_canvas drew ends with the person's next message
      _autoCount.Reset(threadId);   // Brief AL: a message of the user's own starts a new run; the close line counts from here
      _forcedTries.Reset(threadId);   // Brief BT: and buys one forced try
      AppendUserText(wp, doc, threadId, text, image);
      // Brief DX unit 8: asked to look with nothing to look at. The turn still runs, because the model's tools read the canvas; the person learns what those reads are not.
      if (ScreenAsk.Asks(text, image != null))
        AppendReport(wp, doc, threadId, System.Net.WebUtility.HtmlEncode(ScreenAsk.Line), ScreenAsk.Fact);
      RunTurn(wp, doc, threadId, TurnCapability.ReadOnly);
    }

    // ---- the composer's image (Brief DX) -------------------------------------------------------------------

    /// <summary>The image waiting for each thread's next send: in memory, on the UI thread, and gone with Rhino. It reaches the record only through <see cref="OnUserMessage"/>, so it leaves only because the person sent it.</summary>
    private static readonly Dictionary<string, PreparedImage> _heldImages = new Dictionary<string, PreparedImage>(StringComparer.Ordinal);
    /// <summary>The newest attach for each thread: reading an image takes a moment off the UI thread, and a later one must not lose to an earlier one that finishes after it.</summary>
    private static readonly Dictionary<string, int> _attachGen = new Dictionary<string, int>(StringComparer.Ordinal);

    private static string ImagesDir(GH_Document doc) => CommandHistory.ImagesDirFor(doc);

    /// <summary>The model this thread's next turn runs on, as <see cref="RunTurn"/> resolves it, or null when none resolves (the front door then says why).</summary>
    private static ModelInfo ChatModel(GH_Document doc, string threadId)
    {
      try { return DwightSettings.Current.ResolveChatModel(CommandHistory.ThreadModelId(doc, threadId), CommandHistory.ThreadPosture(doc, threadId), out _, out _)?.Model; }
      catch { return null; }
    }

    /// <summary>The models a no-vision refusal may name: the menu's own list for the current provider, then the whole catalogue for the none-here case.</summary>
    private static ChatImage.VisionAdvice VisionAdviceFor()
    {
      try { return VisionAdvisor.Advise(ModelCatalogue.ForProvider(DwightSettings.Current.EffectiveProvider), ModelCatalogue.All); }
      catch { return null; }
    }

    /// <summary>The shell's only way in for an image: a person's click, paste or drop on this composer, or its asking for the bytes of one already in the thread.</summary>
    private static void OnChatImage(WebPanel wp, GH_Document doc, string threadId, string what)
    {
      if (string.IsNullOrEmpty(what)) return;
      if (what.StartsWith("want:", StringComparison.Ordinal)) { SendImageBytes(wp, doc, threadId, what.Substring(5)); return; }
      if (what == "clear") { _heldImages.Remove(threadId ?? ""); _attachGen[threadId ?? ""] = AttachGen(threadId) + 1; try { wp.SetChatHeld(null); } catch { } return; }
      if (what == "big") { RefuseImage(wp, doc, threadId, ChatImage.TooBigLine); return; }
      if (what == "type") { RefuseImage(wp, doc, threadId, ChatImage.WrongTypeLine); return; }

      var model = ChatModel(doc, threadId);
      if (model != null && !model.Vision) { RefuseImage(wp, doc, threadId, ChatImage.NoVisionLine(model.DisplayName, VisionAdviceFor())); return; }
      if (ImagesDir(doc) == null) { RefuseImage(wp, doc, threadId, ChatImage.NotKeptLine); return; }

      Func<ImageIntake> read;
      if (what == "pick")
      {
        var path = PickImageFile();
        if (path == null) return;
        read = () => ReadImageFile(path);
      }
      else read = () => ChatImageCodec.FromDataUrl(what);

      var gen = AttachGen(threadId) + 1;
      _attachGen[threadId ?? ""] = gen;
      Task.Run(() =>
      {
        ImageIntake intake;
        try { intake = read(); } catch { intake = ImageIntake.No(ImageRefusal.Unreadable); }
        Ui(() => { if (AttachGen(threadId) == gen) HoldImage(wp, doc, threadId, intake); });
      });
    }

    private static int AttachGen(string threadId) => _attachGen.TryGetValue(threadId ?? "", out var g) ? g : 0;

    private static void RefuseImage(WebPanel wp, GH_Document doc, string threadId, string line) =>
      AppendPluginHtml(wp, doc, threadId, System.Net.WebUtility.HtmlEncode(line));

    private static void HoldImage(WebPanel wp, GH_Document doc, string threadId, ImageIntake intake)
    {
      if (intake.Image == null) { RefuseImage(wp, doc, threadId, intake.RefusalLine ?? ChatImage.UnreadableLine); return; }
      var replaced = _heldImages.ContainsKey(threadId ?? "");
      _heldImages[threadId ?? ""] = intake.Image;
      try { wp.SetChatHeld(ChipJson(intake.Image, replaced ? ChatImage.ReplacedNote : "", ChatModel(doc, threadId))); } catch (Exception ex) { TurnLog("image chip not drawn: " + ex.Message); }
    }

    /// <summary>The chip, drawn from the bytes that go: the text shown is the text sent.</summary>
    private static string ChipJson(PreparedImage image, string note, ModelInfo model) => new System.Text.Json.Nodes.JsonObject
    {
      ["src"] = ChatImage.DataUrl(image.MediaType, Convert.ToBase64String(image.Bytes)),
      ["line"] = ChatImage.ChipLine(image.Width, image.Height, model != null && model.ImagePatchTokens),
      ["note"] = note ?? "",
    }.ToJsonString();

    /// <summary>The chip as this thread and its model have it now, or none: on opening a chat, since the shell keeps one chip per page, and when the model changes, since the number is the model's.</summary>
    private static void RedrawChip(WebPanel wp, GH_Document doc, string threadId)
    {
      try { wp.SetChatHeld(_heldImages.TryGetValue(threadId ?? "", out var held) ? ChipJson(held, "", ChatModel(doc, threadId)) : null); }
      catch (Exception ex) { TurnLog("image chip not drawn: " + ex.Message); }
    }

    /// <summary>The host's own picker, opened by the person's click on the paper-clip.</summary>
    private static string PickImageFile()
    {
      try
      {
        using (var dlg = new Eto.Forms.OpenFileDialog { Title = "Attach an image", MultiSelect = false })
        {
          dlg.Filters.Add(new Eto.Forms.FileFilter("PNG or JPEG", ".png", ".jpg", ".jpeg"));
          return dlg.ShowDialog(DwightDock.Host()) == Eto.Forms.DialogResult.Ok && !string.IsNullOrEmpty(dlg.FileName) ? dlg.FileName : null;
        }
      }
      catch (Exception ex) { TurnLog("image picker failed: " + ex.Message); return null; }
    }

    private static ImageIntake ReadImageFile(string path)
    {
      var info = new System.IO.FileInfo(path);
      if (!info.Exists) return ImageIntake.No(ImageRefusal.Unreadable);
      if (info.Length > ChatImage.MaxFileBytes) return ImageIntake.No(ImageRefusal.TooBig);
      return ChatImageCodec.Prepare(System.IO.File.ReadAllBytes(path));
    }

    /// <summary>The bytes of an image in this thread, for its thumbnail: the file the provider is sent, read the same way. Only a hash this thread's record holds is answered, so the shell cannot read anything else.</summary>
    private static void SendImageBytes(WebPanel wp, GH_Document doc, string threadId, string sha)
    {
      var image = CommandHistory.ThreadMessages(doc, threadId)?.FirstOrDefault(m => m?.Image != null && m.Image.Sha256 == sha)?.Image;
      var bytes = ChatImageStore.Load(ImagesDir(doc), image);
      try
      {
        if (bytes == null) wp.SetChatImageBytes(sha, null, ChatImage.NotFoundChip);
        else wp.SetChatImageBytes(sha, ChatImage.DataUrl(image.MediaType, Convert.ToBase64String(bytes)), null);
      }
      catch (Exception ex) { TurnLog("image thumbnail not drawn: " + ex.Message); }
    }

    /// <summary>
    /// Run ONE agent turn at a given capability.
    ///
    /// THE CAPABILITY IS A PARAMETER, AND THAT IS THE POINT (#220). It is not a field, not a property of
    /// the thread, and not derived from anything persisted - it arrives on the call stack from whoever
    /// started the turn, which is either this typed message (always
    /// <see cref="TurnCapability.ReadOnly"/>) or a consent click. There is therefore no variable holding
    /// a privilege that a restart could restore: the grant cannot outlive the turn because there is
    /// nowhere for it to live. That is WP-1's non-serialisable-<c>Live</c> instrument (#161) taken one
    /// step further - not "absent by construction after a reopen" but absent between turns, always.
    /// </summary>
    /// <param name="approvedRegions">
    /// For a BUILD turn: the regions the user approved (Stage C). It is REQUIRED there - a Build turn
    /// without one is refused before it starts, so the chat can never reach an unscoped build - and it
    /// bounds what the turn may write however the model asks (#338 in address space). Null for every
    /// other capability, which has no regions to scope.
    /// </param>
    private static void RunTurn(WebPanel wp, GH_Document doc, string threadId, TurnCapability capability,
                                IReadOnlyList<string> approvedRegions = null, IReadOnlyList<ApprovedScript> approvedScripts = null,
                                RunScriptGrant runGrant = null, bool autoApproved = false, bool isAnnouncementFollowUp = false,
                                bool isBuildContinuation = false)
    {
      var selection = LlmActions.SelectedGuids();

      // U90: A RUN_SCRIPT TURN WITHOUT ITS GRANT CANNOT START, for the reason a build without its scope cannot: the grant is the card.
      if (capability == TurnCapability.RunScript && (runGrant == null || string.IsNullOrWhiteSpace(runGrant.Source)))
      {
        AppendPluginHtml(wp, doc, threadId, "Dwight could not start that script: the approved source was lost. Ask again and I will re-offer it.");
        return;
      }

      // A BUILD TURN WITHOUT AN APPROVED SCOPE CANNOT START. The scope is what makes the card a contract
      // (#338), so a build with none is not a build with a wide scope - it is a build nobody agreed to.
      // Structural: the only caller that passes Build is the consent click, and it always has the set.
      if (capability == TurnCapability.Build && (approvedRegions == null || approvedRegions.Count == 0))
      {
        AppendPluginHtml(wp, doc, threadId,
          "Dwight could not start that build: the approved regions were lost, so there is no scope to " +
          "build in. Ask again and I will re-offer it.");
        return;
      }

      // #303: refused BEFORE Begin and before any spend. Without a document every tool returns
      // no_document, so the model would be paid to discover, one refusal at a time, something the plugin
      // knew for certain before the turn started. A deterministic refusal belongs in C#, not in a bill.
      if (doc == null) { AppendPluginHtml(wp, doc, threadId, NoDocumentNotice); return; }

      var ctx = DwightSession.BeginOn(wp, DwightVerbs.Build, selection);
      if (ctx == null) { AppendPluginHtml(wp, doc, threadId, BusyHtml(threadId)); return; }

      var settings = DwightSettings.Current;

      // Lane B 3.1 (#37): THE AGENT RUNS ONLY ON A MODEL THAT CAN DRIVE IT, THROUGH A CONNECTION THAT CARRIES
      // TOOLS. It replaces #250's provider-only refusal, which ran before the model was resolved and so could only
      // test the provider - a pinned Claude Haiku (catalogue agentic=false) went straight through it on Anthropic.
      //
      // Still refused HERE, before Activate, so nothing is claimed and nothing is spent, and in words - "Dwight had
      // nothing to say to that" would be a panel claiming a state the system contradicts (7.0 WP-1.3).
      //
      // THE SEQUENCE - eligibility BEFORE the key, and the pinned rule - is AgentFrontDoor's, GH-free and tested,
      // because it is the property a reshuffle loses silently (#885). The model is resolved FIRST, because the
      // check needs it: the provider-only test that stood here ran before the model existed, so it could only
      // check something coarser - and a pinned Haiku got through.
      // Brief AO: THE THREAD'S OWN PIN FIRST, then the Settings pin, then the posture - and the same front door, unchanged.
      var threadModelId = CommandHistory.ThreadModelId(doc, threadId);
      var resolved = settings.ResolveChatModel(threadModelId, CommandHistory.ThreadPosture(doc, threadId), out var threadPinIgnored, out var effectivePinId);
      var door = AgentFrontDoor.Check(effectivePinId, resolved?.Model, settings.EffectiveProvider,
                                      LlmProviders.AgentRefusal, LlmProviders.HasKey);
      if (door.Refusal == FrontDoorRefusal.NotEligible)
      {
        // Plain text from the check; escaping is the destination's (#335).
        AppendPluginHtml(wp, doc, threadId, System.Net.WebUtility.HtmlEncode(door.Text));
        return;
      }
      if (door.Refusal == FrontDoorRefusal.NoKey)
      {
        // Nothing to release: the busy flag is claimed at Activate, not at Begin (ADR-089), precisely so
        // a notice-only path like this one cannot wedge the gate.
        AppendPluginHtml(wp, doc, threadId, NoKeyNotice);
        return;
      }

      // Brief AO: a thread pin that no longer resolves falls back to the posture, and SAYS so - once per pin, never silently.
      if (threadPinIgnored && SaidPinFallback(threadId, threadModelId))
        AppendPluginHtml(wp, doc, threadId, System.Net.WebUtility.HtmlEncode(ChatHeader.PinFallbackLine));

      var scope = ScopeFor(threadId, doc);
      var history = ModelTranscript(wp, doc, threadId, resolved?.Model);
      var situation = Situation(doc, selection, scope);   // read on the UI thread, before the worker starts
      // U90: Full control is read from the THREAD, on the UI thread, never from the Settings default. When it is on, one paragraph states
      // it to the model as a fact; when it is off the prompt is exactly what it was.
      var fullControl = CommandHistory.ThreadFullControl(doc, threadId);
      var firstRunScriptCard = !CommandHistory.ThreadRunScriptCardShown(doc, threadId);
      if (fullControl) situation = (situation ?? "") + "\n\n" + Dwight.Agent.FullControl.ThreadFact(CommandHistory.ThreadApprovalMode(doc, threadId));
      // Brief CA: a run_script card still waiting, and the scripts a card showed that no build has landed. Facts, because the model has no other way to know them.
      var waitingScript = fullControl ? _consents.WaitingSeed(threadId, Dwight.Agent.FullControl.Kind) : null;
      if (waitingScript != null) situation = (situation ?? "") + "\n\n" + Dwight.Agent.FullControl.WaitingFact(waitingScript.Why, waitingScript.Source);
      // Brief CN: what the turn that ran out of steps read, from its calls, so this one can go on from there.
      var lastReads = _exhaustedReads.FactFor(threadId);
      if (lastReads != null) situation = (situation ?? "") + "\n\n" + lastReads;
      var scriptsOnFile = capability == TurnCapability.Build ? null : ApprovedTurnFact.ScriptsOnFileFact(_scriptsOnFile.For(threadId));
      if (scriptsOnFile != null) situation = (situation ?? "") + "\n\n" + scriptsOnFile;

      // #573: tell the model, as a FACT, whether the canvas changed since its last turn. The prompt already
      // told it to read the canvas before answering, and it answered "what's on the map" from memory after
      // the map had been undone. An instruction it must remember does not bind; a fact the plugin states
      // does not need remembering.
      var canvasFact = CanvasChangeFact.For(LastCanvas(threadId), ReadCanvas(doc),
        history.Any(m => m.Role == LlmRole.Assistant));

      // #624/#634: what the last approved build in this conversation ACTUALLY did, at every turn start until a
      // newer one supersedes it. Read here, after every notice-only return above, so a turn that never reached
      // the model does not advance the turns-ago count.
      var lastBuild = _lastBuild.ForTurn(threadId);
      var buildFact = lastBuild == null ? null : BuildOutcomeFact.For(lastBuild.Record, lastBuild.TurnsAgo);

      // #611c: an approved turn is TOLD what it was approved for, from the same set the ledger is seeded with.
      // The build that never ran was told to raise cards and never told that this turn was the approval.
      // #625/#647: an approved build is told what its rebuilds cut - through the same claim and cut read the card and
      // the build use, and in this scope's id space, so the ids it names are the ids its tools accept.
      var cutsByRegion = capability == TurnCapability.Build
        ? GhBuildRegion.CutsForRebuild(doc, AgentScope.LedgerForDocument(doc), scope.Ids, approvedRegions)
        : null;
      // U33 (#954): and what they KEEP - the components the user edited since Dwight placed them - through the same
      // claim, so the model wires from them instead of placing a second copy the duplicate check would refuse.
      var keptByRegion = capability == TurnCapability.Build
        ? GhBuildRegion.KeptForRebuild(doc, AgentScope.LedgerForDocument(doc), scope.Ids, approvedRegions)
        : null;

      // U22 (#990): and which components it has READ that changed since - a fact the plugin states at turn start, so
      // the model never has to remember to re-read. Never throws; null when nothing it read has changed.
      // Brief V: web search through the provider's own means, only when the user turned it on. Null when off, and then nothing
      // below mentions it. The keys are read at the moment a search runs, from the credential store, and go to a header only.
      var webSearch = WebSearchPlan.For(settings.EffectiveProvider, settings.WebSearchEnabled, resolved.Model.Id,
        () => Dwight.Credentials.CredentialStore.Load(Dwight.Credentials.CredentialStore.OllamaCloudTarget),
        () => Dwight.Credentials.CredentialStore.Load(Dwight.Credentials.CredentialStore.GoogleTarget));

      var system = TurnSystemText.Compose(situation, canvasFact, buildFact, capability, approvedRegions, cutsByRegion,
                                          keptByRegion, GhAgent.StaleReadsFact(doc, scope.Ids),
                                          // U63: the user's build style, a fact the plugin states at every turn start.
                                          BuildStyle.Fact(settings.BuildStyle), approvedScripts, webSearch?.Fact);

      // D7: consent cards raised during this turn, appended AFTER its prose so the reasoning reads first.
      // A per-turn local rather than a field: there is nowhere for a card to leak between turns.
      var pendingCards = new List<string>();
      // Brief AL: the cards of this turn the thread's approval mode approves on its own, and the mode as the THREAD holds it
      // (read here, on the UI thread, from the thread record and never from the Settings default).
      var pendingAuto = new List<AutoPlan>();
      var approvalMode = CommandHistory.ThreadApprovalMode(doc, threadId);
      var autoToRun = new List<AutoPlan>();
      var announcementFollowUpDue = false;   // U97, Brief BT: this turn announced a change, raised no card, and gets the follow-up

      // Stage D: what THIS turn built, per region - the tidy's subject. A per-turn local for the same
      // reason as the cards: there is nowhere for it to leak, so a later turn cannot tidy a region it did
      // not build.
      var builtThisTurn = new Dictionary<string, RegionOutcome>(StringComparer.Ordinal);
      var attemptsThisTurn = new Dictionary<string, int>(StringComparer.Ordinal);
      // Phase-9 U20: the per-TARGET repair budget for THIS TURN, and each target's last outcome for the
      // turn-end line (3c) - see EditAttempts. A turn-scoped local rather than thread state, so it dies with
      // the turn as the approved scope does (#770 shape).
      var editAttemptsThisTurn = new EditAttempts();

      // #624: what this approved build turn did, per region, for the NEXT turns' fact. Null for every other
      // capability - only an approved build supersedes the last build's record.
      var buildRecord = capability == TurnCapability.Build ? new BuildTurnRecord(approvedRegions) : null;

      // PROGRESS GOES ON THE COMPOSER'S LINE (Brief BI), through SetStatus, never through SetThinking: the loader clears
      // chatmode and rewrites the body, so showing it would destroy the conversation it is reporting on. The shell shows
      // the step in place of the cost until the turn clears it, and the canvas glow (lavender, set by BeginOn) is the other
      // half of the thinking indication.
      try { wp.SetStatus("Reading your definition…"); } catch { }
      try { wp.SetChatBusy(true); } catch { }   // #259a: Stop becomes reachable for the whole turn

      // A Stop or a session timeout must NOT render the standard state view over this panel - it would
      // replace the conversation with a notice about the conversation. Report inline, in the thread.
      ctx.OnFailed = (kind, title, message) =>
      {
        // D5: the work stopped, so the progress line must stop too. A status line still reading
        // "Reading your definition..." after a Stop is the same class as the loader that started this
        // arc - a surface asserting a state the system has left.
        //
        // INSTRUMENTED (#290), because the first fix was reported as landing on both exit paths and did
        // not land on the one actually taken. A mid-turn Stop now NAMES its path on the command line, so
        // the next attempt is a diagnosis rather than a second guess.
        TurnLog("OnFailed kind=" + kind + " title=" + title + " -> clearing status + composer");
        RememberCanvas(doc, threadId);   // #573: a stopped turn may still have changed the canvas
        try { wp.SetStatus(""); } catch (Exception ex) { TurnLog("  SetStatus threw: " + ex.Message); }
        try { wp.SetChatBusy(false); } catch (Exception ex) { TurnLog("  SetChatBusy threw: " + ex.Message); }
        AppendPluginHtml(wp, doc, threadId, System.Net.WebUtility.HtmlEncode(message ?? title ?? ""));
      };

      var gen = DwightSession.Activate(wp, capability == TurnCapability.Build
        ? DwightConfig.AgentBuildSessionTimeoutMs
        : DwightConfig.AgentTurnSessionTimeoutMs);
      var cancel = DwightSession.CurrentToken;
      var sw = Stopwatch.StartNew();

      // U61: a live line during a long step. The footer otherwise keeps the last tool's name for as long as the next model reply
      // takes, which in the tower session was minutes and looked stuck. Once a second the step's age is re-read AT PAINT TIME, so a
      // tick queued just before the next tool call finds a young step and paints nothing over that tool's own line.
      var progress = new StepProgress(() => sw.ElapsedMilliseconds);
      var progressTimer = new System.Threading.Timer(_ => Ui(() =>
      {
        var line = progress.Line();
        if (line != null && DwightSession.IsCurrent(gen)) { try { wp.SetStatus(line); } catch { } }
      }), null, 1000, 1000);

      Task.Run(() =>
      {
        TurnResult result;
        try
        {
          result = AgentLoop.Run(new AgentLoop.TurnRequest
        {
          Provider = ProviderFor(settings, resolved),
          System = system,
          Messages = history,
          Capability = capability,
          ApprovedRegions = approvedRegions,
          ApprovedScripts = approvedScripts,
          FullControl = fullControl,
          RunGrant = runGrant,
          // A script that overruns its clock finishes later, on this thread: say so in the panel, through the report door, so the model reads it too.
          OnLateRun = line => Ui(() => AppendReport(wp, doc, threadId, System.Net.WebUtility.HtmlEncode(line), line)),
          Scope = scope,
          Limits = capability == TurnCapability.RunScript ? RunScriptLimits(runGrant) : Limits(capability),
          Thinking = resolved.Thinking,
          Effort = resolved.Effort,
          ForceToolName = isAnnouncementFollowUp ? AgentHostTools.RequestMutation : null,   // Brief BT
          Cancel = cancel,
          WebSearch = webSearch,
          OnLog = line => Ui(() => TurnLog(line)),   // U71: the search tool leaves a tool line like the others
          IsCurrent = () => DwightSession.IsCurrent(gen),
          OnHostTool = (name, args) =>
          {
            var envelope = HostTool(doc, threadId, pendingCards, pendingAuto, approvalMode, fullControl, firstRunScriptCard, editAttemptsThisTurn, AgentHostTools.KindApprovedBy(capability),
              approvedRegions, name, args);
            var line = AgentLoopCore.DispatchLogLine(name, envelope);   // #610: was a card shown, or refused?
            Ui(() => TurnLog(line));
            return envelope;
          },
          OnToolCompleted = (name, args, envelope) => Ui(() =>
          {
            TurnLog(AgentLoopCore.DispatchLogLine(name, envelope));
            OnToolLanded(wp, doc, threadId, name, args, envelope, builtThisTurn, attemptsThisTurn, buildRecord);
          }),
          OnToolCall = tool =>
          {
            progress.ToolCalled();   // U61: the step before this call has ended; the next begins now
            // #613: an approval that arms at dispatch is armed HERE, when its acting tool is called - on
            // this thread and before the call runs, so no later request in the turn can read it stale.
            try { MutationLogFor(threadId).ToolDispatched(AgentHostTools.KindApprovedBy(capability), tool); } catch { }
            Ui(() =>
            {
              if (DwightSession.IsCurrent(gen)) { try { wp.SetStatus("Reading the canvas (" + tool + ")…"); } catch { } }
              else TurnLog("tool-progress suppressed after the turn was superseded (" + tool + ")");
            });
          },
        });
        }
        finally
        {
          progress.Finish();
          try { progressTimer.Dispose(); } catch { }
        }
        sw.Stop();

        Ui(() =>
        {
          // Stopped: DwightSession.Failed already reported it in-thread (ctx.OnFailed). The composer must
          // still be handed back, or Stop would leave the chat unusable - the kill switch wedging the
          // surface it exists to free.
          // #624: the build turn's record supersedes the last one on BOTH branches - a stopped build did
          // things too, and the next turn must be told what, not left with the model's account of it.
          if (buildRecord != null)
          {
            buildRecord.StoppedEarly = !DwightSession.IsCurrent(gen) || result.Outcome != TurnOutcome.Complete;
            _lastBuild.Put(threadId, buildRecord);
          }

          if (!DwightSession.IsCurrent(gen))
          {
            TurnLog("completion on a SUPERSEDED generation (outcome " + result.Outcome + ") -> clearing status + composer");
            // Brief AK, DL: Stop, the session's clock and a Restart bump the generation, so THIS is where a stopped turn ends, and the provider bills it all the same. A Stop that
            // landed after the provider finished ends here too, with the whole bill and no estimate. A Restart has closed the panel by now: the ledger line is written first and
            // owes the screen nothing, and the words go into the thread's record, where the reopened chat shows them.
            if (ctx.ForceEnded)
            {
              var stoppedSpend = RecordTurnSpend(wp, doc, threadId, resolved.Model, result, sw.Elapsed);
              TurnLog("  the stopped turn's spend: " + (stoppedSpend == null ? "none, nothing went out" : SpendMath.TurnReceipt(stoppedSpend, null)));
            }
            RememberCanvas(doc, threadId);
            if (AgentLoopCore.OrphanFreesComposer(wp, DwightSession.RunningPanel))
            {
              try { wp.SetStatus(""); } catch (Exception ex) { TurnLog("  SetStatus threw: " + ex.Message); }
              try { wp.SetChatBusy(false); } catch (Exception ex) { TurnLog("  SetChatBusy threw: " + ex.Message); }
            }
            else TurnLog("  a newer turn is running on this page: its status and composer are left to it");
            try { RefreshHeader(wp, doc, threadId); } catch { }
            DwightSession.Done(gen);
            return;
          }
          TurnLog(AgentLoopCore.TurnEndLine(threadId, result));
          _exhaustedReads.Note(threadId, result);
          // U71: Anthropic's own search is not a tool Dwight dispatches, so its usage count is its only trace.
          if (webSearch != null && webSearch.UsesServerTool)
          {
            var searchLine = AgentLoopCore.SearchLogLine(result.Usage?.WebSearchRequests ?? 0);
            if (searchLine != null) TurnLog(searchLine);
          }
          try
          {
            // #657/#667: the backstop. An approved build turn that called no build_region at all gets a plugin line
            // ABOVE its prose - true whatever the prose says. The model's text is rendered unedited beneath it (#235).
            var noBuildBanner = AgentLoopCore.NoBuildBanner(capability, result, approvedRegions);
            if (noBuildBanner != null) AppendPluginHtml(wp, doc, threadId, noBuildBanner);

            // Brief BT: a follow-up that raised no card does not get to explain itself. Its prose is the excuse the forcing exists to end,
            // so it is not shown as the reason and does not enter the transcript; the plugin says what happened, below.
            var followUpNoCard = AgentLoopCore.FollowUpRaisedNoCard(isAnnouncementFollowUp, result);
            if (!string.IsNullOrWhiteSpace(result.Text) && !followUpNoCard)
              AppendModelMarkdown(wp, doc, threadId, result.Text);

            // ...and only THEN the cards it raised, so a request never precedes its reasoning (D7).
            // Brief AL: a card the mode approves on its own is appended MARKED, and approved once this turn has ended and freed the
            // session (below). Only a turn that COMPLETED does this: a stopped or failed turn shows the card waiting, because a card
            // marked "approved automatically" that never ran would be the panel saying something untrue.
            // Brief BG: the cards this turn's new card retired (U96) become "superseded" receipts as the new one is shown.
            if (pendingCards.Count > 0) ApplySuperseded(wp, doc, threadId);
            for (var ci = 0; ci < pendingCards.Count; ci++)
            {
              var auto = result.Outcome == TurnOutcome.Complete ? pendingAuto.FirstOrDefault(a => a.CardIndex == ci) : null;
              AppendPluginHtml(wp, doc, threadId, auto != null ? auto.AutoHtml : pendingCards[ci]);
              if (auto != null) autoToRun.Add(auto);
            }

            // U32 (#924, #932): A TURN WHOSE TEXT ANNOUNCED A CARD, A BUILD OR A SUBMISSION AND WHOSE TOOL CALLS
            // ASKED FOR NONE gets Dwight's own line, under the model's prose and after any card it did raise. Through
            // the REPORT door, ruled at the wave-2 brief: the user reads it, and the model reads the same fact as a
            // PLUGIN FACT at its next turn - one line, both channels, so they cannot disagree (#624). The decision is
            // AgentLoopCore's (tested there through the real loop); it is null on a turn that asked, on a turn that
            // announced nothing, and on a Stop.
            //
            // Brief BT: the follow-up is decided here, on the STRICT catch, because under "Do not ask" a forced card runs. When it is due, its
            // own line says what this one says, and a follow-up's own failure gets the sentence below, so neither draws this line too.
            announcementFollowUpDue = autoToRun.Count == 0 &&
              AutoFollowUp.ShouldRunForAnnouncement(!string.Equals(approvalMode, ApprovalMode.Ask, StringComparison.Ordinal), result.ProviderForces,
                                                    AgentLoopCore.AnnouncedForForcing(capability, result), isAnnouncementFollowUp) &&
              _forcedTries.TryTake(threadId);
            // Brief CL: with an approval just above this turn, the line says nothing further was asked. "No card was raised" beneath that card would be false to read.
            var followsApproval = ChatToTranscript.FollowsApproval(CommandHistory.ThreadMessages(doc, threadId));
            var nothingAsked = announcementFollowUpDue || followUpNoCard ? null : AgentLoopCore.NothingAskedHtml(capability, result, followsApproval);
            if (nothingAsked != null)
              AppendReport(wp, doc, threadId, nothingAsked, AgentLoopCore.NothingAskedReport(capability, result, followsApproval));

            // U20 3c: DWIGHT'S OWN WORDS FOR AN EDIT THAT ENDED THE TURN REFUSED. The refusal went to the model
            // mid-loop, deliberately, so it could try again; once the turn is over with no card for that
            // component, it is a settled fact about what Dwight checked, and the user reads it in Dwight's voice
            // rather than only in the model's paraphrase (#235). Skipped on a Stop, which already says what
            // happened. A REPORT rather than chrome: the next turn starts from chat history, never from this
            // turn's tool traffic (#223), so without it the model would lose the refusal it was given - and one
            // line on both channels cannot disagree (#624).
            foreach (var line in editAttemptsThisTurn.RefusedLines(stopped: result.Outcome == TurnOutcome.Stopped))
              AppendReport(wp, doc, threadId, ScriptEditReport.NotAppliedHtml(line), ScriptEditReport.NotAppliedText(line));

            // Every non-Complete outcome says what happened, in the user's language, and keeps whatever
            // the turn produced (#223). An empty turn that says nothing would be the panel showing a
            // state the run contradicts. A provider failure carries ITS OWN reason too - the adapters
            // already phrase those for a human ("Claude rejected the API key (401)"), and a generic
            // "could not finish" would throw away the one sentence that says what to do about it.
            // Brief V: a search the provider could not run, in Dwight's words. The turn went on without it, so this is a
            // line about the turn, not a failure of it.
            foreach (var searchSentence in WebSearch.ErrorSentences(result.WebSearchErrors))
              AppendPluginHtml(wp, doc, threadId, System.Net.WebUtility.HtmlEncode(searchSentence));

            var note = result.Note;
            if (result.Outcome == TurnOutcome.Failed && !string.IsNullOrWhiteSpace(result.Error))
              note = (note ?? "").TrimEnd() + " " + result.Error;

            // Brief BT: through the report door, so the next turn's model reads what the user read. A failed or truncated follow-up keeps its
            // own note, which already says what happened in the plugin's words.
            if (followUpNoCard && result.Outcome == TurnOutcome.Complete)
            {
              var noCard = AutoFollowUp.NoCardSentence(note, result.RefusedRequests);
              AppendReport(wp, doc, threadId, System.Net.WebUtility.HtmlEncode(noCard), noCard);
            }
            else if (note != null) AppendPluginHtml(wp, doc, threadId, Dwight.Llm.ProviderLimit.NoteHtml(note));   // Brief CO: the provider's own words quieter, beneath Dwight's
            // #256: a turn that raised a consent card ended with something very definite on screen, so
            // the empty-reply filler must not talk over it. #672: neither must it talk over a build that ran.
            else if (AgentLoopCore.ShowEmptyReplyFiller(capability, result))
              AppendPluginHtml(wp, doc, threadId, System.Net.WebUtility.HtmlEncode(AgentLoopCore.EmptyReplyLine(result)));

            // ---- Stage E: THE CLOSING REPORT ----------------------------------------------------
            //
            // The running summary reports each region AS IT LANDS - but a region that was approved and
            // never attempted produces no line at all, and silence is exactly how a half-finished build
            // reads as a finished one. So every approved address is reconciled against what actually
            // happened, including the ones nothing happened to (#223: an honest stop keeps what the turn
            // produced AND says what happened).
            if (capability == TurnCapability.Build && approvedRegions != null)
            {
              // #616: THIS turn's outcomes, never the thread's record - a later turn that attempts nothing
              // must not close over an earlier turn's row. builtThisTurn holds each region's latest outcome
              // this turn, written by RecordBuild as each tool result landed.
              var closing = BuildProgress.ClosingHtml(approvedRegions, builtThisTurn,
                stoppedEarly: result.Outcome != TurnOutcome.Complete,
                autoClause: ApprovalMode.CloseClause(_autoCount.Shown(threadId, autoToRun.Count)));   // Brief AL: the clause rides the close line
              if (closing != null) AppendPluginHtml(wp, doc, threadId, CardReceipt.CloseLine(closing, result.Outcome != TurnOutcome.Complete));   // Brief BG
            }
            else if (capability != TurnCapability.ReadOnly)
            {
              // Brief AL: a run with no close line of its own (a slider, a rewire, a placement without a map, a script edit) still says how
              // many cards nobody clicked, in the plugin's own count.
              var autoLine = ApprovalMode.CloseClause(_autoCount.Shown(threadId, autoToRun.Count));
              if (autoLine != null) AppendPluginHtml(wp, doc, threadId, System.Net.WebUtility.HtmlEncode(autoLine));
            }

            // ---- Stage D: THE TIDY, as the run's LAST step (#238/#239, Q1) ----------------------
            //
            // Once per run rather than once per region: per-region would churn the canvas three times for
            // one instruction. Its OWN undo record, so each Ctrl-Z undoes exactly one thing and the user
            // can keep the build while dropping the tidy. It carries its own moved-set guard, because
            // neither preservation nor the fingerprint can see a move (#353).
            if (builtThisTurn.Count > 0) TidyAfterBuild(wp, doc, threadId, builtThisTurn, attemptsThisTurn);

            RecordTurnSpend(wp, doc, threadId, resolved.Model, result, sw.Elapsed);
            try { wp.SetStatus(""); } catch { }   // the live step is done; the line goes back to the cost
            try { RefreshHeader(wp, doc, threadId); } catch { }
          }
          finally
          {
            SaveIdSpace(doc, scope);
            // #573: the canvas as this turn left it. If a tidy was scheduled above, its own completion
            // records again, later, and that reading supersedes this one.
            RememberCanvas(doc, threadId);
            try { wp.SetChatBusy(false); } catch { }
            DwightSession.Done(gen);

            // U94: AN AUTOMATICALLY APPROVED BUILD THAT ENDED IN A REVERT OR A REFUSAL GETS ONE READ-ONLY FOLLOW-UP TURN. Nobody clicked, nobody is
            // there to type, and the plan would stop on a diagnostic the model has not read. The outcome is already the next turn's start fact
            // (BuildOutcomeFact); the decision is AutoFollowUp.ShouldRun, GH-free and tested. It counts against the thread's bounds because it is an
            // ordinary turn, and records its spend like one.
            if (autoApproved && capability == TurnCapability.Build && autoToRun.Count == 0 && !buildRecord.StoppedEarly)
            {
              var end = AutoFollowUp.FlowEnd.Succeeded;
              foreach (var o in builtThisTurn.Values)
              {
                if (o == RegionOutcome.Reverted || o == RegionOutcome.RevertedWithResidue) { end = AutoFollowUp.FlowEnd.Reverted; break; }
                if (o == RegionOutcome.Refused) end = AutoFollowUp.FlowEnd.Refused;
              }
              if (AutoFollowUp.ShouldRun(true, end))
              {
                try
                {
                  // Through the report door: this is the follow-up's only new message, and a transcript ending on the model's closing text is refused.
                  AppendReport(wp, doc, threadId, System.Net.WebUtility.HtmlEncode(AutoFollowUp.Line), AutoFollowUp.BuildEndedFact(builtThisTurn, approvedRegions));
                  RunTurn(wp, doc, threadId, TurnCapability.ReadOnly);
                }
                catch (Exception ex) { TurnLog("automatic follow-up threw: " + ex.Message); }
              }

              // Brief CA: stopped with approved regions left, and nothing explains it. Under an automatic mode nobody is there to type "Continue", so the same approval
              // carries on, through the report door. The bound is progress: see AutoFollowUp.ShouldContinueBuild.
              var notAttempted = AutoFollowUp.NotAttempted(approvedRegions, builtThisTurn);
              if (AutoFollowUp.ShouldContinueBuild(true, buildRecord.StoppedEarly, end, notAttempted.Count, builtThisTurn.Count, isBuildContinuation))
              {
                try
                {
                  AppendReport(wp, doc, threadId, System.Net.WebUtility.HtmlEncode(AutoFollowUp.ContinueLine(notAttempted)), AutoFollowUp.ContinueFact(notAttempted));
                  var stillGranted = (approvedScripts ?? new ApprovedScript[0]).Where(s => s != null && notAttempted.Contains(s.Region)).ToList();
                  RunTurn(wp, doc, threadId, TurnCapability.Build, notAttempted, stillGranted, autoApproved: true, isBuildContinuation: true);
                }
                catch (Exception ex) { TurnLog("build continuation threw: " + ex.Message); }
              }
            }

            // U97, Brief BT: AN ANNOUNCEMENT WITH NO CARD GETS ONE FOLLOW-UP TURN, which forces the card where the connection can. Bounded to one
            // per user message by _forcedTries, and a follow-up is never followed up. It counts against the thread's bounds as an ordinary turn.
            if (announcementFollowUpDue)
            {
              try
              {
                // U109: one append. The report door already draws the line and gives the model the fact; a second plugin append drew it twice.
                AppendReport(wp, doc, threadId, System.Net.WebUtility.HtmlEncode(AutoFollowUp.AnnouncementLine), AutoFollowUp.AnnouncementFact);
                RunTurn(wp, doc, threadId, TurnCapability.ReadOnly, isAnnouncementFollowUp: true);
              }
              catch (Exception ex) { TurnLog("announcement follow-up threw: " + ex.Message); }
            }

            // Brief AL: the automatic approval takes the SAME path a click takes - OnConsentClick redeems the token, records the approval
            // with the canvas stamp, appends the user line and routes by the kind - and it runs here, after Done, because it starts the
            // next turn and the session must be free for that.
            foreach (var a in autoToRun)
            {
              try { OnConsentClick(wp, doc, threadId, a.RawToken, a.Mode); }
              catch (Exception ex) { TurnLog("automatic approval threw: " + ex.Message); }
            }
          }
        });
      });
    }

    // ---- the mutation gate (WP-3 Stage B; ADR-075 as ruled at #220/#230) ----------------------------
    //
    // THE GATE IS THE TOOL SET, NOT A CLASSIFIER. A read-only turn holds no tool that writes, so intent
    // is irrelevant to what it CAN do. request_mutation is the model's only move here, and all it does is
    // put a question to the user; the CLICK is the gate, and the click starts a new turn at the granted
    // capability. The model can never widen its own capability.

    /// <summary>
    /// Live consent requests, by token - in memory only, and that is the mechanism (#220). The store and its
    /// rules live in <see cref="ConsentTokens"/>, GH-free, so they are tested; this panel only mints, redeems
    /// and logs.
    /// </summary>
    private static readonly ConsentTokens _consents = new ConsentTokens();

    /// <summary>Brief AL: how many changes in this run were approved on the plugin's own authority, per thread, in memory.</summary>
    private static readonly AutoApprovalCount _autoCount = new AutoApprovalCount();
    private static readonly ExhaustedReads _exhaustedReads = new ExhaustedReads();
    private static readonly ForcedTryBound _forcedTries = new ForcedTryBound();   // Brief BT
    private static readonly ScriptsOnFile _scriptsOnFile = new ScriptsOnFile();   // Brief CA

    /// <summary>Brief AL: a card the thread's mode approves automatically. Built beside the card that waits, because whether it is
    /// approved is only known when the turn completes - a turn that was stopped shows the card waiting, never marked as approved.</summary>
    private sealed class AutoPlan
    {
      public int CardIndex;
      public string Mode;
      public string RawToken;   // what the click would have posted: the token, plus every row for a batch card
      public string AutoHtml;
    }

    /// <summary>
    /// The lock for the per-thread maps below - the mutation log and the last canvas reading. Both are
    /// WRITTEN from the agent's worker thread and READ on the UI thread, exactly as the consent store is
    /// (which has its own lock).
    /// </summary>
    private static readonly object _consentLock = new object();

    /// <summary>Mint a single-use token for a card about to be shown. Logged, because the field needs to
    /// be able to pair a mint with the click that follows it (#137 - instrument, do not infer).</summary>
    private static string MintConsent(string threadId, string kind, HandoffSeed seed)
    {
      QueueSuperseded(threadId);
      var token = _consents.Mint(threadId, kind, seed);
      ConsentLog("minted " + Short(token) + " kind=" + kind + " thread=" + Short(threadId));
      return token;
    }

    // ---- Brief BG: an answered card becomes its receipt --------------------------------------------------------------------------

    /// <summary>The tokens a new card is retiring (U96), per chat, waiting for the UI thread to turn their cards into receipts.</summary>
    private static readonly Dictionary<string, List<string>> _supersededQueue = new Dictionary<string, List<string>>(StringComparer.Ordinal);

    /// <summary>Called at mint, on whichever thread mints: remembers the cards about to be retired. Applied on the UI thread when the new card
    /// is shown (<see cref="ApplySuperseded"/>), because the record and the panel are written there.</summary>
    private static void QueueSuperseded(string threadId)
    {
      try
      {
        var pending = _consents.PendingFor(threadId);
        if (pending.Count == 0) return;
        lock (_consentLock)
        {
          if (!_supersededQueue.TryGetValue(threadId ?? "", out var list)) _supersededQueue[threadId ?? ""] = list = new List<string>();
          list.AddRange(pending);
        }
      }
      catch { }
    }

    private static void ApplySuperseded(WebPanel wp, GH_Document doc, string threadId)
    {
      List<string> tokens;
      lock (_consentLock)
      {
        if (!_supersededQueue.TryGetValue(threadId ?? "", out tokens)) return;
        _supersededQueue.Remove(threadId ?? "");
      }
      foreach (var t in tokens) AnswerCard(wp, doc, threadId, t, CardReceipt.Superseded);
    }

    /// <summary>The card minted with <paramref name="token"/> becomes its receipt with <paramref name="state"/>: in the stored thread (so a
    /// reopened thread renders it the same) and in the panel, in place. A card already a receipt, or not in this thread, is left alone.</summary>
    private static void AnswerCard(WebPanel wp, GH_Document doc, string threadId, string token, string state)
    {
      try
      {
        var changed = CommandHistory.RewriteThreadPluginHtml(doc, threadId, h => CardReceipt.Answer(h, token, state));
        foreach (var html in changed) { try { wp?.SetCardAnswered(token, html); } catch { } }
      }
      catch { }
    }

    /// <summary>
    /// U18: mint the spot card's two tokens as SIBLINGS - one per option, each pre-bound to its own seed, and
    /// redeeming either voids the other (#877 (a)). Logged as a pair, so a field report can see that they
    /// belong to one card.
    /// </summary>
    private static void MintSpotConsent(string threadId, string kind, HandoffSeed seed,
                                        out string groupedToken, out string looseToken)
    {
      QueueSuperseded(threadId);
      _consents.MintSiblings(threadId, kind, seed.ForOption(grouped: true), seed.ForOption(grouped: false),
                             out groupedToken, out looseToken);
      ConsentLog("minted " + Short(groupedToken) + " (grouped) + " + Short(looseToken) + " (loose) kind=" + kind +
                 " spot=" + seed.Spot + " thread=" + Short(threadId));
    }

    /// <summary>
    /// #378b: what this thread has already had approved, and what the canvas looked like at the time. Per
    /// thread and in memory only - it is a loop-breaker, not a policy, so a reopened conversation starts
    /// with no memory of one and the user is never carrying a refusal across sessions.
    /// </summary>
    private static readonly Dictionary<string, MutationRequestLog> _mutationLog =
      new Dictionary<string, MutationRequestLog>(StringComparer.Ordinal);

    private static MutationRequestLog MutationLogFor(string threadId)
    {
      lock (_consentLock)
      {
        if (!_mutationLog.TryGetValue(threadId ?? "", out var log))
          _mutationLog[threadId ?? ""] = log = new MutationRequestLog();
        return log;
      }
    }

    /// <summary>
    /// "HAS ANYTHING ABOUT THIS CANVAS CHANGED?" - which is a different question from the behaviour
    /// fingerprint's "has anything that COMPUTES changed", and answering it needs both halves.
    ///
    /// Reorganise moves things and alters no behaviour whatsoever, so the fingerprint ALONE would report a
    /// perfectly successful Reorganise as having done nothing, and the loop-breaker would then refuse the
    /// next one. Pivots are deliberately absent from the fingerprint, and #353 already established that a
    /// move therefore needs its own instrument - this is that precedent applied a second time.
    ///
    /// Pivots are rounded to whole pixels so sub-pixel float noise can never read as a change.
    ///
    /// Returns null when it cannot be computed, and a null stamp DISARMS the breaker: a loop-breaker that
    /// fired on ignorance would refuse real work on no evidence (#208 - fail open on knowledge, never on
    /// identity).
    /// </summary>
    /// <summary>
    /// #573: the canvas as Dwight last LEFT it, per conversation, so the next turn can be told as a fact
    /// whether it has changed. In memory only - after a restart there is no record, and the next turn is
    /// told Dwight cannot tell, rather than being told nothing changed.
    /// </summary>
    private static readonly Dictionary<string, CanvasReading> _lastCanvas =
      new Dictionary<string, CanvasReading>(StringComparer.Ordinal);

    /// <summary>#624/#634: the last approved build per conversation, in memory only - after a restart it says
    /// nothing, and the canvas fact's "cannot tell" covers the conversation's stale claims.</summary>
    private static readonly LastBuildStore _lastBuild = new LastBuildStore();

    /// <summary>
    /// How long the ASK waits for the trial to call back. Deliberately LONGER than the trial's own 20s
    /// timeout, so the trial's honest, specific refusal ("the edited script did not finish solving in
    /// time") wins - this wait is the backstop for the trial never calling back AT ALL, which is a
    /// different fact and gets a different sentence.
    /// </summary>
    private const double EditTrialWaitSeconds = 35.0;

    /// <summary>
    /// Run the trial and WAIT for it, which is the one place a host tool blocks.
    ///
    /// ITS OWN DOCUMENTATION SAYS THE HOST TOOL NEVER WAITS, and that claim is now precise rather than
    /// absolute: it never waits for a HUMAN, which is unbounded and has no cancellation. This is a
    /// bounded, cancellable, machine-side check, and the session is busy for the turn's duration either
    /// way. A claim about our own behaviour left to contradict the code is a claim nobody re-reads (#315),
    /// so <see cref="HostTool"/>'s summary was amended in the same commit as this method.
    ///
    /// THE WAIT TAKES THE TURN'S CANCELLATION TOKEN. Not best-effort: a blocking wait that ignored Stop
    /// would breach ADR-089's true-cancellation contract, and that alone would make this shape
    /// unacceptable. A cancelled wait returns a refusal - never a card, and never a hang.
    ///
    /// AND THE TRIAL'S TIME COUNTS AGAINST THE TURN'S WALL CLOCK, which is CORRECT rather than a leak.
    /// The turn's budget is the user's patience, and a check Dwight chose to run is Dwight's time to
    /// spend. Exempting it would let a turn spend its whole budget on checks and then report an exhausted
    /// turn as though the model had been slow - so nobody should "fix" this by carving the trial out.
    ///
    /// The event is deliberately NOT disposed: the trial's callback may still arrive after a timeout, and
    /// setting a disposed event throws on the UI thread. An uncollected event is cheaper than that race.
    /// </summary>
    private static bool TrialEdit(GH_Document doc, Guid target, ScriptEditChange change,
                                  out ScriptTrialFacts facts, out ToolResult refusal)
    {
      facts = null;
      refusal = null;

      ScriptTrialFacts observed = null;
      var gate = new System.Threading.ManualResetEventSlim(false);

      // The trial schedules solutions, so it STARTS on the UI thread; this method is on the loop's worker.
      Ui(() => Dwight.Simplify.ScriptEditTrial.Run(doc, target, change.NewSource,
        change.AddInputs, change.AddOutputs, f => { observed = f; try { gate.Set(); } catch { } },
        change.AccessToSet.ToDictionary(kv => kv.Key, kv => PortAccess.ToScript(kv.Value), StringComparer.OrdinalIgnoreCase)));

      try
      {
        if (!gate.Wait(TimeSpan.FromSeconds(EditTrialWaitSeconds), DwightSession.CurrentToken))
        {
          refusal = ToolResult.Fail(ToolErrors.PreconditionRefused,
            "Dwight's check of that edit did not finish, so it has not shown the user anything and nothing " +
            "was changed. Tell them the check could not complete.");
          return false;
        }
      }
      catch (OperationCanceledException)
      {
        refusal = ToolResult.Fail(ToolErrors.PreconditionRefused,
          "Stopped. Dwight did not finish checking that edit, and nothing was changed.");
        return false;
      }

      facts = observed;
      if (facts == null)
      {
        refusal = ToolResult.Fail(ToolErrors.PreconditionRefused,
          "Dwight's check of that edit returned nothing, so it has not shown the user anything and nothing " +
          "was changed.");
        return false;
      }
      return true;
    }

    /// <summary>
    /// The refusal for an edit the trial did not clear - carrying the model's way forward, which is what
    /// makes it the repair loop rather than a no (ADR-092's shape).
    ///
    /// A COMPILE FAILURE GETS THE COMPILER'S OWN WORDS PLUS THE REFLECTED SIGNATURES of every type it
    /// named, which is the machinery that took compile-rate to 87.5%: a hallucinated type reflects to
    /// nothing, and being told it does not exist in this Rhino's RhinoCommon is the correction it needs.
    ///
    /// A FAILED PRESERVES gets the defect CLASS and the port NAMES and no values - the feedback-precision
    /// law (#71), which is why this reads the ports out of the facts rather than composing anything from
    /// the compared data.
    ///
    /// U20 3h: THIS PANEL COMPOSES NONE OF IT. The body is ScriptEditVerdict.RefusalBody's, GH-free and tested
    /// there - including which header a failure gets, chosen from the edit's language, which is derived from the
    /// component's TYPE. It used to be chosen here from the display label, which named no language, so every C#
    /// compile failure was told "The interpreter said:". The panel passes only what it alone can reach: the
    /// reflection over the loaded RhinoCommon.
    /// </summary>
    private static ToolResult EditRefusal(ScriptEditChange change, ScriptEditOutcome outcome,
                                          ScriptTrialFacts facts, int attempt) =>
      ToolResult.Fail(ToolErrors.PreconditionRefused,
        ScriptEditVerdict.RefusalBody(change, outcome, facts, attempt, DwightConfig.ToScriptMaxAttempts,
          errs => Dwight.Simplify.ToScriptCompileGate.ReflectedApiFor(new List<string>(errs))));

    private static CanvasReading ReadCanvas(GH_Document doc)
    {
      if (doc == null) return null;
      var reading = new CanvasReading { Stamp = CanvasStamp(doc) };
      try
      {
        var regions = GhScaffoldMap.CanvasRegions(doc);
        reading.Regions = regions.Select(r => r.Address).ToList();
        // #660: the per-region component counts, so a region emptied or filled without its group changing is
        // stated where it happened. Set only here, so a reading whose regions could not be read has no counts.
        reading.Counts = CanvasReading.CountsByAddress(
          regions.Select(r => new KeyValuePair<string, int>(r.Address, r.ComponentCount)));
      }
      catch { }
      return reading;
    }

    /// <summary>
    /// Record the canvas as Dwight is leaving it. Called at TWO sites, and the second is the one that is
    /// easy to miss: when a turn completes, AND when the post-build tidy completes. The tidy is scheduled
    /// asynchronously and finishes after the turn, so a reading taken only at turn end would come before
    /// Dwight's own layout moves - and the next turn would be told "the canvas changed" about Dwight's own
    /// tidy. An advisory line that is often wrong destroys the credibility of every line beside it.
    /// </summary>
    private static void RememberCanvas(GH_Document doc, string threadId)
    {
      if (doc == null || string.IsNullOrEmpty(threadId)) return;
      try
      {
        var reading = ReadCanvas(doc);
        lock (_consentLock) _lastCanvas[threadId] = reading;
      }
      catch { }
    }

    private static CanvasReading LastCanvas(string threadId)
    {
      lock (_consentLock)
        return threadId != null && _lastCanvas.TryGetValue(threadId, out var r) ? r : null;
    }

    private static string CanvasStamp(GH_Document doc)
    {
      if (doc == null) return null;
      try
      {
        var parts = new List<CanvasStampPart>();
        foreach (var o in doc.Objects.Where(o => o != null))
        {
          var part = new CanvasStampPart { Guid = o.InstanceGuid };
          try { part.X = (int)Math.Round(o.Attributes.Pivot.X); part.Y = (int)Math.Round(o.Attributes.Pivot.Y); }
          catch { part.X = null; part.Y = null; }
          // #680: the whole held state, read here - the fingerprint below is value-only for a slider, by design.
          try { part.State = StateToken.Of(GhBuildState.Read(o)); } catch { part.State = null; }
          parts.Add(part);
        }

        return CanvasStampCore.Compose(BehaviourFingerprint.Of(doc), parts);
      }
      catch { return null; }
    }

    /// <summary>
    /// Redeem a token, or say precisely WHY it cannot be redeemed. Three causes, three answers: one
    /// message for all of them is what made the field report unactionable.
    /// </summary>
    private static bool TakeConsent(string token, string threadId, out string kind, out HandoffSeed seed, out string reason)
    {
      var taken = _consents.Take(token, threadId, out kind, out seed, out reason);
      ConsentLog("click " + Short(token) + " thread=" + Short(threadId) +
                 (taken ? " -> APPROVED kind=" + kind : " -> REFUSED (" + reason + ")") +
                 "  [pending now: " + _consents.Summary() + "]");
      return taken;
    }

    /// <summary>#255: every mint and every click on the command line, so a field report can name the
    /// cause instead of describing the symptom.</summary>
    private static void ConsentLog(string line)
    {
      try { Rhino.RhinoApp.WriteLine("Dwight consent: " + line); } catch { }
    }

    /// <summary>The registry's view of an id, for this thread, synced against the live document first -
    /// aliveness is never stored (#245), so it is read fresh every time it is asked for.</summary>
    private static Func<int, IdState> IdStateFor(string threadId, GH_Document doc)
    {
      var scope = ScopeFor(threadId, doc);
      try { scope.Ids.Sync(LiveGuids(doc)); } catch { }
      return id => scope.Ids.State(id);
    }

    /// <summary>#287(b): the seed's surviving ids as guids, re-grounded one more time against the document
    /// itself. An id that resolves to nothing contributes nothing - there is no fallback.</summary>
    private static List<Guid> SeedGuids(SeedPlan plan, string threadId, GH_Document doc)
    {
      var guids = new List<Guid>();
      try
      {
        var scope = ScopeFor(threadId, doc);
        foreach (var id in plan.PreFillIds)
          if (scope.Ids.TryGetGuid(id, out var g) && doc?.FindObject(g, false) != null) guids.Add(g);
      }
      catch { }
      return guids;
    }

    /// <summary>Say what the verb is about to open ON, and what it will then do - composed GH-free in
    /// <see cref="ConsentCard.OpeningNotice"/>, in the same words the card used (#890: this line once told a
    /// Reorganise user it would preview first, and it does not).</summary>
    private static string OpeningNotice(string verb, List<Guid> onto, string mode) =>
      ConsentCard.OpeningNotice(verb, onto?.Count ?? 0, mode);

    /// <summary>U28: the closure a reorganise/simplify click hands its verb, so a refused approved scope is said in THIS
    /// thread through the report door - on screen, and in the transcript the model reads next turn. A CLOSURE over the
    /// live thread, never a static re-entry (#344): every re-entry of the verb's flow carries it.</summary>
    private static Action<string> ScopeRefusedReport(WebPanel wp, GH_Document doc, string threadId) =>
      message => AppendReport(wp, doc, threadId, ScriptEditReport.NotAppliedHtml(message), ScriptEditReport.NotAppliedText(message));

    private static string Short(string s) =>
      string.IsNullOrEmpty(s) ? "(none)" : (s.Length <= 8 ? s : s.Substring(0, 8));

    /// <summary>DEBUG dev-menu read of the live consent state - the direct instrument for #255.</summary>
    internal static string ConsentDiagnostics() =>
      "pending consents: " + _consents.Summary() + "\nconsumed this session: " + _consents.SpentCount;

    /// <summary>
    /// The host handler for <c>request_mutation</c> (#230). Validates, mints a single-use token, and
    /// schedules the card - then returns. IT NEVER WAITS FOR THE HUMAN: a tool that blocked on a click
    /// would hold the session busy for as long as the user took to read it, and the answer arrives as a
    /// later turn instead.
    ///
    /// IT MAY WAIT FOR A BOUNDED, CANCELLABLE CHECK, and that distinction is exact rather than a
    /// loosening (amended at U20; the sentence above said "returns IMMEDIATELY", which the edit_script
    /// branch made untrue - and a claim about our own behaviour left to contradict the code is a claim
    /// nobody re-reads, #315). <c>edit_script</c> runs its trial here because the card's entire premise is
    /// stating what Dwight ACTUALLY checked, and because the user must never see a preview of code that
    /// does not compile (RR20 #51). The wait carries the turn's cancellation token, is bounded by the
    /// trial's own timeout, and refuses rather than showing a card if it does not complete - see
    /// <see cref="TrialEdit"/>. A build with no map (U18) waits the same way, for its declaration check to
    /// run on the UI thread - see <see cref="PrepareSpot"/>. What is forbidden is waiting on a HUMAN, which
    /// is unbounded and uncancellable; a machine-side check inside a turn that already holds the session is
    /// not that.
    ///
    /// It is handed no <see cref="AgentScope"/> by the loop, so it has no path to the canvas.
    /// </summary>
    private static string HostTool(GH_Document doc, string threadId, List<string> pendingCards,
                                   List<AutoPlan> pendingAuto, string approvalMode, bool fullControl, bool firstRunScriptCard,
                                   EditAttempts editAttemptsThisTurn, string approvedKind,
                                   IReadOnlyList<string> approvedRegions, string name, string argsJson)
    {
      if (!string.Equals(name, AgentHostTools.RequestMutation, StringComparison.OrdinalIgnoreCase))
        return ToolResult.Fail(ToolErrors.UnknownTool, "No host tool named \"" + name + "\".").ToJson();

      var args = AgentToolCore.ParseArgs(argsJson, out var argError);
      if (argError != null) return argError.ToJson();

      // U90: THE RUN_SCRIPT ASK. Only on a conversation whose Full control is on; with it off the kind is not in the list and falls through to the
      // parse below, which refuses it by name with the real list. Whether the card asks is ApprovalMode.Decide's: it runs without asking in one cell.
      if (fullControl && string.Equals(StrOf(args, "kind"), Dwight.Agent.FullControl.Kind, StringComparison.Ordinal))
      {
        var runWhy = StrOf(args, "why");
        if (!Dwight.Agent.FullControl.TryParseAsk(args, runWhy, out var runGrant, out var runError)) return runError.ToJson();
        // Brief CA: this script's card is already waiting: no new card, no new token, and the turn ends.
        if (Dwight.Agent.FullControl.SameScript(_consents.WaitingSeed(threadId, Dwight.Agent.FullControl.Kind)?.Source, runGrant.Source))
          return Dwight.Agent.FullControl.StillWaitingResult();
        var runSeed = new HandoffSeed { Source = runGrant.Source, Why = runGrant.Purpose, RunTimeoutSeconds = runGrant.TimeoutSeconds };
        var runToken = MintConsent(threadId, Dwight.Agent.FullControl.Kind, runSeed);
        pendingCards.Add(ConsentCard.RunScriptHtml(runWhy, runToken, runGrant.Source,
                                                   ConsentCard.ExpandedByDefaultFor(Dwight.Agent.FullControl.Kind, firstRunScriptCard)));
        // Brief CD: the one cell. An automatic approval takes the path every automatic card takes: the turn ends, the marked receipt is drawn with the source folded
        // inside it, and OnConsentClick runs the grant and reports through the report door.
        if (ApprovalMode.Decide(approvalMode, Dwight.Agent.FullControl.Kind, ApprovalMode.FactsFor(Dwight.Agent.FullControl.Kind, 1, 0, fullControl)) == ApprovalDecision.Auto)
          pendingAuto.Add(new AutoPlan
          {
            CardIndex = pendingCards.Count - 1, Mode = ApprovalMode.Normalise(approvalMode), RawToken = runToken,
            AutoHtml = ConsentCard.RunScriptHtml(runWhy, runToken, runGrant.Source, false, ApprovalMode.Normalise(approvalMode)),
          });
        Ui(() => CommandHistory.MarkRunScriptCardShown(doc, threadId));   // this thread has now shown one: the next opens folded
        return CardShownResult(Dwight.Agent.FullControl.Kind);
      }

      // #378b: the loop-breaker is inside the parse, so a repeat ask is refused before a card can exist.
      // #611d: so is a request for the approval this turn is already carrying out - with a pointer at its tool.
      // #617: and a request for regions OUTSIDE this turn's approval is told the truth about them.
      // #626: and a request whose regions have spent their attempts in this turn is put to the user as a card.
      // The ledger is the document's, whose turn permission is this turn's (BeginTurn ran when the loop started);
      // build_region counts an attempt before its verdict returns, and the loop waits for that result before the
      // model's next step, so the count read here is current.
      var ledger = AgentScope.LedgerForDocument(doc);
      Func<string, bool> exhausted = ledger == null ? (Func<string, bool>)null
        : r => ledger.Attempts(r) >= DwightConfig.AgentBuildMaxAttemptsPerRegion;
      if (!AgentHostTools.TryParseRequest(args, MutationLogFor(threadId), CanvasStamp(doc), approvedKind, approvedRegions,
                                          exhausted, out var kind, out var why, out var seed, out var error))
        return error.ToJson();   // an unavailable kind is refused BY NAME with the real list, never a dead card

      // U28 (#918, #939): NO CARD OVER NOTHING, FOR REORGANISE AND SIMPLIFY - decided HERE, before a card exists, by
      // lane B's GH-free rule (VerbScope.ResolveAsk). Named components resolve to the live ones or are refused per id
      // (never widened to the selection, #208); named none resolves the user's selection AT THE ASK, read on the UI
      // thread; named none with nothing selected is refused to the model by name. The resolved ids are written back
      // to the seed, so the card states - and the click runs on - the scope resolved here, not whatever is selected
      // when the user clicks.
      string verbScopeLine = null;
      if (VerbScope.IsComponentScoped(kind))
      {
        // The selection is read only when it can matter: a request that named components never falls back to it.
        var selectedIds = new List<int>();
        if (seed.ComponentIds.Count == 0 && !SelectedIdsAtAsk(doc, threadId, out selectedIds, out var selectionError))
          return selectionError.ToJson();
        var askRefusal = VerbScope.ResolveAsk(kind, seed.ComponentIds, IdStateFor(threadId, doc), selectedIds,
                                              out var askScope);
        if (askRefusal != null) return askRefusal.ToJson();   // refused by name, never a card over nothing
        seed.ComponentIds = askScope.Ids.ToList();
        verbScopeLine = VerbScope.CardScopeLine(askScope);
      }

      // STAGE C: the build card's SCOPE is validated HERE, before the card exists (#338). The model
      // NOMINATES addresses; the plugin checks each against the canvas and keeps only what is really
      // there, so the sentence the user approves can never promise a region that does not exist - an
      // affordance is an assertion that its target exists (#321), and a consent card is the strongest
      // affordance in the product. A build whose nominations all fail is refused rather than shown as an
      // empty promise.
      // Phase-9 U4: a set_value ask is VALIDATED HERE, before the card exists, against the live target -
      // the type, what it holds now, stream A's parser, and the measured restore rule - so the sentence the
      // user approves names the exact change and cannot promise one the plugin would refuse. The reads are
      // the same class LiveRegions makes on this thread (plain property reads off the document).
      SetValueChange setValue = null;
      if (ConsentCard.Route(kind) == ConsentRoute.SetValue)
      {
        var scope = ScopeFor(threadId, doc);
        if (!SetValueAsk.Validate(seed, IdStateFor(threadId, doc), id => GhSetValue.ReadLive(doc, scope.Ids, id),
                                  out setValue, out _, out var askError))
          return askError.ToJson();   // refused by name, never a dead card
        seed.TargetType = setValue.Type;   // #683 (6b): the type the card names, from the live read, for the click's scope rule
      }

      // ---- Phase-9 U20: edit_script is VALIDATED and then TRIALLED, both before the card exists --------
      //
      // THE TRIAL MUST PRECEDE THE CARD, for two independent reasons. RR20 #51: the user never sees a
      // preview of code that does not compile. And this card's whole premise is that it states what Dwight
      // ACTUALLY CHECKED - a card promising a future check would make the vacuous-check outcome
      // meaningless. So a set_value-style "validate at the ask, verify at the click" shape is not available
      // to this kind.
      //
      // AND THE CANVAS STAMP IS ALREADY TAKEN, above, inside TryParseRequest - BEFORE this point, which is a
      // CORRECTNESS property rather than tidiness. The stamp is the loop-breaker's input: one taken while
      // the trial's copy is on the canvas would include Dwight's own instrument, read as "the canvas
      // changed", and CLEAR the breaker - so a looping model would get to ask again indefinitely because our
      // measurement moved the thing the breaker measures. Taking it AFTER the trial would also be
      // net-correct and is the FRAGILE choice: it depends on the cleanup having worked, and silently bakes
      // the copy into the breaker's baseline if it did not.
      ScriptEditChange editScript = null;
      var editOutcome = ScriptEditOutcome.CouldNotCheck;
      ScriptTrialFacts editFacts = null;
      if (ConsentCard.Route(kind) == ConsentRoute.EditScript)
      {
        var editScope = ScopeFor(threadId, doc);
        if (!EditScriptAsk.Validate(seed, IdStateFor(threadId, doc), id => GhEditScript.ReadLive(doc, editScope.Ids, id),
                                    out editScript, out var editAskError))
          return editAskError.ToJson();   // refused by name, never a dead card

        // THE PER-TARGET BUDGET, checked BEFORE another trial is run on a target that has had its tries.
        // Terminal when spent AND SAYING SO, so the model does not spend the loop-breaker discovering it.
        var spent = editAttemptsThisTurn.Spent(editScript.TargetId);
        if (spent >= DwightConfig.ToScriptMaxAttempts)
          return ToolResult.Fail(ToolErrors.PreconditionRefused,
            "You have used all " + DwightConfig.ToScriptMaxAttempts + " attempts at editing " + editScript.Label +
            " this turn, so Dwight will not check another version of it now. Tell the user what you were trying " +
            "to change and what went wrong - they can ask again.").ToJson();

        if (!editScope.Ids.TryGetGuid(editScript.TargetId, out var editTarget))
          return ToolResult.Fail(ToolErrors.RetiredId,
            "Component " + editScript.TargetId + " is not on the canvas.").ToJson();

        // U20 3c: every trial's result is RECORDED against its target, so the turn's end can say in Dwight's
        // own words what became of an edit that got no card. A trial with no outcome (it timed out, or came
        // back empty) is recorded as exactly that - Dwight could not check it.
        if (!TrialEdit(doc, editTarget, editScript, out editFacts, out var trialRefusal))
        {
          editAttemptsThisTurn.Unchecked(editScript.TargetId, editScript.Label);
          return trialRefusal.ToJson();
        }

        editOutcome = ScriptEditVerdict.Outcome(editScript.Claim, editFacts);
        editAttemptsThisTurn.Trialled(editScript.TargetId, editScript.Label, editOutcome, editFacts);
        if (!ScriptEditVerdict.MayOfferCard(editOutcome))
          return EditRefusal(editScript, editOutcome, editFacts, editAttemptsThisTurn.Spent(editScript.TargetId)).ToJson();

        // THE BASE THE CARD DIFFED FROM, written by the PLUGIN for the click's compare-and-set. Without it
        // the click would re-read the source live and compare it against itself, which always agrees - and
        // the one check whose purpose is to notice that the user typed into the component while the card
        // was up would silently never fire.
        seed.BaseSource = editScript.BaseSource;

        // The trial's outcome travels too, so the report the user reads after applying is the one the card
        // was drawn from - recomputing it at the click could disagree with what they approved. It gets its
        // OWN field rather than riding Mode: Mode is validated against the kind's action vocabulary and an
        // outcome name would simply be discarded, and borrowing a field for a question it was not asked is
        // #680's defect exactly.
        seed.Outcome = editOutcome.ToString();
        // U37 (#930, #944): the report NAMES the component it applied to, in set_value's shape ("F1 area" (11)).
        seed.Report = ScriptEditVerdict.WithAccess(ScriptEditVerdict.ReportLine(editOutcome, editFacts, ScriptEditVerdict.Subject(editScript)), editScript);
        seed.TrialNote = ScriptEditVerdict.ModelFact(editOutcome, editFacts);
      }

      // ---- Phase-9 U6 + U21 (#760): edit is VALIDATED, every row, before the card exists -----------------
      //
      // EVERY ROW resolves to exactly one existing target now, or the ask is refused with the failing rows named - the
      // bound on how many rows the card SHOWS never loosens what is CHECKED. The plugin's resolved record overwrites the
      // model's nomination on the seed, so the click re-validates what the card showed (the Place precedent, #862). It
      // runs on the UI thread: it reads the document.
      EditPlan editPlan = null;
      if (ConsentCard.Route(kind) == ConsentRoute.Edit)
      {
        if (!PrepareEdit(doc, threadId, seed, out editPlan, out var editOpsError))
          return editOpsError.ToJson();   // refused by name, never a dead card
        seed.Ops = editPlan.Record;
      }

      IReadOnlyList<string> approved = null;
      List<ApprovedScript> cardScripts = null;   // U65: the scripts a region build card shows whole
      SpotAsk spot = null;
      if (ConsentCard.Route(kind) == ConsentRoute.Build)
      {
        // U18: WHICH PATH, decided on what the model NAMED and never on what the canvas has - so a region
        // that is not there is refused by name below, never quietly turned into a spot. Only an ask naming no
        // region at all reaches the mint.
        var routing = SpotAsk.RouteFor(seed);
        if (routing.Refusal != null) return routing.Refusal.ToJson();
        if (routing.Route == BuildAskRoute.Spot)
        {
          if (seed?.Scripts != null && seed.Scripts.Count > 0)
            return ToolResult.Fail(ToolErrors.InvalidArgs,
              "\"scripts\" belongs to a build into a map region. A placement without a map carries a script's source on the " +
              "component in \"place\" itself, and its card shows it there.").ToJson();
          if (!PrepareSpot(doc, threadId, seed, out spot, out var spotError))
            return spotError.ToJson();   // refused by name, never a dead card
        }
        else
        {
          approved = LiveRegions(doc, seed?.Regions);
          if (approved.Count == 0)
            return ToolResult.Fail(ToolErrors.UnknownRegion,
              "None of those regions are on the canvas, so there is nothing to ask the user to approve. " +
              "Call get_scaffold_map and name regions that exist.",
              new JsonObject
              {
                ["asked"] = new JsonArray((seed?.Regions ?? new List<string>()).Select(r => (JsonNode)(r ?? "")).ToArray()),
                ["available"] = new JsonArray(LiveRegions(doc, null).Select(r => (JsonNode)r).ToArray()),
              }).ToJson();
          seed.Regions = approved.ToList();   // the card and the grant carry the VALIDATED set, not the ask

          // U65: THE SCRIPTS THE CARD SHOWS WHOLE. Parsed by the build's own parser against the VALIDATED regions, and the seed
          // is overwritten with the resolved record - so the text on the card, the text the click grants and the text the
          // build compares against are one text (#153).
          if (!ScriptsAsk.Resolve(seed.Scripts, approved, out var resolvedScripts, out var scriptsError))
            return scriptsError.ToJson();
          // Brief CA: a card that sends none shows again what an earlier card showed and no build has landed. Every word of it is on this card too.
          if (resolvedScripts.Count == 0) resolvedScripts = _scriptsOnFile.For(threadId, approved);
          _scriptsOnFile.Remember(threadId, resolvedScripts);
          cardScripts = resolvedScripts;
          seed.Scripts = resolvedScripts.Count == 0 ? null : ScriptsAsk.Record(resolvedScripts);
        }
      }

      // U18: A SPOT CARD HAS TWO BUTTONS, AND EACH BUTTON ITS OWN TOKEN - minted as siblings, so redeeming
      // one voids the other and the same placement cannot run twice (#877 (a)). The card names every
      // component and the reserved address, from the RECORD PrepareSpot wrote onto the seed.
      // ---- Brief AL: THE ONE DECISION, from the card's own record -----------------------------------------------------
      //
      // ApprovalMode.Decide reads the thread's mode, the card's kind, and facts the PLUGIN computed from the card (the scripts the
      // card carries, the deletes in the plan). Nothing the model wrote reaches it. When it says Auto the card is still built and
      // still shown - the waiting variant here, the marked variant beside it - and the turn decides at its END which to append.
      var scriptCount = spot?.Placement?.Scripts?.Count ?? cardScripts?.Count ?? 0;
      var autoMode = ApprovalMode.Decide(approvalMode, kind, ApprovalMode.FactsFor(kind, scriptCount, editPlan?.Deletes ?? 0))
                     == ApprovalDecision.Auto ? ApprovalMode.Normalise(approvalMode) : null;
      void Raise(string waiting, Func<string> marked, string rawToken)
      {
        pendingCards.Add(waiting);
        if (autoMode != null)
          pendingAuto.Add(new AutoPlan { CardIndex = pendingCards.Count - 1, Mode = autoMode, RawToken = rawToken, AutoHtml = marked() });
      }

      if (spot != null)
      {
        MintSpotConsent(threadId, kind, seed, out var groupedToken, out var looseToken);
        // An automatic approval takes the option the card offers as its primary one: the placement in a group.
        Raise(ConsentCard.SpotHtml(why, spot.Placement, groupedToken, looseToken),
              () => ConsentCard.SpotHtml(why, spot.Placement, groupedToken, looseToken, autoMode), groupedToken);
        return CardShownResult(kind);
      }

      var token = MintConsent(threadId, kind, seed);

      // D7: the card is HELD, not appended here. Anthropic returns text and tool_use in ONE assistant
      // message, so a model that explains and then asks has already produced them in the right order -
      // but the card was appended at DISPATCH time while the turn's prose is appended at COMPLETION, so
      // the card rendered first no matter what the model did. NO PROMPT COULD EVER HAVE FIXED THAT.
      //
      // The card's position in the transcript is a RENDERING decision, so the plugin owns it (ADR-056):
      // the turn appends its text, then its cards, unconditionally. Deterministic, free, and no longer
      // something the model can get wrong.
      // #332: the card names WHICH variant is being approved, not only which verb. The mode is validated
      // here by the same function SeedPlan uses - a pure string check, safe on this thread because it
      // touches no GH state - so an unrecognised suggestion simply goes unnamed rather than unchecked.
      // Phase-9 U4: a set_value card names the validated change and nothing else - it removes nothing, so the rebuild
      // preview below is not its business.
      if (setValue != null)
        Raise(ConsentCard.Html(kind, why, token, setValue), () => ConsentCard.Html(kind, why, token, setValue, autoMode), token);
      // Phase-9 U20: the diff, the COMPLETE source, and a check clause describing what was actually
      // compared. It removes nothing, so the rebuild preview below is not its business either.
      //
      // U20 3c DEPENDS ON THIS LINE FOLLOWING THE TRIAL WITH NOTHING BETWEEN THAT CAN REFUSE. EditAttempts marks
      // the target as HAVING A CARD when the trial is recorded (Trialled, above), before this card is minted -
      // so a refusal added anywhere between the two would leave a target with no card AND no turn-end line,
      // silently. Keep the marking in Trialled, where a test reaches it; if a refusal must go between, it has
      // to record the target as refused first.
      else if (editScript != null)
        Raise(ConsentCard.EditScriptHtml(why, token, editScript, editOutcome, editFacts),
              () => ConsentCard.EditScriptHtml(why, token, editScript, editOutcome, editFacts, autoMode), token);
      // Phase-9 U6 + U21: the batch card - tickable rows, the total in the authority line, the orphans named per delete row.
      else if (editPlan != null)
        // The batch card's click posts the token and the rows left ticked; every row starts ticked, so an automatic approval posts them all.
        Raise(ConsentCard.EditHtml(why, token, editPlan), () => ConsentCard.EditHtml(why, token, editPlan, autoMode),
              token + ":" + string.Join(",", editPlan.Steps.Select(s => (s.Row - 1).ToString(System.Globalization.CultureInfo.InvariantCulture))));
      else
      {
        // F1(b): WHAT A REBUILD WOULD REMOVE, named on the card the user is about to approve.
        //
        // This is the safety property, not a courtesy. The delete-claim now outlives the turn that made
        // it, and what replaces the old turn-scoping is that the user reads the scope AT THE MOMENT OF
        // CONSENT. It is resolved through the same BuildLedger.Resolve the build itself will run, against
        // the canvas as it stands right now, so the card cannot promise a scope different from the one
        // that executes.
        var removal = RebuildPreview.Sentence(
          GhBuildRegion.PreviewRebuild(doc, AgentScope.LedgerForDocument(doc), ScopeFor(threadId, doc).Ids, approved));

        // U28: a reorganise/simplify card also states the scope resolved at the ask, with its count (null otherwise).
        var buildStyle = BuildStyle.Normalise(DwightSettings.Current.BuildStyle);
        Raise(ConsentCard.Html(kind, why, token, SeedPlan.ValidMode(kind, seed?.Mode), approved, removal,
                               verbScopeLine, buildStyle, cardScripts),
              () => ConsentCard.Html(kind, why, token, SeedPlan.ValidMode(kind, seed?.Mode), approved, removal,
                                     verbScopeLine, buildStyle, cardScripts, autoMode), token);
      }

      return CardShownResult(kind);
    }

    /// <summary>
    /// #256: TERMINAL AND SELF-DESCRIBING. The old result said "the user has been asked" and left the model
    /// to infer the rest; given no state it could act on, it called this tool four more times and then told
    /// the user to look for a Grasshopper dialog that has never existed. Every field here exists to close one
    /// of the questions it was left guessing.
    ///
    /// ONE DEFINITION for every card, the spot card included (U18): the loop ends a turn on <c>cardShown</c>,
    /// read off this envelope, so a card path that built its own could show a card without ending the turn.
    /// </summary>
    private static string CardShownResult(string kind) => AgentHostTools.CardShownResult(kind);

    /// <summary>
    /// How long the ASK waits for the spot check to run on the UI thread (U18).
    ///
    /// The check is a parse - no solve, no watchdog of its own - so it takes milliseconds when it runs at all.
    /// This wait is only the backstop for a UI thread that is busy (a long solve) or never gets to it. Upstream,
    /// Stop cancels it at once and the turn's own wall clock still applies; the edit trial's wait in this same
    /// ask is 35s. So: long enough to outlast an ordinary solve, short enough that the model hears about it
    /// inside the turn and can tell the user.
    /// </summary>
    private const double SpotAskWaitSeconds = 30.0;

    /// <summary>
    /// #878: every text the saved conversation holds for this DOCUMENT - every thread, both what was shown
    /// (Html, which is all a card is stored as) and the source beside it - for <see cref="SpotMint.MentionedIn"/>.
    /// Read-only. A store that cannot be read degrades to EMPTY (WP-1's rule) and this then returns nothing,
    /// which is safe with no guard: the conversation that could be misread is then not shown either.
    ///
    /// UI THREAD ONLY: CommandHistory has no lock and ThreadMessages returns the LIVE message list, which the
    /// append doors write on the UI thread. Called from inside PrepareSpot's Ui(...) block for exactly that
    /// reason - moving the call out of it would race those appends on an unlocked list.
    /// </summary>
    /// <summary>Brief CM: what the user last wrote, in the thread that is asking. A click's echo is not their words.</summary>
    private static string LastUserText(IReadOnlyList<StoredMessage> messages) =>
      messages?.LastOrDefault(m => m?.Who == "me" && m.Src != ApprovalMode.ClickUserText && m.Src != ApprovalMode.AutoUserText)?.Src;

    private static List<string> SavedTexts(GH_Document doc)
    {
      var texts = new List<string>();
      try
      {
        foreach (var id in CommandHistory.ThreadIds(doc))
          foreach (var m in CommandHistory.ThreadMessages(doc, id))
          {
            if (!string.IsNullOrEmpty(m?.Html)) texts.Add(m.Html);
            if (!string.IsNullOrEmpty(m?.Src)) texts.Add(m.Src);
          }
      }
      catch { }   // what was read so far still counts; reserving less than everything is the store's own degrade
      return texts;
    }

    /// <summary>
    /// U18: the spot half of a build ask - validate, mint, and write the record onto the seed
    /// (<see cref="SpotAsk.Prepare"/>) - run ON THE UI THREAD.
    ///
    /// WHY THE UI THREAD. This method is on the loop's worker, like the rest of the host tool, but resolving a
    /// declaration CONSTRUCTS a component of each named type to read its ports. build_region only ever did that
    /// on the UI thread (the loop marshals every canvas tool there), and a Grasshopper object's constructor is
    /// not ours to vouch for off it. So the ask resolves where the build resolves - the same code, on the same
    /// thread.
    /// </summary>
    private static bool PrepareSpot(GH_Document doc, string threadId, HandoffSeed seed,
                                    out SpotAsk spot, out ToolResult error)
    {
      spot = null;
      error = null;
      if (doc == null)
      {
        error = ToolResult.Fail(ToolErrors.NoDocument, "No Grasshopper document is open.");
        return false;
      }

      SpotAsk prepared = null;
      ToolResult refused = null;
      var ok = false;
      var gate = new System.Threading.ManualResetEventSlim(false);

      Ui(() =>
      {
        try
        {
          var scope = ScopeFor(threadId, doc);
          try { scope.Ids.Sync(LiveGuids(doc)); } catch { }
          // #878: in use = the canvas's spot names AND every spot the saved conversation mentions, so a restart
          // (which empties the mint's reservations) cannot re-mint a number the conversation already means.
          var resolvers = GhBuildRegion.LiveResolvers(doc, scope.Ids);
          resolvers.LastUserText = () => LastUserText(CommandHistory.ThreadMessages(doc, threadId));
          ok = SpotAsk.Prepare(seed, resolvers, GhBuildRegion.LabelOfId(doc, scope.Ids),
                               SpotMint.For(doc.DocumentID), SpotMint.InUse(LiveRegions(doc, null), SavedTexts(doc)),
                               out prepared, out refused);
        }
        catch (Exception ex)
        {
          refused = ToolResult.Fail(ToolErrors.PreconditionRefused,
            "Dwight could not check that placement (" + ex.Message + "), so it will not offer it.");
        }
        finally { try { gate.Set(); } catch { } }
      });

      try
      {
        if (!gate.Wait(TimeSpan.FromSeconds(SpotAskWaitSeconds), DwightSession.CurrentToken))
        {
          error = ToolResult.Fail(ToolErrors.PreconditionRefused,
            "Dwight's check of that placement did not finish, so it has not shown the user anything and nothing " +
            "was changed. Tell them the check could not complete - Grasshopper may be busy solving.");
          return false;
        }
      }
      catch (OperationCanceledException)
      {
        error = ToolResult.Fail(ToolErrors.PreconditionRefused,
          "Stopped. Dwight did not finish checking that placement, and nothing was changed.");
        return false;
      }

      if (!ok)
      {
        error = refused ?? ToolResult.Fail(ToolErrors.PreconditionRefused,
          "Dwight could not check that placement, so it will not offer it.");
        return false;
      }
      spot = prepared;
      if (seed != null) seed.SpotDone = ConsentCard.SpotDone(prepared?.Placement);   // Brief BQ: History's sentence, from what the card will run
      return true;
    }

    /// <summary>
    /// Phase-9 U6 + U21: the edit half of the ask - read the live canvas, validate EVERY row, build the plan the card is
    /// drawn from - run ON THE UI THREAD, with the bounded, cancellable wait <see cref="PrepareSpot"/> uses (this handler
    /// runs on the turn's worker, and the canvas is read where the canvas lives).
    /// </summary>
    private static bool PrepareEdit(GH_Document doc, string threadId, HandoffSeed seed,
                                    out EditPlan plan, out ToolResult error)
    {
      plan = null;
      error = null;
      if (doc == null)
      {
        error = ToolResult.Fail(ToolErrors.NoDocument, "No Grasshopper document is open.");
        return false;
      }

      EditPlan prepared = null;
      ToolResult refused = null;
      var ok = false;
      var gate = new System.Threading.ManualResetEventSlim(false);

      Ui(() =>
      {
        try
        {
          ok = GhEditOps.Ask(doc, ScopeFor(threadId, doc).Ids, seed?.Ops, out prepared, out refused);
        }
        catch (Exception ex)
        {
          refused = ToolResult.Fail(ToolErrors.PreconditionRefused,
            "Dwight could not check those changes (" + ex.Message + "), so it will not offer them.");
        }
        finally { try { gate.Set(); } catch { } }
      });

      try
      {
        if (!gate.Wait(TimeSpan.FromSeconds(SpotAskWaitSeconds), DwightSession.CurrentToken))
        {
          error = ToolResult.Fail(ToolErrors.PreconditionRefused,
            "Dwight's check of those changes did not finish, so it has not shown the user anything and nothing " +
            "was changed. Tell them the check could not complete - Grasshopper may be busy solving.");
          return false;
        }
      }
      catch (OperationCanceledException)
      {
        error = ToolResult.Fail(ToolErrors.PreconditionRefused,
          "Stopped. Dwight did not finish checking those changes, and nothing was changed.");
        return false;
      }

      if (!ok)
      {
        error = refused ?? ToolResult.Fail(ToolErrors.PreconditionRefused,
          "Dwight could not check those changes, so it will not offer them.");
        return false;
      }
      plan = prepared;
      return true;
    }

    /// <summary>
    /// U28: THE USER'S SELECTION AT THE ASK, as the agent ids every read speaks - read on the UI thread, where the
    /// selection lives, with the same bounded, cancellable wait <see cref="PrepareSpot"/> uses (this handler runs on the
    /// turn's worker). The ids are ISSUED through the thread's registry, exactly as the turn-start situation issues them,
    /// so an id the model was told about and an id resolved here are the same id. A read that cannot finish REFUSES:
    /// "nothing is selected" would be a claim this path could not stand behind.
    /// </summary>
    private static bool SelectedIdsAtAsk(GH_Document doc, string threadId, out List<int> ids, out ToolResult error)
    {
      ids = new List<int>();
      error = null;
      if (doc == null)
      {
        error = ToolResult.Fail(ToolErrors.NoDocument, "No Grasshopper document is open.");
        return false;
      }

      var read = new List<int>();
      var ok = false;
      var gate = new System.Threading.ManualResetEventSlim(false);
      Ui(() =>
      {
        try
        {
          var scope = ScopeFor(threadId, doc);
          foreach (var o in doc.SelectedObjects()) read.Add(scope.Ids.Issue(o.InstanceGuid));
          ok = true;
        }
        catch { }
        finally { try { gate.Set(); } catch { } }
      });

      try
      {
        if (!gate.Wait(TimeSpan.FromSeconds(SpotAskWaitSeconds), DwightSession.CurrentToken) || !ok)
        {
          error = ToolResult.Fail(ToolErrors.PreconditionRefused,
            "Dwight could not read the user's selection just now, so it has not shown them a card and nothing was " +
            "changed. Name the components this is for, by id, and ask again.");
          return false;
        }
      }
      catch (OperationCanceledException)
      {
        error = ToolResult.Fail(ToolErrors.PreconditionRefused,
          "Stopped. Dwight did not finish reading the selection, and nothing was changed.");
        return false;
      }

      ids = read;
      return true;
    }

    /// <summary>
    /// The user clicked Yes. THIS is the gate: a click, on a token this session minted, consumed once.
    ///
    /// A token that is not live is refused with the reason and a way forward - the restored-card case
    /// (the session that minted it is gone) and the already-used case both land here, and neither is a
    /// silent no-op on a button the user just pressed.
    /// </summary>
    private static void OnConsentClick(WebPanel wp, GH_Document doc, string threadId, string rawToken, string autoMode = null)
    {
      // A batch card's button posts "TOKEN:0,2,5" - the token and the rows left ticked (ConsentTokens.Split). Every other
      // card posts the bare token, and `ticked` is then null and read by nothing.
      var token = ConsentTokens.Split(rawToken, out var ticked);

      // Brief BR: A CLICK DURING A RUN IS REFUSED BEFORE THE CARD IS SPENT. One canvas, one undo stack, one verdict: an edit, a value or a spot
      // applies from the click with no turn to refuse it, so it would write under the running turn's before and after, and a card that starts a
      // turn would be spent on a refusal. With one window another chat is one click away, so this is now easy to reach. The card stays live for
      // when the run ends. An automatic approval runs after its own turn's Done, and is not a click.
      if (autoMode == null && DwightSession.IsBusy)
      {
        AppendPluginHtml(wp, doc, threadId, BusyHtml(threadId));
        return;
      }

      if (!TakeConsent(token, threadId, out var kind, out var seed, out var reason))
      {
        // ONE ANSWER PER CAUSE (#255). One message for all of them is exactly why the field could report
        // the symptom and not the cause - "that request was from an earlier session" was also what a
        // wrong-thread and an already-used click said. U18 added a fourth: the other button of a spot card.
        AppendPluginHtml(wp, doc, threadId, System.Net.WebUtility.HtmlEncode(ConsentTokens.Say(reason)));
        return;
      }

      // #378b: the approval is recorded HERE - on redemption, never on mint - with the canvas as it stands
      // BEFORE the approved flow runs. A card the user declined or ignored must not stop the model asking
      // again; only an approval that ran is evidence. #613: recording is not arming. Reorganise and
      // Simplify run from this click, so they arm now; solve arms when its turn dispatches solve_and_check
      // (RunTurn's OnToolCall); build and scaffold never arm, because their flow is a model turn that may
      // never call its tool, and the click alone once refused a build that never ran (#609 link 2).
      MutationLogFor(threadId).RecordApproved(kind, CanvasStamp(doc));

      // The click IS a user turn, so it is recorded as one - visible, persisted, and part of the
      // transcript the model sees next time. An approval that left no trace would make the conversation
      // read as though Dwight had acted unasked.
      // Brief AL: an approval the plugin gave on the mode's authority is recorded as one, in the same place a click is, so the model's
      // transcript shows it exactly as it shows a click.
      if (autoMode != null)
      {
        _autoCount.Add(threadId);
        ConsentLog("auto-approved kind=" + kind + " mode=" + autoMode + " thread=" + Short(threadId));
      }
      // Brief BG: the card the user answered becomes its receipt - in the record and on screen - before the echo line, so the shell can
      // leave the echo unrendered beside a receipt that already says "approved by you". An automatically approved card arrived as one.
      if (autoMode == null) AnswerCard(wp, doc, threadId, token, CardReceipt.ApprovedByYou);
      AppendApprovalEcho(wp, doc, threadId, autoMode != null ? ApprovalMode.AutoUserText : ApprovalMode.ClickUserText);

      // U90: an approved run_script grants EXACTLY the source the card showed, once, to one turn. No seed, no SeedPlan and no model text
      // is read here: the grant is built from the record the card was minted with, and the turn that runs it can only run that.
      if (string.Equals(kind, Dwight.Agent.FullControl.Kind, StringComparison.Ordinal))
      {
        if (!CommandHistory.ThreadFullControl(doc, threadId) || string.IsNullOrWhiteSpace(seed?.Source))
        {
          AppendPluginHtml(wp, doc, threadId, System.Net.WebUtility.HtmlEncode(Dwight.Agent.RunScriptApproval.NotRunLine));
          return;
        }
        var grant = new RunScriptGrant { Source = seed.Source, Purpose = seed.Why, TimeoutSeconds = seed.RunTimeoutSeconds ?? Dwight.Agent.FullControl.DefaultTimeoutSeconds };
        // Brief AP: the change this run records carries the card's words and who approved it, into History.
        Dwight.Session.ChangeLog.Approve(doc, Dwight.Session.HistoryCore.CardScope(ConsentCard.Title(kind), null),
          autoMode != null ? Dwight.Session.HistoryCore.ByAuto(ApprovalMode.Label(autoMode)) : Dwight.Session.HistoryCore.ByYou, false, threadId);
        // U100: THE APPROVAL RUNS THE GRANT. The plugin runs the card's own record, here, with no model call between the click and the
        // run; the model is never asked to send the source again. Then one read-only turn reads the report.
        RunApprovedScript(wp, doc, threadId, grant, autoMode != null);
        return;
      }

      // NARRATIVE only (#220): what stage the conversation reached, for rendering. It confers nothing -
      // the capability below travels on the call stack and dies with the turn.
      CommandHistory.SetThreadPhase(doc, threadId, kind);

      // The route is a pure function of the KIND the user was shown (#235). The model's reason is not a
      // parameter of it and was never stored on the pending record, so a persuasive sentence cannot move
      // where an approval lands.
      // THE ROUTE IS DECIDED FIRST, FROM THE KIND ALONE (#235/#287). Only after it is fixed does the seed
      // get read - and then only to choose which components the verb opens on. It cannot reach this switch.
      var route = ConsentCard.Route(kind);
      var plan = SeedPlan.From(kind, seed, IdStateFor(threadId, doc));
      var onto = SeedGuids(plan, threadId, doc);

      // Brief AP: the changes this approval leads to carry the card's words and who approved it, into History.
      Dwight.Session.ChangeLog.Approve(doc, Dwight.Session.HistoryCore.CardScope(ConsentCard.Title(kind), seed?.Regions),
        autoMode != null ? Dwight.Session.HistoryCore.ByAuto(ApprovalMode.Label(autoMode)) : Dwight.Session.HistoryCore.ByYou,
        route == ConsentRoute.Reorganise || route == ConsentRoute.Simplify, threadId, seed?.SpotDone);

      // #287(b): a nomination that no longer grounds is DROPPED AND SAID, never silently missing.
      if (plan.Dropped.Count > 0)
        AppendPluginHtml(wp, doc, threadId, System.Net.WebUtility.HtmlEncode(
          "Some of what I named is not there any more: " + string.Join("; ", plan.Dropped) + "."));

      switch (route)
      {
        case ConsentRoute.Solve:
          RunTurn(wp, doc, threadId, TurnCapability.ReadAndSolve);
          return;
        case ConsentRoute.Scaffold:
          RunTurn(wp, doc, threadId, TurnCapability.Scaffold);
          return;
        case ConsentRoute.Build:
        {
          // ============================================================================================
          // U18: A SPOT APPROVAL. The plugin applies it HERE, from the card's own record, and the
          // approved turn that follows is a RE-READ rather than a build.
          //
          // WHY NOT HAND IT TO THE TURN. A spot card NAMES EVERY COMPONENT, because a spot has no region
          // name standing in for its contents - the components ARE the scope (#338). If the turn then
          // built from a spec of the model's own, the card could name one set while another was placed,
          // and EVERY GUARD WOULD PASS WITH A TRUE VERDICT: preservation holds, completeness holds, the
          // trio reports success, because the card is the only thing that was wrong and nothing in the
          // system reads the card. So the model is never asked for a spec here at all - the record IS
          // the spec (ADR-075's shape: made unrepresentable, not detected).
          //
          // This is set_value's path exactly: the plugin applies the validated change from the card's own
          // record, then grants one read.
          // ============================================================================================
          if (!string.IsNullOrEmpty(plan.PreFillSpot))
          {
            ApplySpot(wp, doc, threadId, plan, autoMode != null);   // U105: an automatic approval is followed up if it does not land
            return;
          }

          // THE APPROVED SCOPE IS RE-RESOLVED AT CLICK TIME, against the canvas as it is NOW. The card may
          // have sat for a while, and a region can be undone between the ask and the answer - so the turn
          // is scoped to what still exists, never to what was true when the card was drawn. #338's rule
          // for a scope that resolves to nothing is REFUSE, and a partial loss is NAMED rather than
          // quietly narrowed (#327's shape at a mutation).
          var resolution = BuildScope.Resolve(plan.Regions, LiveRegions(doc, null));
          if (!string.IsNullOrEmpty(resolution.Message))
            AppendPluginHtml(wp, doc, threadId, System.Net.WebUtility.HtmlEncode(resolution.Message));
          if (resolution.Outcome == BuildScopeOutcome.Refuse) return;

          AppendPluginHtml(wp, doc, threadId, System.Net.WebUtility.HtmlEncode(
            "Building " + string.Join(", ", resolution.Regions) + ". Stop is live throughout, and each " +
            "region is one Ctrl-Z."));
          // U65: the scripts the card showed, for the regions that still resolve - granted to the turn beside the regions.
          var grantedScripts = plan.Scripts.Where(s => resolution.Regions.Contains(s.Region)).ToList();
          RunTurn(wp, doc, threadId, TurnCapability.Build, resolution.Regions, grantedScripts, autoApproved: autoMode != null);
          return;
        }
        case ConsentRoute.EditScript:
        {
          // Phase-9 U20. WHAT THE USER APPROVED IS WHAT EXECUTES. The card's own record - the claim, the
          // normalised source, the appended ports and the BASE IT DIFFED FROM - is re-validated against the
          // canvas as it is now, then the plugin applies exactly that. No model call sits between the
          // approval and the execution, and no second trial runs: the outcome the card was drawn from
          // travels with it, so the report cannot disagree with what the user read.
          var editScope = ScopeFor(threadId, doc);

          var again = new HandoffSeed
          {
            Target = plan.Target,
            Claim = plan.Claim == ScriptEditClaim.Preserves ? "preserves" : "changes",
            Source = plan.Source,
            AddInputs = plan.AddInputs.ToList(),
            AddOutputs = plan.AddOutputs.ToList(),
            InputAccess = new Dictionary<string, string>(plan.InputAccess, StringComparer.OrdinalIgnoreCase),
          };

          if (!EditScriptAsk.Validate(again, IdStateFor(threadId, doc),
                                      id => GhEditScript.ReadLive(doc, editScope.Ids, id),
                                      out var editChange, out var editClickError))
          {
            // #682's rule: a click that changes nothing is a REPORT, not chrome. The model's last act was
            // raising this card, and a refusal it cannot see would leave it believing the edit ran (#624).
            AppendReport(wp, doc, threadId, ScriptEditReport.NotAppliedHtml(editClickError.Message),
                         ScriptEditReport.NotAppliedText(editClickError.Message));
            return;
          }

          // THE COMPARE-AND-SET, and it is the reason BaseSource travels. The card promised a change FROM a
          // specific text; the re-validation above read the source as it is NOW. If those differ, the diff
          // the user approved is no longer what this edit would do, and applying it would execute a change
          // whose shape they never saw (#775).
          if (!string.Equals(Dwight.Data.SourceToken.Normalise(plan.BaseSource),
                             editChange.BaseSource, StringComparison.Ordinal))
          {
            const string moved =
              "That component's source has changed since Dwight showed you the diff, so the change you " +
              "approved is no longer what it would do. Nothing was changed. Ask again and Dwight will diff " +
              "against what is there now.";
            AppendReport(wp, doc, threadId, ScriptEditReport.NotAppliedHtml(moved), ScriptEditReport.NotAppliedText(moved));
            return;
          }

          if (!editScope.Ids.TryGetGuid(editChange.TargetId, out var editGuid))
          {
            const string gone = "The component I named is no longer on the canvas, so nothing was changed.";
            AppendReport(wp, doc, threadId, ScriptEditReport.NotAppliedHtml(gone), ScriptEditReport.NotAppliedText(gone));
            return;
          }

          // The sentences were rendered AT THE ASK, from the trial's own facts, and carried on the card's
          // record - so the report the user reads after applying is the one the card was drawn from, and
          // nothing here has to re-render it from observations it no longer holds.
          GhEditScript.Apply(doc, editGuid, editChange, plan.Outcome, plan.Report, plan.TrialNote, result =>
            Ui(() =>
            {
              // TWO OUTCOMES, TWO RENDERERS whose names match what they assert. This first called
              // SetValueReport.NotAppliedHtml for BOTH - and that method hardcodes "Not applied." in its
              // markup, so a successful edit read "Not applied. Applied. IT CHANGES WHAT THE COMPONENT
              // PRODUCES": a report contradicting itself in its first two words. #680 one layer out - a
              // renderer carrying a claim in its body inherits that claim, however neutral its signature looks.
              var ok = result != null && result.Ok;
              var text = ok ? plan.Report : result?.Message ?? "Dwight could not apply that edit.";
              // The CHOICE of renderer is ScriptEditReport.Html/Text, not a conditional here - it is the
              // decision the bug was in, and left inline no test could reach it (#612).
              AppendReport(wp, doc, threadId, ScriptEditReport.Html(ok, text), ScriptEditReport.Text(ok, text));
              FollowUpIfAuto(wp, doc, threadId, autoMode != null, ok);   // U105
            }));
          return;
        }
        case ConsentRoute.Edit:
        {
          // Phase-9 U6 + U21. WHAT THE USER TICKED IS WHAT EXECUTES: the card's own record plus the ticks that arrived with
          // the click - re-validated against the canvas as it is NOW (every ticked row must still resolve to exactly one
          // existing target, or the click REFUSES naming the rows), then applied in one undo record and judged by the
          // predicted delta on that set alone. No model call sits between the approval and the execution. The report is
          // a REPORT (#682): the model's last act was raising this card, and a result it cannot see would leave it
          // believing the edit ran (#624). Then the ONE re-read the card named, only after an applied edit.
          GhEditOps.RunApproved(doc, ScopeFor(threadId, doc).Ids, plan.Ops, ticked, (ok, text) =>
            Ui(() =>
            {
              AppendReport(wp, doc, threadId, ScriptEditReport.Html(ok, text), ScriptEditReport.Text(ok, text));
              if (ok) RunTurn(wp, doc, threadId, SetValueCore.Continuation);   // a re-read, holding no approval
              else FollowUpIfAuto(wp, doc, threadId, autoMode != null, landed: false);   // U105
            }));
          return;
        }
        case ConsentRoute.SetValue:
        {
          // Phase-9 U4. WHAT THE USER APPROVED IS WHAT EXECUTES: the validated (target, state) from the
          // card is re-validated against the canvas as it is NOW - the target must still exist and still be
          // the type the card named, and the restore rule must still hold - then the plugin applies exactly
          // that. No model call sits between the approval and the execution (Q1). A target that resolves to
          // nothing REFUSES (#338); nothing falls back to another component.
          var scope = ScopeFor(threadId, doc);

          // #683 (6b): the scope rule first, in its own words - the target is still here and still the type the card
          // named - before the full re-validation, whose parser would otherwise answer a changed type in a parser's terms.
          var idState = plan.Target.HasValue ? IdStateFor(threadId, doc)(plan.Target.Value) : IdState.Unknown;
          var liveType = plan.Target.HasValue ? GhSetValue.ReadLive(doc, scope.Ids, plan.Target.Value)?.Type : null;
          if (!SetValueCore.MayProceed(idState, liveType, plan.TargetType, out var scopeRefusal))
          {
            AppendReport(wp, doc, threadId, SetValueReport.NotAppliedHtml(scopeRefusal), SetValueReport.NotAppliedText(scopeRefusal));
            return;
          }

          var again = new HandoffSeed { Target = plan.Target, State = plan.State };
          // #682: a click that changes nothing is a REPORT, not chrome - the model's last act was raising this card, and a
          // refusal it cannot see would leave it believing the change ran (#624's lesson).
          if (!SetValueAsk.Validate(again, IdStateFor(threadId, doc), id => GhSetValue.ReadLive(doc, scope.Ids, id),
                                    out var change, out var declared, out var clickError))
          {
            AppendReport(wp, doc, threadId, SetValueReport.NotAppliedHtml(clickError.Message),
                         SetValueReport.NotAppliedText(clickError.Message));
            return;
          }
          if (!scope.Ids.TryGetGuid(change.Id, out var targetGuid))
          {
            const string gone = "The component I named is no longer on the canvas, so nothing was changed.";
            AppendReport(wp, doc, threadId, SetValueReport.NotAppliedHtml(gone), SetValueReport.NotAppliedText(gone));
            return;
          }

          GhSetValue.Apply(doc, targetGuid, declared, change, result =>
          {
            // The verdict is the plugin's REPORT: Dwight's card on screen, and a user-turn line in the
            // transcript so the model reads it (ChatToTranscript.Report). Then the ONE continuation the
            // card named (Q2) - and only on an applied change; a reverted one has nothing to re-read.
            AppendReport(wp, doc, threadId, SetValueReport.Html(result), SetValueReport.Text(result));
            if (result.Ok) RunTurn(wp, doc, threadId, SetValueCore.Continuation);   // #678: a re-read, holding no approval
            else if (AutoFollowUp.ShouldRun(autoMode != null, AutoFollowUp.FlowEnd.Refused))
            {
              // U94: an automatic approval that ended in a refusal: the report is in the transcript, and the model reads it once.
              AppendPluginHtml(wp, doc, threadId, System.Net.WebUtility.HtmlEncode(AutoFollowUp.Line));
              RunTurn(wp, doc, threadId, TurnCapability.ReadOnly);
            }
          });
          return;
        }
        // U28 at the click (#918, #939): the verb is handed how many components the card NAMED - so its refusal, if the
        // approved scope no longer resolves, says "named none" or "named N, now gone" truthfully - and a closure over
        // this thread, so that refusal lands HERE as well as in the toast. THE REPORT DOOR, NOT CHROME (the wave-2
        // architect's ruling): #682, a click that changes nothing is a report; and #918's harm was a model answering
        // from a thread that was never told the reorganise had been refused. The renderer is ScriptEditReport's
        // verb-neutral pair ("Not applied - the canvas is unchanged"), never SetValueReport's, whose transcript form
        // says "the user approved this set_value" - a claim about the wrong kind.
        //
        // The opening notice is posted only when there is something to open ON. A seeded route with no surviving ids is
        // refused by VerbScope.Resolve by construction (seeded + empty = Refuse), so an "Opening Reorganise..." line
        // there would announce a run that cannot happen, directly above the refusal that says so.
        case ConsentRoute.Reorganise:
        {
          var named = (seed?.ComponentIds ?? new List<int>()).Distinct().Count();
          if (onto.Count > 0) AppendPluginHtml(wp, doc, threadId, OpeningNotice("Reorganise", onto, plan.PreFillMode));
          LlmActions.Reorganise(onto, plan.PreFillMode, named, ScopeRefusedReport(wp, doc, threadId));
          return;
        }
        case ConsentRoute.Simplify:
        {
          var named = (seed?.ComponentIds ?? new List<int>()).Distinct().Count();
          if (onto.Count > 0) AppendPluginHtml(wp, doc, threadId, OpeningNotice("Simplify", onto, plan.PreFillMode));
          // #332: the approved INTENT travels too. It biases the proposal toward what the user agreed to
          // and is read by nothing that verifies - #287(a) exactly.
          LlmActions.Simplify(onto, plan.PreFillMode, plan.Intent, named, ScopeRefusedReport(wp, doc, threadId));
          return;
        }
        default:
          AppendPluginHtml(wp, doc, threadId, "Dwight could not open that.");
          return;
      }
    }

    /// <summary>
    /// A canvas tool answered (WP-3 Stage D). The one case that needs recording is a scaffold map: the
    /// thread stores the PLAN for WP-4 to build from, and the conversation gets a SUMMARY naming what
    /// landed by address.
    ///
    /// Two readers, two forms, on purpose. The stored map is machine-readable and sits outside the message
    /// list so the caps can never trim the plan (#167); the summary is what a person reads and what a
    /// reopen replays. Rendering from both would give one artifact two sources of truth.
    /// </summary>
    private static void OnToolLanded(WebPanel wp, GH_Document doc, string threadId, string name, string args,
      string envelope, Dictionary<string, RegionOutcome> built = null, Dictionary<string, int> attempts = null,
      BuildTurnRecord record = null)
    {
      // FAIL-SAFE (ADR-029, guardrail 4). This runs on the UI thread via AsyncInvoke, so it is OUTSIDE the
      // loop's own try/catch - a throw here would reach Rhino's unhandled path rather than being absorbed
      // as the best-effort bookkeeping it is. The map has already landed and verified by the time we get
      // here; a recording failure must never cost more than the recording.
      try
      {
        // #96: BUILD-THEN-REPORT WITH A RUNNING SUMMARY. Each region reports AS IT LANDS - no per-region
        // pause, because the user consented once by naming the regions and control comes from watching it
        // happen, plus Stop, plus one Ctrl-Z per step. This is that running summary, and the same call
        // writes the persisted record so a reopened thread can still say what was built.
        if (string.Equals(name, AgentTools.BuildRegion, StringComparison.OrdinalIgnoreCase))
        {
          RecordBuild(wp, doc, threadId, args, envelope, built, attempts, record);
          return;
        }

        if (!string.Equals(name, AgentTools.ProposeScaffold, StringComparison.OrdinalIgnoreCase)) return;

        bool ok;
        try { ok = JsonNode.Parse(envelope)?["ok"]?.GetValue<bool>() ?? false; }
        catch { ok = false; }
        if (!ok) return;   // a refused or reverted map left no canvas to describe and no plan to keep

        CommandHistory.SetThreadScaffoldMap(doc, threadId, ScaffoldMapRecord.Build(args, envelope));
        // NARRATIVE only (#220) - it records which stage the conversation reached and confers nothing.
        CommandHistory.SetThreadPhase(doc, threadId, "scaffold");
        AppendPluginHtml(wp, doc, threadId, CardReceipt.MapLine(ScaffoldMapRecord.SummaryHtml(envelope)));   // Brief BG: a result is a receipt
      }
      catch (Exception ex)
      {
        try { Rhino.RhinoApp.WriteLine("Dwight: the scaffold map landed but could not be recorded - " + ex.Message); }
        catch { }
      }
    }

    /// <summary>
    /// One region's line in the running summary, and its entry in the persisted record (#96, Stage C).
    ///
    /// The OUTCOME is read from the verdict rather than inferred from whether the call succeeded: a build
    /// that applied and verified, one that applied and carries new errors, and one that reverted itself
    /// are three different things, and reporting the middle one as done would be the surface claiming a
    /// state the canvas contradicts.
    /// </summary>
    private static void RecordBuild(WebPanel wp, GH_Document doc, string threadId, string args, string envelope,
      Dictionary<string, RegionOutcome> built, Dictionary<string, int> attempts, BuildTurnRecord record = null)
    {
      // #649 (Finding C): the classification is BuildEnvelope.Classify, GH-free and tested against ToolResult's own
      // envelopes. It used to be written here and read a reverted verdict from error.data, which nothing emits.
      var env = BuildEnvelope.Classify(envelope, args);
      if (env == null) return;   // unreadable, or an un-addressed build with no region row to write

      var address = env.Address;
      var outcome = env.Outcome;
      var placed = env.Placed;
      var note = env.Note;                 // #625/#647: a boundary refusal's note is built from its structure
      var reconnected = env.Reconnected;   // #625: what an applied build reconnected or restored
      var wouldCut = env.WouldCut;         // #647: refused for a wire it would cut

      // The tidy's and the close line's subject: a region's STANDING state this turn (U76). A rebuild that applied or
      // reverted supersedes the earlier one; a refused attempt does not take a standing build away.
      BuildProgress.FoldTurn(built, address, outcome);
      if (outcome == RegionOutcome.Built || outcome == RegionOutcome.BuiltWithErrors) _scriptsOnFile.Landed(threadId, address);   // Brief CA
      record?.Record(address, outcome, placed, note, reconnected, wouldCut, env.Residue);   // #624: the values the running summary shows
      if (attempts != null)
      {
        // #649: the verdict's own attempts-left. On a revert this used to read 0 (no verdict was found), so the
        // tidy was told every reverted region had spent its whole budget.
        attempts[address] = Math.Max(1, DwightConfig.AgentBuildMaxAttemptsPerRegion - (env.AttemptsLeft ?? 0));
      }

      // #394: the values this build set, read back off the canvas by build_region and carried on the
      // verdict. The row is where an unnoticed value becomes noticeable - which is exactly what #377 was.
      var state = env.State;

      AppendPluginHtml(wp, doc, threadId, CardReceipt.BuildLine(outcome, BuildProgress.LineHtml(address, outcome, placed, note, state, reconnected)));   // Brief BG
      OfferNote(wp, doc, threadId, address, outcome, placed);   // Brief DC: "Pin a note on this", the plugin's own receipt sentence, on a click

      try
      {
        var merged = BuildProgress.Merge(CommandHistory.ThreadBuildProgress(doc, threadId),
          address, outcome, placed, note, DateTime.UtcNow.ToString("o"));
        CommandHistory.SetThreadBuildProgress(doc, threadId, merged);
      }
      catch (Exception ex) { TurnLog("build progress not recorded: " + ex.Message); }
    }

    /// <summary>
    /// Lay out what this run built, and stop the built regions' scribbles describing work that is now
    /// done (Stage D).
    ///
    /// Scheduled rather than run inline: this is a canvas mutation and every canvas mutation in the
    /// product goes through ScheduleSolution (guardrail 1). Its outcome is reported in the thread - a
    /// tidy that reverted itself must say so, because the user is about to look at a canvas that does not
    /// match what they just watched happen.
    /// </summary>
    private static void TidyAfterBuild(WebPanel wp, GH_Document doc, string threadId,
      Dictionary<string, RegionOutcome> built, Dictionary<string, int> attempts)
    {
      try
      {
        doc.ScheduleSolution(10, d =>
        {
          var outcome = GhRegionTidy.Run(d, built, attempts, AgentScope.LedgerForDocument(d));
          var landing = GhRegionTidy.Landings(d, built);   // Brief CM: read here, after the tidy has moved them
          Ui(() =>
          {
            // #573: the tidy finishes AFTER the turn, so this is the canvas Dwight actually left. Recorded
            // before any early return, so every exit records.
            RememberCanvas(doc, threadId);

            if (!outcome.Ran) return;

            // A tidy that could not keep its promise says so IN THE THREAD. Silence here would leave the
            // user with a canvas they cannot reconcile against the summary above it.
            if (!string.IsNullOrWhiteSpace(outcome.Note))
              AppendPluginHtml(wp, doc, threadId, System.Net.WebUtility.HtmlEncode(
                "Laying the built regions out: " + outcome.Note + "."));
            else if (outcome.Moved > 0 || outcome.RegionsMoved > 0 || outcome.ScribblesCleared > 0)
              AppendPluginHtml(wp, doc, threadId, System.Net.WebUtility.HtmlEncode(
                RegionTidy.TidyLine(outcome.RegionsTidied, outcome.RegionsMoved, outcome.Moved)));

            // #369: a built region that still overlaps something is STATED, on every path: the words above
            // can be "already laid out", and that must not swallow it. A fact about the canvas needs no
            // consent, while moving the user's regions would.
            if (!string.IsNullOrWhiteSpace(outcome.Crowding))
              AppendPluginHtml(wp, doc, threadId, System.Net.WebUtility.HtmlEncode(outcome.Crowding));

            // Brief CM: where the run's regions are, said and framed once, in words the model reads next turn too.
            if (landing != null)
            {
              AppendReport(wp, doc, threadId, CardReceipt.ReportLine("<p>" + System.Net.WebUtility.HtmlEncode(landing.Line) + "</p>"), landing.Line);
              CanvasNav.GoTo(landing.Frame, NavReason.BuildJustLanded);
            }
          });
        });
      }
      catch (Exception ex) { TurnLog("tidy could not be scheduled: " + ex.Message); }
    }

    private static JsonObject TryParse(string json)
    {
      if (string.IsNullOrWhiteSpace(json)) return null;
      try { return JsonNode.Parse(json) as JsonObject; } catch { return null; }
    }

    private static string Str(JsonNode n)
    {
      if (n == null) return null;
      try { return n.GetValue<string>(); } catch { return null; }
    }

    /// <summary>
    /// What the plugin KNOWS about the situation, told to the model rather than asked of it (ADR-056,
    /// ADR-038's context envelope). Whether a canvas is empty, how big it is, and whether anything is
    /// selected are FACTS - a model should not have to spend a call discovering them, and it should never
    /// be the one guessing them.
    ///
    /// It is deliberately three numbers and no more: what the regions ARE is a read the model makes for
    /// itself, for free (#190), and pushing a whole overview here would be the whole-canvas dump ADR-035
    /// exists to prevent.
    /// </summary>
    private static string Situation(GH_Document doc, IReadOnlyList<Guid> selection, AgentScope scope)
    {
      var objects = 0;
      try { objects = doc?.Objects?.Count ?? 0; } catch { }

      if (objects == 0)
        return "\n\nRight now: the canvas is EMPTY. There is nothing to read and nothing to extend.";

      var line = "\n\nRight now: the canvas holds " + objects + " object" + (objects == 1 ? "" : "s") + ", and ";

      // #257: WHICH components are selected, not merely how many. Saying "4 are selected" without saying
      // WHICH is why the agent explained a region the user had not asked about - it could not scope to a
      // selection it was never given, so it picked one. The ids are issued through the registry, so they
      // are the same ids every read tool speaks (ADR-058 groundedness), and computing them is free -
      // exactly the work the model should never be asked to do (ADR-056).
      var ids = new List<int>();
      try { foreach (var g in selection ?? new List<Guid>()) ids.Add(scope.Ids.Issue(g)); }
      catch { }

      if (ids.Count == 0)
        return line + "the user has NOTHING selected, so the question is about the definition as a whole. " +
               "Read before you answer.";

      ids.Sort();
      var shown = ids.Take(40).ToList();
      return line + "the user has SELECTED these components: " +
             string.Join(", ", shown) +
             (ids.Count > shown.Count ? ", and " + (ids.Count - shown.Count) + " more" : "") +
             ". Unless they clearly mean something else, THAT SELECTION is what they are asking about - " +
             "scope your answer to it and to what feeds it, and say so if you look wider. " +
             "It is captured fresh for every message, so it is what they have selected NOW.";
    }

    /// <summary>
    /// The agent scope - and therefore the id registry - is per THREAD, not per turn (#191: "scoped to the
    /// agent session/thread").
    ///
    /// A fresh scope per turn would re-issue ids from scratch every message, so "the Loft component (42)"
    /// in one answer could address a different object in the next - the mis-grounding class the registry
    /// exists to prevent, arriving through the door the registry left open. Held in memory only: it is a
    /// capability-adjacent object, never persisted, and it re-binds when the document changes because ids
    /// from one document mean nothing in another.
    /// </summary>
    private static readonly Dictionary<string, AgentScope> _scopes = new Dictionary<string, AgentScope>();

    private static AgentScope ScopeFor(string threadId, GH_Document doc)
    {
      if (_scopes.TryGetValue(threadId, out var existing) && existing.IsBoundTo(doc)) return existing;

      // The thread travels with the scope so get_scaffold_map can read THIS conversation's stored plan
      // (WP-4 Stage A) - never another's, and never a guess when there is none.
      var scope = new AgentScope(doc, threadId);
      // #229: restore the PINNED ids from the store, so a reopened conversation - and the scaffold map it
      // carries, whose anchors are agent ids - keeps meaning what it meant. Aliveness is not restored: the
      // next read Syncs against the live document, which is the only honest source for it.
      try { scope.Ids.FromJson(CommandHistory.IdSpace(doc)); } catch { }

      _scopes[threadId] = scope;
      return scope;
    }

    /// <summary>Store the id space after a turn may have issued new ids. Best-effort: identity is a cache
    /// of what the canvas already knows, so a failure here never fails the turn.</summary>
    private static void SaveIdSpace(GH_Document doc, AgentScope scope)
    {
      try
      {
        var json = scope?.Ids?.ToJson();
        if (json != null) CommandHistory.SetIdSpace(doc, json);
      }
      catch { }
    }

    /// <summary>
    /// The provider this turn talks to. Ordinarily the one the user's settings resolve to; in a DEBUG
    /// build the full-path drill (#268) may substitute a SCRIPTED stub so the whole path from the chat's
    /// entry to a rendered completion can be walked deterministically and for free.
    ///
    /// The override is read HERE, after the front door (AgentFrontDoor: eligibility, then the key), so the drill
    /// traverses those guards for real rather than around them. The drill's precondition list asks the SAME front
    /// door, so it refuses - and says why - on exactly the model, provider and key RunTurn would refuse on. In a
    /// Release build this is the ordinary call and nothing else exists.
    /// </summary>
    private static ILlmProvider ProviderFor(DwightSettings settings, ModelResolver.Resolved resolved)
    {
#if DEBUG
      if (DebugProviderOverride != null) return DebugProviderOverride;
#endif
      return LlmProviders.Create(settings.EffectiveProvider, resolved.Model.Id);
    }

#if DEBUG
    /// <summary>Set by the full-path drill for the duration of one turn, and cleared in its finally.
    /// Nothing in a shipping path reads or writes it.</summary>
    internal static ILlmProvider DebugProviderOverride;

    /// <summary>
    /// THE DRILL'S DOOR (#268): open a Scaffold thread the way the user does, and post a message through
    /// the same two calls <see cref="OnUserMessage"/> makes.
    ///
    /// The ONE difference from a typed message is the capability constant - the drill passes
    /// <see cref="TurnCapability.Build"/> so its scripted sequence can include a MUTATING tool, which is
    /// the longest path and the one that hangs rather than errors. The drill's report states that
    /// difference rather than leaving it for a reader to discover (#280).
    /// </summary>
    internal static bool DebugOpenAndSend(string text, TurnCapability capability,
                                          out GH_Document doc, out string threadId, out string why)
    {
      var opened = OpenThread();
      doc = opened.Doc;
      threadId = opened.ThreadId;
      why = null;

      if (!(opened.Panel is WebPanel wp))
      {
        why = "the Build panel opened as the Eto fallback rather than a WebPanel, so the chat's own " +
              "path is not available (WebView2 missing or hung - ADR-088's probe fired).";
        return false;
      }

      AppendUserText(wp, doc, threadId, text);
      RunTurn(wp, doc, threadId, capability);
      return true;
    }

    /// <summary>The stored thread, for the drill's completion witness. It reads Dwight's own record, not
    /// the screen - and the drill says so, because C# cannot witness a paint (#276 threw nothing).</summary>
    internal static IReadOnlyList<Dwight.Session.StoredMessage> DebugThreadMessages(GH_Document doc, string threadId) =>
      CommandHistory.ThreadMessages(doc, threadId);
#endif

    /// <summary>
    /// The region addresses that ACTUALLY EXIST on the canvas right now, filtered to what was nominated
    /// (or all of them when <paramref name="nominated"/> is null).
    ///
    /// Read from the canvas rather than from the stored map, because the canvas is what a build will be
    /// aimed at and #137 says a structural fact comes from the authoritative structure. A nomination that
    /// does not resolve is simply not returned - the caller decides what to say about it.
    /// </summary>
    private static IReadOnlyList<string> LiveRegions(GH_Document doc, IReadOnlyList<string> nominated)
    {
      var map = ScaffoldMapRecord.Read(null, GhScaffoldMap.CanvasRegions(doc));
      var live = map.Regions.Select(r => r.Address).ToList();
      if (nominated == null) return live;

      var wanted = new HashSet<string>(nominated.Where(n => !string.IsNullOrEmpty(n)), StringComparer.Ordinal);
      return live.Where(wanted.Contains).ToList();
    }

    /// <summary>
    /// A turn's ceilings, chosen for the SHAPE OF WORK it is doing (WP-4 Stage C).
    ///
    /// A build turn is not a longer comprehension turn - it reads a map, places a region, solves, reads
    /// the result, and repeats per region, with a 120s solve watchdog inside every write. The
    /// comprehension turn's five-minute wall clock would time out a perfectly healthy three-region build,
    /// and its twelve steps would exhaust before the second region. So Build gets its own numbers with
    /// their own stated bases rather than inheriting numbers chosen for a different job (#216).
    /// </summary>
    // ---- Brief AO: the chat header ------------------------------------------------------------------------------------------

    private static readonly HashSet<string> _pinFallbackSaid = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>True the first time a thread's unresolvable pin is met, so the plugin line is said once per pin and not at every turn.</summary>
    private static bool SaidPinFallback(string threadId, string pinId)
    {
      lock (_pinFallbackSaid) return _pinFallbackSaid.Add((threadId ?? "") + "|" + (pinId ?? ""));
    }

    /// <summary>The header's document, from the thread's own record and the live catalogue. Nothing here comes from the shell.</summary>
    private static string BuildHeaderJson(GH_Document doc, string threadId)
    {
      var settings = DwightSettings.Current;
      var threadModelId = CommandHistory.ThreadModelId(doc, threadId);
      var threadPosture = CommandHistory.ThreadPosture(doc, threadId);
      var resolved = settings.ResolveChatModel(threadModelId, threadPosture, out _, out _);
      var posture = PostureInForce(threadPosture, settings);
      settings.ResolveChatModel("", posture, out _, out var settingsPinId);
      return ChatHeader.Json(new ChatHeaderInput
      {
        Balanced = settings.BalancedChatModel(),
        PostureModels = ChatHeader.Postures.ToDictionary(p => p.Id, p => settings.ResolveChatModel("", p.Id, out _, out _)?.Model),
        SettingsPinned = !string.IsNullOrEmpty(settingsPinId),
        Provider = settings.EffectiveProvider,
        ProviderModels = ModelCatalogue.ForProvider(settings.EffectiveProvider),
        ThreadModelId = threadModelId,
        ThreadPin = string.IsNullOrEmpty(threadModelId) ? null : DwightConfig.FindModel(threadModelId),
        Posture = posture,
        Resolved = resolved?.Model,
        ApprovalMode = CommandHistory.ThreadApprovalMode(doc, threadId),
        FullControl = CommandHistory.ThreadFullControl(doc, threadId),
        Spend = CommandHistory.ThreadSpend(doc, threadId),
        Receipt = ReceiptFor(doc, threadId),
        StepsPerTurn = settings.StepsPerTurn,
      });
    }

    /// <summary>The posture in force for a thread: its own override, else the Settings one.</summary>
    private static string PostureInForce(string threadPosture, DwightSettings settings) =>
      !string.IsNullOrEmpty(threadPosture) && Array.IndexOf(DwightConfig.Postures, threadPosture) >= 0 ? threadPosture : settings.Posture;

    private static string ModelChoiceOf(GH_Document doc, string threadId) =>
      ChatHeader.CurrentChoice(CommandHistory.ThreadModelId(doc, threadId), PostureInForce(CommandHistory.ThreadPosture(doc, threadId), DwightSettings.Current));

    /// <summary>Brief CW: a change of the chat's model says what it costs against Balanced, once, as chrome. Only the header's model command reaches this, so a redraw never does.</summary>
    private static void SayModelChange(WebPanel wp, GH_Document doc, string threadId, string wasChoice)
    {
      var settings = DwightSettings.Current;
      var threadModelId = CommandHistory.ThreadModelId(doc, threadId);
      var resolved = settings.ResolveChatModel(threadModelId, CommandHistory.ThreadPosture(doc, threadId), out _, out var pinId);
      var settingsPinned = string.IsNullOrEmpty(threadModelId) && !string.IsNullOrEmpty(pinId);
      var line = ChatHeader.ChangeLine(wasChoice, ModelChoiceOf(doc, threadId), resolved?.Model, settings.BalancedChatModel(), settingsPinned);
      if (line != null) AppendPluginHtml(wp, doc, threadId, System.Net.WebUtility.HtmlEncode(line));
    }

    // Brief BI: the last turn's receipt. The line a turn finished with while this session holds it; once Rhino has restarted, the same facts read back from the store.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _lastReceipt = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();

    private static string ReceiptFor(GH_Document doc, string threadId)
    {
      if (threadId != null && _lastReceipt.TryGetValue(threadId, out var held)) return SpendMath.CostHover(held);
      var last = CommandHistory.ThreadLastTurn(doc, threadId);
      if (last == null) return "";
      var name = DwightConfig.FindModel(last.Model)?.DisplayName ?? last.Model;
      if (name != null && name.StartsWith("Claude ", StringComparison.Ordinal)) name = name.Substring(7);
      return SpendMath.CostHover(SpendMath.TurnReceipt(last, name));
    }

    /// <summary>
    /// A turn's one line (Brief I, AK, DL): on its thread, on the central ledger, and as the cost's hover, then the words a stopped turn's number needs. Both ends of a completion come here, so a
    /// stopped turn is written the way a finished one is. The ledger line goes first, so a failed append costs it nothing. Returns what was written, or null when nothing went out.
    /// </summary>
    private static StoredSpend RecordTurnSpend(WebPanel wp, GH_Document doc, string threadId, ModelInfo model, TurnResult result, TimeSpan elapsed)
    {
      StoredSpend spent = null;
      try
      {
        var cost = result.Outcome == TurnOutcome.Failed ? "no answer" : LlmActions.CostLine(model, result.Usage, elapsed);
        // Brief I: what the turn cost survives the session. A turn that failed or was stopped may still have
        // been billed, so any turn that used tokens is kept; the chat's total is computed from the turns.
        var metaLine = AgentLoopCore.Meta(result, cost);
        var u = result.Usage;
        var hasUsage = u != null && (u.TotalInputTokens > 0 || u.OutputTokens > 0);
        spent = TurnSpend.Of(hasUsage ? LlmActions.SpendOf(model, u, elapsed, result.Steps, result.ToolCalls) : null, model, result, elapsed, SpendLedger.ReadSample());
        if (spent != null)
        {
          CommandHistory.AppendThreadSpend(doc, threadId, spent);
          var tokens = SpendMath.TokensLine(spent);
          if (tokens.Length > 0) metaLine += " · " + tokens;
        }
        // Brief BI: the turn's receipt is the cost's hover, and the cost is the thread's total. An estimate's hover is read from the stored turn, which says it is one; the meta line is the usage's alone.
        if (spent?.Estimated != null) _lastReceipt.TryRemove(threadId, out _);
        else if (spent != null || result.Outcome != TurnOutcome.Stopped) _lastReceipt[threadId] = metaLine;
        var line = TurnSpend.StoppedLine(spent, result);
        if (line != null) AppendPluginHtml(wp, doc, threadId, System.Net.WebUtility.HtmlEncode(line));
      }
      catch (Exception ex) { TurnLog("the turn's spend threw: " + ex.Message); }
      return spent;
    }

    private static void RefreshHeader(WebPanel wp, GH_Document doc, string threadId)
    {
      try { wp.SetChatHeader(BuildHeaderJson(doc, threadId)); } catch { }
    }

    /// <summary>
    /// A chip was changed in the header. Every change is VALIDATED here and persisted on the thread, then the header is sent again - the shell
    /// never holds a value the host does not. A card already waiting does not change: its mode was decided when it was raised.
    /// </summary>
    private static void OnHeaderCommand(WebPanel wp, GH_Document doc, string threadId, string cmd)
    {
      try
      {
        if (cmd == null) return;
        if (cmd == "usage") { DwightSettingsPanel.OpenAtUsage(); return; }   // Brief BI: the cost opens Settings at Usage; nothing on the thread changes
        if (cmd.StartsWith("model:", StringComparison.Ordinal))
        {
          var id = cmd.Substring(6);
          var wasChoice = ModelChoiceOf(doc, threadId);
          if (id.StartsWith(ChatHeader.PosturePrefix, StringComparison.Ordinal))
          {
            var posture = id.Substring(ChatHeader.PosturePrefix.Length);
            if (ChatHeader.IsPosture(posture)) CommandHistory.SetThreadModel(doc, threadId, "", posture);
          }
          else if (ChatHeader.IsOffered(id, ModelCatalogue.ForProvider(DwightSettings.Current.EffectiveProvider)))
            CommandHistory.SetThreadModel(doc, threadId, id, "");
          SayModelChange(wp, doc, threadId, wasChoice);
        }
        else if (cmd.StartsWith("approval:", StringComparison.Ordinal))
        {
          var mode = cmd.Substring(9);
          if (ApprovalMode.IsValid(mode))
          {
            var wasMode = CommandHistory.ThreadApprovalMode(doc, threadId);
            CommandHistory.SetThreadApprovalMode(doc, threadId, mode);
            var full = CommandHistory.ThreadFullControl(doc, threadId);
            if (ChatHeader.ScriptsRunNewly(full, wasMode, full, mode)) AppendPluginHtml(wp, doc, threadId, System.Net.WebUtility.HtmlEncode(ChatHeader.ScriptsRunLine));
          }
        }
        else if (cmd.StartsWith("full:", StringComparison.Ordinal))
        {
          var on = cmd.Substring(5) == "on";
          if (cmd.Substring(5) == "on" || cmd.Substring(5) == "off")
          {
            var was = CommandHistory.ThreadFullControl(doc, threadId);
            CommandHistory.SetThreadFullControl(doc, threadId, on);
            // Turning it ON says the Settings sentence once, in the thread, so the transcript carries the moment.
            if (on && !was) AppendPluginHtml(wp, doc, threadId, System.Net.WebUtility.HtmlEncode(ChatHeader.FullControlOnLine));
            var modeNow = CommandHistory.ThreadApprovalMode(doc, threadId);
            if (ChatHeader.ScriptsRunNewly(was, modeNow, on, modeNow)) AppendPluginHtml(wp, doc, threadId, System.Net.WebUtility.HtmlEncode(ChatHeader.ScriptsRunLine));
          }
        }
      }
      catch (Exception ex) { TurnLog("header command threw: " + ex.Message); }
      RefreshHeader(wp, doc, threadId);
      if (cmd != null && cmd.StartsWith("model:", StringComparison.Ordinal)) RedrawChip(wp, doc, threadId);   // the chip's number is the model's
    }

    /// <summary>U90: a run_script turn's clock must outlast the script's own, or the turn would time out under a script that is still within its
    /// allowance. The script clock plus room for the model to read the report and answer.</summary>
    private static TurnLimits RunScriptLimits(RunScriptGrant grant) => new TurnLimits
    {
      MaxSteps = DwightConfig.AgentMaxStepsPerTurn,
      WallClockMs = Math.Max(DwightConfig.AgentTurnWallClockMs, ((grant?.TimeoutSeconds ?? Dwight.Agent.FullControl.DefaultTimeoutSeconds) + 180) * 1000),
      MaxTranscriptChars = DwightConfig.AgentMaxTranscriptChars,
      AnswerBudget = DwightConfig.AgentTurnAnswerBudget,
    };

    private static string StrOf(System.Text.Json.Nodes.JsonObject o, string name)
    {
      try { return o?[name]?.GetValue<string>(); } catch { return null; }
    }

    private static TurnLimits Limits(TurnCapability capability) =>
      capability == TurnCapability.Build
        ? new TurnLimits
        {
          MaxSteps = DwightConfig.AgentBuildMaxStepsPerTurn,
          WallClockMs = DwightConfig.AgentBuildWallClockMs,
          MaxTranscriptChars = DwightConfig.AgentMaxTranscriptChars,
          AnswerBudget = DwightConfig.AgentBuildAnswerBudget,
        }
        : new TurnLimits
        {
          MaxSteps = ChatSteps.Clamp(DwightSettings.Current.StepsPerTurn),   // Brief CN: the person's number. An approved build keeps its own, above
          WallClockMs = DwightConfig.AgentTurnWallClockMs,
          MaxTranscriptChars = DwightConfig.AgentMaxTranscriptChars,
          AnswerBudget = DwightConfig.AgentTurnAnswerBudget,
        };

    /// <summary>
    /// The conversation as the MODEL sees it: built from the persisted thread's message SOURCES, so a
    /// reopened conversation carries on rather than starting over.
    ///
    /// Two properties, both structural rather than remembered. A message with no source is chrome (a
    /// greeting, a notice, a consent card) and is excluded - the model is not asked to reason about
    /// Dwight's own UI. And a turn starts from CHAT HISTORY, never from a previous turn's tool traffic
    /// (#223): stale tool results would be paid for again and would describe a canvas that has moved.
    /// </summary>
    private static List<LlmMessage> ModelTranscript(WebPanel wp, GH_Document doc, string threadId, ModelInfo model)
    {
      var stored = CommandHistory.ThreadMessages(doc, threadId);
      var imagesDir = ImagesDir(doc);
      // Brief DX: the thread's images, read here on the UI thread. An image that cannot go says so in the model's transcript, and to the person once.
      var replay = new ImageReplay
      {
        LoadBase64 = image => { var bytes = ChatImageStore.Load(imagesDir, image); return bytes == null ? null : Convert.ToBase64String(bytes); },
        ModelReadsImages = model == null || model.Vision,
        RuleKnown = model != null && model.ImagePatchTokens,
        ModelName = model?.DisplayName,
      };
      var messages = ChatToTranscript.Build(stored, replay);
      foreach (var why in replay.Omitted)
        if (_saidImageOmission.Add((threadId ?? "") + "|" + why + "|" + model?.Id))
          AppendPluginHtml(wp, doc, threadId, System.Net.WebUtility.HtmlEncode(ChatImage.OmittedLine(why, model?.DisplayName)));

      // #296: an empty transcript is a request that cannot succeed, so say WHY here rather than let the
      // provider tell us. On a fresh thread the greeting is chrome and the user's own message is the only
      // turn - if it is missing, it was never recorded, and the record write is the thing to look at.
      TurnLog("transcript: " + stored.Count + " stored, " + messages.Count + " conversational");
      return messages;
    }

    /// <summary>Brief DX: the lines already said about an image that did not go, so a long thread says each once and not on every turn.</summary>
    private static readonly HashSet<string> _saidImageOmission = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>#290: a mid-turn Stop must name the path it took. The first D5 fix covered two exits and
    /// the field took a third, so the exits now report themselves rather than being reasoned about.</summary>
    private static void TurnLog(string line)
    {
      try { Rhino.RhinoApp.WriteLine("Dwight turn: " + line); } catch { }
    }

    private static void Ui(Action a)
    {
      var app = Eto.Forms.Application.Instance;
      if (app != null) app.AsyncInvoke(a); else a();
    }

    // ---- the three append doors (WP-3 Stage 0) ------------------------------------------------------
    //
    // The chat shell injects a message with insertAdjacentHTML, so every append is an HTML injection
    // point, and from WP-3 one of its authors is a MODEL. ADR-088 rule 1 says model text is rendered
    // through the DisableHtml pipeline - and the way to keep that rule is to make the primitive unable to
    // take raw model text at all. So the caller declares PROVENANCE by choosing a door, and each door
    // owns the escaping its author requires. All three funnel into the one AppendBoth primitive, so
    // #182's R1 property is untouched: screen and record are still written in a single call.

    /// <summary>The user's own words. Escaped: typing HTML into the composer must render as text. The raw
    /// string is kept as the message's SOURCE, so the next turn can hand the model what was actually said
    /// rather than its escaped rendering.</summary>
    internal static void AppendUserText(WebPanel wp, GH_Document doc, string threadId, string text, StoredImage image = null)
    {
      AppendBoth(wp, doc, threadId, "me", System.Net.WebUtility.HtmlEncode(text ?? ""), text ?? "", image: image);
      // Brief BQ: a chat moves to the top when the user speaks in it; an approval's echo is another door and moves nothing. After the append,
      // because this refreshes the dock, and a chat's first message is its name.
      CommandHistory.ThreadSpoke(doc, threadId);
    }

    /// <summary>U111: the line a consent click or an automatic approval adds to the conversation. The model reads it as it reads a typed line; only this door marks
    /// it, so the screen can leave it out after a receipt that states the answer, and what a user types with the same words is never hidden.</summary>
    private static void AppendApprovalEcho(WebPanel wp, GH_Document doc, string threadId, string text) =>
      AppendBoth(wp, doc, threadId, "me", System.Net.WebUtility.HtmlEncode(text ?? ""), text ?? "", echo: true);

    /// <summary>THE FOURTH DOOR (Phase-9 U4): a PLUGIN REPORT - Dwight's own account of something it did
    /// on the user's approval, which the model must read next turn. Plugin-authored HTML for the screen
    /// (rendered as Dwight) and an attributed plain-text source that enters the transcript in the USER
    /// turn (<see cref="ChatToTranscript.Report"/>). Never model text, never user text.</summary>
    /// <summary>
    /// U100: run an approved run_script card from its own record, as ApplySpot applies a spot card and the SetValue route a slider. The
    /// grant's source, purpose and clock; GhRunScript.Run on the UI thread inside a scheduled solution; the report into the thread as the
    /// plugin's line and as the transcript line the next turn reads; then ONE read-only turn, after a script that ran. No model call
    /// sits between the click and the run, and none is made to run it.
    ///
    /// THE CLOCK CANNOT INTERRUPT. The script runs on Rhino's thread and nothing can stop it there, so when the clock runs out first the
    /// line says it is still running (never "stopped"), no follow-up turn reads a canvas mid-change, and the late finish is reported
    /// when it comes. The script holds the UI thread while it runs, so the clock's line reaches the panel only once that thread is free.
    /// </summary>
    private static void RunApprovedScript(WebPanel wp, GH_Document doc, string threadId, RunScriptGrant grant, bool automatic)
    {
      if (!grant.TryUse())
      {
        AppendPluginHtml(wp, doc, threadId, System.Net.WebUtility.HtmlEncode(Dwight.Agent.RunScriptApproval.UsedLine));
        return;
      }
      AppendPluginHtml(wp, doc, threadId, System.Net.WebUtility.HtmlEncode(automatic ? Dwight.Agent.RunScriptApproval.AutoRunLine : Dwight.Agent.RunScriptApproval.ApprovedLine));

      var purpose = grant.Purpose;
      var timeout = grant.TimeoutSeconds;
      var gate = new object();
      var finished = false;
      var overran = false;
      var started = System.Diagnostics.Stopwatch.StartNew();
      System.Threading.Timer clock = null;
      clock = new System.Threading.Timer(_ =>
      {
        lock (gate) { if (finished) return; overran = true; }
        Ui(() => AppendReport(wp, doc, threadId,
          System.Net.WebUtility.HtmlEncode(Dwight.Agent.RunScriptApproval.OverranLine(purpose, timeout)),
          Dwight.Agent.RunScriptApproval.OverranTranscript(purpose, timeout, automatic)));
      }, null, timeout * 1000, System.Threading.Timeout.Infinite);

      GhRunScript.Run(doc, grant.Source, purpose, outcome => Ui(() =>
      {
        bool late;
        lock (gate) { finished = true; late = overran; }
        try { clock?.Dispose(); } catch { }
        if (outcome != null) outcome.Seconds = started.Elapsed.TotalSeconds;
        var line = Dwight.Agent.RunScriptApproval.PanelLine(purpose, outcome);
        if (late)
        {
          var lateLine = Dwight.Agent.FullControl.LateReport(purpose, timeout, outcome?.Seconds ?? 0, line);
          AppendReport(wp, doc, threadId, CardReceipt.ReportLine("<p>" + System.Net.WebUtility.HtmlEncode(lateLine) + "</p>"), lateLine);
          return;
        }
        // Brief BG: the report is a receipt row; its full text is inside, and the transcript line is unchanged.
        AppendReport(wp, doc, threadId, CardReceipt.ReportLine("<p>" + System.Net.WebUtility.HtmlEncode(line) + "</p>"),
                     Dwight.Agent.RunScriptApproval.TranscriptLine(outcome, line, automatic));
        if (Dwight.Agent.RunScriptApproval.FollowUp(outcome, false)) RunTurn(wp, doc, threadId, TurnCapability.ReadOnly);
      }));
    }

    internal static void AppendReport(WebPanel wp, GH_Document doc, string threadId, string html, string src) =>
      AppendBoth(wp, doc, threadId, ChatToTranscript.Report, html, src ?? "");

    /// <summary>MODEL-AUTHORED prose. Untrusted by construction (the canvas feeds the prompt), so it goes
    /// through <see cref="ModelMarkdown"/> - the one place model text becomes HTML - and the Markdown it
    /// was rendered from is kept as the SOURCE.</summary>
    internal static void AppendModelMarkdown(WebPanel wp, GH_Document doc, string threadId, string markdown)
    {
      // #291: the ids the model names become places you can go. Linkify runs on the RENDERED, escaped html
      // - never on model text - so nothing the model wrote can become markup (ADR-088 intact); the only
      // markup added is ours, around digits already checked against the registry.
      var scope = ScopeFor(threadId, doc);
      Func<int, IdState> state = id => scope.Ids.State(id);

      var html = ChatAnchors.Linkify(ModelMarkdown.ToHtml(markdown), state);
      var anchors = ChatAnchors.Extract(markdown, state);

      // #291(3): the per-message re-light. Built HERE rather than in the shell so the shell edit for this
      // feature stays CSS-only - and it is the same "anchor:" command with the message's whole id set, so
      // there is no second mechanism to keep honest. Glow-only on click, like every other anchor.
      if (anchors.Count > 1)
        html += "<a class=\"anch anch-all\" href=\"#\" onclick=\"dwightSend('anchor:" +
                string.Join(",", anchors) + "');return false;\">Show these on canvas</a>";

      AppendBoth(wp, doc, threadId, "dw", html, markdown ?? "", anchors);
    }

    /// <summary>
    /// #291: a click on a grounded id, resolved against the canvas AS IT IS NOW.
    ///
    /// The two dishonest outcomes this exists to make impossible: panning to whatever now occupies a
    /// deleted component's place, and showing fewer components than the message named without saying so.
    /// Both are closed by resolving ids rather than positions - there is simply nowhere wrong to go.
    ///
    /// Bar 4: the re-light is GLOW ONLY. CanvasGlow is an ephemeral read-only overlay; it never touches
    /// Attributes.Selected, so the user's actual selection - and therefore what their next action operates
    /// on - is unchanged by a navigation gesture.
    /// </summary>
    private static void OnAnchorClick(WebPanel wp, GH_Document doc, string threadId, int[] ids)
    {
      try
      {
        var scope = ScopeFor(threadId, doc);
        scope.Ids.Sync(LiveGuids(doc));   // aliveness is never stored; the live document is the only source

        // #331: a click is NAVIGATION, not conversation. Its outcome is a TRANSIENT NOTICE - never a chat
        // message - because appending one would insert a turn nobody spoke into a persisted thread, and
        // that thread is replayed to the model and reopened by the user. Every outcome uses this channel;
        // nothing from a click enters the transcript.
        var r = ChatAnchors.Resolve(ids, id => scope.Ids.State(id));
        if (!string.IsNullOrEmpty(r.Message)) DwightToast.Show(r.Message);
        if (r.Alive.Count == 0) return;   // nothing to frame, so NOTHING MOVES

        var guids = new List<Guid>();
        foreach (var id in r.Alive)
          if (scope.Ids.TryGetGuid(id, out var g) && doc?.FindObject(g, false) != null) guids.Add(g);
        if (guids.Count == 0) return;

        CanvasGlow.Show(guids, false, wp);
        CanvasNav.GoTo(guids, NavReason.UserClicked);   // the click IS the consent for moving the canvas
      }
      catch (Exception ex) { TurnLog("anchor click failed: " + ex.Message); }
    }

    /// <summary>
    /// SHOW-AND-EXPLAIN (ADR-054, Stage E): the user asked what a built region actually does.
    ///
    /// It runs as an ordinary READ-ONLY turn in this thread. ADR-054 asked for "Explain on a region from
    /// inside the chat", and the agent holds every read that answers it - so opening the Explain PANEL
    /// would replace the conversation (#232) to say something the conversation can say itself, and would
    /// be the hand-off #340 rules the agent should never make. A read has no spend-versus-apply question
    /// to settle, so that direction can be honoured here without waiting for the study.
    ///
    /// The request is posted as a visible USER message: the button said what it would do, the message
    /// says the same thing, and the spend rides the turn's meta line - per-message consent (#182) made
    /// visible rather than implied.
    /// </summary>
    /// <summary>
    /// U18: APPLY AN APPROVED SPOT, from the card's own record.
    ///
    /// THE MODEL IS NOT ASKED FOR A SPEC HERE, and that is the design rather than a shortcut. A spot card
    /// names every component, so the components are the scope (#338) - and had the approved turn built
    /// from a spec of its own, the card could have named one set while another was placed, with EVERY
    /// GUARD PASSING AND THE VERDICT TRUE. Nothing in the system reads the card, so nothing would have
    /// noticed. The record IS the spec; a different set is unrepresentable rather than refused.
    ///
    /// THE NOMINATION IS PARSED BY THE BUILD ITSELF, not re-parsed here first. <c>GhBuildRegion.Run</c>
    /// validates with the same parser and the same resolvers it will build from, so the check at the
    /// click and the check at the build cannot differ - a canvas that moved while the card sat (a type
    /// uninstalled, a wire target deleted) refuses there, in its own words, rather than being caught by a
    /// second copy of the rules here that could drift from the first (#248).
    /// </summary>
    /// <summary>U105: an approval the plugin applied from the card's record that did not land (reverted or refused) gets the same one read-only
    /// follow-up a reverted region build gets, when nobody clicked it (AutoFollowUp.AfterApplied). Nobody is there to type, and the plan would
    /// otherwise stop on a report the model has not read.</summary>
    private static void FollowUpIfAuto(WebPanel wp, GH_Document doc, string threadId, bool autoApproved, bool landed)
    {
      if (!AutoFollowUp.AfterApplied(autoApproved, landed)) return;
      try
      {
        AppendPluginHtml(wp, doc, threadId, System.Net.WebUtility.HtmlEncode(AutoFollowUp.Line));
        RunTurn(wp, doc, threadId, TurnCapability.ReadOnly);
      }
      catch (Exception ex) { TurnLog("automatic follow-up threw: " + ex.Message); }
    }

    private static void ApplySpot(WebPanel wp, GH_Document doc, string threadId, SeedPlan plan, bool autoApproved = false)
    {
      var scope = ScopeFor(threadId, doc);

      // #877 (c): A SPOT IS ONE PLACEMENT. If the ledger already holds facts for this address, it has been
      // placed, and this click would place it again. The sibling tokens stop the route we know of (the card's
      // other button); this reads the OUTCOME, so it stops the routes nobody has thought of. Checked first,
      // because when the scope gate below would also refuse, this is the sentence that names the real cause.
      // Read through the document's ledger WITHOUT taking a turn on it - a refused click must not reset
      // anyone's permission.
      var placedAlready = SpotAsk.RefuseIfPlaced(AgentScope.LedgerForDocument(doc), plan.PreFillSpot);
      if (placedAlready != null)
      {
        AppendReport(wp, doc, threadId, SpotAsk.NotAppliedHtml(placedAlready), SpotAsk.NotAppliedText(placedAlready));
        return;
      }

      // THE SCOPE GATE, with the minted spot through its OWN parameter - so "approve an address that is
      // not on the canvas" stays inexpressible for a plan region. It refuses a collision BY NAME rather
      // than renumbering: an address on a card is a promise, and one that moved between the card and the
      // click would have been approved as something else.
      var resolution = BuildScope.Resolve(plan.Regions, LiveRegions(doc, null), plan.PreFillSpot);
      if (!string.IsNullOrEmpty(resolution.Message))
        AppendPluginHtml(wp, doc, threadId, System.Net.WebUtility.HtmlEncode(resolution.Message));
      if (resolution.Outcome == BuildScopeOutcome.Refuse) return;

      if (plan.PreFillPlace == null)
      {
        const string lost = "The placement Dwight offered is no longer on this card, so nothing was built.";
        AppendReport(wp, doc, threadId, SpotAsk.NotAppliedHtml(lost), SpotAsk.NotAppliedText(lost));
        return;
      }

      // A FRESH PERMISSION FOR THIS APPLICATION. LedgerFor keeps the document's FACTS and replaces the
      // turn's AUTHORISATION wholesale, so this grant covers exactly this click and cannot outlive it -
      // the same lifetime rule an approved build turn gets, reached the same way.
      var ledger = scope.LedgerFor(new object());
      ledger.AuthoriseSpot(plan.PreFillSpot, plan.PreFillSpotGrouped, plan.PreFillIntoGroup);

      // The address is the PLUGIN's, added here rather than carried in the nomination - which is refused
      // if it names a region at all, because the model naming its own address would be naming a place
      // that does not exist yet (#581's rule, #763's reason).
      var args = (System.Text.Json.Nodes.JsonObject)plan.PreFillPlace.DeepClone();
      args["region"] = plan.PreFillSpot;

      // U47: THE WHOLE CLICK RUNS INSIDE THE GUARD. The verdict arrives later, from a SolutionEnd handler that
      // Grasshopper swallows exceptions out of; the U19 gate saw a click log APPROVED and then nothing - no report, and
      // the build left standing. Whatever throws, here or in the report below, the user reads a line in Dwight's voice and
      // a build that is still on the canvas is taken back. The revert is conditional on the undo count having risen since
      // the click began, so a build the verdict already reverted is never undone a second time (that would take back the
      // user's own previous step).
      // Brief AP: "standing" is a record-id check - the build's own record is on top of the stack - not a count that any
      // record pushed in between would also raise.
      var noteMark = Dwight.Session.ChangeLog.Mark(doc);
      Func<bool> standing = () => Dwight.Session.ChangeLog.TopIsNotedSince(doc, noteMark);
      Action<string> guardReport = line =>
      {
        AppendReport(wp, doc, threadId, System.Net.WebUtility.HtmlEncode(line), "[Dwight - the build did not land] " + line);
        FollowUpIfAuto(wp, doc, threadId, autoApproved, landed: false);   // U105
      };
      Action revert = () => { Dwight.Session.ChangeLog.Reverting(doc); doc.Undo(); };

      SpotClickGuard.Run(() =>
        GhBuildRegion.Run(doc, scope.Ids, ledger, args, result =>
          SpotClickGuard.Run(() =>
          {
            // #682: A CLICK THAT CHANGES NOTHING IS A REPORT, NOT CHROME. The model's last act was raising
            // this card; a refusal it cannot see would leave it believing the placement ran (#624's lesson).
            if (result != null && result.Ok)
            {
              // Brief CL: what landed, measured against every object, said on both channels when it is true and not otherwise. The receipt folds its text, so the person's line stands on its own.
              var overlap = GhRegionTidy.SpotOverlapNote(doc, plan.PreFillSpot, ledger);
              if (overlap != null) AppendPluginHtml(wp, doc, threadId, System.Net.WebUtility.HtmlEncode(overlap));
              var landed = SpotAsk.PlacedReport(plan.PreFillPlace, plan.PreFillIntoName ?? plan.PreFillSpot, result.Result, plan.PreFillSpotGrouped) + (overlap == null ? "" : " " + overlap);
              // U110: the spot's verdict is a receipt of the region's shape and state words; the transcript line is unchanged.
              AppendReport(wp, doc, threadId, CardReceipt.SpotLine(result, args.ToJsonString(), "<p>" + System.Net.WebUtility.HtmlEncode(landed) + "</p>"), landed);
              RunTurn(wp, doc, threadId, TurnCapability.ReadOnly);   // one re-read, holding no approval
            }
            else
            {
              // U44/U46: a build's own header, and the USER'S reading (no reflected signatures); the transcript
              // line keeps the model-facing message whole.
              // U110: a revert is a receipt too, as a region's is; a refusal stays prose, because the user must read it.
              AppendReport(wp, doc, threadId, CardReceipt.SpotLine(result, args.ToJsonString(), SpotAsk.FailedHtml(result)), SpotAsk.FailedText(result));
              FollowUpIfAuto(wp, doc, threadId, autoApproved, landed: false);   // U105: reverted or refused under an automatic mode
            }
          }, standing, revert, guardReport)),
        standing, revert, guardReport);
    }

    private static void OnExplainRegionClick(WebPanel wp, GH_Document doc, string threadId, string address)
    {
      // Exact or nothing (#208): only a real address reaches the model, so a mangled command can never
      // become a question about some other region.
      // BOTH KINDS (U18): a spot is a region on the canvas a user can click and ask about, exactly as a
      // map region is. Refusing one here would be the surface knowing less than the canvas.
      if (!ScaffoldAddress.IsRegionAddress(address))
      {
        DwightToast.Show("That region address is not one Dwight recognises.");
        return;
      }

      AppendUserText(wp, doc, threadId,
        "Explain what region " + address + " does - what it takes, what it produces, and how.");
      RunTurn(wp, doc, threadId, TurnCapability.ReadOnly);
    }

    private static IEnumerable<Guid> LiveGuids(GH_Document doc) =>
      doc?.Objects?.Where(o => o != null).Select(o => o.InstanceGuid) ?? Enumerable.Empty<Guid>();

    /// <summary>
    /// THE OFFER ON A BUILD RECEIPT (Brief DC ruling 6): a built region's receipt carries "Pin a note on this", which writes the plugin's own receipt sentence
    /// (<see cref="CanvasNotesCore.ReceiptSentence"/>: the address, the count, whether it built with errors, and no word the model wrote) on the region's first
    /// component. SHOWN, NEVER RECORDED: the offer is drawn on the screen only, so a reopened thread cannot carry a button whose token has died, and a note
    /// is never written without a click. A refused or reverted build offers nothing.
    /// </summary>
    private static void OfferNote(WebPanel wp, GH_Document doc, string threadId, string address, RegionOutcome outcome, int placed)
    {
      if (outcome != RegionOutcome.Built && outcome != RegionOutcome.BuiltWithErrors) return;
      try
      {
        var sentence = CanvasNotesCore.ReceiptSentence(address, placed, outcome == RegionOutcome.BuiltWithErrors);
        if (sentence == null) return;
        Guid? first = null;
        if (GhScaffoldMap.TryFindGroup(doc, address, out var group, out _, out _) && group != null && group.ObjectIDs.Count > 0) first = group.ObjectIDs[0];
        var token = NoteOffers.Register(sentence, first, Dwight.Session.DwightNotes.ByDwight, threadId);   // Brief DO: a chat's row is its thread
        if (token == null) return;
        wp?.AppendChat("dw", "<p class=\"hint\"><button class=\"btn sm\" onclick=\"dwightSend('pin:" + token + "');this.disabled=true;this.textContent='Pinned'\">Pin a note on this</button></p>");
      }
      catch { }
    }

    /// <summary>PLUGIN-AUTHORED HTML - a greeting, a notice, a consent card. Dwight's own literal markup,
    /// never a model string. If a caller here is ever tempted to interpolate model text, the value belongs
    /// in <see cref="AppendModelMarkdown"/> instead. NO source: this is chrome, not conversation, so it is
    /// excluded from the model transcript by construction rather than by a filter.</summary>
    internal static void AppendPluginHtml(WebPanel wp, GH_Document doc, string threadId, string html) =>
      AppendBoth(wp, doc, threadId, "dw", html, null);

    /// <summary>The same door, for a plugin line that NAMES components - the graduation's context line
    /// (#365). Still chrome (no <c>Src</c>, so it never enters the model transcript), but its anchors are
    /// recorded so the per-message re-light works and the ids stay honest across time (#291).</summary>
    internal static void AppendPluginHtml(WebPanel wp, GH_Document doc, string threadId, string html,
                                          IReadOnlyList<int> anchors) =>
      AppendBoth(wp, doc, threadId, "dw", html, null, anchors);

    /// <summary>
    /// R1 (#182): the ONE append primitive. Screen and record are written in a single call, so the persisted
    /// thread cannot structurally drift from the on-screen one - and WP-3's real agent inherits this unified
    /// path at birth (it appends its plans and replies through the doors above, never to the screen alone).
    /// PRIVATE from WP-3 Stage 0: reaching it requires choosing a door, which is what makes the escaping
    /// rule structural rather than remembered.
    /// </summary>
    private static void AppendBoth(WebPanel wp, GH_Document doc, string threadId, string who, string html,
                                   string src, IReadOnlyList<int> anchors = null, bool echo = false, StoredImage image = null)
    {
      // #299 applied to the OTHER half of this primitive, found while building the full-path drill: the
      // record write reports a drop and the SCREEN write swallowed one. Both halves can fail, and a
      // silent-failing write is a surface claiming a state the system contradicts, one layer below the
      // surface. The catch stays (guardrail 4 - an append must never crash Rhino); it now says so.
      try
      {
        if (image != null) wp?.AppendChatWithImage(html, ChatTranscript.ImageJson(image));
        else if (echo) wp?.AppendApprovalEcho(html);
        else wp?.AppendChat(who, html);
        // Brief ES unit 1: the person is reading these words, so the canvas wears their numbers. A chat writing behind another page leaves that page's.
        if (wp != null && wp.IsShown) CanvasGlow.PutNumbersOf(wp);
      }
      catch (Exception ex) { TurnLog("SCREEN APPEND THREW - " + ex.Message); }

      // #296: the record write can NO-OP - a document with no key (none open, or no anchor) reaches
      // EnsureThread and gets null back, and the message is discarded in silence. The screen then shows a
      // turn the record never held, which is precisely the drift R1 (#182) exists to make impossible, and
      // it produces an empty transcript on the very next send. Report it rather than let it disappear.
      if (!CommandHistory.AppendThreadMessage(doc, threadId, who, html, src, anchors, echo, image))
        TurnLog("APPEND DROPPED - no per-file record for this document, so the message was shown but not " +
                "recorded (who=" + who + ", thread=" + Short(threadId) + ")");
    }

    /// <summary>SEAM (WP-3): assess intent and lay down the scaffold (pseudo-code, named groups A1.., spaced
    /// layout, scribbles). Returns a scaffold-plan id, or null. Stubbed.</summary>
    internal static string RenderScaffoldPlan(GH_Document doc, string intent) => null;

    /// <summary>SEAM (WP-4): build a scaffold region by group address (e.g. "A1".."A3"). Stubbed.</summary>
    internal static void BuildGroup(GH_Document doc, string address) { /* WP-4 */ }

    private static string RenderThread(GH_Document doc, string threadId)
    {
      var msgs = CommandHistory.ThreadMessages(doc, threadId);
      var json = ChatTranscript.ToChatJson(msgs);
      return json == "[]" ? Greeting() : json;   // an evicted / empty thread still greets, honestly
    }

    private static string GreetingHtml() => ChatChrome.Greeting;

    private static string Greeting() =>
      "[{\"who\":\"dw\",\"html\":\"" + GreetingHtml() + "\"}]";
  }
}
