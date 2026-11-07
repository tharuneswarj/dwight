using System.Windows.Forms;
using Grasshopper;
using Dwight.Branding;
using Rhino;

namespace Dwight
{
  /// <summary>
  /// Installs the "Dwight" top-bar menu into Grasshopper's window menu bar
  /// (<c>Instances.DocumentEditor.MainMenuStrip</c>) - the clean, always-available v1
  /// entry point (ADR-024). Its items are surface-agnostic: they call the same
  /// <see cref="DwightActions"/> methods the cursor pop-up uses.
  /// </summary>
  internal static class DwightTopBarMenu
  {
    private static bool _installed;
    private static bool _retrying;

    public static void Install()
    {
      if (_installed) return;

      var editor = Instances.DocumentEditor;
      if (editor?.MainMenuStrip == null)
      {
        // The document editor may not be fully built at CanvasCreated; retry briefly.
        StartRetry();
        return;
      }

      AddMenu(editor.MainMenuStrip);
    }

    private static void StartRetry()
    {
      if (_retrying) return;
      _retrying = true;

      var timer = new Timer { Interval = 250 };
      var attempts = 0;
      timer.Tick += (s, e) =>
      {
        attempts++;
        var editor = Instances.DocumentEditor;
        if (editor?.MainMenuStrip != null)
        {
          timer.Stop();
          timer.Dispose();
          _retrying = false;
          AddMenu(editor.MainMenuStrip);
        }
        else if (attempts > 40) // ~10 seconds, then give up quietly
        {
          timer.Stop();
          timer.Dispose();
          _retrying = false;
          RhinoApp.WriteLine("Dwight: could not find the Grasshopper menu bar to attach the top-bar menu.");
        }
      };
      timer.Start();
    }

