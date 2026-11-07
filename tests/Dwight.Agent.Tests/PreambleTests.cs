using System;
using System.IO;
using System.Linq;
using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Brief BB (U104): the Full control script gets a plugin-written preamble. The model's first script reached the definition through
  /// <c>ghenv</c>, which does not exist under <c>RhinoCode.RunScript</c>; the plugin states the environment (the UsingNormalizer law).
  /// </summary>
  public class PreambleTests
  {
    private const string Shebang = "#! python 3\n";

    // ---- the preamble, byte-pinned ----------------------------------------------------------------------

    [Fact]
    public void The_preamble_is_five_lines_pinned_byte_for_byte()
    {
      Assert.Equal(5, FullControl.PreambleLineCount);
      Assert.Equal(FullControlTests.PreambleText, string.Join("\n", FullControl.PreambleLines) + "\n");
      Assert.Equal("# --- Dwight's preamble: these names are bound before your script runs ---", FullControl.PreambleLines[0]);
      Assert.Equal("import Rhino, Grasshopper, System", FullControl.PreambleLines[1]);
      Assert.Equal("rhdoc = Rhino.RhinoDoc.ActiveDoc", FullControl.PreambleLines[2]);
      Assert.Equal("ghdoc = Grasshopper.Instances.ActiveCanvas.Document if Grasshopper.Instances.ActiveCanvas else None", FullControl.PreambleLines[3]);
      Assert.Equal("# --- your script follows ---", FullControl.PreambleLines[4]);
    }

    [Fact]
    public void A_source_with_no_header_gets_the_shebang_then_the_preamble_then_itself_unchanged()
    {
      const string src = "print(rhdoc.Name)\n";
      Assert.Equal(Shebang + FullControlTests.PreambleText + src, FullControl.ExecutableSource(src));
    }

    [Fact]
    public void A_source_with_its_own_header_keeps_it_first_and_gets_the_preamble_after_it()
    {
      const string src = "#! python 3\nprint(1)\nprint(2)";
      var exe = FullControl.ExecutableSource(src);
      Assert.Equal(Shebang + FullControlTests.PreambleText + "print(1)\nprint(2)", exe);
      Assert.StartsWith("#! python 3\n", exe);
    }

    [Theory]
    [InlineData("print(1)")]
    [InlineData("#! python 3\nprint(1)")]
    [InlineData("#!python3\nx = 1\ny = 2\n")]
    [InlineData("#! python 3")]
    public void ExecutableSource_is_idempotent(string src)
    {
      var once = FullControl.ExecutableSource(src);
      Assert.Equal(once, FullControl.ExecutableSource(once));
      Assert.Equal(1, Count(once, "# --- Dwight's preamble"));
    }

    [Fact]
    public void A_header_alone_still_gets_the_preamble()
    {
      Assert.Equal(Shebang + string.Join("\n", FullControl.PreambleLines), FullControl.ExecutableSource("#! python 3"));
    }

    [Fact]
    public void The_models_own_binding_of_ghdoc_comes_later_and_so_wins()
    {
      var exe = FullControl.ExecutableSource("ghdoc = None\nprint(ghdoc)");
      Assert.True(exe.IndexOf("ghdoc = Grasshopper.Instances", StringComparison.Ordinal) < exe.IndexOf("ghdoc = None", StringComparison.Ordinal));
      Assert.EndsWith("ghdoc = None\nprint(ghdoc)", exe);
    }

    [Fact]
    public void The_grant_still_compares_the_approved_text_never_the_executable_one()
    {
      Assert.Null(FullControl.FirstDifference("print(1)", "print(1)"));
      Assert.NotNull(FullControl.FirstDifference("print(1)", FullControl.ExecutableSource("print(1)")));
    }

    // ---- the positions are the user's --------------------------------------------------------------------

    [Theory]
    [InlineData(7, 1)]     // no header of its own: the shebang and five preamble lines come first
    [InlineData(11, 5)]
    public void Without_its_own_header_the_runtimes_line_minus_six_is_the_users(int runtime, int user)
    {
      Assert.Equal(user, FullControl.UserLine("a = 1\nb = 2\nc = 3\nd = 4\ne = 5", runtime));
    }

    [Theory]
    [InlineData(1, 1)]     // the model's own header
    [InlineData(7, 2)]     // line 2 of the source on the card
    [InlineData(10, 5)]
    public void With_its_own_header_the_runtimes_line_minus_five_is_the_users_after_the_header(int runtime, int user)
    {
      Assert.Equal(user, FullControl.UserLine("#! python 3\na = 1\nb = 2\nc = 3\nd = 4", runtime));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(6)]
    public void A_position_inside_the_plugins_lines_has_no_user_line_without_a_header_of_its_own(int runtime)
    {
      Assert.Null(FullControl.UserLine("a = 1", runtime));   // the shebang Dwight added and the five preamble lines
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(6)]
    public void A_position_inside_the_preamble_has_no_user_line_after_the_models_own_header(int runtime)
    {
      Assert.Null(FullControl.UserLine("#! python 3\na = 1", runtime));
    }

    [Fact]
    public void The_exception_text_carries_the_users_line_or_says_it_is_in_the_preamble()
    {
      const string approved = "a = 1\nb = 2\nc = 3\nd = 4\nz = 1 / 0";
      Assert.Equal("ExecuteException: division by zero (at [5:1])", FullControl.UserPositions("ExecuteException: division by zero (at [11:1])", approved));
      Assert.Equal("ExecuteException: boom (in Dwight's preamble)", FullControl.UserPositions("ExecuteException: boom (at [3:4])", approved));

      const string withHeader = "#! python 3\na = 1\nz = 1 / 0";
      Assert.Equal("ExecuteException: division by zero (at [3:1])", FullControl.UserPositions("ExecuteException: division by zero (at [8:1])", withHeader));
      Assert.Equal("ExecuteException: no position", FullControl.UserPositions("ExecuteException: no position", approved));
      Assert.Null(FullControl.UserPositions(null, approved));
      Assert.Equal("(in Dwight's preamble)", FullControl.InPreamble);
    }

    [Fact]
    public void The_route_rewrites_the_exception_after_the_run_and_hands_the_runtime_the_executable_source()
    {
      var gh = File.ReadAllText(Path.Combine(RepoRoot(), "Dwight", "Agent", "GhRunScript.cs"));
      Assert.Contains("route.Run(FullControl.ExecutableSource(source), out var stdout, out var stderr, out var exception);", gh);
      Assert.Contains("exception = FullControl.UserPositions(exception, source);", gh);
    }

    // ---- the card, the descriptor and the docs say so ------------------------------------------------------

    [Fact]
    public void The_card_summary_gains_the_clause_and_a_fixed_note_follows_the_fold()
    {
      const string src = "print(ghdoc)";
      var html = ConsentCard.RunScriptHtml("why", "tok", src, expanded: true);

      Assert.Contains("Touches the Rhino document: no. " + System.Net.WebUtility.HtmlEncode("Runs with Dwight's preamble: rhdoc and ghdoc bound.") + "</summary>", html);
      Assert.Equal("Runs with Dwight's preamble: rhdoc and ghdoc bound.", FullControl.PreambleSummaryClause);

      Assert.Equal("Dwight adds five lines before your script that import Rhino, Grasshopper and System and bind rhdoc (the Rhino document) " +
                   "and ghdoc (this definition). Nothing else is added.", FullControl.PreambleNote);
      Assert.Contains(System.Net.WebUtility.HtmlEncode(FullControl.PreambleNote), html);

      // The fold is the approved source, byte for byte, and does not contain the preamble.
      Assert.Contains("<pre class=\"consent-source\">" + src + "</pre></details>", html);
      Assert.DoesNotContain("these names are bound before your script runs", html);
      Assert.True(html.IndexOf("</details>", StringComparison.Ordinal) < html.IndexOf("Dwight adds five lines", StringComparison.Ordinal));
    }

    [Fact]
    public void The_descriptor_states_the_environment_in_one_sentence_after_the_report_sentence()
    {
      var d = FullControl.Descriptor().Description;
      Assert.Equal("The script runs in Rhino's Python 3 runtime, not inside a script component: there is no ghenv and no scriptcontext; " +
                   "rhdoc is the Rhino document and ghdoc is this definition, bound by Dwight before your script; print goes to the report.",
                   FullControl.EnvironmentSentence);
      Assert.Contains("it reports what changed. " + FullControl.EnvironmentSentence + " Prefer the build tools", d);
    }

    [Fact]
    public void The_public_docs_state_the_same_facts_in_the_users_words()
    {
      var build = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "public", "build.md"));
      Assert.Contains("Dwight adds five lines before your script", build);
      Assert.Contains("rhdoc", build);
      Assert.Contains("ghdoc", build);
      Assert.Contains("no ghenv", build);

      var log = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "public", "changelog.md"));
      Assert.Contains("rhdoc (the Rhino document) and ghdoc (this definition)", log);
    }

    private static int Count(string text, string needle)
    {
      var n = 0;
      for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
      return n;
    }

    private static string RepoRoot()
    {
      var dir = new DirectoryInfo(AppContext.BaseDirectory);
      while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Dwight.sln"))) dir = dir.Parent;
      Assert.True(dir != null, "the repository root (Dwight.sln) could not be found from " + AppContext.BaseDirectory);
      return dir.FullName;
    }
  }
}
