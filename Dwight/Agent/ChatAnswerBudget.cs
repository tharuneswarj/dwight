namespace Dwight.Agent
{
  /// <summary>
  /// THE ANSWER BUDGET OF A CHAT TURN, COMPUTED FROM THE SCRIPT LIMIT (#1171). The parser accepts a script up to
  /// <see cref="EditScriptAsk.MaxSourceChars"/>, and a card or a build carries that script inside one reply. A budget typed as a second
  /// number could refuse in practice what the parser accepts, and nothing would notice until a reply was cut off: no call is dispatched
  /// from a cut reply, so the card never comes. Tokens are paid when used, so the larger ceiling costs nothing on a short reply.
  ///
  /// THE WORST CASE IS AN ESTIMATE, NOT A MEASUREMENT: 3 characters of code to a token, plus the 30% the catalogue states for the newer
  /// Claude tokenizer. The longest script on a saved card is 2,921 characters, so nothing on record has tested it.
  ///
  /// GH-free (ADR-091), so the relation can be pinned in a test.
  /// </summary>
  internal static class ChatAnswerBudget
  {
    /// <summary>Room for what a reply writes beside the script: its prose and the rest of the call. The 4k a chat reply had before (#216).</summary>
    public const int ProseRoom = 4_000;

    /// <summary>The budget that carries a script of this many characters at the worst case: chars / 3 x 1.3, rounded up, on top of the prose.</summary>
    public static int For(int maxSourceChars) => ProseRoom + (maxSourceChars * 13 + 29) / 30;

    /// <summary>The budget for one step of a chat turn, an approved build's included.</summary>
    public static readonly int Turn = For(EditScriptAsk.MaxSourceChars);
  }
}
