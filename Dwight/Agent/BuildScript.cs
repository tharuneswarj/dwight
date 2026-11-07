using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Dwight.Data;

namespace Dwight.Agent
{
  /// <summary>
  /// WHICH COMPONENT TYPES ARE SCRIPTS A BUILD MAY WRITE (U19, #898) - and the language each one means.
  ///
  /// THE LANGUAGE IS DERIVED FROM THE TYPE AND NEVER STORED BESIDE IT, for the reason U20 learned the hard way
  /// (<see cref="LiveScript.Language"/>): a component described as one language and repaired as another is told
  /// "The interpreter said:" about a compiler error. Here the type IS the choice - "Python 3 Script" or
  /// "C# Script" - so there is no second field to disagree with it, and the model cannot ask for a language the
  /// type does not name.
  ///
  /// THE DECISION RUNS ON THE CANONICAL NAME (U40, #975). "Params > Script > C# Script" is a C# Script, and a
  /// lower-cased "c# script" is too; judged on the raw string either was refused as a type that takes no source.
  ///
  /// STATED BOUND: ONLY THESE TWO. The legacy GhPython (IronPython 2) and the VB/C# classic script components are not
  /// offered - their sources are not what the shim sets, and "a script" a user can place is the Rhino 8 script
  /// component in either language (Phase-9 lanes, U19).
  /// </summary>
  internal static class BuildScriptTypes
  {
    public const string Python = "python";
    public const string CSharp = "csharp";

    public const string PythonTypeName = "Python 3 Script";
    public const string CSharpTypeName = "C# Script";

    /// <summary>The ports a NEW Rhino 8 script component is born with, in order. A build's <c>inputs</c>/<c>outputs</c>
    /// REPLACE these on the side they are declared (U78) - the build removes the defaults before adding the declared ports - so
    /// a declared name may be one of them (Brief EA). <c>edit_script</c>'s may not, because it adds to a component that keeps its ports.
    /// The live component is checked at instantiation too, after that removal: a Rhino whose defaults differ is refused
    /// with the canvas untouched, not built with two ports of one name.</summary>
    public static readonly IReadOnlyList<string> DefaultInputs = new[] { "x", "y" };
    public static readonly IReadOnlyList<string> DefaultOutputs = new[] { "a" };

    /// <summary>U78: a script that declares inputs has exactly those, so the born-with <c>x</c>, <c>y</c> are removed.</summary>
    public static bool ReplacesDefaultInputs(BuildComponent c) => c != null && c.CarriesSource && c.Inputs != null && c.Inputs.Count > 0;

    /// <summary>U78: a script that declares outputs has exactly those, so the born-with <c>a</c> is removed.</summary>
    public static bool ReplacesDefaultOutputs(BuildComponent c) => c != null && c.CarriesSource && c.Outputs != null && c.Outputs.Count > 0;

    /// <summary>The input names a built script has: the declared ones where it declares any, else the component's own.</summary>
    public static List<string> InputsOf(BuildComponent c, IEnumerable<string> own) =>
      ReplacesDefaultInputs(c) ? new List<string>(c.Inputs) : (own ?? DefaultInputs).ToList();

    /// <summary>
    /// Brief EA: a name clash a build can compute before it starts. A side the script declares has exactly those ports, and a side it
    /// leaves undeclared keeps the ports it is born with, so this compares the two sides as they will be built. The first input that is
    /// also an output, or null. It matters in C#, where both are parameters of one method: on the bench "The parameter name 'a' is a
    /// duplicate". In Python it is one variable, read and then assigned, and the bench's a = a - x ran, so the parser asks this only of
    /// a C# script. Ignoring case, as every other comparison of a script's port names does.
    /// </summary>
    public static string NameOnBothSides(IReadOnlyList<string> inputs, IReadOnlyList<string> outputs)
    {
      var built = new HashSet<string>(outputs != null && outputs.Count > 0 ? outputs : DefaultOutputs, StringComparer.OrdinalIgnoreCase);
      return (inputs != null && inputs.Count > 0 ? inputs : DefaultInputs).FirstOrDefault(built.Contains);
    }

    /// <summary>Brief EA: the refusal for <see cref="NameOnBothSides"/>, naming the port, the two lists it will really have, and what to change.</summary>
    public static ToolResult BothSidesRefusal(string name, IReadOnlyList<string> inputs, IReadOnlyList<string> outputs, string where)
    {
      string why;
      if (outputs.Count == 0)
        why = "it declares no \"outputs\", so it keeps the output " + string.Join(", ", DefaultOutputs) + " a new script component is born with, and its inputs are " +
              string.Join(", ", inputs) + ". Declare its \"outputs\", which replace that one, or rename the input.";
      else if (inputs.Count == 0)
        why = "it declares no \"inputs\", so it keeps the inputs " + string.Join(", ", DefaultInputs) + " a new script component is born with, and its outputs are " +
              string.Join(", ", outputs) + ". Declare its \"inputs\", which replace those, or rename the output.";
      else
        why = "its inputs are " + string.Join(", ", inputs) + " and its outputs are " + string.Join(", ", outputs) + ". Rename one of them.";
      return ToolResult.Fail(ToolErrors.InvalidArgs,
        "\"" + name + "\" cannot be both an input and an output of one C# script" + where + ", because they would be two parameters of one method, which does not compile: " + why,
        new JsonObject
        {
          ["port"] = name,
          ["inputs"] = new JsonArray((inputs.Count > 0 ? inputs : DefaultInputs).Select(n => (JsonNode)n).ToArray()),
          ["outputs"] = new JsonArray((outputs.Count > 0 ? outputs : DefaultOutputs).Select(n => (JsonNode)n).ToArray()),
        });
    }

