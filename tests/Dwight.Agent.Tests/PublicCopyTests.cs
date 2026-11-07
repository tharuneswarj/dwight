using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dwight.Data;
using Dwight.Llm;
using Dwight.Session;
using Dwight.UI;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Brief CG: the lines the public repository is staged from. Each is a claim about our own behaviour, so each is held here against the behaviour's side (#315):
  /// the catalogue, the endpoint rule, the surfaces. A sentence that goes stale fails here rather than on someone's first read.
  /// </summary>
  public class PublicCopyTests
  {
    private static string Repo(params string[] parts)
    {
      var dir = new DirectoryInfo(AppContext.BaseDirectory);
      while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Dwight.sln"))) dir = dir.Parent;
      Assert.True(dir != null);
      return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray())).Replace("\r\n", "\n");
    }

    private static string Public(string page) => Repo("docs", "public", page);

    private static IEnumerable<JsonNode> Rows() => JsonNode.Parse(Repo("Dwight", "Resources", "models.json"))["models"].AsArray();

    private static JsonNode Row(string id) => Rows().Single(m => m["id"].GetValue<string>() == id);

    private static bool Agentic(JsonNode row) => row["capabilities"]?["agentic"]?.GetValue<bool>() ?? false;

    private static bool PinOnly(JsonNode row) => row["capabilities"]?["pinOnly"]?.GetValue<bool>() ?? false;

    // ---- the frame: a plugin that adds five tools, with no job title --------------------------------------------------------------------

    public static IEnumerable<object[]> FirstLines() => new[]
    {
      new object[] { "README.md" },
      new object[] { "AGENTS.md" },
      new object[] { "CONTRIBUTING.md" },
      new object[] { "packaging/manifest.yml" },
      new object[] { "Dwight/Dwight.csproj" },
      new object[] { "Dwight/DwightInfo.cs" },
      new object[] { "Dwight/Resources/tour.html" },
    };

    [Theory]
    [MemberData(nameof(FirstLines))]
    public void Nothing_a_person_reads_first_gives_Dwight_a_job_title(string file)
    {
      var text = Repo(file.Split('/'));
      Assert.DoesNotContain("AI assistant", text);
      Assert.DoesNotContain("Your assistant", text);
      Assert.DoesNotContain("Grasshopper-native", text);
    }

    [Theory]
    [InlineData("README.md")]
    [InlineData("AGENTS.md")]
    [InlineData("CONTRIBUTING.md")]
    [InlineData("Dwight/Dwight.csproj")]
    [InlineData("Dwight/DwightInfo.cs")]
    public void Each_says_what_it_is_in_the_frame(string file)
    {
      Assert.Contains("adds five tools to the canvas", Repo(file.Split('/')));
    }

    [Fact]
    public void The_package_says_the_frame_and_its_keywords_are_the_true_ones()
    {
      var manifest = Repo("packaging", "manifest.yml");
      // A folded block, so a phrase may break across two lines and a blank line is a paragraph break; the flattening joins both.
      // The text is Tharun's own of 5 November 2026 (the 1.0.1 package): the short line first, then the paragraph.
      var flat = System.Text.RegularExpressions.Regex.Replace(manifest, @"\n\s+", " ");
      Assert.Contains("AI inside the Grasshopper canvas. Explain, diagnose, reorganise, simplify or build any selection. Your own API key, or fully local with Ollama.", flat);
      Assert.Contains("Select some components, press Ctrl+Shift+Right-click, pick a tool. The model does the judgement. The plugin handles everything exact in C#, like component ids, positions, group boundaries and wiring, and checks every change it makes. Open source. Windows, Rhino 8.24+. Docs at dwight.tha.run.", flat);
      Assert.DoesNotContain("free", flat, StringComparison.OrdinalIgnoreCase); // Tharun, 6 November: "open source" says it; a person pays the provider, never Dwight.

      var keywords = manifest.Substring(manifest.IndexOf("keywords:", StringComparison.Ordinal)).Split('\n')
        .Where(l => l.TrimStart().StartsWith("- ", StringComparison.Ordinal)).Select(l => l.Trim().Substring(2)).ToArray();
      Assert.Equal(new[] { "grasshopper", "rhino", "llm", "explain", "diagnose", "reorganise", "simplify", "build" }, keywords);
    }

    /// <summary>Brief FD: the package manager showed Dwight with no icon. The manifest's `icon` names a file in the package, which the reference says "should be
    /// small (e.g. 64x64)" and "must be either a PNG or a JPEG"; the script is what puts it in the package, so all three ends are held here.</summary>
    [Fact]
    public void The_package_names_its_icon_and_the_script_puts_it_in_the_package()
    {
      Assert.Contains("\nicon: icon.png\n", Repo("packaging", "manifest.yml"));

      var png = File.ReadAllBytes(Path.Combine(RepoRoot(), "packaging", "icon.png"));
      Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png.Take(8).ToArray());
      int BigEndian(int at) => (png[at] << 24) | (png[at + 1] << 16) | (png[at + 2] << 8) | png[at + 3];
      Assert.Equal((64, 64), (BigEndian(16), BigEndian(20)));   // IHDR's width and height

      Assert.Contains("Copy-Item (Join-Path $PSScriptRoot 'icon.png') $dist", Repo("packaging", "build-yak.ps1"));
    }

    [Fact]
    public void The_assembly_description_is_one_sentence_of_the_five_tools()
    {
      var csproj = Repo("Dwight", "Dwight.csproj");
      var start = csproj.IndexOf("<Description>", StringComparison.Ordinal) + "<Description>".Length;
      var description = csproj.Substring(start, csproj.IndexOf("</Description>", StringComparison.Ordinal) - start);
      Assert.DoesNotContain(". ", description);
      Assert.EndsWith(".", description);
      foreach (var tool in new[] { "Explain", "Diagnose", "Reorganise", "Simplify", "Build" }) Assert.Contains(tool, description);
      // What `yak spec`, the file's Properties sheet and any tool that reads the assembly show (Brief FD): the frame, never the copy of a year ago.
      Assert.DoesNotContain("Grasshopper-native", description);
      Assert.DoesNotContain("Scaffold", description);
    }

    // ---- Build runs on all five, and the page says how far -----------------------------------------------------------------------------

    [Fact]
    public void Build_runs_on_all_five_is_what_the_catalogue_says()
    {
      foreach (var provider in new[] { "Anthropic", "OpenAI", "Google", "OllamaLocal", "OllamaCloud" })
        Assert.Contains(Rows(), m => m["provider"].GetValue<string>() == provider && Agentic(m) && !PinOnly(m));

      var readme = Repo("README.md");
      Assert.Contains("Build needs a model that can drive tools and runs on all five", readme);
      Assert.DoesNotContain("Anthropic only", readme);
      Assert.DoesNotContain("pre-release", readme);
      Assert.Contains("its one measured build timed out on the test machine", readme);
    }

    // ---- OpenAI ------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("gpt-5.6-terra", "GPT-5.6 Terra")]
    [InlineData("gpt-5.6-sol", "GPT-5.6 Sol")]
    [InlineData("gpt-6.1-sol", "GPT-6.1 Sol")]
    [InlineData("gpt-6-astra", "GPT-6 Astra")]
    public void Each_OpenAI_model_a_page_names_for_the_chat_can_drive_it(string id, string name)
    {
      Assert.True(Agentic(Row(id)), id);
      Assert.Equal(name, Row(id)["displayName"].GetValue<string>());
      Assert.Contains(name, Public("known-limitations.md"));
      Assert.Contains(name, Public("providers-and-keys.md"));
    }

    [Fact]
    public void The_chat_goes_through_Responses_and_the_verbs_stay_on_the_chat_endpoint_as_the_pages_say()
    {
      var openAi = new OpenAIProvider("https://api.example/v1", null, "gpt-test", ProviderCapability.StrictSchema, true, "OpenAI");
      Assert.True(openAi.UsesResponses(new LlmRequest { Tools = new List<LlmTool> { new LlmTool { Name = "t" } } }));
      Assert.False(openAi.UsesResponses(new LlmRequest()));

      var page = Public("providers-and-keys.md");
      Assert.Contains("On OpenAI the chat goes through the Responses endpoint", page);
      Assert.Contains("The verbs stay on OpenAI's chat endpoint.", page);
      Assert.Contains("Economy, which chooses GPT-5.6 Terra, turns it off", page);
      Assert.Equal("none", OpenAIResponses.EffortOff);

      foreach (var text in new[] { page, Public("known-limitations.md") })
      {
        Assert.DoesNotContain("reasoning off", text);
        Assert.DoesNotContain("run the verbs only", text);
      }
    }

    // ---- models that are not yet for choosing ----------------------------------------------------------------------------------------

    /// <summary>Until the catalogue flips a row, no public page claims it, except Fable's own retention line, which says it runs only if pinned.</summary>
    [Fact]
    public void A_pin_only_model_is_claimed_nowhere_but_its_retention_line()
    {
      var pages = Directory.GetFiles(Path.Combine(RepoRoot(), "docs", "public"), "*.md").Select(f => Path.GetFileName(f))
        .ToDictionary(f => f, Public);
      pages["README.md (root)"] = Repo("README.md");

      foreach (var row in Rows().Where(PinOnly))
      {
        var shortName = row["displayName"].GetValue<string>().Replace("Claude ", "");
        foreach (var page in pages)
        {
          if (!page.Value.Contains(shortName)) continue;
          Assert.Equal("privacy.md", page.Key);
          Assert.Contains("Dwight uses it only if you pin it.", page.Value);
        }
      }
    }

    [Fact]
    public void The_privacy_page_says_OpenAI_is_asked_not_to_store_the_Build_chat_and_the_wire_does_ask()
    {
      Assert.Contains("On OpenAI, the Build chat asks OpenAI not to store the conversation (`store: false`)", Public("privacy.md"));
      var body = (System.Text.Json.Nodes.JsonObject)JsonNode.Parse(OpenAIResponses.BuildBody(new LlmRequest { System = "s", Messages = { LlmMessage.User("hi") } }, "model-test"));
      Assert.False(body["store"].GetValue<bool>());
    }

    [Fact]
    public void Known_limitations_says_the_models_domain_claims_are_the_models_and_never_that_Dwight_checks_them()
    {
      var page = Public("known-limitations.md");
      Assert.Contains("## What the model says about your domain is the model's", page);
      Assert.Contains("Cull Pattern", page);
      Assert.Contains("G-code", page);
      // The page must not claim a check Dwight does not make (a prompt or page claim about ourselves carries a test, #315).
      Assert.DoesNotContain("verifies what the model says", page);
      Assert.DoesNotContain("checks the model's facts", page);
    }

    [Fact]
    public void The_privacy_page_says_a_note_is_sent_with_its_component_and_only_then_and_that_it_never_lives_in_the_definition()
    {
      var page = Public("privacy.md");
      Assert.Contains("## Dwight notes", page);
      Assert.Contains("kept in Dwight's own record for that file on your machine, never in the definition", page);
      Assert.Contains("It is sent to your provider with the component it is on, as text", page);
      Assert.Contains("and only then", page);
    }

    private static string RepoRoot()
    {
      var dir = new DirectoryInfo(AppContext.BaseDirectory);
      while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Dwight.sln"))) dir = dir.Parent;
      Assert.True(dir != null);
      return dir.FullName;
    }

    // ---- where cost and changes are shown ------------------------------------------------------------------------------------------------

    [Fact]
    public void The_cost_is_where_the_pages_say_and_not_in_the_dock()
    {
      foreach (var page in new[] { "providers-and-keys.md", "build.md" })
      {
        var text = Public(page);
        Assert.DoesNotContain("chat footer", text);
        Assert.DoesNotContain("The dock shows", text);
        Assert.Contains("on the line under the text box", text);
        Assert.Contains("Usage in Settings", text);
      }

      Assert.Contains("no cost and no tokens here", Repo("Dwight", "Resources", "dock-shell.html"));
      var panel = Repo("Dwight", "Resources", "panel-shell.html");
      Assert.Contains("dwightSend('hdr:usage')", panel);
      Assert.Contains("if (cmd == \"usage\") { DwightSettingsPanel.OpenAtUsage(); return; }", Repo("Dwight", "UI", "DwightScaffold.cs"));
      Assert.Contains("data-pane=\"Usage\"", Repo("Dwight", "Resources", "settings.html"));
    }

    [Fact]
    public void Ollama_clouds_cost_is_never_called_a_subscription()
    {
      foreach (var page in Directory.GetFiles(Path.Combine(RepoRoot(), "docs", "public"), "*.md"))
        Assert.DoesNotContain("subscription", File.ReadAllText(page), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_Timeline_is_where_the_pages_say_and_its_limit_and_words_are_the_codes()
    {
      Assert.Contains("title=\"Timeline\"", Repo("Dwight", "Resources", "dock-shell.html"));

      var build = Public("build.md");
      Assert.Contains("The Timeline, one icon away from History in Dwight's window", build);
      Assert.Contains("up to the latest " + HistoryCore.MaxChanges, build);
      foreach (var word in new[] { HistoryCore.WordStanding, HistoryCore.WordUndone, HistoryCore.WordGone, HistoryCore.WordUnknown })
        Assert.Contains(word, build);

      Assert.Contains("The Timeline, one icon away from History in Dwight's window", Public("verbs.md"));
      Assert.DoesNotContain("History tab", build);
      Assert.DoesNotContain("History tab", Public("verbs.md"));
    }

    [Fact]
    public void The_pages_call_the_setting_what_Settings_calls_it()
    {
      Assert.DoesNotContain("quality dial", Public("providers-and-keys.md"));
      Assert.DoesNotContain("quality dial", Public("changelog.md"));
      Assert.Contains("Settings has a profile (Economy, Balanced, Quality, Ultra).", Public("providers-and-keys.md"));
    }

    // ---- the changelog ---------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("1 November 2026: a run you stop after its request has gone out is now recorded as an estimate")]
    [InlineData("1 November 2026: Explain has a fourth way to run, Learn")]
    [InlineData("3 November 2026: Learn is now a deck of quiz cards in the panel")]
    [InlineData("1 November 2026: Explain on two or more components is now a Walk by default")]
    [InlineData("31 October 2026: Dwight has one window.")]
    [InlineData("1 November 2026: the question before a large run now counts the read and the answer")]
    [InlineData("1 November 2026: without a map, Build can place up to 12 components on one card")]
    [InlineData("1 November 2026: an Explain's steps now show what travels on each wire")]
    [InlineData("31 October 2026: on OpenAI the Build chat goes through the Responses endpoint")]
    [InlineData("31 October 2026: when a chat turn announces a change and raises no card")]
    [InlineData("31 October 2026: regions a build places stand clear of each other")]
    [InlineData("31 October 2026: the cost reads each model's own cached-input price")]
    [InlineData("31 October 2026: with Full control and Do not ask both on")]
    [InlineData("30 October 2026: Settings has a Usage section")]
    [InlineData("30 October 2026: the dock has a History tab")]
    public void The_changelog_has_the_entry(string line)
    {
      Assert.Contains(line, Public("changelog.md"));
    }

    // ---- Brief DN: what Explain now does ---------------------------------------------------------------------------------------------------------------

    /// <summary>Brief EI: Learn is a deck of quiz cards; Brief EO: mostly why-questions. Each word the page uses is the code's: the kinds and how many of each, the answer forms,
    /// the judge's three verdicts, the stamps, the mark on Dwight's reading, the three doors and the two links, and what each costs.</summary>
    [Fact]
    public void The_pages_say_what_Learn_does_and_each_part_is_the_codes()
    {
      var verbs = Public("verbs.md");
      Assert.Contains("- **Learn**: a deck of quiz cards on your selection, one card at a time in the panel.", verbs);
      Assert.Contains("Most cards ask why: why these components are used, why the data is a list or a single item there, why it is grafted, flattened or merged, why the definition takes this approach.", verbs);
      Assert.Contains("every one is marked as Dwight's reading. At most one card asks what a port holds or how many items a wire carries, read from your solved definition, and at most one asks which component a wire reaches or which components feed one; a deck has seven cards at most.", verbs);
      Assert.Contains("with Focus on, Grasshopper fills the screen and a viewport opens, and the panel stays as the card.", verbs);
      Assert.Contains("You pick one answer, tick every answer that applies, or type a value.", verbs);
      Assert.Contains("Dwight judges them with one small request to your provider's fast model, right, partly or wrong, and that request's cost shows on the card.", verbs);
      Assert.Contains("Every name on a card takes the canvas to that component and lights it, without selecting it.", verbs);
      Assert.Contains("When you answer, the card turns: right in green with a stamp, partly in amber, wrong in red with the true answer, a hint and why.", verbs);
      Assert.Contains("If Dwight cannot write three questions it can check against your selection, no deck is dealt and the card says so.", verbs);
      Assert.Contains("three ways on: Understand, the missed questions one at a time with why, at no cost; Play again, a fresh deck of why questions on the same selection, one more request whose estimate is on the button; and Ask the chat, with the questions, your answers and the score.", verbs);
      Assert.Contains("Under them, Try again shows the same cards in a new order at no cost, and Step by step goes through the same selection one step at a time.", verbs);
      Assert.Contains("The score (\"4 of 6 right\") is kept with the Explain's row in this file's history", verbs);
      Assert.Contains("Learn costs one more request per run, and says what that cost.", verbs);
      Assert.DoesNotContain("except the last card's, which asks why", verbs);   // the concept card is retired

      // what the page says is what the code does
      Assert.Equal(new[] { "Value", "Count", "Which", "Feeds", "Concept", "Why" }, Enum.GetNames(typeof(DeckKind)));   // Concept stays only so an older record reads back
      Assert.Equal(new[] { "ChooseOne", "ChooseAll", "Enter" }, Enum.GetNames(typeof(DeckForm)));
      Assert.Equal(7, DeckPlanner.MaxCards);   // "seven cards at most"
      Assert.Equal(3, DeckPlanner.MinCards);   // "three questions it can check"
      foreach (var (topic, words) in new[] { ("component", "why these components"), ("list", "why the data is a list or a single item"), ("tree", "grafted, flattened or merged"), ("approach", "this approach") })
        Assert.Contains(words, DeckReading.Phrase(topic));
      Assert.Equal(new[] { JudgeVerdict.Right, JudgeVerdict.Partly, JudgeVerdict.Wrong }, new[] { "right", "partly", "wrong" }.Select(DeckGrader.Verdict).ToArray());
      Assert.Equal("Right", DeckText.RightStamp);
      Assert.Equal("Partly", DeckText.PartlyStamp);
      Assert.Equal("Dwight's reading", DeckText.DwightsReading);
      Assert.Equal(new[] { "Understand", "Play again", "Ask the chat", "Try again", "Step by step" }, new[] { DeckText.Understand, DeckText.PlayAgain, DeckText.AskTheChat, DeckText.TryAgain, DeckText.WalkThis });
      Assert.Contains("no new call", DeckText.UnderstandTitle);
      Assert.Contains("no model call", DeckText.TryAgainTitle);
      Assert.Contains("one new call", DeckText.PlayAgainTitle(null));
      Assert.Equal("4 of 6 right", TeachingText.Score(4, 6));
      Assert.Contains("practice, not a lock", DeckText.Peek);
      Assert.StartsWith("Questions cost one more call", TeachingText.Cost(2.1));
    }

    [Fact]
    public void The_pages_say_Explain_sends_structure_only_except_Learn_which_sends_one_value_per_question()
    {
      var verbs = Public("verbs.md");
      Assert.Contains("Explain's Step by step, Detailed and Short modes send structure only", verbs);
      Assert.Contains("Learn sends one more request, which does carry values", verbs);
      Assert.DoesNotContain("Explain sends structure only: component types", verbs);   // the unconditional sentence that Learn made false

      var privacy = Public("privacy.md");
      Assert.Contains("| **Explain** (Step by step, Detailed, Short) | Structure only:", privacy);
      Assert.Contains("| **Explain, Learn** | Step by step's request, and then one more:", privacy);
      Assert.Contains("This is the one Explain that sends a value.", privacy);

      // the row the privacy page is in prose for exists, and its one Value is the true value
      var row = PayloadContract.For("explain.learn");
      Assert.NotNull(row);
      Assert.Equal(new[] { "cards[].true" }, row.Fields.Where(f => f.Carries == Carries.Value).Select(f => f.Path).ToArray());

      // Brief EO: an answer typed in your own words has its own row, and the page names everything it carries
      Assert.Contains("| **Explain, Learn, an answer in your own words** | Only when you type an answer and send it: the card's question, its right answer and why, as Dwight's quiz wrote them, and what you typed, as you typed it, to your provider's fast model. |", privacy);
      Assert.Equal(new[] { "answer", "explain", "question", "true" }, PayloadContract.For("explain.learn.judge").Fields.Select(f => f.Path).OrderBy(p => p, StringComparer.Ordinal).ToArray());
    }

    /// <summary>The Lens's words come from the plugin: the code that writes them never reaches a provider, so the page's "no model call and no cost" is a fact about the source.</summary>
    [Fact]
    public void The_pages_say_the_Lens_costs_nothing_and_its_code_never_reaches_a_provider()
    {
      Assert.Contains("Dwight writes those words itself from the solve, with no model call and no cost.", Public("verbs.md"));
      foreach (var file in new[] { "TreeLens.cs", "TreeLensCore.cs" })
      {
        var text = Repo("Dwight", "UI", file);
        Assert.DoesNotContain("ILlmProvider", text);
        Assert.DoesNotContain("StructuredOutput", text);
        Assert.DoesNotContain("SpendOf", text);
      }
      Assert.Contains("Learn makes it a deck of quiz cards that take you round the canvas.", File.ReadAllText(Path.Combine(RepoRoot(), "README.md")));   // the root README says it too
    }

    /// <summary>A stopped run's cost is an estimate from the request's size, marked, the input's price alone, and the page says exactly how far it reaches (Brief EG); the page's example
    /// is the words the code writes, and its "fewest tokens" and its "three" are what the code does.</summary>
    [Fact]
    public void The_pages_say_a_stopped_runs_cost_is_an_estimate_and_how_far_it_reaches()
    {
      var limits = Public("known-limitations.md");
      Assert.Contains("- **The cost of a run you stop is often an estimate.**", limits);
      Assert.Contains("It is the input's price alone: the request counted at the fewest tokens for its size that your recent runs on that model have used on this computer, " +
        "and priced as if all of it had been read from the provider's cache. So it is at or under what any of those runs would have cost for it, and that is as far as it goes.", limits);
      Assert.Contains("A request the model counts more tightly than any Dwight has measured can cost less than it says, and so can a chat turn, because Dwight measures how the verbs' requests count, not the chat's.", limits);
      Assert.Contains("Until a model has three runs measured, Dwight goes by its provider's runs, and until the provider has three, by a stated ratio.", limits);
      Assert.Equal(3, LedgerSample.AtLeast);
      Assert.DoesNotContain("A chat turn's estimate is at the cached price", limits);   // a verb's is too since the known item after #254 closed, so the page says it of every run
      Assert.Contains("marked estimated (for example, \"about 4 c\")", limits);
      Assert.Contains("A run stopped before anything was sent costs nothing and shows nothing.", limits);
      Assert.Equal("about 4 c", SpendLedgerCore.EstimateWord(4.0));
      foreach (var page in new[] { limits, Public("changelog.md") })
      {
        Assert.DoesNotContain("never more than a floor", page);   // the claims the eighteenth gate's row 22 found false
        Assert.DoesNotContain("never more than the bill", page);
      }

      // The fewest tokens: three Sonnet 5 runs at 2, 2 and 3 characters a token convert 12,000 characters at 3, never at the median's 2.
      var sonnet = new ModelInfo("claude-sonnet-5", "Sonnet 5", "Anthropic", "Mid", 3.0, 15.0, 1_000_000);
      var runs = SpendLedgerCore.Sample(new[] { 6000, 6000, 4000 }.Select(tokens => SpendLedgerCore.Serialize(SpendLedgerCore.ForVerb(
        new StoredSpend { Model = sonnet.Id, CacheWriteTokens = tokens, OutputTokens = 900, Cents = 1, Chars = 12_000 }, DateTime.UtcNow, "doc", "tower.gh", "Explain", sonnet.Provider))));
      var estimate = StoppedEstimate.Of(sonnet, 12_000, TimeSpan.FromSeconds(4), runs);
      Assert.Equal(4000, estimate.InputTokens);
      Assert.Equal("stopped", estimate.Estimated);
      Assert.Equal(0, estimate.OutputTokens);                                                // the input's price alone
      Assert.Equal(sonnet.CostCents(0, 0, estimate.InputTokens, 0), estimate.Cents.Value);   // as if read from the cache
      Assert.Null(StoppedEstimate.Of(sonnet, 0, TimeSpan.Zero, null));                       // nothing sent, nothing shown
    }

    [Fact]
    public void The_pages_say_Explain_on_one_component_has_no_Walk_and_the_code_agrees()
    {
      Assert.Contains("- **Explain on one component has no Step by step.**", Public("known-limitations.md"));
      Assert.Contains("public static string ForCount(int count) => count >= 2 ? Walk : Detailed;", Repo("Dwight", "UI", "WalkCore.cs"));
      Assert.Contains("The default for one component.", Public("verbs.md"));
    }

    /// <summary>Brief EO: the limits of a deck that is mostly the model's reading, each held to the code that makes it true.</summary>
    [Fact]
    public void The_pages_say_most_Learn_cards_are_Dwights_reading_an_answer_in_your_words_is_judged_and_the_hatch_is_a_drawing()
    {
      var limits = Public("known-limitations.md");
      Assert.Contains("- **Most Learn cards are Dwight's reading.**", limits);
      Assert.Contains("Dwight checks only that every component they name is in your selection", limits);
      Assert.Contains("At most one card is read from your solved definition and one from its wires, and neither asks about geometry. A deck has seven cards at most.", limits);
      Assert.Contains("If Dwight cannot get valid questions from the model, Step by step runs without them and says so; the Explain is not failed by it.", limits);
      Assert.Contains("- **An answer in your own words is judged by a model.**", limits);
      Assert.Contains("Partly counts as missed. Where your provider has no fast model, as on some local setups, you pick an option instead, and the card says so.", limits);
      Assert.Contains("- **Learn cannot ask you to click a component on the canvas yet.**", limits);
      Assert.Contains("- **Learn's hatch is a drawing, not a lock.**", limits);
      Assert.Contains("- **A Learn run reopened from History shows its score and cannot be played again.**", limits);
      Assert.DoesNotContain("- **One Learn card is Dwight's own reading.**", limits);
      var reopened = DeckState.FromJson(new DeckState(new[] { new DeckCard { Form = DeckForm.Enter, Value = "1", Question = "q" } }).ToJson())!.ResultView().ToJsonString();
      Assert.DoesNotContain("\"again\"", reopened);
      Assert.DoesNotContain("\"playagain\"", reopened);
      Assert.Equal("Dwight could not write questions this time, so Step by step runs without them.", TeachingText.Unavailable);
      Assert.Contains("there is none to use here, so pick an option", DeckText.NoJudge);
    }

    /// <summary>Nothing is promised that is not on main: Focus mode and the hiding of notes are not claimed by any public page until their briefs merge.</summary>
    [Fact]
    public void No_public_page_claims_what_is_not_merged()
    {
      foreach (var page in Directory.GetFiles(Path.Combine(RepoRoot(), "docs", "public"), "*.md").Select(f => Path.GetFileName(f)))
      {
        var text = Public(page);
        Assert.DoesNotContain("Focus mode", text);
        Assert.DoesNotContain("floating viewport", text);
        Assert.DoesNotContain("hide all notes", text, StringComparison.OrdinalIgnoreCase);
      }
    }

    /// <summary>Brief CV: the pages a person reads state the number the plugin enforces, and a page that kept it by hand goes red the day the constant moves.</summary>
    [Fact]
    public void The_public_pages_state_the_placement_bound_the_plugin_enforces()
    {
      var most = SpotPlacement.MaxComponents;
      Assert.Contains("It can also place up to " + most + " components without a map", Public("build.md"));
      Assert.Contains("**Without a map it places at most " + most + " components at once.**", Public("known-limitations.md"));
      Assert.Contains("More than " + most + " wants a map", Public("known-limitations.md"));
      Assert.DoesNotContain("place a few components", Public("build.md"));
    }

    /// <summary>The forced card's line is the rule's: every mode where the connection can force, an automatic mode only where it cannot, and a follow-up is never followed up.</summary>
    [Fact]
    public void The_forced_cards_line_says_what_the_rule_decides()
    {
      Assert.True(AutoFollowUp.ShouldRunForAnnouncement(false, true, true, false));
      Assert.False(AutoFollowUp.ShouldRunForAnnouncement(false, false, true, false));
      Assert.True(AutoFollowUp.ShouldRunForAnnouncement(true, false, true, false));
      Assert.False(AutoFollowUp.ShouldRunForAnnouncement(true, true, true, true));

      Assert.Contains("under an automatic mode only", Public("changelog.md"));
    }

    // ---- nothing on a public page twice ----------------------------------------------------------------------------------------------------

    /// <summary>Found on disk and not listed here, so a new page is held from the day it exists.</summary>
    public static IEnumerable<object[]> PublicPages()
    {
      var root = RepoRoot();
      return Directory.GetFiles(Path.Combine(root, "docs", "public"), "*.md", SearchOption.AllDirectories)
        .Concat(new[] { Path.Combine(root, "README.md") })
        .Select(f => new object[] { Path.GetRelativePath(root, f).Replace('\\', '/') });
    }

    private static string RepoFile(string relative) => File.ReadAllText(Path.Combine(RepoRoot(), relative));

    /// <summary>Each heading's words, outside code fences, where a line starting with a hash is a comment. The level is not part of it: a section doubled by a merge is the same words wherever it landed.</summary>
    private static List<string> Headings(string page)
    {
      var found = new List<string>();
      var fenced = false;
      foreach (var line in page.Replace("\r\n", "\n").Split('\n'))
      {
        if (line.TrimStart().StartsWith("```", StringComparison.Ordinal)) { fenced = !fenced; continue; }
        if (fenced) continue;
        var heading = Regex.Match(line, @"^#{1,6}\s+(.*\S)\s*$");
        if (heading.Success) found.Add(Words(heading.Groups[1].Value));
      }
      return found;
    }

    /// <summary>Each changelog entry: a paragraph, or one line of a list, and never a heading, which has its own rule.</summary>
    private static List<string> ChangelogEntries(string page)
    {
      var found = new List<string>();
      var paragraph = new List<string>();
      void Close()
      {
        if (paragraph.Count > 0) found.Add(Words(string.Join(" ", paragraph)));
        paragraph.Clear();
      }
      foreach (var line in page.Replace("\r\n", "\n").Split('\n'))
      {
        if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#", StringComparison.Ordinal)) { Close(); continue; }
        if (line.StartsWith("- ", StringComparison.Ordinal)) { Close(); found.Add(Words(line)); continue; }
        paragraph.Add(line);
      }
      Close();
      return found;
    }

    private static string Words(string text) => Regex.Replace(text, @"\s+", " ").Trim();

    private static List<string> Doubled(IEnumerable<string> items) =>
      items.GroupBy(i => i, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();

    /// <summary>Two merges on 1 November doubled sections of these pages, and only a person reading them caught one. Git merges two copies of the same new text without a conflict, and nothing else here looks for it.</summary>
    [Theory]
    [MemberData(nameof(PublicPages))]
    public void No_public_page_carries_the_same_heading_twice(string page)
    {
      Assert.Empty(Doubled(Headings(RepoFile(page))));
    }

    [Fact]
    public void The_changelog_carries_no_entry_twice()
    {
      Assert.Empty(Doubled(ChangelogEntries(Public("changelog.md"))));
    }

    /// <summary>The positive control: a rule that reads nothing also finds nothing doubled, so it must be seen reading the real pages.</summary>
    [Fact]
    public void The_rule_reads_the_real_pages()
    {
      var pages = PublicPages().Select(p => (string)p[0]).ToList();
      Assert.Contains("README.md", pages);
      Assert.Contains("docs/public/changelog.md", pages);
      Assert.Contains("docs/public/verbs.md", pages);
      Assert.DoesNotContain(pages, p => p.EndsWith(".txt", StringComparison.Ordinal));

      Assert.Contains("Explain", Headings(Public("known-limitations.md")));
      Assert.Contains("Changelog", Headings(Public("changelog.md")));
      Assert.True(ChangelogEntries(Public("changelog.md")).Count >= 30);
      Assert.Contains(ChangelogEntries(Public("changelog.md")), e => e.StartsWith("1 November 2026: ", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("# A\n\n## B\n\ntext\n\n## B\n", "B")]
    [InlineData("## Explain\n\n### Explain\n", "Explain")]    // the same words at another level
    [InlineData("## Two  words\n\n## Two words\n", "Two  words")]    // what differs is only the spacing
    [InlineData("## Explain\n\n## explain\n", "Explain")]
    public void A_heading_twice_is_found(string page, string first)
    {
      Assert.Equal(new[] { Words(first) }, Doubled(Headings(page)));
    }

    [Theory]
    [InlineData("## A\n\n## B\n")]
    [InlineData("## A\n\n```\n## A\n```\n")]    // a comment in a code block is not a heading
    [InlineData("## A\n\n```sh\n# run\n# run\n```\n")]
    [InlineData("#hashtag\n\n#hashtag\n")]    // no space, so not a heading
    [InlineData("## A\n\nA\n")]
    public void What_is_not_a_heading_twice_is_not_found(string page)
    {
      Assert.Empty(Doubled(Headings(page)));
    }

    [Theory]
    [InlineData("# Changelog\n\n1 May: one.\n\n1 May: two.\n\n1 May: one.\n", "1 May: one.")]
    [InlineData("1 May: split\nover two lines.\n\n1 May: split over two lines.\n", "1 May: split over two lines.")]
    [InlineData("## 0.1\n\n- **Explain**: a walk.\n- **Diagnose**: a look.\n- **Explain**: a walk.\n", "- **Explain**: a walk.")]
    public void An_entry_twice_is_found(string page, string entry)
    {
      Assert.Equal(new[] { entry }, Doubled(ChangelogEntries(page)));
    }

    [Theory]
    [InlineData("1 May: one.\n\n1 May: two.\n")]
    [InlineData("## 0.1\n\n## 0.1\n")]    // a heading is the other rule's
    [InlineData("1 May: one.\n\n2 May: one.\n")]    // the same words on another day are two entries
    public void What_is_not_an_entry_twice_is_not_found(string page)
    {
      Assert.Empty(Doubled(ChangelogEntries(page)));
    }
  }
}
