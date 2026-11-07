using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;

namespace Dwight.UI
{
  /// <summary>
  /// HOW LONG PLAY HOLDS A STEP (Brief DD): one of the system's named durations times a factor for the step's length, so a step with more components or a longer sentence holds a little longer.
  /// It is a pace and not an animation, so reduced motion leaves it running; and a frame is already a cut, never a glide (<see cref="CanvasNav.FrameStep"/>).
  /// </summary>
  internal static class WalkPace
  {
    /// <summary><c>--dur-slow</c> in the tokens, which a test holds it to.</summary>
    public const double BeatMs = 500;
    public const double MinSeconds = 3, MaxSeconds = 9;

    private const double BaseBeats = 4;
    private const double CharsPerBeat = 10;

    /// <summary>The seconds to hold a step of this many components and this long a sentence.</summary>
    public static double Seconds(int components, int sentenceChars)
    {
      var beats = BaseBeats + Math.Max(0, components) + Math.Max(0, sentenceChars) / CharsPerBeat;
      return Math.Max(MinSeconds, Math.Min(MaxSeconds, beats * BeatMs / 1000.0));
    }
  }

  /// <summary>
  /// WHETHER A WALK IS PLAYING ITSELF (Brief DD: "Sol replay is just a walk, but it happens on its own"). GH-free. It moves <see cref="WalkState"/> through its own Next, so playing is a way of walking and
  /// holds no position of its own. It stops on arriving at the last step, where the bar says so, and Back and Next still work.
  /// </summary>
  internal sealed class WalkPlay
  {
    public bool Playing { get; private set; }

    /// <summary>Is there anything to play? Two steps at least, and no lesson: a Teaching step waits for its answer, and a timer would walk past the question.</summary>
    public static bool CanPlay(int steps, bool lesson) => steps >= 2 && !lesson;

    /// <summary>Play ran out at the last step. Goes when the person moves or plays again.</summary>
    public bool Ended { get; private set; }

    /// <summary>Play. At the last step there is nothing left to play, so it starts again from the first, and on a Walk that has only arrived it starts there too (Brief DH), since pressing Play is as plain an ask as pressing Next. Returns true when that moved the Walk. A Walk of one step has nowhere to go, and says so.</summary>
    public bool Start(WalkState walk)
    {
      if (walk == null || !walk.Live) return false;
      if (walk.Count < 2) { Ended = true; return false; }
      var restarted = (!walk.Started || walk.Index >= walk.Count - 1) && walk.GoTo(0);
      Playing = true;
      Ended = false;
      return restarted;
    }

    /// <summary>The timer fired: on to the next step. Returns true when the Walk moved. Arriving at the last step ends the play.</summary>
    public bool Advance(WalkState walk)
    {
      if (!Playing || walk == null) return false;
      var moved = walk.Next();
      if (!moved || walk.Index >= walk.Count - 1) { Playing = false; Ended = true; }
      return moved;
    }

    /// <summary>Any key, click or Esc. Returns whether it was playing.</summary>
    public bool Pause()
    {
      var was = Playing;
      Playing = false;
      return was;
    }

    /// <summary>The person moved the Walk themselves, so it is no longer at the end of a play.</summary>
    public void UserMoved() => Ended = false;

    /// <summary>What a Walk key does when it arrived during play. Esc pauses and leaves nothing: the next Esc leaves. An arrow is what the person asked for, so it moves as well as pausing.</summary>
    public static WalkKey WhilePlaying(WalkKey key) => key == WalkKey.Leave ? WalkKey.None : key;
  }

  /// <summary>What the plugin read off one component: its name as the canvas shows it, whether it has ever computed, and the milliseconds it took the last time it did.</summary>
  internal sealed class TimeFact
  {
    public Guid Guid;
    public string Label;
    public bool Recorded;
    public double Ms;
  }

