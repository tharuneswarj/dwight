using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Dwight.Session;

namespace Dwight.Session.Tests
{
  /// <summary>Records every call, so a script's effect is asserted as an exact call sequence.</summary>
  internal sealed class RecordingSink : IPanelSink
  {
    public readonly List<string> Calls = new List<string>();
    /// <summary>Ops named here throw when invoked - a WebView call CAN fail, and one failing op must not
    /// abandon the rest of the panel.</summary>
    public readonly HashSet<string> Throw = new HashSet<string>();

    private void Log(string name, params string[] args)
    {
      if (Throw.Contains(name)) throw new InvalidOperationException("sink blew up on " + name);
      Calls.Add(name + "(" + string.Join("|", args) + ")");
    }

    public void SetMarkdown(string markdown) => Log("SetMarkdown", markdown);
    public void SetContent(string text) => Log("SetContent", text);
    public void SetStatus(string text) => Log("SetStatus", text);
    public void SetSteps(string stepsJson) => Log("SetSteps", stepsJson);
    public void SetDeck(string deckJson) => Log("SetDeck", deckJson);
    public void SetFaults(string faultsJson) => Log("SetFaults", faultsJson);
    public void SetChat(string messagesJson) => Log("SetChat", messagesJson);
    public void SetOutcome(string text) => Log("SetOutcome", text);
    public void SetError(string message) => Log("SetError", message);
    public void ShowState(string kind, string title, string message) => Log("ShowState", kind, title, message);
  }

  /// <summary>
  /// WP-1 step 3: the replayable panel script (ADR-091; #161/#169).
  ///
  /// WHY THIS TYPE EXISTS. A recorded command used to replay by invoking a captured
  /// <c>Action&lt;IResultPanel&gt;</c> - a closure, and in several cases a STATEFUL one. A closure cannot be
  /// serialized, so durable history is not "add JSON to CommandHistory": it is replacing replay-by-delegate
  /// with replay-by-DATA. This is that data, and it is deliberately the SAME wire type the store persists
  /// (<see cref="StoredOp"/>), so there is no conversion layer in which the shown and the replayed could drift.
  ///
  /// THE SAFETY PROPERTY, ENFORCED BY THE TYPE: a script carries only strings. There is no field an affordance
  /// could travel in, so a persisted record CANNOT offer a mutation or a paid call - it is not discipline, it
  /// is unrepresentable.
  /// </summary>
  public class PanelScriptTests
  {
    // ---------------------------------------------------------------- dispatch

    [Fact]
    public void Every_whitelisted_op_dispatches_with_its_args()
    {
      var script = PanelScript.New()
        .Op(PanelScript.SetMarkdown, "# hi")
        .Op(PanelScript.SetContent, "plain")
        .Op(PanelScript.SetStatus, "Sonnet · 1.2s")
        .Op(PanelScript.SetSteps, "{\"steps\":[]}")
        .Op(PanelScript.SetFaults, "{\"faults\":[]}")
        .Op(PanelScript.SetChat, "[]")
        .Op(PanelScript.SetOutcome, "moved 12")
        .Op(PanelScript.SetError, "boom")
        .Op(PanelScript.ShowState, "notice", "Nothing is failing", "all clean")
        .Build();

      var sink = new RecordingSink();
      var report = PanelScript.Play(script, sink);

      Assert.Equal(9, report.Played);
      Assert.Equal(0, report.Skipped);
      Assert.Equal(new[]
      {
        "SetMarkdown(# hi)",
        "SetContent(plain)",
        "SetStatus(Sonnet · 1.2s)",
        "SetSteps({\"steps\":[]})",
        "SetFaults({\"faults\":[]})",
        "SetChat([])",
        "SetOutcome(moved 12)",
        "SetError(boom)",
        "ShowState(notice|Nothing is failing|all clean)",
      }, sink.Calls);
    }

