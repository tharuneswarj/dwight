using Xunit;
using Dwight.Session;

namespace Dwight.Session.Tests
{
  /// <summary>WP-1 #179: the provenance-aware dock filter. The clean/messy pair (two files, one DocumentID)
  /// is the case this exists for.</summary>
  public class ProvenanceViewTests
  {
    private const string Clean = @"C:\work\BESS_clean.gh";
    private const string Messy = @"C:\work\BESS_messy.gh";

    // ---- 2+ same-id docs LIVE: own rows only, no interleave, no annotation

    [Fact]
    public void Live_siblings_show_only_the_active_docs_own_rows()
    {
      Assert.Equal((true, false), ProvenanceView.Decide(Clean, Clean, multipleLiveSiblings: true));   // own -> shown
      Assert.Equal((false, false), ProvenanceView.Decide(Messy, Clean, multipleLiveSiblings: true));  // foreign -> hidden
    }

    [Fact]
    public void Live_siblings_never_annotate_a_foreign_row_they_hide_it()
    {
      var (show, foreign) = ProvenanceView.Decide(Messy, Clean, multipleLiveSiblings: true);
      Assert.False(show);
      Assert.False(foreign);   // hidden, not labelled - no live interleave
    }

    // ---- one doc open: full lineage, foreign rows annotated

    [Fact]
    public void A_single_open_doc_shows_the_full_lineage()
    {
      Assert.Equal((true, false), ProvenanceView.Decide(Clean, Clean, multipleLiveSiblings: false));  // own
      Assert.Equal((true, true), ProvenanceView.Decide(Messy, Clean, multipleLiveSiblings: false));   // foreign, shown + flagged
    }

    // ---- the unsaved / save transition

    [Fact]
    public void An_unsaved_doc_treats_empty_origin_rows_as_its_own()
    {
      // before any save both myOrigin and the rows are "" - all own, never foreign.
      Assert.Equal((true, false), ProvenanceView.Decide("", "", multipleLiveSiblings: false));
      Assert.Equal((true, false), ProvenanceView.Decide(null, "", multipleLiveSiblings: false));
      Assert.Equal((true, false), ProvenanceView.Decide(null, null, multipleLiveSiblings: false));
    }

    [Fact]
    public void After_a_save_re_stamps_origins_the_rows_are_the_docs_own()
    {
      // the re-stamp turns "" into the saved path, so post-save the rows read as own, not foreign.
      Assert.Equal((true, false), ProvenanceView.Decide(Clean, Clean, multipleLiveSiblings: false));
    }

    // ---- origin comparison

    [Fact]
    public void Origins_compare_case_insensitively()
    {
      Assert.True(ProvenanceView.SameOrigin(@"C:\Work\A.gh", @"c:\work\a.gh"));
      Assert.False(ProvenanceView.SameOrigin(Clean, Messy));
    }
  }
}
