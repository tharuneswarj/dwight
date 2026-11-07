using System;
using System.Collections.Generic;
using System.Linq;

namespace Dwight.Agent
{
  /// <summary>
  /// DID THIS TURN ASK PERMISSION TO ASK? (F5, from the WP-4 gate.)
  ///
  /// WHAT HAPPENED. The model proposed a pipeline in prose, then asked "want me to go ahead and scaffold
  /// it?", waited for "cool, go ahead", and only then raised the consent card. That is a billed
  /// round-trip and a second user interaction to reach a card whose entire job is to BE the ask.
  ///
  /// WHY IT WAS NOT A REGRESSION. The instruction existed the whole time - "DO NOT ASK PERMISSION TO ASK
  /// ... THE CARD IS THE ASK" - but it lived in <c>request_mutation</c>'s TOOL DESCRIPTOR, and a
  /// descriptor governs how you use a tool once you have decided to call it. This behaviour is about HOW
  /// A TURN ENDS, which is decided before any tool is chosen. The line was never in a position to work.
  /// It now lives in the master prompt, where turn shape belongs.
  ///
  /// AND WHY THIS IS A DETECTOR RATHER THAN A GUARD. A prompt line's effect on a model cannot be
  /// unit-tested, and claiming a test proves compliance would be its own false claim - exactly the class
  /// #316 exists to stop. So the honest instrument measures RECURRENCE: where the behaviour is a model's,
  /// the test is a detector that counts, not an assertion that proves. If it keeps happening, that earns
  /// a structural fix and we will know which one. Measurement before mechanism.
  ///
  /// IT NEVER BLOCKS ANYTHING. It observes a turn that has already finished and records what it saw.
  /// A detector that intervened would be a guard built on a heuristic, and a heuristic that can be wrong
  /// must never be the thing that decides.
  /// </summary>
  internal static class TurnShape
  {
    /// <summary>
    /// The phrasings that ask permission to ask. Deliberately a short, literal list rather than anything
    /// clever: this counts occurrences for a human to read, so a miss costs a data point and a false
    /// positive costs trust in the number.
    /// </summary>
    private static readonly string[] AsksToAsk =
    {
      "shall i", "should i", "want me to", "would you like me to", "do you want me to",
      "let me know if you", "say the word", "just say", "if you want me to",
    };

    /// <summary>
    /// Does this look like a turn that PROPOSED something and then stopped to ask whether to propose it?
    ///
    /// Both halves are required. A turn that called a tool did something; a turn whose text asks a
    /// genuine question ("which of these did you mean?") is not this. What this catches is the specific
    /// shape: no tool call, and a closing question offering to act.
    /// </summary>
    public static bool AskedPermissionToAsk(string finalText, int toolCallsThisTurn)
    {
      if (toolCallsThisTurn > 0) return false;
      if (string.IsNullOrWhiteSpace(finalText)) return false;

      var text = finalText.ToLowerInvariant();

      // The question mark matters: "let me know if you want me to scaffold it" and "shall I scaffold it?"
      // are the same move, but a sentence with neither a question nor an offer is just an explanation.
      return text.Contains("?") && AsksToAsk.Any(p => text.Contains(p));
    }

    // ---- the sibling: CLAIMED TO HAVE TRIED IT (#582) ------------------------------------------------

    /// <summary>
    /// First-person claims of having tried something. Deliberately about TRYING and TESTING, never about
    /// verifying: checking a thing by READING it is legitimate verification ("I verified the wiring with
    /// get_connections"), and counting that would punish the model for doing its job.
    /// </summary>
    private static readonly string[] ClaimsToHaveTried =
    {
      "by trying", "tried it", "tried that", "i tried", "i've tried", "i have tried",
      "tested it", "tested that", "tested this", "i tested", "i've tested", "i have tested",
      "ran it", "i ran", "tried it out", "tested and confirmed",
    };

    /// <summary>Words that turn a claim into its opposite - "I haven't tested it" is an honest statement,
    /// not a claim, and counting it would punish the model for telling the truth.</summary>
    private static readonly string[] Negations =
    {
      "not ", "n't", "never", "without", "cannot", "unable",
    };

