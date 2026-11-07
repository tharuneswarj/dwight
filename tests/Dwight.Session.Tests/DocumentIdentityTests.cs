using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Dwight.Session;

namespace Dwight.Session.Tests
{
  /// <summary>
  /// WP-1 step 2: which file a record belongs to (ADR-067, as refined by the spike).
  ///
  /// The spike settled the hard part structurally: <c>GH_Document.DocumentID</c> is stamped in the .gh archive
  /// (<c>Definition > DocumentHeader > DocumentID</c>), exists at document BIRTH before any save, is preserved
  /// by Write, restored by Read, and is re-keyed by NOTHING in Grasshopper outside cluster internals (an IL
  /// scan of 25,727 methods, controls passed). So the anchor is the DocumentID, there is no ephemeral id and no
  /// promotion step, and the fingerprint is NOT an identity fallback that never runs - it has three real jobs:
  /// the Guid.Empty anchor, divergence detection, and collision disambiguation.
  ///
  /// The collision is real and was confirmed live: a Save-As INHERITS the id, so an original, its Save-As, and
  /// any Explorer copy are one id - and Grasshopper will hold two of them open at once.
  /// </summary>
  public class DocumentIdentityTests
  {
    private static readonly Guid Doc = new Guid("66fd7275-7717-4065-90eb-483736e9296a");

    // ---------------------------------------------------------------- the key

    [Fact]
    public void A_real_DocumentID_is_the_key()
    {
      Assert.Equal("66fd7275-7717-4065-90eb-483736e9296a", DocumentKey.For(Doc, "ABCD"));
    }

    [Fact]
    public void The_key_ignores_the_file_path_entirely()
    {
      // the whole reason for anchoring to the id: a move, a rename, or a branch checkout must not re-key.
      Assert.Equal(DocumentKey.For(Doc, "ABCD"), DocumentKey.For(Doc, "WXYZ"));
    }

    [Fact]
    public void An_EMPTY_DocumentID_falls_back_to_the_fingerprint()
    {
      // The untested edge, handled defensively: a .gh lacking the header chunk would otherwise collide every
      // such file onto one shared Guid.Empty record.
      var key = DocumentKey.For(Guid.Empty, "ABCD1234");
      Assert.NotNull(key);
      Assert.DoesNotContain("00000000-0000-0000-0000-000000000000", key);
      Assert.Contains("ABCD1234", key);
    }

    [Fact]
    public void Two_anchorless_documents_with_different_content_do_NOT_share_a_key()
    {
      Assert.NotEqual(DocumentKey.For(Guid.Empty, "AAAA"), DocumentKey.For(Guid.Empty, "BBBB"));
    }

    [Fact]
    public void No_anchor_and_no_fingerprint_yields_NO_key()
    {
      // null means "memory-only" - never a wrong file's record.
      Assert.Null(DocumentKey.For(Guid.Empty, null));
      Assert.Null(DocumentKey.For(Guid.Empty, ""));
      Assert.Null(DocumentKey.For(Guid.Empty, "   "));
    }

    [Fact]
    public void Every_key_is_safe_to_use_as_a_file_name()
    {
      // the key becomes %APPDATA%\Dwight\sessions\<key>.json, so a stray separator would be a path escape.
      var keys = new[]
      {
        DocumentKey.For(Doc, "ABCD"),
        DocumentKey.For(Guid.Empty, "0123456789ABCDEF"),
      };
      var bad = System.IO.Path.GetInvalidFileNameChars();
      foreach (var k in keys)
      {
        Assert.False(string.IsNullOrWhiteSpace(k));
        Assert.DoesNotContain(k, c => bad.Contains(c));
        Assert.DoesNotContain("..", k);
      }
    }

    [Fact]
    public void A_hostile_fingerprint_can_never_escape_the_sessions_folder()
    {
      // defence in depth: the fingerprint is computed, not user-supplied, but the key is a PATH component.
      var key = DocumentKey.For(Guid.Empty, @"../../../windows/system32/evil");
      if (key != null)
      {
        Assert.DoesNotContain("..", key);
        Assert.DoesNotContain("/", key);
        Assert.DoesNotContain("\\", key);
      }
    }

    // ---------------------------------------------------------------- the fingerprint

    [Fact]
    public void The_fingerprint_is_order_independent()
    {
      var a = DocumentFingerprint.Compute(new[] { "c:Curve", "c:Loft", "e:1>2" });
      var b = DocumentFingerprint.Compute(new[] { "e:1>2", "c:Loft", "c:Curve" });
      Assert.Equal(a, b);
    }

