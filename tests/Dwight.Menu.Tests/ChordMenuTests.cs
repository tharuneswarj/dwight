using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using Dwight.Branding;
using Dwight.UI;
using Xunit;

namespace Dwight.Menu.Tests
{
  /// <summary>
  /// Brief EU: the chord's menu as built, read item by item. Tharun's ruling of 3 November 2026: the two note lines are one Notes entry with a submenu, Note here
  /// and Show notes, the second a ticked item that follows the one switch the dock and Settings share. The dock has no door that opens it on its Notes tab, so
  /// the submenu holds two items and All notes waits for one.
  /// </summary>
  public class ChordMenuTests
  {
    /// <summary>Doors that write down what was asked of them, and count the fail-safe.</summary>
    private sealed class Record
    {
      public readonly List<string> Calls = new List<string>();
      public int Safe;

      public ChordDoors Doors() => new ChordDoors
      {
        ExplainWalk = () => Calls.Add("explain:walk"),
        ExplainDetailed = () => Calls.Add("explain:detailed"),
        ExplainShort = () => Calls.Add("explain:short"),
        ExplainLearn = () => Calls.Add("explain:learn"),
        Diagnose = () => Calls.Add("diagnose"),
        Reorganise = () => Calls.Add("reorganise"),
        Simplify = () => Calls.Add("simplify"),
        Build = () => Calls.Add("build"),
        ReadSelection = () => Calls.Add("read"),
        DumpSelection = () => Calls.Add("dump"),
        NoteHere = () => Calls.Add("note here"),
        SetNotesShown = on => Calls.Add("shown:" + (on ? "on" : "off")),
        Restart = () => Calls.Add("restart"),
        Settings = () => Calls.Add("settings"),
        ReportProblem = () => Calls.Add("report"),
        Safe = action => { Safe++; action(); },
      };
    }

    private static void With(int selCount, bool shown, bool dark, Action<ContextMenuStrip, Record> read)
    {
      var rec = new Record();
      var menu = ChordMenu.Build(selCount, shown, dark, rec.Doors());
      try { read(menu, rec); }
      finally { menu.Dispose(); }
    }

    private static List<string> Rows(ToolStripItemCollection items) =>
      items.Cast<ToolStripItem>().Select(i => i is ToolStripSeparator ? "---" : i.Text).ToList();

    private static ToolStripMenuItem Top(ContextMenuStrip menu, string text) =>
      menu.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == text);

    private static ToolStripMenuItem Child(ToolStripMenuItem parent, string text) =>
      parent.DropDownItems.OfType<ToolStripMenuItem>().Single(i => i.Text == text);

    private static IEnumerable<ToolStripItem> All(ToolStripItemCollection items)
    {
      foreach (ToolStripItem it in items)
      {
        yield return it;
        if (it is ToolStripMenuItem mi && mi.HasDropDownItems) foreach (var sub in All(mi.DropDownItems)) yield return sub;
      }
    }

    public static IEnumerable<object[]> States() =>
      from sel in new[] { 0, 1, 2 } from shown in new[] { true, false } select new object[] { sel, shown };

    // ---- one Notes entry, two items, in the service row -------------------------------------------------------------------------------------------

    [Theory, MemberData(nameof(States))]
    public void The_chord_holds_one_Notes_entry_whose_submenu_is_Note_here_then_Show_notes(int selCount, bool shown) =>
      With(selCount, shown, false, (menu, _) =>
      {
        var notes = Assert.Single(menu.Items.OfType<ToolStripMenuItem>(), i => i.Text == "Notes");
        Assert.True(notes.HasDropDownItems);
        Assert.Equal(new[] { "Note here", "Show notes" }, Rows(notes.DropDownItems));
        Assert.True(notes.Enabled);
        Assert.True(Child(notes, "Note here").Enabled);   // with a selection it anchors, with none it sits at the chord's point
      });

    [Theory, MemberData(nameof(States))]
    public void The_parent_is_the_header_the_five_verbs_and_the_service_row_with_Notes_where_the_two_lines_were(int selCount, bool shown) =>
      With(selCount, shown, false, (menu, _) =>
      {
        var expected = new List<string> { "Dwight", "---", "Explain", "Diagnose", "Reorganise", "Simplify", "Build" };
#if DEBUG
        expected.AddRange(new[] { "---", "Read Selection", "Dump Selection as GhJSON" });
#endif
        expected.AddRange(new[] { "---", "Notes", "Restart Dwight", "Settings...", "Report a problem..." });
        Assert.Equal(expected, Rows(menu.Items));

        var top = Rows(menu.Items);
        foreach (var gone in new[] { "Note here", "Show notes", "Hide notes" }) Assert.DoesNotContain(gone, top);
        Assert.DoesNotContain(All(menu.Items), i => i.Text == "Hide notes");   // never a second wording, anywhere
      });