  /// <summary>
  /// ONE READ OF THE SELECTION'S SOLVE TIMES (Brief DD), and every sentence said about it. GH-free.
  ///
  /// THE LAW IT KEEPS IS U23'S. A component's time is what it took THE LAST TIME IT COMPUTED, and one that did not expire in the latest solve keeps its earlier time. So the figures do not come from
  /// one solve and their sum is not a solve's duration: the selection's total is called the selection's, never the solve's. AND A ZERO IS NEVER READ ON ITS OWN (#843): a component reads 0 both when it ran
  /// under a millisecond and when it never ran, opposite answers to "is this slow?", so <see cref="TimeFact.Recorded"/> is carried through and the two are said differently.
  /// </summary>
  internal sealed class WalkTimes
  {
    /// <summary>Under this a component takes no measurable time: Grasshopper's reading is whole milliseconds.</summary>
    public const double MeasurableMs = 1;

    /// <summary>How many never-ran components a sentence names before it counts the rest.</summary>
    public const int MaxNeverRanNamed = 3;

    private readonly Dictionary<Guid, TimeFact> _facts = new Dictionary<Guid, TimeFact>();

    public WalkTimes(IEnumerable<TimeFact> selection)
    {
      foreach (var f in selection ?? Enumerable.Empty<TimeFact>())
        if (f != null) _facts[f.Guid] = f;
      TotalMs = _facts.Values.Where(Measured).Sum(f => f.Ms);
      Slowest = _facts.Values.Where(Measured).OrderByDescending(f => f.Ms).ThenBy(f => f.Guid).FirstOrDefault();
    }

    public double TotalMs { get; }

    /// <summary>The longest component in the selection, which sets the bars' full width. Null when nothing took measurable time.</summary>
    public TimeFact Slowest { get; }

    public static bool Measured(TimeFact f) => f != null && f.Recorded && f.Ms >= MeasurableMs;

    public TimeFact Of(Guid id) => _facts.TryGetValue(id, out var f) ? f : null;

    /// <summary>This component's share of the bar's full width: its time against the longest in the selection, 0 when it took no measurable time.</summary>
    public double Fraction(Guid id)
    {
      var f = Of(id);
      return Measured(f) && Slowest != null ? f.Ms / Slowest.Ms : 0;
    }

    /// <summary>The step's line, one plugin sentence from a fixed template, or null when nothing of the step was read (the Walk's own line says its components have gone).</summary>
    public string StepLine(IEnumerable<Guid> step)
    {
      var here = (step ?? Enumerable.Empty<Guid>()).Select(Of).Where(f => f != null).ToList();
      if (here.Count == 0) return null;

      var ran = here.Where(f => f.Recorded).ToList();
      var slowest = ran.Where(Measured).OrderByDescending(f => f.Ms).ThenBy(f => f.Guid).FirstOrDefault();
      var line =
        slowest != null ? "Slowest here: " + slowest.Label + ", " + Milliseconds(slowest.Ms) + " of the selection's " + Milliseconds(TotalMs) + "." :
        ran.Count > 0 ? "Nothing here takes measurable time." : null;

      var never = NeverRan(here);
      if (line == null) return never;
      return never == null ? line : line + " " + never;
    }

    /// <summary>The summary's line, for the whole selection. Says what the total is, because a sum of last times is easily taken for a solve's duration.</summary>
    public string SummaryLine()
    {
      if (_facts.Count == 0) return null;
      var neverCount = _facts.Values.Count(f => !f.Recorded);
      if (neverCount == _facts.Count) return "Nothing in this selection has run yet.";

      var line = Slowest != null
        ? "This selection's components last took " + Milliseconds(TotalMs) + ". Slowest: " + Slowest.Label + ", " + Milliseconds(Slowest.Ms) + "."
        : "Nothing in this selection takes measurable time.";
      return neverCount == 0 ? line : line + " " + neverCount + (neverCount == 1 ? " component has" : " components have") + " never run.";
    }

