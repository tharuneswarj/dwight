using System.Linq;
using System.Net;
using Dwight.Agent;
using Dwight.UI;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// The first-run tour's last step DRAWS the build consent card. A picture of a control is a claim that the
  /// control exists, and the mockup this replaced drew a "Not now" button and a "REASON" label that the real
  /// card does not have (the architect's own catch, 2026-09-19). So the picture is now built from the real
  /// card and held to it, on the behaviour's side (#315):
  ///
  /// <list type="bullet">
  /// <item>the title and the authority sentence come from <see cref="ConsentCard"/> itself, so they cannot drift -
  /// asserted here anyway, because "cannot drift" is what a refactor quietly stops being true of;</item>
  /// <item>the two labels are typed in <see cref="FirstRunCore"/> and must appear in the REAL rendered card;</item>
  /// <item>the card has exactly ONE button, because the tour draws one. Lane A's U18b gives the card an alternative
  /// action; when it lands this fails, and the tour's picture is changed on purpose.</item>
  /// </list>
  /// </summary>
  public class TourConsentPictureTests
  {
    private static readonly string[] Regions = { "A1" };

    private static string RealBuildCard() =>
      ConsentCard.Html("build", "You asked for the site boundary first.", "tok", null, Regions);

    // Brief BK: the tour is measured on tools/ui-tour-words.json, the plugin's own payload. A word changed in FirstRunCore,
    // DwightVerbs or ConsentCard fails this until the fixture, and the fit it measures, follow. DWIGHT_WRITE_FIXTURES=1 rewrites it.
    [Fact]
    public void The_fixture_the_tour_is_measured_on_is_the_plugins_own_payload()
    {
      var root = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
      while (root != null && !System.IO.File.Exists(System.IO.Path.Combine(root.FullName, "Dwight.sln"))) root = root.Parent;
      Assert.True(root != null, "the repository root (Dwight.sln) could not be found from " + System.AppContext.BaseDirectory);
      var path = System.IO.Path.Combine(root.FullName, "tools", "ui-tour-words.json");
      var picture = Picture();
      var payload = System.Text.Json.Nodes.JsonNode.Parse(FirstRunCore.InitJson(DwightVerbs.All, "Ctrl+Shift+Right-click", true, FirstRunCore.DocsUrl, FirstRunCore.KeysUrl, picture));
      var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
      var want = payload.ToJsonString(options).Replace("\r\n", "\n");
      if (System.Environment.GetEnvironmentVariable("DWIGHT_WRITE_FIXTURES") == "1") System.IO.File.WriteAllText(path, want + "\n");
      Assert.Equal(want, System.IO.File.ReadAllText(path).Replace("\r\n", "\n").TrimEnd('\n'));
    }

    [Fact]
    public void The_button_the_tour_draws_is_the_button_the_real_card_has()
    {
      Assert.Contains(">" + FirstRunCore.ApproveLabel + "</button>", RealBuildCard());
    }

    [Fact]
    public void The_label_the_tour_draws_on_the_reason_is_the_one_the_real_card_prints()
    {
      Assert.Contains(FirstRunCore.ReasonLabel, RealBuildCard());
    }

    [Fact]
    public void The_real_card_has_exactly_the_one_button_the_tour_draws()
    {
      var html = RealBuildCard();
      var buttons = 0;
      for (var i = html.IndexOf("<button", System.StringComparison.Ordinal); i >= 0;
           i = html.IndexOf("<button", i + 1, System.StringComparison.Ordinal)) buttons++;
      Assert.Equal(1, buttons);
    }

    private static FirstRunCore.ConsentPicture Picture() =>
      new FirstRunCore.ConsentPicture(ConsentCard.BuildAct(Regions), ConsentCard.OneLiner("build"), ConsentCard.MoreLabel);

    // The picture is the waiting card (Brief BG): its act, its one plain line and its fold's label are the real card's words, and the host builds them the same way.
    [Fact]
    public void The_words_the_tour_draws_are_on_the_real_waiting_card()
    {
      var picture = Picture();
      var html = RealBuildCard();
      Assert.Contains("<p class=\"consent-act\"><strong>" + WebUtility.HtmlEncode(picture.Act) + "</strong></p>", html);
      Assert.Contains("<p class=\"consent-line\">" + WebUtility.HtmlEncode(picture.Line) + "</p>", html);
      Assert.Contains("<summary>" + WebUtility.HtmlEncode(picture.More) + "</summary>", html);
      var host = System.IO.File.ReadAllText(RepoFile("Dwight", "UI", "FirstRunTour.cs"));
      Assert.Contains("new FirstRunCore.ConsentPicture(ConsentCard.BuildAct(new[] { \"A1\" }), ConsentCard.OneLiner(\"build\"), ConsentCard.MoreLabel)", host);
    }

    // The same parts in the same order, so a change to the card's frame fails here until the picture follows it.
    [Fact]
    public void The_tour_draws_the_cards_parts_in_the_cards_order()
    {
      string[] parts = { "consent-act", "consent-why", "consent-btns", "consent-line", "consent-more" };
      int[] At(string html) => parts.Select(p => html.IndexOf("class=\"" + p + "\"", System.StringComparison.Ordinal)).ToArray();
      var tour = System.IO.File.ReadAllText(RepoFile("Dwight", "Resources", "tour.html"));
      foreach (var at in new[] { At(RealBuildCard()), At(tour) })
      {
        Assert.All(at, i => Assert.True(i >= 0, "a part of the card is missing"));
        Assert.Equal(at.OrderBy(i => i), at);
      }
    }

    private static string RepoFile(params string[] parts)
    {
      var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
      while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "Dwight.sln"))) dir = dir.Parent;
      Assert.True(dir != null, "the repository root (Dwight.sln) could not be found");
      return System.IO.Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
    }
  }
}
