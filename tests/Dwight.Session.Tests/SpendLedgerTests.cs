using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;
using Dwight.Session;

namespace Dwight.Session.Tests
{
  /// <summary>Brief AK: the central cost ledger. The format a line has, what the reader makes of a dozen lines
  /// (one model unpriced, one line corrupt), the periods in local time on a fixed clock, the CSV export, and
  /// every sentence the Usage view and the privacy page say about it.</summary>
  public class SpendLedgerTests
  {
    private const string Doc = "66fd7275-7717-4065-90eb-483736e9296a";
    private const string Doc2 = "0b9e1a52-1111-4222-8333-944455556666";

    private static StoredSpend Spend(string model, int inp, int outp, double? cents, int steps = 0, int calls = 0, int searches = 0) => new StoredSpend
    {
      Model = model, ElapsedMs = 3300, InputTokens = inp, OutputTokens = outp, CacheReadTokens = 1000, CacheWriteTokens = 200,
      Cents = cents, CostText = cents.HasValue ? SpendMath.CentsText(cents.Value) : "local · free",
      Steps = steps, ToolCalls = calls, Searches = searches,
    };

    private static string[] Keys(string json)
    {
      using (var d = JsonDocument.Parse(json)) return d.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
    }

    // ---------------------------------------------------------------- the line format

    [Fact]
    public void A_verb_line_has_exactly_the_verb_fields_and_nothing_else()
    {
      var l = SpendLedgerCore.ForVerb(Spend("claude-sonnet-5", 900, 300, 1.4), new DateTime(2026, 10, 30, 9, 0, 0, DateTimeKind.Utc),
        Doc, "tower.gh", DwightVerbs.Explain, "anthropic");
      Assert.Equal(new[] { "utc", "documentId", "documentName", "kind", "verb", "provider", "model", "inputTokens",
        "cacheReadTokens", "cacheWriteTokens", "outputTokens", "cents", "seconds" }, Keys(SpendLedgerCore.Serialize(l)));
      var back = SpendLedgerCore.Parse(SpendLedgerCore.Serialize(l));
      Assert.Equal("2026-10-30T09:00:00.0000000Z", back.Utc);
      Assert.Equal("verb", back.Kind);
      Assert.Equal("Explain", back.Verb);
      Assert.Equal(1.4, back.Cents);
      Assert.Equal(3.3, back.Seconds);
      Assert.Equal(900, back.InputTokens);
      Assert.Equal(1000, back.CacheReadTokens);
      Assert.Equal(200, back.CacheWriteTokens);
    }

    // ---- Brief CY: the line carries the request's size, and the question reads the recent lines ----

    private static LedgerLine VerbLine(long chars, int input = 10_000, int output = 800, string verb = "Explain", string provider = "Anthropic",
      string model = "claude-sonnet-5-5", DateTime? utc = null)
    {
      var spend = new StoredSpend { Model = model, InputTokens = input, OutputTokens = output, Cents = 1, Chars = chars };
      return SpendLedgerCore.ForVerb(spend, utc ?? new DateTime(2026, 11, 1, 5, 0, 0, DateTimeKind.Utc), Doc, "tower.gh", verb, provider);
    }

    [Fact]
    public void A_verb_line_round_trips_the_characters_it_sent()
    {
      var text = SpendLedgerCore.Serialize(VerbLine(75_000));
      Assert.Contains("\"chars\":75000", text);
      Assert.Equal(75_000, SpendLedgerCore.Parse(text).Chars);
    }

    [Fact]
    public void A_line_that_did_not_count_them_has_no_field_and_an_older_line_reads_as_none()
    {
      var text = SpendLedgerCore.Serialize(VerbLine(0));
      Assert.DoesNotContain("chars", text);
      Assert.Equal(0, SpendLedgerCore.Parse(text).Chars);
    }

    [Fact]
    public void A_turn_line_never_carries_them()
    {
      var spend = new StoredSpend { Model = "m", InputTokens = 10, OutputTokens = 2, Cents = 1, Chars = 500 };
      var line = SpendLedgerCore.ForTurn(spend, DateTime.UtcNow, Doc, "tower.gh", DwightVerbs.Build, "Anthropic", "t1", 1);
      Assert.DoesNotContain("chars", SpendLedgerCore.Serialize(line));
    }

    [Fact]
    public void The_export_has_the_column_last_and_an_empty_cell_for_a_line_without_it()
    {
      // Brief DL: "estimated" came after "chars", so chars is the last but one, still after every other field and still empty for a line without it.
      Assert.Equal("estimated", SpendLedgerCore.Columns.Last());
      Assert.Equal("chars", SpendLedgerCore.Columns[SpendLedgerCore.Columns.Length - 2]);
      var rows = SpendLedgerCore.CsvRows(new[] { SpendLedgerCore.Serialize(VerbLine(75_000)), SpendLedgerCore.Serialize(VerbLine(0)) }).ToList();
      Assert.EndsWith(",chars,estimated", rows[0]);
      Assert.EndsWith(",75000,", rows[1]);
      Assert.EndsWith(",,", rows[2]);
      Assert.Equal(rows[0].Split(',').Length, rows[1].Split(',').Length);
    }

