using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Dwight.Llm;
using Dwight.Models;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Lane B, section 3.1 step 2 - may the Build chat run on this model, through this provider's connection?
  ///
  /// <para>Two facts, both required: the catalogue's <c>agentic</c> (the MODEL may drive the loop, #189) and each
  /// adapter's <c>PassesTools</c> (Dwight's CONNECTION carries tools at all). The first existed on all 21 models
  /// and nothing enforced it; the second did not exist, so the only check tested the provider - and it ran
  /// before the model was even resolved. A fact with no enforcer is a comment.</para>
  /// </summary>
  public class AgentEligibilityTests
  {
    private static ModelInfo M(string id, string name, string provider, string tier, bool agentic) =>
      new ModelInfo(id, name, provider, tier, 1, 1, 100000, structured: true, agentic: agentic);

    // A slice of the real catalogue's shape: capable and small on the same provider.
    private static readonly ModelInfo Haiku = M("claude-haiku-4-5", "Claude Haiku 4.5", "Anthropic", "Fast", false);
    private static readonly ModelInfo Sonnet = M("claude-sonnet-5", "Claude Sonnet 5", "Anthropic", "Strong", true);
    private static readonly ModelInfo Opus = M("claude-opus-5", "Claude Opus 5", "Anthropic", "Top", true);
    private static readonly ModelInfo Terra = M("gpt-5.6-terra", "GPT-5.6 Terra", "OpenAI", "Mid", true);
    private static readonly ModelInfo Luna = M("gpt-5.6-luna", "GPT-5.6 Luna", "OpenAI", "Fast", false);
    private static readonly ModelInfo Qwen8 = M("qwen3:8b", "Qwen3 8B", "OllamaLocal", "Mid", false);

    private static readonly string[] OnlyAnthropic = { "Anthropic" };

    // ---- the live hole, closed ------------------------------------------------------------------------

    /// <summary>THE HOLE THAT WAS LIVE ON ANTHROPIC. The Advanced pin wins for every task, Scaffold included, and
    /// its list offers every model on the provider - so pinning Haiku ran the agent loop on a model the catalogue
    /// marks agentic=false. Now refused, naming the PIN as the cause and where to unpin it, and offering the
    /// models that can.</summary>
    [Fact]
    public void A_pinned_model_that_cannot_drive_the_loop_is_refused_and_the_pin_is_named()
    {
      var refusal = AgentEligibility.Check(Haiku, adapterPassesTools: true, "Anthropic",
                                           new[] { Sonnet, Opus }, OnlyAnthropic, modelIsPinned: true);

      Assert.NotNull(refusal);
      Assert.Contains("You have pinned Claude Haiku 4.5", refusal);
      Assert.Contains("Settings > Model", refusal);
      Assert.Contains("The Build chat does not run on it", refusal);
      Assert.Contains("On Anthropic it runs on Claude Sonnet 5 or Claude Opus 5 - unpin it, or pin one of those, under Dwight > Settings > Model.", refusal);
    }

    /// <summary>The premise of that hole, read from the SHIPPED catalogue rather than restated: Haiku really is
    /// agentic=false there. If the catalogue ever said otherwise, the hole would not exist and the row above would
    /// be testing a fiction.</summary>
    [Fact]
    public void The_shipped_catalogue_does_mark_Haiku_as_unable_to_drive_the_loop()
    {
      var catalogue = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "models.json")));
      var haiku = catalogue["models"].AsArray().Single(m => m["id"].GetValue<string>() == "claude-haiku-4-5");

      Assert.False(haiku["capabilities"]["agentic"].GetValue<bool>());
    }

    [Fact]
    public void A_capable_model_on_a_connection_that_carries_tools_may_run()
    {
      Assert.Null(AgentEligibility.Check(Sonnet, true, "Anthropic", new[] { Sonnet, Opus }, OnlyAnthropic, false));
    }

    // ---- the acceptance: flip ONE fact -----------------------------------------------------------------

    /// <summary>THE ACCEPTANCE, stated as a test. The day OpenAI tool calling lands, flipping its adapter's ONE fact
    /// lets a capable OpenAI model through - and a small one is still refused, because its own fact says so.</summary>
    [Fact]
    public void Flipping_the_adapter_fact_lets_a_capable_model_through_and_nothing_else()
    {
      var alts = new[] { Terra };

      Assert.NotNull(AgentEligibility.Check(Terra, adapterPassesTools: false, "OpenAI", alts, OnlyAnthropic, false));
      Assert.Null(AgentEligibility.Check(Terra, adapterPassesTools: true, "OpenAI", alts, OnlyAnthropic, false));

      Assert.NotNull(AgentEligibility.Check(Luna, adapterPassesTools: false, "OpenAI", alts, OnlyAnthropic, false));
      Assert.NotNull(AgentEligibility.Check(Luna, adapterPassesTools: true, "OpenAI", alts, OnlyAnthropic, false));
    }

    /// <summary>A small local model is refused in BOTH states - even if the Ollama adapter learned tools tomorrow.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_small_local_model_is_refused_whatever_the_adapter_can_do(bool passesTools)
    {
      Assert.NotNull(AgentEligibility.Check(Qwen8, passesTools, "Ollama (local)",
                                            Array.Empty<ModelInfo>(), OnlyAnthropic, false));
    }

    // ---- what the refusal says ---------------------------------------------------------------------

    /// <summary>THE PROVIDER FIRST. When the connection cannot carry tools no model choice helps, so the refusal
    /// talks about the provider and where the agent DOES run - never about models to switch to.</summary>
    [Fact]
    public void A_connection_without_tools_is_named_before_any_model_is()
    {
      var refusal = AgentEligibility.Check(Luna, false, "OpenAI", new[] { Terra }, OnlyAnthropic, true);

      Assert.Contains("does not run on OpenAI", refusal);
      Assert.Contains("runs on Anthropic", refusal);
      Assert.Contains("Every other verb works on OpenAI as usual", refusal);
      Assert.DoesNotContain("pinned", refusal);
      Assert.DoesNotContain("GPT-5.6 Terra", refusal);
    }

    [Fact]
    public void An_unpinned_model_that_cannot_drive_the_loop_is_told_how_to_get_one_that_can()
    {
      var refusal = AgentEligibility.Check(Luna, true, "OpenAI", new[] { Terra }, OnlyAnthropic, false);

      Assert.StartsWith("The Build chat does not run on GPT-5.6 Luna.", refusal);
      Assert.Contains("On OpenAI the chat runs on GPT-5.6 Terra - pick one under Dwight > Settings > Model.", refusal);
      Assert.DoesNotContain("You have pinned", refusal);
    }

    /// <summary>The alternatives are the provider's AGENTIC models only - a caller that passed a mixed list must not
    /// get a small model offered as the way out.</summary>
    [Fact]
    public void Only_models_that_can_drive_the_loop_are_offered_as_the_way_out()
    {
      var refusal = AgentEligibility.Check(Haiku, true, "Anthropic", new[] { Haiku, Sonnet }, OnlyAnthropic, true);

      Assert.Contains("it runs on Claude Sonnet 5 - unpin it", refusal);
      Assert.DoesNotContain("Claude Haiku 4.5 -", refusal);
    }

    /// <summary>A provider with no agentic model at all gets that said - never "pin one that can: ." over an empty
    /// list.</summary>
    [Fact]
    public void A_provider_with_no_capable_model_says_so_instead_of_offering_an_empty_list()
    {
      var refusal = AgentEligibility.Check(Qwen8, true, "Ollama (local)", Array.Empty<ModelInfo>(), OnlyAnthropic, false);

      Assert.Contains("No model on Ollama (local) can run it", refusal);
      Assert.DoesNotContain(": .", refusal);
    }

    /// <summary>Tharun's ruling (25 October): the refusal says what works, in Dwight's plain voice - no "trust",
    /// no "multi-step tool loop", no em dash - and stays short.</summary>
    [Fact]
    public void The_refusal_says_what_works_in_plain_words_and_stays_short()
    {
      var sol = M("gpt-6.1-sol", "GPT-6.1 Sol", "OpenAI", "Top", false);
      var terra = M("gpt-5.6-terra", "GPT-5.6 Terra", "OpenAI", "Mid", true);
      var sol56 = M("gpt-5.6-sol", "GPT-5.6 Sol", "OpenAI", "Top", true);
      var all = new[]
      {
        AgentEligibility.Check(sol, true, "OpenAI", new[] { terra, sol56 }, OnlyAnthropic, false),
        AgentEligibility.Check(sol, true, "OpenAI", new[] { terra, sol56 }, OnlyAnthropic, true),
        AgentEligibility.Check(Qwen8, true, "Ollama (local)", Array.Empty<ModelInfo>(), OnlyAnthropic, false),
      };

      Assert.All(all, r =>
      {
        var l = r.ToLowerInvariant();
        Assert.DoesNotContain("trust", l);
        Assert.DoesNotContain("tool loop", l);
        Assert.DoesNotContain("\u2014", r);
        Assert.True(r.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 40, r);
      });
      Assert.Equal("The Build chat does not run on GPT-6.1 Sol. On OpenAI the chat runs on GPT-5.6 Terra or GPT-5.6 Sol - pick one under Dwight > Settings > Model.", all[0]);
      Assert.Equal("You have pinned GPT-6.1 Sol for every action. The Build chat does not run on it. On OpenAI it runs on GPT-5.6 Terra or GPT-5.6 Sol - unpin it, or pin one of those, under Dwight > Settings > Model.", all[1]);
    }

    /// <summary>
    /// #883: NO USER-VISIBLE STRING PROMISES A FUTURE OF THE PRODUCT. Every refusal this function can produce is
    /// swept here for "yet" - which the repo-wide copy gate deliberately does NOT check, because most of its hits
    /// are true statements about the user's own work ("nothing is wired yet"). So the words this function owns are
    /// guarded where they are written. Its first draft said "cannot run on OpenAI yet"; the sweep caught it.
    /// </summary>
    [Fact]
    public void No_refusal_promises_a_future()
    {
      var every = new[]
      {
        AgentEligibility.Check(Luna, false, "OpenAI", new[] { Terra }, OnlyAnthropic, false),             // the connection
        AgentEligibility.Check(Luna, false, "OpenAI", new[] { Terra }, System.Array.Empty<string>(), false), // ... with nowhere named
        AgentEligibility.Check(null, true, "Anthropic", new[] { Sonnet }, OnlyAnthropic, false),           // unknown model
        AgentEligibility.Check(Haiku, true, "Anthropic", new[] { Sonnet }, OnlyAnthropic, true),           // pinned
        AgentEligibility.Check(Luna, true, "OpenAI", new[] { Terra }, OnlyAnthropic, false),               // unpinned
        AgentEligibility.Check(Qwen8, true, "Ollama (local)", Array.Empty<ModelInfo>(), OnlyAnthropic, false),   // none can
      };

      Assert.All(every, r =>
      {
        Assert.NotNull(r);
        Assert.DoesNotMatch(@"\b(yet|soon|later|planned|for now)\b", r.ToLowerInvariant());
      });
    }

    /// <summary>Fail closed on a model Dwight cannot look up - the catalogue loader's own rule, absent agentic =>
    /// false - rather than hand the agent an unknown.</summary>
    [Fact]
    public void A_model_not_in_the_catalogue_is_refused_not_guessed_at()
    {
      var refusal = AgentEligibility.Check(null, true, "Anthropic", new[] { Sonnet }, OnlyAnthropic, false);

      Assert.Contains("not in the model catalogue", refusal);
    }

    // ---- each adapter's PassesTools, held to the body it really builds ---------------------------------

    private const string Probe = "zz_probe_tool";

    private static LlmRequest WithOneTool() => new LlmRequest
    {
      System = "s",
      Messages = { LlmMessage.User("hi") },
      Tools = { new LlmTool { Name = Probe, Description = "d", InputSchemaJson = "{\"type\":\"object\",\"properties\":{}}" } },
    };

    /// <summary>THE FLAG CANNOT DRIFT FROM THE CODE, IN EITHER DIRECTION. Each adapter's declaration must equal
    /// whether the body it builds carries a tool it was handed. Flip a flag ahead of the code and this is red; build
    /// tool calling into a body and forget the flag and this is red. Anthropic's row is also the positive control
    /// for the others: the same probe, found where it is sent, so "not found" below means dropped.</summary>
    [Fact]
    public void Anthropic_declares_tools_and_its_body_carries_them()
    {
      var body = AnthropicRequestBody.Build(WithOneTool(), "claude-test", ThinkingWireState.Omit);

      Assert.True(body.Contains(Probe));
      Assert.Equal(AnthropicRequestBody.PassesTools, body.Contains(Probe));
    }

    /// <summary>OpenAI, and the two Ollama providers - the same adapter, built by LlmProviders.Create with three
    /// different capabilities. All three bodies must agree with the one declaration.</summary>
    [Theory]
    [InlineData("StrictSchema", true, "OpenAI")]
    [InlineData("Format", false, "Ollama (local)")]
    [InlineData("PromptOnly", false, "Ollama Cloud")]
    public void The_OpenAI_compatible_adapter_declares_what_its_body_does(string capability, bool completionTokens, string label)
    {
      // By name, because the enum is internal and a public test method cannot take it as a parameter.
      var cap = (ProviderCapability)Enum.Parse(typeof(ProviderCapability), capability);
      var body = new OpenAIProvider("https://example.invalid/v1", null, "model-test", cap, completionTokens, label)
        .BuildBody(WithOneTool(), structured: false);

      Assert.Equal(OpenAIProvider.PassesTools, body.Contains(Probe));
    }

    [Fact]
    public void The_Google_adapter_declares_what_its_body_does()
    {
      var body = new GoogleProvider(null, "gemini-test").BuildBody(WithOneTool(), structured: false);

      Assert.Equal(GoogleProvider.PassesTools, body.Contains(Probe));
    }
  }
}