    /// <summary>
    /// Brief EA: Python's keywords, which are the language's and not read from Rhino. Rhino's Python list also holds builtins, and for any word on it Rhino only warns: on the bench print ran. So that list would refuse names
    /// that work. A keyword cannot be read or assigned as a variable, so the port could never carry anything.
    /// </summary>
    private static readonly HashSet<string> PythonKeywords = new HashSet<string>(StringComparer.Ordinal)
    {
      "False", "None", "True", "and", "as", "assert", "async", "await", "break", "class", "continue", "def", "del", "elif", "else", "except", "finally", "for", "from",
      "global", "if", "import", "in", "is", "lambda", "nonlocal", "not", "or", "pass", "raise", "return", "try", "while", "with", "yield",
    };

    /// <summary>The message output every script has beside its own outputs. It is not a default and is never removed, so a declared output of this name would be a second port of one name.</summary>
    public const string MessageOutput = "out";

    /// <summary>Why this name cannot be this port in this language, or null. Case counts in a language's keywords, and not for the message output, because a wire names a port ignoring case.</summary>
    public static string NameRefusal(string language, string side, string name)
    {
      if (side == "output" && string.Equals(name, MessageOutput, StringComparison.OrdinalIgnoreCase))
        return "every script already has a message output called " + MessageOutput + ", so a wire could not tell the two apart";
      if (language == CSharp && CSharpReservedWords.Contains(name))
        return "Rhino's C# script does not let a port use the reserved word " + name;
      if (language == Python && PythonKeywords.Contains(name))
        return name + " is a Python keyword, so the code could never read or assign it";
      return null;
    }

    /// <summary>"python", "csharp", or null when the type is not a script component a build may write.</summary>
    public static string LanguageOf(string type)
    {
      var bare = Dwight.Components.ComponentMatchCore.Normalize(Dwight.Components.ComponentMatchCore.BareName(type));
      if (bare == Dwight.Components.ComponentMatchCore.Normalize(PythonTypeName)) return Python;
      if (bare == Dwight.Components.ComponentMatchCore.Normalize(CSharpTypeName)) return CSharp;
      return null;
    }

    /// <summary>Is this a COMPILED language? Decides whether a failure is the compiler's words or the interpreter's.</summary>
    public static bool? Compiled(string language) =>
      language == CSharp ? true : language == Python ? (bool?)false : null;

    /// <summary>The language in the user's words - the card's label.</summary>
    public static string Label(string language) =>
      language == CSharp ? "C#" : language == Python ? "Python 3" : "script";

    /// <summary>"\"Python 3 Script\" or \"C# Script\"" - the list a refusal names, from the constants that decide.</summary>
    public static string Named => "\"" + PythonTypeName + "\" or \"" + CSharpTypeName + "\"";
  }

  /// <summary>
  /// HOW A SCRIPT'S INPUT RECEIVES ITS DATA (Brief CP). On the lamp a Bambu script took 42,828 G-code lines on an input with item access, so it ran once per line and made 1,070,700 outputs, and
  /// nobody could ask for anything else: the model named ports, and every one was born item. So a declared input now carries a word, and the plugin applies it (ADR-056, the to-script law: the model
  /// declares, the plugin computes the port). Only what differs from item is held, so a script that declares nothing reads and records exactly as it did. Outputs are unchanged. GH-free (ADR-091).
  /// </summary>
  internal static class PortAccess
  {
    public const string Item = "item";
    public const string List = "list";
    public const string Tree = "tree";

    /// <summary>The refusal's list, and the schema's.</summary>
    public const string Words = "\"item\", \"list\" or \"tree\"";

    /// <summary>What each word means for the code, in the words the schema and a refusal both use.</summary>
    public const string Rule =
      "An input that receives a whole list and must see it at once is \"list\": fed as \"item\", the script runs once per element, so a whole list of G-code lines would run once per line. " +
      "An input that should run once per element is \"item\", the default. An input that must keep its branches is \"tree\".";

    /// <summary>The word as the plugin holds it. False for anything else, which is never guessed.</summary>
    public static bool TryRead(string word, out string access)
    {
      access = (word ?? "").Trim().ToLowerInvariant();
      return access == Item || access == List || access == Tree;
    }

    /// <summary>One input's access: what was declared, or item.</summary>
    public static string Of(IReadOnlyDictionary<string, string> access, string name) =>
      access != null && name != null && access.TryGetValue(name, out var word) ? word : Item;

