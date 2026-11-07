using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using Dwight.Agent;
using Dwight.Data;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// U22 - THE STALENESS STAMP. The model said "I have no canvas-changed event - I only find out by re-reading" (#719);
  /// the plugin now records what each read returned and names, at the next turn, the ids whose content differs.
  ///
  /// Every "changes" assertion has a POSITIVE CONTROL beside it (#212): the same stamp compared with itself is silent,
  /// so a stamp that always differed - or never did - fails one side of the pair.
  /// </summary>
  public class ComponentStampTests
  {
    private static Guid G(int n) { var b = new byte[16]; b[0] = (byte)n; b[1] = (byte)(n >> 8); return new Guid(b); }

    private static FpObject Slider(string value = "5", params Guid[] sources)
    {
      var o = new FpObject { Guid = G(1), TypeName = "Grasshopper.Kernel.Special.GH_NumberSlider", ValueToken = value };
      o.Inputs.Add(new FpInput { Guid = G(100), MappingToken = "none", Sources = sources.ToList() });
      return o;
    }

    private static FpObject Script(string sourceToken, params Guid[] sources)
    {
      var o = new FpObject { Guid = G(2), TypeName = "RhinoCodePluginGH.Components.Python3Component", ValueToken = sourceToken };
      o.Inputs.Add(new FpInput { Guid = G(200), MappingToken = "none", Sources = sources.ToList() });
      o.Inputs.Add(new FpInput { Guid = G(201), MappingToken = "none" });
      return o;
    }

    private static ComponentStamp Of(FpObject o, string nick = "n", string state = null, bool script = false) =>
      ComponentStamp.Of(o, nick, state, script);

    // ---- the stamp -------------------------------------------------------------------------------------------

    [Fact]
    public void The_same_reading_twice_is_silent_the_positive_control()
    {
      Assert.Empty(Of(Slider("5", G(9))).ChangesSince(Of(Slider("5", G(9)))));
      Assert.Equal(Of(Slider("5", G(9))).Digest, Of(Slider("5", G(9))).Digest);
    }

    [Fact]
    public void A_wiring_change_changes_the_stamp_and_names_only_wiring()
    {
      var before = Of(Slider("5", G(9)));
      Assert.Equal(new[] { "wiring" }, Of(Slider("5", G(9), G(10))).ChangesSince(before));   // a source added
      Assert.Equal(new[] { "wiring" }, Of(Slider("5")).ChangesSince(before));                  // a source removed
      Assert.Equal(new[] { "wiring" }, Of(Slider("5", G(11))).ChangesSince(before));           // a source swapped, same count
    }

    [Fact]
    public void A_source_change_changes_the_stamp_and_names_only_source()
    {
      var before = Of(Script("src-a"), script: true);
      Assert.Empty(Of(Script("src-a"), script: true).ChangesSince(before));
      Assert.Equal(new[] { "source" }, Of(Script("src-b"), script: true).ChangesSince(before));
    }

    [Fact]
    public void A_held_value_change_is_a_value_change_for_a_slider_a_state_and_internalised_data()
    {
      var before = Of(Slider("5"), state: "slider:0..10@5/float/dp2");
      Assert.Equal(new[] { "value" }, Of(Slider("6"), state: "slider:0..10@5/float/dp2").ChangesSince(before));    // the value token
      Assert.Equal(new[] { "value" }, Of(Slider("5"), state: "slider:0..20@5/float/dp2").ChangesSince(before));   // a domain-only edit

      var withData = Slider("5");
      withData.Inputs[0].PersistentToken = "1|1:7,;";
      var edited = Slider("5");
      edited.Inputs[0].PersistentToken = "1|1:8,;";
      Assert.Equal(new[] { "value" }, Of(edited).ChangesSince(Of(withData)));
      Assert.Empty(Of(withData).ChangesSince(Of(withData)));
    }

    [Fact]
    public void A_type_a_name_and_a_lock_each_name_themselves()
    {
      var before = Of(Slider("5"), nick: "a");
      Assert.Equal(new[] { "name" }, Of(Slider("5"), nick: "b").ChangesSince(before));

      var locked = Slider("5"); locked.Locked = true;
      Assert.Equal(new[] { "locked" }, Of(locked, nick: "a").ChangesSince(before));

      var retyped = Slider("5"); retyped.TypeName = "Grasshopper.Kernel.Special.GH_Panel";
      Assert.Equal(new[] { "type" }, Of(retyped, nick: "a").ChangesSince(before));
    }

    [Fact]
    public void Several_parts_are_named_in_the_sentence_order()
    {
      var before = Of(Script("a", G(9)), nick: "x", script: true);
      var after = Of(Script("b"), nick: "y", script: true);
      Assert.Equal(new[] { "name", "wiring", "source" }, after.ChangesSince(before));
    }

    [Fact]
    public void A_MOVE_cannot_change_the_stamp_because_nothing_it_is_made_of_holds_a_position()
    {
      // Behaviour: the reader's whole input is FpObject/FpInput. A moved component reads as the same object, so its
      // stamp is the same (the control is the identical-reading test above, which would fail if the stamp were noisy).
      var o = Slider("5", G(9));
      Assert.Empty(Of(o).ChangesSince(Of(o)));

      // Structure: and the input type has no member that could carry where the component sits. A future field named
      // like a position trips this before it can make a tidy look like a change.
      var banned = new[] { "pivot", "position", "bounds", "location", "origin", "point", "x", "y", "left", "top" };
      foreach (var t in new[] { typeof(FpObject), typeof(FpInput) })
      {
        var names = t.GetFields(BindingFlags.Public | BindingFlags.Instance).Select(f => f.Name)
          .Concat(t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name));
        foreach (var n in names)
          Assert.DoesNotContain(n.ToLowerInvariant(), banned);
      }

      // And the canvas token that drives readRevision is over ids and stamps only.
      var tok1 = ComponentStamp.CanvasToken(new[] { new KeyValuePair<int, ComponentStamp>(1, Of(o)) });
      var tok2 = ComponentStamp.CanvasToken(new[] { new KeyValuePair<int, ComponentStamp>(1, Of(o)) });
      var tok3 = ComponentStamp.CanvasToken(new[] { new KeyValuePair<int, ComponentStamp>(1, Of(Slider("6"))) });
      Assert.Equal(tok1, tok2);
      Assert.NotEqual(tok1, tok3);
    }

    // ---- the registry's readings and the revision --------------------------------------------------------------

    [Fact]
    public void The_revision_rises_only_when_the_token_changes()
    {
      var ids = new AgentIdRegistry();
      Assert.Equal(1, ids.ObserveCanvas("a"));
      Assert.Equal(1, ids.ObserveCanvas("a"));   // nothing changed
      Assert.Equal(2, ids.ObserveCanvas("b"));
      Assert.Equal(2, ids.ObserveCanvas("b"));
      Assert.Equal(3, ids.ObserveCanvas("a"));   // monotonic: back to an earlier token is still later
    }

    [Fact]
    public void A_read_records_its_ids_stamp_and_a_newer_read_replaces_it()
    {
      var ids = new AgentIdRegistry();
      var id = ids.Issue(G(1));
      Assert.Empty(ids.Reads());

      ids.RecordRead(id, Of(Slider("5")), null, 1);
      ids.RecordRead(id, Of(Slider("6")), null, 2);
      var reads = ids.Reads();
      Assert.Single(reads);
      Assert.Equal(2, reads[0].Value.Revision);
      Assert.Empty(Of(Slider("6")).ChangesSince(reads[0].Value.Stamp));   // it is the NEWEST reading: "6", not "5"

      ids.RecordRead(999, Of(Slider("5")), null, 1);   // an id never issued is not recorded
      Assert.Single(ids.Reads());
    }

    private static (AgentSnapshot snap, AgentIdRegistry ids, int a, int b) StampedFixture(bool stamped, string bValue = "5")
    {
      var ids = new AgentIdRegistry();
      ids.Sync(new[] { G(1), G(2) });
      var a = ids.Issue(G(1)); var b = ids.Issue(G(2));
      var snap = new AgentSnapshot
      {
        Nodes =
        {
          new AgentNode { Id = a, Type = "Number Slider", Kind = "param", Outputs = new[] { "N" },
                          Stamp = stamped ? Of(Slider("5")) : null },
          new AgentNode { Id = b, Type = "Number Slider", Kind = "param", Outputs = new[] { "N" },
                          Stamp = stamped ? Of(Slider(bValue)) : null },
        },
        Clusters = { new AgentCluster { Id = 0, Label = "R", Members = { a, b } } },
      };
      if (stamped) snap.ReadRevision = ids.ObserveCanvas(ComponentStamp.CanvasToken(snap.Nodes.Select(n => new KeyValuePair<int, ComponentStamp>(n.Id, n.Stamp))));
      return (snap, ids, a, b);
    }

    private static JsonObject Args(string json) => (JsonObject)JsonNode.Parse(json);

    [Fact]
    public void get_component_states_readRevision_with_its_meaning_and_records_the_reading()
    {
      var (snap, ids, a, b) = StampedFixture(stamped: true);
      var r = AgentToolCore.GetComponent(snap, ids, Args("{\"id\":" + a + "}"));
      Assert.True(r.Ok);
      Assert.Equal(1, r.Result["readRevision"].GetValue<int>());
      Assert.Equal(AgentToolCore.ReadRevisionMeans, r.Result["means"]["readRevision"].GetValue<string>());
      Assert.Equal(new[] { a }, ids.Reads().Select(x => x.Key).ToArray());   // a was read; b was not
    }

    [Fact]
    public void get_region_states_readRevision_and_records_every_member()
    {
      var (snap, ids, a, b) = StampedFixture(stamped: true);
      var stamp = AgentToolCore.ShapeHash(snap);
      var r = AgentToolCore.GetRegion(snap, Args("{\"id\":0}"), stamp, ids);
      Assert.True(r.Ok);
      Assert.Equal(1, r.Result["readRevision"].GetValue<int>());
      Assert.Equal(AgentToolCore.ReadRevisionMeans, r.Result["means"]["readRevision"].GetValue<string>());
      Assert.Equal(new[] { a, b }, ids.Reads().Select(x => x.Key).ToArray());
    }

    [Fact]
    public void An_unstamped_snapshot_states_no_revision_and_records_nothing_the_positive_control()
    {
      var (snap, ids, a, b) = StampedFixture(stamped: false);
      var r = AgentToolCore.GetComponent(snap, ids, Args("{\"id\":" + a + "}"));
      Assert.True(r.Ok);
      Assert.Null(r.Result["readRevision"]);
      Assert.Null(r.Result["means"]["readRevision"]);
      Assert.Empty(ids.Reads());
    }

    [Fact]
    public void The_revision_a_second_read_states_rises_when_a_component_changed_between_them()
    {
      var (s1, ids, a, _) = StampedFixture(stamped: true);
      Assert.Equal(1, s1.ReadRevision);

      // the same canvas read again: same revision
      var again = new AgentSnapshot();
      foreach (var n in s1.Nodes) again.Nodes.Add(n);
      again.ReadRevision = ids.ObserveCanvas(ComponentStamp.CanvasToken(again.Nodes.Select(n => new KeyValuePair<int, ComponentStamp>(n.Id, n.Stamp))));
      Assert.Equal(1, again.ReadRevision);

      // a component's value changed: the next read is a later canvas
      var changed = new AgentSnapshot();
      foreach (var n in s1.Nodes)
        changed.Nodes.Add(new AgentNode { Id = n.Id, Type = n.Type, Kind = n.Kind, Outputs = n.Outputs,
                                          Stamp = n.Id == a ? Of(Slider("99")) : n.Stamp });
      changed.ReadRevision = ids.ObserveCanvas(ComponentStamp.CanvasToken(changed.Nodes.Select(n => new KeyValuePair<int, ComponentStamp>(n.Id, n.Stamp))));
      Assert.Equal(2, changed.ReadRevision);
    }

    // ---- the turn-start sentence -----------------------------------------------------------------------------

    private static StaleRead S(int id, params string[] changes) => new StaleRead { Id = id, Changes = changes };

    [Fact]
    public void The_sentence_from_a_fixture()
    {
      var fact = StaleReadsFact.For(new[] { S(14, "wiring"), S(11, "source"), new StaleRead { Id = 19, Removed = true }, S(21, "name", "value") });
      Assert.Equal(
        "READS: since you read them, these components have changed: 11 (source), 14 (wiring), 19 (removed), 21 (name, value). " +
        "What you were told about them may no longer be true; read them again before relying on it.",
        fact);
    }

    [Fact]
    public void Nothing_changed_is_silence_not_a_sentence()
    {
      Assert.Null(StaleReadsFact.For(null));
      Assert.Null(StaleReadsFact.For(new StaleRead[0]));
      Assert.Null(StaleReadsFact.For(new[] { S(3) }));   // a row with no change and not removed names nothing
    }

    [Fact]
    public void The_list_is_cut_at_twenty_with_the_count_stated()
    {
      var thirty = Enumerable.Range(1, 30).Select(i => S(i, "value")).ToArray();
      var fact = StaleReadsFact.For(thirty);
      Assert.Contains("20 (value)", fact);
      Assert.DoesNotContain("21 (value)", fact);
      Assert.Contains("and 10 more (30 in all)", fact);

      // the control: exactly the bound says no "more"
      var twenty = StaleReadsFact.For(thirty.Take(20).ToArray());
      Assert.Contains("20 (value)", twenty);
      Assert.DoesNotContain("more", twenty);
    }

    [Fact]
    public void The_turn_text_carries_the_sentence_right_after_the_canvas_fact_and_is_unchanged_without_it()
    {
      var without = TurnSystemText.Compose("SIT", "CANVAS: changed", null, TurnCapability.ReadOnly, null, null, null);
      var with = TurnSystemText.Compose("SIT", "CANVAS: changed", null, TurnCapability.ReadOnly, null, null, null, "READS: x");
      Assert.DoesNotContain("READS:", without);
      Assert.Contains("CANVAS: changed\n\nREADS: x", with);
    }

    // ---- the payload contract --------------------------------------------------------------------------------

    [Fact]
    public void The_payload_contract_has_a_row_for_every_new_field()
    {
      var component = PayloadContract.For("tool:get_component").Fields.Select(f => f.Path).ToArray();
      var region = PayloadContract.For("tool:get_region").Fields.Select(f => f.Path).ToArray();
      Assert.Contains("readRevision", component);
      Assert.Contains("means.readRevision", component);
      Assert.Contains("readRevision", region);
      Assert.Contains("means.readRevision", region);
      Assert.Contains("means.sig", region);
      Assert.All(new[] { "readRevision", "means.readRevision" }, p =>
        Assert.Equal(Carries.Structure, PayloadContract.For("tool:get_component").Fields.First(f => f.Path == p).Carries));
    }
  }
}
