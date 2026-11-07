using Dwight.Data;
using Xunit;

namespace Dwight.Data.Tests
{
  /// <summary>
  /// #779: THE READER'S OWN TESTS, and they exist because the first set of tests for this change could
  /// not fail.
  ///
  /// The digest's tests were written against hand-built <c>FpObject</c>s. When both guards were reverted
  /// to watch them go red - the discipline that says every new guard is seen to fail once - THIRTEEN OF
  /// FOURTEEN STAYED GREEN. They asserted a pure function sitting beside the reader while the reader,
  /// which is where the defect lived, had no test at all. #612 in this change's own suite, caught before
  /// it shipped rather than after it did.
  ///
  /// So the reader's decisions moved into <see cref="ScriptSourceReflect"/>, which needs no Grasshopper,
  /// and these drive them against STAND-IN TYPES SHAPED LIKE THE REAL COMPONENTS - a class answering
  /// <c>TryGetSource(out string)</c> as Rhino 8's script components do, and a class carrying a <c>Code</c>
  /// property as the legacy GhPython component does. Real reflection, over a real object, through the
  /// same code path the live canvas takes.
  ///
  /// WHAT THESE STILL CANNOT SEE, stated rather than implied: whether the REAL types are shaped the way
  /// the stand-ins are. A Rhino that renamed <c>TryGetSource</c> would pass every test here and read every
  /// script as unreadable. That is a question about the loaded assembly, and it is answered by the deploy
  /// probe and the runtime gate, never from memory (#137).
  /// </summary>
  public class ScriptSourceReflectTests
  {
    // ---- stand-ins, each shaped like the component it stands for ------------------------------------

    /// <summary>Rhino 8's C# / Python 3 / IronPython 2 components: TryGetSource(out string).</summary>
    private sealed class ModernScript
    {
      private readonly string _source;
      private readonly bool _ok;
      public ModernScript(string source, bool ok = true) { _source = source; _ok = ok; }
      public bool TryGetSource(out string source) { source = _source; return _ok; }
    }

    /// <summary>The legacy GhPython component: a plain settable Code property.</summary>
    private sealed class LegacyScript
    {
      public LegacyScript(string code) { Code = code; }
      public string Code { get; }
    }

    /// <summary>A component that answers neither - a locked or encrypted script, or a Rhino whose API has
    /// moved. Nothing can be read from it, and saying so is the whole point.</summary>
    private sealed class OpaqueScript
    {
      public int NotSource => 42;
    }

    /// <summary>A script whose source property THROWS - the case the architect named, because a throw now
    /// flows through the same paths a successful read does.</summary>
    private sealed class ThrowingScript
    {
      public string Code => throw new System.InvalidOperationException("locked");
    }

    // ---- the type rule -------------------------------------------------------------------------------

    [Theory]
    [InlineData("RhinoCodePluginGH.Components.Python3Component")]
    [InlineData("RhinoCodePluginGH.Components.CSharpComponent")]
    [InlineData("GhPython.Component.ZuiPythonComponent")]
    [InlineData("ScriptComponents.Component_CSNET_Script")]
    [InlineData("ScriptComponents.Component_VBNET_Script")]
    public void The_script_component_namespaces_are_recognised(string typeName)
    {
      Assert.True(ScriptSourceReflect.IsScriptTypeName(typeName));
    }

    /// <summary>And ordinary components are NOT, or every object on the canvas would pay for a reflected
    /// source read and every one of them would read as an unreadable script.</summary>
    [Theory]
    [InlineData("Grasshopper.Kernel.Special.GH_NumberSlider")]
    [InlineData("Grasshopper.Kernel.Special.GH_Panel")]
    [InlineData("Grasshopper.Kernel.Parameters.Param_GenericObject")]
    [InlineData("CurveComponents.Component_Loft")]
    [InlineData("")]
    [InlineData(null)]
    public void An_ordinary_component_is_not_a_script(string typeName)
    {
      Assert.False(ScriptSourceReflect.IsScriptTypeName(typeName));
    }

    // ---- the read: the distinction the whole change turns on -----------------------------------------

    [Fact]
    public void A_modern_script_component_is_read_through_TryGetSource()
    {
      Assert.True(ScriptSourceReflect.TryRead(new ModernScript("a = x * 2\n"), out var src));
      Assert.Equal("a = x * 2\n", src);
    }

    [Fact]
    public void A_legacy_script_component_is_read_through_its_Code_property()
    {
      Assert.True(ScriptSourceReflect.TryRead(new LegacyScript("import rhinoscriptsyntax as rs\n"), out var src));
      Assert.Equal("import rhinoscriptsyntax as rs\n", src);
    }

    /// <summary>
    /// THE ONE THAT MATTERS. An EMPTY script is a successful read of an empty script, not a failure to
    /// read - so Dwight's own empty stub keeps a real token and stays rebuildable, while a component we
    /// genuinely could not read is disowned. Collapsing these two is the defect in miniature.
    /// </summary>
    [Fact]
    public void An_empty_script_is_a_successful_read_and_an_unreadable_one_is_not()
    {
      Assert.True(ScriptSourceReflect.TryRead(new ModernScript(""), out var empty));
      Assert.Equal("", empty);

      Assert.False(ScriptSourceReflect.TryRead(new OpaqueScript(), out var none));
      Assert.Null(none);
    }

    /// <summary>A component that says "no" to TryGetSource has not been read, even though the call
    /// succeeded. The bool is the answer, never the out-parameter.</summary>
    [Fact]
    public void A_component_that_refuses_TryGetSource_has_not_been_read()
    {
      Assert.False(ScriptSourceReflect.TryRead(new ModernScript("a = 1", ok: false), out var src));
      Assert.Null(src);
    }

    /// <summary>A throwing property is a failed read, not an empty one. It is the case that decides
    /// whether a locked script gets removed or left alone, so it is pinned here rather than left to the
    /// catch-all.</summary>
    [Fact]
    public void A_source_property_that_throws_is_a_failed_read()
    {
      Assert.False(ScriptSourceReflect.TryRead(new ThrowingScript(), out var src));
      Assert.Null(src);
    }

    [Fact]
    public void A_null_object_is_a_failed_read()
    {
      Assert.False(ScriptSourceReflect.TryRead(null, out var src));
      Assert.Null(src);
    }

    // ---- and the decision the reader hands the token --------------------------------------------------

    /// <summary>
    /// The two halves joined: what the reader returns, turned into the token the fingerprint stores. This
    /// is the function the Grasshopper reader actually calls, so reverting the reader's branch now breaks
    /// a test rather than passing one.
    /// </summary>
    [Fact]
    public void The_read_result_decides_the_token()
    {
      var readable = ScriptSourceReflect.TryRead(new ModernScript("a = 1"), out var src1);
      var empty = ScriptSourceReflect.TryRead(new ModernScript(""), out var src2);
      var opaque = ScriptSourceReflect.TryRead(new OpaqueScript(), out var src3);

      Assert.Equal(SourceToken.Of("a = 1"), SourceToken.ForScript(readable, src1));
      Assert.Equal(SourceToken.Of(""), SourceToken.ForScript(empty, src2));
      Assert.Equal(SourceToken.Unreadable, SourceToken.ForScript(opaque, src3));

      Assert.NotEqual(SourceToken.ForScript(empty, src2), SourceToken.ForScript(opaque, src3));
    }
  }
}
