using System;

namespace Dwight.Agent
{
  /// <summary>
  /// BUILD STYLE (U63): HOW THE USER WANTS A SCAFFOLD BUILT - as components, as scripts, or Dwight's choice. A setting, stated
  /// to the model as a plugin FACT at turn start (#573's lesson: an instruction the model must remember does not bind, a fact
  /// the plugin states does not need remembering) and named on the build card, so the user reads which style the act will use
  /// at the moment of consent.
  ///
  /// GH-free (ADR-091). It holds the three values, the read of a stored string, the fact and the card clause; the setting
  /// itself lives on DwightSettings and the control in the Settings panel.
  ///
  /// A SCRIPT IN A REGION BUILD RIDES THE CARD (U65). The fact tells the model how: list each script in request_mutation's
  /// "scripts" with the regions, so the card shows its whole source, then send the same source to build_region. The gate holds
  /// the build to the card (<see cref="BuildScriptGate"/>, <see cref="ScriptsAsk"/>), and the claim is tested against both.
  /// </summary>
  internal static class BuildStyle
  {
    public const string Components = "components";
    public const string Scripts = "scripts";
    public const string Auto = "auto";

    /// <summary>The three values, in the order the control shows them.</summary>
    public static readonly string[] All = { Components, Scripts, Auto };

    /// <summary>True for exactly the three values (the panel refuses anything else rather than storing it).</summary>
    public static bool IsValid(string value) => Array.IndexOf(All, value) >= 0;

    /// <summary>A stored value read back: an unknown, blank or hand-edited one is "components", the default, never an
    /// exception and never a style nobody chose.</summary>
    public static string Normalise(string value)
    {
      var v = (value ?? "").Trim().ToLowerInvariant();
      return IsValid(v) ? v : Components;
    }

    private static string HowToCarryAScript =>
      "A script in a region build goes on the card the user approves: list each one in request_mutation's \"scripts\" (region, " +
      "type, source, and any inputs or outputs it adds) beside the \"regions\", so the card shows its whole source before it runs, " +
      "then send build_region that same source, unchanged. A script that was not on the card is refused.";

    /// <summary>U79: the Rhino 8 script component's conventions, as facts. Each clause is true of the code: the interface rule is
    /// <c>ApplyScript</c> (defaults removed per declared side), the typing rule is <c>ScriptTypeMap</c> (a hint only where what
    /// feeds the input has a known type), and the C# shape is <c>UsingNormalizer</c> (a RunScript method or a complete
    /// Script_Instance class). Pinned by BuildStyleTests against those.</summary>
    internal const string ScriptConventions =
      "Script conventions (Rhino 8): a script that declares inputs has exactly those inputs, in place of x and y, and one that " +
      "declares outputs has exactly those outputs, in place of a, and x, y and a are names like any other; a script that declares neither " +
      "keeps x, y and a. A keyword of the script's language cannot be a port, and in C# that includes words such as value, file, group and set; nor can an output be called out. In a C# script one name cannot be " +
      "both an input and an output, and a side left undeclared keeps its defaults, so a C# input called a needs its outputs declared. In a Python 3 script: each declared output is a variable you assign; assign a list to emit a list, a " +
      "single value to emit one item; emit Rhino.Geometry types (Point3d, Line, Curve, Brep), never tuples or plain numbers for " +
      "geometry; an input arrives typed by its hint where what feeds it has a known type (a Line-fed input is a " +
      "Rhino.Geometry.Line), otherwise as generic data; `import Rhino.Geometry as rg` is available. In a C# script: the same " +
      "ports are the method's parameters, outputs are `ref` parameters you assign, and RhinoCommon types are in scope.";

    /// <summary>U80: how to choose the language. "C# compiles on placement" and "a compile error is a runtime message you will see in
    /// the verdict" are claims about BuildScriptTypes (a C# Script type is placeable), the shim (SetSource) and the verdict
    /// (ScriptBuildFacts.Errors, verbatim); pinned by BuildStyleTests.</summary>
    internal const string LanguageChoice =
      "Use Python 3 unless the user asks for C# or the work needs typed RhinoCommon APIs or speed on large loops; C# compiles on " +
      "placement and a compile error is a runtime message you will see in the verdict.";

    /// <summary>U81: the planner's half of a scripts build - a script collapses a chain, so there are fewer regions.</summary>
    internal const string FewerRegions =
      "Plan fewer, larger regions: one inputs region, one script region per algorithmic stage, one outputs region; do not " +
      "split a loop across regions.";

    /// <summary>The sentence the turn-start situation carries for this style. Plugin-authored; nothing the user or the model
    /// wrote reaches it.</summary>
    public static string Fact(string style)
    {
      switch (Normalise(style))
      {
        case Scripts:
          return "Build style: scripts - prefer one script component (" + BuildScriptTypes.Named + ") per region where the logic is a " +
                 "loop, a recursion or a formula; place inputs (sliders, panels, params) and outputs as components so they stay " +
                 "reachable. " + FewerRegions + " " + HowToCarryAScript + " " + ScriptConventions + " " + LanguageChoice;
        case Auto:
          return "Build style: let Dwight choose - components for inputs, outputs and anything the user will tweak, and a script " +
                 "(" + BuildScriptTypes.Named + ") for the algorithmic middle, where the logic is a loop, a recursion or a " +
                 "formula. Where the logic is algorithmic, plan fewer, larger regions: one inputs region, one script region per " +
                 "algorithmic stage, one outputs region; do not split a loop across regions. " + HowToCarryAScript + " " +
                 ScriptConventions + " " + LanguageChoice;
        default:
          return "Build style: components.";
      }
    }

    /// <summary>The clause the build card's authority line gains, after the region it names.</summary>
    public static string CardClause(string style)
    {
      switch (Normalise(style))
      {
        case Scripts:
          return ", building in scripts where the logic is algorithmic (each script's whole source is shown below)";
        case Auto:
          return ", choosing components or scripts as the logic suits (each script's whole source is shown below)";
        default:
          return ", as components";
      }
    }
  }
}
