using System;
using System.Collections.Generic;
using System.Linq;

namespace Dwight.Agent
{
  /// <summary>One object's position, GH-free so the guard below can be tested.</summary>
  internal struct TidyPoint
  {
    public double X, Y;
    public TidyPoint(double x, double y) { X = x; Y = y; }
  }

  internal enum TidyVerdict
  {
    /// <summary>Exactly what was declared moved, and nothing else. Keep it.</summary>
    AsDeclared = 0,
    /// <summary>Something moved that the tidy never declared. REVERT.</summary>
    Undeclared = 1,
    /// <summary>Something declared did not move. REVERT - the tidy did not do what it said.</summary>
    Incomplete = 2,
  }

  internal sealed class TidyCheck
  {
    public TidyVerdict Verdict { get; set; }
    /// <summary>Guids that moved without being declared - the dangerous direction.</summary>
    public IReadOnlyList<Guid> Undeclared { get; set; } = new Guid[0];
    /// <summary>Guids declared to move that did not.</summary>
    public IReadOnlyList<Guid> Missing { get; set; } = new Guid[0];
    public string Message { get; set; } = "";
    public bool Ok => Verdict == TidyVerdict.AsDeclared;
  }

  /// <summary>
  /// THE MOVED-SET GUARD (Phase-8 WP-4 Stage D; #353).
  ///
  /// WHY IT HAD TO BE BUILT, AND WHY NEITHER INHERITED INSTRUMENT COULD DO IT. After a region is built,
  /// its components sit in a plain grid inside a box that was sized for TEXT, so the layout is re-run over
  /// them (#239). That is a MOVE - and a move is invisible to both guards this project already has.
  /// <c>BuildPreservation</c> compares <c>FpDoc</c> snapshots, and the behaviour fingerprint deliberately
  /// carries no pivots because it is a guard about BEHAVIOUR. So a bug that dragged the user's components
  /// across the canvas would pass "preserved: true" and pass byte-identical, both correctly, and both
  /// blind. A verdict resting on an instrument that cannot see the change is #208 in geometry's costume.
  ///
  /// SO THE TIDY GETS ITS OWN GUARD, and its contract is the one #338 taught: a tidy is a contract about
  /// WHICH COMPONENTS MOVE. The set that actually moved must be exactly the set that was declared.
  ///
  /// THE DECLARATION IS COMPUTED BEFORE THE MOVE, FROM THE ENGINE'S OWN OUTPUT (#353's sharpening, and
  /// the part that makes this a guard rather than a decoration). A check that derived its expectation
  /// from the observation it is checking would be Family B - it would agree with whatever happened and
  /// certify nothing. So the layout engine's targets are compared against the CURRENT pivots first, the
  /// difference is the declaration, and only then is anything written.
  ///
  /// Declaring only what will REALLY move is what lets this be an equality rather than a subset: writing
  /// a pivot identical to the one already there is not observable, so including such an object in the
  /// declaration would make exact agreement impossible and quietly weaken the check to "nothing extra
  /// moved".
  ///
  /// GH-free (ADR-091).
  /// </summary>
  internal static class RegionTidy
  {
    /// <summary>
    /// How far a pivot must shift to count as moved.
    ///
    /// BASIS: canvas units are floats and a layout target round-trips through float arithmetic, so exact
    /// equality would report spurious movement. A hundredth of a unit is far below anything visible - a
    /// component is ~100 units wide - and far above float noise. The SAME epsilon decides both the
    /// declaration and the observation, which is what keeps the two comparable.
    /// </summary>
    public const double MovedEpsilon = 0.01;

    public static bool Moved(TidyPoint a, TidyPoint b) =>
      Math.Abs(a.X - b.X) > MovedEpsilon || Math.Abs(a.Y - b.Y) > MovedEpsilon;

    /// <summary>
    /// WHAT THE TIDY WILL MOVE - computed from the engine's targets against the pivots as they are NOW,
    /// before anything is written.
    ///
    /// Objects whose target is where they already are are excluded: they will not move, so declaring them
    /// would make the after-check unfalsifiable in one direction.
    /// </summary>
    public static IReadOnlyList<Guid> Declare(IReadOnlyDictionary<Guid, TidyPoint> current,
                                              IReadOnlyDictionary<Guid, TidyPoint> targets)
    {
      var declared = new List<Guid>();
      if (current == null || targets == null) return declared;

      foreach (var kv in targets.OrderBy(k => k.Key))
      {
        if (!current.TryGetValue(kv.Key, out var now)) continue;   // not on the canvas: nothing to move
        if (Moved(now, kv.Value)) declared.Add(kv.Key);
      }
      return declared;
    }

