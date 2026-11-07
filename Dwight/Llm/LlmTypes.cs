using System.Collections.Generic;
using System.Linq;

namespace Dwight.Llm
{
  internal enum LlmRole { User, Assistant }

  /// <summary>How a provider coerces structured output (ADR-058/066). The <see cref="StructuredOutput"/>
  /// seam reads this to pick the strongest strategy each provider supports; the validate + one-repair +
  /// clean-failure ceiling holds for all of them.</summary>
  internal enum ProviderCapability
  {
    ToolCall,        // Anthropic: force a single output tool, read its input
    StrictSchema,    // OpenAI: response_format json_schema (server-enforced)
    ResponseSchema,  // Google: generationConfig.responseSchema + json mime
    Format,          // Ollama-local: native `format` json-schema where the model supports it
    PromptOnly,      // weakest path: schema in the prompt + json_object, then validate + repair
  }

  /// <summary>
  /// A provider-neutral content block. The Anthropic adapter (and any future adapter)
  /// translates these to/from the provider's wire shape, so nothing outside the adapter
  /// ever sees a provider-specific block (ADR-004).
  /// </summary>
  internal sealed class LlmBlock
  {
    public string Kind { get; set; }          // "text" | "image" | "tool_use" | "tool_result" | "thinking" | "redacted_thinking"
    public string Text { get; set; }          // text, or the tool_result content, or the thinking summary
    public string ToolUseId { get; set; }     // tool_use / tool_result
    public string ToolName { get; set; }      // tool_use
    public string ToolInputJson { get; set; } // tool_use (a JSON object string)

    /// <summary>
    /// Phase-8 WP-2 (#192b): the thinking block's opaque signature, carried so an assistant turn can be
    /// echoed back EXACTLY as received.
    ///
    /// WHY THIS FIELD EXISTS. With thinking on, a continuation must return the assistant turn unmodified
    /// - rebuilding it, or dropping a thinking block, is a 400. The neutral block model carried only
    /// text/tool_use/tool_result, so a thinking block was parsed and then silently DISCARDED: the very
    /// first repair round-trip in <see cref="StructuredOutput"/> would have echoed a stripped turn and
    /// failed, before WP-3's agent loop was ever written.
    ///
    /// An EMPTY <see cref="Text"/> with a populated signature is the NORM, not an edge case - display
    /// defaults to omitted on the current families, so the summary text is blank and the signature is
    /// all there is. Round-tripping a blank-text block verbatim is exactly what must happen.
    /// </summary>
    public string Signature { get; set; }     // thinking

    /// <summary>The opaque payload of a <c>redacted_thinking</c> block. Never inspected, never altered -
    /// only carried back, because filtering one out is likewise a 400.</summary>
    public string Data { get; set; }          // redacted_thinking

    /// <summary>
    /// Web search through Anthropic's own server tool (Brief V): the block exactly as the provider sent it, as a JSON object
    /// string, for the two kinds a later request must return UNCHANGED - <c>server_tool_use</c> (the search the model made)
    /// and <c>web_search_tool_result</c> (what the provider found, with <c>encrypted_content</c> the API decrypts on the next
    /// request; a missing or altered one is a 400). Never inspected, never rebuilt - only carried back, like a thinking block.
    /// Null on every other kind, except on OpenAI's Responses endpoint (Brief BV), where a <c>text</c> or <c>tool_use</c> block also
    /// holds its output item and a <see cref="ResponseItemKind"/> block is nothing else, because every item goes back unchanged.
    /// </summary>
    public string RawJson { get; set; }

    /// <summary>Brief BV: an output item of OpenAI's Responses endpoint that is not prose or a call: a reasoning item with its
    /// <c>encrypted_content</c>, or anything the endpoint adds. With <c>store: false</c> the next step must carry it back unchanged. Never
    /// inspected, never shown, and only ever inside one turn's transcript, as a thinking block is.</summary>
    public const string ResponseItemKind = "response_item";

    public static LlmBlock ResponseItem(string rawJson) => new LlmBlock { Kind = ResponseItemKind, RawJson = rawJson };

    /// <summary>
    /// On a <c>text</c> block: the citations array the provider attached to it, as a JSON array string, replayed on the
    /// same block (their <c>encrypted_index</c> is "a reference that must be passed back for multi-turn conversations").
    /// Null for a text block without citations, which is every text block unless web search is on.
    /// </summary>
    public string CitationsJson { get; set; }

    /// <summary>Brief DX: on an <c>image</c> block, what the person attached: "image/png" or "image/jpeg", and the bytes as base64. Kept apart from <see cref="Text"/> so nothing that
    /// counts or searches a transcript's words reads megabytes of them. Each adapter writes it in its own provider's shape.</summary>
    public string MediaType { get; set; }
    public string Base64 { get; set; }

