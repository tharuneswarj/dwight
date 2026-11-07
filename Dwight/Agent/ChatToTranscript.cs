using System;
using System.Collections.Generic;
using Dwight.Llm;
using Dwight.Session;

namespace Dwight.Agent
{
  /// <summary>
  /// THE ONE PLACE a persisted chat thread becomes a model transcript (#296/#297).
  ///
  /// It sits here rather than in the panel shim for one reason: the shim is GH-coupled and cannot be run
  /// headlessly, so a transcript assembled inside it can only ever be tested by a COPY of itself - and a
  /// copy that agrees with the original proves nothing about the original (the Family-B mistake). Lifting
  /// the whole assembly out - selection AND mapping - makes the path the tests traverse the same path the
  /// product traverses.
  ///
  /// The split of duties either side of it: <see cref="ChatTranscript.SelectConversation"/> owns WHICH
  /// messages are turns (#228's chrome rule), this owns what they become on the wire, and
  /// <see cref="AgentLoopCore.CheckTranscript"/> owns whether the result can be sent at all.
  /// </summary>
  internal static class ChatToTranscript
  {
    /// <summary>
    /// Brief CL: IS AN APPROVAL THE LAST THING THE PERSON DID? A follow-up to an approved card is a read-only turn with the card right above it, so its capability cannot
    /// say so. The echo mark is set only by the click and the automatic approval (U111), so a person typing those words is not one.
    /// </summary>
    public static bool FollowsApproval(IReadOnlyList<Dwight.Session.StoredMessage> stored)
    {
      if (stored == null) return false;
      for (var i = stored.Count - 1; i >= 0; i--)
        if (stored[i] != null && string.Equals(stored[i].Who, "me", StringComparison.Ordinal))
          return stored[i].Echo;
      return false;
    }

    /// <summary>
    /// A PLUGIN REPORT (Phase-9 U4): a line the plugin wrote about something it did on the user's approval
    /// - a set_value verdict - that the model must read on its next turn. It renders on screen as Dwight
    /// (it is Dwight's report) and enters the transcript in the USER turn, attributed in its source text:
    /// a trailing assistant turn would be a PREFILL the model continues rather than a fact it reads, and
    /// the turn structure puts an environment's results on the user's side - exactly where a tool result
    /// arrives. Distinct from chrome (no Src, never seen) and from the model's own words ("dw" with Src).
    ///
    /// THE DISCRIMINATOR THAT ACTUALLY PROTECTS IT IS POSITION, NOT WORDING. A model could emit prose that
    /// looks exactly like the attribution line. What stops that being read as a real report on a later
    /// turn is that the model's own words ALWAYS become an ASSISTANT turn and a plugin report ALWAYS
    /// becomes a USER turn - and no model output can place itself in a user turn, because the "report"
    /// record is written only by the plugin's own door. Two code paths producing similar-looking text is
    /// therefore not duplication to merge: merging them would be the defect (the architect's ruling).
    /// </summary>
    public const string Report = "report";

    public static List<LlmMessage> Build(IEnumerable<StoredMessage> stored) => Build(stored, null);

    /// <summary>
    /// Brief DX: THE ONE PLACE AN IMAGE BLOCK IS MADE, from the record of a message the person sent with one attached (a test holds that to the source). It replays on every later turn of the thread, as a
    /// text block does, so the model keeps seeing it. Each image that cannot go says so in its own message's text, because a model reading a message that has lost its picture must not take the words for the
    /// whole of it: a file that is gone, a model that reads no images, or more images than one request can carry, the oldest going first.
    /// </summary>
    public static List<LlmMessage> Build(IEnumerable<StoredMessage> stored, ImageReplay replay)
    {
      var conversation = ChatTranscript.SelectConversation(stored);
      var images = LoadImages(conversation, replay);
      var messages = new List<LlmMessage>();
      foreach (var m in conversation)
      {
        if (m.Who != "me" && m.Who != Report)
        {
          messages.Add(LlmMessage.Assistant(new List<LlmBlock> { LlmBlock.TextBlock(m.Src) }));
          continue;
        }
        if (m.Who != "me" || m.Image == null)
        {
          messages.Add(LlmMessage.User(m.Src));
          continue;
        }
        var user = new LlmMessage { Role = LlmRole.User };
        var text = m.Src ?? "";
        if (images.TryGetValue(m, out var image))
          user.Blocks.Add(image.Block);
        else
          text = string.IsNullOrWhiteSpace(text) ? Note(m, images, replay) : text + "\n\n" + Note(m, images, replay);
        if (!string.IsNullOrWhiteSpace(text)) user.Blocks.Add(LlmBlock.TextBlock(text));
        messages.Add(user);
      }
      return messages;
    }

    private sealed class Loaded { public LlmBlock Block; }

    /// <summary>The images this request carries, by message, reading the newest first so a long thread's bound falls on its oldest. Reasons for the rest go on <paramref name="replay"/>.</summary>
    private static Dictionary<StoredMessage, Loaded> LoadImages(List<StoredMessage> conversation, ImageReplay replay)
    {
      var loaded = new Dictionary<StoredMessage, Loaded>();
      if (replay == null) return loaded;
      long chars = 0;
      for (var i = conversation.Count - 1; i >= 0; i--)
      {
        var m = conversation[i];
        if (m.Who != "me" || m.Image == null) continue;
        if (!replay.ModelReadsImages) { replay.Omitted.Add(ImageOmission.ModelReadsNoImages); continue; }
        var base64 = replay.LoadBase64?.Invoke(m.Image);
        if (string.IsNullOrEmpty(base64)) { replay.Omitted.Add(ImageOmission.NotFound); continue; }
        // The newest always goes: one image was checked against what a provider takes when it was attached.
        if (loaded.Count > 0 && chars + base64.Length > replay.MaxChars) { replay.Omitted.Add(ImageOmission.TooMuchForOneRequest); continue; }
        chars += base64.Length;
        loaded[m] = new Loaded { Block = LlmBlock.Image(m.Image.MediaType, base64, ChatImage.Tokens(m.Image.Width, m.Image.Height), replay.RuleKnown) };
      }
      return loaded;
    }

    /// <summary>Why this message's image is not in the request. With no replay at all, a caller that cannot read files, the plain answer is that this request does not have it.</summary>
    private static string Note(StoredMessage m, Dictionary<StoredMessage, Loaded> images, ImageReplay replay)
    {
      if (replay == null) return ChatImage.OmittedNote(ImageOmission.NotFound, null);
      if (!replay.ModelReadsImages) return ChatImage.OmittedNote(ImageOmission.ModelReadsNoImages, replay.ModelName);
      return string.IsNullOrEmpty(replay.LoadBase64?.Invoke(m.Image))
        ? ChatImage.OmittedNote(ImageOmission.NotFound, replay.ModelName)
        : ChatImage.OmittedNote(ImageOmission.TooMuchForOneRequest, replay.ModelName);
    }
  }
}
