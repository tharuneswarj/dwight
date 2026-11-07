using System;
using System.Diagnostics;
using System.Text;
using Dwight.Data;
using Xunit;
using Xunit.Abstractions;

namespace Dwight.Data.Tests
{
  /// <summary>
  /// #779 - the script-source digest that closed a LIVE defect in the rebuild claim.
  ///
  /// The defect, so these tests are read as what they are rather than as a new feature's coverage:
  /// <c>BehaviourFingerprint</c>'s value token recognised sliders, panels, toggles, swatches and value
  /// lists and returned null for everything else, so a script component contributed nothing to it - and
  /// nothing to <c>AsLeftRecord</c>, which composes that token. <c>build_region</c> already places script
  /// components; it just cannot give them source. So Dwight could place an empty Python component, the
  /// user could write two hundred lines into it, and a rebuild would read that component as ours and
  /// untouched and put it in the removal set.
  ///
  /// Three properties are load-bearing and each has a test whose failure would be a real defect rather
  /// than a broken expectation: the normalisation (or the guard cries wolf on whitespace), the bound (or
  /// a token carries the user's code), and above all the EMPTY-VERSUS-UNREADABLE distinction, which is
  /// the one that decides which way a rebuild's claim fails.
  /// </summary>
  public class SourceTokenTests
  {
    private readonly ITestOutputHelper _out;
    public SourceTokenTests(ITestOutputHelper output) { _out = output; }

    private const string Script = "import math\n\ndef area(r):\n    return math.pi * r * r\n\na = area(x)\n";

    // ---- the normalisation: a line-ending round-trip is not a behaviour change ------------------------

    [Fact]
    public void The_same_script_with_CRLF_endings_digests_identically()
    {
      Assert.Equal(SourceToken.Of(Script), SourceToken.Of(Script.Replace("\n", "\r\n")));
    }

    [Fact]
    public void The_same_script_with_lone_CR_endings_digests_identically()
    {
      Assert.Equal(SourceToken.Of(Script), SourceToken.Of(Script.Replace("\n", "\r")));
    }

    [Fact]
    public void Leading_and_trailing_whitespace_does_not_change_the_digest()
    {
      Assert.Equal(SourceToken.Of(Script), SourceToken.Of("\n\n  " + Script + "  \n\t\n"));
    }

    /// <summary>The normalisation stops at the edges. Python's indentation IS the program, so a token that
    /// ignored it would bless a rewrite that changed what the definition computes - the exact failure this
    /// whole change exists to catch, reintroduced by over-normalising.</summary>
    [Fact]
    public void Indentation_inside_the_script_is_never_normalised_away()
    {
      Assert.NotEqual(SourceToken.Of("if x:\n    a = 1\n"), SourceToken.Of("if x:\n\ta = 1\n"));
    }

    // ---- it actually notices a change ----------------------------------------------------------------

    [Fact]
    public void A_changed_script_digests_differently()
    {
      Assert.NotEqual(SourceToken.Of(Script), SourceToken.Of(Script + "b = area(x) * 2\n"));
    }

    [Fact]
    public void A_one_character_change_digests_differently()
    {
      Assert.NotEqual(SourceToken.Of("a = x + 1"), SourceToken.Of("a = x + 2"));
    }

    // ---- THE DISTINCTION THAT DECIDES WHICH WAY THE CLAIM FAILS ---------------------------------------

    /// <summary>
    /// THE TEST THIS CHANGE EXISTS FOR. An EMPTY script is a real, readable state; a script we could not
    /// read is not a state at all. If those two shared a token, either Dwight's own empty stub would read
    /// as unreadable for ever - so the as-left record would call it adopted and no rebuild could ever
    /// replace it - or an unreadable script would read as an ordinary empty one and be removed. Both are
    /// live failures, in opposite directions, from one collapsed distinction.
    /// </summary>
    [Fact]
    public void An_empty_script_and_an_unreadable_one_never_share_a_token()
    {
      Assert.NotEqual(SourceToken.Unreadable, SourceToken.Of(""));
      Assert.NotEqual(SourceToken.Unreadable, SourceToken.Of("   \n  "));
    }

