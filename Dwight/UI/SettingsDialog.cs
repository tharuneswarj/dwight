using System;
using Eto.Drawing;
using Eto.Forms;
using Dwight.Branding;
using Dwight.Config;
using Dwight.Credentials;
using Dwight.UI.Drawing;

namespace Dwight.UI
{
  /// <summary>
  /// The settings panel (ADR-041): a frameless, branded dialog - curated provider/model picker
  /// (Anthropic now; the seam is ready for others), an honest per-action cost/speed estimate, and
  /// API-key management. The invocation trigger is fixed (Ctrl+Shift+Right-click, ADR-047), shown as
  /// info, not a choice. Caching is automatic and invisible; the estimate shows only model + a plain
  /// cost/speed.
  /// </summary>
  internal sealed class SettingsDialog : Dialog<bool>
  {
    // R11: these were dark whatever the theme, so in light a user could not read what they typed (1.18:1).
    private static Color TitleBg => Brand.Bar;
    private static Color FieldBg => Brand.SurfaceDeep;

    private readonly DropDown _explainModel = NewDrop();
    private readonly DropDown _reorganiseModel = NewDrop();
    // Brief AL: the same three-way approval choice the Settings panel has, with the same sentences (ApprovalMode owns them).
    private readonly DropDown _approval = NewDrop();
    // Full control (U90): the same switch, the same sentence (FullControl owns it).
    private readonly CheckBox _fullControl = new CheckBox { Text = Dwight.Agent.FullControl.SettingLabel, TextColor = Brand.OnSurface, Font = Brand.Body(12.5f) };
    private readonly Label _explainEstimate = Hint("");
    private readonly Label _reorganiseEstimate = Hint("");
    private readonly Label _keyStatus = new Label { Font = Brand.Body(12.5f) };
    private readonly Drawable _keyDot = Dot(Brand.Warning);

    private SettingsDialog()
    {
      Title = "Dwight - Settings";
      WindowStyle = WindowStyle.None;
      // MovableByWindowBackground removed - it swallowed child clicks (close/Save/Cancel dead).
      ClientSize = new Size(480, 760);
      Resizable = false;
      BackgroundColor = Brand.Surface;

      var s = DwightSettings.Current;

      var provider = NewDrop();
      provider.Items.Add("Anthropic");
      provider.SelectedIndex = 0;
      provider.Enabled = false;

      foreach (var m in DwightConfig.AnthropicModels) { _explainModel.Items.Add(m.DisplayName); _reorganiseModel.Items.Add(m.DisplayName); }
      _explainModel.SelectedIndex = IndexOf(s.ExplainModelId);
      _reorganiseModel.SelectedIndex = IndexOf(s.ReorganiseModelId);
      _explainModel.SelectedIndexChanged += (o, e) => UpdateEstimates();
      _reorganiseModel.SelectedIndexChanged += (o, e) => UpdateEstimates();
      UpdateEstimates();
      foreach (var mode in Dwight.Agent.ApprovalMode.All) _approval.Items.Add(Dwight.Agent.ApprovalMode.Label(mode));
      _approval.SelectedIndex = Math.Max(0, Array.IndexOf(Dwight.Agent.ApprovalMode.All, Dwight.Agent.ApprovalMode.Normalise(s.ApprovalMode)));
      _approval.SelectedIndexChanged += (o, e) => UpdateApprovalHint();
      UpdateApprovalHint();
      _fullControl.Checked = s.FullControl;
      RefreshKeyStatus();

      var setKey = new PillButton("Set / replace key");
      setKey.Clicked += (o, e) => { ApiKeyDialog.Run(); RefreshKeyStatus(); };
      var clearKey = new PillButton("Remove");
      clearKey.Clicked += (o, e) => { try { CredentialStore.Delete(CredentialStore.AnthropicTarget); } catch { } RefreshKeyStatus(); };

      var ok = new PillButton("Save", primary: true);
      ok.Clicked += (o, e) => Save(s);
      var cancel = new PillButton("Cancel");
      cancel.Clicked += (o, e) => Close(false);

      var body = new StackLayout
      {
        Orientation = Orientation.Vertical,
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        Spacing = 6,
        Padding = new Padding(18, 14),
        Items =
        {
          // Brief BC: the web panel's order - provider and key, the Build controls, Usage, then the shortcut - with the same short
          // labels, and every explanation as a tooltip on its control, since this dialog has no info icons.
          GroupHeader("Provider & models"),
          Row("Provider", provider),
          Row("Explain  (read)", _explainModel),
          _explainEstimate,
          Row("Reorganise  (edit)", _reorganiseModel),
          _reorganiseEstimate,
          Spacer(10),
          GroupHeader("API key"),
          Row("Status", new StackLayout { Orientation = Orientation.Horizontal, Spacing = 8, VerticalContentAlignment = VerticalAlignment.Center, Items = { _keyDot, _keyStatus } }),
          new StackLayout { Orientation = Orientation.Horizontal, Spacing = 8, Items = { setKey, clearKey } },
          Spacer(10),
          WithTip(GroupHeader("Build"), Dwight.Agent.ChatHeader.SettingsHint),
          Row("Approval", _approval),
          WithTip(_fullControl, Dwight.Agent.FullControl.SettingSentence),
        },
      };
      AddUsage(body);
      body.Items.Add(Spacer(10));
      body.Items.Add(GroupHeader("Invocation"));
      body.Items.Add(WithTip(Row("Pop-up trigger", Field("Ctrl + Shift + Right-click")), "The only trigger - leaves Grasshopper's native menu intact."));

      var footerInner = new StackLayout
      {
        Orientation = Orientation.Horizontal, Spacing = 8,
        Items = { new StackLayoutItem(null, true), cancel, ok },
      };
      var footer = new Panel { BackgroundColor = Brand.Bar, Padding = new Padding(14, 10), Content = footerInner };

      Content = new TableLayout
      {
        BackgroundColor = Brand.Surface,
        Rows =
        {
          new TableRow(TitleBar()),
          new TableRow(new Panel { Height = 1, BackgroundColor = Brand.Divider }),
          new TableRow(body) { ScaleHeight = true },
          new TableRow(footer),
        },
      };

      KeyDown += (s2, e) => { if (e.Key == Keys.Escape) { Close(false); e.Handled = true; } };
    }

