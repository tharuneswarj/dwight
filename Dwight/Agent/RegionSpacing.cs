using System;
using System.Collections.Generic;
using System.Linq;

namespace Dwight.Agent
{
  /// <summary>What the re-space may do with one box on the canvas.</summary>
  internal enum SpaceRole
  {
    /// <summary>Never moves: a region off this card that holds components, anything the user placed or moved, anything that is not a region.</summary>
    Fixed = 0,
    /// <summary>On this card, and holding only what this build placed. It moves first, because it is what changed.</summary>
    Grown = 1,
    /// <summary>A region of a map the plugin laid, holding nothing yet. It moves only when what grew could find no other clear place.</summary>
    Empty = 2,
  }

  /// <summary>One box the re-space reads. An empty address is something on the canvas that is not a region.</summary>
  internal sealed class SpaceBox
  {
    public string Address;
    public double X, Y, W, H;
    public SpaceRole Role;

    public SpaceBox(string address, double x, double y, double w, double h, SpaceRole role)
    { Address = address; X = x; Y = y; W = w; H = h; Role = role; }

    public bool IsRegion => !string.IsNullOrEmpty(Address);
    public double Right => X + W;
    public double Bottom => Y + H;
    public SpaceBox At(double dx, double dy) => new SpaceBox(Address, X + dx, Y + dy, W, H, Role);
  }

  /// <summary>The re-space, before anything is written: how far each region moves, and which ones found no clear place and stay.</summary>
  internal sealed class SpacePlan
  {
    public Dictionary<string, TidyPoint> Moves { get; } = new Dictionary<string, TidyPoint>(StringComparer.Ordinal);
    public List<string> Unplaced { get; } = new List<string>();
  }

  /// <summary>
  /// WHERE BUILT REGIONS GO WHEN THEY HAVE GROWN INTO EACH OTHER (the amendment to #369; ADR-056).
  ///
  /// A map's boxes are sized for TEXT, so a built region grows out of the space it was given (#239). #369 ruled that the tidy must not re-pack the
  /// map, because that would move regions the user never asked about. That reason stands, and this is narrower than a re-pack: it moves the regions
  /// this card approved, among themselves, and an empty region of a plugin-laid map only when what grew has no other clear place. Everything else
  /// is an obstacle, never a mover.
  ///
  /// WHO GIVES WAY. Two regions on the card that collide: the later in reading order moves on, forward, and the earlier stays. A region on the card
  /// that hits something it may not move: it moves itself away, keeping its order against every other region. If no clear place exists within the
  /// bound, it stays where it is and the overlap is reported (<see cref="RegionTidy.CrowdingText"/>), so a canvas with no room still gets a true sentence.
  ///
  /// GH-free (ADR-091): the shim reads the boxes, this decides, and the moved-set guard holds the result (#353).
  /// </summary>
  internal static class RegionSpacing
  {
    /// <summary>What a moved region leaves around itself: the map's own gap between regions.</summary>
    public const double Gap = 60;

    /// <summary>How far one region may travel. Pushes add up along a map, so this allows a long chain of grown regions; further than this and a region
    /// would no longer read as part of its map, so it is reported rather than sent there.</summary>
    public const double MaxShift = 8000;

    /// <summary>How many places are tried for one region. Each try is one read over the obstacles, and this runs on the UI thread.</summary>
    public const int MaxTries = 400;

    /// <summary>The role of one region. Every "may it move" decision is here, so the table can be pinned.</summary>
    public static SpaceRole RoleOf(bool onCard, bool holdsComponents, bool onlyWhatThisBuildPlaced, bool atThePluginsPlace, bool isMapRegion, bool duplicated)
    {
      if (duplicated) return SpaceRole.Fixed;           // two groups answer to one address: which one is which is not known (#208)
      if (!atThePluginsPlace) return SpaceRole.Fixed;   // the user moved it, or nothing says otherwise
      if (onCard) return onlyWhatThisBuildPlaced ? SpaceRole.Grown : SpaceRole.Fixed;   // a rebuild's kept component, or one the user added
      return !holdsComponents && isMapRegion ? SpaceRole.Empty : SpaceRole.Fixed;
    }

