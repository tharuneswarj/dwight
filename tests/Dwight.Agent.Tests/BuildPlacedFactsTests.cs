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
  /// U26 (#916, #923): THE BUILDLINE SAYS WHAT WAS CHECKED, AND WHAT THE PLACED COMPONENTS HOLD.
  ///
  /// Four times "built and verified" stood over a region that was useless - an empty region, an invalid subsurface, a
  /// 240x scale error, 486 zero-length lines - and each was true about the trio and false about the work. The trio is a
  /// GATE; the line is a SENTENCE, and a sentence has to say what it knows. These tests hold the composer to that: no
  /// bare "verified" on any surface; new warnings and empty outputs on PLACED components named; one plugin fact per
  /// geometry output - count, and the total length, total area or bounding size the type has.
  ///
  /// The fixtures are the four witnesses, written as the facts the geometry reads' own reader would have produced.
  /// </summary>
  public class BuildPlacedFactsTests
  {
    private static GeoObject Line(double length, bool valid = true) =>
      new GeoObject { Type = "LineCurve", Length = length, Valid = valid, BboxMin = new[] { 0.0, 0, 0 }, BboxMax = new[] { length, 0, 0 } };

    private static GeoObject Brep(double area, double x, double y, double z) =>
      new GeoObject { Type = "Brep", Area = area, Valid = true, BboxMin = new[] { 0.0, 0, 0 }, BboxMax = new[] { x, y, z } };

    private static GeoObject Surface(double area, bool valid = true) =>
      new GeoObject { Type = "NurbsSurface", Area = area, Valid = valid, BboxMin = new[] { 0.0, 0, 0 }, BboxMax = new[] { 0.5, 0.05, 0 } };

    private static PlacedComponentFact Component(string type, int id, params PlacedOutputFact[] outputs) =>
      new PlacedComponentFact { Ref = type.ToLowerInvariant(), Type = type, Id = id, Outputs = outputs.ToList() };

    // ---- #923: 486 zero-length lines, which Grasshopper does not warn about ----------------------------------

    [Fact]
    public void THE_DIAGRID_WITNESS_486_lines_of_total_length_zero_is_said_in_so_many_words()
    {
      var measured = Enumerable.Range(0, 486).Select(_ => Line(0, valid: false)).ToList();
      var fact = BuildPlacedFacts.Aggregate("Line", 486, 486, measured, null);

      var line = BuildPlacedFacts.Line(Component("Line", 24, fact), "m");

      Assert.Equal("Line (24): Line: 486 lines (486 invalid), total length 0 m", line);
    }

    // ---- #916c: forty cells of 0.025 m2 on a 240 m2 facade --------------------------------------------------

    [Fact]
    public void THE_SCALE_WITNESS_total_area_is_summed_and_formatted_so_a_reader_sees_1_m2_beside_240()
    {
      var measured = Enumerable.Range(0, 40).Select(_ => Surface(0.025)).ToList();
      var fact = BuildPlacedFacts.Aggregate("Surface", 40, 40, measured, null);

      var text = BuildPlacedFacts.Geometry(fact, "m");

      Assert.Equal("Surface: 40 surfaces, total area 1.00 m2, bounds 0.5 x 0.05 x 0 m", text);
    }

    // ---- #916b: a warning on a component the build placed is the build's own -------------------------------

    [Fact]
    public void THE_SUBSURFACE_WITNESS_a_placed_components_own_warning_is_named_on_its_line()
    {
      var c = Component("Isotrim", 31, BuildPlacedFacts.Aggregate("S", 1, 1, new[] { Surface(0, valid: false) }, null));
      c.Warnings.Add("Subsurface is invalid because the trimming V-domain is singular");

      var line = BuildPlacedFacts.Line(c, "m");

      Assert.StartsWith("Isotrim (31): WARNING Subsurface is invalid because the trimming V-domain is singular; ", line);
      Assert.Contains("1 surface (1 invalid)", line);
    }

    // ---- #916a: three of four components producing nothing --------------------------------------------------

    [Fact]
    public void THE_EMPTY_REGION_WITNESS_empty_outputs_are_named_by_port()
    {
      var c = Component("Divide Domain2", 28,
        new PlacedOutputFact { Port = "S", Items = 0 });
      c.Warnings.Add("Failed to collect data");

      var line = BuildPlacedFacts.Line(c, "m");

      Assert.Equal("Divide Domain2 (28): WARNING Failed to collect data; empty output S", line);
    }

    [Fact]
    public void A_floating_param_that_holds_nothing_says_so_without_a_port_name()
    {
      var c = Component("Number", 9, new PlacedOutputFact { Port = null, Items = 0 });
      Assert.Equal("Number (9): empty - it holds nothing", BuildPlacedFacts.Line(c, null));
    }

    // ---- the ordinary good case, and the silence rule --------------------------------------------------------

    [Fact]
    public void A_loft_gets_one_fact_count_and_bounding_size()
    {
      var fact = BuildPlacedFacts.Aggregate("Loft", 1, 1, new[] { Brep(2950.4, 61.2, 24.1, 18.0) }, null);
      Assert.Equal("Loft: 1 brep, total area 2950 m2, bounds 61.2 x 24.1 x 18.0 m", BuildPlacedFacts.Geometry(fact, "m"));
    }

    [Fact]
    public void Points_have_neither_length_nor_area_so_their_fact_is_count_and_spread()
    {
      var pts = new[]
      {
        new GeoObject { Type = "Point", Valid = true, BboxMin = new[] { 0.0, 0, 0 }, BboxMax = new[] { 0.0, 0, 0 } },
        new GeoObject { Type = "Point", Valid = true, BboxMin = new[] { 19.5, 11.4, 0 }, BboxMax = new[] { 19.5, 11.4, 0 } },
      };
      var fact = BuildPlacedFacts.Aggregate("P", 2, 2, pts, null);
      Assert.Equal("P: 2 points, 19.5 x 11.4 x 0 m", BuildPlacedFacts.Geometry(fact, "m"));
    }

    [Fact]
    public void A_component_with_nothing_to_say_has_no_line()
    {
      // No warning, no empty output, no geometry (a Series holding numbers): silence, so the facts that matter stand out.
      var c = Component("Series", 12, new PlacedOutputFact { Port = "S", Items = 40, Geometry = 0 });
      Assert.Null(BuildPlacedFacts.Line(c, "m"));
    }

    [Fact]
    public void Mixed_geometry_on_one_output_is_counted_as_objects()
    {
      var fact = BuildPlacedFacts.Aggregate("G", 2, 2, new[] { Line(3), Brep(1, 1, 1, 1) }, null);
      Assert.Equal("object", fact.Kind);
      Assert.StartsWith("G: 2 objects", BuildPlacedFacts.Geometry(fact, null));
    }

    [Fact]
    public void Without_a_document_unit_no_unit_is_guessed()
    {
      var fact = BuildPlacedFacts.Aggregate("Line", 3, 3, new[] { Line(1), Line(2), Line(3) }, null);
      Assert.Equal("Line: 3 lines, total length 6.00", BuildPlacedFacts.Geometry(fact, null));
    }

    [Fact]
    public void A_bound_the_reader_applied_is_said_on_the_fact()
    {
      var fact = BuildPlacedFacts.Aggregate("L", 9000, 9000, new[] { Line(1) }, "measured on the first 5000 of 9000");
      Assert.EndsWith("(measured on the first 5000 of 9000)", BuildPlacedFacts.Geometry(fact, "m"));
    }

    // ---- bounds on the sentence itself ------------------------------------------------------------------------

    [Fact]
    public void Lines_are_capped_and_the_rest_is_counted()
    {
      var many = Enumerable.Range(1, BuildPlacedFacts.MaxLines + 3)
        .Select(i => Component("Line", i, BuildPlacedFacts.Aggregate("L", 1, 1, new[] { Line(1) }, null))).ToList();

      var lines = BuildPlacedFacts.Lines(many, "m");

      Assert.Equal(BuildPlacedFacts.MaxLines + 1, lines.Count);
      Assert.Equal("3 more components measured - read them with get_geometry_summary.", lines.Last());
    }

    [Fact]
    public void The_summary_is_bounded_and_says_it_was_cut()
    {
      var c = Component("Panel", 1);
      c.Warnings.Add(new string('w', 2000));
      var verdict = new JsonObject { ["newErrors"] = 0, ["placedFacts"] = BuildPlacedFacts.ToJson(new[] { c }) };

      var summary = BuildPlacedFacts.Summary(verdict);

      Assert.Equal(BuildPlacedFacts.MaxChars, summary.Length);
      Assert.EndsWith("...", summary);
    }

    // ---- what was checked, said instead of the word -----------------------------------------------------------

    [Fact]
    public void Checked_names_the_three_legs_and_counts_errors_rather_than_judging_them()
    {
      Assert.Equal("Checked: every declared component is placed and wired as declared; nothing that existed before has changed; the solution has no new errors. Not checked: whether the result is what you meant, or what the outputs hold.", BuildPlacedFacts.Checked(0));
      Assert.Equal("Checked: every declared component is placed and wired as declared; nothing that existed before has changed; the solution has 2 new errors. Not checked: whether the result is what you meant, or what the outputs hold.", BuildPlacedFacts.Checked(2));
      Assert.False(BuildPlacedFacts.SaysVerified(BuildPlacedFacts.Checked(0)));
    }

    [Fact]
    public void Checked_says_wired_only_when_wires_were_declared()
    {
      // Ninth gate, row 7 (1 November 2026): a lone Panel with no wires read "placed and wired as declared".
      Assert.StartsWith("Checked: every declared component is placed as declared; nothing that existed before has changed;", BuildPlacedFacts.Checked(0, 0));
      Assert.StartsWith("Checked: every declared component is placed and wired as declared; nothing that existed before has changed;", BuildPlacedFacts.Checked(0, 3));
      Assert.Equal(BuildPlacedFacts.Checked(2), BuildPlacedFacts.Checked(2, 1));
      Assert.DoesNotContain("wired", BuildPlacedFacts.Checked(0, 0));
      Assert.False(BuildPlacedFacts.SaysVerified(BuildPlacedFacts.Checked(0, 0)));
    }

    [Fact]
    public void The_summary_reads_the_verdicts_own_checked_sentence_so_a_lone_component_never_reads_wired()
    {
      // Tenth gate, row 1: Summary recomputed the sentence through the one-argument form and said "wired" of a lone Panel.
      var lone = BuildVerdict.Build(true, new List<int> { 7 }, 0, 0, Clean(), 1, 0, new JsonArray(), region: "Spot 1",
        placedFacts: BuildPlacedFacts.ToJson(new PlacedComponentFact[0]), units: "m");
      var summary = BuildPlacedFacts.Summary(lone);
      Assert.StartsWith("Checked: every declared component is placed as declared;", summary);
      Assert.DoesNotContain("wired", summary);
      var wired = BuildVerdict.Build(true, new List<int> { 7, 8 }, 1, 1, Clean(), 2, 0, new JsonArray(), region: "A1",
        placedFacts: BuildPlacedFacts.ToJson(new PlacedComponentFact[0]), units: "m");
      Assert.Contains("placed and wired as declared", BuildPlacedFacts.Summary(wired));
      // A verdict without the field falls back to the wire count it carries.
      var bare = new JsonObject { ["newErrors"] = 0, ["asked"] = new JsonObject { ["components"] = 1, ["wires"] = 0 } };
      Assert.DoesNotContain("wired", BuildPlacedFacts.Summary(bare));
    }

    [Fact]
    public void A_verdict_with_no_wires_asked_does_not_say_wired()
    {
      var lone = BuildVerdict.Build(true, new List<int> { 7 }, 0, 0, Clean(), 1, 0, new JsonArray(), region: "Spot 1");
      Assert.DoesNotContain("wired", lone["checked"].GetValue<string>());
      Assert.StartsWith("Checked: every declared component is placed as declared;", lone["checked"].GetValue<string>());
      var wired = BuildVerdict.Build(true, new List<int> { 7, 8 }, 1, 1, Clean(), 2, 0, new JsonArray(), region: "A1");
      Assert.Contains("placed and wired as declared", wired["checked"].GetValue<string>());
    }

    [Fact]
    public void The_verified_detector_is_a_real_instrument()
    {
      // #212: a detector that never fires has not been shown to look. The old sentence must trip it; the new ones must not.
      Assert.True(BuildPlacedFacts.SaysVerified("A1 built and verified (6 components)."));
      Assert.True(BuildPlacedFacts.SaysVerified("could not be VERIFIED"));
      Assert.False(BuildPlacedFacts.SaysVerified("unverified"));
      Assert.False(BuildPlacedFacts.SaysVerified(null));
    }

    // ---- the wire: through a REAL verdict and a REAL envelope, never a hand-made shape (#325, #649) --------------

    private static BuildPreservation.Result Clean() => BuildPreservation.Check(new FpDoc(), new FpDoc(), new BuildIdentity());

    private static JsonObject AppliedVerdict(IReadOnlyList<PlacedComponentFact> facts, int newErrors = 0) =>
      BuildVerdict.Build(true, new List<int> { 24 }, 1, 1, Clean(), 1, newErrors, new JsonArray(), region: "B2",
        placedFacts: BuildPlacedFacts.ToJson(facts), units: "m");

    [Fact]
    public void Facts_round_trip_through_the_verdicts_own_JSON()
    {
      var fact = BuildPlacedFacts.Aggregate("Line", 486, 486, Enumerable.Range(0, 486).Select(_ => Line(0, false)).ToList(), null);
      var before = Component("Line", 24, fact);
      before.Nick = "Struts";
      before.Warnings.Add("w");

      var after = BuildPlacedFacts.FromJson(AppliedVerdict(new[] { before })["placedFacts"]);

      var c = Assert.Single(after);
      Assert.Equal(24, c.Id);
      Assert.Equal("Struts", c.Nick);
      Assert.Equal(new[] { "w" }, c.Warnings);
      var o = Assert.Single(c.Outputs);
      Assert.Equal(486, o.Geometry);
      Assert.Equal(486, o.Invalid);
      Assert.Equal(0.0, o.TotalLength);
      Assert.Equal("line", o.Kind);
      Assert.Equal(BuildPlacedFacts.Line(before, "m"), BuildPlacedFacts.Line(c, "m"));
    }

    [Fact]
    public void An_applied_verdict_carries_checked_and_placedFacts_and_never_the_word()
    {
      var v = AppliedVerdict(new PlacedComponentFact[0]);

      Assert.Equal(BuildPlacedFacts.Checked(0), v["checked"].GetValue<string>());
      Assert.NotNull(v["placedFacts"]);
      Assert.Equal("m", v["units"].GetValue<string>());
      Assert.False(BuildPlacedFacts.SaysVerified(v["checked"].GetValue<string>()));
    }

    [Fact]
    public void A_reverted_verdict_carries_no_checked_sentence_because_nothing_stood()
    {
      var v = BuildVerdict.Build(false, new List<int> { 24 }, 1, 1, Clean(), 1, 0, new JsonArray(),
        placedFacts: BuildPlacedFacts.ToJson(new[] { Component("Line", 24) }));
      Assert.Null(v["checked"]);
      Assert.Null(v["placedFacts"]);
    }

    /// <summary>THE LINE THE USER READS, through the envelope the panel is actually handed: an applied verdict's note
    /// is what was checked plus the facts, so the buildline carries "486 lines, total length 0 m" with no code in the
    /// panel changing.</summary>
    [Fact]
    public void The_envelope_carries_the_facts_as_the_applied_builds_note()
    {
      var fact = BuildPlacedFacts.Aggregate("Line", 486, 486, Enumerable.Range(0, 486).Select(_ => Line(0, false)).ToList(), null);
      var envelope = ToolResult.Success(AppliedVerdict(new[] { Component("Line", 24, fact) })).ToJson();

      var env = BuildEnvelope.Classify(envelope, "{\"region\":\"B2\",\"components\":[{\"ref\":\"line\",\"type\":\"Line\"}]}");

      Assert.Equal(RegionOutcome.Built, env.Outcome);
      Assert.Equal("Checked: every declared component is placed and wired as declared; nothing that existed before has changed; the solution has no new errors. Not checked: whether the result is what you meant, or what the outputs hold. " +
                   "Line (24): Line: 486 lines (486 invalid), total length 0 m.", env.Note);

      var html = BuildProgress.LineHtml(env.Address, env.Outcome, env.Placed, env.Note, env.State, env.Reconnected);
      Assert.Contains("built - 1 component. One Ctrl-Z removes it.", html);
      Assert.Contains("486 lines (486 invalid), total length 0 m", html);
      Assert.False(BuildPlacedFacts.SaysVerified(html), html);
    }

    [Fact]
    public void A_build_standing_with_new_errors_counts_them_in_its_checked_sentence()
    {
      var c = Component("Loft", 7);
      c.Errors.Add("Insufficient curves for a loft.");
      var envelope = ToolResult.Success(AppliedVerdict(new[] { c }, newErrors: 1)).ToJson();

      var env = BuildEnvelope.Classify(envelope, "{\"region\":\"B2\"}");

      Assert.Equal(RegionOutcome.BuiltWithErrors, env.Outcome);
      Assert.StartsWith("Checked: every declared component is placed and wired as declared; nothing that existed before has changed; the solution has 1 new error. Not checked: whether the result is what you meant, or what the outputs hold. Loft (7): ERROR Insufficient curves for a loft.", env.Note);
    }

    /// <summary>THE MODEL'S COPY: the turn-start fact carries the same facts, so the next turn is told what the region
    /// holds rather than that it was "verified".</summary>
    [Fact]
    public void The_turn_start_fact_carries_the_facts_and_not_the_word()
    {
      var fact = BuildPlacedFacts.Aggregate("Line", 486, 486, Enumerable.Range(0, 486).Select(_ => Line(0, false)).ToList(), null);
      var env = BuildEnvelope.Classify(ToolResult.Success(AppliedVerdict(new[] { Component("Line", 24, fact) })).ToJson(), "{\"region\":\"B2\"}");

      var record = new BuildTurnRecord(new[] { "B2" });
      record.Record(env.Address, env.Outcome, env.Placed, env.Note, env.Reconnected, env.WouldCut);
      var text = BuildOutcomeFact.For(record, 1);

      Assert.Contains("B2 built (1 component). Checked: every declared component is placed and wired as declared; nothing that existed before has changed; the solution has no new errors. Not checked: whether the result is what you meant, or what the outputs hold. " +
                      "Line (24): Line: 486 lines (486 invalid), total length 0 m.", text);
      Assert.False(BuildPlacedFacts.SaysVerified(text.Replace("read from each build's own verdict", "")), text);
    }

    [Fact]
    public void No_surface_says_the_bare_word_any_more()
    {
      var line = BuildProgress.LineHtml("A1", RegionOutcome.Built, 6, BuildPlacedFacts.Checked(0));
      var close = BuildProgress.ClosingHtml(new[] { "A1" }, new Dictionary<string, RegionOutcome> { ["A1"] = RegionOutcome.Built }, false);
      var record = new BuildTurnRecord(new[] { "A1" });
      record.Record("A1", RegionOutcome.Built, 6, BuildPlacedFacts.Checked(0));

      Assert.False(BuildPlacedFacts.SaysVerified(line), line);
      Assert.False(BuildPlacedFacts.SaysVerified(close), close);
      Assert.False(BuildPlacedFacts.SaysVerified(BuildOutcomeFact.For(record, 1)
        .Replace("read from each build's own verdict", "")));
    }

    // ---- number formatting, for a person ----------------------------------------------------------------------

    [Theory]
    [InlineData(0, "0")]
    [InlineData(0.025, "0.025")]
    [InlineData(1.0, "1.00")]
    [InlineData(18.0, "18.0")]
    [InlineData(61.24, "61.2")]
    [InlineData(240.4, "240")]
    [InlineData(2950.4, "2950")]
    public void Numbers_read_at_the_precision_a_person_needs(double v, string expected) =>
      Assert.Equal(expected, BuildPlacedFacts.Fmt(v));

    // ---- U43 (#975): the spot's report carries the SAME checked sentence and per-component facts --------------------

    private static JsonObject SpotRecord() => (JsonObject)JsonNode.Parse(
      "{\"components\":[{\"ref\":\"n\",\"type\":\"Number\"},{\"ref\":\"c\",\"type\":\"Curve > Primitive > Circle\"}]}");

    [Fact]
    public void U43_The_spot_report_carries_the_checked_sentence_and_the_geometry_fact_through_the_one_composer()
    {
      var fact = BuildPlacedFacts.Aggregate("Line", 486, 486, Enumerable.Range(0, 486).Select(_ => Line(0, false)).ToList(), null);
      var applied = ToolResult.Success(AppliedVerdict(new[] { Component("Line", 24, fact) }));

      var report = SpotAsk.PlacedReport(SpotRecord(), "Spot 1", applied.Result);

      Assert.Equal("Placed Number, Curve > Primitive > Circle in Spot 1. " +
                   "Checked: every declared component is placed and wired as declared; nothing that existed before has changed; the solution has no new errors. Not checked: whether the result is what you meant, or what the outputs hold. " +
                   "Line (24): Line: 486 lines (486 invalid), total length 0 m.", report);
      // one sentence in one place: the region build's note and the spot report end in the identical composed text
      var env = BuildEnvelope.Classify(applied.ToJson(), "{\"region\":\"B2\"}");
      Assert.EndsWith(env.Note, report);
      Assert.False(BuildPlacedFacts.SaysVerified(report), report);
    }

    [Fact]
    public void U43_The_spot_report_names_new_errors_by_count()
    {
      var applied = ToolResult.Success(AppliedVerdict(new[] { Component("Line", 24) }, newErrors: 2));
      Assert.Contains("2 new errors", SpotAsk.PlacedReport(SpotRecord(), "Spot 1", applied.Result));
    }

    [Fact]
    public void U43_With_no_verdict_the_report_is_the_old_line_and_still_states_nothing_it_did_not_check()
    {
      Assert.Equal("Placed Number, Curve > Primitive > Circle in Spot 1.", SpotAsk.PlacedReport(SpotRecord(), "Spot 1"));
      Assert.Equal("Placed Number, Curve > Primitive > Circle in Spot 1.", SpotAsk.PlacedReport(SpotRecord(), "Spot 1", null));
    }

    /// <summary>The LOOSE approval makes no group, so the line names none - and still carries the address the saved conversation reads.</summary>
    [Fact]
    public void A_loose_placement_report_names_no_group_but_keeps_the_address()
    {
      var line = SpotAsk.PlacedReport(SpotRecord(), "Spot 1", null, false);
      Assert.Equal("Placed Number, Curve > Primitive > Circle loose on the canvas (Spot 1).", line);
      Assert.DoesNotContain(" in Spot", line);
      Assert.Contains("Spot 1", line);
    }
  }
}
