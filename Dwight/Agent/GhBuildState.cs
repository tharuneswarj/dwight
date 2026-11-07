using System;
using System.Linq;
using Grasshopper.GUI.Base;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Special;
using Grasshopper.Kernel.Types;

namespace Dwight.Agent
{
  /// <summary>
  /// Apply a declared <see cref="BuildState"/> to a freshly placed component, and read it back (#377).
  ///
  /// THE ORDER IS NOT NEGOTIABLE, AND IT WAS MEASURED, NOT REASONED ABOUT. Instrumenting the live
  /// GH_NumberSlider through the bridge produced one result that no amount of reading the API would have
  /// suggested: setting the VALUE before the DOMAIN silently corrupts it. A slider set to 40 and then
  /// given the domain 0-to-10 ends up holding 1 - the value was clamped by the factory 0-to-1 domain it
  /// still had at the time, and widening the domain afterwards does not restore it. So: accuracy, then
  /// decimal places, then minimum, then maximum, then value. Every step of that sequence is load-bearing.
  ///
  /// WHAT THIS DELIBERATELY DOES NOT DO is decide whether the result is acceptable. It applies and it
  /// reports; <see cref="Matches"/> is consulted by the completeness leg, and a mismatch reverts the whole
  /// build like any other incomplete one. The check is EQUALITY, never a tolerance (#383): off-grid
  /// declarations are refused at parse time, so by the time a value reaches here Grasshopper has nothing
  /// to snap and any difference at all is a real defect.
  ///
  /// The read-back is the AUTHORITY (#137). Nothing here re-derives what Grasshopper would do with a
  /// number - it sets the number, asks the slider what it holds, and compares. That is also why the
  /// verification window is the right one: <see cref="GhBuildRegion"/> reads state back inside the
  /// SolutionEnd handler, so anything AddObject or the solve itself did to the slider is already inside
  /// the compared region, and a wrong assumption here fails loudly instead of shipping quietly.
  /// </summary>
  internal static class GhBuildState
  {
    public static void Apply(IGH_DocumentObject obj, BuildState state)
    {
      if (obj == null || state == null) return;

      try
      {
        if (state.Kind == BuildStateKind.Slider && obj is GH_NumberSlider slider)
        {
          slider.Slider.Type = Accuracy(state.Rounding);
          slider.Slider.DecimalPlaces = state.DecimalPlaces;
          slider.Slider.Minimum = state.Min;
          slider.Slider.Maximum = state.Max;
          slider.Slider.Value = state.Value;      // last, always - see the note above
        }
        else if (state.Kind == BuildStateKind.PanelText && obj is GH_Panel panel)
        {
          panel.UserText = state.Text;
        }
        // U30: a CONSTANT is the param's own persistent data - one item, the type the param holds. Cleared first so a
        // param born with anything (it is born empty, but the read-back demands exactly one item) holds only this.
        else if (state.Kind == BuildStateKind.Constant)
        {
          switch (obj)
          {
            case Param_Number pn when state.ConstantType == ConstantType.Number:
              pn.PersistentData.Clear();
              pn.PersistentData.Append(new GH_Number(ConstantNumber.ToDouble(state.Value)));
              break;
            case Param_Integer pi when state.ConstantType == ConstantType.Integer:
              pi.PersistentData.Clear();
              pi.PersistentData.Append(new GH_Integer((int)state.Value));
              break;
            case Param_String ps when state.ConstantType == ConstantType.Text:
              ps.PersistentData.Clear();
              ps.PersistentData.Append(new GH_String(state.Text ?? ""));
              break;
          }
        }
      }
      catch { /* a failure to apply shows up as a mismatch below, which reverts the build */ }
    }

