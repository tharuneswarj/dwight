// Negative controls for tools/check-product-copy.js. NOTHING here is user-visible, or it is true, so the
// gate must flag nothing. If it flags anything, it is reading what a user never sees - and is only a grep.
//
// A comment may say this arrives in a later phase, or is planned for a later release, or is coming soon.
/* A block comment may too: arrives with the segmenter, planned for later, soon. */
class NegativeControls
{
  /// <summary>A doc comment: the chunker is planned for a later phase.</summary>
  string a = "Nothing has been built yet on this canvas."; // true of the USER'S work - and "yet" is not gated
  string b = "No Dwight commands yet on this file.";
  int c = 1; // a trailing comment: coming soon, in a later release
}
