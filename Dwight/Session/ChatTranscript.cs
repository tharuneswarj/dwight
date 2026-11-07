using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Dwight.Session
{
  /// <summary>
  /// Renders a persisted Scaffold thread's messages into the chat shell's JSON shape (WP-1 step 5). GH-free so
  /// the render is unit-tested: the shape it emits - <c>[{who,html}]</c> - must match what the greeting and the
  /// live <c>AppendChat</c> produce, or a reopened thread would render differently than it did live. That is the
  /// text-shown-is-text-recorded law (#153) applied to chat: open, reopen, and every append all render through
  /// this one shape.
  /// </summary>
  internal static class ChatTranscript
  {
    public static string ToChatJson(IEnumerable<StoredMessage> messages)
    {
      var arr = new JsonArray();
      foreach (var m in messages ?? Enumerable.Empty<StoredMessage>())
      {
        if (m == null) continue;
        var o = new JsonObject { ["who"] = m.Who ?? "dw", ["html"] = m.Html ?? "" };
        if (m.Echo) o["echo"] = true;   // U111: the shell hides by this mark, never by the words
        // Brief DX: the image by its hash and its words, and the shell asks the host for the bytes, so a thread with twenty images is not one call of forty megabytes.
        if (m.Image != null) o["img"] = JsonNode.Parse(ImageJson(m.Image));
        arr.Add(o);
      }
      return arr.ToJsonString();
    }

    /// <summary>Brief DX: what the shell draws an image's place from, on a reopen and on a live append alike.</summary>
    public static string ImageJson(StoredImage image) =>
      new JsonObject { ["sha"] = image?.Sha256 ?? "", ["meta"] = ChatImage.SizeLine(image?.Width ?? 0, image?.Height ?? 0) }.ToJsonString();

    /// <summary>
    /// THE CONVERSATION, as the model sees it (#296) - the messages that are TURNS rather than chrome.
    ///
    /// #228 made a null <c>Src</c> mean "not part of the conversation": a greeting, a notice, a consent
    /// card. That exclusion is right, and it has a consequence nobody tested - on a BRAND-NEW thread the
    /// greeting is chrome, so the ONLY conversational message is the user's own. Anything that leaves that
    /// one message out produces an EMPTY transcript, and an empty transcript is a request that cannot
    /// succeed.
    ///
    /// Lifted out of the shim so exactly that case can be asserted headlessly: the rule that decides what
    /// the model sees is now a pure function over stored messages, not a loop inside a GH-coupled method.
    ///
    /// Brief DX: a person may send an image with no words, and that is a turn. Only their own door writes one, so this lets no chrome through.
    /// </summary>
    public static List<StoredMessage> SelectConversation(IEnumerable<StoredMessage> messages) =>
      (messages ?? Enumerable.Empty<StoredMessage>())
        .Where(m => m != null && (!string.IsNullOrWhiteSpace(m.Src) || (m.Image != null && m.Who == "me")))
        .ToList();
  }
}