    /// <summary>"gcode (list), temp (item)". The default is said too, because it decides how many times the code runs.</summary>
    public static List<string> Shown(IEnumerable<string> names, IReadOnlyDictionary<string, string> access) =>
      (names ?? new string[0]).Select(n => n + " (" + Of(access, n) + ")").ToList();

    /// <summary>As a record holds them: a plain name for item, so a record with nothing declared is byte for byte what it was, and a name with its word otherwise.</summary>
    public static JsonArray Record(IEnumerable<string> names, IReadOnlyDictionary<string, string> access) =>
      new JsonArray((names ?? new string[0]).Select(n => Of(access, n) == Item
        ? (JsonNode)n
        : new JsonObject { ["name"] = n, ["access"] = Of(access, n) }).ToArray());

    /// <summary>A record read back, shape-checked only: the parser has already been through what is in it.</summary>
    public static void ReadRecord(JsonArray record, out List<string> names, out Dictionary<string, string> access)
    {
      names = new List<string>();
      access = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
      foreach (var n in record ?? new JsonArray())
      {
        if (n is JsonObject o)
        {
          var name = o["name"]?.GetValue<string>();
          if (string.IsNullOrEmpty(name)) continue;
          names.Add(name);
          if (TryRead(o["access"]?.GetValue<string>(), out var word) && word != Item) access[name] = word;
        }
        else names.Add(n.GetValue<string>());
      }
    }

    public static Dwight.Simplify.ScriptAccess ToScript(string word) =>
      word == List ? Dwight.Simplify.ScriptAccess.List : word == Tree ? Dwight.Simplify.ScriptAccess.Tree : Dwight.Simplify.ScriptAccess.Item;

    /// <summary>Do two declarations say the same thing? A name that is absent is item, so an explicit item and no word at all agree.</summary>
    public static bool Same(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b, IEnumerable<string> names) =>
      (names ?? new string[0]).All(n => string.Equals(Of(a, n), Of(b, n), StringComparison.Ordinal));
  }

  /// <summary>
  /// THE SCRIPT HALF OF A COMPONENT DECLARATION (U19): <c>source</c>, <c>inputs</c>, <c>outputs</c>.
  ///
  /// A SCRIPT IS ONE MORE COMPONENT IN THE RECORD, NOT A NEW KIND (#898). Its source must be set INSIDE the build,
  /// before the verdict, in the one undo record - and a separate kind would have meant a second consent surface, a
  /// second ledger and a second undo story for the same act. So this parses into the same <see cref="BuildComponent"/>
  /// every other declaration becomes, through the same parser, and the spot card names it the way it names a slider.
  ///
  /// EVERYTHING IS REFUSED HERE OR NOT AT ALL, and every refusal costs the model one of its asks, so each names the
  /// door: a <c>source</c> on a type that is not a script says which types take one; a source over the consent bound
  /// says why the bound exists; a port name says what a usable one looks like.
  /// </summary>
  /// <summary>
  /// Brief EA, on the architect's ruling (ADR-092: the loaded assembly, never a curated map). THE WORDS RHINO'S C# SCRIPT REFUSES, READ FROM RHINO: the same read <c>ScriptVariableParam.ValidateVariableName</c> makes, which
  /// on the bench gave 'Input parameter "value" can not use reserved keyword "value"'. Reflection, because this is reached through Rhino.Runtime.Code as <c>GhRunScript</c> reaches it, and read-only accessors only. The list
  /// here is the fallback for a Rhino that does not answer. A read that fails is tried again next time, and only a read that succeeds is kept for the session.
  /// </summary>
  internal static class CSharpReservedWords
  {
    public const string FromRhino = "read from Rhino";
    public const string FromList = "Dwight's own list";

    /// <summary>The 120 words of Rhino 8's own list, read from <c>LanguageSyntax.Keywords</c> on the fourteenth build (Release C21A60CBD2B8482D), 2 November 2026. A measured list, for a Rhino whose read fails.</summary>
    internal static readonly HashSet<string> Fallback = new HashSet<string>(StringComparer.Ordinal)
    {
      "abstract", "add", "alias", "and", "args", "as", "ascending", "async", "await", "base", "bool", "break", "by", "byte", "case", "catch", "char", "checked", "class",
      "const", "continue", "decimal", "default", "delegate", "descending", "do", "double", "dynamic", "else", "enum", "equals", "event", "explicit", "extern", "false",
      "file", "finally", "fixed", "float", "for", "foreach", "from", "get", "global", "goto", "group", "if", "implicit", "in", "init", "int", "interface", "internal",
      "into", "is", "join", "let", "lock", "long", "managed", "nameof", "namespace", "new", "nint", "not", "notnull", "nuint", "null", "object", "on", "operator", "or",
      "orderby", "out", "override", "params", "partial", "private", "protected", "public", "readonly", "record", "ref", "remove", "required", "return", "sbyte", "scoped",
      "sealed", "select", "set", "short", "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong",
      "unchecked", "unmanaged", "unsafe", "ushort", "using", "value", "var", "virtual", "void", "volatile", "when", "where", "while", "with", "yield",
    };

