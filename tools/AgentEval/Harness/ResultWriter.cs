using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Dwight.AgentEval
{
  /// <summary>One JSON and one readable transcript per row, both through <see cref="KeyRule"/>. The transcript opens with what the row is not.</summary>
  internal static class ResultWriter
  {
    private static readonly JsonSerializerOptions Options = new JsonSerializerOptions { WriteIndented = true, IncludeFields = true };

    public static void Write(RowResult r, string directory, KeyRule keys)
    {
      if (r.Judge == "scope") r.ScopeVerdicts = ScopeJudge.JudgeRow(r);
      keys.WriteFile(Path.Combine(directory, r.RowId + ".json"), JsonSerializer.Serialize(r, Options));
      keys.WriteFile(Path.Combine(directory, r.RowId + ".md"), Transcript(r));
    }

    public static string Transcript(RowResult r)
    {
      var s = new StringBuilder();
      s.AppendLine("# " + r.RowId);
      s.AppendLine();
      s.AppendLine("**" + r.DidNotRunInGrasshopper + "**");
      s.AppendLine();
      foreach (var item in r.NotReal) s.AppendLine("- " + item);
      s.AppendLine();
      s.AppendLine("Persona: " + r.Persona + ". Provider: " + r.Provider + ", " + r.Model + ". Approval mode: " + r.ApprovalMode +
                   ". Fixture: " + r.Fixture + " (" + r.FixtureOrigin + "). Full control: " + (r.FullControl ? "on" : "off") +
                   ". Component list: " + (r.ComponentListLoaded ? "loaded" : "not loaded") + ". Steps per turn: " + r.StepsPerTurn + ".");
      s.AppendLine("Ended: " + r.EndedBecause + ". " + r.Turns.Count + " turns, " + r.TotalInputTokens + " in / " + r.TotalOutputTokens + " out, " +
                   r.TotalCents.ToString("0.00") + " cents, " + r.TotalSeconds.ToString("0.0") + " s.");
      if (r.UnknownToolsTried.Count > 0) s.AppendLine("Tools it tried that do not exist: " + string.Join(", ", r.UnknownToolsTried) + ".");
      if (r.FixtureMisses.Count > 0) s.AppendLine("Fixture misses, the instrument's gap and not the product's: " + string.Join(", ", r.FixtureMisses) + ".");
      if (r.SaidItLacked.Count > 0)
      {
        s.AppendLine("Where it said it lacked something:");
        foreach (var line in r.SaidItLacked) s.AppendLine("- " + line);
      }
      if (r.ScopeVerdicts.Count > 0)
      {
        s.AppendLine("Scope judge (words and calls, not a reading of the reply):");
        foreach (var v in r.ScopeVerdicts)
          s.AppendLine("- turn " + v.UserTurn + ": " + v.Mark + " (expected " + v.Expected + (v.AsExpected ? ", as expected" : ", NOT as expected") + "), " + v.Sentences + " sentences, " + v.ToolCalls + " tool calls" + (v.ContentWords.Count > 0 ? ", content words: " + string.Join(", ", v.ContentWords) : "") + ".");
      }
      s.AppendLine();
      s.AppendLine("The user's turns, in order:");
      foreach (var u in r.UserTurns) s.AppendLine("> " + u.Replace("\n", "\n> "));

      foreach (var t in r.Turns)
      {
        s.AppendLine();
        s.AppendLine("## Turn " + t.Number + " (" + t.Capability + (t.FollowUp ? ", follow-up" : "") + (t.Forced ? ", forced" : "") + ")");
        foreach (var fact in t.TurnStartFacts)
          s.AppendLine("Told at the start (" + fact.Name + ", " + (fact.Sent ? "in the system text" : "NOT in the system text") + "): " + fact.Text);
        s.AppendLine("Outcome " + t.Outcome + (t.ClosingStep ? ", with its closing step" : "") + ", stop " + (t.StopReason ?? "none") + ", " + t.Steps + " steps, request_mutation offered: " + (t.RequestOffered ? "yes" : "no") +
                     ", " + (t.InputTokens + t.CacheReadTokens + t.CacheWriteTokens) + " in (" + t.CacheReadTokens + " cached) / " + t.OutputTokens + " out, " +
                     t.Cents.ToString("0.00") + " cents, " + t.Seconds.ToString("0.0") + " s.");
        if (t.AnnouncedLineCatch || t.AnnouncedForcingCatch || t.SaidWillNoCard)
          s.AppendLine("Ended announcing with no card: line's catch " + (t.AnnouncedLineCatch ? "yes" : "no") + ", forcing catch " + (t.AnnouncedForcingCatch ? "yes" : "no") +
                       ", plain words " + (t.SaidWillNoCard ? "yes" : "no") + ". \"" + t.AnnouncedSentence + "\"");
        foreach (var tool in t.Tools)
        {
          s.AppendLine();
          s.AppendLine("- **" + tool.Name + "** (" + tool.AnsweredBy + ")");
          s.AppendLine("  - args: `" + (tool.Arguments ?? "") + "`");
          s.AppendLine("  - answer: `" + Short(tool.Answer) + "`");
        }
        foreach (var c in t.Cards)
        {
          s.AppendLine();
          s.AppendLine("**CARD** kind " + c.Kind + (c.Spot == null ? "" : " at " + c.Spot) + ", mode decided " + c.Decision + ", " + c.Answer + ". Why: " + c.Why);
          s.AppendLine("Request: `" + c.Seed + "`");
        }
        if (!string.IsNullOrEmpty(t.Text))
        {
          s.AppendLine();
          s.AppendLine("The model said:");
          s.AppendLine();
          s.AppendLine(t.Text);
        }
        if (!string.IsNullOrEmpty(t.WithheldText))
        {
          s.AppendLine();
          s.AppendLine("The model said, withheld from the user as the product withholds it:");
          s.AppendLine();
          s.AppendLine(t.WithheldText);
        }
        foreach (var line in t.PluginLines)
        {
          s.AppendLine();
          s.AppendLine("Plugin: " + line);
        }
        if (!string.IsNullOrEmpty(t.Note)) { s.AppendLine(); s.AppendLine("Note: " + t.Note); }
        if (!string.IsNullOrEmpty(t.Error)) { s.AppendLine(); s.AppendLine("Error: " + t.Error); }
      }
      return s.ToString();
    }

    private static string Short(string text)
    {
      if (string.IsNullOrEmpty(text)) return "";
      return text.Length <= 1500 ? text : text.Substring(0, 1500) + " [" + (text.Length - 1500) + " more characters in the JSON]";
    }
  }
}