    [Fact]
    public void The_fingerprint_is_deterministic_across_calls()
    {
      var tokens = new[] { "c:Curve", "c:Loft" };
      Assert.Equal(DocumentFingerprint.Compute(tokens), DocumentFingerprint.Compute(tokens));
    }

    [Fact]
    public void The_fingerprint_CHANGES_when_the_content_changes()
    {
      var a = DocumentFingerprint.Compute(new[] { "c:Curve", "c:Loft" });
      var b = DocumentFingerprint.Compute(new[] { "c:Curve", "c:Loft", "c:Extrude" });
      Assert.NotEqual(a, b);
    }

    [Fact]
    public void The_fingerprint_distinguishes_a_rewiring_that_keeps_the_same_components()
    {
      // divergence detection is one of its three jobs, so edges must count - not just the component multiset.
      var a = DocumentFingerprint.Compute(new[] { "c:A", "c:B", "c:C", "e:A>B" });
      var b = DocumentFingerprint.Compute(new[] { "c:A", "c:B", "c:C", "e:A>C" });
      Assert.NotEqual(a, b);
    }

    [Fact]
    public void Repeated_tokens_are_significant()
    {
      // two Loft components are not one Loft component; a set-based hash would erase the difference.
      var a = DocumentFingerprint.Compute(new[] { "c:Loft" });
      var b = DocumentFingerprint.Compute(new[] { "c:Loft", "c:Loft" });
      Assert.NotEqual(a, b);
    }

    [Fact]
    public void An_empty_or_null_token_set_yields_a_stable_non_null_fingerprint()
    {
      var empty = DocumentFingerprint.Compute(new string[0]);
      Assert.False(string.IsNullOrEmpty(empty));
      Assert.Equal(empty, DocumentFingerprint.Compute(null));
    }

    [Fact]
    public void Null_tokens_inside_the_set_are_tolerated()
    {
      var ex = Record.Exception(() => DocumentFingerprint.Compute(new[] { "c:A", null, "c:B" }));
      Assert.Null(ex);
    }

    [Fact]
    public void The_fingerprint_is_file_name_safe_hex()
    {
      var fp = DocumentFingerprint.Compute(new[] { "c:Curve" });
      Assert.Matches("^[0-9A-F]+$", fp);
    }

    /// <summary>
    /// #303: WHAT AN ABSENT KEY ACTUALLY MEANS - the composition nobody had written down.
    ///
    /// Compute never returns blank (asserted above), and For only returns null on a blank fingerprint. Put
    /// the two together and a REAL document is always identifiable: an empty untitled canvas, a document
    /// with no DocumentID, a foreign .gh - every one of them gets a key. So the null key the fallback was
    /// written for cannot occur, and the "(none)" bucket means exactly one thing: NO DOCUMENT.
    ///
    /// That mattered because four call sites read "(none)" as "refuse", including the two that create and
    /// read a chat thread. They were written for an unidentifiable document; what they actually caught was
    /// an absent one, and they turned "we cannot save this conversation" into "we cannot have it" (#296).
    /// </summary>
    [Fact]
    public void Every_real_document_is_identifiable_so_an_absent_key_means_no_document()
    {
      // an anchored document - the normal case
      Assert.NotNull(DocumentKey.For(Guid.NewGuid(), null));

      // no anchor, and NOTHING on the canvas: still keyed, because the fingerprint hashes emptiness too
      var emptyCanvas = DocumentFingerprint.Compute(new string[0]);
      Assert.NotNull(DocumentKey.For(Guid.Empty, emptyCanvas));

      // no anchor, with content
      Assert.NotNull(DocumentKey.For(Guid.Empty, DocumentFingerprint.Compute(new[] { "c:Curve" })));

      // the ONLY null: nothing to fingerprint at all, which is what "no document" produces
      Assert.Null(DocumentKey.For(Guid.Empty, null));
      Assert.Null(DocumentKey.For(Guid.Empty, "   "));
    }

    [Fact]
    public void Two_unanchored_documents_still_get_different_keys()
    {
      // The reason the fingerprint fallback exists at all - it must not collapse every unanchored document
      // into one shared record, which is the wrong-history hazard #163 refused to accept.
      var a = DocumentKey.For(Guid.Empty, DocumentFingerprint.Compute(new[] { "c:Curve" }));
      var b = DocumentKey.For(Guid.Empty, DocumentFingerprint.Compute(new[] { "c:Surface" }));
      Assert.NotEqual(a, b);
    }
  }
}
