using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Dwight.Data
{
  /// <summary>Why an attempt failed, in the terms the MODEL needs to fix it.</summary>
  internal enum RefineKind
  {
    CompileError,       // the script did not compile
    RuntimeError,       // it compiled but threw
    OutputMismatch,     // it ran, but computed something different from the originals
    Incomplete,         // an accepted change did not take effect (the completeness guard)
    StructuralMismatch, // the applied change did not match what was declared (layer 1)
  }

  /// <summary>Phase-8 WP-0 (#88): one output's mismatch, CLASSIFIED. It carries the output NAME, its failure CLASS,
  /// and value-free structural COUNTS - and, by construction, NOTHING ELSE. There is no field for a value, a
  /// position, or a permutation, so <see cref="RefineFeedback.ToPrompt"/> cannot leak one (#89). Built from the
  /// <see cref="MismatchClassifier"/>'s facts at compare time, while the goo is still live.</summary>
  internal sealed class OutputDiff
  {
    public readonly string Name;
    public readonly MismatchKind Kind;
    public readonly int ItemsBefore, ItemsAfter, PathsBefore, PathsAfter;
    public OutputDiff(string name, MismatchKind kind, int itemsBefore, int itemsAfter, int pathsBefore, int pathsAfter)
    { Name = name; Kind = kind; ItemsBefore = itemsBefore; ItemsAfter = itemsAfter; PathsBefore = pathsBefore; PathsAfter = pathsAfter; }
  }

  /// <summary>
  /// RR17 #37: a failure, structured for a RETRY - not for a label.
  ///
  /// Today Simplify and to-script revert-and-stop, so the user pays for a call and gets nothing back. The bounded
  /// refine loop (feed the specific failure back to the model, retry, verify every attempt exactly as the first
  /// one) is built generically in Phase 8 WP-0 and retrofitted here. This type is what makes that retrofit
  /// possible: the failure paths record WHAT went wrong in a machine-usable shape, so the loop can hand the model
  /// its own compiler error or its own per-output diff instead of a sentence written for a human.
  ///
  /// It is deliberately GH-free and display-free. <see cref="ToPrompt"/> renders the model-facing text; the panel
  /// copy stays where it is. The one rule that must survive the retrofit: EVERY retried attempt is verified by
  /// the same stack as the first - a refine loop that relaxes verification to "get a pass" is worse than no loop.
  /// </summary>
  internal sealed class RefineFeedback
  {
    public RefineKind Kind;
    /// <summary>The raw specifics: the compiler message (with its line:col), the completeness reason.</summary>
    public string Detail;
    /// <summary>For an output mismatch: the NAMES of the outputs that differed (the per-output correspondence
    /// to-script uniquely has). Empty for the other kinds.</summary>
    public List<string> Outputs = new List<string>();
    /// <summary>Phase-8 WP-0 (#88): the CLASSIFIED per-output diffs (name + class + value-free counts). When
    /// present, <see cref="ToPrompt"/> renders the class-aware feedback; when empty, it falls back to the legacy
    /// name-only text. Populated for to-script (the output oracle); better-logic's multiset compare uses the
    /// name-only path (#104).</summary>
    public List<OutputDiff> Diffs = new List<OutputDiff>();

    public static RefineFeedback Compile(string message) => new RefineFeedback { Kind = RefineKind.CompileError, Detail = message };
    public static RefineFeedback Runtime(string message) => new RefineFeedback { Kind = RefineKind.RuntimeError, Detail = message };
    public static RefineFeedback Incomplete(string reason) => new RefineFeedback { Kind = RefineKind.Incomplete, Detail = reason };
    public static RefineFeedback Structural(string reason) => new RefineFeedback { Kind = RefineKind.StructuralMismatch, Detail = reason };
    public static RefineFeedback Mismatch(IEnumerable<string> outputs) =>
      new RefineFeedback { Kind = RefineKind.OutputMismatch, Outputs = (outputs ?? Enumerable.Empty<string>()).ToList() };
    /// <summary>Phase-8 WP-0 (#88): the class-aware output mismatch - the output oracle's feedback. Carries the
    /// classified diffs AND their names (so the legacy <see cref="Outputs"/> consumers still see the names). A
    /// distinct name (not an overload) so the null-literal call sites of <see cref="Mismatch(IEnumerable{string})"/>
    /// stay unambiguous.</summary>
    public static RefineFeedback MismatchClassified(IEnumerable<OutputDiff> diffs)
    {
      var list = (diffs ?? Enumerable.Empty<OutputDiff>()).Where(d => d != null).ToList();
      return new RefineFeedback
      {
        Kind = RefineKind.OutputMismatch,
        Diffs = list,
        Outputs = list.Select(d => d.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList(),
      };
    }

    /// <summary>The feedback as the model should receive it on a retry: specific, actionable, and carrying the
    /// invariant that the interface is NOT up for renegotiation (a model that "fixes" a failure by changing the
    /// param count is the failure mode the arity check already refuses).</summary>
    public string ToPrompt()
    {
      switch (Kind)
      {
        case RefineKind.CompileError:
          return "Your script did not compile:\n" + (Detail ?? "(no message)") +
                 "\nFix the error and return the corrected script. The interface (the inputs and outputs, in order) is unchanged.";
        case RefineKind.RuntimeError:
          return "Your script compiled but failed while running:\n" + (Detail ?? "(no message)") +
                 "\nFix the cause and return the corrected script. The interface is unchanged.";
        case RefineKind.OutputMismatch:
          // RR20 #71 / ADR-095 - THE KNIFE: this feedback is DIAGNOSTIC-ONLY and must NEVER carry the EXPECTED
          // VALUES. Tell the model "expected 47.3, got 12.0" and it can simply HARDCODE 47.3 - and our verification
          // would CERTIFY it, because beside-verify solves against ONE input state, where a frozen constant matches
          // perfectly. We would then delete the user's working components and leave behind a script that returns the
          // right number for exactly one slider position and nonsense everywhere else.
          // LEGAL (#88): which OUTPUT differed, its CLASS ("wrong order" / "wrong values" / "wrong structure"), and
          // value-free COUNTS ("4 items, expected 7"). ILLEGAL, ALWAYS: the values, a permutation, or a position.
          return Diffs.Count > 0 ? ClassAwareMismatch() : LegacyMismatch();
        case RefineKind.Incomplete:
          return "The change did not fully take effect: " + (Detail ?? "(no detail)") +
                 "\nReturn a proposal whose changes can all actually be applied.";
        default:
          return "The applied change did not match what was declared: " + (Detail ?? "(no detail)") +
                 "\nReturn a proposal that declares exactly what it changes.";
      }
    }

    /// <summary>The class-aware output-mismatch feedback (#88): one line per differing output stating its CLASS and
    /// value-free counts, then the standing guardrails. Every specific it carries is a count, a class word, or an
    /// output name - never a value, a permutation, or a position (#89, enforced by <see cref="OutputDiff"/> having
    /// no field for any of those).</summary>
    private string ClassAwareMismatch()
    {
      var sb = new StringBuilder();
      sb.Append("Your script ran, but it did not reproduce the original components' results:\n");
      var anyOrder = false;
      foreach (var d in Diffs)
      {
        var name = string.IsNullOrWhiteSpace(d.Name) ? "(unnamed output)" : d.Name;
        // to-script carries real item counts; better-logic's multiset compare carries names + class only (#104), so
        // the count detail is OMITTED when unknown rather than printed as a misleading "0 items".
        var hasCounts = d.ItemsAfter > 0 || d.ItemsBefore > 0;
        switch (d.Kind)
        {
          case MismatchKind.Order:
            anyOrder = true;
            sb.Append($"  - `{name}`: the RIGHT VALUES in the WRONG ORDER" +
                      (hasCounts ? $" ({d.ItemsAfter} item(s) across {d.PathsAfter} branch(es))" : "") + ".\n");
            break;
          case MismatchKind.Structure:
            sb.Append($"  - `{name}`: WRONG STRUCTURE" +
                      (hasCounts ? $" - it produced {d.ItemsAfter} item(s) in {d.PathsAfter} branch(es); the original produced {d.ItemsBefore} in {d.PathsBefore}" : "") + ".\n");
            break;
          case MismatchKind.Value:
            sb.Append($"  - `{name}`: WRONG VALUES" +
                      (hasCounts ? $" - the shape matches ({d.ItemsAfter} item(s) across {d.PathsAfter} branch(es)) but the numbers differ" : " - the numbers differ") + ".\n");
            break;
          default:   // Wrapper (our over-strict digest) and any future class: a plain difference, nothing to act on
            sb.Append($"  - `{name}`: the result differs.\n");
            break;
        }
      }
      sb.Append("A Grasshopper list is ordered and downstream components index into it, so the same items in a ");
      sb.Append("different sequence is a different result.\n");
      // #93(b): the retry must NOT paper over an ordering difference with a post-hoc sort/reverse - that overfits
      // the single input state beside-verify checks. Fix the enumeration itself, at its source in the graph.
      if (anyOrder)
        sb.Append("Fix an ordering difference by changing HOW YOU ENUMERATE the data from the graph - do NOT append " +
                  "a sort or a reverse to force the order to match, which only overfits this one input.\n");
      sb.Append("Do NOT hardcode any value to make the check pass - the script must COMPUTE its outputs from its ");
      sb.Append("inputs. The interface (the inputs and outputs, in order) is unchanged.\n");
      // #141/#147: THE MINIMAL-CHANGE PIN. Measured: the output-oracle's retry rewrote wholesale and pushed the
      // model into richer code that then failed to COMPILE - 0 recoveries in 22 firings, the build regressed, and
      // only one compile-repair shot remained. A retry should edit, not re-architect. (This pin rides the OUTPUT
      // oracle's text only; the compile-repair prompt is untouched - it already works.)
      sb.Append("Change the PREVIOUS script MINIMALLY to fix exactly the difference described above - do not ");
      sb.Append("restructure it, do not rewrite working parts, and keep its existing using directives and overall shape.");
      return sb.ToString();
    }

    /// <summary>The legacy name-only mismatch text (no classification available): used by better-logic's multiset
    /// compare and any caller that has names but no per-output classes.</summary>
    private string LegacyMismatch() =>
      "Your script ran, but it did not reproduce the original components' results. These outputs differed: " +
      (Outputs.Count > 0 ? string.Join(", ", Outputs) : "(unnamed)") +
      ".\nThe data structure (the branch/item shape) and the ORDER of items count as well as the values - a " +
      "Grasshopper list is ordered and downstream components index into it, so the same items in a different " +
      "sequence is a different result. Re-read the selection's wiring and any script sources in the payload, " +
      "and return a script that reproduces them EXACTLY. Do NOT hardcode any value to make a test pass - the " +
      "script must COMPUTE its outputs from its inputs. The interface is unchanged.";
  }
}