    /// <summary>What the image weighs against the transcript's bound, by the measured rule (<see cref="Dwight.Session.ChatImage.Tokens"/>) on every model: a rough weight beats none, and neither a person nor a provider reads it.</summary>
    public int ImageTokens { get; set; }

    /// <summary>Does the measured rule hold for this model? Only then is <see cref="ImageTokens"/> a price: a stopped step counts the image from it, and otherwise leaves the image out and says so.</summary>
    public bool ImageRuleKnown { get; set; }

    public const string ImageKind = "image";

    public static LlmBlock Image(string mediaType, string base64, int imageTokens = 0, bool ruleKnown = false) =>
      new LlmBlock { Kind = ImageKind, MediaType = mediaType, Base64 = base64, ImageTokens = imageTokens, ImageRuleKnown = ruleKnown };

    public static LlmBlock TextBlock(string text) => new LlmBlock { Kind = "text", Text = text };

    public static LlmBlock ServerToolUse(string id, string rawJson) =>
      new LlmBlock { Kind = "server_tool_use", ToolUseId = id, RawJson = rawJson };

    public static LlmBlock WebSearchResult(string toolUseId, string rawJson) =>
      new LlmBlock { Kind = "web_search_tool_result", ToolUseId = toolUseId, RawJson = rawJson };

    public static LlmBlock Thinking(string text, string signature) =>
      new LlmBlock { Kind = "thinking", Text = text ?? "", Signature = signature };

    public static LlmBlock RedactedThinking(string data) =>
      new LlmBlock { Kind = "redacted_thinking", Data = data };

    public static LlmBlock ToolUse(string id, string name, string inputJson) =>
      new LlmBlock { Kind = "tool_use", ToolUseId = id, ToolName = name, ToolInputJson = inputJson };

    public static LlmBlock ToolResult(string id, string content) =>
      new LlmBlock { Kind = "tool_result", ToolUseId = id, Text = content };
  }

  internal sealed class LlmMessage
  {
    public LlmRole Role { get; set; }
    public List<LlmBlock> Blocks { get; set; } = new List<LlmBlock>();

    public static LlmMessage User(string text) =>
      new LlmMessage { Role = LlmRole.User, Blocks = { LlmBlock.TextBlock(text) } };

    public static LlmMessage Assistant(List<LlmBlock> blocks) =>
      new LlmMessage { Role = LlmRole.Assistant, Blocks = blocks };
  }

  /// <summary>A tool defined once in a neutral schema; the adapter maps it to the provider format.</summary>
  internal sealed class LlmTool
  {
    public string Name { get; set; }
    public string Description { get; set; }
    public string InputSchemaJson { get; set; } // a JSON Schema object, as a string
  }

  internal sealed class LlmRequest
  {
    public string System { get; set; }
    public List<LlmMessage> Messages { get; set; } = new List<LlmMessage>();
    public List<LlmTool> Tools { get; set; } = new List<LlmTool>();
    /// <summary>
    /// The ANSWER budget - what the model may spend on the reply the caller actually wants. When
    /// thinking is on, the provider seam adds a reserve ON TOP of this rather than carving it out
    /// (#216, <see cref="ThinkingBudget"/>).
    ///
    /// The default is ADR-030's pinned value, not a smaller number of its own. It used to be 4096 while
    /// <c>DwightConfig.MaxTokens</c> was 16000, so any call site that forgot to set it silently took the
    /// smaller ceiling - a drift with no symptom until an answer came back short. Every shipping caller
    /// passes it explicitly today; this makes forgetting harmless rather than quietly wrong.
    /// </summary>
    public int MaxTokens { get; set; } = ThinkingBudget.DefaultAnswerBudget;

    /// <summary>RR20 #62: sampling temperature, when the provider allows it. The EVAL sets 0 so an A/B measures the
    /// change and not the sampler - at n~50 a few points of run-to-run drift would swamp the effect we are trying
    /// to see. Null = leave the provider's default alone (the product path).</summary>
    public double? Temperature { get; set; }

    /// <summary>When set, the provider is asked to FORCE this exact tool (Anthropic
    /// <c>tool_choice:{type:tool,name}</c>) - the structured-output path (ADR-058/A5): the model must
    /// answer by calling the tool, so the reply is shape-constrained. Null = the model chooses freely.
    /// Brief BT: the agent loop sets it too, on a follow-up turn that must raise its card; each adapter's
    /// <see cref="ILlmProvider.SendsForcedTool"/> says whether it reaches the wire.</summary>
    public string ForceToolName { get; set; }