    public static SpacePlan Plan(IReadOnlyList<SpaceBox> boxes)
    {
      var plan = new SpacePlan();
      if (boxes == null || boxes.Count == 0) return plan;

      var start = new Dictionary<string, SpaceBox>(StringComparer.Ordinal);
      foreach (var b in boxes.Where(b => b.IsRegion)) start[b.Address] = b;
      var now = new Dictionary<string, SpaceBox>(start, StringComparer.Ordinal);

      var standing = boxes.Where(b => b.Role == SpaceRole.Fixed).OrderBy(b => b.X).ThenBy(b => b.Y).ThenBy(b => b.Address, StringComparer.Ordinal).ToList();
      var grown = InReadingOrder(boxes, SpaceRole.Grown);
      var empty = InReadingOrder(boxes, SpaceRole.Empty);

      // What grew, in reading order, clear of everything that stands, every empty region, and the ones already settled. A later region on the card is
      // not looked at yet: it gives way when its own turn comes.
      var settled = new List<string>();
      var waiting = new List<string>();
      foreach (var address in grown)
      {
        var others = standing.Concat(empty.Select(e => now[e])).Concat(settled.Select(s => now[s])).ToList();
        if (TryPlace(now[address], others, start, now, out var offset)) Apply(plan, now, address, offset);
        else waiting.Add(address);
        settled.Add(address);
      }

      // What found no place around the empty regions, now free to land on them: they make way below.
      foreach (var address in waiting)
      {
        var others = standing.Concat(settled.Where(s => s != address).Select(s => now[s])).ToList();
        if (TryPlace(now[address], others, start, now, out var offset)) Apply(plan, now, address, offset);
        else plan.Unplaced.Add(address);
      }

      // The empty regions something this build grew landed on. One nothing landed on does not move, and an overlap this build had no part in is not its to fix (#369).
      foreach (var address in empty)
      {
        if (!settled.Any(s => Overlaps(now[address], now[s], 0))) continue;
        var others = standing.Concat(settled.Select(s => now[s])).Concat(empty.Where(e => e != address).Select(e => now[e])).ToList();
        if (TryPlace(now[address], others, start, now, out var offset)) Apply(plan, now, address, offset);
        else plan.Unplaced.Add(address);
      }

      return plan;
    }

    /// <summary>
    /// THE TARGETS FOR A MOVED REGION: every member the same distance, on top of where the tidy already means to put it. A translation, so what the
    /// inner layout arranged is kept, and the region's box moves by exactly this much. A member the record does not hold is left out, never guessed.
    /// </summary>
    public static void Shift(IDictionary<Guid, TidyPoint> targets, IReadOnlyDictionary<Guid, TidyPoint> current, IEnumerable<Guid> members, TidyPoint offset)
    {
      foreach (var id in members)
      {
        if (!current.TryGetValue(id, out var now)) continue;
        var from = targets.TryGetValue(id, out var inner) ? inner : now;
        targets[id] = new TidyPoint(from.X + offset.X, from.Y + offset.Y);
      }
    }

    /// <summary>
    /// A REGION'S BOX AS IT WILL BE, before anything is written. A group's box is its members' box with a margin Grasshopper chooses, so the margin is
    /// measured off the group as it stands and laid around where the members are going, rather than a number assumed here.
    /// </summary>
    public static RegionTidy.TidyBox PredictBox(RegionTidy.TidyBox groupNow, RegionTidy.TidyBox membersNow, RegionTidy.TidyBox membersAfter)
    {
      var left = membersNow.X - groupNow.X;
      var top = membersNow.Y - groupNow.Y;
      var right = groupNow.X + groupNow.W - (membersNow.X + membersNow.W);
      var bottom = groupNow.Y + groupNow.H - (membersNow.Y + membersNow.H);
      return new RegionTidy.TidyBox(groupNow.Address, membersAfter.X - left, membersAfter.Y - top,
        membersAfter.W + left + right, membersAfter.H + top + bottom);
    }

