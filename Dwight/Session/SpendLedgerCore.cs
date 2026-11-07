using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Dwight.Session
{
  /// <summary>One line of the central ledger (Brief AK): what one verb run or one chat turn cost, with the
  /// file and the model it ran against. Numbers and names only - never a prompt, a reply, a card, a
  /// component name or a path. <see cref="DocumentName"/> is the file name alone.</summary>
  internal sealed class LedgerLine
  {
    public const string KindVerb = "verb";
    public const string KindTurn = "turn";

    public string Utc;
    public string DocumentId;
    public string DocumentName;
    public string Kind;
    public string Verb;
    public string ThreadId;      // turns only
    public int Turn;             // turns only, the ordinal (1-based)
    public string Provider;
    public string Model;
    public long InputTokens;
    public long CacheReadTokens;
    public long CacheWriteTokens;
    public long OutputTokens;
    public double? Cents;        // null when the model is unpriced (local, Ollama Cloud)
    public double Seconds;
    public int Steps;            // turns only
    public int ToolCalls;        // turns only
    public int Searches;         // written only when the turn searched
    public long Chars;           // verb runs only (Brief CY): the characters sent, system plus user. An older line has none
    /// <summary>Brief DL: "stopped" on a line that is an ESTIMATE (a run stopped before any usage came back). Null on a billed line, which is every older one.</summary>
    public string Estimated;
    /// <summary>Brief DL: the run's key, written only on an estimated line and on the billed line that REPLACES it, so a reader can tell which estimate was superseded. Null otherwise.</summary>
    public string RunId;
    /// <summary>Brief EO: what kind of call within the verb this line is, written only when there is one: "learn" on a typed Learn answer's judge, one line per judged
    /// answer. Totals by verb count it under its verb; the sample a "Continue?" estimate is read from never reads it (<see cref="LedgerSample.Add"/>). Null otherwise.</summary>
    public string Label;

    /// <summary>All input the model read, cached or not (the same sum as <see cref="StoredSpend.TotalInputTokens"/>).</summary>
    public long TotalInputTokens => InputTokens + CacheReadTokens + CacheWriteTokens;
  }

  /// <summary>
  /// Brief CY: WHAT THE LEDGER'S RECENT LINES SAY ABOUT A READ NOT YET SENT, read once for one question. Each answer is taken over the
  /// newest <see cref="Window"/> lines of its key, and null under <see cref="AtLeast"/> of them, when the caller uses its stated default.
  /// Holds numbers and never a whole line. Verb runs only: a chat turn does not ask and its lines carry no characters.
  ///
  /// <para>Brief EG: CHARACTERS PER TOKEN ARE KEYED BY PROVIDER AND MODEL. One median per provider converted a stopped Sonnet 5.5 Explain at
  /// Haiku 5.5's 1.72, because the window was full of Haiku lines, and the estimate stood 27 percent above its bill (the eighteenth gate,
  /// row 22). Under <see cref="AtLeast"/> lines for the model the answer is the provider's, and under that the caller's stated default.</para>
  /// </summary>
  internal sealed class LedgerSample
  {
    public const int Window = 20;
    public const int AtLeast = 3;

    /// <summary>How long after a read the sentence may mention the cache. The cache is the provider's, so the sentence only says "if".</summary>
    public static readonly TimeSpan CacheLife = TimeSpan.FromMinutes(5);

    /// <summary>A sample with nothing in it, so every answer is the stated default.</summary>
    public static LedgerSample Empty => new LedgerSample();

    private readonly Dictionary<string, Queue<double>> _charsPerTokenByModel = new Dictionary<string, Queue<double>>(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<double>> _charsPerTokenByProvider = new Dictionary<string, Queue<double>>(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<double>> _output = new Dictionary<string, Queue<double>>(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _reads = new Dictionary<string, DateTime>(StringComparer.Ordinal);

    public void Add(LedgerLine l)
    {
      if (l == null || l.Kind != LedgerLine.KindVerb) return;
      if (l.Estimated != null) return;   // Brief DL: an estimate says nothing about the ratio it was made from, so it never feeds it back
      if (!string.IsNullOrEmpty(l.Label)) return;   // Brief EO: a labelled call (a judged Learn answer) is not the verb's read, and its tiny size would pull the verb's estimate down
      // A call that answered nothing (it failed, or was cut off before a word) says nothing about how long an answer is.
      if (l.OutputTokens > 0) Push(_output, Key(l.Verb, l.Model), l.OutputTokens);
      if (l.Chars <= 0 || l.TotalInputTokens <= 0) return;
      var ratio = (double)l.Chars / l.TotalInputTokens;
      Push(_charsPerTokenByModel, Key(l.Provider, l.Model), ratio);
      Push(_charsPerTokenByProvider, l.Provider ?? "", ratio);
      if (DateTime.TryParse(l.Utc, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at))
        _reads[Key(l.DocumentId, l.Verb, l.Model, l.Chars.ToString(CultureInfo.InvariantCulture))] = at;
    }

    /// <summary>The median characters per token this model's recent lines measured, else its provider's, or null when too few carry them. The likely count,
    /// for the question before a read ("Continue?"), which estimates the bill and is no floor.</summary>
    public double? CharsPerToken(string provider, string model) =>
      Median(_charsPerTokenByModel, Key(provider, model)) ?? Median(_charsPerTokenByProvider, provider ?? "");

    /// <summary>
    /// Brief EG: THE MOST characters per token this model's recent lines measured, else its provider's, or null when too few carry them: the fewest tokens it has counted
    /// for text of a size. A stopped request's estimate converts at this, so it sits at or below every comparable run measured here. A median cannot be a floor, since half
    /// the lines it is taken from counted fewer tokens than it says.
    /// </summary>
    public double? FloorCharsPerToken(string provider, string model) =>
      Most(_charsPerTokenByModel, Key(provider, model)) ?? Most(_charsPerTokenByProvider, provider ?? "");

    /// <summary>The median answer this verb gave on this model lately, in tokens, or null when too few lines exist.</summary>
    public long? OutputTokens(string verb, string model)
    {
      var median = Median(_output, Key(verb, model));
      return median.HasValue ? (long)Math.Round(median.Value) : (long?)null;
    }

    /// <summary>Was this very read sent inside the cache's life? The same file, verb and model, and the same characters, which is how a
    /// line can tell the same selection: another selection or another verb's prompt would not hit the cache whatever the clock says.</summary>
    public bool ReadWithinCacheLife(string documentId, string verb, string model, long chars, DateTime nowUtc)
    {
      if (string.IsNullOrEmpty(documentId) || chars <= 0) return false;
      if (!_reads.TryGetValue(Key(documentId, verb, model, chars.ToString(CultureInfo.InvariantCulture)), out var at)) return false;
      var age = nowUtc - at;
      return age >= TimeSpan.Zero && age <= CacheLife;
    }

    private static string Key(params string[] parts) => string.Join("\n", parts.Select(p => p ?? ""));

    private static void Push(Dictionary<string, Queue<double>> groups, string key, double value)
    {
      if (!groups.TryGetValue(key, out var q)) groups[key] = q = new Queue<double>();
      q.Enqueue(value);
      if (q.Count > Window) q.Dequeue();
    }

    private static double? Median(Dictionary<string, Queue<double>> groups, string key)
    {
      if (!groups.TryGetValue(key, out var q) || q.Count < AtLeast) return null;
      var sorted = q.OrderBy(v => v).ToList();
      var mid = sorted.Count / 2;
      return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    private static double? Most(Dictionary<string, Queue<double>> groups, string key) =>
      groups.TryGetValue(key, out var q) && q.Count >= AtLeast ? q.Max() : (double?)null;
  }

  /// <summary>Whether a group's rows carry a price: every row, some rows, or none. An unpriced model's rows
  /// carry tokens and no cents, so a group of them must never print a false zero.</summary>
  internal enum Pricing { Empty, Priced, Partly, NotPriced }

  /// <summary>The sum of a set of ledger lines.</summary>
  internal sealed class LedgerTotal
  {
    public string Label = "";
    /// <summary>Brief FN: a model group's id and provider as the lines wrote them, so the view can name it from the catalogue. Null on every other group.</summary>
    public string Model, Provider;
    public int Runs;
    public int Turns;
    public long InputTokens;
    public long CacheReadTokens;
    public long CacheWriteTokens;
    public long OutputTokens;
    public double Cents;
    public int PricedRows;
    public int UnpricedRows;
    /// <summary>The unpriced rows that ran on Ollama Cloud, whose cost depends on a plan Dwight cannot see.</summary>
    public int OllamaCloudUnpricedRows;
    /// <summary>Brief DL: the rows that are estimates (a run stopped before any usage came back), and their cents, which are kept APART from the billed cents.</summary>
    public int EstimatedRows;
    public double EstimatedCents;

    public long TotalInputTokens => InputTokens + CacheReadTokens + CacheWriteTokens;
    public int Rows => Runs + Turns;

    /// <summary>The rows that were billed: an estimate is not one.</summary>
    public int BilledRows => Rows - EstimatedRows;

    public Pricing Pricing =>
      BilledRows == 0 ? Pricing.Empty : UnpricedRows == 0 ? Pricing.Priced : PricedRows == 0 ? Pricing.NotPriced : Pricing.Partly;

    public void Add(LedgerLine l)
    {
      if (l.Kind == LedgerLine.KindTurn) Turns++; else Runs++;
      InputTokens += l.InputTokens; CacheReadTokens += l.CacheReadTokens; CacheWriteTokens += l.CacheWriteTokens;
      OutputTokens += l.OutputTokens;
      if (l.Estimated != null) { EstimatedRows++; if (l.Cents.HasValue) EstimatedCents += l.Cents.Value; return; }   // Brief DL: apart from what was billed
      if (l.Cents.HasValue) { Cents += l.Cents.Value; PricedRows++; }
      else { UnpricedRows++; if (l.Provider == SpendMath.OllamaCloudProvider) OllamaCloudUnpricedRows++; }
    }
  }

  /// <summary>The four periods the Usage view states, in the order it states them.</summary>
  internal enum LedgerPeriod { Today = 0, Last7Days = 1, ThisMonth = 2, AllTime = 3 }

  /// <summary>The ledger read once: a total per period, and per period the totals by model, by verb and by
  /// document. Holds groups, never lines, so a long ledger costs only as much memory as it has groups.</summary>
  internal sealed class LedgerSummary
  {
    public static readonly LedgerPeriod[] Periods = { LedgerPeriod.Today, LedgerPeriod.Last7Days, LedgerPeriod.ThisMonth, LedgerPeriod.AllTime };

    public readonly LedgerTotal[] Totals = Periods.Select(p => new LedgerTotal { Label = SpendLedgerCore.PeriodName(p) }).ToArray();
    public readonly Dictionary<string, LedgerTotal>[] ByModel = Periods.Select(_ => new Dictionary<string, LedgerTotal>()).ToArray();
    public readonly Dictionary<string, LedgerTotal>[] ByVerb = Periods.Select(_ => new Dictionary<string, LedgerTotal>()).ToArray();
    public readonly Dictionary<string, LedgerTotal>[] ByDocument = Periods.Select(_ => new Dictionary<string, LedgerTotal>()).ToArray();
    /// <summary>Lines that could not be parsed. Skipped, counted, and said.</summary>
    public int Skipped;

    public LedgerTotal Total(LedgerPeriod p) => Totals[(int)p];

    /// <summary>A grouping for one period, the costliest first, then the most tokens, then by name.</summary>
    public static IReadOnlyList<LedgerTotal> Ordered(Dictionary<string, LedgerTotal> groups) =>
      groups.Values.OrderByDescending(g => g.Cents).ThenByDescending(g => g.TotalInputTokens + g.OutputTokens)
        .ThenBy(g => g.Label, StringComparer.OrdinalIgnoreCase).ToList();
  }

  /// <summary>
  /// The central cost ledger's decisions (Brief AK; ADR-091 GH-free): the one-line format, the append, the
  /// streaming reader and its totals, the CSV export, and every sentence the Usage view prints. The file is
  /// <c>%APPDATA%\Dwight\spend.jsonl</c>; the shim (<see cref="SpendLedger"/>) only names the path and the
  /// write sites.
  ///
  /// THE LEDGER IS THE USER'S RECORD (WP-1's preservation policy): append-only, never rewritten, never
  /// truncated or rotated by anything in Dwight. A line that cannot be read is skipped and counted, never
  /// repaired, so reading can never cost a byte of it.
  /// </summary>
  internal static class SpendLedgerCore
  {
    public const string FileName = "spend.jsonl";

    /// <summary>The privacy page's paragraph about the ledger, word for word. A test holds docs/public/privacy.md to it.</summary>
    public const string PrivacyParagraph =
      "Dwight also keeps one record of what it has spent, across every file and every chat, in `%APPDATA%\\Dwight\\spend.jsonl`, " +
      "so Settings can show you how much you have used. Each line is numbers and names only: the time, the file's id and its name " +
      "(never its folder), the verb, the provider and model, the tokens, the size of the request in characters, the cost and how long it took. It never holds a prompt, " +
      "a reply, a card, a component name or a path. Dwight only ever adds to it; it never trims or deletes it, and it sends it nowhere.";

    /// <summary>The CSV export's header: one column per ledger field, in the ledger's own order.</summary>
    public static readonly string[] Columns =
    {
      "utc", "documentId", "documentName", "kind", "verb", "threadId", "turn", "provider", "model",
      "inputTokens", "cacheReadTokens", "cacheWriteTokens", "outputTokens", "cents", "seconds", "steps", "toolCalls", "searches", "chars", "estimated",
    };

    private static readonly object Gate = new object();

    // ---------------------------------------------------------------- building a line

    /// <summary>The line for a verb run's spend. Null when there is no spend to record.</summary>
    public static LedgerLine ForVerb(StoredSpend s, DateTime utc, string documentId, string documentName, string verb, string provider, string runId = null, string label = null)
    {
      if (s == null) return null;
      var line = Line(s, utc, documentId, documentName, LedgerLine.KindVerb, verb, provider, null, 0);
      line.RunId = string.IsNullOrEmpty(runId) ? null : runId;
      line.Label = string.IsNullOrEmpty(label) ? null : label;
      return line;
    }

    /// <summary>The line for a chat turn's spend; <paramref name="turn"/> is its 1-based ordinal on the thread.</summary>
    public static LedgerLine ForTurn(StoredSpend s, DateTime utc, string documentId, string documentName, string verb, string provider,
      string threadId, int turn) =>
      s == null ? null : Line(s, utc, documentId, documentName, LedgerLine.KindTurn, verb, provider, threadId, turn);

    private static LedgerLine Line(StoredSpend s, DateTime utc, string documentId, string documentName, string kind, string verb,
      string provider, string threadId, int turn) => new LedgerLine
    {
      Utc = utc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
      DocumentId = documentId ?? "",
      DocumentName = NameOnly(documentName),
      Kind = kind,
      Verb = verb ?? "",
      ThreadId = kind == LedgerLine.KindTurn ? threadId ?? "" : null,
      Turn = kind == LedgerLine.KindTurn ? turn : 0,
      Provider = provider ?? "",
      Model = s.Model ?? "",
      InputTokens = s.InputTokens,
      CacheReadTokens = s.CacheReadTokens,
      CacheWriteTokens = s.CacheWriteTokens,
      OutputTokens = s.OutputTokens,
      Cents = s.Cents,
      Seconds = Math.Round(s.ElapsedMs / 1000.0, 1),
      Steps = kind == LedgerLine.KindTurn ? s.Steps : 0,
      ToolCalls = kind == LedgerLine.KindTurn ? s.ToolCalls : 0,
      Searches = s.Searches,
      Chars = kind == LedgerLine.KindVerb ? s.Chars : 0,
      Estimated = s.Estimated,
    };

    // ---------------------------------------------------------------- whose job a verb run's line is (Brief CT)

    /// <summary>Did the model read or write anything? A call that failed before any reply, or a refusal the provider
    /// does not bill, leaves a spend of zeros, which is no cost to record.</summary>
    public static bool HasUsage(StoredSpend s) =>
      s != null && (s.TotalInputTokens > 0 || s.OutputTokens > 0 || s.Searches > 0);

    /// <summary>A run's paid calls are over: is its line written now? Yes once, when it has usage, and the flag then
    /// tells any row that follows. With no usage the flag stays off, so a row writes what it always did.</summary>
    public static bool TakeRunLine(StoredSpend s, ref bool ledgered)
    {
      if (ledgered || !HasUsage(s)) return false;
      ledgered = true;
      return true;
    }

    /// <summary>A history row is being recorded: does it write its run's line? Yes, unless the line is already written.</summary>
    public static bool RecordWritesLine(StoredSpend s, bool spendLedgered) => s != null && !spendLedgered;

    /// <summary>The spend a run keeps from a call it carries on after (Reorganise's labelling): what the call used, if it
    /// worked or was billed anyway. A failed call with no usage keeps none, so its row stays empty and writes no line.</summary>
    public static StoredSpend KeptSpend(bool callOk, StoredSpend used) => callOk || HasUsage(used) ? used : null;

    /// <summary>The file name alone. A path never enters the ledger, whatever the caller hands in.</summary>
    public static string NameOnly(string name)
    {
      if (string.IsNullOrEmpty(name)) return "";
      var cut = name.LastIndexOfAny(new[] { '\\', '/' });
      return cut >= 0 ? name.Substring(cut + 1) : name;
    }

    // ---------------------------------------------------------------- the line format

    /// <summary>One JSON object, no newline. The fields a verb line does not have are absent, not empty;
    /// <c>cents</c> is always present and null when the model is unpriced.</summary>
    public static string Serialize(LedgerLine l)
    {
      using (var ms = new MemoryStream())
      {
        using (var w = new Utf8JsonWriter(ms))
        {
          w.WriteStartObject();
          w.WriteString("utc", l.Utc ?? "");
          w.WriteString("documentId", l.DocumentId ?? "");
          w.WriteString("documentName", l.DocumentName ?? "");
          w.WriteString("kind", l.Kind ?? LedgerLine.KindVerb);
          w.WriteString("verb", l.Verb ?? "");
          var turn = l.Kind == LedgerLine.KindTurn;
          if (turn) { w.WriteString("threadId", l.ThreadId ?? ""); w.WriteNumber("turn", l.Turn); }
          w.WriteString("provider", l.Provider ?? "");
          w.WriteString("model", l.Model ?? "");
          w.WriteNumber("inputTokens", l.InputTokens);
          w.WriteNumber("cacheReadTokens", l.CacheReadTokens);
          w.WriteNumber("cacheWriteTokens", l.CacheWriteTokens);
          w.WriteNumber("outputTokens", l.OutputTokens);
          if (l.Cents.HasValue) w.WriteNumber("cents", Math.Round(l.Cents.Value, 6)); else w.WriteNull("cents");
          w.WriteNumber("seconds", l.Seconds);
          if (turn) { w.WriteNumber("steps", l.Steps); w.WriteNumber("toolCalls", l.ToolCalls); }
          if (l.Searches > 0) w.WriteNumber("searches", l.Searches);
          if (l.Chars > 0) w.WriteNumber("chars", l.Chars);
          if (!string.IsNullOrEmpty(l.Estimated)) w.WriteString("estimated", l.Estimated);
          if (!string.IsNullOrEmpty(l.RunId)) w.WriteString("runId", l.RunId);
          if (!string.IsNullOrEmpty(l.Label)) w.WriteString("label", l.Label);
          w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
      }
    }

    /// <summary>A line read back, or null when it cannot be (not JSON, not an object, no usable kind).</summary>
    public static LedgerLine Parse(string text)
    {
      if (string.IsNullOrWhiteSpace(text)) return null;
      try
      {
        using (var doc = JsonDocument.Parse(text))
        {
          var o = doc.RootElement;
          if (o.ValueKind != JsonValueKind.Object) return null;
          var kind = Str(o, "kind");
          if (kind != LedgerLine.KindVerb && kind != LedgerLine.KindTurn) return null;
          return new LedgerLine
          {
            Utc = Str(o, "utc"),
            DocumentId = Str(o, "documentId"),
            DocumentName = Str(o, "documentName"),
            Kind = kind,
            Verb = DwightVerbs.Canonical(Str(o, "verb")),   // Brief AO: a line that says Scaffold counts under Build
            ThreadId = o.TryGetProperty("threadId", out var th) && th.ValueKind == JsonValueKind.String ? th.GetString() : null,
            Turn = (int)Num(o, "turn"),
            Provider = Str(o, "provider"),
            Model = Str(o, "model"),
            InputTokens = Num(o, "inputTokens"),
            CacheReadTokens = Num(o, "cacheReadTokens"),
            CacheWriteTokens = Num(o, "cacheWriteTokens"),
            OutputTokens = Num(o, "outputTokens"),
            Cents = o.TryGetProperty("cents", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDouble() : (double?)null,
            Seconds = o.TryGetProperty("seconds", out var sec) && sec.ValueKind == JsonValueKind.Number ? sec.GetDouble() : 0,
            Steps = (int)Num(o, "steps"),
            ToolCalls = (int)Num(o, "toolCalls"),
            Searches = (int)Num(o, "searches"),
            Chars = Num(o, "chars"),
            Estimated = o.TryGetProperty("estimated", out var es) && es.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(es.GetString()) ? es.GetString() : null,
            RunId = o.TryGetProperty("runId", out var ri) && ri.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(ri.GetString()) ? ri.GetString() : null,
            Label = o.TryGetProperty("label", out var lb) && lb.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(lb.GetString()) ? lb.GetString() : null,
          };
        }
      }
      catch { return null; }
    }

    private static string Str(JsonElement o, string name) =>
      o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static long Num(JsonElement o, string name) =>
      o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    // ---------------------------------------------------------------- the append

    /// <summary>Append one line, creating the folder if it is missing. One <see cref="File.AppendAllText(string, string)"/>
    /// of one line under a process-wide lock, so two writers in this process never interleave. Never rewrites
    /// the file. Throws on failure; the caller decides what a failure costs (it never costs the session row).</summary>
    public static void Append(string path, LedgerLine line)
    {
      if (string.IsNullOrEmpty(path) || line == null) return;
      var text = Serialize(line) + "\n";
      lock (Gate)
      {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.AppendAllText(path, text, new UTF8Encoding(false));
      }
    }

    // ---------------------------------------------------------------- the reader

    /// <summary>The ledger's lines, streamed: one line in memory at a time. A missing file has no lines.</summary>
    public static IEnumerable<string> ReadLines(string path)
    {
      if (string.IsNullOrEmpty(path) || !File.Exists(path)) yield break;
      // FileShare.ReadWrite: a turn may append while the view reads, and the reader must neither block it nor fail.
      using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
      using (var r = new StreamReader(fs, Encoding.UTF8))
      {
        string s;
        while ((s = r.ReadLine()) != null) yield return s;
      }
    }

    /// <summary>How much of the ledger's end a question reads (Brief CY): about 2,700 lines, far more than twenty per key, and bounded
    /// because the question is asked on Rhino's thread and the ledger is never trimmed.</summary>
    public const int SampleBytes = 1024 * 1024;

    /// <summary>The newest lines only: at most <paramref name="maxBytes"/> from the end, without the part of a line the cut lands in.</summary>
    public static IEnumerable<string> ReadTail(string path, int maxBytes)
    {
      if (string.IsNullOrEmpty(path) || !File.Exists(path)) yield break;
      using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
      {
        var cut = fs.Length > maxBytes;
        if (cut) fs.Seek(-maxBytes, SeekOrigin.End);
        using (var r = new StreamReader(fs, Encoding.UTF8))
        {
          if (cut) r.ReadLine();
          string s;
          while ((s = r.ReadLine()) != null) yield return s;
        }
      }
    }

    /// <summary>What these lines say about a read not yet sent. A line that cannot be read is passed over.</summary>
    public static LedgerSample Sample(IEnumerable<string> lines)
    {
      var sample = new LedgerSample();
      foreach (var text in lines ?? Enumerable.Empty<string>()) sample.Add(Parse(text));
      return sample;
    }

    /// <summary>The totals for the four periods, each in local time against <paramref name="nowLocal"/> in
    /// <paramref name="zone"/>: today, the last seven days (today and the six before it), this calendar month,
    /// and all time. A blank line is ignored; a line that cannot be read is skipped and counted.</summary>
    public static LedgerSummary Summarise(IEnumerable<string> lines, DateTime nowLocal, TimeZoneInfo zone = null)
    {
      zone = zone ?? TimeZoneInfo.Local;
      var today = nowLocal.Date;
      var weekStart = today.AddDays(-6);
      var sum = new LedgerSummary();
      var superseded = SupersededRuns(lines);   // Brief DL: read first, so an estimate is not counted beside the bill that replaced it
      foreach (var text in lines ?? Enumerable.Empty<string>())
      {
        if (string.IsNullOrWhiteSpace(text)) continue;
        var l = Parse(text);
        if (l == null || !TryLocal(l.Utc, zone, out var local)) { sum.Skipped++; continue; }
        if (l.Estimated != null && l.RunId != null && superseded.Contains(l.RunId)) continue;
        var day = local.Date;
        foreach (var p in LedgerSummary.Periods)
        {
          var inPeriod =
            p == LedgerPeriod.AllTime ||
            (p == LedgerPeriod.ThisMonth && day.Year == today.Year && day.Month == today.Month && day <= today) ||
            (p == LedgerPeriod.Last7Days && day >= weekStart && day <= today) ||
            (p == LedgerPeriod.Today && day == today);
          if (!inPeriod) continue;
          var i = (int)p;
          sum.Totals[i].Add(l);
          var byModel = Group(sum.ByModel[i], ModelKey(l), ModelLabel(l));
          byModel.Model = l.Model ?? ""; byModel.Provider = l.Provider ?? "";
          byModel.Add(l);
          Group(sum.ByVerb[i], l.Verb ?? "", string.IsNullOrEmpty(l.Verb) ? "Unnamed" : l.Verb).Add(l);
          // Keyed by the file's id, so a renamed file is one row; the label is the latest name it was seen under.
          var docKey = string.IsNullOrEmpty(l.DocumentId) ? "name:" + l.DocumentName : l.DocumentId;
          Group(sum.ByDocument[i], docKey, DocumentLabel(l.DocumentName)).Add(l);
        }
      }
      return sum;
    }

    /// <summary>
    /// Brief DL: THE KEYS OF THE RUNS WHOSE ESTIMATE WAS REPLACED BY A BILL. The ledger is append-only, so an estimate is never rewritten: when the usage does arrive after all, a billed
    /// line carrying the same run key is appended, and a reader leaves the estimate out. Needs the lines twice, which the file's own reader gives (it starts afresh on each pass).
    /// </summary>
    public static HashSet<string> SupersededRuns(IEnumerable<string> lines)
    {
      var keys = new HashSet<string>(StringComparer.Ordinal);
      foreach (var text in lines ?? Enumerable.Empty<string>())
      {
        if (string.IsNullOrWhiteSpace(text) || text.IndexOf("\"runId\"", StringComparison.Ordinal) < 0) continue;
        var l = Parse(text);
        if (l != null && l.Estimated == null && l.RunId != null) keys.Add(l.RunId);
      }
      return keys;
    }

    private static bool TryLocal(string utc, TimeZoneInfo zone, out DateTime local)
    {
      local = default;
      if (!DateTime.TryParse(utc, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var u))
        return false;
      local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(u, DateTimeKind.Utc), zone);
      return true;
    }

    private static LedgerTotal Group(Dictionary<string, LedgerTotal> groups, string key, string label)
    {
      if (!groups.TryGetValue(key, out var g)) groups[key] = g = new LedgerTotal();
      g.Label = label;   // the latest wins: lines are in time order, so a renamed file shows its current name
      return g;
    }

    private static string ModelKey(LedgerLine l) => (l.Provider ?? "") + "\n" + (l.Model ?? "");

    private static string ModelLabel(LedgerLine l) => ModelLabel(l.Model, l.Provider, null);

    /// <summary>
    /// Brief FN: a model's row as Usage names it. The line keeps the id (it is the record, and the export writes it); the row reads the catalogue's display
    /// name for it, as the rest of Settings names a model ("Claude Sonnet 5.5 (Anthropic)", not "claude-sonnet-5-5 (Anthropic)"). An id the catalogue no
    /// longer holds, a model since removed, is named by its id, as before. The provider is the line's own word.
    /// </summary>
    public static string ModelLabel(string model, string provider, Func<string, string> displayName)
    {
      string shown = null;
      if (!string.IsNullOrEmpty(model) && displayName != null) { try { shown = displayName(model); } catch { shown = null; } }
      if (string.IsNullOrWhiteSpace(shown)) shown = string.IsNullOrEmpty(model) ? "unknown model" : model;
      return string.IsNullOrEmpty(provider) ? shown : shown + " (" + provider + ")";
    }

    public static string DocumentLabel(string name) => string.IsNullOrEmpty(name) ? "an unsaved file" : name;

    // ---------------------------------------------------------------- the CSV export

    /// <summary>The export: the header, then one row per ledger line that can be read, the same fields. Streams.</summary>
    public static IEnumerable<string> CsvRows(IEnumerable<string> lines)
    {
      yield return string.Join(",", Columns);
      foreach (var text in lines ?? Enumerable.Empty<string>())
      {
        var l = Parse(text);
        if (l == null) continue;
        var turn = l.Kind == LedgerLine.KindTurn;
        yield return string.Join(",", new[]
        {
          Csv(l.Utc), Csv(l.DocumentId), Csv(l.DocumentName), Csv(l.Kind), Csv(l.Verb),
          Csv(turn ? l.ThreadId : ""), turn ? N(l.Turn) : "",
          Csv(l.Provider), Csv(l.Model),
          N(l.InputTokens), N(l.CacheReadTokens), N(l.CacheWriteTokens), N(l.OutputTokens),
          l.Cents.HasValue ? l.Cents.Value.ToString("0.######", CultureInfo.InvariantCulture) : "",
          l.Seconds.ToString("0.#", CultureInfo.InvariantCulture),
          turn ? N(l.Steps) : "", turn ? N(l.ToolCalls) : "",
          l.Searches > 0 ? N(l.Searches) : "",
          l.Chars > 0 ? N(l.Chars) : "",
          Csv(l.Estimated),
        });
      }
    }

    /// <summary>Write the export to <paramref name="dest"/>; returns the number of rows written (the header not counted).</summary>
    public static int ExportCsv(string ledgerPath, string dest)
    {
      var n = -1;
      using (var w = new StreamWriter(dest, false, new UTF8Encoding(false)))
        foreach (var row in CsvRows(ReadLines(ledgerPath))) { w.Write(row); w.Write("\r\n"); n++; }
      return n;
    }

    private static string N(long n) => n.ToString(CultureInfo.InvariantCulture);

    private static string Csv(string s)
    {
      s = s ?? "";
      return s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    // ---------------------------------------------------------------- the Usage view's payload

    /// <summary>What Settings' Usage section renders: every cell already worded here, so the shell only places
    /// text (it computes nothing and says nothing of its own). <paramref name="bytes"/> is the file's size, or
    /// null when there is no file yet. <paramref name="displayName"/> is the catalogue's name for a model id, or null for one it does not hold
    /// (Brief FN); a model's row then carries the name it is shown by as "label" and the id beside it as "id".</summary>
    public static string UsageJson(LedgerSummary s, long? bytes, Func<string, string> displayName = null)
    {
      using (var ms = new MemoryStream())
      {
        using (var w = new Utf8JsonWriter(ms))
        {
          w.WriteStartObject();
          w.WriteStartArray("periods");
          foreach (var p in LedgerSummary.Periods) Row(w, s.Total(p));
          w.WriteEndArray();
          Groups(w, "byModel", s.ByModel, displayName);
          Groups(w, "byVerb", s.ByVerb, null);
          Groups(w, "byDocument", s.ByDocument, null);
          w.WriteString("fileLine", FileLine(bytes));
          w.WriteString("skippedLine", SkippedLine(s.Skipped));
          w.WriteString("emptyLine", EmptyPeriodLine);
          w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
      }
    }

    private static void Groups(Utf8JsonWriter w, string name, Dictionary<string, LedgerTotal>[] byPeriod, Func<string, string> displayName)
    {
      w.WriteStartArray(name);
      foreach (var groups in byPeriod)
      {
        w.WriteStartArray();
        foreach (var g in LedgerSummary.Ordered(groups)) Row(w, g, displayName);
        w.WriteEndArray();
      }
      w.WriteEndArray();
    }

    private static void Row(Utf8JsonWriter w, LedgerTotal t, Func<string, string> displayName = null)
    {
      w.WriteStartObject();
      if (t.Model != null && displayName != null)
      {
        var named = ModelLabel(t.Model, t.Provider, displayName);
        w.WriteString("label", named);
        if (named != t.Label) w.WriteString("id", t.Label);   // the id it was recorded under, for the cell's hover
      }
      else w.WriteString("label", t.Label);
      w.WriteNumber("runs", t.Runs);
      w.WriteNumber("turns", t.Turns);
      w.WriteString("tokensIn", SpendMath.TokensText(t.TotalInputTokens));
      w.WriteString("tokensOut", SpendMath.TokensText(t.OutputTokens));
      w.WriteString("cost", CostText(t));
      w.WriteEndObject();
    }

    // ---------------------------------------------------------------- the sentences the view prints (pinned)

    public static string PeriodName(LedgerPeriod p) =>
      p == LedgerPeriod.Today ? "Today" :
      p == LedgerPeriod.Last7Days ? "Last 7 days" :
      p == LedgerPeriod.ThisMonth ? "This month" : "All time";

    /// <summary>A group's cost: a price, or the words that say why there is none. Never a false zero. A line holds numbers and names and no words,
    /// so these are worded when the view reads it, and no line is touched by a change to them.</summary>
    public static string CostText(LedgerTotal t)
    {
      string billed;
      switch (t.Pricing)
      {
        case Pricing.Empty: billed = "none"; break;
        case Pricing.NotPriced: billed = t.OllamaCloudUnpricedRows == t.UnpricedRows ? SpendMath.OllamaCloudCostShort : "not priced"; break;
        case Pricing.Partly: billed = Money(t.Cents) + ", partly priced"; break;
        default: billed = Money(t.Cents); break;
      }
      if (t.EstimatedRows == 0) return billed;
      // Brief DL: what was billed and, apart from it, about what the stopped runs cost, said as an estimate. A table cell is narrow, so this is the short form, in the footer's own money words, measured to fit at the Usage table's width: "$3.41 + ~12.0¢ est.".
      var estimate = Money(t.EstimatedCents) + " est.";
      return billed == "none" ? estimate : billed + " + " + estimate;
    }

    /// <summary>The same split in a whole sentence, for where there is room (the Eto fallback's period line): "$3.41 billed, about 12 c estimated for stopped runs".</summary>
    public static string CostSentence(LedgerTotal t)
    {
      if (t.EstimatedRows == 0) return CostText(t);
      var estimate = EstimateWord(t.EstimatedCents) + " estimated for stopped runs";
      var billed = t.BilledRows == 0 ? null : CostText(new LedgerTotal { Runs = t.Runs - t.EstimatedRows, Turns = t.Turns, PricedRows = t.PricedRows, UnpricedRows = t.UnpricedRows, OllamaCloudUnpricedRows = t.OllamaCloudUnpricedRows, Cents = t.Cents });
      return billed == null ? estimate : billed + " billed, " + estimate;
    }

    /// <summary>"about 12 c", "about $1.20", or "less than 0.1 c": a sum of estimates is never stated to the cent.</summary>
    public static string EstimateWord(double cents) =>
      cents < 0.1 ? "less than 0.1 c" : cents < 100 ? "about " + cents.ToString("0.#", CultureInfo.InvariantCulture) + " c" : "about $" + (cents / 100.0).ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>"~12.3¢" under a dollar (the footer's wording), "$4.27" from a dollar up.</summary>
    public static string Money(double cents) =>
      cents < 100 ? SpendMath.CentsText(cents) : "$" + (cents / 100.0).ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>The Eto fallback's line for one period: "Today: 3 runs, 2 turns, 41k in / 6k out, ~12.3¢".</summary>
    public static string PeriodLine(LedgerTotal t) =>
      t.Label + ": " + Count(t.Runs, "run") + ", " + Count(t.Turns, "turn") + ", " +
      SpendMath.TokensText(t.TotalInputTokens) + " in / " + SpendMath.TokensText(t.OutputTokens) + " out, " + CostSentence(t);

    public static string Count(int n, string noun) => n + " " + noun + (n == 1 ? "" : "s");

    /// <summary>"Your spend is recorded in spend.jsonl, 48 KB, numbers only." The size is the file's own.</summary>
    public static string FileLine(long? bytes) =>
      bytes.HasValue
        ? "Your spend is recorded in " + FileName + ", " + SizeText(bytes.Value) + ", numbers only."
        : "Nothing has been spent yet. Your spend will be recorded in " + FileName + ", numbers only.";

    public static string SizeText(long bytes)
    {
      if (bytes < 1024) return bytes + (bytes == 1 ? " byte" : " bytes");
      if (bytes < 1024 * 1024) return Math.Max(1, (long)Math.Round(bytes / 1024.0)) + " KB";
      return (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
    }

    /// <summary>Said when the reader skipped lines; empty when it skipped none.</summary>
    public static string SkippedLine(int skipped) =>
      skipped <= 0 ? "" :
      skipped == 1 ? "1 line of the record could not be read and is not counted." :
      skipped + " lines of the record could not be read and are not counted.";

    /// <summary>A breakdown table with no rows for the chosen period.</summary>
    public const string EmptyPeriodLine = "Nothing in this period.";

    /// <summary>The Eto fallback's line when the record could not be read at all.</summary>
    public const string UnreadableLine = "Your spend could not be read.";

    /// <summary>The export's closing status.</summary>
    public static string ExportedLine(int rows) => "Exported " + Count(rows, "row") + ".";

    /// <summary>Said once per session on Rhino's command line when an append fails. The session file's record stands.</summary>
    public const string AppendFailedLine =
      "Dwight: could not add to the spend record (spend.jsonl). This file's own history still holds the cost.";
  }
}
