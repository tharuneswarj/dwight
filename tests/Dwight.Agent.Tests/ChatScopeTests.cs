using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Dwight.Session;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Brief EE: the chat keeps to its work, names a vision model, and points at the canvas. The scope rule's real test is the AgentEval row
  /// (tools/AgentEval/rows/rows-scope.json, run on Sonnet 5.5 and Gemini 3.8 Flash before and after the rule); the half here is the cheap one, that
  /// the words the rule rests on are in the prompt and at the web search tool. (#315)
  /// </summary>
  public class ChatScopeTests
  {
    // ---- unit 1: the scope rule is told to the model, in the prompt and at the tool ------------------------------------------------------------

    [Fact]
    public void The_prompt_carries_the_scope_rule_after_the_image_rule()
    {
      var prompt = Dwight.Prompts.Prompts.AgentMasterWith(AgentHostTools.CapabilitySentence());
      var image = prompt.IndexOf("10. An image the person attached", StringComparison.Ordinal);
      var scope = prompt.IndexOf("11. You work on Grasshopper, Rhino, computational design and the person's definition, and on nothing else.", StringComparison.Ordinal);
      Assert.True(image > 0 && scope > image);
      Assert.Contains("(the weather, the news, a recipe, general chat), say in one sentence that it is outside what Dwight does", prompt);
      Assert.Contains("Do not answer it, do not search for it, do not call any tool for it, do not raise a card for it.", prompt);
      Assert.Contains("A question about Grasshopper, Rhino, RhinoCommon, Python or C# for scripts, geometry, fabrication, or the trade the definition serves is inside your work.", prompt);
    }

    [Fact]
    public void The_web_search_descriptor_says_what_it_is_for_and_what_it_is_not()
    {
      var d = Dwight.Agent.WebSearch.Descriptor().Description;
      Assert.StartsWith("Search the web for Grasshopper, Rhino, RhinoCommon, plugin and computational-design facts. Not for news, weather or anything outside the definition's work.", d);
    }

    [Fact]
    public void The_scope_eval_row_is_four_turns_three_off_topic_and_one_on_and_names_both_models()
    {
      var rows = JsonNode.Parse(Read("tools", "AgentEval", "rows", "rows-scope.json")).AsArray();
      Assert.Equal(2, rows.Count);
      Assert.Equal(new[] { "claude-sonnet-5-5", "gemini-3.8-flash" }, rows.Select(r => r["model"].GetValue<string>()).ToArray());
      foreach (var r in rows)
      {
        Assert.Equal("scope", r["judge"].GetValue<string>());
        Assert.Equal("small-organised", r["fixture"].GetValue<string>());
        var turns = r["turns"].AsArray().Select(t => t.GetValue<string>()).ToArray();
        Assert.Equal(4, turns.Length);
        Assert.Contains("weather", turns[0]);
        Assert.Contains("news", turns[1]);
        Assert.Contains("recipe", turns[2]);
        Assert.Contains("Series", turns[3]);
      }
    }

    // ---- unit 2: a vision model suggested by name ----------------------------------------------------------------------------------------------

    private static ModelInfo M(string id, string name, string provider, bool agentic, bool vision, int pref = 100) =>
      new ModelInfo(id, name, provider, "Strong", 0, 0, 0, agentic: agentic, vision: vision, preference: pref);

    private static readonly ModelInfo[] Glm =
    {
      M("glm-5.3:cloud", "GLM-5.3 (cloud)", "OllamaCloud", true, false),
      M("glm-5.3-flash:cloud", "GLM-5.3 Flash (cloud)", "OllamaCloud", true, true),
      M("kimi-k3:cloud", "Kimi K3 (cloud)", "OllamaCloud", true, true),
      M("third:cloud", "Third (cloud)", "OllamaCloud", true, true),
      M("tiny:cloud", "Tiny (cloud)", "OllamaCloud", false, true),   // reads images but cannot drive the chat: the menu never offers it, so it is never named
    };

    private static readonly ModelInfo[] Everything =
      Glm.Concat(new[]
      {
        M("claude-opus-5-5", "Claude Opus 5.5", "Anthropic", true, true, 7),
        M("claude-sonnet-5-5", "Claude Sonnet 5.5", "Anthropic", true, true, 5),
        M("gpt-x", "GPT X", "OpenAI", true, true, 1),
      }).ToArray();

    [Fact]
    public void Two_vision_models_on_the_same_provider_are_named_in_the_menus_order_and_a_third_is_left_out()
    {
      var line = ChatImage.NoVisionLine("GLM-5.3 (cloud)", VisionAdvisor.Advise(Glm, Everything));
      Assert.Equal("GLM-5.3 (cloud) does not read images, so the image was not attached. GLM-5.3 Flash (cloud) and Kimi K3 (cloud) do; pick one from the model menu.", line);
    }

    [Fact]
    public void One_on_the_provider_is_named_alone()
    {
      var only = new[] { Glm[0], Glm[1] };
      Assert.Equal("GLM-5.3 (cloud) does not read images, so the image was not attached. GLM-5.3 Flash (cloud) does; pick it from the model menu.",
        ChatImage.NoVisionLine("GLM-5.3 (cloud)", VisionAdvisor.Advise(only, Everything)));
    }

    [Fact]
    public void None_on_the_provider_names_Anthropics_best_ranked_that_does()
    {
      var provider = new[] { Glm[0], Glm[4] };   // the vision row is not agentic, so the menu does not offer it
      Assert.Equal("GLM-5.3 (cloud) does not read images, so the image was not attached. No model on this provider reads images; Claude Sonnet 5.5 (Anthropic) does if you have a key.",
        ChatImage.NoVisionLine("GLM-5.3 (cloud)", VisionAdvisor.Advise(provider, Everything)));
    }

    [Fact]
    public void With_no_alternative_at_all_the_sentence_is_todays()
    {
      var none = new[] { M("a", "A", "P", true, false) };
      Assert.Equal("A does not read images, so the image was not attached. Pick a model that does from the model menu.",
        ChatImage.NoVisionLine("A", VisionAdvisor.Advise(none, none)));
      Assert.Equal("A does not read images, so this message was not sent and nothing was spent. Remove the image, or pick a model that does from the model menu.",
        ChatImage.NoVisionAtSendLine("A", VisionAdvisor.Advise(none, none)));
      Assert.Equal(ChatImage.NoVisionLine("A"), ChatImage.NoVisionLine("A", null));
    }

    [Fact]
    public void The_refusal_at_the_send_names_the_same_models_and_offers_to_remove_the_image()
    {
      Assert.Equal("GLM-5.3 (cloud) does not read images, so this message was not sent and nothing was spent. GLM-5.3 Flash (cloud) and Kimi K3 (cloud) do; pick one from the model menu. Or remove the image.",
        ChatImage.NoVisionAtSendLine("GLM-5.3 (cloud)", VisionAdvisor.Advise(Glm, Everything)));
    }

    [Fact]
    public void Both_doors_ask_the_advisor_and_neither_builds_the_sentence_itself()
    {
      var scaffold = Read("Dwight", "UI", "DwightScaffold.cs");
      Assert.Contains("ChatImage.NoVisionAtSendLine(model.DisplayName, VisionAdviceFor())", scaffold);
      Assert.Contains("ChatImage.NoVisionLine(model.DisplayName, VisionAdviceFor())", scaffold);
      Assert.Contains("VisionAdvisor.Advise(ModelCatalogue.ForProvider(DwightSettings.Current.EffectiveProvider), ModelCatalogue.All)", scaffold);
    }

    // ---- unit 4: show_on_canvas ---------------------------------------------------------------------------------------------------------------

    private static Guid G(int n) { var b = new byte[16]; b[0] = (byte)n; return new Guid(b); }

    private sealed class FakePointer : ICanvasPointer
    {
      public List<Guid> Lit; public bool? Dim; public List<Guid> Panned;
      public void Light(IReadOnlyList<Guid> guids, bool dim) { Lit = guids.ToList(); Dim = dim; }
      public void Pan(IReadOnlyList<Guid> guids) { Panned = guids.ToList(); }
    }

    private static (AgentIdRegistry ids, int a, int b) Registry()
    {
      var ids = new AgentIdRegistry();
      ids.Sync(new[] { G(1), G(2) });
      return (ids, ids.Issue(G(1)), ids.Issue(G(2)));
    }

    private static JsonObject Args(string json) => (JsonObject)JsonNode.Parse(json);

    [Fact]
    public void The_resolved_set_is_what_was_asked_and_it_is_lit_and_panned_to()
    {
      var (ids, a, b) = Registry();
      var fake = new FakePointer();
      var r = ShowOnCanvasCore.Run(Args("{\"ids\":[" + a + "," + b + "]}"), ids, g => true, fake);
      Assert.True(r.Ok);
      Assert.Equal(new[] { G(1), G(2) }, fake.Lit);
      Assert.Equal(new[] { G(1), G(2) }, fake.Panned);
      Assert.False(fake.Dim);
      var json = (JsonObject)r.Result;
      Assert.Equal(new[] { a, b }, json["shown"].AsArray().Select(n => n.GetValue<int>()).ToArray());
      Assert.True(json["panned"].GetValue<bool>());
    }

    [Fact]
    public void An_unknown_id_refuses_the_whole_call_with_the_real_list_and_lights_nothing()
    {
      var (ids, a, b) = Registry();
      var fake = new FakePointer();
      var r = ShowOnCanvasCore.Run(Args("{\"ids\":[" + a + ",999]}"), ids, g => true, fake);
      Assert.False(r.Ok);
      Assert.Equal(ToolErrors.UnknownId, r.Code);
      Assert.Contains("999", r.Message);
      Assert.Null(fake.Lit);   // nothing glows when any id is refused: never fewer components than were named
      Assert.Null(fake.Panned);
      Assert.Equal(new[] { a, b }, r.Detail["alive"].AsArray().Select(n => n.GetValue<int>()).ToArray());
    }

    [Fact]
    public void An_id_alive_in_the_registry_but_gone_from_the_canvas_is_refused_too()
    {
      var (ids, a, _) = Registry();
      var fake = new FakePointer();
      var r = ShowOnCanvasCore.Run(Args("{\"ids\":[" + a + "]}"), ids, g => false, fake);
      Assert.False(r.Ok);
      Assert.Null(fake.Lit);
    }

    [Fact]
    public void Dim_is_passed_through_and_a_call_without_it_leaves_the_wash_alone()
    {
      var (ids, a, _) = Registry();
      var dimmed = new FakePointer();
      Assert.True(ShowOnCanvasCore.Run(Args("{\"ids\":[" + a + "],\"dim\":true}"), ids, g => true, dimmed).Ok);
      Assert.True(dimmed.Dim);
      var plain = new FakePointer();
      Assert.True(ShowOnCanvasCore.Run(Args("{\"ids\":[" + a + "],\"dim\":false}"), ids, g => true, plain).Ok);
      Assert.False(plain.Dim);
      var omitted = new FakePointer();
      Assert.True(ShowOnCanvasCore.Run(Args("{\"ids\":[" + a + "]}"), ids, g => true, omitted).Ok);
      Assert.False(omitted.Dim);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"ids\":[]}")]
    [InlineData("{\"ids\":[\"x\"]}")]
    [InlineData("{\"ids\":[1],\"dim\":\"yes\"}")]
    public void Malformed_arguments_are_refused_as_invalid(string json)
    {
      var (ids, _, _) = Registry();
      var r = ShowOnCanvasCore.Run(Args(json), ids, g => true, new FakePointer());
      Assert.False(r.Ok);
      Assert.Equal(ToolErrors.InvalidArgs, r.Code);
    }

    [Fact]
    public void The_descriptor_is_a_read_the_prompt_names_and_every_capability_may_call()
    {
      var d = AgentToolCatalog.All().Single(t => t.Name == AgentToolCatalog.ShowOnCanvas);
      Assert.False(d.Mutates);
      Assert.False(d.Solves);
      Assert.Equal("Light the named components on the canvas so the person can see what you are talking about, optionally dimming the rest. Use it when you explain a part of the definition. It changes nothing.", d.Description);
      var schema = JsonNode.Parse(d.InputSchemaJson);
      Assert.Equal(new[] { "ids" }, schema["required"].AsArray().Select(n => n.GetValue<string>()).ToArray());
      Assert.NotNull(schema["properties"]["dim"]);
      foreach (TurnCapability c in Enum.GetValues(typeof(TurnCapability)))
        if (c != TurnCapability.RunScript) Assert.True(AgentLoopCore.IsGranted(c, AgentToolCatalog.ShowOnCanvas), c + " cannot call show_on_canvas");
      Assert.Contains("To point at components while you explain, call show_on_canvas with their ids.", AgentHostTools.CapabilitySentence());
    }

    [Fact]
    public void The_host_end_never_touches_the_selection_and_the_navigation_has_its_own_reason()
    {
      var host = Read("Dwight", "Agent", "GhShowOnCanvas.cs");
      Assert.Contains("CanvasGlow.ShowWalkStep(guids, null, _owner, dim)", host);
      Assert.Contains("CanvasNav.GoTo(guids, Dwight.UI.NavReason.ChatPointed)", host);
      Assert.DoesNotContain("Selected", host);   // glow only: the person's next action keeps operating on what they selected (#322)
      Assert.Contains("GhShowOnCanvas.Run(doc, scope, args)", Read("Dwight", "Agent", "AgentTools.cs"));
      Assert.Contains("CanvasGlow.ClearIfOwner(ScopeFor(threadId, doc));", Read("Dwight", "UI", "DwightScaffold.cs"));   // the glow ends with the person's next message
    }

    private static string Read(params string[] parts)
    {
      var dir = new DirectoryInfo(AppContext.BaseDirectory);
      while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Dwight.sln"))) dir = dir.Parent;
      Assert.True(dir != null);
      return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray())).Replace("\r\n", "\n");
    }
  }
}
