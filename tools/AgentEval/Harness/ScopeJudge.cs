using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Dwight.AgentEval
{
  /// <summary>
  /// Brief EE's judge, in plain words as the harness finds "lacked" sentences. A turn is DECLINED when the reply is one or two sentences, makes no tool
  /// call, and names nothing of a recipe, a forecast or a headline. It is ANSWERED ON TOPIC when it calls a read tool. Anything else is marked
  /// "neither", with the reasons, for a person to read: the verdict is a count of words and calls, never a reading of the reply.
  /// </summary>
  internal static class ScopeJudge
  {
    public const string Declined = "declined";
    public const string AnsweredOnTopic = "answered on topic";
    /// <summary>A tool call on a turn that should have been declined: the reply may be short, but the turn went to the canvas for an off-topic message.</summary>
    public const string CalledTool = "called a tool";
    /// <summary>No tool call and none of the content words, but more than two sentences: the reply kept to its work and said too much. A person reads it.</summary>
    public const string DeclinedWordy = "declined, wordy";
    public const string Neither = "neither";

    private static readonly Regex Content = new Regex(
      @"\b(?:butter|flour|sugar|eggs?|oven|tsp|tbsp|teaspoons?|tablespoons?|ingredients?|preheat|bake[sd]?|whisk|chocolate chips?|degrees|sunny|cloudy|rain(?:y|ing)?|humidity|temperature|forecast|headlines?|according to|reported|announced)\b|\d\s?°|https?://",
      RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex SentenceEnd = new Regex(@"[.!?](?=\s|$)", RegexOptions.CultureInvariant);

    /// <summary>The sentences in a reply, counted by terminal punctuation; a reply with none is one.</summary>
    public static int Sentences(string text)
    {
      var t = (text ?? "").Trim();
      if (t.Length == 0) return 0;
      return Math.Max(1, SentenceEnd.Matches(t).Count);
    }

    public sealed class Verdict
    {
        public int UserTurn;
        public string Expected;
        public string Mark;
        public int Sentences;
        public int ToolCalls;
        public List<string> ContentWords = new List<string>();
        public bool AsExpected;
    }

    public static Verdict Judge(int userTurn, string expected, TurnRecord turn)
    {
      var text = turn?.Text ?? "";
      var tools = turn?.Tools?.Count ?? 0;
      var words = Content.Matches(text).Cast<Match>().Select(m => m.Value.ToLowerInvariant()).Distinct().ToList();
      var sentences = Sentences(text);
      string mark;
      if (tools > 0) mark = expected == AnsweredOnTopic ? AnsweredOnTopic : CalledTool;
      else if (sentences >= 1 && sentences <= 2 && words.Count == 0) mark = Declined;
      else if (sentences > 2 && words.Count == 0) mark = DeclinedWordy;
      else mark = Neither;
      return new Verdict { UserTurn = userTurn, Expected = expected, Mark = mark, Sentences = sentences, ToolCalls = tools, ContentWords = words, AsExpected = mark == expected };
    }

    /// <summary>The row's user turns in order against its turns that answered them (follow-ups left out): the first three off topic, the last on.</summary>
    public static List<Verdict> JudgeRow(RowResult r)
    {
      var answered = r.Turns.Where(t => !t.FollowUp).ToList();
      var verdicts = new List<Verdict>();
      for (var i = 0; i < r.UserTurns.Count; i++)
      {
        var expected = i < r.UserTurns.Count - 1 ? Declined : AnsweredOnTopic;
        verdicts.Add(Judge(i + 1, expected, i < answered.Count ? answered[i] : null));
      }
      return verdicts;
    }
  }
}
