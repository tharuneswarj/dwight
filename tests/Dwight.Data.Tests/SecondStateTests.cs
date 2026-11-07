using System.Collections.Generic;
using Dwight.Eval;
using Xunit;

namespace Dwight.Data.Tests
{
  /// <summary>
  /// Phase-8 WP-0 Stage C (#93c/#99/#101): the overfit measurement's two LABEL-CRITICAL pure cores. The de-
  /// experimentalization decision (#99) rests on the second-state number, so the scoring rule is tested exactly,
  /// headless, before it ever runs on the paid corpus.
  ///
  /// The perturbation is HARNESS-ONLY (#93a - the product holds at single-input verify): perturb the EXTERNAL value
  /// sources, re-solve, compare 1:1 again, restore, file never saved.
  /// </summary>
  public class PerturbationSequenceTests
  {
    [Fact]
    public void A_float_source_gets_in_range_candidates_all_different_from_current()
    {
      var cands = PerturbationSequence.Candidates(0m, 10m, 3m, integer: false);
      Assert.NotEmpty(cands);
      Assert.All(cands, c => Assert.True(c >= 0m && c <= 10m));   // in range
      Assert.DoesNotContain(3m, cands);                            // never a no-op perturbation
    }

    [Fact]
    public void The_midpoint_is_the_first_candidate_when_it_differs_from_current()
    {
      var cands = PerturbationSequence.Candidates(0m, 10m, 3m, integer: false);
      Assert.Equal(5m, cands[0]);   // deterministic: midpoint first
    }

    [Fact]
    public void An_integer_source_yields_whole_numbers_and_skips_a_no_op()
    {
      // [0,10] current 5: the midpoint (5) IS the current value -> skipped; the rest are whole numbers.
      var cands = PerturbationSequence.Candidates(0m, 10m, 5m, integer: true);
      Assert.DoesNotContain(5m, cands);
      Assert.All(cands, c => Assert.Equal(decimal.Truncate(c), c));   // whole numbers
    }

    [Fact]
    public void A_degenerate_source_min_equals_max_has_no_candidates()
    {
      // nothing to perturb TO - this becomes a no-valid-perturbation row, scored on state 1 alone (#101).
      Assert.Empty(PerturbationSequence.Candidates(5m, 5m, 5m, integer: false));
    }

    [Fact]
    public void Candidates_are_deduplicated()
    {
      var cands = PerturbationSequence.Candidates(0m, 4m, 0m, integer: true);
      Assert.Equal(cands.Count, new System.Collections.Generic.HashSet<decimal>(cands).Count);
    }

    // #121(a): a range-less source (a numeric PANEL - the corpus house style: panel -> named relay -> consumer) has
    // no min/max, so its deterministic candidates are fixed MULTIPLES of the current value. Same #101(c) validity
    // gate catches a candidate that breaks the definition; different generator.
    [Fact]
    public void A_range_less_source_perturbs_by_multiples_of_its_current_value()
    {
      var cands = PerturbationSequence.CandidatesFromCurrent(60m);
      Assert.NotEmpty(cands);
      Assert.DoesNotContain(60m, cands);          // never a no-op
      Assert.Contains(30m, cands);                // 0.5x
      Assert.Contains(90m, cands);                // 1.5x
    }

    [Fact]
    public void A_zero_valued_range_less_source_is_degenerate_no_multiple_moves_it()
    {
      // 0 * anything == 0 == current, so there is nowhere to perturb - a no-valid-perturbation row (scored state 1).
      Assert.Empty(PerturbationSequence.CandidatesFromCurrent(0m));
    }

    [Fact]
    public void Range_less_candidates_work_for_a_negative_current_and_are_deduplicated()
    {
      var cands = PerturbationSequence.CandidatesFromCurrent(-4m);
      Assert.NotEmpty(cands);
      Assert.DoesNotContain(-4m, cands);
      Assert.Equal(cands.Count, new System.Collections.Generic.HashSet<decimal>(cands).Count);
    }
  }