    /// <summary>
    /// After the move: did exactly the declared set move?
    ///
    /// <paramref name="before"/> and <paramref name="after"/> are the pivots of EVERY object in the
    /// document, not only the ones the tidy touched - the whole point is to catch something moving that
    /// the tidy never named, and a guard that only looked at its own targets could not see that at all.
    /// </summary>
    public static TidyCheck Check(IReadOnlyDictionary<Guid, TidyPoint> before,
                                  IReadOnlyDictionary<Guid, TidyPoint> after,
                                  IReadOnlyList<Guid> declared)
    {
      var result = new TidyCheck();
      var promised = new HashSet<Guid>(declared ?? new Guid[0]);
      var observed = new HashSet<Guid>();

      foreach (var kv in before ?? new Dictionary<Guid, TidyPoint>())
      {
        if (!after.TryGetValue(kv.Key, out var now)) continue;   // gone: not this guard's subject
        if (Moved(kv.Value, now)) observed.Add(kv.Key);
      }

      var undeclared = observed.Where(g => !promised.Contains(g)).OrderBy(g => g).ToList();
      var missing = promised.Where(g => !observed.Contains(g)).OrderBy(g => g).ToList();

      result.Undeclared = undeclared;
      result.Missing = missing;

      // The dangerous direction FIRST, and it is reported by itself: something moved that nobody declared
      // means the tidy touched work outside its contract, which is the whole reason this exists.
      if (undeclared.Count > 0)
      {
        result.Verdict = TidyVerdict.Undeclared;
        result.Message = undeclared.Count + " component(s) moved that the tidy did not declare, so it was " +
                         "reverted - the layout is as it was.";
        return result;
      }

      if (missing.Count > 0)
      {
        result.Verdict = TidyVerdict.Incomplete;
        result.Message = missing.Count + " component(s) the tidy declared did not move, so it did not do " +
                         "what it said and was reverted.";
        return result;
      }

      result.Verdict = TidyVerdict.AsDeclared;
      return result;
    }

    /// <summary>
    /// ANCHOR the engine's layout onto where the members ALREADY ARE.
    ///
    /// THE GUARD CANNOT EXPRESS THIS, and that is why it is here. <see cref="Check"/> verifies WHICH
    /// objects moved, never WHERE TO - so a layout that relocated a whole region across the canvas would
    /// have every move DECLARED and would be dutifully certified. The property that rules that out is
    /// arithmetic rather than a check: the laid-out block keeps the top-left the members had, so a tidy
    /// ARRANGES INSIDE a region and can never relocate it.
    ///
    /// Extracted GH-free so the property is asserted rather than reviewed - the shim reads the bounds and
    /// calls this, so the tested path is the product path (#300).
    /// </summary>
    public static IReadOnlyDictionary<Guid, TidyPoint> Anchor(IReadOnlyDictionary<Guid, TidyPoint> engineCentres,
                                                              IReadOnlyDictionary<Guid, TidyPoint> current)
    {
      var placed = new Dictionary<Guid, TidyPoint>();
      if (engineCentres == null || engineCentres.Count == 0 || current == null) return placed;

      var subject = engineCentres.Keys.Where(current.ContainsKey).ToList();
      if (subject.Count == 0) return placed;

      var fromX = engineCentres.Where(k => subject.Contains(k.Key)).Min(k => k.Value.X);
      var fromY = engineCentres.Where(k => subject.Contains(k.Key)).Min(k => k.Value.Y);
      var toX = subject.Min(g => current[g].X);
      var toY = subject.Min(g => current[g].Y);

      foreach (var g in subject)
        placed[g] = new TidyPoint(toX + (engineCentres[g].X - fromX), toY + (engineCentres[g].Y - fromY));

      return placed;
    }

    /// <summary>A region's box on the canvas, GH-free so the crowding check below can be tested.</summary>
    public struct TidyBox
    {
      public string Address;
      public double X, Y, W, H;
      /// <summary>What a box with no address is, as the canvas names it, so a person can go and look.</summary>
      public string Label;
      public TidyBox(string address, double x, double y, double w, double h, string label = null)
      { Address = address; X = x; Y = y; W = w; H = h; Label = label; }
    }

