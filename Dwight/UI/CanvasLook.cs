using System;
using System.Drawing;
using Dwight.Branding;

namespace Dwight.UI
{
  /// <summary>
  /// Brief DJ: WHAT THE CANVAS IS DRAWN WITH, EVERY NUMBER WITH ITS REASON, AND THE THEME READ FROM THE CANVAS. GH-free (ADR-091). Tharun saw the tenth build and found the glow faint and the wires dull
  /// against the mock-ups. Grasshopper's canvas has no theme of its own to ask, and a person's skin can be anything, so a colour that cannot serve both is chosen from the canvas's own colour at paint time,
  /// never assumed. The numbers were judged on Rhino 2's captures of the tenth build (<c>decisions-2026-11-01/canvas-look</c>) against Grasshopper's light canvas 212,208,200 and a dark one 38,40,44.
  /// </summary>
  internal static class CanvasLook
  {
    // ---- the theme, from the canvas ------------------------------------------------------------------------------------------------------------

    /// <summary>Below this relative luminance a canvas is dark: perceptual mid-grey. Grasshopper's default reads 0.63 and the dark one 0.02.</summary>
    public const double DarkBelow = 0.18;

    public static bool IsDark(Color canvas) => Luminance(canvas) < DarkBelow;

    /// <summary>What a colour measures, by the WCAG formula the shell's gate also uses.</summary>
    public static double Luminance(Color c) => 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);

    public static double Contrast(Color a, Color b)
    {
      var x = Luminance(a);
      var y = Luminance(b);
      return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    private static double Linear(byte channel)
    {
      var c = channel / 255.0;
      return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    /// <summary>What a translucent colour comes to over another, so a contrast can be measured on what the eye gets.</summary>
    public static Color Over(Color top, Color under)
    {
      var a = top.A / 255.0;
      int Mix(byte t, byte u) => (int)Math.Round(t * a + u * (1 - a));
      return Color.FromArgb(Mix(top.R, under.R), Mix(top.G, under.G), Mix(top.B, under.B));
    }

    // ---- the wash --------------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// THE WASH IS A TINT AWAY FROM THE CANVAS, NOT THE CANVAS'S OWN COLOUR, AND THE STEP STANDS ON AN ISLAND OF THE CANVAS'S TRUE COLOUR. Painted in its own colour it changed nothing but the grid and the
    /// components. So the rest of the canvas moves away from the step: darker on a light canvas, lighter on a dark one. At 120 and 90 it was heavy and uniform (the eleventh gate, read in Rhino 2's pixels). At 70 the
    /// light canvas was too pale beside the mock-up (Tharun's word on the twelfth build: people use the light canvas, and the dark one needs nothing). The alpha is where the island reads clearly and the rest still
    /// reads as the definition, about 2.2:1 between island and field on the light canvas and 2.1:1 on the dark, with black text on a grey component under it about 4.8:1 and 5.9:1. At 110 on the light canvas that text
    /// falls under 4.5:1, so this is close to as dark as it goes. These two are the only numbers a dim slider would have to scale, should one ever be asked for: the Dim switch only says whether the wash is drawn.
    /// </summary>
    public const int WashAlphaOnLight = 100;
    public const int WashAlphaOnDark = 60;

    public static Color Wash(Color canvas) =>
      IsDark(canvas) ? Color.FromArgb(WashAlphaOnDark, UiTokens.Surface(false)) : Color.FromArgb(WashAlphaOnLight, ColorTranslator.FromHtml(UiTokens.TextHex));

    /// <summary>The hole's corners. At this strength a square hole reads as a box drawn on the canvas.</summary>
    public const float HoleRadius = 12f;

    /// <summary>How far the glow reaches past a component's bounds: its outermost ring is a pen 15 wide on a path grown by 15, so half the pen lies beyond that, at 22.5.</summary>
    public const float GlowReach = 22.5f;

    /// <summary>The true canvas showing beyond the glow, so the glow lives on the island and does not fill it. The hole was 24, which left this 1.5, and no island read.</summary>
    public const float IslandMargin = 8f;

    /// <summary>
    /// How wide a band the wash leaves along each of the step's wires. No one wire colour can clear 3:1 against the canvas and also against the wash over it (it measures about 1.4:1 and 1.5:1), so a lit wire runs on
    /// the canvas's true colour all the way, and the band reads as part of what is lit.
    /// </summary>
    public const float WireBand = 12f;

    // ---- the glow --------------------------------------------------------------------------------------------------------------------------------

    /// <summary>The solid edge on the component's rounded bounds, drawn over the halo.</summary>
    public const float EdgeWidth = 2f;

    /// <summary>The brand amber fails 3:1 on the light canvas (1.9:1), and the darker amber fails it on the dark one (2.8:1), so the edge takes the one that clears it.</summary>
    public static Color Edge(Color canvas) => IsDark(canvas) ? UiTokens.Brand : UiTokens.BrandInk;

    /// <summary>The halo's alpha, ring 1 the innermost. They were 98, 76 and 54, and the outermost was lost on the dark canvas.</summary>
    public static int HaloAlpha(int ring) => ring <= 1 ? 160 : ring == 2 ? 120 : 80;

    // ---- the wires ---------------------------------------------------------------------------------------------------------------------------------

    /// <summary>A step's outline and arrows, thicker than Grasshopper's own wire. Dashed for an arrow as before, and solid for an outline.</summary>
    public const float MarkWidth = 3f;
    public const float MarkHaloWidth = 8f;
    public const int MarkHaloAlpha = 70;

    /// <summary>The Lens's line over Grasshopper's own wire: the step's data flow is lit, not only chipped.</summary>
    public const float LensWireWidth = 2.5f;

    /// <summary>The same curve as the marks' over-drawn wire, so the two lie on one another. Its bend is half the wire's run, and never less than 18.</summary>
    public static (PointF C1, PointF C2) WireControls(PointF a, PointF b)
    {
      var dx = Math.Max(Math.Abs(b.X - a.X) * 0.5f, 18f);
      return (new PointF(a.X + dx, a.Y), new PointF(b.X - dx, b.Y));
    }

    // ---- the chips ---------------------------------------------------------------------------------------------------------------------------------

    /// <summary>Never under 11 at zoom 1, in the canvas's own units, so it scales with everything else on it.</summary>
    public const float ChipFontPx = 11f;

    /// <summary>A filled pill: its corners are half its height.</summary>
    public static float PillRadius(float height) => height / 2f;

    public const float PillEdgeWidth = 1f;
  }
}
