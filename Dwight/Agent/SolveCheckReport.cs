using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Dwight.Agent
{
  /// <summary>One component as <c>solve_and_check</c> sees it: its id, its type, Grasshopper's runtime
  /// messages for it, and the flat item count on each of its outputs. The GH-coupled shim maps the solved
  /// graph into these; the projection below is what the model reads.</summary>
  internal sealed class SolveCheckRow
  {
    public int Id { get; set; }
    public string Type { get; set; }
    public List<MsgDto> Msgs { get; set; } = new List<MsgDto>();
    public int[] OutCounts { get; set; } = Array.Empty<int>();
  }

  /// <summary>
  /// The <c>solve_and_check</c> payload, GH-free (ADR-091) so its SHAPE is asserted rather than read off a
  /// live document.
  ///
  /// EVERY NUMBER IT STATES SAYS WHAT IT COUNTS (#917, U27). <c>"solved": false</c> used to mean "nothing was
  /// expired, so this is the state as it already stood" - and that meaning lived in a C# comment. The model,
  /// handed a bare false beside zero errors, explained it to the user as "the sliders aren't wired to anything
  /// yet", twice. It also read <c>outCounts</c> as "a flattened total"; it is a flat count PER OUTPUT, and
  /// nothing said so. A fact the plugin states must be STATED (#315 at the tool payload): the sentence that
  /// gives each figure its meaning travels IN the payload, under <c>means</c>, keyed by the field it explains.
  /// </summary>
  internal static class SolveCheckReport
  {
    /// <summary>What <c>outCounts</c> is, wherever a read carries one. One definition, so get_component and
    /// solve_and_check cannot drift apart about it.</summary>
    public const string OutCountsMeans =
      "outCounts is the flat item count on each output port, in port order, summed across every branch - " +
      "it is neither a branch structure nor a total across ports. get_tree_signature gives the structure on a wire.";

    public static string SolvedMeans(bool solved) => solved
      ? "true: this call ran a solve, and every figure here comes from it."
      : "false: NOTHING NEEDED SOLVING. No component was expired, so no solve was run and every figure here is the " +
        "definition's current state as it already stood. It does not mean the definition failed to solve, and it " +
        "says nothing about what is or is not wired.";

    public static JsonObject Build(IEnumerable<SolveCheckRow> rows, bool solved)
    {
      var list = (rows ?? Enumerable.Empty<SolveCheckRow>()).OrderBy(r => r.Id).ToList();

      var msgs = new JsonArray();
      var empties = new JsonArray();
      int errors = 0, warnings = 0;

      foreach (var r in list)
      {
        foreach (var m in r.Msgs ?? Enumerable.Empty<MsgDto>())
        {
          if (m.Level == "error") errors++;
          else if (m.Level == "warning") warnings++;
          msgs.Add(new JsonObject { ["id"] = r.Id, ["type"] = r.Type, ["level"] = m.Level, ["text"] = m.Text });
        }

        if (r.OutCounts != null && r.OutCounts.Length > 0 && r.OutCounts.Any(c => c == 0))
          empties.Add(new JsonObject
          {
            ["id"] = r.Id,
            ["type"] = r.Type,
            ["outCounts"] = new JsonArray(r.OutCounts.Select(c => (JsonNode)c).ToArray()),
          });
      }

      return new JsonObject
      {
        ["solved"] = solved,
        ["components"] = list.Count,
        ["errors"] = errors,
        ["warnings"] = warnings,
        ["msgs"] = msgs,
        ["emptyOutputs"] = empties,
        ["means"] = new JsonObject
        {
          ["solved"] = SolvedMeans(solved),
          ["components"] = "every component and param on the canvas at the top level (a Cluster counts as one), " +
                           "listed below or not.",
          ["errors"] = "runtime messages at error level, summed over every component, from the last solution.",
          ["warnings"] = "runtime messages at warning level, summed over every component, from the last solution.",
          ["msgs"] = "every runtime message Grasshopper holds, by component, verbatim.",
          ["emptyOutputs"] = "only the components with at least one output that produced nothing - the diagnostic " +
                             "signal. Components that produced data are omitted; ask get_component for one by id.",
          ["outCounts"] = OutCountsMeans,
        },
      };
    }
  }
}
