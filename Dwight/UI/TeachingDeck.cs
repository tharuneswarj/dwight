using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Dwight.UI
{
  /// <summary>Brief EI: what a card asks about, which decides where its truth comes from. Brief EO: <see cref="Why"/>, a question about the selection's logic the model writes and
  /// answers, marked as Dwight's reading; <see cref="Concept"/> is retired and stays only so an older record reads back.</summary>
  internal enum DeckKind { Value, Count, Which, Feeds, Concept, Why }

  /// <summary>Brief EI: how a card is answered: one option, every option that applies, or a value typed in.</summary>
  internal enum DeckForm { ChooseOne, ChooseAll, Enter }

  /// <summary>One component of the selection as a deck names it: its run id (the number the canvas badges show) and its words. The guid is the plugin's and never reaches a payload.</summary>
  internal sealed class DeckNode
  {
    public Guid Guid;
    public int Id;
    public string Type;
    public string Nick;

    /// <summary>"Divide Curve (14)": the person's nickname when set, else the type, and the run id, as the canvas's badge shows it.</summary>
    public string Label => (string.IsNullOrWhiteSpace(Nick) ? (Type ?? "") : Nick.Trim()) + " (" + Id.ToString(CultureInfo.InvariantCulture) + ")";
  }

  /// <summary>One wire inside the selection, and how many items its source port held in the solved definition (-1 when the plugin could not read it).</summary>
  internal sealed class DeckWire
  {
    public Guid From;
    public string FromPort;
    public Guid To;
    public string ToPort;
    public int Items = -1;
  }

  /// <summary>
  /// Everything a deck is planned from, READ BY THE PLUGIN on the UI thread before the call (Brief EI; ADR-056): the selection's components with their run ids, the
  /// wires among them with the item count on each, and the values the solve holds (<see cref="PortReading"/>). GH-free: the shim (<c>GhTeaching.Deck</c>) fills it.
  /// </summary>
  internal sealed class DeckInput
  {
    public IReadOnlyList<DeckNode> Nodes = Array.Empty<DeckNode>();
    public IReadOnlyList<DeckWire> Wires = Array.Empty<DeckWire>();
    public IReadOnlyDictionary<Guid, IReadOnlyList<PortReading>> Readings = new Dictionary<Guid, IReadOnlyList<PortReading>>();

    public DeckNode Node(Guid g) => Nodes.FirstOrDefault(n => n.Guid == g);
  }

  /// <summary>
  /// A question the plugin can grade, before the model words it (Brief EI). The truth is the plugin's: a value it read, a wire it followed, a set of sources it
  /// counted. The guids are plugin-only.
  /// </summary>
  internal sealed class DeckFact
  {
    public DeckKind Kind;
    public DeckForm Form;
    /// <summary>The Walk's step this card belongs to.</summary>
    public int Step = -1;
    public Guid Subject;
    public int SubjectId;
    public string SubjectType;
    public string SubjectNick;
    public string Port;
    /// <summary>For a "which" card: "consumer" (which component takes this output) or "source" (where this input comes from).</summary>
    public string Relation;
    /// <summary>A value or a count, as the plugin read it, for a Value or Count card.</summary>
    public string Truth;
    /// <summary>The plugin-built options of a Which or Feeds card, by nickname and id; null otherwise.</summary>
    public IReadOnlyList<string> Options;
    /// <summary>Which of <see cref="Options"/> are true.</summary>
    public IReadOnlyList<int> TrueSet;
    public IReadOnlyList<Guid> Lights = Array.Empty<Guid>();
    public Guid? Cover;
  }

  /// <summary>
  /// WHICH CARDS THE PLUGIN DEALS (Brief EO, Tharun's rulings from his tour of the twenty-first). A deck is mostly why-questions, which the model writes and answers in the
  /// one call. The plugin deals at most ONE card read from the live solve (a value it read, or else a count on a wire), so the person touches the canvas once, and at most
  /// ONE about the wiring (which component a wire reaches, or which components feed one), the first the Walk's order offers. The plugin builds every option a wiring card
  /// offers and holds every truth it deals. Deterministic: the same selection plans the same deck.
  /// </summary>
  internal static class DeckPlanner
  {
    public const int MaxCards = 7;
    public const int MaxWhy = 5;
    /// <summary>Under this many cards after the checks, no deck is dealt, and the card says so.</summary>
    public const int MinCards = 3;
    public const int MaxOptions = 4;
    /// <summary>A number with at most this many significant digits is typed in; a longer one is chosen from options.</summary>
    public const int EnterDigits = 4;

    public static IReadOnlyList<DeckFact> Plan(DeckInput input, IReadOnlyList<IReadOnlyList<Guid>> steps)
    {
      var facts = new List<DeckFact>();
      if (input == null || steps == null || input.Nodes.Count < 2) return facts;
      var stepOf = StepOf(steps);
      var used = new HashSet<string>(StringComparer.Ordinal);

      // The one card read from the live solve: the first value the plugin read in the Walk's order, else the first count on a wire.
      var read = TeachingChooser.Choose(steps, input.Readings).Select(r => r.Step >= 0 && r.Step < steps.Count ? ValueFact(input, r, steps[r.Step]) : null).FirstOrDefault(f => f != null)
              ?? Enumerable.Range(0, steps.Count).SelectMany(s => Structural(input, steps, s, stepOf, used)).FirstOrDefault(f => f.Kind == DeckKind.Count);
      if (read != null) { used.Add(KeyOf(read)); facts.Add(read); }

      // The one card about the wiring: the first which or feeds question the Walk's order offers.
      var wiring = Enumerable.Range(0, steps.Count).SelectMany(s => Structural(input, steps, s, stepOf, used)).FirstOrDefault(f => f.Kind == DeckKind.Which || f.Kind == DeckKind.Feeds);
      if (wiring != null) facts.Add(wiring);

      return facts.OrderBy(f => f.Step).ThenBy(f => (int)f.Kind).ToList();
    }

    /// <summary>How many why-questions the call asks for: the rest of the seven, five at most.</summary>
    public static int WhyWanted(int facts) => Math.Max(0, Math.Min(MaxWhy, MaxCards - Math.Max(0, facts)));

    /// <summary>The form a reading is asked in (Brief EI): typed when it is a count or a number of at most four significant digits, else chosen from options.</summary>
    public static DeckForm FormFor(PortReading r) =>
      r.Kind == ReadingKind.ListCount || SignificantDigits(r.Value) <= EnterDigits ? DeckForm.Enter : DeckForm.ChooseOne;

    /// <summary>The digits a person would have to type: sign, point and leading zeros not counted. "0.25" is 2, "1200" is 4, "3.1416" is 5.</summary>
    public static int SignificantDigits(string text)
    {
      var digits = new string((text ?? "").Where(char.IsDigit).ToArray()).TrimStart('0');
      return digits.Length == 0 ? 1 : digits.Length;
    }

    private static DeckFact ValueFact(DeckInput input, TeachingFact reading, IReadOnlyList<Guid> step)
    {
      var r = reading.Reading;
      var node = input.Node(r.Component);
      if (node == null) return null;
      return new DeckFact
      {
        Kind = DeckKind.Value, Form = FormFor(r), Step = reading.Step,
        Subject = node.Guid, SubjectId = node.Id, SubjectType = r.Type ?? node.Type, SubjectNick = r.Nick ?? node.Nick, Port = r.Port, Truth = r.Value,
        Lights = step.ToList(), Cover = node.Guid,
      };
    }

    /// <summary>The questions about wires a step offers, in a fixed order: how many items on a wire, which component takes or feeds a port, which components feed one.</summary>
    private static IEnumerable<DeckFact> Structural(DeckInput input, IReadOnlyList<IReadOnlyList<Guid>> steps, int s, IReadOnlyDictionary<Guid, int> stepOf, HashSet<string> used)
    {
      foreach (var id in steps[s])
      {
        var node = input.Node(id);
        if (node == null) continue;
        foreach (var f in new[] { CountFact(input, node, s, steps[s]), WhichFact(input, node, s, stepOf, "consumer"), WhichFact(input, node, s, stepOf, "source"), FeedsFact(input, node, s, stepOf) })
          if (f != null && !used.Contains(KeyOf(f))) yield return f;
      }
    }

    private static DeckFact CountFact(DeckInput input, DeckNode node, int s, IReadOnlyList<Guid> step)
    {
      var read = input.Readings.TryGetValue(node.Guid, out var list) ? list.Select(r => r.Port).ToList() : new List<string>();
      var wire = input.Wires.Where(w => w.From == node.Guid && w.Items >= 2 && !read.Contains(w.FromPort)).OrderBy(w => w.FromPort, StringComparer.Ordinal).FirstOrDefault();
      if (wire == null) return null;
      return new DeckFact
      {
        Kind = DeckKind.Count, Form = DeckForm.Enter, Step = s, Subject = node.Guid, SubjectId = node.Id, SubjectType = node.Type, SubjectNick = node.Nick,
        Port = wire.FromPort, Truth = wire.Items.ToString(CultureInfo.InvariantCulture), Lights = step.ToList(), Cover = node.Guid,
      };
    }

    private static DeckFact WhichFact(DeckInput input, DeckNode node, int s, IReadOnlyDictionary<Guid, int> stepOf, string relation)
    {
      var consumer = relation == "consumer";
      var byPort = input.Wires.Where(w => (consumer ? w.From : w.To) == node.Guid && (consumer ? w.To : w.From) != node.Guid)
        .GroupBy(w => consumer ? w.FromPort : w.ToPort, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal);
      foreach (var port in byPort)
      {
        var ends = port.Select(w => consumer ? w.To : w.From).Distinct().ToList();
        if (ends.Count != 1) continue;   // exactly one answer, or the question has none
        var truth = input.Node(ends[0]);
        if (truth == null) continue;
        var others = Distractors(input, node, new[] { truth.Guid }, stepOf, s, MaxOptions - 1);
        if (others.Count == 0) continue;
        var (options, trueSet, guids) = Options(new[] { truth }, others);
        return new DeckFact
        {
          Kind = DeckKind.Which, Form = DeckForm.ChooseOne, Step = s, Subject = node.Guid, SubjectId = node.Id, SubjectType = node.Type, SubjectNick = node.Nick,
          Port = port.Key, Relation = relation, Options = options, TrueSet = trueSet, Lights = new[] { node.Guid }.Concat(guids).ToList(),
        };
      }
      return null;
    }

    private static DeckFact FeedsFact(DeckInput input, DeckNode node, int s, IReadOnlyDictionary<Guid, int> stepOf)
    {
      var sources = input.Wires.Where(w => w.To == node.Guid && w.From != node.Guid).Select(w => w.From).Distinct().Select(input.Node).Where(n => n != null).ToList();
      if (sources.Count < 2 || sources.Count >= MaxOptions) return null;
      var others = Distractors(input, node, sources.Select(n => n.Guid).ToList(), stepOf, s, MaxOptions - sources.Count);
      if (others.Count == 0) return null;
      var (options, trueSet, guids) = Options(sources, others);
      return new DeckFact
      {
        Kind = DeckKind.Feeds, Form = DeckForm.ChooseAll, Step = s, Subject = node.Guid, SubjectId = node.Id, SubjectType = node.Type, SubjectNick = node.Nick,
        Options = options, TrueSet = trueSet, Lights = new[] { node.Guid }.Concat(guids).ToList(),
      };
    }

    /// <summary>The other options: components of the selection, never the subject and never a truth, nearest the subject's step first, then by id.</summary>
    private static List<DeckNode> Distractors(DeckInput input, DeckNode subject, IReadOnlyCollection<Guid> truths, IReadOnlyDictionary<Guid, int> stepOf, int s, int count) =>
      input.Nodes.Where(n => n.Guid != subject.Guid && !truths.Contains(n.Guid))
        .OrderBy(n => stepOf.TryGetValue(n.Guid, out var t) ? Math.Abs(t - s) : int.MaxValue).ThenBy(n => n.Id)
        .Take(Math.Max(0, count)).ToList();

    /// <summary>Every option in id order, so where the truth stands follows from the ids and never from which is true.</summary>
    private static (List<string> Options, List<int> TrueSet, List<Guid> Guids) Options(IEnumerable<DeckNode> truths, IEnumerable<DeckNode> others)
    {
      var truthSet = new HashSet<Guid>(truths.Select(n => n.Guid));
      var all = truths.Concat(others).OrderBy(n => n.Id).ToList();
      return (all.Select(n => n.Label).ToList(), all.Select((n, i) => (n, i)).Where(p => truthSet.Contains(p.n.Guid)).Select(p => p.i).ToList(), all.Select(n => n.Guid).ToList());
    }

    internal static Dictionary<Guid, int> StepOf(IReadOnlyList<IReadOnlyList<Guid>> steps)
    {
      var map = new Dictionary<Guid, int>();
      for (var s = 0; s < (steps?.Count ?? 0); s++) foreach (var g in steps[s] ?? Array.Empty<Guid>()) if (!map.ContainsKey(g)) map[g] = s;
      return map;
    }

    private static string KeyOf(DeckFact f) => f.Kind + "\n" + f.Subject + "\n" + (f.Port ?? "") + "\n" + (f.Relation ?? "");
  }

  /// <summary>What the judge said of a typed answer (Brief EO unit 2): right, partly or wrong. <see cref="None"/> is no verdict, which the card reads as could not judge.</summary>
  internal enum JudgeVerdict { None, Right, Partly, Wrong }

  /// <summary>
  /// THE GRADER (Brief EI), GH-free and the only judge of an option or a value. Choose one: the index. Choose all: the exact set. Enter: numbers compared as numbers at the
  /// reading's own precision, an integer exactly and a decimal to the digits the reading shows; a count exactly. Something that is not a number is not an answer. A typed
  /// why-answer is the one thing it cannot grade: the judge's call does (<see cref="JudgeCall"/>), and <see cref="Verdict"/> reads its word.
  /// </summary>
  internal static class DeckGrader
  {
    public static bool ChooseOne(int picked, IReadOnlyCollection<int> trueSet) => trueSet != null && trueSet.Count == 1 && trueSet.Contains(picked);

    public static bool ChooseAll(IEnumerable<int> picked, IReadOnlyCollection<int> trueSet) =>
      picked != null && trueSet != null && new HashSet<int>(picked).SetEquals(trueSet);

    /// <summary>Null when <paramref name="entered"/> is not a number; else whether it is the reading, rounded alike at the reading's precision.</summary>
    public static bool? Enter(string entered, string truth)
    {
      if (!TryNumber(entered, out var given) || !TryNumber(truth, out var real)) return null;
      var places = Places(truth);
      return places == 0 ? given == real : Math.Round(given, places, MidpointRounding.AwayFromZero) == Math.Round(real, places, MidpointRounding.AwayFromZero);
    }

    public static bool TryNumber(string text, out decimal value) =>
      decimal.TryParse((text ?? "").Trim(), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out value);

    /// <summary>The judge's word, exactly one of three; anything else is no verdict.</summary>
    public static JudgeVerdict Verdict(string word)
    {
      switch ((word ?? "").Trim().ToLowerInvariant())
      {
        case "right": return JudgeVerdict.Right;
        case "partly": return JudgeVerdict.Partly;
        case "wrong": return JudgeVerdict.Wrong;
        default: return JudgeVerdict.None;
      }
    }

    private static int Places(string text)
    {
      var t = (text ?? "").Trim();
      var dot = t.IndexOf('.');
      return dot < 0 ? 0 : t.Length - dot - 1;
    }
  }

  /// <summary>One card as the person sees it: the plugin's truth and the model's words, checked against each other.</summary>
  internal sealed class DeckCard
  {
    public DeckKind Kind;
    public DeckForm Form;
    public int Step = -1;
    public string Question;
    public string Hint;
    /// <summary>Brief EO: why the answer is what it is, two or three sentences, the model's. Shown under "Why" on a turned card and walked by Understand.</summary>
    public string Explain;
    /// <summary>Brief EO: what a why-question is about, one of <see cref="DeckReading.Topics"/>; the result's reading groups the missed cards by it.</summary>
    public string Topic;
    /// <summary>The options, for a choose card; empty for a typed one.</summary>
    public List<string> Options = new List<string>();
    /// <summary>Which options are true, for a choose card.</summary>
    public List<int> TrueSet = new List<int>();
    /// <summary>The value, for a typed card: as the plugin read it.</summary>
    public string Value;
    /// <summary>The model's card is marked as Dwight's reading; every other card's truth was read from the definition by the plugin.</summary>
    public bool Dwight;
    public List<Guid> Lights = new List<Guid>();
    public Guid? Cover;

    /// <summary>The true answer as words: the value, the true option, or the true options joined.</summary>
    public string TrueText => Form == DeckForm.Enter ? Value ?? "" : string.Join(", ", TrueSet.Where(i => i >= 0 && i < Options.Count).Select(i => Options[i]));
  }

  internal enum DeckOutcome { None, Right, Wrong, NotANumber, Judging, Partly }

  /// <summary>The plugin's words on a deck (#235), every one of them here so a test can hold them and the shells only place text.</summary>
  internal static class DeckText
  {
    public const string Kicker = "Learn";
    public const string Start = "Start";
    public const string Check = "Check";
    public const string Next = "Next";
    public const string Score = "Your score";
    public const string Read = "Read from your definition";
    public const string DwightsReading = "Dwight's reading";
    public const string RightStamp = "Right";
    public const string PartlyStamp = "Partly";
    public const string ChooseAll = "Choose all that apply.";
    public const string TypeHere = "Type a number";
    public const string NotANumber = "Type a number, such as 12 or 3.5.";
    public const string Peek = TeachingText.Peek;
    public const string AllRight = "Every one right.";
    public const string Missed = "Missed";
    public const string WalkThis = "Step by step";
    public const string WalkThisTitle = "Go through the same selection one step at a time: no new call";
    public const string AskTheChat = "Ask the chat";
    public const string AskTheChatTitle = "Open this run's chat with the questions, your answers and the score";
    public const string TryAgain = "Try again";
    public const string TryAgainTitle = "The same cards with the options in a new order: no model call";
    public const string BackToQuiz = "Back to the quiz";
    public const string FromHistory = "Read from history, so the answers are as they were. To play again, run Learn again on the live definition.";

    // Brief EO: the why-cards, the typed answer, the judge, the result's doors and Understand.
    public const string OwnWords = "or say it in your own words";
    public const string OwnPlaceholder = "Your answer, in a sentence";
    public const string Judging = "Judging your answer";
    public const string Stop = "Stop";
    public const string CouldNotJudge = "Dwight could not judge that answer. Pick an option instead.";
    public const string NoJudge = "A typed answer is judged by your provider's fast model, and there is none to use here, so pick an option.";
    public const string Why = "Why";
    public const string Understand = "Understand";
    public const string UnderstandTitle = "The missed questions, one at a time, with why the answer is what it is: no new call";
    public const string PlayAgain = "Play again";
    public const string BackToScore = "Back to the score";
    public const string JudgedBy = "Judged by Dwight";
    public const string Writing = "Writing a fresh deck of questions";
    public const string Continue = "Continue";
    public const string Cancel = "Cancel";

    public static string Of(int k, int n) => k + " of " + n;
    public static string Count(int n) => n + (n == 1 ? " question" : " questions");

    /// <summary>Card 0's gist, from the plan: how many cards, over how many steps, and where the truths come from.</summary>
    public static string Gist(int questions, int steps, bool why, bool read) =>
      Count(questions) + " on " + steps + (steps == 1 ? " step" : " steps") + ", one card at a time; the canvas goes to what each one asks about. " +
      (why && read ? "The why questions are Dwight's reading; the rest are read from your definition." : why ? "Every answer is Dwight's reading." : "Every answer is read from your definition.");

    /// <summary>Card 0's title when the Explain gave no summary.</summary>
    public static string Title(int components) => "A quiz on these " + components + " components";

    public static string Answer(string trueText, bool many) => (many ? "The answers are " : "The answer is ") + trueText + ".";
    public static string Given(string given) => "You answered " + given + ".";
    public static string Streak(int run) => run + " in a row";
    public static string MissedLine(int k, string trueText, bool many) => "Question " + k + ": " + (many ? "the answers are " : "the answer is ") + trueText + ".";
    public static string NotAnswered(int k) => "Question " + k + ": not answered.";
    public static string AskDraft(int k, string question) => "About question " + k + " of this quiz: " + (question ?? "").Trim() + " ";

    /// <summary>Play again's label: the estimate first, as any paid call says it.</summary>
    public static string PlayAgainLabel(string estimate) => string.IsNullOrEmpty(estimate) ? PlayAgain : PlayAgain + " · " + estimate;
    public static string PlayAgainTitle(string estimate) =>
      "A fresh deck of why-questions on the same selection: one new call" + (string.IsNullOrEmpty(estimate) ? "" : ", " + estimate);

    /// <summary>Brief EO unit 1: a deck that keeps fewer than three cards after its checks is not dealt, and its card says so.</summary>
    public static string TooFew(int kept) =>
      "Dwight wrote " + (kept == 0 ? "no question" : kept == 1 ? "only one question" : "only " + kept + " questions") +
      " it could check against your selection, and a deck needs three, so none is dealt this time. Step by step shows the steps, and Play again asks for a fresh deck.";
  }

  /// <summary>The deck's two views that hold no deck: the question before Play again sends, and the card while it writes. Every word the plugin's.</summary>
  internal static class DeckViews
  {
    /// <summary>"Continue?" before Play again, above the Setting's threshold: the estimate's own sentence, Continue sends, Cancel goes back to the score and sends nothing.</summary>
    public static JsonObject Confirm(string sentence) => new JsonObject
    {
      ["view"] = "result", ["confirm"] = true, ["line"] = sentence ?? "",
      ["doors"] = new JsonArray(new JsonObject { ["cmd"] = "againgo", ["label"] = DeckText.Continue, ["title"] = DeckText.Continue },
                                new JsonObject { ["cmd"] = "score", ["label"] = DeckText.Cancel, ["title"] = DeckText.Cancel }),
      ["links"] = new JsonArray(), ["back"] = false,
    };

    /// <summary>While Play again writes: one line and its Stop.</summary>
    public static JsonObject Writing() => new JsonObject
    {
      ["view"] = "result", ["busy"] = true, ["line"] = DeckText.Writing,
      ["doors"] = new JsonArray(new JsonObject { ["cmd"] = "stop", ["label"] = DeckText.Stop, ["title"] = DeckText.Stop }),
      ["links"] = new JsonArray(), ["back"] = false,
    };
  }

  /// <summary>
  /// THE RESULT'S READING (Brief EO unit 4): what the person has and has not understood, in words, from the kinds and steps of the cards alone. A pure function of the
  /// outcomes: the plugin writes it, no model is asked, and the same outcomes always read the same.
  /// </summary>
  internal static class DeckReading
  {
    /// <summary>The topics a why-card may carry, in the order the reading names them.</summary>
    public static readonly string[] Topics = { "tree", "list", "component", "approach" };

    private static readonly string[] Order = { "tree", "list", "component", "approach", "read", "wiring" };

    /// <summary>The group a card belongs to: a why-card by its topic, the reading from the live solve, or the wiring question.</summary>
    public static string GroupOf(DeckCard c)
    {
      if (c == null) return "approach";
      switch (c.Kind)
      {
        case DeckKind.Value: case DeckKind.Count: return "read";
        case DeckKind.Which: case DeckKind.Feeds: return "wiring";
        default: return Topics.Contains(c.Topic ?? "") ? c.Topic : "approach";
      }
    }

    public static string Phrase(string group)
    {
      switch (group)
      {
        case "tree": return "why the data tree is grafted, flattened or merged";
        case "list": return "why the data is a list or a single item";
        case "component": return "why these components";
        case "read": return "the reading from the live solve";
        case "wiring": return "the question about the wiring";
        default: return "why the definition takes this approach";
      }
    }

    /// <summary>Every card's outcome in, one or two sentences out. <paramref name="right"/>[i] is whether card i was answered right.</summary>
    public static string Of(IReadOnlyList<DeckCard> cards, IReadOnlyList<bool> right)
    {
      if (cards == null || cards.Count == 0) return "";
      var groups = new Dictionary<string, (List<int> Missed, int Total)>();
      for (var i = 0; i < cards.Count; i++)
      {
        var g = GroupOf(cards[i]);
        if (!groups.TryGetValue(g, out var v)) v = (new List<int>(), 0);
        var ok = right != null && i < right.Count && right[i];
        if (!ok) v.Missed.Add(cards[i].Step);
        groups[g] = (v.Missed, v.Total + 1);
      }
      var missed = Order.Where(g => groups.ContainsKey(g) && groups[g].Missed.Count > 0).ToList();
      var whole = Order.Where(g => groups.ContainsKey(g) && groups[g].Missed.Count == 0).ToList();
      if (missed.Count == 0) return DeckText.AllRight + " You have the why of this selection.";
      var sb = new StringBuilder("Missed: ");
      sb.Append(string.Join("; ", missed.Select(g => Phrase(g) + Steps(groups[g].Missed))));
      sb.Append('.');
      if (whole.Count > 0) sb.Append(" Right: ").Append(Join(whole.Select(Phrase).ToList())).Append('.');
      return sb.ToString();
    }

    /// <summary>", on step 2" or ", on steps 2 and 4", from 0-based steps; nothing for a card with no step.</summary>
    public static string Steps(IEnumerable<int> steps)
    {
      var s = steps.Where(x => x >= 0).Distinct().OrderBy(x => x).Select(x => (x + 1).ToString(CultureInfo.InvariantCulture)).ToList();
      if (s.Count == 0) return "";
      return s.Count == 1 ? ", on step " + s[0] : ", on steps " + Join(s);
    }

    private static string Join(IReadOnlyList<string> parts) =>
      parts.Count <= 1 ? string.Join("", parts) : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[parts.Count - 1];
  }

  /// <summary>
  /// A NAME ON A CARD IS A DOOR (Brief EO unit 5): every "Nick (id)" a card's words hold becomes the anchor the chat draws, for the components of this deck alone (#322: only
  /// issued ids, only the parenthesised form). A segment is a string of text or an object { t, id }; the shell sets each as text, so nothing here is markup.
  /// </summary>
  internal static class DeckAnchors
  {
    private static readonly Regex Bracketed = new Regex(@"\((\d{1,6})\)", RegexOptions.CultureInvariant);

    public static JsonArray Segments(string text, IReadOnlyDictionary<int, string> labels)
    {
      var arr = new JsonArray();
      text = text ?? "";
      if (labels == null || labels.Count == 0) { if (text.Length > 0) arr.Add(text); return arr; }
      var byLength = labels.Where(kv => !string.IsNullOrEmpty(kv.Value)).OrderByDescending(kv => kv.Value.Length).ToList();
      var plain = new StringBuilder();
      var i = 0;
      while (i < text.Length)
      {
        int id = -1, len = 0;
        foreach (var kv in byLength)
          if (text.Length - i >= kv.Value.Length && string.CompareOrdinal(text, i, kv.Value, 0, kv.Value.Length) == 0) { id = kv.Key; len = kv.Value.Length; break; }
        if (id < 0 && text[i] == '(')
        {
          var m = Bracketed.Match(text, i);
          if (m.Success && m.Index == i && int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && labels.ContainsKey(n)) { id = n; len = m.Length; }
        }
        if (id < 0) { plain.Append(text[i]); i++; continue; }
        if (plain.Length > 0) { arr.Add(plain.ToString()); plain.Clear(); }
        arr.Add(new JsonObject { ["t"] = text.Substring(i, len), ["id"] = id });
        i += len;
      }
      if (plain.Length > 0) arr.Add(plain.ToString());
      return arr;
    }

    /// <summary>The run ids a text names in the parenthesised form, in order, de-duplicated.</summary>
    public static IEnumerable<int> Named(string text)
    {
      if (string.IsNullOrEmpty(text)) yield break;
      var seen = new HashSet<int>();
      foreach (Match m in Regex.Matches(text, @"\((\d{1,6})(?:\s*,\s*(\d{1,6}))*\)"))
        foreach (Match d in Regex.Matches(m.Value, @"\d+"))
          if (int.TryParse(d.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && seen.Add(id)) yield return id;
    }
  }

  /// <summary>
  /// WHERE ONE DECK IS (Brief EI, Brief EO), GH-free. Each card is answered once and turns: right in green, partly in amber, wrong in red with why the answer is what it is.
  /// A why-card may be answered in the person's own words, which the judge's call reads; until it answers the card is judging, and a judge that fails or is stopped turns the
  /// card "could not judge" and leaves its options. The score is right of asked; Try again keeps the cards and costs nothing. The whole state is strings and numbers, so a
  /// run's record carries it (<see cref="ToJson"/>) and a reopened run shows its result, never a live door it cannot honour (<see cref="Reopened"/>).
  /// </summary>
  internal sealed class DeckState
  {
    public const int MaxTypedChars = 300;

    private sealed class Slot
    {
      public bool Answered;
      public bool Right;
      public bool Partly;
      public string Given;
      public List<int> Picked = new List<int>();
      public bool NotANumber;
      public bool Judging;
      public bool CouldNotJudge;
      public string Typed;
      public string Judged;
      public string JudgeCost;
    }

    private readonly List<DeckCard> _cards;
    private Slot[] _slots;
    private readonly Dictionary<int, (Guid Guid, string Label)> _nodes = new Dictionary<int, (Guid, string)>();

    public DeckState(IEnumerable<DeckCard> cards, string id = null, IEnumerable<DeckNode> nodes = null)
    {
      _cards = (cards ?? Enumerable.Empty<DeckCard>()).Where(c => c != null).Take(DeckPlanner.MaxCards).ToList();
      _slots = _cards.Select(_ => new Slot()).ToArray();
      Id = string.IsNullOrEmpty(id) ? Guid.NewGuid().ToString("N") : id;
      foreach (var n in nodes ?? Enumerable.Empty<DeckNode>()) if (n != null && !_nodes.ContainsKey(n.Id)) _nodes[n.Id] = (n.Guid, n.Label);
    }

    /// <summary>Brief EO unit 1: the deck the checks left too few cards for. It deals nothing; its card says how many Dwight could write.</summary>
    public static DeckState TooFewOf(int kept, IEnumerable<DeckNode> nodes = null) => new DeckState(Array.Empty<DeckCard>(), null, nodes) { Few = Math.Max(0, kept) };

    /// <summary>Which deck this is, so a live page tells its own deck from a record's.</summary>
    public string Id { get; }
    public IReadOnlyList<DeckCard> Cards => _cards;
    public int Count => _cards.Count;
    public int Attempt { get; private set; }
    /// <summary>Read back from the record: its answers stand, and nothing is offered that needs the live definition.</summary>
    public bool Reopened { get; private set; }
    /// <summary>Set on a deck that was not dealt: how many cards the checks kept. Null on a dealt deck.</summary>
    public int? Few { get; private set; }
    public bool Dealt => Few == null && _cards.Count > 0;
    /// <summary>The host's sentence when typed answers cannot be judged on this provider; null when they can. Never saved: it is a fact about now.</summary>
    public string JudgeOff { get; set; }
    /// <summary>Play again's estimate, as the host computed it; empty when it has none. Never saved.</summary>
    public string AgainEstimate { get; set; }
    /// <summary>The missed card Understand is on, or -1. Never saved.</summary>
    public int UnderstandAt { get; private set; } = -1;

    /// <summary>The run id of each component the deck names, its guid and its label, for the anchors.</summary>
    public bool TryGuid(int id, out Guid guid) { var ok = _nodes.TryGetValue(id, out var v); guid = ok ? v.Guid : Guid.Empty; return ok; }
    public IReadOnlyDictionary<int, string> Labels => _nodes.ToDictionary(kv => kv.Key, kv => kv.Value.Label);

    public bool IsAnswered(int i) => i >= 0 && i < _slots.Length && _slots[i].Answered;
    public bool IsJudging(int i) => i >= 0 && i < _slots.Length && _slots[i].Judging;
    public string TypedAt(int i) => i >= 0 && i < _slots.Length ? _slots[i].Typed : null;
    public int Right => _slots.Count(s => s.Right);
    public int Answered => _slots.Count(s => s.Answered);
    public bool AllAnswered => _slots.Length > 0 && _slots.All(s => s.Answered);
    /// <summary>The first card not yet answered, or the last card when every one is.</summary>
    public int FirstOpen { get { var i = Array.FindIndex(_slots, s => !s.Answered); return i < 0 ? Math.Max(0, _slots.Length - 1) : i; } }

    /// <summary>A card may be opened when it is answered or is the first one open: the deck is walked in order.</summary>
    public bool MayOpen(int i) => i >= 0 && i < _slots.Length && (i <= FirstOpen || _slots[i].Answered);

    /// <summary>The longest run of right answers in card order.</summary>
    public int Streak { get { int best = 0, run = 0; foreach (var s in _slots) { run = s.Right ? run + 1 : 0; best = Math.Max(best, run); } return best; } }

    /// <summary>The component hatched while card <paramref name="i"/> is open and unanswered.</summary>
    public Guid? CoverOf(int i) => i >= 0 && i < _cards.Count && !_slots[i].Answered ? _cards[i].Cover : null;

    /// <summary>The cards missed or left unanswered, in card order: what Understand walks.</summary>
    public IReadOnlyList<int> MissedCards => Enumerable.Range(0, _cards.Count).Where(i => !_slots[i].Right).ToList();

    /// <summary>
    /// An answer to card <paramref name="i"/>: "o:2" one option, "s:0,2" a set, "v:3.5" a typed value, "t:words" a why-card in the person's own words. A card takes one
    /// answer; a typed value that is not a number is not an answer, and the card says so and waits. Own words go to the judge: the card is judging until
    /// <see cref="Judged"/> or <see cref="JudgeFailed"/>, and takes no other answer meanwhile.
    /// </summary>
    public DeckOutcome Answer(int i, string answer)
    {
      if (Reopened || i < 0 || i >= _cards.Count || _slots[i].Answered || _slots[i].Judging || string.IsNullOrEmpty(answer) || answer.Length < 2 || answer[1] != ':') return DeckOutcome.None;
      var card = _cards[i];
      var s = _slots[i];
      var body = answer.Substring(2);
      switch (answer[0])
      {
        case 'o' when card.Form == DeckForm.ChooseOne:
          if (!int.TryParse(body, NumberStyles.None, CultureInfo.InvariantCulture, out var one) || one < 0 || one >= card.Options.Count) return DeckOutcome.None;
          s.Picked = new List<int> { one };
          s.Given = card.Options[one];
          s.Right = DeckGrader.ChooseOne(one, card.TrueSet);
          break;
        case 's' when card.Form == DeckForm.ChooseAll:
          var picked = new List<int>();
          foreach (var part in body.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
          {
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var k) || k < 0 || k >= card.Options.Count) return DeckOutcome.None;
            if (!picked.Contains(k)) picked.Add(k);
          }
          if (picked.Count == 0) return DeckOutcome.None;
          picked.Sort();
          s.Picked = picked;
          s.Given = string.Join(", ", picked.Select(k => card.Options[k]));
          s.Right = DeckGrader.ChooseAll(picked, card.TrueSet);
          break;
        case 'v' when card.Form == DeckForm.Enter:
          var graded = DeckGrader.Enter(body, card.Value);
          if (graded == null) { s.NotANumber = true; return DeckOutcome.NotANumber; }
          s.Given = body.Trim();
          s.Right = graded.Value;
          break;
        case 't' when card.Kind == DeckKind.Why && JudgeOff == null && !s.CouldNotJudge:
          var words = body.Trim();
          if (words.Length == 0 || words.Length > MaxTypedChars) return DeckOutcome.None;
          s.Typed = words;
          s.Judging = true;
          return DeckOutcome.Judging;
        default:
          return DeckOutcome.None;
      }
      s.Answered = true;
      s.NotANumber = false;
      return s.Right ? DeckOutcome.Right : DeckOutcome.Wrong;
    }

    /// <summary>The judge's verdict on card <paramref name="i"/>'s own words: right counts, partly and wrong do not. No verdict is a judge that failed.</summary>
    public DeckOutcome Judged(int i, JudgeVerdict verdict, string sentence, string cost)
    {
      if (i < 0 || i >= _slots.Length || !_slots[i].Judging) return DeckOutcome.None;
      if (verdict == JudgeVerdict.None) { JudgeFailed(i); return DeckOutcome.None; }
      var s = _slots[i];
      s.Judging = false;
      s.Answered = true;
      s.Given = s.Typed;
      s.Right = verdict == JudgeVerdict.Right;
      s.Partly = verdict == JudgeVerdict.Partly;
      s.Judged = (sentence ?? "").Trim();
      s.JudgeCost = cost ?? "";
      return s.Right ? DeckOutcome.Right : s.Partly ? DeckOutcome.Partly : DeckOutcome.Wrong;
    }

    /// <summary>The judge failed or was stopped: the card is not answered, its own words go, and its options are the way to answer it.</summary>
    public bool JudgeFailed(int i)
    {
      if (i < 0 || i >= _slots.Length || !_slots[i].Judging) return false;
      _slots[i].Judging = false;
      _slots[i].CouldNotJudge = true;
      return true;
    }

    /// <summary>
    /// Try again: every answer cleared and every choose card's options in a new order, the truth followed to where it now stands. No model call: the cards are the ones
    /// the run made. A deck read back from the record cannot, because its truths were read from a definition that may have changed since.
    /// </summary>
    public bool TryAgain()
    {
      if (Reopened || _cards.Count == 0 || _slots.Any(s => s.Judging)) return false;
      Attempt++;
      foreach (var c in _cards)
      {
        if (c.Options.Count < 2) continue;
        var order = Rotate(Enumerable.Range(0, c.Options.Count).ToList(), 1);   // option at new place j is the one that stood at order[j]
        c.Options = order.Select(o => c.Options[o]).ToList();
        c.TrueSet = c.TrueSet.Select(t => order.IndexOf(t)).OrderBy(t => t).ToList();
      }
      _slots = _cards.Select(_ => new Slot()).ToArray();
      UnderstandAt = -1;
      return true;
    }

    private static List<int> Rotate(List<int> xs, int by) => xs.Skip(by).Concat(xs.Take(by)).ToList();

    /// <summary>Understand: the first missed card, or the next or the one before; -1 leaves it for the score. Returns where it is.</summary>
    public int UnderstandGo(int to)
    {
      var missed = MissedCards;
      UnderstandAt = to >= 0 && to < missed.Count ? to : -1;
      return UnderstandAt;
    }

    // ---------------------------------------------------------------- views (every word the plugin's; the shells set them as text)

    private JsonArray Seg(string text) => DeckAnchors.Segments(text, Labels);

    /// <summary>Card 0: the idea, before anything on the canvas changes. A deck that was not dealt shows its result card instead.</summary>
    public JsonObject StartView(string title, int components, int steps, string cost)
    {
      if (!Dealt) return ResultView();
      var v = new JsonObject
      {
        ["view"] = "start", ["kicker"] = DeckText.Kicker,
        ["title"] = string.IsNullOrWhiteSpace(title) ? DeckText.Title(components) : title.Trim(),
        ["gist"] = DeckText.Gist(Count, steps, _cards.Any(c => c.Dwight), _cards.Any(c => !c.Dwight)),
        ["count"] = DeckText.Count(Count), ["start"] = DeckText.Start,
      };
      if (!string.IsNullOrEmpty(cost)) v["cost"] = cost;
      return v;
    }

    /// <summary>A question card, open, judging or turned.</summary>
    public JsonObject CardView(int i)
    {
      if (i < 0 || i >= _cards.Count) return null;
      var c = _cards[i];
      var s = _slots[i];
      var ticks = new JsonArray();
      for (var k = 0; k < _slots.Length; k++) ticks.Add(_slots[k].Answered ? (_slots[k].Right ? "right" : _slots[k].Partly ? "partly" : "wrong") : k == i ? "on" : "open");
      var v = new JsonObject
      {
        ["view"] = "card", ["k"] = i + 1, ["n"] = _cards.Count, ["of"] = DeckText.Of(i + 1, _cards.Count), ["ticks"] = ticks,
        ["kind"] = c.Kind.ToString().ToLowerInvariant(), ["form"] = c.Form == DeckForm.ChooseOne ? "one" : c.Form == DeckForm.ChooseAll ? "all" : "enter",
        ["question"] = c.Question ?? "", ["q"] = Seg(c.Question), ["turned"] = s.Answered, ["back"] = i > 0,
      };
      if (c.Form != DeckForm.Enter)
      {
        var opts = new JsonArray();
        for (var k = 0; k < c.Options.Count; k++)
        {
          var state = !s.Answered ? "open" : c.TrueSet.Contains(k) ? "true" : s.Picked.Contains(k) ? "false" : "open";
          opts.Add(new JsonObject { ["key"] = ((char)('A' + k)).ToString(), ["text"] = c.Options[k], ["seg"] = Seg(c.Options[k]), ["state"] = state, ["picked"] = s.Answered && s.Picked.Contains(k) });
        }
        v["options"] = opts;
        if (c.Form == DeckForm.ChooseAll && !s.Answered) v["prompt"] = DeckText.ChooseAll;
      }
      else
      {
        v["field"] = new JsonObject { ["placeholder"] = DeckText.TypeHere, ["value"] = s.Answered ? s.Given ?? "" : "" };
      }
      if (!s.Answered)
      {
        if (s.Judging)
        {
          v["busy"] = true;
          v["judging"] = new JsonObject { ["line"] = DeckText.Judging, ["stop"] = DeckText.Stop, ["typed"] = s.Typed ?? "" };
        }
        else if (c.Kind == DeckKind.Why && !Reopened)
        {
          if (s.CouldNotJudge) v["line"] = DeckText.CouldNotJudge;
          else if (JudgeOff != null) v["ownOff"] = JudgeOff;
          else v["own"] = new JsonObject { ["label"] = DeckText.OwnWords, ["placeholder"] = DeckText.OwnPlaceholder, ["check"] = DeckText.Check };
        }
        if (c.Form != DeckForm.ChooseOne) v["check"] = DeckText.Check;
        if (s.NotANumber) v["line"] = DeckText.NotANumber;
        if (c.Cover.HasValue) v["peek"] = DeckText.Peek;
      }
      else
      {
        var many = c.Form == DeckForm.ChooseAll && c.TrueSet.Count > 1;
        var verdict = s.Right ? "right" : s.Partly ? "partly" : "wrong";
        var turn = new JsonObject
        {
          ["right"] = s.Right, ["verdict"] = verdict, ["answer"] = DeckText.Answer(c.TrueText, many), ["answerSeg"] = Seg(DeckText.Answer(c.TrueText, many)),
          ["mark"] = c.Dwight ? DeckText.DwightsReading : DeckText.Read, ["dwight"] = c.Dwight,
        };
        if (s.Right) turn["stamp"] = DeckText.RightStamp;
        else if (s.Partly) turn["stamp"] = DeckText.PartlyStamp;
        if (!s.Right) turn["given"] = DeckText.Given(s.Given ?? "");
        if (!s.Right && !string.IsNullOrWhiteSpace(c.Hint)) { turn["hint"] = c.Hint; turn["hintSeg"] = Seg(c.Hint); }
        if (!string.IsNullOrWhiteSpace(s.Judged)) { turn["judged"] = s.Judged; turn["judgedSeg"] = Seg(s.Judged); turn["judgedBy"] = DeckText.JudgedBy; }
        if (!string.IsNullOrWhiteSpace(s.JudgeCost)) turn["cost"] = s.JudgeCost;
        if (!string.IsNullOrWhiteSpace(c.Explain)) turn["why"] = new JsonObject { ["head"] = DeckText.Why, ["text"] = c.Explain, ["seg"] = Seg(c.Explain) };
        v["turnedAs"] = turn;
        v["next"] = i == _cards.Count - 1 ? DeckText.Score : DeckText.Next;
      }
      return v;
    }

    /// <summary>
    /// The result card (Brief EO unit 4): the score as "5 of 7", the plugin's reading of what was missed, the missed cards, three doors (Understand, Play again, Ask the chat)
    /// and two quiet links (Try again, Walk this). A deck read back from the record offers neither Play again nor Try again. A deck that was not dealt says why instead.
    /// </summary>
    public JsonObject ResultView()
    {
      var doors = new JsonArray();
      var links = new JsonArray();
      if (!Dealt)
      {
        var few = new JsonObject { ["view"] = "result", ["few"] = true, ["line"] = DeckText.TooFew(Few ?? 0), ["back"] = false };
        if (!Reopened) doors.Add(Door("playagain", DeckText.PlayAgainLabel(AgainEstimate), DeckText.PlayAgainTitle(AgainEstimate)));
        links.Add(Door("walk", DeckText.WalkThis, DeckText.WalkThisTitle));
        few["doors"] = doors; few["links"] = links;
        return few;
      }
      var missed = new JsonArray();
      for (var i = 0; i < _cards.Count; i++)
      {
        if (_slots[i].Right) continue;
        var c = _cards[i];
        var text = _slots[i].Answered ? DeckText.MissedLine(i + 1, c.TrueText, c.Form == DeckForm.ChooseAll && c.TrueSet.Count > 1) : DeckText.NotAnswered(i + 1);
        missed.Add(new JsonObject { ["k"] = i, ["text"] = text });
      }
      if (missed.Count > 0) doors.Add(Door("understand", DeckText.Understand, DeckText.UnderstandTitle));
      if (!Reopened) doors.Add(Door("playagain", DeckText.PlayAgainLabel(AgainEstimate), DeckText.PlayAgainTitle(AgainEstimate)));
      doors.Add(Door("ask", DeckText.AskTheChat, DeckText.AskTheChatTitle));
      if (!Reopened) links.Add(Door("again", DeckText.TryAgain, DeckText.TryAgainTitle));
      links.Add(Door("walk", DeckText.WalkThis, DeckText.WalkThisTitle));
      var v = new JsonObject
      {
        ["view"] = "result", ["stamp"] = DeckText.Of(Right, _cards.Count), ["reading"] = DeckReading.Of(_cards, _slots.Select(s => s.Right).ToList()),
        ["missed"] = missed, ["doors"] = doors, ["links"] = links, ["back"] = !Reopened && _cards.Count > 0,
      };
      if (missed.Count > 0) v["missedHead"] = DeckText.Missed;
      if (Streak >= 3) v["streak"] = DeckText.Streak(Streak);
      if (Reopened) v["note"] = DeckText.FromHistory;
      return v;
    }

    private static JsonObject Door(string cmd, string label, string title) => new JsonObject { ["cmd"] = cmd, ["label"] = label, ["title"] = title };

    /// <summary>Understand (Brief EO unit 4): one missed card at a time, its question, the answer and why, with no new call. Null when there is nothing to walk.</summary>
    public JsonObject UnderstandView()
    {
      var missed = MissedCards;
      var at = UnderstandAt;
      if (at < 0 || at >= missed.Count) return null;
      var i = missed[at];
      var c = _cards[i];
      var why = !string.IsNullOrWhiteSpace(c.Explain) ? c.Explain : c.Hint ?? "";
      var answer = DeckText.Answer(c.TrueText, c.Form == DeckForm.ChooseAll && c.TrueSet.Count > 1);
      var v = new JsonObject
      {
        ["view"] = "understand", ["kicker"] = DeckText.Understand, ["of"] = DeckText.Of(at + 1, missed.Count), ["at"] = at, ["card"] = i,
        ["question"] = c.Question ?? "", ["q"] = Seg(c.Question), ["answer"] = answer, ["answerSeg"] = Seg(answer),
        ["mark"] = c.Dwight ? DeckText.DwightsReading : DeckText.Read, ["dwight"] = c.Dwight,
        ["why"] = new JsonObject { ["head"] = DeckText.Why, ["text"] = why, ["seg"] = Seg(why) },
        ["back"] = at > 0, ["next"] = at < missed.Count - 1 ? DeckText.Next : DeckText.BackToScore, ["score"] = DeckText.BackToScore,
      };
      if (_slots[i].Answered && !_slots[i].Right) v["given"] = DeckText.Given(_slots[i].Given ?? "");
      return v;
    }

    /// <summary>The deck as its chat carries it: each card's question, and the person's answer and the true answer only once the card is turned.</summary>
    public IReadOnlyList<Dwight.Agent.LessonLine> Lines() =>
      _cards.Select((c, i) => new Dwight.Agent.LessonLine
      {
        Step = c.Step,
        Question = c.Question,
        Answer = _slots[i].Answered ? _slots[i].Given : null,
        TrueValue = _slots[i].Answered ? c.TrueText : null,
        Right = _slots[i].Right,
        DwightsReading = c.Dwight,
      }).ToList();

    /// <summary>What "Ask" from a card leaves in the composer: the question, and once the card is turned, the answer given and the true one.</summary>
    public string AskDraft(int i)
    {
      if (i < 0 || i >= _cards.Count) return "";
      var c = _cards[i];
      var draft = DeckText.AskDraft(i + 1, c.Question);
      return _slots[i].Answered ? draft + "I answered " + _slots[i].Given + "; the answer is " + c.TrueText + ". " : draft;
    }

    // ---------------------------------------------------------------- the record (strings and numbers only)

    public string ToJson()
    {
      var cards = new JsonArray();
      foreach (var c in _cards)
      {
        var o = new JsonObject
        {
          ["kind"] = c.Kind.ToString().ToLowerInvariant(), ["form"] = c.Form.ToString().ToLowerInvariant(), ["step"] = c.Step, ["question"] = c.Question ?? "", ["hint"] = c.Hint ?? "",
          ["dwight"] = c.Dwight, ["lights"] = new JsonArray(c.Lights.Select(g => (JsonNode)g.ToString("D")).ToArray()),
        };
        if (!string.IsNullOrEmpty(c.Explain)) o["explain"] = c.Explain;
        if (!string.IsNullOrEmpty(c.Topic)) o["topic"] = c.Topic;
        if (c.Form == DeckForm.Enter) o["value"] = c.Value ?? "";
        else { o["options"] = new JsonArray(c.Options.Select(x => (JsonNode)x).ToArray()); o["true"] = new JsonArray(c.TrueSet.Select(x => (JsonNode)x).ToArray()); }
        if (c.Cover.HasValue) o["cover"] = c.Cover.Value.ToString("D");
        cards.Add(o);
      }
      var answers = new JsonArray();
      for (var i = 0; i < _slots.Length; i++)
      {
        if (!_slots[i].Answered) continue;
        var a = new JsonObject { ["card"] = i, ["right"] = _slots[i].Right, ["given"] = _slots[i].Given ?? "", ["picked"] = new JsonArray(_slots[i].Picked.Select(x => (JsonNode)x).ToArray()) };
        if (_slots[i].Partly) a["partly"] = true;
        if (!string.IsNullOrEmpty(_slots[i].Judged)) a["judged"] = _slots[i].Judged;
        answers.Add(a);
      }
      var nodes = new JsonArray(_nodes.OrderBy(kv => kv.Key).Select(kv => (JsonNode)new JsonObject { ["id"] = kv.Key, ["guid"] = kv.Value.Guid.ToString("D"), ["label"] = kv.Value.Label }).ToArray());
      var root = new JsonObject { ["v"] = 2, ["id"] = Id, ["attempt"] = Attempt, ["cards"] = cards, ["answers"] = answers, ["nodes"] = nodes };
      if (Few != null) root["few"] = Few.Value;
      return root.ToJsonString();
    }

    /// <summary>A deck read back from a run's record, or null when the text is not one. It is marked <see cref="Reopened"/> unless <paramref name="live"/>. A record from
    /// before Brief EO (version 1, with its concept card) reads back as it was written.</summary>
    public static DeckState FromJson(string json, bool live = false)
    {
      try
      {
        var o = JsonNode.Parse(json ?? "") as JsonObject;
        if (o == null || o["cards"] is not JsonArray cards) return null;
        var list = new List<DeckCard>();
        foreach (var n in cards)
        {
          if (n is not JsonObject c) return null;
          var card = new DeckCard
          {
            Kind = Enum.TryParse<DeckKind>(Str(c["kind"]), true, out var kind) ? kind : DeckKind.Value,
            Form = Enum.TryParse<DeckForm>(Str(c["form"]), true, out var form) ? form : DeckForm.ChooseOne,
            Step = Int(c["step"], -1), Question = Str(c["question"]) ?? "", Hint = Str(c["hint"]) ?? "", Dwight = Bool(c["dwight"]),
            Explain = Str(c["explain"]), Topic = Str(c["topic"]), Value = Str(c["value"]),
            Options = (c["options"] as JsonArray)?.Select(x => Str(x) ?? "").ToList() ?? new List<string>(),
            TrueSet = (c["true"] as JsonArray)?.Select(x => Int(x, -1)).Where(x => x >= 0).ToList() ?? new List<int>(),
            Lights = (c["lights"] as JsonArray)?.Select(x => Guid.TryParse(Str(x), out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToList() ?? new List<Guid>(),
            Cover = Guid.TryParse(Str(c["cover"]), out var cov) ? cov : (Guid?)null,
          };
          if (card.Form != DeckForm.Enter && (card.Options.Count < 2 || card.TrueSet.Count == 0 || card.TrueSet.Any(t => t >= card.Options.Count))) return null;
          if (card.Form == DeckForm.Enter && string.IsNullOrEmpty(card.Value)) return null;
          list.Add(card);
        }
        var nodes = new List<DeckNode>();
        foreach (var nn in (o["nodes"] as JsonArray) ?? new JsonArray())
          if (nn is JsonObject no && Guid.TryParse(Str(no["guid"]), out var ng)) nodes.Add(NodeOf(Int(no["id"], -1), ng, Str(no["label"]) ?? ""));
        var few = o["few"] == null ? (int?)null : Int(o["few"], 0);
        if (few == null && list.Count == 0) return null;
        var deck = new DeckState(list, Str(o["id"]), nodes.Where(x => x.Id >= 0)) { Attempt = Int(o["attempt"], 0), Reopened = !live, Few = few };
        foreach (var a in (o["answers"] as JsonArray) ?? new JsonArray())
        {
          var i = Int(a?["card"], -1);
          if (i < 0 || i >= deck._slots.Length) continue;
          deck._slots[i] = new Slot
          {
            Answered = true, Right = Bool(a["right"]), Partly = Bool(a["partly"]), Given = Str(a["given"]) ?? "", Judged = Str(a["judged"]),
            Picked = (a["picked"] as JsonArray)?.Select(x => Int(x, -1)).Where(x => x >= 0).ToList() ?? new List<int>(),
          };
        }
        return deck;
      }
      catch { return null; }
    }

    /// <summary>A node read back from the record keeps the label it was written with, whatever its nickname is now: the label is "Name (id)", so the name is the label less its id.</summary>
    private static DeckNode NodeOf(int id, Guid guid, string label)
    {
      var suffix = " (" + id.ToString(CultureInfo.InvariantCulture) + ")";
      return new DeckNode { Id = id, Guid = guid, Nick = label.EndsWith(suffix, StringComparison.Ordinal) ? label.Substring(0, label.Length - suffix.Length) : label };
    }

    private static string Str(JsonNode n) { try { return n?.GetValue<string>(); } catch { return null; } }
    private static int Int(JsonNode n, int fallback) { try { return n == null ? fallback : n.GetValue<int>(); } catch { return fallback; } }
    private static bool Bool(JsonNode n) { try { return n != null && n.GetValue<bool>(); } catch { return false; } }
  }

  /// <summary>What the deck's one call is asked about (Brief EO): the plugin's facts, the selection's components, the Walk's steps and words, and the Explain's own request.</summary>
  internal sealed class DeckAsk
  {
    public IReadOnlyList<DeckFact> Facts = Array.Empty<DeckFact>();
    public IReadOnlyList<DeckNode> Nodes = Array.Empty<DeckNode>();
    public IReadOnlyList<IReadOnlyList<Guid>> Steps = Array.Empty<IReadOnlyList<Guid>>();
    public IReadOnlyList<string> Sentences = Array.Empty<string>();
    public string Summary = "";
    /// <summary>The Explain's own request about the selection, the text the Walk sent, sent again as it is: structure only.</summary>
    public string Graph = "";

    public int Why => DeckPlanner.WhyWanted(Facts?.Count ?? 0);
  }

  /// <summary>
  /// THE ONE CALL A DECK MAKES (Brief EI; Brief EO), the pure half: the payload and the checks. The model receives the plugin's facts (a value card's TRUE value, a wiring
  /// card's plugin-built options) to word, the Walk's steps and the Explain's own request, and the number of why-questions to write about the selection's logic. It returns
  /// words: a question and a hint per fact card, three options for a value card chosen from options (TRUE among them, copied exactly), and per why-card a question naming
  /// its components, four options with the index of the true one, a hint, an explanation and a topic. Every reply is checked: a card that fails is dropped and never shown
  /// wrong, and a card naming any component outside the SELECTION by its id is dropped (ADR-058). The payload holds run ids and words only, never a guid or a coordinate.
  /// </summary>
  internal static partial class TeachingCall
  {
    public const int MaxQuestionChars = 200, MaxOptionChars = 60, MaxWhyOptionChars = 120, MaxHintChars = 240, MaxExplainChars = 480;
    public const int MaxSentences = 24;

    public static string Payload(DeckAsk ask)
    {
      var cards = new JsonArray();
      var k = 0;
      foreach (var f in ask.Facts)
      {
        k++;
        var o = new JsonObject
        {
          ["card"] = k, ["kind"] = f.Kind.ToString().ToLowerInvariant(),
          ["form"] = f.Form == DeckForm.ChooseOne ? "choose" : f.Form == DeckForm.ChooseAll ? "choose-all" : "enter",
          ["step"] = f.Step, ["component"] = f.SubjectType ?? "", ["id"] = f.SubjectId,
        };
        if (!string.IsNullOrWhiteSpace(f.SubjectNick)) o["nick"] = f.SubjectNick;
        if (!string.IsNullOrEmpty(f.Port)) o["port"] = f.Port;
        if (!string.IsNullOrEmpty(f.Relation)) o["relation"] = f.Relation;
        if (f.Kind == DeckKind.Value || f.Kind == DeckKind.Count) o["true"] = f.Truth;
        if (f.Options != null) o["options"] = new JsonArray(f.Options.Select(x => (JsonNode)x).ToArray());
        cards.Add(o);
      }
      return new JsonObject
      {
        ["selection"] = new JsonArray(ask.Nodes.OrderBy(n => n.Id).Select(n => (JsonNode)n.Label).ToArray()),
        ["summary"] = ask.Summary ?? "",
        ["steps"] = new JsonArray((ask.Sentences ?? Array.Empty<string>()).Take(MaxSentences).Select(s => (JsonNode)(s ?? "")).ToArray()),
        ["graph"] = ask.Graph ?? "",
        ["cards"] = cards,
        ["why"] = ask.Why,
      }.ToJsonString();
    }

    /// <summary>Read a reply against the ask: every card kept whose checks all hold, why-cards to the number asked, in the Walk's order. The first reason a card failed is
    /// reported, for the one repair.</summary>
    public static (IReadOnlyList<DeckCard> Kept, string Error) Parse(string json, DeckAsk ask)
    {
      var kept = new List<DeckCard>();
      string error = null;
      JsonNode reply;
      try { reply = JsonNode.Parse(json); }
      catch (Exception ex) { return (kept, "the reply was not valid JSON (" + ex.Message + ")"); }
      var selection = new HashSet<int>(ask.Nodes.Select(n => n.Id));
      var guidOf = ask.Nodes.GroupBy(n => n.Id).ToDictionary(g => g.Key, g => g.First().Guid);
      var stepOf = DeckPlanner.StepOf(ask.Steps);

      var arr = reply?["cards"] as JsonArray;
      if (arr == null && ask.Facts.Count > 0) error = "the reply has no cards for the plugin's facts";
      for (var k = 0; k < ask.Facts.Count && arr != null; k++)
      {
        var f = ask.Facts[k];
        var node = arr.FirstOrDefault(n => n?["card"] != null && SafeInt(n["card"]) == k + 1);
        if (node == null) { error = error ?? "card " + (k + 1) + " has no question"; continue; }
        var why = Check(node, f, selection, out var card);
        if (why != null) { error = error ?? "card " + (k + 1) + ": " + why; continue; }
        card.Lights = Lit(card.Lights, guidOf, card.Question, card.Hint, card.Explain, string.Join(" ", card.Options));
        kept.Add(card);
      }

      var whys = reply?["why"] as JsonArray;
      var wanted = ask.Why;
      if (wanted > 0 && whys == null) error = error ?? "the reply has no why questions";
      var taken = 0;
      foreach (var w in whys ?? new JsonArray())
      {
        if (taken >= wanted) break;
        var why = CheckWhy(w, selection, guidOf, stepOf, ask.Nodes, out var card);
        if (why != null) { error = error ?? "why question " + (taken + 1) + ": " + why; continue; }
        kept.Add(card);
        taken++;
      }
      if (taken < wanted && error == null && whys != null && whys.Count < wanted) error = "there must be " + wanted + " why questions";
      return (kept.Select((c, i) => (c, i)).OrderBy(p => p.c.Step < 0 ? int.MaxValue : p.c.Step).ThenBy(p => p.c.Kind == DeckKind.Why ? 1 : 0).ThenBy(p => p.i).Select(p => p.c).ToList(), error);
    }

    private static string Check(JsonNode node, DeckFact f, HashSet<int> selection, out DeckCard card)
    {
      card = null;
      var question = (Str(node["question"]) ?? "").Trim();
      var hint = (Str(node["hint"]) ?? "").Trim();
      var explain = (Str(node["explain"]) ?? "").Trim();
      if (question.Length == 0 || question.Length > MaxQuestionChars) return "the question is empty or too long";
      if (hint.Length == 0 || hint.Length > MaxHintChars) return "the hint is empty or too long";
      if (explain.Length > MaxExplainChars) return "the explanation is too long";
      card = new DeckCard { Kind = f.Kind, Form = f.Form, Step = f.Step, Question = question, Hint = hint, Explain = explain.Length > 0 ? explain : null, Lights = f.Lights.ToList(), Cover = f.Cover };
      if (f.Form == DeckForm.Enter) card.Value = f.Truth;
      else if (f.Options != null) { card.Options = f.Options.ToList(); card.TrueSet = f.TrueSet.ToList(); }
      else
      {
        // A value chosen from options: the model writes three, and the plugin's TRUE must be one of them, copied exactly.
        var opts = (node["options"] as JsonArray)?.Select(o => (Str(o) ?? "").Trim()).ToList();
        if (opts == null || opts.Count != 3) { card = null; return "there must be exactly three options"; }
        if (opts.Any(o => o.Length == 0 || o.Length > MaxOptionChars)) { card = null; return "an option is empty or too long"; }
        if (opts.Select(o => o.ToLowerInvariant()).Distinct().Count() != 3) { card = null; return "the three options must differ"; }
        var t = opts.FindIndex(o => string.Equals(o, f.Truth, StringComparison.Ordinal));
        if (t < 0) { card = null; return "the true value \"" + f.Truth + "\" must be one of the three options, copied exactly"; }
        card.Options = opts;
        card.TrueSet = new List<int> { t };
      }
      var unknown = Outside(selection, card.Question, card.Hint, card.Explain, string.Join(" ", card.Options));
      if (unknown != null) { card = null; return "it names component " + unknown + ", which is not in the selection"; }
      return null;
    }

    private static string CheckWhy(JsonNode node, HashSet<int> selection, IReadOnlyDictionary<int, Guid> guidOf, IReadOnlyDictionary<Guid, int> stepOf, IReadOnlyList<DeckNode> nodes, out DeckCard card)
    {
      card = null;
      if (node == null) return "it is empty";
      var question = (Str(node["question"]) ?? "").Trim();
      var hint = (Str(node["hint"]) ?? "").Trim();
      var explain = (Str(node["explain"]) ?? "").Trim();
      var topic = (Str(node["topic"]) ?? "").Trim().ToLowerInvariant();
      var opts = (node["options"] as JsonArray)?.Select(o => (Str(o) ?? "").Trim()).ToList();
      var answer = node["answer"] == null ? -1 : SafeInt(node["answer"]);
      if (question.Length == 0 || question.Length > MaxQuestionChars) return "the question is empty or too long";
      if (hint.Length == 0 || hint.Length > MaxHintChars) return "the hint is empty or too long";
      if (explain.Length == 0 || explain.Length > MaxExplainChars) return "the explanation is empty or too long";
      if (opts == null || opts.Count != 4) return "there must be exactly four options";
      if (opts.Any(o => o.Length == 0 || o.Length > MaxWhyOptionChars)) return "an option is empty or too long";
      if (opts.Select(o => o.ToLowerInvariant()).Distinct().Count() != 4) return "the four options must differ";
      if (answer < 0 || answer > 3) return "the answer must be the index of one of the four options";
      if (!DeckReading.Topics.Contains(topic)) return "the topic must be one of " + string.Join(", ", DeckReading.Topics);
      var named = DeckAnchors.Named(question).ToList();
      if (named.Count == 0) return "the question must name the components it is about, as Name (id)";
      var unknown = Outside(selection, question, hint, explain, string.Join(" ", opts));
      if (unknown != null) return "it names component " + unknown + ", which is not in the selection";
      var all = new[] { question, hint, explain }.Concat(opts).SelectMany(DeckAnchors.Named).Distinct().ToList();
      var lights = all.Where(guidOf.ContainsKey).Select(id => guidOf[id]).ToList();
      var step = named.Where(guidOf.ContainsKey).Select(id => stepOf.TryGetValue(guidOf[id], out var s) ? s : int.MaxValue).DefaultIfEmpty(int.MaxValue).Min();
      card = new DeckCard
      {
        Kind = DeckKind.Why, Form = DeckForm.ChooseOne, Step = step == int.MaxValue ? -1 : step, Question = question, Hint = hint, Explain = explain, Topic = topic,
        Options = opts, TrueSet = new List<int> { answer }, Dwight = true, Lights = lights.Count > 0 ? lights : nodes.Select(n => n.Guid).ToList(),
      };
      return null;
    }

    /// <summary>A card lights what it asks about and every component its words name, so a name clicked on an open card is already lit.</summary>
    private static List<Guid> Lit(IEnumerable<Guid> lights, IReadOnlyDictionary<int, Guid> guidOf, params string[] texts)
    {
      var list = lights.ToList();
      foreach (var id in texts.SelectMany(DeckAnchors.Named))
        if (guidOf.TryGetValue(id, out var g) && !list.Contains(g)) list.Add(g);
      return list;
    }

    /// <summary>The first id a text names in the parenthesised form that is not a component of the selection, as "(id)"; null when every one is.</summary>
    private static string Outside(HashSet<int> selection, params string[] texts)
    {
      foreach (var t in texts) foreach (var id in DeckAnchors.Named(t)) if (!selection.Contains(id)) return "(" + id.ToString(CultureInfo.InvariantCulture) + ")";
      return null;
    }

    /// <summary>
    /// What the cards a call kept come to (Brief EO unit 1): a deck when there are three or more; under three, a deck that is not dealt and says how many there were;
    /// and none at all when the call itself failed and kept nothing, when the Walk runs without questions and says so, as it always has.
    /// </summary>
    public static DeckState Deal(IReadOnlyList<DeckCard> kept, bool callFailed, IEnumerable<DeckNode> nodes)
    {
      var cards = kept ?? Array.Empty<DeckCard>();
      if (cards.Count >= DeckPlanner.MinCards) return new DeckState(cards, null, nodes);
      if (cards.Count == 0 && callFailed) return null;
      return DeckState.TooFewOf(cards.Count, nodes);
    }

    private static string Str(JsonNode n) { try { return n?.GetValue<string>(); } catch { return null; } }
    private static int SafeInt(JsonNode n) { try { return n.GetValue<int>(); } catch { return int.MinValue; } }
  }

  /// <summary>
  /// THE JUDGE (Brief EO unit 2), the pure half. A why-card answered in the person's own words is read by one small call on the provider's fast model: it is given the
  /// question, the true option, the explanation and the answer, and returns right, partly or wrong with one sentence. The sentence may name only the deck's components.
  /// </summary>
  internal static partial class JudgeCall
  {
    public const int MaxSentenceChars = 240;

    public static string Payload(DeckCard card, string typed) => new JsonObject
    {
      ["question"] = card?.Question ?? "",
      ["true"] = card?.TrueText ?? "",
      ["explain"] = card?.Explain ?? "",
      ["answer"] = typed ?? "",
    }.ToJsonString();

    /// <summary>The verdict and its sentence, or the reason the reply is not one. A sentence naming a component outside the deck is no verdict.</summary>
    public static (JudgeVerdict Verdict, string Sentence, string Error) Parse(string json, ICollection<int> ids)
    {
      try
      {
        var o = JsonNode.Parse(json ?? "");
        var verdict = DeckGrader.Verdict(Str(o?["verdict"]));
        var sentence = (Str(o?["sentence"]) ?? "").Trim();
        if (verdict == JudgeVerdict.None) return (JudgeVerdict.None, null, "the verdict must be right, partly or wrong");
        if (sentence.Length == 0 || sentence.Length > MaxSentenceChars) return (JudgeVerdict.None, null, "the sentence is empty or too long");
        foreach (var id in DeckAnchors.Named(sentence))
          if (ids == null || !ids.Contains(id)) return (JudgeVerdict.None, null, "it names component (" + id.ToString(CultureInfo.InvariantCulture) + "), which is not in the deck");
        return (verdict, sentence, null);
      }
      catch (Exception ex) { return (JudgeVerdict.None, null, "the reply was not valid JSON (" + ex.Message + ")"); }
    }

    private static string Str(JsonNode n) { try { return n?.GetValue<string>(); } catch { return null; } }
  }
}
