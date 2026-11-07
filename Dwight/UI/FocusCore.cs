using System;
using System.Collections.Generic;
using System.Drawing;

namespace Dwight.UI
{
  /// <summary>What the host does after a change to <see cref="FocusState"/>. It carries this out and decides nothing.</summary>
  internal enum FocusDo { Nothing, Engage, Restore }

  /// <summary>
  /// FOCUS MODE (Brief DH, Tharun's ruling of 1 November 2026), GH-free (ADR-091). By default the panel is the Walk. Focus is the other way to walk: the window collapses to the chip, the bar opens,
  /// Grasshopper fills the screen and a Rhino viewport sits over the canvas. Focus is a property of the Walk, not of Explain's mode, so Learn has it too.
  ///
  /// TWO FACTS, because the choice outlives the windows. <see cref="Wanted"/> is the person's choice, off by default and remembered for this session only. <see cref="Active"/> is whether the windows are
  /// arranged for it now, which needs a live Walk that has reached a step, since a finished Explain arrives quietly.
  ///
  /// <code>
  /// Wanted Active   event                 Wanted Active   the host
  /// off    off      Toggle, a Walk live   on     on       Engage
  /// off    off      Toggle, no Walk       on     off      nothing: the next Walk's first step
  /// on     on       Toggle                off    off      Restore; a live Walk carries on in the panel
  /// on     off      Toggle                off    off      nothing
  /// on     off      a step is shown       on     on       Engage
  /// on     on       a step is shown       on     on       nothing
  /// on     on       the Walk ended        on     off      Restore
  /// any    off      the Walk ended        same   off      nothing
  /// on     on       the view closed       off    off      Restore; a live Walk carries on in the panel
  /// any    off      the view closed       same   off      nothing
  /// on     on       the bar closed        off    off      Restore; the Walk carries on in the panel on the same step
  /// any    off      the bar closed        same   off      nothing
  /// any    any      Declined              off    off      the refusal has already restored
  /// </code>
  ///
  /// The panel's Leave and another run starting end the Walk, so each of them is the "Walk ended" row: the windows go back, and the next Walk comes back to Focus on its first step, because
  /// turning Focus off is a choice the control makes and leaving a Walk is not. Closing Focus's own view by hand is that choice made another way (the twelfth gate), so the switch follows it,
  /// and so is the bar's X, its Focus switch and Esc in Focus (Tharun's sitting, Brief EC unit 1): a person who wanted the panel back has not finished the Walk.
  /// </summary>
  internal sealed class FocusState
  {
    public bool Wanted { get; private set; }
    public bool Active { get; private set; }

    public FocusDo Toggle(bool walkLive)
    {
      if (Wanted)
      {
        Wanted = false;
        return TakeBack();
      }
      Wanted = true;
      if (!walkLive) return FocusDo.Nothing;
      Active = true;
      return FocusDo.Engage;
    }

    public FocusDo StepShown()
    {
      if (!Wanted || Active) return FocusDo.Nothing;
      Active = true;
      return FocusDo.Engage;
    }

    public FocusDo WalkEnded() => TakeBack();

    /// <summary>The person closed the floating view themselves: Focus reads as off, and the next step does not open another.</summary>
    public FocusDo ViewClosed() => Off();

    /// <summary>The bar's X, its Focus switch, or Esc while it shows (Brief EC unit 1): Focus off, the Walk on.</summary>
    public FocusDo BarClosed() => Off();

    private FocusDo Off()
    {
      if (!Active) return FocusDo.Nothing;
      Wanted = false;
      return TakeBack();
    }

    /// <summary>It could not be arranged, and the person was told why. Leaving it wanted would ask again on every step.</summary>
    public void Declined()
    {
      Wanted = false;
      Active = false;
    }

    private FocusDo TakeBack()
    {
      if (!Active) return FocusDo.Nothing;
      Active = false;
      return FocusDo.Restore;
    }
  }

