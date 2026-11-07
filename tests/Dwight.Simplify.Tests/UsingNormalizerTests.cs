using System;
using System.Linq;
using Dwight.Simplify;
using Xunit;

namespace Dwight.Simplify.Tests
{
  /// <summary>
  /// Phase-8 WP-0 (#141/#146): the USING-NORMALIZER. The Rhino 8 C# script component supplies NO implicit usings -
  /// instrumented, not assumed (#141 step 1: a bare body using List&lt;&gt;/Point3d FAILS; the same body with the
  /// explicit usings COMPILES). So the model must declare every namespace it needs, and it does so unreliably: in
  /// the ARM-2 arm, 12 of 28 build failures were outright missing a using, and 3 of 9 even in the oracle-off
  /// baseline. The plugin therefore OWNS the format contract (ADR-056): it unions a plugin-defined baseline set with
  /// whatever the model emits, on EVERY generation, at zero token cost - deleting the whole missing-using class.
  ///
  /// C# ONLY. Python is not preempted (#146) - it has no using mechanism and its own arm decides.
  /// </summary>
  public class UsingNormalizerTests
  {
    private static string[] UsingsOf(string s) =>
      s.Split('\n').Select(l => l.Trim())
       .Where(l => l.StartsWith("using ") && l.EndsWith(";"))
       .Select(l => l.Substring(6).TrimEnd(';').Trim())
       .ToArray();

    [Fact]
    public void A_bare_body_gains_the_whole_baseline_set()
    {
      var body = "var pts = new List<Point3d>();\na = pts.Count;";
      var outp = UsingNormalizer.Normalize(body, ScriptLanguage.CSharp);

      foreach (var ns in UsingNormalizer.CSharpBaseline) Assert.Contains(ns, UsingsOf(outp));
      Assert.Contains("var pts = new List<Point3d>();", outp);   // the body is preserved verbatim
    }

    [Fact]
    public void The_models_own_usings_are_UNIONED_not_replaced()
    {
      // a namespace the model needed that is NOT in our baseline must survive - we add, never subtract.
      var src = "using System.Text;\n\nvar sb = new StringBuilder();\na = sb.ToString();";
      var outp = UsingNormalizer.Normalize(src, ScriptLanguage.CSharp);

      Assert.Contains("System.Text", UsingsOf(outp));                       // the model's own is kept
      Assert.Contains("System.Collections.Generic", UsingsOf(outp));        // and the baseline is added
    }

    [Fact]
    public void A_using_the_model_already_declared_is_not_duplicated()
    {
      var src = "using System.Linq;\nusing System.Linq;\n\na = 1;";
      var outp = UsingNormalizer.Normalize(src, ScriptLanguage.CSharp);

      Assert.Equal(1, UsingsOf(outp).Count(u => u == "System.Linq"));
    }

    [Fact]
    public void The_body_is_never_left_with_stray_duplicate_using_lines()
    {
      // the model's using lines are lifted into the merged header, not left behind to be emitted twice.
      var src = "using Rhino.Geometry;\n\nvar p = new Point3d(1,2,3);\na = p.X;";
      var outp = UsingNormalizer.Normalize(src, ScriptLanguage.CSharp);

      Assert.Equal(1, UsingsOf(outp).Count(u => u == "Rhino.Geometry"));
      var afterHeader = string.Join("\n", outp.Split('\n').SkipWhile(l => l.Trim().StartsWith("using ") || l.Trim() == ""));
      Assert.DoesNotContain("using ", afterHeader);
    }

    [Fact]
    public void An_alias_or_static_using_is_preserved()
    {
      // `using X = Y;` and `using static X;` are not plain namespace imports - keep them, never dedupe them away.
      var src = "using static System.Math;\nusing Pt = Rhino.Geometry.Point3d;\n\na = Sqrt(4.0);";
      var outp = UsingNormalizer.Normalize(src, ScriptLanguage.CSharp);

      Assert.Contains("using static System.Math;", outp);
      Assert.Contains("using Pt = Rhino.Geometry.Point3d;", outp);
    }

    [Fact]
    public void Python_is_returned_untouched()
    {
      // #146: C# only. Python has no using mechanism and is not preempted - its arm decides.
      var src = "import math\na = math.sqrt(4)";
      Assert.Equal(src, UsingNormalizer.Normalize(src, ScriptLanguage.Python3));
    }

    [Fact]
    public void Null_and_empty_sources_never_throw()
    {
      Assert.NotNull(UsingNormalizer.Normalize(null, ScriptLanguage.CSharp));
      Assert.NotNull(UsingNormalizer.Normalize("", ScriptLanguage.CSharp));
    }

    [Fact]
    public void Normalizing_twice_is_idempotent()
    {
      // the gate may see an already-normalized source (a retry carries the previous text forward); it must not grow.
      var once = UsingNormalizer.Normalize("var x = new List<int>();\na = x.Count;", ScriptLanguage.CSharp);
      var twice = UsingNormalizer.Normalize(once, ScriptLanguage.CSharp);
      Assert.Equal(once, twice);
    }
  }
}
