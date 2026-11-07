using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dwight.Session
{
  /// <summary>
  /// What one model-backed run cost, as numbers that survive a close and reopen of Rhino. Held on a verb's
  /// record and on each chat turn, in the per-document store. Light on purpose: no breakdown by model, no
  /// export, no setting. Nothing here is a secret and nothing here is the user's content.
  ///
  /// <c>cents</c> is null for a local model (free) and for Ollama Cloud (the cost depends on a plan Dwight cannot see);
  /// <c>costText</c> keeps those words, so the number is never invented where the product has none. A stored row keeps
  /// the words it was written with.
  /// </summary>
  internal sealed class StoredSpend
  {
    [JsonPropertyName("model")] public string Model { get; set; }
    /// <summary>The model's provider, for the central ledger (Brief AK). In memory only: the session file never
    /// held it, and the ledger line is written at the moment the spend is, so it never needs reading back.</summary>
    [JsonIgnore] public string Provider { get; set; }
    /// <summary>Brief CY: the characters this spend sent, system plus user, for the central ledger, or 0 when they were not counted. In
    /// memory only, like <see cref="Provider"/>.</summary>
    [JsonIgnore] public long Chars { get; set; }
    /// <summary>Brief DL: "stopped" when this spend is an ESTIMATE, not a bill: a run was stopped after its request went out and the provider's usage never came back. The input is
    /// estimated from the request's characters at the ledger's measured ratio, the output is 0, and the cents are the input's alone. Null for a bill.</summary>
    [JsonPropertyName("estimated")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string Estimated { get; set; }
    [JsonPropertyName("elapsedMs")] public long ElapsedMs { get; set; }
    [JsonPropertyName("inputTokens")] public int InputTokens { get; set; }
    [JsonPropertyName("outputTokens")] public int OutputTokens { get; set; }
    [JsonPropertyName("cacheReadTokens")] public int CacheReadTokens { get; set; }
    [JsonPropertyName("cacheWriteTokens")] public int CacheWriteTokens { get; set; }
    /// <summary>Estimated cost in cents, or null when the run had no price Dwight can state (local, Ollama Cloud).</summary>
    [JsonPropertyName("cents")] public double? Cents { get; set; }
    /// <summary>The words for a run with no number: "local · free", "Ollama Cloud · cost depends on your plan".</summary>
    [JsonPropertyName("costText")] public string CostText { get; set; }
    /// <summary>Chat turns only: model round trips and tool calls in the turn. 0 on a verb run.</summary>
    [JsonPropertyName("steps")] public int Steps { get; set; }
    [JsonPropertyName("toolCalls")] public int ToolCalls { get; set; }
    /// <summary>Web searches the turn ran (Brief V). Zero, and then absent from the file, unless web search was on.</summary>
    [JsonPropertyName("searches")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int Searches { get; set; }
    /// <summary>A newer minor's fields survive an older build's read-write cycle (same rule as the store).</summary>
    [JsonExtensionData] public Dictionary<string, JsonElement> Extra { get; set; }

    /// <summary>All input the model read, cached or not.</summary>
    [JsonIgnore] public int TotalInputTokens => InputTokens + CacheReadTokens + CacheWriteTokens;
  }

  /// <summary>The sum of a chat's turns. Computed when read, never stored twice.</summary>
  internal readonly struct SpendTotal
  {
    public readonly int Turns;
    public readonly double Cents;
    public readonly bool AnyPriced;
    public readonly string UnpricedText;
    public readonly long InputTokens;
    public readonly long OutputTokens;

    public SpendTotal(int turns, double cents, bool anyPriced, string unpricedText, long input, long output)
    { Turns = turns; Cents = cents; AnyPriced = anyPriced; UnpricedText = unpricedText; InputTokens = input; OutputTokens = output; }
  }

  /// <summary>The GH-free arithmetic and wording for <see cref="StoredSpend"/> (ADR-091).</summary>
  internal static class SpendMath
  {
    /// <summary>The catalogue's provider value for Ollama Cloud. The ledger's lines carry it, and the words below go by it.</summary>
    public const string OllamaCloudProvider = "OllamaCloud";

    /// <summary>Dwight cannot tell which plan a key is on, and on pay-as-you-go a turn is not covered by anything, so these say what is
    /// true on every plan. The short words fit a table cell; the row names the provider; the sentence is for a hover. The fit gate holds the
    /// dropdown's words, which sit after the longest model name where a priced row says "~3.0¢".</summary>
    public const string OllamaCloudCostShort = "cost depends on your plan";
    public const string OllamaCloudCostPick = "by your plan";
    public const string OllamaCloudCostText = "Ollama Cloud · " + OllamaCloudCostShort;
    public const string OllamaCloudCostSentence = "Ollama Cloud: Dwight cannot see what this cost, because it depends on your plan.";

    /// <summary>A receipt as its hover shows it: where the line holds Ollama Cloud's short words, the whole sentence goes last instead, because a
    /// hover has the room. Every other line, a stored row's older words among them, comes back as it was.</summary>
    public static string CostHover(string line)
    {
      if (string.IsNullOrEmpty(line) || line.IndexOf(OllamaCloudCostText, StringComparison.Ordinal) < 0) return line ?? "";
      var rest = line.Replace(" · " + OllamaCloudCostText, "").Replace(OllamaCloudCostText + " · ", "").Replace(OllamaCloudCostText, "");
      return rest.Length == 0 ? OllamaCloudCostSentence : rest + " · " + OllamaCloudCostSentence;
    }

    public static SpendTotal Sum(IEnumerable<StoredSpend> turns)
    {
      var n = 0; double cents = 0; var priced = false; string text = null; long inp = 0, outp = 0;
      foreach (var t in turns ?? Enumerable.Empty<StoredSpend>())
      {
        if (t == null) continue;
        n++;
        inp += t.TotalInputTokens; outp += t.OutputTokens;
        if (t.Cents.HasValue) { cents += t.Cents.Value; priced = true; }
        else if (!string.IsNullOrWhiteSpace(t.CostText)) text = t.CostText;
      }
      return new SpendTotal(n, cents, priced, text, inp, outp);
    }

    /// <summary>
    /// Brief EG's addendum: a bill carried on an exception (a Stop after the provider's count arrived, a refusal, a repair turn that threw) joined to the run's spend
    /// with the characters of the request that was in flight, as a completed call's line has them, so a counted stopped run teaches the ratio as a finished one does.
    /// With no request marked in flight (a run's later call that marks none) it has none, and nor has the run's line: a ratio is never measured from half a run.
    /// </summary>
    public static StoredSpend Carried(StoredSpend run, StoredSpend paid, long inFlightChars)
    {
      if (paid == null) return run;
      paid.Chars = Math.Max(0, inFlightChars);
      return Combine(run, paid);
    }

    /// <summary>Two spends of one run added together (Simplify's attempts). Either may be null.</summary>
    public static StoredSpend Combine(StoredSpend a, StoredSpend b)
    {
      if (a == null) return b;
      if (b == null) return a;
      return new StoredSpend
      {
        Model = b.Model ?? a.Model,
        Provider = b.Provider ?? a.Provider,
        ElapsedMs = a.ElapsedMs + b.ElapsedMs,
        InputTokens = a.InputTokens + b.InputTokens,
        OutputTokens = a.OutputTokens + b.OutputTokens,
        CacheReadTokens = a.CacheReadTokens + b.CacheReadTokens,
        CacheWriteTokens = a.CacheWriteTokens + b.CacheWriteTokens,
        Cents = a.Cents.HasValue || b.Cents.HasValue ? (a.Cents ?? 0) + (b.Cents ?? 0) : (double?)null,
        CostText = a.Cents.HasValue || b.Cents.HasValue
          ? SpendMath.CentsText((a.Cents ?? 0) + (b.Cents ?? 0)) : (b.CostText ?? a.CostText),
        Steps = a.Steps + b.Steps,
        ToolCalls = a.ToolCalls + b.ToolCalls,
        Searches = a.Searches + b.Searches,
        // Tokens add across attempts, so characters add too, or the line has none and its ratio is not measured from half a run.
        Chars = a.Chars > 0 && b.Chars > 0 ? a.Chars + b.Chars : 0,
        Estimated = a.Estimated ?? b.Estimated,   // a total with an estimated part is an estimate, said so
      };
    }

    /// <summary>"~12.3¢", or "&lt;0.1¢" for a very small sum; the same wording the footer has always used.</summary>
    public static string CentsText(double cents) =>
      cents < 0.1 ? "<0.1¢" : "~" + cents.ToString("0.0", CultureInfo.InvariantCulture) + "¢";

    /// <summary>41300 -> "41k", 1234 -> "1.2k", 523 -> "523".</summary>
    public static string TokensText(long n)
    {
      if (n < 1000) return n.ToString(CultureInfo.InvariantCulture);
      var k = n / 1000.0;
      return (k >= 9.95 ? Math.Round(k).ToString("0", CultureInfo.InvariantCulture)
                      : k.ToString("0.#", CultureInfo.InvariantCulture)) + "k";
    }

    /// <summary>"3 turns · ~12.3¢ · 41k in / 6k out". Empty when there is nothing to say.</summary>
    public static string ChatLine(SpendTotal t)
    {
      if (t.Turns == 0) return "";
      var parts = new List<string> { t.Turns + (t.Turns == 1 ? " turn" : " turns") };
      if (t.AnyPriced) parts.Add(CentsText(t.Cents));
      else if (!string.IsNullOrWhiteSpace(t.UnpricedText)) parts.Add(t.UnpricedText);
      if (t.InputTokens > 0 || t.OutputTokens > 0)
        parts.Add(TokensText(t.InputTokens) + " in / " + TokensText(t.OutputTokens) + " out");
      return string.Join(" · ", parts);
    }

    /// <summary>"118k in / 1.1k out" for one turn. Empty when it read nothing and wrote nothing.</summary>
    public static string TokensLine(StoredSpend s) =>
      s == null || (s.TotalInputTokens == 0 && s.OutputTokens == 0)
        ? "" : TokensText(s.TotalInputTokens) + " in / " + TokensText(s.OutputTokens) + " out";

    /// <summary>
    /// One turn's receipt, read back from what the store keeps: "Sonnet 4.6 · 7.8s · ~7.0¢ · cached · 3 steps · 2 tool calls · 118k in / 1.1k out". It is what the
    /// cost shows when hovered on a reopened chat (Brief BI); a turn just run shows the meta line it finished with, which can carry more.
    /// </summary>
    public static string TurnReceipt(StoredSpend s, string modelName)
    {
      if (s == null) return "";
      var parts = new List<string>();
      if (!string.IsNullOrWhiteSpace(modelName)) parts.Add(modelName);
      parts.Add((s.ElapsedMs / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "s");
      if (s.Cents.HasValue) parts.Add(CentsText(s.Cents.Value));
      else if (!string.IsNullOrWhiteSpace(s.CostText)) parts.Add(s.CostText);
      if (s.Estimated != null) parts.Add("estimated");   // Brief DL: marked everywhere it appears
      if (s.CacheReadTokens > 0) parts.Add("cached");
      if (s.Steps > 0) parts.Add(s.Steps + (s.Steps == 1 ? " step" : " steps"));
      if (s.ToolCalls > 0) parts.Add(s.ToolCalls + (s.ToolCalls == 1 ? " tool call" : " tool calls"));
      var tokens = TokensLine(s);
      if (tokens.Length > 0) parts.Add(tokens);
      return string.Join(" · ", parts);
    }
  }
}
