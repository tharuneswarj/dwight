using System.Collections.Generic;
using System.Text.Json.Nodes;
using Xunit;
using Dwight.Session;

namespace Dwight.Session.Tests
{
  /// <summary>WP-1 step 5: the persisted thread renders to the SAME chat shape as the live shell, so a
  /// reopened conversation is byte-for-byte what was on screen.</summary>
  public class ChatTranscriptTests
  {
    [Fact]
    public void An_empty_thread_renders_as_an_empty_array()
    {
      Assert.Equal("[]", ChatTranscript.ToChatJson(new List<StoredMessage>()));
      Assert.Equal("[]", ChatTranscript.ToChatJson(null));
    }

    [Fact]
    public void Messages_render_who_and_html_in_order()
    {
      var json = ChatTranscript.ToChatJson(new[]
      {
        new StoredMessage { Who = "dw", Html = "hi", Utc = "t0" },
        new StoredMessage { Who = "me", Html = "build a wall", Utc = "t1" },
      });

      var arr = JsonNode.Parse(json).AsArray();
      Assert.Equal(2, arr.Count);
      Assert.Equal("dw", (string)arr[0]["who"]);
      Assert.Equal("hi", (string)arr[0]["html"]);
      Assert.Equal("me", (string)arr[1]["who"]);
      Assert.Equal("build a wall", (string)arr[1]["html"]);
      // utc is stored but NOT rendered - the shell shows who/html only
      Assert.Null(arr[0]["utc"]);
    }

    [Fact]
    public void Null_fields_and_null_entries_are_tolerated()
    {
      var json = ChatTranscript.ToChatJson(new[]
      {
        null,
        new StoredMessage { Who = null, Html = null },
      });
      var arr = JsonNode.Parse(json).AsArray();
      Assert.Single(arr);                       // the null entry dropped
      Assert.Equal("dw", (string)arr[0]["who"]); // null who defaults to dw
      Assert.Equal("", (string)arr[0]["html"]);
    }

    [Fact]
    public void Html_is_carried_verbatim_not_double_escaped()
    {
      // the caller HTML-encodes user text once; ToChatJson must JSON-encode but not re-HTML-encode it.
      var json = ChatTranscript.ToChatJson(new[] { new StoredMessage { Who = "me", Html = "a &amp; b" } });
      var arr = JsonNode.Parse(json).AsArray();
      Assert.Equal("a &amp; b", (string)arr[0]["html"]);
    }
  }
}
