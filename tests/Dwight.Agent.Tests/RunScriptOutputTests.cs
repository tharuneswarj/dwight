using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Xunit;
using Dwight.Agent;

namespace Dwight.Agent.Tests
{
  /// <summary>Brief AY: the script's output reaches the report (U102), and the report's Ctrl-Z clause is measured from the record's id
  /// (U101).</summary>
  public class RunScriptOutputTests
  {
    private static DocSnapshot Snap(int c, int w, int g, int e, string fp) =>
      new DocSnapshot { Components = c, Wires = w, Groups = g, Errors = e, Fingerprint = fp };

    // A stand-in for Rhino's RunContext: read-write Stream properties, and the write-only stream it starts with.
    private sealed class FakeContext
    {
      public Stream OutputStream { get; set; } = new WriteOnlyStream();
      public Stream ErrorStream { get; set; } = new WriteOnlyStream();
      public Stream ReadOnlyThing { get; } = new MemoryStream();
    }

    private sealed class WriteOnlyStream : MemoryStream
    {
      public override bool CanRead => false;
      public override bool CanSeek => false;
    }

    private static PropertyInfo P(string name) => typeof(FakeContext).GetProperty(name);

    // ---------------------------------------------------------------- U102

    [Fact]
    public void The_text_the_script_writes_to_the_assigned_output_stream_reaches_the_report()
    {
      var ctx = new FakeContext();
      var streams = ScriptStreams.Attach(ctx, P("OutputStream"), P("ErrorStream"));
      Assert.Same(streams.Out, ctx.OutputStream);
      Assert.Same(streams.Err, ctx.ErrorStream);

      // the runtime writes to what it finds on the context, as Rhino's did: "1\n" from print(1)
      var bytes = Encoding.UTF8.GetBytes("1\n");
      ctx.OutputStream.Write(bytes, 0, bytes.Length);
      Assert.Equal("1\n", ScriptStreams.Read(streams.Out));   // read from the start, whatever the position
      Assert.Null(ScriptStreams.Read(streams.Err));            // nothing written: null, not ""

      var report = FullControl.ReportFor("Print one", new RunOutcome { Before = Snap(1, 1, 1, 0, "a"), After = Snap(1, 1, 1, 0, "a"), Stdout = ScriptStreams.Read(streams.Out) });
      Assert.Contains("Printed output: 1.", report);
    }

    [Fact]
    public void The_output_is_read_even_after_the_runtime_disposes_the_stream_and_a_byte_order_mark_is_dropped()
    {
      var s = new MemoryStream();
      var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("héllo")).ToArray();
      s.Write(bytes, 0, bytes.Length);
      s.Dispose();
      Assert.Equal("héllo", ScriptStreams.Read(s));
      Assert.Null(ScriptStreams.Read(null));
    }

    [Fact]
    public void The_probe_requires_streams_it_can_assign()
    {
      Assert.True(ScriptStreams.Assignable(P("OutputStream")));
      Assert.True(ScriptStreams.Assignable(P("ErrorStream")));
      Assert.False(ScriptStreams.Assignable(P("ReadOnlyThing")));   // get-only: Dwight could not read what was printed
      Assert.False(ScriptStreams.Assignable(null));
    }

    [Fact]
    public void The_error_stream_follows_the_printed_output_bounded_and_saying_so()
    {
      var r = FullControl.ReportFor("Run it", new RunOutcome { Before = Snap(1, 1, 1, 0, "a"), After = Snap(1, 1, 1, 0, "a"), Stdout = "ok", Stderr = "warning: x" });
      Assert.Contains("Printed output: ok. Error output: warning: x. ", r);
      var big = new string('e', FullControl.MaxStdoutChars + 10);
      var cut = FullControl.ReportFor("Run it", new RunOutcome { Before = Snap(1, 1, 1, 0, "a"), After = Snap(1, 1, 1, 0, "a"), Stderr = big });
      Assert.Contains("[cut at " + FullControl.MaxStdoutChars + " of " + big.Length + " characters]", cut);
      Assert.DoesNotContain("Error output", FullControl.ReportFor("Run it", new RunOutcome { Before = Snap(1, 1, 1, 0, "a"), After = Snap(1, 1, 1, 0, "a") }));
    }

    [Fact]
    public void The_route_assigns_its_own_streams_before_the_run_and_no_old_style_read_remains()
    {
      var gh = Read("Dwight", "Agent", "GhRunScript.cs");
      var attach = gh.IndexOf("streams = ScriptStreams.Attach(ctx, _outProp, _errProp);", StringComparison.Ordinal);
      var run = gh.IndexOf("_runScript.Invoke(null, new[] { source, ctx });", StringComparison.Ordinal);
      Assert.True(attach > 0 && run > attach, "the streams are assigned before RunScript");
      Assert.Contains("stdout = ScriptStreams.Read(streams.Out);", gh);
      Assert.Contains("stderr = ScriptStreams.Read(streams.Err);", gh);
      Assert.DoesNotContain("GetContents", gh);           // the old read that returned null every time
      Assert.DoesNotContain("ReadStream(", gh);
      Assert.Contains("ScriptStreams.Assignable(outProp)", gh);
      Assert.Contains("internal static bool DebugRecordDocumentUndo", gh);
      Assert.True(gh.IndexOf("#if DEBUG", StringComparison.Ordinal) < gh.IndexOf("internal static bool DebugRecordDocumentUndo", StringComparison.Ordinal));
      Assert.Equal("Dwight cannot reach Rhino's Python 3 runtime on this Rhino (RunContext.OutputStream and RunContext.ErrorStream cannot be assigned, so the script's output could not be read).",
        ReadConst(gh, "UnassignableStreams"));
    }