    /// <summary>
    /// Brief CL: WHAT THE OVERLAP SENTENCE COMPARES, THE REAL OBJECTS AND NOTHING ELSE. It was wrong both ways on the first real files. It fired five times where none of the new work
    /// touched the old, because it read a region's group box against every other box, a person's groups included, and a group's box is a margin around its members with nothing in
    /// its corners. And it said nothing when five new components landed on three old objects, because a spot has no tidy, so nothing measured it. Now each object of a
    /// region is compared by itself with every other object, so a hole in a region's rectangle is not an overlap, and a group or a sketch, which only frames other things, is not compared.
    /// Each member carries its region's address; anything else carries its name.
    /// </summary>
    public static List<TidyBox> CrowdingBoxes(IEnumerable<(string Address, IReadOnlyList<TidyBox> Members)> regions, IEnumerable<(TidyBox Box, bool IsFrame)> others)
    {
      var boxes = new List<TidyBox>();
      foreach (var r in regions ?? Enumerable.Empty<(string, IReadOnlyList<TidyBox>)>())
        foreach (var m in r.Members ?? new List<TidyBox>())
          if (m.W > 0 && m.H > 0) boxes.Add(new TidyBox(r.Address, m.X, m.Y, m.W, m.H));
      foreach (var o in others ?? Enumerable.Empty<(TidyBox, bool)>())
        if (!o.IsFrame && o.Box.W > 0 && o.Box.H > 0) boxes.Add(o.Box);
      return boxes;
    }

    /// <summary>
    /// WHICH BUILT REGIONS NOW CROWD A NEIGHBOUR (#369).
    ///
    /// #239 named the problem - a group's box is derived from its MEMBERS, so a region sized for text
    /// grows when it is built and can crowd the region beside it - and the tidy deliberately does NOT
    /// re-pack the map: that would move regions the user never asked about, which is the unasked widening
    /// #338 is about, in geometry. But "we do not fix it" must not become "we do not mention it".
    ///
    /// SO THE DISPOSITION IS DETECT AND SAY. No mutation, no consent needed - stating a fact about the
    /// canvas requires neither, and Reorganise remains the consented way to re-space it.
    ///
    /// AMENDED (31 October 2026): <see cref="RegionSpacing"/> now moves what this card approved, among
    /// themselves, so this reports what is STILL overlapping after it: what it may not move, or what found
    /// no clear place. A box with no address is something on the canvas that is not a region.
    ///
    /// Only pairs involving a region THIS RUN BUILT are reported: a pre-existing overlap between two
    /// untouched regions is not news, and reporting it would be this tidy taking credit for someone
    /// else's mess.
    /// </summary>
    public static IReadOnlyList<(string Grown, string Neighbour)> Crowding(IReadOnlyList<TidyBox> boxes,
                                                                          ISet<string> built)
    {
      var found = new List<(string, string)>();
      if (boxes == null || built == null) return found;

      for (var i = 0; i < boxes.Count; i++)
        for (var j = i + 1; j < boxes.Count; j++)
        {
          var a = boxes[i];
          var b = boxes[j];
          if (!Intersects(a, b)) continue;
          if (!string.IsNullOrEmpty(a.Address) && string.Equals(a.Address, b.Address, StringComparison.Ordinal)) continue;   // two members of one region

          var aBuilt = built.Contains(a.Address ?? "");
          var bBuilt = built.Contains(b.Address ?? "");
          if (!aBuilt && !bBuilt) continue;   // not ours to report

          // The GROWN one is named first, because it is the one that changed.
          var grown = aBuilt ? a : b;
          var other = aBuilt ? b : a;
          found.Add((grown.Address, !string.IsNullOrEmpty(other.Address) ? other.Address
                                    : string.IsNullOrWhiteSpace(other.Label) ? OtherWork : "\"" + other.Label.Trim() + "\""));
        }

      return found
        .Distinct()
        .OrderBy(p => ScaffoldAddress.SortKey(p.Item1).Letters, StringComparer.Ordinal)
        .ThenBy(p => ScaffoldAddress.SortKey(p.Item1).Number)
        .ToList();
    }