    /// <summary>
    /// Does the component actually hold what was declared? Reports the FIRST difference in the user's
    /// terms, because this string ends up explaining a reverted build.
    ///
    /// A type that is not the class its state vocabulary belongs to is a mismatch, never a shrug: the
    /// spec named a type that accepts state and got something else, and papering over that is the #208
    /// class exactly - a verdict resting on a resolution step that quietly gave up.
    /// </summary>
    public static bool Matches(IGH_DocumentObject obj, BuildState state, out string mismatch)
    {
      mismatch = null;
      if (state == null) return true;

      if (state.Kind == BuildStateKind.Slider)
      {
        if (!(obj is GH_NumberSlider slider))
        {
          mismatch = "a slider state was declared, but the placed component is not a Number Slider";
          return false;
        }

        var s = slider.Slider;
        if (s.Minimum != state.Min || s.Maximum != state.Max)
        {
          mismatch = "the slider's domain is " + Plain(s.Minimum) + " to " + Plain(s.Maximum) +
                     ", not the declared " + Plain(state.Min) + " to " + Plain(state.Max);
          return false;
        }
        if (s.Value != state.Value)
        {
          mismatch = "the slider holds " + Plain(s.Value) + ", not the declared " + Plain(state.Value);
          return false;
        }
        if (s.Type != Accuracy(state.Rounding))
        {
          mismatch = "the slider's rounding is " + s.Type + ", not the declared " + state.Rounding;
          return false;
        }
        return true;
      }

      // U30: a constant reads back as exactly one persistent item of the declared value - equality, never a tolerance
      // (#383), because the declaration was on the param's own grid before it was applied.
      if (state.Kind == BuildStateKind.Constant)
      {
        var paramName = ParamName(state.ConstantType);
        if (!IsConstantParam(obj, state.ConstantType))
        {
          mismatch = "a constant was declared, but the placed component is not a " + paramName + " param";
          return false;
        }
        var held = ReadConstant(obj);
        if (held == null)
        {
          mismatch = "the " + paramName + " param holds nothing, not the declared " + BuildStateText.Value(state);
          return false;
        }
        var same = state.ConstantType == ConstantType.Text
          ? string.Equals(held.Text, state.Text, StringComparison.Ordinal)
          : held.Value == state.Value;
        if (!same)
        {
          mismatch = "the " + paramName + " param holds " + BuildStateText.Value(held) + ", not the declared " + BuildStateText.Value(state);
          return false;
        }
        return true;
      }

      if (!(obj is GH_Panel panel))
      {
        mismatch = "panel text was declared, but the placed component is not a Panel";
        return false;
      }
      if (!string.Equals(panel.UserText, state.Text, StringComparison.Ordinal))
      {
        mismatch = "the panel's text is not what was declared";
        return false;
      }
      return true;
    }

