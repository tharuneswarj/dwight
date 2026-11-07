using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Grasshopper.Kernel;
using Dwight.Data;

namespace Dwight.Agent
{
  /// <summary>
  /// FULL CONTROL, the part that touches Grasshopper and Rhino (U90). It runs the approved script on the UI thread, inside the
  /// document's own scheduled solution (guardrail 1), measures what changed, and reports - it does not verify.
  ///
  /// THE RUNTIME IS REACHED BY REFLECTION, with an up-front probe, exactly as <c>ScriptComponentShim</c> reaches the script components:
  /// <c>Rhino.Runtime.Code</c> ships in Rhino's own folder and is not an SDK we can reference. The route is
  /// <c>Rhino.Runtime.Code.RhinoCode.RunScript(string text, RunContext context)</c>, the same entry the Rhino 8 script editor uses.
  /// The language is chosen by the source's own first line (<see cref="FullControl.Shebang"/>), so what runs is Python 3 and nothing else.
  /// If the route is not there Dwight says so and runs NOTHING: it does not fall back to the older IronPython 2 runtime, because a
  /// script the user read as Python 3 is not the same script in another language.
  ///
  /// WHAT ONE CTRL-Z COVERS, honestly. A script can change the document by any route the API offers, and Grasshopper records an undo
  /// event only for what goes through its own undo calls. Dwight records one event, named by the purpose, for every object the script
  /// ADDED to the canvas. What the script changed some other way, deleted, or did to the Rhino document or outside Rhino is not
  /// recorded, and the card says so before the user approves.
  /// </summary>
  internal static class GhRunScript
  {
    /// <summary>How long after the script returns Dwight waits for the solution it caused before it reads the document again. A
    /// solution that never ends is reported with what could be read at once, never waited on.</summary>
    private const int SolveWaitMs = 20_000;

    public static void Run(GH_Document doc, string source, string purpose, Action<RunOutcome> done)
    {
      var finished = false;
      Action<RunOutcome> finish = o => { if (finished) return; finished = true; try { done?.Invoke(o); } catch { } };

      if (doc == null) { finish(new RunOutcome { Unavailable = "There is no open definition to run the script on." }); return; }

      try
      {
        doc.ScheduleSolution(5, d =>
        {
          try
          {
            // PROBE FIRST, while nothing has been touched: a Rhino whose runtime moved produces an honest notice, not a half-run script.
            var route = RhinoPython3.Probe(out var unavailable);
            if (route == null) { finish(new RunOutcome { Unavailable = unavailable }); return; }

            var before = Snapshot(d);
            var knownBefore = new System.Collections.Generic.HashSet<Guid>(d.Objects.Select(o => o.InstanceGuid));

            var started = System.Diagnostics.Stopwatch.StartNew();
            // U99: the runtime reads the language from the first line, so it is handed the EXECUTABLE source - the approved text with the
            // "#! python 3" header added when it has none (FullControl.ExecutableSource). Handed the card's text as received, every script
            // raised CodeLanguageNotFoundException before it ran (discriminated on Rhino.Runtime.Code 8.36.26293). Idempotent: a source that
            // already carries a header is returned as it is.
            route.Run(FullControl.ExecutableSource(source), out var stdout, out var stderr, out var exception);
            started.Stop();
            // U104: the runtime counts the shebang and the preamble Dwight added, so its positions are rewritten to lines of the source on
            // the card ("(at [5:1])" is line 5 of what the user read); a position inside Dwight's own lines says so.
            exception = FullControl.UserPositions(exception, source);

            // One undo event, named by the purpose, for what the script ADDED. It is recorded straight after the run, before the
            // solution the run causes, so a Ctrl-Z returns the canvas to the objects it had.
            var addedCount = 0;
            var recordId = Guid.Empty;
            try
            {
              var added = d.Objects.Where(o => !knownBefore.Contains(o.InstanceGuid)).ToList();
              addedCount = added.Count;
              if (added.Count > 0)
              {
                // Brief AP: the record's id, kept at the source, with the delta History shows for a Full control run.
                var now = Snapshot(d);
                var delta = FullControl.Delta(before.Components, now.Components, "component") + ", " +
                            FullControl.Delta(before.Wires, now.Wires, "wire") + ", " + FullControl.Delta(before.Groups, now.Groups, "group");
                Dwight.Session.ChangeLog.NoteFullControl(d, recordId = d.UndoUtil.RecordAddObjectEvent(UndoName(purpose), added), UndoName(purpose), delta);
              }
            }
            catch { }

            var immediate = Snapshot(d);
            var outcome = new RunOutcome
            {
              Before = before, After = immediate, Stdout = stdout, Stderr = stderr, Exception = exception, Seconds = started.Elapsed.TotalSeconds,
              Added = addedCount, RecordId = recordId,
            };
            // U101: whether the record stands is MEASURED - its id against the top of the undo stack when the run is reported - so the
            // report's Ctrl-Z clause is never said for a record that is not there.
            Action measureUndo = () =>
            {
              try { var top = d.UndoServer?.UndoGuids; outcome.TopAfter = top != null && top.Count > 0 ? top[0] : Guid.Empty; } catch { }
            };

            // The errors a script causes only exist once the definition has solved, so the final reading waits for the solution this one
            // triggers - and falls back to what is already readable if that solution does not end.
            var reported = false;
            GH_Document.SolutionEndEventHandler onEnd = null;
            onEnd = (sender, e) =>
            {
              try { d.SolutionEnd -= onEnd; } catch { }
              if (reported) return;
              reported = true;
              try { outcome.After = Snapshot(d); } catch { }
              measureUndo();
              finish(outcome);
            };
            d.SolutionEnd += onEnd;

            var fallback = new System.Threading.Timer(_ =>
            {
              if (reported) return;
              reported = true;
              try { d.SolutionEnd -= onEnd; } catch { }
              measureUndo();
              finish(outcome);
            }, null, SolveWaitMs, System.Threading.Timeout.Infinite);
            GC.KeepAlive(fallback);

          }
          catch (Exception ex)
          {
            finish(new RunOutcome { Unavailable = "Dwight could not run the script (" + ex.Message + ")." });
          }
        });
      }
      catch (Exception ex)
      {
        finish(new RunOutcome { Unavailable = "Dwight could not schedule the script on Rhino's thread (" + ex.Message + ")." });
      }
    }

