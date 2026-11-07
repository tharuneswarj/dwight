using System;
using System.Linq;
using System.Text.Json;
using Dwight;
using Dwight.UI;
using Xunit;

namespace Dwight.Session.Tests
{
  /// <summary>
  /// The first-run tour's GH-free half (Phase 9, stream E). Three things here can be wrong in a way a
  /// user would feel: the tour opening when it should not (or never opening at all), the surface being
  /// handed facts that disagree with the product, and a Documentation affordance pointing at a page
  /// that does not exist.
  /// </summary>
  public class FirstRunTests
  {
    // ---- when the tour opens by itself ---------------------------------------------------------

    [Fact]
    public void A_profile_that_has_never_seen_the_tour_gets_it()
    {
      Assert.True(FirstRunCore.ShouldShowTour(tourSeen: false, shownThisSession: false));
    }

    [Fact]
    public void A_profile_that_has_seen_it_never_gets_it_again()
    {
      Assert.False(FirstRunCore.ShouldShowTour(tourSeen: true, shownThisSession: false));
    }

    /// <summary>The upgrade case, which is the same case. A settings.json written by a build with no
    /// tour has no TourSeen field, so it reads as false and the tour shows once - the intended reading
    /// of "shows once", since somebody upgrading has not seen it either. What is deliberately absent is
    /// any version in the decision: nothing here can make a dismissed tour come back.</summary>
    [Fact]
    public void An_upgrade_from_a_build_without_the_tour_is_a_first_run_and_then_never_again()
    {
      // The upgrading user's settings.json has no TourSeen field, so it reads false: they get it once.
      Assert.True(FirstRunCore.ShouldShowTour(tourSeen: false, shownThisSession: false));
      // And the very next launch, with the flag now written, they do not - there is no version in this
      // decision, so no later release can bring back a tour somebody has already dismissed.
      Assert.False(FirstRunCore.ShouldShowTour(tourSeen: true, shownThisSession: false));
    }

    /// <summary>A second document opened in the same Grasshopper session must not re-open the tour,
    /// even if the seen-flag write failed - the in-session guard is the half that still works when
    /// settings.json is not writable.</summary>
    [Fact]
    public void It_does_not_reopen_within_one_session()
    {
      Assert.False(FirstRunCore.ShouldShowTour(tourSeen: false, shownThisSession: true));
    }

    // ---- what the surface is told ---------------------------------------------------------------

    private static JsonElement Init(bool needsKey = false, string docs = "", FirstRunCore.ConsentPicture consent = null, string keys = "") =>
      JsonDocument.Parse(FirstRunCore.InitJson(DwightVerbs.All, "Ctrl+Shift+Right-click", needsKey, docs, keys, consent)).RootElement;

    [Fact]
    public void The_payload_carries_every_verb_with_its_line()
    {
      var verbs = Init().GetProperty("verbs").EnumerateArray().ToList();
      Assert.Equal(DwightVerbs.All.Length, verbs.Count);

      for (var i = 0; i < DwightVerbs.All.Length; i++)
      {
        Assert.Equal(DwightVerbs.All[i].Name, verbs[i].GetProperty("name").GetString());
        Assert.Equal(DwightVerbs.All[i].Line, verbs[i].GetProperty("desc").GetString());
      }
    }

    // ---- the tags: a verb's short form, for the tour's cards ---------------------------------------

    /// <summary>The tour's verb cards show a tagline under each name, and the shell holds none of them. This is
    /// the half that proves they arrive: every tag in the payload is the one <see cref="DwightVerbs"/> holds.</summary>
    [Fact]
    public void The_payload_carries_every_verbs_tag()
    {
      var verbs = Init().GetProperty("verbs").EnumerateArray().ToList();
      for (var i = 0; i < DwightVerbs.All.Length; i++)
        Assert.Equal(DwightVerbs.All[i].Tag, verbs[i].GetProperty("tag").GetString());
    }

