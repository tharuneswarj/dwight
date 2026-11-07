using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// U18's CARRIER: the nomination that becomes the card, and then becomes the build.
  ///
  /// THE DANGER THIS SHAPE REMOVES. A spot card must name every component, because a spot has no region
  /// name standing in for its contents - the components ARE the scope (#338). If the card named one set
  /// and the approved turn then built from a spec of the model's own, EVERY GUARD WOULD PASS AND THE
  /// VERDICT WOULD BE TRUE: preservation holds, completeness holds, the trio reports success, because the
  /// card is the only thing that was wrong and nothing in the system reads the card.
  ///
  /// So the divergence is not detected, it is made unrepresentable: the validated spec produced here IS
  /// what gets built. That is ADR-075's shape - a capability by construction rather than an intent
  /// detected - and it is the same move as #338's own fix, where a seeded route cannot REACH WholeCanvas
  /// rather than being checked for it.
  ///
  /// EVERY REFUSAL HERE COSTS ONE OF THE MODEL'S THREE ASKS, so every refusal must be worth the try. These
  /// tests assert the DIAGNOSTIC as much as the refusal: a bound with no loop to close would make three
  /// attempts arbitrary rather than fair (ADR-094).
  /// </summary>
  public class SpotAskTests
  {
    private const string Slider = "Number Slider";
    private const string Panel = "Panel";

    private static readonly string[] Installed = { Slider, Panel, "Circle", "Curve" };

    private static bool TypeExists(string t) => Installed.Contains(t, StringComparer.OrdinalIgnoreCase);
    private static IReadOnlyList<string> Nearest(string t) => new[] { "Number Slider", "Panel" };
    private static IdState Alive(int id) => IdState.Alive;

    private static JsonObject Component(string type, string nick = null, JsonObject state = null)
    {
      var o = new JsonObject { ["ref"] = (nick ?? type).ToLowerInvariant(), ["type"] = type };
      if (nick != null) o["nick"] = nick;
      if (state != null) o["state"] = state;
      return o;
    }

    private static HandoffSeed Seed(params JsonObject[] components) => Seed(null, components);

    private static HandoffSeed Seed(JsonArray wires, params JsonObject[] components)
    {
      var place = new JsonObject { ["components"] = new JsonArray(components.Cast<JsonNode>().ToArray()) };
      if (wires != null) place["wires"] = wires;
      return new HandoffSeed { Place = place };
    }

    private static bool Validate(HandoffSeed seed, out SpotAsk ask, out ToolResult error,
                                 Func<int, string> labelOfId = null) =>
      SpotAsk.Validate(seed, TypeExists, Nearest, Alive, null, null,
                       labelOfId ?? (_ => null), out ask, out error);

    // ---- the happy path: the plugin names what the model nominated ----------------------------------

    [Fact]
    public void A_valid_nomination_becomes_a_spec_and_a_placement()
    {
      Assert.True(Validate(Seed(Component(Slider), Component(Panel)), out var ask, out var error), error?.ToJson());

      Assert.Equal(2, ask.Spec.Components.Count);
      Assert.Equal(new[] { "a Number Slider", "a Panel" }, ask.Placement.Components.ToArray());
    }

    /// <summary>
    /// THE CARD IS RENDERED FROM THE VALIDATED READ, NEVER THE NOMINATION (ADR-056, and Simplify's
    /// externalize precedent - the model once wired a float to the wrong port and verification passed).
    /// A nickname is what the user will see on the canvas, so it wins where given; the TYPE is the
    /// plugin's resolved one either way.
    /// </summary>
    [Fact]
    public void The_placement_names_what_the_user_will_see()
    {
      Assert.True(Validate(Seed(Component(Slider, nick: "Radius")), out var ask, out _));

      Assert.Equal(new[] { "a Radius" }, ask.Placement.Components.ToArray());
    }

    [Fact]
    public void The_article_agrees_with_the_name()
    {
      Assert.True(Validate(Seed(Component(Panel, nick: "Output")), out var ask, out _));
      Assert.Equal(new[] { "an Output" }, ask.Placement.Components.ToArray());
    }

    // ---- refusals that cost a try, so each must be worth one ---------------------------------------

    /// <summary>An uninstalled type refuses BY NAME with the installed near-matches - never a silent drop
    /// and never a partial card (#208: fail open on knowledge, never on identity).</summary>
    [Fact]
    public void An_uninstalled_type_refuses_and_names_what_is_installed()
    {
      Assert.False(Validate(Seed(Component("Frobnicator")), out var ask, out var error));

      Assert.Null(ask);
      Assert.Equal(ToolErrors.UnknownComponentType, error.Code);
      Assert.Contains("Frobnicator", error.ToJson());
      Assert.Contains("Number Slider", error.ToJson());   // the repair payload, not just the rejection
    }

    /// <summary>Nothing to place is nothing to approve - and the refusal names the field, so the model can
    /// fix it rather than guess at it.</summary>
    [Fact]
    public void A_nomination_with_nothing_in_it_refuses_and_names_the_field()
    {
      foreach (var seed in new[] { new HandoffSeed(), new HandoffSeed { Place = new JsonObject() },
                                   Seed(new JsonObject[0]) })
      {
        Assert.False(Validate(seed, out _, out var error));
        Assert.Equal(ToolErrors.InvalidArgs, error.Code);
        Assert.Contains("place", error.Message);
      }
    }

    /// <summary>
    /// THE MODEL MAY NOT NAME ITS OWN ADDRESS (#581's rule, #763's reason). The spot is minted and
    /// reserved by the plugin, so a nomination carrying a region is guessing at a name that does not
    /// exist yet - and an address the model could choose would stop meaning what the plugin says it means.
    /// </summary>
    [Fact]
    public void A_nomination_naming_its_own_region_refuses()
    {
      var seed = Seed(Component(Slider));
      seed.Place["region"] = "Spot 1";

      Assert.False(Validate(seed, out _, out var error));

      // Brief CM: it may name a place that exists, and one that does not is still refused: a spot's own address is the plugin's to mint.
      Assert.Equal(ToolErrors.UnknownRegion, error.Code);
      Assert.Contains("is not a region or group on this canvas", error.Message);
    }

    /// <summary>Over the bound it REFUSES rather than truncating, and names the door. A truncated placement
    /// would build less than was asked for, on a card that could not say so.</summary>
    [Fact]
    public void Too_many_to_name_refuses_toward_propose_scaffold()
    {
      var many = Enumerable.Range(0, SpotPlacement.MaxComponents + 1)
        .Select(i => Component(Panel, nick: "Panel" + i)).ToArray();

      Assert.False(Validate(Seed(many), out _, out var error));

      Assert.Equal(ToolErrors.PreconditionRefused, error.Code);
      Assert.Contains("propose_scaffold", error.Message);
    }

    [Fact]
    public void Exactly_the_bound_is_allowed()
    {
      var atLimit = Enumerable.Range(0, SpotPlacement.MaxComponents)
        .Select(i => Component(Panel, nick: "Panel" + i)).ToArray();

      Assert.True(Validate(Seed(atLimit), out var ask, out var error), error?.ToJson());
      Assert.Equal(SpotPlacement.MaxComponents, ask.Placement.Components.Count);
    }

    /// <summary>Brief CV, through the whole ask: twelve validated declarations reach a card, and the thirteenth is refused with the keys the stop line reads.</summary>
    [Fact]
    public void Twelve_pass_the_whole_ask_and_thirteen_are_refused()
    {
      var twelve = Enumerable.Range(0, 12).Select(i => Component(Panel, nick: "Panel" + i)).ToArray();
      Assert.True(Validate(Seed(twelve), out var ask, out var error), error?.ToJson());
      Assert.Equal(12, ask.Placement.Components.Count);

      var thirteen = Enumerable.Range(0, 13).Select(i => Component(Panel, nick: "Panel" + i)).ToArray();
      Assert.False(Validate(Seed(thirteen), out _, out error));
      Assert.Equal(ToolErrors.PreconditionRefused, error.Code);
      Assert.Equal(13, error.Detail["asked"].GetValue<int>());
      Assert.Equal(12, error.Detail["mostThatCanBeNamed"].GetValue<int>());
    }

    // ---- "beside", read from the canvas rather than from the model ---------------------------------

    /// <summary>
    /// The card says where in words a person can check by looking, and the WORDS come from a live read:
    /// the model names an id, the plugin says what that id is called. Taking the name from the nomination
    /// would let a card describe the placement as sitting beside something it is not.
    /// </summary>
    [Fact]
    public void Beside_is_read_from_the_canvas_by_id()
    {
      var wires = new JsonArray(new JsonNode[]
      {
        new JsonObject
        {
          ["from"] = new JsonObject { ["id"] = 7 },
          ["to"] = new JsonObject { ["ref"] = "number slider" },
        },
      });

      Assert.True(Validate(Seed(wires, Component(Slider)), out var ask, out var error,
        labelOfId: id => id == 7 ? "Curve" : null), error?.ToJson());

      Assert.Equal("Curve", ask.Placement.Beside);
    }

    /// <summary>Connecting to nothing is a first-class case: the card then says where it goes without promising a direction,
    /// because the search picks the nearest clear place. The sentence after it landed names the direction, with a number.</summary>
    [Fact]
    public void A_placement_that_connects_to_nothing_has_nothing_to_sit_beside()
    {
      Assert.True(Validate(Seed(Component(Slider)), out var ask, out _));

      Assert.Null(ask.Placement.Beside);
      Assert.Contains(ConsentCard.ClearPlace,
        ConsentCard.SpotAuthority(ask.Placement, grouped: true));
    }

    // ---- ROUTING: on what was NAMED, never on what was FOUND -----------------------------------------

    /// <summary>
    /// THE GUARD THE ROUTING FUNCTION EXISTS FOR. A build ask naming a region that does not exist must take
    /// the REGIONS path - where a missing region is refused by name - and must never reach the spot path,
    /// where the plugin would mint an address and place the components somewhere else. Routing on what was
    /// FOUND would turn a typo into a placement with a verdict saying it succeeded (#338 and #208 at once).
    /// "Mints nothing" follows from the route: the regions path never reaches the mint.
    /// </summary>
    [Theory]
    [InlineData("A1")]            // a real-looking address that is not on the canvas
    [InlineData("A99")]
    [InlineData("the left bit")]  // not an address at all
    public void A_named_region_takes_the_regions_path_even_if_it_does_not_exist(string named)
    {
      var routing = SpotAsk.RouteFor(new HandoffSeed { Regions = new List<string> { named } });

      Assert.Null(routing.Refusal);
      Assert.Equal(BuildAskRoute.Regions, routing.Route);
      Assert.NotEqual(BuildAskRoute.Spot, routing.Route);
    }

    [Fact]
    public void Naming_no_region_takes_the_spot_path()
    {
      var routing = SpotAsk.RouteFor(Seed(Component(Slider)));

      Assert.Null(routing.Refusal);
      Assert.Equal(BuildAskRoute.Spot, routing.Route);
    }

    /// <summary>Blank entries name nothing, so they do not count as naming a region.</summary>
    [Fact]
    public void Blank_region_entries_name_nothing()
    {
      var seed = Seed(Component(Slider));
      seed.Regions = new List<string> { "", "   " };

      Assert.Equal(BuildAskRoute.Spot, SpotAsk.RouteFor(seed).Route);
    }

    /// <summary>Two kinds of scope in one request is refused, not resolved either way - dropping one half
    /// would silently discard something the model said it meant.</summary>
    [Fact]
    public void Naming_regions_and_giving_a_placement_is_refused()
    {
      var seed = Seed(Component(Slider));
      seed.Regions = new List<string> { "A1" };

      var routing = SpotAsk.RouteFor(seed);

      Assert.NotNull(routing.Refusal);
      Assert.Equal(ToolErrors.InvalidArgs, routing.Refusal.Code);
      Assert.Contains("two different kinds of scope", routing.Refusal.Message);
    }

    // ---- THE ASK AND THE BUILD RESOLVE WITH THE SAME CODE (#862, Q1) ---------------------------------

    /// <summary>
    /// A Circle takes Plane and Radius and gives a Circle; id 7 is an existing Curve. A wire INTO the
    /// Circle names its port, so the spelling the model uses must match a real one.
    /// </summary>
    private static SpecResolvers Resolvers() => new SpecResolvers
    {
      TypeExists = TypeExists,
      Nearest = Nearest,
      IdState = Alive,
      PortsOfType = t => string.Equals(t, "Circle", StringComparison.OrdinalIgnoreCase)
        ? new PortShape { Inputs = new[] { "Plane", "Radius" }, Outputs = new[] { "Circle" } }
        : new PortShape { Inputs = new[] { "A" }, Outputs = new[] { "B" } },
      PortsOfId = id => id == 7
        ? new PortShape { Inputs = new[] { "Curve" }, Outputs = new[] { "Curve" } }
        : null,
    };

    private static JsonObject CircleFedFrom7(string radiusPort) => new JsonObject
    {
      ["components"] = new JsonArray(new JsonNode[] { new JsonObject { ["ref"] = "c", ["type"] = "Circle" } }),
      ["wires"] = new JsonArray(new JsonNode[]
      {
        new JsonObject
        {
          ["from"] = new JsonObject { ["id"] = 7 },
          ["to"] = new JsonObject { ["ref"] = "c", ["param"] = radiusPort },
        },
      }),
    };

    /// <summary>
    /// THE TEST THAT FAILS IF THE ASK AND THE BUILD EVER DRIFT.
    ///
    /// #862 rules that a wrong wire refuses AT THE ASK, before a card exists. The ask can only do that by
    /// asking the build's own question, so both parse through one <see cref="SpecResolvers"/>. If someone
    /// later gives the ask its own resolvers - or, the quiet version, passes null port resolvers there -
    /// the wrong-wire case below stops agreeing: the ask would ACCEPT what the build refuses, moving every
    /// wrong-wire refusal to the click while the rest of the suite stayed green.
    /// </summary>
    [Fact]
    public void The_ask_and_the_build_resolve_the_same_spec_identically()
    {
      var resolvers = Resolvers();
      var good = CircleFedFrom7("radius");     // lower case on purpose: the canvas's spelling must win

      Assert.True(resolvers.Parse(good, out var built, out var buildError), buildError?.ToJson());
      Assert.True(SpotAsk.Validate(new HandoffSeed { Place = good }, resolvers, _ => "Curve", out var asked, out var askError),
        askError?.ToJson());

      Assert.Equal(built.Components.Select(c => c.Type), asked.Spec.Components.Select(c => c.Type));
      Assert.Equal(built.Wires.Select(w => w.ToParam), asked.Spec.Wires.Select(w => w.ToParam));
      Assert.Equal("Radius", asked.Spec.Wires[0].ToParam);   // resolved to the real port's spelling, both ways
    }

    [Fact]
    public void A_wrong_wire_is_refused_identically_by_the_ask_and_by_the_build()
    {
      var resolvers = Resolvers();
      var wrong = CircleFedFrom7("Radus");     // no such port

      Assert.False(resolvers.Parse(wrong, out _, out var buildError));
      Assert.False(SpotAsk.Validate(new HandoffSeed { Place = wrong }, resolvers, _ => "Curve", out var asked, out var askError));

      Assert.Null(asked);
      Assert.Equal(buildError.Code, askError.Code);
      Assert.Equal(buildError.Message, askError.Message);

      // WORTH THE TRY IT COSTS (#864): the refusal carries the REAL ports as structured detail, which is
      // what the model receives in the envelope and can act on mechanically - not buried in prose. (This
      // assertion first checked the MESSAGE and failed; the ports were never there, they were in the detail
      // all along. The test was reading the wrong field, not the subject withholding the repair.)
      var available = askError.Detail["available"].AsArray().Select(n => n.GetValue<string>()).ToArray();
      Assert.Equal(new[] { "Plane", "Radius" }, available);
      Assert.Contains("Radius", askError.ToJson());
    }

    // ---- THE RECORD, WRITTEN IN THE PLUGIN'S WORDS --------------------------------------------------

    /// <summary>A resolver set whose type lookup is normalised (as ComponentWhitelist's is) and which knows the
    /// installed spelling - so a lower-case nomination is accepted, and the record can say it properly.</summary>
    private static SpecResolvers Canonicalising() => new SpecResolvers
    {
      TypeExists = t => Installed.Any(i => string.Equals(i, t?.Trim(), StringComparison.OrdinalIgnoreCase)),
      Nearest = Nearest,
      IdState = Alive,
      CanonicalType = t => Installed.FirstOrDefault(i => string.Equals(i, t?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? t,
    };

    private static JsonObject FloatSlider(string type, string nick, decimal min, decimal max, decimal value) => new JsonObject
    {
      ["components"] = new JsonArray(new JsonNode[]
      {
        new JsonObject
        {
          ["ref"] = "s", ["type"] = type, ["nick"] = nick,
          ["state"] = new JsonObject
          {
            ["min"] = JsonValue.Create(min), ["max"] = JsonValue.Create(max),
            ["value"] = JsonValue.Create(value), ["rounding"] = "float",
          },
        },
      }),
    };

    /// <summary>
    /// THE ROUND TRIP - what makes "the record IS the spec" true rather than asserted.
    ///
    /// The record is serialised, sent through TEXT (the stricter path: if it survives a string it survives
    /// anywhere), parsed back through the build's own parser, and required to be the SAME spec. Without this
    /// the record could hold text that parses to something other than what was validated - #153 at the record
    /// level: the text stored must be the text that gets built.
    ///
    /// Its sharpest assertion is the one a naive serialiser fails: a slider's decimal places are never
    /// written, they are DERIVED from the scale of the numbers - so 2.50 must come back as 2.50, not 2.5, or
    /// the slider is born with a different precision from the one the card named.
    /// </summary>
    [Fact]
    public void The_record_parses_back_to_the_spec_it_was_written_from()
    {
      var resolvers = Canonicalising();
      Assert.True(SpotAsk.Validate(new HandoffSeed { Place = FloatSlider(Slider, "Width", 0m, 10m, 2.50m) },
        resolvers, _ => null, out var ask, out var error), error?.ToJson());

      var throughText = (JsonObject)JsonNode.Parse(ask.Record.ToJsonString());
      Assert.True(resolvers.Parse(throughText, out var back, out var backError), backError?.ToJson());

      var before = ask.Spec.Components[0];
      var after = back.Components[0];
      Assert.Equal(before.Ref, after.Ref);
      Assert.Equal(before.Type, after.Type);
      Assert.Equal(before.Nick, after.Nick);
      Assert.Equal(before.State.Min, after.State.Min);
      Assert.Equal(before.State.Max, after.State.Max);
      Assert.Equal(before.State.Value, after.State.Value);
      Assert.Equal(before.State.Rounding, after.State.Rounding);
      Assert.Equal(2, before.State.DecimalPlaces);
      Assert.Equal(before.State.DecimalPlaces, after.State.DecimalPlaces);   // the scale survived the text

      Assert.True(BuildSpecSchemaTests.SameMeaning(SpotAsk.Serialise(back), ask.Record));
    }

    /// <summary>
    /// A differently-cased type comes out in the PLUGIN'S spelling - in the record AND on the card. The
    /// parser only checks a type exists, through a normalised lookup, and keeps the model's casing; before
    /// this, a card read "a number slider" (the nomination echoed back) while its own code comment claimed it
    /// rendered from the validated read. Canonicalising the spec both are drawn from makes that true.
    /// </summary>
    [Fact]
    public void A_differently_cased_type_comes_out_in_the_plugins_spelling()
    {
      Assert.True(SpotAsk.Validate(new HandoffSeed { Place = FloatSlider("number slider", null, 0m, 1m, 0.5m) },
        Canonicalising(), _ => null, out var ask, out var error), error?.ToJson());

      Assert.Equal(Slider, ask.Record["components"][0]["type"].GetValue<string>());
      // U30: the card shows the validated value beside the component, so a slider with a state reads "set to 0.5".
      Assert.Equal(new[] { "a Number Slider set to 0.5" }, ask.Placement.Components.ToArray());
      Assert.DoesNotContain("number slider", ConsentCard.SpotAuthority(ask.Placement, grouped: true));
    }

    /// <summary>
    /// A NICK IS THE USER'S TEXT AND STAYS THE USER'S TEXT - the plugin cannot author one. So it is NOT
    /// rewritten into the record; it is kept as data and ESCAPED AT EVERY RENDER (#335: escaping is a
    /// property of the destination). What must never happen is that it reaches a card as markup.
    /// </summary>
    [Fact]
    public void A_nick_carrying_markup_is_kept_as_data_and_never_reaches_a_card_as_markup()
    {
      const string nick = "<b onclick=\"x()\">Count</b>";
      Assert.True(SpotAsk.Validate(new HandoffSeed { Place = FloatSlider(Slider, nick, 0m, 10m, 4m) },
        Canonicalising(), _ => null, out var ask, out var error), error?.ToJson());

      Assert.Equal(nick, ask.Record["components"][0]["nick"].GetValue<string>());   // the user's text, untouched

      var html = ConsentCard.SpotHtml("why", ask.Placement, "g", "l");
      Assert.DoesNotContain("<b ", html);
      Assert.DoesNotContain("onclick=\"x()\"", html);
      Assert.Contains("&lt;b", html);
    }

    // ---- the record is the spec --------------------------------------------------------------------

    /// <summary>
    /// THE PROPERTY THE WHOLE SHAPE EXISTS FOR: what the card names and what gets built come from ONE
    /// object. This asserts they cannot disagree because they are not two things - if a later refactor
    /// reintroduces a separate spec for the build, the card's components and the spec's will be able to
    /// drift, and there is no guard anywhere that would notice.
    /// </summary>
    [Fact]
    public void The_card_and_the_build_read_the_same_object()
    {
      Assert.True(Validate(Seed(Component(Slider, nick: "Count"), Component(Panel)), out var ask, out _));

      var named = ask.Placement.Components;
      var built = ask.Spec.Components.Select(c => string.IsNullOrWhiteSpace(c.Nick) ? c.Type : c.Nick).ToList();

      Assert.Equal(built.Count, named.Count);
      for (var i = 0; i < built.Count; i++)
        Assert.Contains(built[i], named[i]);
    }

    // ---- THE ASK: validate, then mint, then the record overwrites the nomination (#862) ---------------

    private static bool Prepare(HandoffSeed seed, SpotMint mint, out SpotAsk ask, out ToolResult error,
                                IEnumerable<string> onCanvas = null) =>
      SpotAsk.Prepare(seed, Canonicalising(), _ => null, mint, onCanvas ?? new string[0], out ask, out error);

    /// <summary>
    /// What the card names and what the click builds are the PLUGIN's: the minted address, and the record in
    /// the plugin's own words - never the nomination as it arrived. The nomination here spells its type in
    /// lower case; the seed must come back holding the canonical spelling, because the click builds from it.
    /// </summary>
    [Fact]
    public void Prepare_mints_the_address_and_the_record_replaces_the_nomination()
    {
      var mint = new SpotMint();
      var seed = Seed(Component("number slider", nick: "Count"));
      var nominated = seed.Place;

      Assert.True(Prepare(seed, mint, out var ask, out var error), error?.ToJson());

      Assert.Equal("Spot 1", ask.Placement.Address);
      Assert.Equal("Spot 1", seed.Spot);
      Assert.True(mint.IsReserved("Spot 1"));
      Assert.Same(ask.Record, seed.Place);             // the click builds from the record...
      Assert.NotSame(nominated, seed.Place);           // ...never from what the model sent
      Assert.Equal(Slider, seed.Place["components"][0]["type"].GetValue<string>());
    }

    /// <summary>
    /// THE MINT COMES LAST. A reservation is a promise to a card; a refused ask has no card, so it reserves
    /// nothing and leaves the seed as it arrived.
    /// </summary>
    [Fact]
    public void A_refused_ask_reserves_nothing_and_writes_nothing()
    {
      var mint = new SpotMint();
      var seed = Seed(Component("Not A Thing"));
      var nominated = seed.Place;

      Assert.False(Prepare(seed, mint, out var ask, out var error));

      Assert.Null(ask);
      Assert.NotNull(error);
      Assert.Empty(mint.Reserved());
      Assert.Null(seed.Spot);
      Assert.Same(nominated, seed.Place);
    }

    /// <summary>Two cards outstanding never name one place, and a spot already grouped on the canvas is
    /// never re-issued.</summary>
    [Fact]
    public void Each_ask_gets_its_own_address_past_the_canvas_and_past_every_promise()
    {
      var mint = new SpotMint();

      Assert.True(Prepare(Seed(Component(Panel)), mint, out var first, out _, onCanvas: new[] { "Spot 1" }));
      Assert.True(Prepare(Seed(Component(Panel)), mint, out var second, out _, onCanvas: new[] { "Spot 1" }));

      Assert.Equal("Spot 2", first.Placement.Address);
      Assert.Equal("Spot 3", second.Placement.Address);
    }

    /// <summary>
    /// THE ROUTE-TO-PREPARE EDGE (the architect's review of #29). A build that names no region AND places
    /// nothing routes to the SPOT path - routing is on what was named, and nothing was - so the spot path is
    /// where it must be refused: in words that name the missing field, with nothing minted and nothing written.
    /// Otherwise an empty ask could become an empty card, which is an affordance asserting a target that does
    /// not exist (#321).
    /// </summary>
    [Theory]
    [InlineData("{\"kind\":\"build\",\"why\":\"x\"}")]
    [InlineData("{\"kind\":\"build\",\"why\":\"x\",\"place\":{\"components\":[]}}")]
    [InlineData("{\"kind\":\"build\",\"why\":\"x\",\"regions\":[],\"place\":{}}")]
    public void A_build_naming_no_region_and_placing_nothing_is_refused_and_mints_nothing(string request)
    {
      var mint = new SpotMint();
      Assert.True(AgentHostTools.TryParseRequest((JsonObject)JsonNode.Parse(request), out _, out _, out var seed,
                                                 out var parseError), parseError?.ToJson());

      var routing = SpotAsk.RouteFor(seed);
      Assert.Null(routing.Refusal);
      Assert.Equal(BuildAskRoute.Spot, routing.Route);

      Assert.False(Prepare(seed, mint, out var ask, out var error));
      Assert.Null(ask);
      Assert.Equal(ToolErrors.InvalidArgs, error.Code);
      Assert.Contains("\"place\"", error.Message);     // the words name the field to fill
      Assert.Empty(mint.Reserved());
      Assert.Null(seed.Spot);
    }

    /// <summary>
    /// THE FORGE STAYS CLOSED (the architect's review of #29). The spot address is the PLUGIN's: written in
    /// exactly one place, from the mint. If the model could name one, it could turn an approval for regions
    /// into a placement at an address of its choosing. That is true today only by construction - the
    /// published schema has no such field and the parse reads none - so this pins both halves, and goes red
    /// the day someone adds one "for convenience".
    /// </summary>
    [Fact]
    public void The_model_cannot_name_a_spot_or_its_option()
    {
      var schema = JsonNode.Parse(AgentHostTools.SchemaOf(AgentHostTools.RequestMutation));
      var spotKeys = PropertyNames(schema).Where(k => k.IndexOf("spot", StringComparison.OrdinalIgnoreCase) >= 0).ToList();
      Assert.Empty(spotKeys);

      var forged = (JsonObject)JsonNode.Parse(
        "{\"kind\":\"build\",\"why\":\"x\",\"regions\":[\"A1\"],\"spot\":\"Spot 1\",\"Spot\":\"Spot 1\",\"spotGrouped\":true}");
      Assert.True(AgentHostTools.TryParseRequest(forged, out _, out _, out var seed, out var error), error?.ToJson());
      Assert.Null(seed.Spot);
      Assert.False(seed.SpotGrouped);
    }

    /// <summary>Every property name declared anywhere in a JSON schema, however deeply nested.</summary>
    private static IEnumerable<string> PropertyNames(JsonNode node)
    {
      if (node is JsonObject o)
      {
        if (o["properties"] is JsonObject props)
          foreach (var kv in props) yield return kv.Key;
        foreach (var kv in o)
          foreach (var name in PropertyNames(kv.Value)) yield return name;
      }
      else if (node is JsonArray a)
        foreach (var item in a)
          foreach (var name in PropertyNames(item)) yield return name;
    }

    [Fact]
    public void With_no_mint_the_ask_refuses_rather_than_offering_an_address_it_cannot_promise()
    {
      var seed = Seed(Component(Panel));
      Assert.False(Prepare(seed, null, out var ask, out var error));
      Assert.Null(ask);
      Assert.Equal(ToolErrors.PreconditionRefused, error.Code);
      Assert.Null(seed.Spot);
    }

    // ---- #877 (c): THE LEDGER INVARIANT - a spot is one placement ---------------------------------------

    private static BuildLedger LedgerHolding(string address)
    {
      var ledger = new BuildLedger(new object());
      ledger.CommitBuild(address, new Dictionary<Guid, string> { [Guid.NewGuid()] = "as-left" });
      return ledger;
    }

    /// <summary>
    /// A spot the ledger already holds facts for has been placed, so a click that would place it again is
    /// refused - and the refusal names the spot. Every other spot, and a ledger that has never built, is
    /// untouched.
    /// </summary>
    [Fact]
    public void A_spot_the_ledger_already_holds_is_refused_and_no_other_is()
    {
      var ledger = LedgerHolding("Spot 1");

      var refusal = SpotAsk.RefuseIfPlaced(ledger, "Spot 1");
      Assert.NotNull(refusal);
      Assert.Contains("Spot 1", refusal);

      Assert.Null(SpotAsk.RefuseIfPlaced(ledger, "Spot 2"));
      Assert.Null(SpotAsk.RefuseIfPlaced(new BuildLedger(new object()), "Spot 1"));
      Assert.Null(SpotAsk.RefuseIfPlaced(null, "Spot 1"));   // a document that has never built has no ledger yet
    }

    /// <summary>
    /// WHY THE INVARIANT EXISTS BESIDE THE SIBLING TOKENS, shown rather than argued. The two options are minted
    /// here WITHOUT siblings - the world before #877 (a), or any route that bypasses it - so BOTH clicks are
    /// redeemed. The ledger still stops the second placement, because it reads what happened rather than which
    /// button was pressed.
    /// </summary>
    [Fact]
    public void With_the_sibling_tokens_bypassed_the_ledger_still_refuses_the_second_placement()
    {
      // U96 (Brief AV): two cards in one chat now retire each other, so the bypass is two separate stores - no sibling link and no
      // retirement - which is the state this test exists to reach: both tokens redeemable, the ledger the last defence.
      var tokens = new ConsentTokens();
      var otherStore = new ConsentTokens();
      var seed = new HandoffSeed { Spot = "Spot 1", Place = new JsonObject() };
      var loose = tokens.Mint("t", "build", seed.ForOption(grouped: false));      // no sibling: bypassed
      var grouped = otherStore.Mint("t", "build", seed.ForOption(grouped: true));

      var ledger = new BuildLedger(new object());

      Assert.True(tokens.Take(loose, "t", out _, out var first, out _));
      Assert.Null(SpotAsk.RefuseIfPlaced(ledger, first.Spot));                   // first click: unused, builds
      ledger.CommitBuild(first.Spot, new Dictionary<Guid, string> { [Guid.NewGuid()] = "as-left" });

      Assert.True(otherStore.Take(grouped, "t", out _, out var second, out _));  // the bypass: redeemed too
      Assert.NotNull(SpotAsk.RefuseIfPlaced(ledger, second.Spot));               // ...and refused at the build
    }
  }

  /// <summary>
  /// #876: THERE IS NO PER-TURN SPOT CAP, because the turn loop already bounds a model that keeps asking - a
  /// second refused request ends the turn. This pins that the SPOT PATH's refusals are ones the loop counts:
  /// the real loop, the real request parse, the real route and <see cref="SpotAsk.Prepare"/>, with only the
  /// canvas replaced by fixed resolvers.
  ///
  /// STATED BOUND: the panel's own host tool is GH-bound, so this drives the same functions it calls, in the
  /// order it calls them, rather than the panel itself. A success returns the shared shown-card envelope's
  /// shape, which is what the panel returns for every card.
  ///
  /// SERIAL WITH TurnShapeTests, for the reason AgentTurnDriverTests gives: a driven turn lands in that static
  /// record's denominator.
  /// </summary>
  [Collection(TurnShapeTests.StaticRecord)]
  public class SpotAskTurnTests
  {
    private static readonly SpecResolvers Canvas = new SpecResolvers
    {
      TypeExists = t => string.Equals(t, "Panel", StringComparison.OrdinalIgnoreCase),
      Nearest = t => new[] { "Panel" },
      IdState = id => IdState.Alive,
    };

    private const string Unbuildable =
      "{\"kind\":\"build\",\"why\":\"x\",\"place\":{\"components\":[{\"ref\":\"a\",\"type\":\"Not A Thing\"}]}}";

    [Fact]
    public void Two_refused_spot_asks_end_the_turn()
    {
      var mint = new SpotMint();
      var refusedBySpotPath = 0;

      string Ask(string json)
      {
        var args = AgentToolCore.ParseArgs(json, out var argError);
        if (argError != null) return argError.ToJson();
        if (!AgentHostTools.TryParseRequest(args, out _, out _, out var seed, out var parseError)) return parseError.ToJson();
        var routing = SpotAsk.RouteFor(seed);
        if (routing.Refusal != null || routing.Route != BuildAskRoute.Spot)
          return ToolResult.Fail(ToolErrors.InvalidArgs, "not the spot path").ToJson();
        if (SpotAsk.Prepare(seed, Canvas, _ => null, mint, new string[0], out _, out var spotError))
          return AgentTurnDriverTests.CardShown;
        refusedBySpotPath++;
        return spotError.ToJson();
      }

      var provider = new AgentTurnDriverTests.ScriptedProvider(
        AgentTurnDriverTests.Calls("t1", AgentHostTools.RequestMutation, Unbuildable),
        AgentTurnDriverTests.Calls("t2", AgentHostTools.RequestMutation, Unbuildable),
        AgentTurnDriverTests.Says("this step must never be requested"));

      var result = AgentTurnDriverTests.Drive(provider, call => Ask(call.ToolInputJson));

      Assert.Equal(2, refusedBySpotPath);            // both refusals were the spot path's own, not the parse's
      Assert.Equal(2, result.Steps);
      Assert.Equal(2, result.RefusedRequests);
      Assert.False(result.AskedForConsent);
      Assert.Equal(TurnOutcome.Complete, result.Outcome);
      Assert.Empty(mint.Reserved());                 // and no refused ask reserved an address
    }
  }
}