    private static string UndoName(string purpose)
    {
      var p = (purpose ?? "").Trim().TrimEnd('.');
      if (p.Length > 60) p = p.Substring(0, 60) + "...";
      return "Dwight: " + (p.Length == 0 ? "run a script" : p);
    }

    /// <summary>Counts and one hash: the facts a report is computed from. Never geometry, never a value.</summary>
    public static DocSnapshot Snapshot(GH_Document d)
    {
      var wires = 0;
      var errors = 0;
      foreach (var o in d.Objects)
      {
        try
        {
          if (o is IGH_Param p) wires += p.SourceCount;
          else if (o is IGH_Component c) foreach (var ip in c.Params.Input) wires += ip.SourceCount;
          if (o is IGH_ActiveObject a && a.RuntimeMessageLevel == GH_RuntimeMessageLevel.Error) errors++;
        }
        catch { }
      }
      string fp = null;
      try { fp = BehaviourFingerprint.Of(d); } catch { }
      // Brief CA: the Rhino document too, counts and never geometry. A script that baked to a new layer read as nothing having happened, and the model said so.
      int? rhinoObjects = null, rhinoLayers = null;
      try
      {
        var rhino = Rhino.RhinoDoc.ActiveDoc;
        if (rhino != null)
        {
          rhinoObjects = rhino.Objects.ObjectCount(new Rhino.DocObjects.ObjectEnumeratorSettings
          {
            NormalObjects = true, LockedObjects = true, HiddenObjects = true, ActiveObjects = true, ReferenceObjects = false, DeletedObjects = false,
          });
          rhinoLayers = rhino.Layers.ActiveCount;
        }
      }
      catch { }
      return new DocSnapshot
      {
        RhinoObjects = rhinoObjects,
        RhinoLayers = rhinoLayers,
        Components = d.ObjectCount,
        Wires = wires,
        Groups = d.Objects.OfType<Grasshopper.Kernel.Special.GH_Group>().Count(),
        Errors = errors,
        Fingerprint = fp ?? "",
      };
    }
  }

  /// <summary>
  /// Rhino 8's Python 3 runtime, reached by reflection over <c>Rhino.Runtime.Code</c> and probed before use. Everything this class needs
  /// is resolved in <see cref="Probe"/> - the assembly, <c>RhinoCode.RunScript(string, RunContext)</c>, the context type and its
  /// two-argument constructor, its output and error streams - so a Rhino that renamed any of them gives one sentence and no run.
  /// </summary>
  internal sealed class RhinoPython3
  {
    private MethodInfo _runScript;
    private ConstructorInfo _ctxCtor;
    private PropertyInfo _outProp, _errProp, _recordDocUndoProp;

#if DEBUG
    /// <summary>A PROBE ROW FOR THE GATE, NOT A PROMISE (Brief AY). <c>RunContext.RecordDocumentUndo</c> is a read-write bool the route
    /// never sets; it most likely governs the RHINO document's undo, not Grasshopper's. Set this to true (by reflection from the bridge)
    /// to run one script with it on and write down what Rhino's own Undo then does. Debug builds only; no report sentence depends on it.</summary>
    internal static bool DebugRecordDocumentUndo = false;   // set by reflection from the bridge, never by code
#endif

