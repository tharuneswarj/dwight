using System;
using System.Collections.Generic;
using Dwight.Models;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// The catalogue DELIVERY rule (ADR-081). These tests exist because the bug they cover was invisible:
  /// the user copy wins over the bundled catalogue by design, but it was seeded once and never
  /// revisited - so shipping a new model lineup updated the .gha and changed nothing anyone could see.
  /// The plugin went on truthfully reporting the models it was using, and they were the stale ones.
  ///
  /// Found at the Phase-8 WP-2 Stage-B deploy: a machine seeded months earlier still resolved the old
  /// lineup after every redeploy. Left unfixed it would also have pointed the Stage-B thinking capture
  /// at the wrong model family (#192c) - a measurement trap, not just a UI staleness.
  /// </summary>
  public class CatalogueSeedTests
  {
    private static string Cat(int? version, string extra = "")
    {
      var v = version.HasValue ? $"\"catalogueVersion\": {version.Value}," : "";
      return "{" + v + extra + "\"models\":[]}";
    }

    // ---- reading the version --------------------------------------------------------------------

    [Fact]
    public void A_copy_with_no_catalogue_version_reads_as_zero()
    {
      // Every install seeded before this mechanism shipped - it must upgrade exactly once.
      Assert.Equal(0, CatalogueSeed.Version(Cat(null)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    public void The_declared_version_is_read(int version) =>
      Assert.Equal(version, CatalogueSeed.Version(Cat(version)));

    [Fact]
    public void Unreadable_or_absent_json_reads_as_zero_rather_than_throwing()
    {
      Assert.Equal(0, CatalogueSeed.Version(null));
      Assert.Equal(0, CatalogueSeed.Version(""));
      Assert.Equal(0, CatalogueSeed.Version("{not json"));
      Assert.Equal(0, CatalogueSeed.Version("{\"catalogueVersion\":\"two\",\"models\":[]}"));
    }

    // ---- the refresh decision -------------------------------------------------------------------

    [Fact]
    public void A_newer_bundled_catalogue_refreshes_a_legacy_user_copy()
    {
      // THE HEADLINE CASE: the real machine's file, seeded long ago with no version at all.
      Assert.True(CatalogueSeed.ShouldRefresh(bundledJson: Cat(2), userJson: Cat(null)));
    }

    [Fact]
    public void A_newer_bundled_catalogue_refreshes_an_older_versioned_copy() =>
      Assert.True(CatalogueSeed.ShouldRefresh(Cat(3), Cat(2)));

    [Fact]
    public void An_equal_version_leaves_the_user_copy_alone()
    {
      // The user's edits keep winning between revisions - that is ADR-081's whole point.
      Assert.False(CatalogueSeed.ShouldRefresh(Cat(2), Cat(2)));
    }

    [Fact]
    public void A_user_copy_that_is_ahead_is_never_downgraded() =>
      Assert.False(CatalogueSeed.ShouldRefresh(Cat(1), Cat(5)));

    [Fact]
    public void With_no_bundled_catalogue_there_is_nothing_to_refresh_from()
    {
      Assert.False(CatalogueSeed.ShouldRefresh(null, Cat(1)));
      Assert.False(CatalogueSeed.ShouldRefresh("", Cat(1)));
    }

    [Fact]
    public void An_unparseable_bundled_catalogue_never_triggers_a_refresh()
    {
      // Reading as 0 means it can never exceed a real version - a corrupt build cannot wipe a
      // user's catalogue.
      Assert.False(CatalogueSeed.ShouldRefresh("{not json", Cat(1)));
    }

    // ---- preserving the superseded copy ----------------------------------------------------------

    [Fact]
    public void The_superseded_copy_is_named_for_the_version_it_held()
    {
      Assert.Equal("models.v0.bak", CatalogueSeed.BakFileName(0, _ => false));
      Assert.Equal("models.v3.bak", CatalogueSeed.BakFileName(3, _ => false));
    }

    [Fact]
    public void A_second_supersession_never_overwrites_the_first()
    {
      // An edited catalogue is the user's work; a name collision must not destroy the earlier one.
      var taken = new HashSet<string> { "models.v0.bak" };
      Assert.Equal("models.v0.2.bak", CatalogueSeed.BakFileName(0, taken.Contains));

      taken.Add("models.v0.2.bak");
      Assert.Equal("models.v0.3.bak", CatalogueSeed.BakFileName(0, taken.Contains));
    }

    [Fact]
    public void BakFileName_tolerates_no_existence_check()
    {
      Assert.Equal("models.v1.bak", CatalogueSeed.BakFileName(1, null));
    }
  }
}
