using System.Globalization;

namespace Dwight.Data
{
  /// <summary>What KIND of value an item turned out to be, once unwrapped. The order of the checks is a law, not a
  /// preference - see <see cref="ItemDigest.From"/>.</summary>
  internal enum ItemKind { Empty, Number, Geometry, Text, Opaque }

  /// <summary>The unwrapped facts about one item. The GH shim (<c>GooDigest</c>) fills this in; the DECISION is
  /// made here, GH-free, so both laws below are headless-testable.</summary>
  internal readonly struct ItemFacts
  {
    public readonly ItemKind Kind;
    public readonly double Number;
    public readonly string Text;        // the geometry signature, or the text value
    public readonly ulong Bytes;        // the serialized-goo hash (the last-resort fallback)

    private ItemFacts(ItemKind kind, double number, string text, ulong bytes)
    { Kind = kind; Number = number; Text = text; Bytes = bytes; }

    public static ItemFacts Empty() => new ItemFacts(ItemKind.Empty, 0, null, 0);
    public static ItemFacts OfNumber(double d) => new ItemFacts(ItemKind.Number, d, null, 0);
    public static ItemFacts OfGeometry(string signature) => new ItemFacts(ItemKind.Geometry, 0, signature, 0);
    public static ItemFacts OfText(string text) => new ItemFacts(ItemKind.Text, 0, text, 0);
    public static ItemFacts OfBytes(ulong hash) => new ItemFacts(ItemKind.Opaque, 0, null, hash);
  }

  /// <summary>
  /// RR18: the per-item digest DECISION - UNWRAP, then digest by type-class. GH-free (ADR-091), because the two
  /// laws it has to hold simultaneously are exactly the ones that broke, one after the other:
  ///
  ///   LAW 1 (RR6, must NOT regress): two different rectangles of the same size digest DIFFERENTLY. Geometry is
  ///   therefore checked BEFORE any text path - a rectangle renders to a type DESCRIPTION, so a text digest would
  ///   silently make every rectangle equal to every other one. This false-verified a moved rectangle once already.
  ///
  ///   LAW 2 (RR18, the false NEGATIVE): the same NUMBER digests the same regardless of the wrapper it arrives in.
  ///   The old digest hashed the goo's serialized bytes - and the bytes carry the WRAPPER TYPE. An Addition's
  ///   Result emits a typed number; a script's untyped output emits a generically-wrapped one. Same value,
  ///   different bytes, "the results differed" - so a to-script could NEVER pass, no matter how correct the script
  ///   was. Normalising a number to its canonical invariant form is correct, not lax: Grasshopper coerces these
  ///   wrappers identically downstream, so a definition cannot tell them apart either.
  ///
  /// The fallback stays byte-exact for anything that is neither a number, geometry, nor text - so an unknown type
  /// is still compared strictly, never waved through.
  /// </summary>
  internal static class ItemDigest
  {
    public static string From(ItemFacts facts)
    {
      switch (facts.Kind)
      {
        case ItemKind.Empty:
          return "";
        case ItemKind.Number:
          return "n:" + Canonical(facts.Number);
        case ItemKind.Geometry:      // BEFORE text, always (law 1)
          return "g:" + (facts.Text ?? "");
        case ItemKind.Text:
          return "t:" + (facts.Text ?? "");
        default:
          return "b:" + facts.Bytes;
      }
    }

    /// <summary>A number's canonical form: round-trip ("R") and invariant, so 125, 125.0 and 125.00 are ONE token
    /// and a locale can never change a digest. Negative zero is normalised, so -0.0 and 0.0 do not disagree.</summary>
    public static string Canonical(double d)
    {
      if (d == 0d) d = 0d;   // collapses -0.0
      return d.ToString("R", CultureInfo.InvariantCulture);
    }
  }
}
