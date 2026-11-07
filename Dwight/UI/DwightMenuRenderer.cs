using System.Windows.Forms;
using Dwight.Branding;

namespace Dwight.UI
{
  /// <summary>Branded paint for the native menus (Phase-10 reskin): sand surface, AMBER-tint hover,
  /// themed text + border - all hexes from <see cref="UiTokens"/>, the C# mirror of the CSS token
  /// layer, so the native menus and the WebView shells share one palette. Applied to the chord menu,
  /// its submenus, and the top-bar Dwight dropdown (via <see cref="MenuBrand"/>). Native = instant;
  /// rows are square (the drawing-set look - and a WinForms ToolStrip limit that now works FOR the
  /// brand). Takes the theme at construction.</summary>
  internal sealed class DwightMenuRenderer : ToolStripProfessionalRenderer
  {
    private readonly bool _d;
    public DwightMenuRenderer(bool dark) : base(new Colors(dark)) { _d = dark; }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
      using (var b = new System.Drawing.SolidBrush(UiTokens.Surface(_d)))
        e.Graphics.FillRectangle(b, new System.Drawing.Rectangle(System.Drawing.Point.Empty, e.ToolStrip.Size));
    }
    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
      using (var p = new System.Drawing.Pen(UiTokens.BorderStrong(_d)))
        e.Graphics.DrawRectangle(p, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
    }
    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
      if ((e.Item.Tag as string) == "dwight-header")            // the "Dwight" header: bright brand amber, not greyed
        e.TextColor = UiTokens.BrandAccent(_d);
      else
        e.TextColor = e.Item.Enabled ? UiTokens.Text(_d) : UiTokens.Disabled(_d);
      base.OnRenderItemText(e);
    }

    private sealed class Colors : ProfessionalColorTable
    {
      private readonly bool _d;
      public Colors(bool dark) { _d = dark; }
      private System.Drawing.Color Bg  => UiTokens.Surface(_d);
      private System.Drawing.Color Sel => UiTokens.HoverTint(_d);   // amber-tint hover
      public override System.Drawing.Color MenuItemSelected => Sel;
      public override System.Drawing.Color MenuItemSelectedGradientBegin => Sel;
      public override System.Drawing.Color MenuItemSelectedGradientEnd => Sel;
      public override System.Drawing.Color ToolStripDropDownBackground => Bg;
      public override System.Drawing.Color ImageMarginGradientBegin => Bg;
      public override System.Drawing.Color ImageMarginGradientMiddle => Bg;
      public override System.Drawing.Color ImageMarginGradientEnd => Bg;
    }
  }
}
