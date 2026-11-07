#if DEBUG
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Dwight.Llm;

namespace Dwight.Agent
{
  /// <summary>One scripted reply. Either a tool call (optionally with prose beside it, which is the shape
  /// a real assistant turn usually has) or a final answer that ends the turn.</summary>
  internal sealed class ScriptedStep
  {
    /// <summary>Prose in this reply. Never blank on a step that has no tool call - an assistant message
    /// with an empty text block fails the loop's own pre-flight on the NEXT send (#297), which would make
    /// the drill fail for a defect it introduced rather than one it found.</summary>
    public string Text;

    /// <summary>The tool to call, or null for a final answer.</summary>
    public string ToolName;
    public string ToolArgsJson;
  }

  /// <summary>What the stub SAW on one call. The drill reports these, because a claim that the loop ran
  /// is worth more when it can say what the loop actually sent.</summary>
  internal sealed class ScriptedCall
  {
    public int MessagesReceived;
    public IReadOnlyList<string> ToolsAdvertised = new string[0];
    public int AnswerBudget;
    /// <summary>The tool_result envelopes that arrived in this call's last user message - i.e. what the
    /// PREVIOUS step's tools returned. This is how the drill learns whether a tool actually ran.</summary>
    public IReadOnlyList<string> ToolResultsIn = new string[0];
  }

  /// <summary>
  /// A SCRIPTED, TOKEN-FREE STAND-IN FOR A PROVIDER (#268, WP-4 Stage 0).
  ///
  /// It exists for exactly one reason: the full-path drill has to traverse <c>AgentLoop.Run</c> and the
  /// chat entry above it, and a real provider would make that traversal cost money, take a minute, and
  /// depend on what a model felt like calling. A stub makes the path deterministic and free, which is
  /// what lets the drill be run on every deploy rather than saved for special occasions.
  ///
  /// IT IS NOT A MOCK OF THE LOOP. The loop, the dispatcher, the tools, the marshalling, the session and
  /// the render are all REAL - #97's rule ("a harness testing a mock measures the mock") is why only the
  /// model is replaced. Everything the drill claims about is genuine code doing genuine work.
  ///
  /// DEBUG-ONLY, and nothing references it from a shipping path.
  /// </summary>
  internal sealed class ScriptedProvider : ILlmProvider
  {
    private readonly List<ScriptedStep> _script;
    private readonly List<ScriptedCall> _calls = new List<ScriptedCall>();
    private readonly object _lock = new object();
    private int _next;

    public ScriptedProvider(IEnumerable<ScriptedStep> script)
    {
      _script = (script ?? Enumerable.Empty<ScriptedStep>()).ToList();
    }

    public string ModelId => "scripted-drill-stub";

    /// <summary>Stated honestly rather than left at a default: the loop never reads it (only
    /// <c>StructuredOutput</c> does), but a field that lies about which dialect it speaks is the kind of
    /// thing a later reader believes.</summary>
    public ProviderCapability Capability => ProviderCapability.ToolCall;

    public int Steps => _script.Count;
    public int Calls { get { lock (_lock) return _calls.Count; } }
    public IReadOnlyList<ScriptedCall> Observed { get { lock (_lock) return _calls.ToList(); } }

    /// <summary>Every tool_result envelope the loop fed back across the whole run, paired with the tool
    /// that produced it. The pairing is by ORDER within a step, which is how the loop builds them.</summary>
    public IReadOnlyList<(string Tool, string Envelope)> ToolResults
    {
      get
      {
        lock (_lock)
        {
          var pairs = new List<(string, string)>();
          // Call n carries the results of call n-1's tool calls, so walk them in step order. Bounded by
          // the SCRIPT, not by the call count: if the loop outran the script there is no scripted step to
          // attribute those results to, and the drill's own "ran out of script" clause is what reports
          // that - an index-out-of-range here would take the whole report down with it.
          for (var i = 1; i < _calls.Count && i - 1 < _script.Count; i++)
          {
            var tool = _script[i - 1].ToolName;
            foreach (var envelope in _calls[i].ToolResultsIn) pairs.Add((tool ?? "(none)", envelope));
          }
          return pairs;
        }
      }
    }

    public LlmResponse Send(LlmRequest request, CancellationToken cancel = default)
    {
      // Honour cancellation exactly as a real adapter does, so a Stop during the drill behaves the way a
      // Stop during a real turn does (ADR-089). A stub that ignored the token would quietly make the
      // drill's threading claim weaker than the path it is standing in for.
      cancel.ThrowIfCancellationRequested();

      int index;
      lock (_lock)
      {
        index = _next++;
        _calls.Add(new ScriptedCall
        {
          MessagesReceived = request?.Messages?.Count ?? 0,
          ToolsAdvertised = (request?.Tools ?? new List<LlmTool>()).Select(t => t.Name).ToList(),
          AnswerBudget = request?.MaxTokens ?? 0,
          ToolResultsIn = LastToolResults(request),
        });
      }

      // The loop asked for more than the script holds. Do NOT invent a step - answer with a marked,
      // turn-ending reply so the drill's "the loop outran the script" clause can see it, rather than
      // looping forever or throwing something that reads as a provider fault.
      if (index >= _script.Count)
        return new LlmResponse
        {
          StopReason = "end_turn",
          Blocks = { LlmBlock.TextBlock("[the drill's script is exhausted]") },
        };

      var step = _script[index];
      var response = new LlmResponse
      {
        StopReason = step.ToolName == null ? "end_turn" : "tool_use",
        // A stub bills nothing, and saying so beats leaving a zero that reads as an unmeasured field.
        Usage = new LlmUsage(),
      };

      if (!string.IsNullOrWhiteSpace(step.Text)) response.Blocks.Add(LlmBlock.TextBlock(step.Text));
      if (step.ToolName != null)
        response.Blocks.Add(LlmBlock.ToolUse("drill-" + index, step.ToolName, step.ToolArgsJson ?? "{}"));

      return response;
    }

    private static IReadOnlyList<string> LastToolResults(LlmRequest request)
    {
      var last = request?.Messages?.LastOrDefault();
      if (last?.Blocks == null) return new string[0];
      return last.Blocks.Where(b => b?.Kind == "tool_result").Select(b => b.Text ?? "").ToList();
    }
  }
}
#endif
