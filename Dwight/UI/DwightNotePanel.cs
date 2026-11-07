using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Eto.Drawing;
using Eto.Forms;
using Grasshopper;
using Grasshopper.Kernel;
using Dwight.Agent;
using Dwight.Branding;
using Dwight.Session;

namespace Dwight.UI
{
  /// <summary>
  /// The note editor (Brief DC, ruling 4 and 5): a small panel on the shared WebView shell, built the way the Report-a-problem panel is. It shows the
  /// text, Save, Delete (only for a note that exists; the words say it is gone for good) and "Ask Dwight about this". It writes through the per-file
  /// store and nothing else: a note changes no definition, so there is no consent card and no undo record (ruling 8).
  ///
  /// A new note is opened empty from the chord ("Note here") or pinned from an offer; saving makes it with <see cref="DwightNotes.TryMake"/>, whose
  /// bound and sentence are the plugin's. Nothing is ever written without a click on Save (or on a "Pin" offer).
  /// </summary>
  internal sealed class DwightNotePanel : Form
  {
    private static DwightNotePanel _current;
    private readonly WebView _web;
    private readonly List<string> _pending = new List<string>();
    private bool _loaded;

    private readonly GH_Document _doc;
    private readonly string _noteId;      // null for a new note
    private readonly Guid? _anchor;       // for a new note
    private readonly System.Drawing.PointF _point;       // for a new note: the free point, in document coordinates

    /// <summary>Open the editor on an existing note.</summary>
    public static void Open(GH_Document doc, StoredNote note) => Show(new DwightNotePanel(doc, note.Id, null, default));

    /// <summary>Open the editor empty, for a new note anchored to <paramref name="anchor"/> or, with none, at <paramref name="point"/>.</summary>
    public static void OpenNew(GH_Document doc, Guid? anchor, System.Drawing.PointF point) => Show(new DwightNotePanel(doc, null, anchor, point));

    private static void Show(DwightNotePanel p)
    {
      try { _current?.Close(); } catch { }
      try
      {
        _current = p;
        p.Closed += (s, e) => { if (_current == p) _current = null; };
        WindowPlacer.PlaceCentred(p, "note, before show");
        p.Show();
        NativeWindowEffects.ApplyFloating(p);
        NativeWindowEffects.SetGhOwner(p);
        WindowPlacer.PlaceCentred(p, "note, after show");
      }
      catch { _current = null; }   // no WebView2: the note is still drawn on the canvas, it just cannot be edited here
    }

    private DwightNotePanel(GH_Document doc, string noteId, Guid? anchor, System.Drawing.PointF point)
    {
      if (!WebViewSupport.IsWebView2Available())
        throw new InvalidOperationException("WebView2 runtime is not available");
      _doc = doc; _noteId = noteId; _anchor = anchor; _point = point;

      WindowStyle = WindowStyle.None; ShowInTaskbar = false; Topmost = false; Resizable = false;
      ClientSize = new Size(380, 440);   // Brief FN: every state it opens in fits with nothing to scroll at 100, 125 and 150 percent (tools/check-window-fit.js); at 360 the about line sat under the footer
      BackgroundColor = DwightTheme.Current() == "dark" ? Brand.Dark : Brand.White;

      WebViewSupport.ForceWebView2();
      _web = new WebView { BrowserContextMenuEnabled = false };
      WebViewSupport.HardenNavigation(_web);
      _web.DocumentLoaded += (s, e) =>
      {
        _loaded = true;
        WindowPlacer.PlaceCentred(this, "note, page loaded");
        foreach (var js in _pending) Exec(js);
        _pending.Clear();
        PushInit();
      };
      _web.MessageReceived += (s, e) => Handle(e.Message);
      _web.DocumentTitleChanged += (s, e) => { var t = e.Title; if (!string.IsNullOrEmpty(t) && t.StartsWith("dwight:", StringComparison.Ordinal)) Handle(t.Substring(7)); };
      Content = _web;

      var html = ShellAssets.Inline(WebViewSupport.LoadResource("Dwight.Note.html"));
      if (html != null) _web.LoadHtml(html, null);
      KeyDown += (s, e) => { if (e.Key == Keys.Escape) Close(); };

      Action<string> onTheme = t => Run($"dwightSetTheme({Js(t)})");
      DwightTheme.Changed += onTheme;
      Closed += (s, e) => DwightTheme.Changed -= onTheme;
    }