    /// <summary>An empty script and a written one must differ, which is the moment the user's two hundred
    /// lines become visible to the guard.</summary>
    [Fact]
    public void An_empty_script_and_a_written_one_never_share_a_token()
    {
      Assert.NotEqual(SourceToken.Of(""), SourceToken.Of(Script));
    }

    /// <summary>Null in, null out. A caller with no source is not a caller with an empty one, and
    /// conflating them is how "there is no code here" turns into "there is no component here".</summary>
    [Fact]
    public void Null_source_yields_no_token_at_all()
    {
      Assert.Null(SourceToken.Of(null));
    }

    /// <summary>The unreadable marker is STABLE across reads, and that is a decision about the fingerprint:
    /// an unstable one would abort Reorganise's byte-identical guard on any definition holding one locked
    /// script - breaking a working feature to report a change nobody made.</summary>
    [Fact]
    public void The_unreadable_marker_is_stable_across_reads()
    {
      Assert.Equal(SourceToken.Unreadable, SourceToken.Unreadable);
      Assert.False(string.IsNullOrEmpty(SourceToken.Unreadable));
    }

    // ---- the bound: the user's code never travels in a token ------------------------------------------

    /// <summary>A token is fixed-width whatever the script's length, so a 12,000-character script costs the
    /// token set exactly what a one-liner does - and, more importantly, no part of the user's code is
    /// carried in a token that ends up in a record, a payload or a log.</summary>
    [Fact]
    public void The_token_is_bounded_and_carries_none_of_the_source()
    {
      var big = new StringBuilder();
      for (var i = 0; i < 2000; i++) big.Append("secret_identifier_").Append(i).Append(" = ").Append(i).Append('\n');

      var small = SourceToken.Of("a = 1");
      var large = SourceToken.Of(big.ToString());

      Assert.Equal(small.Length, large.Length);
      Assert.DoesNotContain("secret_identifier", large, StringComparison.Ordinal);
      Assert.True(large.Length < 128, "a token must stay bounded: " + large.Length);
    }

    // ---- the cost, measured rather than asserted ------------------------------------------------------

    /// <summary>
    /// THE HEADLESS HALF OF #779's COST CONDITION, and it is deliberately not a benchmark assertion.
    ///
    /// The digest is added to a read the fingerprint performs over every object, twice per apply, so the
    /// architect required the cost measured before merge. This measures the half a headless worktree can
    /// honestly measure - the normalisation and the hash - over 40 script components of realistic size.
    /// THE HALF IT CANNOT MEASURE is the reflected read into RhinoCodePluginGH, which needs Rhino; that
    /// term was measured separately on a live canvas and both numbers are reported in the PR, each named
    /// as what it is.
    ///
    /// The assertion is a loose order-of-magnitude tripwire, not a timing gate: a figure tight enough to
    /// be meaningful would be tight enough to flake on a loaded CI runner, and a flaky guard is one people
    /// learn to rerun rather than read.
    /// </summary>
    [Fact]
    public void The_digest_cost_over_forty_scripts_is_measured_and_reported()
    {
      var sources = new string[40];
      for (var i = 0; i < sources.Length; i++)
      {
        var sb = new StringBuilder("# component ").Append(i).Append('\n');
        while (sb.Length < 2500) sb.Append("value_").Append(sb.Length).Append(" = math.sin(x * ").Append(i).Append(")\n");
        sources[i] = sb.ToString();
      }

      foreach (var s in sources) SourceToken.Of(s);   // warm: JIT and the SHA provider, not the measurement

      const int passes = 200;
      var sw = Stopwatch.StartNew();
      for (var p = 0; p < passes; p++)
        foreach (var s in sources) SourceToken.Of(s);
      sw.Stop();

      var perDoc = sw.Elapsed.TotalMilliseconds / passes;
      _out.WriteLine($"digest of 40 scripts (~2.5 KB each): {perDoc:F4} ms per ReadDoc-equivalent pass, " +
                     $"{perDoc / sources.Length * 1000:F2} us per script component");
      Assert.True(perDoc < 100, $"the digest term should be far under 100 ms per pass; measured {perDoc:F4} ms");
    }
  }
}
