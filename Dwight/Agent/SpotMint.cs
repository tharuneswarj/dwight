using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;

namespace Dwight.Agent
{
  /// <summary>
  /// WHERE A SPOT ADDRESS COMES FROM, AND WHY IT CANNOT MOVE ONCE IT IS ISSUED (U18).
  ///
  /// A spot is the one region approved BEFORE it exists: the card names it, and the build creates it. That
  /// gap between the card and the click is the whole reason this type exists, and it has exactly one
  /// governing property:
  ///
  ///   AN ADDRESS ON A CARD IS A PROMISE. IT IS RESERVED WHEN THE CARD IS MINTED AND IT NEVER RENUMBERS.
  ///
  /// A card saying "in a new group called Spot 2" that lands as Spot 3 would be a card that was not true
  /// at the moment it was approved - #253 at the consent surface, and the one thing a card may never do.
  /// So a second card minted while the first is outstanding gets a DIFFERENT address, and if the canvas
  /// somehow gains the reserved address before the click, the build REFUSES BY NAME rather than shuffling
  /// to a free one (BuildScope.Resolve's collision case). Refusing is recoverable - the user asks again
  /// and gets a fresh card. A silently renumbered address is not: it has already been approved as
  /// something else.
  ///
  /// RESERVATIONS ARE IN MEMORY AND NEVER PERSISTED, exactly as the build ledger's permissions are, and
  /// they are never released. A card that is never clicked leaks its number, and that costs nothing: gaps
  /// are not recycled by design (#720's law - the conversation may still say "Spot 2" about a placement
  /// that was undone), so a leaked reservation is indistinguishable from a used one.
  ///
  /// A RESTART LOSES THE RESERVATIONS, SO THE MINT ALSO READS THE SAVED CONVERSATION (#878). The canvas
  /// carries a spot's address only as a GROUP NAME, so after a restart the canvas alone would let numbering
  /// continue past every GROUPED spot still standing and past nothing else: a LOOSE spot leaves no name
  /// behind, and neither does a grouped spot that was undone. Either number could then be minted again while
  /// the saved conversation still uses it for the earlier placement - two "Spot 1"s meaning different things.
  /// (This note once said a restart was harmless. It was not; the gap was corrected at U18 and closed here.)
  ///
  /// So the in-use set is the canvas's names AND every spot address the persisted conversation MENTIONS
  /// (<see cref="InUse"/>, <see cref="MentionedIn"/>). The thing that holds the old meaning is the thing that
  /// is read - a separate list of "spots used" would be a second record of the conversation that could drift
  /// from it. Three properties, each deliberate:
  ///   - DOCUMENT-WIDE, every thread on the file, because this mint is per document so that two threads never
  ///     share an address, and that rule has to survive a restart too. The document's record holds a file AND
  ///     its Save-As copies (they share the DocumentID, WP-1), so a copy's threads reserve as well - which only
  ///     skips numbers, the harmless direction.
  ///   - CONSERVATIVE: ANY mention reserves - a card, a report, the model's prose, the user's words, in any
  ///     case. Reserving too much only skips numbers, and gaps are never recycled anyway (#720); reserving too
  ///     little is the defect. A model that invents "Spot 40" pushes numbering past 40, which is harmless.
  ///   - AN UNREADABLE STORE READS AS EMPTY (WP-1's rule), so the scan then reserves nothing - and that is safe
  ///     without a guard, because the conversation that could be misread is then not shown to anyone either.
  ///
  /// GH-free and store-free (ADR-091): the caller supplies what the canvas holds and the saved texts, exactly
  /// as #720's in-use set does, so the minting law is testable without a document or a session store.
  /// </summary>
  internal sealed class SpotMint
  {
    /// <summary>
    /// One mint per document, held in memory.
    ///
    /// Keyed by the DOCUMENT because "which spots are spoken for" is a fact about the file, not about a
    /// conversation: two threads on one canvas must not mint the same address, and the same thread on two
    /// files must not share a counter. That is the same reasoning - and the same shape - as the build
    /// ledger's per-document facts.
    /// </summary>
    private static readonly Dictionary<Guid, SpotMint> _byDocument = new Dictionary<Guid, SpotMint>();

    public static SpotMint For(Guid documentId)
    {
      lock (_byDocument)
      {
        if (!_byDocument.TryGetValue(documentId, out var mint))
        {
          mint = new SpotMint();
          _byDocument[documentId] = mint;
        }
        return mint;
      }
    }

