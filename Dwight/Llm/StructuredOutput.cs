using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;

namespace Dwight.Llm
{
  /// <summary>
  /// The provider-capability output abstraction (ADR-058 / Spec A5): each adapter declares its
  /// <see cref="ProviderCapability"/> and coerces the structured request on the wire its own way (forced
  /// tool / strict json_schema / response-schema / format / prompt-injected); this seam runs ONE shared
  /// ceiling for every provider - read the structured result, validate against the flat schema, and on
  /// failure run exactly ONE repair round-trip before a clean failure. Phase 5 wires Anthropic, OpenAI,
  /// Google, and Ollama (local + Cloud) behind it (ADR-066). Five wire strategies, one behaviour.
  /// </summary>
  /// <summary>
  /// A BILLED CALL THAT THROWS STILL CARRIES ITS BILL OUT (Brief DF). A provider charges for a reply whether or not the caller gets to use it: a repair
  /// round-trip that throws after a first turn that was billed, a refusal the provider billed, a Stop that lands after the first turn. The usage rides on the
  /// exception's own <see cref="Exception.Data"/>, so the exception's TYPE is unchanged (every existing catch still catches it) and the caller that
  /// wants the bill reads it with <see cref="TryTake"/> before it records the run's one ledger line. Nothing a provider billed is thrown away in a catch.
  /// </summary>
  internal static class UsageCarrier
  {
    private const string Key = "dwight.billed-usage";

    /// <summary>Put the usage on the exception, only when something was billed. Never throws.</summary>
    public static void Attach(Exception ex, LlmUsage usage)
    {
      if (ex == null || usage == null) return;
      if (usage.InputTokens + usage.OutputTokens + usage.CacheReadInputTokens + usage.CacheCreationInputTokens <= 0) return;
      try { ex.Data[Key] = usage; } catch { }
    }

    /// <summary>The usage an exception carries, or false when it carries none.</summary>
    public static bool TryTake(Exception ex, out LlmUsage usage)
    {
      usage = null;
      try { usage = ex?.Data[Key] as LlmUsage; } catch { }
      return usage != null;
    }
  }

  internal static class StructuredOutput
  {
    /// <summary>The validated structured result: the JSON object string, the aggregated usage, and (on
    /// failure after the single repair) the reason.</summary>
    internal sealed class Result
    {
      public bool Ok;
      public string Json;          // the validated object, ready to parse
      public string Error;         // a calm reason when !Ok
      public LlmUsage Usage = new LlmUsage();
    }

    /// <summary>
    /// Get a schema-shaped object from the model. <paramref name="validate"/> returns (ok, error) for a
    /// candidate JSON string; the first failure triggers one repair, fed back as a tool_result so the
    /// tool_use/tool_result pairing Anthropic requires stays intact.
    /// </summary>
    public static Result Get(
      ILlmProvider provider,
      string system,
      string userPrompt,
      string toolName,
      string toolDescription,
      string schemaJson,
      Func<string, (bool ok, string error)> validate,
      int maxTokens,
      CancellationToken cancel = default,
      double? temperature = null,
      bool thinking = false,
      string effort = null)
    {
      var result = new Result();
      try
      {
        return Run(result, provider, system, userPrompt, toolName, toolDescription, schemaJson, validate, maxTokens, cancel, temperature, thinking, effort);
      }
      catch (Exception ex)
      {
        // Brief DF: a first turn that was billed is not lost with the local when the repair turn throws (or a Stop lands between them).
        UsageCarrier.Attach(ex, result.Usage);
        throw;
      }
    }