    /// <summary>
    /// What this component ACTUALLY HOLDS, named, for the region's success row (#394).
    ///
    /// THE CHEAPEST WITNESS AGAINST A REPEAT OF #377 IS THE NUMBER ITSELF, ON SCREEN, WHEN IT LANDS. The
    /// defect that began this cycle was a value nobody could see: seven sliders sitting at 0.250 while
    /// every surface said built and verified, and it took a person opening the canvas to notice. The
    /// verdict correctly reports state only on a MISMATCH - that is the verdict's job - but the row the
    /// user reads is where an unnoticed value becomes noticeable.
    ///
    /// It reads the OBJECT, never the declaration. Equality has already been enforced by the time this is
    /// called, so the two agree - but reading the declaration would make this a restatement of the ask
    /// rather than a report of the result, and #153's rule is that the text shown IS the text applied.
    /// The moment those can diverge is exactly the moment this line has to be the one that noticed.
    /// </summary>
    public static string Describe(IGH_DocumentObject obj, BuildState state)
    {
      if (obj == null || state == null) return null;

      var name = obj.NickName;
      if (string.IsNullOrWhiteSpace(name)) name = obj.Name;

      try
      {
        if (state.Kind == BuildStateKind.Slider && obj is GH_NumberSlider slider)
          return name + "=" + Plain(slider.Slider.Value);

        if (state.Kind == BuildStateKind.PanelText && obj is GH_Panel panel)
        {
          var text = (panel.UserText ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
          if (text.Length > 24) text = text.Substring(0, 23) + "…";
          return name + "=\"" + text + "\"";
        }

        // U30: the constant the param actually holds, read off the object like the other two.
        if (state.Kind == BuildStateKind.Constant)
        {
          var held = ReadConstant(obj);
          return held == null ? name + "=(nothing)" : name + "=" + BuildStateText.Value(held);
        }
      }
      catch { }

      return null;
    }

    /// <summary>
    /// What a Number Slider or a Panel holds NOW, in the shared vocabulary - null for any other type (#680). The one
    /// reader: set_value's live read, the as-left record and the canvas stamp all take a component's held state from
    /// here, so no two of them can read a slider differently.
    /// </summary>
    public static BuildState Read(IGH_DocumentObject obj)
    {
      try
      {
        if (obj is GH_NumberSlider s)
          return new BuildState
          {
            Kind = BuildStateKind.Slider,
            Min = s.Slider.Minimum, Max = s.Slider.Maximum, Value = s.Slider.Value,
            Rounding = Rounding(s.Slider.Type), DecimalPlaces = s.Slider.DecimalPlaces,
          };
        if (obj is GH_Panel p)
          return new BuildState { Kind = BuildStateKind.PanelText, Text = p.UserText ?? "" };
        // U30: a Number, Integer or Text param holding exactly ONE persistent item is a constant. Anything else it
        // holds (nothing, or several items) is not a state this vocabulary speaks, so it reads as null - as before.
        return ReadConstant(obj);
      }
      catch { }
      return null;
    }

    /// <summary>The one persistent item of a Number, Integer or Text param, as a constant - or null when the param
    /// holds anything but exactly one item, or is not one of those three.</summary>
    private static BuildState ReadConstant(IGH_DocumentObject obj)
    {
      try
      {
        switch (obj)
        {
          case Param_Number pn when pn.PersistentDataCount == 1:
          {
            var g = pn.PersistentData.AllData(true).OfType<GH_Number>().FirstOrDefault();
            return g == null ? null : new BuildState { Kind = BuildStateKind.Constant, ConstantType = ConstantType.Number, Value = ConstantNumber.FromDouble(g.Value) };
          }
          case Param_Integer pi when pi.PersistentDataCount == 1:
          {
            var g = pi.PersistentData.AllData(true).OfType<GH_Integer>().FirstOrDefault();
            return g == null ? null : new BuildState { Kind = BuildStateKind.Constant, ConstantType = ConstantType.Integer, Value = g.Value };
          }
          case Param_String ps when ps.PersistentDataCount == 1:
          {
            var g = ps.PersistentData.AllData(true).OfType<GH_String>().FirstOrDefault();
            return g == null ? null : new BuildState { Kind = BuildStateKind.Constant, ConstantType = ConstantType.Text, Text = g.Value ?? "" };
          }
        }
      }
      catch { }
      return null;
    }

    private static bool IsConstantParam(IGH_DocumentObject obj, ConstantType type)
    {
      switch (type)
      {
        case ConstantType.Number: return obj is Param_Number;
        case ConstantType.Integer: return obj is Param_Integer;
        case ConstantType.Text: return obj is Param_String;
        default: return false;
      }
    }

    private static string ParamName(ConstantType type) =>
      type == ConstantType.Number ? "Number" : type == ConstantType.Integer ? "Integer" : "Text";

    private static SliderRounding Rounding(GH_SliderAccuracy a)
    {
      switch (a)
      {
        case GH_SliderAccuracy.Integer: return SliderRounding.Integer;
        case GH_SliderAccuracy.Even: return SliderRounding.Even;
        case GH_SliderAccuracy.Odd: return SliderRounding.Odd;
        default: return SliderRounding.Float;
      }
    }

    private static GH_SliderAccuracy Accuracy(SliderRounding rounding)
    {
      switch (rounding)
      {
        case SliderRounding.Integer: return GH_SliderAccuracy.Integer;
        case SliderRounding.Even: return GH_SliderAccuracy.Even;
        case SliderRounding.Odd: return GH_SliderAccuracy.Odd;
        default: return GH_SliderAccuracy.Float;
      }
    }

    private static string Plain(decimal d) =>
      d.ToString(System.Globalization.CultureInfo.InvariantCulture);
  }
}
