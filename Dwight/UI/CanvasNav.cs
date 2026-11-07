using System;
using System.Collections.Generic;
using System.Drawing;
using Grasshopper;

namespace Dwight.UI
{
  /// <summary>Frame the GH canvas on a set of components ("show on canvas" / "go to components", spec).
  /// Reads each guid's bounds, fits the union into the viewport with padding, and pans to it. Fail-safe -
  /// a missing component or canvas just no-ops, never crashes Rhino (ADR-029).</summary>
  /// <summary>
  /// WHY FRAMING THE CANVAS IS CONSENTED, enumerated rather than left to taste (#291 bar 3).
  ///
  /// Moving someone's canvas is a user-visible side effect, so every automatic one needs a reason it can
  /// state. There are exactly four, and a short enum is the point: adding another is a visible
  /// decision instead of a quiet one, and every framing in the codebase is greppable by its reason.
  /// </summary>
  internal enum NavReason
  {
    /// <summary>The user clicked something whose whole meaning is "take me there". The click is its own
    /// consent, so this needs no further justification anywhere it appears.</summary>
    UserClicked = 0,

    /// <summary>A scaffold map has just landed. Narrow and justified - the user
    /// approved this map seconds ago and the map IS the deliverable, and it lands clear of existing work
    /// (#242) where they would otherwise have to hunt for it. Nothing else auto-pans but the reasons below: not a message
    /// append, not a turn completing, not a panel reopening, not re-lighting an older message.</summary>
    MapJustLanded = 1,

    /// <summary>Brief CM: a build the user approved has just landed, a spot at its verdict and a run's regions after the tidy. The same reason as the map's, and it was measured:
    /// on a 1,289-object file a slider landed off-screen with the view unmoved, and the person could not find it.</summary>
    BuildJustLanded = 2,

    /// <summary>Brief EE: the chat's <c>show_on_canvas</c>, the model pointing at what it is explaining in answer to the person's own message. Narrow: it frames only the
    /// components named, glow-only, and the person sees it happen in a turn they started.</summary>
    ChatPointed = 3,
  }

  internal static class CanvasNav
  {
    /// <param name="why">Required, and required to be one of exactly four things - see <see cref="NavReason"/>.
    /// It is not read: it exists so the call site must declare which consented framing this is.</param>
    public static void GoTo(IEnumerable<Guid> guids, NavReason why)
    {
      try
      {
        var canvas = Instances.ActiveCanvas;
        var doc = canvas?.Document;
        if (canvas == null || doc == null || guids == null || canvas.Width <= 0 || canvas.Height <= 0) return;

        RectangleF? box = null;
        foreach (var id in guids)
        {
          var b = doc.FindObject(id, true)?.Attributes?.Bounds;
          if (b == null) continue;
          box = box == null ? b.Value : RectangleF.Union(box.Value, b.Value);
        }
        if (box == null) return;

        var r = RectangleF.Inflate(box.Value, box.Value.Width * 0.25f + 60f, box.Value.Height * 0.25f + 60f);
        var vp = canvas.Viewport;
        var zoom = Math.Min((float)canvas.Width / r.Width, (float)canvas.Height / r.Height);
        zoom = Math.Max(0.1f, Math.Min(zoom, 1.6f)); // never over-zoom a single small component
        vp.Zoom = zoom;
        vp.MidPoint = new PointF(r.X + r.Width / 2f, r.Y + r.Height / 2f);
        canvas.Refresh();
      }
      catch { }
    }

    /// <summary>Brief ED: a note, from a dot or the dock's Notes pane. The canvas centres on a document point, at <paramref name="zoom"/> when one is given and at its own zoom
    /// otherwise.</summary>
    public static void Centre(PointF mid, float? zoom, NavReason why)
    {
      try
      {
        var canvas = Instances.ActiveCanvas;
        if (canvas == null || canvas.Document == null || canvas.Width <= 0 || canvas.Height <= 0) return;
        var vp = canvas.Viewport;
        if (zoom.HasValue) vp.Zoom = zoom.Value;
        vp.MidPoint = mid;
        canvas.Refresh();
      }
      catch { }
    }

    /// <summary>Brief DA: a Walk's step, framed in what the bar and the window leave free (<see cref="WalkGeometry.Frame"/>). The clears are in the canvas's own pixels. A step with nothing
    /// left on the canvas moves nothing.</summary>
    public static void FrameStep(IEnumerable<Guid> guids, NavReason why, float bottomClear, float rightClear)
    {
      try
      {
        var canvas = Instances.ActiveCanvas;
        var doc = canvas?.Document;
        if (canvas == null || doc == null || guids == null || canvas.Width <= 0 || canvas.Height <= 0) return;

        RectangleF? box = null;
        foreach (var id in guids)
        {
          var b = doc.FindObject(id, true)?.Attributes?.Bounds;
          if (b == null) continue;
          box = box == null ? b.Value : RectangleF.Union(box.Value, b.Value);
        }
        if (box == null) return;

        var (zoom, mid) = WalkGeometry.Frame(box.Value, canvas.Width, canvas.Height, bottomClear, rightClear);
        var vp = canvas.Viewport;
        vp.Zoom = zoom;
        vp.MidPoint = mid;
        canvas.Refresh();
      }
      catch { }
    }
  }
}