    private static List<string> InReadingOrder(IEnumerable<SpaceBox> boxes, SpaceRole role) =>
      boxes.Where(b => b.Role == role && b.IsRegion).Select(b => b.Address).Distinct(StringComparer.Ordinal)
        .OrderBy(a => ScaffoldAddress.SortKey(a).Letters, StringComparer.Ordinal)
        .ThenBy(a => ScaffoldAddress.SortKey(a).Number)
        .ToList();

    private static void Apply(SpacePlan plan, Dictionary<string, SpaceBox> now, string address, TidyPoint offset)
    {
      if (offset.X == 0 && offset.Y == 0) return;
      now[address] = now[address].At(offset.X, offset.Y);
      plan.Moves.TryGetValue(address, out var sofar);
      plan.Moves[address] = new TidyPoint(sofar.X + offset.X, sofar.Y + offset.Y);
    }

    /// <summary>
    /// The nearest clear place for one region that keeps its order against every other region, cheapest first. A step in the direction the map reads
    /// costs its distance, and any other step costs <see cref="AgainstTheWay"/> times that, so a region leaves its column or its row only when the way the
    /// map reads is closed or very much further. Offset zero with nothing in the way is the answer most regions get, and it is why a region nothing hit never moves.
    /// </summary>
    private static bool TryPlace(SpaceBox region, IReadOnlyList<SpaceBox> others, IReadOnlyDictionary<string, SpaceBox> start,
      IReadOnlyDictionary<string, SpaceBox> now, out TidyPoint offset)
    {
      // Something that already encloses this region's corner is a frame the user drew around it, not something it hit.
      var obstacles = others.Where(o => o.IsRegion || !(o.X <= region.X && region.X < o.Right && o.Y <= region.Y && region.Y < o.Bottom));
      return Search(region, obstacles, place => KeepsOrder(region, place, start, now), 0, out offset);
    }

    /// <summary>
    /// THE NEAREST CLEAR PLACE FOR SOMETHING NEW (Brief CM): the same search and the same margin as a region that grew, with two differences, because it is arriving and nothing
    /// around it is its own. It keeps the gap where it starts as well, and nothing is a frame round it, so it never lands inside a box the user drew. False when nothing within the
    /// search's reach is clear. With <paramref name="within"/>, only a place wholly inside that box counts.
    /// </summary>
    public static bool TryFindClear(SpaceBox box, IReadOnlyList<SpaceBox> obstacles, out TidyPoint offset, SpaceBox within = null) =>
      Search(box, obstacles ?? new SpaceBox[0],
        place => within == null || (place.X >= within.X && place.Y >= within.Y && place.Right <= within.Right && place.Bottom <= within.Bottom),
        Gap - 0.001, out offset);

