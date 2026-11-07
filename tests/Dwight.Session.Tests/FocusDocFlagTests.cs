using System;
using System.IO;
using System.Linq;
using Dwight.UI;
using Xunit;

namespace Dwight.Session.Tests
{
  /// <summary>
  /// The eleventh gate, on Focus mode: opening the floating view set RhinoDoc.Modified true, reproducibly, with one floating Perspective view open and nothing edited; closing it set it true again.
  /// Rhino treats a view as part of the document, and a person would be asked to save a file they had not touched. THE LAW: Focus never leaves a document marked modified that was not, and a document already
  /// modified stays so. The rule is GH-free and held here, and the Rhino proof is the twelfth gate's.
  /// </summary>
  public class FocusDocFlagTests
  {
    // ---- the rule --------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_clean_document_is_cleared_after_the_change_and_once_more_on_the_next_idle()
    {
      var flag = new FocusDocFlag();

      flag.Before(false);

      Assert.True(flag.After());
      Assert.True(flag.OnIdle(true));
      Assert.False(flag.OnIdle(true));   // once: anything after that is the person's
    }

    [Fact]
    public void A_document_already_modified_is_never_touched()
    {
      var flag = new FocusDocFlag();

      flag.Before(true);

      Assert.False(flag.After());
      Assert.False(flag.OnIdle(true));
    }

    [Fact]
    public void An_idle_with_nothing_to_clear_clears_nothing_and_is_done()
    {
      var flag = new FocusDocFlag();
      flag.Before(false);
      flag.After();

      Assert.False(flag.OnIdle(false));
      Assert.False(flag.OnIdle(true));   // an edit after that idle is the person's
    }

    [Fact]
    public void Nothing_read_means_nothing_cleared()
    {
      var flag = new FocusDocFlag();

      Assert.False(flag.After());
      Assert.False(flag.OnIdle(true));
    }

    [Fact]
    public void A_read_counts_for_one_change()
    {
      var flag = new FocusDocFlag();
      flag.Before(false);
      flag.After();

      Assert.False(flag.After());   // a second After with no read of its own
    }

    [Fact]
    public void An_edit_during_focus_is_kept_through_the_close()
    {
      var flag = new FocusDocFlag();
      flag.Before(false);
      flag.After();
      flag.OnIdle(true);

      flag.Before(true);   // the close: the person has edited since

      Assert.False(flag.After());
      Assert.False(flag.OnIdle(true));
    }

    [Fact]
    public void A_save_during_focus_is_kept_through_the_close()
    {
      var flag = new FocusDocFlag();
      flag.Before(true);   // already modified when Focus came on
      flag.After();

      flag.Before(false);   // saved since, so the close's own flag must not undo that

      Assert.True(flag.After());
      Assert.True(flag.OnIdle(true));
    }

    /// <summary>Focus off before the idle that follows the add: Rhino's own flag from the add reads true before the close, and it is not the person's.</summary>
    [Fact]
    public void A_close_before_the_adds_idle_is_not_taken_for_an_edit()
    {
      var flag = new FocusDocFlag();
      flag.Before(false);
      flag.After();

      flag.Before(true);

      Assert.True(flag.After());
      Assert.True(flag.OnIdle(true));
      Assert.False(flag.OnIdle(true));
    }

    [Fact]
    public void A_dirty_document_with_an_idle_pending_from_nothing_stays_dirty()
    {
      var flag = new FocusDocFlag();
      flag.Before(true);
      flag.After();

      flag.Before(true);

      Assert.False(flag.After());
    }

    // ---- the law, over a small Rhino: the flag set at once or a beat later, with and without an edit ---------------------------------------------------

    private sealed class Doc
    {
      public bool Modified;
      public bool FlagsAtOnce;
      private bool _pending;

      /// <summary>What Rhino does to the flag when a view is added or closed.</summary>
      public void ViewChanged()
      {
        if (FlagsAtOnce) Modified = true;
        else _pending = true;
      }

      public void Idle(FocusDocFlag flag)
      {
        if (_pending) { Modified = true; _pending = false; }
        if (flag.OnIdle(Modified)) Modified = false;
      }

      public void Around(FocusDocFlag flag, Action change = null)
      {
        flag.Before(Modified);
        (change ?? ViewChanged)();
        if (flag.After()) Modified = false;
      }
    }

