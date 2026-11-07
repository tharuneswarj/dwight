using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dwight.Agent;
using Dwight.Llm;
using Dwight.Session;
using Xunit;
using static Dwight.Agent.Tests.AgentTurnDriverTests;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Brief CL: three plugin sentences that were false on the first real file. The law: a plugin sentence is a claim, and must be true in every state that can reach it (#297, #624).
  /// One test per sentence per state, driving the real loop where the state comes from a turn. The panel's and the tidy's wiring link Grasshopper, so they are read by their text, and
  /// only a Rhino row can say what the real canvas now reads.
  /// </summary>
  [Collection(TurnShapeTests.StaticRecord)]
  public class TurnEndTruthTests
  {
    private const string Ok = "{\"ok\":true,\"result\":{}}";
    private static readonly string Refused = AgentLoopCore.Refusal(ToolErrors.PreconditionRefused, "No.");
    private const string Announcing = "Raising the card now.";

    private static string Repo(params string[] parts)
    {
      var dir = new DirectoryInfo(AppContext.BaseDirectory);
      while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Dwight.sln"))) dir = dir.Parent;
      Assert.True(dir != null);
      return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray())).Replace("\r\n", "\n");
    }

    private static TurnCapability CapabilityOf(string door) =>
      door == AgentToolCatalog.ProposeScaffold ? TurnCapability.Scaffold
      : door == FullControl.ToolName ? TurnCapability.RunScript
      : TurnCapability.ReadOnly;

    private static TurnResult OneDoor(string door, string answer) =>
      Drive(new ScriptedProvider(Calls("t1", door, "{}"), Says(Announcing), Says(Announcing)), _ => answer);

    // ---- 1. the doors ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_doors_are_one_named_set()
    {
      Assert.Equal(new[] { "request_mutation", "propose_scaffold", "run_script" }, AgentLoopCore.CardDoors);
      Assert.True(AgentLoopCore.IsCardDoor("PROPOSE_SCAFFOLD"));
      Assert.False(AgentLoopCore.IsCardDoor(AgentToolCatalog.BuildRegion));
      Assert.False(AgentLoopCore.IsCardDoor(AgentToolCatalog.GetOverview));
    }

    /// <summary>So a new door cannot be forgotten: every tool only an approval can reach is either a door or named here as kept out, with the reason beside it.</summary>
    [Fact]
    public void Every_tool_only_an_approval_can_reach_is_classified()
    {
      var keptOut = new[]
      {
        AgentToolCatalog.BuildRegion,    // a build that ran and then promises another card has the line to read, in the "no further request" words
        AgentToolCatalog.SolveAndCheck,  // the same: a solve raised no card of its own
      };
      var capabilities = new[] { TurnCapability.ReadAndSolve, TurnCapability.Scaffold, TurnCapability.Build, TurnCapability.RunScript };
      var names = AgentToolCatalog.All().Select(t => t.Name).Concat(AgentHostTools.All().Select(t => t.Name)).Append(FullControl.ToolName).Distinct();

      var onlyAnApproval = names.Where(n => !AgentLoopCore.IsGranted(TurnCapability.ReadOnly, n) && capabilities.Any(c => AgentLoopCore.IsGranted(c, n))).OrderBy(n => n);

      Assert.Equal(AgentLoopCore.CardDoors.Where(d => d != AgentHostTools.RequestMutation).Concat(keptOut).OrderBy(n => n), onlyAnApproval);
    }

    [Theory]
    [InlineData("propose_scaffold")]
    [InlineData("run_script")]
    public void A_turn_that_went_through_a_door_gets_no_line_on_either_channel(string door)
    {
      var result = OneDoor(door, Ok);
      var capability = CapabilityOf(door);

      Assert.Equal(1, result.CardDoorCalls);
      Assert.Equal(0, result.RequestCalls);
      Assert.False(AgentLoopCore.AnnouncedNothingAsked(capability, result));
      Assert.Null(AgentLoopCore.NothingAskedHtml(capability, result));
      Assert.Null(AgentLoopCore.NothingAskedReport(capability, result));
    }

    [Fact]
    public void A_turn_whose_card_came_through_request_mutation_gets_no_line()
    {
      var result = Drive(new ScriptedProvider(Calls("t1", AgentHostTools.RequestMutation, "{}", Announcing)), _ => AgentHostTools.CardShownResult("scaffold"));

      Assert.True(result.AskedForConsent);
      Assert.Equal(1, result.CardDoorCalls);
      Assert.False(AgentLoopCore.AnnouncedNothingAsked(TurnCapability.ReadOnly, result));
      Assert.Null(AgentLoopCore.NothingAskedHtml(TurnCapability.ReadOnly, result));
    }

    /// <summary>A refused request ASKED (#659), so it has no line; a refused map did not get through, so what the model says of it still needs one.</summary>
    [Theory]
    [InlineData("propose_scaffold", true)]
    [InlineData("run_script", true)]
    [InlineData("request_mutation", false)]
    public void A_refusal_is_not_through_the_door(string door, bool line)
    {
      var result = OneDoor(door, Refused);

      Assert.Equal(0, result.CardDoorCalls);
      Assert.Equal(line, AgentLoopCore.AnnouncedNothingAsked(CapabilityOf(door), result));
    }

    /// <summary>The cold judging's split, measured: Sonnet 5.5's map turn ended on the map and drew the line in all four transcripts; Opus and Fable went on to ask for the build in the same
    /// turn and drew none. Both shapes, through the real loop.</summary>
    [Fact]
    public void A_map_turn_draws_no_line_whether_it_ends_there_or_goes_on_to_ask()
    {
      var endsThere = Drive(new ScriptedProvider(Calls("t1", AgentToolCatalog.ProposeScaffold, "{}"), Says(Announcing)), _ => Ok);
      Assert.False(AgentLoopCore.AnnouncedNothingAsked(TurnCapability.Scaffold, endsThere));

      var goesOn = Drive(new ScriptedProvider(Calls("t1", AgentToolCatalog.ProposeScaffold, "{}"), Calls("t2", AgentHostTools.RequestMutation, "{}", Announcing)),
                         c => c.ToolName == AgentHostTools.RequestMutation ? AgentHostTools.CardShownResult("build") : Ok);
      Assert.True(goesOn.AskedForConsent);
      Assert.False(AgentLoopCore.AnnouncedNothingAsked(TurnCapability.Scaffold, goesOn));
    }

    /// <summary>The positive control: the same words with no door do draw the line, so the tests above measure the door.</summary>
    [Fact]
    public void The_same_words_with_no_door_draw_the_line()
    {
      var result = Drive(new ScriptedProvider(Calls("t1", AgentToolCatalog.GetOverview, "{}"), Says(Announcing)), _ => Ok);

      Assert.True(AgentLoopCore.AnnouncedNothingAsked(TurnCapability.ReadOnly, result));
      Assert.Contains("<strong>No request reached Dwight.</strong>", AgentLoopCore.NothingAskedHtml(TurnCapability.ReadOnly, result));
    }

    // ---- 1. the sentence, in each state that can reach it ---------------------------------------------------------------------------------

    private static TurnResult Announced() => new TurnResult { Outcome = TurnOutcome.Complete, Text = Announcing };

    [Fact]
    public void With_no_approval_above_it_the_line_is_as_it_was()
    {
      var html = AgentLoopCore.NothingAskedHtml(TurnCapability.ReadOnly, Announced());

      Assert.Contains(AgentLoopCore.NothingAskedSentence, html);
      Assert.Equal(AgentLoopCore.NothingAskedFact, AgentLoopCore.NothingAskedReport(TurnCapability.ReadOnly, Announced()));
      Assert.Equal(AgentLoopCore.NothingAskedSentence, AgentLoopCore.NothingAskedSentenceFor(TurnCapability.ReadOnly, false));
    }

    /// <summary>Every state a card just above can reach. "No card was raised" was false to read in each, so neither channel says it.</summary>
    [Theory]
    [InlineData("Build", false)]         // an approved build that then promises another card
    [InlineData("Scaffold", false)]      // an approved map whose door was refused
    [InlineData("ReadAndSolve", false)]  // an approved solve
    [InlineData("ReadOnly", true)]       // the follow-up after a value, an edit or a script
    public void With_an_approval_above_it_the_line_says_nothing_further_was_asked(string capabilityName, bool followsApproval)
    {
      var capability = (TurnCapability)Enum.Parse(typeof(TurnCapability), capabilityName);
      var result = new TurnResult { Outcome = TurnOutcome.Complete, Text = "Raising the card for B1 now." };

      var html = AgentLoopCore.NothingAskedHtml(capability, result, followsApproval);
      var report = AgentLoopCore.NothingAskedReport(capability, result, followsApproval);

      Assert.Contains("<strong>No further request reached Dwight.</strong>", html);
      Assert.Contains(AgentLoopCore.NothingFurtherAskedSentence, html);
      Assert.DoesNotContain("no card was raised", html);
      Assert.Equal(AgentLoopCore.NothingFurtherAskedFact, report);
      Assert.DoesNotContain("no card was raised", report);
      Assert.Equal(AgentLoopCore.NothingFurtherAskedSentence, AgentLoopCore.NothingAskedSentenceFor(capability, followsApproval));
    }

    [Fact]
    public void The_further_sentence_claims_what_the_condition_guarantees()
    {
      Assert.Equal("Dwight received no further request after the approval above - nothing new was submitted for your approval and no new card was raised.",
                   AgentLoopCore.NothingFurtherAskedSentence);
      Assert.StartsWith("PLUGIN FACT (from Dwight, not the user):", AgentLoopCore.NothingFurtherAskedFact);
      Assert.Contains("no request_mutation call", AgentLoopCore.NothingFurtherAskedFact);
      Assert.DoesNotContain("it did not happen", AgentLoopCore.NothingFurtherAskedFact);   // the approval above did
    }

    private static StoredMessage Me(string text, bool echo = false) => new StoredMessage { Who = "me", Src = text, Html = text, Echo = echo };
    private static StoredMessage Dw(string text) => new StoredMessage { Who = "dw", Src = text, Html = text };

    [Fact]
    public void Following_an_approval_is_read_from_the_echo_mark()
    {
      Assert.False(ChatToTranscript.FollowsApproval(null));
      Assert.False(ChatToTranscript.FollowsApproval(new List<StoredMessage>()));
      Assert.False(ChatToTranscript.FollowsApproval(new[] { Dw("Hello.") }));
      Assert.False(ChatToTranscript.FollowsApproval(new[] { Me("Build it.") }));

      var echo = Me(ApprovalMode.AutoUserText, echo: true);
      Assert.True(ChatToTranscript.FollowsApproval(new[] { Me("Build it."), echo }));
      Assert.True(ChatToTranscript.FollowsApproval(new[] { Me("Build it."), echo, new StoredMessage { Who = ChatToTranscript.Report, Src = "Set." }, Dw("Done.") }));

      // Typing after it starts a new exchange, and typing the same words is not an approval (U111).
      Assert.False(ChatToTranscript.FollowsApproval(new[] { echo, Dw("Done."), Me("And now?") }));
      Assert.False(ChatToTranscript.FollowsApproval(new[] { Dw("Done."), Me(ApprovalMode.ClickUserText) }));
    }

    [Fact]
    public void The_panel_decides_both_channels_from_one_reading_of_the_thread()
    {
      var panel = Repo("Dwight", "UI", "DwightScaffold.cs");
      Assert.Contains("var followsApproval = ChatToTranscript.FollowsApproval(CommandHistory.ThreadMessages(doc, threadId));", panel);
      Assert.Contains("AgentLoopCore.NothingAskedHtml(capability, result, followsApproval)", panel);
      Assert.Contains("AgentLoopCore.NothingAskedReport(capability, result, followsApproval)", panel);
      Assert.Contains("AgentLoopCore.WentThroughCardDoor(call.ToolName, envelope)", Repo("Dwight", "Agent", "AgentTurnDriver.cs"));
    }

    // ---- 2. the count -----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_clause_names_what_it_counts_and_claims_no_Ctrl_Z()
    {
      Assert.Null(ApprovalMode.CloseClause(0));
      Assert.Equal("1 card was approved automatically.", ApprovalMode.CloseClause(1));
      Assert.Equal("3 cards were approved automatically.", ApprovalMode.CloseClause(3));
      foreach (var n in new[] { 1, 2, 3 })
      {
        Assert.DoesNotContain("change", ApprovalMode.CloseClause(n));
        Assert.DoesNotContain("Ctrl-Z", ApprovalMode.CloseClause(n));
      }
    }

    /// <summary>The brief's row: a scaffold card and two build cards in one run under "Do not ask", read at each close the panel draws. Each card is drawn marked in the turn that
    /// raised it and approved once that turn ends, so the count is what is on screen at every step, and nothing counts twice.</summary>
    [Fact]
    public void A_scaffold_card_and_two_build_cards_read_as_three_and_each_close_counts_what_is_on_screen()
    {
      var count = new AutoApprovalCount();
      const string T = "thread";

      // Turn 1, read-only: raises the map's card, marked. A read-only turn draws no close.
      count.Add(T);                                                    // its approval, once the turn has ended

      // Turn 2, the approved map: raises "Build A1", marked, above this turn's close.
      Assert.Equal("2 cards were approved automatically.", ApprovalMode.CloseClause(count.Shown(T, 1)));
      count.Add(T);

      // Turn 3, the approved build: raises nothing.
      Assert.Equal("2 cards were approved automatically.", ApprovalMode.CloseClause(count.Shown(T, 0)));

      // Turn 4, read-only: raises "Build B1", marked. Turn 5, its build.
      count.Add(T);
      Assert.Equal("3 cards were approved automatically.", ApprovalMode.CloseClause(count.Shown(T, 0)));

      // A message of the person's own starts a new run.
      count.Reset(T);
      Assert.Null(ApprovalMode.CloseClause(count.Shown(T, 0)));
    }

    /// <summary>What the first file showed: a turn that stops marks nothing, so it counts nothing it did not draw.</summary>
    [Fact]
    public void A_card_this_turn_did_not_mark_is_not_counted()
    {
      var count = new AutoApprovalCount();
      Assert.Equal(0, count.Shown("t", 0));
      Assert.Equal(0, count.Shown("t", -1));
      Assert.Equal(1, count.Shown("t", 1));
    }

    [Fact]
    public void The_panel_counts_what_it_drew()
    {
      var panel = Repo("Dwight", "UI", "DwightScaffold.cs");
      Assert.Contains("autoClause: ApprovalMode.CloseClause(_autoCount.Shown(threadId, autoToRun.Count))", panel);
      Assert.Contains("var autoLine = ApprovalMode.CloseClause(_autoCount.Shown(threadId, autoToRun.Count));", panel);
      Assert.DoesNotContain("CloseClause(_autoCount.Get(", panel);
    }

    // ---- 4. a delete that empties a group -----------------------------------------------------------------------------------------------------

    private static readonly Guid Panel = new Guid(1, 0, 0, new byte[8]);
    private static readonly Guid Slider = new Guid(2, 0, 0, new byte[8]);
    private static readonly Guid Header = new Guid(3, 0, 0, new byte[8]);

    private static EditGroupFacts Group(string name, params Guid[] members) =>
      new EditGroupFacts { Guid = Guid.NewGuid(), Name = name, Members = members };

    private static EditGroupFate Fate(Guid[] deleted, params EditGroupFacts[] groups) =>
      EditEmptyGroups.Decide(groups, deleted, g => !deleted.Contains(g));

    /// <summary>Rhino B's row: "remove the panel you just added", from the spot group Dwight made for it.</summary>
    [Fact]
    public void A_group_Dwight_made_goes_with_the_last_thing_in_it()
    {
      var spot = Group("Spot 4 - Panel", Panel);
      var fate = Fate(new[] { Panel }, spot);

      Assert.Equal(new[] { spot.Guid }, fate.Remove);
      Assert.Equal(new[] { "Spot 4 - Panel" }, fate.Removed);
      Assert.Empty(fate.LeftEmpty);
      Assert.Equal("The group \"Spot 4 - Panel\" held nothing else, so it went too.", EditEmptyGroups.Sentence(fate));
    }

    [Fact]
    public void A_group_that_still_holds_something_stays_and_is_not_mentioned()
    {
      var fate = Fate(new[] { Panel }, Group("Spot 4 - Panel and 1 more", Panel, Slider));

      Assert.Empty(fate.Remove);
      Assert.Empty(fate.LeftEmpty);
      Assert.Null(EditEmptyGroups.Sentence(fate));
    }

    /// <summary>A map's region keeps its header scribble, which no edit can delete, so emptying it of components does not empty it.</summary>
    [Fact]
    public void A_region_of_the_map_keeps_its_header_and_stays()
    {
      Assert.Empty(Fate(new[] { Panel }, Group("A1 - Teacher Answer Key", Header, Panel)).Remove);
    }

    /// <summary>The person's own group is theirs, so it is named and left.</summary>
    [Fact]
    public void A_group_of_the_persons_own_is_left_and_named()
    {
      var fate = Fate(new[] { Panel }, Group("My inputs", Panel));

      Assert.Empty(fate.Remove);
      Assert.Equal(new[] { "My inputs" }, fate.LeftEmpty);
      Assert.Equal("The group \"My inputs\" is now empty; it is not one Dwight made, so it was left.", EditEmptyGroups.Sentence(fate));

      Assert.Contains("with no name", EditEmptyGroups.Sentence(Fate(new[] { Panel }, Group("", Panel))));
    }

    /// <summary>An empty group this edit did not empty is not its to judge, or to remove.</summary>
    [Fact]
    public void A_group_this_delete_did_not_touch_is_not_judged()
    {
      var empty = EditEmptyGroups.Decide(new[] { Group("Spot 9 - Old", Slider) }, new[] { Panel }, _ => false);

      Assert.Empty(empty.Remove);
      Assert.Empty(empty.LeftEmpty);
      Assert.Empty(EditEmptyGroups.Decide(new[] { Group("Spot 9 - Old") }, new[] { Panel }, _ => false).Remove);
      Assert.Empty(EditEmptyGroups.Decide(null, new[] { Panel }, _ => false).Remove);
    }

    [Fact]
    public void Several_groups_are_said_together()
    {
      var fate = Fate(new[] { Panel, Slider }, Group("Spot 1 - Panel", Panel), Group("Spot 2 - Count", Slider));
      Assert.Equal("The groups \"Spot 1 - Panel\", \"Spot 2 - Count\" held nothing else, so they went too.", EditEmptyGroups.Sentence(fate));

      var left = Fate(new[] { Panel, Slider }, Group("Mine", Panel), Group("Also mine", Slider));
      Assert.Equal("The groups \"Mine\", \"Also mine\" are now empty; they are not ones Dwight made, so they were left.", EditEmptyGroups.Sentence(left));
    }

    private static EditPlan OneDelete() => new EditPlan
    {
      Steps = new List<EditStep> { new EditStep { Row = 1, Kind = EditOpKind.Delete, Text = "Delete Panel (5) and every wire on it" } },
    };

    /// <summary>The claim comes from the count: the report says what was checked and what the canvas went from and to, never "nothing else".</summary>
    [Fact]
    public void The_report_states_the_count_it_read_and_never_says_nothing_else()
    {
      var fate = Fate(new[] { Panel }, Group("Spot 4 - Panel", Panel));
      var report = EditOpsAsk.AppliedReport(OneDelete(), 1, 1291, 1289, fate);

      Assert.Contains("the canvas went from 1,291 objects to 1,289.", report);
      Assert.Contains("The group \"Spot 4 - Panel\" held nothing else, so it went too.", report);
      Assert.Contains("wires and values differ only as those changes should", report);
      Assert.DoesNotContain("nothing else on the canvas", report);
      Assert.EndsWith("One Ctrl-Z undoes all of it.", report);
    }

    [Fact]
    public void A_wiring_edit_says_the_count_stood()
    {
      var wire = new EditPlan { Steps = new List<EditStep> { new EditStep { Row = 1, Kind = EditOpKind.Wire, Text = "Wire A to B" } } };
      Assert.Contains("the canvas still holds 1,289 objects.", EditOpsAsk.AppliedReport(wire, 1, 1289, 1289));
    }

    [Theory]
    [InlineData(1291, 1, 1, 1289)]   // Rhino B's row, as it now ends
    [InlineData(1291, 1, 0, 1290)]   // a group of the person's own stays
    [InlineData(1289, 0, 0, 1289)]   // a wiring edit
    public void The_count_the_edit_should_leave(int before, int deleted, int groups, int expected)
    {
      Assert.Equal(expected, EditPrediction.ExpectedObjects(before, deleted, groups));
    }

    [Fact]
    public void A_count_that_is_wrong_is_said_and_taken_back()
    {
      Assert.Equal("Dwight made the changes, checked the result, and put everything back because the canvas held 1290 objects where those changes should have left 1289. Nothing on the canvas is changed.",
                   EditPrediction.CountRevertText(1289, 1290));

      var apply = Repo("Dwight", "Agent", "GhEditOps.cs");
      var check = apply.IndexOf("if (objectsAfter != expectedObjects)", StringComparison.Ordinal);
      var rollback = apply.IndexOf("SimplifyApply.Rollback(d, actions);", check, StringComparison.Ordinal);
      var record = apply.IndexOf("d.UndoUtil.RecordEvent(UndoName, actions)", StringComparison.Ordinal);
      Assert.True(0 < check && check < rollback && rollback < record, "the count is checked and taken back before the one undo record");
    }

    /// <summary>The order that makes it true, read by its text: counted and copied before the first delete, the group in the same record, and the report given what was read.</summary>
    [Fact]
    public void The_apply_counts_before_it_deletes_and_keeps_one_record()
    {
      var apply = Repo("Dwight", "Agent", "GhEditOps.cs");
      var count = apply.IndexOf("var objectsBefore = d.Objects.Count;", StringComparison.Ordinal);
      var copy = apply.IndexOf("Members = (g.ObjectIDs ?? new List<Guid>()).ToList()", StringComparison.Ordinal);
      var delete = apply.IndexOf("d.RemoveObject(obj, false);", StringComparison.Ordinal);
      var decide = apply.IndexOf("EditEmptyGroups.Decide(groupsBefore, deleted,", StringComparison.Ordinal);
      var removeGroup = apply.IndexOf("d.RemoveObject(group, false);", StringComparison.Ordinal);
      var actual = apply.IndexOf("var actual = BehaviourFingerprint.ReadDoc(d);", StringComparison.Ordinal);
      Assert.True(0 < count && count < delete && 0 < copy && copy < delete && delete < decide && decide < removeGroup && removeGroup < actual);

      var withGroup = apply.Substring(decide, removeGroup - decide);
      Assert.Contains("actions.Add(new GH_RemoveObjectAction(group));", withGroup);

      Assert.Contains("EditOpsAsk.AppliedReport(plan, cardTotal, objectsBefore, objectsAfter, groups)", apply);
    }

    /// <summary>A consent card is a contract about scope (#338): the group goes only because the card, the prompt and the page all say it can.</summary>
    [Fact]
    public void The_card_the_prompt_and_the_page_say_a_group_can_go()
    {
      var plan = OneDelete();
      Assert.Contains("a group Dwight made goes too if the delete leaves it empty", ConsentCard.EditAuthority(plan));
      Assert.DoesNotContain("a group Dwight made", ConsentCard.EditAuthority(new EditPlan { Steps = new List<EditStep> { new EditStep { Kind = EditOpKind.Wire } } }));
      Assert.Contains("a group Dwight made that a delete leaves empty, which goes too", ConsentCard.Authority("edit"));

      Assert.Contains("except that a delete which empties a group Dwight made removes that group", AgentHostTools.KindScope["edit"].Cannot);
      Assert.Contains("A delete that empties a group Dwight made removes that group too; a group of your own stays, and the report names it.", Repo("docs", "public", "build.md"));
    }

    // ---- 3. the overlap ------------------------------------------------------------------------------------------------------------------------

    private static RegionTidy.TidyBox Box(string address, double x, double y, double w, double h, string label = null) =>
      new RegionTidy.TidyBox(address, x, y, w, h, label);

    private static IReadOnlyList<(string Grown, string Neighbour)> Crowded(IReadOnlyList<RegionTidy.TidyBox> members, params (RegionTidy.TidyBox Box, bool IsGroup)[] others) =>
      RegionTidy.Crowding(RegionTidy.CrowdingBoxes(new[] { ("A1", members) }, others), new HashSet<string> { "A1" });

    private static readonly IReadOnlyList<RegionTidy.TidyBox> A1Members = new[] { Box("A1", 1000, 0, 100, 50), Box("A1", 1200, 100, 100, 50) };

    /// <summary>What the first file read: a region clear of every object, with a group's box across it. There is no work there.</summary>
    [Fact]
    public void A_group_box_with_no_object_under_it_is_not_work()
    {
      Assert.Empty(Crowded(A1Members, (Box("", 1100, -50, 500, 300, "Students"), true)));
      Assert.Null(RegionTidy.CrowdingText(Crowded(A1Members, (Box("", 1100, -50, 500, 300, "Students"), true)), true));
    }

    /// <summary>The same box as an ordinary object would be real work, so the test above measures the group and not the geometry.</summary>
    [Fact]
    public void The_same_box_as_an_object_is_work()
    {
      Assert.Single(Crowded(A1Members, (Box("", 1100, -50, 500, 300, "Big Panel"), false)));
    }

    /// <summary>The members of that group are themselves, so real work under the region is still said, by its name.</summary>
    [Fact]
    public void A_real_object_under_the_region_is_said_by_its_name()
    {
      var pairs = Crowded(A1Members, (Box("", 1100, -50, 500, 300, "Students"), true), (Box("", 1050, 20, 60, 40, "Series (Srs)"), false));

      Assert.Equal(new[] { ("A1", "\"Series (Srs)\"") }, pairs.Select(p => (p.Grown, p.Neighbour)));
      Assert.Equal("A1 now overlaps \"Series (Srs)\". The canvas was not re-spaced - Reorganise will tidy it.", RegionTidy.CrowdingText(pairs));
    }

    /// <summary>Each member is compared by itself, so a stale group box cannot be an input, and something sitting in the gap between two members has not been hit.</summary>
    [Fact]
    public void A_hole_in_a_region_is_not_an_overlap()
    {
      var boxes = RegionTidy.CrowdingBoxes(new[] { ("A1", A1Members) }, null);
      Assert.Equal(new[] { "A1", "A1" }, boxes.Select(b => b.Address));
      Assert.Equal((1000d, 0d, 100d, 50d), (boxes[0].X, boxes[0].Y, boxes[0].W, boxes[0].H));

      Assert.Empty(Crowded(A1Members, (Box("", 1110, 60, 80, 30, "In the gap"), false)));
    }

    /// <summary>Two members of one region can touch without it being news.</summary>
    [Fact]
    public void Members_of_one_region_do_not_overlap_each_other_as_far_as_the_sentence_goes()
    {
      Assert.Empty(Crowded(new[] { Box("A1", 0, 0, 100, 50), Box("A1", 50, 10, 100, 50) }));
    }

    // Real-looking canvases, both directions: the sentence must follow the objects.
    private static (RegionTidy.TidyBox Box, bool IsFrame)[] Originals()
    {
      var work = new List<(RegionTidy.TidyBox, bool)>();
      for (var i = 0; i < 1289; i++) work.Add((Box("", (i % 60) * 730, (i / 60) * 370, 150, 60, "Original " + i), false));
      work.Add((Box("", -20, -20, 44270, 8040, "Everything"), true));        // a person's group around all of it
      work.Add((Box("", 40000, -500, 5200, 2000, "Notes"), true));            // and one reaching past the drawing's edge
      work.Add((Box("", 30000, -900, 16000, 9000, "An arrow"), true));        // a sketch across it
      return work.ToArray();
    }

    /// <summary>The first two files: new work to the right of the drawing, with gaps of 262 to 415 units, under groups that reach past its edge. It fired on these. Nothing touched, so it says nothing.</summary>
    [Theory]
    [InlineData(43450, 262)]
    [InlineData(43450, 415)]
    public void New_work_clear_of_the_drawing_says_nothing(double drawingRight, double gap)
    {
      var x = drawingRight + gap;
      var members = new[] { Box("A1", x, 40, 180, 60), Box("A1", x + 240, 40, 120, 90), Box("A1", x, 160, 150, 20) };

      var pairs = Crowded(members, Originals());

      Assert.Empty(pairs);
      Assert.Null(RegionTidy.CrowdingText(pairs, true));
      Assert.Null(RegionTidy.CrowdingText(pairs, false));
    }

    /// <summary>The third file: five new components inside the old work's bounds, three old objects under them. It said nothing on this. Now it says so, and names them.</summary>
    [Fact]
    public void New_work_on_top_of_the_drawing_says_so_and_names_what_it_hit()
    {
      var built = new[]
      {
        Box("Spot 1", 7320, 1490, 150, 60),   // on Original 250
        Box("Spot 1", 8040, 1490, 150, 60),   // on Original 251
        Box("Spot 1", 8780, 1480, 150, 60),   // on Original 252
        Box("Spot 1", 7700, 1600, 100, 40),   // in clear space
        Box("Spot 1", 8500, 1600, 100, 40),   // in clear space
      };
      var work = Originals();

      var pairs = RegionTidy.Crowding(RegionTidy.CrowdingBoxes(new[] { ("Spot 1", (IReadOnlyList<RegionTidy.TidyBox>)built) }, work), new HashSet<string> { "Spot 1" });

      Assert.Equal(new[] { "\"Original 250\"", "\"Original 251\"", "\"Original 252\"" }, pairs.Select(p => p.Neighbour).OrderBy(n => n, StringComparer.Ordinal).ToArray());
      Assert.Equal("Spot 1 now overlaps \"Original 250\", \"Original 251\" and \"Original 252\". The canvas was not re-spaced - Reorganise will tidy it.", RegionTidy.CrowdingText(pairs));
    }

    /// <summary>A spot has no tidy, which is why it was never measured. It now is, on both channels, before the one read-only turn.</summary>
    [Fact]
    public void A_spot_is_measured_when_it_lands_and_the_model_reads_it_too()
    {
      var panel = Repo("Dwight", "UI", "DwightScaffold.cs");
      var measure = panel.IndexOf("var overlap = GhRegionTidy.SpotOverlapNote(doc, plan.PreFillSpot, ledger);", StringComparison.Ordinal);
      var said = panel.IndexOf("if (overlap != null) AppendPluginHtml(wp, doc, threadId, System.Net.WebUtility.HtmlEncode(overlap));", StringComparison.Ordinal);
      var landed = panel.IndexOf("plan.PreFillSpotGrouped) + (overlap == null ? \"\" : \" \" + overlap);", StringComparison.Ordinal);
      var turn = panel.IndexOf("RunTurn(wp, doc, threadId, TurnCapability.ReadOnly);   // one re-read, holding no approval", StringComparison.Ordinal);
      Assert.True(0 < measure && measure < said && said < landed && landed < turn);

      var tidy = Repo("Dwight", "Agent", "GhRegionTidy.cs");
      Assert.Contains("OverlapSentence(doc, builtAddresses, null, null, respaced)", tidy);
      Assert.Contains("OverlapSentence(doc, new[] { spot }, spot, ledger?.PlacedAt(spot), false)", tidy);
    }

    [Fact]
    public void The_ledger_says_what_it_placed_at_an_address()
    {
      var ledger = new BuildLedger(new object());
      var placed = new Guid(9, 0, 0, new byte[8]);
      ledger.CommitBuild("Spot 1", new Dictionary<Guid, string> { [placed] = "t" });

      Assert.Equal(new[] { placed }, ledger.PlacedAt("Spot 1"));
      Assert.Empty(ledger.PlacedAt("Spot 2"));
      Assert.Empty(ledger.PlacedAt(null));
    }

    [Fact]
    public void A_region_with_nothing_measured_and_an_object_with_no_size_compare_as_nothing()
    {
      var none = new RegionTidy.TidyBox[0];
      Assert.Empty(RegionTidy.CrowdingBoxes(new[] { ("A1", (IReadOnlyList<RegionTidy.TidyBox>)none), ("A2", (IReadOnlyList<RegionTidy.TidyBox>)new[] { Box("A2", 0, 0, 0, 0) }) }, new[] { (Box("", 0, 0, 0, 10), false) }));
      Assert.Empty(RegionTidy.CrowdingBoxes(null, null));
    }

    [Fact]
    public void Two_regions_compare_by_their_members_and_touching_is_not_overlapping()
    {
      var a = (IReadOnlyList<RegionTidy.TidyBox>)new[] { Box("A1", 0, 0, 100, 100) };
      var overlapping = RegionTidy.CrowdingBoxes(new[] { ("A1", a), ("B1", (IReadOnlyList<RegionTidy.TidyBox>)new[] { Box("B1", 50, 0, 100, 100) }) }, null);
      var flush = RegionTidy.CrowdingBoxes(new[] { ("A1", a), ("B1", (IReadOnlyList<RegionTidy.TidyBox>)new[] { Box("B1", 100, 0, 100, 100) }) }, null);

      Assert.Equal(new[] { ("A1", "B1") }, RegionTidy.Crowding(overlapping, new HashSet<string> { "A1" }).Select(p => (p.Grown, p.Neighbour)));
      Assert.Empty(RegionTidy.Crowding(flush, new HashSet<string> { "A1" }));
    }

    [Fact]
    public void Each_region_names_up_to_three_and_counts_the_rest_and_a_name_is_said_once()
    {
      var one = new List<(string, string)> { ("A1", "\"a\"") };
      var two = new List<(string, string)> { ("A1", "B1"), ("A1", "\"a\"") };
      var three = new List<(string, string)> { ("A1", "\"a\""), ("A1", "\"b\""), ("A1", "\"c\"") };
      var five = new List<(string, string)> { ("A1", "\"a\""), ("A1", "\"b\""), ("A1", "\"c\""), ("A1", "\"d\""), ("A1", "\"e\""), ("B1", "\"a\"") };

      Assert.StartsWith("A1 now overlaps \"a\".", RegionTidy.CrowdingText(one));
      Assert.StartsWith("A1 now overlaps B1 and \"a\".", RegionTidy.CrowdingText(two));
      Assert.StartsWith("A1 now overlaps \"a\", \"b\" and \"c\".", RegionTidy.CrowdingText(three));
      Assert.StartsWith("A1 now overlaps \"a\", \"b\", \"c\" and 2 more; B1 now overlaps \"a\".", RegionTidy.CrowdingText(five));

      var same = Crowded(A1Members, (Box("", 1050, 20, 60, 40, "Panel"), false), (Box("", 1060, 25, 60, 40, "Panel"), false));
      Assert.Single(same);
    }

    /// <summary>A label-less box still reads as before. A group or a sketch around the region frames it and is not compared; a large real object under it is a real overlap, which the old corner rule hid.</summary>
    [Fact]
    public void An_unnamed_object_reads_as_other_work_and_only_a_frame_is_let_off()
    {
      var unnamed = Crowded(A1Members, (Box("", 1050, 20, 60, 40), false));
      Assert.Equal(new[] { ("A1", RegionTidy.OtherWork) }, unnamed.Select(p => (p.Grown, p.Neighbour)));

      Assert.Empty(Crowded(A1Members, (Box("", 0, -100, 3000, 3000, "Frame"), true)));
      Assert.Single(Crowded(A1Members, (Box("", 0, -100, 3000, 3000, "A huge panel"), false)));
    }

    /// <summary>On the first file the region named was the one that had moved, under "These stayed". The tail now holds whichever it was.</summary>
    [Fact]
    public void After_a_respace_the_sentence_does_not_say_these_stayed()
    {
      var text = RegionTidy.CrowdingText(new List<(string, string)> { ("A1", "\"Series\"") }, true);

      Assert.Equal("A1 now overlaps \"Series\". Dwight moved regions apart where it could without moving your work, and these still overlap - Reorganise will tidy it.", text);
      Assert.DoesNotContain("stayed", text);
      Assert.DoesNotContain("other regions", text);
    }

    [Fact]
    public void The_tidy_reads_objects_after_the_forced_layout_and_never_a_groups_own_box()
    {
      var tidy = Repo("Dwight", "Agent", "GhRegionTidy.cs");
      var start = tidy.IndexOf("private static string OverlapSentence(", StringComparison.Ordinal);
      var end = tidy.IndexOf("private static Dictionary<Guid, TidyPoint> Pivots(", StringComparison.Ordinal);
      var note = tidy.Substring(start, end - start);

      Assert.True(note.IndexOf("ForceRegionLayout(doc);", StringComparison.Ordinal) < note.IndexOf("Attributes.Bounds", StringComparison.Ordinal));
      Assert.DoesNotContain("g.Attributes.Bounds", note);
      Assert.Contains("member is GH_Group || !owned.Add(id)", note);
      Assert.Contains("o is GH_Group || o is GH_Markup", note);
      Assert.Contains("RegionTidy.CrowdingBoxes(regions, others)", note);
      Assert.Contains("LivePorts.PortLabel(o.Name, o.NickName)", note);
    }
  }
}