    private static bool Search(SpaceBox region, IEnumerable<SpaceBox> others, Func<SpaceBox, bool> allowed, double marginWhereItIs, out TidyPoint offset)
    {
      offset = default;
      var obstacles = others
        .Where(o => o.Right >= region.X - MaxShift - Gap && o.X <= region.Right + MaxShift + Gap && o.Bottom >= region.Y - MaxShift - Gap && o.Y <= region.Bottom + MaxShift + Gap)
        .ToList();

      var open = new List<(double Cost, int Seq, double Dx, double Dy)> { (0, 0, 0, 0) };
      var seen = new HashSet<(long, long)> { (0, 0) };
      var seq = 0;
      for (var tries = 0; tries < MaxTries && open.Count > 0; tries++)
      {
        var best = 0;
        for (var i = 1; i < open.Count; i++)
          if (open[i].Cost < open[best].Cost || (open[i].Cost == open[best].Cost && open[i].Seq < open[best].Seq)) best = i;
        var at = open[best];
        open.RemoveAt(best);

        var place = region.At(at.Dx, at.Dy);
        if (!allowed(place)) continue;

        // Where it is now, only a true overlap counts. Where it would land, it must also leave the gap.
        var moved = at.Dx != 0 || at.Dy != 0;
        var margin = moved ? Gap - 0.001 : marginWhereItIs;
        var hit = obstacles.Where(o => Overlaps(place, o, margin))
          .OrderByDescending(o => Math.Min(place.Right, o.Right) - Math.Max(place.X, o.X))
          .ThenBy(o => o.Address, StringComparer.Ordinal).ThenBy(o => o.X).ThenBy(o => o.Y)
          .FirstOrDefault();
        if (hit == null) { offset = new TidyPoint(at.Dx, at.Dy); return true; }

        var way = TheWayTheMapReads(region, hit);
        foreach (var (dx, dy, step) in new[]
        {
          (hit.Right + Gap - region.X, at.Dy, Step.Right),
          (at.Dx, hit.Bottom + Gap - region.Y, Step.Down),
          (hit.X - Gap - region.Right, at.Dy, Step.Left),
          (at.Dx, hit.Y - Gap - region.Bottom, Step.Up),
        })
        {
          if (Math.Abs(dx) > MaxShift || Math.Abs(dy) > MaxShift) continue;
          if (!seen.Add((Key(dx), Key(dy)))) continue;
          var distance = Math.Abs(dx - at.Dx) + Math.Abs(dy - at.Dy);
          open.Add((at.Cost + distance * (way == null || way == step ? 1 : AgainstTheWay), ++seq, dx, dy));
        }
      }
      return false;
    }

    private const double AgainstTheWay = 10;

    private enum Step { Right, Down, Left, Up }

    /// <summary>Which way a region goes to leave another region in the map's order: past one that reads before it, away from one that reads after it.
    /// Null for anything with no place in that order, where the nearest way out is the right one.</summary>
    private static Step? TheWayTheMapReads(SpaceBox region, SpaceBox other)
    {
      if (!ScaffoldAddress.IsAddress(region.Address) || !ScaffoldAddress.IsAddress(other.Address)) return null;
      var a = ScaffoldAddress.SortKey(region.Address);
      var b = ScaffoldAddress.SortKey(other.Address);
      var letters = string.CompareOrdinal(a.Letters, b.Letters);
      if (letters != 0) return letters > 0 ? Step.Right : Step.Left;
      return a.Number > b.Number ? Step.Down : Step.Up;
    }

    /// <summary>Does this place keep the order the map started with, against every other region? Across letters that is left to right, because a letter is
    /// the dependency layer; within a letter it is top to bottom. Two that started level have no order to lose.</summary>
    private static bool KeepsOrder(SpaceBox region, SpaceBox place, IReadOnlyDictionary<string, SpaceBox> start, IReadOnlyDictionary<string, SpaceBox> now)
    {
      if (!ScaffoldAddress.IsAddress(region.Address) || !start.TryGetValue(region.Address, out var began)) return true;
      var key = ScaffoldAddress.SortKey(region.Address);
      foreach (var kv in now)
      {
        if (kv.Key == region.Address || !ScaffoldAddress.IsAddress(kv.Key)) continue;
        var otherBegan = start[kv.Key];
        var acrossLetters = !string.Equals(ScaffoldAddress.SortKey(kv.Key).Letters, key.Letters, StringComparison.Ordinal);
        var was = acrossLetters ? began.X - otherBegan.X : began.Y - otherBegan.Y;
        var will = acrossLetters ? place.X - kv.Value.X : place.Y - kv.Value.Y;
        if (was != 0 && Math.Sign(was) != Math.Sign(will)) return false;
      }
      return true;
    }

    private static bool Overlaps(SpaceBox a, SpaceBox b, double margin) =>
      a.X < b.Right + margin && b.X < a.Right + margin && a.Y < b.Bottom + margin && b.Y < a.Bottom + margin;

    private static long Key(double v) => (long)Math.Round(v * 2);
  }
}