    /// <summary>"From DwightVerbs only". A verb list that is not the product's carries exactly the tags it was
    /// built with - and an untagged verb comes out untagged - so nothing inside FirstRunCore is supplying a
    /// tagline of its own. Without this the test above could pass while a second table did the work.</summary>
    [Fact]
    public void A_tag_reaches_the_payload_from_the_verb_that_carries_it_and_from_nowhere_else()
    {
      var sentinel = new[]
      {
        new DwightVerbs.Verb("Alpha", "line a", "tag-a"),
        new DwightVerbs.Verb("Beta", "line b", ""),
      };
      var verbs = JsonDocument.Parse(FirstRunCore.InitJson(sentinel, "c", false, "", "")).RootElement
        .GetProperty("verbs").EnumerateArray().ToList();

      Assert.Equal("tag-a", verbs[0].GetProperty("tag").GetString());
      Assert.Equal("", verbs[1].GetProperty("tag").GetString());
    }

    /// <summary>A verb cell on the tour is about 78px wide at Space Mono 9.5px - some thirteen characters a
    /// line. Thirty is what still wraps to two lines with a word able to move, and a tag that wraps to a third
    /// line at 150% scaling is a paragraph, which is the one thing the redesign exists to remove. The bound is
    /// measured against the rendered card, not guessed; if it changes, it changes with a screenshot.</summary>
    private const int MaxTagLength = 30;

    [Fact]
    public void Every_tag_is_short_enough_for_its_cell()
    {
      Assert.All(DwightVerbs.All, v =>
      {
        Assert.False(string.IsNullOrWhiteSpace(v.Tag), v.Name + " has no tag");
        Assert.True(v.Tag.Length <= MaxTagLength, v.Name + " tag is " + v.Tag.Length + " characters: " + v.Tag);
      });
    }

    // ---- the trust badges and the consent picture --------------------------------------------------

    [Fact]
    public void The_payload_carries_the_trust_badges_and_they_are_two_short_ones()
    {
      var trust = Init().GetProperty("trust").EnumerateArray().Select(e => e.GetString()).ToArray();
      Assert.Equal(FirstRunCore.Trust, trust);
      Assert.Equal(2, trust.Length);   // Tharun's ruling of 2026-10-25: the key and the local option, nothing else on a first screen
      Assert.Equal(new[] { "Bring your own key", "A fully-local option" }, trust);
      Assert.All(trust, t => Assert.InRange(t.Length, 1, 32));   // a badge, not a sentence
    }

    [Fact]
    public void The_payload_carries_the_consent_picture_it_is_given_and_the_labels_it_holds()
    {
      var consent = Init(consent: new FirstRunCore.ConsentPicture("Build A1.", "Checked after it runs.", "What this does.")).GetProperty("consent");
      Assert.Equal("Build A1.", consent.GetProperty("act").GetString());
      Assert.Equal("Checked after it runs.", consent.GetProperty("line").GetString());
      Assert.Equal("What this does.", consent.GetProperty("more").GetString());
      Assert.Equal(FirstRunCore.ApproveLabel, consent.GetProperty("approve").GetString());
      Assert.Equal(FirstRunCore.ReasonLabel, consent.GetProperty("reasonLabel").GetString());
    }

    /// <summary>With no picture the card has no words to draw - and the payload says so with empty strings,
    /// never with wording of its own, so a caller that forgot the picture shows an empty card and not a
    /// plausible invented one.</summary>
    [Fact]
    public void Without_a_consent_picture_the_payload_carries_no_card_wording()
    {
      var consent = Init().GetProperty("consent");
      Assert.Equal("", consent.GetProperty("act").GetString());
      Assert.Equal("", consent.GetProperty("line").GetString());
      Assert.Equal("", consent.GetProperty("more").GetString());
    }

    [Fact]
    public void The_payload_carries_the_chord_it_is_given()
    {
      Assert.Equal("Ctrl+Shift+Right-click", Init().GetProperty("chord").GetString());
    }

