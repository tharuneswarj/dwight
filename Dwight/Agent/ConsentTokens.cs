using System;
using System.Collections.Generic;
using System.Linq;

namespace Dwight.Agent
{
  /// <summary>
  /// One live consent request: the conversation that raised it, the kind the user was SHOWN, and the seed.
  /// </summary>
  internal sealed class PendingConsent
  {
    public string ThreadId;
    public string Kind;

    /// <summary>
    /// D1's hand-off seed - what the agent was actually looking at, so approving the card opens the verb
    /// on THAT rather than on whatever happens to be selected when the user clicks.
    ///
    /// It sits BESIDE Kind and never inside it, and that placement is the whole guarantee: ConsentCard
    /// .Route still takes only the kind the user was SHOWN, so routing is decided before this field is
    /// ever read. #235 settled that a persuasive sentence must not move where an approval lands; a
    /// persuasive payload must not either.
    /// </summary>
    public HandoffSeed Seed;

    /// <summary>
    /// U18: the token of the OTHER button on the same card, or null.
    ///
    /// A spot card offers one act two ways - in a group, or loose - and each button carries its own token.
    /// Two live tokens for ONE placement let a user who clicked both get it twice: loose first, then grouped,
    /// because the collision check reads GROUPS and a loose placement leaves none (#877). So redeeming either
    /// token voids the other.
    ///
    /// NULL ON EVERY OTHER CARD, and the voiding is guarded by that null - so no single-button card can
    /// change behaviour through this field.
    /// </summary>
    public string Sibling;
  }

  /// <summary>
  /// Live consent requests, by token. IN MEMORY ONLY, AND THAT IS THE MECHANISM (#220).
  ///
  /// The card itself is persisted, because it is chrome and the conversation should read back whole - so
  /// after a restart the card RENDERS AGAIN, button and all. This store does not come back, so the click
  /// posts a token that no longer exists and is refused with a re-ask path. The card comes back; the
  /// authority does not. That makes the privilege split visible to the user rather than merely asserted in
  /// code, and it is the same instrument WP-1 used for <c>Live</c> (#161).
  ///
  /// GH-free (ADR-091), extracted from the panel at U18 so the one rule it gained - redeeming one button of
  /// a two-button card voids the other - is tested rather than only written.
  /// </summary>
  internal sealed class ConsentTokens
  {
    private readonly Dictionary<string, PendingConsent> _pending = new Dictionary<string, PendingConsent>();

    /// <summary>
    /// Tokens already spent, WITH HOW. Kept (bounded) so a second click reports "you already approved that"
    /// rather than being indistinguishable from a token that never existed - three causes wearing one
    /// message is why the field could not tell us which one fired (#255). A token voided by its sibling is
    /// kept as its own cause for the same reason.
    /// </summary>
    private readonly List<KeyValuePair<string, string>> _spent = new List<KeyValuePair<string, string>>();

    /// <summary>
    /// The lock the store always needed. It is WRITTEN from the agent's worker thread (the host tool runs
    /// inside the loop) and READ on the UI thread (the click), and it was once a bare Dictionary: no mutual
    /// exclusion and no memory barrier between the two.
    /// </summary>
    private readonly object _lock = new object();

    public const int MaxRemembered = 64;

    /// <summary>The reasons <see cref="Take"/> can give. Each has its own sentence in <see cref="Say"/>.</summary>
    public const string AlreadyUsed = "already used";
    public const string OtherOptionUsed = "other option used";
    public const string NotLive = "not live";
    public const string NoToken = "no token";

    /// <summary>U96: a later card in the same chat retired this one. The reason a click on it is refused.</summary>
    public const string Superseded = "superseded";

    /// <summary>U96: retired token -> the token of the card that retired it. Bounded with the spent list.</summary>
    private readonly Dictionary<string, string> _supersededBy = new Dictionary<string, string>();

    /// <summary>Mint a single-use token for a card about to be shown.</summary>
    public string Mint(string threadId, string kind, HandoffSeed seed)
    {
      var token = NewToken();
      lock (_lock)
      {
        Retire(threadId, token);
        _pending[token] = new PendingConsent { ThreadId = threadId, Kind = kind, Seed = seed };
      }
      return token;
    }

