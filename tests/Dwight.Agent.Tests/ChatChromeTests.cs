using System;
using Dwight.Agent;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Defect 3, found at the WP-4 runtime gate: the chat greeted every user with "the build agent arrives
  /// in a later Phase-8 work-package" - and kept saying it for the whole of the work-package that shipped
  /// the build agent. The first person to complete a real six-region build had been told, one message
  /// earlier, that the thing they had just used did not exist yet.
  ///
  /// It is #316's class pointed at the HUMAN rather than the model: a claim about our own behaviour that
  /// can go stale in silence. #316's remedy applies unchanged - the claim carries a test on the
  /// BEHAVIOUR's side, so the assertion below is not "the greeting says the right words" but "the
  /// greeting agrees with what the surface can actually be asked to do".
  /// </summary>
  public class ChatChromeTests
  {
    /// <summary>
    /// The behaviour-side tie. <see cref="AgentHostTools.Kinds"/> is the enumerable truth about what this
    /// surface can be asked for; the greeting is the claim. If building ever leaves that list, this fails
    /// and somebody reads the greeting - which is the whole mechanism the original defect lacked.
    /// </summary>
    [Fact]
    public void Greeting_OffersBuilding_BecauseTheSurfaceCanActuallyBuild()
    {
      Assert.Contains("build", AgentHostTools.Kinds);                                    // the behaviour
      Assert.Contains("build", ChatChrome.Greeting, StringComparison.OrdinalIgnoreCase); // ...and the claim
    }

    /// <summary>
    /// A DEFERRAL IS A DATED CLAIM. It acquires an owner the day the deferred thing arrives, and nobody is
    /// watching that day. The list below is not a style rule - it is the exact vocabulary that shipped the
    /// defect, kept so the next person reaching for it is stopped by a red test rather than by a gate.
    /// </summary>
    [Fact]
    public void Greeting_PromisesNothingForLater()
    {
      foreach (var deferral in new[] { "later", "not yet", "coming", "will arrive", "arrives", "future", "work-package", "phase" })
        Assert.DoesNotContain(deferral, ChatChrome.Greeting, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The two notices are shown INSTEAD of doing the work, so an empty one is a blank stare
    /// where an explanation belongs. Cheap, and it is the failure the surface cannot report itself.</summary>
    [Fact]
    public void EveryPieceOfChrome_ActuallySaysSomething()
    {
      Assert.NotEmpty(ChatChrome.Greeting.Trim());
      Assert.NotEmpty(ChatChrome.NoDocument.Trim());
      Assert.NotEmpty(ChatChrome.NoKey.Trim());
    }
  }
}
