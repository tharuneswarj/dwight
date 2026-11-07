using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Dwight.Agent
{
  /// <summary>One question of a Learn run, as the person met it. <see cref="Answer"/> is null while the question waits, and then the true value is not carried either.</summary>
  internal sealed class LessonLine
  {
    public int Step;
    public string Question;
    public string Answer;
    public string TrueValue;
    public bool Right;
    /// <summary>Brief EI: the concept card, whose answer is the model's own reading and not read from the definition. The chat is told so.</summary>
    public bool DwightsReading;
  }

  /// <summary>A region a Reorganise laid out, by its label and its components.</summary>
  internal sealed class RunRegion
  {
    public string Label;
    public IReadOnlyList<Guid> Members;
  }

  /// <summary>
  /// What a finished run rendered, as the data it rendered it from (Brief EC unit 5b). The panel keeps these as it draws them; nothing here is read from the
  /// page or from the text a person would copy, because a structured result has none (Rhino 2's measurement: Simplify's and Diagnose's hand-offs carried nothing).
  /// </summary>
  internal sealed class RunResult
  {
    public string Verb;
    /// <summary>The run's id, which its row and its ledger line carry: every door from this run finds the one chat keyed to it. Null on a row from an older build.</summary>
    public string RunKey;
    public DateTime At;
    public IReadOnlyList<Guid> Sources;
    /// <summary>The run's own numbering, which the words of its result use.</summary>
    public IReadOnlyDictionary<Guid, int> Labels;
    /// <summary>Explain's Markdown, Reorganise's outcome line, an error's words.</summary>
    public string Prose;
    public string StepsSummary;
    public IReadOnlyList<string> Steps;
    public IReadOnlyList<LessonLine> Lesson;
    public int LessonQuestions;
    public string FaultsJson;
    public string SimplifyPreviewJson;
    public string SimplifyAppliedJson;
    public string OutcomeTitle;
    public string OutcomeMessage;
    public string OutcomeAdviceJson;
    public IReadOnlyList<RunRegion> Regions;
  }

  /// <summary>
  /// WHAT A RUN'S CHAT IS SEEDED WITH (Brief EC unit 5b, from Rhino 2's measurement of the seventeenth). One message through the MODEL door, so the model reads
  /// it as Dwight's prior words: the context line naming the verb, its time and every source in the chat's numbering, then the result in the plugin's Markdown,
  /// with every id the result carries translated to the chat's (<see cref="RunIds"/>). The context line used to be chrome, and asked, the model could not name
  /// the action it came from. GH-free (ADR-091): the shim hands in the names and the chat's ids.
  /// </summary>
  internal static class RunHandOff
  {
    /// <summary>Components named in the context line, and ids listed after them; past both, the rest are counted. A whole-canvas run must not put thousands of names in every turn (ADR-035).</summary>
    public const int MaxNamed = 40, MaxListed = 400;

    public static string Seed(RunResult run, Func<Guid, string> nameOf, Func<Guid, int?> chatIdOf)
    {
      run = run ?? new RunResult();
      var ids = RunIds.Through(run.Labels, chatIdOf);
      var parts = new List<string> { ContextLine(run.Verb, run.At, run.Sources, nameOf, chatIdOf) };
      var result = Result(run, ids, nameOf, chatIdOf);
      if (!string.IsNullOrWhiteSpace(result)) parts.Add(result.Trim());
      else parts.Add(NoResult);
      return string.Join("\n\n", parts);
    }

    public const string NoResult = "It left no result to carry here.";

    /// <summary>"This conversation carries your Explain of 14:02, which ran on 6 components: Height (1), Radius (2), ... Its result follows."</summary>
    public static string ContextLine(string verb, DateTime at, IReadOnlyList<Guid> sources, Func<Guid, string> nameOf, Func<Guid, int?> chatIdOf)
    {
      var name = string.IsNullOrWhiteSpace(verb) ? "run" : verb.Trim();
      var head = "This conversation carries your " + name + " of " + Clock(at);
      var shown = new List<string>();
      var gone = 0;
      var count = 0;
      foreach (var g in sources ?? (IReadOnlyList<Guid>)Array.Empty<Guid>())
      {
        count++;
        var id = chatIdOf?.Invoke(g);
        if (!id.HasValue) { gone++; continue; }
        if (shown.Count < MaxNamed) shown.Add(Named(nameOf?.Invoke(g), id.Value));
        else if (shown.Count < MaxListed) shown.Add("(" + id.Value + ")");
      }
      if (count == 0) return head + ". Its result follows.";
      var placed = count - gone;
      var more = placed - shown.Count;
      var list = string.Join(", ", shown) + (more > 0 ? ", and " + more + " more" : "");
      var tail = gone > 0 ? (shown.Count > 0 ? "; " : "") + gone + (gone == 1 ? " is" : " are") + " no longer on the canvas" : "";
      return head + ", which ran on " + count + (count == 1 ? " component: " : " components: ") + list + tail + ". Its result follows.";
    }

    public static string Clock(DateTime at) => at.ToString("HH:mm", CultureInfo.InvariantCulture);

    private static string Named(string name, int id) =>
      string.IsNullOrWhiteSpace(name) ? "(" + id + ")" : OneLine(name) + " (" + id + ")";

    /// <summary>The result as Markdown, from whichever data the run rendered. Null when it rendered nothing a reader could use.</summary>
    public static string Result(RunResult run, Func<int, int?> ids, Func<Guid, string> nameOf, Func<Guid, int?> chatIdOf)
    {
      if (run == null) return null;
      var parts = new List<string>();
      if (run.Steps != null && run.Steps.Count > 0) parts.Add(Walk(run.StepsSummary, run.Steps, ids));
      else if (!string.IsNullOrWhiteSpace(run.Prose)) parts.Add(RunIds.RewriteMarkdown(run.Prose.Trim(), ids));
      if (run.Lesson != null && run.LessonQuestions > 0) parts.Add(Lesson(run.Lesson, run.LessonQuestions, ids));
      if (!string.IsNullOrWhiteSpace(run.FaultsJson)) parts.Add(Diagnose(run.FaultsJson, ids, chatIdOf));
      if (!string.IsNullOrWhiteSpace(run.SimplifyAppliedJson)) parts.Add(SimplifyApplied(run.SimplifyAppliedJson, ids));
      else if (!string.IsNullOrWhiteSpace(run.SimplifyPreviewJson)) parts.Add(SimplifyPreview(run.SimplifyPreviewJson, ids));
      if (!string.IsNullOrWhiteSpace(run.OutcomeTitle) || !string.IsNullOrWhiteSpace(run.OutcomeMessage)) parts.Add(Outcome(run.OutcomeTitle, run.OutcomeMessage, run.OutcomeAdviceJson, ids));
      if (run.Regions != null && run.Regions.Count > 0) parts.Add(Regions(run.Regions, nameOf, chatIdOf));
      parts.RemoveAll(string.IsNullOrWhiteSpace);
      return parts.Count == 0 ? null : string.Join("\n\n", parts);
    }

    /// <summary>A Walk: its summary, then its steps in order, each in the model's own words.</summary>
    public static string Walk(string summary, IReadOnlyList<string> steps, Func<int, int?> ids)
    {
      var sb = new StringBuilder();
      if (!string.IsNullOrWhiteSpace(summary)) sb.Append(RunIds.RewriteMarkdown(summary.Trim(), ids)).Append("\n\n");
      for (var i = 0; i < steps.Count; i++)
        sb.Append(i + 1).Append(". ").Append(OneLine(RunIds.RewriteMarkdown(steps[i] ?? "", ids))).Append('\n');
      return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// "Question 2 of 4 (step 3): ... They answered 12; the answer is 12." A question still open says so and carries no answer: the model must not hold an answer the person
    /// has not reached. Brief EI: the concept card has no step and its answer is marked as Dwight's own reading, never as read from the definition.
    /// <para>Brief EI's addendum (the twentieth's gate): a card names components by the run's numbers ("Move (15)"), so every question, answer and true answer goes
    /// through <paramref name="ids"/>, the one translation the prose takes, and a seed carries one number per component (ADR-058).</para>
    /// </summary>
    public static string Lesson(IReadOnlyList<LessonLine> lines, int questions, Func<int, int?> ids)
    {
      string Chat(string text) => OneLine(RunIds.RewriteMarkdown(text ?? "", ids));
      var sb = new StringBuilder("The person took it as a quiz of " + questions + (questions == 1 ? " question" : " questions") + ":\n");
      var k = 0;
      var answered = 0;
      var right = 0;
      foreach (var l in lines ?? (IReadOnlyList<LessonLine>)Array.Empty<LessonLine>())
      {
        k++;
        sb.Append("- Question ").Append(k).Append(" of ").Append(questions);
        if (l.Step >= 0) sb.Append(" (step ").Append(l.Step + 1).Append(')');
        sb.Append(": ").Append(Chat(l.Question));
        if (l.Answer == null) sb.Append(" Not answered yet.");
        else
        {
          answered++;
          if (l.Right) right++;
          sb.Append(" They answered ").Append(Chat(l.Answer)).Append("; the answer is ").Append(Chat(l.TrueValue))
            .Append(l.DwightsReading ? " (Dwight's own reading, not read from the definition)." : " (read from the definition).");
        }
        sb.Append('\n');
      }
      sb.Append(answered == questions ? "They got " + right + " of " + questions + "." : right + " of " + answered + " answered right so far.");
      return sb.ToString();
    }

    /// <summary>Diagnose's result as the panel draws it: the lead, each chain's root with Grasshopper's own message, the model's reading and fix, the rest of the chain, and what is not wired in.</summary>
    public static string Diagnose(string faultsJson, Func<int, int?> ids, Func<Guid, int?> chatIdOf)
    {
      try
      {
        var root = JsonNode.Parse(faultsJson);
        var sb = new StringBuilder("**Diagnose.**");
        var lead = Str(root?["lead"]);
        if (!string.IsNullOrWhiteSpace(lead)) sb.Append(' ').Append(RunIds.RewriteMarkdown(OneLine(lead), ids));
        foreach (var chain in root?["chains"]?.AsArray() ?? new JsonArray())
        {
          var r = chain?["root"];
          if (r == null) continue;
          sb.Append("\n\n").Append(Fault("Root cause", r, ids, chatIdOf));
          foreach (var m in chain?["members"]?.AsArray() ?? new JsonArray())
            if (m != null) sb.Append('\n').Append(Fault(Str(m["role"]) == "intrinsic" ? "Its own error" : "Downstream", m, ids, chatIdOf));
        }
        var stray = (root?["stray"]?.AsArray() ?? new JsonArray()).Where(s => s != null).Select(s => Title(s, ids, chatIdOf)).ToList();
        if (stray.Count > 0) sb.Append("\n\nNot wired in: ").Append(string.Join(", ", stray)).Append('.');
        return sb.ToString();
      }
      catch { return null; }
    }

    private static string Fault(string what, JsonNode f, Func<int, int?> ids, Func<Guid, int?> chatIdOf)
    {
      var line = "- **" + what + ":** " + Title(f, ids, chatIdOf);
      var message = Str(f["ghMessage"]);
      if (!string.IsNullOrWhiteSpace(message)) line += ". Grasshopper says: \"" + OneLine(message) + "\"";
      var cause = Str(f["causeMd"]) ?? Plain(Str(f["cause"]));
      if (!string.IsNullOrWhiteSpace(cause)) line += ". " + OneLine(RunIds.RewriteMarkdown(cause, ids));
      var fix = Str(f["fixMd"]) ?? Plain(Str(f["fix"]));
      if (!string.IsNullOrWhiteSpace(fix)) line += " Fix: " + OneLine(RunIds.RewriteMarkdown(fix, ids));
      return line.TrimEnd();
    }

    /// <summary>A fault's title in the chat's numbering: its own bracket translated, or the chat's id for its guid when the title carries none.</summary>
    private static string Title(JsonNode f, Func<int, int?> ids, Func<Guid, int?> chatIdOf)
    {
      var title = RunIds.RewriteMarkdown(OneLine(Str(f["title"]) ?? "a component"), ids);
      if (title.Contains("(")) return title;
      return Guid.TryParse(Str(f["guid"]), out var g) && chatIdOf?.Invoke(g) is int id ? title + " (" + id + ")" : title;
    }

    /// <summary>Simplify's proposal before anything is applied: each change in the model's words with what it removes, adds and renames, then the advice.</summary>
    public static string SimplifyPreview(string previewJson, Func<int, int?> ids)
    {
      try
      {
        var root = JsonNode.Parse(previewJson);
        var title = Str(root?["title"]) ?? "Better logic";
        var changes = root?["changes"]?.AsArray() ?? new JsonArray();
        var canApply = root?["canApply"]?.GetValue<bool>() ?? changes.Count > 0;
        var sb = new StringBuilder("**Simplify, " + title.Trim().ToLowerInvariant() + ".** ");
        if (!canApply || changes.Count == 0) sb.Append("The selection is already clean: nothing to change.");
        else
        {
          sb.Append(changes.Count).Append(changes.Count == 1 ? " change proposed" : " changes proposed").Append(", none applied yet:");
          foreach (var c in changes)
          {
            if (c == null) continue;
            var ops = new List<string>();
            var rem = (c["removes"]?.AsArray() ?? new JsonArray()).Select(r => Op(r, ids)).ToList();
            if (rem.Count > 0) ops.Add("removes " + string.Join(", ", rem));
            var add = (c["adds"]?.AsArray() ?? new JsonArray()).Select(a => Str(a?["type"]) + (string.IsNullOrWhiteSpace(Str(a?["nick"])) ? "" : " \"" + Str(a["nick"]) + "\"")).ToList();
            if (add.Count > 0) ops.Add("adds " + string.Join(", ", add));
            var ren = (c["renames"]?.AsArray() ?? new JsonArray()).Select(r => Op(r, ids) + " to \"" + Str(r?["nick"]) + "\"").ToList();
            if (ren.Count > 0) ops.Add("renames " + string.Join(", ", ren));
            sb.Append("\n- ").Append(OneLine(RunIds.RewriteMarkdown(Str(c["text"]) ?? "", ids)));
            if (ops.Count > 0) sb.Append(" (").Append(string.Join("; ", ops)).Append(')');
          }
        }
        sb.Append(Advice(root?["advice"], ids));
        return sb.ToString();
      }
      catch { return null; }
    }

    /// <summary>What a Simplify applied: its own summary, each change by name, what was skipped, and the advice.</summary>
    public static string SimplifyApplied(string appliedJson, Func<int, int?> ids)
    {
      try
      {
        var root = JsonNode.Parse(appliedJson);
        var sb = new StringBuilder("**Simplify, applied.** " + OneLine(Str(root?["summary"]) ?? ""));
        foreach (var c in root?["changes"]?.AsArray() ?? new JsonArray())
          if (c != null) sb.Append("\n- ").Append(OneLine(RunIds.RewriteMarkdown(Str(c["text"]) ?? "", ids)));
        var skipped = (root?["skipped"]?.AsArray() ?? new JsonArray()).Select(Str).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        if (skipped.Count > 0) sb.Append("\nSkipped: ").Append(string.Join("; ", skipped.Select(s => OneLine(RunIds.RewriteMarkdown(s, ids))))).Append('.');
        sb.Append(Advice(root?["advice"], ids));
        return sb.ToString();
      }
      catch { return null; }
    }

    /// <summary>A Simplify that ended in a sentence of its own (nothing to change, refused, reverted), with its advice.</summary>
    public static string Outcome(string title, string message, string adviceJson, Func<int, int?> ids)
    {
      var sb = new StringBuilder();
      if (!string.IsNullOrWhiteSpace(title)) sb.Append("**").Append(OneLine(title).TrimEnd('.')).Append(".** ");
      if (!string.IsNullOrWhiteSpace(message)) sb.Append(OneLine(RunIds.RewriteMarkdown(message, ids)));
      try { if (!string.IsNullOrWhiteSpace(adviceJson)) sb.Append(Advice(JsonNode.Parse(adviceJson), ids)); } catch { }
      return sb.ToString().Trim();
    }

    private static string Advice(JsonNode advice, Func<int, int?> ids)
    {
      var lines = new List<string>();
      foreach (var a in advice as JsonArray ?? new JsonArray())
      {
        var text = string.Join(" ", new[] { Str(a?["observation"]), Str(a?["suggestion"]) }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(OneLine));
        if (!string.IsNullOrWhiteSpace(text)) lines.Add("- " + RunIds.RewriteMarkdown(text, ids));
      }
      return lines.Count == 0 ? "" : "\n\nDwight also noticed:\n" + string.Join("\n", lines);
    }

    private static string Op(JsonNode r, Func<int, int?> ids)
    {
      var type = Str(r?["type"]) ?? "component";
      var id = r?["id"] is JsonNode n && int.TryParse(n.ToString(), out var v) ? ids?.Invoke(v) : null;
      return id.HasValue ? type + " (" + id.Value + ")" : type;
    }

    /// <summary>A Reorganise's regions, by label, each with its components in the chat's numbering.</summary>
    public static string Regions(IReadOnlyList<RunRegion> regions, Func<Guid, string> nameOf, Func<Guid, int?> chatIdOf)
    {
      var sb = new StringBuilder("Its regions:");
      foreach (var r in regions)
      {
        if (r == null) continue;
        var members = (r.Members ?? (IReadOnlyList<Guid>)Array.Empty<Guid>()).Select(g => (Id: chatIdOf?.Invoke(g), Name: nameOf?.Invoke(g))).Where(x => x.Id.HasValue).Select(x => Named(x.Name, x.Id.Value)).ToList();
        sb.Append("\n- **").Append(OneLine(string.IsNullOrWhiteSpace(r.Label) ? "Unnamed" : r.Label)).Append("**");
        if (members.Count > 0) sb.Append(": ").Append(string.Join(", ", members.Take(MaxNamed))).Append(members.Count > MaxNamed ? ", and " + (members.Count - MaxNamed) + " more" : "");
      }
      return sb.ToString();
    }

    private static string Str(JsonNode n) { try { return n?.GetValue<string>(); } catch { return n?.ToString(); } }

    private static string Plain(string html)
    {
      if (string.IsNullOrEmpty(html)) return null;
      var text = WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]*>", ""));
      return Regex.Replace(text, @"\s+", " ").Trim();
    }

    private static string OneLine(string s) => Regex.Replace(s ?? "", @"\s+", " ").Trim();
  }
}
