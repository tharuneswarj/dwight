using Eto.Drawing;
using Eto.Forms;
using Dwight.Branding;
using Dwight.Session;

namespace Dwight.UI
{
  /// <summary>Brief CS: the ask on the Eto fallback, which renders no state buttons. The same sentence and the same two words
  /// as the panel, and Enter takes Cancel, since it is the paid answer that should need a deliberate click.</summary>
  internal sealed class LargeRunDialog : Dialog<bool>
  {
    private LargeRunDialog(string title, string sentence)
    {
      Title = "Dwight - " + title;
      ClientSize = new Size(470, 130);
      Resizable = false;
      BackgroundColor = Brand.Surface;

      var info = new Label
      {
        Text = sentence,
        Wrap = WrapMode.Word,
        TextColor = Brand.OnSurface,
        Font = Brand.Body(12f),
      };

      var go = new Button { Text = LargeRun.ContinueLabel };
      go.Click += (s, e) => Close(true);

      var cancel = new Button { Text = LargeRun.CancelLabel };
      cancel.Click += (s, e) => Close(false);
      DefaultButton = cancel;
      AbortButton = cancel;

      var buttons = new StackLayout
      {
        Orientation = Orientation.Horizontal,
        Spacing = 6,
        Items = { new StackLayoutItem(null, true), cancel, go },
      };

      Content = new TableLayout
      {
        Padding = new Padding(12),
        Spacing = new Size(8, 10),
        Rows =
        {
          new TableRow(info) { ScaleHeight = true },
          new TableRow(buttons),
        },
      };
    }

    /// <summary>True on Continue. A dialog that cannot open has not been answered, so it reads as Cancel: nothing is spent.</summary>
    public static bool Ask(Window owner, string title, string sentence)
    {
      try { return new LargeRunDialog(title, sentence).ShowModal(owner); }
      catch { return false; }
    }
  }
}
