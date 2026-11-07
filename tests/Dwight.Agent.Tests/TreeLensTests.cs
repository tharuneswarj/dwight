using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Dwight.Agent;
using Dwight.UI;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Brief DB: the Tree Lens. Every rule is pure and tested here: the words on a chip, where data stops, where a chip goes, the cap and the keys. The live read and the drawing are bound to
  /// Grasshopper, so they are pinned by their text, and the Rhino rows are the architect's. No model call anywhere in this unit, and a test holds that too.
  /// </summary>
  public class TreeLensTests
  {
    // ---- Brief EB: a wire that leaves the selection ends at its edge --------------------------------------------------------------

    private static Guid EdgeG(int n) => new Guid(n, 0, 0, new byte[8]);

    [Fact]
    public void A_wire_with_a_far_end_outside_the_selection_leaves_it_and_one_inside_does_not()
    {
      var six = Enumerable.Range(1, 6).Select(EdgeG).ToList();
      Assert.False(LensEdge.Leaves(six, EdgeG(1), EdgeG(2)));
      Assert.True(LensEdge.Leaves(six, EdgeG(1), EdgeG(15)));
      Assert.True(LensEdge.Leaves(six, EdgeG(15), EdgeG(1)));
      Assert.False(LensEdge.Leaves(new Guid[0], EdgeG(1), EdgeG(15)));   // no selection known: nothing to leave
      Assert.False(LensEdge.Leaves(null, EdgeG(1), EdgeG(15)));
    }

    [Fact]
    public void A_chip_at_the_edge_sits_beside_the_selected_grip_on_the_side_the_wire_leaves_by()
    {
      var outgoing = ChipPlace.AtEdge(100, 50, true, 40, 12);
      Assert.Equal(100 + ChipPlace.Margin, outgoing.X);
      Assert.Equal(44, outgoing.Y);

      var incoming = ChipPlace.AtEdge(100, 50, false, 40, 12);
      Assert.Equal(100 - ChipPlace.Margin - 40, incoming.X);
      Assert.True(incoming.X + incoming.W < 100);
      Assert.True(outgoing.X > 100);
    }

    /// <summary>A chip's words take the wire's source and its counts, and nothing of where the wire goes, so a far consumer cannot be named. A far source gives a noun at most, and never its name.</summary>
    [Fact]
    public void A_chip_at_the_edge_names_the_wires_value_and_not_the_far_component()
    {
      var far = new WireSource { Nick = "Vase G-code", OutputName = "G", TypeHint = "Text" };
      var words = TreeWords.For(new TreeSigDto { Paths = 1, Depth = 1, Items = new[] { 3 } }, new WireCounts { Total = 3, Solved = true }, far);
      Assert.StartsWith("3 ", words);
      Assert.DoesNotContain("Vase", words);
      Assert.DoesNotContain("G-code", words);
    }

    [Fact]
    public void A_wire_that_leaves_is_not_lit_and_its_chip_goes_to_the_edge_in_the_host()
    {
      var s = File.ReadAllText(Path.Combine(EdgeRepo(), "Dwight", "UI", "TreeLens.cs"));
      Assert.Contains("if (c.Leaves) continue;", s);
      Assert.Contains("Leaves = LensEdge.Leaves(_selection, fromOwner, OwnerOf(to).InstanceGuid),", s);
      Assert.Contains("FromSelected = !c.Leaves || c.Outgoing,", s);
      Assert.Contains("ToSelected = !c.Leaves || !c.Outgoing,", s);
      // the same StepWires is what the lit lines and the wash's bands both read
      Assert.Contains("foreach (var (a, b) in StepWires())", s);
      Assert.Contains("foreach (var (a, b) in TreeLens.StepWires()) ExcludeWire(region, a, b);", File.ReadAllText(Path.Combine(EdgeRepo(), "Dwight", "UI", "CanvasGlow.cs")));
    }

    private static string EdgeRepo()
    {
      var dir = AppContext.BaseDirectory;
      while (dir != null && !File.Exists(Path.Combine(dir, "Dwight.sln"))) dir = Path.GetDirectoryName(dir);
      Assert.NotNull(dir);
      return dir;
    }

    // ---- the writer's real output: a signature goes through get_tree_signature, and the words read what came back ----------------------------------

    /// <summary>The tool's own output for a wire of this shape, read back into the type the lens reads. So a rule the capture has no wire for is still tested against the writer's format.</summary>
    private static TreeSigDto Sig(int paths, int depth, params int[] items)
    {
      var ids = new AgentIdRegistry();
      var from = Guid.NewGuid();
      var to = Guid.NewGuid();
      ids.Sync(new[] { from, to });
      var fromId = ids.Issue(from);
      var toId = ids.Issue(to);
      var snap = new AgentSnapshot
      {
        Nodes =
        {
          new AgentNode { Id = fromId, Type = "Source", Kind = "component" },
          new AgentNode { Id = toId, Type = "Target", Kind = "component" },
        },
        Edges = { new AgentEdgeDto { FromNode = fromId, FromParam = "O", ToNode = toId, ToParam = "I", Tree = new TreeSigDto { Paths = paths, Depth = depth, Items = items } } },
      };
      var args = (JsonObject)JsonNode.Parse("{\"from\":{\"node\":" + fromId + ",\"param\":\"O\"},\"to\":{\"node\":" + toId + ",\"param\":\"I\"}}");
      var result = AgentToolCore.GetTreeSignature(snap, ids, args);
      Assert.True(result.Ok, result.ToJson());
      return ReadSig((JsonObject)JsonNode.Parse(result.ToJson())["result"]);
    }

    private static TreeSigDto ReadSig(JsonObject tool) => new TreeSigDto
    {
      Paths = tool["paths"].GetValue<int>(),
      Depth = tool["depth"].GetValue<int>(),
      Items = tool["items"].AsArray().Select(i => i.GetValue<int>()).ToArray(),
    };

    private static WireCounts Live(long total) => new WireCounts { Total = total };

    private static WireSource Hint(string type) => new WireSource { TypeHint = type };

    // ---- the words on real wires: the product's own get_tree_signature, captured by Rhino 2 on the ninth build, never typed by hand ---------------------

    private static JsonNode Shapes => JsonNode.Parse(Repo("tests", "Dwight.Agent.Tests", "Fixtures", "tree-sigs-shapes.json"));
    private static JsonNode Tools4 => JsonNode.Parse(Repo("tests", "Dwight.Agent.Tests", "Fixtures", "tree-sigs-tools4.json"));

    private static JsonNode Case(string wire) => Shapes["cases"].AsArray().Single(c => c["wire"].GetValue<string>() == wire);

    /// <summary>The capture holds the first item's type, where the product reads the output's own hint. For these sources they name the same thing; the Rhino row reads the hint itself.</summary>
    private static WireSource Hinted(JsonNode live) => new WireSource { TypeHint = live["first_item_type"]?.GetValue<string>()?.Replace("GH_", "") };

    [Theory]
    [InlineData("Number Slider (size3).size3 -> Partition List (Part_mixed).S", "1 number")]
    [InlineData("Number Slider (size3).size3 -> Partition List (Part_equal).S", "1 number")]
    [InlineData("Number Slider (count9).count9 -> Series (S9).C", "1 number")]
    [InlineData("Boolean Toggle (tog).tog -> Dispatch (Disp).P", "1 boolean")]
    [InlineData("Boolean Toggle (tog).tog -> Cull Pattern (Cull).P", "1 boolean")]
    [InlineData("Boolean Toggle (tog_false).tog_false -> Cull Pattern (Cull_all).P", "1 boolean")]
    [InlineData("Series (S10).S -> Panel (P_one_list).P_one_list", "10 numbers")]
    [InlineData("Series (S10).S -> Partition List (Part_mixed).L", "10 numbers")]
    [InlineData("Series (S10).S -> Graft Tree (Graft).T", "10 numbers")]
    [InlineData("Series (S10).S -> Dispatch (Disp).L", "10 numbers")]
    [InlineData("Series (S10).S -> Cull Pattern (Cull).L", "10 numbers")]
    [InlineData("Series (S10).S -> Cull Pattern (Cull_all).L", "10 numbers")]
    [InlineData("Series (S9).S -> Partition List (Part_equal).L", "9 numbers")]
    [InlineData("Partition List (Part_mixed).C -> Panel (P_mixed).P_mixed", "4 lists, 1 to 3 each")]
    [InlineData("Partition List (Part_equal).C -> Panel (P_equal).P_equal", "3 lists of 3")]
    [InlineData("Graft Tree (Graft).T -> Panel (P_graft).P_graft", "10 lists of 1")]
    [InlineData("Dispatch (Disp).A -> Panel (P_dispA).P_dispA", "10 numbers")]
    [InlineData("Dispatch (Disp).B -> Panel (P_dispB).P_dispB", "nothing")]
    [InlineData("Cull Pattern (Cull).L -> Panel (P_cull).P_cull", "10 numbers")]
    [InlineData("Cull Pattern (Cull_all).L -> Panel (P_cull_all).P_cull_all", "nothing")]
    public void The_tools_own_output_on_real_wires_reads_as(string wire, string words)
    {
      var c = Case(wire);
      var live = c["live"];
      var counts = new WireCounts { Total = live["live_DataCount"].GetValue<long>() };
      Assert.Equal(words, TreeWords.For(ReadSig((JsonObject)c["tool_reply"]["result"]), counts, Hinted(live)));
    }

    [Fact]
    public void Every_wire_in_the_capture_has_its_words_above_and_the_tool_agrees_with_the_live_read()
    {
      var cases = Shapes["cases"].AsArray();
      Assert.Equal(20, cases.Count);
      foreach (var c in cases)
      {
        var sig = ReadSig((JsonObject)c["tool_reply"]["result"]);
        var live = c["live"];
        Assert.Equal(live["live_PathCount"].GetValue<int>(), sig.Paths);
        Assert.Equal(live["live_items_per_path"].AsArray().Select(i => i.GetValue<int>()), sig.Items);
        Assert.Equal(live["live_DataCount"].GetValue<long>(), sig.Items.Sum());
      }
    }

    /// <summary>A zero-item wire from a filter is said as nothing, never "0 of 10".</summary>
    [Fact]
    public void A_filter_that_passed_nothing_on_says_nothing()
    {
      var c = Case("Cull Pattern (Cull_all).L -> Panel (P_cull_all).P_cull_all");
      var counts = new WireCounts { Total = 0, OfSource = 10 };
      Assert.Equal(TreeWords.Nothing, TreeWords.For(ReadSig((JsonObject)c["tool_reply"]["result"]), counts, null));
    }

    /// <summary>Tools4's one real wire. Its other outputs are unwired, so the tool refuses to call them wires and only the lens's own read of the output sees them.</summary>
    [Fact]
    public void Tools4s_one_wire_reads_as_one_boolean_and_the_tool_refuses_an_unwired_output()
    {
      var calls = Tools4["tool_calls"].AsArray();
      var wire = calls.First(c => (bool)c["reply"]["ok"]);
      Assert.Equal("1 boolean", TreeWords.For(ReadSig((JsonObject)wire["reply"]["result"]), new WireCounts { Total = 1 }, Hint("Boolean")));

      var refused = calls.First(c => !(bool)c["reply"]["ok"]);
      Assert.Equal(ToolErrors.UnknownId, refused["reply"]["error"]["code"].GetValue<string>());
    }

    /// <summary>
    /// WHY A DEAD END IS ONLY SAID OF A SPLITTER, measured on tools4: most of its outputs hold items and nothing reads them, because it is a file of fixtures and every chain ends. A line on
    /// each would be true and would say nothing about where data stops.
    /// </summary>
    [Fact]
    public void Tools4s_unread_outputs_are_not_stops()
    {
      var outputs = Tools4["live_outputs"].AsArray();
      var holding = outputs.Where(o => o["live_DataCount"].GetValue<long>() > 0).ToList();
      Assert.True(holding.Count > 5);

      var nodes = holding.Select(o => new LensNode
      {
        Guid = Guid.NewGuid(),
        Type = o["source_name"]?.GetValue<string>(),
        Label = o["source_name"]?.GetValue<string>(),
        Outputs = { new LensOutput { Guid = Guid.NewGuid(), Port = "O", Count = o["live_DataCount"].GetValue<long>(), Recipients = 0 } },
      });
      Assert.Empty(TreeStops.Find(nodes));
    }

    // ---- the words ------------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Number", "96 numbers")]
    [InlineData("Integer", "96 numbers")]
    [InlineData("Point", "96 points")]
    [InlineData("Curve", "96 curves")]
    [InlineData("Surface", "96 surfaces")]
    [InlineData("Brep", "96 breps")]
    [InlineData("Mesh", "96 meshes")]
    [InlineData("Plane", "96 planes")]
    [InlineData("Vector", "96 vectors")]
    [InlineData("Boolean", "96 booleans")]
    [InlineData("Text", "96 texts")]
    [InlineData("Colour", "96 colours")]
    [InlineData("Domain", "96 domains")]
    [InlineData("Generic Data", "96 items")]
    [InlineData("Geometry", "96 items")]
    [InlineData(null, "96 items")]
    public void One_list_takes_its_noun_from_the_type_hint_or_says_items(string hint, string words) =>
      Assert.Equal(words, TreeWords.For(Sig(1, 1, 96), Live(96), Hint(hint)));

    [Theory]
    [InlineData("Surface", "1 surface")]
    [InlineData("Number", "1 number")]
    [InlineData("Domain", "1 domain")]
    [InlineData("Domain²", "1 domain")]
    [InlineData("Generic Data", "1 item")]
    public void One_item_says_one_of_it(string hint, string words) =>
      Assert.Equal(words, TreeWords.For(Sig(1, 1, 1), Live(1), Hint(hint)));

    [Fact]
    public void A_nickname_or_output_name_gives_a_noun_in_the_plural()
    {
      Assert.Equal("96 panels", TreeWords.For(Sig(1, 1, 96), Live(96), new WireSource { Nick = "Facade panels", TypeHint = "Generic Data" }));
      Assert.Equal("96 points", TreeWords.For(Sig(1, 1, 96), Live(96), new WireSource { OutputName = "Points", TypeHint = "Generic Data" }));
      Assert.Equal("1 panel", TreeWords.For(Sig(1, 1, 1), Live(1), new WireSource { Nick = "Panels" }));
    }

    /// <summary>The yellow Panel component is called Panel and holds text. A singular name says nothing about what is on the wire, or every panel's text would read as panels.</summary>
    [Fact]
    public void A_singular_name_gives_no_noun_and_a_name_does_not_beat_the_persons_nick()
    {
      Assert.Equal("3 texts", TreeWords.For(Sig(1, 1, 3), Live(3), new WireSource { OutputName = "Panel", TypeHint = "Text" }));
      Assert.Equal("3 curves", TreeWords.For(Sig(1, 1, 3), Live(3), new WireSource { Nick = "Curves", OutputName = "Points", TypeHint = "Number" }));
      Assert.Equal("3 items", TreeWords.For(Sig(1, 1, 3), Live(3), new WireSource { Nick = "Spots", OutputName = "Thing" }));
    }

    [Fact]
    public void Equal_branches_are_lists_of_a_number()
    {
      Assert.Equal("12 lists of 8", TreeWords.For(Sig(12, 2, Enumerable.Repeat(8, 12).ToArray()), Live(96), Hint("Point")));
      Assert.Equal("3 lists of 1", TreeWords.For(Sig(3, 2, 1, 1, 1), Live(3), Hint("Point")));
    }

    [Fact]
    public void Mixed_branches_say_their_range()
    {
      Assert.Equal("12 lists, 8 to 11 each", TreeWords.For(Sig(12, 2, 8, 8, 9, 9, 10, 10, 11, 11, 8, 9, 10, 11), Live(116), Hint("Point")));
      Assert.Equal("2 lists, 0 to 4 each", TreeWords.For(Sig(2, 1, 0, 4), Live(4), Hint("Point")));
    }

    /// <summary>The signature lists the first 24 branches only, so past them a claim about every branch needs the live total behind it, or it is not made.</summary>
    [Fact]
    public void Past_the_first_24_branches_the_words_claim_only_what_the_facts_support()
    {
      var first24 = Enumerable.Repeat(8, 24).ToArray();
      Assert.Equal("30 lists of 8", TreeWords.For(Sig(30, 2, first24), Live(240), Hint("Point")));
      Assert.Equal("30 lists, 300 items", TreeWords.For(Sig(30, 2, first24), Live(300), Hint("Point")));

      var mixed = Enumerable.Range(0, 24).Select(i => i % 2 == 0 ? 8 : 9).ToArray();
      Assert.Equal("30 lists, 260 items", TreeWords.For(Sig(30, 2, mixed), Live(260), Hint("Point")));
    }

    [Fact]
    public void A_big_number_reads_the_same_on_every_machine() =>
      Assert.Equal("1,200 points", TreeWords.For(Sig(1, 1, 1200), Live(1200), Hint("Point")));

    /// <summary>The mock-up's best case: Dispatch takes 96 and sends 58 out of this port.</summary>
    [Fact]
    public void A_filter_says_how_much_of_its_list_went_on()
    {
      var counts = new WireCounts { Total = 58, OfSource = 96 };
      Assert.Equal("58 of 96", TreeWords.For(Sig(1, 1, 58), counts, Hint("Surface")));
    }

    [Fact]
    public void A_filter_that_passes_everything_on_is_an_ordinary_count()
    {
      var counts = new WireCounts { Total = 96, OfSource = 96 };
      Assert.Equal("96 surfaces", TreeWords.For(Sig(1, 1, 96), counts, Hint("Surface")));
    }

    /// <summary>The table is what keeps "1 of 96" off a component whose output is a new thing.</summary>
    [Theory]
    [InlineData("Dispatch", true)]
    [InlineData("Cull Pattern", true)]
    [InlineData("Cull Index", true)]
    [InlineData("Cull Nth", true)]
    [InlineData("List Item", true)]
    [InlineData("Shift List", true)]
    [InlineData("Sift Pattern", true)]
    [InlineData("Split List", true)]
    [InlineData("Sub List", true)]
    [InlineData("Random Reduce", true)]
    [InlineData("List Length", false)]
    [InlineData("Bounds", false)]
    [InlineData("Area", false)]
    [InlineData(null, false)]
    public void What_counts_as_a_filter(string type, bool filter) => Assert.Equal(filter, TreeWords.IsFilter(type));

    [Fact]
    public void Nothing_on_the_wire_says_nothing()
    {
      Assert.Equal(TreeWords.Nothing, TreeWords.For(Sig(1, 1, 0), Live(0), Hint("Point")));
      Assert.Equal(TreeWords.Nothing, TreeWords.For(null, Live(0), Hint("Point")));
      Assert.Equal("nothing", TreeWords.Nothing);
    }

    [Fact]
    public void A_source_that_has_not_solved_says_no_data_yet()
    {
      var counts = new WireCounts { Solved = false };
      Assert.Equal("no data yet", TreeWords.For(null, counts, Hint("Point")));
    }

    [Fact]
    public void An_error_on_the_source_says_stopped_with_Grasshoppers_own_first_words()
    {
      var counts = new WireCounts { SourceError = "Input parameter Curve failed to collect data and more words after it", Total = 96, Solved = false };
      Assert.Equal("stopped: Input parameter Curve failed to collect", TreeWords.For(Sig(1, 1, 96), counts, Hint("Point")));
    }

    [Fact]
    public void An_errors_words_are_verbatim_one_line_and_bounded()
    {
      Assert.Equal("Bad thing", TreeWords.FirstWords("  Bad thing\r\nmore lines"));
      Assert.Equal("One two three four five six", TreeWords.FirstWords("One  two three four\tfive six seven"));
      Assert.Equal(new string('x', 48), TreeWords.FirstWords(new string('x', 90)));
      Assert.Equal("", TreeWords.FirstWords(null));
    }

    /// <summary>An error beats the count, the count beats nothing, and "not solved" beats an empty read: the order the words are asked in is the order a person needs them.</summary>
    [Fact]
    public void The_order_of_the_rules()
    {
      Assert.StartsWith("stopped: ", TreeWords.For(null, new WireCounts { SourceError = "x", Solved = false }, null));
      Assert.Equal("no data yet", TreeWords.For(Sig(1, 1, 3), new WireCounts { Solved = false, Total = 3 }, null));
      Assert.Equal("nothing", TreeWords.For(Sig(1, 1, 3), new WireCounts { Total = 0 }, null));
    }

    [Fact]
    public void No_counts_at_all_is_nothing_and_never_a_crash() => Assert.Equal(TreeWords.Nothing, TreeWords.For(null, null, null));

    /// <summary>A count with no signature behind it has no shape to say, so it says nothing rather than guessing one, and the read that found it cannot crash a paint.</summary>
    [Fact]
    public void A_count_with_no_signature_is_nothing()
    {
      Assert.Equal(TreeWords.Nothing, TreeWords.For(null, Live(5), Hint("Point")));
      Assert.Equal(TreeWords.Nothing, TreeWords.For(new TreeSigDto { Paths = 0 }, Live(5), Hint("Point")));
    }

    [Theory]
    [InlineData(1, 1, "Item")]
    [InlineData(1, 96, "List")]
    [InlineData(12, 96, "Tree")]
    [InlineData(1, 0, "None")]
    public void The_glyph_is_the_shape(int paths, long total, string shape) =>
      Assert.Equal(shape, TreeWords.ShapeOf(new TreeSigDto { Paths = paths, Depth = 1, Items = new[] { (int)total } }, total).ToString());

    [Fact]
    public void No_signature_has_no_glyph() => Assert.Equal(ChipShape.None, TreeWords.ShapeOf(null, 0));

    // ---- where it stops -------------------------------------------------------------------------------------------------------------------------

    private static LensNode Dispatch(int a = 58, int b = 38, int aRecipients = 1, int bRecipients = 0) => new LensNode
    {
      Guid = Guid.NewGuid(),
      Type = "Dispatch",
      Label = "Dispatch (23)",
      Outputs =
      {
        new LensOutput { Guid = Guid.NewGuid(), Port = "A", Count = a, Recipients = aRecipients, Source = Hint("Generic Data") },
        new LensOutput { Guid = Guid.NewGuid(), Port = "B", Count = b, Recipients = bRecipients, Source = Hint("Generic Data") },
      },
    };

    [Fact]
    public void A_splitters_output_nothing_reads_is_said_and_its_click_selects_the_splitter_and_rings_the_port()
    {
      var node = Dispatch();
      var stop = Assert.Single(TreeStops.Find(new[] { node }));

      Assert.Equal("38 items leave port B of Dispatch (23), and port B connects to nothing.", stop.Text);
      Assert.Equal(new[] { node.Guid }, stop.Select);
      Assert.Equal(node.Outputs[1].Guid, stop.Port);
      Assert.Equal("Select Dispatch (23)", stop.Action);
    }

    [Fact]
    public void The_noun_on_a_dead_end_is_the_chips_noun()
    {
      var node = Dispatch();
      node.Outputs[1].Source = new WireSource { Nick = "Panels" };
      Assert.StartsWith("38 panels leave port B", TreeStops.Find(new[] { node })[0].Text);
    }

    [Fact]
    public void A_splitter_with_everything_read_says_nothing() => Assert.Empty(TreeStops.Find(new[] { Dispatch(bRecipients: 2) }));

    [Fact]
    public void An_empty_output_nothing_reads_is_not_a_stop() => Assert.Empty(TreeStops.Find(new[] { Dispatch(b: 0) }));

    /// <summary>Move's transform, Area's centroid, the end of every chain. True of nearly every component, and a line on each would teach a person to skip the lines.</summary>
    [Fact]
    public void Another_components_unread_output_is_not_a_stop()
    {
      var move = new LensNode
      {
        Guid = Guid.NewGuid(), Type = "Move", Label = "Move",
        Outputs = { new LensOutput { Guid = Guid.NewGuid(), Port = "X", Count = 96, Recipients = 0, Source = Hint("Transform") } },
      };
      Assert.Empty(TreeStops.Find(new[] { move }));
    }

    [Theory]
    [InlineData("Dispatch", true)]
    [InlineData("Sift Pattern", true)]
    [InlineData("Split List", true)]
    [InlineData("Split Tree", true)]
    [InlineData("Cull Pattern", false)]
    [InlineData("Move", false)]
    [InlineData(null, false)]
    public void What_counts_as_a_splitter(string type, bool splitter) => Assert.Equal(splitter, TreeStops.IsSplitter(type));

    private const string CollectWarning = "Input parameter Base failed to collect data";

    private static LensNode Extrude(string warning = CollectWarning, params LensInput[] inputs)
    {
      var n = new LensNode { Guid = Guid.NewGuid(), Type = "Extrude", Label = "Extrude (14)" };
      n.Inputs.AddRange(inputs);
      if (warning != null) n.Warnings.Add(warning);
      return n;
    }

    [Fact]
    public void An_unwired_input_that_was_needed_forwards_Grasshoppers_own_warning()
    {
      var node = Extrude(inputs: new LensInput { Port = "B", Name = "Base", Optional = false, Count = 0 });
      var stop = Assert.Single(TreeStops.Find(new[] { node }));

      Assert.Equal("Extrude (14), input B has no source. Grasshopper says: " + CollectWarning, stop.Text);
      Assert.Equal(new[] { node.Guid }, stop.Select);
      Assert.Equal("Select Extrude (14)", stop.Action);
    }

    [Fact]
    public void The_warning_forwarded_is_the_one_naming_the_input()
    {
      var node = Extrude(inputs: new LensInput { Port = "D", Name = "Direction", Count = 0 });
      node.Warnings.Clear();
      node.Warnings.Add("Input parameter Base failed to collect data");
      node.Warnings.Add("Input parameter Direction failed to collect data");
      Assert.EndsWith("Grasshopper says: Input parameter Direction failed to collect data", TreeStops.Find(new[] { node })[0].Text);
    }

    /// <summary>A one-letter port is not named by the same letter inside a word: the d in "failed" is not port D.</summary>
    [Fact]
    public void A_port_is_named_by_its_whole_word_only()
    {
      var node = Extrude(inputs: new LensInput { Port = "D", Name = "Other", Count = 0 });
      node.Warnings.Clear();
      node.Warnings.Add("Something failed");
      node.Warnings.Add("Input D is needed");
      Assert.EndsWith("Grasshopper says: Input D is needed", TreeStops.Find(new[] { node })[0].Text);
    }

    [Fact]
    public void An_input_that_has_what_it_needs_says_nothing()
    {
      Assert.Empty(TreeStops.Find(new[] { Extrude(inputs: new LensInput { Port = "B", Name = "Base", Optional = true, Count = 0 }) }));       // optional
      Assert.Empty(TreeStops.Find(new[] { Extrude(inputs: new LensInput { Port = "B", Name = "Base", Optional = false, Count = 3 }) }));      // held data of its own
      Assert.Empty(TreeStops.Find(new[] { Extrude(warning: null, inputs: new LensInput { Port = "B", Name = "Base", Count = 0 }) }));         // Grasshopper said nothing, and nor does the lens
    }

    [Fact]
    public void A_wire_carrying_nothing_into_a_component_that_warns_names_both_ends_and_selects_both()
    {
      var from = Guid.NewGuid();
      var node = Extrude(inputs: new LensInput { Port = "B", Name = "Base", Count = 0, Feeds = { new LensFeed { Node = from, Label = "Dispatch (23)", Count = 0 } } });
      var stop = Assert.Single(TreeStops.Find(new[] { node }));

      Assert.Equal("Nothing reaches input B of Extrude (14) from Dispatch (23). Grasshopper says: " + CollectWarning, stop.Text);
      Assert.Equal(new[] { from, node.Guid }, stop.Select);
      Assert.Equal("Select Dispatch (23) and Extrude (14)", stop.Action);
    }

    [Fact]
    public void A_wire_carrying_something_is_not_a_stop_and_nor_is_an_empty_wire_with_no_warning()
    {
      Assert.Empty(TreeStops.Find(new[] { Extrude(inputs: new LensInput { Port = "B", Name = "Base", Count = 4, Feeds = { new LensFeed { Node = Guid.NewGuid(), Label = "x", Count = 4 } } }) }));
      Assert.Empty(TreeStops.Find(new[] { Extrude(warning: null, inputs: new LensInput { Port = "B", Name = "Base", Count = 0, Feeds = { new LensFeed { Node = Guid.NewGuid(), Label = "x", Count = 0 } } }) }));
    }

    /// <summary>A component that stopped and has no wire out of it has no chip to say so, so the line is what says where the error is happening.</summary>
    [Fact]
    public void An_error_is_said_with_Grasshoppers_own_words()
    {
      var node = new LensNode { Guid = Guid.NewGuid(), Type = "Loft", Label = "Loft", Errors = { "Curves are not compatible for lofting", "another" } };
      var stop = Assert.Single(TreeStops.Find(new[] { node }));
      Assert.Equal("Loft stopped: Curves are not compatible for lofting", stop.Text);
      Assert.Equal(new[] { node.Guid }, stop.Select);
    }

    [Fact]
    public void Past_five_lines_the_last_says_how_many_it_did_not_name_and_selects_nothing()
    {
      var nodes = Enumerable.Range(0, 7).Select(_ => Dispatch()).ToList();
      var stops = TreeStops.Find(nodes);

      Assert.Equal(TreeStops.MaxLines + 1, stops.Count);
      Assert.Equal("And 2 more lines like these in what is lit.", stops.Last().Text);
      Assert.Empty(stops.Last().Select);
      Assert.Null(stops.Last().Action);
      Assert.Equal("And 1 more line like these in what is lit.", TreeStops.Find(Enumerable.Range(0, 6).Select(_ => Dispatch())).Last().Text);
      Assert.Equal(TreeStops.MaxLines, TreeStops.Find(Enumerable.Range(0, 5).Select(_ => Dispatch())).Count);
    }

    [Fact]
    public void Nothing_read_is_nothing_said()
    {
      Assert.Empty(TreeStops.Find(null));
      Assert.Empty(TreeStops.Find(new LensNode[] { null }));
    }

    // ---- what a surface is handed ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_view_carries_written_words_and_only_a_line_with_something_to_select_has_an_action()
    {
      var view = JsonNode.Parse(LensView.Json(2, new[]
      {
        new LensStop { Text = "A line.", Action = "Select X" },
        new LensStop { Text = "And 1 more line like these in what is lit." },
      }, LensCap.StepOnlyLine));

      Assert.Equal(2, view["step"].GetValue<int>());
      Assert.Equal("A line.", view["lines"][0]["text"].GetValue<string>());
      Assert.Equal("Select X", view["lines"][0]["action"].GetValue<string>());
      Assert.Null(view["lines"][1]["action"]);
      Assert.Equal(LensCap.StepOnlyLine, view["note"].GetValue<string>());
    }

    /// <summary>A live lens offers its one control, whichever way round it is; a surface with no lens has nothing to offer.</summary>
    [Fact]
    public void The_view_offers_the_toggle_only_while_a_lens_is_live()
    {
      Assert.False(JsonNode.Parse(LensView.Json(-1, null, null, live: true, showsAll: false))["everywire"].GetValue<bool>());
      Assert.True(JsonNode.Parse(LensView.Json(-1, null, null, live: true, showsAll: true))["everywire"].GetValue<bool>());
      Assert.Null(JsonNode.Parse(LensView.Json(-1, null, null))["everywire"]);
    }

    [Fact]
    public void A_view_with_no_cap_has_no_note() => Assert.Null(JsonNode.Parse(LensView.Json(-1, null, null))["note"]);

    // ---- where a chip goes ----------------------------------------------------------------------------------------------------------------------

    private static readonly LensRect Left = new LensRect(0, 100, 100, 40);
    private static readonly LensRect Right = new LensRect(400, 120, 100, 40);

    [Fact]
    public void A_chip_sits_at_its_wires_midpoint()
    {
      var at = ChipPlace.Place(100, 120, 400, 140, Left, Right, 60, 12, out var lifted);

      Assert.False(lifted);
      Assert.Equal(new LensRect(220, 124, 60, 12), at);
    }

    /// <summary>The mock-up's fix: a short wire has no room for its words, so the chip goes above the row of the two components it joins.</summary>
    [Fact]
    public void A_chip_on_a_short_wire_lifts_above_the_row()
    {
      var near = new LensRect(130, 90, 100, 40);
      var at = ChipPlace.Place(100, 120, 130, 110, Left, near, 60, 12, out var lifted);

      Assert.True(lifted);
      Assert.Equal(115 - 30, at.X);
      Assert.Equal(90 - ChipPlace.Lift - 12, at.Y);
      Assert.False(at.Overlaps(Left));
      Assert.False(at.Overlaps(near));
    }

    [Fact]
    public void A_wire_just_long_enough_keeps_its_chip_and_one_shorter_lifts_it()
    {
      var w = 60.0;
      var room = w + 2 * ChipPlace.Margin;
      ChipPlace.Place(100, 300, 100 + room, 300, Left, new LensRect(100 + room, 500, 10, 10), w, 12, out var fits);
      Assert.False(fits);
      ChipPlace.Place(100, 300, 100 + room - 1, 300, Left, new LensRect(100 + room, 500, 10, 10), w, 12, out var tight);
      Assert.True(tight);
    }

    /// <summary>A long wire whose midpoint lands on a component would cover it.</summary>
    [Fact]
    public void A_chip_that_would_cover_a_component_lifts()
    {
      var under = new LensRect(240, 100, 40, 60);
      var at = ChipPlace.Place(100, 120, 400, 140, under, Right, 60, 12, out var lifted);
      Assert.True(lifted);
      Assert.False(at.Overlaps(under));
    }

    [Fact]
    public void Chips_landing_on_one_another_stack_upward()
    {
      var first = new LensRect(100, 100, 60, 12);
      var second = ChipPlace.ClearOf(new LensRect(110, 100, 60, 12), new[] { first });
      Assert.False(second.Overlaps(first));
      Assert.Equal(100 - 12 - ChipPlace.StackGap, second.Y);
      Assert.Equal(110, second.X);

      var third = ChipPlace.ClearOf(new LensRect(110, 100, 60, 12), new[] { first, second });
      Assert.False(third.Overlaps(first));
      Assert.False(third.Overlaps(second));
    }

    [Fact]
    public void A_chip_clear_of_the_rest_stays_where_it_is()
    {
      var chip = new LensRect(500, 500, 60, 12);
      Assert.Equal(chip, ChipPlace.ClearOf(chip, new[] { new LensRect(0, 0, 60, 12) }));
      Assert.Equal(chip, ChipPlace.ClearOf(chip, null));
    }

    /// <summary>A tower of chips no longer leaves the last one on the tower: past the bound it goes to the other side, which here is free.</summary>
    [Fact]
    public void A_crowd_is_bounded_so_a_paint_can_never_loop()
    {
      var tower = Enumerable.Range(0, 100).Select(i => new LensRect(100, 100 - i * 15.0, 60, 12)).ToList();
      var at = ChipPlace.ClearOf(new LensRect(100, 100, 60, 12), tower);
      Assert.Equal(100 + 12 + ChipPlace.StackGap, at.Y, 6);
      Assert.DoesNotContain(tower, o => o.Overlaps(at));
    }

    // ---- the fifteenth gate: a chip never covers a badge, a port's label or another chip ----------------------------------------------

    private const double ChipW = 70, ChipH = 17;

    /// <summary>The same curve the host draws, from CanvasLook.WireControls.</summary>
    private static ChipWire WireOf(double ax, double ay, double bx, double by, LensRect fromBox, LensRect toBox, bool fromSelected = true, bool toSelected = true)
    {
      var bend = Math.Max(Math.Abs(bx - ax) * 0.5, 18);
      return new ChipWire
      {
        FromX = ax, FromY = ay, ToX = bx, ToY = by, FromBox = fromBox, ToBox = toBox, FromSelected = fromSelected, ToSelected = toSelected,
        Points = ChipPlace.WirePoints(ax, ay, ax + bend, ay, bx - bend, by, bx, by),
      };
    }

    /// <summary>The gate's shape, measured from its capture: a column of sliders, short wires into one component, and its badge just over its corner. The sequence is the host's: every chip seated in order, then the lines.</summary>
    private static (List<LensRect> Seats, List<LensRect> Off, List<ChipWire> Wires) ShortWires(int wires)
    {
      var target = new LensRect(750, 308, 66, Math.Max(45, wires * 20 + 5));
      var badge = new LensRect(748, 294, 14, 13);
      var sliders = Enumerable.Range(0, wires).Select(i => new LensRect(560, 300 + i * 45.0, 162, 20)).ToList();
      var off = new List<LensRect>(sliders) { target, badge };
      var seats = new List<LensRect>();
      var list = new List<ChipWire>();
      for (var i = 0; i < wires; i++)
      {
        var wire = WireOf(sliders[i].Right, sliders[i].Y + 10, target.X, target.Y + 12 + i * 20.0, sliders[i], target);
        list.Add(wire);
        seats.Add(ChipPlace.Seat(wire, ChipW, ChipH, seats, off));
      }
      return (seats, off, list);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(12)]
    public void Short_wires_into_one_component_cover_no_badge_label_or_chip(int wires)
    {
      var (seats, off, _) = ShortWires(wires);
      Assert.Equal(wires, seats.Count);
      foreach (var seat in seats)
        Assert.DoesNotContain(off, o => o.Overlaps(seat));
      for (var i = 0; i < wires; i++)
        for (var j = i + 1; j < wires; j++)
          Assert.False(seats[i].Overlaps(seats[j]), "chips " + i + " and " + j);
    }

    /// <summary>The architect's read of the first drawing: clear of everything, and it told the eye nothing, with chip 12 twelve rows from its wire. A chip belongs to its wire.</summary>
    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(12)]
    public void Each_chip_sits_within_reach_of_its_own_wire(int wires)
    {
      var (seats, _, list) = ShortWires(wires);
      for (var i = 0; i < wires; i++)
        Assert.True(list[i].Points.Min(p => ChipPlace.Distance(seats[i], p.X, p.Y)) <= ChipPlace.Reach, "chip " + i);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(12)]
    public void A_chips_line_goes_to_its_own_wire_and_crosses_no_other_chip(int wires)
    {
      var (seats, _, list) = ShortWires(wires);
      for (var i = 0; i < wires; i++)
      {
        var others = seats.Where((_, j) => j != i).ToList();
        var line = ChipPlace.Leader(seats[i], list[i].Points, others);
        if (line == null) continue;
        Assert.Contains(line[line.Count - 1], list[i].Points);
        for (var k = 1; k < line.Count; k++)
          Assert.DoesNotContain(others, o => ChipPlace.Crosses(o, line[k - 1].X, line[k - 1].Y, line[k].X, line[k].Y));
      }
    }

    // ---- the sixteenth gate's geometry, read live: the destination stands against the sources' ends, and every wire runs into one grip ----

    /// <summary>
    /// Read through the bridge on the sixteenth build: sliders 150 wide and 20 tall, 38 apart at x 0; a Mass Addition 64 by 44 at x 163, centred on the column; its one input grip at x 165, 2 inside its edge; its two output
    /// rows 20 by 20 at x 205, holding R and Pr. A chip measures 69 by 16.3. Each slider wears a badge, and so does the component.
    /// </summary>
    private static (List<LensRect> Seats, List<LensRect> Off, List<LensRect> Labels, List<ChipWire> Wires, LensRect Body) GateWires(int wires)
    {
      const double w = 68.8, h = 16.25;
      var middle = wires * 38 / 2.0 - 20;
      var body = new LensRect(163, middle - 22, 64, 44);
      var sliders = Enumerable.Range(0, wires).Select(i => new LensRect(0, i * 38.0, 150, 20)).ToList();
      var off = new List<LensRect>(sliders) { body, new LensRect(161, body.Y - 14, 14, 13) };
      foreach (var slider in sliders) off.Add(new LensRect(slider.X - 2, slider.Y - 14, 14, 13));
      var labels = new List<LensRect>
      {
        ChipPlace.LabelIn(new LensRect(165, middle - 20, 10, 40), 5, 12),
        ChipPlace.LabelIn(new LensRect(205, middle - 20, 20, 20), 9, 12),
        ChipPlace.LabelIn(new LensRect(205, middle, 20, 20), 14, 12),
      };
      var seats = new List<LensRect>();
      var list = new List<ChipWire>();
      foreach (var slider in sliders)
      {
        var wire = WireOf(slider.Right, slider.Y + 10, 165, middle, slider, body);
        list.Add(wire);
        seats.Add(ChipPlace.Seat(wire, w, h, seats, off));
      }
      return (seats, off, labels, list, body);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(12)]
    public void At_the_gates_geometry_no_chip_covers_a_badge_a_body_or_a_chip(int wires)
    {
      var (seats, off, _, _, _) = GateWires(wires);
      foreach (var seat in seats)
        Assert.DoesNotContain(off, o => o.Overlaps(seat));
      for (var i = 0; i < wires; i++)
        for (var j = i + 1; j < wires; j++)
          Assert.False(seats[i].Overlaps(seats[j]), "chips " + i + " and " + j);
    }

    /// <summary>The law as ruled: every chip within 40 px of its own wire, or lined to it. A chip without a line is a chip without a meaning.</summary>
    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(12)]
    public void At_the_gates_geometry_every_chip_is_within_reach_of_its_wire_or_lined_to_it(int wires)
    {
      var (seats, _, labels, list, _) = GateWires(wires);
      for (var i = 0; i < wires; i++)
      {
        var away = list[i].Points.Min(p => ChipPlace.Distance(seats[i], p.X, p.Y));
        var line = ChipPlace.Leader(seats[i], list[i].Points, seats.Where((_, j) => j != i), labels);
        Assert.True(away <= ChipPlace.Reach || line != null, "chip " + i + " is " + away + " away with no line");
        if (away > ChipPlace.Margin) Assert.NotNull(line);
      }
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(12)]
    public void At_the_gates_geometry_no_line_crosses_a_chip_or_a_label_and_each_ends_on_its_own_wire(int wires)
    {
      var (seats, _, labels, list, _) = GateWires(wires);
      for (var i = 0; i < wires; i++)
      {
        var others = seats.Where((_, j) => j != i).ToList();
        var line = ChipPlace.Leader(seats[i], list[i].Points, others, labels);
        if (line == null) continue;
        Assert.Contains(line[line.Count - 1], list[i].Points);
        Assert.InRange(line.Count, 2, 3);   // at most one bend
        for (var k = 1; k < line.Count; k++)
        {
          Assert.DoesNotContain(others, o => ChipPlace.Crosses(o, line[k - 1].X, line[k - 1].Y, line[k].X, line[k].Y));
          Assert.DoesNotContain(labels, o => ChipPlace.Crosses(o, line[k - 1].X, line[k - 1].Y, line[k].X, line[k].Y));
        }
      }
    }

    /// <summary>The gate's two middle wires: their sources sit against the component's body, so their chips go to its far side, each level with its own wire's row. They were 239 and 257 px away.</summary>
    [Theory]
    [InlineData(4, 1, 2)]
    [InlineData(8, 3, 4)]
    [InlineData(12, 5, 6)]
    public void At_the_gates_geometry_the_middle_chips_go_to_the_far_side_level_with_their_rows(int wires, int first, int second)
    {
      var (seats, _, _, list, body) = GateWires(wires);
      foreach (var i in new[] { first, second })
      {
        Assert.Equal(body.Right + ChipPlace.Margin, seats[i].X);
        Assert.Equal(list[i].FromY - 16.25 / 2, seats[i].Y, 6);
      }
      for (var i = 0; i < wires; i++)
        if (i != first && i != second) Assert.Equal(list[i].FromX + ChipPlace.Margin, seats[i].X);   // the rest sit right of their own grips
      Assert.DoesNotContain(seats, s => s.Y < 0);   // none in the stack above the group
    }

    [Fact]
    public void The_far_side_is_only_for_a_source_end_blocked_by_the_destinations_own_body()
    {
      // blocked by something else: on to the destination's own places
      var (from, to) = (new LensRect(0, 100, 100, 20), new LensRect(400, 100, 100, 20));
      var other = new LensRect(110, 100, 100, 20);
      var at = ChipPlace.Seat(WireOf(100, 110, 400, 110, from, to), 60, 12, null, new[] { OnMiddle, other, SourceAbove, SourceBelow });
      Assert.Equal(DestLeft, at);

      // and only on a wire inside the selection
      var near = new LensRect(110, 90, 64, 44);
      var leaving = ChipPlace.Seat(WireOf(100, 110, 112, 112, from, near, true, false), 60, 12, null, new[] { near, SourceAbove, SourceBelow });
      Assert.True(leaving.X < near.Right, "a wire leaving the selection keeps its chip at the selected end");
    }

    [Fact]
    public void A_far_side_already_taken_goes_on_to_the_destinations_own_places()
    {
      var from = new LensRect(0, 100, 100, 20);
      var body = new LensRect(110, 90, 64, 40);
      var wire = WireOf(100, 110, 112, 110, from, body);
      var taken = new LensRect(180, 104, 60, 12);
      var at = ChipPlace.Seat(wire, 60, 12, new[] { taken }, new[] { from, body, new LensRect(40, 85, 60, 12), new LensRect(40, 123, 60, 12) });
      Assert.False(at.Overlaps(taken));
      Assert.Equal(112, at.X);    // over the destination, within reach of its grip
    }

    // ---- the box, from the live read ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// THE CAUSE OF THE GATE'S STACK. A port's GetTopLevel is not its component's live attributes: read on the sixteenth build, its Bounds were (0, 0, 150, 20) while the component stood at (163, 34, 64, 44). Every use of
    /// GetTopLevel in the plugin reads its DocObject, which is right, and the one that read its Bounds put every destination in the wrong place. No GH here, so this holds the source, across the whole plugin.
    /// </summary>
    [Fact]
    public void Nothing_reads_bounds_from_a_ports_top_level_attributes()
    {
      var dir = Path.Combine(EdgeRepo(), "Dwight");
      foreach (var file in Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories))
      {
        var text = File.ReadAllText(file);
        Assert.False(text.Contains("GetTopLevel?.Bounds") || text.Contains("GetTopLevel.Bounds"), file);
      }
      Assert.Contains("(attributes.GetTopLevel?.DocObject?.Attributes ?? attributes).Bounds", File.ReadAllText(Path.Combine(dir, "UI", "TreeLens.cs")));
    }

    // ---- the search's order ---------------------------------------------------------------------------------------------------------------------

    private static readonly LensRect SourceBox = new LensRect(0, 100, 100, 20);
    private static readonly LensRect DestBox = new LensRect(400, 100, 100, 20);
    private static readonly LensRect OnMiddle = new LensRect(220, 104, 60, 12);
    private static readonly LensRect SourceRight = new LensRect(106, 104, 60, 12);
    private static readonly LensRect SourceAbove = new LensRect(40, 85, 60, 12);
    private static readonly LensRect SourceBelow = new LensRect(40, 123, 60, 12);
    private static readonly LensRect DestLeft = new LensRect(334, 104, 60, 12);
    private static readonly LensRect DestAbove = new LensRect(400, 85, 60, 12);
    private static readonly LensRect DestBelow = new LensRect(400, 123, 60, 12);

    private static LensRect SeatLong(bool fromSelected, bool toSelected, params LensRect[] blocked) =>
      ChipPlace.Seat(WireOf(100, 110, 400, 110, SourceBox, DestBox, fromSelected, toSelected), 60, 12, null, blocked);

    [Fact]
    public void A_wire_that_holds_its_chip_keeps_it_on_the_middle() => Assert.Equal(OnMiddle, SeatLong(true, true));

    [Fact]
    public void Then_beside_the_source_grip_on_its_wire_side_then_over_and_under_the_sources_end()
    {
      Assert.Equal(SourceRight, SeatLong(true, true, OnMiddle));
      Assert.Equal(SourceAbove, SeatLong(true, true, OnMiddle, SourceRight));
      Assert.Equal(SourceBelow, SeatLong(true, true, OnMiddle, SourceRight, SourceAbove));
    }

    [Fact]
    public void Then_beside_the_destination_grip()
    {
      Assert.Equal(DestLeft, SeatLong(true, true, OnMiddle, SourceRight, SourceAbove, SourceBelow));
      Assert.Equal(DestAbove, SeatLong(true, true, OnMiddle, SourceRight, SourceAbove, SourceBelow, DestLeft));
      Assert.Equal(DestBelow, SeatLong(true, true, OnMiddle, SourceRight, SourceAbove, SourceBelow, DestLeft, DestAbove));
    }

    private static readonly LensRect[] BothEnds = { OnMiddle, SourceRight, SourceAbove, SourceBelow, DestLeft, DestAbove, DestBelow };

    [Fact]
    public void Then_along_the_wire_off_the_middle()
    {
      // long enough that blocking its ends leaves room along it
      var far = new LensRect(1000, 100, 100, 20);
      var wire = WireOf(100, 110, 1000, 110, SourceBox, far);
      var blocked = new[]
      {
        new LensRect(520, 104, 60, 12), SourceRight, SourceAbove, SourceBelow,
        new LensRect(934, 104, 60, 12), new LensRect(1000, 85, 60, 12), new LensRect(1000, 123, 60, 12),
      };
      var at = ChipPlace.Seat(wire, 60, 12, null, blocked);
      Assert.Equal(wire.Points[4].X - 30, at.X, 6);      // a quarter along, not the trunk at the middle
      Assert.Equal(wire.Points[4].Y - 6, at.Y, 6);
      Assert.False(at.Overlaps(new LensRect(520, 104, 60, 12)));

      // and the other way along when that is taken
      var next = ChipPlace.Seat(wire, 60, 12, new[] { at }, blocked);
      Assert.Equal(wire.Points[12].X - 30, next.X, 6);
    }

    /// <summary>The trunk the first drawing showed: where short wires share a midpoint, the middle is the one place along the wire a chip must not start from.</summary>
    [Fact]
    public void A_short_wire_with_both_ends_full_tries_along_it_before_its_middle()
    {
      var from = new LensRect(0, 100, 100, 20);
      var to = new LensRect(130, 300, 100, 20);
      var wire = WireOf(100, 110, 130, 310, from, to);
      var blocked = new[]
      {
        new LensRect(106, 104, 60, 12), new LensRect(40, 85, 60, 12), new LensRect(40, 123, 60, 12),
        new LensRect(64, 304, 60, 12), new LensRect(130, 285, 60, 12), new LensRect(130, 323, 60, 12),
      };
      var at = ChipPlace.Seat(wire, 60, 12, null, blocked);
      var middle = wire.Points[wire.Points.Count / 2];
      Assert.NotEqual(middle.Y - 6, at.Y);
      Assert.Equal(wire.Points[4].Y - 6, at.Y, 6);
    }

    /// <summary>The same for the destination: a tall component's top is no place for a chip about a port halfway down it.</summary>
    [Fact]
    public void A_place_beside_the_destination_must_lie_within_reach_of_it_too()
    {
      var tall = new LensRect(400, 0, 100, 400);
      var wire = WireOf(100, 110, 400, 200, SourceBox, tall, fromSelected: false);
      var at = ChipPlace.Seat(wire, 60, 12, null, new[] { new LensRect(334, 194, 60, 12) });
      Assert.NotEqual(-15, at.Y);
      Assert.NotEqual(403, at.Y);
    }

    [Fact]
    public void Only_a_wire_crowded_at_both_ends_and_along_its_length_falls_back_on_the_stack()
    {
      var wire = WireOf(100, 110, 400, 110, SourceBox, DestBox);
      var along = wire.Points.Select(p => new LensRect(p.X - 30, p.Y - 6, 60, 12));
      var at = SeatLong(true, true, BothEnds.Concat(along).ToArray());
      Assert.Equal(104 - (12 + ChipPlace.StackGap), at.Y, 6);       // the stack, from the middle, one row up past what is on the wire
      Assert.Equal(220, at.X, 6);
    }

    [Fact]
    public void A_short_wire_goes_to_its_source_end_and_never_starts_on_the_trunk()
    {
      var near = new LensRect(130, 100, 100, 40);
      var at = ChipPlace.Seat(WireOf(100, 110, 130, 110, SourceBox, near), 60, 12, null, new[] { SourceBox, near });
      Assert.Equal(new LensRect(40, 85, 60, 12), at);   // right of the grip is on the destination, so over the source's end
    }

    [Fact]
    public void A_source_outside_the_selection_is_passed_over()
    {
      Assert.Equal(DestLeft, SeatLong(false, true));
      Assert.Equal(DestAbove, SeatLong(false, true, DestLeft));
    }

    [Fact]
    public void A_wire_leaving_the_selection_keeps_its_chip_at_the_selected_end_whatever_is_there()
    {
      Assert.Equal(SourceRight, SeatLong(true, false));
      var blocked = SeatLong(true, false, SourceRight, SourceAbove, SourceBelow);
      Assert.True(blocked.X < 200, "never along the wire or at its far end");

      Assert.Equal(DestLeft, SeatLong(false, true, OnMiddle));
      var incoming = SeatLong(false, true, DestLeft, DestAbove, DestBelow);
      Assert.True(incoming.X > 300, "never along the wire or at its far end");
    }

    /// <summary>A tall component's top is no place for a chip about a port halfway down it.</summary>
    [Fact]
    public void A_place_beside_an_end_must_lie_within_reach_of_that_end()
    {
      var tall = new LensRect(0, 0, 100, 400);
      var wire = WireOf(100, 200, 400, 200, tall, DestBox);
      var blocked = new[] { new LensRect(220, 194, 60, 12), new LensRect(106, 194, 60, 12) };
      var at = ChipPlace.Seat(wire, 60, 12, null, blocked);
      Assert.NotEqual(-15, at.Y);                        // over the tall source's top
      Assert.NotEqual(403, at.Y);                        // or under it
      Assert.True(ChipPlace.Distance(at, wire.FromX, wire.FromY) > ChipPlace.Reach);
      Assert.True(wire.Points.Min(p => ChipPlace.Distance(at, p.X, p.Y)) <= ChipPlace.Reach);   // it found the destination's end instead
    }

    // ---- the line ------------------------------------------------------------------------------------------------------------------------------

    private static readonly IReadOnlyList<(double X, double Y)> Flat = ChipPlace.WirePoints(100, 110, 200, 110, 300, 110, 400, 110);

    [Fact]
    public void A_chip_on_or_beside_its_wire_needs_no_line()
    {
      Assert.Null(ChipPlace.Leader(new LensRect(220, 104, 60, 12), Flat, null));
      Assert.Null(ChipPlace.Leader(new LensRect(220, 110 + ChipPlace.Margin, 60, 12), Flat, null));
      Assert.NotNull(ChipPlace.Leader(new LensRect(220, 110 + ChipPlace.Margin + 1, 60, 12), Flat, null));
    }

    [Fact]
    public void A_line_goes_straight_to_the_nearest_point_of_its_wire()
    {
      var chip = new LensRect(220, 60, 60, 12);
      var line = ChipPlace.Leader(chip, Flat, null);
      Assert.Equal(2, line.Count);
      Assert.Equal(72, line[0].Y, 6);
      Assert.Equal(110, line[1].Y, 6);
      Assert.Equal(line[0].X, line[1].X, 6);
    }

    [Fact]
    public void A_line_takes_the_next_nearest_point_rather_than_cross_a_chip()
    {
      var chip = new LensRect(220, 60, 60, 12);
      var inTheWay = new LensRect(180, 85, 120, 12);
      var line = ChipPlace.Leader(chip, Flat, new[] { inTheWay });
      Assert.False(ChipPlace.Crosses(inTheWay, line[0].X, line[0].Y, line[line.Count - 1].X, line[line.Count - 1].Y));
      Assert.Equal(110, line[line.Count - 1].Y, 6);
    }

    /// <summary>Withdrawn by the sixteenth gate: a chip with no line had no meaning. The route goes round what is in the way, with one bend.</summary>
    [Fact]
    public void A_line_with_no_straight_way_goes_round_with_one_bend()
    {
      // a short wire right under a wide chip, so every straight line to it meets the chip in the way
      var chip = new LensRect(220, 60, 60, 12);
      var wire = ChipPlace.WirePoints(240, 110, 245, 110, 255, 110, 260, 110);
      var inTheWay = new LensRect(225, 85, 50, 12);
      var line = ChipPlace.Leader(chip, wire, new[] { inTheWay });

      Assert.Equal(3, line.Count);
      Assert.False(ChipPlace.Crosses(inTheWay, line[0].X, line[0].Y, line[1].X, line[1].Y));
      Assert.False(ChipPlace.Crosses(inTheWay, line[1].X, line[1].Y, line[2].X, line[2].Y));
      Assert.Contains(line[2], wire);
    }

    [Fact]
    public void The_bend_taken_is_the_shortest_clear_one()
    {
      var chip = new LensRect(220, 60, 60, 12);
      var wire = ChipPlace.WirePoints(240, 110, 245, 110, 255, 110, 260, 110);
      var inTheWay = new LensRect(230, 85, 100, 12);    // its near end is on the left
      var line = ChipPlace.Leader(chip, wire, new[] { inTheWay });
      Assert.True(line[1].X < 230, "round the near end, not the far one");
      double Leg((double X, double Y) a, (double X, double Y) b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
      Assert.True(Leg(line[0], line[1]) + Leg(line[1], line[2]) < 50, "the shortest way round is about 44");
    }

    [Fact]
    public void A_line_keeps_off_a_label_as_it_does_a_chip_and_may_cross_a_body()
    {
      var chip = new LensRect(220, 60, 60, 12);
      var label = new LensRect(230, 85, 40, 12);
      var withoutLabel = ChipPlace.Leader(chip, Flat, null);
      Assert.True(ChipPlace.Crosses(label, withoutLabel[0].X, withoutLabel[0].Y, withoutLabel[1].X, withoutLabel[1].Y));

      var line = ChipPlace.Leader(chip, Flat, null, new[] { label });
      Assert.False(ChipPlace.Crosses(label, line[0].X, line[0].Y, line[line.Count - 1].X, line[line.Count - 1].Y));
    }

    /// <summary>A crowd with no route at all still gets its line: the ruling is that a chip without one has no meaning.</summary>
    [Fact]
    public void With_every_route_closed_a_chip_still_has_its_straight_line()
    {
      var chip = new LensRect(220, 60, 60, 12);
      var line = ChipPlace.Leader(chip, Flat, new[] { new LensRect(-5000, 85, 10000, 12) });
      Assert.Equal(2, line.Count);
      Assert.Equal(110, line[1].Y, 6);
    }

    [Fact]
    public void With_no_wire_to_reach_there_is_no_line() => Assert.Null(ChipPlace.Leader(new LensRect(220, 60, 60, 12), null, null));

    [Fact]
    public void A_ports_label_is_its_words_centred_in_its_row()
    {
      var label = ChipPlace.LabelIn(new LensRect(205, 36, 20, 20), 9, 12);
      Assert.Equal(new LensRect(210.5, 40, 9, 12), label);
    }

    [Theory]
    [InlineData(0, 50, 100, 50, true)]      // through
    [InlineData(40, 50, 60, 50, true)]      // inside
    [InlineData(0, 0, 100, 100, true)]      // across a corner to corner
    [InlineData(0, 10, 100, 10, false)]     // above
    [InlineData(0, 50, 30, 50, false)]      // stops short
    [InlineData(35, 50, 35, 90, false)]     // beside
    [InlineData(0, 40, 200, 40, true)]      // through, past it
    [InlineData(90, 50, 120, 50, false)]    // past its far side
    [InlineData(50, 80, 50, 100, false)]    // under it
    public void A_straight_line_crosses_a_rectangle_or_it_does_not(double x1, double y1, double x2, double y2, bool crosses) =>
      Assert.Equal(crosses, ChipPlace.Crosses(new LensRect(40, 30, 40, 40), x1, y1, x2, y2));

    [Fact]
    public void The_points_run_the_drawn_curve_from_source_to_destination()
    {
      var points = ChipPlace.WirePoints(0, 0, 50, 0, 50, 100, 100, 100);
      Assert.Equal((0.0, 0.0), points[0]);
      Assert.Equal((100.0, 100.0), points[points.Count - 1]);
      Assert.Equal(50, points[points.Count / 2].X, 6);
      Assert.Equal(50, points[points.Count / 2].Y, 6);
    }

    // ---- the stack, the last resort -----------------------------------------------------------------------------------------------------------

    private static readonly LensRect First = new LensRect(100, 100, 60, 12);
    private static double Row => 12 + ChipPlace.StackGap;

    [Fact]
    public void A_first_row_on_a_badge_moves_up_past_it()
    {
      var badge = new LensRect(110, 103, 14, 13);
      var at = ChipPlace.ClearOf(First, null, new[] { badge }, 300);
      Assert.Equal(100 - Row, at.Y);
      Assert.Equal(100, at.X);

      // one that reaches into the next row too: it still goes up past it, because no stack of chips has reached it
      var taller = new LensRect(110, 95, 14, 13);
      Assert.Equal(100 - 2 * Row, ChipPlace.ClearOf(First, null, new[] { taller }, 300).Y);
    }

    [Fact]
    public void A_stack_that_reaches_a_badge_goes_on_from_the_other_side()
    {
      var badge = new LensRect(110, 100 - Row, 14, 13);
      var at = ChipPlace.ClearOf(First, new[] { First }, new[] { badge }, 300);
      Assert.Equal(300, at.Y);
      Assert.Equal(100, at.X);

      var next = ChipPlace.ClearOf(First, new[] { First, at }, new[] { badge }, 300);
      Assert.Equal(300 + Row, next.Y);   // and stacks downward from there
    }

    /// <summary>What the gate's second wire does: its first row is on the first chip and on the badge, and climbing away from both is not reaching either.</summary>
    [Fact]
    public void A_chip_that_starts_on_a_chip_and_a_badge_climbs_away_from_both()
    {
      var taller = new LensRect(110, 95, 14, 13);
      var at = ChipPlace.ClearOf(First, new[] { First }, new[] { taller }, 300);
      Assert.Equal(100 - 2 * Row, at.Y);
    }

    [Fact]
    public void Other_chips_alone_do_not_stop_the_stack()
    {
      var at = ChipPlace.ClearOf(First, new[] { First, new LensRect(100, 100 - Row, 60, 12) }, new[] { new LensRect(900, 900, 10, 10) }, 300);
      Assert.Equal(100 - 2 * Row, at.Y);
    }

    [Fact]
    public void With_both_sides_full_a_chip_goes_along_the_wire()
    {
      var above = Enumerable.Range(0, ChipPlace.MaxStack).Select(k => new LensRect(100, 100 - k * Row, 60, 12));
      var below = Enumerable.Range(0, ChipPlace.MaxStack).Select(k => new LensRect(100, 300 + k * Row, 60, 12));
      var at = ChipPlace.ClearOf(First, above.Concat(below), null, 300);
      Assert.Equal(100, at.Y);
      Assert.Equal(100 - 60 - ChipPlace.StackGap, at.X);
    }

    [Fact]
    public void A_crowd_past_all_of_that_goes_above_everything_and_covers_nothing()
    {
      var wall = new LensRect(-5000, -40, 10000, 5000);
      var at = ChipPlace.ClearOf(First, null, new[] { wall }, 300);
      Assert.False(at.Overlaps(wall));
      Assert.Equal(-40 - Row, at.Y);
    }

    [Fact]
    public void The_other_side_is_under_both_components_when_a_chip_lifted_and_the_next_row_when_it_did_not()
    {
      var a = new LensRect(0, 100, 50, 40);
      var b = new LensRect(60, 110, 50, 60);
      Assert.Equal(170 + ChipPlace.Lift, ChipPlace.OtherSide(First, true, a, b));
      Assert.Equal(First.Bottom + ChipPlace.StackGap, ChipPlace.OtherSide(First, false, a, b));
    }

    [Fact]
    public void A_line_from_a_chip_starts_on_the_chips_edge_nearest_its_wire()
    {
      Assert.Equal((130.0, 112.0), ChipPlace.LeaderEnd(First, 130, 200));   // below: the bottom edge, straight down
      Assert.Equal((130.0, 100.0), ChipPlace.LeaderEnd(First, 130, 50));    // above: the top edge
      Assert.Equal((160.0, 106.0), ChipPlace.LeaderEnd(First, 400, 106));   // beside: the side
      Assert.Equal((100.0, 100.0), ChipPlace.LeaderEnd(First, 0, 0));       // off a corner: the corner
    }

    [Fact]
    public void The_host_measures_what_it_keeps_off_and_draws_every_line_before_any_chip()
    {
      var lens = File.ReadAllText(Path.Combine(EdgeRepo(), "Dwight", "UI", "TreeLens.cs"));
      Assert.Contains("foreach (var r in CanvasGlow.BadgeRects(g, doc))", lens);
      Assert.Contains("if (obj is IGH_ActiveObject && obj.Attributes != null && near.IntersectsWith(obj.Attributes.Bounds))", lens);
      var seated = lens.IndexOf("seats.Add((chip, seat, wire));", StringComparison.Ordinal);
      var lines = lens.IndexOf("DrawLeader(g, ink, seat, wire, placed, labels);", StringComparison.Ordinal);
      var chips = lens.IndexOf("DrawChip(g, chip, new RectangleF(", StringComparison.Ordinal);
      Assert.True(seated > 0 && seated < lines && lines < chips);   // every chip has its place before any line is drawn, and every line before any chip
      Assert.Contains("var line = ChipPlace.Leader(seat, wire, placed.Where(o => !o.Equals(seat)), labels);", lens);
      Assert.Contains("g.DrawLines(ink, line.Select(", lens);   // a line can bend
      Assert.Contains("labels.Add(ChipPlace.LabelIn(", lens);
      // the wire as it is drawn, which the seat's own search reads and not only the lit line
      Assert.Contains("var (c1, c2) = CanvasLook.WireControls(a, b);\n      points = ChipPlace.WirePoints(a.X, a.Y, c1.X, c1.Y, c2.X, c2.Y, b.X, b.Y);", lens.Replace("\r\n", "\n"));

      // one function, so what is kept off is what is drawn
      var glow = File.ReadAllText(Path.Combine(EdgeRepo(), "Dwight", "UI", "CanvasGlow.cs"));
      Assert.Contains("foreach (var (text, rect) in Badges(g, doc, font))", glow);
      Assert.Contains("return Badges(g, doc, font).Select(b => b.Rect).ToList();", glow);
    }

    // ---- the cap --------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_steps_own_wire_always_draws()
    {
      Assert.True(LensCap.Draws(true, LensMode.Step, 500));
      Assert.True(LensCap.Draws(true, LensMode.All, 500));
    }

    [Fact]
    public void Another_wire_draws_only_when_all_were_asked_for_and_the_cap_does_not_hold()
    {
      Assert.True(LensCap.Draws(false, LensMode.All, LensCap.MaxInView));
      Assert.False(LensCap.Draws(false, LensMode.All, LensCap.MaxInView + 1));
      Assert.False(LensCap.Draws(false, LensMode.Step, 1));
    }

    [Fact]
    public void The_cap_is_forty_and_only_holds_when_all_were_asked_for()
    {
      Assert.Equal(40, LensCap.MaxInView);
      Assert.False(LensCap.Capped(LensMode.All, 40));
      Assert.True(LensCap.Capped(LensMode.All, 41));
      Assert.False(LensCap.Capped(LensMode.Step, 500));
    }

    /// <summary>T is what got here, so "press T for all" would send the person round in a circle. Zooming in is what lowers a count of the wires in view.</summary>
    [Fact]
    public void The_sentence_is_true_whether_or_not_a_step_is_current()
    {
      Assert.Null(LensCap.Line(LensMode.Step, 500, true));
      Assert.Null(LensCap.Line(LensMode.All, 40, true));
      Assert.Equal("Chips on this step only: more than 40 wires are in view. Zoom in for the rest.", LensCap.Line(LensMode.All, 41, true));
      Assert.Equal("No chips: more than 40 wires are in view. Zoom in, or pick a step.", LensCap.Line(LensMode.All, 41, false));
      Assert.Contains(LensCap.MaxInView.ToString(), LensCap.StepOnlyLine);
      Assert.Contains(LensCap.MaxInView.ToString(), LensCap.NoChipsLine);
    }

    // ---- the keys -------------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("T", true)]
    [InlineData("Escape", true)]
    [InlineData("Right", false)]
    [InlineData("", false)]
    public void With_no_live_result_every_key_goes_back_unhandled(string key, bool chips) =>
      Assert.Equal(LensKeyAction.None, LensKeys.Dispatch(key, live: false, modifiers: false, claimed: false, chipsShowing: chips, canvasActive: true));

    [Fact]
    public void A_bare_T_toggles_on_a_live_lens() =>
      Assert.Equal(LensKeyAction.Toggle, LensKeys.Dispatch("T", live: true, modifiers: false, claimed: false, chipsShowing: false, canvasActive: true));

    [Fact]
    public void T_with_a_modifier_or_while_something_else_holds_the_canvas_is_not_ours()
    {
      Assert.Equal(LensKeyAction.None, LensKeys.Dispatch("T", live: true, modifiers: true, claimed: false, chipsShowing: true, canvasActive: true));
      Assert.Equal(LensKeyAction.None, LensKeys.Dispatch("T", live: true, modifiers: false, claimed: true, chipsShowing: true, canvasActive: true));
    }

    [Fact]
    public void Esc_is_only_taken_while_chips_are_showing()
    {
      Assert.Equal(LensKeyAction.Hide, LensKeys.Dispatch("Escape", live: true, modifiers: false, claimed: false, chipsShowing: true, canvasActive: true));
      Assert.Equal(LensKeyAction.None, LensKeys.Dispatch("Escape", live: true, modifiers: false, claimed: false, chipsShowing: false, canvasActive: true));
    }

    [Fact]
    public void No_other_key_is_touched()
    {
      foreach (var key in new[] { "Left", "Right", "Up", "Down", "Space", "A", "t" })
        Assert.Equal(LensKeyAction.None, LensKeys.Dispatch(key, live: true, modifiers: false, claimed: false, chipsShowing: true, canvasActive: true));
    }

    /// <summary>Brief DJ, item 8: a Walk has ended, so there is no step. T still asks for every wire of the selection, and those draw their text.</summary>
    [Fact]
    public void T_after_a_walk_draws_the_selections_chips_with_no_step()
    {
      Assert.Equal(LensKeyAction.Toggle, LensKeys.Dispatch("T", live: true, modifiers: false, claimed: false, chipsShowing: false, canvasActive: true));
      Assert.True(LensCap.Draws(false, LensKeys.Toggled(LensMode.Step), LensCap.MaxInView));
    }

    /// <summary>The ruling on T: it toggles only while an Explain result is live and the canvas holds the keys. It is never consumed, in any of the three.</summary>
    [Fact]
    public void T_toggles_only_while_a_result_is_live_and_the_canvas_holds_the_keys()
    {
      Assert.Equal(LensKeyAction.Toggle, LensKeys.Dispatch("T", live: true, modifiers: false, claimed: false, chipsShowing: false, canvasActive: true));
      Assert.Equal(LensKeyAction.None, LensKeys.Dispatch("T", live: true, modifiers: false, claimed: false, chipsShowing: false, canvasActive: false));
      Assert.Equal(LensKeyAction.None, LensKeys.Dispatch("T", live: false, modifiers: false, claimed: false, chipsShowing: false, canvasActive: true));
    }

    [Fact]
    public void Esc_with_a_text_box_active_is_not_ours() =>
      Assert.Equal(LensKeyAction.None, LensKeys.Dispatch("Escape", live: true, modifiers: false, claimed: false, chipsShowing: true, canvasActive: false));

    [Fact]
    public void T_goes_between_the_step_and_all()
    {
      Assert.Equal(LensMode.All, LensKeys.Toggled(LensMode.Step));
      Assert.Equal(LensMode.Step, LensKeys.Toggled(LensMode.All));
    }

    // ---- the host, by the text of the code --------------------------------------------------------------------------------------------------------

    private static string Repo(params string[] parts)
    {
      var dir = new DirectoryInfo(AppContext.BaseDirectory);
      while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Dwight.sln"))) dir = dir.Parent;
      Assert.NotNull(dir);
      return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray())).Replace("\r\n", "\n");
    }

    private static string Lens => Repo("Dwight", "UI", "TreeLens.cs");

    private static string Between(string text, string from, string to)
    {
      var a = text.IndexOf(from, StringComparison.Ordinal);
      Assert.True(a >= 0, from);
      var b = text.IndexOf(to, a + from.Length, StringComparison.Ordinal);
      Assert.True(b > a, to);
      return text.Substring(a, b - a);
    }

    /// <summary>"No model call anywhere in this unit": nothing in it can reach a provider.</summary>
    [Fact]
    public void Nothing_in_the_unit_can_reach_a_model()
    {
      foreach (var src in new[] { Lens, Repo("Dwight", "UI", "TreeLensCore.cs") })
      {
        Assert.DoesNotContain("ILlmProvider", src);
        Assert.DoesNotContain("StructuredOutput", src);
        Assert.DoesNotContain("NewProvider", src);
        Assert.DoesNotContain("LlmActions", src);
      }
    }

    /// <summary>U47, U48: a read inside a solution must have a clock, and this one has none.</summary>
    [Fact]
    public void The_facts_are_never_read_during_a_solution()
    {
      var read = Between(Lens, "private static void Read()", "private static void ReadWires(");
      Assert.Contains("if (doc.SolutionState == GH_ProcessStep.Process) return;", read);
      Assert.InRange(read.IndexOf("doc.SolutionState", StringComparison.Ordinal), 0, read.IndexOf("ReadWires(doc", StringComparison.Ordinal));

      // The end of a solution reads after it, on the next turn of Rhino's thread, never inside the event.
      var end = Between(Lens, "private static void OnSolutionEnd(", "private static void Invalidate()");
      Assert.Contains("canvas.BeginInvoke(", end);
      Assert.InRange(end.IndexOf("BeginInvoke", StringComparison.Ordinal), 0, end.IndexOf("Read();", StringComparison.Ordinal));
    }

    [Fact]
    public void The_read_reaches_only_what_is_asked_for()
    {
      var read = Between(Lens, "private static void Read()", "private static void ReadWires(");
      Assert.Contains("foreach (var g in _step) ReadWires(doc, g, true, wires);", read);
      Assert.Contains("if (_mode == LensMode.All) foreach (var g in _selection) ReadWires(doc, g, false, wires);", read);
    }

    [Fact]
    public void The_key_hook_is_attached_only_while_live_and_released_when_it_clears()
    {
      var begin = Between(Lens, "public static void Begin(", "THE SEAM");
      Assert.Contains("_canvas.KeyDown += OnKeyDown;", begin);
      Assert.InRange(begin.IndexOf("_live = true;", StringComparison.Ordinal), 0, begin.IndexOf("KeyDown +=", StringComparison.Ordinal));

      var clear = Between(Lens, "public static void Clear()", "A click on a line");
      Assert.Contains("_canvas.KeyDown -= OnKeyDown;", clear);
      Assert.Contains("_doc.SolutionEnd -= OnSolutionEnd;", clear);
      Assert.Contains("Changed = null;", clear);
    }

    /// <summary>Grasshopper sends a bare letter to Rhino's command line before the canvas's KeyDown can see it, so T is heard on the form, for the lens's life only, and it is never consumed. No hook on Grasshopper's own window: that is the one place a slip takes Rhino down.</summary>
    [Fact]
    public void T_is_heard_on_the_form_and_never_consumed()
    {
      var begin = Between(Lens, "public static void Begin(", "THE SEAM");
      Assert.Contains("_keyForm = _canvas.FindForm(); if (_keyForm != null) _keyForm.KeyDown += OnFormKey;", begin);
      Assert.InRange(begin.IndexOf("_live = true;", StringComparison.Ordinal), 0, begin.IndexOf("_keyForm = _canvas.FindForm();", StringComparison.Ordinal));

      var clear = Between(Lens, "public static void Clear()", "A click on a line");
      Assert.Contains("if (_keyForm != null) _keyForm.KeyDown -= OnFormKey;", clear);
      Assert.Contains("_keyForm = null;", clear);

      var key = Between(Lens, "private static void OnFormKey(", "The canvas holds the keys unless");
      Assert.Contains("if (e.KeyCode != Keys.T) return;", key);
      Assert.Contains("LensKeys.Dispatch(LensKeys.ToggleKey, _live, e.Modifiers != Keys.None, claimed, showing, CanvasActive())", key);
      Assert.Contains("_canvas?.ActiveInteraction != null", key);
      Assert.Contains("if (action == LensKeyAction.Toggle) Toggle();", key);
      Assert.DoesNotContain("e.Handled", key);
      Assert.DoesNotContain("SuppressKeyPress", key);

      Assert.DoesNotContain("NativeWindow", Lens);
      Assert.DoesNotContain("WndProc", Lens);
      Assert.DoesNotContain("Keys.T", Between(Lens, "private static void OnKeyDown(", "T, heard on Grasshopper's form"));
    }

    /// <summary>Grasshopper's own test: a text box or a combo box holds the keys.</summary>
    [Fact]
    public void The_canvas_is_active_unless_a_text_box_or_a_combo_box_is()
    {
      var active = Between(Lens, "private static bool CanvasActive()", "A solution has changed the facts");
      Assert.Contains("_canvas?.FindForm()?.ActiveControl", active);
      Assert.Contains("!(active is TextBoxBase) && !(active is ComboBox)", active);
    }

    [Fact]
    public void The_hook_asks_the_pure_decision_and_only_takes_a_key_it_was_given()
    {
      var key = Between(Lens, "private static void OnKeyDown(", "A solution has changed the facts");
      Assert.Contains("LensKeys.Dispatch(key, _live, e.Modifiers != Keys.None, claimed, showing, CanvasActive())", key);
      Assert.Contains("?.ActiveInteraction != null || WalkSession.IsLive;", key);   // a live Walk's Esc is the Walk's, or a pause would cost the step's chips
      Assert.InRange(key.IndexOf("if (action == LensKeyAction.None) return;", StringComparison.Ordinal), 0, key.IndexOf("e.Handled = true;", StringComparison.Ordinal));
    }

    /// <summary>A chip is drawn only on the document it was read from, and a surface is never told anything from inside a paint.</summary>
    [Fact]
    public void A_paint_only_draws_and_never_reads_or_tells()
    {
      var paint = Between(Lens, "internal static void Paint(", "private static bool InView(");
      Assert.Contains("!ReferenceEquals(canvas.Document, _doc)", paint);
      Assert.Contains("canvas.BeginInvoke((Action)Tell);", paint);
      Assert.DoesNotContain("Read();", paint);
      Assert.DoesNotContain("VolatileData", paint);
      Assert.Contains("LensCap.Draws(chip.InStep, _mode, _wiresInView)", paint);
    }

    [Fact]
    public void The_chips_are_the_annotation_ink()
    {
      Assert.Contains("private static readonly Color Ink = UiTokens.TealWire;", Lens);
    }

    [Fact]
    public void The_lens_clears_with_the_glow_and_another_panels_glow_ends_it()
    {
      var glow = Repo("Dwight", "UI", "CanvasGlow.cs");
      Assert.Contains("public static void Clear() { TreeLens.Clear();", glow);
      Assert.Contains("if (thinking) TreeLens.Clear(); else TreeLens.KeepOnlyFor(owner);", glow);
      Assert.Contains("TreeLens.ClearIfOwner(owner);", glow);
      Assert.Contains("TreeLens.Clear();   // Brief DB: only an Explain has a lens", glow);
      Assert.Contains("TreeLens.Paint(g, sender);", glow);
      Assert.Contains("if (_live && !ReferenceEquals(owner, _owner)) Clear();", Lens);
    }

    /// <summary>The dock's reopen renders, then glows. The lens it just began must outlive that glow, because the glow is its own panel's.</summary>
    [Fact]
    public void A_panels_own_glow_after_its_render_does_not_end_its_lens()
    {
      var session = Repo("Dwight", "DwightSession.cs");
      Assert.InRange(session.IndexOf("Render(panel, cmd.Script, cmd.Live);", StringComparison.Ordinal), 0, session.IndexOf("CanvasGlow.Show(cmd.SourceGuids, thinking: false, owner: panel);", StringComparison.Ordinal));
      Assert.Contains("TreeLens.Begin(selection, this);", Repo("Dwight", "UI", "WebPanel.cs"));
    }

    [Fact]
    public void The_panel_begins_the_lens_with_its_steps_and_each_step_goes_through_the_one_seam()
    {
      var panel = Repo("Dwight", "UI", "WebPanel.cs");
      Assert.Contains("Run($\"dwightSteps({Js(WalkOrder.ForPanel(stepsJson))})\"); BeginLens(); }", panel);

      // Brief DA: a step goes through the Walk, and the Walk carries the seam, with the panel told first which step the lines sit under.
      var step = Between(panel, "private void OnStep(string idx)", "Diagnose / stray");
      Assert.Contains("WalkSession.GoTo(this, i);", step);
      var session = Repo("Dwight", "UI", "WalkSession.cs");
      var told = session.IndexOf("_panel.SetLensStep(i);", StringComparison.Ordinal);
      Assert.True(told >= 0 && told < session.IndexOf("TreeLens.SetCurrent(_state.Current);", StringComparison.Ordinal));
      Assert.Contains("internal void SetLensStep(int i) => _lensStep = i;", panel);

      var overview = Between(panel, "private void OnOverview()", "Whole-canvas Diagnose chain-picker");
      Assert.Contains("_lensStep = -1;\n      TreeLens.SetCurrent(null);", overview);

      Assert.Contains("TreeLens.SelectStop(line)", panel);

      // Esc ends the step from the canvas, so the lines go back above the steps without the panel being told by a click.
      Assert.Contains("LensView.Json(TreeLens.HasStep ? _lensStep : -1, TreeLens.Stops, TreeLens.Note, TreeLens.IsLive, TreeLens.ShowsAll)", panel);
      Assert.Contains("public static bool HasStep => _step.Count > 0;", Lens);
      Assert.Equal(1, panel.Split(new[] { "TreeLens.SetCurrent(" }, StringSplitOptions.None).Length - 1);   // the summary; every step is the Walk's
    }

    [Fact]
    public void The_shell_places_written_words_escaped_and_a_click_is_a_line_number()
    {
      var shell = Repo("Dwight", "Resources", "panel-shell.html");
      var lens = Between(shell, "function dwightLens(json)", "// A collapsible fault row");
      Assert.Equal(2, lens.Split(new[] { "esc(l.text)" }, StringSplitOptions.None).Length - 1);   // a line that can be clicked, and one that cannot
      Assert.DoesNotContain("+l.text+", lens);
      Assert.Contains("esc(l.action)", lens);
      Assert.Contains("esc(d.note)", lens);
      Assert.Contains("dwightSend(\\'lens:'+i+'\\')", lens);
      Assert.Contains("Every wire, in the footer, or T on the canvas, shows them for the whole selection.", shell);   // the button before the key
    }

    [Fact]
    public void A_click_selects_what_the_line_names_and_changes_nothing_else()
    {
      var select = Between(Lens, "public static void SelectStop(int index)", "private static void OnKeyDown(");
      Assert.Contains("if (stop.Select.Count == 0) return;", select);
      Assert.Contains("foreach (var g in stop.Select)", select);
      Assert.Contains("o.Attributes.Selected = true;", select);
      Assert.Contains("_port = stop.Port;", select);
      Assert.Contains("CanvasNav.GoTo(stop.Select, NavReason.UserClicked);", select);
      foreach (var mutation in new[] { "NewSolution", "RecordUndoEvent", "ExpireSolution", "AddObject", "RemoveObject" })
        Assert.DoesNotContain(mutation, Lens);
    }
  }
}
