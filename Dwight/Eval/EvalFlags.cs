namespace Dwight.Eval
{
  /// <summary>
  /// RR20 #56: the BEFORE-run must isolate ONE VARIABLE.
  ///
  /// The trap the architect caught: a before-run by CHECKING OUT 4e84213 would revert the payload AND everything
  /// else that moved with it - including the `GooDigest` unwrap fix (058059b). That commit's verify-rate is
  /// artificially near-ZERO because the comparator falsely rejected everything, so RR20 would look miraculous and
  /// we would be scoring OUR OWN BUG FIX as a prompt improvement. That is not a delta, it is a coincidence.
  ///
  /// So the before-run is HEAD with only the RR20 INPUTS switched off, and everything else - the comparator, the
  /// digest, the harness, the corpus, the model - held constant. Same binary, same everything, one variable.
  ///
  /// These are EVAL-ONLY switches. They default to ON, so the product is never affected; the harness flips them.
  /// </summary>
  internal static class EvalFlags
  {
    /// <summary>RR20 #40/#50: per-port `connects` / `runtime` / `constant`, and a constant's `feeds`. Off = the
    /// model is told the interface's SHAPE but not its MEANING - the state that made it read a 0.75 Factor as a
    /// count.</summary>
    public static bool PortSemantics = true;

    /// <summary>RR20 #49: the reflected RhinoCommon signatures seeded into the prompt. Off = the model works from
    /// memory of the API.</summary>
    public static bool ApiSeed = true;

    /// <summary>RR20 #51: hand the model its own compiler error (plus reflected signatures) and let it retry. Off =
    /// one shot, which is what the user used to get - and a preview of code that may not build.</summary>
    public static bool CompileRepair = true;

    /// <summary>Phase-8 WP-0 (#94): the SECOND oracle - feed the CLASS-ONLY output-diff back and let the model retry
    /// within the shared budget. This is the ONE variable of the WP-0 measurement: the RR20 flags are held ON in
    /// both WP-0 arms, and only this toggles. WP-0-before = this OFF (the shipped ~80/~38 row); WP-0-after = this
    /// ON. Set independently of <see cref="AllOn"/>/<see cref="AllOff"/> so the arm's RR20 state and the oracle
    /// state never get entangled.</summary>
    public static bool OutputOracle = true;

    /// <summary>Phase-8 WP-0 Stage C (#101/#127): run the SECOND-INPUT-STATE overfit check on every state-1-verified
    /// row (perturb the upstream value sources, re-solve, compare 1:1 again, restore). Harness-only (the product
    /// holds at single-input, #93a) and independent of the RR20 arm and the oracle - it scores overfit on BOTH the
    /// oracle-off base-rate run and the oracle-on arms, and on the free saved-script shakedown (#127). Off only when
    /// deliberately measuring state-1 alone.</summary>
    public static bool SecondState = true;

    public static void AllOn() { PortSemantics = true; ApiSeed = true; CompileRepair = true; }
    public static void AllOff() { PortSemantics = false; ApiSeed = false; CompileRepair = false; }

    public static string Describe() => $"portSemantics={PortSemantics}, apiSeed={ApiSeed}, compileRepair={CompileRepair}, outputOracle={OutputOracle}, secondState={SecondState}";
  }
}
