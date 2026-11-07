using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// The consent tokens: minted per card, redeemed once, and - since U18 - minted in PAIRS for a spot card,
  /// whose two buttons are one act offered two ways.
  ///
  /// THE HAZARD #877 NAMED. A spot card's buttons each carry a live token. Clicked one after the other - loose
  /// first, then grouped - both would redeem, and the placement would run twice: the scope gate's collision
  /// check reads GROUPS, and a loose placement leaves none for it to find. So the two tokens are SIBLINGS, and
  /// redeeming either voids the other.
  /// </summary>
  public class ConsentTokensTests
  {
    private const string Thread = "thread-1";

    private static HandoffSeed SpotSeed() =>
      new HandoffSeed { Spot = "Spot 1", Place = new JsonObject { ["components"] = new JsonArray() } };

    private static void MintPair(ConsentTokens tokens, out string grouped, out string loose)
    {
      var seed = SpotSeed();
      tokens.MintSiblings(Thread, "build", seed.ForOption(grouped: true), seed.ForOption(grouped: false),
                          out grouped, out loose);
    }

    // ---- one card, one redemption ---------------------------------------------------------------------

    [Fact]
    public void A_token_redeems_once_and_the_second_click_says_so()
    {
      var tokens = new ConsentTokens();
      var token = tokens.Mint(Thread, "build", new HandoffSeed());

      Assert.True(tokens.Take(token, Thread, out var kind, out _, out _));
      Assert.Equal("build", kind);

      Assert.False(tokens.Take(token, Thread, out _, out _, out var reason));
      Assert.Equal(ConsentTokens.AlreadyUsed, reason);
    }

    [Fact]
    public void An_unknown_token_is_not_live()
    {
      Assert.False(new ConsentTokens().Take("never-minted", Thread, out _, out _, out var reason));
      Assert.Equal(ConsentTokens.NotLive, reason);
    }

    // ---- #877 (a): THE SIBLINGS ------------------------------------------------------------------------

    /// <summary>
    /// THE FIELD CASE. Loose first, then grouped: ONE redemption, so one placement, and the second click is
    /// told the user already approved this card - with the other button.
    /// </summary>
    [Fact]
    public void Loose_then_grouped_places_once_and_the_second_click_says_already_approved()
    {
      var tokens = new ConsentTokens();
      MintPair(tokens, out var grouped, out var loose);

      Assert.True(tokens.Take(loose, Thread, out _, out var taken, out _));
      Assert.False(taken.SpotGrouped);

      Assert.False(tokens.Take(grouped, Thread, out _, out var none, out var reason));
      Assert.Null(none);
      Assert.Equal(ConsentTokens.OtherOptionUsed, reason);
      Assert.Contains("already approved", ConsentTokens.Say(reason));
    }

    [Fact]
    public void Grouped_then_loose_places_once_too()
    {
      var tokens = new ConsentTokens();
      MintPair(tokens, out var grouped, out var loose);

      Assert.True(tokens.Take(grouped, Thread, out _, out var taken, out _));
      Assert.True(taken.SpotGrouped);

      Assert.False(tokens.Take(loose, Thread, out _, out _, out var reason));
      Assert.Equal(ConsentTokens.OtherOptionUsed, reason);
    }

    /// <summary>A click from another conversation consumes nothing, so it voids nothing either - the card
    /// stays live where it belongs, and still places exactly once there.</summary>
    [Fact]
    public void A_click_from_another_conversation_voids_nothing()
    {
      var tokens = new ConsentTokens();
      MintPair(tokens, out var grouped, out var loose);

      Assert.False(tokens.Take(grouped, "another-thread", out _, out _, out var wrong));
      Assert.StartsWith("wrong thread", wrong);

      Assert.True(tokens.Take(loose, Thread, out _, out _, out _));
      Assert.False(tokens.Take(grouped, Thread, out _, out _, out var reason));
      Assert.Equal(ConsentTokens.OtherOptionUsed, reason);
    }

    /// <summary>
    /// THE GUARD ON THE VOIDING IS "HAS A SIBLING". Two single-button cards are two acts, and approving one
    /// must leave the other live - otherwise the sibling rule would reach cards it was never written for.
    /// </summary>
    [Fact]
    public void Two_separate_cards_are_not_siblings()
    {
      // Brief AV (U96) changes this test, deliberately: two cards in ONE chat are no longer both live, because the later one retires
      // the earlier. They are still not SIBLINGS: the earlier is refused as "superseded", never as "other option used", and two cards
      // in different chats leave each other live, which is the property this test was written to keep.
      var tokens = new ConsentTokens();
      var first = tokens.Mint(Thread, "build", new HandoffSeed());
      var second = tokens.Mint(Thread, "build", new HandoffSeed());

      Assert.False(tokens.Take(first, Thread, out _, out _, out var reason));
      Assert.Equal(ConsentTokens.Superseded, reason);
      Assert.NotEqual(ConsentTokens.OtherOptionUsed, reason);
      Assert.True(tokens.Take(second, Thread, out _, out _, out _));

      var elsewhere = new ConsentTokens();
      var a = elsewhere.Mint("thread-a", "build", new HandoffSeed());
      var b = elsewhere.Mint("thread-b", "build", new HandoffSeed());
      Assert.True(elsewhere.Take(a, "thread-a", out _, out _, out _));
      Assert.True(elsewhere.Take(b, "thread-b", out _, out _, out _));
    }

    /// <summary>#255: one sentence per cause. Four causes wearing fewer sentences would leave a field report
    /// unable to say which one fired.</summary>
    [Fact]
    public void Every_reason_has_its_own_sentence()
    {
      var sentences = new[]
      {
        ConsentTokens.Say(ConsentTokens.AlreadyUsed),
        ConsentTokens.Say(ConsentTokens.OtherOptionUsed),
        ConsentTokens.Say(ConsentTokens.NotLive),
        ConsentTokens.Say("wrong thread (card a vs panel b)"),
      };
      Assert.Equal(sentences.Length, sentences.Distinct().Count());
    }

    // ---- each button's seed ---------------------------------------------------------------------------

    /// <summary>
    /// ForOption copies EVERY property and changes only the option. Checked by reflection over the type, so a
    /// property added to HandoffSeed later is covered without anyone remembering to add it here - which is
    /// the same reason ForOption copies by MemberwiseClone rather than by a list.
    /// </summary>
    [Fact]
    public void Each_option_carries_every_field_of_the_seed_and_differs_only_in_the_option()
    {
      var seed = new HandoffSeed
      {
        Spot = "Spot 1",
        Place = new JsonObject { ["components"] = new JsonArray() },
        Regions = new List<string> { "A1" },
        Mode = "m",
        Why = "because",
        Target = 7,
      };

      var grouped = seed.ForOption(grouped: true);
      var loose = seed.ForOption(grouped: false);

      Assert.True(grouped.SpotGrouped);
      Assert.False(loose.SpotGrouped);

      foreach (var p in typeof(HandoffSeed).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                                           .Where(p => p.Name != nameof(HandoffSeed.SpotGrouped) && p.CanRead))
      {
        // A value type comes back boxed afresh on every read, so it is compared by value; everything else by
        // identity, which is what MemberwiseClone gives and what "a copy of every field" means here.
        if (p.PropertyType.IsValueType)
        {
          Assert.Equal(p.GetValue(seed), p.GetValue(grouped));
          Assert.Equal(p.GetValue(seed), p.GetValue(loose));
        }
        else
        {
          Assert.Same(p.GetValue(seed), p.GetValue(grouped));
          Assert.Same(p.GetValue(seed), p.GetValue(loose));
        }
      }
    }
  }
}
