using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Brief CI: kb_lookup reads a component's ports before it is on the canvas. The whole of the decision is GH-free, so the three sources, the clock and the
  /// one answer are driven here with a stated instance; the part that touches Grasshopper is read by its text, and the gate row in Rhino is the architect's.
  /// </summary>
  public class KbLookupPortsTests
  {
    private static Guid G(int n) => new Guid(n, 0, 0, new byte[8]);

    private static readonly List<InstalledType> Types = new List<InstalledType>
    {
      new InstalledType { Name = "HB Model", Category = "Honeybee", SubCategory = "2 :: Zones", Description = "Create a model.", Guid = G(1) },
      new InstalledType { Name = "LB Sunpath", Category = "Ladybug", SubCategory = "2 :: Analyze", Description = "Create a sunpath.", Guid = G(2) },
      new InstalledType { Name = "Rectangle", Category = "Curve", SubCategory = "Primitive", Description = "A rectangle.", Guid = G(3) },
      new InstalledType { Name = "Rectangle", Category = "Params", SubCategory = "Geometry", Description = "Rectangles.", Guid = G(4) },
    };

    private static JsonObject Ports(params string[] inputs) => LivePorts.Instantiated(inputs, new[] { "out" });

    private static JsonObject Canvas(params string[] inputs) => new JsonObject
    {
      ["inputs"] = new JsonArray(inputs.Select(i => (JsonNode)i).ToArray()),
      ["outputs"] = new JsonArray("out"),
      ["readFrom"] = LivePorts.ReadFrom,
    };

    /// <summary>A hand on every moving part: steps wait in a queue until pumped, and the clock fires when told.</summary>
    private sealed class Rig : IDisposable
    {
      public readonly Queue<Action> Posted = new Queue<Action>();
      public readonly List<ToolResult> Finished = new List<ToolResult>();
      public readonly List<string> Findings = new List<string>();
      public readonly List<string> Instantiated = new List<string>();
      public Func<InstalledType, JsonObject> Instance = i => Ports("a");
      public Func<InstalledType, JsonObject> OnCanvas = i => null;
      public Action Fire;
      public int ClockMs = -1;
      public bool ClockDisposed;

      public void Start(string json) =>
        KbPortsRun.Start((JsonObject)JsonNode.Parse(json), Types, _ => new[] { "HB Model" }, i => OnCanvas(i),
          i => { Instantiated.Add(i.Name); return Instance(i); }, a => Posted.Enqueue(a),
          (fire, ms) => { Fire = fire; ClockMs = ms; return this; }, r => Finished.Add(r), f => Findings.Add(f));

      public void Pump() { while (Posted.Count > 0) Posted.Dequeue()(); }

      public void Dispose() => ClockDisposed = true;

      public JsonObject Answer => (JsonObject)Assert.Single(Finished).Result;
    }

    private static Rig Run(string json, Action<Rig> arrange = null)
    {
      var rig = new Rig();
      arrange?.Invoke(rig);
      rig.Start(json);
      rig.Pump();
      return rig;
    }

    // ---- the three sources ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void An_instance_on_the_canvas_answers_and_nothing_is_instantiated()
    {
      var rig = Run("{\"type\":\"HB Model\"}", r => r.OnCanvas = _ => Canvas("_rooms"));

      Assert.Empty(rig.Instantiated);
      Assert.Equal(LivePorts.FromCanvas, (string)rig.Answer["portsFrom"]);
      Assert.Equal("_rooms", (string)rig.Answer["ports"]["inputs"][0]);
      Assert.Equal(LivePorts.ReadFrom, (string)rig.Answer["ports"]["readFrom"]);
      Assert.False(rig.Answer.ContainsKey("portsNote"));
      Assert.Equal(-1, rig.ClockMs);   // no instance to wait for, so no clock
    }

    [Fact]
    public void With_none_on_the_canvas_the_one_type_asked_about_is_instantiated_and_says_so()
    {
      var rig = Run("{\"type\":\"HB Model\"}", r => r.Instance = _ => Ports("_rooms", "_name_ (name)"));

      Assert.Equal(new[] { "HB Model" }, rig.Instantiated);
      Assert.Equal(LivePorts.FromInstantiated, (string)rig.Answer["portsFrom"]);
      Assert.Equal(new[] { "_rooms", "_name_ (name)" }, rig.Answer["ports"]["inputs"].AsArray().Select(n => (string)n).ToArray());
      Assert.Equal("a temporary instance of the installed type, never added to this definition", (string)rig.Answer["ports"]["readFrom"]);
      Assert.False(rig.Answer.ContainsKey("portsNote"));
      Assert.True(rig.ClockDisposed);
    }

    [Fact]
    public void A_fault_is_the_old_unavailable_plus_one_sentence_and_the_exceptions_words_stay_out_of_the_answer()
    {
      var rig = Run("{\"type\":\"HB Model\"}", r => r.Instance = _ => throw new InvalidOperationException("a secret path from inside a plugin"));

      Assert.Equal("unavailable", (string)rig.Answer["ports"]);
      Assert.Equal(LivePorts.FromUnavailable, (string)rig.Answer["portsFrom"]);
      Assert.Equal(KbPortsRun.CouldNotInstantiate, (string)rig.Answer["portsNote"]);
      Assert.DoesNotContain("secret", rig.Answer.ToJsonString());

      var finding = Assert.Single(rig.Findings);
      Assert.StartsWith("HB Model: InvalidOperationException", finding);   // recorded as a finding by name
    }

    [Fact]
    public void A_type_that_gives_no_instance_says_the_same()
    {
      var rig = Run("{\"type\":\"HB Model\"}", r => r.Instance = _ => null);

      Assert.Equal("unavailable", (string)rig.Answer["ports"]);
      Assert.Equal(KbPortsRun.CouldNotInstantiate, (string)rig.Answer["portsNote"]);
      Assert.Equal("HB Model: the installed type gave no instance", Assert.Single(rig.Findings));
    }

    [Fact]
    public void The_sentence_is_one_sentence_and_what_it_promises_is_what_a_build_does()
    {
      Assert.Equal("Dwight could not instantiate this component here to read its ports, so do not state them from memory: a build that names a wrong port is refused with the real list.",
                   KbPortsRun.CouldNotInstantiate);

      // The promise (#315), on the behaviour's side: the resolver refuses a wrong name and lists the real ports.
      Assert.False(BuildSpecParser.Port(new[] { "P", "R" }, new[] { "Plane", "Radius" }, "Diameter", "input", "Circle", out _, out var error));
      Assert.Equal(new[] { "P (Plane)", "R (Radius)" }, error.Detail["available"].AsArray().Select(n => (string)n).ToArray());
    }

    /// <summary>The harness has no way to instantiate, and neither does a caller that hands in nothing: no list is unavailable, with no claim that anything was tried.</summary>
    [Fact]
    public void Without_an_attempt_the_answer_says_unavailable_and_no_sentence()
    {
      var r = KbLookupReport.Build("HB Model", QualifiedNames.Resolve("HB Model", Types), null, _ => null);

      Assert.Equal("unavailable", (string)r.Result["ports"]);
      Assert.Equal(LivePorts.FromUnavailable, (string)r.Result["portsFrom"]);
      Assert.False(r.Result.AsObject().ContainsKey("portsNote"));
    }

    // ---- nothing is made that was not asked for ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"type\":\"HB Mode\"}")]
    [InlineData("{\"type\":\"a\",\"types\":[\"b\"]}")]
    public void A_refusal_makes_no_instance_and_keeps_its_answer(string json)
    {
      var rig = Run(json);

      Assert.Empty(rig.Instantiated);
      Assert.Equal(-1, rig.ClockMs);
      Assert.False(Assert.Single(rig.Finished).Ok);
    }

    [Fact]
    public void An_unknown_name_still_carries_its_nearest_names()
    {
      var rig = Run("{\"type\":\"HB Mode\"}");
      Assert.Equal("HB Model", (string)Assert.Single(rig.Finished).Detail["nearestInstalled"][0]);
    }

    [Fact]
    public void A_list_instantiates_each_type_once_and_each_row_has_its_own_answer()
    {
      var rig = Run("{\"types\":[\"HB Model\",\"HB Model\",\"LB Sunpath\",\"No such\"]}");

      Assert.Equal(new[] { "HB Model", "LB Sunpath" }, rig.Instantiated);
      var rows = rig.Answer["results"].AsArray();
      Assert.Equal(4, rows.Count);
      Assert.Equal(LivePorts.FromInstantiated, (string)rows[0]["portsFrom"]);
      Assert.Equal(LivePorts.FromInstantiated, (string)rows[1]["portsFrom"]);   // a JsonNode has one parent, so a shared one would throw here
      Assert.Equal(LivePorts.FromInstantiated, (string)rows[2]["portsFrom"]);
      Assert.False((bool)rows[3]["found"]);
    }

    [Fact]
    public void Each_candidate_of_an_ambiguous_name_is_read_for_itself()
    {
      var rig = Run("{\"type\":\"Rectangle\"}", r =>
      {
        r.OnCanvas = i => i.Guid == G(3) ? Canvas("P", "X") : null;
        r.Instance = _ => Ports("Rectangle");
      });

      Assert.Equal(new[] { "Rectangle" }, rig.Instantiated);   // only the one with nothing on the canvas
      var candidates = rig.Answer["candidates"].AsArray();
      var component = candidates.Single(c => (string)c["qualified"] == "Curve > Primitive > Rectangle");
      var param = candidates.Single(c => (string)c["qualified"] == "Params > Geometry > Rectangle");
      Assert.Equal(LivePorts.FromCanvas, (string)component["portsFrom"]);
      Assert.Equal(LivePorts.FromInstantiated, (string)param["portsFrom"]);
    }

    /// <summary>The brief: "Cache nothing across documents". A second call asks again.</summary>
    [Fact]
    public void Nothing_is_kept_between_calls()
    {
      var calls = 0;
      Func<InstalledType, JsonObject> counted = _ => { calls++; return Ports("a"); };
      Run("{\"type\":\"HB Model\"}", r => r.Instance = counted);
      Run("{\"type\":\"HB Model\"}", r => r.Instance = counted);
      Assert.Equal(2, calls);
    }

    // ---- the clock -----------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1, 5000)]
    [InlineData(2, 5250)]
    [InlineData(25, 11000)]
    [InlineData(1000, 15000)]
    [InlineData(0, 5000)]
    public void The_clock_is_a_few_seconds_for_one_and_has_a_ceiling(int instances, int ms)
    {
      Assert.Equal(ms, KbPortsRun.ClockMs(instances));
    }

    [Fact]
    public void The_clock_answers_for_a_step_still_in_flight_and_a_late_return_is_dropped()
    {
      var rig = new Rig();
      rig.Instance = _ =>
      {
        rig.Fire();                     // the clock runs out while the UI thread is inside this instance
        Assert.Single(rig.Finished);    // the caller already has its answer
        return Ports("late");
      };
      rig.Start("{\"types\":[\"HB Model\",\"LB Sunpath\"]}");
      Assert.Equal(5250, rig.ClockMs);
      rig.Pump();

      Assert.Equal(new[] { "HB Model" }, rig.Instantiated);   // the second is never started
      var rows = rig.Answer["results"].AsArray();
      foreach (var row in rows)
      {
        Assert.Equal("unavailable", (string)row["ports"]);
        Assert.Equal(KbPortsRun.CouldNotInstantiate, (string)row["portsNote"]);
      }
      Assert.Contains("no answer within 5250 ms: HB Model, LB Sunpath", rig.Findings);
      Assert.Contains("HB Model came back after the clock had run out, and that instance was dropped.", rig.Findings);
      Assert.Empty(rig.Posted);
    }

    /// <summary>The clock can run out between two steps, with the next one already queued on the UI thread: that one must not make an instance nobody is waiting for.</summary>
    [Fact]
    public void A_step_still_waiting_when_the_clock_runs_out_makes_no_instance()
    {
      var rig = new Rig();
      rig.Start("{\"type\":\"HB Model\"}");
      Assert.Single(rig.Posted);

      rig.Fire();
      rig.Pump();

      Assert.Empty(rig.Instantiated);
      Assert.Equal(KbPortsRun.CouldNotInstantiate, (string)rig.Answer["portsNote"]);
    }

    /// <summary>The race itself: the clock has seen an unfinished run and is on its way to answer when the last step answers first. One answer, because a tool that
    /// answers twice sets the loop's result under it.</summary>
    [Fact]
    public void The_clock_and_the_last_step_racing_still_answer_once()
    {
      var finished = new List<ToolResult>();
      var posted = new Queue<Action>();
      Action fire = null;
      KbPortsRun.Start((JsonObject)JsonNode.Parse("{\"type\":\"HB Model\"}"), Types, null, _ => null, _ => Ports("a"), posted.Enqueue,
        (f, ms) => { fire = f; return null; }, finished.Add,
        finding => { while (posted.Count > 0) posted.Dequeue()(); });   // the clock records its finding before it answers, so the step lands exactly there

      fire();

      Assert.Equal(LivePorts.FromInstantiated, (string)Assert.Single(finished).Result["portsFrom"]);
    }

    [Fact]
    public void A_clock_that_fires_after_the_answer_does_nothing()
    {
      var rig = Run("{\"type\":\"HB Model\"}");
      Assert.Single(rig.Finished);
      rig.Fire();
      Assert.Single(rig.Finished);
      Assert.Empty(rig.Findings);
    }

    [Fact]
    public void A_post_that_throws_still_answers_once()
    {
      var finished = new List<ToolResult>();
      KbPortsRun.Start((JsonObject)JsonNode.Parse("{\"type\":\"HB Model\"}"), Types, null, _ => null, _ => Ports("a"),
        _ => throw new InvalidOperationException("no application"), (f, ms) => null, finished.Add);

      Assert.Equal(KbPortsRun.CouldNotInstantiate, (string)Assert.Single(finished).Result["portsNote"]);
    }

    /// <summary>The positive control (#212) for the real clock: a thread held inside the instance cannot stop the answer, because the clock is on its own thread.</summary>
    [Fact]
    public void The_real_clock_answers_while_the_instance_is_still_stuck()
    {
      var inInstance = new ManualResetEventSlim(false);
      var release = new ManualResetEventSlim(false);
      var answered = new ManualResetEventSlim(false);
      ToolResult result = null;

      var stuck = new Thread(() => KbPortsRun.Start((JsonObject)JsonNode.Parse("{\"type\":\"HB Model\"}"), Types, null, _ => null,
        _ => { inInstance.Set(); release.Wait(30000); return Ports("late"); },
        step => step(), (fire, _) => KbPortsRun.RealClock(fire, 100),
        r => { result = r; answered.Set(); }));
      stuck.IsBackground = true;
      stuck.Start();

      Assert.True(inInstance.Wait(10000));
      Assert.True(answered.Wait(10000), "the clock did not answer while the instance was stuck");
      Assert.False(release.IsSet);
      Assert.Equal("unavailable", (string)result.Result["ports"]);
      Assert.Equal(KbPortsRun.CouldNotInstantiate, (string)result.Result["portsNote"]);

      release.Set();
      Assert.True(stuck.Join(10000));
    }

    // ---- the part that touches Grasshopper, read by its text --------------------------------------------------------------------------------

    private static string Repo(params string[] parts)
    {
      var dir = new DirectoryInfo(AppContext.BaseDirectory);
      while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Dwight.sln"))) dir = dir.Parent;
      Assert.True(dir != null);
      return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray())).Replace("\r\n", "\n");
    }

    [Theory]
    [InlineData("AddObject")]
    [InlineData("ScheduleSolution")]
    [InlineData("NewSolution")]
    [InlineData("ExpireSolution")]
    [InlineData("ExpirePreview")]
    [InlineData("CollectData")]
    [InlineData("ComputeData")]
    [InlineData("RecordUndoEvent")]
    [InlineData("MergeDocument")]
    public void The_instance_is_never_added_or_solved(string call)
    {
      Assert.DoesNotContain(call + "(", Code(Repo("Dwight", "Agent", "GhKbPorts.cs")));
    }

    /// <summary>Summaries may name what is not done, so the pin reads the code alone.</summary>
    private static string Code(string source) =>
      string.Join("\n", source.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    [Fact]
    public void The_shim_reads_ports_as_the_live_read_does_and_this_is_the_only_new_instance()
    {
      var shim = Repo("Dwight", "Agent", "GhKbPorts.cs");
      Assert.Contains("Instances.ComponentServer.EmitObjectProxy(info.Guid)?.CreateInstance()", shim);
      Assert.Contains("c.Params.Input.Select(AgentTools.LookupPortLabel)", shim);
      Assert.Contains("c.Params.Output.Select(AgentTools.LookupPortLabel)", shim);
      Assert.Contains("KbPortsRun.RealClock", shim);
      Assert.Contains("app.AsyncInvoke(step)", shim);   // the live read's own marshalling

      var tools = Repo("Dwight", "Agent", "AgentTools.cs");
      Assert.DoesNotContain("CreateInstance", tools);
      Assert.Contains("c.Params.Input.Select(LookupPortLabel)", tools);
    }

    [Fact]
    public void The_loop_comes_through_the_clocked_door_and_the_tool_runs_on_the_ui_thread()
    {
      var tools = Repo("Dwight", "Agent", "AgentTools.cs");
      var door = tools.IndexOf("GhKbPorts.Run(doc, args, GhKbPorts.NextOnUi, finish);", StringComparison.Ordinal);
      var fallThrough = tools.IndexOf("finish(Dispatch(name, argsJson, scope));", StringComparison.Ordinal);
      Assert.True(door > 0 && door < fallThrough, "kb_lookup must leave InvokeAsync before the synchronous dispatch");
      Assert.Contains("GhKbPorts.Run(doc, args, step => step(), r => answer = r);", tools);

      Assert.Contains("Ui(() => AgentTools.InvokeAsync(call.ToolName, call.ToolInputJson, req.Scope, json =>", Repo("Dwight", "Agent", "AgentLoop.cs"));
    }

    /// <summary>The core has no static state to go stale: the only statics are its constants and pure functions.</summary>
    [Fact]
    public void The_run_holds_nothing_static()
    {
      var fields = typeof(KbPortsRun).GetFields(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
      Assert.All(fields, f => Assert.True(f.IsLiteral, f.Name));
    }

    // ---- what we say about it (#315) ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_tool_says_where_ports_come_from_and_not_to_guess()
    {
      var d = AgentToolCatalog.DescriptionOf(AgentToolCatalog.KbLookup);
      Assert.Contains("from a temporary instance that never joins the definition and is never solved", d);
      Assert.Contains("`portsFrom` says which: canvas, instantiated or unavailable.", d);
      Assert.Contains("do not state port names from memory", d);

      Assert.Equal("canvas", LivePorts.FromCanvas);
      Assert.Equal("instantiated", LivePorts.FromInstantiated);
      Assert.Equal("unavailable", LivePorts.FromUnavailable);

      Assert.Contains("kb_lookup for what an installed component does and what its ports are.", Repo("Dwight", "Prompts", "Prompts.cs"));
    }

    [Fact]
    public void The_pages_say_what_the_tool_does()
    {
      var build = Repo("docs", "public", "build.md");
      Assert.Contains("the description and ports of any installed component", build);
      Assert.Contains("a temporary instance of the installed type, which is never added to your definition and never solved", build);
      Assert.Contains("it says the ports are unavailable", build);

      var privacy = Repo("docs", "public", "privacy.md");
      Assert.Contains("its ports, read from an instance on the canvas or from a temporary instance that never joins the definition.", privacy);
      Assert.DoesNotContain("its ports when an instance is on the canvas", privacy);
    }

    [Fact]
    public void The_harness_says_it_cannot_do_this()
    {
      Assert.Contains("so here a type not on the canvas reads unavailable where the product would answer", Repo("tools", "AgentEval", "Harness", "EvalModel.cs"));
    }
  }
}
