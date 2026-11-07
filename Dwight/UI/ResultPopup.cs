using System;
using Eto.Drawing;
using Eto.Forms;
using Dwight.Branding;
using Dwight.UI.Drawing;

namespace Dwight.UI
{
  /// <summary>
  /// The read-output surface (ADR-021), Phase-3 brand pass + ADR-045 custom rendering. A
  /// <b>frameless</b> window (no OS/Rhino title bar): its own custom title bar (the pixel-mantis
  /// glyph + action name + close), a custom-drawn Markdown body (<see cref="DocumentView"/>), and a
  /// footer with the model/time/cost line and pill buttons. Draggable by its background
  /// (<c>MovableByWindowBackground</c>). Same public API the actions already call. Drive it from the
  /// UI thread.
  ///
  /// NOTE: the near-cursor thinking chip and the collapse-to-dock recents tray (ADR-046) are the
  /// next build; this is the rendering + frameless proof.
  /// </summary>
  internal sealed class ResultPopup : Form, IResultPanel
  {
    private static Color TitleBg => Brand.Bar;

    private readonly DocumentView _doc = new DocumentView();
    private readonly Label _cost;
    private readonly string _action;
    private string _raw = "";
    private bool _pinned;

    private ResultPopup(string title)
    {
      _action = ActionFrom(title);
      Title = title;
      WindowStyle = WindowStyle.None;          // frameless - no OS title bar (ADR-046 surface)
      // NOTE: MovableByWindowBackground was swallowing every child click (close/Done/Copy/Esc all
      // dead). Disabled so the buttons work; drag is sacrificed for now. The whole frameless/chrome
      // question is being re-decided (Eto custom-draw vs WebView) - see the as-built / ADR.
      ClientSize = new Size(600, 520);
      Resizable = false;
      ShowInTaskbar = false;
      BackgroundColor = Brand.Surface;

      Content = new TableLayout
      {
        BackgroundColor = Brand.Surface,
        Rows =
        {
          new TableRow(BuildTitleBar()),
          new TableRow(new Panel { Height = 1, BackgroundColor = Brand.Divider }),
          new TableRow(BuildBody()) { ScaleHeight = true },
          new TableRow(BuildFooter(out _cost)),
        },
      };

      KeyDown += (s, e) => { if (e.Key == Keys.Escape) { Close(); e.Handled = true; } };
    }

    // ---- chrome ------------------------------------------------------------------------

    private Control BuildTitleBar()
    {
      var titleContent = new Drawable { BackgroundColor = TitleBg, Height = 40 };
      titleContent.Paint += (s, e) => PaintTitle(titleContent, e);

      var close = new IconButton("✕");
      close.Clicked += (s, e) => Close();

      return new StackLayout
      {
        Orientation = Orientation.Horizontal,
        BackgroundColor = TitleBg,
        VerticalContentAlignment = VerticalAlignment.Center,
        Padding = new Padding(0, 0, 5, 0),
        Items = { new StackLayoutItem(titleContent, true), close },
      };
    }

    private void PaintTitle(Drawable bar, PaintEventArgs e)
    {
      var g = e.Graphics;
      g.AntiAlias = true;
      DwightGlyph.Draw(g, 13f, (bar.Height - 18f) / 2f, 18f);

      var font = Brand.Title(12.5f, FontStyle.Bold);
      var ty = (bar.Height - g.MeasureString(font, "Dwight").Height) / 2f;
      var x = 40f;
      g.DrawText(font, Brand.OnSurface, x, ty, "Dwight");
      x += g.MeasureString(font, "Dwight").Width + 5f;
      g.DrawText(font, Brand.Subtle, x, ty, "·");
      x += g.MeasureString(font, "·").Width + 5f;
      g.DrawText(font, Brand.AccentInk, x, ty, _action);
    }

    private Control BuildBody()
    {
      _doc.SetPlain("Thinking…", Brand.Subtle);
      return new Scrollable
      {
        Content = _doc,
        ExpandContentWidth = true,
        Border = BorderType.None,
        BackgroundColor = Brand.Surface,
      };
    }

    private Control BuildFooter(out Label cost)
    {
      cost = new Label { Text = "", TextColor = Brand.Subtle, Font = Brand.Body(11f), VerticalAlignment = VerticalAlignment.Center };

      var pin = new PillButton("Pin");
      pin.Clicked += (s, e) => { _pinned = !_pinned; Topmost = _pinned; pin.Text = _pinned ? "Pinned" : "Pin"; };
      var copy = new PillButton("Copy");
      copy.Clicked += (s, e) => { try { new Clipboard().Text = _raw ?? ""; } catch { } };
      var done = new PillButton("Done", primary: true);
      done.Clicked += (s, e) => Close();

      var inner = new StackLayout
      {
        Orientation = Orientation.Horizontal,
        Spacing = 8,
        VerticalContentAlignment = VerticalAlignment.Center,
        Items = { cost, new StackLayoutItem(null, true), pin, copy, done },
      };
      return new Panel { BackgroundColor = TitleBg, Padding = new Padding(12, 8), Content = inner };
    }

    // ---- show / content ----------------------------------------------------------------