    /// <summary>
    /// Brief CN: this step may not call a tool, and <see cref="Tools"/> stays as it is. An empty list is not the way to say so: a tool_use in the history needs its tools defined, and
    /// on a preserved-thinking model removing one breaks the conversation's prefix, where tool_choice sits outside it. Each adapter says "none" in its own words; Ollama's endpoint
    /// takes no tool_choice, so there the tools are left off.
    /// </summary>
    public bool ForbidTools { get; set; }

    /// <summary>The desired structured output, provider-neutral (ADR-058/066): the seam sets these and each
    /// adapter coerces per its <see cref="ProviderCapability"/> (forced tool / strict json_schema /
    /// response-schema / format / prompt-injected), populating <see cref="LlmResponse.StructuredJson"/>.
    /// Null = a free-form (prose) request.</summary>
    public string StructuredName { get; set; }
    public string StructuredDescription { get; set; }
    public string StructuredSchemaJson { get; set; }

    /// <summary>Mark the stable prefix (tools + system) as prompt-cached (ADR-037). The adapter
    /// puts a <c>cache_control</c> breakpoint on the system block, which caches the tools
    /// and the system together, and (U57) a second on the last non-thinking block of the last message,
    /// so each step of a turn reads the prior transcript from cache. Both are placed at serialisation
    /// only; the stored transcript never carries one.</summary>
    public bool CacheSystem { get; set; }

    /// <summary>Phase-8 WP-2 (ADR-082 completed): the TASK's thinking decision, resolved by
    /// <see cref="Dwight.Models.ModelResolver"/>. What actually goes on the wire depends on the model
    /// family too - see <see cref="ThinkingWire"/>. False for every comprehension task.</summary>
    public bool Thinking { get; set; }

    /// <summary>Reasoning depth when <see cref="Thinking"/> is on ("low".."max"), from the user's
    /// profile. Null takes the API default. Changing it between requests invalidates the prompt cache,
    /// so it is held stable for a task rather than varied per call.</summary>
    public string Effort { get; set; }

    /// <summary>
    /// Brief V: ask the PROVIDER to search the web inside this request (Anthropic's server tool, <c>web_search_20250305</c>).
    /// False for every request unless the user turned web search on AND the provider has such a tool; false leaves the body
    /// byte-identical to what it was before the feature existed (pinned by a test). The client-tool providers (Ollama,
    /// Google) do not use this - for them <c>web_search</c> is an ordinary tool in <see cref="Tools"/>.
    /// </summary>
    public bool ServerWebSearch { get; set; }
  }

  /// <summary>Token accounting returned with a response - drives the honest cost/speed estimate
  /// (ADR-041) and confirms prompt-cache hits (ADR-037). All fields are best-effort: a provider
  /// that doesn't report a field leaves it zero.</summary>
  internal sealed class LlmUsage
  {
    public int InputTokens { get; set; }              // uncached input billed at full price
    public int OutputTokens { get; set; }             // generated tokens
    public int CacheReadInputTokens { get; set; }     // served from cache (~0.1x) => a cache hit
    public int CacheCreationInputTokens { get; set; } // written to cache this request (~1.25x)

    /// <summary>
    /// Phase-8 WP-2 Stage C (#199): billed reasoning tokens, from
    /// <c>usage.output_tokens_details.thinking_tokens</c>. Part of <see cref="OutputTokens"/>, not extra.
    ///
    /// This is the INBOUND half of the ADR-082 proof. The request shape shows what Dwight ASKED for;
    /// only this shows what the model actually DID. Above zero on a Scaffold call proves it really
    /// thought; ZERO on a comprehension call is the evidence that "thinking stays off for comprehension"
    /// is true in fact and not merely in intent - which matters because thinking is billed as output,
    /// so a silent regression here is an invisible charge on the user's own key.
    ///
    /// Reported only when the provider sends it; absent leaves it 0 (ADR-029 best-effort, like the rest
    /// of this type). When streaming, it arrives on the final <c>message_delta</c>.
    /// </summary>
    public int ThinkingTokens { get; set; }

    /// <summary>Brief V: searches the provider ran and bills per search - Anthropic's
    /// <c>usage.server_tool_use.web_search_requests</c>, or the count of searches Dwight made through a client tool
    /// (Ollama's endpoint, Google's grounded call). Zero unless web search was on.</summary>
    public int WebSearchRequests { get; set; }

    public bool CacheHit => CacheReadInputTokens > 0;
    public int TotalInputTokens => InputTokens + CacheReadInputTokens + CacheCreationInputTokens;
  }

