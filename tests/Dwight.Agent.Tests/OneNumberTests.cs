using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Dwight.UI;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>xUnit runs a collection with parallelization disabled on its own, after the parallel ones have finished.</summary>
  [CollectionDefinition(OneNumberTests.RunsAlone, DisableParallelization = true)]
  public class OneNumberCollection { }

  /// <summary>
  /// Brief ES unit 1: at any moment, the number beside a name in the words a person is reading and the badge on that component are the same number. The
  /// numbering the canvas wears is a static the product writes, so this class runs alone, and every test that pushes a numbering belongs here.
  /// The twenty-first's gate measured the defect: the chat wrote add1 (5) from its registry, and the Walk's badge on add1 showed 7, the run's number.
  /// </summary>
  [Collection(RunsAlone)]
  public class OneNumberTests
  {
    public const string RunsAlone = "Pushes the numbering the canvas wears, so runs alone";

    private static Guid G(int n) => new Guid(n, 0, 0, new byte[8]);
    private static readonly Guid S1 = G(1), S2 = G(2), S3 = G(3), S4 = G(4), Add1 = G(5), Add2 = G(6), Mul = G(7);

    /// <summary>The Explain run's numbering, left to right by pivot, as the twenty-first's payload gave it: add1 is 7 there.</summary>
    private static Dictionary<Guid, int> RunLabels() => new Dictionary<Guid, int> { [S1] = 1, [S2] = 2, [S3] = 3, [S4] = 4, [Mul] = 5, [Add2] = 6, [Add1] = 7 };

    /// <summary>The chat's registry, issued in the order the seed met them: add1 is 5 there, mul 7.</summary>
    private static AgentIdRegistry Registry(params Guid[] met)
    {
      var ids = new AgentIdRegistry();
      foreach (var g in met) ids.Issue(g);
      return ids;
    }

    private static readonly Dictionary<Guid, RectangleF> Bounds = Enumerable.Range(1, 7).ToDictionary(G, n => new RectangleF(100f * n, 40f, 60f, 30f));

    /// <summary>The badges as the canvas draws them: CanvasGlow's own measured function, with the font it draws in, over the components lit.</summary>
    private static Dictionary<Guid, string> Drawn(params Guid[] lit)
    {
      using (var bmp = new Bitmap(1, 1))
      using (var g = Graphics.FromImage(bmp))
      using (var font = new Font("Segoe UI", 6.5f, FontStyle.Bold))
      {
        var badges = CanvasNumbers.Badges(lit, id => Bounds.TryGetValue(id, out var b) ? b : (RectangleF?)null, t => g.MeasureString(t, font));
        var byGuid = new Dictionary<Guid, string>();
        foreach (var id in lit)
        {
          var b = Bounds[id];
          var hit = badges.Where(x => Math.Abs(x.Rect.Left - (b.Left - 2f)) < 0.01f && x.Rect.Bottom <= b.Top).ToList();   // the badge sits on this component's top-left corner, above it
          if (hit.Count == 1) byGuid[id] = hit[0].Text;
        }
        Assert.Equal(badges.Count, byGuid.Count);   // every badge drawn belongs to exactly one lit component
        return byGuid;
      }
    }

    /// <summary>The number the words wrote beside a name: the digits in its brackets, or null where the words gave none.</summary>
    private static string NumberIn(string words)
    {
      var m = Regex.Match(words ?? "", @"\((\d+)\)$");
      return m.Success ? m.Groups[1].Value : null;
    }

    [Fact]
    public void THE_BADGE_ON_A_COMPONENT_IS_THE_NUMBER_BESIDE_ITS_NAME_IN_THE_WORDS_WRITTEN_LAST()
    {
      CanvasNumbers.Clear();
      var lit = new[] { S1, S2, Add1, Mul };
      var names = new Dictionary<Guid, string> { [S1] = "s1", [S2] = "s2", [Add1] = "add1", [Mul] = "mul" };
      var run = CanvasNumbers.Of(RunLabels());
      var chat = CanvasNumbers.Of(Registry(S1, S2, S3, S4, Add1, Add2, Mul));

      void Each(Func<Guid, int?> words)
      {
        var drawn = Drawn(lit);
        foreach (var g in lit)
        {
          var written = NumberIn(CanvasNumbers.Name(words, names[g], g));
          Assert.NotNull(written);
          Assert.Equal(written, drawn[g]);
        }
      }

      // An Explain run writes its result in its own numbering, and the canvas wears it.
      CanvasNumbers.Wrote(RunLabels());
      Assert.Equal("add1 (7)", CanvasNumbers.Name(run, "add1", Add1));
      Each(run);

      // A chat turn writes add1 (5), and the badge on add1 says 5, not the run's 7 left behind.
      CanvasNumbers.Wrote(chat);
      Assert.Equal("add1 (5)", CanvasNumbers.Name(chat, "add1", Add1));
      Assert.Equal("5", Drawn(Add1)[Add1]);
      Each(chat);

      // The Walk's next step writes in the run's numbering again, and the canvas follows it back.
      CanvasNumbers.Wrote(run);
      Assert.Equal("7", Drawn(Add1)[Add1]);
      Each(run);
      CanvasNumbers.Clear();
    }

    [Fact]
    public void THE_COMPOSER_REFUSES_TO_NUMBER_A_COMPONENT_THE_CURRENT_MAP_DOES_NOT_HOLD()
    {
      CanvasNumbers.Clear();
      var chat = CanvasNumbers.Of(Registry(S1, S2, S3, S4, Add1, Add2));   // the chat never met mul
      CanvasNumbers.Wrote(RunLabels());   // the run did: mul is 5 there
      Assert.Equal("5", Drawn(Mul)[Mul]);
      Assert.Equal("mul", CanvasNumbers.Name(chat, "mul", Mul));   // a chat writing behind the run's page: its own numbering has none, and the run's 5 on the canvas is not borrowed

      CanvasNumbers.Wrote(chat);
      Assert.Equal("mul", CanvasNumbers.Name(chat, "mul", Mul));   // the bare name: no number, not the run's 5, not one made up
      Assert.Null(CanvasNumbers.Number(Mul));
      Assert.Empty(Drawn(Mul));   // and no badge on it either: a push replaces the map, never merges it
      Assert.Equal("add1 (5)", CanvasNumbers.Name(chat, "add1", Add1));

      Assert.Equal("mul", CanvasNumbers.Name(null, "mul", Mul));   // no numbering at all: the name alone
      Assert.Equal("(5)", CanvasNumbers.Name(chat, "", Add1));     // no name: the number alone, as the hand-off writes one
      Assert.Equal("", CanvasNumbers.Name(chat, "", Mul));
      CanvasNumbers.Clear();
      Assert.Empty(Drawn(Add1));
    }

    [Fact]
    public void A_run_numbering_is_copied_so_a_map_changed_later_cannot_move_a_badge()
    {
      CanvasNumbers.Clear();
      var labels = RunLabels();
      CanvasNumbers.Wrote(labels);
      labels[Add1] = 99;
      Assert.Equal("7", Drawn(Add1)[Add1]);
      CanvasNumbers.Clear();
    }

    [Fact]
    public void A_chat_numbering_reads_the_registry_and_never_issues_an_id()
    {
      var ids = Registry(S1);
      var chat = CanvasNumbers.Of(ids);
      Assert.Null(chat(Add1));
      Assert.Equal(1, ids.Issued);   // reading a number is not issuing one
      ids.Issue(Add1);
      Assert.Equal(2, chat(Add1));   // an id issued later is read as it is
    }

    // ---- the GH-bound halves, pinned by source (ADR-091) -----------------------------------------------------------------------------------------------------------------

    private static string Repo(params string[] parts)
    {
      var dir = new DirectoryInfo(AppContext.BaseDirectory);
      while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Dwight.sln"))) dir = dir.Parent;
      Assert.True(dir != null, "the repository root (Dwight.sln) could not be found");
      return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray())).Replace("\r\n", "\n");
    }

    private static string Body(string source, string signature)
    {
      var start = source.IndexOf(signature, StringComparison.Ordinal);
      Assert.True(start >= 0, "not found: " + signature);
      var open = source.IndexOf('{', start);
      var depth = 0;
      for (var i = open; i < source.Length; i++)
      {
        if (source[i] == '{') depth++;
        else if (source[i] == '}' && --depth == 0) return source.Substring(start, i - start + 1);
      }
      return source.Substring(start);
    }

    [Fact]
    public void The_canvas_draws_its_badges_through_the_one_measured_function_over_the_numbers_it_wears()
    {
      var glow = Repo("Dwight", "UI", "CanvasGlow.cs");
      Assert.Contains("CanvasNumbers.Badges(_guids.Concat(_secondary), id => doc.FindObject(id, true)?.Attributes?.Bounds, t => g.MeasureString(t, font))", glow);
      Assert.DoesNotContain("Dictionary<Guid, int> _labels", glow);   // no second map for a badge to be read from
      Assert.Contains("public static int? BadgeOf(Guid guid) => CanvasNumbers.Number(guid);", glow);
      Assert.Contains("if (CanvasNumbers.Numbered) DrawBadges(g, doc);", glow);
      Assert.Contains("CanvasNumbers.Clear();", Body(glow, "public static void Clear()"));
      Assert.Contains("CanvasNumbers.Wrote(map);", Body(glow, "public static void SetLabels("));
    }

    [Fact]
    public void A_surface_that_lights_components_puts_its_own_numbers_on_them()
    {
      var glow = Repo("Dwight", "UI", "CanvasGlow.cs");
      Assert.Contains("if (!thinking) PutNumbersOf(owner);", Body(glow, "public static void Show(IEnumerable<Guid> guids, bool thinking, object owner = null)"));
      Assert.Contains("PutNumbersOf(owner);", Body(glow, "private static void ShowTieredInner("));
      Assert.Contains("PutNumbersOf(owner);", Body(glow, "public static void ShowMarks("));
      var put = Body(glow, "internal static void PutNumbersOf(object owner)");
      Assert.Contains("owner is WebPanel page ? page.Numbers()", put);
      Assert.Contains("owner is Dwight.Agent.AgentScope scope ? CanvasNumbers.Of(scope.Ids)", put);   // show_on_canvas lights in the chat's numbers
      Assert.Contains("if (numbers != null) CanvasNumbers.Wrote(numbers);", put);   // a page with no numbering leaves the canvas as it was
    }

    [Fact]
    public void A_chat_page_numbers_by_its_registry_and_puts_its_numbers_on_the_canvas_when_it_writes_while_shown()
    {
      var scaffold = Repo("Dwight", "UI", "DwightScaffold.cs");
      Assert.Contains("wp.ChatNumbers = () => CanvasNumbers.Of(ScopeFor(threadId, doc).Ids);", Body(scaffold, "private static void Wire(IResultPanel panel, GH_Document doc, string threadId)"));
      Assert.Contains("if (wp != null && wp.IsShown) CanvasGlow.PutNumbersOf(wp);", Body(scaffold, "private static void AppendBoth("));
      var panel = Repo("Dwight", "UI", "WebPanel.cs");
      Assert.Contains("internal Func<Guid, int?> Numbers() => ChatNumbers != null ? ChatNumbers() : CanvasNumbers.Of(RunLabels);", panel);
      Assert.Contains("internal bool IsShown => ReferenceEquals(_current, this);", panel);
    }

    [Fact]
    public void The_window_turned_to_a_page_puts_that_pages_numbers_on_the_canvas()
    {
      var dock = Repo("Dwight", "UI", "DwightDock.cs");
      var apply = Body(dock, "private void Apply(WindowStep step)");
      var current = apply.IndexOf("WebPanel.SetCurrent(Page(_faceId));", StringComparison.Ordinal);
      var put = apply.IndexOf("CanvasGlow.PutNumbersOf(Page(_faceId));", StringComparison.Ordinal);
      Assert.True(current >= 0 && put > current, "the page's numbers go on the canvas once the window shows it");
    }

    [Fact]
    public void Play_again_keeps_the_page_numbering_the_new_cards_are_written_in()
    {
      var learn = Repo("Dwight", "LlmActions.Learn.cs");
      Assert.Contains("panel.SetRunLabels(labels);", learn);
      Assert.Contains("CanvasGlow.SetLabels(labels);", learn);
      var panel = Repo("Dwight", "UI", "WebPanel.cs");
      Assert.Contains("internal void SetRunLabels(IReadOnlyDictionary<Guid, int> labels) { if (labels != null) RunLabels = labels; }", panel);
    }

    [Fact]
    public void The_lens_names_a_component_by_the_number_the_canvas_wears()
    {
      var lens = Repo("Dwight", "UI", "TreeLens.cs");
      Assert.Contains("return CanvasNumbers.Name(CanvasNumbers.Wearing, UserNick(obj) ?? obj.Name, obj.InstanceGuid);", Body(lens, "internal static string LabelOf(IGH_DocumentObject obj)"));
    }
  }
}