    private StoredNote Existing => _noteId == null ? null : CommandHistory.Notes(_doc).FirstOrDefault(n => n.Id == _noteId);

    private void PushInit()
    {
      var note = Existing;
      var init = new JsonObject
      {
        ["head"] = note == null ? "New note" : (note.By == DwightNotes.ByDwight ? "A note Dwight wrote" : "Your note"),
        ["text"] = note?.Text ?? "",
        ["html"] = note != null ? ModelMarkdown.ToHtml(note.Text) : null,   // ADR-088: a note's text is untrusted, and this is the one place text becomes HTML
        ["about"] = About(note),
        ["canDelete"] = note != null,
        ["canAsk"] = true,
        ["applied"] = DwightTheme.Current(),
      };
      if (note != null)
      {
        var stale = Stale(note);
        if (stale != null) init["stale"] = stale;
        init["pin"] = PinState(note);
      }
      Run($"dwightNoteInit({Js(init.ToJsonString())})");
    }

    private string Stale(StoredNote note)
    {
      var sentence = NoteState.Sentence(NoteState.Of(note, AnchorPresent(note), CurrentStamp(note)));
      return sentence == null ? null : char.ToUpperInvariant(sentence[0]) + sentence.Substring(1) + ".";
    }

    /// <summary>Brief ED: the switch, in the plugin's words: "Pinned to" the component's nickname, or "Free". A note re-pinned has its new component's stamp, so the stale line goes with the old one.</summary>
    private JsonObject PinState(StoredNote note)
    {
      var pinned = NotePlace.Pinned(note);
      var obj = pinned && Guid.TryParse(note.Anchor, out var g) ? _doc.FindObject(g, false) : null;
      return new JsonObject { ["on"] = pinned, ["label"] = NotePlace.PinLabel(pinned, obj?.NickName), ["stale"] = Stale(note) };
    }

    /// <summary>
    /// The switch was pressed. Off: the note is free where it stands. On: it is pinned to the first selected component where it stands, its offset worked out so the card does
    /// not move, or with nothing selected, refused in the plugin's words. Either way one write through the per-file store, and the switch is the host's to move.
    /// </summary>
    private void Pin(bool on)
    {
      var note = Existing;
      var origin = note == null ? null : CanvasNotes.Origin(_doc, note);
      if (origin == null) { Run($"dwightSetStatus({Js("That note is no longer there.")})"); return; }
      string refusal = null;
      if (NotePlace.Pinned(note) != on)
      {
        if (!on) CommandHistory.PlaceNote(_doc, note.Id, NotePlace.Unpinned(origin.Value.X, origin.Value.Y));
        else
        {
          var anchor = CanvasNotesCore.AnchorFor(DwightActions.CurrentSelection().Select(o => o.InstanceGuid).ToList());
          var obj = anchor.HasValue ? _doc.FindObject(anchor.Value, false) : null;
          var b = obj?.Attributes?.Bounds;
          if (b == null) refusal = NotePlace.PinRefusal;
          else
          {
            string stamp = null;
            try { stamp = GhAgent.StampOf(obj)?.Digest; } catch { }
            CommandHistory.PlaceNote(_doc, note.Id, NotePlace.PinnedTo(anchor.Value, (b.Value.X, b.Value.Y), origin.Value.X, origin.Value.Y, stamp));
          }
        }
      }
      Run($"dwightNotePin({Js(PinState(Existing ?? note).ToJsonString())})");
      Run($"dwightSetStatus({Js(refusal)})");
    }

    private string About(StoredNote note)
    {
      if (note == null)
        return _anchor.HasValue ? "This note is about the selected component. It stays until you delete it."
                                : "This note sits at a point on the canvas. It stays until you delete it.";
      return "A deleted note is gone: Dwight does not keep it. A note is kept in Dwight's own record for this file, never in the definition.";
    }