  public class SecondStateScoreTests
  {
    // signature: Decide(built, state1Match, hasExternalInputs, hasPerturbable, perturbationValid, state2Match)
    [Fact]
    public void Not_built_fails_state_one()
    {
      var v = SecondStateScore.Decide(built: false, state1Match: false, hasExternalInputs: true, hasPerturbable: true, perturbationValid: true, state2Match: false);
      Assert.False(v.Verified);
      Assert.Equal(SecondStateBucket.FailedState1, v.Bucket);
      Assert.False(v.CountsForOverfit);
    }

    [Fact]
    public void Built_but_state_one_mismatch_fails_state_one()
    {
      var v = SecondStateScore.Decide(built: true, state1Match: false, hasExternalInputs: true, hasPerturbable: true, perturbationValid: true, state2Match: true);
      Assert.False(v.Verified);
      Assert.Equal(SecondStateBucket.FailedState1, v.Bucket);
    }

    [Fact]
    public void No_external_inputs_is_SELF_CONTAINED_verified_and_NOT_a_tripwire_member()
    {
      // A1 (#133): a single-point input space - state-1 verification is complete by construction. Verified on state 1,
      // and NOT the tripwire's member (it is not a measurement gap - there is nothing to perturb).
      var v = SecondStateScore.Decide(built: true, state1Match: true, hasExternalInputs: false, hasPerturbable: false, perturbationValid: false, state2Match: false);
      Assert.True(v.Verified);
      Assert.Equal(SecondStateBucket.SelfContained, v.Bucket);
      Assert.False(v.CountsForOverfit);
    }

    [Fact]
    public void Has_inputs_but_none_perturbable_is_NO_PERTURBABLE_the_genuine_gap()
    {
      // #133: HAS external inputs, none reach a perturbable value source - the real measurement gap, the tripwire's
      // member. Still verified on state 1 (we couldn't check overfit), never failed for its input types.
      var v = SecondStateScore.Decide(built: true, state1Match: true, hasExternalInputs: true, hasPerturbable: false, perturbationValid: false, state2Match: false);
      Assert.True(v.Verified);
      Assert.Equal(SecondStateBucket.NoPerturbable, v.Bucket);
      Assert.False(v.CountsForOverfit);
    }

    [Fact]
    public void No_valid_perturbation_scores_on_state_one_alone()
    {
      var v = SecondStateScore.Decide(built: true, state1Match: true, hasExternalInputs: true, hasPerturbable: true, perturbationValid: false, state2Match: false);
      Assert.True(v.Verified);
      Assert.Equal(SecondStateBucket.NoValidPerturbation, v.Bucket);
      Assert.False(v.CountsForOverfit);
    }

    [Fact]
    public void A_valid_perturbation_that_still_matches_is_second_state_verified()
    {
      var v = SecondStateScore.Decide(built: true, state1Match: true, hasExternalInputs: true, hasPerturbable: true, perturbationValid: true, state2Match: true);
      Assert.True(v.Verified);
      Assert.Equal(SecondStateBucket.SecondStateVerified, v.Bucket);
      Assert.True(v.CountsForOverfit);
    }

    [Fact]
    public void A_valid_perturbation_that_now_diverges_is_OVERFIT_and_not_verified()
    {
      var v = SecondStateScore.Decide(built: true, state1Match: true, hasExternalInputs: true, hasPerturbable: true, perturbationValid: true, state2Match: false);
      Assert.False(v.Verified);
      Assert.Equal(SecondStateBucket.Overfit, v.Bucket);
      Assert.True(v.CountsForOverfit);
    }

    [Fact]
    public void Decide_never_emits_the_State1NotReproduced_anomaly_bucket()
    {
      // F8 (#129): State1NotReproduced is stamped by the orchestration directly, NEVER a Decide outcome.
      foreach (var built in new[] { true, false })
        foreach (var s1 in new[] { true, false })
          foreach (var hasIn in new[] { true, false })
            foreach (var hasP in new[] { true, false })
              foreach (var valid in new[] { true, false })
                foreach (var s2 in new[] { true, false })
                  Assert.NotEqual(SecondStateBucket.State1NotReproduced,
                    SecondStateScore.Decide(built, s1, hasIn, hasP, valid, s2).Bucket);
    }
  }