    [Theory]
    // already modified, Rhino flags at once, edited during Focus, saved during Focus
    [InlineData(false, true, false, false)]
    [InlineData(false, false, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, true, false)]
    [InlineData(false, false, true, false)]
    [InlineData(true, true, false, true)]
    [InlineData(true, false, false, true)]
    public void Focus_on_and_off_leaves_the_document_as_the_person_left_it(bool already, bool atOnce, bool edit, bool save)
    {
      var doc = new Doc { Modified = already, FlagsAtOnce = atOnce };
      var flag = new FocusDocFlag();

      doc.Around(flag);       // Focus on: the view is added
      doc.Idle(flag);
      Assert.Equal(already, doc.Modified);

      if (edit) doc.Modified = true;
      if (save) doc.Modified = false;

      doc.Around(flag);       // Focus off: the view is closed
      doc.Idle(flag);

      Assert.Equal(save ? false : already || edit, doc.Modified);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Focus_off_inside_the_same_idle_leaves_a_clean_document_clean(bool atOnce)
    {
      var doc = new Doc { Modified = false, FlagsAtOnce = atOnce };
      var flag = new FocusDocFlag();

      doc.Around(flag);
      doc.Around(flag);
      doc.Idle(flag);

      Assert.False(doc.Modified);
    }

    [Fact]
    public void The_small_rhino_really_flags_without_the_rule()
    {
      // The positive control: with no rule in the way, the document ends modified, which is what the gate measured.
      var doc = new Doc { Modified = false, FlagsAtOnce = true };

      doc.ViewChanged();

      Assert.True(doc.Modified);
    }

    // ---- the seams, by text (ADR-091): the Rhino proof is the twelfth gate's -----------------------------------------------------------------------------------

    private static string Repo(params string[] parts)
    {
      var dir = AppContext.BaseDirectory;
      while (dir != null && !File.Exists(Path.Combine(dir, "Dwight.sln"))) dir = Path.GetDirectoryName(dir);
      Assert.NotNull(dir);
      return File.ReadAllText(Path.Combine(new[] { dir }.Concat(parts).ToArray())).Replace("\r\n", "\n");
    }

    private static void InOrder(string source, params string[] parts)
    {
      var at = 0;
      foreach (var part in parts)
      {
        var found = source.IndexOf(part, at, StringComparison.Ordinal);
        Assert.True(found >= 0, "not found in order: " + part);
        at = found + part.Length;
      }
    }

    private static string Between(string source, string from, string to)
    {
      var a = source.IndexOf(from, StringComparison.Ordinal);
      Assert.True(a >= 0, from);
      var b = source.IndexOf(to, a + from.Length, StringComparison.Ordinal);
      Assert.True(b >= 0, to);
      return source.Substring(a, b - a);
    }

    private static string Focus => Repo("Dwight", "UI", "WalkFocus.cs");

    [Fact]
    public void Each_change_to_the_view_reads_before_and_restores_after_even_if_it_throws()
    {
      InOrder(Between(Focus, "private static void Keeping(", "private static void OnIdle("),
        "_flag.Before(doc.Modified);", "try", "change();", "finally", "if (_flag.After()) doc.Modified = false;", "RhinoApp.Idle += OnIdle;");
    }

    [Fact]
    public void The_add_with_everything_that_follows_it_is_inside_the_rule()
    {
      InOrder(Between(Focus, "private static bool OpenViewport(", "/// <summary>The canvas moved"), "Keeping(doc, () =>", "doc.Views.Add(", "PushViewInfo(", "view.ActiveViewport.DisplayMode = from.DisplayMode;", "view.Redraw();", "});");
    }

    [Fact]
    public void The_close_and_giving_the_active_view_back_are_inside_the_rule()
    {
      InOrder(Between(Focus, "private static void CloseViewport(", "private static double Dpi()"), "Keeping(doc, () =>", "view?.Close();", "doc.Views.ActiveView = prior.Active;", "});");
    }

    [Fact]
    public void Moving_the_view_is_inside_the_rule()
    {
      InOrder(Between(Focus, "public static void Place()", "/// <summary>Every window back"), "Keeping(RhinoDoc.FromRuntimeSerialNumber(_docSerial), () => NativeWindowEffects.MoveTo(");
    }

    [Fact]
    public void The_document_is_the_one_the_view_was_opened_in_and_a_closed_one_is_left_alone()
    {
      var focus = Focus;
      Assert.Contains("_docSerial = doc.RuntimeSerialNumber;", Between(focus, "private static bool OpenViewport(", "/// <summary>The canvas moved"));
      Assert.Contains("var doc = RhinoDoc.FromRuntimeSerialNumber(_docSerial);", Between(focus, "private static void CloseViewport(", "private static void Keeping("));
      Assert.Contains("if (doc == null) { change(); return; }", Between(focus, "private static void Keeping(", "private static void OnIdle("));
    }

    [Fact]
    public void The_next_idle_reads_it_once_and_lets_go()
    {
      InOrder(Between(Focus, "private static void OnIdle(", "/// <summary>Rhino is closing"), "RhinoApp.Idle -= OnIdle;", "var doc = RhinoDoc.FromRuntimeSerialNumber(_docSerial);", "if (doc != null && _flag.OnIdle(doc.Modified)) doc.Modified = false;");
    }

    [Fact]
    public void One_idle_handler_at_a_time()
    {
      Assert.Contains("RhinoApp.Idle -= OnIdle;\n", Between(Focus, "private static void Keeping(", "private static void OnIdle("));
    }

    [Fact]
    public void Rhino_closing_settles_the_flag_after_the_walk_has_ended()
    {
      InOrder(Repo("Dwight", "DwightPriority.cs"), "RhinoApp.Closing += (s, e) =>", "Dwight.UI.WalkSession.Drop();", "Dwight.UI.WalkFocus.Settle();");
      Assert.Contains("OnIdle(null, EventArgs.Empty);", Between(Focus, "public static void Settle()", "private static double Dpi()"));
    }

    [Fact]
    public void Restoring_closes_the_view_inside_its_own_step_so_a_throw_costs_the_rest_nothing()
    {
      Assert.Contains("try { CloseViewport(prior); } catch { }", Between(Focus, "public static void Restore()", "private static void CloseViewport("));
    }
  }
}
