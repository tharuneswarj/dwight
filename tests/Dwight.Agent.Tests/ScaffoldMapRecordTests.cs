using System.Linq;
using System.Text.Json.Nodes;
using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// The stored map and the conversation's account of it (Stage D).
  ///
  /// Two readers, two forms: WP-4 builds from the stored plan, and a person reads the summary. The tests
  /// hold what each needs - that the plan survives WITH its addresses, and that the summary tells the
  /// truth about what landed, including the parts a user would otherwise have to notice for themselves.
  /// </summary>
  public class ScaffoldMapRecordTests
  {
    private const string Args = @"{""regions"":[
      {""ref"":""prep"",""name"":""Prepare curves"",""purpose"":""clean and divide"",
       ""pseudo"":[""divide each curve"",""sort by parameter""],
       ""inputs"":[""curves from Divide (42)""],""outputs"":[""point grid""]},
      {""ref"":""roof"",""name"":""Loft the roof"",""purpose"":""loft the grid"",""dependsOn"":[""prep""]}]}";

    /// <summary>
    /// A stand-in for what <c>GhProposeScaffold.Verdict</c> writes. Updated at WP-4 Stage A to the v2
    /// shape: every region carries its <c>ref</c> (the join key, #355) and expresses <c>dependsOn</c> in
    /// ADDRESSES rather than in the model's ref namespace.
    ///
    /// A hand-written fixture standing in for a producer's output can drift from it silently - the #248
    /// class - so the assertions below check the PROPERTIES the producer is now contracted to provide,
    /// and <c>ScaffoldMapReadTests.What_Build_writes_is_what_Read_joins</c> holds the two ends together.
    /// </summary>
    private const string Verdict = @"{""ok"":true,""result"":{
      ""applied"":true,""preserved"":true,
      ""regions"":[
        {""ref"":""prep"",""key"":""A1"",""name"":""Prepare curves"",""group"":""A1 - Prepare curves"",""dependsOn"":[]},
        {""ref"":""roof"",""key"":""B1"",""name"":""Loft the roof"",""group"":""B1 - Loft the roof"",""dependsOn"":[""A1""]}]}}";

    // ---- the stored map ----------------------------------------------------------------------------

    [Fact]
    public void The_stored_map_keeps_the_plan_and_the_addresses_together()
    {
      // Either half alone is useless to WP-4: the plan has no addresses to build BY, and the addresses
      // have nothing to build.
      var stored = (JsonObject)JsonNode.Parse(ScaffoldMapRecord.Build(Args, Verdict));

      // The version is asserted against the CONSTANT, not a literal: the number is the producer's own
      // contract, and a magic 1 here is a number somebody has to chase every time the format moves.
      Assert.Equal(ScaffoldMapRecord.CurrentMapVersion, stored["mapVersion"].GetValue<int>());
      Assert.Equal(2, stored["plan"]["regions"].AsArray().Count);
      Assert.Equal(2, stored["assigned"].AsArray().Count);
      Assert.Equal("A1", stored["assigned"][0]["key"].GetValue<string>());

      // #355, AND THIS IS WHAT MAKES THE TWO HALVES "TOGETHER" RATHER THAN MERELY ADJACENT: every
      // assigned region carries the REF that matches it to its plan entry, and states its dependencies in
      // ADDRESSES - the namespace the addresses themselves are in. Without the ref the halves sat in one
      // file and could not be joined; without the address translation the assigned half described its own
      // dependency graph in a namespace it could not resolve.
      foreach (var region in stored["assigned"].AsArray())
        Assert.False(string.IsNullOrEmpty(region["ref"]?.GetValue<string>()));

      var b1 = stored["assigned"].AsArray().Single(r => r["key"].GetValue<string>() == "B1");
      Assert.Equal(new[] { "A1" }, b1["dependsOn"].AsArray().Select(d => d.GetValue<string>()).ToArray());
    }

    [Fact]
    public void The_stored_plan_keeps_what_the_body_scribble_did_not_draw()
    {
      // The scribble is bounded (#238); the stored plan is not. This is the half that makes that bound
      // safe - the pseudo-code and the declared boundaries survive for the builder.
      var stored = (JsonObject)JsonNode.Parse(ScaffoldMapRecord.Build(Args, Verdict));
      var first = stored["plan"]["regions"][0];

      Assert.Equal(2, first["pseudo"].AsArray().Count);
      Assert.Equal("curves from Divide (42)", first["inputs"][0].GetValue<string>());
      Assert.Equal("point grid", first["outputs"][0].GetValue<string>());
    }

    [Fact]
    public void A_malformed_half_still_produces_a_readable_record()
    {
      // Never throw on the bookkeeping path: a map that landed and verified must not be un-recorded
      // because its envelope surprised the recorder.
      var stored = (JsonObject)JsonNode.Parse(ScaffoldMapRecord.Build("not json", "also not json"));
      Assert.NotNull(stored["plan"]);
      Assert.Empty(stored["assigned"].AsArray());
    }

    // ---- the summary -------------------------------------------------------------------------------

    [Fact]
    public void The_summary_names_every_region_by_its_address()
    {
      var html = ScaffoldMapRecord.SummaryHtml(Verdict);

      Assert.Contains("2</strong> regions", html);
      Assert.Contains("A1", html);
      Assert.Contains("Prepare curves", html);
      Assert.Contains("B1", html);
      Assert.Contains("Ctrl-Z", html);
    }

    [Fact]
    public void Region_names_are_escaped_because_the_model_wrote_them()
    {
      // The same rule as the consent card (#235): Dwight's sentence carries the facts, the model's words
      // are data inside it - and this one reaches insertAdjacentHTML.
      var hostile = @"{""ok"":true,""result"":{""regions"":[
        {""key"":""A1"",""name"":""<img src=x onerror=alert(1)>""}]}}";

      var html = ScaffoldMapRecord.SummaryHtml(hostile);

      Assert.DoesNotContain("<img src=x onerror=alert(1)>", html);
      Assert.Contains("&lt;img", html);
    }

    [Fact]
    public void The_summary_states_what_the_map_quietly_did()
    {
      // A reversed dependency changes the reading order of the user's map. They did not ask for it and
      // would not otherwise know - so it is said, exactly as the verdict says it to the model.
      var withNotes = @"{""ok"":true,""result"":{
        ""regions"":[{""key"":""A1"",""name"":""One""}],
        ""reversedForReadingOrder"":[""b -> a""],
        ""unplaceable"":[""C1 - Crowded""],
        ""droppedAnchors"":[""a -> 42""]}}";

      var html = ScaffoldMapRecord.SummaryHtml(withNotes);

      Assert.Contains("Reading order", html);
      Assert.Contains("Could not place", html);
      Assert.Contains("C1 - Crowded", html);
      Assert.Contains("no longer on the canvas", html);
    }

    [Fact]
    public void A_clean_map_says_nothing_about_what_did_not_happen()
    {
      var html = ScaffoldMapRecord.SummaryHtml(Verdict);

      Assert.DoesNotContain("Reading order", html);
      Assert.DoesNotContain("Could not place", html);
      Assert.DoesNotContain("no longer on the canvas", html);
    }

    [Fact]
    public void An_empty_map_says_so_rather_than_claiming_success()
    {
      var html = ScaffoldMapRecord.SummaryHtml(@"{""ok"":true,""result"":{""regions"":[]}}");
      Assert.Contains("nothing was laid down", html);
    }

    [Fact]
    public void A_bare_result_envelope_reads_the_same_as_a_wrapped_one()
    {
      // The recorder is handed the wire envelope, but the shape it cares about is the result - accepting
      // both means a caller cannot record nothing by unwrapping one layer too many.
      var bare = @"{""regions"":[{""key"":""A1"",""name"":""One""}]}";
      Assert.Contains("A1", ScaffoldMapRecord.SummaryHtml(bare));
      Assert.Single(ScaffoldMapRecord.Regions(bare));
    }
  }
}
