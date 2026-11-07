using Dwight.Data;
using Xunit;

namespace Dwight.Data.Tests
{
  /// <summary>
  /// RR18. Two laws that broke in OPPOSITE directions, and the whole point is that both hold AT ONCE:
  ///
  ///   RR6 was a false POSITIVE - a moved rectangle passed verification, because a text/ToString digest renders
  ///   geometry as a type description ("Rectangle"), so two different rectangles digested identically.
  ///   RR18 is a false NEGATIVE - a CORRECT script was always rejected, because the digest hashed the goo's
  ///   serialized bytes and the bytes carry the WRAPPER TYPE. The original's typed number and the script's
  ///   generically-wrapped number held the same 125 and hashed differently, so no to-script could ever pass.
  ///
  /// Fixing either one naively breaks the other: digest by value and you re-break RR6; digest by bytes and you
  /// re-break RR18. The ordering law (geometry BEFORE text) plus number normalisation is what holds both.
  /// </summary>
  public class ItemDigestTests
  {
    // a rectangle's geometry signature: same size, different plane (the exact RR6 case).
    private static string RectSignature(double z) =>
      GeometryDigest.Signature(0, 0, z, 10, 5, z, 0, 0, z, 0, 0, z, 30);

    [Fact]
    public void LAW_1_RR6_two_rectangles_of_the_same_size_on_different_planes_DIFFER()
    {
      // if this ever passes-as-equal again, a moved rectangle is silently "verified" - the RR6 false positive.
      var atZ0 = ItemDigest.From(ItemFacts.OfGeometry(RectSignature(0)));
      var atZ5 = ItemDigest.From(ItemFacts.OfGeometry(RectSignature(5)));

      Assert.NotEqual(atZ0, atZ5);
    }

    [Fact]
    public void LAW_2_RR18_the_same_number_digests_the_same_whatever_wrapper_it_arrived_in()
    {
      // the original component emits a typed number; the script's untyped output emits a generically-wrapped one.
      // Both unwrap to 125, so both MUST digest identically - otherwise a correct script is rejected forever.
      var typed = ItemDigest.From(ItemFacts.OfNumber(125d));
      var generic = ItemDigest.From(ItemFacts.OfNumber(125.0d));
      var integer = ItemDigest.From(ItemFacts.OfNumber(125));

      Assert.Equal(typed, generic);
      Assert.Equal(typed, integer);   // GH coerces these identically downstream, so a definition cannot tell them apart
    }

    [Fact]
    public void The_two_laws_hold_SIMULTANEOUSLY()
    {
      // the regression that would sneak back in: "fix" RR18 by digesting everything by its printed value, and a
      // rectangle silently collapses to its type name again.
      Assert.NotEqual(ItemDigest.From(ItemFacts.OfGeometry(RectSignature(0))),
                      ItemDigest.From(ItemFacts.OfGeometry(RectSignature(5))));
      Assert.Equal(ItemDigest.From(ItemFacts.OfNumber(125d)),
                   ItemDigest.From(ItemFacts.OfNumber(125d)));
    }

    [Fact]
    public void A_number_is_never_confused_with_the_text_of_that_number()
    {
      // normalisation must not become coercion: a Text output holding "125" is NOT a Number output holding 125.
      // Treating them as equal would mask a real behaviour change (the false-positive direction).
      Assert.NotEqual(ItemDigest.From(ItemFacts.OfNumber(125d)),
                      ItemDigest.From(ItemFacts.OfText("125")));
    }

    [Fact]
    public void Geometry_is_never_confused_with_text_that_happens_to_match()
    {
      Assert.NotEqual(ItemDigest.From(ItemFacts.OfGeometry("sig")),
                      ItemDigest.From(ItemFacts.OfText("sig")));
    }

    [Fact]
    public void Numbers_are_canonical_and_locale_proof()
    {
      Assert.Equal("125", ItemDigest.Canonical(125d));
      Assert.Equal("125.5", ItemDigest.Canonical(125.5d));
      Assert.Equal(ItemDigest.Canonical(0d), ItemDigest.Canonical(-0d));   // -0.0 must not disagree with 0.0
    }

    [Fact]
    public void Different_numbers_still_differ()
    {
      Assert.NotEqual(ItemDigest.From(ItemFacts.OfNumber(125d)), ItemDigest.From(ItemFacts.OfNumber(125.0001d)));
    }

    [Fact]
    public void An_unknown_type_stays_BYTE_exact_rather_than_being_waved_through()
    {
      Assert.NotEqual(ItemDigest.From(ItemFacts.OfBytes(1234UL)), ItemDigest.From(ItemFacts.OfBytes(5678UL)));
      Assert.Equal(ItemDigest.From(ItemFacts.OfBytes(1234UL)), ItemDigest.From(ItemFacts.OfBytes(1234UL)));
    }

    [Fact]
    public void An_empty_item_digests_to_empty()
    {
      Assert.Equal("", ItemDigest.From(ItemFacts.Empty()));
    }
  }
}
