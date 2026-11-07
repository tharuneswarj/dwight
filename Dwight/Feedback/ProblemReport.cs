using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Dwight.Feedback
{
  /// <summary>
  /// The facts a problem report may carry. This type IS the list: there is no field here for a definition,
  /// a component name, a value, a geometry, a file path, a prompt, a model reply or a key, so none of them
  /// can be handed to <see cref="ProblemReport.Compose"/> by accident (ADR-091: GH-free, so it is tested).
  /// </summary>
  internal sealed class ProblemFacts
  {
    public string DwightVersion;
    public string RhinoVersion;
    public string GrasshopperVersion;
    public string OsVersion;
    /// <summary>The provider's NAME only (for example "Anthropic"). Never a key, never a model override.</summary>
    public string Provider;
    /// <summary>The verb or chat step last run this session; null or empty reads as "none".</summary>
    public string LastAction;
    /// <summary>Text Dwight itself produced when something failed, newest last. At most three are shown.</summary>
    public IList<string> LastErrors;
  }

  /// <summary>
  /// Composes the text of a "Report a problem" message and the two links that carry it. Dwight sends nothing
  /// itself: the links open the user's browser or mail app with this text, and the user decides what to send.
  /// </summary>
  internal static class ProblemReport
  {
    /// <summary>A URL has a length limit in practice; past this the body is cut and says so.</summary>
    public const int MaxBodyChars = 6000;
    public const string TruncatedLine = "[truncated]";
    private const int MaxErrorChars = 300;
    private const int MaxErrors = 3;

    /// <summary>The block shown in the panel and sent in the report.</summary>
    public static string Compose(ProblemFacts f, string userText)
    {
      f = f ?? new ProblemFacts();
      var sb = new StringBuilder();
      var what = (userText ?? "").Trim();
      sb.AppendLine("What happened:");
      sb.AppendLine(what.Length == 0 ? "(not described)" : what);
      sb.AppendLine();
      sb.AppendLine("Diagnostics (computed by Dwight; no definition, names, values, geometry, file paths, prompts, replies or keys):");
      sb.AppendLine("- Dwight: " + Field(f.DwightVersion));
      sb.AppendLine("- Rhino: " + Field(f.RhinoVersion));
      sb.AppendLine("- Grasshopper: " + Field(f.GrasshopperVersion));
      sb.AppendLine("- OS: " + Field(f.OsVersion));
      sb.AppendLine("- Provider: " + Field(f.Provider));
      sb.AppendLine("- Last verb or chat step: " + (string.IsNullOrWhiteSpace(f.LastAction) ? "none" : Scrub(f.LastAction)));
      var errs = new List<string>();
      if (f.LastErrors != null)
        foreach (var e in f.LastErrors) if (!string.IsNullOrWhiteSpace(e)) errs.Add(Scrub(e));
      if (errs.Count == 0) sb.AppendLine("- Last errors: none");
      else
      {
        sb.AppendLine("- Last errors:");
        for (int i = Math.Max(0, errs.Count - MaxErrors); i < errs.Count; i++) sb.AppendLine("  - " + errs[i]);
      }
      return sb.ToString().TrimEnd();
    }

    /// <summary>The diagnostic block alone, without the user's text: what the panel shows read-only.</summary>
    public static string DiagnosticBlock(ProblemFacts f)
    {
      var all = Compose(f, "");
      var i = all.IndexOf("Diagnostics", StringComparison.Ordinal);
      return i < 0 ? all : all.Substring(i);
    }

    public static string IssueUrl(string repo, string title, string body) =>
      "https://github.com/" + (repo ?? "").Trim('/') + "/issues/new?template=bug_report.md&title=" + Encode(title) + "&body=" + Encode(Bound(body));

    /// <summary>Null when there is no address: the mail button must not exist without one.</summary>
    public static string MailUrl(string address, string title, string body)
    {
      if (string.IsNullOrWhiteSpace(address)) return null;
      return "mailto:" + Uri.EscapeDataString(address.Trim()).Replace("%40", "@") + "?subject=" + Encode(title) + "&body=" + Encode(Bound(body));
    }

    /// <summary>Which of the panel's two doors exist. The GitHub button needs a public repository; the mail button needs an address. Pure, so the
    /// rule is tested; the panel and its page read it and nothing else.</summary>
    public static void Doors(bool repoPublic, string email, out bool canIssue, out bool canMail)
    {
      canIssue = repoPublic;
      canMail = !string.IsNullOrWhiteSpace(email);
    }

    /// <summary>Cuts a body to <see cref="MaxBodyChars"/> and says so, on a line of its own.</summary>
    public static string Bound(string body)
    {
      body = body ?? "";
      if (body.Length <= MaxBodyChars) return body;
      return body.Substring(0, MaxBodyChars).TrimEnd() + "\n\n" + TruncatedLine;
    }

    /// <summary>RFC 3986 percent-encoding: unreserved characters stay, every other UTF-8 byte is %XX.</summary>
    public static string Encode(string s)
    {
      var sb = new StringBuilder();
      foreach (var b in Encoding.UTF8.GetBytes(s ?? ""))
      {
        var c = (char)b;
        if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '.' || c == '_' || c == '~') sb.Append(c);
        else sb.Append('%').Append(b.ToString("X2"));
      }
      return sb.ToString();
    }

    private static readonly Regex WinPath = new Regex(@"[A-Za-z]:[\/][^\s""'<>|]*", RegexOptions.Compiled);
    private static readonly Regex UncPath = new Regex(@"\\[^\s""'<>|]+", RegexOptions.Compiled);
    private static readonly Regex KeyLike = new Regex(@"\b(sk-[A-Za-z0-9_\-]{8,}|AIza[A-Za-z0-9_\-]{12,})", RegexOptions.Compiled);

    /// <summary>One line, no file paths, nothing shaped like a key, at most 300 characters. An error message
    /// can quote a path; the report must not carry it.</summary>
    public static string Scrub(string s)
    {
      s = Regex.Replace(s ?? "", @"\s+", " ").Trim();
      s = UncPath.Replace(s, "[path]");
      s = WinPath.Replace(s, "[path]");
      s = KeyLike.Replace(s, "[key]");
      if (s.Length > MaxErrorChars) s = s.Substring(0, MaxErrorChars) + "...";
      return s;
    }

    private static string Field(string s) => string.IsNullOrWhiteSpace(s) ? "unknown" : Scrub(s);
  }

  /// <summary>The last few errors Dwight itself produced this session: text only, in memory, gone when Rhino
  /// closes. Fed from the places the fail-safe guardrail already catches.</summary>
  internal static class LastErrors
  {
    private const int Keep = 3;
    private static readonly object Gate = new object();
    private static readonly List<string> Items = new List<string>();

    public static void Record(string text)
    {
      if (string.IsNullOrWhiteSpace(text)) return;
      lock (Gate)
      {
        Items.Add(ProblemReport.Scrub(text));
        while (Items.Count > Keep) Items.RemoveAt(0);
      }
    }

    public static IList<string> Snapshot() { lock (Gate) return new List<string>(Items); }
    public static void Clear() { lock (Gate) Items.Clear(); }
  }
}