    private static Result Run(
      Result result,
      ILlmProvider provider,
      string system,
      string userPrompt,
      string toolName,
      string toolDescription,
      string schemaJson,
      Func<string, (bool ok, string error)> validate,
      int maxTokens,
      CancellationToken cancel,
      double? temperature,
      bool thinking,
      string effort)
    {
      var messages = new List<LlmMessage> { LlmMessage.User(userPrompt) };

      for (var attempt = 0; attempt < 2; attempt++)   // initial + one repair - the SAME ceiling for every provider
      {
        cancel.ThrowIfCancellationRequested();   // WP-3.1: don't start (or retry) a stopped call
        // Provider-neutral structured request: each adapter coerces per its capability (forced tool /
        // strict json_schema / response-schema / format / prompt-injected) and exposes the result the same way.
        var response = provider.Send(new LlmRequest
        {
          System = system,
          Messages = messages,
          StructuredName = toolName,
          StructuredDescription = toolDescription,
          StructuredSchemaJson = schemaJson,
          MaxTokens = maxTokens,
          CacheSystem = true,
          Temperature = temperature,   // RR20 #62: the eval pins this to 0; the product leaves it null
          // ADR-082 completed (WP-2): the task's thinking decision. False for every comprehension task,
          // which on a default-on model family means an EXPLICIT disable rather than silence (#192).
          // The repair turn below echoes the assistant blocks back, so thinking blocks must round-trip
          // verbatim - see LlmBlock.Signature.
          Thinking = thinking,
          Effort = effort,
        }, cancel);

        // Brief BZ: a refusal is its own outcome too. A repair would ask the same checks the same question and be charged for it.
        if (LlmRefusal.Is(response))
        {
          if (LlmRefusal.IsBilled(response)) Accumulate(result.Usage, response.Usage);
          result.Error = LlmRefusal.Sentence(response.RefusalCategory);
          return result;
        }
        Accumulate(result.Usage, response.Usage);

        // #216: TRUNCATION IS ITS OWN OUTCOME and never renders as a completed result. A call cut off at
        // max_tokens has no more to give, so a repair round-trip only spends again to be cut off again -
        // and the old path reported it as "did not match the required shape", sending the model to fix a
        // schema that was never the problem. Thinking is billed inside the budget, so this is the exact
        // shape the Stage-D measurement produced: the whole allowance spent, nothing returned.
        if (string.Equals(response.StopReason, "max_tokens", StringComparison.Ordinal) &&
            response.StructuredJson == null && ExtractToolInput(response, toolName) == null)
        {
          result.Error = "the reply was cut off at the token limit before any usable answer - the budget " +
                         "was too small for this request" + (thinking ? " (thinking is billed inside it)" : "") + ".";
          return result;
        }

        var candidate = response.StructuredJson ?? ExtractToolInput(response, toolName) ?? ExtractJsonObject(response.Text);
        if (candidate != null)
        {
          var (ok, error) = Safe(validate, candidate);
          if (ok) { result.Ok = true; result.Json = candidate; return result; }

          if (attempt == 0)
          {
            // Repair round-trip: echo the assistant turn, return a tool_result carrying the validation
            // error, and ask for a corrected call. Keeps the tool_use/tool_result contract intact.
            messages.Add(LlmMessage.Assistant(response.Blocks));
            messages.Add(RepairTurn(response, toolName, provider.StructuredArrivesAsText
              ? $"That did not match the required shape: {error}. Return the corrected JSON object as your whole reply."
              : $"That did not match the required shape: {error}. Call {toolName} again with corrected JSON."));
            continue;
          }
          result.Error = error;
        }
        else if (attempt == 0)
        {
          // WP-3.3: use RepairTurn here too - if the assistant turn ends in an unanswered tool_use, a plain
          // user message is an invalid request shape on Anthropic; RepairTurn answers it with a tool_result.
          messages.Add(LlmMessage.Assistant(response.Blocks));
          messages.Add(RepairTurn(response, toolName, provider.StructuredArrivesAsText
            ? "Return ONLY the JSON object, as your whole reply."
            : $"Return ONLY a call to the {toolName} tool with valid JSON."));
          continue;
        }
        else result.Error = "the model did not return a usable structured result.";
      }

      if (string.IsNullOrEmpty(result.Error))
        result.Error = "the model did not return a usable structured result.";
      return result;
    }

    /// <summary>Build the user turn that carries the repair note. If the assistant emitted a tool_use,
    /// the note rides as that tool's tool_result (required pairing); otherwise it is a plain message.</summary>
    private static LlmMessage RepairTurn(LlmResponse response, string toolName, string note)
    {
      var toolUses = response.ToolUses;
      if (toolUses.Count == 0) return LlmMessage.User(note);

      var msg = new LlmMessage { Role = LlmRole.User };
      foreach (var t in toolUses) msg.Blocks.Add(LlmBlock.ToolResult(t.ToolUseId, note));
      return msg;
    }

    private static string ExtractToolInput(LlmResponse response, string toolName)
    {
      foreach (var b in response.ToolUses)
        if (b.ToolName == toolName && !string.IsNullOrWhiteSpace(b.ToolInputJson))
          return b.ToolInputJson;
      return null;
    }

    /// <summary>Pull the single JSON object out of a model reply, tolerating prose or code fences.</summary>
    private static string ExtractJsonObject(string s)
    {
      if (string.IsNullOrWhiteSpace(s)) return null;
      s = s.Trim();
      if (s.StartsWith("```"))
      {
        var nl = s.IndexOf('\n');
        if (nl >= 0) s = s.Substring(nl + 1);
        if (s.EndsWith("```")) s = s.Substring(0, s.Length - 3);
        s = s.Trim();
      }
      var open = s.IndexOf('{');
      var close = s.LastIndexOf('}');
      if (open < 0 || close <= open) return null;
      return s.Substring(open, close - open + 1);
    }

    private static (bool, string) Safe(Func<string, (bool ok, string error)> validate, string json)
    {
      try
      {
        // A candidate must at least parse as a JSON object before the caller's validator runs.
        if (!(JsonNode.Parse(json) is JsonObject)) return (false, "not a JSON object");
        return validate(json);
      }
      catch (Exception ex) { return (false, "invalid JSON (" + ex.Message + ")"); }
    }

    private static void Accumulate(LlmUsage total, LlmUsage one)
    {
      if (one == null) return;
      total.InputTokens += one.InputTokens;
      total.OutputTokens += one.OutputTokens;
      total.CacheReadInputTokens += one.CacheReadInputTokens;
      total.CacheCreationInputTokens += one.CacheCreationInputTokens;
    }
  }
}