    [Fact]
    public void Two_attempts_add_their_characters_and_a_side_with_none_leaves_the_run_without()
    {
      var counted = new StoredSpend { Chars = 1000 };
      Assert.Equal(3000, SpendMath.Combine(counted, new StoredSpend { Chars = 2000 }).Chars);
      Assert.Equal(0, SpendMath.Combine(counted, new StoredSpend()).Chars);
    }

    [Fact]
    public void The_characters_stay_out_of_the_session_file()
    {
      var json = System.Text.Json.JsonSerializer.Serialize(new StoredSpend { Model = "m", Chars = 75_000 });
      Assert.DoesNotContain("75000", json);
    }

    private static LedgerSample SampleOf(params LedgerLine[] lines) => SpendLedgerCore.Sample(lines.Select(SpendLedgerCore.Serialize));

    [Fact]
    public void The_ratio_is_the_median_of_a_providers_lines()
    {
      Assert.Equal(3.0, SampleOf(VerbLine(20_000), VerbLine(30_000), VerbLine(90_000)).CharsPerToken("Anthropic", "claude-sonnet-5-5"));
      Assert.Equal(2.5, SampleOf(VerbLine(10_000), VerbLine(20_000), VerbLine(30_000), VerbLine(90_000)).CharsPerToken("Anthropic", "claude-sonnet-5-5"));
      Assert.Null(SampleOf(VerbLine(20_000), VerbLine(30_000), VerbLine(90_000)).CharsPerToken("OpenAI", "claude-sonnet-5-5"));
    }

    [Fact]
    public void Under_three_lines_there_is_no_ratio_and_no_answer()
    {
      var two = SampleOf(VerbLine(20_000), VerbLine(30_000));
      Assert.Null(two.CharsPerToken("Anthropic", "claude-sonnet-5-5"));
      Assert.Null(two.OutputTokens("Explain", "claude-sonnet-5-5"));
    }

    [Fact]
    public void An_older_line_without_characters_is_skipped_for_the_ratio_and_still_counts_for_the_answer()
    {
      var sample = SampleOf(VerbLine(0, output: 700), VerbLine(0, output: 800), VerbLine(0, output: 900), VerbLine(30_000), VerbLine(30_000));
      Assert.Null(sample.CharsPerToken("Anthropic", "claude-sonnet-5-5"));
      Assert.Equal(800, sample.OutputTokens("Explain", "claude-sonnet-5-5"));
    }

    [Fact]
    public void Only_the_newest_twenty_lines_of_a_key_count()
    {
      var old = Enumerable.Range(0, 30).Select(_ => VerbLine(100_000, output: 5000));
      var recent = Enumerable.Range(0, 20).Select(_ => VerbLine(20_000, output: 500));
      var sample = SampleOf(old.Concat(recent).ToArray());
      Assert.Equal(2.0, sample.CharsPerToken("Anthropic", "claude-sonnet-5-5"));
      Assert.Equal(500, sample.OutputTokens("Explain", "claude-sonnet-5-5"));
    }

    [Fact]
    public void The_answer_is_kept_per_verb_and_per_model()
    {
      var sample = SampleOf(VerbLine(0, output: 800), VerbLine(0, output: 800), VerbLine(0, output: 800));
      Assert.Null(sample.OutputTokens("Diagnose", "claude-sonnet-5-5"));
      Assert.Null(sample.OutputTokens("Explain", "claude-opus-5-5"));
    }

    [Fact]
    public void A_call_that_answered_nothing_says_nothing_about_how_long_an_answer_is()
    {
      var sample = SampleOf(VerbLine(0, output: 0), VerbLine(0, output: 0), VerbLine(0, output: 800), VerbLine(0, output: 800));
      Assert.Null(sample.OutputTokens("Explain", "claude-sonnet-5-5"));
    }

    [Fact]
    public void A_chat_turn_is_not_a_verb_run_and_a_broken_line_is_passed_over()
    {
      var turn = SpendLedgerCore.Serialize(SpendLedgerCore.ForTurn(new StoredSpend { Model = "claude-sonnet-5-5", InputTokens = 10, OutputTokens = 9000 },
        DateTime.UtcNow, Doc, "tower.gh", "Explain", "Anthropic", "t1", 1));
      var verb = SpendLedgerCore.Serialize(VerbLine(0, output: 800));
      var sample = SpendLedgerCore.Sample(new[] { turn, turn, turn, "not json", "", verb, verb, verb });
      Assert.Equal(800, sample.OutputTokens("Explain", "claude-sonnet-5-5"));
    }

