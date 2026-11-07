using System;
using Eto.Drawing;
using Eto.Forms;
using Dwight.Branding;
using Dwight.Credentials;

namespace Dwight.UI
{
  /// <summary>
  /// Minimal "Set API Key" dialog (Phase 2; the full settings UI is Phase 3). Writes the key
  /// straight to the Windows Credential Manager (ADR-011) - it is never held in a field longer
  /// than needed, logged, or written to disk.
  ///
  /// Layout uses a single-column <see cref="TableLayout"/> so the label and password box fill
  /// the dialog width (a plain DynamicLayout left them as a narrow column in Tharun's test).
  /// </summary>
  internal sealed class ApiKeyDialog : Dialog<bool>
  {
    private readonly PasswordBox _box;
    private readonly string _target;
    private readonly string _label;

    private ApiKeyDialog(string target, string label)
    {
      _target = target;
      _label = label;
      Title = $"Dwight - Set {label} API Key";
      ClientSize = new Size(470, 200);
      Resizable = false;
      BackgroundColor = Brand.Surface;

      var stored = CredentialStore.Exists(_target);
      var info = new Label
      {
        Text = stored
          ? "An API key is already stored. Entering a new one replaces it."
          : $"Paste your {label} API key. It is stored in Windows Credential Manager, never in plaintext.",
        Wrap = WrapMode.Word,
        TextColor = Brand.OnSurface,
        Font = Brand.Body(12f),
      };

      _box = new PasswordBox();

      var ok = new Button { Text = "Save" };
      ok.Click += (s, e) => Save();
      DefaultButton = ok;

      var cancel = new Button { Text = "Cancel" };
      cancel.Click += (s, e) => Close(false);
      AbortButton = cancel;

      var clear = new Button { Text = "Remove stored key", Enabled = stored };
      clear.Click += (s, e) =>
      {
        try { CredentialStore.Delete(_target); } catch { }
        Close(false);
      };

      var buttons = new StackLayout
      {
        Orientation = Orientation.Horizontal,
        Spacing = 6,
        Items = { clear, new StackLayoutItem(null, true), cancel, ok },
      };

      Content = new TableLayout
      {
        Padding = new Padding(12),
        Spacing = new Size(8, 10),
        Rows =
        {
          new TableRow(info),
          new TableRow(_box),
          null,                       // flexible spacer pushes the buttons to the bottom
          new TableRow(buttons),
        },
      };
    }

    private void Save()
    {
      var key = _box.Text?.Trim();
      if (string.IsNullOrEmpty(key))
      {
        MessageBox.Show(this, "Please enter an API key, or Cancel.", "Dwight", MessageBoxButtons.OK);
        return;
      }
      try
      {
        CredentialStore.Save(_target, key);
        Close(true);
      }
      catch (Exception ex)
      {
        MessageBox.Show(this, "Could not store the key: " + ex.Message, "Dwight", MessageBoxButtons.OK);
      }
    }

    /// <summary>Show the key dialog for a specific provider target; returns true if a key was saved.</summary>
    public static bool Run(string target, string label)
    {
      try { return new ApiKeyDialog(target, label).ShowModal(); }
      catch { return false; }
    }

    /// <summary>Back-compat: the Anthropic key dialog.</summary>
    public static bool Run() => Run(CredentialStore.AnthropicTarget, "Anthropic");

    /// <summary>Open the key dialog for the CURRENTLY CONFIGURED provider (WP-1.9) - the "Add your key" button
    /// in a no-key notice must match the provider the user picked, not always Anthropic.</summary>
    public static bool RunForConfiguredProvider()
    {
      var provider = Dwight.Config.DwightSettings.Current?.EffectiveProvider ?? DwightConfig.ProviderAnthropic;
      var target = CredentialStore.TargetFor(provider);
      if (target == null) return Run();   // local / unknown: fall back to the Anthropic dialog (harmless)
      return Run(target, DwightConfig.ProviderLabel(provider));
    }
  }
}