  /// <summary>#134: the broadened root walk decision, headless - collect value sources across the upstream cone,
  /// recurse through pass-throughs (relays AND computed components), dedupe, bound by depth + cycle guard + root cap.</summary>
  public class RootConeWalkTests
  {
    // a tiny int-graph: kind by id, upstream by id.
    private static (List<int> roots, bool trunc) Walk(Dictionary<int, NodeKind> kind, Dictionary<int, int[]> up,
      int[] seeds, int depth = 16, int cap = 32)
      => RootConeWalk.Collect(seeds,
           n => kind.TryGetValue(n, out var k) ? k : NodeKind.Passthrough,
           n => up.TryGetValue(n, out var u) ? u : new int[0],
           n => n, depth, cap);

    [Fact]
    public void A_value_source_one_relay_up_is_collected()
    {
      var (roots, _) = Walk(new() { [1] = NodeKind.Passthrough, [2] = NodeKind.ValueSource },
                            new() { [1] = new[] { 2 } }, new[] { 1 });
      Assert.Equal(new[] { 2 }, roots);
    }

    [Fact]
    public void A_computed_component_recurses_into_ALL_its_inputs_the_cone()
    {
      // 1 = a Multiplication fed by two sliders -> BOTH are collected (the broadened cone, #134).
      var (roots, _) = Walk(new() { [1] = NodeKind.Passthrough, [2] = NodeKind.ValueSource, [3] = NodeKind.ValueSource },
                            new() { [1] = new[] { 2, 3 } }, new[] { 1 });
      Assert.Equal(new HashSet<int> { 2, 3 }, new HashSet<int>(roots));
    }

    [Fact]
    public void Roots_are_deduped_when_two_paths_reach_one_source()
    {
      // siteLength (one source) feeding two boundary ports -> one root (#126a).
      var (roots, _) = Walk(new() { [1] = NodeKind.Passthrough, [2] = NodeKind.Passthrough, [3] = NodeKind.ValueSource },
                            new() { [1] = new[] { 3 }, [2] = new[] { 3 } }, new[] { 1, 2 });
      Assert.Single(roots);
      Assert.Equal(3, roots[0]);
    }

    [Fact]
    public void A_cycle_terminates_and_does_not_double_count()
    {
      var (roots, _) = Walk(new() { [1] = NodeKind.Passthrough, [2] = NodeKind.Passthrough, [3] = NodeKind.ValueSource },
                            new() { [1] = new[] { 2 }, [2] = new[] { 1, 3 } }, new[] { 1 });
      Assert.Equal(new[] { 3 }, roots);
    }

    [Fact]
    public void The_depth_bound_stops_a_chain_before_a_far_source()
    {
      // 1->2->3->4(source); depth bound 2 reaches node 3 (depth 2) but not node 4 (depth 3).
      var (roots, _) = Walk(new() { [1] = NodeKind.Passthrough, [2] = NodeKind.Passthrough, [3] = NodeKind.Passthrough, [4] = NodeKind.ValueSource },
                            new() { [1] = new[] { 2 }, [2] = new[] { 3 }, [3] = new[] { 4 } }, new[] { 1 }, depth: 2);
      Assert.Empty(roots);
    }

    [Fact]
    public void A_dead_end_passthrough_persistent_data_param_collects_nothing()
    {
      // node 2 is a persistent-data param: a Passthrough with NO upstream -> stops, non-perturbable (#134).
      var (roots, _) = Walk(new() { [1] = NodeKind.Passthrough, [2] = NodeKind.Passthrough },
                            new() { [1] = new[] { 2 } }, new[] { 1 });
      Assert.Empty(roots);
    }

    [Fact]
    public void The_root_cap_truncates_and_flags_it_no_silent_caps()
    {
      var kind = new Dictionary<int, NodeKind> { [1] = NodeKind.Passthrough };
      kind[2] = kind[3] = kind[4] = NodeKind.ValueSource;
      var (roots, trunc) = Walk(kind, new() { [1] = new[] { 2, 3, 4 } }, new[] { 1 }, cap: 2);
      Assert.Equal(2, roots.Count);
      Assert.True(trunc);
    }
  }
}