    [Fact]
    public void A_read_is_known_by_its_file_verb_model_and_characters_inside_five_minutes()
    {
      var at = new DateTime(2026, 11, 1, 5, 0, 0, DateTimeKind.Utc);
      var sample = SampleOf(VerbLine(75_000, utc: at));
      Assert.True(sample.ReadWithinCacheLife(Doc, "Explain", "claude-sonnet-5-5", 75_000, at.AddMinutes(5)));
      Assert.False(sample.ReadWithinCacheLife(Doc, "Explain", "claude-sonnet-5-5", 75_000, at.AddMinutes(5).AddSeconds(1)));
      Assert.False(sample.ReadWithinCacheLife(Doc, "Explain", "claude-sonnet-5-5", 75_001, at));
      Assert.False(sample.ReadWithinCacheLife(Doc, "Diagnose", "claude-sonnet-5-5", 75_000, at));
      Assert.False(sample.ReadWithinCacheLife(Doc, "Explain", "claude-opus-5-5", 75_000, at));
      Assert.False(sample.ReadWithinCacheLife("", "Explain", "claude-sonnet-5-5", 75_000, at));
      Assert.False(sample.ReadWithinCacheLife(Doc, "Explain", "claude-sonnet-5-5", 0, at));
    }

    [Fact]
    public void An_empty_sample_answers_nothing()
    {
      Assert.Null(LedgerSample.Empty.CharsPerToken("Anthropic", "claude-sonnet-5-5"));
      Assert.Null(LedgerSample.Empty.OutputTokens("Explain", "m"));
      Assert.False(LedgerSample.Empty.ReadWithinCacheLife(Doc, "Explain", "m", 1, DateTime.UtcNow));
    }

    [Fact]
    public void The_tail_is_the_newest_lines_without_the_part_the_cut_lands_in()
    {
      var path = Path.Combine(Path.GetTempPath(), "dwight-tail-" + Guid.NewGuid().ToString("N") + ".jsonl");
      try
      {
        File.WriteAllText(path, string.Join("\n", Enumerable.Range(0, 100).Select(i => "line" + i.ToString("000"))) + "\n");
        var all = SpendLedgerCore.ReadTail(path, 1024 * 1024).ToList();
        Assert.Equal(100, all.Count);

        var tail = SpendLedgerCore.ReadTail(path, 25).ToList();
        Assert.Equal(new[] { "line097", "line098", "line099" }, tail);

        Assert.Empty(SpendLedgerCore.ReadTail(path + ".missing", 25));
      }
      finally { File.Delete(path); }
    }

    /// <summary>The read is bounded, and it finds what the question needs inside that bound: a ledger of a thousand runs' lines fits it twice over.</summary>
    [Fact]
    public void The_bound_on_the_tail_holds_far_more_than_twenty_lines_a_key()
    {
      var line = SpendLedgerCore.Serialize(VerbLine(75_000));
      Assert.True(SpendLedgerCore.SampleBytes / (line.Length + 40) > 2000);
    }

    [Fact]
    public void A_turn_line_has_exactly_the_turn_fields_and_searches_only_when_it_searched()
    {
      var utc = new DateTime(2026, 10, 30, 9, 0, 0, DateTimeKind.Utc);
      var plain = SpendLedgerCore.ForTurn(Spend("claude-sonnet-5", 10000, 2000, 4.1, 3, 5), utc, Doc, "tower.gh",
        DwightVerbs.Build, "anthropic", "t1", 2);
      var turnKeys = new[] { "utc", "documentId", "documentName", "kind", "verb", "threadId", "turn", "provider", "model",
        "inputTokens", "cacheReadTokens", "cacheWriteTokens", "outputTokens", "cents", "seconds", "steps", "toolCalls" };
      Assert.Equal(turnKeys, Keys(SpendLedgerCore.Serialize(plain)));

      var searched = SpendLedgerCore.ForTurn(Spend("claude-sonnet-5", 10000, 2000, 4.1, 3, 5, searches: 2), utc, Doc, "tower.gh",
        DwightVerbs.Build, "anthropic", "t1", 3);
      Assert.Equal(turnKeys.Concat(new[] { "searches" }).ToArray(), Keys(SpendLedgerCore.Serialize(searched)));
      var back = SpendLedgerCore.Parse(SpendLedgerCore.Serialize(searched));
      Assert.Equal("turn", back.Kind);
      Assert.Equal("Build", back.Verb);
      Assert.Equal("t1", back.ThreadId);
      Assert.Equal(3, back.Turn);
      Assert.Equal(3, back.Steps);
      Assert.Equal(5, back.ToolCalls);
      Assert.Equal(2, back.Searches);
    }

    [Fact]
    public void An_unpriced_line_writes_cents_as_null_never_as_zero()
    {
      var l = SpendLedgerCore.ForVerb(Spend("qwen3.5:9b", 900, 300, null), DateTime.UtcNow, Doc, "tower.gh", DwightVerbs.Explain, "ollama");
      var json = SpendLedgerCore.Serialize(l);
      Assert.Contains("\"cents\":null", json);
      Assert.Null(SpendLedgerCore.Parse(json).Cents);
    }

    [Fact]
    public void A_path_never_enters_a_line_only_the_file_name()
    {
      var l = SpendLedgerCore.ForVerb(Spend("m", 1, 1, 0.1), DateTime.UtcNow, Doc, @"\server\share\Projects\tower.gh", DwightVerbs.Explain, "anthropic");
      Assert.Equal("tower.gh", l.DocumentName);
      Assert.DoesNotContain("share", SpendLedgerCore.Serialize(l));
      Assert.Equal("a.gh", SpendLedgerCore.NameOnly("/x/y/a.gh"));
    }

