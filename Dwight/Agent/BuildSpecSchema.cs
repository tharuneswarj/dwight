using System.Text.Json.Nodes;

namespace Dwight.Agent
{
  /// <summary>
  /// WHAT A COMPONENT DECLARATION LOOKS LIKE ON THE WIRE - published once, for every tool that takes one (U18).
  ///
  /// Two tools now accept the same shape: <c>build_region</c> (the components it places into a region) and
  /// <c>request_mutation</c>'s <c>place</c> (the components a spot card will name and the click will build).
  /// Both are PARSED by one definition already - <see cref="BuildSpecParser"/>, through
  /// <see cref="SpecResolvers"/> - so this is the other half: what is PUBLISHED about that shape has one
  /// definition too (#248). Two schemas describing one parser would agree today and drift the day someone
  /// edited one, and the model would then be told two different things about the same fields.
  ///
  /// WHY IT IS A NEW FILE AND NOT A HELPER INSIDE THE CATALOGUE. <c>build_region</c>'s declaration is built
  /// inline in <c>AgentToolCatalog.cs</c>, which another lane owns. Rather than reach into that file, this
  /// helper is used by <c>place</c>, and a test asserts <c>build_region</c>'s published sub-schema is
  /// SEMANTICALLY equal to it - so drift goes red, by name, for whoever changes either side. Switching
  /// <c>build_region</c> to call this is that lane's option, not an obligation.
  ///
  /// Each call builds FRESH nodes: a JsonNode has one parent, so a shared instance would be one node with two
  /// owners, and editing one tool's schema would silently edit the other's (the catalogue's own rule).
  /// </summary>
  internal static class BuildSpecSchema
  {
    /// <summary>The components a spec places: a ref wires can name, an installed type, an optional nick,
    /// and the optional state a Number Slider, a Panel, or a Number/Integer/Text constant is born with (U30).</summary>
    /// <summary>Brief CP: a script's declared inputs, each a name with its access beside it. One declaration for a build's components and a region card's scripts, because both go through one parser.</summary>
    public static JsonObject ScriptInputs(string description) =>
      Sch.Arr(Sch.Sub("One input.", Sch.Props(
        ("name", Sch.Str("Exactly as the code reads it.")),
        ("access", Sch.Enum("How it receives its data. " + PortAccess.Rule, PortAccess.Item, PortAccess.List, PortAccess.Tree))),
        "name"), description);

    public static JsonObject Components() => Sch.Arr(Sch.Sub(null, Sch.Props(
      ("ref", Sch.Str("A label for this component, used by wires in this spec.")),
      ("type", Sch.Str("An INSTALLED component type name. Where one bare name is shared by more than one " +
        "installed component (Rectangle, Line and Circle each name a param AND a component), the bare name is " +
        "refused with the qualified forms, and you give one of those instead: \"Category > SubCategory > Name\", " +
        "for example \"Curve > Primitive > Rectangle\".")),
      ("nick", Sch.Str()),
      ("state", Sch.Sub("Optional. The value this component STARTS " +
        "with. A Number Slider placed without one is born at Grasshopper's factory setting - domain 0 " +
        "to 1, value 0.250 - so a slider you mean as a count will read 0.250 and the definition will " +
        "solve to nothing. Number Slider takes min, max, value and optional rounding " +
        "(float/integer/even/odd). Panel takes text. A CONSTANT - a fixed number nobody will drag - is a " +
        "Number or Integer param with value alone (whole for Integer), or a Text param with text alone: " +
        "neither a slider nor a param left empty. No other type takes a state.", Sch.Props(
        ("min", Sch.Num("Number Slider only.")),
        ("max", Sch.Num("Number Slider only.")),
        ("value", Sch.Num("Number Slider: must lie within min..max, and must be exactly " +
          "representable under the rounding - declare 7 or 8, never 7.6, for an integer slider. " +
          "Number or Integer param: the constant it holds.")),
        ("rounding", Sch.Enum("Number Slider only.", "float", "integer", "even", "odd")),
        ("text", Sch.Str("Panel or Text param only."))))),
      // U19 (#898): a script is one more component, not a new kind. Its language is its TYPE.
      ("source", Sch.Str("Only on type \"Python 3 Script\" or \"C# Script\", and ONLY where the user was shown " +
        "it: in a placement made without a map (request_mutation's place), or in a map region whose approved card listed this " +
        "script in request_mutation's \"scripts\" - build_region refuses source that card did not show, and a source that " +
        "differs from the one shown by a character. The COMPLETE script, not a " +
        "fragment: the user is shown all of it, untruncated, before it runs, and THE CODE RUNS IN THEIR " +
        "DEFINITION. A new script component has inputs x and y and an output a; declaring \"inputs\" replaces x and y, " +
        "declaring \"outputs\" replaces a, and the code reads and writes the ports it has by name. A script that does not compile (C#) or run (Python), or that produces nothing " +
        "although it was fed, takes the whole placement back with it. On any other type this is refused.")),
      ("inputs", ScriptInputs("Script types only, with \"source\". Inputs that REPLACE x and y, in order, each named exactly " +
        "as the code reads it (letters, digits, underscore; x, y and a are names like any other, and a keyword of the script's language is refused). Wire to them like any port. " +
        "Name only what the code reads - Dwight computes the port's type.")),
      ("outputs", Sch.Arr(Sch.Str(), "Script types only, with \"source\". Output names that REPLACE a, in order, exactly as the " +
        "code assigns them (letters, digits, underscore; x, y and a are names like any other, and a keyword of the script's language or out is refused). In a C# script one " +
        "name cannot be both an input and an output, and a side left undeclared keeps its defaults, so a C# input called a needs \"outputs\" declared."))),
      "ref", "type"));

    /// <summary>The wires a spec declares: each end either a ref from this spec or the id of an existing
    /// component, with a param only where the end has more than one port.</summary>
    public static JsonObject Wires() => Sch.Arr(Sch.Sub(null, Sch.Props(
      ("from", Sch.Sub(null, Sch.Props(("ref", Sch.Str()), ("id", Sch.Int()), ("param", Sch.Str())))),
      ("to", Sch.Sub(null, Sch.Props(("ref", Sch.Str()), ("id", Sch.Int()), ("param", Sch.Str()))))),
      "from", "to"));
  }
}
