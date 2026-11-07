using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// The captured reply streams (U38, #873), and the one-place damage the reader must refuse.
  ///
  /// <para>The six Anthropic files and the OpenAI file are what the providers REALLY sent, written by Dwight's
  /// own capture window on a scratch definition (a slider feeding a circle). They are byte-exact - LF line ends,
  /// no BOM - and <c>.gitattributes</c> keeps git from rewriting them. The Google file's name says HAND-MADE:
  /// Google rejected the key on the capture day, so it is typed from the documented wire shape and witnesses
  /// only that the reader reads the documentation (#200's class - it cannot witness the live wire).</para>
  ///
  /// <para>Every fixture is a run of events, each closed by one blank line, so splitting on "\n\n" yields the
  /// events in order and a test can damage the k-th one and expect the refusal to name event k.</para>
  /// </summary>
  internal static class StreamFixtures
  {
    /// <summary>The xUnit collection every test that runs a provider reader shares with the capture window's own
    /// tests. The window is static, so a reader run in parallel with a capture test records into ITS window and
    /// changes its counts - the first full run found exactly that (a "single" stream that was two).</summary>
    public const string CaptureWindow = TurnShapeTests.StaticRecord;   // one collection with the TurnShape readers: several of these classes drive turns, which Observe into the static counters (CI flake, 30 October)

    public const string Anthropic01 = "anthropic-20261020T162410Z-01.sse";
    public const string Anthropic02 = "anthropic-20261020T162412Z-02.sse";
    public const string Anthropic03 = "anthropic-20261020T162413Z-03.sse";
    public const string Anthropic04 = "anthropic-20261020T162415Z-04.sse";
    public const string Anthropic05 = "anthropic-20261020T162417Z-05.sse";
    public const string Anthropic06 = "anthropic-20261020T162419Z-06.sse";
    public const string OpenAI07 = "openai-20261020T164340Z-07.sse";
    public const string GoogleHandMade = "google-HANDMADE-documented-wire-shape.sse";
    /// <summary>21 October 2026, the day the Google and Ollama Cloud keys were accepted: what those two providers
    /// REALLY sent to Explain > Short on a scratch slider named k, written by the same capture window (a DEBUG build;
    /// the window records nothing in Release). The Ollama Cloud stream is OpenAI-shaped and carries 20 `reasoning`
    /// deltas before its 41 content deltas - the first captured stream with a field the reader must pass over.</summary>
    public const string Google01 = "google-20261021T054519Z-01.sse";
    public const string OllamaCloud02 = "ollamacloud-20261021T054535Z-02.sse";

    /// <summary>25 October 2026, the providers gate run through the bridge on a scratch slider and circle (a DEBUG
    /// build): what Anthropic (Sonnet 5), Google (Gemini 3.8 Flash) and Ollama Cloud (GLM-5.3) REALLY sent to the agent
    /// chat, tool calls included. The gate's capture counter said 12 Google streams were read but only 6 files were
    /// written; the files below are the ones that were written (the capture window was not changed).</summary>
    public const string AnthropicLive01 = "anthropic-20261025T111524Z-01.sse";   // get_overview call
    public const string AnthropicLive02 = "anthropic-20261025T111525Z-02.sse";   // get_region call
    public const string AnthropicLive03 = "anthropic-20261025T111526Z-03.sse";
    public const string AnthropicLive04 = "anthropic-20261025T111547Z-04.sse";   // request_mutation call, arguments in fragments
    public const string AnthropicLive05 = "anthropic-20261025T111608Z-05.sse";
    public const string GoogleLive01 = "google-20261025T111202Z-01.sse";
    public const string GoogleLive02 = "google-20261025T111220Z-02.sse";   // kb_lookup call
    public const string GoogleLive03 = "google-20261025T111225Z-03.sse";   // text parts, then a request_mutation call
    public const string GoogleLive04 = "google-20261025T111243Z-04.sse";   // get_overview call, empty arguments
    public const string GoogleLive05 = "google-20261025T111246Z-05.sse";   // get_region call
    public const string GoogleLive06 = "google-20261025T111248Z-06.sse";
    public const string OllamaCloudLive01 = "ollamacloud-20261025T111337Z-01.sse";   // two get_component calls, index 0 and 1
    public const string OllamaCloudLive02 = "ollamacloud-20261025T111338Z-02.sse";
    public const string OllamaCloudLive03 = "ollamacloud-20261025T111355Z-03.sse";   // request_mutation call in one chunk
    public const string OllamaCloudLive04 = "ollamacloud-20261025T111418Z-04.sse";   // get_overview + search
    public const string OllamaCloudLive05 = "ollamacloud-20261025T111421Z-05.sse";   // one get_component call
    public const string OllamaCloudLive06 = "ollamacloud-20261025T111423Z-06.sse";

    /// <summary>25 October 2026, the providers gate's third run: what OpenAI (GPT-5.6 Terra, chat-completions) REALLY
    /// sent to the agent chat on the same scratch definition. The capture's CRLF line ends are written as LF here.</summary>
    public const string OpenAILive01 = "openai-20261025T113002Z-01.sse";   // the final read answer, text, finish "stop"
    public const string OpenAILive02 = "openai-20261025T113015Z-02.sse";   // get_overview call, arguments "{}"
    public const string OpenAILive03 = "openai-20261025T113017Z-03.sse";   // request_mutation call, arguments in fragments
    public const string OpenAILive04 = "openai-20261025T113034Z-04.sse";   // get_overview call
    public const string OpenAILive05 = "openai-20261025T113035Z-05.sse";   // get_region call, arguments in five fragments
    public const string OpenAILive06 = "openai-20261025T113036Z-06.sse";   // the final build answer, text, finish "stop"

    /// <summary>31 October 2026, Brief BW's gate on the DEBUG build of 2e4f822: what OpenAI's Responses endpoint REALLY sent to the agent chat on a scratch definition, REDUCED.
    /// Every event is real and in its real order. Only the whole-response echo on the lifecycle events (the system prompt and the tools, on every one) is cut to its reply
    /// fields, and each encrypted_content is replaced. What the hand-made set could not show: no [DONE]; response.content_part.done; obfuscation on the deltas; phase and
    /// call_id on items; an encrypted_content on the added event that differs from the done event's, which is the one to carry back. No request met a failed, incomplete or refused reply, so those three stay HANDMADE.</summary>
    public const string ResponsesCall1 = "openai-responses-20261031T112516Z-01.sse";      // GPT-5.6 Terra: reasoning, get_overview
    public const string ResponsesCall2 = "openai-responses-20261031T112517Z-02.sse";      // Terra, the next step: reasoning, get_scaffold_map
    public const string ResponsesParallel = "openai-responses-20261031T112448Z-03.sse";   // Terra: reasoning, then three calls
    public const string ResponsesText = "openai-responses-20261031T112454Z-06.sse";       // Terra: the final answer, no reasoning item
    public const string ResponsesSolText = "openai-responses-20261031T112731Z-02.sse";    // GPT-6.1 Sol: a read answer
    public const string ResponsesForced = "openai-responses-20261031T113011Z-03.sse";     // GPT-6 Astra: a named forced step at medium: reasoning, a message, the call

    public static readonly string[] ResponsesLive =
      { ResponsesCall1, ResponsesCall2, ResponsesParallel, ResponsesText, ResponsesSolText, ResponsesForced };

    /// <summary>The live tool-call stream each provider's tool-call tests read (the hand-made ones they replaced are gone).</summary>
    public const string GoogleToolCall = GoogleLive03;
    public const string OllamaToolCall = OllamaCloudLive05;
    public const string OpenAIToolCall = OpenAILive05;

    /// <summary>HAND-MADE tool-call streams (Phase 10, Brief H): typed from each provider's documented wire shape, not
    /// captured live. Each file's header comment cites the page and says what could not be checked. They witness that
    /// the reader reads the DOCUMENTATION. Kept only where no live capture covers the case: parallel calls on OpenAI and on
    /// Google (every live step on both carried exactly one call).</summary>
    public const string OpenAIParallelCallsHandMade = "openai-HANDMADE-parallel-calls-documented-wire-shape.sse";
    public const string GoogleParallelCallsHandMade = "google-HANDMADE-parallel-calls-documented-wire-shape.sse";

    public static readonly string[] Captured =
    {
      Anthropic01, Anthropic02, Anthropic03, Anthropic04, Anthropic05, Anthropic06, OpenAI07, Google01, OllamaCloud02,
      OpenAILive01, OpenAILive02, OpenAILive03, OpenAILive04, OpenAILive05, OpenAILive06,
      AnthropicLive01, AnthropicLive02, AnthropicLive03, AnthropicLive04, AnthropicLive05,
      GoogleLive01, GoogleLive02, GoogleLive03, GoogleLive04, GoogleLive05, GoogleLive06,
      OllamaCloudLive01, OllamaCloudLive02, OllamaCloudLive03, OllamaCloudLive04, OllamaCloudLive05, OllamaCloudLive06,
    };

    public static readonly string[] AnthropicCaptured =
      { Anthropic01, Anthropic02, Anthropic03, Anthropic04, Anthropic05, Anthropic06 };

    public static string Read(string name) =>
      File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "streams", name), new UTF8Encoding(false));

    public static TextReader Reader(string text) => new StringReader(text);

    /// <summary>The fixture's events as raw blocks (field lines, no closing blank line), in order.</summary>
    public static List<string> Blocks(string text)
    {
      if (!text.EndsWith("\n\n")) throw new InvalidOperationException("fixture does not end with a closed event");
      return text.Substring(0, text.Length - 2).Split(new[] { "\n\n" }, StringSplitOptions.None).ToList();
    }

    public static string Join(IEnumerable<string> blocks) => string.Join("\n\n", blocks) + "\n\n";

    /// <summary>The fixture with its k-th event (1-based) rewritten by <paramref name="damage"/>.</summary>
    public static string Damage(string text, int k, Func<string, string> damage)
    {
      var b = Blocks(text);
      b[k - 1] = damage(b[k - 1]);
      return Join(b);
    }

    /// <summary>The 1-based index of the first event whose block contains <paramref name="needle"/>.</summary>
    public static int IndexOf(string text, string needle)
    {
      var b = Blocks(text);
      for (var i = 0; i < b.Count; i++) if (b[i].Contains(needle)) return i + 1;
      throw new InvalidOperationException("no event contains " + needle);
    }
  }
}