    private static HashSet<string> _read;
    private static bool _noRuntime;

    public static bool Contains(string word) => Current().Words.Contains(word);

    /// <summary>Which source is answering, for a bench to report.</summary>
    public static string Source => Current().Source;

    private static (HashSet<string> Words, string Source) Current()
    {
      if (_read != null) return (_read, FromRhino);
      var resolved = Resolve(ReadFromRhino);
      if (resolved.Source == FromRhino) _read = resolved.Words;
      return resolved;
    }

    /// <summary>The words a reader gives, or the fallback when it gives none or throws. Pure, so what happens when Rhino does not answer can be held with no Rhino.</summary>
    internal static (HashSet<string> Words, string Source) Resolve(Func<IEnumerable<string>> read)
    {
      try
      {
        var words = read?.Invoke()?.Where(w => !string.IsNullOrEmpty(w)).ToList();
        if (words != null && words.Count > 0) return (new HashSet<string>(words, StringComparer.Ordinal), FromRhino);
      }
      catch { }
      return (Fallback, FromList);
    }

    private static IEnumerable<string> ReadFromRhino()
    {
      if (_noRuntime) return null;
      var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Rhino.Runtime.Code");
      if (asm == null)
      {
        try { asm = System.Reflection.Assembly.Load("Rhino.Runtime.Code"); }
        catch { _noRuntime = true; return null; }
      }

      const System.Reflection.BindingFlags statics = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static;
      var specType = asm.GetType("Rhino.Runtime.Code.Languages.LanguageSpec");
      var languages = asm.GetType("Rhino.Runtime.Code.RhinoCode")?.GetProperty("Languages", statics)?.GetValue(null);
      var csharp = specType?.GetProperty("CSharp", statics)?.GetValue(null);
      if (languages == null || csharp == null) return null;

      var query = languages.GetType().GetMethods().FirstOrDefault(m => m.Name == "QueryLatest" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == specType);
      var language = query?.Invoke(languages, new[] { csharp });
      var support = language == null ? null : asm.GetType("Rhino.Runtime.Code.Languages.ILanguage")?.GetProperty("Support")?.GetValue(language);
      var syntax = support == null ? null : asm.GetType("Rhino.Runtime.Code.Languages.ISupport")?.GetProperty("Syntax")?.GetValue(support);
      return syntax == null ? null : asm.GetType("Rhino.Runtime.Code.Languages.LanguageSyntax")?.GetProperty("Keywords")?.GetValue(syntax) as IEnumerable<string>;
    }
  }

  internal static class BuildScriptParser
  {
    /// <summary>Appended ports a single script may declare. Bounded so a card can name them (ADR-035).</summary>
    public const int MaxPorts = 12;

    public static bool TryParse(JsonNode component, string type, string reference,
      out string source, out List<string> inputs, out List<string> outputs, out ToolResult error) =>
      TryParse(component, type, reference, out source, out inputs, out outputs, out _, out error);

    public static bool TryParse(JsonNode component, string type, string reference,
      out string source, out List<string> inputs, out List<string> outputs, out int lineShift, out ToolResult error) =>
      TryParse(component, type, reference, out source, out inputs, out outputs, out lineShift, out _, out error);