    private static void AddMenu(MenuStrip menuStrip)
    {
      if (_installed) return;
      _installed = true;

      // Runtime feedback (UI overhaul round 1): no icon on the top-bar entry - it ate menu-bar
      // width and read oddly next to the text-only native menus. The word alone is the brand touch.
      var mantis = new ToolStripMenuItem("Dwight");

      // The LLM actions.
      var explain = new ToolStripMenuItem(DwightVerbs.Explain);
      try { explain.Image = ActionIcons.Create(DwightVerbs.Explain, 16); } catch { }
      var walk = new ToolStripMenuItem("Step by step");
      walk.Click += (s, e) => DwightCanvasPopup.Safe(() => LlmActions.Explain(Dwight.UI.ExplainModes.Walk));
      explain.DropDownItems.Add(walk);
      AddSub(explain, "Detailed", () => LlmActions.Explain(Dwight.UI.ExplainModes.Detailed));
      AddSub(explain, "Short", () => LlmActions.Explain(Dwight.UI.ExplainModes.Short));
      var learn = new ToolStripMenuItem("Learn");   // Brief DE: a Walk with practice questions, for this run only; group-only like the Walk
      learn.Click += (s, e) => DwightCanvasPopup.Safe(() => LlmActions.Explain(Dwight.UI.ExplainModes.Learn));
      explain.DropDownItems.Add(learn);
      // A Walk is group-only (ADR-060): reveal it only when 2+ components are selected. The top-bar
      // menu is built once, so adapt at open-time rather than at construction.
      explain.DropDownOpening += (s, e) => { try { walk.Visible = learn.Visible = DwightActions.CurrentSelection().Count >= 2; } catch { } };
      mantis.DropDownItems.Add(explain);

      var diagnose = new ToolStripMenuItem(DwightVerbs.Diagnose);
      try { diagnose.Image = ActionIcons.Create(DwightVerbs.Diagnose, 16); } catch { }
      diagnose.Click += (s, e) => DwightCanvasPopup.Safe(() => LlmActions.Diagnose());
      mantis.DropDownItems.Add(diagnose);

      var reorganise = new ToolStripMenuItem(DwightVerbs.Reorganise);
      try { reorganise.Image = ActionIcons.Create(DwightVerbs.Reorganise, 16); } catch { }
      reorganise.Click += (s, e) => DwightCanvasPopup.Safe(() => LlmActions.Reorganise());
      mantis.DropDownItems.Add(reorganise);

      // ADR-061 / ADR-060: one verb; the modes are the panel chooser now (the 4-mode submenu + "To cluster" gone).
      var simplify = new ToolStripMenuItem(DwightVerbs.Simplify);
      try { simplify.Image = ActionIcons.Create(DwightVerbs.Simplify, 16); } catch { }
      simplify.Click += (s, e) => DwightCanvasPopup.Safe(() => LlmActions.Simplify());
      mantis.DropDownItems.Add(simplify);

      var scaffold = new ToolStripMenuItem(DwightVerbs.Build);
      try { scaffold.Image = ActionIcons.Create(DwightVerbs.Build, 16); } catch { }
      scaffold.Click += (s, e) => DwightCanvasPopup.Safe(() => Dwight.UI.DwightScaffold.Open());
      mantis.DropDownItems.Add(scaffold);

      mantis.DropDownItems.Add(new ToolStripSeparator());

#if DEBUG   // THE DEV TOOLS SHIP IN NO RELEASE (Tharun, 21 October 2026, #1017): everything from here to the matching
            // #endif - the data-layer diagnostics, the Dev submenu and every drill in it - exists only in a DEBUG build.
            // The published .gha and the Yak package are Release, so a user never sees a menu that can write a file,
            // sabotage an apply or start a paid probe.
      // Phase 1 - data-layer tools (kept for diagnostics).
      var read = new ToolStripMenuItem("Read Selection");
      read.Click += (s, e) => DwightCanvasPopup.Safe(() => DwightActions.ReadSelection());
      mantis.DropDownItems.Add(read);

      var dump = new ToolStripMenuItem("Dump Selection as GhJSON");
      dump.Click += (s, e) => DwightCanvasPopup.Safe(() => DwightActions.DumpSelectionAsGhJson());
      mantis.DropDownItems.Add(dump);

      mantis.DropDownItems.Add(new ToolStripSeparator());

      // Phase 6 dev tools (not user-facing v1; gated off once the work-packages are accepted). The
      // layout engine is verified on layout alone before any LLM/segmenter exists (the phase doc's bar).
      var dev = new ToolStripMenuItem("Dev");
#if DEBUG
      AddSub(dev, "MSAGL layout spike", () => Dwight.Layout.MsaglSpike.Run());   // WP-0 throwaway spike (WP-5.7: DEBUG-only)
#endif
      AddSub(dev, "Lay out (engine only)", () => Dwight.Layout.LayoutCommand.Run());   // WP-A: layout gate
      dev.DropDownItems.Add(new ToolStripSeparator());
      AddSub(dev, "Segment (export clusters)", () => Dwight.Segment.SegmentCommand.ExportClusters());   // WP-B
      AddSub(dev, "Score against my chunks", () => Dwight.Segment.SegmentCommand.ScoreAgainstMyChunks()); // WP-B benchmark
      AddSub(dev, "Label clusters", () => Dwight.Labelling.LabelCommand.Run());   // WP-C label gate
      // WP-3: "Reorganise (best-practice)" graduated out of Dev into the Reorganise boundary opt-in (ADR-086 revised).
#if DEBUG
      // WP-2 Stage 5 live-fire drills: corrupt exactly the NEXT Simplify apply, then self-clear. Stray-rewire trips
      // layer 1 (predicted != actual, pre-commit STOP); value-change trips layer 2 (re-solve differs, auto-revert).
      dev.DropDownItems.Add(new ToolStripSeparator());
      AddSub(dev, "Sabotage next Simplify: stray rewire", () => Dwight.Simplify.SimplifyApply.PendingSabotage = Dwight.Simplify.SimplifyApply.Sabotage.StrayRewire);
      AddSub(dev, "Sabotage next Simplify: value change", () => Dwight.Simplify.SimplifyApply.PendingSabotage = Dwight.Simplify.SimplifyApply.Sabotage.ValueChange);
      AddSub(dev, "Clear Simplify sabotage", () => Dwight.Simplify.SimplifyApply.PendingSabotage = Dwight.Simplify.SimplifyApply.Sabotage.None);

      // Phase-8 WP-2 Stage C (#199/#200): the two gate instruments. Both are read-only diagnostics.
      dev.DropDownItems.Add(new ToolStripSeparator());
      // #131, given a surface (#247): the FIRST thing any runtime gate clicks. It names the symbols it
      // checked and which build they date the loaded .gha to - a stale deploy stops the gate here.
      AddSub(dev, "Loaded build probe (run this FIRST)", () => Dwight.Agent.AgentDiagnostics.ShowLoadedBuild());
      AddSub(dev, "Last LLM request (thinking check)", () => Dwight.Agent.AgentDiagnostics.ShowLastExchange());
      AddSub(dev, "Live model ids (reconcile list)", () => Dwight.Agent.AgentDiagnostics.ShowLiveModelIds());
      AddSub(dev, "Loaded catalogue version", () => Dwight.Agent.AgentDiagnostics.ShowCatalogueVersion());
      // D2: the one encoding stage no headless test can reach - the WebView hand-off.
      AddSub(dev, "Text encoding probe", () => Dwight.Agent.AgentDiagnostics.ShowTextEncodingProbe());
      // Lane B (#873): the raw reply streams, captured inside a WINDOW - nothing is recorded until Start, which
      // clears everything before it, so a client definition's replies from earlier in the session can never be
      // written. The new reader is gated on these: it must know what each provider legitimately sends.
      AddSub(dev, "Start stream capture (SCRATCH DOC)", () => Dwight.Agent.AgentDiagnostics.StartStreamCapture());
      AddSub(dev, "Write stream capture", () => Dwight.Agent.AgentDiagnostics.WriteStreamCapture());

      // Phase-8 WP-2 Stage D (#193e): the agent tool surface, exercised against a live document. The
      // build entries write - SCRATCH DOCUMENTS ONLY (#160).
      dev.DropDownItems.Add(new ToolStripSeparator());
      AddSub(dev, "Agent: run reads + refusals", () => Dwight.Agent.AgentHarness.RunReads());
      AddSub(dev, "Agent: solve_and_check", () => Dwight.Agent.AgentHarness.RunSolveCheck());
      AddSub(dev, "Agent: build_region (SCRATCH DOC)", () => Dwight.Agent.AgentHarness.RunBuild());
      AddSub(dev, "Agent: arm build revert drill", () => Dwight.Agent.AgentHarness.ArmBuildSabotage());
      AddSub(dev, "Agent: Build thinking probe (PAID)", () => Dwight.Agent.AgentHarness.RunThinkingProbe());

      // Phase-8 WP-3 Stage A (#223): the loop's TOOL DISPATCH, against a MUTATING tool from a worker
      // thread - the path that HANGS rather than errors if the threading is wrong. SCRATCH DOCS ONLY.
      // Renamed at #280: it starts INSIDE the loop's dispatch, and the label must say what it measures.
      AddSub(dev, "Agent: tool dispatch drill (SCRATCH DOC)", () => Dwight.Agent.AgentHarness.RunToolDispatchDrill());
      // Phase-8 WP-4 Stage 0 (#268): the legs the drill above cannot see - the chat's turn entry, the
      // provider call and the step loop. Scripted stub provider, so it costs nothing. SCRATCH DOCS ONLY.
      AddSub(dev, "Agent: full-path turn drill (SCRATCH DOC)", () => Dwight.Agent.AgentHarness.RunFullPathTurnDrill());
      // #255: the direct instrument for the consent blocker - what is pending, and what has been spent.
      AddSub(dev, "Agent: pending consents", () => Dwight.Agent.AgentHarness.ShowConsents());
      // #143/#212: does the build's self-correction loop actually recover anything? Reads Dwight's
      // own record; costs nothing; reports the DENOMINATOR so a zero can be told from an absence.
      AddSub(dev, "Agent: build recoveries", () => Dwight.Agent.AgentDiagnostics.ShowBuildRecoveries());
      // F5: does the model still ask permission to ask? Counted, with its denominator, never enforced.
      AddSub(dev, "Agent: turn shape (permission-to-ask)", () => Dwight.Agent.AgentDiagnostics.ShowTurnShape());
      // Capability alignment: the generated statement, so "what Dwight says it can do" has something real to
      // be compared against.
      AddSub(dev, "Agent: what Dwight is told it can do", () => Dwight.Agent.AgentDiagnostics.ShowCapabilityStatement());
#endif
      mantis.DropDownItems.Add(dev);

      mantis.DropDownItems.Add(new ToolStripSeparator());
#endif

      var restart = new ToolStripMenuItem("Restart Dwight");   // kill switch: drop a wedged task, reset
      restart.Click += (s, e) => DwightCanvasPopup.Safe(() => DwightSession.Restart());
      mantis.DropDownItems.Add(restart);

      var settings = new ToolStripMenuItem("Settings...");
      settings.Click += (s, e) => DwightCanvasPopup.Safe(() => LlmActions.OpenSettings());
      mantis.DropDownItems.Add(settings);

      var report = new ToolStripMenuItem("Report a problem...");
      report.Click += (s, e) => DwightCanvasPopup.Safe(() => Dwight.UI.DwightFeedbackPanel.Open());
      mantis.DropDownItems.Add(report);

      // Brand Dwight's own dropdown (renderer + font + theme), recursing its submenus - never the GH
      // menu bar. Re-apply on theme change so the top-bar menu follows light/dark like everything else.
      Dwight.UI.MenuBrand.Apply(mantis.DropDown, Dwight.UI.DwightTheme.Current() == "dark");
      Dwight.UI.DwightTheme.Changed += _ => { try { Dwight.UI.MenuBrand.Apply(mantis.DropDown, Dwight.UI.DwightTheme.Current() == "dark"); } catch { } };

      menuStrip.Items.Add(mantis);
    }

    private static void AddSub(ToolStripMenuItem parent, string text, System.Action action)
    {
      var item = new ToolStripMenuItem(text);
      item.Click += (s, e) => DwightCanvasPopup.Safe(action);   // WP-1.5: never throw into Rhino
      parent.DropDownItems.Add(item);
    }
  }
}
