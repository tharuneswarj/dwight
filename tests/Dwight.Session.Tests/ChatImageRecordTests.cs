using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Dwight.Session;
using Xunit;

namespace Dwight.Session.Tests
{
  /// <summary>Brief DX: what the record holds of an image. The hash and the size, never the bytes, so the record stays small and the folder stays copy-portable, with the store's preservation policy unchanged.</summary>
  public class ChatImageRecordTests
  {
    private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static StoredImage Image() => new StoredImage { Sha256 = Sha, MediaType = ChatImage.Png, Width = 1568, Height = 1000 };

    private static SessionRecord WithMessage(StoredMessage m) => new SessionRecord
    {
      Threads = new List<StoredThread> { new StoredThread { Id = "t1", Messages = new List<StoredMessage> { m } } },
    };

    [Fact]
    public void The_record_keeps_the_hash_and_the_size_and_they_round_trip()
    {
      var json = SessionStoreCore.Write(WithMessage(new StoredMessage { Who = "me", Html = "x", Src = "x", Image = Image() }));
      var img = SessionStoreCore.Read(json).Record.Threads[0].Messages[0].Image;
      Assert.Equal(Sha, img.Sha256);
      Assert.Equal(ChatImage.Png, img.MediaType);
      Assert.Equal((1568, 1000), (img.Width, img.Height));

      var stored = JsonNode.Parse(json)["threads"][0]["messages"][0]["img"].AsObject();
      Assert.Equal(new[] { "h", "sha256", "type", "w" }, stored.Select(kv => kv.Key).OrderBy(k => k, System.StringComparer.Ordinal));
    }

    [Fact]
    public void A_message_without_an_image_writes_no_field_and_an_older_record_reads_with_none()
    {
      var json = SessionStoreCore.Write(WithMessage(new StoredMessage { Who = "me", Html = "x", Src = "x" }));
      Assert.DoesNotContain("\"img\"", json);
      Assert.Null(SessionStoreCore.Read("{\"schema\":1,\"minor\":9,\"threads\":[{\"id\":\"t1\",\"messages\":[{\"who\":\"me\",\"html\":\"x\",\"src\":\"x\"}]}]}").Record.Threads[0].Messages[0].Image);
      Assert.Equal(11, SessionStoreCore.SchemaMinor);   // 11 is Brief EC's run key on a thread
    }

    [Fact]
    public void A_newer_builds_field_on_the_image_survives_a_read_write_cycle()
    {
      var json = "{\"schema\":1,\"minor\":99,\"threads\":[{\"id\":\"t1\",\"messages\":[{\"who\":\"me\",\"html\":\"x\",\"src\":\"x\",\"img\":{\"sha256\":\"" + Sha + "\",\"type\":\"image/png\",\"w\":1,\"h\":1,\"future\":7}}]}]}";
      Assert.Contains("\"future\":7", SessionStoreCore.Write(SessionStoreCore.Read(json).Record));
    }

    [Fact]
    public void An_image_costs_the_file_its_hash_so_the_threads_bound_is_not_moved_by_a_picture()
    {
      var plain = new StoredMessage { Who = "me", Html = "x", Src = "x", Utc = "t" };
      var with = new StoredMessage { Who = "me", Html = "x", Src = "x", Utc = "t", Image = Image() };
      Assert.Equal(ChatBudget.Size(plain) + Sha.Length, ChatBudget.Size(with));
    }

    [Fact]
    public void An_image_with_no_words_is_a_turn_and_chrome_with_an_image_on_it_is_not()
    {
      var image = new StoredMessage { Who = "me", Html = "", Src = "", Image = Image() };
      var greeting = new StoredMessage { Who = "dw", Html = "hello" };
      var handEdited = new StoredMessage { Who = "dw", Html = "card", Image = Image() };
      Assert.Equal(new[] { image }, ChatTranscript.SelectConversation(new[] { greeting, handEdited, image }));
      Assert.Empty(ChatTranscript.SelectConversation(new[] { new StoredMessage { Who = "me", Html = "", Src = "  " } }));
    }

    [Fact]
    public void The_shell_is_told_the_hash_and_the_words_and_never_the_bytes()
    {
      var json = JsonNode.Parse(ChatTranscript.ToChatJson(new[] { new StoredMessage { Who = "me", Html = "x", Src = "x", Image = Image() }, new StoredMessage { Who = "dw", Html = "y" } })).AsArray();
      Assert.Equal(Sha, (string)json[0]["img"]["sha"]);
      Assert.Equal("1568 x 1000 px", (string)json[0]["img"]["meta"]);
      Assert.Null(json[1]["img"]);
    }

    [Fact]
    public void A_thread_title_skips_an_image_with_no_words_and_reads_the_first_words_said()
    {
      var messages = new[] { new StoredMessage { Who = "me", Html = "", Src = "", Image = Image() }, new StoredMessage { Who = "me", Html = "a lamp", Src = "a lamp" } };
      Assert.Equal("a lamp", HistoryCore.ThreadTitle(messages));
    }

    [Fact]
    public void The_images_folder_sits_under_the_documents_key_beside_its_record()
    {
      Assert.Equal(Path.Combine("sessions", "KEY", "images"), SessionStoreCore.ImagesDir("sessions", "KEY"));
    }
  }
}