    /// <param name="lineShift">U88: for a C# source, how many lines the normaliser put before the model's first line, so a compiler
    /// position can be given in the model's numbering. Zero for Python and for a class that passed through.</param>
    /// <param name="inputAccess">Brief CP: each input whose declared access is not item.</param>
    public static bool TryParse(JsonNode component, string type, string reference,
      out string source, out List<string> inputs, out List<string> outputs, out int lineShift, out Dictionary<string, string> inputAccess, out ToolResult error)
    {
      source = null; inputs = new List<string>(); outputs = new List<string>(); error = null; lineShift = 0;
      inputAccess = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

      var sourceNode = component?["source"];
      var inputsNode = component?["inputs"];
      var outputsNode = component?["outputs"];
      if (sourceNode == null && inputsNode == null && outputsNode == null) return true;   // an ordinary component

      var where = " (component \"" + reference + "\")";
      var language = BuildScriptTypes.LanguageOf(type);

      if (language == null)
      {
        var field = sourceNode != null ? "source" : inputsNode != null ? "inputs" : "outputs";
        error = ToolResult.Fail(ToolErrors.InvalidArgs,
          "\"" + BuildBare(type) + "\" is not a script component, so it takes no \"" + field + "\"" + where + ". Only " +
          BuildScriptTypes.Named + " carry source. To put code on the canvas, place one of those; to change a value on " +
          "any other component, use a \"state\" where its type takes one.",
          new JsonObject
          {
            ["asked"] = type ?? "",
            ["typesThatTakeSource"] = new JsonArray((JsonNode)BuildScriptTypes.PythonTypeName, (JsonNode)BuildScriptTypes.CSharpTypeName),
          });
        return false;
      }

      if (sourceNode == null)
      {
        error = ToolResult.Fail(ToolErrors.InvalidArgs,
          "\"" + (inputsNode != null ? "inputs" : "outputs") + "\" names ports for the code to read or write, so it needs the " +
          "\"source\" that does" + where + ". Send the complete script with it.");
        return false;
      }

      string raw;
      try { raw = sourceNode.GetValue<string>(); }
      catch
      {
        error = ToolResult.Fail(ToolErrors.InvalidArgs, "\"source\" must be the script's text, as a string" + where + ".");
        return false;
      }
      if (string.IsNullOrWhiteSpace(raw))
      {
        error = ToolResult.Fail(ToolErrors.InvalidArgs,
          "\"source\" is empty" + where + ". Send the COMPLETE script - the user is shown the whole thing before it runs. " +
          "To place a script component with nothing in it, leave \"source\" out.");
        return false;
      }

      // The text shown IS the text tested IS the text applied (#153): normalised once, here, so the card, the record and
      // the component all hold the same bytes.
      var text = SourceToken.Normalise(raw);

      // U45 (#141/#146): a C# source is put in the component's own shape HERE, once, before anything is measured, shown
      // or stored - so the card shows the text that will run and the component receives that same text (#153). A bare
      // `private void RunScript(...)` is wrapped in the Script_Instance class with its usings unioned; a complete class
      // passes through untouched. Python has no such shape and is left exactly as written.
      if (language == BuildScriptTypes.CSharp)
      {
        var modelText = text;
        text = Dwight.Simplify.UsingNormalizer.ToComponentTemplate(text);
        lineShift = Dwight.Simplify.UsingNormalizer.LineShift(modelText, text);
      }
      if (text.Length > EditScriptAsk.MaxSourceChars)
      {
        error = ToolResult.Fail(ToolErrors.InvalidArgs,
          "That script is " + text.Length + " characters and Dwight's limit is " + EditScriptAsk.MaxSourceChars + where +
          ". The whole source is shown on the card before it runs, and a card nobody finishes reading is a card nobody has " +
          "consented to - so write a smaller script.");
        return false;
      }

      if (!Names(inputsNode, "inputs", "input", language, where, out inputs, out error, inputAccess)) return false;
      if (!Names(outputsNode, "outputs", "output", language, where, out outputs, out error)) return false;

      // Brief EA: refused here, so before any card and before anything is billed for a build that could never land.
      var onBoth = language == BuildScriptTypes.CSharp ? BuildScriptTypes.NameOnBothSides(inputs, outputs) : null;
      if (onBoth != null)
      {
        error = BuildScriptTypes.BothSidesRefusal(onBoth, inputs, outputs, where);
        return false;
      }

      source = text;
      return true;
    }

    /// <param name="access">Where this side takes an access word beside each name (Brief CP, inputs only), what was declared. A bare name is still a name: a model's earlier turns and a saved record hold them.</param>
    private static bool Names(JsonNode node, string field, string side, string language, string where,
      out List<string> names, out ToolResult error, Dictionary<string, string> access = null)
    {
      names = new List<string>(); error = null;
      if (node == null) return true;

      if (!(node is JsonArray arr))
      {
        error = ToolResult.Fail(ToolErrors.InvalidArgs, "\"" + field + "\" must be an array of names" + where + ".");
        return false;
      }
      if (arr.Count > MaxPorts)
      {
        error = ToolResult.Fail(ToolErrors.InvalidArgs,
          "A script can be given at most " + MaxPorts + " extra " + side + "s here; this asks for " + arr.Count + where + ".");
        return false;
      }

      var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      foreach (var n in arr)
      {
        string name;
        JsonNode word = null;
        var named = access != null ? n as JsonObject : null;
        try { name = ((named != null ? named["name"] : n)?.GetValue<string>() ?? "").Trim(); } catch { name = ""; }
        if (named != null) word = named["access"];
        if (name.Length == 0)
        {
          error = ToolResult.Fail(ToolErrors.InvalidArgs,
            "A new " + side + " needs a name" + where + ". Dwight computes the port's type and how it receives data; the name " +
            "is the part only you can supply.");
          return false;
        }
        if (!IsIdentifier(name))
        {
          error = ToolResult.Fail(ToolErrors.InvalidArgs,
            "\"" + name + "\" cannot be an " + side + " name" + where + ": it has to be usable " +
            "as a variable in the script, so it must start with a letter or underscore and contain only letters, digits and underscores.");
          return false;
        }
        var refusal = BuildScriptTypes.NameRefusal(language, side, name);
        if (refusal != null)
        {
          error = ToolResult.Fail(ToolErrors.InvalidArgs, "\"" + name + "\" cannot be an " + side + " name" + where + ": " + refusal + ". Pick another name.");
          return false;
        }
        if (!seen.Add(name))
        {
          error = ToolResult.Fail(ToolErrors.InvalidArgs,
            "\"" + name + "\" is named twice in the new " + side + "s" + where + ". Each port needs its own name.");
          return false;
        }
        if (word != null)
        {
          string said = null;
          try { said = word.GetValue<string>(); } catch { }
          if (!PortAccess.TryRead(said, out var declared))
          {
            error = ToolResult.Fail(ToolErrors.InvalidArgs,
              "\"" + (said ?? word.ToJsonString()) + "\" is not an access for the input \"" + name + "\"" + where + ". Use " + PortAccess.Words + ". " + PortAccess.Rule);
            return false;
          }
          if (declared != PortAccess.Item) access[name] = declared;
        }
        names.Add(name);
      }
      return true;
    }

