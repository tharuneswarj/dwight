using System;
using Grasshopper;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Rhino;

namespace Dwight
{
  /// <summary>
  /// Dwight's startup hook. Grasshopper calls <see cref="PriorityLoad"/> once, early
  /// in boot, before the canvas exists. We defer until the canvas is created, then
  /// install Dwight's own invocation surfaces (ADR-024): a top-bar menu and a
  /// cursor-summoned pop-up. Dwight owns these rather than borrowing Grasshopper's
  /// native canvas menu, which has no clean public extension hook.
  /// </summary>
  public class DwightPriority : GH_AssemblyPriority
  {
    public override GH_LoadingInstruction PriorityLoad()
    {
      Instances.CanvasCreated += OnCanvasCreated;
      return GH_LoadingInstruction.Proceed;
    }

    private void OnCanvasCreated(GH_Canvas canvas)
    {
      // One-shot: the canvas is stable for the session.
      Instances.CanvasCreated -= OnCanvasCreated;

      // #253: the banner is the FIRST thing Dwight says every session, so it must describe the build the
      // user actually has. It read "Phase 1, read path" for six phases after that stopped being true - a
      // surface stating a version of itself the system had left behind, which is the same class as a panel
      // claiming a state the canvas contradicts, one layer earlier.
      // The verb names and the chord come from their single sources (DwightVerbs / DwightChord), so this
      // line cannot drift from the menu that lists them or the handler that tests them - which is the
      // same defect #253 names, caught one layer earlier.
      RhinoApp.WriteLine(
        $"Dwight loaded. Five actions: {string.Join(", ", DwightVerbs.Names)}. " +
        $"Surfaces: top-bar \"Dwight\" menu, and {Dwight.UI.DwightChord.Label} on the canvas.");

      // Every surface is initialised in ISOLATION (WP-1 incident #176): a failure in any one is logged and the
      // rest still install. The fail-safe guardrail (mother doc 4.6) extends to startup - a store or a surface
      // failure must NEVER cost the OTHER surfaces. Before this, the hook was a bare sequence, so one throw took
      // down everything downstream of it; that latent cascade is the bug, independent of whatever triggers it.

      // WP-5: the label cache's on-disk persistence (opt-in by the host, so headless tests never touch the
      // user's file). Reorganise labels survive a restart instead of being re-billed every session.
      Safe("label-cache persistence", () => Dwight.Labelling.LabelCache.EnablePersistence(Dwight.Labelling.LabelCache.DefaultPath));

      // Phase-8 WP-1 step 4: durable per-file command history + Scaffold threads (ADR-067). NOTE: Enable only
      // stores a path - it creates no folder and reads no file (that is deferred to the first Save/Load), so it
      // cannot throw here; the wrapper is defence in depth, not a today-fix.
      Safe("session store", () => Dwight.Session.SessionStore.Enable(Dwight.Session.SessionStore.DefaultDir));

      Safe("top-bar menu", () => DwightTopBarMenu.Install());
      Safe("chord popup", () => DwightCanvasPopup.Attach(canvas));
      Safe("notes", () => Dwight.UI.CanvasNotes.Attach(canvas));   // Dwight notes: drawn whenever a document is open
      Safe("dock", () => Dwight.UI.DwightDock.Attach());   // the bottom-right per-file command dock (Plan 3)

      // Brief DH: Focus's windows go back when Rhino closes too: a Walk's ending restores them, and this is the last chance for one to end.
      Safe("walk close", () => RhinoApp.Closing += (s, e) => { try { Dwight.UI.WalkSession.Drop(); Dwight.UI.WalkFocus.Settle(); } catch { } });

      // LAST, and deliberately so: the tour opens in front of the surfaces it describes, and if it fails
      // it fails after everything that matters is already installed. It opens only on a profile that has
      // never seen it, costs nothing, and touches nothing but the flag that stops it opening again.
      Safe("first-run tour", () => Dwight.UI.FirstRunTour.OpenIfFirstRun());
    }

    /// <summary>Initialise one startup surface, logging (never rethrowing) a failure so it can never cascade to
    /// the others. The command-line line names exactly which surface failed and why - the instrument that turns
    /// "some surfaces are missing" into a named cause without the bridge.</summary>
    private static void Safe(string surface, Action init)
    {
      try { init(); }
      catch (Exception ex)
      {
        RhinoApp.WriteLine($"Dwight: {surface} failed to initialise - {ex.GetType().Name}: {ex.Message}. Other surfaces are unaffected.");
      }
    }
  }
}