    /// <summary>Touching edges are not a collision - two boxes laid out flush against each other are the
    /// engine doing its job, and reporting that would make the notice noise.</summary>
    private static bool Intersects(TidyBox a, TidyBox b) =>
      a.X < b.X + b.W && b.X < a.X + a.W &&
      a.Y < b.Y + b.H && b.Y < a.Y + a.H;

    /// <summary>How the sentence names something on the canvas that is not a region.</summary>
    public const string OtherWork = "other work on the canvas";

    /// <summary>How many things one region's clause names before it counts the rest.</summary>
    public const int MaxNamed = 3;

    private static string Named(IReadOnlyList<string> names)
    {
      if (names.Count == 1) return names[0];
      if (names.Count <= MaxNamed) return string.Join(", ", names.Take(names.Count - 1)) + " and " + names[names.Count - 1];
      return string.Join(", ", names.Take(MaxNamed)) + " and " + (names.Count - MaxNamed) + " more";
    }

    /// <summary>The crowding line, in the user's language: what happened, what was NOT done, and what to
    /// do about it. A notice that named a problem with no route out would be worse than none. "Not re-spaced"
    /// is said only when nothing was. Brief CL: once regions have moved it does not say these ones "stayed", because on the first real file the one it named was the one that moved.</summary>
    public static string CrowdingText(IReadOnlyList<(string Grown, string Neighbour)> pairs, bool respaced = false)
    {
      if (pairs == null || pairs.Count == 0) return null;

      var parts = pairs.GroupBy(p => p.Grown).Select(g => g.Key + " now overlaps " + Named(g.Select(p => p.Neighbour).ToList())).ToList();
      return string.Join("; ", parts) + (respaced
        ? ". Dwight moved regions apart where it could without moving your work, and these still overlap - Reorganise will tidy it."
        : ". The canvas was not re-spaced - Reorganise will tidy it.");
    }

    /// <summary>What the tidy did, for the thread. Components only: a region's scribbles move with it and are not what the user counts.</summary>
    public static string TidyLine(int regionsTidied, int regionsMoved, int componentsMoved)
    {
      var moved = regionsMoved + (regionsMoved == 1 ? " region" : " regions") + " apart";
      var laid = "Laid out " + regionsTidied + (regionsTidied == 1 ? " region" : " regions");
      var said = regionsMoved == 0 ? laid : regionsTidied == 0 ? "Moved " + moved : laid + " and moved " + moved;
      return said + (componentsMoved > 0 || regionsMoved == 0 ? " (" + componentsMoved + " component(s) moved)" : "") +
             ". That is a separate Ctrl-Z from the build, so you can keep the components and undo just the arrangement.";
    }

    /// <summary>
    /// WHAT HAPPENS TO A REGION'S BODY SCRIBBLE once the region is no longer only a plan (#238).
    ///
    /// A body scribble describes work NOT YET DONE. #238's rule is the honest-surface law turned around -
    /// the canvas must not carry a description its own contents contradict - so:
    ///
    ///   - BUILT: the body is REMOVED. The components now ARE the description, and a plan sitting beside
    ///     its own implementation is the stale claim #238 named. The HEADER always stays: it carries the
    ///     address, it is the navigation layer (scribbles do not fade at zoom-out), and it is how a region
    ///     is found again.
    ///   - STOPPED: the body is REWRITTEN to say the build stopped and where to look. A region abandoned
    ///     mid-correction that still described its plan would be the most misleading state of all.
    ///   - UNTOUCHED: the body STAYS. The plan is still a plan, and a region nobody built is not stale.
    /// </summary>
    public static ScribbleAction ForRegion(RegionOutcome? outcome)
    {
      if (outcome == null) return ScribbleAction.Keep;
      switch (outcome.Value)
      {
        case RegionOutcome.Built: return ScribbleAction.Remove;
        case RegionOutcome.BuiltWithErrors: return ScribbleAction.Replace;
        case RegionOutcome.Reverted: return ScribbleAction.Keep;   // nothing landed: the plan still stands
        default: return ScribbleAction.Keep;
      }
    }

    /// <summary>The replacement body for a region that was built but is not finished - stated on the
    /// CANVAS, because that is where somebody will next be looking at it.</summary>
    public static string StoppedText(string address, int attempts) =>
      address + ": built, but not verified clean after " + attempts +
      (attempts == 1 ? " attempt" : " attempts") + ". See the conversation for what is still wrong.";
  }

  internal enum ScribbleAction
  {
    Keep = 0,
    Remove = 1,
    Replace = 2,
  }
}