    private static string NeverRan(IReadOnlyList<TimeFact> here)
    {
      var never = here.Where(f => !f.Recorded).Select(f => f.Label).ToList();
      if (never.Count == 0) return null;
      var named = string.Join(", ", never.Take(MaxNeverRanNamed));
      return "Never ran: " + named + (never.Count > MaxNeverRanNamed ? " and " + (never.Count - MaxNeverRanNamed) + " more." : ".");
    }

    private static string Milliseconds(double ms) => Math.Round(ms).ToString("n0", CultureInfo.InvariantCulture) + " ms";
  }

  /// <summary>
  /// WHAT THE STEP LINE SAYS OF THE READ ITSELF: the times, with when they were read, or that there are none yet. The read waits for a solve to end, and THE WAIT HAS A CLOCK (U47, U48): past
  /// <see cref="MaxWaitSeconds"/> it says so rather than waiting in silence, and a solve that ends later still reads.
  /// </summary>
  internal static class WalkTimeLine
  {
    public const double MaxWaitSeconds = 30;

    public const string Waiting = "Waiting for the solve to settle.";
    public const string GaveUp = "The solve has not settled, so there are no times yet.";

    public static bool WaitIsOver(DateTime sinceUtc, DateTime nowUtc) => (nowUtc - sinceUtc).TotalSeconds >= MaxWaitSeconds;

    /// <summary>When the times were read, which is not when anything computed: a Walk may start long after the last solve, and a component may have computed long before it.</summary>
    public static string Clock(DateTime readAtLocal) => "Read at " + readAtLocal.ToString("HH:mm", CultureInfo.InvariantCulture) + ", after the last solve.";

    public static string For(WalkTimes times, IEnumerable<Guid> step, DateTime? readAtLocal, bool waiting, bool gaveUp)
    {
      if (times == null) return gaveUp ? GaveUp : waiting ? Waiting : null;
      var line = times.StepLine(step);
      if (line == null) return null;
      return readAtLocal.HasValue ? line + " " + Clock(readAtLocal.Value) : line;
    }
  }

  /// <summary>Where the time bars go, in document units (ADR-056). Each sits under its component, inside the Walk's hole, and the component's own width is the full bar, so the longest in the selection fills its component.</summary>
  internal static class TimeBars
  {
    public const float Gap = 3f, Height = 3f, DotSize = 4f;

    /// <summary>The shortest bar drawn: a component that took measurable time is never drawn as if it had not.</summary>
    public const float MinLength = 3f;

    /// <summary>The annotation ink at low opacity, of 255.</summary>
    public const int InkAlpha = 150;

    public static List<RectangleF> Bars(WalkTimes times, IEnumerable<(Guid Id, RectangleF? Bounds)> step)
    {
      var bars = new List<RectangleF>();
      foreach (var (id, bounds) in step ?? Enumerable.Empty<(Guid, RectangleF?)>())
      {
        if (times == null || !bounds.HasValue) continue;
        var share = times.Fraction(id);
        if (share <= 0) continue;
        var b = bounds.Value;
        bars.Add(new RectangleF(b.X, b.Bottom + Gap, Math.Max(MinLength, b.Width * (float)share), Height));
      }
      return bars;
    }

    /// <summary>One dot under each component that has never run. It has no time to draw, and the step's line says the words.</summary>
    public static List<RectangleF> Dots(WalkTimes times, IEnumerable<(Guid Id, RectangleF? Bounds)> step)
    {
      var dots = new List<RectangleF>();
      foreach (var (id, bounds) in step ?? Enumerable.Empty<(Guid, RectangleF?)>())
      {
        if (times == null || !bounds.HasValue) continue;
        var f = times.Of(id);
        if (f == null || f.Recorded) continue;
        dots.Add(new RectangleF(bounds.Value.X, bounds.Value.Bottom + Gap, DotSize, DotSize));
      }
      return dots;
    }
  }
}
