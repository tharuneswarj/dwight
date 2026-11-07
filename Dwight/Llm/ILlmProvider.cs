using System;
using System.Threading;

namespace Dwight.Llm
{
  /// <summary>
  /// The provider-agnostic seam (ADR-004/009). Dwight's tools and messages are neutral;
  /// each provider adapter translates one request and one response. v1 ships only the
  /// Anthropic adapter; others slot in behind this interface later.
  /// </summary>
  internal interface ILlmProvider
  {
    string ModelId { get; }

    /// <summary>How this provider coerces structured output (ADR-058/066). The <see cref="StructuredOutput"/>
    /// seam reads it to choose the wire strategy; the shared validate/repair/groundedness ceiling is the
    /// same for all capabilities.</summary>
    ProviderCapability Capability { get; }

    /// <summary>Brief BT: does this connection put <see cref="LlmRequest.ForceToolName"/> on the wire? False unless an adapter says so
    /// beside the code that makes it true. Ollama's OpenAI endpoint does not accept tool_choice.</summary>
    bool SendsForcedTool => false;

    /// <summary>Brief BV: does a forced step keep its reasoning on this connection? False unless an adapter says so: forced plus adaptive thinking has a live
    /// record on Sonnet 5 alone, and a 400 there would lose the card the turn exists to raise. This changes no other adapter's wire.</summary>
    bool ThinksWhileForced => false;

    /// <summary>Brief BZ: does a structured reply arrive as the reply's text, with no tool to answer? It decides how a repair is worded, because "call the tool again" names nothing there.</summary>
    bool StructuredArrivesAsText => false;

    /// <summary>Brief BZ: must every earlier message stay as it was sent? Preserved thinking: clearing an earlier tool result invalidates every later thinking block, which is a 400 on an
    /// account created on or after 31 August 2026. False unless an adapter says so.</summary>
    bool KeepsHistoryAppendOnly => false;

    /// <summary>One synchronous round-trip. Throws <see cref="LlmException"/> on failure, or
    /// <see cref="OperationCanceledException"/> if <paramref name="cancel"/> fires (Stop/timeout, WP-3.1) -
    /// which the caller surfaces as the Stopped state, never an error.</summary>
    LlmResponse Send(LlmRequest request, CancellationToken cancel = default);
  }

  /// <summary>A user-presentable LLM failure (network, auth, rate limit). Never carries the API key.</summary>
  internal sealed class LlmException : Exception
  {
    public LlmException(string message) : base(message) { }
  }
}
