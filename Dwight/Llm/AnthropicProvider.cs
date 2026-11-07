using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;

namespace Dwight.Llm
{
  /// <summary>
  /// The Anthropic adapter: one neutral <see cref="LlmRequest"/> in, one
  /// <see cref="LlmResponse"/> out, over the Messages API via plain HTTPS (CLAUDE.md: no
  /// SDK for v1 - <c>System.Net.Http</c> + <c>System.Text.Json</c>). All Anthropic-specific
  /// shapes (the content blocks, the header set, the SSE stream, the error envelope) are
  /// contained here so the rest of Dwight stays provider-neutral (ADR-004).
  ///
  /// Streaming (ADR-030): the request is sent with <c>stream:true</c> and the response is
  /// consumed as Server-Sent Events. A long generation (e.g. reorganising 20-30 components)
  /// keeps the connection fed token-by-token, so it never trips the idle request timeout that
  /// a non-streaming call hits on large outputs. Found in Tharun's Phase 2 runtime test.
  /// </summary>
  internal sealed class AnthropicProvider : ILlmProvider
  {
    private const string Endpoint = "https://api.anthropic.com/v1/messages";
    private const string AnthropicVersion = "2023-06-01";

    // Generous backstop: with streaming the connection is actively fed, so this only guards a
    // genuinely wedged request, not normal long generations.
    private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

    private readonly string _apiKey;

    public string ModelId { get; }

    /// <summary>Anthropic coerces structured output by forcing a single output tool (ADR-058).</summary>
    public ProviderCapability Capability => ProviderCapability.ToolCall;

    /// <summary>Brief BT: <see cref="AnthropicRequestBody"/> writes it as <c>tool_choice:{type:tool,name}</c>. Brief BZ: Sonnet 5.5, Opus 5.5 and Fable 5.1 answer that with a 400,
    /// so it is the row's fact, and they take the unforced follow-up.</summary>
    public bool SendsForcedTool => Row?.ForcedToolChoice ?? true;

    /// <summary>Brief BZ: on these rows a verb's JSON is the reply's text, not a tool call.</summary>
    public bool StructuredArrivesAsText => Row?.StructuredFormat ?? false;

    /// <summary>Brief BZ: on these rows an earlier message may not be rewritten (preserved thinking), so the loop does not elide.</summary>
    public bool KeepsHistoryAppendOnly => (Row?.ThinkingBound ?? false) && !AnthropicWire.ElideAnywayForGate;

    private ModelInfo Row => Dwight.Models.ModelCatalogue.Find(ModelId);

    public AnthropicProvider(string apiKey, string modelId)
    {
      _apiKey = apiKey;
      ModelId = modelId;
    }

    public LlmResponse Send(LlmRequest request, CancellationToken cancel = default)
    {
      // The schema pre-flight (LlmSchemaCheck): a tool whose schema cannot be published is not
      // published. Refused HERE rather than only in the agent loop, because the five verbs never
      // run that loop - this is the seam every request passes through, before the HTTP call and
      // before anything is spent.
      var badSchema = LlmSchemaCheck.Check(request);
      if (badSchema != null) throw new LlmException("Dwight could not send this request: " + badSchema + ".");

      var body = BuildBody(request);
      LlmTrace.RecordRequest(ModelId, body);   // DEBUG-only; compiles out of Release (#199)

      using (var msg = new HttpRequestMessage(HttpMethod.Post, Endpoint))
      {
        msg.Headers.Add("x-api-key", _apiKey);
        msg.Headers.Add("anthropic-version", AnthropicVersion);
        var beta = AnthropicWire.Of(Row).BetaHeader;
        if (beta != null) msg.Headers.Add("anthropic-beta", beta);
        msg.Content = new StringContent(body, Encoding.UTF8, "application/json");

        HttpResponseMessage resp;
        try
        {
          resp = Http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, cancel).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) { throw; }   // WP-3.1: Stop/timeout - surfaced as Stopped, not an error
        catch (Exception ex)
        {
          throw new LlmException($"Could not reach Claude ({ex.Message}). Check your internet connection.");
        }

        using (resp)
        {
          if (!resp.IsSuccessStatusCode)
          {
            var errBody = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            throw new LlmException(AnthropicRequestBody.DescribeError((int)resp.StatusCode, errBody,
              AnthropicRequestBody.RetentionClause(Row?.DisplayName, Row?.RetentionDays ?? 0)));
          }

          using (var stream = resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
          using (var reader = new StreamReader(stream, Encoding.UTF8))
          {
            // U38 (#873): the ONE spec-correct reader - a damaged reply is refused here, before anything in it
            // is used, as a failed run in Dwight's words; never skipped into a shorter answer.
            var parsed = AnthropicReplyStream.Read(reader, cancel);
            // The inbound half (#199), with TWO independent witnesses: the billed token count from the
            // usage object, and the thinking-block count from the content stream. They cannot fail the
            // same way, so agreeing at zero is evidence and disagreeing names the defect.
            LlmTrace.RecordResponse(parsed.Usage,
              parsed.Blocks.Count(b => b.Kind == "thinking" || b.Kind == "redacted_thinking"),
              parsed.StopReason);   // "max_tokens" means CUT OFF, which no token count reveals on its own
            AnthropicRequestBody.SurfaceStructured(parsed, request, AnthropicWire.Of(Row));
            return parsed;
          }
        }
      }
    }

    /// <summary>
    /// Build the request body. The body itself is a pure function in <see cref="AnthropicRequestBody"/>
    /// so it can be asserted headlessly; this resolves the one thing that needs live catalogue data -
    /// the THREE-STATE thinking wire (#192).
    ///
    /// The state comes from the model's <c>thinkingDefault</c> fact, never from matching the model id:
    /// omitting the field runs thinking on some families and not others, so a single boolean would have
    /// honoured ADR-082's "thinking off for comprehension" on one family while silently breaking it -
    /// and being billed for it - on another.
    /// </summary>
    private string BuildBody(LlmRequest request)
    {
      var info = Row;
      var state = ThinkingWire.Resolve(request.Thinking, info?.ThinkingDefault);
      return AnthropicRequestBody.Build(request, ModelId, state, AnthropicWire.Of(info));
    }
  }
}