  /// <summary>
  /// Brief BZ: a refusal is an answer from the provider's safety checks, a 200 with <c>stop_reason: "refusal"</c>, often with nothing in it. It is a first-class outcome in the plugin's
  /// words and never an empty reply. Any partial output is discarded, as the provider's page says to.
  ///
  /// <para>No fallback model runs. The API offers one, and a change of model beneath a verdict is the #208 class, so doing that visibly is a decision of its own.</para>
  /// </summary>
  internal static class LlmRefusal
  {
    public const string StopReason = "refusal";

    public static bool Is(LlmResponse response) => response != null && string.Equals(response.StopReason, StopReason, System.StringComparison.Ordinal);

    /// <summary>The sentence the user reads. The category is the provider's own word, read as far as the page defines it; null and any new word get the plain form. It says the check can
    /// be wrong, because the page says benign work can trigger these, and it names what to do. Plain text: the caller escapes it for its destination (#335).</summary>
    public static string Sentence(string category)
    {
      string read;
      switch (category)
      {
        case "cyber": read = "possible cybersecurity harm"; break;
        case "bio": read = "possible biological harm"; break;
        case "frontier_llm": read = "help building a competing AI model"; break;
        case "reasoning_extraction": read = "a request to write out its private reasoning"; break;
        case "general_harms": read = "another area of its usage policy"; break;
        default: read = null; break;
      }
      return "Claude declined this request" + (read == null ? "" : ": its safety check read it as " + read) +
             ". Ordinary work can trigger this, and anything already done stands. Dwight did not retry on another model. Reword the request, or pick another model.";
    }

    /// <summary>Is this refusal billed? One that arrives before any output is billed only in the categories the provider's page names (read 31 October 2026), and one mid-stream always is.
    /// Tokens are reported either way, so the cost must not count a refusal the provider does not charge for.</summary>
    public static bool IsBilled(LlmResponse response)
    {
      if (response == null) return false;
      var output = response.Usage != null && response.Usage.OutputTokens > 0;
      if (output || response.Blocks.Count > 0) return true;
      return response.RefusalCategory == "bio" || response.RefusalCategory == "frontier_llm" || response.RefusalCategory == "reasoning_extraction";
    }
  }

  internal sealed class LlmResponse
  {
    public string StopReason { get; set; }

    /// <summary>Brief BZ: <c>stop_details.category</c> when <see cref="StopReason"/> is a refusal, or null. Informational: a refusal is decided by the stop reason alone, and null is a normal value.</summary>
    public string RefusalCategory { get; set; }

    /// <summary>Brief V: the <c>error_code</c> of each <c>web_search_tool_result_error</c> in this reply (e.g.
    /// <c>max_uses_exceeded</c>), so Dwight can say in its own voice that a search did not run. The provider still returns
    /// 200 for these; the model reads the error in its context and answers anyway, so this is not a failed run.</summary>
    public List<string> WebSearchErrors { get; set; } = new List<string>();

    public List<LlmBlock> Blocks { get; set; } = new List<LlmBlock>();
    public LlmUsage Usage { get; set; } = new LlmUsage();

    /// <summary>The structured-output JSON the adapter extracted (per its capability), or null for a
    /// prose reply. The <see cref="StructuredOutput"/> seam reads this uniformly across providers.</summary>
    public string StructuredJson { get; set; }

    /// <summary>True when a "length" finish came from a local model whose context window the prompt alone filled
    /// (Ollama's 4096-token default). The truncation sentence then names that cause instead of the answer budget.</summary>
    public bool ContextWindowFilled { get; set; }

    /// <summary>True when a "length" finish came after reasoning deltas and NO content delta and no call: the model
    /// spent the whole reply thinking and wrote no answer (GLM-5.3 on Ollama Cloud, 25 October: 4000 of 4000
    /// tokens). The truncation sentence then says that, instead of advising a shorter question.</summary>
    public bool ThoughtWithoutAnswer { get; set; }

    /// <summary>
    /// The reply's prose. CONSECUTIVE text blocks are joined VERBATIM, inserting nothing (U82): with web search on, Anthropic
    /// splits one passage into several text blocks at each citation, sometimes in the middle of a Markdown table row, and a
    /// newline per block turned the row into broken lines no table parser reads. Text blocks that something else separates (a
    /// search call, a result) are different passages and keep one newline between them, as before.
    /// </summary>
    public string Text
    {
      get
      {
        var sb = new System.Text.StringBuilder();
        var previousWasText = false;
        var any = false;
        foreach (var b in Blocks)
        {
          if (b.Kind != "text") { previousWasText = false; continue; }
          if (any && !previousWasText) sb.Append('\n');
          sb.Append(b.Text);
          previousWasText = true; any = true;
        }
        return sb.ToString();
      }
    }
    public List<LlmBlock> ToolUses => Blocks.Where(b => b.Kind == "tool_use").ToList();
    public bool WantsTool => StopReason == "tool_use" || ToolUses.Count > 0;
  }
}
