using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace Dwight.Data
{
  /// <summary>
  /// The GH-free core of the behaviour fingerprint (WP-1.8): the token assembly, the relay splice, and the
  /// hash - everything the <see cref="BehaviourFingerprint"/> reader delegates to once it has read the live
  /// document into the plain <see cref="FpDoc"/> model. Extracted here (the Layout/Segment headless-core
  /// pattern) so the safety-critical logic can be unit-tested without Grasshopper.
  ///
  /// The fingerprint proves Reorganise changed nothing that computes (ADR-086): a node token per computational
  /// object, its editable value, each input's data-tree mapping, its ordered wire sources (resolved back
  /// THROUGH provably pass-through relays so an inserted labelled boundary param is invisible), and its
  /// persistent-data digest. It is compared only against itself (before vs after one apply), so the token
  /// FORMAT is private and needs no cross-version or cross-culture stability - only internal consistency.
  /// </summary>
  internal static class BehaviourFingerprintCore
  {
    public static string Compute(FpDoc doc)
    {
      var tokens = new List<string>();
      if (doc?.Objects != null)
      {
        var byGuid = new Dictionary<Guid, FpObject>();
        foreach (var o in doc.Objects) if (o != null) byGuid[o.Guid] = o;

        var passThrough = new HashSet<Guid>();
        foreach (var o in doc.Objects) if (o != null && IsPassThrough(o)) passThrough.Add(o.Guid);

        foreach (var o in doc.Objects)
        {
          if (o == null || passThrough.Contains(o.Guid)) continue;   // relay/boundary param spliced out
          tokens.Add("n:" + o.Guid + ":" + o.TypeName);
          if (o.ValueToken != null) tokens.Add("v:" + o.Guid + ":" + o.ValueToken);

          foreach (var ip in o.Inputs ?? EmptyInputs)
          {
            if (ip == null) continue;
            tokens.Add("m:" + ip.Guid + ":" + ip.MappingToken);
            var sources = ip.Sources ?? EmptyGuids;
            for (var i = 0; i < sources.Count; i++)
            {
              var real = ResolveThrough(sources[i], passThrough, byGuid);
              // include the source INDEX (WP-1.8): a param merges its sources in wire order, so reordering
              // them changes the computed data even though the source SET is identical.
              tokens.Add("w:" + real + ">" + ip.Guid + ":" + i);
            }
            // the param's own internalized value digest (WP-1.8): an in-place edit changes computation while
            // leaving topology and counts identical, so the value must enter the fingerprint.
            if (ip.PersistentToken != null) tokens.Add("p:" + ip.Guid + ":" + ip.PersistentToken);
          }
        }
      }

      tokens.Sort(StringComparer.Ordinal);
      using (var sha = SHA256.Create())
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("|", tokens))));
    }

    /// <summary>A generic param with exactly one source, no tree toggle, and no data of its own copies its
    /// input straight through, so it is transparent to computation and spliced out of the fingerprint.</summary>
    public static bool IsPassThrough(FpObject o)
    {
      return o != null && o.IsGenericParam && o.SourceCount == 1
        && o.DataMappingNone && !o.Simplify && !o.Reverse && !o.Locked && !o.HasPersistentData;
    }

    /// <summary>Walk a wire source back through any chain of pass-through relays to the real upstream output,
    /// so A -&gt; relay -&gt; B fingerprints exactly as A -&gt; B. Cycle-safe via the visited set.</summary>
    private static Guid ResolveThrough(Guid src, HashSet<Guid> passThrough, Dictionary<Guid, FpObject> byGuid)
    {
      var cur = src;
      var visited = new HashSet<Guid>();
      while (passThrough.Contains(cur) && visited.Add(cur) && byGuid.TryGetValue(cur, out var o))
      {
        if (o.Inputs == null || o.Inputs.Count < 1 || o.Inputs[0]?.Sources == null || o.Inputs[0].Sources.Count < 1) break;
        cur = o.Inputs[0].Sources[0];
      }
      return cur;
    }

    private static readonly List<FpInput> EmptyInputs = new List<FpInput>();
    private static readonly List<Guid> EmptyGuids = new List<Guid>();
  }

  /// <summary>A GH-free snapshot of what a document computes, produced by the fingerprint reader. Groups and
  /// scribbles are excluded by the reader (Reorganise adds those); only computational objects appear.</summary>
  internal sealed class FpDoc
  {
    public List<FpObject> Objects { get; set; } = new List<FpObject>();
  }

  /// <summary>One computational object (component or standalone param). For a standalone param, <see cref="Inputs"/>
  /// holds itself, and the pass-through flags describe it as a potential relay.</summary>
  /// <summary>
  /// One computational object, as the behaviour fingerprint sees it.
  ///
  /// THERE IS NO NICKNAME HERE AND THAT IS DELIBERATE. This record answers "does this definition still
  /// compute the same thing?", and a rename does not change what a definition computes - so adding
  /// NickName would make Reorganise's byte-identical guard fail on a rename, which is a defect, not a
  /// tightening.
  ///
  /// <see cref="Dwight.Agent.AsLeftRecord"/> DOES carry the nickname, on purpose, because it answers a
  /// different question - "has the user adopted this component since Dwight placed it?" - for which a
  /// rename is the strongest available signal. The two are not variants of each other and must never be
  /// unified: this note exists so that anyone who notices the discrepancy finds the reason before they
  /// "fix" it.
  /// </summary>
  internal sealed class FpObject
  {
    public Guid Guid;
    public string TypeName;
    public string ValueToken;            // nullable: the editable value (slider/panel/toggle/swatch/value-list, or a script's source digest)

    /// <summary>
    /// This object IS a script component and its source could NOT be read (#779) - locked, encrypted, or
    /// a Rhino whose script API has moved.
    ///
    /// IT EXISTS BECAUSE TWO CONSUMERS NEED OPPOSITE FAILURE DIRECTIONS FROM ONE READ. The fingerprint
    /// asks "does this definition still compute the same thing?" and takes the stable
    /// <see cref="SourceToken.Unreadable"/> in <see cref="ValueToken"/>, so Reorganise's byte-identical
    /// guard does not abort on a definition holding a locked script - as blind as it was before, and no
    /// blinder. The as-left record asks "has the user made this theirs?" and must answer CANNOT TELL,
    /// which its own doctrine already treats as changed, so the component is LEFT ALONE. A digest that
    /// turned a read failure into "unchanged" would convert a safety property into its opposite, quietly.
    ///
    /// So this flag is the as-left record's half of the answer, carried on the SAME read rather than
    /// taken by a second reader - the shape #680 earned when a borrowed token inherited the wrong
    /// question.
    /// </summary>
    public bool ScriptSourceUnreadable;

    public bool IsGenericParam;          // a standalone Param_GenericObject (a pass-through candidate)
    public int SourceCount;              // the standalone param's source count (relay detection)
    public bool DataMappingNone = true;  // DataMapping == None
    public bool Simplify;
    public bool Reverse;
    public bool Locked;                  // a locked node blocks all data - never a transparent relay (WP-1.8)
    public bool HasPersistentData;       // carries internalized data of its own
    public List<FpInput> Inputs { get; set; } = new List<FpInput>();
  }

  /// <summary>One input param: its data-tree mapping, its persistent-data digest, and its ordered wire sources.</summary>
  internal sealed class FpInput
  {
    public Guid Guid;
    public string MappingToken;          // DataMapping/Simplify/Reverse
    public string PersistentToken;       // nullable: a bounded digest of the param's own internalized data
    public List<Guid> Sources { get; set; } = new List<Guid>();  // ordered source-param guids
  }
}