    /// <summary>Brief AK: the four period totals as text and the two buttons, nothing more. The wording is
    /// <see cref="Dwight.Session.SpendLedgerCore.PeriodLine"/>, the same words the tests pin.</summary>
    private void AddUsage(StackLayout body)
    {
      body.Items.Add(Spacer(10));
      body.Items.Add(GroupHeader("Usage"));
      try
      {
        var path = Dwight.Session.SpendLedger.LedgerPath;
        var sum = Dwight.Session.SpendLedgerCore.Summarise(Dwight.Session.SpendLedgerCore.ReadLines(path), DateTime.Now);
        foreach (var p in Dwight.Session.LedgerSummary.Periods)
          body.Items.Add(new Label { Text = Dwight.Session.SpendLedgerCore.PeriodLine(sum.Total(p)), TextColor = Brand.OnSurface, Font = Brand.Body(11.5f) });
      }
      catch { body.Items.Add(Hint(Dwight.Session.SpendLedgerCore.UnreadableLine)); }

      var export = new PillButton("Export CSV...");
      export.Clicked += (o, e) =>
      {
        using (var dlg = new SaveFileDialog { Title = "Export your spend", FileName = "dwight-spend.csv" })
        {
          dlg.Filters.Add(new FileFilter("CSV", ".csv"));
          if (dlg.ShowDialog(this) != DialogResult.Ok || string.IsNullOrEmpty(dlg.FileName)) return;
          try { Dwight.Session.SpendLedgerCore.ExportCsv(Dwight.Session.SpendLedger.LedgerPath, dlg.FileName); } catch { }
        }
      };
      var folder = new PillButton("Open folder");
      folder.Clicked += (o, e) => DwightSettingsPanel.OpenLedgerFolder();
      body.Items.Add(new StackLayout { Orientation = Orientation.Horizontal, Spacing = 8, Items = { export, folder } });
    }

    private Control TitleBar()
    {
      var content = new Drawable { BackgroundColor = TitleBg, Height = 40 };
      content.Paint += (s, e) =>
      {
        var g = e.Graphics; g.AntiAlias = true;
        DwightGlyph.Draw(g, 13f, (content.Height - 18f) / 2f, 18f);
        var font = Brand.Title(12.5f, FontStyle.Bold);
        var ty = (content.Height - g.MeasureString(font, "Dwight").Height) / 2f;
        var x = 40f;
        g.DrawText(font, Brand.OnSurface, x, ty, "Dwight");
        x += g.MeasureString(font, "Dwight").Width + 5f;
        g.DrawText(font, Brand.Subtle, x, ty, "·");
        x += g.MeasureString(font, "·").Width + 5f;
        g.DrawText(font, Brand.AccentInk, x, ty, "Settings");
      };
      var close = new IconButton("✕");
      close.Clicked += (s, e) => Close(false);
      return new StackLayout
      {
        Orientation = Orientation.Horizontal, BackgroundColor = TitleBg, VerticalContentAlignment = VerticalAlignment.Center,
        Padding = new Padding(0, 0, 5, 0),
        Items = { new StackLayoutItem(content, true), close },
      };
    }

    private void Save(DwightSettings s)
    {
      s.ExplainModelId = ModelIdAt(_explainModel.SelectedIndex, DwightConfig.DefaultExplainModel.Id);
      s.ReorganiseModelId = ModelIdAt(_reorganiseModel.SelectedIndex, DwightConfig.DefaultReorganiseModel.Id);
      // WP-1.9: do NOT force Provider back to Anthropic here - this Eto fallback only edits the legacy per-action
      // model ids, and clobbering the provider silently broke an OpenAI/Google/Ollama user. Full provider
      // reconciliation of the Eto dialog stays carried debt; the destructive write is removed.
      s.ApprovalMode = ApprovalModeAt(_approval.SelectedIndex);
      s.FullControl = _fullControl.Checked == true;
      s.Save();
      Close(true);
    }

