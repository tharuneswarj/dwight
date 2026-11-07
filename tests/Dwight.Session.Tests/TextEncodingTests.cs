using System.Collections.Generic;
using System.Text.Json;
using Dwight.Session;
using Dwight.UI;
using Xunit;

namespace Dwight.Session.Tests
{
  /// <summary>
  /// D2: the model's text arrives mangled - Ã— for ×, â€" for an en-dash, â†' for an arrow. That pattern
  /// is UTF-8 BYTES DECODED AS LATIN-1, so somewhere a byte-level hand-off is using the wrong encoding.
  ///
  /// These tests are the INSTRUMENT, not the fix (#137). They push the exact characters through every
  /// stage of the path that can be reached without Rhino. Whichever stage drops them names the cause;
  /// if they all pass, the corruption is in the WebView hand-off itself and the remaining candidates
  /// shrink to one.
  /// </summary>
  public class TextEncodingTests
  {
    private const string Tricky = "3 × 4 curves — see the arrow → and a ° degree, ½ a branch";

    [Fact]
    public void Stage1_markdown_rendering_keeps_every_character()
    {
      var html = ModelMarkdown.ToHtml(Tricky);

      Assert.Contains("×", html);
      Assert.Contains("—", html);
      Assert.Contains("→", html);
      Assert.Contains("°", html);
      Assert.Contains("½", html);
      Assert.DoesNotContain("Ã", html);   // the mojibake signature
      Assert.DoesNotContain("â", html);
    }

    [Fact]
    public void Stage2_the_js_marshalling_escapes_rather_than_re_encodes()
    {
      // WebPanel.Js is JsonSerializer.Serialize with default options, whose encoder escapes non-ASCII to
      // \uXXXX. An escaped character is encoding-INDEPENDENT: it survives whatever the page decodes as.
      // If this holds, the C#-to-JS hop cannot be the mangler.
      var js = JsonSerializer.Serialize(Tricky);

      Assert.Contains("\\u00D7", js);   // the ESCAPED form of ×
      Assert.Contains("\\u2014", js);   // the ESCAPED form of an em dash
      Assert.Contains("\\u2192", js);   // the ESCAPED form of →
      Assert.DoesNotContain("Ã", js);

      Assert.Equal(Tricky, JsonSerializer.Deserialize<string>(js));
    }

    [Fact]
    public void Stage3_the_store_round_trips_every_character()
    {
      var rec = new SessionRecord
      {
        Threads = new List<StoredThread>
        {
          new StoredThread
          {
            Id = "t1",
            Messages = new List<StoredMessage>
            {
              new StoredMessage { Who = "dw", Html = "<p>" + Tricky + "</p>", Src = Tricky, Utc = "u" },
            },
          },
        },
      };

      var read = SessionStoreCore.Read(SessionStoreCore.Write(rec));
      var message = read.Record.Threads[0].Messages[0];

      Assert.Equal(Tricky, message.Src);
      Assert.Contains("×", message.Html);
      Assert.Contains("→", message.Html);
      Assert.DoesNotContain("Ã", message.Html);
    }

    [Fact]
    public void Stage4_the_shell_declares_utf8_before_anything_that_could_be_sniffed()
    {
      // If the page never declares its encoding, the renderer guesses - and windows-1252 is the guess
      // that produces exactly this mojibake. The declaration must also sit inside the first 1024 bytes,
      // because that is all a sniffing renderer reads before deciding.
      var html = System.IO.File.ReadAllText(ShellPath());

      var idx = html.IndexOf("charset", System.StringComparison.OrdinalIgnoreCase);
      Assert.True(idx >= 0, "the shell declares no charset at all");
      Assert.True(idx < 1024, "the charset declaration sits at byte " + idx + ", past the sniffing window");
      Assert.Contains("utf-8", html.Substring(0, 1024), System.StringComparison.OrdinalIgnoreCase);
    }

    private static string ShellPath()
    {
      var dir = System.AppContext.BaseDirectory;
      for (var i = 0; i < 8 && dir != null; i++)
      {
        var candidate = System.IO.Path.Combine(dir, "Dwight", "Resources", "panel-shell.html");
        if (System.IO.File.Exists(candidate)) return candidate;
        dir = System.IO.Directory.GetParent(dir)?.FullName;
      }
      throw new System.IO.FileNotFoundException("panel-shell.html not found from " + System.AppContext.BaseDirectory);
    }
  }
}
