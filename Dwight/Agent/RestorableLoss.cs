using System;
using System.Collections.Generic;
using System.Linq;
using Dwight.Data;

namespace Dwight.Agent
{
  /// <summary>One input a fresh build may restore: on <see cref="InputGuid"/>, the source <see cref="LostSource"/>
  /// was at <see cref="Position"/> when Dwight left the component, and is the only thing that has changed.</summary>
  internal sealed class RestorableLossFound
  {
    public Guid InputGuid { get; set; }
    public int Position { get; set; }
    public Guid LostSource { get; set; }
  }

  /// <summary>
  /// THE NARROW SIBLING OF A REBUILD'S BOUNDARY (#625, ruled at #648 iv).
  ///
  /// B1's XY Plane was fed by A1's Construct Point. The user deleted A1's components by hand, so the XY Plane lost
  /// its source - and because its as-left state included that source, it now reads as edited-by-the-user, so
  /// Dwight could neither reconnect it nor replace it. A FRESH build of A1 may restore that one source, on the
  /// same terms as a rebuild's reconnection: plugin-computed, declared, named on the card. Undeclared it is not
  /// refused - a fresh build cuts nothing, so the sibling is a permission, never an obligation.
  ///
  /// THE DIFF IS THE PLUGIN'S, and every condition is required:
  ///   - everything about the component except its input sources is exactly as Dwight left it (the as-left
  ///     token of "now, with the recorded sources put back" equals the stored token);
  ///   - exactly one input differs, and there the recorded sources are now's sources with exactly ONE guid
  ///     inserted, at position p;
  ///   - that guid is an output of a DORMANT fact of the region being built - one of that region's earlier
  ///     components, no longer on the canvas. A lost source that was the user's, or another region's, is not
  ///     this build's to restore.
  ///
  /// BOUNDS, stated: a fact recorded without structured sources (before this unit) can never qualify, and the
  /// ledger is in memory, so nothing qualifies after a restart.
  ///
  /// GH-free (ADR-091): the shim reads the component and the ledger's recorded state; this decides.
  /// </summary>
  internal static class RestorableLoss
  {
    /// <param name="stateToken">The component's whole held state now (#680), so "nothing but the sources differs" covers a
    /// slider's domain and rounding too.</param>
    public static RestorableLossFound Find(AsLeftState asLeft, FpObject now, string nickName, string stateToken,
                                           ISet<Guid> dormantOutputsOfRegion)
    {
      if (asLeft == null || now == null || string.IsNullOrEmpty(asLeft.Token) || asLeft.Sources == null) return null;
      if (dormantOutputsOfRegion == null || dormantOutputsOfRegion.Count == 0) return null;

      var inputs = now.Inputs ?? new List<FpInput>();
      if (inputs.Count != asLeft.Sources.Count || inputs.Any(i => !asLeft.Sources.ContainsKey(i.Guid))) return null;

      // 1. Nothing but the sources differs: now, with the recorded sources put back, hashes to the recorded token.
      var putBack = Clone(now, i => asLeft.Sources[i.Guid]);
      if (!AsLeftRecord.Unchanged(asLeft.Token, AsLeftRecord.Of(putBack, nickName, stateToken))) return null;

      // 2. Exactly one input differs.
      var differing = inputs.Where(i => !(i.Sources ?? new List<Guid>()).SequenceEqual(asLeft.Sources[i.Guid])).ToList();
      if (differing.Count != 1) return null;

      var input = differing[0];
      var was = asLeft.Sources[input.Guid];
      var isNow = input.Sources ?? new List<Guid>();

      // 3. ...and there it lost exactly one source, with the rest in order.
      if (was.Count != isNow.Count + 1) return null;
      var p = 0;
      while (p < isNow.Count && was[p] == isNow[p]) p++;
      if (!was.Skip(p + 1).SequenceEqual(isNow.Skip(p))) return null;

      // 4. ...and the lost source was this region's own earlier component.
      var lost = was[p];
      if (!dormantOutputsOfRegion.Contains(lost)) return null;

      return new RestorableLossFound { InputGuid = input.Guid, Position = p, LostSource = lost };
    }

    private static FpObject Clone(FpObject o, Func<FpInput, IReadOnlyList<Guid>> sources)
    {
      var c = new FpObject
      {
        Guid = o.Guid,
        TypeName = o.TypeName,
        ValueToken = o.ValueToken,
        Locked = o.Locked,
        IsGenericParam = o.IsGenericParam,
        SourceCount = o.SourceCount,
        DataMappingNone = o.DataMappingNone,
        Simplify = o.Simplify,
        Reverse = o.Reverse,
        HasPersistentData = o.HasPersistentData,
      };
      foreach (var i in o.Inputs ?? new List<FpInput>())
        c.Inputs.Add(new FpInput
        {
          Guid = i.Guid,
          MappingToken = i.MappingToken,
          PersistentToken = i.PersistentToken,
          Sources = (sources(i) ?? new Guid[0]).ToList(),
        });
      return c;
    }
  }
}