    // ---------------------------------------------------------------- U101

    private static readonly Guid Rec = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void The_three_Ctrl_Z_sentences_are_pinned()
    {
      Assert.Equal("One Ctrl-Z undoes the 3 objects the script added.", FullControl.UndoStandingLine(3));
      Assert.Equal("One Ctrl-Z undoes the 1 object the script added.", FullControl.UndoStandingLine(1));
      Assert.Equal("Nothing was recorded for Ctrl-Z: the script added no objects to this definition, and what it changed stays as it left it.", FullControl.UndoNotPushedLine);
      Assert.Equal("The undo record was not found after the run; what the script changed may stay.", FullControl.UndoNotFoundLine);
    }

    [Fact]
    public void The_clause_is_measured_from_the_records_id_against_the_top_of_the_stack()
    {
      Assert.Equal(RunUndoState.Standing, FullControl.UndoStateOf(new RunOutcome { Added = 3, RecordId = Rec, TopAfter = Rec }));
      Assert.Equal(RunUndoState.NotPushed, FullControl.UndoStateOf(new RunOutcome { Added = 0 }));                         // a rename adds nothing
      Assert.Equal(RunUndoState.NotFound, FullControl.UndoStateOf(new RunOutcome { Added = 2, RecordId = Rec, TopAfter = Guid.NewGuid() }));   // something above it
      Assert.Equal(RunUndoState.NotFound, FullControl.UndoStateOf(new RunOutcome { Added = 2, RecordId = Rec, TopAfter = Guid.Empty }));      // the stack dropped it
      Assert.Equal(RunUndoState.NotFound, FullControl.UndoStateOf(new RunOutcome { Added = 2 }));                          // objects added, the push failed
      Assert.Equal(RunUndoState.NotFound, FullControl.UndoStateOf(null));
    }

    [Fact]
    public void A_rename_only_script_hears_nothing_was_recorded_and_never_the_reassuring_sentence()
    {
      var rename = new RunOutcome { Before = Snap(4, 3, 0, 0, "a"), After = Snap(4, 3, 0, 0, "b"), Added = 0 };
      var report = FullControl.ReportFor("Rename both sliders", rename);
      Assert.EndsWith("Nothing was recorded for Ctrl-Z: the script added no objects to this definition, and what it changed stays as it left it.", report);
      Assert.DoesNotContain(FullControl.UndoClause, report);
      Assert.Equal(report, RunScriptApproval.PanelLine("Rename both sliders", rename));   // the panel and the tool result say one thing

      var added = new RunOutcome { Before = Snap(1, 0, 0, 0, "a"), After = Snap(4, 2, 0, 0, "b"), Added = 3, RecordId = Rec, TopAfter = Rec };
      Assert.EndsWith("One Ctrl-Z undoes the 3 objects the script added.", FullControl.ReportFor("Add three", added));

      var raised = new RunOutcome { Exception = "NameError: x", Before = Snap(1, 0, 0, 0, "a"), After = Snap(1, 0, 0, 0, "a"), Added = 0 };
      Assert.EndsWith(FullControl.UndoNotPushedLine, FullControl.ReportFor("Fail", raised));
      Assert.Contains("\"report\":", FullControl.ToResult("Fail", raised).ToJson());
      Assert.Contains(FullControl.UndoNotPushedLine, FullControl.ToResult("Fail", raised).ToJson());
    }

    [Fact]
    public void The_runner_measures_the_record_it_pushed_when_it_reports()
    {
      var gh = Read("Dwight", "Agent", "GhRunScript.cs");
      Assert.Contains("recordId = d.UndoUtil.RecordAddObjectEvent(UndoName(purpose), added)", gh);
      Assert.Contains("Added = addedCount, RecordId = recordId,", gh);
      Assert.Equal(2, gh.Split(new[] { "measureUndo();" }, StringSplitOptions.None).Length - 1);   // both report paths measure
    }

    // ---------------------------------------------------------------- helpers

    private static string Read(params string[] parts)
    {
      var dir = AppContext.BaseDirectory;
      while (dir != null && !File.Exists(Path.Combine(dir, "Dwight.sln"))) dir = Path.GetDirectoryName(dir);
      Assert.NotNull(dir);
      return File.ReadAllText(Path.Combine(new[] { dir }.Concat(parts).ToArray()));
    }

    private static string ReadConst(string source, string name)
    {
      var i = source.IndexOf("const string " + name, StringComparison.Ordinal);
      Assert.True(i >= 0, name + " not found");
      var q1 = source.IndexOf('"', i);
      var q2 = source.IndexOf("\";", q1 + 1, StringComparison.Ordinal);
      return source.Substring(q1 + 1, q2 - q1 - 1);
    }
  }
}