    private static bool IsIdentifier(string s)
    {
      if (s.Length == 0 || !(char.IsLetter(s[0]) || s[0] == '_')) return false;
      return s.All(c => char.IsLetterOrDigit(c) || c == '_');
    }

    private static string BuildBare(string type) => Dwight.Components.ComponentMatchCore.BareName(type ?? "");
  }
}

namespace Dwight.Agent
{
  /// <summary>One script a placement carries, as its card names it (U19). Plugin-computed from the validated spec.</summary>
  internal sealed class SpotScript
  {
    /// <summary>The nickname the user will see on the canvas, else the type's bare name.</summary>
    public string Name { get; set; }
    public string LanguageLabel { get; set; }
    public string Source { get; set; }

    /// <summary>Inputs (the defaults and the appended ones, in order) that nothing in the spec wires to.</summary>
    public List<string> UnwiredInputs { get; set; } = new List<string>();

    /// <summary>U98: the declared inputs and outputs, in order, for the card's folded summary line (as a region card's script has).</summary>
    public List<string> Inputs { get; set; } = new List<string>();
    public List<string> Outputs { get; set; } = new List<string>();

    /// <summary>Brief CP: each input whose declared access is not item.</summary>
    public IReadOnlyDictionary<string, string> InputAccess { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when the script has inputs and NONE is wired - the case where an empty result proves nothing.</summary>
    public bool AllInputsUnwired { get; set; }
  }

  internal static class SpotScripts
  {
    /// <summary>
    /// The scripts of a validated spec, with the inputs nothing wires to. THE UNWIRED SET IS A FACT OF THE SPEC, not of
    /// the canvas: a wire the spec declares into an input counts, whatever the source on the far end will turn out to
    /// hold. <paramref name="portsOfType"/> supplies a type's input names; null falls back to the defaults a new Rhino 8
    /// script component is born with (<see cref="BuildScriptTypes.DefaultInputs"/>).
    /// </summary>
    public static List<SpotScript> Of(BuildSpec spec, Func<string, PortShape> portsOfType)
    {
      var result = new List<SpotScript>();
      foreach (var c in spec?.Components ?? new List<BuildComponent>())
      {
        if (c == null || !c.CarriesSource) continue;

        IReadOnlyList<string> own = null;
        try { own = portsOfType?.Invoke(c.Type)?.Inputs; } catch { own = null; }
        var inputs = BuildScriptTypes.InputsOf(c, own);   // U78: declared inputs replace x, y

        var wired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in spec.Wires ?? new List<BuildWire>())
          if (w != null && string.Equals(w.ToRef, c.Ref, StringComparison.Ordinal))
            wired.Add(string.IsNullOrEmpty(w.ToParam) && inputs.Count > 0 ? inputs[0] : w.ToParam ?? "");

        var unwired = inputs.Where(i => !wired.Contains(i)).ToList();
        result.Add(new SpotScript
        {
          Name = string.IsNullOrWhiteSpace(c.Nick) ? Dwight.Components.ComponentMatchCore.BareName(c.Type ?? "script") : c.Nick.Trim(),
          LanguageLabel = BuildScriptTypes.Label(c.Language),
          Source = c.Source,
          Inputs = inputs.ToList(),
          InputAccess = c.InputAccess,
          Outputs = (BuildScriptTypes.ReplacesDefaultOutputs(c) ? c.Outputs : BuildScriptTypes.DefaultOutputs).ToList(),
          UnwiredInputs = unwired,
          AllInputsUnwired = inputs.Count > 0 && unwired.Count == inputs.Count,
        });
      }
      return result;
    }
  }
}

namespace Dwight.Agent
{
  /// <summary>
  /// CODE RUNS ONLY WHERE THE USER SAW IT (U19, U65). A script's complete source runs on the user's machine, so it runs only
  /// when a card they approved SHOWED that source, untruncated. Two cards can: a PLACEMENT card (a spot), whose record is the
  /// spec itself, and a REGION build card that carries the script in its "scripts" section (U65) - the model writes a region
  /// build's spec after the click, so the card cannot be the spec, and the gate instead holds the spec to the card: every
  /// script in a region build must equal, byte for byte after the one normalisation both sides share, a script the approved
  /// card showed for that region (<see cref="ApprovedScript.Matches"/>).
  ///
  /// THE CONDITION, in one sentence: a script is let through only where the card cannot have shown anything but this source.
  /// Anywhere else - a region build whose card did not list it, or whose source differs by a character - it is refused before
  /// anything else in the build, however real the region.
  ///
  /// The discriminators are the turn's permission - the approved spot, the scripts the click granted - which the user's click
  /// set and no argument the model sends can reach. GH-free (ADR-091): the caller hands in those facts.
  /// </summary>
  internal static class BuildScriptGate
  {
    public const string Reason = "code_needs_a_placement_card";

