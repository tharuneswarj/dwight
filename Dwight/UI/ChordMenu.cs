using System;
using System.Windows.Forms;
using Dwight.Branding;

namespace Dwight.UI
{
  /// <summary>What each of the chord's items does when clicked. The chord (<c>DwightCanvasPopup</c>) wires these to the live verbs; a test wires them to a record.</summary>
  internal sealed class ChordDoors
  {
    /// <summary>Explain's four rows, one door each, so the builder needs no mode ids; the chord gives each its <c>ExplainModes</c> id.</summary>
    public Action ExplainWalk, ExplainDetailed, ExplainShort, ExplainLearn;
    public Action Diagnose, Reorganise, Simplify, Build;
    /// <summary>The two data-layer diagnostics, on the menu in a Debug build only.</summary>
    public Action ReadSelection, DumpSelection;
    public Action NoteHere;
    public Action<bool> SetNotesShown;
    public Action Restart, Settings, ReportProblem;
    /// <summary>The fail-safe dispatch every click goes through (WP-1.5): a menu action must never throw into Rhino.</summary>
    public Action<Action> Safe;
  }

  /// <summary>
  /// The chord's menu (ADR-024, ADR-047), built here GH-free and Eto-free so a test holds the items a person sees rather than the source text (Brief EU).
  /// Everything it reads arrives as an argument: the selection's size, whether notes are shown, the theme, and the doors.
  /// </summary>
  internal static class ChordMenu
  {
    /// <summary><see cref="ShowNotes"/> is the switch's one name on all three doors, <c>NotesVisibility.Label</c>; a test holds the two equal, because the notes
    /// core cannot be compiled where this menu is built.</summary>
    public const string Notes = "Notes", NoteHere = "Note here", ShowNotes = "Show notes";

    public static ContextMenuStrip Build(int selCount, bool notesShown, bool dark, ChordDoors doors)
    {
      var menu = new ContextMenuStrip();

      ToolStripMenuItem Item(string text, Action action)
      {
        var item = new ToolStripMenuItem(text);
        item.Click += (s, e) => doors.Safe(action);
        return item;
      }
      ToolStripMenuItem Add(string text, Action action) { var item = Item(text, action); menu.Items.Add(item); return item; }
      ToolStripMenuItem Sub(ToolStripMenuItem parent, string text, Action action) { var item = Item(text, action); parent.DropDownItems.Add(item); return item; }

      // Runtime feedback (UI overhaul round 1): no icon on the header - a glyph in the image margin
      // read as a hidden/disabled option. The header is the amber word alone.
      var header = new ToolStripMenuItem("Dwight") { Enabled = false, Tag = "dwight-header" };
      menu.Items.Add(header);
      menu.Items.Add(new ToolStripSeparator());

      // The menu is the five verbs; the only selection-driven change is Explain's modes (ADR-060/062):
      // a Walk only makes sense for a group, so a single component shows Detailed + Short only.
      var explain = new ToolStripMenuItem(DwightVerbs.Explain);
      try { explain.Image = ActionIcons.Create(DwightVerbs.Explain, 16); } catch { }
      if (selCount >= 2) Sub(explain, "Step by step", doors.ExplainWalk);
      Sub(explain, "Detailed", doors.ExplainDetailed);
      Sub(explain, "Short", doors.ExplainShort);
      if (selCount >= 2) Sub(explain, "Learn", doors.ExplainLearn);   // Brief DE: a Walk with practice questions, for this run only
      menu.Items.Add(explain);

      var diagnose = Add(DwightVerbs.Diagnose, doors.Diagnose);   // the fifth action, after Explain (ADR-059)
      try { diagnose.Image = ActionIcons.Create(DwightVerbs.Diagnose, 16); } catch { }

      var reorg = Add(DwightVerbs.Reorganise, doors.Reorganise);
      try { reorg.Image = ActionIcons.Create(DwightVerbs.Reorganise, 16); } catch { }

      // ADR-061 / ADR-060: one verb; the modes live in the panel chooser (Simplify's modes change the contract,
      // not just the payload). The old 4-mode submenu incl. the dropped "To cluster" is gone (Phase-7 trim).
      var simplify = Add(DwightVerbs.Simplify, doors.Simplify);
      try { simplify.Image = ActionIcons.Create(DwightVerbs.Simplify, 16); } catch { }

      var scaffold = Add(DwightVerbs.Build, doors.Build);
      try { scaffold.Image = ActionIcons.Create(DwightVerbs.Build, 16); } catch { }

#if DEBUG   // the two data-layer diagnostics are dev tools and ship in no Release (#1017/#1019); the chord is five verbs,
            // Restart and Settings (ADR-060).
      menu.Items.Add(new ToolStripSeparator());
      Add("Read Selection", doors.ReadSelection);
      Add("Dump Selection as GhJSON", doors.DumpSelection);
#endif
      menu.Items.Add(new ToolStripSeparator());
      // A service row, with Restart and Settings: the menu is the five verbs, and a note is not one (Brief DC, ruling 5). Brief EU (Tharun's ruling of
      // 3 November 2026): the note lines are one Notes entry with a submenu, so the row is shorter. Note here is enabled always: with a selection the
      // note anchors to its first component, with none it sits at the chord's own point. Show notes is ticked when notes are shown and always reads
      // the same, so a person sees the state rather than a verb to guess at; a click does the opposite of the tick they saw, through the one door.
      var notes = new ToolStripMenuItem(Notes);
      Sub(notes, NoteHere, doors.NoteHere);
      Sub(notes, ShowNotes, () => doors.SetNotesShown(!notesShown)).Checked = notesShown;
      menu.Items.Add(notes);
      Add("Restart Dwight", doors.Restart);   // kill switch: drop a wedged task, reset
      Add("Settings...", doors.Settings);
      Add("Report a problem...", doors.ReportProblem);

      MenuBrand.Apply(menu, dark);   // branded renderer + font + themed fore, recursing submenus
      return menu;
    }
  }
}