    private readonly HashSet<string> _reserved = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// The next spot address, reserved so no other card can take it.
    ///
    /// <paramref name="onCanvas"/> is what the canvas already carries - group names read by the shim. The
    /// new address continues past BOTH that and every address this mint has already promised, so two
    /// outstanding cards can never name one place.
    /// </summary>
    public string Reserve(IEnumerable<string> onCanvas)
    {
      lock (_reserved)
      {
        var inUse = (onCanvas ?? Enumerable.Empty<string>()).Concat(_reserved);
        var minted = ScaffoldAddress.NextSpot(inUse);
        _reserved.Add(minted);
        return minted;
      }
    }

    /// <summary>Has this mint promised this address to a card? For the assertion that a build is carrying
    /// out a promise rather than naming a place nobody offered.</summary>
    public bool IsReserved(string address)
    {
      if (string.IsNullOrEmpty(address)) return false;
      lock (_reserved) { return _reserved.Contains(address); }
    }

    /// <summary>Every address this mint has promised, in address order - for a report or a refusal that
    /// has to say what is already spoken for.</summary>
    public IReadOnlyList<string> Reserved()
    {
      lock (_reserved)
      {
        return _reserved
          .OrderBy(a => ScaffoldAddress.SortKey(a).Letters, StringComparer.Ordinal)
          .ThenBy(a => ScaffoldAddress.SortKey(a).Number)
          .ToList();
      }
    }

    /// <summary>
    /// Everything that answers to a spot address right now: what the canvas carries, plus every spot the saved
    /// conversation mentions. This is what the ask hands <see cref="Reserve"/>, so a restart - which empties the
    /// reservations - still cannot re-mint a number the conversation already means something by.
    /// </summary>
    public static IReadOnlyList<string> InUse(IEnumerable<string> onCanvas, IEnumerable<string> savedTexts) =>
      (onCanvas ?? Enumerable.Empty<string>()).Concat(MentionedIn(savedTexts)).ToList();

    /// <summary>
    /// Every spot address the texts MENTION, as its canonical address ("Spot 3").
    ///
    /// THE TEXTS ARE WHAT THE STORE HOLDS, WHICH IS HTML AS WELL AS SOURCE: a card is stored only as its
    /// rendered markup, so "Spot 1" may reach this wrapped in tags or with an encoded space. So tags are
    /// replaced by a space (never by nothing - that could weld two words into one) and entities are decoded
    /// BEFORE the scan. A test builds its fixtures from the real card and the real report, because a
    /// hand-typed "Spot 1" would pass on a scanner that misses what the writer actually writes.
    ///
    /// A mention is "spot", any case, then whitespace (a non-breaking space included), then digits, with no
    /// letter or digit directly before it - so "Spotlight 3" and "hotspot 3" are not addresses.
    ///
    /// ONE NUMBER IS SKIPPED: int.MaxValue. <see cref="ScaffoldAddress.NextSpot"/> mints the highest number
    /// plus one, and a user who types "Spot 2147483647" must not be able to overflow the next address into a
    /// negative one. Every smaller number reserves as written.
    /// </summary>
    public static IReadOnlyList<string> MentionedIn(IEnumerable<string> texts)
    {
      var found = new HashSet<string>(StringComparer.Ordinal);
      foreach (var text in texts ?? Enumerable.Empty<string>())
      {
        if (string.IsNullOrEmpty(text)) continue;
        var plain = WebUtility.HtmlDecode(Tag.Replace(text, " "));
        foreach (Match m in Mention.Matches(plain))
          if (int.TryParse(m.Groups[1].Value, out var n) && n >= 1 && n < int.MaxValue)
            found.Add(ScaffoldAddress.SpotPrefix + n);
      }
      return found.ToList();
    }

    private static readonly Regex Tag = new Regex("<[^>]*>", RegexOptions.CultureInvariant);

    private static readonly Regex Mention = new Regex(@"(?<![A-Za-z0-9])spot\s+([0-9]{1,10})(?![0-9])",
      RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Drop every reservation for this document. TEST-ONLY seam: the store is static, so without
    /// it one test's reservations would be another's starting state.</summary>
    internal static void ForgetAll()
    {
      lock (_byDocument) { _byDocument.Clear(); }
    }
  }
}