    public static ToolResult Check(BuildSpec spec, bool isApprovedSpot) => Check(spec, isApprovedSpot, null);

    /// <summary>
    /// Brief CA: A FACT THE PLUGIN HOLDS IS NOT SOMETHING THE MODEL MUST REMEMBER. A turn starts from chat history and never a previous turn's tool traffic (#223),
    /// so the source a model sent to raise a card is gone by the build, and it told the user the approved source was "missing from my handoff". A script component
    /// with no source, in a region whose card showed a script of that language, takes the card's text and ports here, before the parse, so its wires resolve against
    /// them. It is exactly the text the user read (U65), and <see cref="Adopt"/> holds it to the card as it holds anything sent. Several in one region pair up in
    /// the card's order. A region whose card showed none is untouched: an empty script stays an empty script, and code the card did not show stays refused.
    /// </summary>
    /// <returns>How many components took the card's text.</returns>
    public static int FillFromCard(JsonObject args, IReadOnlyList<ApprovedScript> approvedScripts)
    {
      var region = (args?["region"] as JsonValue)?.TryGetValue<string>(out var r) == true ? r?.Trim() : null;
      if (string.IsNullOrEmpty(region) || approvedScripts == null || !(args["components"] is JsonArray components)) return 0;

      var left = approvedScripts.Where(s => s != null && string.Equals(s.Region, region, StringComparison.Ordinal)).ToList();
      // A script the build does send uses up the card's, so a sourceless neighbour cannot take the same text twice.
      foreach (var c in components.OfType<JsonObject>().Where(c => c["source"] != null))
      {
        var language = LanguageOfComponent(c);
        var used = left.FirstOrDefault(s => s.Language == language);
        if (used != null) left.Remove(used);
      }

      var filled = 0;
      foreach (var c in components.OfType<JsonObject>().Where(c => c["source"] == null))
      {
        var language = LanguageOfComponent(c);
        if (language == null) continue;
        var card = left.FirstOrDefault(s => s.Language == language);
        if (card == null) continue;
        left.Remove(card);
        c["source"] = card.Source;
        if (c["inputs"] == null) c["inputs"] = PortAccess.Record(card.Inputs, card.InputAccess);
        if (c["outputs"] == null) c["outputs"] = new JsonArray((card.Outputs ?? new List<string>()).Select(o => (JsonNode)o).ToArray());
        filled++;
      }
      return filled;
    }

    private static string LanguageOfComponent(JsonObject c) =>
      (c["type"] as JsonValue)?.TryGetValue<string>(out var t) == true ? BuildScriptTypes.LanguageOf((t ?? "").Trim()) : null;

    /// <summary>Would the build go ahead? Changes nothing: <see cref="Adopt"/> is the call that takes the card's text.</summary>
    /// <param name="approvedScripts">The scripts the approving click granted this turn (U65), or null when it granted none.</param>
    public static ToolResult Check(BuildSpec spec, bool isApprovedSpot, IReadOnlyList<ApprovedScript> approvedScripts)
    {
      if (isApprovedSpot) return null;
      var scripts = (spec?.Components ?? new List<BuildComponent>()).Where(c => c != null && c.CarriesSource).ToList();
      if (scripts.Count == 0) return null;

      var missing = ScriptsAsk.Uncovered(spec, spec?.Region, approvedScripts);
      return missing.Count == 0 ? null : Refuse(missing, approvedScripts);
    }