    /// <summary>
    /// Mint the two tokens of a two-button card as SIBLINGS: redeeming either one voids the other. Each
    /// carries its own seed, so the option is fixed when the token is minted and the click is a LOOKUP,
    /// never a parse (#208).
    /// </summary>
    public void MintSiblings(string threadId, string kind, HandoffSeed first, HandoffSeed second,
                             out string firstToken, out string secondToken)
    {
      firstToken = NewToken();
      secondToken = NewToken();
      lock (_lock)
      {
        Retire(threadId, firstToken);
        _pending[firstToken] = new PendingConsent { ThreadId = threadId, Kind = kind, Seed = first, Sibling = secondToken };
        _pending[secondToken] = new PendingConsent { ThreadId = threadId, Kind = kind, Seed = second, Sibling = firstToken };
      }
    }

    /// <summary>
    /// U96: A NEW CARD RETIRES THE EARLIER PENDING ONES IN ITS OWN CHAT. A conversation that has moved on to a later card
    /// has left the earlier ones behind, and clicking one would run a correct scope the conversation no longer means (the gate
    /// thread's was an unwired, superseded script). Retirement happens at MINT, never at turn end, so a card that waits across
    /// turns with no newer card still works; a card in another chat is untouched; a spot card's pair retires together because
    /// both its tokens are in the thread's pending set. Caller holds the lock.
    /// </summary>
    private void Retire(string threadId, string byToken)
    {
      var old = _pending.Where(kv => string.Equals(kv.Value.ThreadId, threadId, StringComparison.Ordinal)).Select(kv => kv.Key).ToList();
      foreach (var token in old)
      {
        _pending.Remove(token);
        Spend(token, Superseded);
        _supersededBy[token] = byToken;
      }
    }

    /// <summary>Brief BG: the tokens pending in a chat right now - the cards a new card in that chat is about to retire.</summary>
    public List<string> PendingFor(string threadId)
    {
      lock (_lock)
        return _pending.Where(kv => string.Equals(kv.Value.ThreadId, threadId, StringComparison.Ordinal)).Select(kv => kv.Key).ToList();
    }

    /// <summary>Brief CA: the seed of the card of this kind still waiting in a chat, or null. A chat has one waiting card at most, because a new one
    /// retires the old (U96).</summary>
    public HandoffSeed WaitingSeed(string threadId, string kind)
    {
      lock (_lock)
        return _pending.Where(kv => string.Equals(kv.Value.ThreadId, threadId, StringComparison.Ordinal) && string.Equals(kv.Value.Kind, kind, StringComparison.Ordinal))
          .Select(kv => kv.Value.Seed).FirstOrDefault();
    }

    /// <summary>U96: the token of the card that retired this one, or null.</summary>
    public string SupersededBy(string token)
    {
      lock (_lock) return token != null && _supersededBy.TryGetValue(token, out var by) ? by : null;
    }

    /// <summary>U96: how many cards a later card has retired this session (bounded with the spent list).</summary>
    public int RetiredCount
    {
      get { lock (_lock) return _spent.Count(s => s.Value == Superseded); }
    }

    /// <summary>
    /// Redeem a token, or say precisely WHY it cannot be redeemed. One message for every cause is what
    /// made the field report unactionable (#255).
    /// </summary>
    public bool Take(string token, string threadId, out string kind, out HandoffSeed seed, out string reason)
    {
      kind = null; seed = null; reason = null;

      if (string.IsNullOrEmpty(token)) { reason = NoToken; return false; }

      lock (_lock)
      {
        if (!_pending.TryGetValue(token, out var pending))
        {
          var spent = _spent.FirstOrDefault(s => s.Key == token);
          reason = spent.Key != null ? spent.Value : NotLive;
          return false;
        }

        // A click from another conversation consumes nothing - the card is still live where it belongs.
        if (!string.Equals(pending.ThreadId, threadId, StringComparison.Ordinal))
        {
          reason = "wrong thread (card " + Short(pending.ThreadId) + " vs panel " + Short(threadId) + ")";
          return false;
        }

        _pending.Remove(token);
        Spend(token, AlreadyUsed);

        // THE SIBLING IS VOIDED IN THE SAME LOCK, so there is no moment at which both are live and one is
        // spent - a second click racing the first finds it gone either way.
        if (pending.Sibling != null && _pending.Remove(pending.Sibling))
          Spend(pending.Sibling, OtherOptionUsed);

        kind = pending.Kind;
        seed = pending.Seed;
        return true;
      }
    }

