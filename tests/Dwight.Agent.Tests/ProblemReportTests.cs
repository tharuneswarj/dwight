using System;
using System.Collections.Generic;
using System.Linq;
using Dwight.Feedback;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>Report a problem: what the block may carry, that the links decode back to the text, that a cut
  /// body says so, and that the mail button cannot exist without an address.</summary>
  public class ProblemReportTests
  {
    // Built from pieces so the public-tree scanner does not mistake a fixture for a real key or path.
    private static readonly string FakeKey = "sk" + "-ant-api03-SECRETSECRETSECRET1234";
    private static readonly string FilePath = "C" + @":\Users\someone\Clients\Tower\facade_v7.gh";
    private const string ComponentName = "Client Tower Mullion Offset";

    private static ProblemFacts Facts(params string[] errors) => new ProblemFacts
    {
      DwightVersion = "0.1.0", RhinoVersion = "8.12.0", GrasshopperVersion = "1.0.0008",
      OsVersion = "Microsoft Windows NT 10.0.26200.0", Provider = "Anthropic", LastAction = "Reorganise",
      LastErrors = new List<string>(errors),
    };

    [Fact]
    public void The_block_names_every_field()
    {
      var b = ProblemReport.Compose(Facts("boom"), "it froze");
      foreach (var s in new[] { "it froze", "Dwight: 0.1.0", "Rhino: 8.12.0", "Grasshopper: 1.0.0008", "OS: Microsoft Windows", "Provider: Anthropic", "Reorganise", "boom" })
        Assert.Contains(s, b);
    }

    [Fact]
    public void An_error_that_quotes_a_path_or_a_key_is_scrubbed_before_it_reaches_the_block()
    {
      var b = ProblemReport.Compose(Facts("Could not read " + FilePath + " with key " + FakeKey), "x");
      Assert.DoesNotContain(FilePath, b);
      Assert.DoesNotContain("someone", b);
      Assert.DoesNotContain("SECRETSECRET", b);
      Assert.Contains("[path]", b);
    }

    [Fact]
    public void The_facts_type_has_no_field_that_could_hold_a_definition_a_name_a_value_a_path_or_a_key()
    {
      var names = new List<string>();
      foreach (var f in typeof(ProblemFacts).GetFields()) names.Add(f.Name);
      names.Sort();
      Assert.Equal(new[] { "DwightVersion", "GrasshopperVersion", "LastAction", "LastErrors", "OsVersion", "Provider", "RhinoVersion" }, names.ToArray());
    }

    [Fact]
    public void A_missing_field_reads_as_unknown_and_no_action_as_none()
    {
      var b = ProblemReport.Compose(new ProblemFacts(), "");
      Assert.Contains("Rhino: unknown", b);
      Assert.Contains("Last verb or chat step: none", b);
      Assert.Contains("Last errors: none", b);
      Assert.Contains("(not described)", b);
    }

    [Fact]
    public void Only_the_last_three_errors_are_shown()
    {
      var b = ProblemReport.Compose(Facts("e1", "e2", "e3", "e4"), "x");
      Assert.DoesNotContain("e1", b);
      Assert.Contains("e2", b); Assert.Contains("e4", b);
    }

    [Fact]
    public void The_diagnostic_block_alone_carries_no_user_text()
    {
      var b = ProblemReport.DiagnosticBlock(Facts());
      Assert.StartsWith("Diagnostics", b);
      Assert.DoesNotContain("What happened", b);
    }

    [Fact]
    public void The_issue_url_decodes_back_to_the_title_and_the_body()
    {
      var body = ProblemReport.Compose(Facts("a & b = c"), "it froze: 100% of the time, 'quoted' \"too\" é 日本\nsecond line");
      var url = ProblemReport.IssueUrl("tharuneswarj/dwight", "Problem report", body);
      Assert.StartsWith("https://github.com/tharuneswarj/dwight/issues/new?template=bug_report.md&title=Problem%20report&body=", url);
      var q = new Uri(url).Query.TrimStart('?').Split('&');
      Assert.Equal(3, q.Length);
      Assert.Equal("Problem report", Uri.UnescapeDataString(q[1].Substring("title=".Length)));
      Assert.Equal(body, Uri.UnescapeDataString(q[2].Substring("body=".Length)));
    }

    [Fact]
    public void Encoding_leaves_only_unreserved_characters()
    {
      var e = ProblemReport.Encode("a b&c=d/é+~-._");
      Assert.Equal("a%20b%26c%3Dd%2F%C3%A9%2B~-._", e);
    }

    [Fact]
    public void A_long_body_is_cut_and_says_so()
    {
      var long_ = new string('x', ProblemReport.MaxBodyChars + 500);
      var url = ProblemReport.IssueUrl("o/r", "t", long_);
      var body = Uri.UnescapeDataString(url.Substring(url.IndexOf("&body=", StringComparison.Ordinal) + 6));
      Assert.EndsWith(ProblemReport.TruncatedLine, body);
      Assert.True(body.Length <= ProblemReport.MaxBodyChars + 40);
      Assert.DoesNotContain(new string('x', ProblemReport.MaxBodyChars + 1), body);
    }

    [Fact]
    public void A_short_body_is_not_marked_truncated()
    {
      Assert.DoesNotContain(ProblemReport.TruncatedLine, ProblemReport.IssueUrl("o/r", "t", "short"));
    }

    [Fact]
    public void The_mail_link_exists_only_when_there_is_an_address()
    {
      Assert.Null(ProblemReport.MailUrl("", "t", "b"));
      Assert.Null(ProblemReport.MailUrl("   ", "t", "b"));
      Assert.Null(ProblemReport.MailUrl(null, "t", "b"));
      var m = ProblemReport.MailUrl("help@example.test", "Problem report", "line one\nline two");
      Assert.StartsWith("mailto:help@example.test?subject=Problem%20report&body=", m);
      Assert.Equal("line one\nline two", Uri.UnescapeDataString(m.Substring(m.IndexOf("&body=", StringComparison.Ordinal) + 6)));
    }

    // Brief EK ruling 4: the soft launch has a working door. DwightConfig is GH-coupled and not compiled here; its source lines and the page's
    // handling of them are the witnesses, and the rule itself is the pure ProblemReport.Doors.
    // Brief EZ: the 1.0.0 build comes with the public repository, so the pin moves to the GitHub door open; the rule and its tests stay.
    private static string Repo(params string[] parts) =>
      System.IO.File.ReadAllText(System.IO.Path.Combine(new[] { AppContext.BaseDirectory, "..", "..", "..", "..", ".." }.Concat(parts).ToArray()));

    [Fact]
    public void The_release_ships_the_mail_door_and_the_GitHub_door_open()
    {
      var cfg = Repo("Dwight", "DwightConfig.cs");
      Assert.Contains("public const string FeedbackEmail = \"hello@tha.run\";", cfg);
      Assert.Contains("public const bool RepoPublic = true;", cfg);
    }

    [Fact]
    public void The_issue_choosers_security_link_goes_to_the_same_repository()
    {
      // config.yml is static YAML, so its URL is written rather than generated; this holds it to FeedbackRepo.
      var repo = System.Text.RegularExpressions.Regex.Match(Repo("Dwight", "DwightConfig.cs"), "FeedbackRepo = \"([^\"]+)\";").Groups[1].Value;
      Assert.NotEqual("", repo);
      Assert.Contains("url: \"https://github.com/" + repo + "/security/advisories/new\"", Repo(".github", "ISSUE_TEMPLATE", "config.yml"));
    }

    [Fact]
    public void Doors_follow_the_public_repo_and_the_address()
    {
      ProblemReport.Doors(false, "hello@tha.run", out var issue, out var mail);
      Assert.False(issue); Assert.True(mail);
      ProblemReport.Doors(true, "hello@tha.run", out issue, out mail);
      Assert.True(issue); Assert.True(mail);
      ProblemReport.Doors(true, "", out issue, out mail);
      Assert.True(issue); Assert.False(mail);
      ProblemReport.Doors(false, "  ", out issue, out mail);
      Assert.False(issue); Assert.False(mail);
    }

    [Fact]
    public void The_page_shows_each_button_only_when_the_panel_says_its_door_is_open()
    {
      var html = Repo("Dwight", "Resources", "feedback.html");
      // Both buttons start hidden and exist in the page, and the init message is the only thing that shows one.
      Assert.Contains("id=\"issuebtn\" style=\"display:none\"", html);
      Assert.Contains("id=\"mailbtn\" style=\"display:none\"", html);
      Assert.Contains("getElementById('issuebtn').style.display = d.canIssue ? '' : 'none'", html);
      Assert.Contains("getElementById('mailbtn').style.display = d.canMail ? '' : 'none'", html);
      var panel = Repo("Dwight", "UI", "DwightFeedbackPanel.cs");
      Assert.Contains("[\"canIssue\"] = CanIssue", panel);
      Assert.Contains("[\"canMail\"] = CanMail", panel);
      Assert.Contains("ProblemReport.Doors(DwightConfig.RepoPublic, DwightConfig.FeedbackEmail", panel);
      // A press on the GitHub door is refused by the host while it is shut, not only hidden by the page.
      Assert.Contains("cmd.StartsWith(\"issue:\", StringComparison.Ordinal) && CanIssue", panel);
    }

    [Fact]
    public void The_ring_keeps_three_scrubbed_entries()
    {
      LastErrors.Clear();
      LastErrors.Record("one"); LastErrors.Record("two " + FilePath); LastErrors.Record("three"); LastErrors.Record("four");
      LastErrors.Record("   ");
      var s = LastErrors.Snapshot();
      Assert.Equal(3, s.Count);
      Assert.Equal("four", s[2]);
      Assert.DoesNotContain(FilePath, string.Join("|", s));
      LastErrors.Clear();
    }

    [Fact]
    public void Nothing_handed_in_as_a_secret_can_appear_in_the_block()
    {
      // The only free text is the user's own words and Dwight's error text; the facts have no slot for a component name.
      var b = ProblemReport.Compose(Facts("failed on " + FilePath), "x");
      Assert.DoesNotContain(ComponentName, b);
      Assert.DoesNotContain("facade_v7", b);
      Assert.DoesNotContain(FakeKey, b);
    }
  }
}