    /// <summary>
    /// U73: THE CARD IS THE CONTRACT, SO THE APPROVED SOURCE IS THE AUTHORITY. Where a build's script agrees with an approved
    /// one on region, language, inputs and outputs (the last two as sets), the spec's component is rewritten to the CARD's
    /// source and the card's port order BEFORE placement, so the text built, the text the verdict's placed-as-declared leg
    /// compares against and the text the user read are one text. The substitutions are returned for the tool result to say
    /// so. Where the ports or the region differ, or no script was shown, the build is refused with the field named and the
    /// approved record in the detail. A placement card's script (<paramref name="isApprovedSpot"/>) is the spec itself and
    /// is left alone.
    /// </summary>
    public static ToolResult Adopt(BuildSpec spec, bool isApprovedSpot, IReadOnlyList<ApprovedScript> approvedScripts,
      out List<ScriptSubstitution> substitutions)
    {
      substitutions = new List<ScriptSubstitution>();
      if (isApprovedSpot) return null;
      var scripts = (spec?.Components ?? new List<BuildComponent>()).Where(c => c != null && c.CarriesSource).ToList();
      if (scripts.Count == 0) return null;

      var missing = ScriptsAsk.Pair(spec, spec?.Region, approvedScripts, out var covering);
      if (missing.Count > 0) return Refuse(missing, approvedScripts);

      foreach (var c in scripts)
      {
        var card = covering[c.Ref];
        var sub = new ScriptSubstitution { Ref = c.Ref };
        if (ScriptsAsk.FirstDifference(c.Source, card.Source, out var line, out var sent, out var shown))
        { sub.SourceDiffered = true; sub.FirstDifferenceLine = line; sub.SentLine = sent; sub.CardLine = shown; }
        // U86: a card that listed no ports does not decide them; the build's, which the card's source names, stand.
        var takeBuildPorts = ScriptsAsk.CoversByBuildPorts(card, spec.Region, c);
        sub.PortsTakenFromBuild = takeBuildPorts;
        sub.PortOrderDiffered = !takeBuildPorts
                             && (!(c.Inputs ?? new List<string>()).SequenceEqual(card.Inputs ?? new List<string>(), StringComparer.Ordinal)
                              || !(c.Outputs ?? new List<string>()).SequenceEqual(card.Outputs ?? new List<string>(), StringComparer.Ordinal)
                              || !PortAccess.Same(c.InputAccess, card.InputAccess, card.Inputs));   // Brief CP: what the person read on the card is what runs

        // U83: the same script under other port names. The card's names win; the build's wires, which were resolved against
        // the names the build declared, move onto the card's ports by position - the i-th declared port is the i-th card port.
        if (ScriptsAsk.CoversByPosition(card, spec.Region, c))
        {
          var inMap = PositionMap(c.Inputs, card.Inputs, sub.PortsMappedByPosition);
          var outMap = PositionMap(c.Outputs, card.Outputs, sub.PortsMappedByPosition);
          foreach (var w in spec.Wires ?? new List<BuildWire>())
          {
            if (w == null) continue;
            if (string.Equals(w.ToRef, c.Ref, StringComparison.Ordinal) && w.ToParam != null && inMap.TryGetValue(w.ToParam, out var ti)) w.ToParam = ti;
            if (string.Equals(w.FromRef, c.Ref, StringComparison.Ordinal) && w.FromParam != null && outMap.TryGetValue(w.FromParam, out var fo)) w.FromParam = fo;
          }
          foreach (var r in spec.Reconnects ?? new List<BuildReconnect>())
            if (r != null && string.Equals(r.FromRef, c.Ref, StringComparison.Ordinal) && r.FromParam != null && outMap.TryGetValue(r.FromParam, out var ro)) r.FromParam = ro;
        }

        c.Source = card.Source;
        c.LineShift = card.LineShift;   // U88: the built text is the card's, so its lines are numbered as the card's were
        if (!takeBuildPorts)
        {
          c.Inputs = new List<string>(card.Inputs ?? new List<string>());
          c.Outputs = new List<string>(card.Outputs ?? new List<string>());
          c.InputAccess = new Dictionary<string, string>(card.InputAccess ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        }
        if (sub.SourceDiffered || sub.PortOrderDiffered || sub.PortsMappedByPosition.Count > 0 || sub.PortsTakenFromBuild) substitutions.Add(sub);
      }
      return null;
    }

    /// <summary>The build's declared names to the card's, position by position; the pairs that differ are also recorded for the
    /// verdict's note. Names compare ordinally, as the wires carry the declared spelling.</summary>
    private static Dictionary<string, string> PositionMap(IList<string> declared, IList<string> card, List<KeyValuePair<string, string>> record)
    {
      var map = new Dictionary<string, string>(StringComparer.Ordinal);
      for (var i = 0; i < declared.Count && i < card.Count; i++)
      {
        map[declared[i]] = card[i];
        if (!string.Equals(declared[i], card[i], StringComparison.Ordinal)) record.Add(new KeyValuePair<string, string>(declared[i], card[i]));
      }
      return map;
    }

    private static ToolResult Refuse(List<UncoveredScript> missing, IReadOnlyList<ApprovedScript> approved)
    {
      var because = string.Join("; ", missing.Select(m => m.Why));
      var detail = new JsonObject
      {
        ["reason"] = Reason,
        ["scripts"] = new JsonArray(missing.Select(m => (JsonNode)m.Ref).ToArray()),
        ["fields"] = new JsonArray(missing.Select(m => (JsonNode)m.Field).ToArray()),
        ["cause"] = "code runs only if the card the user approved showed that script: " + because,
      };
      var records = new JsonArray(missing.Where(m => m.Approved != null).Select(m => m.Approved).Distinct()
        .Select(a => (JsonNode)a.ToRecord()).ToArray());
      if (records.Count > 0) detail["approved"] = records;

      return ToolResult.Fail(ToolErrors.PreconditionRefused,
        "Nothing was built. " + because.Substring(0, 1).ToUpperInvariant() + because.Substring(1) + ". The user must read the complete " +
        "code before it runs, so a script is built only from the card they approved. Either send build_region the card's script " +
        "(the approved record is in this result's detail), or ask for a new card - request_mutation kind=build with the regions " +
        "and a \"scripts\" entry (region, type, source) for each script - or ask for a placement card instead: give \"place\" " +
        "with these components and leave \"regions\" out. To build without code, leave the source out.",
        detail);
    }
  }
}
