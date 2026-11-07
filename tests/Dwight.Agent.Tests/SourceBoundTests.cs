using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Phase-9 U1 (reads-unification, #382(9)): the agent's bound on a script component's source.
  ///
  /// The verbs' reader treats an oversized script as OPAQUE (to-script must reproduce the whole thing or
  /// nothing). The agent is a READER, not a reproducer, so it takes a bounded PREFIX with a visible marker
  /// and the totals - the ChatBudget discipline: truncate and say so, never drop. GH-free (ADR-091).
  /// </summary>
  public class SourceBoundTests
  {
    [Fact]
    public void UNDER_BOTH_CAPS_THE_SOURCE_IS_RETURNED_WHOLE_AND_UNMARKED()
    {
      var s = SourceBound.Trim("a = 1\nb = 2\n", "python", maxLines: 10, maxChars: 100);

      Assert.Equal("a = 1\nb = 2", s.Text);
      Assert.False(s.Truncated);
      Assert.Null(s.Omitted);
      Assert.Equal(2, s.TotalLines);
      Assert.Equal("python", s.Language);
    }

    [Fact]
    public void OVER_THE_LINE_CAP_IT_CUTS_AT_A_LINE_BOUNDARY_AND_STATES_WHAT_IT_OMITTED()
    {
      var src = "l1\nl2\nl3\nl4\nl5";
      var s = SourceBound.Trim(src, "python", maxLines: 2, maxChars: 1000);

      Assert.True(s.Truncated);
      Assert.StartsWith("l1\nl2\n", s.Text);
      Assert.DoesNotContain("l3", s.Text);
      Assert.Equal(5, s.TotalLines);
      Assert.Equal(src.Length, s.TotalChars);
      Assert.Contains("3 more lines", s.Omitted);
      Assert.Contains(src.Length + " chars in total", s.Omitted);
    }

    [Fact]
    public void THE_MARKER_IS_A_COMMENT_IN_THE_SCRIPTS_OWN_LANGUAGE()
    {
      // A marker that reads as code would be a line the model might "fix". A comment is inert in the
      // language it sits in, and it is VISIBLE - the model sees the cut where it happens.
      var py = SourceBound.Trim("a\nb\nc", "python", 1, 1000);
      var cs = SourceBound.Trim("a\nb\nc", "csharp", 1, 1000);

      Assert.EndsWith("# [Dwight cut this here: 2 more lines not shown; 5 chars in total]", py.Text);
      Assert.EndsWith("// [Dwight cut this here: 2 more lines not shown; 5 chars in total]", cs.Text);
    }

    [Fact]
    public void OVER_THE_CHAR_CAP_IT_STILL_CUTS_ON_A_LINE_BOUNDARY()
    {
      // Never mid-line: a half statement is worse than one fewer statement.
      var s = SourceBound.Trim("short\nthis line is long\nlast", "csharp", maxLines: 100, maxChars: 12);

      Assert.True(s.Truncated);
      Assert.StartsWith("short\n//", s.Text);
      Assert.DoesNotContain("this line", s.Text);
    }

    [Fact]
    public void CRLF_IS_NORMALISED_AND_EMPTY_INPUT_IS_NULL()
    {
      Assert.Equal("a\nb", SourceBound.Trim("a\r\nb\r\n", "csharp", 10, 100).Text);
      Assert.Null(SourceBound.Trim("", "csharp", 10, 100));
      Assert.Null(SourceBound.Trim("   \n  ", "csharp", 10, 100));
      Assert.Null(SourceBound.Trim(null, "csharp", 10, 100));
    }

    [Theory]
    [InlineData("RhinoCodePluginGH.Components.CSharpComponent", "csharp")]
    [InlineData("RhinoCodePluginGH.Components.Python3Component", "python")]
    [InlineData("RhinoCodePluginGH.Components.IronPython2Component", "ironpython")]
    [InlineData("GhPython.Component.ZuiPythonComponent", "ironpython")]
    [InlineData("ScriptComponents.Component_CSNET_Script", "csharp")]
    [InlineData("ScriptComponents.Component_VBNET_Script", "vb")]
    [InlineData("SomePlugin.WeirdScript", "script")]
    public void THE_LANGUAGE_IS_READ_OFF_THE_COMPONENTS_TYPE_NAME(string typeFullName, string expected)
    {
      // The names of the Rhino 8 script components are NOT referenceable at compile time (RhinoCodePluginGH
      // is a plugin), so this is a string rule - which is exactly why it is tested here and confirmed
      // against the loaded assembly by the deploy probe rather than trusted from memory (#137).
      Assert.Equal(expected, SourceBound.LanguageOf(typeFullName));
    }
  }
}
