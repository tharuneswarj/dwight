using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Dwight.Agent
{
  /// <summary>How a slider's values are constrained. Mirrors Grasshopper's own slider accuracy.</summary>
  internal enum SliderRounding { Float, Integer, Even, Odd }

  /// <summary>Which vocabulary a component's <c>state</c> speaks. A type that has no entry here takes no
  /// state at all, and saying so is a refusal rather than a silence (#208). <see cref="Constant"/> is U30's: a
  /// primitive param (Number, Integer, Text) born holding one value - a constant, neither a slider nor a valueless param.</summary>
  internal enum BuildStateKind { Slider, PanelText, Constant }

  /// <summary>Which primitive a <see cref="BuildStateKind.Constant"/> is: the param's own data type, so a Number's
  /// value is a number and a Text's is text, and nothing is coerced between them.</summary>
  internal enum ConstantType { Number, Integer, Text }

  /// <summary>
  /// WHO IS ASKING (U30). A build may place a constant; set_value changes a Number Slider or a Panel and nothing else -
  /// that is the published contract of a consent kind, and a parser that quietly widened it would make the kind table's
  /// claim false (#315). One parser, one table; the scope only decides which rows are in play, and the refusal lists the
  /// types the asking scope accepts.
  /// </summary>
  internal enum StateScope { SetValue, Build }

  /// <summary>
  /// THE INITIAL STATE OF A COMPONENT THIS BUILD PLACES (#377).
  ///
  /// THE DEFECT. A placed Number Slider is born with Grasshopper's factory state - domain 0 to 1, value
  /// 0.250, three decimal places - so a build that asked for "a count of 40 louvres" produced a slider
  /// reading 0.250 into a component expecting a count, and the definition solved to nothing. Every guard
  /// passed, correctly: the slider WAS placed and WAS wired. It was simply born starved.
  ///
  /// OWN-LEDGER ONLY, BY CONSTRUCTION (#377's boundary). State rides on a <see cref="BuildComponent"/>,
  /// and those are new objects by definition - so there is no representable way to reach an existing
  /// slider from here. Editing what the user already has is a different capability with different
  /// consequences (<c>set_value</c>), and it is homed there rather than smuggled in as a field here.
  ///
  /// WHERE THAT HOME ACTUALLY IS, corrected: <c>set_value</c> is being BUILT by the Phase-10 capability
  /// slice (stream B's U4) as a <c>request_mutation</c> kind and a catalogue tool with its own verdict -
  /// not by the #340 study, which this comment named until stream B started it. #340 owns the one-way
  /// flow question, never this capability. The boundary the paragraph above draws is unchanged and is the
  /// point: what a build PLACES and what a user ALREADY OWNS are different consent classes, whoever
  /// implements the second one.
  ///
  /// THE PLUGIN COMPUTES THE PRESENTATION, THE MODEL SUPPLIES THE JUDGEMENT (ADR-056). The model says
  /// what the numbers MEAN - a count from 0 to 100, starting at 40. It never sends a decimal-place count,
  /// because that is derivable from the numbers it already sent, and #146's rule is that a deterministic,
  /// plugin-computable quantity is never a billed round-trip.
  ///
  /// EVERYTHING HERE IS REFUSED AT PARSE TIME OR NOT AT ALL. The refusals below were earned by
  /// instrumenting the live GH_NumberSlider API rather than reasoning about it, and each one exists
  /// because Grasshopper's own behaviour in that case is to accept silently and be wrong:
  ///
  ///   - an INVERTED domain is accepted without complaint and yields a dead slider (min=100, max=0);
  ///   - a value OUTSIDE the domain is CLAMPED without complaint (250 into 0..10 becomes 10);
  ///   - a value off the accuracy's grid is SNAPPED without complaint (7.6 under Integer becomes 8).
  ///
  /// The third is why the verdict compares by EQUALITY and not by tolerance (#383). Integrality and
  /// parity are properties of the DECLARATION, checkable here with no reference to Grasshopper at all -
  /// so refusing "7.6 is not an integer" replicates none of GH's rounding and breaches none of #137. The
  /// declared value is on-grid before it is ever applied, GH therefore snaps nothing, and the read-back
  /// can be required to match exactly. A tolerance would have hidden the one failure that matters: a
  /// value that landed somewhere else entirely.
  /// </summary>
  internal sealed class BuildState
  {
    public BuildStateKind Kind { get; set; }

    // ---- slider ------------------------------------------------------------------------------------
    public decimal Min { get; set; }
    public decimal Max { get; set; }
    public decimal Value { get; set; }
    public SliderRounding Rounding { get; set; }

    /// <summary>
    /// PLUGIN-DERIVED, never sent by the model: the precision the declared numbers themselves carry.
    ///
    /// This is also what makes Float on-grid by construction. Grasshopper snaps a float slider to its
    /// decimal places, so a dp chosen independently of the value would re-introduce the very silent snap
    /// the parse refusals exist to prevent - 2.567 at dp=2 becomes 2.57. Derived from the declaration, the
    /// declaration is always exactly representable.
    /// </summary>
    public int DecimalPlaces { get; set; }

    // ---- panel -------------------------------------------------------------------------------------
    public string Text { get; set; }

    // ---- constant (U30) ----------------------------------------------------------------------------
    /// <summary>For <see cref="BuildStateKind.Constant"/>: which primitive. A Number or Integer carries its value in
    /// <see cref="Value"/>; a Text carries it in <see cref="Text"/>.</summary>
    public ConstantType ConstantType { get; set; }
  }

  /// <summary>A held state's VALUE in the user's words - the one thing a consent card or a success row needs beside
  /// the component's name: "40", "12.5", "\"ground\"". The domain belongs to set_value's fuller description.</summary>
  internal static class BuildStateText
  {
    public static string Value(BuildState s)
    {
      if (s == null) return null;
      switch (s.Kind)
      {
        case BuildStateKind.Slider: return Plain(s.Value);
        case BuildStateKind.Constant:
          return s.ConstantType == ConstantType.Text ? Quoted(s.Text) : Plain(s.Value);
        default: return Quoted(s.Text);
      }
    }

    /// <summary>What a component's NAME says it holds, on the card and in History: <see cref="Value"/>, or null when it holds no text. A diagnostic keeps <see cref="Value"/>,
    /// where "holds \"\"" is the fact.</summary>
    public static string NamedValue(BuildState s)
    {
      var v = Value(s);
      return v == "\"\"" ? null : v;
    }

    private static string Quoted(string text)
    {
      var t = (text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
      if (t.Length > 60) t = t.Substring(0, 59) + "…";
      return "\"" + t + "\"";
    }

    private static string Plain(decimal d) => d.ToString(System.Globalization.CultureInfo.InvariantCulture);
  }

  /// <summary>
  /// A component's WHOLE held state as one token, for the instruments that ask "did anything about this component
  /// change?" - the as-left record and the canvas stamp (#680).
  ///
  /// NOT for the behaviour fingerprint, which is right to read a slider's VALUE only: its question is behaviour, and a
  /// domain or rounding change that leaves the value where it was changes no output. The defect #680 names is the two
  /// other instruments BORROWING that token for a different question. Numbers are written without trailing zeros, so
  /// the same state read twice is the same token whatever scale the reader happened to return.
  /// </summary>
  internal static class StateToken
  {
    public static string Of(BuildState s)
    {
      if (s == null) return null;
      if (s.Kind == BuildStateKind.PanelText) return "panel:" + (s.Text ?? "");
      if (s.Kind == BuildStateKind.Constant)
        return "const:" + s.ConstantType + "@" + (s.ConstantType == ConstantType.Text ? (s.Text ?? "") : N(s.Value));
      return "slider:" + N(s.Min) + ".." + N(s.Max) + "@" + N(s.Value) + "/" + s.Rounding + "/dp" + s.DecimalPlaces;
    }

    private static string N(decimal d) => d.ToString("G29", System.Globalization.CultureInfo.InvariantCulture);
  }

  /// <summary>
  /// U59: a Number or Integer param's constant is a double. The spec carries it as a decimal (exact text of what the
  /// model wrote); these are the two seams between that and the double the param holds. Both go through text, because
  /// the direct casts are the lossy ones: <c>(decimal)double</c> rounds to 15 significant digits, so a read-back of
  /// tau would differ from the declaration and a correct build would be reverted as a mismatch.
  /// </summary>
  internal static class ConstantNumber
  {
    /// <summary>What a double always holds exactly as written.</summary>
    public const int SignificantDigits = 15;

    private static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;

    /// <summary>The double a declaration lands as - parsed from its text, never cast from the decimal.</summary>
    public static double ToDouble(decimal declared) =>
      double.Parse(declared.ToString(Inv), System.Globalization.NumberStyles.Float, Inv);

    /// <summary>The decimal a held double reads back as: its shortest round-trip text, not a 15-digit rounding.</summary>
    public static decimal FromDouble(double held)
    {
      try { return decimal.Parse(held.ToString("R", Inv), System.Globalization.NumberStyles.Float, Inv); }
      catch (OverflowException) { return (decimal)held; }
    }

    /// <summary>True when the double holding this declaration reads back as exactly the same number.</summary>
    public static bool HoldsExactly(decimal declared)
    {
      try { return FromDouble(ToDouble(declared)) == declared; }
      catch (OverflowException) { return false; }
    }

    /// <summary>The nearest value a double holds exactly as written.</summary>
    public static decimal Nearest(decimal declared) => FromDouble(ToDouble(declared));
  }

  internal static class BuildStateParser
  {
    /// <summary>Grasshopper's own slider precision cap, and therefore ours. A declaration finer than this
    /// cannot be held exactly, so it is refused rather than silently rounded.</summary>
    public const int MaxDecimalPlaces = 6;

    /// <summary>
    /// Which component types accept a <c>state</c>, and which vocabulary each speaks. Deliberately a
    /// SHORT, EXPLICIT list: a type absent from it is refused by name with this list, so a state the
    /// plugin would ignore can never be silently accepted under a verdict saying the build landed as
    /// specified - the #208 class, which arrives in exactly this shape.
    /// </summary>
    private static readonly Dictionary<string, BuildStateKind> Accepts =
      new Dictionary<string, BuildStateKind>(StringComparer.OrdinalIgnoreCase)
      {
        ["Number Slider"] = BuildStateKind.Slider,
        ["Panel"] = BuildStateKind.PanelText,
      };

    /// <summary>
    /// U30: THE PRIMITIVE PARAMS A BUILD MAY PLACE WITH A VALUE - a constant. Seen twice at a sitting: three
    /// constant sliders on a staircase (a slider nobody will drag is the wrong component), and a valueless
    /// <c>Number</c> that cost a 23-component rebuild. BUILD SCOPE ONLY: set_value's contract names a Number
    /// Slider or a Panel and this table must not widen it in silence (#315). Short and explicit, like
    /// <see cref="Accepts"/>: a param absent here is refused by name with the list the asking scope accepts.
    /// </summary>
    private static readonly Dictionary<string, ConstantType> Constants =
      new Dictionary<string, ConstantType>(StringComparer.OrdinalIgnoreCase)
      {
        ["Number"] = ConstantType.Number,
        ["Integer"] = ConstantType.Integer,
        ["Text"] = ConstantType.Text,
      };

    /// <summary>The types set_value accepts - the slider and the panel, exactly as its kind table says.</summary>
    public static IEnumerable<string> TypesThatAcceptState => Accepts.Keys;

    /// <summary>The types a BUILD accepts a state for: set_value's two, plus the primitive constants (U30).</summary>
    public static IEnumerable<string> TypesThatAcceptStateInBuild => Accepts.Keys.Concat(Constants.Keys);

    public static bool AcceptsState(string type) =>
      !string.IsNullOrWhiteSpace(type) && Accepts.ContainsKey(Dwight.Components.ComponentMatchCore.BareName(type));

    /// <summary>
    /// Parse and validate one component's declared initial state in set_value's scope - a Number Slider or a
    /// Panel. Returns true with a null <paramref name="state"/> when none was declared - the ordinary case, and
    /// never an error.
    /// </summary>
    public static bool TryParse(JsonNode node, string type, string reference,
      out BuildState state, out ToolResult error) =>
      TryParse(node, type, reference, StateScope.SetValue, out state, out error);

    /// <summary>
    /// The same parse in the asking <paramref name="scope"/>: a build may also declare a CONSTANT on a Number,
    /// Integer or Text param (U30). One parser, one table per vocabulary; the scope decides which rows are in play
    /// and the refusal lists the types that scope accepts.
    /// </summary>
    public static bool TryParse(JsonNode node, string type, string reference, StateScope scope,
      out BuildState state, out ToolResult error)
    {
      state = null;
      error = null;

      if (node == null) return true;                       // no state declared: the common case

      var where = " (component \"" + reference + "\")";

      if (!(node is JsonObject obj))
      {
        error = ToolResult.Fail(ToolErrors.InvalidArgs,
          "\"state\" must be an object" + where + ".");
        return false;
      }

      // U40 (#975): the decision runs on the CANONICAL name. "Params > Primitive > Number" is a Number; judged on the raw
      // string it was refused as a type that takes no state, with the accepted list naming "Number" in the same breath.
      var t = Dwight.Components.ComponentMatchCore.BareName(type);
      if (scope == StateScope.Build && Constants.TryGetValue(t, out var constant))
        return TryConstant(obj, t, constant, where, out state, out error);

      if (!Accepts.TryGetValue(t, out var kind))
      {
        var accepted = scope == StateScope.Build ? TypesThatAcceptStateInBuild : TypesThatAcceptState;
        error = ToolResult.Fail(ToolErrors.InvalidArgs,
          "\"" + t + "\" does not take a \"state\"" + where + ".",
          new JsonObject
          {
            ["asked"] = type ?? "",
            // Derived from the dictionaries that actually DECIDE, never a list typed out beside them: a
            // published list that can disagree with the behaviour it describes is the #200/#202 class,
            // and it is cheapest to make unrepresentable at the moment the list is written.
            ["typesThatAcceptState"] = new JsonArray(
              accepted.OrderBy(k => k, StringComparer.Ordinal).Select(k => (JsonNode)k).ToArray()),
          });
        return false;
      }

      return kind == BuildStateKind.Slider
        ? TrySlider(obj, where, out state, out error)
        : TryPanel(obj, where, out state, out error);
    }

    /// <summary>
    /// A CONSTANT (U30): a Number or Integer takes <c>value</c> alone, a Text takes <c>text</c> alone. Every other key
    /// is refused by name - a Number given min and max is a slider that was asked for by the wrong type, and the
    /// refusal says so rather than dropping the domain. The value's TYPE is the param's: a number for a Number, a
    /// whole number for an Integer (7.6 is refused with its neighbours, as a slider's is), text for a Text.
    /// </summary>
    private static bool TryConstant(JsonObject obj, string type, ConstantType constant, string where,
      out BuildState state, out ToolResult error)
    {
      state = null;
      error = null;

      if (constant == ConstantType.Text)
      {
        if (obj["min"] != null || obj["max"] != null || obj["value"] != null || obj["rounding"] != null)
        {
          error = ToolResult.Fail(ToolErrors.InvalidArgs,
            "A Text param's state takes only \"text\"" + where + " - it holds one piece of text.");
          return false;
        }
        string text;
        try { text = obj["text"]?.GetValue<string>(); } catch { text = null; }
        if (text == null)
        {
          error = ToolResult.Fail(ToolErrors.InvalidArgs, "A Text param's state needs \"text\"" + where + ".");
          return false;
        }
        state = new BuildState { Kind = BuildStateKind.Constant, ConstantType = ConstantType.Text, Text = text };
        return true;
      }

      if (obj["min"] != null || obj["max"] != null || obj["rounding"] != null)
      {
        error = ToolResult.Fail(ToolErrors.InvalidArgs,
          "A " + type + " param's state takes only \"value\"" + where + " - it is a CONSTANT, not a slider. " +
          "For a value the user will drag, place a Number Slider with min, max and value instead.");
        return false;
      }
      if (obj["text"] != null)
      {
        error = ToolResult.Fail(ToolErrors.InvalidArgs,
          "A " + type + " param holds one number, not text" + where + " - give \"value\"; for text, place a Text param or a Panel.");
        return false;
      }
      var node = obj["value"];
      if (node == null)
      {
        error = ToolResult.Fail(ToolErrors.InvalidArgs, "A " + type + " param's state needs \"value\"" + where + ".");
        return false;
      }

      decimal value;
      if (!TryNumber(node, out value))
      {
        error = ToolResult.Fail(ToolErrors.InvalidArgs,
          "\"value\" must be a number" + where + " - a " + type + " param holds one number. For text, place a Text param or a Panel.");
        return false;
      }

      if (constant == ConstantType.Integer && !OnGrid(value, SliderRounding.Integer, out var low, out var high))
      {
        error = ToolResult.Fail(ToolErrors.InvalidArgs,
          Plain(value) + " is not a whole number" + where + "; declare " + Plain(low) + " or " + Plain(high) +
          ", or place a Number param for a fraction.");
        return false;
      }

      // U59: a constant in a Number or Integer param is a DOUBLE, not a slider. The slider's six-place cap is
      // Grasshopper's slider precision and stays the slider's rule; here only what a double cannot hold exactly as
      // written is refused (15 significant digits always round-trip, and so does 6.283185307179586).
      if (!ConstantNumber.HoldsExactly(value))
      {
        error = ToolResult.Fail(ToolErrors.InvalidArgs,
          "A constant in a " + type + " param is a double, which holds about " + ConstantNumber.SignificantDigits +
          " significant digits exactly, and " + Plain(value) + " is finer than a double can hold" + where +
          " - declare " + Plain(ConstantNumber.Nearest(value)) + " or fewer digits.");
        return false;
      }

      state = new BuildState { Kind = BuildStateKind.Constant, ConstantType = constant, Value = value };
      return true;
    }

    private static bool TryNumber(JsonNode node, out decimal value)
    {
      value = 0;
      if (node is JsonValue v && v.TryGetValue<string>(out _)) return false;   // "12" is text, not a number: never coerced
      try { value = node.GetValue<decimal>(); return true; } catch { }
      try { value = (decimal)node.GetValue<double>(); return true; } catch { }
      return false;
    }

    private static bool TryPanel(JsonObject obj, string where, out BuildState state, out ToolResult error)
    {
      state = null;
      error = null;

      if (obj["min"] != null || obj["max"] != null || obj["value"] != null || obj["rounding"] != null)
      {
        error = ToolResult.Fail(ToolErrors.InvalidArgs,
          "A Panel's state takes only \"text\"" + where + ".");
        return false;
      }

      string text;
      try { text = obj["text"]?.GetValue<string>(); }
      catch { text = null; }

      if (text == null)
      {
        error = ToolResult.Fail(ToolErrors.InvalidArgs,
          "A Panel's state needs \"text\"" + where + ".");
        return false;
      }

      state = new BuildState { Kind = BuildStateKind.PanelText, Text = text };
      return true;
    }

    private static bool TrySlider(JsonObject obj, string where, out BuildState state, out ToolResult error)
    {
      state = null;
      error = null;

      if (obj["text"] != null)
      {
        error = ToolResult.Fail(ToolErrors.InvalidArgs,
          "A Number Slider's state takes \"min\", \"max\", \"value\" and \"rounding\" - not \"text\"" + where + ".");
        return false;
      }

      // ALL THREE OR NONE. A domain is not a thing you can declare half of: the half left undeclared
      // would silently keep the factory 0-to-1, which is the defect this whole unit exists to close.
      if (!Number(obj, "min", where, out var min, out error) ||
          !Number(obj, "max", where, out var max, out error) ||
          !Number(obj, "value", where, out var value, out error))
        return false;

      var rounding = SliderRounding.Float;
      var rawRounding = obj["rounding"];
      if (rawRounding != null)
      {
        string name;
        try { name = rawRounding.GetValue<string>(); } catch { name = null; }
        if (!Enum.TryParse(name ?? "", true, out rounding) || !Enum.IsDefined(typeof(SliderRounding), rounding))
        {
          error = ToolResult.Fail(ToolErrors.InvalidArgs,
            "\"" + name + "\" is not a slider rounding" + where + ".",
            new JsonObject { ["available"] = new JsonArray(new JsonNode[] { "float", "integer", "even", "odd" }) });
          return false;
        }
      }

      // Grasshopper accepts an inverted domain in silence and produces a slider that cannot be moved.
      if (min > max)
      {
        error = ToolResult.Fail(ToolErrors.InvalidArgs,
          "The slider's minimum (" + Plain(min) + ") is above its maximum (" + Plain(max) + ")" + where + ".");
        return false;
      }

      // AN EVEN OR ODD SLIDER'S BOUNDS ARE ON PARITY TOO (#658(1), #668). Measured on the authoritative
      // path (#658): an even slider declared 1 to 99 landed as 0 to 98, and the equality check reverted
      // it - a billed round-trip for a failure class the plugin can compute (#146). The refusal names
      // both legal neighbours and claims NO mapping: the neighbours are true whatever Grasshopper does
      // with an off-parity bound, and 1->0 / 99->98 is one measurement, not a rule we reproduce (#137).
      if (!BoundOnParity("minimum", min, rounding, where, out error) ||
          !BoundOnParity("maximum", max, rounding, where, out error))
        return false;

      // Grasshopper clamps a value outside the domain in silence, so the slider would sit on a number
      // nobody asked for and every check would still pass.
      if (value < min || value > max)
      {
        error = ToolResult.Fail(ToolErrors.InvalidArgs,
          "The slider's value " + Plain(value) + " is outside its domain " + Plain(min) + " to " +
          Plain(max) + where + ". Widen the domain or move the value inside it.");
        return false;
      }

      // OFF-GRID DECLARATIONS ARE REFUSED, NOT ROUNDED (#383). These are properties of the number the
      // model wrote down - is it whole, is it even - and checking them borrows nothing from Grasshopper's
      // rounding. Refusing here is what lets the verdict later demand exact equality.
      if (!OnGrid(value, rounding, out var nearestLow, out var nearestHigh))
      {
        error = ToolResult.Fail(ToolErrors.InvalidArgs,
          Plain(value) + " is not " + Constraint(rounding) + where + "; declare " +
          Plain(nearestLow) + " or " + Plain(nearestHigh) + ".");
        return false;
      }

      var places = Math.Max(Scale(min), Math.Max(Scale(max), Scale(value)));
      if (rounding != SliderRounding.Float) places = 0;
      if (places > MaxDecimalPlaces)
      {
        error = ToolResult.Fail(ToolErrors.InvalidArgs,
          "A slider holds at most " + MaxDecimalPlaces + " decimal places, and " + Plain(value) +
          " needs " + places + where + ".");
        return false;
      }

      state = new BuildState
      {
        Kind = BuildStateKind.Slider,
        Min = min, Max = max, Value = value,
        Rounding = rounding,
        DecimalPlaces = places,
      };
      return true;
    }

    /// <summary>
    /// Is the declared value representable under this accuracy? Integrality and parity only - no rounding
    /// rule of Grasshopper's is reproduced here, which is precisely why this is allowed to be our check
    /// (#137). The two neighbours are handed back so the refusal repairs rather than blocks (ADR-092).
    /// </summary>
    /// <remarks>Made reachable for set_value (Phase-9 U4), whose restore rule asks the same question of the
    /// slider's CURRENT value against the NEW rounding - one grid check, never two (the "do not fork" rule).</remarks>
    public static bool IsOnGrid(decimal value, SliderRounding rounding, out decimal low, out decimal high) =>
      OnGrid(value, rounding, out low, out high);

    private static bool OnGrid(decimal value, SliderRounding rounding, out decimal low, out decimal high)
    {
      low = value; high = value;
      if (rounding == SliderRounding.Float) return true;   // on-grid by construction: dp follows the value

      var floor = decimal.Floor(value);
      if (rounding == SliderRounding.Integer)
      {
        if (value == floor) return true;
        low = floor; high = floor + 1;
        return false;
      }

      // Even / Odd: the value must be whole AND have the right parity.
      var wantEven = rounding == SliderRounding.Even;
      if (value == floor && IsEven(floor) == wantEven) return true;

      low = floor;
      while (!(low == decimal.Floor(low) && IsEven(low) == wantEven)) low -= 1;
      high = low + 2;
      return false;
    }

    /// <summary>An Even or Odd slider's bound must itself be whole and of the slider's parity. Float and
    /// Integer sliders are not this rule's (#668). Internal, not private, so the loaded-build ladder can name it
    /// through nameof.</summary>
    internal static bool BoundOnParity(string bound, decimal d, SliderRounding rounding, string where, out ToolResult error)
    {
      error = null;
      if (rounding != SliderRounding.Even && rounding != SliderRounding.Odd) return true;
      if (OnGrid(d, rounding, out var low, out var high)) return true;

      error = ToolResult.Fail(ToolErrors.InvalidArgs,
        "The slider's " + bound + " " + Plain(d) + " is not " + Constraint(rounding) + where + "; declare " +
        Plain(low) + " or " + Plain(high) + ".");
      return false;
    }

    private static bool IsEven(decimal whole) => Math.Abs(whole % 2) == 0;

    /// <summary>The constraint in the user's words, so the refusal reads as an instruction.</summary>
    private static string Constraint(SliderRounding r)
    {
      switch (r)
      {
        case SliderRounding.Integer: return "an integer";
        case SliderRounding.Even: return "an even number";
        case SliderRounding.Odd: return "an odd number";
        default: return "representable";
      }
    }

    /// <summary>The number of decimal places a declaration actually carries.</summary>
    private static int Scale(decimal d) => (decimal.GetBits(d)[3] >> 16) & 0xFF;

    private static string Plain(decimal d) =>
      d.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static bool Number(JsonObject obj, string field, string where, out decimal value, out ToolResult error)
    {
      value = 0;
      error = null;

      var node = obj[field];
      if (node == null)
      {
        error = ToolResult.Fail(ToolErrors.InvalidArgs,
          "A Number Slider's state needs \"min\", \"max\" and \"value\" together - \"" + field +
          "\" is missing" + where + ".");
        return false;
      }

      try { value = node.GetValue<decimal>(); return true; }
      catch { }
      try { value = (decimal)node.GetValue<double>(); return true; }
      catch { }

      error = ToolResult.Fail(ToolErrors.InvalidArgs,
        "\"" + field + "\" must be a number" + where + ".");
      return false;
    }
  }
}