    [Fact]
    public void Append_creates_the_folder_and_only_ever_adds_a_line()
    {
      var dir = Path.Combine(Path.GetTempPath(), "dwight-ledger-" + Guid.NewGuid().ToString("N"));
      var path = Path.Combine(dir, "sub", SpendLedgerCore.FileName);
      try
      {
        var l = SpendLedgerCore.ForVerb(Spend("m", 1, 1, 0.1), DateTime.UtcNow, Doc, "a.gh", DwightVerbs.Explain, "anthropic");
        SpendLedgerCore.Append(path, l);
        SpendLedgerCore.Append(path, l);
        var lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);
        Assert.All(lines, x => Assert.NotNull(SpendLedgerCore.Parse(x)));
        Assert.False(File.ReadAllBytes(path).Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }));   // no BOM
      }
      finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ---------------------------------------------------------------- the reader, on a dozen lines

    // The fixed clock: 30 October 2026, 10:00 local, in a zone 5h30 ahead of UTC (no daylight saving), so
    // "today" and "this month" are decided in LOCAL time - a line at 20:00 UTC on the 29th is the 30th here.
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("ledger-test", TimeSpan.FromMinutes(330), "ledger-test", "ledger-test");
    private static readonly DateTime Now = new DateTime(2026, 10, 30, 10, 0, 0);

    private static string V(string utc, string doc, string name, string verb, string provider, string model, int inp, int outp, double? cents) =>
      SpendLedgerCore.Serialize(SpendLedgerCore.ForVerb(Spend(model, inp, outp, cents), DateTime.Parse(utc, null, System.Globalization.DateTimeStyles.AdjustToUniversal),
        doc, name, verb, provider));

    private static string T(string utc, string doc, string name, string provider, string model, int turn, int inp, int outp, double? cents) =>
      SpendLedgerCore.Serialize(SpendLedgerCore.ForTurn(Spend(model, inp, outp, cents, 2, 3), DateTime.Parse(utc, null, System.Globalization.DateTimeStyles.AdjustToUniversal),
        doc, name, DwightVerbs.Build, provider, "t1", turn));

    // Each line's total input is inp + 1200 (the fixture's cache read and write).
    private static readonly string[] Fixture =
    {
      V("2026-09-15T08:00:00Z", Doc,  "tower.gh",  DwightVerbs.Explain,    "anthropic", "claude-sonnet-5", 800,  100, 50.0),   // September: all time only
      V("2026-10-02T08:00:00Z", Doc,  "tower.gh",  DwightVerbs.Diagnose,   "anthropic", "claude-sonnet-5", 800,  100, 10.0),   // this month
      V("2026-10-20T08:00:00Z", Doc2, "bridge.gh", DwightVerbs.Reorganise, "anthropic", "claude-haiku-5",  800,  100, 2.0),    // this month
      V("2026-10-23T17:00:00Z", Doc2, "bridge.gh", DwightVerbs.Simplify,   "ollama",    "qwen3.5:9b",      800,  100, null),   // 23rd 22:30 local: outside 7 days
      V("2026-10-23T19:00:00Z", Doc2, "bridge.gh", DwightVerbs.Explain,    "anthropic", "claude-haiku-5",  800,  100, 1.0),    // 24th 00:30 local: the 7th day
      V("2026-10-28T08:00:00Z", Doc,  "tower.gh",  DwightVerbs.Explain,    "ollama",    "qwen3.5:9b",      800,  100, null),   // 7 days
      "{ this line is not json",
      T("2026-10-29T20:00:00Z", Doc,  "tower.gh",  "anthropic", "claude-sonnet-5", 1, 8800, 900, 4.0),                          // 30th 01:30 local: today
      T("2026-10-30T02:00:00Z", Doc,  "tower.gh",  "anthropic", "claude-sonnet-5", 2, 8800, 900, 6.0),                          // today
      T("2026-10-30T03:00:00Z", Doc,  "tower.gh",  "ollama",    "qwen3.5:9b",      3, 8800, 900, null),                         // today, unpriced
      V("2026-10-30T04:00:00Z", "",   "",          DwightVerbs.Explain,    "anthropic", "claude-haiku-5",  800,  100, 0.5),    // today, an unsaved file
      "",
    };

    private static LedgerSummary Sum() => SpendLedgerCore.Summarise(Fixture, Now, Zone);

    [Fact]
    public void A_corrupt_line_is_skipped_counted_and_said_and_a_blank_line_is_neither()
    {
      var s = Sum();
      Assert.Equal(1, s.Skipped);
      Assert.Equal("1 line of the record could not be read and is not counted.", SpendLedgerCore.SkippedLine(1));
      Assert.Equal("3 lines of the record could not be read and are not counted.", SpendLedgerCore.SkippedLine(3));
      Assert.Equal("", SpendLedgerCore.SkippedLine(0));
    }

    [Fact]
    public void The_four_periods_are_decided_in_local_time()
    {
      var s = Sum();
      var today = s.Total(LedgerPeriod.Today);
      Assert.Equal(1, today.Runs);
      Assert.Equal(3, today.Turns);
      Assert.Equal(10.5, today.Cents, 6);
      Assert.Equal(Pricing.Partly, today.Pricing);

      var week = s.Total(LedgerPeriod.Last7Days);
      Assert.Equal(3, week.Runs);    // the 24th (local, the seventh day) and the 28th and today; not the 23rd
      Assert.Equal(3, week.Turns);

      var month = s.Total(LedgerPeriod.ThisMonth);
      Assert.Equal(6, month.Runs);
      Assert.Equal(3, month.Turns);
      Assert.Equal(23.5, month.Cents, 6);

      var all = s.Total(LedgerPeriod.AllTime);
      Assert.Equal(7, all.Runs);
      Assert.Equal(3, all.Turns);
      Assert.Equal(73.5, all.Cents, 6);
      Assert.Equal(7 * 2000 + 3 * 10000, all.TotalInputTokens);
      Assert.Equal(7 * 100 + 3 * 900, all.OutputTokens);
      Assert.Equal(7 * 800 + 3 * 8800, all.InputTokens);
      Assert.Equal(10 * 1000, all.CacheReadTokens);
      Assert.Equal(10 * 200, all.CacheWriteTokens);
    }

    // A line as a build before the wording change wrote it. It holds numbers and names, and no words about cost.
    private const string PastOllamaCloudLine =
      "{\"utc\":\"2026-10-20T10:00:00.0000000Z\",\"documentId\":\"" + Doc + "\",\"documentName\":\"tower.gh\",\"kind\":\"turn\",\"verb\":\"Build\"," +
      "\"threadId\":\"t1\",\"turn\":1,\"provider\":\"OllamaCloud\",\"model\":\"glm-5.3:cloud\",\"inputTokens\":100,\"cacheReadTokens\":0,\"cacheWriteTokens\":0," +
      "\"outputTokens\":10,\"cents\":null,\"seconds\":2.5,\"steps\":1,\"toolCalls\":0}";

    private static string WithProvider(string provider, string cents = "null") =>
      PastOllamaCloudLine.Replace("\"OllamaCloud\"", "\"" + provider + "\"").Replace("\"cents\":null", "\"cents\":" + cents);

    private static LedgerTotal AllTime(params string[] lines) => SpendLedgerCore.Summarise(lines, Now, Zone).Total(LedgerPeriod.AllTime);

    [Fact]
    public void Usage_says_what_is_true_on_every_Ollama_Cloud_plan_and_never_a_subscription()
    {
      var s = SpendLedgerCore.Summarise(new[] { PastOllamaCloudLine }, Now, Zone);
      Assert.Equal("cost depends on your plan", SpendLedgerCore.CostText(s.Total(LedgerPeriod.AllTime)));
      Assert.Equal("All time: 0 runs, 1 turn, 100 in / 10 out, cost depends on your plan", SpendLedgerCore.PeriodLine(s.Total(LedgerPeriod.AllTime)));
      var json = SpendLedgerCore.UsageJson(s, 100);
      Assert.Contains("\"cost\":\"cost depends on your plan\"", json);
      Assert.DoesNotContain("subscription", json, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Brief FN: a model's row is named as the catalogue names it -----------------------------------------------------

    private static string ModelLine(string model, string provider, double? cents) => SpendLedgerCore.Serialize(new LedgerLine
    {
      Utc = "2026-10-30T03:00:00.0000000Z", DocumentId = "d1", DocumentName = "lamp.gh", Kind = LedgerLine.KindVerb, Verb = "Explain",
      Provider = provider, Model = model, InputTokens = 1000, OutputTokens = 100, Cents = cents, Seconds = 1,
    });

    private static readonly Func<string, string> Catalogue = (id) => id == "claude-sonnet-5-5" ? "Claude Sonnet 5.5" : id == "glm-5.3:cloud" ? "GLM-5.3 (cloud)" : null;

    [Fact]
    public void A_models_row_reads_the_catalogues_name_with_its_provider_and_keeps_the_id_beside_it()
    {
      Assert.Equal("Claude Sonnet 5.5 (Anthropic)", SpendLedgerCore.ModelLabel("claude-sonnet-5-5", "Anthropic", Catalogue));
      Assert.Equal("GLM-5.3 (cloud) (OllamaCloud)", SpendLedgerCore.ModelLabel("glm-5.3:cloud", "OllamaCloud", Catalogue));
      // A model the catalogue no longer holds is named by its id, as it always was; so is every model when there is no catalogue to ask.
      Assert.Equal("claude-sonnet-4-5 (Anthropic)", SpendLedgerCore.ModelLabel("claude-sonnet-4-5", "Anthropic", Catalogue));
      Assert.Equal("claude-sonnet-5-5 (Anthropic)", SpendLedgerCore.ModelLabel("claude-sonnet-5-5", "Anthropic", null));
      Assert.Equal("unknown model (Anthropic)", SpendLedgerCore.ModelLabel("", "Anthropic", Catalogue));
      Assert.Equal("claude-sonnet-5-5", SpendLedgerCore.ModelLabel("claude-sonnet-5-5", "", (id) => throw new InvalidOperationException()));   // a lookup that fails names it by its id

      var s = SpendLedgerCore.Summarise(new[] { ModelLine("claude-sonnet-5-5", "Anthropic", 2), ModelLine("claude-sonnet-4-5", "Anthropic", 1) }, Now, Zone);
      using (var d = JsonDocument.Parse(SpendLedgerCore.UsageJson(s, 100, Catalogue)))
      {
        var rows = d.RootElement.GetProperty("byModel")[(int)LedgerPeriod.AllTime].EnumerateArray().ToList();
        var known = rows.Single(r => r.GetProperty("label").GetString() == "Claude Sonnet 5.5 (Anthropic)");
        Assert.Equal("claude-sonnet-5-5 (Anthropic)", known.GetProperty("id").GetString());
        var gone = rows.Single(r => r.GetProperty("label").GetString() == "claude-sonnet-4-5 (Anthropic)");
        Assert.False(gone.TryGetProperty("id", out _));   // named by its id already, so nothing beside it
        // Only the models are renamed: the verbs and the files read as they did.
        Assert.Equal("Explain", d.RootElement.GetProperty("byVerb")[(int)LedgerPeriod.AllTime][0].GetProperty("label").GetString());
        Assert.Equal("lamp.gh", d.RootElement.GetProperty("byDocument")[(int)LedgerPeriod.AllTime][0].GetProperty("label").GetString());
      }
      // With no catalogue the payload is the one it was, byte for byte.
      Assert.DoesNotContain("\"id\"", SpendLedgerCore.UsageJson(s, 100));
      Assert.Contains("\"label\":\"claude-sonnet-5-5 (Anthropic)\"", SpendLedgerCore.UsageJson(s, 100));
    }

    [Fact]
    public void Settings_asks_the_catalogue_for_the_names_and_the_page_keeps_the_id_on_hover()
    {
      var root = new DirectoryInfo(AppContext.BaseDirectory);
      while (root != null && !File.Exists(Path.Combine(root.FullName, "Dwight.sln"))) root = root.Parent;
      Assert.NotNull(root);
      Assert.Contains("SpendLedgerCore.UsageJson(sum, bytes, id => DwightConfig.FindModel(id)?.DisplayName)", File.ReadAllText(Path.Combine(root.FullName, "Dwight", "UI", "DwightSettingsPanel.cs")));
      Assert.Contains("td.title=(i===0&&r.id)?String(r.id):v;", File.ReadAllText(Path.Combine(root.FullName, "Dwight", "Resources", "settings.html")));
    }

    [Fact]
    public void Usage_keeps_its_other_words_for_every_other_group()
    {
      Assert.Equal("not priced", SpendLedgerCore.CostText(AllTime(WithProvider("Ollama"))));   // local keeps its own reading
      Assert.Equal("not priced", SpendLedgerCore.CostText(AllTime(PastOllamaCloudLine, WithProvider("Ollama"))));   // a mix: the plan is not the whole reason
      Assert.Equal("~2.0¢, partly priced", SpendLedgerCore.CostText(AllTime(PastOllamaCloudLine, WithProvider("Anthropic", "2"))));
      Assert.Equal("~2.0¢", SpendLedgerCore.CostText(AllTime(WithProvider("Anthropic", "2"))));
      Assert.Equal("~2.0¢", SpendLedgerCore.CostText(AllTime(WithProvider("OllamaCloud", "2"))));   // a price, when a line has one, is never hidden
    }

    [Fact]
    public void A_past_Ollama_Cloud_line_keeps_what_it_wrote()
    {
      // Read and written back it is the same bytes, and the line format has no field the new words could go in.
      Assert.Equal(PastOllamaCloudLine, SpendLedgerCore.Serialize(SpendLedgerCore.Parse(PastOllamaCloudLine)));
      Assert.DoesNotContain(SpendLedgerCore.Columns, c => c.IndexOf("text", StringComparison.OrdinalIgnoreCase) >= 0);
      var path = Path.Combine(Path.GetTempPath(), "dwight-past-line-" + Guid.NewGuid().ToString("N") + ".jsonl");
      try
      {
        File.WriteAllText(path, PastOllamaCloudLine + "\n");
        var before = File.ReadAllBytes(path);
        SpendLedgerCore.UsageJson(SpendLedgerCore.Summarise(SpendLedgerCore.ReadLines(path), Now, Zone), before.Length);
        Assert.Equal(before, File.ReadAllBytes(path));
      }
      finally { File.Delete(path); }
    }

    [Fact]
    public void Totals_by_model_name_the_provider_and_an_unpriced_model_says_so()
    {
      var byModel = LedgerSummary.Ordered(Sum().ByModel[(int)LedgerPeriod.AllTime]);
      Assert.Equal(new[] { "claude-sonnet-5 (anthropic)", "claude-haiku-5 (anthropic)", "qwen3.5:9b (ollama)" }, byModel.Select(g => g.Label));
      var sonnet = byModel[0];
      Assert.Equal(2, sonnet.Runs);
      Assert.Equal(2, sonnet.Turns);
      Assert.Equal("~70.0¢", SpendLedgerCore.CostText(sonnet));
      var qwen = byModel[2];
      Assert.Equal(Pricing.NotPriced, qwen.Pricing);
      Assert.Equal("not priced", SpendLedgerCore.CostText(qwen));
      Assert.Equal(2, qwen.Runs);
      Assert.Equal(1, qwen.Turns);
    }

    [Fact]
    public void Totals_by_verb_and_by_document()
    {
      var s = Sum();
      var verbs = s.ByVerb[(int)LedgerPeriod.AllTime];
      Assert.Equal(4, verbs[DwightVerbs.Explain].Runs);
      Assert.Equal(51.5, verbs[DwightVerbs.Explain].Cents, 6);
      Assert.Equal("~51.5¢, partly priced", SpendLedgerCore.CostText(verbs[DwightVerbs.Explain]));
      Assert.Equal(3, verbs[DwightVerbs.Build].Turns);
      Assert.Equal(0, verbs[DwightVerbs.Build].Runs);
      Assert.Equal("not priced", SpendLedgerCore.CostText(verbs[DwightVerbs.Simplify]));

      var docs = LedgerSummary.Ordered(s.ByDocument[(int)LedgerPeriod.AllTime]);
      Assert.Equal(new[] { "tower.gh", "bridge.gh", "an unsaved file" }, docs.Select(g => g.Label));
      Assert.Equal(3, docs[0].Runs);
      Assert.Equal(3, docs[0].Turns);
      Assert.Equal("~70.0¢, partly priced", SpendLedgerCore.CostText(docs[0]));
      Assert.Equal("~3.0¢, partly priced", SpendLedgerCore.CostText(docs[1]));
      Assert.Equal("~0.5¢", SpendLedgerCore.CostText(docs[2]));
    }

    [Fact]
    public void A_file_renamed_under_one_id_is_one_row_under_its_latest_name()
    {
      var lines = new[]
      {
        V("2026-10-30T01:00:00Z", Doc, "old.gh", DwightVerbs.Explain, "anthropic", "m", 1, 1, 1.0),
        V("2026-10-30T02:00:00Z", Doc, "new.gh", DwightVerbs.Explain, "anthropic", "m", 1, 1, 1.0),
      };
      var g = Assert.Single(SpendLedgerCore.Summarise(lines, Now, Zone).ByDocument[(int)LedgerPeriod.AllTime].Values);
      Assert.Equal("new.gh", g.Label);
      Assert.Equal(2, g.Runs);
    }

    [Fact]
    public void An_empty_ledger_has_no_rows_and_says_none_never_zero_cents()
    {
      var s = SpendLedgerCore.Summarise(Array.Empty<string>(), Now, Zone);
      Assert.Equal(Pricing.Empty, s.Total(LedgerPeriod.AllTime).Pricing);
      Assert.Equal("none", SpendLedgerCore.CostText(s.Total(LedgerPeriod.AllTime)));
      Assert.Empty(SpendLedgerCore.ReadLines(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "spend.jsonl")));
    }

    // ---------------------------------------------------------------- the CSV export

    [Fact]
    public void The_csv_export_has_the_ledgers_fields_as_its_header_and_one_row_per_readable_line()
    {
      var rows = SpendLedgerCore.CsvRows(new[]
      {
        T("2026-10-30T02:00:00Z", Doc, "tower, v2.gh", "anthropic", "claude-sonnet-5", 2, 8800, 900, 6.0),
        "not json",
        V("2026-10-30T04:00:00Z", Doc, "a.gh", DwightVerbs.Explain, "ollama", "qwen3.5:9b", 800, 100, null),
      }).ToList();
      Assert.Equal(3, rows.Count);
      Assert.Equal("utc,documentId,documentName,kind,verb,threadId,turn,provider,model,inputTokens,cacheReadTokens," +
        "cacheWriteTokens,outputTokens,cents,seconds,steps,toolCalls,searches,chars,estimated", rows[0]);   // Brief DL: the estimated column, last
      Assert.Equal("2026-10-30T02:00:00.0000000Z," + Doc + ",\"tower, v2.gh\",turn,Build,t1,2,anthropic,claude-sonnet-5," +
        "8800,1000,200,900,6,3.3,2,3,,,", rows[1]);
      Assert.Equal("2026-10-30T04:00:00.0000000Z," + Doc + ",a.gh,verb,Explain,,,ollama,qwen3.5:9b,800,1000,200,100,,3.3,,,,,", rows[2]);
    }

    // ---------------------------------------------------------------- the sentences (pinned)

    [Fact]
    public void The_views_sentences_are_pinned()
    {
      Assert.Equal("Your spend is recorded in spend.jsonl, 48 KB, numbers only.", SpendLedgerCore.FileLine(48 * 1024));
      Assert.Equal("Your spend is recorded in spend.jsonl, 310 bytes, numbers only.", SpendLedgerCore.FileLine(310));
      Assert.Equal("Your spend is recorded in spend.jsonl, 2.5 MB, numbers only.", SpendLedgerCore.FileLine(2_621_440));
      Assert.Equal("Nothing has been spent yet. Your spend will be recorded in spend.jsonl, numbers only.", SpendLedgerCore.FileLine(null));
      Assert.Equal("Nothing in this period.", SpendLedgerCore.EmptyPeriodLine);
      Assert.Equal("$2.50", SpendLedgerCore.Money(250));
      Assert.Equal("~99.9¢", SpendLedgerCore.Money(99.9));
      Assert.Equal("Exported 12 rows.", SpendLedgerCore.ExportedLine(12));
      Assert.Equal("Exported 1 row.", SpendLedgerCore.ExportedLine(1));
      Assert.Equal("Your spend could not be read.", SpendLedgerCore.UnreadableLine);
      Assert.Equal("Dwight: could not add to the spend record (spend.jsonl). This file's own history still holds the cost.",
        SpendLedgerCore.AppendFailedLine);
      Assert.Equal(new[] { "Today", "Last 7 days", "This month", "All time" }, LedgerSummary.Periods.Select(SpendLedgerCore.PeriodName));
    }

    [Fact]
    public void The_eto_fallbacks_period_lines_are_pinned()
    {
      var s = Sum();
      Assert.Equal("Today: 1 run, 3 turns, 32k in / 2.8k out, ~10.5¢, partly priced", SpendLedgerCore.PeriodLine(s.Total(LedgerPeriod.Today)));
      Assert.Equal("All time: 7 runs, 3 turns, 44k in / 3.4k out, ~73.5¢, partly priced", SpendLedgerCore.PeriodLine(s.Total(LedgerPeriod.AllTime)));
    }

    [Fact]
    public void The_usage_payload_carries_four_periods_and_a_breakdown_per_period_already_worded()
    {
      using (var d = JsonDocument.Parse(SpendLedgerCore.UsageJson(Sum(), 48 * 1024)))
      {
        var r = d.RootElement;
        var periods = r.GetProperty("periods").EnumerateArray().ToList();
        Assert.Equal(new[] { "Today", "Last 7 days", "This month", "All time" }, periods.Select(p => p.GetProperty("label").GetString()));
        Assert.Equal("~10.5¢, partly priced", periods[0].GetProperty("cost").GetString());
        Assert.Equal("44k", periods[3].GetProperty("tokensIn").GetString());
        foreach (var name in new[] { "byModel", "byVerb", "byDocument" })
          Assert.Equal(4, r.GetProperty(name).GetArrayLength());
        Assert.Equal("Your spend is recorded in spend.jsonl, 48 KB, numbers only.", r.GetProperty("fileLine").GetString());
        Assert.Equal("1 line of the record could not be read and is not counted.", r.GetProperty("skippedLine").GetString());
        Assert.Equal("Nothing in this period.", r.GetProperty("emptyLine").GetString());
      }
    }

    [Fact]
    public void The_settings_shell_places_the_usage_section_after_the_build_controls_closed_and_says_nothing_of_its_own()
    {
      // Brief BO: Usage is its own group, after the Build controls, and reads the ledger only when it is opened.
      var html = File.ReadAllText(RepoFile("Dwight", "Resources", "settings.html"));
      var fullControl = html.IndexOf("<label>Full control</label>", StringComparison.Ordinal);
      var usage = html.IndexOf("<section class=\"pane\" data-pane=\"Usage\" hidden>", StringComparison.Ordinal);
      var about = html.IndexOf("<section class=\"pane\" data-pane=\"About\" hidden>", StringComparison.Ordinal);
      Assert.True(fullControl > 0 && usage > fullControl && about > usage, "Usage sits after the Build controls and before About");
      Assert.Contains("if(id==='Usage'&&was!=='Usage') dwightSend('usage:open');", html);
      Assert.Contains(">Export CSV...</button>", html);
      Assert.Contains(">Open folder</button>", html);
      Assert.Contains("dwightSend('usage:export')", html);
      Assert.Contains("dwightSend('usage:folder')", html);
      Assert.DoesNotContain("Nothing in this period", html);   // the plugin words it, the shell places it
      Assert.DoesNotContain("—", html);                        // no em dash anywhere a user reads
    }

    [Fact]
    public void The_privacy_page_carries_the_ledgers_paragraph_word_for_word()
    {
      var md = File.ReadAllText(RepoFile("docs", "public", "privacy.md")).Replace("\r\n", "\n");
      Assert.Contains("\n" + SpendLedgerCore.PrivacyParagraph + "\n", md);
      Assert.DoesNotContain("—", SpendLedgerCore.PrivacyParagraph);
    }

    private static string RepoFile(params string[] parts)
    {
      var dir = AppContext.BaseDirectory;
      while (dir != null)
      {
        var candidate = Path.Combine(new[] { dir }.Concat(parts).ToArray());
        if (File.Exists(candidate)) return candidate;
        dir = Directory.GetParent(dir)?.FullName;
      }
      throw new FileNotFoundException(string.Join("/", parts) + " not found from " + AppContext.BaseDirectory);
    }
  }
}