    private bool AnchorPresent(StoredNote note) => Guid.TryParse(note.Anchor, out var g) && _doc.FindObject(g, false) != null;

    private string CurrentStamp(StoredNote note)
    {
      try { return Guid.TryParse(note.Anchor, out var g) ? GhAgent.StampOf(_doc.FindObject(g, false))?.Digest : null; } catch { return null; }
    }

    private void Handle(string cmd)
    {
      var app = Application.Instance;
      Action act = () =>
      {
        try
        {
          if (cmd == "close") { Close(); return; }
          if (cmd.StartsWith("save:", StringComparison.Ordinal)) { Save(cmd.Substring(5)); return; }
          if (cmd == "delete") { CommandHistory.DeleteNote(_doc, _noteId); Close(); return; }
          if (cmd == "ask") { Ask(); return; }
          if (cmd.StartsWith("pin:", StringComparison.Ordinal)) { Pin(cmd.StartsWith("pin:on", StringComparison.Ordinal)); return; }   // the title fallback may trail a timestamp
        }
        catch { }
      };
      if (app != null) app.AsyncInvoke(act); else act();
    }

    private void Save(string text)
    {
      if (_noteId != null)
      {
        if (!CommandHistory.EditNote(_doc, _noteId, text, out var refusal)) { Run($"dwightSetStatus({Js(refusal)})"); return; }
        Close();
        return;
      }

      var made = MakeNew(_doc, _anchor, _point, text, DwightNotes.ByYou, null, out var why);
      if (made == null) { Run($"dwightSetStatus({Js(why)})"); return; }
      CanvasNotes.Reveal(made.Id);
      CommandHistory.AddNote(_doc, made);
      Close();
    }

    /// <summary>
    /// Make a new note about a component or a point: an anchored note carries its offset from the component's corner (below it, by
    /// <see cref="CanvasNotesCore.FirstGap"/>), the place it was first put (so it is still drawn there if the component goes), and the component's stamp.
    /// Null, with the plugin's sentence in <paramref name="refusal"/>, when the text is refused.
    /// </summary>
    internal static StoredNote MakeNew(GH_Document doc, Guid? anchor, System.Drawing.PointF point, string text, string by, string fromRun, out string refusal)
    {
      refusal = null;
      var obj = anchor.HasValue ? doc.FindObject(anchor.Value, false) : null;
      var b = obj?.Attributes?.Bounds;
      string stamp = null;
      double ox = point.X, oy = point.Y, lx = point.X, ly = point.Y;
      if (obj != null && b != null)
      {
        ox = 0; oy = b.Value.Height + CanvasNotesCore.FirstGap;
        lx = b.Value.X + ox; ly = b.Value.Y + oy;
        try { stamp = GhAgent.StampOf(obj)?.Digest; } catch { }
      }
      else anchor = null;   // the component is not here: the note is about the point

      if (!DwightNotes.TryMake(text, by, anchor, ox, oy, fromRun, stamp, DateTime.UtcNow, out var note, out refusal)) return null;
      note.Last = new[] { lx, ly };
      return note;
    }

    private void Ask()
    {
      var note = Existing;
      var text = note?.Text ?? "";
      if (note != null && Guid.TryParse(note.Anchor, out var g))
      {
        var obj = _doc.FindObject(g, false);
        if (obj != null)
        {
          foreach (var o in _doc.SelectedObjects().ToList()) o.Attributes.Selected = false;
          obj.Attributes.Selected = true;
          Instances.ActiveCanvas?.Invalidate();
        }
      }
      Close();
      DwightScaffold.OpenWithDraft(text);
    }

    private void Run(string js) { var app = Application.Instance; Action a = () => { if (_loaded) Exec(js); else _pending.Add(js); }; if (app != null) app.AsyncInvoke(a); else a(); }
    private void Exec(string js) { try { _web.ExecuteScript(js); } catch { } }
    private static string Js(string s) => JsonSerializer.Serialize(s ?? "");
  }
}
