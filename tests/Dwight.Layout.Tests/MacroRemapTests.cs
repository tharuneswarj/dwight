using System;
using System.Collections.Generic;
using Xunit;

namespace Dwight.Layout.Tests
{
  /// <summary>
  /// Phase 7 WP-1: the arrangement switcher re-runs layout on the SAME clustering without re-segmenting, so it
  /// must remap the cached guid-&gt;cluster segmentation onto a freshly-built graph. The first apply MOVES pivots,
  /// and DwightGraph assigns ids by pivot order, so a fresh build gives the same components DIFFERENT ids (the
  /// WP-A cycling trap). Remap must therefore key off the stable InstanceGuid, never the id, and drop a component
  /// whose guid is gone.
  /// </summary>
  public class MacroRemapTests
  {
    private static Guid G(int i) => new Guid(i, 0, 0, new byte[8]);

    [Fact]
    public void Remap_keys_off_guid_so_id_reassignment_preserves_the_clustering()
    {
      Guid a = G(1), b = G(2), c = G(3);
      var guidToCluster = new Dictionary<Guid, int> { [a] = 0, [b] = 0, [c] = 1 };
      // a fresh build after a prior apply assigns DIFFERENT ids (pivots moved) - here 7,8,9 not 1,2,3.
      var idToGuid = new Dictionary<int, Guid> { [7] = a, [8] = b, [9] = c };

      var macro = MacroRemap.Remap(guidToCluster, idToGuid);

      Assert.Equal(0, macro[7]);
      Assert.Equal(0, macro[8]);
      Assert.Equal(1, macro[9]);
    }

    [Fact]
    public void Remap_drops_a_component_whose_guid_is_no_longer_present()
    {
      Guid a = G(1), b = G(2);
      var guidToCluster = new Dictionary<Guid, int> { [a] = 0, [b] = 0 };
      // id 6's guid was never clustered (a component added/replaced since the cached segmentation).
      var idToGuid = new Dictionary<int, Guid> { [5] = a, [6] = G(99) };

      var macro = MacroRemap.Remap(guidToCluster, idToGuid);

      Assert.True(macro.ContainsKey(5));
      Assert.False(macro.ContainsKey(6));
    }
  }
}
