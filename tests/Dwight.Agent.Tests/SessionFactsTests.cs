using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Dwight.Agent;
using Dwight.Data;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Phase-9 U23 (lane C), unit 2 - the session's inert facts: the units every geometry fact is in, and the
  /// installed plugins as an inventory. Two properties matter more than the fields:
  ///
  ///   1. NO RHINO DOCUMENT IS A REFUSAL, NEVER A DEFAULT - here and in the geometry reads, in one sentence. The
  ///      geometry reads used to measure against 0.001 when there was no document, and said nothing (#208).
  ///   2. WHAT IS WITHHELD IS STRUCTURAL: a plugin entry has exactly four fields, so a location, an email or an id
  ///      cannot ride along.
  /// </summary>
  public class SessionFactsTests
  {
    private static JsonObject Ok(ToolResult r)
    {
      Assert.True(r.Ok, r.ToJson());
      return (JsonObject)r.Result;
    }

    private static DocumentUnitsFact Mm() =>
      new DocumentUnitsFact { Units = "Millimeters", AbsoluteTolerance = 0.001, AngleToleranceDegrees = 1 };

    // ---- units ---------------------------------------------------------------------------------------

    [Fact]
    public void The_units_and_tolerances_are_sent_as_read()
    {
      var o = Ok(DocumentUnitsReport.Build(Mm()));

      Assert.Equal("Millimeters", o["units"].GetValue<string>());
      Assert.Equal(0.001, o["absoluteTolerance"].GetValue<double>());
      Assert.Equal(1.0, o["angleToleranceDegrees"].GetValue<double>());
    }

    /// <summary>WHICH document, in words (the architect's ruling) - and never its name or path.</summary>
    [Fact]
    public void The_document_is_described_never_named()
    {
      var o = Ok(DocumentUnitsReport.Build(Mm()));

      Assert.Equal(DocumentUnitsReport.DocumentText, o["document"].GetValue<string>());
      Assert.Contains("active document", o["document"].GetValue<string>());

      // The VALUES, not the serialised text: the serialiser escapes an apostrophe as ', and a backslash
      // found there would be the encoder's, not a path's.
      var values = o.Where(kv => kv.Value is JsonValue v && v.TryGetValue<string>(out _))
                    .Select(kv => kv.Value.GetValue<string>()).ToList();
      Assert.All(values, s => Assert.DoesNotContain("\\", s));
      Assert.All(values, s => Assert.DoesNotContain(".3dm", s, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The numbers are only useful if the model connects them to the geometry reads.</summary>
    [Fact]
    public void The_payload_says_what_the_units_apply_to()
    {
      var measures = Ok(DocumentUnitsReport.Build(Mm()))["measures"].GetValue<string>();

      Assert.Contains("geometry reads", measures);
      Assert.Contains("squared", measures);
      Assert.Contains("cubed", measures);
      Assert.Contains("tolerance", measures);
    }

    /// <summary>A custom unit's NAME is the user's text; it is not sent, and the payload says so rather than
    /// leaving the model to wonder what "CustomUnits" is.</summary>
    [Fact]
    public void A_custom_unit_says_its_name_is_not_read()
    {
      var o = Ok(DocumentUnitsReport.Build(new DocumentUnitsFact { Units = "CustomUnits", AbsoluteTolerance = 0.01 }));

      Assert.Contains("custom unit", o["measures"].GetValue<string>());
      Assert.DoesNotContain("custom unit", Ok(DocumentUnitsReport.Build(Mm()))["measures"].GetValue<string>());
    }

    [Fact]
    public void No_active_document_is_refused_never_defaulted()
    {
      var r = DocumentUnitsReport.Build(null);

      Assert.False(r.Ok);
      Assert.Equal(ToolErrors.NoDocument, r.Code);
      Assert.Equal(DocumentUnitsReport.NoActiveDocumentText, r.Message);
    }

    // ---- the tolerance the geometry reads measure against -------------------------------------------

    /// <summary>
    /// THE DEFECT (#208): with no active document the geometry reads measured intersections and nearness against
    /// 0.001 - a tolerance nobody set - and said nothing. Every reading that is not a real tolerance is the
    /// refusal: missing, thrown, zero, negative, not a number.
    /// </summary>
    [Fact]
    public void A_missing_or_unusable_tolerance_is_refused_never_defaulted()
    {
      var readings = new Func<double?>[]
      {
        () => null,
        () => throw new InvalidOperationException("no document"),
        () => 0,
        () => -0.01,
        () => double.NaN,
        null,
      };

      foreach (var read in readings)
      {
        Assert.False(DocumentUnitsReport.TryTolerance(read, out var tol, out var error));
        Assert.Equal(0, tol);
        Assert.Equal(DocumentUnitsReport.NoActiveDocumentText, error.Message);
      }
    }

    [Fact]
    public void A_real_tolerance_is_used_as_read()
    {
      Assert.True(DocumentUnitsReport.TryTolerance(() => 0.01, out var tol, out var error));
      Assert.Equal(0.01, tol);
      Assert.Null(error);
    }

    /// <summary>ONE SENTENCE FOR ONE FACT: the geometry reads and the units read refuse a missing document in the
    /// same words, with the same code - two wordings would be two answers to one question.</summary>
    [Fact]
    public void The_geometry_refusal_and_the_units_refusal_are_the_same_words()
    {
      DocumentUnitsReport.TryTolerance(() => null, out _, out var geometry);
      var units = DocumentUnitsReport.Build(null);

      Assert.Equal(units.Message, geometry.Message);
      Assert.Equal(units.Code, geometry.Code);
    }

    // ---- plugins -------------------------------------------------------------------------------------

    private static PluginFact Plugin(string name, int usedHere = 0, string version = "1.0", int components = 5) =>
      new PluginFact { Name = name, Version = version, Components = components, UsedHere = usedHere };

    private static PluginFact Core(string name, int components, int usedHere = 0) =>
      new PluginFact { Name = name, Core = true, Components = components, UsedHere = usedHere };

    private static JsonArray Listed(JsonObject o) => (JsonArray)o["plugins"];

    [Fact]
    public void Grasshoppers_own_libraries_are_one_entry()
    {
      var o = Ok(PluginInventoryReport.Build(new[] { Core("Curve", 100, 3), Core("Math", 80, 2), Plugin("Kangaroo2") }));

      Assert.Equal(2, o["core"]["libraries"].GetValue<int>());
      Assert.Equal(180, o["core"]["components"].GetValue<int>());
      Assert.Equal(5, o["core"]["usedHere"].GetValue<int>());
      Assert.Equal(1, o["installed"].GetValue<int>());
      Assert.Single(Listed(o));
      Assert.Equal("Kangaroo2", Listed(o)[0]["name"].GetValue<string>());
    }

    /// <summary>The plugins THIS definition depends on first - the question a user with a slow or broken file is
    /// usually asking - then the rest by name.</summary>
    [Fact]
    public void Plugins_used_here_come_first_then_by_name()
    {
      var o = Ok(PluginInventoryReport.Build(new[]
      {
        Plugin("Zebra"), Plugin("anemone", usedHere: 2), Plugin("Pufferfish", usedHere: 7), Plugin("Human"),
      }));

      Assert.Equal(new[] { "Pufferfish", "anemone", "Human", "Zebra" },
        Listed(o).Select(p => p["name"].GetValue<string>()).ToArray());
    }

    /// <summary>WHAT IS WITHHELD IS STRUCTURAL: each entry has exactly these fields, so a location, an author
    /// contact or an id cannot ride along - there is nowhere for one to go.</summary>
    [Fact]
    public void A_plugin_entry_has_exactly_four_fields()
    {
      var entry = (JsonObject)Listed(Ok(PluginInventoryReport.Build(new[] { Plugin("Kangaroo2", usedHere: 1) })))[0];

      Assert.Equal(new[] { "components", "name", "usedHere", "version" },
        entry.Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void A_plugin_that_gives_no_version_is_sent_without_one()
    {
      var entry = Listed(Ok(PluginInventoryReport.Build(new[] { Plugin("Kangaroo2", version: "  ") })))[0];

      Assert.Null(entry["version"]);
    }

    [Fact]
    public void Past_the_cap_the_omitted_note_counts_what_is_hidden_and_how_many_are_used_here()
    {
      var libs = Enumerable.Range(0, PluginInventoryReport.PluginCap + 3).Select(i => Plugin("P" + i.ToString("D3"))).ToList();
      libs[PluginInventoryReport.PluginCap + 2] = Plugin("P999");   // unused, sorts last
      var o = Ok(PluginInventoryReport.Build(libs));

      Assert.Equal(PluginInventoryReport.PluginCap, Listed(o).Count);
      Assert.StartsWith("3 more plugins are installed and not listed, none of them used in this definition", o["omitted"].GetValue<string>());
      Assert.Contains("kb_lookup", o["omitted"].GetValue<string>());
    }

    [Fact]
    public void Nothing_past_the_cap_says_nothing_about_omissions()
    {
      Assert.Null(Ok(PluginInventoryReport.Build(new[] { Plugin("Kangaroo2") }))["omitted"]);
    }

    /// <summary>What the list CANNOT contain is in the payload, so an absence is not read as "not installed".</summary>
    [Fact]
    public void The_payload_states_what_the_list_cannot_contain()
    {
      var bound = Ok(PluginInventoryReport.Build(new PluginFact[0]))["bound"].GetValue<string>();

      Assert.Contains(".rhp", bound);
      Assert.Contains("failed to load", bound);
    }

    // ---- one name per plugin -------------------------------------------------------------------------

    /// <summary>ONE RULE for what a plugin is called, used by get_component and by the inventory - a second copy
    /// is how one plugin ends up with two names.</summary>
    [Theory]
    [InlineData(null, PluginName.Unnamed)]
    [InlineData("", PluginName.Unnamed)]
    [InlineData("   ", PluginName.Unnamed)]
    [InlineData("Kangaroo2", "Kangaroo2")]
    public void A_plugin_is_called_by_one_rule(string raw, string called)
    {
      Assert.Equal(called, PluginName.Of(raw));
    }
  }
}
