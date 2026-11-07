using System;
using System.Linq;
using System.Text.Json.Nodes;
using Dwight.Llm;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// U38 (#873) - ONE spec-correct reader for every provider's reply stream, which REFUSES a damaged reply
  /// instead of skipping the damage in silence.
  ///
  /// <para>Two halves, and the first is the positive control (#212): every REAL captured stream reads clean,
  /// event for event, and the legitimate variants the specification allows (multi-line data, CRLF and CR line
  /// ends, comments, a BOM) read to the same events. Only then does a refusal on a damaged copy mean anything -
  /// a reader that refused everything would pass the second half alone.</para>
  ///
  /// <para>What these witness: the reader's decisions on the providers' real bytes and on each of them damaged
  /// in one place. What they cannot: that a live provider never sends something these seven streams do not
  /// contain. Google was not captured (its key was rejected), so its fixture is hand-made and says so.</para>
  /// </summary>
  public class SseReaderTests
  {
    private static SseEvent[] Events(string text) => SseReader.Events(StreamFixtures.Reader(text)).ToArray();

    private static (SseEvent Event, JsonNode Json)[] Json(string text, string sentinel = null) =>
      SseReader.JsonEvents(StreamFixtures.Reader(text), sentinel).ToArray();

    private static StreamDamageException Refused(string text, string sentinel = null) =>
      Assert.Throws<StreamDamageException>(() => Json(text, sentinel));

    // ---- the positive control: the real streams read clean ---------------------------------------------

    [Theory]
    [InlineData(StreamFixtures.Anthropic01, 15)]
    [InlineData(StreamFixtures.Anthropic02, 10)]
    [InlineData(StreamFixtures.Anthropic03, 26)]
    [InlineData(StreamFixtures.Anthropic04, 15)]
    [InlineData(StreamFixtures.Anthropic05, 9)]
    [InlineData(StreamFixtures.Anthropic06, 43)]
    [InlineData(StreamFixtures.OpenAI07, 49)]
    [InlineData(StreamFixtures.Google01, 4)]
    [InlineData(StreamFixtures.OllamaCloud02, 65)]
    [InlineData(StreamFixtures.AnthropicLive01, 7)]
    [InlineData(StreamFixtures.AnthropicLive02, 9)]
    [InlineData(StreamFixtures.AnthropicLive03, 32)]
    [InlineData(StreamFixtures.AnthropicLive04, 53)]
    [InlineData(StreamFixtures.AnthropicLive05, 18)]
    [InlineData(StreamFixtures.GoogleLive01, 6)]
    [InlineData(StreamFixtures.GoogleLive02, 2)]
    [InlineData(StreamFixtures.GoogleLive03, 5)]
    [InlineData(StreamFixtures.GoogleLive04, 2)]
    [InlineData(StreamFixtures.GoogleLive05, 2)]
    [InlineData(StreamFixtures.GoogleLive06, 4)]
    [InlineData(StreamFixtures.OllamaCloudLive01, 117)]
    [InlineData(StreamFixtures.OllamaCloudLive02, 236)]
    [InlineData(StreamFixtures.OllamaCloudLive03, 803)]
    [InlineData(StreamFixtures.OllamaCloudLive04, 457)]
    [InlineData(StreamFixtures.OllamaCloudLive05, 80)]
    [InlineData(StreamFixtures.OllamaCloudLive06, 307)]
    [InlineData(StreamFixtures.OpenAILive01, 84)]
    [InlineData(StreamFixtures.OpenAILive02, 5)]
    [InlineData(StreamFixtures.OpenAILive03, 126)]
    [InlineData(StreamFixtures.OpenAILive04, 5)]
    [InlineData(StreamFixtures.OpenAILive05, 9)]
    [InlineData(StreamFixtures.OpenAILive06, 37)]
    public void Every_captured_stream_reads_every_event_and_refuses_nothing(string fixture, int events)
    {
      var read = Json(StreamFixtures.Read(fixture), sentinel: "[DONE]");

      Assert.Equal(events, read.Length);
      Assert.Equal(Enumerable.Range(1, events), read.Select(e => e.Event.Index));
    }

    [Fact]
    public void The_hand_made_google_stream_with_crlf_ends_reads_its_three_events()
    {
      var read = Json(StreamFixtures.Read(StreamFixtures.GoogleHandMade));

      Assert.Equal(3, read.Length);
      Assert.All(read, e => Assert.NotNull(e.Json["candidates"]));
    }

    /// <summary>Anthropic names every event, and the name agrees with the data's own type - so the reader is
    /// pairing each <c>event:</c> line with ITS data, not a neighbour's.</summary>
    [Theory]
    [InlineData(StreamFixtures.Anthropic01)]
    [InlineData(StreamFixtures.Anthropic03)]
    [InlineData(StreamFixtures.Anthropic06)]
    public void Each_anthropic_event_name_matches_its_own_data(string fixture)
    {
      foreach (var (ev, json) in Json(StreamFixtures.Read(fixture)))
        Assert.Equal(ev.Name, json["type"].GetValue<string>());
    }

    [Fact]
    public void The_openai_done_sentinel_is_returned_as_itself_and_never_parsed()
    {
      var last = Json(StreamFixtures.Read(StreamFixtures.OpenAI07), sentinel: "[DONE]").Last();

      Assert.Equal("[DONE]", last.Event.Data);
      Assert.Null(last.Json);
    }

    /// <summary>Without the sentinel declared, "[DONE]" is not JSON and is refused - the sentinel is a
    /// per-provider fact, not a hole every provider's reader shares.</summary>
    [Fact]
    public void A_sentinel_is_only_accepted_where_the_provider_declares_one()
    {
      var text = StreamFixtures.Read(StreamFixtures.OpenAI07);
      var ex = Refused(text);
      Assert.Equal(49, ex.EventIndex);
    }

    // ---- what the SPEC allows reads the same: the refusal is never false on a legitimate event (#613) ----

    /// <summary>THE REASON THE READER IS SPEC-CORRECT. A legitimate event may spread its data over several
    /// <c>data:</c> lines, joined by a newline. Split between JSON tokens, that is still the same JSON - and it
    /// must read to the same value, or the refusal below would fire on a stream nothing was wrong with.</summary>
    [Fact]
    public void A_legitimate_multi_line_event_reads_to_the_same_json_as_the_one_line_form()
    {
      var text = StreamFixtures.Read(StreamFixtures.Anthropic01);
      var k = StreamFixtures.IndexOf(text, "\" 2 objects,\"");
      var split = StreamFixtures.Damage(text, k, b => b.Replace(",\"delta\":", ",\ndata: \"delta\":"));

      Assert.Contains("\ndata: \"delta\":", split);   // the split really happened
      var original = Json(text);
      var multi = Json(split);
      Assert.Equal(original.Length, multi.Length);
      Assert.Equal(original[k - 1].Json.ToJsonString(), multi[k - 1].Json.ToJsonString());
      Assert.Contains("\n", multi[k - 1].Event.Data);   // and was joined by a newline, as the spec says
    }

    [Theory]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public void Crlf_and_cr_line_ends_read_to_the_same_events(string eol)
    {
      var text = StreamFixtures.Read(StreamFixtures.Anthropic03);
      var other = text.Replace("\n", eol);

      Assert.Equal(Events(text).Select(e => e.Data), Events(other).Select(e => e.Data));
    }

    [Fact]
    public void Comments_a_bom_and_a_well_formed_unknown_field_are_ignored_as_the_spec_says()
    {
      var text = StreamFixtures.Read(StreamFixtures.Anthropic02);
      var noisy = "﻿: keep-alive\n\n" + StreamFixtures.Damage(text, 3, b => ": a comment\nid: 7\nx-trace: abc\n" + b);

      Assert.Equal(Events(text).Select(e => e.Data), Events(noisy).Select(e => e.Data));
    }

    /// <summary>A field with no colon has an empty value, and one space after the colon is stripped - but only
    /// one, so a value that begins with spaces keeps the rest.</summary>
    [Fact]
    public void Field_values_are_split_exactly_as_the_spec_says()
    {
      var ev = Events("event:  two\ndata\ndata:x\ndata: y\n\n").Single();

      Assert.Equal(" two", ev.Name);
      Assert.Equal("\nx\ny", ev.Data);
    }

    // ---- damage in one place is REFUSED, naming the event and the cause ---------------------------------

    /// <summary>A TRUNCATED EVENT: a text delta's data line cut part-way. The old loop dropped it and the answer
    /// came back with those words missing.</summary>
    [Fact]
    public void A_truncated_event_is_refused_by_index()
    {
      var text = StreamFixtures.Read(StreamFixtures.Anthropic01);
      var k = StreamFixtures.IndexOf(text, "\" 2 objects,\"");
      var ex = Refused(StreamFixtures.Damage(text, k, b => b.Substring(0, b.IndexOf(" 2 objects"))));

      Assert.Equal(k, ex.EventIndex);
      Assert.Contains("not valid JSON", ex.Cause);
    }

    /// <summary>A SPLIT DATA LINE: a line broken in two, the second half with no field name. The spec would
    /// ignore the second half as an unknown field - dropping the data it held - so it is refused instead.</summary>
    [Fact]
    public void A_data_line_broken_in_two_is_refused_by_index()
    {
      var text = StreamFixtures.Read(StreamFixtures.Anthropic03);
      var k = StreamFixtures.IndexOf(text, "eled it");
      var ex = Refused(StreamFixtures.Damage(text, k, b => b.Replace("\"eled it\"", "\"eled\n it\"")));

      Assert.Equal(k, ex.EventIndex);
      Assert.Contains("not an SSE field", ex.Cause);
    }

    /// <summary>The same break with a <c>data:</c> prefix on the second half is spec-legal - and lands a newline
    /// INSIDE a JSON string, which no provider sends. Refused as JSON, not accepted as a joined event.</summary>
    [Fact]
    public void A_data_line_split_inside_a_string_is_refused_even_with_a_data_prefix()
    {
      var text = StreamFixtures.Read(StreamFixtures.Anthropic03);
      var k = StreamFixtures.IndexOf(text, "eled it");
      var ex = Refused(StreamFixtures.Damage(text, k, b => b.Replace("\"eled it\"", "\"eled\ndata:  it\"")));

      Assert.Equal(k, ex.EventIndex);
      Assert.Contains("not valid JSON", ex.Cause);
    }

    /// <summary>A line that lost its <c>data:</c> - the JSON is intact, but read as a field it would be
    /// ignored, and the event named above it would carry nothing.</summary>
    [Fact]
    public void A_json_line_that_lost_its_data_prefix_is_refused()
    {
      var text = StreamFixtures.Read(StreamFixtures.OpenAI07);
      var ex = Refused(StreamFixtures.Damage(text, 5, b => b.Substring("data: ".Length)), sentinel: "[DONE]");

      Assert.Equal(5, ex.EventIndex);
      Assert.Contains("not an SSE field", ex.Cause);
    }

    /// <summary>A MISSING BLANK LINE between two events joins them into one event of two JSON values.</summary>
    [Fact]
    public void A_missing_blank_line_between_events_is_refused_at_the_joined_event()
    {
      var text = StreamFixtures.Read(StreamFixtures.Anthropic05);
      var blocks = StreamFixtures.Blocks(text);
      blocks[3] = blocks[3] + "\n" + blocks[4];
      blocks.RemoveAt(4);

      var ex = Refused(StreamFixtures.Join(blocks));
      Assert.Equal(4, ex.EventIndex);
      Assert.Contains("not valid JSON", ex.Cause);
    }

    /// <summary>A missing blank line at the END is a stream cut inside its last event. The spec discards that
    /// event in silence - the old defect in another place.</summary>
    [Fact]
    public void A_stream_that_ends_inside_an_event_is_refused()
    {
      var text = StreamFixtures.Read(StreamFixtures.Anthropic05);
      var cut = text.Substring(0, text.Length - 2);   // the closing blank line of the last event

      var ex = Refused(cut);
      Assert.Equal(9, ex.EventIndex);
      Assert.Contains("cut off", ex.Cause);
    }

    [Fact]
    public void A_named_event_whose_data_line_was_lost_is_refused()
    {
      var text = StreamFixtures.Read(StreamFixtures.Anthropic02);
      var ex = Refused(StreamFixtures.Damage(text, 4, b => b.Split('\n')[0]));   // keep "event: ...", drop "data: ..."

      Assert.Equal(4, ex.EventIndex);
      Assert.Contains("carries no data", ex.Cause);
    }

    [Fact]
    public void A_damaged_google_event_is_refused_by_index()
    {
      var text = StreamFixtures.Read(StreamFixtures.GoogleHandMade);
      var damaged = text.Replace("\"text\":\" drives the radius", "\"text\":\" drives the radius\r\n");

      var ex = Refused(damaged);
      Assert.Equal(2, ex.EventIndex);
    }

    // ---- the sentence the user reads -------------------------------------------------------------------

    /// <summary>The refusal reaches the user as a failed run in Dwight's words, naming the place and the cause -
    /// never as a shorter answer, and never claiming more than is true (this reply was not used; nothing is said
    /// about earlier steps of the same turn, which may have run).</summary>
    [Fact]
    public void The_refusal_is_a_failed_run_in_dwights_words_naming_the_event_and_the_cause()
    {
      var text = StreamFixtures.Read(StreamFixtures.Anthropic01);
      var damage = Refused(StreamFixtures.Damage(text, 5, b => b.Substring(0, b.Length - 8)));
      var msg = SseReader.Refusal(damage).Message;

      Assert.StartsWith("Dwight could not read the model's reply in full, so it did not act on it: ", msg);
      Assert.Contains("event 5 (line ", msg);
      Assert.Contains("not valid JSON", msg);
      Assert.EndsWith("Nothing from this reply was used.", msg);
      Assert.DoesNotContain("nothing was run", msg.ToLowerInvariant());
    }

    /// <summary>The reply text is the user's, but it is not echoed into the refusal: the sentence names WHERE,
    /// never quotes what was there.</summary>
    [Fact]
    public void The_refusal_does_not_quote_the_damaged_reply()
    {
      var text = StreamFixtures.Read(StreamFixtures.Anthropic03);
      var k = StreamFixtures.IndexOf(text, "eled it");
      var msg = SseReader.Refusal(Refused(StreamFixtures.Damage(text, k, b => b.Replace("\"eled it\"", "\"eled\n it\"")))).Message;

      Assert.DoesNotContain("eled", msg);
    }
  }
}
