using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Dwight.Data;

namespace Dwight.Agent
{
  /// <summary>
  /// A COMPONENT'S CONTENT, AS A READ SAW IT (Phase-9 U22) - the staleness stamp.
  ///
  /// THE MISS IT CLOSES. The model said, in its own words, "I have no canvas-changed event - I only find out by
  /// re-reading" (#719). The canvas-change fact (#573/#660) already tells it, at the start of a turn, THAT the canvas
  /// moved and where the region set moved; it cannot tell it WHICH of the components it holds a reading of are now
  /// out of date. This is that: at the moment a read returns a component, the registry keeps this stamp against its
  /// id, and at the next turn the plugin compares it with the live component and names the ids that differ. A fact the
  /// plugin states beats an instruction the model must remember (#573) - the model never has to remember to re-read.
  ///
  /// WHAT GOES IN, AND WHAT MUST NOT. The parts are the things a read reports and a decision rests on: the TYPE, the
  /// NICKNAME, the WIRING (which source feeds each input, in order), the held VALUE (a slider, panel or constant's
  /// whole state, and the internalised data on the inputs) and a script's SOURCE. A MOVE must not change it: the
  /// stamp is composed from <see cref="FpObject"/>, which is position-free by construction, and the test pins that by
  /// reflection as well as by behaviour - a moved component is not a changed one, and a fact that cried "changed" at
  /// every tidy would be skipped the day it mattered.
  ///
  /// REPORTED LIMITS, NOT HIDDEN ONES. (1) Internalised data is digested through the fingerprint's BOUNDED token (24
  /// branches, 8 items per branch): an edit beyond that sample does not change the stamp. (2) A script whose source
  /// could not be read carries an "unreadable" source part on both sides, so an edit to it is invisible; that is the
  /// existing #779 vocabulary (cannot tell), and here it errs toward silence because the alternative is a line that is
  /// wrong every turn. (3) The stamp lives in memory with the registry's session, like the reads it describes.
  ///
  /// GH-free (ADR-091): the shell reads the live object into an <see cref="FpObject"/> and hands it here.
  /// </summary>
  internal sealed class ComponentStamp
  {
    /// <summary>The parts, in the order they are named in a sentence.</summary>
    public const string Type = "type";
    public const string Name = "name";
    public const string Wiring = "wiring";
    public const string Value = "value";
    public const string Source = "source";
    public const string Locked = "locked";

    private readonly string[] _parts;
    private static readonly string[] Names = { Type, Name, Wiring, Value, Source, Locked };

    private ComponentStamp(string[] parts) { _parts = parts; }

    /// <summary>The whole stamp as one string - for a canvas-wide token, never shown to the model.</summary>
    public string Digest => string.Join("/", _parts);

    /// <param name="o">The object read by the fingerprint's own reader (type, value token, inputs with their ordered
    /// wire sources and bounded persistent-data digests).</param>
    /// <param name="nickName">The live nickname - the fingerprint deliberately carries none (see AsLeftRecord).</param>
    /// <param name="stateToken">The whole held state (<see cref="StateToken.Of"/>), null for a type that holds none.</param>
    /// <param name="isScript">The object is a script component: its value token IS its source digest, so it is filed
    /// under source, not value.</param>
    public static ComponentStamp Of(FpObject o, string nickName, string stateToken, bool isScript)
    {
      if (o == null) return null;

      var wiring = new StringBuilder();
      var held = new StringBuilder();
      foreach (var ip in o.Inputs ?? new List<FpInput>())
      {
        if (ip == null) continue;
        wiring.Append(ip.Guid).Append('<');
        foreach (var s in ip.Sources ?? new List<Guid>()) wiring.Append(s).Append(',');   // order is part of the wiring
        wiring.Append(';');
        if (ip.PersistentToken != null) held.Append(ip.Guid).Append('=').Append(ip.PersistentToken).Append(';');
      }

      var value = new StringBuilder();
      if (!isScript) value.Append(o.ValueToken ?? "");
      value.Append('|').Append(stateToken ?? "").Append('|').Append(held);

      var source = isScript
        ? (o.ScriptSourceUnreadable ? "unreadable" : (o.ValueToken ?? ""))
        : "";

      return new ComponentStamp(new[]
      {
        H(o.TypeName ?? ""),
        H(nickName ?? ""),
        H(wiring.ToString()),
        H(value.ToString()),
        H(source),
        o.Locked ? "1" : "0",
      });
    }

