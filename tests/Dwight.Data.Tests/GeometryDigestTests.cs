using Dwight.Data;
using Xunit;

namespace Dwight.Data.Tests
{
  /// <summary>
  /// The geometric-signature composer (RR6). The false-verify that motivated it: layer 2 hashed each item's
  /// ToString(), and geometry goo stringifies to a TYPE DESCRIPTION - two different rectangles print the same
  /// words, so a moved rectangle passed verification. The digest now hashes serialized bytes at runtime and, as a
  /// typed fallback, this signature (bbox + endpoints + length). These headless tests pin the fallback composer:
  /// the same geometry passes through unchanged (bit-exact, no tolerance), and two rectangles that differ only in
  /// POSITION - same size, different plane - produce DIFFERENT signatures. The full serialized-bytes path and the
  /// solve-twice-unchanged determinism are runtime-verified (Tharun's replay of the domain case).
  /// </summary>
  public class GeometryDigestTests
  {
    // A rectangle in the world XY plane, origin at (ox, oy, oz), size w x h: its axis-aligned bbox, a corner as a
    // representative endpoint, and its perimeter length. Pure arithmetic - no Rhino runtime.
    private static string RectSig(double ox, double oy, double oz, double w, double h) =>
      GeometryDigest.Signature(
        ox, oy, oz, ox + w, oy + h, oz,          // bbox min / max
        ox, oy, oz, ox, oy, oz,                  // a closed rectangle: start == end corner
        2 * (w + h));                            // perimeter length

    [Fact]
    public void The_same_geometry_yields_the_same_signature()
    {
      // "prove the same exact item passes through" - a same-inputs re-solve produces identical geometry, which
      // must digest identically or a correct apply would falsely revert.
      Assert.Equal(RectSig(0, 0, 0, 10, 10), RectSig(0, 0, 0, 10, 10));
    }

    [Fact]
    public void Two_rectangles_same_size_different_plane_differ()
    {
      // THE RR6 case: two 10x10 rectangles, identical size (and identical perimeter length), but on different
      // planes (z = 0 vs z = 5). ToString() collides; the signature must not.
      var onZ0 = RectSig(0, 0, 0, 10, 10);
      var onZ5 = RectSig(0, 0, 5, 10, 10);
      Assert.NotEqual(onZ0, onZ5);
    }

    [Fact]
    public void A_moved_rectangle_in_the_same_plane_differs()
    {
      // a plain number into a domain input recentres the rectangle: same size, same plane, shifted origin.
      Assert.NotEqual(RectSig(0, 0, 0, 10, 10), RectSig(3, 4, 0, 10, 10));
    }

    [Fact]
    public void A_changed_length_differs()
    {
      // the endpoints/bbox can coincide while the curve between them differs - length carries that.
      var a = GeometryDigest.Signature(0, 0, 0, 10, 10, 0, 0, 0, 0, 0, 0, 0, 40);
      var b = GeometryDigest.Signature(0, 0, 0, 10, 10, 0, 0, 0, 0, 0, 0, 0, 41);
      Assert.NotEqual(a, b);
    }
  }
}
