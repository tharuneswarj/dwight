using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;

namespace Dwight.UI
{
  /// <summary>What kind of value a port holds, which decides whether a question can be asked about it.</summary>
  internal enum ReadingKind { Number, ListCount }

  /// <summary>
  /// One value the plugin READ from the live solve (Brief DE): a port on a component, with its true value as text. The model never asserts a value; it is
  /// handed this and writes the question around it. GH-free (ADR-091): the shim reads the canvas and hands these in.
  /// </summary>
  internal sealed class PortReading
  {
    public Guid Component;
    public string Type;
    public string Nick;
    public string Port;
    public ReadingKind Kind;
    /// <summary>The true value, as the question's options will show it: a number as written, or the count of a list of strings.</summary>
    public string Value;
  }

  /// <summary>A step that has an answer the plugin can read: which step, and the reading it will ask about.</summary>
  internal sealed class TeachingFact
  {
    public int Step;
    public PortReading Reading;
  }

  /// <summary>One practice question, as the model wrote it and the plugin checked it.</summary>
  internal sealed class TeachingQuestion
  {
    public int Step;
    public string Question;
    public IReadOnlyList<string> Options;
    public int TrueIndex;
    public string Hint;
    /// <summary>The component whose value is covered while the question is open. The plugin's, never the model's.</summary>
    public Guid Component;
    /// <summary>The true value, from the reading and never from the reply.</summary>
    public string TrueValue;
  }

  /// <summary>
  /// WHICH STEPS HAVE A QUESTION (Brief DE ruling 2). One question per step that has an answer the plugin can read: the first component of the step, in the
  /// step's order, that has a reading, and the first reading on it. A step with none has no question and nothing is said about it. The plugin reads the true
  /// value; this only chooses where.
  /// </summary>
  internal static class TeachingChooser
  {
    public static IReadOnlyList<TeachingFact> Choose(IReadOnlyList<IReadOnlyList<Guid>> steps, IReadOnlyDictionary<Guid, IReadOnlyList<PortReading>> readings)
    {
      var facts = new List<TeachingFact>();
      if (steps == null || readings == null) return facts;
      for (var i = 0; i < steps.Count; i++)
      {
        foreach (var id in steps[i] ?? (IReadOnlyList<Guid>)Array.Empty<Guid>())
        {
          if (!readings.TryGetValue(id, out var list) || list == null) continue;
          var r = list.FirstOrDefault(x => x != null && !string.IsNullOrWhiteSpace(x.Value));
          if (r == null) continue;
          facts.Add(new TeachingFact { Step = i, Reading = r });
          break;
        }
      }
      return facts;
    }
  }

  /// <summary>A number as a person reads it, for the options and the reveal: whole numbers without a point, others to four places, never a bool, never a NaN.</summary>
  internal static class TeachingNumbers
  {
    public static bool TryText(object v, out string text)
    {
      text = null;
      if (v is bool) return false;
      double d;
      if (v is int i) d = i;
      else if (v is long l) d = l;
      else if (v is double x) d = x;
      else if (v is float f) d = f;
      else if (v is decimal m) d = (double)m;
      else return false;
      if (double.IsNaN(d) || double.IsInfinity(d)) return false;
      text = d == Math.Round(d) && Math.Abs(d) < 1e12 ? ((long)d).ToString(CultureInfo.InvariantCulture) : d.ToString("0.####", CultureInfo.InvariantCulture);
      return true;
    }
  }

  /// <summary>The steps of an Explain, as the panel's view holds them: each step's components, and its sentence as plain words for the question writer.</summary>
  internal static class TeachingSteps
  {
    public static (List<IReadOnlyList<Guid>> Steps, List<string> Sentences) Of(string stepsView)
    {
      var steps = new List<IReadOnlyList<Guid>>();
      var sentences = new List<string>();
      try
      {
        var arr = JsonNode.Parse(stepsView)?["steps"]?.AsArray();
        if (arr != null)
          foreach (var s in arr)
          {
            var ids = new List<Guid>();
            var raw = s?["ids"]?.AsArray();
            if (raw != null) foreach (var id in raw) if (Guid.TryParse(id?.GetValue<string>(), out var g)) ids.Add(g);
            steps.Add(ids);
            var html = s?["text"]?.GetValue<string>() ?? "";
            var text = System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(html, "<[^>]*>", ""));
            sentences.Add(System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim());
          }
      }
      catch { }
      return (steps, sentences);
    }

    /// <summary>Brief EI: the Explain's summary as plain words, for the deck's first card and the concept card's context; empty when there is none.</summary>
    public static string Summary(string stepsView)
    {
      try
      {
        var html = JsonNode.Parse(stepsView)?["summary"]?.GetValue<string>() ?? "";
        var text = System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(html, "<[^>]*>", ""));
        return System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
      }
      catch { return ""; }
    }
  }

  /// <summary>The gate that wraps everything in this unit: unless Learn was chosen for this run, nothing here runs, costs, draws or says anything.</summary>
  internal static class TeachingGate
  {
    /// <summary>Questions are asked only when the person chose Learn for this run, and only on a Walk (two or more components, and a page that can show one).</summary>
    public static bool Active(bool learnChosen, bool isWalk) => learnChosen && isWalk;
  }

  /// <summary>The plugin's words in a lesson (#235) that outlive the deck's own (<see cref="DeckText"/>): the peek, the note when there are no questions, the pair a row keeps, and the cost.</summary>
  internal static class TeachingText
  {
    public const string Peek = "You can peek; this is practice, not a lock.";
    public const string Unavailable = "Dwight could not write questions this time, so Step by step runs without them.";

    /// <summary>"4 of 6 right".</summary>
    public static string Score(int right, int asked) => right + " of " + asked + " right";

    /// <summary>The pair a row may hold, or null: something was asked and the right ones are among them. The writer, the store's read and the reopened line all ask this
    /// one question, so what a row is given is what the store keeps and what the reopened line says.</summary>
    public static int[] Pair(int right, int asked) => asked > 0 && right >= 0 && right <= asked ? new[] { right, asked } : null;

    /// <summary>"Questions cost one more call, 2.1 cents": the ledger's figure after the call. No figure when the run had no price.</summary>
    public static string Cost(double? cents) =>
      cents.HasValue ? "Questions cost one more call, " + (cents.Value < 0.1 ? "less than 0.1" : cents.Value.ToString("0.0", CultureInfo.InvariantCulture)) + " cents."
                     : "Questions cost one more call.";
  }
}