    [Fact]
    public void Ops_replay_in_recorded_order()
    {
      // order is meaningful: a status set before its content would be overwritten by the content render.
      var script = PanelScript.New()
        .Op(PanelScript.SetMarkdown, "body")
        .Op(PanelScript.SetStatus, "meta")
        .Build();
      var sink = new RecordingSink();
      PanelScript.Play(script, sink);
      Assert.Equal("SetMarkdown(body)", sink.Calls[0]);
      Assert.Equal("SetStatus(meta)", sink.Calls[1]);
    }

    // ---------------------------------------------------------------- the closed whitelist

    [Theory]
    [InlineData("SetPreview")]        // #169 C1: dropped - no site emitted it; the panel method is gone too (#326)
    [InlineData("SetThinking")]       // transient loader state
    [InlineData("SetRunMeta")]
    [InlineData("SetStage")]
    [InlineData("AppendChat")]        // incremental; SetChat carries the whole thread
    [InlineData("SetSimplifyApplied")]// the live-affordance surface
    [InlineData("SetSimplifyOutcome")]
    [InlineData("")]
    public void Deliberately_excluded_ops_are_NOT_in_the_whitelist(string op)
    {
      // a closed set contains only what is used; this test is the lock on that.
      Assert.False(PanelScript.IsKnown(op));
    }

    [Fact]
    public void The_whitelist_is_exactly_the_ten_recorded_ops()
    {
      Assert.Equal(10, PanelScript.KnownOps.Count);   // Brief EI: SetDeck, a Learn run's deck and its answers, is the tenth
      Assert.True(PanelScript.IsKnown(PanelScript.SetDeck));
    }

    // ---------------------------------------------------------------- never a silently partial panel

    [Fact]
    public void An_unknown_op_is_skipped_and_COUNTED_never_thrown()
    {
      // a newer Dwight's op arriving in an older build must not blank the panel.
      var script = PanelScript.New()
        .Op(PanelScript.SetMarkdown, "body")
        .Op("SetHologram", "whatever")
        .Op(PanelScript.SetStatus, "meta")
        .Build();

      var sink = new RecordingSink();
      var report = PanelScript.Play(script, sink);

      Assert.Equal(2, report.Played);
      Assert.Equal(1, report.Skipped);
      Assert.Contains("SetHologram", report.SkippedOps);
      Assert.Equal(2, sink.Calls.Count);            // the known ops still ran, in order
    }

    [Fact]
    public void An_op_with_too_few_args_is_skipped_not_guessed_at()
    {
      var script = PanelScript.New().Op(PanelScript.ShowState, "notice").Build();   // needs 3
      var sink = new RecordingSink();
      var report = PanelScript.Play(script, sink);
      Assert.Equal(0, report.Played);
      Assert.Equal(1, report.Skipped);
      Assert.Empty(sink.Calls);
    }

    [Fact]
    public void A_sink_that_THROWS_on_one_op_does_not_abandon_the_rest()
    {
      // fail safe (mother doc 4.6): a WebView call can fail; the panel must still render what it can.
      var script = PanelScript.New()
        .Op(PanelScript.SetMarkdown, "body")
        .Op(PanelScript.SetStatus, "meta")
        .Build();

      var sink = new RecordingSink();
      sink.Throw.Add("SetMarkdown");
      var report = PanelScript.Play(script, sink);

      Assert.Equal(1, report.Played);
      Assert.Equal(1, report.Skipped);
      Assert.Equal("SetStatus(meta)", Assert.Single(sink.Calls));
    }

    [Fact]
    public void The_skip_note_is_honest_when_something_was_lost_and_absent_when_nothing_was()
    {
      var clean = PanelScript.Play(PanelScript.New().Op(PanelScript.SetStatus, "x").Build(), new RecordingSink());
      Assert.Null(clean.Note);

      var lossy = PanelScript.Play(PanelScript.New().Op("Nope").Build(), new RecordingSink());
      Assert.NotNull(lossy.Note);
      Assert.Contains("1", lossy.Note);
      Assert.Contains("couldn't be restored", lossy.Note);
    }

    [Fact]
    public void The_skip_note_pluralizes()
    {
      var r = PanelScript.Play(PanelScript.New().Op("A").Op("B").Build(), new RecordingSink());
      Assert.Contains("2 parts", r.Note);
    }

