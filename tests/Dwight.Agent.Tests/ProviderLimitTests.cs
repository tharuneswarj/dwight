using System;
using Dwight.Agent;
using Dwight.Llm;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Brief CO: a provider's limit, said by Dwight. Each body is the shape the provider documents (the URLs are on <see cref="ProviderLimit"/>), and the sentence is a claim, so a limit the provider set
  /// is never called the person's, and neither is told to wait a moment.
  /// </summary>
  public class ProviderLimitTests
  {
    private static readonly TimeZoneInfo India = TimeZoneInfo.CreateCustomTimeZone("India", TimeSpan.FromMinutes(330), "India Standard Time", "India Standard Time");

    private const string Tonight = "You have reached your specified API usage limits. You will regain access on 2026-11-01 at 00:00 UTC.";

    private static string AnthropicBody(string message) => "{\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\",\"message\":\"" + message + "\"},\"request_id\":\"req_1\"}";

    private static string OpenAiBody(string code, string type = "insufficient_quota") =>
      "{\"error\":{\"message\":\"the provider's words\",\"type\":\"" + type + "\",\"param\":null,\"code\":" + (code == null ? "null" : "\"" + code + "\"") + "}}";

    // ---- the sentence, as the brief pins it ------------------------------------------------------------------------

    /// <summary>Tonight's error, word for word: whose limit, that the canvas is unchanged, what to do, and the time in the person's own zone.</summary>
    [Fact]
    public void Tonights_error_says_whose_limit_what_changed_what_to_do_and_when_in_local_time()
    {
      var said = AnthropicRequestBody.DescribeError(400, AnthropicBody(Tonight), null, India);

      Assert.Equal(
        "Your Anthropic account has reached a usage limit you set in its console, so Anthropic refused this request. That request changed nothing on the canvas. " +
        "Raise the limit there, or wait: Anthropic says access returns at 05:30 on 1 November, India Standard Time (2026-11-01 at 00:00 UTC).\n" +
        "Anthropic said: " + Tonight, said);
    }

    /// <summary>A zone that is not this machine's or CI's, so a dropped zone cannot pass because the machine happens to be in the one expected.</summary>
    [Fact]
    public void The_zone_reaches_the_sentence()
    {
      var tokyo = TimeZoneInfo.CreateCustomTimeZone("Tokyo", TimeSpan.FromHours(9), "Tokyo Standard Time", "Tokyo Standard Time");

      Assert.Contains("access returns at 09:00 on 1 November, Tokyo Standard Time (2026-11-01 at 00:00 UTC).", AnthropicRequestBody.DescribeError(400, AnthropicBody(Tonight), null, tokyo));
    }

    /// <summary>The plugin's own default: this machine's zone, never UTC and never a guess.</summary>
    [Fact]
    public void With_no_zone_given_it_uses_the_machines()
    {
      Assert.Contains(ProviderLimit.ReturnsAt(Tonight, "Anthropic", TimeZoneInfo.Local), ProviderLimit.Sentence(ProviderLimitKind.SetByYou, "Anthropic", Tonight));
    }

    /// <summary>Nothing from the provider means no line saying what the provider said.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public void With_no_words_from_the_provider_there_is_no_said_line(string raw)
    {
      var said = ProviderLimit.Sentence(ProviderLimitKind.SetByYou, "Anthropic", raw);

      Assert.DoesNotContain("said:", said);
      Assert.DoesNotContain("\n", said);
    }

    [Fact]
    public void With_no_time_it_ends_at_try_again_later_and_no_time_is_guessed()
    {
      const string message = "You have reached your specified API usage limits.";

      var said = AnthropicRequestBody.DescribeError(400, AnthropicBody(message), null, India);

      Assert.Contains("Raise the limit there, or try again later.", said);
      Assert.DoesNotContain("returns at", said);
      Assert.EndsWith("Anthropic said: " + message, said);
    }

    [Theory]
    [InlineData("You will regain access on 2026-13-45 at 00:00 UTC.")]   // not a date
    [InlineData("You will regain access on 2026-11-01 at 99:00 UTC.")]   // not a time
    [InlineData("You will regain access on 2026-11-01 at 00:00.")]       // no zone said: UTC is not assumed
    [InlineData("You will regain access tomorrow.")]
    public void A_time_it_cannot_read_is_not_converted(string detail)
    {
      Assert.Null(ProviderLimit.ReturnsAt(detail, "Anthropic", India));
    }

    /// <summary>The date moves with the zone, and the name follows the season.</summary>
    [Fact]
    public void The_conversion_uses_the_machines_zone_and_its_name_for_that_date()
    {
      var newYork = TimeZoneInfo.CreateCustomTimeZone("NY", TimeSpan.FromHours(-5), "Eastern Standard Time", "Eastern Standard Time", "Eastern Daylight Time",
        new[]
        {
          TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 10),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 3)),
        });

      Assert.Equal("Anthropic says access returns at 20:00 on 31 October, Eastern Daylight Time (2026-11-01 at 00:00 UTC).",
        ProviderLimit.ReturnsAt("on 2026-11-01 at 00:00 UTC", "Anthropic", newYork));
      Assert.Equal("Anthropic says access returns at 19:00 on 31 December, Eastern Standard Time (2027-01-01 at 00:00 UTC).",
        ProviderLimit.ReturnsAt("on 2027-01-01 at 00:00 UTC", "Anthropic", newYork));
    }

    [Fact]
    public void A_zone_with_no_name_says_its_offset()
    {
      var nameless = TimeZoneInfo.CreateCustomTimeZone("x", TimeSpan.FromMinutes(-210), "", "");

      Assert.Contains("20:30 on 31 October, UTC-03:30 (", ProviderLimit.ReturnsAt("on 2026-11-01 at 00:00 UTC", "Anthropic", nameless));
    }

    // ---- Anthropic ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Anthropics_workspace_limit_is_also_one_you_set()
    {
      var said = AnthropicRequestBody.DescribeError(400, AnthropicBody("You have reached your specified workspace API usage limits. You will regain access on 2026-11-01 at 00:00 UTC."), null, India);

      Assert.StartsWith("Your Anthropic account has reached a usage limit you set in its console", said);
    }

    /// <summary>The documented 429 for a tier's cap. It has no retry-after and "keeps failing until access resumes", so the rate line's advice is false, and the person did not set it.</summary>
    [Fact]
    public void A_usage_tiers_cap_is_not_called_yours_and_is_not_told_to_wait_a_moment()
    {
      const string body = "{\"type\":\"error\",\"error\":{\"type\":\"rate_limit_error\",\"message\":\"You have reached your API usage limits: your organization has crossed its monthly API usage threshold. " +
                          "You will regain access on 2026-09-01 at 00:00 UTC.\",\"details\":{\"error_code\":\"enforced_spend_limit_reached\"}},\"request_id\":\"req_018\"}";

      var said = AnthropicRequestBody.DescribeError(429, body, null, India);

      Assert.StartsWith("Your Anthropic account has reached a usage limit Anthropic sets on it, so Anthropic refused this request. That request changed nothing on the canvas. Trying again now will not help.", said);
      Assert.Contains("See its console for what can change, or wait: Anthropic says access returns at 05:30 on 1 September, India Standard Time (2026-09-01 at 00:00 UTC).", said);
      Assert.DoesNotContain("you set", said);
      Assert.DoesNotContain("Wait a moment", said);
    }

    [Fact]
    public void An_ordinary_429_keeps_the_rate_line_and_says_the_canvas_is_unchanged()
    {
      Assert.Equal("Claude is rate-limiting your key (429). Wait a moment and try again. " + ProviderLimit.NothingChanged,
        AnthropicRequestBody.DescribeError(429, "{\"type\":\"error\",\"error\":{\"type\":\"rate_limit_error\",\"message\":\"slow down\"}}"));
    }

    [Theory]
    [InlineData("messages: field required")]
    [InlineData("Your credit balance is too low to access the Anthropic API.")]   // not a limit they set, and not documented on the error pages: left as the provider's own words
    public void Any_other_400_stays_the_providers_error(string message)
    {
      Assert.Equal("Claude returned an error (400): " + message, AnthropicRequestBody.DescribeError(400, AnthropicBody(message)));
    }

    [Fact]
    public void The_limit_sentence_is_not_a_reason_to_blame_retention()
    {
      var said = AnthropicRequestBody.DescribeError(400, AnthropicBody(Tonight), "Fable needs 30-day data retention.", India);

      Assert.DoesNotContain("retention", said);
    }

    [Fact]
    public void A_body_that_is_not_json_is_no_class()
    {
      Assert.Equal(ProviderLimitKind.None, ProviderLimit.OfAnthropic(400, "<html>nope</html>"));
      Assert.Equal(ProviderLimitKind.None, ProviderLimit.OfAnthropic(400, null));
      Assert.Equal(ProviderLimitKind.None, ProviderLimit.OfOpenAIBody("OpenAI", "<html>nope</html>"));
      Assert.Equal(ProviderLimitKind.None, ProviderLimit.OfGoogle(429, "<html>nope</html>"));
    }

    // ---- OpenAI ------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("organization_spend_limit_exceeded", "SetByYou")]
    [InlineData("project_spend_limit_exceeded", "SetByYou")]
    [InlineData("billing_hard_limit_reached", "SetByYou")]
    [InlineData("organization_usage_limit_exceeded", "SetByProvider")]
    [InlineData("credit_balance_exhausted", "OutOfCredit")]
    [InlineData("insufficient_quota", "LimitOrCredit")]
    public void OpenAIs_codes_each_say_what_they_document(string code, string kind)
    {
      Assert.Equal(kind, ProviderLimit.OfOpenAIBody("OpenAI", OpenAiBody(code)).ToString());
    }

    [Fact]
    public void A_code_decides_over_the_broader_type_as_OpenAI_says_to_read_it()
    {
      Assert.Equal(ProviderLimitKind.SetByYou, ProviderLimit.OfOpenAIBody("OpenAI", OpenAiBody("project_spend_limit_exceeded", "insufficient_quota")));
      Assert.Equal(ProviderLimitKind.LimitOrCredit, ProviderLimit.OfOpenAIBody("OpenAI", OpenAiBody(null, "insufficient_quota")));
    }

    [Fact]
    public void OpenAIs_sentences_name_it_and_keep_its_words()
    {
      var yours = OpenAIProvider.DescribeError("OpenAI", 429, OpenAiBody("organization_spend_limit_exceeded"));
      Assert.StartsWith("Your OpenAI account has reached a usage limit you set in its console, so OpenAI refused this request. " + ProviderLimit.NothingChanged + " Raise the limit there, or try again later.", yours);
      Assert.EndsWith("\nOpenAI said: the provider's words", yours);

      Assert.StartsWith("Your OpenAI account has no credit left, so OpenAI refused this request. " + ProviderLimit.NothingChanged + " Add credit in its console, then try again.",
        OpenAIProvider.DescribeError("OpenAI", 429, OpenAiBody("credit_balance_exhausted")));
      Assert.StartsWith("Your OpenAI account has reached a usage limit or run out of credit, so OpenAI refused this request. " + ProviderLimit.NothingChanged + " See its console, then try again.",
        OpenAIProvider.DescribeError("OpenAI", 429, OpenAiBody("insufficient_quota")));
      Assert.StartsWith("Your OpenAI account has reached a usage limit OpenAI sets on it,", OpenAIProvider.DescribeError("OpenAI", 429, OpenAiBody("organization_usage_limit_exceeded")));
    }

    [Theory]
    [InlineData("slow_down", "rate_limit_error")]
    [InlineData("rate_limit_exceeded", "requests")]
    public void OpenAIs_rate_limits_keep_the_rate_line(string code, string type)
    {
      Assert.Equal("OpenAI is rate-limiting (429). Wait a moment and try again. " + ProviderLimit.NothingChanged, OpenAIProvider.DescribeError("OpenAI", 429, OpenAiBody(code, type)));
    }

    /// <summary>The Responses stream can fail the same way after a 200, and the plugin's line is the same.</summary>
    [Fact]
    public void A_failed_response_with_a_limit_code_says_the_same_sentence()
    {
      var error = System.Text.Json.Nodes.JsonNode.Parse("{\"code\":\"project_spend_limit_exceeded\",\"message\":\"the provider's words\"}");

      Assert.StartsWith("Your OpenAI account has reached a usage limit you set in its console", OpenAIResponses.FailedLine("OpenAI", error));
      Assert.StartsWith("OpenAI could not finish this reply: boom (server_error)",
        OpenAIResponses.FailedLine("OpenAI", System.Text.Json.Nodes.JsonNode.Parse("{\"code\":\"server_error\",\"message\":\"boom\"}")));
      Assert.StartsWith("Ollama Cloud could not finish this reply:", OpenAIResponses.FailedLine("Ollama Cloud", error));
    }

    // ---- Ollama ------------------------------------------------------------------------------------------------------

    /// <summary>Ollama speaks OpenAI's wire and documents no such code or limit, so a code on it means nothing here and its 429 is only what it documents.</summary>
    [Theory]
    [InlineData("Ollama Cloud")]
    [InlineData("Ollama (local)")]
    public void Ollama_is_never_told_it_set_a_limit(string label)
    {
      Assert.Equal(ProviderLimitKind.None, ProviderLimit.OfOpenAIBody(label, OpenAiBody("organization_spend_limit_exceeded")));
      Assert.Equal(label + " is rate-limiting (429). Wait a moment and try again. " + ProviderLimit.NothingChanged, OpenAIProvider.DescribeError(label, 429, OpenAiBody("organization_spend_limit_exceeded")));
    }

    // ---- Google ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Googles_daily_quota_is_a_limit_it_sets()
    {
      var said = GoogleProvider.DescribeError(429, "{\"error\":{\"code\":\"quota_exceeded\",\"message\":\"You have exceeded your daily quota.\"}}");

      Assert.StartsWith("Your Google account has reached a usage limit Google sets on it, so Google refused this request. " + ProviderLimit.NothingChanged, said);
      Assert.EndsWith("\nGoogle said: You have exceeded your daily quota.", said);
    }

    [Theory]
    [InlineData(402, "{\"error\":{\"code\":\"payment_required\",\"message\":\"Your Prepay credit balance is depleted.\"}}")]
    [InlineData(402, "{\"error\":{\"code\":402,\"message\":\"Your Prepay credit balance is depleted.\",\"status\":\"PAYMENT_REQUIRED\"}}")]
    [InlineData(402, "not json")]
    public void Googles_empty_prepay_balance_is_out_of_credit(int status, string body)
    {
      Assert.StartsWith("Your Google account has no credit left, so Google refused this request.", GoogleProvider.DescribeError(status, body));
    }

    [Theory]
    [InlineData("Your project has exceeded its monthly spend cap.")]
    [InlineData("Your project has exceeded its monthly spending cap.")]
    public void Googles_project_spend_cap_is_one_you_set(string message)
    {
      var said = GoogleProvider.DescribeError(429, "{\"error\":{\"code\":429,\"message\":\"" + message + "\",\"status\":\"RESOURCE_EXHAUSTED\"}}");

      Assert.StartsWith("Your Google account has reached a usage limit you set in its console, so Google refused this request.", said);
    }

    /// <summary>The documented RESOURCE_EXHAUSTED covers a per-minute limit too, and nothing documented tells them apart, so it is not called anyone's limit.</summary>
    [Fact]
    public void A_bare_resource_exhausted_keeps_the_rate_line()
    {
      Assert.Equal("Google is rate-limiting (429). Wait a moment and try again. " + ProviderLimit.NothingChanged,
        GoogleProvider.DescribeError(429, "{\"error\":{\"code\":429,\"message\":\"You exceeded your current quota, please check your plan and billing details.\",\"status\":\"RESOURCE_EXHAUSTED\"}}"));
    }

    // ---- how it reads in the panel -----------------------------------------------------------------------------------

    /// <summary>The loop's note says what the turn had, and this says why it stopped. The two read as one, with the provider's own words quieter beneath.</summary>
    [Fact]
    public void The_loops_note_and_the_sentence_read_as_one()
    {
      var said = AnthropicRequestBody.DescribeError(400, AnthropicBody(Tonight), null, India);
      var note = AgentLoopCore.NoteFor(TurnOutcome.Failed, null, hasText: true).TrimEnd() + " " + said;

      var html = ProviderLimit.NoteHtml(note);

      Assert.StartsWith("Dwight could not finish this turn. Here is what it had so far. Your Anthropic account has reached a usage limit you set in its console", html);
      Assert.EndsWith("<p class=\"hint\">Anthropic said: " + Tonight + "</p>", html);
    }

    [Fact]
    public void A_note_with_no_line_break_is_as_it_was_and_both_parts_are_escaped()
    {
      Assert.Equal("Claude &lt;b&gt;", ProviderLimit.NoteHtml("Claude <b>"));
      Assert.Equal("a &lt;b&gt;<p class=\"hint\">c &lt;d&gt;</p>", ProviderLimit.NoteHtml("a <b>\nc <d>"));
      Assert.Equal("", ProviderLimit.NoteHtml(null));
    }
  }
}