    public static RhinoPython3 Probe(out string unavailable)
    {
      unavailable = null;
      try
      {
        var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Rhino.Runtime.Code");
        if (asm == null) { try { asm = Assembly.Load("Rhino.Runtime.Code"); } catch { } }
        if (asm == null)
        {
          unavailable = "Dwight cannot reach Rhino's Python 3 runtime on this Rhino (Rhino.Runtime.Code is not loaded).";
          return null;
        }

        var rhinoCode = asm.GetType("Rhino.Runtime.Code.RhinoCode");
        var ctxType = asm.GetType("Rhino.Runtime.Code.Execution.RunContext");
        if (rhinoCode == null || ctxType == null)
        {
          unavailable = "Dwight cannot reach Rhino's Python 3 runtime on this Rhino (its script API has changed).";
          return null;
        }

        var run = rhinoCode.GetMethods(BindingFlags.Public | BindingFlags.Static)
          .FirstOrDefault(m => m.Name == "RunScript" && m.GetParameters().Length == 2 &&
                               m.GetParameters()[0].ParameterType == typeof(string) && m.GetParameters()[1].ParameterType == ctxType);
        var ctor = ctxType.GetConstructor(new[] { typeof(bool), typeof(bool) });
        var outProp = ctxType.GetProperty("OutputStream");
        var errProp = ctxType.GetProperty("ErrorStream");
        if (run == null || ctor == null || outProp == null || errProp == null)
        {
          unavailable = "Dwight cannot reach Rhino's Python 3 runtime on this Rhino (RunScript or its output streams are not where they were).";
          return null;
        }
        // U102: the script's output is read from streams Dwight assigns, so both properties must take one.
        if (!ScriptStreams.Assignable(outProp) || !ScriptStreams.Assignable(errProp))
        {
          unavailable = UnassignableStreams;
          return null;
        }

        return new RhinoPython3 { _runScript = run, _ctxCtor = ctor, _outProp = outProp, _errProp = errProp, _recordDocUndoProp = ctxType.GetProperty("RecordDocumentUndo") };
      }
      catch (Exception ex)
      {
        unavailable = "Dwight cannot reach Rhino's Python 3 runtime (" + ex.Message + ").";
        return null;
      }
    }

    /// <summary>The probe's sentence when the context's streams cannot be assigned, so the output could not be read.</summary>
    public const string UnassignableStreams =
      "Dwight cannot reach Rhino's Python 3 runtime on this Rhino (RunContext.OutputStream and RunContext.ErrorStream cannot be assigned, so the script's output could not be read).";

    /// <summary>Run the source. <paramref name="stdout"/> and <paramref name="stderr"/> are what the script wrote, read from streams Dwight
    /// assigned before the run (U102); <paramref name="exception"/> is the script's own failure, verbatim, or null. Never throws.</summary>
    public void Run(string source, out string stdout, out string stderr, out string exception)
    {
      stdout = null; stderr = null; exception = null;
      object ctx = null;
      (MemoryStream Out, MemoryStream Err) streams = (null, null);
      try
      {
        ctx = _ctxCtor.Invoke(new object[] { true, true });
        // U102: the context's own streams are Rhino's command line, write-only. Dwight's own, assigned BEFORE the run, are what it reads.
        streams = ScriptStreams.Attach(ctx, _outProp, _errProp);
#if DEBUG
        if (DebugRecordDocumentUndo && _recordDocUndoProp != null && _recordDocUndoProp.CanWrite) _recordDocUndoProp.SetValue(ctx, true);
#endif
        _runScript.Invoke(null, new[] { source, ctx });
      }
      catch (TargetInvocationException tie)
      {
        exception = Describe(tie.InnerException ?? tie);
      }
      catch (Exception ex)
      {
        exception = Describe(ex);
      }

      // Read back what was written. The error stream's text is reported as "Error output" beside the printed output; a Python error that
      // arrived as an exception is already in <paramref name="exception"/>.
      stdout = ScriptStreams.Read(streams.Out);
      stderr = ScriptStreams.Read(streams.Err);
      try { (ctx as IDisposable)?.Dispose(); } catch { }
    }

    private static string Describe(Exception ex)
    {
      var text = ex.GetType().Name + ": " + ex.Message;
      try
      {
        var pos = ex.GetType().GetProperty("Position")?.GetValue(ex);   // ExecuteException names where the script failed
        if (pos != null && !string.IsNullOrWhiteSpace(pos.ToString())) text += " (at " + pos + ")";
      }
      catch { }
      return text;
    }

  }
}
