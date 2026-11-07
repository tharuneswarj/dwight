using System;
using System.Collections.Generic;

namespace Dwight.Agent
{
  /// <summary>
  /// EVERY PORT ON THE CANVAS, BY ITS OWN GUID (U48; the second U6 gate run, rows 1, 3, 4).
  ///
  /// The edit click refused "row 1 names a port that is no longer there" for a Number Slider wired to a Construct Point's
  /// X input - twice, by nickname and by full name - while the card, minted from the same canvas, had accepted the row.
  /// The mint reads the canvas into <see cref="EditNode"/>s and gives every port its own guid; the apply then looked that
  /// guid up with <c>doc.FindObject</c>, which answers for TOP-LEVEL DOCUMENT OBJECTS. A slider is one (its param guid is its
  /// object guid), so the source side resolved; a component's input param is not one, so the target side never did. A
  /// refusal that is false for every component port is the worst kind: it reads as the model's fault and is the plugin's.
  ///
  /// The cure is one index over BOTH kinds of port, built from the objects the document holds: a top-level param is a port,
  /// and so is each input and output param of each component. GH-free (ADR-091): the shell fills it from the document, and
  /// <see cref="FromCanvas"/> fills it from the same <see cref="EditCanvas"/> the validator reads, so a test can ask the
  /// question the shell asks - "does every port the plan names resolve?" - without Grasshopper.
  /// </summary>
  internal sealed class EditPortIndex<T> where T : class
  {
    private readonly Dictionary<Guid, T> _byGuid = new Dictionary<Guid, T>();

    public int Count => _byGuid.Count;

    public void Add(Guid guid, T port)
    {
      if (guid != Guid.Empty && port != null) _byGuid[guid] = port;
    }

    public T Find(Guid guid) => _byGuid.TryGetValue(guid, out var p) ? p : null;
  }

  internal static class EditPortCheck
  {
    /// <summary>An index of every port in the canvas the validator read, valued by the port itself.</summary>
    public static EditPortIndex<EditPort> FromCanvas(EditCanvas canvas)
    {
      var index = new EditPortIndex<EditPort>();
      if (canvas?.Nodes == null) return index;
      foreach (var n in canvas.Nodes.Values)
      {
        if (n == null) continue;
        foreach (var p in n.Inputs) index.Add(p.Guid, p);
        foreach (var p in n.Outputs) index.Add(p.Guid, p);
      }
      return index;
    }

    /// <summary>
    /// The first port a wire or unwire row names that <paramref name="exists"/> cannot find, as a sentence that says what is
    /// TRUE: which row, which end, which port, on what. Null when every port resolves. A delete row names no port.
    /// </summary>
    public static string FirstUnresolved(EditPlan plan, Func<Guid, bool> exists)
    {
      if (plan == null || exists == null) return null;
      foreach (var step in plan.Steps)
      {
        if (step.Kind == EditOpKind.Delete) continue;

        if (step.FromPort == null || !exists(step.FromPort.Guid))
          return "row " + step.Row + "'s source port " + PortText(step.FromPort, step.From) + " could not be found on the canvas as it is now.";
        if (step.ToPort == null || !exists(step.ToPort.Guid))
          return "row " + step.Row + "'s input port " + PortText(step.ToPort, step.To) + " could not be found on the canvas as it is now.";
      }
      return null;
    }

    private static string PortText(EditPort port, EditNode owner)
    {
      var name = port == null ? "" : port.Label;
      var on = EditOpsAsk.Ref(owner);
      return string.IsNullOrWhiteSpace(name) || name == "." ? "of " + on : name + " on " + on;
    }
  }
}
