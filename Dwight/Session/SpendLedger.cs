using System;
using System.IO;
using Grasshopper.Kernel;

namespace Dwight.Session
{
  /// <summary>
  /// The file-I/O shim for the central cost ledger (Brief AK; the decisions live in the GH-free
  /// <see cref="SpendLedgerCore"/>). It names the path and turns a document and a spend into a line; it is
  /// called from the two places a spend enters the session file (<see cref="CommandHistory.Record"/>
  /// and <see cref="CommandHistory.AppendThreadSpend"/>), with the same spend object, so the two records can
  /// never disagree, and from the end of a run's paid calls (Brief CT), which may never reach the session file.
  ///
  /// Gated on <see cref="SessionStore.Enabled"/>, the host's one opt-in: a headless context never writes a byte.
  /// A failed append is said once per session on Rhino's command line and costs nothing else.
  /// </summary>
  internal static class SpendLedger
  {
    private static bool _warned;

    public static string LedgerPath =>
      Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dwight", SpendLedgerCore.FileName);

    public static void RecordVerb(GH_Document doc, string verb, DateTime when, StoredSpend spend, string runId = null)
    {
      if (spend == null) return;
      Append(SpendLedgerCore.ForVerb(spend, when, DocId(doc), DocName(doc), DwightVerbs.Canonical(verb), spend.Provider, runId));
    }

    /// <summary>Brief CT: a run's paid calls are over, so its line is written now, once, whether or not a row ever follows.</summary>
    public static void RecordRun(GH_Document doc, string verb, StoredSpend spend, ref bool ledgered, string runId = null)
    {
      // Brief DL: an estimated line carries the run's key, so a bill that arrives after all can replace it rather than double it.
      if (SpendLedgerCore.TakeRunLine(spend, ref ledgered)) RecordVerb(doc, verb, DateTime.UtcNow, spend, spend.Estimated != null ? runId : null);
    }

    /// <summary>Brief EO: a call a Learn deck makes after its run (a judged answer, Play again), one line each, under the verb with its label. A call that used nothing writes nothing.</summary>
    public static void RecordLabelled(GH_Document doc, string verb, StoredSpend spend, string label)
    {
      if (!SpendLedgerCore.HasUsage(spend)) return;
      Append(SpendLedgerCore.ForVerb(spend, DateTime.UtcNow, DocId(doc), DocName(doc), DwightVerbs.Canonical(verb), spend.Provider, null, label));
    }

    public static void RecordTurn(GH_Document doc, string threadId, int turn, StoredSpend spend)
    {
      if (spend == null) return;
      Append(SpendLedgerCore.ForTurn(spend, DateTime.UtcNow, DocId(doc), DocName(doc), DwightVerbs.Build, spend.Provider, threadId, turn));
    }

    /// <summary>Brief CY: what the ledger says about a read not yet sent, read once for one question. A ledger that cannot be read
    /// says nothing, and the estimate uses its stated defaults.</summary>
    public static LedgerSample ReadSample()
    {
      try { return SpendLedgerCore.Sample(SpendLedgerCore.ReadTail(LedgerPath, SpendLedgerCore.SampleBytes)); }
      catch { return LedgerSample.Empty; }
    }

    private static void Append(LedgerLine line)
    {
      if (!SessionStore.Enabled || line == null) return;
      try { SpendLedgerCore.Append(LedgerPath, line); }
      catch
      {
        if (_warned) return;
        _warned = true;
        try { Rhino.RhinoApp.WriteLine(SpendLedgerCore.AppendFailedLine); } catch { }
      }
    }

    internal static string DocId(GH_Document doc)
    {
      try { return doc == null || doc.DocumentID == Guid.Empty ? "" : doc.DocumentID.ToString(); } catch { return ""; }
    }

    private static string DocName(GH_Document doc)
    {
      try { return string.IsNullOrEmpty(doc?.FilePath) ? "" : Path.GetFileName(doc.FilePath); } catch { return ""; }
    }
  }
}
