using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Dwight.Data;
using Dwight.Llm;
using Dwight.Session;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Brief DX: the image block on the seam, in each provider family's own shape, and what must not move when there is no image. The shapes are from each provider's page, read 2 November 2026; the
  /// bridge turn is what measures them against a live provider. Ollama's is the one the brief named differently: Dwight talks to its OpenAI-compatible endpoint, whose page shows an image_url part
  /// and lists no images field, so writing the native /api/chat array there would risk the model answering without the image.
  /// </summary>
  public class ChatImageWireTests
  {
    private const string Bytes = "iVBORw0KGgo=";
    private const string DataUrl = "data:image/png;base64," + Bytes;

    private static LlmMessage WithImage(string text = "plan regions for something like this")
    {
      var m = new LlmMessage { Role = LlmRole.User };
      m.Blocks.Add(LlmBlock.Image(ChatImage.Png, Bytes, 2016, ruleKnown: true));
      if (text != null) m.Blocks.Add(LlmBlock.TextBlock(text));
      return m;
    }

    private static List<LlmTool> Tools() => AgentToolCatalog.All()
      .Select(t => new LlmTool { Name = t.Name, Description = t.Description, InputSchemaJson = t.InputSchemaJson }).ToList();

    private static LlmRequest Request(LlmMessage message) => new LlmRequest { System = "s", Messages = { message }, Tools = Tools() };

    private static JsonNode Anthropic(LlmMessage m) => JsonNode.Parse(AnthropicRequestBody.Build(Request(m), "claude-sonnet-5-5", ThinkingWireState.Omit));

    private static OpenAIProvider Ollama(string label = "Ollama Cloud") =>
      new OpenAIProvider("https://example.invalid/v1", null, "kimi-k3:cloud", ProviderCapability.PromptOnly, false, label);

    private static JsonNode OllamaBody(LlmMessage m) => JsonNode.Parse(Ollama().BuildBody(Request(m), structured: false));

    private static JsonNode GoogleBody(LlmMessage m) => JsonNode.Parse(new GoogleProvider("k", "gemini-3.8-flash").BuildBody(Request(m), structured: false));

    private static JsonNode ResponsesBody(LlmMessage m) => JsonNode.Parse(OpenAIResponses.BuildBody(Request(m), "gpt-5.6-terra"));

    // ---- each family's shape --------------------------------------------------------------------------

    [Fact]
    public void Anthropic_takes_a_base64_source_before_the_words()
    {
      var content = Anthropic(WithImage())["messages"][0]["content"].AsArray();
      Assert.Equal(2, content.Count);
      Assert.Equal("{\"type\":\"image\",\"source\":{\"type\":\"base64\",\"media_type\":\"image/png\",\"data\":\"" + Bytes + "\"}}", content[0].ToJsonString());
      Assert.Equal("text", (string)content[1]["type"]);
    }

    [Fact]
    public void OpenAI_takes_an_input_image_with_a_data_url_in_the_user_items_content()
    {
      var user = ResponsesBody(WithImage())["input"][0];
      Assert.Equal("user", (string)user["role"]);
      var content = user["content"].AsArray();
      Assert.Equal("{\"type\":\"input_image\",\"image_url\":\"" + DataUrl + "\"}", content[0].ToJsonString());
      Assert.Equal("{\"type\":\"input_text\",\"text\":\"plan regions for something like this\"}", content[1].ToJsonString());
    }

    [Fact]
    public void Google_takes_inline_data_as_a_part()
    {
      var parts = GoogleBody(WithImage())["contents"][0]["parts"].AsArray();
      Assert.Equal("{\"inlineData\":{\"mimeType\":\"image/png\",\"data\":\"" + Bytes + "\"}}", parts[0].ToJsonString());
      Assert.Equal("plan regions for something like this", (string)parts[1]["text"]);
    }

    [Theory]
    [InlineData("Ollama Cloud")]
    [InlineData("Ollama (local)")]
    public void Ollama_takes_an_image_url_part_on_the_endpoint_Dwight_uses_and_never_the_native_images_array(string label)
    {
      var user = ((JsonNode)JsonNode.Parse(Ollama(label).BuildBody(Request(WithImage()), structured: false)))["messages"].AsArray().Last(n => (string)n["role"] == "user");
      var content = user["content"].AsArray();
      Assert.Equal("{\"type\":\"image_url\",\"image_url\":\"" + DataUrl + "\"}", content[0].ToJsonString());
      Assert.Equal("{\"type\":\"text\",\"text\":\"plan regions for something like this\"}", content[1].ToJsonString());
      Assert.Null(user["images"]);   // the native field: the page for this endpoint does not list it
    }

    [Fact]
    public void An_image_with_no_words_is_sent_with_no_empty_text_beside_it()
    {
      var image = WithImage(text: null);
      Assert.Single(Anthropic(image)["messages"][0]["content"].AsArray());
      Assert.Single(ResponsesBody(image)["input"][0]["content"].AsArray());
      Assert.Single(GoogleBody(image)["contents"][0]["parts"].AsArray());
      Assert.Single(OllamaBody(image)["messages"].AsArray().Last(n => (string)n["role"] == "user")["content"].AsArray());
    }

    [Fact]
    public void With_no_image_a_user_turn_is_still_a_string_on_the_two_wires_where_it_was_one()
    {
      Assert.Equal("hello", (string)ResponsesBody(LlmMessage.User("hello"))["input"][0]["content"]);
      Assert.Equal("hello", (string)OllamaBody(LlmMessage.User("hello"))["messages"].AsArray().Last(n => (string)n["role"] == "user")["content"]);
    }

    [Fact]
    public void The_cache_breakpoint_rides_the_words_after_an_image_and_the_image_itself_when_it_stands_alone()
    {
      var request = Request(WithImage());
      request.CacheSystem = true;
      var content = JsonNode.Parse(AnthropicRequestBody.Build(request, "claude-sonnet-5-5", ThinkingWireState.Omit))["messages"][0]["content"].AsArray();
      Assert.Null(content[0]["cache_control"]);
      Assert.NotNull(content[1]["cache_control"]);

      request = Request(WithImage(text: null));
      request.CacheSystem = true;
      content = JsonNode.Parse(AnthropicRequestBody.Build(request, "claude-sonnet-5-5", ThinkingWireState.Omit))["messages"][0]["content"].AsArray();
      Assert.NotNull(content[0]["cache_control"]);
    }

    // ---- the transcript ---------------------------------------------------------------------------------

    private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static StoredImage Stored(string sha = Sha) => new StoredImage { Sha256 = sha, MediaType = ChatImage.Png, Width = 1568, Height = 1000 };

    private static StoredMessage Me(string src, StoredImage image = null) => new StoredMessage { Who = "me", Html = src, Src = src, Image = image };

    private static ImageReplay Replay(string base64 = Bytes) => new ImageReplay { LoadBase64 = _ => base64, RuleKnown = true };

    [Fact]
    public void An_image_replays_on_later_turns_as_text_does_and_is_counted_by_the_formula()
    {
      var stored = new List<StoredMessage> { Me("like this", Stored()), new StoredMessage { Who = "dw", Html = "ok", Src = "ok" }, Me("and now?") };
      var messages = ChatToTranscript.Build(stored, Replay());
      Assert.Equal(3, messages.Count);
      var first = messages[0].Blocks;
      Assert.Equal(LlmBlock.ImageKind, first[0].Kind);
      Assert.Equal(Bytes, first[0].Base64);
      Assert.Equal(ChatImage.Tokens(1568, 1000), first[0].ImageTokens);
      Assert.Equal("like this", first[1].Text);
      Assert.Null(AgentLoopCore.CheckTranscript(messages));
    }

    [Fact]
    public void An_image_with_no_words_is_a_turn_and_the_pre_flight_passes_it()
    {
      var messages = ChatToTranscript.Build(new[] { Me("", Stored()) }, Replay());
      Assert.Single(messages);
      Assert.Equal(new[] { LlmBlock.ImageKind }, messages[0].Blocks.Select(b => b.Kind));
      Assert.Null(AgentLoopCore.CheckTranscript(messages));
    }

    [Fact]
    public void A_file_that_is_gone_leaves_a_plugin_line_in_the_turn_and_says_so_once_to_the_person()
    {
      var replay = Replay(base64: null);
      var messages = ChatToTranscript.Build(new[] { Me("like this", Stored()) }, replay);
      var only = Assert.Single(messages[0].Blocks);
      Assert.Equal("like this\n\n" + ChatImage.OmittedNote(ImageOmission.NotFound, null), only.Text);
      Assert.Contains(ImageOmission.NotFound, replay.Omitted);
      Assert.Null(AgentLoopCore.CheckTranscript(messages));
    }

    [Fact]
    public void An_image_with_no_words_whose_file_is_gone_is_still_a_sendable_turn()
    {
      var messages = ChatToTranscript.Build(new[] { Me("", Stored()) }, Replay(base64: null));
      Assert.Equal(ChatImage.OmittedNote(ImageOmission.NotFound, null), Assert.Single(messages[0].Blocks).Text);
      Assert.Null(AgentLoopCore.CheckTranscript(messages));
    }

    [Fact]
    public void A_model_that_reads_no_images_gets_the_words_and_a_line_naming_it_and_no_file_is_read()
    {
      var read = 0;
      var replay = new ImageReplay { LoadBase64 = _ => { read++; return Bytes; }, ModelReadsImages = false, ModelName = "Qwen3 8B (local)" };
      var messages = ChatToTranscript.Build(new[] { Me("like this", Stored()) }, replay);
      Assert.Contains("Qwen3 8B (local) does not read images", Assert.Single(messages[0].Blocks).Text);
      Assert.Equal(0, read);
      Assert.Contains(ImageOmission.ModelReadsNoImages, replay.Omitted);
    }

    [Fact]
    public void Past_what_one_request_carries_the_oldest_goes_first_and_the_newest_always_stands()
    {
      var replay = new ImageReplay { LoadBase64 = _ => new string('A', 40), MaxChars = 100 };
      var messages = ChatToTranscript.Build(new[] { Me("one", Stored()), Me("two", Stored()), Me("three", Stored()) }, replay);
      Assert.Equal(new[] { false, true, true }, messages.Select(m => m.Blocks.Any(b => b.Kind == LlmBlock.ImageKind)));
      Assert.Contains("size limit", messages[0].Blocks.Single().Text);
      Assert.Contains(ImageOmission.TooMuchForOneRequest, replay.Omitted);

      // Even one image over the bound goes, because it was checked against what a provider takes when it was attached.
      Assert.Contains(ChatToTranscript.Build(new[] { Me("one", Stored()) }, new ImageReplay { LoadBase64 = _ => new string('A', 400), MaxChars = 100 })[0].Blocks, b => b.Kind == LlmBlock.ImageKind);
    }

    [Fact]
    public void Only_the_persons_own_message_makes_an_image_block_whatever_the_record_says()
    {
      var stored = new List<StoredMessage>
      {
        new StoredMessage { Who = "dw", Html = "x", Src = "x", Image = Stored() },
        new StoredMessage { Who = ChatToTranscript.Report, Html = "y", Src = "y", Image = Stored() },
        new StoredMessage { Who = "dw", Html = "chrome", Image = Stored() },
      };
      var read = 0;
      var messages = ChatToTranscript.Build(stored, new ImageReplay { LoadBase64 = _ => { read++; return Bytes; } });
      Assert.Equal(0, read);   // not even a file read for a message that cannot carry one, and it cannot count against the request's bound
      Assert.Equal(2, messages.Count);   // chrome stays out, even with an image on it
      Assert.DoesNotContain(messages.SelectMany(m => m.Blocks), b => b.Kind == LlmBlock.ImageKind);
      // And no line about an image either: only the person attaches one, so only their message can have lost one.
      Assert.Equal("x", messages[0].Blocks.Single().Text);
      Assert.Equal("y", messages[1].Blocks.Single().Text);
    }

    [Fact]
    public void A_caller_with_no_replay_says_the_image_is_not_there_and_never_throws()
    {
      Assert.Contains("does not carry it", Assert.Single(ChatToTranscript.Build(new[] { Me("like this", Stored()) })[0].Blocks).Text);
    }

    // ---- the pre-flight ---------------------------------------------------------------------------------

    [Fact]
    public void The_pre_flight_refuses_an_empty_image_and_one_outside_a_user_turn_in_our_own_words()
    {
      Assert.Contains("image with nothing in it", AgentLoopCore.CheckTranscript(new[] { new LlmMessage { Role = LlmRole.User, Blocks = { LlmBlock.Image(ChatImage.Png, "") } } }));
      Assert.Contains("no media type", AgentLoopCore.CheckTranscript(new[] { new LlmMessage { Role = LlmRole.User, Blocks = { LlmBlock.Image(null, Bytes) } } }));
      Assert.Contains("outside a user turn", AgentLoopCore.CheckTranscript(new[] { LlmMessage.Assistant(new List<LlmBlock> { LlmBlock.Image(ChatImage.Png, Bytes) }) }));
    }

    // ---- the cost (unit 5) --------------------------------------------------------------------------------

    private static readonly Dwight.ModelInfo Sonnet = new Dwight.ModelInfo("claude-sonnet-5-5", "Claude Sonnet 5.5", DwightConfig.ProviderAnthropic, "Strong", 2.0, 10.0, 1_000_000, cacheWritePerMTok: 2.5);

    /// <summary>The bridge turn's measurement, 2 November 2026: Anthropic's token count on Claude Sonnet 5.5, with and without each image, through this codec. The test equals the measurement.
    /// A line here changes only with a new measurement: a measurement beats the table.</summary>
    [Theory]
    [InlineData(1568, 1176, 2355)]
    [InlineData(1568, 1000, 2019)]
    [InlineData(1092, 1092, 1524)]
    [InlineData(800, 600, 641)]
    [InlineData(400, 400, 228)]
    [InlineData(1568, 1568, 3139)]
    [InlineData(1000, 333, 435)]
    public void The_estimate_is_what_the_provider_counted(int width, int height, int imageAlone) =>
      Assert.Equal(imageAlone, ChatImage.Tokens(width, height));

    [Fact]
    public void The_line_names_the_image_to_the_nearest_hundred_where_the_rule_is_known()
    {
      Assert.Equal("+1 image, about 2,000 tokens", ChatImage.EstimateLine(1568, 1000, ruleKnown: true));
      Assert.Equal("+1 image, about 2,400 tokens", ChatImage.EstimateLine(1568, 1176, ruleKnown: true));
      Assert.Equal("+1 image, about 1,500 tokens", ChatImage.EstimateLine(1092, 1092, ruleKnown: true));
    }

    /// <summary>The fourteenth's gate, the same 1568 x 1176 sketch: the ledger's difference with and without the image was 1,064 on Gemini 3.8 Flash and 1,820 on qwen3.5:9b, where the chip read about 2,500. Never a number that is wrong, so no number.</summary>
    [Theory]
    [InlineData(1568, 1176)]
    [InlineData(10, 10)]
    public void Where_the_rule_is_not_known_the_line_holds_no_number(int width, int height)
    {
      Assert.Equal("+1 image, counted by the provider", ChatImage.EstimateLine(width, height, ruleKnown: false));
      Assert.DoesNotContain("token", ChatImage.EstimateLine(width, height, ruleKnown: false));
    }

    [Fact]
    public void On_a_model_with_no_rule_the_image_still_goes_and_still_weighs_but_a_stopped_step_leaves_it_out_of_the_price()
    {
      var messages = ChatToTranscript.Build(new[] { Me("like this", Stored()) }, new ImageReplay { LoadBase64 = _ => Bytes, RuleKnown = false });
      var block = messages[0].Blocks[0];
      Assert.Equal(LlmBlock.ImageKind, block.Kind);   // the rule is about the number, never about the image
      Assert.False(block.ImageRuleKnown);

      var counted = ChatToTranscript.Build(new[] { Me("like this", Stored()) }, Replay());
      Assert.True(counted[0].Blocks[0].ImageRuleKnown);
      Assert.Equal(AgentLoopCore.Size(counted), AgentLoopCore.Size(messages));   // or on Gemini and Ollama an image would weigh nothing on the transcript's bound
      Assert.True(AgentLoopCore.Size(messages) >= ChatImage.Tokens(1568, 1000) * 2);

      var stopped = Drive(new Stopping { Cancel = true }, messages[0]);
      Assert.Equal(0, stopped.InFlightImageTokens);
      Assert.Equal(1, stopped.InFlightUncountedImages);

      var known = Drive(new Stopping { Cancel = true }, WithImage("words"));
      Assert.Equal(2016, known.InFlightImageTokens);
      Assert.Equal(0, known.InFlightUncountedImages);
      Assert.Equal(0, Drive(new Stopping { Cancel = true }, LlmMessage.User("words")).InFlightUncountedImages);

      // A block that does not say reads as unknown, and every image in the request counts.
      var two = new LlmMessage { Role = LlmRole.User, Blocks = { LlmBlock.Image(ChatImage.Png, Bytes, 2016), LlmBlock.Image(ChatImage.Png, Bytes, 900), LlmBlock.TextBlock("words") } };
      Assert.Equal(2, AgentTurnDriver.RequestUncountedImages(new[] { two }));
      Assert.Equal(0, AgentTurnDriver.RequestImageTokens(new[] { two }));
    }

    [Fact]
    public void The_chip_reads_the_models_own_fact_and_is_drawn_again_when_the_model_changes()
    {
      var scaffold = ReadDoc("Dwight", "UI", "DwightScaffold.cs");
      Assert.Contains("ChatImage.ChipLine(image.Width, image.Height, model != null && model.ImagePatchTokens)", scaffold);
      Assert.Contains("RuleKnown = model != null && model.ImagePatchTokens,", scaffold);
      Assert.Contains("if (cmd != null && cmd.StartsWith(\"model:\", StringComparison.Ordinal)) RedrawChip(wp, doc, threadId);", scaffold);

      // After the header has been written, or the chip would be drawn for the model just left.
      var header = scaffold.Substring(scaffold.IndexOf("private static void OnHeaderCommand(", StringComparison.Ordinal));
      var written = header.IndexOf("CommandHistory.SetThreadModel(doc, threadId, id, \"\")", StringComparison.Ordinal);
      var redrawn = header.IndexOf("RedrawChip(wp, doc, threadId)", StringComparison.Ordinal);
      Assert.True(written > 0 && redrawn > written);
      Assert.DoesNotContain("ChatImage.Tokens(", scaffold);   // no number reaches the chip except through the fact
    }

    [Fact]
    public void The_limitations_page_says_the_new_truth()
    {
      var page = ReadDoc("docs", "public", "known-limitations.md");
      Assert.Contains("it says the provider counts the image", page);
      Assert.DoesNotContain("count fewer for the same image", page);
      Assert.Contains("on any other model it says the provider counts them", ReadDoc("docs", "public", "build.md"));
      Assert.DoesNotContain("its size and about how many tokens", ReadDoc("docs", "public", "changelog.md"));
    }

    [Fact]
    public void A_stopped_request_prices_its_image_as_tokens_and_never_through_the_characters()
    {
      var without = StoppedEstimate.Of(Sonnet, 12_000, TimeSpan.Zero, null);
      var with = StoppedEstimate.Of(Sonnet, 12_000, TimeSpan.Zero, null, 2016);
      Assert.Equal(without.InputTokens + 2016, with.InputTokens);
      Assert.True(with.Cents > without.Cents);
      Assert.Equal(without.Chars, with.Chars);   // the ledger's ratio reads Chars, so an image must not move it
    }

    private sealed class Stopping : ILlmProvider
    {
      public readonly List<LlmRequest> Seen = new List<LlmRequest>();
      public bool Cancel;
      public string ModelId => "recording";
      public ProviderCapability Capability => default(ProviderCapability);
      public bool SendsForcedTool => false;
      public LlmResponse Send(LlmRequest request, System.Threading.CancellationToken cancel = default)
      {
        Seen.Add(request);
        if (Cancel) throw new OperationCanceledException();
        return new LlmResponse { StopReason = "stop", Blocks = { LlmBlock.TextBlock("A plan.") } };
      }
    }

    private static TurnResult Drive(ILlmProvider provider, LlmMessage message) =>
      AgentTurnDriver.Drive(new TurnDrive
      {
        Provider = provider,
        System = "system",
        Messages = new List<LlmMessage> { message },
        Tools = Tools(),
        Limits = new TurnLimits { MaxSteps = 2, WallClockMs = 0, MaxTranscriptChars = 120_000, AnswerBudget = 1000 },
        Execute = _ => "{\"ok\":true,\"result\":{}}",
      }, Stopwatch.StartNew());

    [Fact]
    public void A_turn_stopped_in_flight_carries_the_images_tokens_apart_from_its_characters()
    {
      var image = Drive(new Stopping { Cancel = true }, WithImage("words"));
      var plain = Drive(new Stopping { Cancel = true }, LlmMessage.User("words"));
      Assert.Equal(TurnOutcome.Stopped, image.Outcome);
      Assert.Equal(2016, image.InFlightImageTokens);
      Assert.Equal(0, plain.InFlightImageTokens);
      Assert.Equal(plain.InFlightChars, image.InFlightChars);   // megabytes of base64 are not characters of text
    }

    [Fact]
    public void An_images_weight_on_the_transcript_bound_is_its_tokens_so_it_cannot_clear_a_turns_tool_results()
    {
      var words = new List<LlmMessage> { LlmMessage.User("words") };
      var image = new List<LlmMessage> { new LlmMessage { Role = LlmRole.User, Blocks = { LlmBlock.Image(ChatImage.Png, new string('A', 3_000_000), 2016), LlmBlock.TextBlock("words") } } };
      Assert.Equal(AgentLoopCore.Size(words) + 2016 * 2, AgentLoopCore.Size(image));
    }

    // ---- the prompt and the contract (units 6, 7, 8): tests on the behaviour's side (#315) ------------------

    [Fact]
    public void The_prompt_says_the_rulings_words()
    {
      var master = Dwight.Prompts.Prompts.AgentMaster;
      Assert.Contains("An image the person attached is a reference for what they want; it names nothing on the canvas. Read it for intent and plan through the tools as always.", master);
      Assert.Contains("You cannot see the canvas or the viewport as a picture", master);
      Assert.Contains("ask the person to take a screenshot and attach it", master);
    }

    [Fact]
    public void A_turn_with_an_image_is_offered_the_same_tools_and_the_same_system_as_a_turn_without()
    {
      var with = new Stopping();
      var without = new Stopping();
      Drive(with, WithImage("words"));
      Drive(without, LlmMessage.User("words"));
      Assert.Equal(without.Seen[0].Tools.Select(t => t.Name), with.Seen[0].Tools.Select(t => t.Name));
      Assert.Equal(without.Seen[0].System, with.Seen[0].System);
      Assert.Equal(without.Seen[0].ForceToolName, with.Seen[0].ForceToolName);
      Assert.Equal(new[] { LlmBlock.ImageKind, "text" }, with.Seen[0].Messages[0].Blocks.Select(b => b.Kind));
    }

    [Fact]
    public void CHAT_IMAGE_EMITS_EXACTLY_ITS_ROW_AND_A_TURN_WITH_ONE_NOTHING_ELSE()
    {
      var row = PayloadContract.For("chat:image");
      Assert.NotNull(row);
      Assert.Equal(Scope.WhenAttached, row.Scope);
      Assert.Equal(AssertedBy.UnitTest, row.AssertedBy);

      var with = PayloadPaths.Collect(Anthropic(WithImage("words")));
      var without = PayloadPaths.Collect(Anthropic(LlmMessage.User("words")));
      const string block = "messages[].content[].";
      Assert.Empty(without.Except(with));   // nothing a turn sends is lost by attaching
      var added = with.Except(without).ToList();
      Assert.All(added, p => Assert.StartsWith(block, p));   // and nothing leaves but inside the block
      Assert.Equal(row.Fields.Select(f => f.Path).OrderBy(p => p, StringComparer.Ordinal), added.Select(p => p.Substring(block.Length)).OrderBy(p => p, StringComparer.Ordinal));

      // The same tools, the same system, byte for byte.
      Assert.Equal(Anthropic(LlmMessage.User("words"))["tools"].ToJsonString(), Anthropic(WithImage("words"))["tools"].ToJsonString());
      Assert.Equal(Anthropic(LlmMessage.User("words"))["system"].ToJsonString(), Anthropic(WithImage("words"))["system"].ToJsonString());
    }

    [Fact]
    public void The_chat_image_row_reads_as_the_chats_with_its_own_when_and_the_page_says_the_same()
    {
      var row = PayloadContract.For("chat:image");
      Assert.Equal("If you attach one", PayloadContract.WhenGoes(row));
      Assert.Contains("An image goes only if you attach it.", PayloadContract.SettingsHtml());
      Assert.Contains("Sent only when you attach it to a turn", PayloadContract.Markdown());
      // After the group line, with the chat's rows, and under its short name.
      var html = PayloadContract.SettingsHtml();
      Assert.True(html.IndexOf("The Build chat</td>", StringComparison.Ordinal) < html.IndexOf(">an image you attach<", StringComparison.Ordinal));
      Assert.Contains(row.Sends, ReadDoc("docs", "public", "privacy.md"));
    }

    // ---- unit 8: Dwight cannot see the screen -----------------------------------------------------------------

    [Theory]
    [InlineData("Look at my screen and tell me what is wrong")]
    [InlineData("can you see my canvas?")]
    [InlineData("LOOK AT THE VIEWPORT please")]
    [InlineData("what’s on my screen")]
    [InlineData("Take a screenshot of the canvas")]
    [InlineData("First tidy A1.\nThen look at the canvas.")]
    public void Asked_to_look_with_nothing_attached_it_says_so(string text) => Assert.True(ScreenAsk.Asks(text, imageAttached: false));

    [Theory]
    [InlineData("add a slider to A1")]
    [InlineData("look at this component and tell me what it holds")]
    [InlineData("Don't look at the canvas, just answer")]
    [InlineData("the screen of the facade should be a louvre")]
    [InlineData("")]
    public void Anything_else_passes_without_the_line(string text) => Assert.False(ScreenAsk.Asks(text, imageAttached: false));

    [Fact]
    public void With_an_image_attached_there_is_something_to_look_at() => Assert.False(ScreenAsk.Asks("look at my screen", imageAttached: true));

    [Fact]
    public void The_line_is_the_rulings_and_the_limitations_page_carries_the_same_sentence()
    {
      Assert.Equal("I cannot see your screen. Take a screenshot and attach it with the paper-clip or Ctrl+V and I will read it.", ScreenAsk.Line);
      Assert.Contains(ScreenAsk.Line, ReadDoc("docs", "public", "known-limitations.md"));
      Assert.Contains(ScreenAsk.Line, ScreenAsk.Fact);
    }

    [Fact]
    public void Only_the_persons_typed_message_draws_the_line_and_before_the_turn_runs()
    {
      var scaffold = ReadDoc("Dwight", "UI", "DwightScaffold.cs");
      Assert.Equal(1, Count(scaffold, "ScreenAsk.Asks("));
      var door = scaffold.Substring(scaffold.IndexOf("private static void OnUserMessage(", StringComparison.Ordinal));
      door = door.Substring(0, door.IndexOf("// ---- the composer's image", StringComparison.Ordinal));
      Assert.True(door.IndexOf("ScreenAsk.Asks(", StringComparison.Ordinal) < door.IndexOf("RunTurn(", StringComparison.Ordinal));
      Assert.Contains("AppendReport(wp, doc, threadId, System.Net.WebUtility.HtmlEncode(ScreenAsk.Line), ScreenAsk.Fact)", door);
    }

    /// <summary>THE RULING'S LAST LINE: no code path in the plugin produces an image block from anything but the person's attach. Read from the source across the whole plugin, because the thing it
    /// forbids is a path that does not exist yet, and no fixture can drive that (ADR-091).</summary>
    [Fact]
    public void No_path_makes_an_image_block_from_anything_but_the_persons_attach()
    {
      var sources = Directory.GetFiles(Path.Combine(Repo(), "Dwight"), "*.cs", SearchOption.AllDirectories)
        .Select(f => (Path: f.Replace('\\', '/'), Text: File.ReadAllText(f))).ToList();
      string Only(string needle) => Assert.Single(sources, s => s.Text.Contains(needle)).Path;

      // An image block is made in one place, from the record of a message.
      Assert.EndsWith("Agent/ChatToTranscript.cs", sources.Where(s => s.Text.Contains("LlmBlock.Image(")).Select(s => s.Path).Single());
      Assert.DoesNotContain(sources, s => !s.Path.EndsWith("Llm/LlmTypes.cs") && s.Text.Contains("Kind = LlmBlock.ImageKind"));
      Assert.DoesNotContain(sources, s => s.Text.Contains("Kind = \"image\"") && !s.Path.EndsWith("Llm/LlmTypes.cs"));

      // A record's image is written in one place, by the person's door, from what was held.
      Assert.EndsWith("Session/CommandHistory.cs", sources.Where(s => s.Text.Contains("m.Image = ")).Select(s => s.Path).Single());
      Assert.EndsWith("Session/ChatImageStore.cs", sources.Where(s => s.Text.Contains("new StoredImage")).Select(s => s.Path).Single());

      var scaffold = sources.Single(s => s.Path.EndsWith("UI/DwightScaffold.cs")).Text;
      Assert.Equal(1, Count(scaffold, "ChatImageStore.Save("));
      Assert.Equal(1, Count(scaffold, "_heldImages[threadId ?? \"\"] = "));   // held only in HoldImage
      Assert.Equal(1, Count(scaffold, "HoldImage(wp, doc, threadId, intake)"));   // from OnChatImage alone
      Assert.Equal(1, Count(scaffold, "wp.OnChatImage = what => OnChatImage(wp, doc, threadId, what)"));   // the one wiring, from the shell's command
      Assert.Equal(2, Count(scaffold, "OnChatImage("));   // its definition and that one call, so nothing in the plugin starts an attach by itself
      Assert.Equal(1, Count(scaffold, "image: image"));   // AppendUserText's call to the primitive; no other door passes one
      Assert.Contains("OnChatImage?.Invoke(cmd.Substring(8))", sources.Single(s => s.Path.EndsWith("UI/WebPanel.cs")).Text);
      Assert.Equal(1, Count(ReadDoc("Dwight", "Resources", "panel-shell.html"), "chatimg:'+r.result"));   // only a file the person gave reaches it

      // And what would capture a picture goes nowhere near an image: no file holding the chat's image path holds a capture call.
      var captures = new[] { "CaptureToBitmap", "GenerateHiResImage", "CopyFromScreen", "ViewCapture", "ScreenCaptureToBitmap", "CapturePreviewAsync", "Clipboard.GetImage", ".GetImage(" };
      foreach (var path in new[] { "Agent/ChatToTranscript.cs", "Session/ChatImage.cs", "Session/ChatImageCodec.cs", "Session/ChatImageStore.cs", "UI/DwightScaffold.cs", "UI/WebPanel.cs" })
      {
        var text = sources.Single(s => s.Path.EndsWith(path)).Text;
        foreach (var capture in captures) Assert.DoesNotContain(capture, text);
      }

      // The model has no tool that takes a picture.
      foreach (var tool in AgentToolCatalog.All())
        foreach (var word in new[] { "screenshot", "capture", "image" })
          Assert.DoesNotContain(word, tool.Name, StringComparison.OrdinalIgnoreCase);
    }

    // ---- the shim's wiring, read from the source, since it needs Rhino (ADR-091) -------------------------------

    [Fact]
    public void A_refused_send_keeps_the_words_and_the_chip_and_spends_nothing()
    {
      var door = ReadDoc("Dwight", "UI", "DwightScaffold.cs");
      door = door.Substring(door.IndexOf("private static void OnUserMessage(", StringComparison.Ordinal));
      door = door.Substring(0, door.IndexOf("// ---- the composer's image", StringComparison.Ordinal));
      var refused = door.IndexOf("if (refusal != null)", StringComparison.Ordinal);
      Assert.True(refused > 0 && refused < door.IndexOf("_heldImages.Remove(", StringComparison.Ordinal));
      Assert.Contains("wp.SetComposerDraft(text)", door.Substring(refused));
      var leaves = door.IndexOf("return;", door.IndexOf("wp.SetComposerDraft(text)", StringComparison.Ordinal), StringComparison.Ordinal);
      Assert.True(leaves > 0 && leaves < door.IndexOf("_heldImages.Remove(", StringComparison.Ordinal), "a refused send must leave before the image is let go and the turn runs");
    }

    [Fact]
    public void The_attach_refuses_by_the_models_name_before_it_reads_a_byte()
    {
      var door = ReadDoc("Dwight", "UI", "DwightScaffold.cs");
      door = door.Substring(door.IndexOf("private static void OnChatImage(", StringComparison.Ordinal));
      var refuses = door.IndexOf("ChatImage.NoVisionLine(model.DisplayName, VisionAdviceFor())", StringComparison.Ordinal);   // Brief EE: it also names alternatives; the old pin read -1 and passed by being less than anything
      Assert.True(refuses >= 0 && refuses < door.IndexOf("Task.Run(", StringComparison.Ordinal));
      Assert.Equal("Qwen3 8B (local) does not read images, so the image was not attached. Pick a model that does from the model menu.", ChatImage.NoVisionLine("Qwen3 8B (local)"));
    }

    [Fact]
    public void The_shell_refuses_at_the_hosts_own_cap()
    {
      Assert.Contains("_chatImgMax=" + ChatImage.MaxFileBytes.ToString(System.Globalization.CultureInfo.InvariantCulture), ReadDoc("Dwight", "Resources", "panel-shell.html"));
    }

    [Fact]
    public void The_thumbnail_the_record_and_the_provider_read_one_file()
    {
      var scaffold = ReadDoc("Dwight", "UI", "DwightScaffold.cs");
      Assert.Equal(2, Count(scaffold, "ChatImageStore.Load(imagesDir, image)") + Count(scaffold, "ChatImageStore.Load(ImagesDir(doc), image)"));
      Assert.Contains("ChatImage.DataUrl(image.MediaType, Convert.ToBase64String(image.Bytes))", scaffold.Substring(scaffold.IndexOf("private static string ChipJson(", StringComparison.Ordinal)));   // the chip, from the held bytes
    }

    [Fact]
    public void A_chip_belongs_to_its_thread_so_opening_another_chat_draws_that_chats_or_none()
    {
      var wire = ReadDoc("Dwight", "UI", "DwightScaffold.cs");
      wire = wire.Substring(wire.IndexOf("private static void Wire(", StringComparison.Ordinal));
      wire = wire.Substring(0, wire.IndexOf("private static void OnUserMessage(", StringComparison.Ordinal));
      var chat = wire.IndexOf("wp.SetChat(json);", StringComparison.Ordinal);
      Assert.True(chat >= 0 && chat < wire.IndexOf("RedrawChip(wp, doc, threadId);", StringComparison.Ordinal));
      Assert.Contains("wp.SetChatHeld(_heldImages.TryGetValue(threadId ?? \"\", out var held) ? ChipJson(held, \"\", ChatModel(doc, threadId)) : null)", ReadDoc("Dwight", "UI", "DwightScaffold.cs"));
    }

    private static int Count(string text, string needle)
    {
      int n = 0, at = 0;
      while ((at = text.IndexOf(needle, at, StringComparison.Ordinal)) >= 0) { n++; at += needle.Length; }
      return n;
    }

    private static string ReadDoc(params string[] parts) => File.ReadAllText(Path.Combine(new[] { Repo() }.Concat(parts).ToArray()));

    private static string Repo()
    {
      var dir = new DirectoryInfo(AppContext.BaseDirectory);
      while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Dwight.sln"))) dir = dir.Parent;
      return dir?.FullName ?? throw new InvalidOperationException("no repository above the test binaries");
    }
  }
}