    /// <summary>The key state is a plugin-computed fact, and the surface's last action turns on it.
    /// It travels as a boolean, so there is no string for a provider name to leak through.</summary>
    [Fact]
    public void The_payload_states_whether_a_key_is_still_needed()
    {
      Assert.True(Init(needsKey: true).GetProperty("needsKey").GetBoolean());
      Assert.False(Init(needsKey: false).GetProperty("needsKey").GetBoolean());
    }

    [Fact]
    public void The_payload_is_valid_json_with_no_verbs_at_all()
    {
      var json = FirstRunCore.InitJson(Array.Empty<DwightVerbs.Verb>(), "", false, "", "");
      Assert.Empty(JsonDocument.Parse(json).RootElement.GetProperty("verbs").EnumerateArray());
    }

    [Fact]
    public void A_null_verb_list_does_not_throw()
    {
      var root = JsonDocument.Parse(FirstRunCore.InitJson(null, null, false, null, null)).RootElement;
      Assert.Empty(root.GetProperty("verbs").EnumerateArray());
      Assert.Equal("", root.GetProperty("keys").GetString());
    }

    // ---- the documentation link -----------------------------------------------------------------

    /// <summary>The Documentation affordance is offered, because its target exists: the site's /docs returned
    /// 200 on 2026-09-20, when this was flipped from empty (an affordance is an assertion that its target
    /// exists, #321). What this test holds is the SHAPE of that decision, which is all a headless test can
    /// hold: the constant is the live address, the link is visible, and it is an absolute https URL - because
    /// OpenDocs hands it to the shell to launch, so a path, a file: URL or a script must never be able to sit
    /// there. If the page ever moves, this is the line that changes, on purpose.</summary>
    [Fact]
    public void The_documentation_link_points_at_the_live_docs()
    {
      Assert.Equal("https://dwight.tha.run/docs", FirstRunCore.DocsUrl);
      Assert.True(FirstRunCore.DocsLinkVisible);
      Assert.True(Uri.TryCreate(FirstRunCore.DocsUrl, UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps,
        "DocsUrl is launched by the OS shell, so it must be an absolute https URL");
    }

    /// <summary>The other side of the same switch, proved without the constant: whatever URL the plugin
    /// passes reaches the surface verbatim, and an empty one reaches it as empty.</summary>
    [Fact]
    public void The_payload_carries_the_docs_url_it_is_given()
    {
      Assert.Equal("https://dwight.tha.run/docs", Init(docs: "https://dwight.tha.run/docs").GetProperty("docs").GetString());
      Assert.Equal("", Init(docs: "").GetProperty("docs").GetString());
    }

    // ---- the keys page (Brief EN) -----------------------------------------------------------------

    /// <summary>The tour's last step and Settings' key field point a person with no key at the site's keys page. What a headless test
    /// can hold is the shape of the constant: the page's address, under the live docs on the same host, an absolute https URL with
    /// nothing riding on it (OpenLink hands it to the shell to launch), and offered because it is set. If the page moves, this is the
    /// line that changes, on purpose.</summary>
    [Fact]
    public void The_keys_link_points_at_the_sites_keys_page()
    {
      Assert.Equal("https://dwight.tha.run/docs/keys", FirstRunCore.KeysUrl);
      Assert.True(FirstRunCore.KeysLinkVisible);
      Assert.True(Uri.TryCreate(FirstRunCore.KeysUrl, UriKind.Absolute, out var keys) && keys.Scheme == Uri.UriSchemeHttps,
        "KeysUrl is launched by the OS shell, so it must be an absolute https URL");
      var docs = new Uri(FirstRunCore.DocsUrl);
      Assert.Equal(docs.Host, keys.Host);
      Assert.StartsWith(docs.AbsolutePath + "/", keys.AbsolutePath);
      Assert.True(keys.Query == "" && keys.Fragment == "", "nothing rides on the address");
    }

    [Fact]
    public void The_payload_carries_the_keys_url_it_is_given()
    {
      Assert.Equal("https://example.test/keys", Init(keys: "https://example.test/keys").GetProperty("keys").GetString());
      Assert.Equal("", Init(keys: "").GetProperty("keys").GetString());
      // The two links travel apart: neither field is filled from the other.
      var both = Init(docs: "DOCS", keys: "KEYS");
      Assert.Equal("DOCS", both.GetProperty("docs").GetString());
      Assert.Equal("KEYS", both.GetProperty("keys").GetString());
    }

    // ---- the command table: what a bare command from a page opens ----------------------------------

    /// <summary>The shells send a bare command and the plugin decides the address (<see cref="FirstRunCore.LinkFor"/>), so no string
    /// from a page can become something the process launches. "keys" opens the keys page and "docs" the documentation, each its own.</summary>
    [Fact]
    public void Keys_resolves_to_the_keys_page_and_docs_to_the_documentation()
    {
      Assert.Equal(FirstRunCore.KeysUrl, FirstRunCore.LinkFor("keys"));
      Assert.Equal(FirstRunCore.DocsUrl, FirstRunCore.LinkFor("docs"));
      Assert.NotEqual(FirstRunCore.LinkFor("docs"), FirstRunCore.LinkFor("keys"));
    }

    /// <summary>... and to nothing else. An exact match only: a near miss, a command carrying an argument, another of the shells'
    /// commands, or an address sent as if it were a command opens nothing at all.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Keys")]
    [InlineData("KEYS")]
    [InlineData(" keys")]
    [InlineData("keys ")]
    [InlineData("keys:")]
    [InlineData("keys:https://example.test")]
    [InlineData("key")]
    [InlineData("docs/keys")]
    [InlineData("settings")]
    [InlineData("finish")]
    [InlineData("close")]
    [InlineData("tour")]
    [InlineData("setkey:abc")]
    [InlineData("https://dwight.tha.run/docs/keys")]
    [InlineData("https://dwight.tha.run/docs")]
    public void Nothing_but_an_exact_command_opens_a_link(string command)
    {
      Assert.Null(FirstRunCore.LinkFor(command));
    }

    /// <summary>The GH-bound halves, pinned by source (ADR-091): both windows send "keys" to the one launcher, the launcher opens only
    /// what the command table returns, each window is handed the constant, and the address is typed once, in FirstRunCore.</summary>
    [Fact]
    public void Both_windows_open_keys_through_the_one_command_table()
    {
      var tour = Source("Dwight", "UI", "FirstRunTour.cs");
      Assert.Contains("if (cmd == \"docs\" || cmd == \"keys\") { OpenLink(cmd); return; }", tour);
      Assert.Contains("var url = FirstRunCore.LinkFor(command);", tour);
      Assert.Contains("new System.Diagnostics.ProcessStartInfo(url)", tour);
      Assert.Contains("FirstRunCore.DocsUrl, FirstRunCore.KeysUrl, ConsentPicture()", tour);

      var settings = Source("Dwight", "UI", "DwightSettingsPanel.cs");
      Assert.Contains("if (cmd == \"docs\" || cmd == \"keys\") { FirstRunTour.OpenLink(cmd); return; }", settings);
      Assert.Contains("[\"keys\"] = FirstRunCore.KeysUrl,", settings);

      foreach (var file in new[] { tour, settings, Source("Dwight", "Resources", "tour.html"), Source("Dwight", "Resources", "settings.html") })
        Assert.DoesNotContain("docs/keys", file);
      var core = Source("Dwight", "UI", "FirstRunCore.cs");
      Assert.Equal(core.IndexOf("docs/keys", StringComparison.Ordinal), core.LastIndexOf("docs/keys", StringComparison.Ordinal));
    }

    private static string Source(params string[] parts)
    {
      var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
      while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "Dwight.sln"))) dir = dir.Parent;
      Assert.True(dir != null, "the repository root (Dwight.sln) could not be found from " + AppContext.BaseDirectory);
      return System.IO.File.ReadAllText(System.IO.Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray()));
    }

    // ---- the no-WebView2 tour -------------------------------------------------------------------

    /// <summary>A machine with no WebView2 gets the tour on the command line rather than nothing. It
    /// must carry the same content, from the same source - not a shorter second copy that drifts.</summary>
    [Fact]
    public void The_command_line_tour_names_every_verb_and_the_chord()
    {
      var text = FirstRunCore.CommandLineText(DwightVerbs.All, "Ctrl+Shift+Right-click", needsKey: false);

      foreach (var v in DwightVerbs.All)
      {
        Assert.Contains(v.Name, text);
        Assert.Contains(v.Line, text);
      }
      Assert.Contains("Ctrl+Shift+Right-click", text);
      Assert.Contains("Settings", text);   // it says how to get the tour back
    }

    /// <summary>The claim the tour exists to make (ADR-075) survives into the fallback too, because the
    /// fallback is the whole tour for the user who gets it - but SCOPED to where it is true. It read
    /// "Nothing changes your canvas without a card", and a chord-invoked Reorganise moves the canvas with no
    /// card and no preview (LlmActions.RunReorganise goes straight to "Applying"): the promise held only in
    /// the chat, and a first-run surface that over-promises is the worst place to start.</summary>
    [Fact]
    public void The_command_line_tour_makes_the_consent_promise_for_the_chat_only()
    {
      var text = FirstRunCore.CommandLineText(DwightVerbs.All, "Ctrl+Shift+Right-click", needsKey: false);
      Assert.Contains("In the chat, nothing changes your canvas without a card", text);
      Assert.DoesNotContain("Nothing changes your canvas without a card", text);   // the unscoped sentence, capital N
    }

    /// <summary>The second half of the same promise was also a universal: "One Ctrl-Z undoes an approved
    /// change" is false of a multi-region build, which is one Ctrl-Z PER REGION. What holds for every verb
    /// is that what Dwight builds is checked and Ctrl-Z undoes it, with Full control the one exception, said so.</summary>
    [Fact]
    public void The_command_line_tour_does_not_say_one_undo_covers_everything()
    {
      var text = FirstRunCore.CommandLineText(DwightVerbs.All, "Ctrl+Shift+Right-click", needsKey: false);
      Assert.Contains("Dwight checks what it builds, and Ctrl-Z undoes it. Full control, off by default, is the one exception.", text);
      Assert.DoesNotContain("Every change Dwight makes is checked", text);   // false under Full control
      Assert.Contains("so what it builds can be", text);
      Assert.DoesNotContain("so its work can be", text);                     // false under Full control
      Assert.DoesNotContain("One Ctrl-Z undoes", text);
    }

    [Fact]
    public void The_command_line_tour_mentions_the_key_only_when_one_is_needed()
    {
      Assert.Contains("needs an API key", FirstRunCore.CommandLineText(DwightVerbs.All, "chord", needsKey: true));
      Assert.DoesNotContain("needs an API key", FirstRunCore.CommandLineText(DwightVerbs.All, "chord", needsKey: false));
    }

    // ---- the single source itself ---------------------------------------------------------------

    /// <summary>Five verbs, named once. The menus, the startup banner and the tour all read this list,
    /// so a rename lands everywhere or nowhere - which is the only reason the tour can be trusted to
    /// teach the names the menu actually shows.</summary>
    [Fact]
    public void There_are_five_verbs_each_named_once_and_each_with_a_line()
    {
      Assert.Equal(5, DwightVerbs.All.Length);
      Assert.Equal(5, DwightVerbs.Names.Distinct().Count());
      Assert.All(DwightVerbs.All, v =>
      {
        Assert.False(string.IsNullOrWhiteSpace(v.Name));
        Assert.False(string.IsNullOrWhiteSpace(v.Line));
        Assert.False(string.IsNullOrWhiteSpace(v.Tag));
      });
      // The ORDER is pinned, not just the set: the tour draws an Explain answer from the first entry, a
      // Build chat from the last, and keys its five icons by position so that no verb name is typed into
      // the shell. A reorder that this let through would draw the wrong verb's picture under the right name.
      Assert.Equal(new[] { "Explain", "Diagnose", "Reorganise", "Simplify", "Build" }, DwightVerbs.Names);
    }
  }
}
