using System;
using System.Collections.Generic;
using System.Linq;
using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// U18: WHERE A SPOT ADDRESS COMES FROM, AND THE PROPERTY EVERYTHING ELSE LEANS ON.
  ///
  /// A spot is the one region approved BEFORE it exists - the card names it, the build creates it - so
  /// there is a gap between the card and the click that no other address has. The governing rule:
  ///
  ///   AN ADDRESS ON A CARD IS A PROMISE. IT IS RESERVED WHEN THE CARD IS MINTED AND IT NEVER RENUMBERS.
  ///
  /// A card saying "in a new group called Spot 2" that landed as Spot 3 would not have been true when it
  /// was approved, which is the one thing a card may never be. So a collision REFUSES (BuildScope's job)
  /// and the mint never quietly shuffles.
  /// </summary>
  [Collection("SpotMint")]
  public class SpotMintTests : IDisposable
  {
    private static readonly Guid DocA = new Guid("aaaa1818-0000-0000-0000-000000000001");
    private static readonly Guid DocB = new Guid("bbbb1818-0000-0000-0000-000000000002");

    public SpotMintTests() => SpotMint.ForgetAll();
    public void Dispose() => SpotMint.ForgetAll();

    private static readonly string[] Bare = new string[0];

    [Fact]
    public void The_first_card_on_a_bare_canvas_reserves_Spot_1() =>
      Assert.Equal("Spot 1", SpotMint.For(DocA).Reserve(Bare));

    /// <summary>
    /// THE RACE THIS TYPE EXISTS FOR. Two cards can be outstanding at once - two threads on one canvas, or
    /// one thread whose first card has not been clicked. Without a reservation both would read the same
    /// bare canvas and mint the same address, and the second build would land in the first's group.
    /// </summary>
    [Fact]
    public void Two_outstanding_cards_never_name_the_same_place()
    {
      var mint = SpotMint.For(DocA);

      var first = mint.Reserve(Bare);
      var second = mint.Reserve(Bare);      // the canvas has not changed - neither card has been clicked

      Assert.Equal("Spot 1", first);
      Assert.Equal("Spot 2", second);
      Assert.NotEqual(first, second);
    }

    /// <summary>Continues past what the canvas already carries, so a restart - which loses every
    /// reservation - still cannot re-mint a spot that exists.</summary>
    [Fact]
    public void A_fresh_mint_continues_past_the_canvas()
    {
      Assert.Equal("Spot 4", SpotMint.For(DocA).Reserve(new[] { "Spot 1", "Spot 2", "Spot 3" }));
    }

    [Fact]
    public void It_continues_past_both_the_canvas_and_its_own_promises()
    {
      var mint = SpotMint.For(DocA);

      Assert.Equal("Spot 3", mint.Reserve(new[] { "Spot 2" }));   // canvas has 2
      Assert.Equal("Spot 4", mint.Reserve(new[] { "Spot 2" }));   // canvas still has 2; 3 is promised
    }

    /// <summary>Plan addresses are a different namespace and do not crowd the spot counter - the point of
    /// their being two namespaces.</summary>
    [Fact]
    public void A_canvas_full_of_map_regions_still_starts_at_Spot_1()
    {
      Assert.Equal("Spot 1", SpotMint.For(DocA).Reserve(new[] { "A1", "A2", "B1", "C9" }));
    }

    /// <summary>
    /// "Which spots are spoken for" is a fact about the FILE, not about a conversation. Two threads on one
    /// canvas must not mint the same address; the same thread on two files must not share a counter.
    /// </summary>
    [Fact]
    public void Two_documents_do_not_share_a_counter()
    {
      Assert.Equal("Spot 1", SpotMint.For(DocA).Reserve(Bare));
      Assert.Equal("Spot 1", SpotMint.For(DocB).Reserve(Bare));
      Assert.Equal("Spot 2", SpotMint.For(DocA).Reserve(Bare));
    }

    [Fact]
    public void One_mint_per_document()
    {
      Assert.Same(SpotMint.For(DocA), SpotMint.For(DocA));
      Assert.NotSame(SpotMint.For(DocA), SpotMint.For(DocB));
    }

    // ---- the promise ---------------------------------------------------------------------------------

    [Fact]
    public void A_reserved_address_is_known_to_be_reserved()
    {
      var mint = SpotMint.For(DocA);
      var minted = mint.Reserve(Bare);

      Assert.True(mint.IsReserved(minted));
      Assert.False(mint.IsReserved("Spot 9"));
      Assert.False(mint.IsReserved("A1"));
      Assert.False(mint.IsReserved(null));
      Assert.False(mint.IsReserved(""));
    }

    /// <summary>
    /// A CARD THAT IS NEVER CLICKED LEAKS ITS NUMBER, AND THAT IS FINE - stated as a property rather than
    /// left to be discovered. Gaps are not recycled by design (#720: the conversation may still say
    /// "Spot 2" about a placement that was undone), so a leaked reservation is indistinguishable from a
    /// used one. The alternative - releasing on dismissal - would let a re-minted address mean two
    /// different things to one conversation.
    /// </summary>
    [Fact]
    public void An_abandoned_card_does_not_release_its_number()
    {
      var mint = SpotMint.For(DocA);

      mint.Reserve(Bare);                                  // Spot 1, never clicked
      Assert.Equal("Spot 2", mint.Reserve(Bare));          // the next card does NOT reuse it
    }

    [Fact]
    public void Reserved_addresses_are_reported_in_address_order()
    {
      var mint = SpotMint.For(DocA);
      for (var i = 0; i < 11; i++) mint.Reserve(Bare);

      var reserved = mint.Reserved();

      Assert.Equal("Spot 1", reserved.First());
      Assert.Equal("Spot 11", reserved.Last());            // never "Spot 10" last - #286 for the new shape
    }

    // ---- the mint and the scope gate agree -----------------------------------------------------------

    /// <summary>
    /// THE END-TO-END PROPERTY U19'S CARD DEPENDS ON: what the mint reserved is what the scope gate
    /// approves, unchanged.
    /// </summary>
    [Fact]
    public void What_was_reserved_is_what_gets_approved()
    {
      var minted = SpotMint.For(DocA).Reserve(new[] { "A1", "Spot 1" });

      var resolution = BuildScope.Resolve(new string[0], new[] { "A1", "Spot 1" }, mintedSpot: minted);

      Assert.Equal("Spot 2", minted);
      Assert.Equal(BuildScopeOutcome.Approved, resolution.Outcome);
      Assert.Equal(new[] { minted }, resolution.Regions.ToArray());
    }

    /// <summary>
    /// And if the canvas gains that address between the card and the click, it REFUSES BY NAME rather than
    /// renumbering. Refusing is recoverable - the user asks again and gets a fresh card. A silently
    /// renumbered address is not: it has already been approved as something else.
    /// </summary>
    [Fact]
    public void A_collision_after_the_card_refuses_rather_than_moving_the_promise()
    {
      var minted = SpotMint.For(DocA).Reserve(Bare);

      // ...someone else's Spot 1 appears before the click.
      var resolution = BuildScope.Resolve(new string[0], new[] { minted }, mintedSpot: minted);

      Assert.Equal(BuildScopeOutcome.Refuse, resolution.Outcome);
      Assert.Contains(minted + " is already on the canvas", resolution.Message);
      Assert.Contains("fresh one", resolution.Message);
    }

    // ---- #878: A RESTART, AND THE SAVED CONVERSATION ------------------------------------------------------
    //
    // After a restart the reservations are gone, and a LOOSE spot leaves no name on the canvas - so the mint
    // also reads every spot the saved conversation mentions. The fixtures below are the WRITERS' REAL OUTPUT:
    // the card from ConsentCard.SpotHtml and the report from SpotAsk.PlacedReport, stored as the panel stores
    // them. A hand-typed "Spot 1" would pass on a scanner that misses what the writers actually write.

    private static string CardFor(string address) =>
      ConsentCard.SpotHtml("so you can see the value", new SpotPlacement
      {
        Address = address,
        Components = new List<string> { "a Panel" },
      }, "grouped-token", "loose-token");

    private static readonly System.Text.Json.Nodes.JsonObject PanelRecord = new System.Text.Json.Nodes.JsonObject
    {
      ["components"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonNode[]
      {
        new System.Text.Json.Nodes.JsonObject { ["ref"] = "p", ["type"] = "Panel" },
      }),
    };

    /// <summary>The report as the store holds it: the encoded HTML shown, and the source beside it.</summary>
    private static string[] ReportFor(string address)
    {
      var landed = SpotAsk.PlacedReport(PanelRecord, address);
      return new[] { System.Net.WebUtility.HtmlEncode(landed), landed };
    }

    /// <summary>
    /// THE CASE #878 IS ABOUT. The user approved Spot 1 LOOSE, it landed, and Rhino restarted. The canvas carries
    /// no "Spot 1" (a loose spot has no group) and the fresh mint has no reservations - but the saved conversation
    /// still says "Spot 1" about that placement. The next card must not mint Spot 1 again.
    /// </summary>
    [Fact]
    public void After_a_restart_a_spot_the_saved_conversation_mentions_is_not_minted_again()
    {
      var saved = new List<string> { CardFor("Spot 1") };
      saved.AddRange(ReportFor("Spot 1"));

      var next = SpotMint.For(DocA).Reserve(SpotMint.InUse(Bare, saved));   // a fresh mint: ForgetAll ran

      Assert.Equal("Spot 2", next);
    }

    /// <summary>The twin: THE CANVAS STILL COUNTS. A grouped Spot 3 standing on the canvas, with no mention of it
    /// in the saved conversation, still pushes the next mint past it.</summary>
    [Fact]
    public void A_grouped_spot_on_the_canvas_still_counts_with_nothing_saved()
    {
      Assert.Equal("Spot 4", SpotMint.For(DocA).Reserve(SpotMint.InUse(new[] { "Spot 3" }, Bare)));
    }

    [Fact]
    public void The_canvas_and_the_saved_conversation_both_count()
    {
      Assert.Equal("Spot 6", SpotMint.For(DocA).Reserve(SpotMint.InUse(new[] { "Spot 3" }, ReportFor("Spot 5"))));
    }

    /// <summary>Each real writer, on its own, is found - the card as the only record of an address it offered,
    /// and the report as the only record of one that landed.</summary>
    [Fact]
    public void The_real_card_and_the_real_report_are_each_read()
    {
      Assert.Equal(new[] { "Spot 7" }, SpotMint.MentionedIn(new[] { CardFor("Spot 7") }).ToArray());
      Assert.Equal(new[] { "Spot 8" }, SpotMint.MentionedIn(ReportFor("Spot 8")).ToArray());
    }

    /// <summary>
    /// THE STORE HOLDS HTML, so an address can arrive wrapped in markup or with an encoded space. Tags are replaced
    /// by a space and entities decoded BEFORE the scan - never tags removed outright, which would weld "Spot" to
    /// the next word.
    /// </summary>
    [Theory]
    [InlineData("<strong>Spot</strong>&nbsp;4 is where it went", "Spot 4")]
    [InlineData("<p>Spot</p><p>9</p>", "Spot 9")]
    [InlineData("placed in spot 2, loose", "Spot 2")]            // any case: over-reserving only skips numbers
    [InlineData("Spot&#32;11.", "Spot 11")]
    public void A_mention_is_found_through_markup_and_entities(string stored, string expected)
    {
      Assert.Equal(new[] { expected }, SpotMint.MentionedIn(new[] { stored }).ToArray());
    }

    [Theory]
    [InlineData("The Spotlight 3 component")]
    [InlineData("a hotspot 4 on the curve")]
    [InlineData("Build A1 and B2")]
    [InlineData("Spot 0 is nobody's address")]
    [InlineData("Spot 2147483647")]                   // would overflow NextSpot's highest-plus-one
    [InlineData("Spot 99999999999")]                  // more digits than any address can have
    [InlineData("")]
    [InlineData(null)]
    public void What_is_not_a_spot_address_reserves_nothing(string stored)
    {
      Assert.Empty(SpotMint.MentionedIn(new[] { stored }));
    }

    // ---- the seed carries it in its own field --------------------------------------------------------

    /// <summary>
    /// The spot travels in a field of its OWN, never in Regions - so "approve an address that is not on
    /// the canvas" is not expressible for a plan region at all, rather than being checked for and
    /// rejected.
    /// </summary>
    [Fact]
    public void The_seed_carries_a_spot_apart_from_its_regions()
    {
      var plan = SeedPlan.From("build", new HandoffSeed
      {
        Regions = new List<string> { "A1" },
        Spot = "Spot 3",
        SpotGrouped = true,
      }, _ => IdState.Alive);

      Assert.Equal(new[] { "A1" }, plan.Regions.ToArray());
      Assert.Equal("Spot 3", plan.PreFillSpot);
      Assert.True(plan.PreFillSpotGrouped);
      Assert.DoesNotContain("Spot 3", plan.Regions);
    }

    /// <summary>A plan address cannot enter through the spot field, and a spot cannot enter through the
    /// regions list. The two carriers are shape-checked in opposite directions.</summary>
    [Fact]
    public void Neither_shape_can_travel_in_the_others_carrier()
    {
      var plan = SeedPlan.From("build", new HandoffSeed
      {
        Regions = new List<string> { "A1", "Spot 9" },
        Spot = "A2",
      }, _ => IdState.Alive);

      Assert.Equal(new[] { "A1" }, plan.Regions.ToArray());   // "Spot 9" is not a plan address
      Assert.Null(plan.PreFillSpot);                          // "A2" is not a spot
    }
  }
}