    public static ResultPopup ShowThinking(string title)
    {
      var popup = new ResultPopup(title);
      try
      {
        var p = Mouse.Position;
        popup.Location = new Point((int)p.X + 12, (int)p.Y + 12);
      }
      catch { /* default placement */ }
      popup.Show();
      return popup;
    }

    /// <summary>Render Markdown (Explain output) - custom-drawn (ADR-045).</summary>
    public void SetMarkdown(string markdown) => RunOnUi(() => { _raw = markdown ?? ""; _doc.SetMarkdown(_raw); });

    /// <summary>Show plain text (Reorganise status / simple messages).</summary>
    public void SetContent(string content) => RunOnUi(() => { _raw = content ?? ""; _doc.SetPlain(_raw, Brand.OnSurface); });

    public void SetError(string message) => RunOnUi(() =>
    {
      _raw = message ?? "Something went wrong.";
      _doc.SetPlain(_raw, Brand.Error);
      _cost.Text = "Error";
      _cost.TextColor = Brand.Error;
    });

    /// <summary>The model/time/cost line in the footer (ADR-041).</summary>
    public void SetStatus(string text) => RunOnUi(() => { _cost.Text = text ?? ""; _cost.TextColor = Brand.Subtle; });

    // ---- shared-shell members (the Eto fallback degrades gracefully: no staged strip) ----

    public void SetThinking(string label, bool withStages) => SetContent(label ?? "Thinking…");

    public void SetRunMeta(string text) { /* the Eto fallback loader is plain text; no run-meta line */ }

    public void SetStage(int activeIndex) { /* the Eto fallback has no staged strip */ }



    public void ShowState(string kind, string title, string message, string actionLabel, string actionCmd,
      string actionLabel2 = null, string actionCmd2 = null) => RunOnUi(() =>
    {
      _raw = string.IsNullOrEmpty(title) ? (message ?? "") : (title + "\n\n" + (message ?? ""));
      _doc.SetPlain(_raw, kind == "error" ? Brand.Error : Brand.OnSurface);
      // action buttons (incl. the WP-3 boundary opt-in's two choices) are not rendered in the Eto fallback.
    });

    /// <summary>Brief CS: this surface renders no state buttons, so the question is a plain dialog, with the sentence left in the window behind it.</summary>
    public void AskToContinue(string title, string sentence, Action onContinue, Action onCancel) => RunOnUi(() =>
    {
      _raw = title + "\n\n" + sentence;
      _doc.SetPlain(_raw, Brand.OnSurface);
      if (LargeRunDialog.Ask(this, title, sentence)) onContinue?.Invoke();
      else onCancel?.Invoke();
    });

    public void SetSteps(string stepsJson) => SetContent("Step by step renders in the WebView panel.");

    /// <summary>The fallback degrades to a plain readout of the lead, each chain (root + downstream), and
    /// the stray list (the interactive glow-on-click + collapsible rows live in the WebView panel).</summary>
    public void SetFaults(string faultsJson) => RunOnUi(() =>
    {
      var sb = new System.Text.StringBuilder();
      try
      {
        var root = System.Text.Json.Nodes.JsonNode.Parse(faultsJson);
        var lead = root?["lead"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(lead)) sb.AppendLine(lead).AppendLine();
        var chains = root?["chains"]?.AsArray();
        if (chains != null)
          foreach (var c in chains)
          {
            AppendFault(sb, c?["root"], isRoot: true);
            var members = c?["members"]?.AsArray();
            if (members != null) foreach (var m in members) AppendFault(sb, m, isRoot: false);
            sb.AppendLine();
          }
        var stray = root?["stray"]?.AsArray();
        if (stray != null && stray.Count > 0)
        {
          sb.AppendLine("Not wired in (may be intentional):");
          foreach (var s in stray) sb.AppendLine("  - " + (s?["title"]?.GetValue<string>() ?? "component"));
        }
      }
      catch { sb.Append("Diagnose results render in the WebView panel."); }
      _raw = sb.ToString().TrimEnd();
      _doc.SetPlain(_raw, Brand.OnSurface);
    });

    private static void AppendFault(System.Text.StringBuilder sb, System.Text.Json.Nodes.JsonNode f, bool isRoot)
    {
      if (f == null) return;
      var title = f["title"]?.GetValue<string>() ?? "Component";
      var cause = f["cause"]?.GetValue<string>() ?? "";
      var fix = f["fix"]?.GetValue<string>() ?? "";
      sb.AppendLine((isRoot ? "• " : "   - ") + title + (isRoot ? " (root cause)" : ""));
      if (!string.IsNullOrWhiteSpace(cause)) sb.AppendLine((isRoot ? "   " : "     ") + cause);
      if (!string.IsNullOrWhiteSpace(fix)) sb.AppendLine((isRoot ? "   Fix: " : "     Fix: ") + fix);
    }

    public void SetChat(string messagesJson) => SetContent("The Build chat needs the WebView panel.");
    public void AppendChat(string who, string html) { /* fallback: no live chat */ }

    private static string ActionFrom(string title)
    {
      if (string.IsNullOrEmpty(title)) return "";
      var i = title.IndexOf(" - ", StringComparison.Ordinal);
      return i >= 0 ? title.Substring(i + 3) : title;
    }

    private static void RunOnUi(Action action)
    {
      var app = Application.Instance;
      if (app != null) app.AsyncInvoke(action); else action();
    }
  }
}
