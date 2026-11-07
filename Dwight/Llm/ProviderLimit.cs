using System;
using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Dwight.Llm
{
  /// <summary>Whose limit stopped a request, as far as the provider's own error says. The class decides the sentence, so it is only ever what the error documents.</summary>
  internal enum ProviderLimitKind
  {
    None = 0,

    /// <summary>A spend or usage limit the person set on their own account.</summary>
    SetByYou,

    /// <summary>A limit the provider sets on the account, such as a usage tier's monthly cap. Telling the person they set it would be false, and waiting a moment would not help.</summary>
    SetByProvider,

    /// <summary>The account has no prepaid credit left.</summary>
    OutOfCredit,

    /// <summary>The provider says quota and does not say which: a limit, or no credit.</summary>
    LimitOrCredit,
  }

  /// <summary>
  /// A PROVIDER'S LIMIT, SAID BY DWIGHT (Brief CO). At 00:33 on 1 November Anthropic returned 400 "You have reached your specified API usage limits. You will regain access on 2026-11-01 at 00:00 UTC.",
  /// and the person read that raw. Judged cold, they learned a limit was reached but not whose, not that the canvas was unchanged, not what to do, and got a time they had to convert.
  ///
  /// <para>MATCHED ON WHAT EACH PROVIDER DOCUMENTS, on a code where one exists and on the sentence only where none does. The class is a claim: "a limit you set" is false of a usage tier's cap, and "wait a
  /// moment" is false of both. An error that documents neither stays a plain error or a rate limit.</para>
  ///
  /// <para>The sentence and the provider's own words are separated by a line break, so a panel can show the second more quietly (<see cref="NoteHtml"/>) and a plain surface still reads both.</para>
  /// </summary>
  internal static class ProviderLimit
  {
    /// <summary>True in every state that reaches it. "Nothing on the canvas changed" would be false on a build turn that placed A1 and then hit the limit: it is this request that changed nothing.</summary>
    public const string NothingChanged = "That request changed nothing on the canvas.";

    /// <summary>Anthropic's "You will regain access on 2026-09-01 at 00:00 UTC", the one form any provider documents. Anything else is no time at all, and none is guessed.</summary>
    private static readonly Regex Returns = new Regex(@"\b(\d{4}-\d{2}-\d{2}) at (\d{1,2}:\d{2}) UTC\b", RegexOptions.CultureInvariant);

    private static JsonNode ErrorOf(string body)
    {
      try { return JsonNode.Parse(body ?? "")?["error"]; }
      catch { return null; }
    }

    private static string Text(JsonNode node)
    {
      try { return node?.GetValue<string>(); }
      catch { return null; }
    }

    /// <summary>
    /// Anthropic, https://platform.claude.com/docs/en/api/rate-limits. A limit you set: 400 invalid_request_error, with no code, whose message "starts with" one of two documented sentences.
    /// A usage tier's cap: 429 rate_limit_error with details.error_code enforced_spend_limit_reached.
    /// </summary>
    public static ProviderLimitKind OfAnthropic(int status, string body)
    {
      var error = ErrorOf(body);
      if (status == 400)
      {
        var message = Text(error?["message"]) ?? "";
        return message.StartsWith("You have reached your specified API usage limits", StringComparison.Ordinal) ||
               message.StartsWith("You have reached your specified workspace API usage limits", StringComparison.Ordinal)
          ? ProviderLimitKind.SetByYou
          : ProviderLimitKind.None;
      }
      return string.Equals(Text(error?["details"]?["error_code"]), "enforced_spend_limit_reached", StringComparison.Ordinal)
        ? ProviderLimitKind.SetByProvider
        : ProviderLimitKind.None;
    }

    /// <summary>
    /// OpenAI, https://developers.openai.com/api/docs/guides/error-codes, which says to read error.code for the cause because error.type "can still be insufficient_quota". billing_hard_limit_reached is
    /// not on that page and is the older name for a limit the person set, named in Brief CO. Only OpenAI itself: Ollama speaks this wire and has none of these.
    /// </summary>
    public static ProviderLimitKind OfOpenAICode(string code, string type)
    {
      switch (code)
      {
        case "organization_spend_limit_exceeded":
        case "project_spend_limit_exceeded":
        case "billing_hard_limit_reached":
          return ProviderLimitKind.SetByYou;
        case "organization_usage_limit_exceeded":
          return ProviderLimitKind.SetByProvider;
        case "credit_balance_exhausted":
          return ProviderLimitKind.OutOfCredit;
        case "insufficient_quota":
          return ProviderLimitKind.LimitOrCredit;
      }
      return string.Equals(type, "insufficient_quota", StringComparison.Ordinal) ? ProviderLimitKind.LimitOrCredit : ProviderLimitKind.None;
    }

    public static ProviderLimitKind OfOpenAIBody(string label, string body)
    {
      if (!string.Equals(label, "OpenAI", StringComparison.Ordinal)) return ProviderLimitKind.None;
      var error = ErrorOf(body);
      return OfOpenAICode(Text(error?["code"]), Text(error?["type"]));
    }

    /// <summary>
    /// Google, https://ai.google.dev/gemini-api/docs/api-errors: quota_exceeded is the daily quota, and 402 (payment_required) an empty Prepay balance. A project's spend cap is documented on
    /// https://ai.google.dev/gemini-api/docs/billing with no error of its own, so it is matched on its words alone. A bare 429 RESOURCE_EXHAUSTED is documented for per-minute limits as well, and nothing
    /// documented tells them from a daily one, so it stays a rate limit.
    /// </summary>
    public static ProviderLimitKind OfGoogle(int status, string body)
    {
      var error = ErrorOf(body);
      var code = Text(error?["code"]);
      if (status == 402) return ProviderLimitKind.OutOfCredit;
      if (string.Equals(code, "quota_exceeded", StringComparison.Ordinal)) return ProviderLimitKind.SetByProvider;
      var message = Text(error?["message"]) ?? "";
      return status == 429 && (message.IndexOf("spend cap", StringComparison.OrdinalIgnoreCase) >= 0 || message.IndexOf("spending cap", StringComparison.OrdinalIgnoreCase) >= 0)
        ? ProviderLimitKind.SetByYou
        : ProviderLimitKind.None;
    }

    /// <summary>Dwight's sentence for the class, then the provider's own words on a line of their own, so nothing is hidden. Null for no class.</summary>
    public static string Sentence(ProviderLimitKind kind, string provider, string rawDetail, TimeZoneInfo zone = null)
    {
      var back = ReturnsAt(rawDetail, provider, zone ?? TimeZoneInfo.Local);
      string said;
      switch (kind)
      {
        case ProviderLimitKind.SetByYou:
          said = "Your " + provider + " account has reached a usage limit you set in its console, so " + provider + " refused this request. " + NothingChanged +
                 (back != null ? " Raise the limit there, or wait: " + back : " Raise the limit there, or try again later.");
          break;
        case ProviderLimitKind.SetByProvider:
          said = "Your " + provider + " account has reached a usage limit " + provider + " sets on it, so " + provider + " refused this request. " + NothingChanged +
                 " Trying again now will not help." + (back != null ? " See its console for what can change, or wait: " + back : " See its console for what can change.");
          break;
        case ProviderLimitKind.OutOfCredit:
          said = "Your " + provider + " account has no credit left, so " + provider + " refused this request. " + NothingChanged + " Add credit in its console, then try again.";
          break;
        case ProviderLimitKind.LimitOrCredit:
          said = "Your " + provider + " account has reached a usage limit or run out of credit, so " + provider + " refused this request. " + NothingChanged + " See its console, then try again.";
          break;
        default:
          return null;
      }
      return string.IsNullOrWhiteSpace(rawDetail) ? said : said + "\n" + provider + " said: " + rawDetail.Trim();
    }

    /// <summary>"Anthropic says access returns at 05:30 on 1 November, India Standard Time (2026-11-01 at 00:00 UTC)." The plugin's arithmetic on the provider's own time, or null when it gave none it can read.</summary>
    internal static string ReturnsAt(string rawDetail, string provider, TimeZoneInfo zone)
    {
      var m = Returns.Match(rawDetail ?? "");
      if (!m.Success) return null;
      if (!DateTime.TryParseExact(m.Groups[1].Value + " " + m.Groups[2].Value, "yyyy-MM-dd H:mm", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var utc))
        return null;

      var local = TimeZoneInfo.ConvertTimeFromUtc(utc, zone);
      var name = zone.IsDaylightSavingTime(local) ? zone.DaylightName : zone.StandardName;
      if (string.IsNullOrWhiteSpace(name))
      {
        var offset = zone.GetUtcOffset(local);
        name = "UTC" + (offset < TimeSpan.Zero ? "-" : "+") + offset.ToString(@"hh\:mm", CultureInfo.InvariantCulture);
      }
      return provider + " says access returns at " + local.ToString("HH:mm", CultureInfo.InvariantCulture) + " on " +
             local.ToString("d MMMM", CultureInfo.InvariantCulture) + ", " + name + " (" + m.Value + ").";
    }

    /// <summary>A note for the panel, with whatever follows its first line break shown more quietly.</summary>
    public static string NoteHtml(string note)
    {
      var at = (note ?? "").IndexOf('\n');
      if (at < 0) return WebUtility.HtmlEncode(note ?? "");
      return WebUtility.HtmlEncode(note.Substring(0, at)) + "<p class=\"hint\">" + WebUtility.HtmlEncode(note.Substring(at + 1)) + "</p>";
    }
  }
}
