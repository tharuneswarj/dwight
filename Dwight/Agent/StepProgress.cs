using System;

namespace Dwight.Agent
{
  /// <summary>
  /// A LIVE LINE DURING A LONG STEP (U61). The tower session's step 31 ran for minutes with the footer still reading the last
  /// tool's name, and it looked stuck. This is the plugin's own account of a step in flight - "Still working - step 7, 48 s" -
  /// authored here, updated once a second by the caller, and replaced by the next tool's line or cleared with the turn.
  ///
  /// GH-free (ADR-091): a clock is handed in, so the formatting and the threshold are tested rather than watched. The caller owns
  /// the timer and the surface; this owns what the line says and when it is worth saying. Thread-safe, because the timer ticks
  /// on one thread while the loop reports tool calls on another.
  ///
  /// A STEP is one model reply and the tools it called; a tool call is the loop's signal that the next one has begun, so the
  /// step number is the tool calls so far plus one, and the seconds are those since the current step began. It is shown only
  /// once a step has run <see cref="ShowAfterSeconds"/>, so a quick step flickers nothing.
  /// </summary>
  internal sealed class StepProgress
  {
    /// <summary>A step shorter than this shows no line: the tool's own status already says what is happening.</summary>
    public const int ShowAfterSeconds = 5;

    private readonly Func<long> _nowMs;
    private readonly object _gate = new object();
    private int _step = 1;
    private long _stepStartMs;
    private bool _finished;

    public StepProgress(Func<long> nowMs)
    {
      _nowMs = nowMs ?? throw new ArgumentNullException(nameof(nowMs));
      _stepStartMs = _nowMs();
    }

    /// <summary>A tool was called: the step before it has ended and the next begins now.</summary>
    public void ToolCalled()
    {
      lock (_gate) { _step++; _stepStartMs = _nowMs(); }
    }

    /// <summary>The turn is over: no further line is ever produced, so a tick queued before the end cannot paint after it.</summary>
    public void Finish()
    {
      lock (_gate) { _finished = true; }
    }

    /// <summary>The line for this instant, or null when there is none to show (a young step, or a finished turn).</summary>
    public string Line()
    {
      lock (_gate)
      {
        if (_finished) return null;
        var seconds = (int)Math.Max(0, (_nowMs() - _stepStartMs) / 1000);
        return seconds < ShowAfterSeconds ? null : Format(_step, seconds);
      }
    }

    /// <summary>"Still working - step 7, 48 s"; past a minute, "Still working - step 7, 2 min 5 s". The house dash is " - ".</summary>
    public static string Format(int step, int seconds)
    {
      var s = Math.Max(0, seconds);
      var time = s < 60 ? s + " s" : (s / 60) + " min " + (s % 60) + " s";
      return "Still working - step " + Math.Max(1, step) + ", " + time;
    }
  }
}