    private static string ApprovalModeAt(int index) =>
      index >= 0 && index < Dwight.Agent.ApprovalMode.All.Length ? Dwight.Agent.ApprovalMode.All[index] : Dwight.Agent.ApprovalMode.Ask;

    /// <summary>Brief BC: the chosen mode's sentence and the thread line, as the dropdown's tooltip (the web panel shows them behind its info icon).</summary>
    private void UpdateApprovalHint() =>
      _approval.ToolTip = Dwight.Agent.ApprovalMode.Description(ApprovalModeAt(_approval.SelectedIndex)) + " " + Dwight.Agent.ApprovalMode.ThreadHint;

    /// <summary>A control with its explanation as a tooltip.</summary>
    private static T WithTip<T>(T control, string tip) where T : Control { control.ToolTip = tip; return control; }

    private void UpdateEstimates()
    {
      _explainEstimate.Text = Estimate(ModelAt(_explainModel.SelectedIndex, DwightConfig.DefaultExplainModel), 4000, 500);
      _reorganiseEstimate.Text = Estimate(ModelAt(_reorganiseModel.SelectedIndex, DwightConfig.DefaultReorganiseModel), 9000, 1500);
    }

    private static string Estimate(ModelInfo m, int typicalIn, int typicalOut)
    {
      var cents = (typicalIn / 1_000_000.0 * m.InputPerMTok + typicalOut / 1_000_000.0 * m.OutputPerMTok) * 100.0;
      return $"{m.SpeedNote} · ~{cents:0.0}¢ per action (typical; usually less with caching)";
    }

    private void RefreshKeyStatus()
    {
      var has = false;
      try { has = CredentialStore.Exists(CredentialStore.AnthropicTarget); } catch { }
      _keyStatus.Text = has ? "Stored in Windows Credential Manager" : "Not set";
      _keyStatus.TextColor = has ? Brand.OnSurface : Brand.Subtle;
      _keyDot.BackgroundColor = Colors.Transparent;
      _keyDotColor = has ? Brand.Success : Brand.Warning;
      _keyDot.Invalidate();
    }

    private static int IndexOf(string modelId)
    {
      for (var i = 0; i < DwightConfig.AnthropicModels.Count; i++)
        if (string.Equals(DwightConfig.AnthropicModels[i].Id, modelId, StringComparison.OrdinalIgnoreCase)) return i;
      return 0;
    }

    private static ModelInfo ModelAt(int index, ModelInfo fallback) =>
      index >= 0 && index < DwightConfig.AnthropicModels.Count ? DwightConfig.AnthropicModels[index] : fallback;

    private static string ModelIdAt(int index, string fallback) =>
      index >= 0 && index < DwightConfig.AnthropicModels.Count ? DwightConfig.AnthropicModels[index].Id : fallback;

    // ---- small branded building blocks ----

    private static DropDown NewDrop() => new DropDown { BackgroundColor = FieldBg, TextColor = Brand.OnSurface, Font = Brand.Body(12.5f) };

    private static Label GroupHeader(string text) => new Label
    {
      Text = text.ToUpperInvariant(), Font = Brand.Title(11f, FontStyle.Bold), TextColor = Brand.AccentInk,
    };

    private static Control Row(string label, Control field) => new TableLayout
    {
      Spacing = new Size(12, 0),
      Rows = { new TableRow(
        new TableCell(new Label { Text = label, TextColor = Brand.OnSurface, Font = Brand.Body(12.5f), VerticalAlignment = VerticalAlignment.Center }) { ScaleWidth = false },
        new TableCell(field, true)) },
    };

    private static Label Hint(string text) => new Label { Text = text, TextColor = Brand.Subtle, Font = Brand.Body(10.5f) };

    private static Control Field(string text) => new Panel
    {
      BackgroundColor = FieldBg, Padding = new Padding(10, 7),
      Content = new Label { Text = text, TextColor = Brand.OnSurface, Font = Brand.Body(12.5f) },
    };

    private static Control Spacer(int h) => new Panel { Height = h };

    private static Color _keyDotColor = Brand.Warning;

    private static Drawable Dot(Color color)
    {
      _keyDotColor = color;
      var d = new Drawable { Width = 14, Height = 14 };
      d.Paint += (s, e) =>
      {
        var g = e.Graphics; g.AntiAlias = true;
        g.FillEllipse(new Color(_keyDotColor, 0.25f), 0, 0, 14, 14);
        g.FillEllipse(_keyDotColor, 3.5f, 3.5f, 7, 7);
      };
      return d;
    }

    /// <summary>Show the settings dialog; returns true if saved.</summary>
    public static bool Run()
    {
      try { return new SettingsDialog().ShowModal(); }
      catch { return false; }
    }
  }
}