    // ---------------------------------------------------------------- degenerate input

    [Fact]
    public void Null_and_empty_scripts_are_no_ops_not_throws()
    {
      Assert.Equal(0, PanelScript.Play(null, new RecordingSink()).Played);
      Assert.Equal(0, PanelScript.Play(new List<StoredOp>(), new RecordingSink()).Played);
    }

    [Fact]
    public void A_null_sink_is_tolerated()
    {
      var ex = Record.Exception(() => PanelScript.Play(PanelScript.New().Op(PanelScript.SetStatus, "x").Build(), null));
      Assert.Null(ex);
    }

    [Fact]
    public void Null_entries_and_null_args_are_tolerated()
    {
      var ops = new List<StoredOp>
      {
        null,
        new StoredOp { Op = null, Args = null },
        new StoredOp { Op = PanelScript.SetStatus, Args = null },
        new StoredOp { Op = PanelScript.SetMarkdown, Args = new List<string> { null } },
      };
      var sink = new RecordingSink();
      var ex = Record.Exception(() => PanelScript.Play(ops, sink));
      Assert.Null(ex);
    }

    // ---------------------------------------------------------------- the structural guarantee

    [Fact]
    public void No_part_of_a_script_can_carry_a_DELEGATE()
    {
      // THE load-bearing test. The rule "a persisted record never carries a mutation or paid-call offer" is
      // enforced HERE, in the type, rather than by remembering it at twelve call sites.
      foreach (var t in new[] { typeof(StoredOp), typeof(PlayReport) })
      {
        foreach (var f in t.GetFields())
          Assert.False(typeof(Delegate).IsAssignableFrom(f.FieldType), t.Name + "." + f.Name + " is a delegate");
        foreach (var p in t.GetProperties())
          Assert.False(typeof(Delegate).IsAssignableFrom(p.PropertyType), t.Name + "." + p.Name + " is a delegate");
      }
    }

    [Fact]
    public void A_script_carries_only_strings()
    {
      foreach (var p in typeof(StoredOp).GetProperties())
        Assert.True(p.PropertyType == typeof(string) || p.PropertyType == typeof(List<string>),
                    "StoredOp." + p.Name + " is neither string nor List<string>");
    }

    // ---------------------------------------------------------------- the integration that matters

    [Fact]
    public void A_script_survives_the_STORE_and_replays_identically()
    {
      // The whole point of step 3: what is persisted must replay. Build -> Write -> Read -> Play, and the call
      // sequence must match the same script played directly.
      var script = PanelScript.New()
        .Op(PanelScript.SetFaults, "{\"lead\":\"2 issues\"}")
        .Op(PanelScript.SetStatus, "Sonnet · 0.8s")
        .Build();

      var direct = new RecordingSink();
      PanelScript.Play(script, direct);

      var rec = new SessionRecord
      {
        Commands = new List<StoredCommand>
        {
          new StoredCommand { Action = "Diagnose", Script = script, LiveClass = "content" },
        },
      };
      var readBack = SessionStoreCore.Read(SessionStoreCore.Write(rec));
      var replayed = new RecordingSink();
      PanelScript.Play(Assert.Single(readBack.Record.Commands).Script, replayed);

      Assert.Equal(direct.Calls, replayed.Calls);
    }

    [Fact]
    public void A_ShowState_script_never_persists_an_ACTION_command()
    {
      // A persisted "retry" actionCmd without its handler is a dead button - a panel state the canvas
      // contradicts. ShowState records three args, full stop.
      var script = PanelScript.New()
        .Op(PanelScript.ShowState, "error", "Dwight hit a problem", "the key was rejected")
        .Build();

      var json = SessionStoreCore.Write(new SessionRecord
      {
        Commands = new List<StoredCommand> { new StoredCommand { Action = "Explain", Script = script } },
      });

      Assert.DoesNotContain("retry", json);
      Assert.Equal(3, Assert.Single(script).Args.Count);
    }
  }
}