  /// <summary>
  /// FOCUS NEVER LEAVES A DOCUMENT MARKED MODIFIED THAT WAS NOT, AND A DOCUMENT ALREADY MODIFIED STAYS SO (the eleventh gate). Rhino treats a view as part of the document: adding the floating view set
  /// <c>RhinoDoc.Modified</c>, and so did closing it, with nothing edited, and a person would then be asked to save a file they had not touched.
  ///
  /// THE RULE, the same for adding, moving and closing the view. Read the flag right before the change. If it read false, clear it right after, and read it once more on the next idle, because Rhino's own
  /// flagging is not known to have finished by then, and clear it once more. If it read true, leave it: that is the person's edit. A read made while an idle is still owed cannot be trusted to be the
  /// person's, because it may be Rhino's flag from the last change, so it counts as clean. An edit inside that one idle's gap would be lost, and that gap is the one thing this cannot tell apart.
  ///
  /// True from <see cref="After"/> or <see cref="OnIdle"/> means set the flag false. Nothing here ever sets it true. GH-free (ADR-091): the host reads and writes, this decides.
  /// </summary>
  internal sealed class FocusDocFlag
  {
    private bool _read;
    private bool _clean;
    private bool _idleOwed;

    public void Before(bool modified)
    {
      _read = true;
      _clean = !modified || _idleOwed;
    }

    public bool After()
    {
      if (!_read) return false;
      _read = false;
      if (!_clean) return false;
      _idleOwed = true;
      return true;
    }

    public bool OnIdle(bool modified)
    {
      var owed = _idleOwed;
      _idleOwed = false;
      return owed && modified;
    }
  }

  /// <summary>What lights a Walk's step on the canvas. The host draws it with CanvasGlow; a test draws it with a fake that records what it was asked.</summary>
  internal interface IWalkGlow
  {
    /// <summary>The step's glow and marks, and the wash over the rest when <paramref name="wash"/>.</summary>
    void ShowStep(IReadOnlyList<Guid> step, bool wash);
    /// <summary>The wash over the step already lit, and nothing else.</summary>
    void SetWash(bool wash);
    /// <summary>The step's glow, marks and wash taken off the canvas, the Walk's own state kept.</summary>
    void HideStep();
  }

  /// <summary>
  /// THE CANVAS UNDER A WALK'S STEP (Brief EC units 2 and 4, Tharun's sitting of 2 November 2026), GH-free (ADR-091). Two of his rulings, held in one place because they meet on the same canvas:
  ///
  /// THE DIM FOLLOWS THE SWITCH AT ONCE. The setting is still saved and still read at every step; a change while a step is lit washes or unwashes that step in the same call. (Brief DH had it
  /// honoured only on the next step; Tharun overruled that: "it needs me to go to the next one in order to start the dim".)
  ///
  /// MINIMISED, THE CANVAS IS THE PERSON'S. The window going to its chip takes the step's glow, marks and wash off and leaves the Walk, its step and its panel as they were; the window coming back
  /// lights the same step again, with the dim as the switch says now, and no model call. Focus's own chip is not a minimise: in Focus the bar is the Walk, the window is only its companion, and the
  /// canvas stays lit.
  /// </summary>
  internal sealed class WalkCanvas
  {
    private readonly IWalkGlow _glow;
    private IReadOnlyList<Guid> _step;

    public WalkCanvas(IWalkGlow glow) { _glow = glow; }

    /// <summary>The step is off the canvas because the window was minimised; the Walk's keys go to Grasshopper meanwhile, so the step cannot move under a canvas that does not show it.</summary>
    public bool Hidden { get; private set; }

    /// <summary>A step is shown: lit, with the wash the setting asks for.</summary>
    public void StepShown(IReadOnlyList<Guid> step, bool dim)
    {
      _step = step;
      Hidden = false;
      _glow?.ShowStep(step, dim);
    }

    /// <summary>The Dim switch moved, from the panel, the bar or Settings. Returns whether the canvas changed.</summary>
    public bool DimChanged(bool dim)
    {
      if (_step == null || Hidden) return false;
      _glow?.SetWash(dim);
      return true;
    }

    /// <summary>The window went to its chip. Returns whether the step came off the canvas.</summary>
    public bool WindowMinimised(bool focusActive)
    {
      if (_step == null || Hidden || focusActive) return false;
      Hidden = true;
      _glow?.HideStep();
      return true;
    }

    /// <summary>The window came back, or the Walk's page is shown again after its chat (unit 5b): the same step lit again. Returns whether it was relit.</summary>
    public bool Relight(bool dim, bool force = false)
    {
      if (_step == null || (!Hidden && !force)) return false;
      Hidden = false;
      _glow?.ShowStep(_step, dim);
      return true;
    }

