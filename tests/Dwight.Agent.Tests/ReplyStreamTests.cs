using System;
using System.Linq;
using Dwight.Llm;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// U38 (#873) - each provider's reply, assembled through the ONE reader, from the streams the providers
  /// REALLY sent: every captured stream reads to the same text and the same tool calls the product showed,
  /// and a reply damaged in one place is refused as a failed run in Dwight's words - never a shorter answer.
  ///
  /// <para>What these witness: the assembly the providers' <c>Send</c> calls, on real bytes (Anthropic, OpenAI)
  /// and on the documented shape (Google, hand-made). What they cannot: the HTTP and decoding layer above it
  /// (<c>StreamReader</c> over the response), a live Google or Ollama stream, and an event that vanished WHOLE
  /// with its blank line - no reader can see an event that never arrived; what it can see, and now refuses, is
  /// the gap it leaves in the protocol (a block that never started, a reply that never stopped).</para>
  /// </summary>
  [Collection(StreamFixtures.CaptureWindow)]
  public class ReplyStreamTests
  {
    private const string Dwight = "Dwight could not read the model's reply in full, so it did not act on it: ";

    private static LlmResponse Anthropic(string text) => AnthropicReplyStream.Read(StreamFixtures.Reader(text));
    private static LlmResponse OpenAI(string text) => OpenAIProvider.ReadStream(StreamFixtures.Reader(text), default, "OpenAI");
    private static LlmResponse Google(string text) => GoogleProvider.ReadStream(StreamFixtures.Reader(text), default);

    private static LlmResponse OllamaCloud(string text) => OpenAIProvider.ReadStream(StreamFixtures.Reader(text), default, "Ollama Cloud");

    private static LlmBlock[] Calls(LlmResponse r) => r.Blocks.Where(b => b.Kind == "tool_use").ToArray();

    private static void Call(LlmBlock b, string id, string name, string input)
    {
      Assert.Equal(id, b.ToolUseId);
      Assert.Equal(name, b.ToolName);
      Assert.Equal(input, b.ToolInputJson);
    }

    // ---- Anthropic: the six captured streams read to what the product showed ----------------------------

    [Fact]
    public void Anthropic_01_reads_its_text_and_its_get_cluster_call_with_the_arguments_the_fragments_made()
    {
      var r = Anthropic(StreamFixtures.Read(StreamFixtures.Anthropic01));

      Assert.Equal(new[] { "text", "tool_use" }, r.Blocks.Select(b => b.Kind));
      Assert.Equal("The canvas holds just 2 objects, both inside a single cluster.", r.Blocks[0].Text);
      Call(r.Blocks[1], "toolu_01H2H7271DWQCxZFGKcNHLeC", "get_cluster", "{\"id\": 0}");
      Assert.Equal("tool_use", r.StopReason);
      Assert.Equal(272, r.Usage.InputTokens);
      Assert.Equal(72, r.Usage.OutputTokens);
      Assert.Equal(12684, r.Usage.CacheReadInputTokens);
    }

    [Fact]
    public void Anthropic_02_reads_one_search_call()
    {
      var r = Anthropic(StreamFixtures.Read(StreamFixtures.Anthropic02));

      Call(Calls(r).Single(), "toolu_014bWGHuQ8rDPFuynLsEApL9", "search", "{\"query\": \"Cluster\"}");
      Assert.Equal("tool_use", r.StopReason);
    }

    [Fact]
    public void Anthropic_03_reads_its_text_and_two_search_calls_in_block_order()
    {
      var r = Anthropic(StreamFixtures.Read(StreamFixtures.Anthropic03));

      Assert.Equal("The overview labeled it \"Cluster 0\" but that's a display label, not a real component id — let me find the actual component id for it.",
        r.Blocks[0].Text);
      var calls = Calls(r);
      Assert.Equal(2, calls.Length);
      Call(calls[0], "toolu_015e2LVSGt65q7cWANH7ox4T", "search", "{\"query\": \"\"}");
      Call(calls[1], "toolu_01NTvDfWqxRpiLZ9ggzoiEQp", "search", "{\"query\": \"Circle\"}");
    }

    [Fact]
    public void Anthropic_04_reads_a_search_and_a_get_component_call()
    {
      var calls = Calls(Anthropic(StreamFixtures.Read(StreamFixtures.Anthropic04)));

      Assert.Equal(2, calls.Length);
      Call(calls[0], "toolu_01GUQS4iYizLxzNdoQZ8rog6", "search", "{\"query\": \"Slider\"}");
      Call(calls[1], "toolu_01EQBvMbxoHogRGhHWXME6aq", "get_component", "{\"id\": 2}");
    }

    [Fact]
    public void Anthropic_05_reads_one_get_connections_call()
    {
      Call(Calls(Anthropic(StreamFixtures.Read(StreamFixtures.Anthropic05))).Single(),
        "toolu_01KgfvWSJFH87SMdPYQVoRQi", "get_connections", "{\"id\": 2}");
    }

    [Fact]
    public void Anthropic_06_reads_the_closing_answer_whole()
    {
      var r = Anthropic(StreamFixtures.Read(StreamFixtures.Anthropic06));

      Assert.Equal("end_turn", r.StopReason);
      Assert.Equal("## Summary\n\nThe canvas is minimal — just two components, wired together:\n\n- **Number Slider** \"radius\" (1) — holds value `5`\n- **Circle** (2) — nickname \"Cir\", takes a Plane (`P`, defaulted to origin) and Radius (`R`)\n\nThe slider (1) feeds the Circle's `R` input directly. The Circle's `P` input isn't wired — it's using its internalised default, a plane at the origin (0,0,0).\n\nThe Circle's output (`C`) isn't connected to anything downstream — it's a dead end, just producing a circle of radius 5 at the world origin with nothing consuming it.",
        r.Blocks.Single().Text);
    }

    /// <summary>A tool that takes no arguments streams no fragments; its start event's <c>input</c> - <c>{}</c> -
    /// is what the model said, so that is what is carried. The old reader INVENTED <c>{}</c> for any empty
    /// input; this one reads it from the event, and refuses when the event has none.</summary>
    [Fact]
    public void Anthropic_a_call_with_no_fragments_carries_the_input_its_start_event_sent()
    {
      var text = StreamFixtures.Read(StreamFixtures.Anthropic05);
      var blocks = StreamFixtures.Blocks(text).Where(b => !b.Contains("input_json_delta")).ToList();

      Assert.Equal("{}", Calls(Anthropic(StreamFixtures.Join(blocks))).Single().ToolInputJson);

      var noInput = StreamFixtures.Join(blocks.Select(b => b.Replace("\"input\":{},", "")));
      var ex = Assert.Throws<LlmException>(() => Anthropic(noInput));
      Assert.Contains("get_connections", ex.Message);
      Assert.Contains("no arguments sent", ex.Message);
    }

    // ---- Anthropic: damage only the Messages protocol can see -------------------------------------------

    /// <summary>THE CASE THE OLD LOOP COULD NOT SEE: every event is valid JSON, and the argument fragments do not
    /// join into an object. It used to be dispatched as whatever the fragments made; it is refused at the call's
    /// stop, naming the tool and the event.</summary>
    [Fact]
    public void Anthropic_an_invalid_argument_fragment_is_refused_naming_the_call_and_its_event()
    {
      var text = StreamFixtures.Read(StreamFixtures.Anthropic01);
      var damaged = text.Replace("\"partial_json\":\"{\\\"id\\\": \"", "\"partial_json\":\"{\\\"id\\\" \"");
      Assert.NotEqual(text, damaged);
      var stop = StreamFixtures.IndexOf(text, "\"type\":\"content_block_stop\",\"index\":1");

      var ex = Assert.Throws<LlmException>(() => Anthropic(damaged));
      Assert.StartsWith(Dwight, ex.Message);
      Assert.Contains("event " + stop + " (line ", ex.Message);
      Assert.Contains("\"get_cluster\"", ex.Message);
      Assert.Contains("do not join into valid JSON", ex.Message);
    }

    /// <summary>Fragments that join into JSON that is not an object are not arguments either (#828).</summary>
    [Fact]
    public void Anthropic_fragments_that_join_into_a_non_object_are_refused()
    {
      var text = StreamFixtures.Read(StreamFixtures.Anthropic05);
      var blocks = StreamFixtures.Blocks(text).Where(b => !b.Contains("input_json_delta")).ToList();
      var start = blocks.FindIndex(b => b.Contains("\"type\":\"content_block_start\",\"index\":0"));
      blocks.Insert(start + 1, "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0," +
                               "\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"[1,2]\"}}");

      var ex = Assert.Throws<LlmException>(() => Anthropic(StreamFixtures.Join(blocks)));
      Assert.Contains("not a set of arguments", ex.Message);
    }

    [Fact]
    public void Anthropic_a_delta_for_a_block_that_never_started_is_refused()
    {
      var text = StreamFixtures.Read(StreamFixtures.Anthropic01);
      var start = StreamFixtures.IndexOf(text, "\"type\":\"content_block_start\",\"index\":1");
      var blocks = StreamFixtures.Blocks(text);
      blocks.RemoveAt(start - 1);

      var ex = Assert.Throws<LlmException>(() => Anthropic(StreamFixtures.Join(blocks)));
      Assert.Contains("event " + start + " (line ", ex.Message);   // the delta that now sits where the start was
      Assert.Contains("never started", ex.Message);
    }

    [Fact]
    public void Anthropic_a_block_started_twice_is_refused()
    {
      var text = StreamFixtures.Read(StreamFixtures.Anthropic02);
      var start = StreamFixtures.IndexOf(text, "\"type\":\"content_block_start\"");
      var blocks = StreamFixtures.Blocks(text);
      blocks.Insert(start, blocks[start - 1]);

      var ex = Assert.Throws<LlmException>(() => Anthropic(StreamFixtures.Join(blocks)));
      Assert.Contains("a second time", ex.Message);
    }

    /// <summary>A REPLY CUT SHORT at an event boundary - the connection closed cleanly mid-answer. Every event is
    /// whole, so only the protocol can tell, and the old loop returned the words so far as the answer.</summary>
    [Theory]
    [InlineData(1)]   // message_stop lost
    [InlineData(3)]   // and the stop reason, and the last block's stop
    [InlineData(6)]   // mid-answer, inside the text
    public void Anthropic_a_reply_cut_short_at_an_event_boundary_is_refused_not_returned_shorter(int dropLast)
    {
      var blocks = StreamFixtures.Blocks(StreamFixtures.Read(StreamFixtures.Anthropic06));
      var cut = StreamFixtures.Join(blocks.Take(blocks.Count - dropLast));

      var ex = Assert.Throws<LlmException>(() => Anthropic(cut));
      Assert.StartsWith(Dwight + "the reply stream ended ", ex.Message);
    }

    [Fact]
    public void Anthropic_a_streamed_error_is_the_providers_own_failure_in_its_words()
    {
      var text = "event: error\ndata: {\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"Overloaded\"}}\n\n";

      var ex = Assert.Throws<LlmException>(() => Anthropic(text));
      Assert.Equal("Overloaded", ex.Message);
    }

    /// <summary>A parseable event of the wrong shape names its event; it is not an unexplained crash.</summary>
    [Fact]
    public void Anthropic_an_event_of_the_wrong_shape_is_refused_by_index()
    {
      var text = StreamFixtures.Read(StreamFixtures.Anthropic01);
      var k = StreamFixtures.IndexOf(text, "\" 2 objects,\"");
      var damaged = StreamFixtures.Damage(text, k, b => b.Replace("\"text\":\" 2 objects,\"", "\"text\":2"));

      var ex = Assert.Throws<LlmException>(() => Anthropic(damaged));
      Assert.Contains("event " + k + " (line ", ex.Message);
      Assert.Contains("shape", ex.Message);
    }

    /// <summary>Every damage class at the SSE layer reaches the user through the Anthropic assembly as the same
    /// failed run - the refusal is not a reader-only property.</summary>
    [Fact]
    public void Anthropic_a_damaged_event_reaches_the_user_as_a_failed_run_naming_the_event()
    {
      var text = StreamFixtures.Read(StreamFixtures.Anthropic03);
      var k = StreamFixtures.IndexOf(text, "eled it");
      var damaged = StreamFixtures.Damage(text, k, b => b.Replace("\"eled it\"", "\"eled\n it\""));

      var ex = Assert.Throws<LlmException>(() => Anthropic(damaged));
      Assert.StartsWith(Dwight + "event " + k + " (line ", ex.Message);
      Assert.EndsWith("Nothing from this reply was used.", ex.Message);
    }

    // ---- OpenAI: the captured stream, and its damage ----------------------------------------------------

    [Fact]
    public void OpenAI_07_reads_its_answer_whole_with_its_finish_reason_and_usage()
    {
      var r = OpenAI(StreamFixtures.Read(StreamFixtures.OpenAI07));

      Assert.Equal("**A single `Number Slider` (1) drives the radius of the `Circle` (2):** the `R` input receives one value on one branch, so the Circle computes one circle in its default plane.",
        r.Blocks.Single().Text);
      Assert.Equal("stop", r.StopReason);
      Assert.Equal(741, r.Usage.InputTokens);
      Assert.Equal(156, r.Usage.OutputTokens);
    }

    /// <summary>25 October 2026, GPT-5.6 Terra on chat-completions: the build request arrives as one call whose
    /// arguments come in fragments; the reader joins them whole and the finish word "tool_calls" becomes "tool_use".</summary>
    [Fact]
    public void OpenAI_live_Terra_stream_assembles_its_request_mutation_call_as_the_file_quotes_it()
    {
      var r = OpenAI(StreamFixtures.Read(StreamFixtures.OpenAILive03));

      Assert.Equal(new[] { "text", "tool_use" }, r.Blocks.Select(x => x.Kind));   // the model spoke first, then called
      Assert.Contains("Number Slider", r.Blocks[0].Text);
      var call = r.Blocks[1];
      Assert.Equal("tool_use", call.Kind);
      Assert.Equal("call_YHNbRJk7kfe0oCEgxa1RAAzP", call.ToolUseId);
      Assert.Equal("request_mutation", call.ToolName);
      Assert.Equal("{\"kind\":\"build\",\"why\":\"Place one loose Number Slider named radius, left unconnected, with Grasshopper\u2019s default 0\u20131 range and 0.250 value.\"," +
        "\"place\":{\"components\":[{\"ref\":\"radius\",\"type\":\"Number Slider\",\"nick\":\"radius\",\"state\":{\"min\":0,\"max\":1,\"value\":0.25,\"rounding\":\"float\"}}],\"wires\":[]}}",
        call.ToolInputJson);
      Assert.Equal("tool_use", r.StopReason);
      Assert.Equal(9060, r.Usage.TotalInputTokens);   // Brief BT: the prompt whole; ForcedCardTests pins its split
      Assert.Equal(147, r.Usage.OutputTokens);
    }

    [Fact]
    public void OpenAI_a_truncated_chunk_is_refused_by_index()
    {
      var text = StreamFixtures.Read(StreamFixtures.OpenAI07);
      var k = StreamFixtures.IndexOf(text, "\"content\":\" single\"");
      var damaged = StreamFixtures.Damage(text, k, b => b.Substring(0, b.IndexOf(" single")));

      var ex = Assert.Throws<LlmException>(() => OpenAI(damaged));
      Assert.StartsWith(Dwight + "event " + k + " (line ", ex.Message);
      Assert.Contains("not valid JSON", ex.Message);
    }

    [Fact]
    public void OpenAI_a_reply_cut_short_before_its_finish_reason_is_refused()
    {
      var text = StreamFixtures.Read(StreamFixtures.OpenAI07);
      var fin = StreamFixtures.IndexOf(text, "\"finish_reason\":\"stop\"");
      var cut = StreamFixtures.Join(StreamFixtures.Blocks(text).Take(fin - 1));

      var ex = Assert.Throws<LlmException>(() => OpenAI(cut));
      Assert.Contains("no finish_reason arrived", ex.Message);
    }

    [Fact]
    public void OpenAI_a_missing_blank_line_between_chunks_is_refused()
    {
      var text = StreamFixtures.Read(StreamFixtures.OpenAI07);
      var blocks = StreamFixtures.Blocks(text);
      blocks[9] = blocks[9] + "\n" + blocks[10];
      blocks.RemoveAt(10);

      var ex = Assert.Throws<LlmException>(() => OpenAI(StreamFixtures.Join(blocks)));
      Assert.StartsWith(Dwight + "event 10 (line ", ex.Message);
    }

    // ---- Ollama (local): the 4096-token default window, filled by the prompt ----------------------------

    private const string OllamaLocalLabel = "Ollama (local)";

    private static string LengthStream(string usage) =>
      "data: {\"choices\":[{\"index\":0,\"delta\":{\"reasoning\":\"The\"},\"finish_reason\":null}]}\n\n" +
      "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"length\"}]}\n\n" +
      "data: {\"choices\":[],\"usage\":" + usage + "}\n\n" +
      "data: [DONE]\n\n";

    [Fact]
    public void Ollama_local_length_finish_with_the_prompt_filling_the_window_says_so()
    {
      // The 25 October capture: prompt_tokens 4095 of total 4096, one completion token.
      var r = OpenAIProvider.ReadStream(StreamFixtures.Reader(LengthStream("{\"prompt_tokens\":4095,\"completion_tokens\":1,\"total_tokens\":4096}")), default, OllamaLocalLabel);

      Assert.Equal("max_tokens", r.StopReason);
      Assert.True(r.ContextWindowFilled);
      Assert.Equal(TurnOutcome.Truncated, AgentLoopCore.PostResponse(r.StopReason, false));
      var note = AgentLoopCore.NoteFor(TurnOutcome.Truncated, null, false, r.ContextWindowFilled);
      Assert.Contains("Ollama's context window is too small for the chat: the prompt alone filled it.", note);
      Assert.Contains("OLLAMA_CONTEXT_LENGTH=32768", note);
    }

    /// <summary>U53: a "length" finish after reasoning deltas and no content is a model that spent its whole reply
    /// thinking. The sentence says so (the shorter-question advice would be false), unless there WAS text.</summary>
    [Fact]
    public void A_length_finish_that_was_all_thinking_says_the_model_wrote_no_answer()
    {
      var usage = "{\"prompt_tokens\":10000,\"completion_tokens\":4000,\"total_tokens\":14000}";
      var r = OpenAIProvider.ReadStream(StreamFixtures.Reader(LengthStream(usage)), default, "Ollama Cloud");

      Assert.Equal("max_tokens", r.StopReason);
      Assert.True(r.ThoughtWithoutAnswer);
      Assert.False(r.ContextWindowFilled);
      var note = AgentLoopCore.NoteFor(TurnOutcome.Truncated, null, false, r.ContextWindowFilled, r.ThoughtWithoutAnswer);
      Assert.StartsWith("The model spent its whole reply thinking and wrote no answer.", note);
      Assert.Contains("Dwight > Settings > Model", note);
      Assert.DoesNotContain("shorter question", note);

      var withText =
        "data: {\"choices\":[{\"index\":0,\"delta\":{\"reasoning\":\"Hm\"},\"finish_reason\":null}]}\n\n" +
        "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Part of an answ\"},\"finish_reason\":null}]}\n\n" +
        "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"length\"}]}\n\n" +
        "data: [DONE]\n\n";
      var cut = OpenAIProvider.ReadStream(StreamFixtures.Reader(withText), default, "Ollama Cloud");
      Assert.False(cut.ThoughtWithoutAnswer);
      Assert.Contains("shorter question", AgentLoopCore.NoteFor(TurnOutcome.Truncated, null, true, false, cut.ThoughtWithoutAnswer));
    }

    [Fact]
    public void A_length_finish_with_room_left_keeps_the_answer_budget_sentence_and_so_does_any_other_provider()
    {
      var roomy = "{\"prompt_tokens\":1000,\"completion_tokens\":3000,\"total_tokens\":4000}";
      var local = OpenAIProvider.ReadStream(StreamFixtures.Reader(LengthStream(roomy)), default, OllamaLocalLabel);
      Assert.False(local.ContextWindowFilled);

      var filled = "{\"prompt_tokens\":4095,\"completion_tokens\":1,\"total_tokens\":4096}";
      var cloud = OpenAIProvider.ReadStream(StreamFixtures.Reader(LengthStream(filled)), default, "Ollama Cloud");
      var openai = OpenAIProvider.ReadStream(StreamFixtures.Reader(LengthStream(filled)), default, "OpenAI");
      Assert.False(cloud.ContextWindowFilled);
      Assert.False(openai.ContextWindowFilled);

      Assert.Contains("token limit", AgentLoopCore.NoteFor(TurnOutcome.Truncated, null, false, false));
    }

    [Theory]
    [InlineData(4095, 1, 4096, true)]
    [InlineData(4094, 2, 4096, true)]    // within two of the total
    [InlineData(1000, 1, 4000, true)]    // one completion token or fewer
    [InlineData(1000, 3000, 4000, false)]
    [InlineData(-1, -1, -1, false)]      // no usage arrived: not claimed
    public void The_window_is_called_filled_only_when_the_usage_shows_it(int prompt, int completion, int total, bool expected) =>
      Assert.Equal(expected, OpenAIProvider.PromptFilledWindow(prompt, completion, total));

    [Fact]
    public void The_404_text_names_the_model_id_it_asked_for()
    {
      var text = OpenAIProvider.DescribeError("Ollama (local)", 404, "{}", "qwen3.5:9b");
      Assert.Contains("could not find the model \"qwen3.5:9b\" (404)", text);
      Assert.DoesNotContain("\"\"", OpenAIProvider.DescribeError("OpenAI", 404, "{}", null));
    }

    // ---- Google: HAND-MADE to the documented shape (not exercised live) ---------------------------------

    [Fact]
    public void Google_the_hand_made_stream_reads_its_answer_and_finish_reason()
    {
      var r = Google(StreamFixtures.Read(StreamFixtures.GoogleHandMade));

      Assert.Equal("A Number Slider drives the radius of a Circle in its default plane.", r.Blocks.Single().Text);
      Assert.Equal("STOP", r.StopReason);
      Assert.Equal(120, r.Usage.InputTokens);
      Assert.Equal(14, r.Usage.OutputTokens);
    }

    [Fact]
    public void Google_a_reply_cut_short_before_its_finish_reason_is_refused()
    {
      var text = StreamFixtures.Read(StreamFixtures.GoogleHandMade);
      var cut = text.Substring(0, text.LastIndexOf("data: ", StringComparison.Ordinal));

      var ex = Assert.Throws<LlmException>(() => Google(cut));
      Assert.Contains("no finishReason arrived", ex.Message);
    }

    /// <summary>A BLOCKED PROMPT is a legitimate end with no candidate at all - the refusal for a reply cut short
    /// must not fire on it (#613). It reads as an empty answer carrying the block reason.</summary>
    [Fact]
    public void Google_a_blocked_prompt_is_a_finished_reply_not_damage()
    {
      var text = "data: {\"promptFeedback\":{\"blockReason\":\"SAFETY\"},\"usageMetadata\":{\"promptTokenCount\":9,\"totalTokenCount\":9}}\r\n\r\n";

      var r = Google(text);
      Assert.Equal("SAFETY", r.StopReason);
      Assert.Equal("", r.Blocks.Single().Text);
    }

    [Fact]
    public void Google_a_damaged_event_is_refused_as_a_failed_run()
    {
      var text = StreamFixtures.Read(StreamFixtures.GoogleHandMade);
      var damaged = text.Replace("\" in its default plane.\"", "\" in its default");

      var ex = Assert.Throws<LlmException>(() => Google(damaged));
      Assert.StartsWith(Dwight + "event 3 (line ", ex.Message);
    }
    // ---- Google and Ollama Cloud: the streams captured on 21 October, once the keys were accepted -----------

    /// <summary>The live Google wire, finally: the hand-made fixture above witnessed the documentation, this one
    /// witnesses what Gemini sent - four events, the text split across them, the finish reason and usage on the last.</summary>
    [Fact]
    public void Google_01_the_live_stream_reads_its_answer_whole_with_its_finish_reason_and_usage()
    {
      var r = Google(StreamFixtures.Read(StreamFixtures.Google01));

      Assert.Equal("**The Number Slider (1)** named `k` is a floating-point numeric input currently awaiting downstream connections to drive parameters elsewhere in the definition.", r.Blocks.Single().Text);
      Assert.Equal("STOP", r.StopReason);
      Assert.Equal(745, r.Usage.InputTokens);
      Assert.Equal(30, r.Usage.OutputTokens);
    }

    /// <summary>Ollama Cloud speaks the OpenAI chat shape through the same adapter, and its stream carries a field
    /// no other captured stream has: twenty `reasoning` deltas before the answer. The answer is the content deltas
    /// alone; not one word of the reasoning reaches the text.</summary>
    [Fact]
    public void OllamaCloud_02_reads_its_answer_from_the_content_deltas_and_passes_over_the_reasoning_ones()
    {
      var r = OpenAIProvider.ReadStream(StreamFixtures.Reader(StreamFixtures.Read(StreamFixtures.OllamaCloud02)), default, "Ollama Cloud");

      Assert.Equal("**The selection is just the Number Slider `k` (1)** \u2014 a single numeric source with no wires in or out, so it currently produces one flat item (`{paths: 1, depth: 1, items: 1}`-style scalar) and drives nothing downstream. As a slider it exposes a bounded value the user can scrub, but with no edges attached it is an isolated input: nothing in the graph consumes it and it computes nothing beyond the number itself.", r.Blocks.Single().Text);
      Assert.DoesNotContain("Explain concisely", r.Blocks.Single().Text);   // the first reasoning delta's words
      Assert.Equal("stop", r.StopReason);
      Assert.Equal(777, r.Usage.InputTokens);
      Assert.Equal(147, r.Usage.OutputTokens);
    }

    // ---- the tool calls the providers REALLY sent (the 25 October gate run) ---------------------------------
    // Each reads, through the one reader, to the call the model made: its id, its name, and its arguments exactly as
    // the stream carried them. The hand-made tool-call fixtures these replace stood for the documented shape only.

    /// <summary>Anthropic sends the arguments in fragments (the first an empty string); the reader joins them, spacing
    /// and all, and the join must be the whole object the model wrote.</summary>
    [Fact]
    public void Anthropic_live_04_joins_a_request_mutation_calls_fragments_into_its_whole_arguments()
    {
      var r = Anthropic(StreamFixtures.Read(StreamFixtures.AnthropicLive04));

      Call(Calls(r).Single(), "toolu_012mxMNyTYXA6Pew4QdSriCG", "request_mutation", "{\"kind\": \"build\", \"why\": \"Add a loose Number Slider named \\\"radius\\\" on the canvas, as requested.\", \"place\": {\"components\":[{\"ref\":\"radiusSlider\",\"type\":\"Number Slider\",\"nick\":\"radius\",\"state\":{\"min\":0,\"max\":10,\"value\":5,\"rounding\":\"float\"}}]}}");
      Assert.Equal("tool_use", r.StopReason);
      Assert.Equal(231, r.Usage.InputTokens);
      Assert.Equal(211, r.Usage.OutputTokens);
    }

    /// <summary>Gemini sends an id and a thought signature on the call part, and finishes the reply with STOP in a later
    /// event; the call is read whole from the part that carried it.</summary>
    [Fact]
    public void Google_live_03_reads_a_request_mutation_call_whole_with_the_id_gemini_sent()
    {
      var r = Google(StreamFixtures.Read(StreamFixtures.GoogleLive03));

      var call = Calls(r).Single();
      Call(call, "call_1488139", "request_mutation", "{\"why\":\"Place an unattached Number Slider named \\u0027radius\\u0027 on the canvas.\",\"kind\":\"build\",\"place\":{\"wires\":[],\"components\":[{\"ref\":\"radius_slider\",\"nick\":\"radius\",\"type\":\"Number Slider\",\"state\":{\"max\":10,\"min\":0,\"value\":5,\"rounding\":\"float\"}}]}}");
      Assert.Equal(2844, call.Signature.Length);
      Assert.Equal("tool_use", r.StopReason);
    }

    /// <summary>GLM-5.3 on Ollama Cloud sends a call whole in one chunk (here 301 characters of arguments), with
    /// its id and index, amid hundreds of `reasoning` deltas that never reach the reply.</summary>
    [Fact]
    public void OllamaCloud_live_03_reads_a_request_mutation_call_whole_from_one_chunk()
    {
      var r = OllamaCloud(StreamFixtures.Read(StreamFixtures.OllamaCloudLive03));

      Call(Calls(r).Single(), "call_cxsn4a1m", "request_mutation", "{\"kind\":\"build\",\"place\":{\"components\":[{\"nick\":\"radius\",\"ref\":\"radius\",\"state\":{\"max\":10,\"min\":0,\"rounding\":\"integer\",\"value\":5},\"type\":\"Number Slider\"}]},\"why\":\"Adds a standalone Number Slider named radius (0-10, integer, starting at 5) to the canvas, unwired, so you can hook it up as you see fit.\"}");
      Assert.Equal("tool_use", r.StopReason);
    }

    /// <summary>Two calls in one step arrive under index 0 and index 1, each whole; they stay two calls, in order.</summary>
    [Fact]
    public void OllamaCloud_live_01_keeps_two_calls_in_one_step_apart_by_index()
    {
      var r = OllamaCloud(StreamFixtures.Read(StreamFixtures.OllamaCloudLive01));

      var calls = Calls(r);
      Assert.Equal(2, calls.Length);
      Call(calls[0], "call_z34eyuln", "get_component", "{\"id\":1}");
      Call(calls[1], "call_go24c99h", "get_component", "{\"id\":2}");
      Assert.Equal("tool_use", r.StopReason);
    }
  }
}
