using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using Dwight.Agent;
using Dwight.Llm;
using Dwight.Session;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Brief V - web search through the provider. Dwight never fetches the open web: the model searches through ITS provider,
  /// by the provider's own means, off by default.
  ///
  /// <para><b>What these witness.</b> Every wire shape here is typed from the provider's own page (cited on each fixture and in
  /// the code beside the shape), and the network is a FAKE: no test calls a provider, ollama.com or Google. So they witness
  /// that Dwight sends what the documentation says and reads what the documentation shows. They cannot witness the live wire,
  /// what a real search returns, or a real <c>pause_turn</c>; the fixtures say HAND-MADE for that reason and the sitting
  /// replaces them with captures.</para>
  /// </summary>
  [Collection(StreamFixtures.CaptureWindow)]
  public class WebSearchTests
  {
    // ---- the fake network ------------------------------------------------------------------------------------------

    private sealed class FakeNet
    {
      public readonly List<(string Url, List<KeyValuePair<string, string>> Headers, string Body)> Sent =
        new List<(string, List<KeyValuePair<string, string>>, string)>();
      public WebHttpReply Reply = new WebHttpReply { Status = 200, Body = "{}" };
      public Exception Throw;

      public WebHttpReply Post(string url, IReadOnlyList<KeyValuePair<string, string>> headers, string body, CancellationToken cancel)
      {
        Sent.Add((url, headers.ToList(), body));
        if (Throw != null) throw Throw;
        return Reply;
      }
      public WebPost AsPost => Post;
    }

    private static JsonObject Args(string json) => (JsonObject)JsonNode.Parse(json);

    private static JsonObject ResultOf(ToolResult r)
    {
      Assert.True(r.Ok, r.Message);
      return (JsonObject)r.Result;
    }

    // ---- which provider gets which path ----------------------------------------------------------------------------

    [Theory]
    [InlineData("Anthropic", "ProviderServerTool")]
    [InlineData("OllamaLocal", "OllamaEndpoint")]
    [InlineData("OllamaCloud", "OllamaEndpoint")]
    [InlineData("Google", "GoogleNotYet")]
    [InlineData("OpenAI", "NotAvailable")]
    [InlineData("SomethingElse", "Off")]
    public void Each_provider_takes_the_path_it_has_when_the_setting_is_on(string provider, string expected) =>
      Assert.Equal(Enum.Parse<WebSearchMode>(expected), WebSearch.ModeFor(provider, enabled: true));

    [Theory]
    [InlineData("Anthropic")] [InlineData("OllamaLocal")] [InlineData("OllamaCloud")] [InlineData("Google")] [InlineData("OpenAI")]
    public void With_the_setting_off_there_is_no_mode_no_sentence_and_no_plan(string provider)
    {
      Assert.Equal(WebSearchMode.Off, WebSearch.ModeFor(provider, enabled: false));
      Assert.Null(WebSearch.TurnFact(WebSearchMode.Off));
      Assert.Null(WebSearchPlan.For(provider, false, "m", () => "k", () => "k"));
    }

    [Fact]
    public void The_client_tool_is_advertised_for_ollama_and_google_only()
    {
      Assert.True(WebSearch.IsClientTool(WebSearchMode.OllamaEndpoint));
      Assert.True(WebSearch.IsClientTool(WebSearchMode.GoogleGroundedCall));
      Assert.False(WebSearch.IsClientTool(WebSearchMode.ProviderServerTool));   // the provider runs it in the request
      Assert.False(WebSearch.IsClientTool(WebSearchMode.NotAvailable));          // OpenAI: nothing advertised
      Assert.False(WebSearch.IsClientTool(WebSearchMode.Off));
    }

    // ---- what the model is told ------------------------------------------------------------------------------------

    [Fact]
    public void The_turn_start_sentence_is_the_ruled_wording_for_every_provider_that_can_search()
    {
      const string ruled = "Web search is on: you may search when a request depends on a real place, product or standard you are not sure of; " +
                           "say what you searched and cite what you used.";
      Assert.Equal(ruled, WebSearch.OnFact);
      foreach (var mode in new[] { WebSearchMode.OllamaEndpoint, WebSearchMode.ProviderServerTool, WebSearchMode.GoogleGroundedCall })
        Assert.Equal(ruled, WebSearch.TurnFact(mode));
    }

    [Fact]
    public void On_openai_the_sentence_says_it_is_not_available_yet_and_no_tool_is_advertised()
    {
      Assert.Equal("Web search is not available on OpenAI's chat endpoint yet; it will be with the Responses endpoint.",
        WebSearch.TurnFact(WebSearchMode.NotAvailable));
      var plan = WebSearchPlan.For("OpenAI", true, "m", () => "k", () => "k");
      Assert.NotNull(plan);
      Assert.False(plan.AddsClientTool);
      Assert.False(plan.UsesServerTool);
      Assert.Equal(WebSearch.NotYetFact, plan.Fact);
    }

    [Fact]
    public void On_google_the_grounded_call_is_gated_off_the_turn_is_told_why_and_no_tool_is_advertised()
    {
      Assert.Equal("Web search on Google is not on yet: Google asks that its search suggestions be shown with grounded answers, and the chat does not show them yet.",
        WebSearch.TurnFact(WebSearchMode.GoogleNotYet));
      var plan = WebSearchPlan.For("Google", true, "gemini-3.8-flash", () => "k", () => "k");
      Assert.NotNull(plan);
      Assert.False(plan.AddsClientTool);
      Assert.False(plan.UsesServerTool);
      Assert.Equal(WebSearch.GoogleNotYetFact, plan.Fact);
      Assert.DoesNotContain("\u2014", WebSearch.GoogleNotYetFact);
    }

    [Fact]
    public void The_system_text_carries_the_sentence_only_when_given_and_is_otherwise_unchanged()
    {
      var without = TurnSystemText.Compose("SITUATION", null, null, TurnCapability.ReadOnly, null);
      var withNull = TurnSystemText.Compose("SITUATION", null, null, TurnCapability.ReadOnly, null, null, null, null, null);
      var with = TurnSystemText.Compose("SITUATION", null, null, TurnCapability.ReadOnly, null, null, null, null, webSearchFact: WebSearch.OnFact);

      Assert.Equal(without, withNull);                       // off: byte-identical to before
      Assert.DoesNotContain("Web search", without);
      Assert.DoesNotContain("web_search", without);
      Assert.Equal(without + "\n\n" + WebSearch.OnFact, with);
    }

    // ---- the published descriptor, and the claims in it -----------------------------------------------------------

    [Fact]
    public void The_descriptor_is_a_read_that_requires_a_query_and_its_schema_parses()
    {
      var d = WebSearch.Descriptor();
      Assert.Equal("web_search", d.Name);
      Assert.False(d.Mutates);
      Assert.False(d.Solves);
      var schema = JsonNode.Parse(d.InputSchemaJson);
      Assert.Equal("object", schema["type"].GetValue<string>());
      Assert.Equal(new[] { "query" }, schema["required"].AsArray().Select(n => n.GetValue<string>()));
      Assert.NotNull(schema["properties"]["query"]);
      Assert.NotNull(schema["properties"]["max_results"]);
      Assert.Null(LlmSchemaCheck.Check(new LlmRequest
      {
        Tools = { new LlmTool { Name = d.Name, Description = d.Description, InputSchemaJson = d.InputSchemaJson } },
      }));
    }

    /// <summary>The conformance the published descriptor is held to (#217): what it says is legal to send is accepted, and
    /// what it requires is refused when missing.</summary>
    [Fact]
    public void The_handler_accepts_what_the_descriptor_publishes_and_refuses_what_it_requires()
    {
      var net = new FakeNet { Reply = new WebHttpReply { Status = 200, Body = "{\"results\":[]}" } };
      var ok = WebSearch.OllamaSearch(Args("{\"query\":\"eiffel tower height\",\"max_results\":3}"), "k", false, net.AsPost, default, out _);
      Assert.True(ok.Ok);

      var missing = WebSearch.OllamaSearch(Args("{}"), "k", false, net.AsPost, default, out _);
      Assert.False(missing.Ok);
      Assert.Equal(ToolErrors.InvalidArgs, missing.Code);
      var notText = WebSearch.OllamaSearch(Args("{\"query\":42}"), "k", false, net.AsPost, default, out _);
      Assert.Equal(ToolErrors.InvalidArgs, notText.Code);
      var blank = WebSearch.OllamaSearch(Args("{\"query\":\"   \"}"), "k", false, net.AsPost, default, out _);
      Assert.Equal(ToolErrors.InvalidArgs, blank.Code);
      var tooLong = WebSearch.OllamaSearch(Args("{\"query\":\"" + new string('a', WebSearch.MaxQueryChars + 1) + "\"}"), "k", false, net.AsPost, default, out _);
      Assert.Equal(ToolErrors.InvalidArgs, tooLong.Code);
      Assert.Single(net.Sent);   // only the one valid call reached the network
    }

    /// <summary>A PROMPT CLAIM ABOUT OUR OWN BEHAVIOUR CARRIES A TEST ON THE BEHAVIOUR'S SIDE (#315): the description says at most
    /// 5 results and excerpts cut to 1,500 characters; the handler does exactly that, and says when it cut.</summary>
    [Fact]
    public void The_description_claims_the_bounds_the_handler_really_applies()
    {
      var d = WebSearch.Descriptor().Description;
      Assert.Contains("At most 5 results", d);
      Assert.Contains("1,500 characters", d);
      Assert.Equal(5, WebSearch.MaxResults);
      Assert.Equal(1500, WebSearch.ContentCut);

      var results = new JsonArray();
      for (var i = 0; i < 8; i++)
        results.Add(new JsonObject { ["title"] = "T" + i, ["url"] = "https://example.invalid/" + i, ["content"] = new string('x', i == 0 ? 2000 : 100) });
      var net = new FakeNet { Reply = new WebHttpReply { Status = 200, Body = new JsonObject { ["results"] = results }.ToJsonString() } };

      var r = ResultOf(WebSearch.OllamaSearch(Args("{\"query\":\"q\"}"), "k", false, net.AsPost, default, out var searches));

      Assert.Equal(1, searches);
      var kept = r["results"].AsArray();
      Assert.Equal(5, kept.Count);
      Assert.Equal(3, r["omitted"].GetValue<int>());                                   // what it left out, stated
      Assert.Equal(1500, kept[0]["content"].GetValue<string>().Length);
      Assert.True(kept[0]["contentCut"].GetValue<bool>());                               // and which one it cut
      Assert.Null(kept[1]["contentCut"]);
      Assert.Contains("1500", r["cutNote"].GetValue<string>());
    }

    // ---- Ollama: the search endpoint -------------------------------------------------------------------------------

    [Fact]
    public void Ollama_search_posts_the_documented_body_to_the_documented_endpoint_with_the_key_in_a_header_only()
    {
      var net = new FakeNet
      {
        // docs.ollama.com/capabilities/web-search: {"results":[{"title","url","content"}]}
        Reply = new WebHttpReply { Status = 200, Body = "{\"results\":[{\"title\":\"Eiffel Tower\",\"url\":\"https://example.invalid/eiffel\",\"content\":\"330 m tall.\"}]}" },
      };

      var r = ResultOf(WebSearch.OllamaSearch(Args("{\"query\":\" eiffel tower height \"}"), "sk-secret", false, net.AsPost, default, out var searches));

      var sent = Assert.Single(net.Sent);
      Assert.Equal("https://ollama.com/api/web_search", sent.Url);
      Assert.Equal("{\"query\":\"eiffel tower height\",\"max_results\":5}", sent.Body);
      var header = Assert.Single(sent.Headers);
      Assert.Equal("Authorization", header.Key);
      Assert.Equal("Bearer sk-secret", header.Value);
      Assert.DoesNotContain("sk-secret", sent.Url + sent.Body);
      Assert.DoesNotContain("sk-secret", r.ToJsonString());   // and never in what the model reads
      Assert.Equal(1, searches);
      Assert.Equal("330 m tall.", r["results"][0]["content"].GetValue<string>());
      Assert.Contains("untrusted", r["note"].GetValue<string>());
    }

    [Theory]
    [InlineData(0, 1)] [InlineData(3, 3)] [InlineData(99, 5)]
    public void Ollama_search_never_asks_for_more_than_five(int asked, int sent)
    {
      var net = new FakeNet { Reply = new WebHttpReply { Status = 200, Body = "{\"results\":[]}" } };
      WebSearch.OllamaSearch(Args("{\"query\":\"q\",\"max_results\":" + asked + "}"), "k", false, net.AsPost, default, out _);
      Assert.Equal(sent, JsonNode.Parse(net.Sent.Single().Body)["max_results"].GetValue<int>());
    }

    [Fact]
    public void Ollama_local_with_no_key_is_refused_with_the_ruled_sentence_and_nothing_leaves_the_machine()
    {
      var net = new FakeNet();
      var r = WebSearch.OllamaSearch(Args("{\"query\":\"q\"}"), null, isLocal: true, net.AsPost, default, out var searches);

      Assert.False(r.Ok);
      Assert.Equal("Web search on Ollama local needs an Ollama account key: add one under Settings, or turn web search off.", r.Message);
      Assert.Empty(net.Sent);
      Assert.Equal(0, searches);
      Assert.Equal(r.Message, WebSearch.NeedsOllamaKeyLocal);

      var cloud = WebSearch.OllamaSearch(Args("{\"query\":\"q\"}"), "  ", isLocal: false, net.AsPost, default, out _);
      Assert.Equal(WebSearch.NeedsOllamaKeyCloud, cloud.Message);
      Assert.Empty(net.Sent);
    }

    [Theory]
    [InlineData(401, "rejected the key (401)")]
    [InlineData(403, "rejected the key (403)")]
    [InlineData(429, "rate limiting searches (429)")]
    [InlineData(500, "HTTP 500")]
    public void Ollama_search_failures_are_refusals_in_words_that_never_carry_the_key(int status, string expected)
    {
      var net = new FakeNet { Reply = new WebHttpReply { Status = status, Body = "denied for sk-secret" } };
      var r = WebSearch.OllamaSearch(Args("{\"query\":\"q\"}"), "sk-secret", false, net.AsPost, default, out var searches);

      Assert.False(r.Ok);
      Assert.Equal(ToolErrors.PreconditionRefused, r.Code);
      Assert.Contains(expected, r.Message);
      Assert.DoesNotContain("sk-secret", r.ToJson());
      Assert.Equal(0, searches);   // a search that did not run is not counted
    }

    [Fact]
    public void Ollama_search_that_cannot_be_reached_or_read_is_a_refusal_not_a_throw()
    {
      var unreachable = new FakeNet { Reply = new WebHttpReply { Error = "Could not reach ollama.com (boom). Check your connection." } };
      var a = WebSearch.OllamaSearch(Args("{\"query\":\"q\"}"), "k", false, unreachable.AsPost, default, out _);
      Assert.False(a.Ok);
      Assert.Contains("Could not reach ollama.com", a.Message);

      var garbage = new FakeNet { Reply = new WebHttpReply { Status = 200, Body = "<html>not json</html>" } };
      var b = WebSearch.OllamaSearch(Args("{\"query\":\"q\"}"), "k", false, garbage.AsPost, default, out _);
      Assert.False(b.Ok);
      Assert.Contains("not in a shape Dwight can read", b.Message);
    }

    [Fact]
    public void A_stop_during_a_search_is_not_swallowed_into_a_refusal()
    {
      var plan = WebSearchPlan.For("OllamaCloud", true, "m", () => "k", () => null, (u, h, b, c) => throw new OperationCanceledException());
      Assert.Throws<OperationCanceledException>(() => plan.Run("{\"query\":\"q\"}", default));
    }

    // ---- Google: the grounded sub-call -----------------------------------------------------------------------------

    /// <summary>HAND-MADE from ai.google.dev/api/generate-content (GroundingMetadata: groundingChunks[].web, webSearchQueries) -
    /// the response a grounded generateContent call is documented to return. Not a live capture.</summary>
    private const string GroundedReply =
      "{\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"The Sydney Opera House sails reach 65 m.\"}]}," +
      "\"groundingMetadata\":{\"webSearchQueries\":[\"sydney opera house height\",\"opera house sail dimensions\"]," +
      "\"groundingChunks\":[{\"web\":{\"uri\":\"https://example.invalid/a\",\"title\":\"a.example\"}}," +
      "{\"web\":{\"uri\":\"https://example.invalid/a\",\"title\":\"dup\"}},{\"web\":{\"uri\":\"https://example.invalid/b\",\"title\":\"b.example\"}}]}}]," +
      "\"usageMetadata\":{\"promptTokenCount\":120,\"candidatesTokenCount\":30,\"thoughtsTokenCount\":10}}";

    [Fact]
    public void The_google_grounded_call_carries_only_google_search_and_no_function_declarations()
    {
      var net = new FakeNet { Reply = new WebHttpReply { Status = 200, Body = GroundedReply } };

      ResultOf(WebSearch.GroundedSearch(Args("{\"query\":\"sydney opera house height\"}"), "g-secret", "gemini-3.8-flash", net.AsPost, default,
        out _, out _));

      var sent = Assert.Single(net.Sent);
      Assert.Equal("https://generativelanguage.googleapis.com/v1beta/models/gemini-3.8-flash:generateContent", sent.Url);
      var header = Assert.Single(sent.Headers);
      Assert.Equal("x-goog-api-key", header.Key);
      Assert.Equal("g-secret", header.Value);
      Assert.DoesNotContain("g-secret", sent.Url + sent.Body);

      var body = (JsonObject)JsonNode.Parse(sent.Body);
      var tools = body["tools"].AsArray();
      var only = Assert.Single(tools);
      Assert.Equal(new[] { "google_search" }, ((JsonObject)only).Select(kv => kv.Key));
      Assert.Empty(((JsonObject)only["google_search"]));
      Assert.DoesNotContain("functionDeclarations", sent.Body);
      Assert.Null(body["toolConfig"]);
      Assert.Equal("sydney opera house height", body["contents"][0]["parts"][0]["text"].GetValue<string>());
    }

    [Fact]
    public void The_grounded_answer_comes_back_as_bounded_text_with_its_sources_and_counts_the_queries_it_ran()
    {
      var net = new FakeNet { Reply = new WebHttpReply { Status = 200, Body = GroundedReply } };

      var r = ResultOf(WebSearch.GroundedSearch(Args("{\"query\":\"q\"}"), "k", "m", net.AsPost, default, out var usage, out var searches));

      Assert.Equal("The Sydney Opera House sails reach 65 m.", r["text"].GetValue<string>());
      Assert.True(r["grounded"].GetValue<bool>());
      Assert.Equal(2, searches);                                                         // two queries ran: two billed searches
      Assert.Equal(new[] { "https://example.invalid/a", "https://example.invalid/b" },   // duplicates folded
        r["sources"].AsArray().Select(s => s["url"].GetValue<string>()));
      Assert.Equal(2, r["searched"].AsArray().Count);
      Assert.Equal(120, usage.InputTokens);
      Assert.Equal(40, usage.OutputTokens);
      Assert.Contains("untrusted", r["note"].GetValue<string>());
    }

    [Fact]
    public void A_grounded_answer_is_cut_to_its_bound_and_says_so_and_an_ungrounded_one_says_that()
    {
      var long_ = new JsonObject
      {
        ["candidates"] = new JsonArray(new JsonObject
        {
          ["content"] = new JsonObject { ["parts"] = new JsonArray(
            new JsonObject { ["text"] = "private thinking", ["thought"] = true },
            new JsonObject { ["text"] = new string('y', 5000) }) },
        }),
      }.ToJsonString();
      var net = new FakeNet { Reply = new WebHttpReply { Status = 200, Body = long_ } };

      var r = ResultOf(WebSearch.GroundedSearch(Args("{\"query\":\"q\"}"), "k", "m", net.AsPost, default, out _, out var searches));

      Assert.Equal(WebSearch.GroundedTextCut, r["text"].GetValue<string>().Length);
      Assert.DoesNotContain("private thinking", r["text"].GetValue<string>());           // a thought part is not an answer
      Assert.Contains("cut", r["textCut"].GetValue<string>());
      Assert.False(r["grounded"].GetValue<bool>());
      Assert.Equal(0, searches);                                                         // nothing searched, nothing billed
      Assert.Contains("no sources", r["ungroundedNote"].GetValue<string>());
    }

    [Fact]
    public void The_grounded_call_refuses_without_a_key_before_any_network_and_on_an_empty_or_unreadable_reply()
    {
      var net = new FakeNet { Reply = new WebHttpReply { Status = 200, Body = "{\"candidates\":[{\"content\":{\"parts\":[]}}]}" } };
      Assert.False(WebSearch.GroundedSearch(Args("{\"query\":\"q\"}"), "", "m", net.AsPost, default, out _, out _).Ok);
      Assert.Empty(net.Sent);

      var empty = WebSearch.GroundedSearch(Args("{\"query\":\"q\"}"), "k", "m", net.AsPost, default, out _, out _);
      Assert.False(empty.Ok);
      Assert.Contains("no text", empty.Message);

      net.Reply = new WebHttpReply { Status = 200, Body = "[]" };
      Assert.Contains("not in a shape Dwight can read", WebSearch.GroundedSearch(Args("{\"query\":\"q\"}"), "k", "m", net.AsPost, default, out _, out _).Message);

      net.Reply = new WebHttpReply { Status = 429, Body = "" };
      Assert.Contains("rate limiting", WebSearch.GroundedSearch(Args("{\"query\":\"q\"}"), "k", "m", net.AsPost, default, out _, out _).Message);
    }

    // ---- the plan: counting, and folding into the turn's usage -----------------------------------------------------

    [Fact]
    public void A_plan_runs_the_right_handler_per_provider_and_folds_searches_and_subcall_tokens_into_the_turn()
    {
      var net = new FakeNet { Reply = new WebHttpReply { Status = 200, Body = GroundedReply } };
      var plan = WebSearchPlan.ForMode(WebSearchMode.GoogleGroundedCall, "gemini-3.8-flash", () => "ollama-key", () => "google-key", net.AsPost);

      var envelope = JsonNode.Parse(plan.Run("{\"query\":\"q\"}", default));
      Assert.True(envelope["ok"].GetValue<bool>());
      Assert.StartsWith("https://generativelanguage.googleapis.com/", net.Sent.Single().Url);
      Assert.Equal("google-key", net.Sent.Single().Headers.Single().Value);   // Google's key, not Ollama's

      var turn = new LlmUsage { InputTokens = 1000, OutputTokens = 100 };
      plan.FoldInto(turn);
      Assert.Equal(2, turn.WebSearchRequests);
      Assert.Equal(1120, turn.InputTokens);
      Assert.Equal(140, turn.OutputTokens);

      var ollama = new FakeNet { Reply = new WebHttpReply { Status = 200, Body = "{\"results\":[]}" } };
      var oplan = WebSearchPlan.For("OllamaLocal", true, "qwen", () => "ollama-key", () => "google-key", ollama.AsPost);
      oplan.Run("{\"query\":\"q\"}", default);
      Assert.Equal("Bearer ollama-key", ollama.Sent.Single().Headers.Single().Value);
      Assert.Equal(1, oplan.Searches);
    }

    [Fact]
    public void A_plan_refuses_a_call_for_a_mode_that_has_no_client_tool_and_bad_arguments()
    {
      var net = new FakeNet();
      var anthropic = WebSearchPlan.For("Anthropic", true, "m", () => "k", () => "k", net.AsPost);
      Assert.False(JsonNode.Parse(anthropic.Run("{\"query\":\"q\"}", default))["ok"].GetValue<bool>());
      Assert.False(JsonNode.Parse(WebSearchPlan.For("OllamaCloud", true, "m", () => "k", () => "k", net.AsPost).Run("[1]", default))["ok"].GetValue<bool>());
      Assert.Empty(net.Sent);
    }

    // ---- Anthropic: the request body -------------------------------------------------------------------------------

    private static LlmRequest GoldenRequest() => new LlmRequest
    {
      System = "sys",
      CacheSystem = true,
      MaxTokens = 2000,
      Messages =
      {
        LlmMessage.User("hello"),
        LlmMessage.Assistant(new List<LlmBlock> { LlmBlock.TextBlock("hi"), LlmBlock.ToolUse("t1", "get_overview", "{}") }),
        new LlmMessage { Role = LlmRole.User, Blocks = { LlmBlock.ToolResult("t1", "{\"ok\":true}") } },
      },
      Tools = { new LlmTool { Name = "get_overview", Description = "d", InputSchemaJson = "{\"type\":\"object\",\"properties\":{}}" } },
    };

    /// <summary>THE SETTING OFF MEANS A BYTE-IDENTICAL BODY. The golden file is what the body was BEFORE this feature existed,
    /// produced by the code at that commit and stored byte for byte.</summary>
    [Fact]
    public void With_web_search_off_the_anthropic_body_is_byte_identical_to_the_one_before_the_feature()
    {
      var golden = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "golden", "anthropic-body-before-web-search.json"), new UTF8Encoding(false));
      var body = AnthropicRequestBody.Build(GoldenRequest(), "claude-test", ThinkingWireState.Omit);

      Assert.Equal(golden, body);
      Assert.DoesNotContain("web_search", body);
      Assert.False(GoldenRequest().ServerWebSearch);   // the default is off
    }

    [Fact]
    public void With_web_search_on_the_server_tool_is_appended_last_in_the_documented_shape_and_nothing_else_moves()
    {
      var off = (JsonObject)JsonNode.Parse(AnthropicRequestBody.Build(GoldenRequest(), "claude-test", ThinkingWireState.Omit));
      var req = GoldenRequest();
      req.ServerWebSearch = true;
      var on = (JsonObject)JsonNode.Parse(AnthropicRequestBody.Build(req, "claude-test", ThinkingWireState.Omit));

      // platform.claude.com/docs/en/agents-and-tools/tool-use/web-search-tool: {"type":"web_search_20250305","name":"web_search","max_uses":5}
      var tools = on["tools"].AsArray();
      Assert.Equal(2, tools.Count);
      Assert.Equal("{\"type\":\"web_search_20250305\",\"name\":\"web_search\",\"max_uses\":5}", tools[1].ToJsonString());
      Assert.Equal(off["tools"][0].ToJsonString(), tools[0].ToJsonString());            // Dwight's own tools, untouched

      on.Remove("tools"); off.Remove("tools");
      Assert.Equal(off.ToJsonString(), on.ToJsonString());                               // every other key identical
    }

    [Fact]
    public void The_search_tool_alone_still_produces_a_tools_array_and_no_forced_choice()
    {
      var req = new LlmRequest { System = "s", MaxTokens = 100, ServerWebSearch = true };
      req.Messages.Add(LlmMessage.User("hi"));
      var body = (JsonObject)JsonNode.Parse(AnthropicRequestBody.Build(req, "m", ThinkingWireState.Omit));
      Assert.Single(body["tools"].AsArray());
      Assert.Null(body["tool_choice"]);
    }

    // ---- Anthropic: reading the stream, and sending it back unchanged ---------------------------------------------

    private const string SearchFixture = "anthropic-HANDMADE-web-search-documented-wire-shape.sse";
    private const string ErrorFixture = "anthropic-HANDMADE-web-search-error-and-pause-turn.sse";

    private static LlmResponse ReadAnthropic(string fixture) => AnthropicReplyStream.Read(StreamFixtures.Reader(StreamFixtures.Read(fixture)));

    /// <summary>The content block of the k-th content_block_start event of a fixture, as the provider wrote it.</summary>
    private static JsonNode FixtureBlock(string fixture, int index)
    {
      var text = StreamFixtures.Read(fixture);
      foreach (var block in StreamFixtures.Blocks(text.Substring(text.IndexOf("event: message_start", StringComparison.Ordinal))))
      {
        var data = block.Split('\n').First(l => l.StartsWith("data: ", StringComparison.Ordinal)).Substring(6);
        var node = JsonNode.Parse(data);
        if (node["type"].GetValue<string>() == "content_block_start" && node["index"].GetValue<int>() == index)
          return node["content_block"];
      }
      throw new InvalidOperationException("no block " + index);
    }

    [Fact]
    public void The_search_stream_reads_into_text_a_search_a_result_and_a_cited_text_in_order()
    {
      var r = ReadAnthropic(SearchFixture);

      Assert.Equal(new[] { "text", "server_tool_use", "web_search_tool_result", "text", "text" }, r.Blocks.Select(b => b.Kind));
      Assert.Equal("end_turn", r.StopReason);
      Assert.Empty(r.ToolUses);                         // the provider's search is not a call Dwight dispatches
      Assert.False(r.WantsTool);
      Assert.Equal("srvtoolu_01WYG3ziw53XMcoyKL4XcZmE", r.Blocks[1].ToolUseId);
      Assert.Equal("srvtoolu_01WYG3ziw53XMcoyKL4XcZmE", r.Blocks[2].ToolUseId);

      // The query arrived as an input_json_delta and joined into the block's input.
      var call = JsonNode.Parse(r.Blocks[1].RawJson);
      Assert.Equal("server_tool_use", call["type"].GetValue<string>());
      Assert.Equal("web_search", call["name"].GetValue<string>());
      Assert.Equal("claude shannon birth date", call["input"]["query"].GetValue<string>());
      Assert.Equal("direct", call["caller"]["type"].GetValue<string>());                // the start block's other fields survive

      Assert.Equal(1, r.Usage.WebSearchRequests);
      Assert.Equal(6039, r.Usage.InputTokens);
      Assert.Equal(931, r.Usage.OutputTokens);
      Assert.Empty(r.WebSearchErrors);

      Assert.Null(r.Blocks[3].CitationsJson);
      var citations = JsonNode.Parse(r.Blocks[4].CitationsJson).AsArray();
      Assert.Equal("web_search_result_location", citations[0]["type"].GetValue<string>());
      Assert.Equal("https://en.wikipedia.org/wiki/Claude_Shannon", citations[0]["url"].GetValue<string>());
      // U82: the two consecutive text blocks after the search join verbatim (nothing between "results, " and "Claude"); the block
      // before the search call is a different passage and keeps its one newline
      Assert.Equal("I'll search for when Claude Shannon was born.\nBased on the search results, Claude Shannon was born on April 30, 1916, in Petoskey, Michigan", r.Text);
    }

    /// <summary>encrypted_content MUST ROUND-TRIP VERBATIM: the result block the reader kept is, parsed, deep-equal to the one the
    /// provider wrote, and the next request's body carries it (and the search call, and the citations) unchanged.</summary>
    [Fact]
    public void The_result_the_provider_sent_returns_in_the_next_request_unchanged_with_its_encrypted_content()
    {
      var r = ReadAnthropic(SearchFixture);
      var asSent = FixtureBlock(SearchFixture, 2);
      Assert.Contains("encrypted_content", asSent.ToJsonString());

      var req = new LlmRequest { System = "s", MaxTokens = 100 };
      req.Messages.Add(LlmMessage.User("when was shannon born?"));
      req.Messages.Add(LlmMessage.Assistant(r.Blocks));
      req.Messages.Add(LlmMessage.User("thanks"));
      var body = (JsonObject)JsonNode.Parse(AnthropicRequestBody.Build(req, "m", ThinkingWireState.Omit));
      var replayed = body["messages"][1]["content"].AsArray();

      Assert.Equal(5, replayed.Count);
      Assert.True(JsonNode.DeepEquals(asSent, replayed[2]), "the web_search_tool_result block changed on the way back");
      Assert.Equal("EqgfCioIARgBIiQ3YTAwMjY1Mi1mZjM5LTQ1NGUtODgxNC1kNjNjNTk1ZWI3Y-HANDMADE",
        replayed[2]["content"][0]["encrypted_content"].GetValue<string>());
      Assert.Equal("server_tool_use", replayed[1]["type"].GetValue<string>());
      Assert.Equal("claude shannon birth date", replayed[1]["input"]["query"].GetValue<string>());
      Assert.Equal("Eo8BCioIAhgBIiQyYjQ0OWJmZi1lNm-HANDMADE", replayed[4]["citations"][0]["encrypted_index"].GetValue<string>());
      Assert.Null(replayed[3]["citations"]);   // a text block that had none gets none
    }

    [Fact]
    public void A_search_block_that_cannot_be_replayed_is_refused_in_words_never_sent_as_something_else()
    {
      var req = new LlmRequest { System = "s", MaxTokens = 100 };
      req.Messages.Add(LlmMessage.User("q"));
      req.Messages.Add(LlmMessage.Assistant(new List<LlmBlock> { new LlmBlock { Kind = "web_search_tool_result", ToolUseId = "srvtoolu_x", RawJson = null } }));

      var ex = Assert.Throws<LlmException>(() => AnthropicRequestBody.Build(req, "m", ThinkingWireState.Omit));
      Assert.Contains("was not recorded", ex.Message);
      Assert.Contains("That step was not sent", ex.Message);
    }

    [Fact]
    public void The_cache_breakpoint_skips_the_providers_search_blocks()
    {
      var r = ReadAnthropic(SearchFixture);
      var req = new LlmRequest { System = "s", MaxTokens = 100, CacheSystem = true };
      req.Messages.Add(LlmMessage.User("q"));
      // a paused turn can end on a search call: the tail marker must walk back past it to a block that can carry one
      req.Messages.Add(LlmMessage.Assistant(new List<LlmBlock> { r.Blocks[0], r.Blocks[1] }));
      var body = (JsonObject)JsonNode.Parse(AnthropicRequestBody.Build(req, "m", ThinkingWireState.Omit));
      var content = body["messages"][1]["content"].AsArray();
      Assert.NotNull(content[0]["cache_control"]);
      Assert.Null(content[1]["cache_control"]);
    }

    [Fact]
    public void A_search_error_is_noted_without_failing_the_read_and_a_pause_turn_stop_is_carried_through()
    {
      var r = ReadAnthropic(ErrorFixture);

      Assert.Equal(new[] { "too_many_requests" }, r.WebSearchErrors);
      Assert.Equal("pause_turn", r.StopReason);
      Assert.Equal(new[] { "server_tool_use", "web_search_tool_result", "server_tool_use" }, r.Blocks.Select(b => b.Kind));
      // the paused turn ends on a search call with no result yet, and it is still returned whole
      Assert.Equal("nsw pavilion plan", JsonNode.Parse(r.Blocks[2].RawJson)["input"]["query"].GetValue<string>());
      Assert.True(JsonNode.DeepEquals(FixtureBlock(ErrorFixture, 1), JsonNode.Parse(r.Blocks[1].RawJson)));
    }

    [Fact]
    public void A_damaged_search_stream_is_refused_like_any_other_damaged_reply()
    {
      var text = StreamFixtures.Read(SearchFixture);
      var blocks = StreamFixtures.Blocks(text);

      // cut before message_stop: a reply that ended early is not a shorter answer
      var cut = StreamFixtures.Join(blocks.Where(b => !b.Contains("message_stop")).ToList());
      Assert.Throws<LlmException>(() => AnthropicReplyStream.Read(StreamFixtures.Reader(cut)));

      // the result's start event lost: its stop then closes a block that never started
      var lost = StreamFixtures.Join(blocks.Where(b => !b.Contains("\"type\":\"web_search_tool_result\"")).ToList());
      var ex = Assert.Throws<LlmException>(() => AnthropicReplyStream.Read(StreamFixtures.Reader(lost)));
      Assert.Contains("never started", ex.Message);
    }

    // ---- the loop: pause_turn, errors, usage -----------------------------------------------------------------------

    private sealed class Recorder : ILlmProvider
    {
      private readonly Queue<LlmResponse> _replies;
      public readonly List<bool> ServerSearchAsked = new List<bool>();
      public readonly List<List<LlmMessage>> Seen = new List<List<LlmMessage>>();
      public Recorder(params LlmResponse[] replies) { _replies = new Queue<LlmResponse>(replies); }
      public string ModelId => "recorder";
      public ProviderCapability Capability => default(ProviderCapability);
      public LlmResponse Send(LlmRequest request, CancellationToken cancel = default)
      {
        ServerSearchAsked.Add(request.ServerWebSearch);
        Seen.Add(request.Messages.ToList());
        return _replies.Dequeue();
      }
    }

    private static TurnResult Drive(Recorder p, bool serverSearch = true, Func<LlmBlock, string> execute = null) =>
      AgentTurnDriver.Drive(new TurnDrive
      {
        Provider = p,
        System = "s",
        Messages = new List<LlmMessage> { LlmMessage.User("how tall is it?") },
        Limits = new TurnLimits { MaxSteps = 8, WallClockMs = 0, MaxTranscriptChars = 1_000_000, AnswerBudget = 1000 },
        Execute = execute ?? (_ => "{}"),
        ServerWebSearch = serverSearch,
      }, Stopwatch.StartNew());

    private static LlmResponse Paused(string id) => new LlmResponse
    {
      StopReason = "pause_turn",
      Blocks = { LlmBlock.TextBlock("searching"), LlmBlock.ServerToolUse(id, "{\"type\":\"server_tool_use\",\"id\":\"" + id + "\",\"name\":\"web_search\",\"input\":{\"query\":\"q\"}}") },
    };

    [Fact]
    public void The_request_asks_for_the_providers_search_only_when_the_turn_does()
    {
      var on = new Recorder(TurnReply("done"));
      Drive(on, serverSearch: true);
      Assert.Equal(new[] { true }, on.ServerSearchAsked);

      var off = new Recorder(TurnReply("done"));
      Drive(off, serverSearch: false);
      Assert.Equal(new[] { false }, off.ServerSearchAsked);
    }

    private static LlmResponse TurnReply(string text) =>
      new LlmResponse { StopReason = "end_turn", Blocks = { LlmBlock.TextBlock(text) } };

    [Fact]
    public void A_paused_turn_is_continued_once_by_sending_the_paused_message_back_unchanged()
    {
      var paused = Paused("srvtoolu_a");
      var p = new Recorder(paused, TurnReply("It is 330 m."));

      var result = Drive(p);

      Assert.Equal(TurnOutcome.Complete, result.Outcome);
      Assert.Equal(2, result.Steps);
      Assert.Equal("It is 330 m.", result.Text);
      var second = p.Seen[1];
      Assert.Equal(2, second.Count);                                          // the user's question, then the paused assistant message
      Assert.Equal(LlmRole.Assistant, second[1].Role);
      Assert.Same(paused.Blocks[1], second[1].Blocks[1]);                     // the very block, unchanged
      Assert.Null(result.Note);
    }

    [Fact]
    public void A_second_pause_in_a_row_ends_the_turn_with_a_sentence()
    {
      var p = new Recorder(Paused("srvtoolu_a"), Paused("srvtoolu_b"));

      var result = Drive(p);

      Assert.Equal(TurnOutcome.Complete, result.Outcome);
      Assert.Equal(2, result.Steps);
      Assert.Equal(WebSearch.PausedTwiceNote, result.Note);
      Assert.Contains("paused its web search twice in a row", result.Note);
    }

    [Fact]
    public void A_pause_that_is_followed_by_progress_does_not_count_against_a_later_pause()
    {
      var tool = new LlmResponse { StopReason = "tool_use", Blocks = { LlmBlock.ToolUse("c1", "get_overview", "{}") } };
      var p = new Recorder(Paused("srvtoolu_a"), tool, Paused("srvtoolu_b"), TurnReply("done"));

      var result = Drive(p);

      Assert.Equal(4, result.Steps);
      Assert.Equal("done", result.Text);
      Assert.Null(result.Note);
    }

    [Fact]
    public void A_search_the_provider_could_not_run_is_collected_for_dwights_own_words_and_the_turn_is_not_failed()
    {
      var errored = new LlmResponse
      {
        StopReason = "end_turn",
        Blocks = { LlmBlock.TextBlock("I could not search, so from memory: about 330 m.") },
        WebSearchErrors = { "max_uses_exceeded", "too_many_requests", "max_uses_exceeded" },
      };

      var result = Drive(new Recorder(errored));

      Assert.Equal(TurnOutcome.Complete, result.Outcome);
      Assert.Equal(new[] { "max_uses_exceeded", "too_many_requests", "max_uses_exceeded" }, result.WebSearchErrors);
      var sentences = WebSearch.ErrorSentences(result.WebSearchErrors);
      Assert.Equal(2, sentences.Count);                                      // one sentence per distinct code
      Assert.Contains("limit of 5 searches", sentences[0]);
      Assert.Contains("rate limited", sentences[1]);
    }

    [Fact]
    public void A_mixed_step_sends_back_only_the_client_results_with_the_search_call_left_open()
    {
      // docs: "stop_reason is tool_use ... content contains the server_tool_use block and the client tool_use block, but no result
      // block for the server tool"; the follow-up user message must contain nothing except tool_result blocks.
      var mixed = new LlmResponse
      {
        StopReason = "tool_use",
        Blocks =
        {
          LlmBlock.ServerToolUse("srvtoolu_m", "{\"type\":\"server_tool_use\",\"id\":\"srvtoolu_m\",\"name\":\"web_search\",\"input\":{\"query\":\"q\"}}"),
          LlmBlock.ToolUse("toolu_m", "get_overview", "{}"),
        },
      };
      var p = new Recorder(mixed, TurnReply("done"));

      var result = Drive(p, execute: _ => "{\"ok\":true}");

      Assert.Equal(TurnOutcome.Complete, result.Outcome);
      var sent = p.Seen[1];
      var followUp = sent[sent.Count - 1];
      Assert.Equal(LlmRole.User, followUp.Role);
      Assert.All(followUp.Blocks, b => Assert.Equal("tool_result", b.Kind));  // nothing else, or the API reads the turn as over
      Assert.Equal("toolu_m", Assert.Single(followUp.Blocks).ToolUseId);
      Assert.Null(AgentLoopCore.CheckTranscript(sent));                        // and Dwight's own pre-flight accepts it
    }

    [Fact]
    public void Searches_the_provider_billed_accumulate_across_the_steps_of_a_turn()
    {
      var one = new LlmResponse { StopReason = "tool_use", Blocks = { LlmBlock.ToolUse("c1", "get_overview", "{}") }, Usage = { WebSearchRequests = 2 } };
      var two = new LlmResponse { StopReason = "end_turn", Blocks = { LlmBlock.TextBlock("done") }, Usage = { WebSearchRequests = 1 } };

      Assert.Equal(3, Drive(new Recorder(one, two)).Usage.WebSearchRequests);
    }

    // ---- the reader, usage ------------------------------------------------------------------------------------------

    [Fact]
    public void A_stream_with_no_search_reads_exactly_as_before_and_reports_no_searches()
    {
      var r = AnthropicReplyStream.Read(StreamFixtures.Reader(StreamFixtures.Read(StreamFixtures.Anthropic01)));
      Assert.Equal(0, r.Usage.WebSearchRequests);
      Assert.Empty(r.WebSearchErrors);
      Assert.All(r.Blocks, b => { Assert.Null(b.RawJson); Assert.Null(b.CitationsJson); });
    }

    // ---- the price, and the words ----------------------------------------------------------------------------------

    [Fact]
    public void Anthropic_searches_are_priced_at_ten_dollars_per_thousand_and_the_others_are_counted_not_priced()
    {
      Assert.Equal(1.0, WebSearch.SearchCents("Anthropic", 1), 6);          // $10 / 1,000 = 1 cent each
      Assert.Equal(300.0, WebSearch.SearchCents("Anthropic", 300), 6);     // 300 searches = $3.00
      Assert.Equal(0.0, WebSearch.SearchCents("Anthropic", 0), 6);
      Assert.Equal(0.0, WebSearch.SearchCents("Google", 50), 6);           // free allowance Dwight cannot see
      Assert.Equal(0.0, WebSearch.SearchCents("OllamaCloud", 50), 6);
      Assert.Equal("1 search", WebSearch.SearchesText(1));
      Assert.Equal("3 searches", WebSearch.SearchesText(3));
    }

    [Fact]
    public void A_stored_turn_carries_its_searches_only_when_it_had_some_and_they_add_up()
    {
      var none = JsonSerializer.Serialize(new StoredSpend { Model = "m", Steps = 1 });
      Assert.DoesNotContain("searches", none);                              // old turns and search-off turns serialise as they did

      var some = JsonSerializer.Serialize(new StoredSpend { Model = "m", Searches = 2 });
      Assert.Contains("\"searches\":2", some);
      Assert.Equal(2, JsonSerializer.Deserialize<StoredSpend>(some).Searches);

      var sum = SpendMath.Combine(new StoredSpend { Searches = 2, Cents = 1 }, new StoredSpend { Searches = 1, Cents = 1 });
      Assert.Equal(3, sum.Searches);
    }

    [Fact]
    public void No_sentence_a_user_or_a_model_reads_here_has_an_em_dash()
    {
      var strings = new List<string>
      {
        WebSearch.OnFact, WebSearch.NotYetFact, WebSearch.GoogleNotYetFact, WebSearch.PausedTwiceNote, WebSearch.UntrustedNote,
        WebSearch.NeedsOllamaKeyLocal, WebSearch.NeedsOllamaKeyCloud,
        WebSearch.Descriptor().Description, WebSearch.Descriptor().InputSchemaJson,
      };
      strings.AddRange(new[] { "max_uses_exceeded", "too_many_requests", "invalid_tool_input", "query_too_long", "request_too_large", "unavailable", "x" }
        .Select(WebSearch.ErrorSentence));
      foreach (var s in strings)
      {
        Assert.DoesNotContain("\u2014", s);
        Assert.DoesNotContain("–", s);
      }
    }
  }
}
