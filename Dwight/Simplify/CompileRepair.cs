using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Dwight.Simplify
{
  /// <summary>
  /// THE REPAIR MESSAGE A SCRIPT'S AUTHOR IS SENT when the script fails its trial: the compiler's or interpreter's
  /// own words, the reflected signatures of every type they named, and the instruction. GH-free (ADR-091) - the
  /// reflection is done by the caller and handed in as text.
  ///
  /// IT WAS INLINE IN <c>ToScriptCompileGate.RepairPrompt</c>, which is Grasshopper-coupled, so no test could reach
  /// the one decision in it that can be got wrong: WHICH HEADER. A caller that picked the language from the wrong
  /// field sent every C# compile failure "FAILED TO RUN. The interpreter said:" (U20, found at its sitting), and
  /// the suite could not see it. The composition is unchanged, moved here so a test can hold the header.
  /// </summary>
  internal static class CompileRepair
  {
    public const string CompiledHeader = "Your script DID NOT COMPILE. The compiler said:";
    public const string InterpretedHeader = "Your script FAILED TO RUN. The interpreter said:";

    /// <summary>For a script whose language could not be determined. Naming a compiler or an interpreter there
    /// would be a guess presented as a fact, so it says only what is known: it did not run, and what it reported
    /// (#208 - fail open on knowledge, never on identity).</summary>
    public const string UnknownLanguageHeader = "Your script did not run. It reported:";

    /// <summary>
    /// THE EVIDENCE HALF OF THE REPAIR MESSAGE (U19): the header for the language's kind, at most five of its errors, and the
    /// reflected signatures when there are any - with NO instruction after it. <see cref="Text"/> is this plus to-script's
    /// instruction ("do not change the interface"), which is right for a script whose interface is fixed and WRONG for a
    /// build, where the model declared the interface itself. A caller that frames its own instruction takes the evidence
    /// alone. The bytes <see cref="Text"/> returns are unchanged: a test pins them, because to-script's stated numbers were
    /// measured with them.
    /// </summary>
    public static string Evidence(IReadOnlyList<string> errors, bool? compiled, string reflectedApi)
    {
      var sb = new StringBuilder();
      sb.AppendLine(compiled == true ? CompiledHeader : compiled == false ? InterpretedHeader : UnknownLanguageHeader);
      foreach (var e in (errors ?? new string[0]).Take(5)) sb.AppendLine("  " + e);

      if (reflectedApi != null)
      {
        sb.AppendLine();
        sb.AppendLine("Here are the REAL signatures, reflected from the RhinoCommon assembly loaded in this Rhino");
        sb.AppendLine("(this is ground truth - if a type you used is missing below, it DOES NOT EXIST):");
        sb.AppendLine(reflectedApi);
      }

      return sb.ToString();
    }

    /// <summary>The message: the header for the language's kind (null = not known), at most five of its errors,
    /// the reflected API when there is any, and the instruction. <paramref name="reflectedApi"/> is null when no
    /// type was named.</summary>
    public static string Text(IReadOnlyList<string> errors, bool? compiled, string reflectedApi)
    {
      var sb = new StringBuilder(Evidence(errors, compiled, reflectedApi));
      sb.AppendLine();
      sb.AppendLine("Fix the error and return the corrected script. Do not change the interface - the inputs and");

      // THE CLOSING CLAUSE POINTS AT THE SIGNATURES, SO IT IS SAID ONLY WHEN THERE ARE SIGNATURES TO POINT AT (the
      // architect's ruling on lane C's finding). Without any, "use only what the signatures above show" named text that
      // was not there. SCOPED: with signatures present this line is BYTE-IDENTICAL to what to-script's stated numbers
      // (87.5% compile, 39.6% verify) were measured with - a test asserts it - so the measured case is untouched.
      sb.AppendLine(reflectedApi != null
        ? "outputs, in order, are fixed. Do not invent an API: use only what the signatures above show."
        : "outputs, in order, are fixed. Do not invent an API.");
      return sb.ToString();
    }
  }
}
