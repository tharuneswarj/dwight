// Positive controls for tools/check-product-copy.js. Every line marked PLANTED carries one user-visible
// promise of the product's future, and the gate must find each - or it reports ITSELF as broken.
// The phrases are the real defects the #883 pass found, so the controls test the class, not an invention.
class PositiveControls
{
  string a = "Explaining a whole definition arrives with the segmenter in a later phase."; // PLANTED
  string b = "Reorganising very large graphs in pieces is planned for a later phase."; // PLANTED
  string c = "Custom endpoints land in a later pass."; // PLANTED
  string d = "Attachments arrive with Scaffold"; // PLANTED
  string e = "This mode is coming soon."; // PLANTED
  string f = $"Chunking {n} graphs arrives in a future release."; // PLANTED
  string g = @"Verbatim strings are read too - this one is planned for next month."; // PLANTED
}
