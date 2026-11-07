using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// BRIEF CU. Under "Do not ask", Haiku 4.5 ended turns on two real files with "it asked twice for your approval of a build,
  /// and neither request could be put to you": two requests failed, and nothing said why, though the refusal it wrote to the
  /// model carried a code. Every code a request can come back with now has its own sentence, read off the refusal's structure
  /// and never its message. These tests hold that against the closed set and against the request path's own source, so the law
  /// is a test and not a reminder.
  /// </summary>
  public class RefusalNoteTests
  {
    private const string Marker = "MODEL-ADDRESSED-MARKER";
    private const string BuildAsk = "{\"kind\":\"build\"}";

    /// <summary>The sentence a code nobody mapped gets, so "not the generic line" is measured and not retyped.</summary>
    private static string Generic(string args = BuildAsk) =>
      AgentLoopCore.RefusedRequestNote(args, ToolResult.Fail("a_code_nobody_mapped", Marker).ToJson());

    private static string NoteFor(string code, JsonObject detail = null, string args = BuildAsk) =>
      AgentLoopCore.RefusedRequestNote(args, ToolResult.Fail(code, Marker, detail).ToJson());

    // ---- one sentence per code, word for word ------------------------------------------------------------------------------

    [Theory]
    [InlineData(ToolErrors.RegionNotApproved, "Dwight stopped: it asked twice to approve regions outside the build you approved.")]
    [InlineData(ToolErrors.AlreadyApproved, "Dwight stopped: it asked twice to approve a build that this turn was already carrying out.")]
    [InlineData(ToolErrors.UnknownRegion, "Dwight stopped: it asked twice to approve a build of regions that are not on the canvas.")]
    [InlineData(ToolErrors.UnknownId, "Dwight stopped: it asked twice to approve a build, and the last request named a component that is not on the canvas.")]
    [InlineData(ToolErrors.RetiredId, "Dwight stopped: it asked twice to approve a build, and the last request named a component that is no longer on the canvas.")]
    [InlineData(ToolErrors.RegionOccupied, "Dwight stopped: it asked twice to build into a region that already holds work it would have had to remove.")]
    [InlineData(ToolErrors.RegionAttemptsExhausted, "Dwight stopped: it asked twice to build a region that had already used its attempts this turn.")]
    [InlineData(ToolErrors.StaleDocument, "Dwight stopped: it asked twice to approve a build while the canvas had changed under it.")]
    [InlineData(ToolErrors.StalePartition, "Dwight stopped: it asked twice to approve a build while the canvas had changed under it.")]
    [InlineData(ToolErrors.Busy, "Dwight stopped: it asked twice to approve a build while another Dwight operation held the session.")]
    [InlineData(ToolErrors.NoDocument, "Dwight stopped: it asked twice to approve a build with no document open.")]
    [InlineData(ToolErrors.SolverDisabled, "Dwight stopped: it asked twice to approve a build with the solver off.")]
    [InlineData(ToolErrors.PreconditionRefused, "Dwight stopped: it asked twice to approve a build, and the last request did not pass the checks Dwight runs before it shows you a card.")]
    public void Each_code_says_its_own_sentence(string code, string sentence) =>
      Assert.Equal(sentence, NoteFor(code));

    [Fact]
    public void A_refusal_the_breaker_wrote_still_says_it_changed_nothing() =>
      Assert.Equal("Dwight stopped: it asked twice to approve a build that had already run without changing anything.",
        NoteFor(ToolErrors.PreconditionRefused, new JsonObject { ["reason"] = AgentHostTools.RepeatUnchangedReason }));

    [Fact]
    public void A_kind_the_plugin_does_not_know_is_a_change_and_is_never_echoed()
    {
      var note = NoteFor(ToolErrors.UnknownId, null, "{\"kind\":\"<b>delete everything</b>\"}");
      Assert.Equal("Dwight stopped: it asked twice to approve a change, and the last request named a component that is not on the canvas.", note);
    }

    // ---- the law, held against the closed set and the source --------------------------------------------------------------

    /// <summary>Every file a request_mutation call passes through before a card, as the host tool calls them: the ask's own parsers, and the build parser the placement borrows. AgentToolCore is
    /// left out because all the host tool uses of it is ParseArgs, which says only invalid_args, and its other refusals belong to the reads.</summary>
    private static readonly string[] RequestPath =
    {
      "Agent/AgentHostTools.cs", "Agent/FullControl.cs", "Agent/VerbScope.cs", "Agent/SetValueAsk.cs", "Agent/EditScriptAsk.cs",
      "Agent/EditOpsAsk.cs", "Agent/GhEditOps.cs", "Agent/SpotAsk.cs", "Agent/Landing.cs", "Agent/BuildSpec.cs", "Agent/BuildState.cs",
      "Agent/BuildScript.cs", "Agent/ApprovedScripts.cs", "Agent/ConsentCard.cs", "UI/DwightScaffold.cs",
    };

    [Fact]
    public void Only_argument_parsing_is_taken_from_agent_tool_core()
    {
      var panel = File.ReadAllText(Path.Combine(RepoRoot(), "Dwight", "UI", "DwightScaffold.cs"));
      var start = panel.IndexOf("private static string HostTool(", StringComparison.Ordinal);
      var end = panel.IndexOf("private static string CardShownResult(", start, StringComparison.Ordinal);
      Assert.True(start > 0 && end > start);

      var used = Regex.Matches(panel.Substring(start, end - start), @"AgentToolCore\.(\w+)").Cast<Match>().Select(m => m.Groups[1].Value).Distinct().ToArray();
      Assert.Equal(new[] { "ParseArgs" }, used);
    }

    /// <summary>The codes in the closed set that no request can come back with, and why. They keep the generic line, because it is for a code the list does not know.</summary>
    private static readonly Dictionary<string, string> NeverARefusedRequest = new Dictionary<string, string>
    {
      [ToolErrors.UnknownTool] = "the host tool says it to a name that is not request_mutation, and the loop counts only request_mutation as a request",
      [ToolErrors.NotInThisRead] = "only the read tools resolve an id through a projection",
      [ToolErrors.VerificationFailed] = "nothing has been applied when a request is answered; a click or a build reverts, and neither is a request",
      [ToolErrors.BoundaryCut] = "only a rebuild in build_region meets a boundary",
    };

    private static List<string> ClosedSet() =>
      typeof(ToolErrors).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.IsLiteral && f.FieldType == typeof(string))
        .Select(f => (string)f.GetRawConstantValue()).ToList();

    /// <summary>The code each <c>ToolResult.Fail</c> in the request path's source names, the conditional form included.</summary>
    private static List<string> CodesOnTheRequestPath()
    {
      var names = new HashSet<string>(StringComparer.Ordinal);
      foreach (var file in RequestPath)
      {
        var path = Path.Combine(new[] { RepoRoot(), "Dwight" }.Concat(file.Split('/')).ToArray());
        foreach (Match call in Regex.Matches(File.ReadAllText(path), @"ToolResult\.Fail\(([^,]*),"))
          foreach (Match name in Regex.Matches(call.Groups[1].Value, @"ToolErrors\.(\w+)"))
            names.Add(name.Groups[1].Value);
      }
      return names.Select(n =>
      {
        var field = typeof(ToolErrors).GetField(n, BindingFlags.Public | BindingFlags.Static);
        Assert.True(field != null, "the request path names ToolErrors." + n + ", which this test cannot read");
        return (string)field.GetRawConstantValue();
      }).ToList();
    }

    [Fact]
    public void Every_code_the_request_path_can_return_has_a_sentence_other_than_the_generic()
    {
      var codes = CodesOnTheRequestPath();

      // the check has something to check: what the host tool, the build parser and the id checks each put there
      Assert.Contains(ToolErrors.AlreadyApproved, codes);
      Assert.Contains(ToolErrors.UnknownComponentType, codes);
      Assert.Contains(ToolErrors.NoDocument, codes);
      Assert.Contains(ToolErrors.RetiredId, codes);

      foreach (var code in codes.Where(c => c != ToolErrors.UnknownTool))
        Assert.True(NoteFor(code) != Generic(), code + " is on the request path and gets the generic sentence");
    }

    /// <summary>A constant added to the closed set is red here until it has a sentence or a reason it can never reach one.</summary>
    [Fact]
    public void Every_code_in_the_closed_set_has_a_sentence_or_a_reason_no_request_returns_it()
    {
      var set = ClosedSet();
      Assert.Contains(ToolErrors.InvalidArgs, set);   // the reflection found the set

      foreach (var code in set)
      {
        if (NeverARefusedRequest.ContainsKey(code))
          Assert.True(NoteFor(code) == Generic(), code + " has a sentence now: take it off the list of codes no request returns");
        else
          Assert.True(NoteFor(code) != Generic(), code + " has no sentence and no reason a request cannot return it");
      }
    }

    /// <summary>The reasons above are claims, so they are measured: of the listed codes, only the host tool's own guard is in the request path's source.</summary>
    [Fact]
    public void A_code_said_never_to_reach_a_request_is_not_on_the_request_path() =>
      Assert.Equal(new[] { ToolErrors.UnknownTool }, CodesOnTheRequestPath().Intersect(NeverARefusedRequest.Keys).ToArray());

    /// <summary>That one guard is true only while the loop counts a request by this name.</summary>
    [Fact]
    public void The_host_tools_unknown_tool_is_said_only_to_a_name_that_is_not_a_request()
    {
      var panel = File.ReadAllText(Path.Combine(RepoRoot(), "Dwight", "UI", "DwightScaffold.cs"));
      var guard = panel.IndexOf("if (!string.Equals(name, AgentHostTools.RequestMutation, StringComparison.OrdinalIgnoreCase))", StringComparison.Ordinal);
      Assert.True(guard > 0);
      Assert.Equal(panel.IndexOf("ToolErrors.UnknownTool", StringComparison.Ordinal), panel.IndexOf("ToolErrors.UnknownTool", guard, StringComparison.Ordinal));
      Assert.Equal(panel.LastIndexOf("ToolErrors.UnknownTool", StringComparison.Ordinal), panel.IndexOf("ToolErrors.UnknownTool", StringComparison.Ordinal));
      Assert.False(AgentHostTools.IsRequest("anything_else"));
    }

    /// <summary>A code passed as a bare string would never meet the closed set, and so never these tests.</summary>
    [Fact]
    public void No_refusal_in_the_product_passes_its_code_as_a_bare_string()
    {
      var source = Path.Combine(RepoRoot(), "Dwight");
      var found = Directory.GetFiles(source, "*.cs", SearchOption.AllDirectories)
        .Where(f => Regex.IsMatch(File.ReadAllText(f), @"ToolResult\.Fail\(\s*""")).ToList();
      Assert.Empty(found);
    }

    // ---- the name the request asked for ---------------------------------------------------------------------------------------

    private static ToolResult RefusedAsNotInstalled(string type)
    {
      var args = new JsonObject { ["components"] = new JsonArray(new JsonObject { ["ref"] = "r", ["type"] = type }) };
      Assert.False(BuildSpecParser.TryParse(args, _ => false, _ => new[] { "NEAREST-MARKER" }, id => IdState.Alive, out _, out var error));
      Assert.Equal(ToolErrors.UnknownComponentType, error.Code);
      return error;
    }

    /// <summary>The parser's own refusal, because a reader is tested against the writer's real output.</summary>
    [Fact]
    public void A_component_that_is_not_installed_is_named_in_quotes_from_the_key_the_plugin_wrote()
    {
      var note = AgentLoopCore.RefusedRequestNote(BuildAsk, RefusedAsNotInstalled("Bambu Profile").ToJson());

      Assert.Equal("Dwight stopped: it asked twice to approve a build, and the last request named a component that is not installed in this Grasshopper: \"Bambu Profile\".", note);
      Assert.DoesNotContain("NEAREST-MARKER", note);
      Assert.DoesNotContain("No installed component", note);   // the parser's message, written for the model
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void With_no_name_to_give_the_sentence_ends_at_grasshopper(bool notAString)
    {
      var detail = notAString ? new JsonObject { ["asked"] = 12 } : new JsonObject();
      Assert.Equal("Dwight stopped: it asked twice to approve a build, and the last request named a component that is not installed in this Grasshopper.",
        NoteFor(ToolErrors.UnknownComponentType, detail));
    }

    [Fact]
    public void A_name_the_model_invented_cannot_reach_past_one_short_line()
    {
      var note = NoteFor(ToolErrors.UnknownComponentType, new JsonObject { ["asked"] = "line one\nline two\t" + new string('x', 400) });

      Assert.DoesNotContain("\n", note);
      Assert.Contains("\"line one line two xxx", note);
      Assert.True(note.Length < 220, "the note ran to " + note.Length + " characters");
      Assert.EndsWith("...\".", note);
      Assert.DoesNotContain("<p class=\"hint\">", Dwight.Llm.ProviderLimit.NoteHtml(note));   // no line break, so no quieter half
    }

    /// <summary>Escaping is the destination's (#335): the note carries the name as written, and the panel's door encodes it.</summary>
    [Fact]
    public void A_name_holding_markup_is_inert_where_the_note_is_rendered()
    {
      var note = NoteFor(ToolErrors.UnknownComponentType, new JsonObject { ["asked"] = "<img src=x>" });

      Assert.Contains("\"<img src=x>\"", note);
      var html = Dwight.Llm.ProviderLimit.NoteHtml(note);
      Assert.DoesNotContain("<img", html);
      Assert.Contains("&lt;img src=x&gt;", html);
    }

    /// <summary>The name is carried only where the ruling allows it. The same key elsewhere is a count, a list, or a word the note has no business repeating.</summary>
    [Theory]
    [InlineData(ToolErrors.UnknownId)]
    [InlineData(ToolErrors.RetiredId)]
    [InlineData(ToolErrors.UnknownRegion)]
    [InlineData(ToolErrors.RegionOccupied)]
    [InlineData(ToolErrors.NoDocument)]
    [InlineData(ToolErrors.PreconditionRefused)]
    [InlineData(ToolErrors.InvalidArgs)]
    public void No_other_class_repeats_what_the_request_asked_for(string code) =>
      Assert.DoesNotContain("ASKED-MARKER", NoteFor(code, new JsonObject { ["asked"] = "ASKED-MARKER" }));

    // ---- a sentence is a claim, true of every refusal that reaches it --------------------------------------------------------

    private const string ConnectionPoint = "named a connection point";
    private const string LeftOut = "each time left out something Dwight needed";

    private static string NoteOf(ToolResult refusal, string args = BuildAsk) =>
      AgentLoopCore.RefusedRequestNote(args, refusal.ToJson());

    [Fact]
    public void A_port_the_component_does_not_have_is_a_connection_point_that_is_not_there()
    {
      Assert.False(BuildSpecParser.Port(new[] { "L", "N" }, new[] { "Length", "Count" }, "Sequence", "to", "input", out _, out var error));
      Assert.Equal("Dwight stopped: it asked twice to approve a build, and the last request named a connection point that is not on that component.", NoteOf(error));
    }

    /// <summary>These carry "available" too. One named no port and one named two, and neither named one that is not there.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("L")]
    public void A_wire_that_did_not_say_which_port_is_not_called_a_port_that_is_not_there(string given)
    {
      Assert.False(BuildSpecParser.Port(new[] { "L", "l" }, null, given, "to", "input", out _, out var error));
      Assert.NotNull(error.Detail["available"]);

      var note = NoteOf(error);
      Assert.DoesNotContain(ConnectionPoint, note);
      Assert.Contains(LeftOut, note);
    }

    [Fact]
    public void A_slider_rounding_is_not_called_a_connection_point()
    {
      var state = JsonNode.Parse("{\"min\":0,\"max\":1,\"value\":0.5,\"rounding\":\"nope\"}");
      Assert.False(BuildStateParser.TryParse(state, "Number Slider", "r", out _, out var error));
      Assert.NotNull(error.Detail["available"]);

      Assert.DoesNotContain(ConnectionPoint, NoteOf(error));
    }

    [Fact]
    public void A_kind_Dwight_does_not_have_is_named_as_that_and_never_echoed()
    {
      const string args = "{\"kind\":\"teleport\",\"why\":\"x\"}";
      Assert.False(AgentHostTools.TryParseRequest((JsonObject)JsonNode.Parse(args), out _, out _, out var error));
      Assert.NotNull(error.Detail["available"]);

      Assert.Equal("Dwight stopped: it asked twice to approve a change, and the last request asked for something Dwight cannot offer.", NoteOf(error, args));
    }

    [Fact]
    public void A_request_with_no_kind_left_something_out()
    {
      const string args = "{\"why\":\"x\"}";
      Assert.False(AgentHostTools.TryParseRequest((JsonObject)JsonNode.Parse(args), out _, out _, out var error));
      Assert.NotNull(error.Detail["available"]);

      var note = NoteOf(error, args);
      Assert.DoesNotContain(ConnectionPoint, note);
      Assert.Contains(LeftOut, note);
    }

    // ---- the precondition refusals a person can do something about ---------------------------------------------------------

    private const string DidNotPass = "did not pass the checks Dwight runs before it shows you a card";

    [Fact]
    public void Too_many_components_for_one_card_is_named_as_that()
    {
      var named = Enumerable.Range(0, SpotPlacement.MaxComponents + 1).Select(i => "Number Slider " + i).ToList();
      Assert.False(SpotPlacement.TryDescribe(named, "Spot 1", null, out _, out var error));

      Assert.Equal("Dwight stopped: it asked twice to approve a build, and the last request placed more components than one card can name.", NoteOf(error));
    }

    [Fact]
    public void Too_many_changes_for_one_card_is_named_as_that()
    {
      var ops = new JsonArray(Enumerable.Range(0, EditOpsAsk.MaxOps + 1).Select(i => (JsonNode)new JsonObject { ["op"] = "delete", ["id"] = i }).ToArray());
      Assert.False(EditOpsAsk.Validate(ops, null, out _, out var error));
      Assert.Equal(ToolErrors.PreconditionRefused, error.Code);

      Assert.Equal("Dwight stopped: it asked twice to approve a change, and the last request held more changes than one card can carry.", NoteOf(error, "{\"kind\":\"edit\"}"));
    }

    [Fact]
    public void A_name_several_components_share_is_named_with_the_name_and_without_the_forms()
    {
      var args = new JsonObject { ["components"] = new JsonArray(new JsonObject { ["ref"] = "r", ["type"] = "Rectangle" }) };
      Assert.False(BuildSpecParser.TryParse(args, _ => true, _ => new string[0], id => IdState.Alive, null, null,
        _ => new[] { "Curve > Primitive > Rectangle", "Params > Geometry > Rectangle" }, out _, out var error));
      Assert.Equal(ToolErrors.PreconditionRefused, error.Code);

      var note = NoteOf(error);
      Assert.Equal("Dwight stopped: it asked twice to approve a build, and the last request used a name that more than one installed component shares: \"Rectangle\".", note);
      Assert.DoesNotContain("Primitive", note);
    }

    /// <summary>A precondition with nothing to read is still true of the fallback: too many for a build, and a check that could not finish.</summary>
    [Fact]
    public void A_precondition_with_no_key_to_read_falls_back_to_the_check()
    {
      var args = new JsonObject { ["components"] = new JsonArray(Enumerable.Range(0, BuildSpecParser.MaxComponents + 1)
        .Select(i => (JsonNode)new JsonObject { ["ref"] = "r" + i, ["type"] = "Number Slider" }).ToArray()) };
      Assert.False(BuildSpecParser.TryParse(args, _ => true, _ => new string[0], id => IdState.Alive, out _, out var error));
      Assert.Equal(ToolErrors.PreconditionRefused, error.Code);

      Assert.Contains(DidNotPass, NoteOf(error));
    }

    /// <summary>The same key on another code says nothing here: a count is read only where the plugin wrote it as one.</summary>
    [Fact]
    public void The_card_bounds_are_read_only_from_a_precondition_refusal()
    {
      var note = NoteFor(ToolErrors.InvalidArgs, new JsonObject { ["most"] = 5, ["mostThatCanBeNamed"] = 5 });
      Assert.DoesNotContain("one card", note);
    }

    // ---- through the loop, as the Rhino row will see it -----------------------------------------------------------------------

    private static TurnResult TwiceRefused(ToolResult refusal) =>
      AgentTurnDriverTests.Drive(new AgentTurnDriverTests.ScriptedProvider(
        AgentTurnDriverTests.Calls("t1", AgentHostTools.RequestMutation, BuildAsk),
        AgentTurnDriverTests.Calls("t2", AgentHostTools.RequestMutation, BuildAsk)), call => refusal.ToJson());

    [Fact]
    public void A_build_naming_a_component_that_is_not_installed_twice_ends_with_its_name()
    {
      var result = TwiceRefused(RefusedAsNotInstalled("Bambu Profile"));

      Assert.Equal(2, result.RefusedRequests);
      Assert.Equal("Dwight stopped: it asked twice to approve a build, and the last request named a component that is not installed in this Grasshopper: \"Bambu Profile\".", result.Note);
    }

    [Fact]
    public void A_placement_too_big_for_a_card_twice_ends_saying_so()
    {
      Assert.False(SpotPlacement.TryDescribe(Enumerable.Range(0, SpotPlacement.MaxComponents + 1).Select(i => "c" + i).ToList(), "Spot 1", null, out _, out var error));

      Assert.Contains("placed more components than one card can name", TwiceRefused(error).Note);
    }

    // ---- two voices ------------------------------------------------------------------------------------------------------------

    /// <summary>TWO VOICES (#235), across the whole set: nothing of the message, the cause or a near match reaches the person.</summary>
    [Fact]
    public void Nothing_written_for_the_model_reaches_the_note_under_any_code()
    {
      var detail = new JsonObject { ["cause"] = "CAUSE-MARKER", ["nearestInstalled"] = new JsonArray("NEAREST-MARKER") };

      foreach (var code in ClosedSet())
      {
        var note = NoteFor(code, (JsonObject)JsonNode.Parse(detail.ToJsonString()));
        Assert.StartsWith("Dwight stopped:", note);
        foreach (var marker in new[] { Marker, "CAUSE-MARKER", "NEAREST-MARKER", ToolErrors.CauseOf(code) ?? Marker })
          Assert.DoesNotContain(marker, note);
      }
    }

    private static string RepoRoot()
    {
      var dir = new DirectoryInfo(AppContext.BaseDirectory);
      while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Dwight.sln"))) dir = dir.Parent;
      Assert.True(dir != null, "the repository root (Dwight.sln) could not be found from " + AppContext.BaseDirectory);
      return dir.FullName;
    }
  }
}