    /// <summary>
    /// Does this turn claim to have TRIED something on the canvas while calling nothing that mutates or
    /// solves? (#582, from Dwight's own self-audit: asked to try things on the canvas, the agent read once and
    /// reported "tested/confirmed by trying just now" having called no mutation.)
    ///
    /// Judged per SENTENCE: a sentence containing a claim counts only if it holds no negation. A turn that
    /// called solve_and_check or build_region did try something, whatever it then said about it.
    /// </summary>
    public static bool ClaimedToHaveTried(string finalText, int actingCallsThisTurn)
    {
      if (actingCallsThisTurn > 0) return false;
      if (string.IsNullOrWhiteSpace(finalText)) return false;

      foreach (var sentence in finalText.ToLowerInvariant().Split('.', '!', '?', '\n', ';'))
      {
        if (!ClaimsToHaveTried.Any(p => sentence.Contains(p))) continue;
        if (Negations.Any(n => sentence.Contains(n))) continue;
        return true;
      }
      return false;
    }

    // ---- the third number: ANNOUNCED A CHANGE while requesting and acting on nothing (#659, #669) ----------

    /// <summary>
    /// First-person announcements of a change this turn - a build, a rebuild, a reorganise, a card. A short literal list,
    /// as the siblings are: a miss costs a data point, a false positive costs trust in the number.
    /// </summary>
    private static readonly string[] AnnouncesChange =
    {
      "rebuilding ", "i'll rebuild", "i will rebuild", "let me rebuild",
      "i'll build", "i will build", "let me build", "building it now",
      "raising the build", "raising a card",
      "i'll reorganise", "i'll lay down", "laying down the",
    };

    /// <summary>#669: a CONDITIONAL OFFER describes what follows the user's move, so it promised nothing for this turn.
    /// "If you approve, I'll rebuild A1" is not an announcement.</summary>
    private static readonly string[] ConditionalOffers =
      { "if you", "once you", "when you", "i can help you", "i can:",
        // U60: the offer phrases AsksToAsk already names. "Say the word and I'll raise a build card" is an offer that waits on the
        // user, the same as "if you approve" - it announced nothing for this turn.
        "say the word", "just say", "let me know if you", "want me to", "tell me and" };

    /// <summary>What the user is told to do. "say" only before what a person says back, because ", say 10," is an example. Brief CH's list, from lane A's sentences.</summary>
    private const string UsersMove = @"(?:please|tell me|let me know|say(?= (?:which|what|how|when|so|yes|go|if|whether|the word)\b)|reply|respond|answer|choose|pick|confirm|give me|send me|ask me|describe)\b";

    /// <summary>Where such a word is an instruction: opening the sentence, or after a comma, colon or bracket, or a plain joining word. "I'll confirm the wiring" is not one.</summary>
    private const string OpensAMove = @"(?:^\s*|[,:(]\s*|\b(?:just|simply|and|or|then)\s+)";