    /// <summary>The Walk has ended: nothing of it is drawn from here on.</summary>
    public void Ended()
    {
      _step = null;
      Hidden = false;
    }
  }

  /// <summary>Where Focus puts things, in the one window's logical points (#176), computed here and tested on rectangles (ADR-056).</summary>
  internal static class FocusGeometry
  {
    public const int ViewportMinW = 240, ViewportMaxW = 640, ViewportMinH = 160;

    /// <summary>
    /// The viewport: the canvas's top-right corner, three tenths of its width and four by three, always above the bar at its tallest, so neither can cover the other however the bar grows. Null where no
    /// viewport of a useful size fits, and Focus then runs without one.
    /// </summary>
    public static OneWindowCore.Box? Viewport(OneWindowCore.Box canvas)
    {
      var margin = OneWindowCore.EdgeMargin;
      var w = Math.Max(ViewportMinW, Math.Min(ViewportMaxW, canvas.W * 3 / 10));
      if (w + 2 * margin > canvas.W) return null;

      var free = canvas.H - WalkGeometry.FrameBottomClear - 2 * margin;
      var h = Math.Min(w * 3 / 4, free);
      if (h < ViewportMinH) return null;

      return new OneWindowCore.Box(canvas.X + canvas.W - w - margin, canvas.Y + margin, w, h);
    }

    /// <summary>How much of the canvas's right side the viewport takes from framing, so a step is never framed underneath it. In logical points, as <see cref="WalkGeometry.RightClear"/>'s is.</summary>
    public static int RightClear(OneWindowCore.Box canvas, OneWindowCore.Box? viewport)
    {
      if (!(viewport is OneWindowCore.Box v)) return 0;
      return Math.Max(0, canvas.X + canvas.W - v.X);
    }

    /// <summary>A window's box is logical points and Rhino's position is screen pixels, so they part at any scale but 100% (incident #176).</summary>
    public static Rectangle Physical(OneWindowCore.Box box, double dpi)
    {
      var s = dpi > 0 ? dpi / 96.0 : 1.0;
      return new Rectangle((int)Math.Round(box.X * s), (int)Math.Round(box.Y * s), (int)Math.Round(box.W * s), (int)Math.Round(box.H * s));
    }

    /// <summary>
    /// How wide a canvas must be for the bar to fit beside the chip where it stands: a true number for the sentence, because the chip can be nudged left. It is what
    /// <see cref="WalkGeometry.Surface"/> needs and no other number, and a test holds them to each other.
    /// </summary>
    public static int NeededWidth(OneWindowCore.Box canvas, OneWindowCore.Box? chip)
    {
      var margin = OneWindowCore.EdgeMargin;
      var taken = chip is OneWindowCore.Box c ? Math.Max(canvas.X + canvas.W - c.X, OneWindowCore.CornerClear - margin) : OneWindowCore.CornerClear - margin;
      return WalkGeometry.BarMinW + 2 * margin + taken;
    }
  }

  /// <summary>The plugin's words on Focus (#235).</summary>
  internal static class FocusText
  {
    public const string Label = "Focus";

    public const string Title = "Focus: the full canvas, the bar and a Rhino viewport";

    public const string NoViewport = "Focus is on without a Rhino viewport: Rhino could not open one here.";

    public const string ViewClosed = "Focus is off: its Rhino viewport was closed.";

    /// <summary>Brief EC unit 1: the bar's X is Focus off, and the Walk stays on its step in the panel.</summary>
    public const string BarClose = "Back to the panel: Esc";

    /// <summary>Why Focus would not arrange the windows, read from the same measure that decided. A short canvas is not a narrow one, and "too narrow" would send the person to the wrong edge.</summary>
    public static string Decline(OneWindowCore.Box canvas, OneWindowCore.Box? chip)
    {
      if (canvas.H < WalkGeometry.BarMinCanvasH)
        return "Grasshopper's window is too short for the bar; Focus needs at least " + WalkGeometry.BarMinCanvasH + " points of height.";
      return "Grasshopper's window is too narrow for the bar; Focus needs at least " + FocusGeometry.NeededWidth(canvas, chip) + " points.";
    }
  }
}