    /// <summary>The parts that differ from an EARLIER reading, in sentence order. Empty when none does.</summary>
    public IReadOnlyList<string> ChangesSince(ComponentStamp earlier)
    {
      if (earlier == null) return Array.Empty<string>();
      var changed = new List<string>();
      for (var i = 0; i < Names.Length; i++)
        if (!string.Equals(_parts[i], earlier._parts[i], StringComparison.Ordinal)) changed.Add(Names[i]);
      return changed;
    }

    /// <summary>One token over a set of stamped nodes: ids and stamps only, so it cannot move with a position, and any
    /// stamped node's content changing - or a stamped node appearing or going - changes it. Unstamped nodes are left
    /// out (their content was not read, so it cannot be claimed unchanged).</summary>
    public static string CanvasToken(IEnumerable<KeyValuePair<int, ComponentStamp>> nodes)
    {
      var sb = new StringBuilder();
      foreach (var kv in (nodes ?? Array.Empty<KeyValuePair<int, ComponentStamp>>()).Where(k => k.Value != null).OrderBy(k => k.Key))
        sb.Append(kv.Key).Append(':').Append(kv.Value.Digest).Append(';');
      return H(sb.ToString());
    }

    private static string H(string s)
    {
      using (var sha = SHA256.Create())
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(s))).Substring(0, 16);
    }
  }

  /// <summary>One id the model has read, and what the read saw.</summary>
  internal sealed class ReadRecord
  {
    public ComponentStamp Stamp { get; set; }
    /// <summary>The cluster it sits inside, or null at the top level - the live lookup needs it.</summary>
    public int? Inside { get; set; }
    /// <summary>The read revision it was read at.</summary>
    public int Revision { get; set; }
  }

  /// <summary>One component whose reading is out of date.</summary>
  internal sealed class StaleRead
  {
    public int Id { get; set; }
    /// <summary>The parts that differ. Empty with <see cref="Removed"/> true.</summary>
    public IReadOnlyList<string> Changes { get; set; } = Array.Empty<string>();
    /// <summary>The component is no longer on the canvas.</summary>
    public bool Removed { get; set; }
  }

  /// <summary>
  /// THE TURN-START LINE ABOUT THE COMPONENTS THE MODEL HAS READ (U22), composed here so the sentence the model reads
  /// is the sentence a test reads (ADR-091).
  ///
  /// SILENT WHEN NOTHING DIFFERS, for the reason the canvas fact is (#573): a line that appears every turn trains the
  /// reader to skip it. BOUNDED (ADR-035): at most <see cref="MaxListed"/> ids are named and the rest are counted, with
  /// the total stated, never dropped. It names ids and parts only - never who changed what, and never a value: Dwight
  /// knows that a component differs from the reading, not why.
  ///
  /// IT REPEATS UNTIL THE MODEL RE-READS. The stamp is the reading's, so a component that changed stays named every
  /// turn until a read returns it again; the line is a claim about the model's reading, and that stays true until the
  /// reading is replaced.
  /// </summary>
  internal static class StaleReadsFact
  {
    public const int MaxListed = 20;

    public static string For(IReadOnlyList<StaleRead> stale)
    {
      var rows = (stale ?? Array.Empty<StaleRead>()).Where(s => s != null && (s.Removed || s.Changes.Count > 0))
                                                    .OrderBy(s => s.Id).ToList();
      if (rows.Count == 0) return null;

      var listed = rows.Take(MaxListed).Select(s => s.Removed ? s.Id + " (removed)" : s.Id + " (" + string.Join(", ", s.Changes) + ")");
      var line = string.Join(", ", listed);
      if (rows.Count > MaxListed) line += ", and " + (rows.Count - MaxListed) + " more (" + rows.Count + " in all)";

      return "READS: since you read them, these components have changed: " + line + ". What you were told about them " +
             "may no longer be true; read them again before relying on it.";
    }
  }
}