    // ---- the tick is the state ---------------------------------------------------------------------------------------------------------------------

    [Theory, InlineData(true), InlineData(false)]
    public void Show_notes_is_ticked_exactly_when_notes_are_shown_and_reads_the_same_either_way(bool shown) =>
      With(1, shown, false, (menu, _) =>
      {
        var notes = Top(menu, "Notes");
        var show = Child(notes, "Show notes");
        Assert.Equal(shown, show.Checked);
        Assert.Equal("Show notes", show.Text);
        Assert.False(show.CheckOnClick);   // the click asks the one door; the item does not flip itself
        Assert.False(Child(notes, "Note here").Checked);
        Assert.False(notes.Checked);
      });

    [Theory, InlineData(true, "shown:off"), InlineData(false, "shown:on")]
    public void A_click_on_Show_notes_asks_the_one_switch_for_the_opposite_of_the_tick(bool shown, string asked) =>
      With(0, shown, false, (menu, rec) =>
      {
        Child(Top(menu, "Notes"), "Show notes").PerformClick();
        Assert.Equal(new[] { asked }, rec.Calls);
        Assert.Equal(1, rec.Safe);
      });

    [Fact]
    public void A_click_on_Note_here_opens_a_note() =>
      With(0, true, false, (menu, rec) =>
      {
        Child(Top(menu, "Notes"), "Note here").PerformClick();
        Assert.Equal(new[] { "note here" }, rec.Calls);
        Assert.Equal(1, rec.Safe);
      });

    // ---- every door is the one it says, through the fail-safe ----------------------------------------------------------------------------------------

    [Fact]
    public void Every_item_does_what_it_says_and_every_click_goes_through_the_fail_safe() =>
      With(2, true, false, (menu, rec) =>
      {
        var leaves = All(menu.Items).OfType<ToolStripMenuItem>().Where(i => i.Enabled && !i.HasDropDownItems).ToList();
        foreach (var leaf in leaves) leaf.PerformClick();

        var expected = new List<string> { "explain:walk", "explain:detailed", "explain:short", "explain:learn", "diagnose", "reorganise", "simplify", "build" };
#if DEBUG
        expected.AddRange(new[] { "read", "dump" });
#endif
        expected.AddRange(new[] { "note here", "shown:off", "restart", "settings", "report" });
        Assert.Equal(expected, rec.Calls);
        Assert.Equal(expected.Count, rec.Safe);
      });

    [Fact]
    public void Explain_offers_a_Walk_and_Learn_only_for_a_group()
    {
      With(1, true, false, (menu, _) => Assert.Equal(new[] { "Detailed", "Short" }, Rows(Top(menu, "Explain").DropDownItems)));
      With(2, true, false, (menu, _) => Assert.Equal(new[] { "Step by step", "Detailed", "Short", "Learn" }, Rows(Top(menu, "Explain").DropDownItems)));
    }

    // ---- the submenu wears the brand like the parent --------------------------------------------------------------------------------------------------

    [Theory, InlineData(false), InlineData(true)]
    public void The_Notes_submenu_takes_the_renderer_font_and_colours_of_the_parent_in_both_themes(bool dark) =>
      With(2, true, dark, (menu, _) =>
      {
        foreach (var strip in new ToolStrip[] { menu, Top(menu, "Notes").DropDown, Top(menu, "Explain").DropDown })
        {
          var renderer = Assert.IsType<DwightMenuRenderer>(strip.Renderer);
          Assert.Equal(UiTokens.Surface(dark).ToArgb(), renderer.ColorTable.ToolStripDropDownBackground.ToArgb());
          Assert.Equal(UiTokens.HoverTint(dark).ToArgb(), renderer.ColorTable.MenuItemSelected.ToArgb());
          Assert.Equal(UiTokens.Text(dark).ToArgb(), strip.ForeColor.ToArgb());
          Assert.Equal(menu.Font.Name, strip.Font.Name);
          Assert.Equal(9f, strip.Font.Size);
        }
        Assert.True(Top(menu, "Dwight").Font.Bold);
      });
  }
}