    /// <summary>
    /// Split what a card's button posted into the TOKEN and the TICKED ROW INDICES (Phase-9 U6 + U21). A batch card posts
    /// <c>TOKEN:0,2,5</c>; every other card posts the bare token. A token is 32 hex characters and never contains a colon,
    /// so the first colon is the boundary and the split is unambiguous.
    ///
    /// <paramref name="ticked"/> is NULL when no ticks were sent at all (a bare token), an empty list when the colon was
    /// there and nothing followed, and otherwise the indices in the order sent. A part that is not a whole number becomes
    /// <c>-1</c> - an index no card has - so a malformed click REFUSES at the subset rather than being quietly narrowed to
    /// the parts that happened to parse (resolve exactly or refuse, #208). Which rows the user ticked is never part of the
    /// token itself, so one token cannot be redeemed for a set the user did not choose.
    /// </summary>
    public static string Split(string raw, out List<int> ticked)
    {
      ticked = null;
      if (raw == null) return null;
      var colon = raw.IndexOf(':');
      if (colon < 0) return raw;

      ticked = new List<int>();
      var tail = raw.Substring(colon + 1);
      if (tail.Length > 0)
        foreach (var part in tail.Split(','))
          ticked.Add(int.TryParse(part.Trim(), System.Globalization.NumberStyles.None,
                                  System.Globalization.CultureInfo.InvariantCulture, out var i) ? i : -1);
      return raw.Substring(0, colon);
    }

    /// <summary>
    /// What the user is told when a click cannot be redeemed - one sentence per cause (#255).
    /// </summary>
    public static string Say(string reason)
    {
      switch (reason)
      {
        case AlreadyUsed:
          return "You already approved that one - it is running, or it has already run.";
        case OtherOptionUsed:
          // Its sibling was approved: the user already said yes to this card, with the other button. It
          // cannot also run this way - that would be the same placement twice.
          return "You already approved that one, with the other button - it is running, or it has already " +
                 "run. Only one of the two options can be used.";
        case Superseded:
          return "That card was superseded by a later one in this chat; the later card is the one to answer.";
        case NotLive:
          return "That card is from an earlier session of Dwight. The card is saved with the conversation, " +
                 "but the permission behind it deliberately is not - so it cannot be acted on now. Ask again " +
                 "and I will re-offer it.";
        default:
          return "That card belongs to a different conversation, so Dwight will not act on it here. Ask " +
                 "again in this one and I will re-offer it.";
      }
    }

    /// <summary>The live tokens, for the command-line log and the DEBUG diagnostics.</summary>
    public string Summary()
    {
      lock (_lock)
      {
        var retired = _spent.Count(s => s.Value == Superseded);
        var tail = retired > 0 ? " (retired: " + retired + ")" : "";
        if (_pending.Count == 0) return "none" + tail;
        return string.Join(", ", _pending.Select(kv => Short(kv.Key) + "/" + kv.Value.Kind + "@" + Short(kv.Value.ThreadId))) + tail;
      }
    }

    public int SpentCount
    {
      get { lock (_lock) return _spent.Count; }
    }

    private void Spend(string token, string how)
    {
      _spent.Add(new KeyValuePair<string, string>(token, how));
      while (_spent.Count > MaxRemembered)
      {
        _supersededBy.Remove(_spent[0].Key);
        _spent.RemoveAt(0);
      }
    }

    private static string NewToken() => Guid.NewGuid().ToString("N");

    internal static string Short(string s) =>
      string.IsNullOrEmpty(s) ? "(none)" : (s.Length <= 8 ? s : s.Substring(0, 8));
  }
}