    private static readonly System.Text.RegularExpressions.Regex TellsTheUser =
      new System.Text.RegularExpressions.Regex(OpensAMove + UsersMove, System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>The user's move, then the promise, in one sentence: "Tell me what you want to try and I'll raise the card", "Reply with 1, 2, or 3, and I'll map out ...".</summary>
    private static readonly System.Text.RegularExpressions.Regex MoveThenPromise = new System.Text.RegularExpressions.Regex(
      OpensAMove + UsersMove + @".*?(?:\b(?:and|then|so)\s+|,\s*)(?:then\s+)?(?:i'll|i will)\b", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>A sentence that carries on from the one before it: "Then I'll raise a revised scaffold and build it".</summary>
    private static readonly System.Text.RegularExpressions.Regex CarriesOn = new System.Text.RegularExpressions.Regex(
      @"^\s*(?:and\s+)?(?:then|after that|after which|from there)\s*,?\s*(?:i'll|i will)\b", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Brief CH: THE ONE RULE FOR "THIS PROMISE WAITS ON THE USER", read by the line's catches and the forcing catch, so the two cannot drift again. The line said "Dwight did not receive a
    /// request" under "Tell me what you want to try and I'll raise the card", a sentence that asks something and promises what follows the answer. Lane A measured five of those in 51 turns.
    ///
    /// <para>DECIDED PER SENTENCE, OR PER ADJACENT PAIR, AND NEVER ON THE WHOLE MESSAGE. A sentence waits when the user's move and the promise sit in it, or when it is "Then I'll ..." straight after a
    /// sentence that tells the user to do something or already waits. So "Tell me the latitude. Then I'll raise a revised scaffold" waits, and "Next I'll build the components. ... Tell me if you want to
    /// change anything first" does not: that announces first and invites edits second, and the invitation clears nothing before it.</para>
    ///
    /// <para>A list item is read as a sentence but is never the sentence before: it says nothing about what the writer's next sentence waits on.</para>
    /// </summary>
    private static List<(string Text, bool Waits)> ReadSentences(string lowered)
    {
      var read = new List<(string, bool)>();
      var theOneBeforeAsks = false;
      foreach (var line in (lowered ?? "").Split('\n'))
      {
        var item = ListItem.IsMatch(line);
        foreach (var sentence in line.Split('.', '!', '?', ';'))
        {
          if (string.IsNullOrWhiteSpace(sentence)) continue;
          var plain = sentence.Replace("*", "").Replace("`", "");
          var waits = ConditionalOffers.Any(c => plain.Contains(c)) || MoveThenPromise.IsMatch(plain) || (theOneBeforeAsks && CarriesOn.IsMatch(plain));
          read.Add((sentence, waits));
          if (!item) theOneBeforeAsks = waits || TellsTheUser.IsMatch(plain);
        }
      }
      return read;
    }

    /// <summary>The text the sentence readers take: lower case, with a typographic apostrophe read as a straight one.</summary>
    private static string Lowered(string text) => (text ?? "").ToLowerInvariant().Replace(RightSingleQuote, '\'');

    /// <summary>A markdown list item: a bullet ("- ", "* ", U+2022) or a number then "." or ")" then a space.</summary>
    private static readonly System.Text.RegularExpressions.Regex ListItem =
      new System.Text.RegularExpressions.Regex(@"^\s*(?:[-*\u2022]|\d+[.)])\s", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>U+2019, the typographic apostrophe model prose often carries. Written as a code, not a literal.</summary>
    private const char RightSingleQuote = (char)0x2019;

    /// <summary>
    /// Does this turn ANNOUNCE a change - "I'll rebuild A1", "raising the build card" - while it made no request_mutation
    /// call and no call that mutates or solves? (#659: the announcement promised a card or a build the turn never
    /// produced.) MEASURE ONLY (#669): it counts, it never blocks or repairs.
    ///
    /// Judged per SENTENCE, as #582 is: a sentence with an announcement counts only if it holds no negation and no
    /// conditional offer. A typographic apostrophe (U+2019) is read as a straight one - the same words.
    /// </summary>
    public static bool AnnouncedChangeWithoutRequest(string finalText, int requestCallsThisTurn, int actingCallsThisTurn)
    {
      if (requestCallsThisTurn > 0 || actingCallsThisTurn > 0) return false;
      if (string.IsNullOrWhiteSpace(finalText)) return false;

      // U54: a LIST ITEM is an OFFER, not an announcement. A greeting that says "When you're ready, I can help you:" and then
      // bullets "- I'll lay down a scaffold ..." promised nothing for this turn, but the newline split put the bullet in a
      // sentence of its own with the conditional left on the line above. Whole lines are dropped here, before the sentence
      // split, so a bullet's own full stops cannot leave a fragment that has lost its marker. Build family only: a bulleted
      // "Raising the card for B1 now" is still an announced request (AnnouncedRequestWithoutAsking is untouched).
      finalText = string.Join("\n", finalText.Split('\n').Where(line => !ListItem.IsMatch(line)));

      foreach (var (sentence, waits) in ReadSentences(Lowered(finalText)))
      {
        if (!AnnouncesChange.Any(p => sentence.Contains(p))) continue;
        if (Negations.Any(n => sentence.Contains(n))) continue;
        if (waits) continue;
        return true;
      }
      return false;
    }

    // ---- U32 (#924, #932): the REQUEST family - a card, a submission, a result - read on EVERY turn -----------------

    /// <summary>
    /// Phrasings that announce a REQUEST for the user's approval this turn - raising a card, submitting, calling the tool -
    /// or report the result of one. A short literal list, as the siblings are, taken from the field instances of #924 and
    /// #932 ("Raising the rebuild card for B1 now", "Calling it now", "Now raising B2 as requested", "Submitting now, as
    /// instructed", "I'll raise it instead as changes", "Submitted as instructed, and refused again").
    ///
    /// Unlike the build family above, this family is read even when the turn ACTED: an approved build turn that ran
    /// build_region and then wrote "now raising B2" built one thing and asked for the other in prose only (#924's
    /// post-build close). Building is not asking, so only a request_mutation call answers it.
    /// </summary>
    internal static readonly string[] AnnouncesRequest =
    {
      // raising a card
      "raising the card", "raising a card", "raising the build card", "raising the rebuild card", "raising the edit card",
      "raising the request", "raising it now", "raising it as", "now raising",
      "i'll raise", "i will raise", "let me raise", "i'm raising",
      "the card is up", "here is the card", "here's the card",
      // submitting
      "submitting", "submitted as", "submitted it", "submitted the", "i've submitted", "i have submitted", "i submitted",
      "i'll submit", "i will submit", "let me submit",
      // calling the tool
      "calling it now", "calling the card", "calling request_mutation", "calling the rebuild", "calling the build",
      "calling now", "i'll call request_mutation", "i will call request_mutation",
      "requesting approval", "requesting your approval",
      // a quoted outcome of a request: a refusal code is the plugin's word, never the model's (#932)
      "precondition_refused", "cardshown",
    };

    /// <summary>
    /// What turns a clause of the request family into its opposite. The shared <see cref="Negations"/> plus the words that
    /// say NO request happened - "nothing was submitted" is the truth this line exists to tell, not a claim against it.
    /// Kept apart from <see cref="Negations"/> on purpose: "I tried it and nothing changed" IS a claim of trying, and
    /// widening the shared list would stop #582 counting it.
    /// </summary>
    /// <summary>"Raising B2", "raising A1 now": raising a region ADDRESS (a letter and a digit) is a card, where "raising
    /// the beam" is a design word. The address shape is <c>ScaffoldAddress</c>'s, read loosely: a miss costs a line,
    /// and the line is true whenever it is shown.</summary>
    private static readonly System.Text.RegularExpressions.Regex RaisingAnAddress =
      new System.Text.RegularExpressions.Regex(@"\braising [a-z][0-9]", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static readonly string[] RequestNegations = Negations.Concat(new[]
    {
      "nothing", "no card", "no request",
      // U32 precision (#975): a NEGATED announcement. "I stop here rather than submit an edit" is a decision NOT to ask,
      // and it fired the line. Clause-level, like the rest: a caveat after a real announcement ("raising the card now,
      // rather than building") still has its announcing clause read on its own words.
      "rather than", "instead of", "will not", "won't", "i stop", "i'm stopping", "i am stopping", "declin",
    }).ToArray();

    /// <summary>
    /// Phrases that say the writer is NOT proceeding, judged over the whole SENTENCE: the clause splitter cuts at a
    /// parenthesis, so "I stop here rather than submit an edit (precondition_refused)" leaves the quoted refusal code as
    /// a clause of its own with no negation in it. These are decisions to halt, not caveats about something else, so
    /// they govern the sentence - kept to the words that cannot also announce ("instead of" and "rather than" stay
    /// clause-level, because "raising the card rather than building" announces).
    /// </summary>
    private static readonly string[] SentenceHalts = { "i stop", "i'm stopping", "i am stopping", "declin" };

    /// <summary>
    /// Does this turn's text ANNOUNCE a request - a card, a submission, a call - or quote a request's result, while the turn
    /// made NO request_mutation call? (U32.) Building does not answer it; only asking does.
    ///
    /// Judged per CLAUSE for negation and per SENTENCE for a conditional offer. A clause, because a sentence often carries a
    /// caveat after its announcement ("Raising the build card for A2 now: it will not touch A1"), and a caveat must not hide
    /// the announcement before it. A sentence, because a conditional governs the whole of it ("If you approve, I'll submit
    /// it") and splitting at the comma would lose the "if".
    /// </summary>
    public static bool AnnouncedRequestWithoutAsking(string finalText, int requestCallsThisTurn)
    {
      if (requestCallsThisTurn > 0) return false;
      if (string.IsNullOrWhiteSpace(finalText)) return false;

      foreach (var (sentence, waits) in ReadSentences(Lowered(finalText)))
      {
        if (waits) continue;
        if (SentenceHalts.Any(h => sentence.Contains(h))) continue;
        foreach (var clause in sentence.Split(',', ':', '(', ')', '"', (char)0x2014, (char)0x2013).SelectMany(c => c.Split(new[] { " - " }, StringSplitOptions.None)))
        {
          if (!AnnouncesRequest.Any(p => clause.Contains(p)) && !RaisingAnAddress.IsMatch(clause)) continue;
          if (RequestNegations.Any(n => clause.Contains(n))) continue;
          return true;
        }
      }
      return false;
    }

    /// <summary>
    /// U32: THE ONE RULE the turn-end line and the counter share. A turn ANNOUNCED something and ASKED for nothing when it
    /// made no request_mutation call and either (a) its text announced a request or quoted a request's result - read on
    /// every turn, an acting one included - or (b) it announced a build while acting on nothing, #659's original shape,
    /// kept exactly so the counter only grows. <paramref name="buildFamily"/> is false on an approved build turn: there a
    /// build announcement with no build is answered by the no-build banner and the closing report, and a second line saying
    /// the same thing in other words would be noise.
    /// </summary>
    public static bool AnnouncedWithoutAsking(string finalText, int requestCallsThisTurn, int actingCallsThisTurn,
                                              bool buildFamily = true) =>
      !EndsAskingTheUser(finalText) &&
      (AnnouncedRequestWithoutAsking(finalText, requestCallsThisTurn)
       || (buildFamily && AnnouncedChangeWithoutRequest(finalText, requestCallsThisTurn, actingCallsThisTurn)));

    /// <summary>
    /// Brief CA: A MESSAGE THAT ASKS THE USER SOMETHING, AND ENDS THERE, DID WHAT IT SHOULD. "I'd propose rebuilding the Fins region ... Would you like me to scaffold this
    /// or raise a rebuild card? Let me know which" drew "Dwight did not receive a request" beneath a real question. Read from its last paragraph: it holds a question, and
    /// nothing after that question announces. "Is that right? Raising the card now." still announces, and keeps the line.
    /// </summary>
    internal static bool EndsAskingTheUser(string text)
    {
      if (string.IsNullOrWhiteSpace(text)) return false;
      var paragraph = text.Replace("\r\n", "\n").TrimEnd().Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
      var mark = paragraph.LastIndexOf('?');
      if (mark < 0) return false;
      var after = paragraph.Substring(mark + 1);
      return !AnnouncedRequestWithoutAsking(after, 0) && !AnnouncedChangeWithoutRequest(after, 0, 0);
    }

    // ---- U97: the STRUCTURAL catch - an announced intent with no card, on a turn that could have raised one -----------

    private const char NewlineChar = (char)10;
    private const char ApostropheChar = (char)39;

    /// <summary>
    /// U97: first-person intent, the openings of "I'll lay out and group the whole canvas" and the gate's "this is a component delete,
    /// which falls under the edit capability". Pinned (see the tests): a miss costs a line the model would have been shown, so the list
    /// is generous, and the precision comes from the structure (a mutation route, no tool call in the last step, no question) and from
    /// <see cref="IntentVerbs"/>, not from the list.
    /// </summary>
    internal static readonly string[] IntentOpenings =
    {
      "i'll ", "i will ", "let me ", "i'm going to ", "i am going to ", "next i ", "now i'll ", "falls under the ", "i can now ",
      "i'll go ahead",
      // U107: announcing that you will ASK (raise a card) and not asking is the exact case.
      "raising ", "raise the ", "i'll raise", "let me raise",
    };

    /// <summary>
    /// U97 precision: an intent phrase counts only beside a verb that CHANGES the canvas. "Let me explain", "I'll check the wiring" and
    /// "I'll summarise" announce a read or an answer, and a card was never owed for them. "falls under the" is the model reasoning about
    /// which capability a change needs, which is itself the announcement, so it counts on its own.
    /// </summary>
    internal static readonly string[] IntentVerbs =
    {
      "build", "rebuild", "lay out", "layout", "group", "ungroup", "delete", "remove", "rename", "move", "wire", "rewire", "connect",
      "disconnect", "place", "add ", "create", "set ", "change", "reorgani", "simplif", "edit", "replace", "apply", "scaffold", "tidy",
      "arrange", "colour", "color", "run ", "write", "insert", "swap", "clean up",
      "card", "map",   // U107: a card is the act of asking ("scaffold" is above)
    };

    /// <summary>
    /// U97: DID THE TURN ANNOUNCE A CHANGE AND RAISE NOTHING, read from the turn's STRUCTURE first. The turn could have raised a
    /// card (<paramref name="mutationAvailable"/>: a mutation capability was granted, or request_mutation was on offer), its LAST step
    /// made no tool call, it made no request_mutation call, and its last message asks no question. Then, lexically and on that last
    /// message only, a first-person intent from <see cref="IntentOpenings"/> beside a changing verb, or an <see cref="AsksToAsk"/>
    /// phrase (which, with no question mark, is an announcement and not an offer). Sentences that are conditional offers or negated are
    /// skipped, as the siblings do.
    /// </summary>
    public static bool AnnouncedIntentWithoutCard(string lastMessage, bool mutationAvailable, int lastStepToolCalls, int requestCalls)
    {
      if (!mutationAvailable || lastStepToolCalls > 0 || requestCalls > 0) return false;
      if (string.IsNullOrWhiteSpace(lastMessage)) return false;
      if (lastMessage.Contains("?")) return false;

      var text = string.Join(Environment.NewLine, lastMessage.Split(NewlineChar).Where(line => !ListItem.IsMatch(line)));
      foreach (var (sentence, waits) in ReadSentences(Lowered(text)))
      {
        if (Negations.Any(n => sentence.Contains(n))) continue;
        if (waits) continue;
        if (sentence.Contains("falls under the ")) return true;
        if (IntentOpenings.Any(o => sentence.Contains(o)) && IntentVerbs.Any(v => sentence.Contains(v))) return true;
      }
      return false;
    }

    // ---- Brief BT: the catch that may FORCE a card. Stricter than the line's, because under "Do not ask" a forced card RUNS -------

    /// <summary>A whole message that waits on the user, with or without a question mark: "Please confirm the radius and I'll build it."</summary>
    private static readonly System.Text.RegularExpressions.Regex ForcingWaitsOnUser = new System.Text.RegularExpressions.Regex(
      @"\b(?:please|confirm (?:the|which|that|this|your|whether)|say (?:which|what|how|when)|let me know|tell me|before i can|wait for|waiting for|after you|as \w+ as you|i'll need|i will need|i need|i'd need|i would need|need your|up to you|your call|your choice)\b",
      System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>A last sentence that invites a change and holds nothing up: "Tell me if you want to change anything in the plan first." Not "Tell me whether the fins rotate", which needs an answer.</summary>
    private static readonly System.Text.RegularExpressions.Regex ClosingInvitation = new System.Text.RegularExpressions.Regex(
      @"\b(?:tell me|let me know)\s+if\s+you(?:'d| would)?\s+(?:want|like|prefer|wish|need)\b", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>The message without its closing invitation, which announces first and invites edits second and so does not clear the promise before it. Only the last sentence: an invitation
    /// before the promise stays in, and costs only the line.</summary>
    private static string WithoutClosingInvitation(string text)
    {
      var trimmed = text.TrimEnd(' ', '\t', '\r', NewlineChar, '.', '!');
      var start = trimmed.LastIndexOfAny(new[] { '.', '!', ';', NewlineChar }) + 1;
      return ClosingInvitation.IsMatch(trimmed.Substring(start)) ? trimmed.Substring(0, start) : text;
    }

    /// <summary>A sentence that is conditional on something still to come. Sentence-level: a condition governs the whole of it, and
    /// splitting at the comma would lose the "if". ", and I'll" is the same move in a plainer coat: "Clear A1, and I'll rebuild it" puts
    /// the user's act first.</summary>
    private static readonly System.Text.RegularExpressions.Regex ForcingConditional = new System.Text.RegularExpressions.Regex(
      @"\b(?:if|once|when|whenever|until|unless|should you|i can help you|i can:|say the word|just say|want me to|tell me and|and i'll|and i will)\b",
      System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>A clause that denies, reports the past or supposes. Clause-level, as the request family is: "The card was never raised, so
    /// I'll create the map" still has its second clause read on its own words.</summary>
    private static readonly System.Text.RegularExpressions.Regex ForcingVeto = new System.Text.RegularExpressions.Regex(
      @"(?:\bnot\b|n't\b|\bnever\b|\bwithout\b|\bcannot\b|\bunable\b|\bnothing\b|\bno card\b|\bno request\b|\brather than\b|\binstead of\b|\bdeclin|\bi stop\b|\bi'm stopping\b|\bi am stopping\b" +
      @"|\bi said\b|\bi had\b|\bi've\b|\bi have\b|\bearlier\b|\bpreviously\b|\blast time\b|\bwould\b|\bcould\b|\bmight\b" +
      // The object is talk, not canvas: "I'll simplify the explanation", "I'll lay out the options".
      @"|\bexplanation\b|\banswer\b|\bsummary\b|\boptions?\b|\bchoices\b|\bexamples?\b|\breasons?\b|\bmy approach\b)",
      System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// First-person intent, the verb right after it, a word-bounded verb that changes the canvas, and no other word between them but a short
    /// fixed list of adverbs. That adjacency is the whole precision: "I'll explain how I would build this" has an intent and a verb and is
    /// not this. "run" and a bare "write" are left out on purpose ("I'll run through it", "I'll write a short note"); a miss costs the line.
    /// Brief CH: "raise" takes any object, as "Then I'll raise a revised scaffold" and "I'll raise the delete" do, but not a thing a person raises in talk; "map out" is the plain future of a map.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex ForcingIntent = new System.Text.RegularExpressions.Regex(
      // "let me" only where it opens the clause: "none of my tools let me set a value" is a statement about tools.
      @"(?:\b(?:i'll|i will|i'm going to|i am going to|i'm about to|i am about to|and will)|^\s*(?:(?:so|now|next|then|first)\s+)?let me)\s+" +
      @"(?:(?:now|first|then|next|directly|just|also|go ahead and|proceed to|instead|immediately|simply)\s+)*" +
      @"(?:place|add(?! up)|append|create|build(?! on)|rebuild|lay out|lay down|group|ungroup|delete|remove|rename|move(?! on)|wire|rewire|connect|disconnect|set(?! out)|change|reorganise|reorganize|simplify|edit|replace|apply|scaffold|tidy|arrange|insert|swap" +
      @"|write (?:the|a|that|this) (?:[\w-]+ )?script|raise(?! (?:(?:(?:a|an|the|that|this) )?(?:question|concern|point|issue|objection|flag)\b|(?:(?:it|this|that) )?with\b))|map out(?! (?:how|why|what|where|when)\b))\b" +
      // "Raising the scaffold card first", "raising A3", "raising the build now", "so I'm preparing the approval card now". Not "before
      // raising the card", which puts something else first.
      @"|(?<!\bbefore )\braising (?:(?:the|a|an) (?:[\w-]+ ){0,3}card\b|the (?:build|rebuild) (?:now|for)\b|[a-z][0-9]\b)" +
      @"|\b(?:i'm|i am) preparing (?:the|a|an) (?:[\w-]+ ){0,3}card\b" +
      // "and can build them now": a capability stated for this very moment, with its "now" required.
      @"|\b(?:i|and) can (?:now (?:place|add|create|build|rebuild|lay out|delete|remove|wire|set|change|edit|replace|apply)\b|(?:place|add|create|build|rebuild|lay out|delete|remove|wire|set|change|edit|replace|apply)\b[^,;:]*\bnow\b)",
      System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>A quoted span, straight or typographic: "I'm not going to just assert "I'll group the whole canvas"" is the real case.</summary>
    private static readonly System.Text.RegularExpressions.Regex ForcingQuoted = new System.Text.RegularExpressions.Regex(
      "\"[^\"\\n]*\"|\u201C[^\u201D\\n]*\u201D", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Brief BT: the sentence that may force a card, or null. The structure comes first, as <see cref="AnnouncedIntentWithoutCard"/>'s does:
    /// the last step made no tool call, the turn made no request, and the message asks nothing. List items, quoted lines and fenced code
    /// are dropped whole, because none of them is the writer's own promise for this turn. A false positive here changes the canvas under
    /// "Do not ask", so every doubt returns null and costs only the line.
    ///
    /// Brief CH: a sentence the one rule reads as waiting never forces (<see cref="ReadSentences"/>). This catch also keeps its own stricter gate over the message, because "I'll build A1. It has three
    /// sliders. Please confirm the radius." must not build under "Do not ask"; that costs only the line. The one thing the gate no longer reads is a closing invitation, which is what cost it a01 and a02.
    /// </summary>
    internal static string ForcingSentence(string lastMessage, int lastStepToolCalls, int requestCalls)
    {
      if (lastStepToolCalls > 0 || requestCalls > 0) return null;
      if (string.IsNullOrWhiteSpace(lastMessage) || lastMessage.Contains("?")) return null;

      var lines = new List<string>();
      var fenced = false;
      foreach (var line in lastMessage.Split(NewlineChar))
      {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("```", StringComparison.Ordinal)) { fenced = !fenced; continue; }
        if (fenced || trimmed.StartsWith(">", StringComparison.Ordinal) || ListItem.IsMatch(line)) continue;
        lines.Add(line);
      }

      var text = string.Join("\n", lines).ToLowerInvariant().Replace(RightSingleQuote, ApostropheChar).Replace("*", "").Replace("`", "");
      text = WithoutClosingInvitation(text);
      if (ForcingWaitsOnUser.IsMatch(text)) return null;
      text = ForcingQuoted.Replace(text, " ");   // a quotation is someone's words, never this turn's promise

      foreach (var (sentence, waits) in ReadSentences(text))
      {
        if (waits || ForcingConditional.IsMatch(sentence)) continue;
        foreach (var clause in sentence.Split(',', ':', '(', ')', '"', (char)0x2014, (char)0x2013).SelectMany(c => c.Split(new[] { " - " }, StringSplitOptions.None)))
        {
          if (ForcingIntent.IsMatch(clause) && !ForcingVeto.IsMatch(clause)) return sentence.Trim();
        }
      }
      return null;
    }

    /// <summary>Brief BT: did this turn announce a change precisely enough to force the card it did not raise?</summary>
    public static bool AnnouncedForForcing(string lastMessage, int lastStepToolCalls, int requestCalls) =>
      ForcingSentence(lastMessage, lastStepToolCalls, requestCalls) != null;

    private static readonly List<string> _announced = new List<string>();

    /// <summary>How many turns this session announced a change while requesting and acting on nothing.</summary>
    public static int AnnouncedChangeCount { get; private set; }

    public static IReadOnlyList<string> AnnouncedChangeExamples => _announced;

    private static readonly List<string> _claims = new List<string>();

    /// <summary>How many turns this session claimed to have tried something while acting on nothing.</summary>
    public static int ClaimedTriedCount { get; private set; }

    public static IReadOnlyList<string> ClaimedTriedExamples => _claims;

    // ---- the record, for a human to read ------------------------------------------------------------

    private static readonly List<string> _seen = new List<string>();
    private const int MaxRemembered = 20;

    /// <summary>How many turns this session ended by asking permission to ask.</summary>
    public static int Count { get; private set; }

    /// <summary>How many turns were EXAMINED. Without it a zero cannot be told from an absence, which is
    /// #212's whole point: a measurement that has only ever produced one value has not been shown to
    /// measure.</summary>
    public static int Examined { get; private set; }

    public static IReadOnlyList<string> Examples => _seen;

    /// <summary>Observe one finished turn. Called after the turn is complete; changes nothing. All three
    /// detectors share one denominator, because they examine the same turns. <paramref name="requestCallsThisTurn"/>
    /// is REQUIRED, never defaulted: a forgotten zero would count a turn that asked as one that did not.</summary>
    public static void Observe(string finalText, int toolCallsThisTurn, int actingCallsThisTurn, int requestCallsThisTurn)
    {
      Examined++;

      if (AskedPermissionToAsk(finalText, toolCallsThisTurn))
      {
        Count++;
        if (_seen.Count < MaxRemembered) _seen.Add(Trim(finalText));
      }

      if (ClaimedToHaveTried(finalText, actingCallsThisTurn))
      {
        ClaimedTriedCount++;
        if (_claims.Count < MaxRemembered) _claims.Add(Trim(finalText));
      }

      // U32: the counter reads the SAME rule the turn-end line is drawn from, so the number and the line cannot disagree
      // about which turns announced without asking. A superset of #659's rule by construction.
      if (AnnouncedWithoutAsking(finalText, requestCallsThisTurn, actingCallsThisTurn))
      {
        AnnouncedChangeCount++;
        if (_announced.Count < MaxRemembered) _announced.Add(Trim(finalText));
      }
    }

    // ---- #624: approved build turns that never dispatched build_region -----------------------------------

    /// <summary>
    /// The denominator: turns that held an approved BUILD. Without it "0 turns never built" cannot be told from
    /// "no approved build has run" (#212).
    /// </summary>
    public static int ApprovedBuildTurns { get; private set; }

    /// <summary>
    /// Approved build turns that dispatched NO build_region - #624's field case: the turn wrote "Both fixes
    /// landed and verified" and the close reported nothing attempted. A detector: it counts, it never blocks,
    /// and the closing report and the build fact are what tell the user and the model the truth.
    /// </summary>
    public static int ApprovedBuildTurnsWithoutBuild { get; private set; }

    /// <summary>Observe one finished approved build turn and how many build_region calls it dispatched.</summary>
    public static void ObserveApprovedBuild(int buildRegionCalls)
    {
      ApprovedBuildTurns++;
      if (buildRegionCalls <= 0) ApprovedBuildTurnsWithoutBuild++;
    }

    /// <summary>Reset, for a drill that wants a clean denominator.</summary>
    public static void Clear()
    {
      Count = 0;
      Examined = 0;
      _seen.Clear();
      ClaimedTriedCount = 0;
      _claims.Clear();
      AnnouncedChangeCount = 0;
      _announced.Clear();
      ApprovedBuildTurns = 0;
      ApprovedBuildTurnsWithoutBuild = 0;
    }

    private static string Trim(string text)
    {
      var t = (text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
      return t.Length <= 160 ? t : t.Substring(t.Length - 160);   // the END is where the ask lives
    }
  }
}
