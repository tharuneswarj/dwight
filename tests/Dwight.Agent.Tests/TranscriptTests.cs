using System.Collections.Generic;
using Dwight.Agent;
using Dwight.Llm;
using Dwight.Session;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// #296/#297 - the transcript, end to end and headless.
  ///
  /// The defect that produced these tests: the FIRST turn of a session reached Anthropic with an empty
  /// <c>messages</c> array and came back as a provider error. Two things were missing, and both are
  /// covered here. The transcript a fresh thread produces had never been asserted at all - the greeting is
  /// chrome (#228), so a new thread's only conversational message is the user's own, and nothing checked
  /// that it survived to the wire. And the loop trusted whatever it was handed, so when the list was wrong
  /// the provider explained our defect to us in its words instead of ours.
  ///
  /// These run over <see cref="ChatToTranscript"/> and <see cref="AgentLoopCore.CheckTranscript"/> - the
  /// same code the panel calls, not a restatement of it.
  /// </summary>
  public class TranscriptTests
  {
    private static StoredMessage Chrome(string html) =>
      new StoredMessage { Who = "dw", Html = html, Src = null };      // greeting / notice / card: no source

    private static StoredMessage User(string text) =>
      new StoredMessage { Who = "me", Html = text, Src = text };

    private static StoredMessage Model(string markdown) =>
      new StoredMessage { Who = "dw", Html = "<p>" + markdown + "</p>", Src = markdown };

    // ---- a fresh thread ----------------------------------------------------------------------

    [Fact]
    public void FreshThread_IsGreetingPlusOneMessage_AndIsSendable()
    {
      // Exactly what the panel holds the instant a user sends their first line.
      var stored = new List<StoredMessage> { Chrome("Hi, I'm Dwight."), User("plan me a louvre facade") };

      var transcript = ChatToTranscript.Build(stored);

      Assert.Single(transcript);                                       // the greeting is chrome, correctly
      Assert.Equal(LlmRole.User, transcript[0].Role);
      Assert.Equal("plan me a louvre facade", transcript[0].Blocks[0].Text);
      Assert.Null(AgentLoopCore.CheckTranscript(transcript));          // ...and it can be sent
    }

    [Fact]
    public void FreshThread_WithTheUserMessageMISSING_IsRefusedBeforeSending()
    {
      // The #296 shape: the user's message was shown but never recorded, so only chrome remains. The loop
      // must refuse this in OUR words - the provider must never be the one to notice.
      var transcript = ChatToTranscript.Build(new List<StoredMessage> { Chrome("Hi, I'm Dwight.") });

      Assert.Empty(transcript);
      var problem = AgentLoopCore.CheckTranscript(transcript);
      Assert.NotNull(problem);
      Assert.Contains("empty", problem);
    }

    // ---- a reopened thread -------------------------------------------------------------------

    [Fact]
    public void ReopenedThread_SurvivesTheStoreRoundTrip_AndIsSendable()
    {
      // #297 named this second case: the class is "the transcript is wrong", and a reopened thread builds
      // it from disk rather than from memory. Read it back through the real store - Normalize and all -
      // and assert the conversation is still there and still sendable.
      var rec = new SessionRecord
      {
        Threads = new List<StoredThread>
        {
          new StoredThread
          {
            Id = "t1",
            Messages = new List<StoredMessage>
            {
              Chrome("Hi, I'm Dwight."),
              User("plan me a louvre facade"),
              Model("Here is a plan in four regions."),
              User("make region B adaptive"),
            }
          }
        }
      };

      var reopened = SessionStoreCore.Read(SessionStoreCore.Write(rec)).Record.Threads[0].Messages;
      var transcript = ChatToTranscript.Build(reopened);

      Assert.Equal(3, transcript.Count);                               // chrome dropped, three turns kept
      Assert.Equal(LlmRole.User, transcript[0].Role);
      Assert.Equal(LlmRole.Assistant, transcript[1].Role);
      Assert.Equal(LlmRole.User, transcript[2].Role);
      Assert.Equal("Here is a plan in four regions.", transcript[1].Blocks[0].Text);
      Assert.Null(AgentLoopCore.CheckTranscript(transcript));
    }

    [Fact]
    public void ReopenedThread_ThatHeldOnlyChrome_IsRefusedRatherThanSent()
    {
      // A thread whose model messages predate #228 (no Src recorded) reopens as chrome-only. Reopening it
      // and typing nothing new must refuse locally, not bill and 400.
      var rec = new SessionRecord
      {
        Threads = new List<StoredThread>
        {
          new StoredThread { Id = "t1", Messages = new List<StoredMessage> { Chrome("a"), Chrome("b") } }
        }
      };

      var reopened = SessionStoreCore.Read(SessionStoreCore.Write(rec)).Record.Threads[0].Messages;
      Assert.NotNull(AgentLoopCore.CheckTranscript(ChatToTranscript.Build(reopened)));
    }

    // ---- the pre-flight's other clauses -------------------------------------------------------

    [Fact]
    public void AWellFormedToolRoundTrip_Passes()
    {
      var ok = new List<LlmMessage>
      {
        LlmMessage.User("what is on the canvas?"),
        LlmMessage.Assistant(new List<LlmBlock>
        {
          LlmBlock.Thinking("", "sig-abc"),
          LlmBlock.ToolUse("tu_1", "get_overview", "{}"),
        }),
        new LlmMessage { Role = LlmRole.User, Blocks = { LlmBlock.ToolResult("tu_1", "{\"ok\":true}") } },
        LlmMessage.Assistant(new List<LlmBlock> { LlmBlock.TextBlock("Twelve components.") }),
      };

      Assert.Null(AgentLoopCore.CheckTranscript(ok));
    }

    [Fact]
    public void AnUnansweredToolCallAtTheEnd_IsNamed()
    {
      var bad = new List<LlmMessage>
      {
        LlmMessage.User("look"),
        LlmMessage.Assistant(new List<LlmBlock> { LlmBlock.ToolUse("tu_1", "get_overview", "{}") }),
      };

      Assert.Contains("unanswered", AgentLoopCore.CheckTranscript(bad));
    }

    [Fact]
    public void AToolResultWithNoCall_IsNamed()
    {
      var bad = new List<LlmMessage>
      {
        LlmMessage.User("look"),
        new LlmMessage { Role = LlmRole.User, Blocks = { LlmBlock.ToolResult("tu_9", "{}") } },
      };

      Assert.Contains("never made", AgentLoopCore.CheckTranscript(bad));
    }

    [Fact]
    public void AThinkingBlockStrippedOfItsSignature_IsNamed()
    {
      // #192b's failure, caught here instead of as a 400 on the first repair round-trip.
      var bad = new List<LlmMessage>
      {
        LlmMessage.User("hello"),
        LlmMessage.Assistant(new List<LlmBlock> { LlmBlock.Thinking("reasoning", null) }),
      };

      Assert.Contains("signature", AgentLoopCore.CheckTranscript(bad));
    }

    [Fact]
    public void AnEmptyTextBlock_IsNamed()
    {
      var bad = new List<LlmMessage> { LlmMessage.User("   ") };
      Assert.Contains("empty text", AgentLoopCore.CheckTranscript(bad));
    }

    [Fact]
    public void AMessageWithNoBlocks_IsNamed()
    {
      var bad = new List<LlmMessage> { new LlmMessage { Role = LlmRole.User } };
      Assert.Contains("no content", AgentLoopCore.CheckTranscript(bad));
    }

    [Fact]
    public void TheProblemNamesWHICHMessage_SoItCanBeFound()
    {
      var bad = new List<LlmMessage>
      {
        LlmMessage.User("one"),
        LlmMessage.User("two"),
        new LlmMessage { Role = LlmRole.User },
      };

      Assert.Contains("message 3 of 3", AgentLoopCore.CheckTranscript(bad));
    }
  }
}
