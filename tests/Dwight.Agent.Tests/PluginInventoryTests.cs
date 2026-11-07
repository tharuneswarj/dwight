using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// A plugin made of user objects belongs to no library, so the inventory could not see it. Lane A's first finding: a model told a student
  /// "Honeybee and Butterfly are not installed at all" on a machine with 577 user objects, Ladybug Tools among them. The shapes here are that
  /// machine's, read from its component list: each user object carries a library id of its own that matches no library.
  /// </summary>
  public class PluginInventoryTests
  {
    private static readonly Guid CoreLib = Guid.NewGuid();
    private static readonly Guid Puffer = Guid.NewGuid();
    private static readonly Guid Ladybug = Guid.NewGuid();
    private static readonly Guid ScriptType = Guid.NewGuid();
    private static readonly Guid LoftType = Guid.NewGuid();
    private static readonly Guid PufferType = Guid.NewGuid();

    private static IEnumerable<LoadedLibrary> Libraries() => new[]
    {
      new LoadedLibrary { Id = CoreLib, Name = "Math Components", Version = "8.36", Core = true },
      new LoadedLibrary { Id = Puffer, Name = "Pufferfish", Version = "3.0.0.0" },
      new LoadedLibrary { Id = Ladybug, Name = "LadybugGrasshopper" },
    };

    private static ServerProxy Compiled(Guid library, string category, Guid? guid = null) =>
      new ServerProxy { Guid = guid ?? Guid.NewGuid(), Library = library, Category = category };

    private static ServerProxy UserObject(string category, Guid? guid = null, bool obsolete = false) =>
      new ServerProxy { Guid = guid ?? Guid.NewGuid(), Library = Guid.NewGuid(), UserObject = true, Category = category, Obsolete = obsolete };

    private static List<ServerProxy> Machine()
    {
      var list = new List<ServerProxy>
      {
        Compiled(CoreLib, "Maths", ScriptType),
        Compiled(CoreLib, "Surface", LoftType),
        Compiled(Puffer, "Pufferfish", PufferType),
        Compiled(Puffer, "Pufferfish"),
        Compiled(Ladybug, "Ladybug"),
      };
      for (var i = 0; i < 4; i++) list.Add(UserObject("Honeybee"));
      for (var i = 0; i < 3; i++) list.Add(UserObject("Ladybug"));
      for (var i = 0; i < 2; i++) list.Add(UserObject("Pufferfish"));
      return list;
    }

    private static PluginFact Named(List<PluginFact> facts, string name) => facts.Single(f => f.Name == name);

    [Fact]
    public void A_plugin_of_user_objects_is_listed_by_its_category_with_how_many()
    {
      var facts = PluginInventoryFacts.Join(Libraries(), Machine(), null);

      var honeybee = Named(facts, "Honeybee");
      Assert.Equal(4, honeybee.Components);
      Assert.Equal(4, honeybee.UserObjects);
      Assert.False(honeybee.Core);
      Assert.Null(honeybee.Version);
      Assert.Equal(3, Named(facts, "Ladybug").Components);   // the category the user sees, beside the 1-component library of another name
      Assert.Equal(1, Named(facts, "LadybugGrasshopper").Components);
    }

    [Fact]
    public void A_category_that_shares_a_plugins_name_is_that_plugin_and_never_a_second_row()
    {
      var facts = PluginInventoryFacts.Join(Libraries(), Machine(), null);

      var pufferfish = Assert.Single(facts, f => f.Name == "Pufferfish");
      Assert.Equal(4, pufferfish.Components);
      Assert.Equal(2, pufferfish.UserObjects);
      Assert.Equal("3.0.0.0", pufferfish.Version);
    }

    [Fact]
    public void A_core_library_is_left_alone_and_a_users_object_under_its_name_stays_their_own_row()
    {
      var proxies = Machine();
      proxies.Add(UserObject("Math Components"));

      var facts = PluginInventoryFacts.Join(Libraries(), proxies, null);

      var core = facts.Single(f => f.Core);
      Assert.Equal(2, core.Components);
      Assert.Equal(0, core.UserObjects);
      Assert.Equal(1, facts.Single(f => !f.Core && f.Name == "Math Components").UserObjects);
    }

    [Fact]
    public void An_obsolete_user_object_counts_for_nothing_and_a_category_of_only_those_is_not_listed()
    {
      var facts = PluginInventoryFacts.Join(Libraries(), new[] { UserObject("Old Tool", obsolete: true) }, null);

      Assert.DoesNotContain(facts, f => f.Name == "Old Tool");
    }

    [Fact]
    public void A_category_with_no_name_uses_the_one_name_for_that_and_case_does_not_split_a_plugin()
    {
      var facts = PluginInventoryFacts.Join(null, new[] { UserObject(null), UserObject("  "), UserObject("Honeybee"), UserObject("honeybee ") }, null);

      Assert.Equal(2, Named(facts, Dwight.Data.PluginName.Unnamed).UserObjects);
      Assert.Equal(2, facts.Single(f => string.Equals(f.Name, "Honeybee", StringComparison.OrdinalIgnoreCase)).UserObjects);
    }

    [Fact]
    public void Objects_on_the_canvas_count_where_they_came_from()
    {
      var uo = Guid.NewGuid();
      var proxies = Machine();
      proxies.Add(UserObject("Honeybee", uo));

      var facts = PluginInventoryFacts.Join(Libraries(), proxies, new[]
      {
        new CanvasObjectFact { ComponentGuid = LoftType, Category = "Surface" },               // native: core
        new CanvasObjectFact { ComponentGuid = ScriptType, Category = "Maths" },               // a plain script: core
        new CanvasObjectFact { ComponentGuid = ScriptType, Category = "Honeybee" },            // a placed user object is a script that still says Honeybee
        new CanvasObjectFact { ComponentGuid = uo, Category = null },                          // the user object's own type
        new CanvasObjectFact { ComponentGuid = PufferType, Category = "Pufferfish" },          // compiled, in a merged plugin
        new CanvasObjectFact { ComponentGuid = ScriptType, Category = "Something Else" },      // a category nothing provides: its type's library
        new CanvasObjectFact { ComponentGuid = Guid.NewGuid(), Category = "Honeybee" },        // an unknown type is not counted, as it never was
      });

      Assert.Equal(3, facts.Single(f => f.Core).UsedHere);
      Assert.Equal(2, Named(facts, "Honeybee").UsedHere);
      Assert.Equal(1, Named(facts, "Pufferfish").UsedHere);
    }

    [Fact]
    public void The_report_says_user_objects_only_where_there_are_any_and_says_how_to_read_them()
    {
      var result = PluginInventoryReport.Build(PluginInventoryFacts.Join(Libraries(), Machine(), null));
      Assert.True(result.Ok);
      var plugins = result.Result["plugins"].AsArray();

      var honeybee = plugins.Single(p => p["name"].GetValue<string>() == "Honeybee");
      Assert.Equal(4, honeybee["userObjects"].GetValue<int>());
      var plain = plugins.Single(p => p["name"].GetValue<string>() == "LadybugGrasshopper");
      Assert.Null(plain["userObjects"]);
      Assert.Contains("user objects", result.Result["bound"].GetValue<string>());
      Assert.Equal(4, result.Result["installed"].GetValue<int>());
      Assert.Equal(2, result.Result["core"]["components"].GetValue<int>());
    }

    /// <summary>Ruled: a plugin must not be lost alphabetically behind the cap. 43 rows is an ordinary machine, so it fits; past 80 a plugin used on this
    /// canvas is still listed, whatever its name, and the omitted sentence says how many are not.</summary>
    [Fact]
    public void The_cap_holds_an_ordinary_machine_and_a_plugin_used_here_is_never_the_one_hidden()
    {
      Assert.Equal(80, PluginInventoryReport.PluginCap);

      var ordinary = Enumerable.Range(0, 43).Select(i => new PluginFact { Name = "Plugin " + i.ToString("00"), Components = 1 })
        .Concat(new[] { new PluginFact { Name = "Wombat", Components = 59 } }).ToList();
      var listed = PluginInventoryReport.Build(ordinary).Result;
      Assert.Contains(listed["plugins"].AsArray(), p => p["name"].GetValue<string>() == "Wombat");
      Assert.Null(listed["omitted"]);

      var tooMany = Enumerable.Range(0, 90).Select(i => new PluginFact { Name = "Plugin " + i.ToString("00"), Components = 1 })
        .Concat(new[] { new PluginFact { Name = "Zzz", Components = 1, UsedHere = 3 } }).ToList();
      var capped = PluginInventoryReport.Build(tooMany).Result;
      Assert.Equal("Zzz", capped["plugins"].AsArray()[0]["name"].GetValue<string>());
      Assert.Equal(80, capped["plugins"].AsArray().Count);
      Assert.StartsWith("11 more plugins are installed and not listed", capped["omitted"].GetValue<string>());
    }

    /// <summary>The GH half only gathers. It has no list of its own to go stale, and the descriptor tells the model the form exists.</summary>
    [Fact]
    public void The_shim_hands_the_server_to_the_join_and_the_descriptor_says_so()
    {
      var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
      while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "Dwight.sln"))) dir = dir.Parent;
      Assert.True(dir != null);
      var shim = System.IO.File.ReadAllText(System.IO.Path.Combine(dir.FullName, "Dwight", "Agent", "GhSessionFacts.cs"));

      Assert.Contains("PluginInventoryFacts.Join(libraries, proxies, canvas)", shim);
      Assert.Contains("proxy.Kind == GH_ObjectType.UserObject", shim);
      Assert.Contains("listed by its ribbon category", AgentToolCatalog.All().Single(t => t.Name == AgentToolCatalog.GetInstalledPlugins).Description);
    }
  }
}
