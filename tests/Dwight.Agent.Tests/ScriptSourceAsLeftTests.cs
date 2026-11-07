using System;
using System.Collections.Generic;
using Dwight.Agent;
using Dwight.Data;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// #779: THE AS-LEFT RECORD COULD NOT SEE A SCRIPT'S SOURCE, AND THAT WAS LIVE ON `main`.
  ///
  /// <c>build_region</c> already places any installed type, script components included - it simply cannot
  /// give them source. So: Dwight builds a region holding an empty Python component; the user writes two
  /// hundred lines into it; the user asks for that region to be rebuilt. The as-left token was composed
  /// from the fingerprint's value token (null for a script), the nickname and the held state (null for a
  /// script), so it was UNCHANGED - the component classified as ours-and-untouched and entered the removal
  /// set. It is #631 at source level, and it was reachable without U19 or U20.
  ///
  /// IT WAS NOT A SILENT DELETION, and the record should say so: <c>RebuildPreview</c> names each
  /// component on the card before anything is removed, so the user saw "Python 3 Script" in the removal
  /// list. That makes it a CONSENT defect rather than a silent-deletion one - better, and still the card
  /// being true and unhelpful at the one moment it matters, because it names the component and not that
  /// it holds the user's afternoon.
  ///
  /// THE TWO DIRECTIONS THESE TESTS PIN, and they are opposite on purpose:
  ///   - a source EDIT must read as CHANGED, so the component is adopted and left standing;
  ///   - a source we could NOT READ must also leave the component standing, by a different route - the
  ///     token is null, which this record's own doctrine treats as "we cannot tell", and both resolution
  ///     paths already fail toward LEAVE IT on a null (the grouped path compares it as changed; the loose
  ///     path excludes it from the claim). A digest that turned a read failure into "unchanged" would
  ///     convert a safety property into its opposite, quietly.
  /// </summary>
  public class ScriptSourceAsLeftTests
  {
    private static readonly Guid ScriptGuid = new Guid("77777777-0000-0000-0000-000000000001");

    private const string PythonType = "RhinoCodePluginGH.Components.Python3Component";

    /// <summary>The fingerprint's view of a script component whose source WAS read - the digest in the
    /// value token, exactly as <c>BehaviourFingerprint.ReadObject</c> composes it.</summary>
    private static FpObject Readable(string source) => new FpObject
    {
      Guid = ScriptGuid, TypeName = PythonType, ValueToken = SourceToken.Of(source),
    };

    /// <summary>The fingerprint's view of a script component whose source could NOT be read: the stable
    /// marker in the value token for the fingerprint's question, and the flag for this record's.</summary>
    private static FpObject Unreadable() => new FpObject
    {
      Guid = ScriptGuid, TypeName = PythonType,
      ValueToken = SourceToken.Unreadable, ScriptSourceUnreadable = true,
    };

    private static string Token(FpObject o) => AsLeftRecord.Of(o, "Python 3 Script", null);

    // ---- direction one: an edit is seen -------------------------------------------------------------

    /// <summary>
    /// THE DEFECT ITSELF. Dwight leaves an empty script; the user writes into it; the tokens must differ,
    /// or the rebuild removes their work with the card's blessing.
    /// </summary>
    [Fact]
    public void A_user_writing_into_an_empty_script_Dwight_placed_reads_as_changed()
    {
      var asLeft = Token(Readable(""));
      var now = Token(Readable("import math\na = math.pi * x * x\n"));

      Assert.NotNull(asLeft);
      Assert.NotNull(now);
      Assert.NotEqual(asLeft, now);
      Assert.False(AsLeftRecord.Unchanged(asLeft, now), "the user's script must read as adopted, not as Dwight's to remove");
    }

    /// <summary>A one-character edit is still an edit. The digest is exact, so there is no threshold below
    /// which a change to what the definition computes goes unnoticed.</summary>
    [Fact]
    public void A_one_character_edit_to_a_script_reads_as_changed()
    {
      Assert.False(AsLeftRecord.Unchanged(Token(Readable("a = x + 1")), Token(Readable("a = x + 2"))));
    }

    /// <summary>And the converse, or the feature is useless: a script nobody touched still reads as
    /// Dwight's, so a rebuild can still replace what it placed. A guard that called everything changed
    /// would be as wrong as one that called everything unchanged, and far more likely to be shipped.</summary>
    [Fact]
    public void An_untouched_script_still_reads_as_unchanged_so_rebuild_still_works()
    {
      var source = "a = x * 2\n";
      Assert.True(AsLeftRecord.Unchanged(Token(Readable(source)), Token(Readable(source))));
    }

    /// <summary>A line-ending round-trip is not an edit. A save, a copy between editors or a checkout with
    /// different autocrlf must not make Dwight disown what it placed - a guard that cries wolf on
    /// whitespace is one people learn to override.</summary>
    [Fact]
    public void A_line_ending_round_trip_is_not_an_edit()
    {
      Assert.True(AsLeftRecord.Unchanged(Token(Readable("a = 1\nb = 2\n")), Token(Readable("a = 1\r\nb = 2\r\n"))));
    }

    /// <summary>Dwight's own empty stub is a READABLE state, not an unreadable one, so it keeps a real
    /// token and stays rebuildable. Collapsing "empty" onto "cannot read" would have left every empty
    /// component Dwight placed permanently adopted - a live feature broken to fix a blind spot.</summary>
    [Fact]
    public void An_empty_script_Dwight_placed_keeps_a_real_token_and_stays_rebuildable()
    {
      var asLeft = Token(Readable(""));
      Assert.NotNull(asLeft);
      Assert.True(AsLeftRecord.Unchanged(asLeft, Token(Readable(""))));
    }

    // ---- direction two: a read failure lands on LEAVE IT ---------------------------------------------

    /// <summary>
    /// THE ARCHITECT'S REQUIRED TEST (#779). A script whose source cannot be read - locked, encrypted, or
    /// a Rhino whose script API has moved - has no honest as-left token, because its body is the thing
    /// most likely to have changed and the one thing we cannot see. Null is this record's existing
    /// vocabulary for "we cannot tell", and <see cref="AsLeftRecord.Unchanged"/> answers FALSE on it, so
    /// the component is adopted and LEFT STANDING.
    ///
    /// If this ever goes green the other way, a rebuild will remove a locked script on the strength of a
    /// token that never knew what was inside it.
    /// </summary>
    [Fact]
    public void A_script_whose_source_cannot_be_read_has_no_token_and_is_therefore_left_alone()
    {
      var asLeft = Token(Unreadable());
      Assert.Null(asLeft);
      Assert.False(AsLeftRecord.Unchanged(asLeft, Token(Unreadable())));
      Assert.False(AsLeftRecord.Unchanged(Token(Readable("a = 1")), Token(Unreadable())));
      Assert.False(AsLeftRecord.Unchanged(asLeft, Token(Readable("a = 1"))));
    }

    /// <summary>
    /// The failure direction holds however the read fails and in whichever order. A component that was
    /// readable when Dwight placed it and unreadable now - the file was password-protected since, or this
    /// Rhino lacks the plugin that could read it - must also be left alone rather than removed on the
    /// strength of the token we happen to still hold.
    /// </summary>
    [Fact]
    public void A_component_that_became_unreadable_after_Dwight_placed_it_is_left_alone()
    {
      Assert.False(AsLeftRecord.Unchanged(Token(Readable("a = 1")), Token(Unreadable())));
    }

    /// <summary>The flag is what carries the answer, not the type name: a component we could read is
    /// unaffected by the escape hatch, so an ordinary script is not swept into "cannot tell" by being a
    /// script.</summary>
    [Fact]
    public void Being_a_script_is_not_by_itself_a_reason_to_disown_a_component()
    {
      Assert.NotNull(Token(Readable("a = 1")));
    }

    /// <summary>Nothing else changed shape. A component that is not a script composes its token exactly as
    /// before, so #680's slider work is untouched by this change.</summary>
    [Fact]
    public void A_non_script_component_composes_its_token_exactly_as_before()
    {
      var slider = new FpObject
      {
        Guid = ScriptGuid, TypeName = "Number Slider", ValueToken = "slider:4",
        Inputs = new List<FpInput>(),
      };
      var token = AsLeftRecord.Of(slider, "Count", "slider:0..10@4/Integer/dp0");
      Assert.NotNull(token);
      Assert.True(AsLeftRecord.Unchanged(token, AsLeftRecord.Of(slider, "Count", "slider:0..10@4/Integer/dp0")));
    }
  }
}
