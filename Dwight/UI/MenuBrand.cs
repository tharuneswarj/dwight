using System.Drawing;
using System.Drawing.Text;
using System.Windows.Forms;

namespace Dwight.UI
{
  /// <summary>Brands a native menu tree the Dwight way: the branded renderer + the brand font (falling
  /// back to Segoe UI when the font isn't installed - the brand font files ship in Phase 8) + the themed
  /// fore colour, applied recursively to every submenu. Used by the chord menu (whole tree) and the
  /// top-bar Dwight dropdown (its own dropdown only - never the GH menu bar). Native = instant. The caller
  /// says which theme, so the chord's builder stays GH-free and Eto-free and a test can brand a menu (Brief EU).</summary>
  internal static class MenuBrand
  {
    private static System.Collections.Generic.HashSet<string> _installed;

    public static void Apply(ToolStrip menu, bool dark)
    {
      if (menu == null) return;
      var fore = Dwight.Branding.UiTokens.Text(dark);
      try { menu.Renderer = new DwightMenuRenderer(dark); } catch { }
      try { menu.Font = MenuFont(9f); } catch { }
      menu.ForeColor = fore;
      foreach (ToolStripItem it in menu.Items)
      {
        if ((it.Tag as string) == "dwight-header") { try { it.Font = new Font(menu.Font, FontStyle.Bold); } catch { } }
        if (it is ToolStripMenuItem mi && mi.HasDropDownItems) Apply(mi.DropDown, dark);
      }
    }

    private static Font MenuFont(float size)
    {
      EnsureInstalled();
      foreach (var f in new[] { "Inter", "Segoe UI Variable Text", "Segoe UI" })
        if (_installed.Contains(f)) return new Font(f, size);
      return new Font("Segoe UI", size);
    }

    private static void EnsureInstalled()
    {
      if (_installed != null) return;
      _installed = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
      try { using (var c = new InstalledFontCollection()) foreach (var fam in c.Families) _installed.Add(fam.Name); } catch { }
    }
  }
}
