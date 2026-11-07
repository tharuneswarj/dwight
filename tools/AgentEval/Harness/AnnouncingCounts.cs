using System.Collections.Generic;
using System.Linq;

namespace Dwight.AgentEval
{
  /// <summary>How often each provider and model ended a turn announcing with no card, for the ruling on the catch. It counts; it decides nothing.</summary>
  internal sealed class AnnouncingRow
  {
    public string Provider;
    public string Model;
    /// <summary>Turns that were not approved builds and not follow-ups: the ones that could have asked of their own accord.</summary>
    public int TurnsThatCouldAsk;
    public int TurnsThatAsked;
    public int NoCardLineCatch;
    public int NoCardForcingCatch;
    public int NoCardPlainWords;
    /// <summary>Follow-ups that ran, and how many of them ended on a card.</summary>
    public int FollowUps;
    public int FollowUpsThatRaisedACard;
  }

  internal static class AnnouncingCounts
  {
    public static List<AnnouncingRow> ByProvider(IEnumerable<RowResult> results) =>
      (results ?? Enumerable.Empty<RowResult>())
        .GroupBy(r => (r.Provider, r.Model))
        .OrderBy(g => g.Key.Provider).ThenBy(g => g.Key.Model)
        .Select(g =>
        {
          var turns = g.SelectMany(r => r.Turns).ToList();
          var free = turns.Where(t => t.Capability != "Build" && !t.FollowUp).ToList();
          var followUps = turns.Where(t => t.FollowUp).ToList();
          return new AnnouncingRow
          {
            Provider = g.Key.Provider,
            Model = g.Key.Model,
            TurnsThatCouldAsk = free.Count,
            TurnsThatAsked = free.Count(t => t.Tools.Any(x => x.Name == Dwight.Agent.AgentHostTools.RequestMutation)),
            NoCardLineCatch = free.Count(t => t.AnnouncedLineCatch),
            NoCardForcingCatch = free.Count(t => t.AnnouncedForcingCatch),
            NoCardPlainWords = free.Count(t => t.SaidWillNoCard),
            FollowUps = followUps.Count,
            FollowUpsThatRaisedACard = followUps.Count(t => t.Cards.Count > 0),
          };
        })
        .ToList();
  }
}
