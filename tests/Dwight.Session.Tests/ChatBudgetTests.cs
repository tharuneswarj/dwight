using System.Collections.Generic;
using System.Linq;
using Dwight.Session;
using Xunit;

namespace Dwight.Session.Tests
{
  /// <summary>
  /// The size-aware thread bound (WP-3 Stage 0). Two properties matter and neither is obvious from the
  /// constants alone: a truncated message must still be WELL-FORMED HTML (the shell appends with
  /// insertAdjacentHTML, so an unclosed element swallows every later message), and a bound applied on
  /// both read and write must be IDEMPOTENT.
  /// </summary>
  public class ChatBudgetTests
  {
    private static StoredMessage Msg(string html, string who = "dw") =>
      new StoredMessage { Who = who, Html = html, Utc = "2026-08-17T00:00:00.0000000Z" };

    [Fact]
    public void A_message_within_budget_is_returned_untouched()
    {
      const string html = "<p>short and sweet</p>";
      Assert.Equal(html, ChatBudget.TrimHtml(html, 1000, out var truncated));
      Assert.False(truncated);
    }

    [Fact]
    public void Truncation_closes_every_element_it_cut_through()
    {
      // The cut lands deep inside three nested elements. If the closers were not emitted, the rest of the
      // thread would render INSIDE this message's <div> - the corruption the bound must never cause.
      var html = "<div class=\"a\"><ul><li>" + new string('x', 500) + "</li></ul></div>";
      var trimmed = ChatBudget.TrimHtml(html, 100, out var truncated);

      Assert.True(truncated);
      Assert.True(trimmed.Length < html.Length + 200);
      Assert.Equal(Count(trimmed, "<div"), Count(trimmed, "</div>"));
      Assert.Equal(Count(trimmed, "<ul"), Count(trimmed, "</ul>"));
      Assert.Equal(Count(trimmed, "<li"), Count(trimmed, "</li>"));
    }

    [Fact]
    public void Truncation_never_cuts_inside_a_tag()
    {
      // The budget falls in the middle of the <strong ...> tag. Cutting there would emit a half-tag that
      // the browser would either swallow or mis-parse; the cut backs off to the last element boundary.
      var html = "<p>abc</p><strong data-x=\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\">bold</strong>";
      var trimmed = ChatBudget.TrimHtml(html, 20, out _);

      Assert.DoesNotContain("<strong data", trimmed);
      Assert.StartsWith("<p>abc</p>", trimmed);
    }

    [Fact]
    public void A_void_element_is_never_pushed_onto_the_open_stack()
    {
      // <br> and <img> have no closing tag; treating them as open would emit </br>, which is invalid.
      var html = "<p>one<br><img src=\"x\">" + new string('y', 400) + "</p>";
      var trimmed = ChatBudget.TrimHtml(html, 60, out _);

      Assert.DoesNotContain("</br>", trimmed);
      Assert.DoesNotContain("</img>", trimmed);
      Assert.Equal(Count(trimmed, "<p"), Count(trimmed, "</p>"));
    }

    [Fact]
    public void Truncation_says_so_and_states_the_original_size()
    {
      var html = "<p>" + new string('z', 400) + "</p>";
      var trimmed = ChatBudget.TrimHtml(html, 80, out _);

      Assert.Contains("the rest is not stored", trimmed);
      Assert.Contains(html.Length.ToString(), trimmed);   // honest about how much there was
    }

    [Fact]
    public void Truncation_is_idempotent_so_read_and_write_cannot_compound()
    {
      // Normalize runs on BOTH read and write. Without headroom for the closers and the marker, the
      // result lands back over the cap, the next cycle truncates it again and appends a SECOND marker,
      // and a stored message shrinks a little on every save. The trimmed form must be a fixed point.
      var html = "<div><p>" + new string('q', 5000) + "</p></div>";
      var once = ChatBudget.TrimHtml(html, 500);
      var twice = ChatBudget.TrimHtml(once, 500);

      Assert.Equal(once, twice);
      Assert.True(once.Length <= 500, "the trimmed message must fit the budget it was trimmed to");
      Assert.Equal(1, Count(once, "the rest is not stored"));
    }

    [Fact]
    public void An_unterminated_tag_is_cut_before_rather_than_carried()
    {
      var html = "<p>fine</p><div class=\"never closed";
      var trimmed = ChatBudget.TrimHtml(html, 15, out _);

      Assert.DoesNotContain("<div", trimmed);
      Assert.StartsWith("<p>fine</p>", trimmed);
    }

    [Fact]
    public void A_thread_within_budget_keeps_every_message()
    {
      var msgs = new List<StoredMessage> { Msg("<p>a</p>"), Msg("<p>b</p>"), Msg("<p>c</p>") };
      Assert.Equal(3, ChatBudget.TrimThread(msgs, 10_000).Count);
    }

    [Fact]
    public void A_thread_over_budget_head_trims_and_keeps_the_live_tail()
    {
      // 10 messages of ~200 chars; a 600-char budget can hold only the last few.
      var msgs = Enumerable.Range(0, 10)
        .Select(i => Msg("<p>" + i + new string('m', 200) + "</p>"))
        .ToList();

      var kept = ChatBudget.TrimThread(msgs, 600);

      Assert.True(kept.Count < msgs.Count);
      Assert.Same(msgs[msgs.Count - 1], kept[kept.Count - 1]);       // the newest survives
      Assert.DoesNotContain(msgs[0], kept);                          // the oldest is what goes
      Assert.True(kept.Sum(ChatBudget.Size) <= 600);
    }

    [Fact]
    public void A_single_oversized_message_still_leaves_the_thread_non_empty()
    {
      // The byte cap must never empty a thread: a short thread is honest, an empty one is not.
      var msgs = new List<StoredMessage> { Msg("<p>old</p>"), Msg("<p>" + new string('w', 5000) + "</p>") };
      var kept = ChatBudget.TrimThread(msgs, 100);

      Assert.Single(kept);
      Assert.Same(msgs[1], kept[0]);
    }

    [Fact]
    public void Normalize_applies_both_bounds_and_leaves_the_scaffold_map_alone()
    {
      // The map is the artifact the whole store exists to protect (#167): it sits outside the message
      // list precisely so trimming can never reach it. Asserted here rather than assumed, because the
      // exemption is what lets the message caps be blunt.
      var map = new string('M', SessionStoreCore.MaxThreadChars * 2);
      var rec = new SessionRecord
      {
        Threads = new List<StoredThread>
        {
          new StoredThread
          {
            Id = "t1",
            ScaffoldMap = map,
            Messages = Enumerable.Range(0, 40)
              .Select(i => Msg("<div><p>" + i + new string('n', SessionStoreCore.MaxMessageChars) + "</p></div>"))
              .ToList(),
          },
        },
      };

      var round = SessionStoreCore.Read(SessionStoreCore.Write(rec));
      var thread = Assert.Single(round.Record.Threads);

      Assert.Equal(map, thread.ScaffoldMap);                                   // untrimmed, in full
      Assert.All(thread.Messages, m => Assert.Contains("the rest is not stored", m.Html));
      Assert.True(thread.Messages.Sum(ChatBudget.Size) <= SessionStoreCore.MaxThreadChars);
      Assert.NotEmpty(thread.Messages);
    }

    private static int Count(string haystack, string needle)
    {
      var n = 0;
      for (var i = haystack.IndexOf(needle); i >= 0; i = haystack.IndexOf(needle, i + 1)) n++;
      return n;
    }
  }
}
