using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using Dwight.Models;
using Dwight.Session;

namespace Dwight.Agent
{
  /// <summary>One entry in the header's model list.</summary>
  internal sealed class HeaderChoice
  {
    public string Id { get; set; }
    public string Label { get; set; }
    /// <summary>"posture" or "model".</summary>
    public string Group { get; set; }
    /// <summary>The choice's hover, or null. Brief BZ: what a row needs said before it is chosen.</summary>
    public string Title { get; set; }
  }

  /// <summary>What the header is drawn from. Every field is read from the THREAD's record (and the live catalogue), never from the shell.</summary>
  internal sealed class ChatHeaderInput
  {
    public string Provider { get; set; }
    public IReadOnlyList<ModelInfo> ProviderModels { get; set; }

    /// <summary>The thread's pinned model id, or empty when the posture chooses.</summary>
    public string ThreadModelId { get; set; }
    /// <summary>The catalogue's row for that id (any provider), or null when the catalogue does not know it.</summary>
    public ModelInfo ThreadPin { get; set; }
    /// <summary>The posture in force for this thread: its own override, else the Settings posture.</summary>
    public string Posture { get; set; }
    /// <summary>The model a turn on this thread would resolve to right now.</summary>
    public ModelInfo Resolved { get; set; }

    public string ApprovalMode { get; set; }
    public bool FullControl { get; set; }
    public SpendTotal Spend { get; set; }
    /// <summary>The last turn's receipt, shown when the cost is hovered. Empty before the first turn.</summary>
    public string Receipt { get; set; }
    /// <summary>Brief CN: the steps a turn of this chat may take, from Settings.</summary>
    public int StepsPerTurn { get; set; } = DwightConfig.AgentMaxStepsPerTurn;

    /// <summary>Brief CW: what Balanced resolves to on this provider, whatever this chat or Settings has chosen. Every price is said against it.</summary>
    public ModelInfo Balanced { get; set; }
    /// <summary>Brief CW: the model each posture would run this chat on if it were chosen, by posture id. A Settings pin stands in for all of them while it is in force.</summary>
    public IReadOnlyDictionary<string, ModelInfo> PostureModels { get; set; }
    /// <summary>Brief CW: a Settings pin is in force, so choosing a posture does not choose the model.</summary>
    public bool SettingsPinned { get; set; }
  }

  /// <summary>
  /// Brief CN: "STEPS PER TURN", under Build in Settings. Twelve for every file ended turns on a 1,289-object definition before the model had finished reading, so a person can choose.
  /// Not per chat: unlike the model or the approval mode it changes no scope and no consent, so every chat takes it from its next turn. An approved build keeps its own budget.
  /// The budget scaled to a definition's size is a later ruling.
  /// </summary>
  internal static class ChatSteps
  {
    public const int Min = 4;
    public const int Max = 40;

    public static int Clamp(int steps) => Math.Max(Min, Math.Min(Max, steps));
  }

  /// <summary>
  /// THE BUILD CHAT'S HEADER (Brief AO, Tharun's rulings of 30 October 2026): four chips under the title bar - the model, the approval mode, Full
  /// control and the cost so far - so the controls a user changes while working are where they work, with Settings keeping the defaults.
  ///
  /// THE SHELL HOLDS NO VALUE THE HOST DOES NOT. Everything here is computed from the thread's record and handed to the shell as one JSON
  /// document; a click goes back through the host (<c>dwightSend('hdr:...')</c>), is validated and persisted on the thread, and the host
  /// sends the header again. GH-free (ADR-091), so the list, the texts and the resolution order are tested.
  /// </summary>
  internal static class ChatHeader
  {
    /// <summary>
    /// Does a chat pinned to this model think? It does not, as it never has, except on OpenAI (Brief BV): there, off is <c>reasoning.effort: none</c>, the weakness the move to
    /// the Responses endpoint exists to end, and GPT-6 Astra and GPT-6.1 Sol refuse it outright. So a pinned OpenAI reasoning model takes the effort the same profile gives an
    /// unpinned one, and no other provider's wire changes.
    /// Brief BZ: the same for a row that cannot be told to stop thinking (Sonnet 5.5, Opus 5.5, Fable 5.1): off there is the lowest effort a verb uses, which is not what a person
    /// pinning one for a build means, nor what the row sends when a profile resolves to it.
    /// </summary>
    public static bool PinThinks(ModelInfo pin, string provider, string profile) =>
      (string.Equals(provider, "OpenAI", StringComparison.Ordinal) || (pin != null && pin.CannotDisableThinking)) &&
      !string.Equals(profile, "economy", StringComparison.OrdinalIgnoreCase) &&
      pin != null && (pin.Reasoning || pin.ReasoningKnob);

    // ---- the model list ---------------------------------------------------------------------------------------------

    public const string PosturePrefix = "posture:";

    /// <summary>The postures as the Settings control names them, in its order. A posture is chosen per thread without a pin.</summary>
    public static readonly (string Id, string Label)[] Postures =
    {
      ("economy", "Economy"), ("balanced", "Balanced"), ("quality", "Quality"), ("ultra", "Ultra"),
    };

    public static string PostureLabel(string posture)
    {
      var p = Postures.FirstOrDefault(x => string.Equals(x.Id, posture, StringComparison.OrdinalIgnoreCase));
      return p.Label ?? "Balanced";
    }

    public static bool IsPosture(string id) =>
      Postures.Any(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The picker's list: the postures first, then every model of the current provider whose catalogue row says <c>agentic</c> is true, and
    /// nothing else. A model that cannot drive the chat is never offered, so a user cannot pick the refusal
    /// (<c>No_Fast_band_model_is_agentic_on_any_provider</c> is the law behind the filter: no Fast-band model appears).
    /// </summary>
    public static IReadOnlyList<HeaderChoice> ModelChoices(IReadOnlyList<ModelInfo> providerModels) => ModelChoices(providerModels, null, null, false);

    /// <summary>The same list with each row's hover (Brief CW): what it costs against Balanced, so a person sees the price before choosing.</summary>
    public static IReadOnlyList<HeaderChoice> ModelChoices(IReadOnlyList<ModelInfo> providerModels, ModelInfo balanced, IReadOnlyDictionary<string, ModelInfo> postureModels, bool settingsPinned)
    {
      var list = Postures.Select(p =>
      {
        ModelInfo model = null;
        postureModels?.TryGetValue(p.Id, out model);
        return new HeaderChoice { Id = PosturePrefix + p.Id, Label = p.Label, Group = "posture", Title = PostureTitle(model, balanced, settingsPinned) };
      }).ToList();
      foreach (var m in providerModels ?? new ModelInfo[0])
        if (m != null && m.Agentic)
          list.Add(new HeaderChoice { Id = m.Id, Label = NameOf(m), Group = "model", Title = RowTitle(m, balanced) });
      return list;
    }

    /// <summary>Is this id one the picker would offer for the provider? The host checks it before it writes anything.</summary>
    public static bool IsOffered(string id, IReadOnlyList<ModelInfo> providerModels) =>
      !string.IsNullOrEmpty(id) && ModelChoices(providerModels).Any(c => string.Equals(c.Id, id, StringComparison.Ordinal));

    private static string NameOf(ModelInfo m) =>
      m == null ? "" : string.IsNullOrWhiteSpace(m.DisplayName) ? m.Id : m.DisplayName;

    /// <summary>The row the picker marks as on: the thread's pin, else the posture in force. A change is this moving, so a redraw, or choosing what is already chosen, says nothing.</summary>
    public static string CurrentChoice(string threadModelId, string posture) =>
      !string.IsNullOrEmpty(threadModelId) ? threadModelId : PosturePrefix + (posture ?? "balanced");

    // ---- Brief CW: what a choice costs against Balanced ------------------------------------------------------------------

    public const string SamePriceLine = "the same price per token as Balanced";
    public const string NotBilledLine = "runs on this machine, nothing is billed";
    public const string SameModelLine = "the same model as Balanced";

    /// <summary>Input plus output per million tokens, the sum <see cref="ModelPick.Choose"/> orders by.</summary>
    private static double PriceOf(ModelInfo m) => m.InputPerMTok + m.OutputPerMTok;

    private static bool IsSameModel(ModelInfo a, ModelInfo b) =>
      a != null && b != null && string.Equals(a.Id, b.Id, StringComparison.OrdinalIgnoreCase);

    private static bool IsBalanced(string posture) => PostureLabel(posture) == "Balanced";

    /// <summary>
    /// Brief CW (Tharun's ruling of 1 November 2026): what a choice costs against what Balanced resolves to on the same provider, per token, because that is what the catalogue
    /// knows. What a run costs is a measurement and stays in the gate record, so this never quotes one. Null for a choice that resolves to the Balanced model, and where the
    /// catalogue gives nothing to compare. Each line must be true of the row that reaches it: Ollama Cloud has a plan rather than a price and does not run on this machine, and
    /// Haiku 5.5 at a twentieth of Sonnet 5.5 is not about half.
    /// </summary>
    public static string PriceLine(ModelInfo chosen, ModelInfo balanced)
    {
      if (chosen == null || IsSameModel(chosen, balanced)) return null;
      if (chosen.IsLocal) return NotBilledLine;
      if (chosen.IsSubscription) return SpendMath.OllamaCloudCostShort;
      var against = balanced == null ? 0 : PriceOf(balanced);
      if (PriceOf(chosen) <= 0 || against <= 0) return null;

      var times = PriceOf(chosen) / against;
      if (Math.Abs(times - 1) < 1e-9) return SamePriceLine;
      if (times < 1)
        return times < 0.4 ? "less than half Balanced's price per token"
             : times < 0.6 ? "about half Balanced's price per token"
             : "a little less than Balanced per token";
      var n = times.ToString(times < 3 ? "0.#" : "0", CultureInfo.InvariantCulture);
      return n == "1" ? "a little more than Balanced per token" : "about " + n + " times Balanced's price per token";
    }

    private static string AsSentence(string line) =>
      string.IsNullOrEmpty(line) ? null : char.ToUpperInvariant(line[0]) + line.Substring(1) + ".";

    private static string Joined(string first, string second) =>
      string.IsNullOrEmpty(first) ? second : string.IsNullOrEmpty(second) ? first : first + " " + second;

    /// <summary>"Head, the price." A choice that is the Balanced model has no price line, but a change to it still says so, so the person learns the dial both ways.</summary>
    private static string WithPrice(string head, ModelInfo model, ModelInfo balanced)
    {
      var clause = PriceLine(model, balanced) ?? (IsSameModel(model, balanced) ? SameModelLine : null);
      return head + (clause == null ? "" : ", " + clause) + ".";
    }

    /// <summary>A Settings pin wins over every posture, so a posture row and a change of posture name the pin, in the words the chat's own refusal uses for it.</summary>
    public static string PinnedLine(ModelInfo pin, ModelInfo balanced) => WithPrice(NameOf(pin) + " is pinned for every action", pin, balanced);

    /// <summary>The hover of a row that names a model: its price, then the retention sentence where it has one.</summary>
    public static string RowTitle(ModelInfo model, ModelInfo balanced) =>
      Joined(AsSentence(PriceLine(model, balanced)), model?.RetentionSentence);

    /// <summary>The hover of a posture row, from the model that posture would run on.</summary>
    public static string PostureTitle(ModelInfo model, ModelInfo balanced, bool settingsPinned) =>
      model == null ? null : settingsPinned ? Joined(PinnedLine(model, balanced), model.RetentionSentence) : RowTitle(model, balanced);

    /// <summary>
    /// The line a change writes into the transcript, once, as chrome. Null when the choice did not move. <paramref name="model"/> is what the next turn would run on, read after
    /// the write, so the sentence is true whichever rung of the resolution order chose it.
    /// </summary>
    public static string ChangeLine(string wasChoice, string isChoice, ModelInfo model, ModelInfo balanced, bool settingsPinned)
    {
      if (model == null || string.IsNullOrEmpty(isChoice) || string.Equals(wasChoice, isChoice, StringComparison.Ordinal)) return null;
      if (!isChoice.StartsWith(PosturePrefix, StringComparison.Ordinal)) return WithPrice(NameOf(model) + " for this chat", model, balanced);
      if (settingsPinned) return PinnedLine(model, balanced);
      var posture = isChoice.Substring(PosturePrefix.Length);
      if (IsBalanced(posture)) return "Back to Balanced: " + NameOf(model) + ".";
      return WithPrice(PostureLabel(posture) + " uses " + NameOf(model) + " here", model, balanced);
    }

    /// <summary>The line under Settings' Profile control. "In a chat" because that is the model named: a verb resolves by its own task, and a small Explain stays cheap on any profile.</summary>
    public static string SettingsLine(string posture, ModelInfo model, ModelInfo balanced, bool pinned)
    {
      if (model == null) return null;
      if (pinned) return PinnedLine(model, balanced);
      var head = "In a chat, " + PostureLabel(posture) + " uses " + NameOf(model);
      return IsBalanced(posture) ? head + "." : WithPrice(head, model, balanced);
    }

    // ---- the resolution order ---------------------------------------------------------------------------------------

    /// <summary>
    /// A turn resolves its model from the THREAD's pin first, then the Settings pin, then the posture. Returns the pin that applies, or null
    /// when the posture chooses. <paramref name="threadPinIgnored"/> is true when the thread HAS a pin that does not belong to the provider in
    /// use (U52's case: the provider changed under it): the turn falls back and says so, once, with <see cref="PinFallbackLine"/>.
    /// </summary>
    public static ModelInfo PickPin(string threadModelId, ModelInfo threadPin, ModelInfo settingsPin, string provider, out bool threadPinIgnored)
    {
      threadPinIgnored = false;
      if (!string.IsNullOrEmpty(threadModelId))
      {
        if (ModelPick.PinBelongsTo(threadPin, provider)) return threadPin;
        threadPinIgnored = true;
      }
      return ModelPick.PinBelongsTo(settingsPin, provider) ? settingsPin : null;
    }

    public const string PinFallbackLine =
      "The model pinned for this chat is not on this provider, so Dwight used the profile to choose one instead. Pick a model under the chat's text box to pin another.";

    // ---- the chips' texts -------------------------------------------------------------------------------------------

    /// <summary>"Sonnet 5" when a pin chooses, "Balanced · Sonnet 5" when the posture does, "GPT-5.6 Terra (not on this provider)" for a stale pin.</summary>
    public static string ModelChip(ChatHeaderInput i)
    {
      var pinned = !string.IsNullOrEmpty(i.ThreadModelId);
      if (pinned)
      {
        var name = i.ThreadPin != null ? NameOf(i.ThreadPin) : i.ThreadModelId;
        if (!ModelPick.PinBelongsTo(i.ThreadPin, i.Provider)) return name + " (not on this provider)";
        return name;
      }
      var resolved = i.Resolved != null ? NameOf(i.Resolved) : null;
      return resolved == null ? PostureLabel(i.Posture) : PostureLabel(i.Posture) + " · " + resolved;
    }

    public static string ApprovalChip(string mode)
    {
      switch (ApprovalMode.Normalise(mode))
      {
        case ApprovalMode.ScriptsAndDeletions: return "Scripts and deletions";
        case ApprovalMode.Never: return "Do not ask";
        default: return "Ask every time";
      }
    }

    public static string FullControlChip(bool on) => on ? "Full control on" : "Full control off";

    /// <summary>Brief CN: "12 steps a turn", on the chat's own line, so the number the note states is one a person has already seen.</summary>
    public static string StepsChip(int steps) => ChatSteps.Clamp(steps) + " steps a turn";

    /// <summary>"~23.5¢ · 3 turns", "not priced · 3 turns" for a model with no per-call price, "no turns yet" before the first. The same totals the footer uses.</summary>
    public static string CostChip(SpendTotal t)
    {
      if (t.Turns == 0) return "no turns yet";
      var turns = t.Turns + (t.Turns == 1 ? " turn" : " turns");
      return (t.AnyPriced ? SpendMath.CentsText(t.Cents) : "not priced") + " · " + turns;
    }

    // ---- the composer's one line: what each icon says when hovered (Brief BI). Each is also the button's accessible name. ------------------

    /// <summary>"Model: Sonnet 4.6, pinned for this chat", "Model: Balanced chooses, Sonnet 5 now", "Model: GPT-5.6 Terra, not on this provider; the profile chooses".</summary>
    public static string ModelTip(ChatHeaderInput i)
    {
      if (!string.IsNullOrEmpty(i.ThreadModelId))
      {
        var name = i.ThreadPin != null ? NameOf(i.ThreadPin) : i.ThreadModelId;
        return ModelPick.PinBelongsTo(i.ThreadPin, i.Provider)
          ? "Model: " + name + ", pinned for this chat"
          : "Model: " + name + ", not on this provider; the profile chooses";
      }
      var chooses = "Model: " + PostureLabel(i.Posture) + " chooses";
      return i.Resolved == null ? chooses : chooses + ", " + NameOf(i.Resolved) + " now";
    }

    /// <summary>"Approval: ask every time", in the Settings' words.</summary>
    public static string ApprovalTip(string mode)
    {
      switch (ApprovalMode.Normalise(mode))
      {
        case ApprovalMode.ScriptsAndDeletions: return "Approval: ask for scripts and deletions only";
        case ApprovalMode.Never: return "Approval: do not ask";
        default: return "Approval: ask every time";
      }
    }

    public static string FullControlTip(bool on) => on ? "Full control: on" : "Full control: off";

    /// <summary>The plugin line the thread carries when Full control is turned on from the header, so the transcript has the moment.</summary>
    public static string FullControlOnLine => "Full control is on for this chat: " + FullControl.SettingSentence;

    /// <summary>Brief CD: said once, when a chat comes to have both Full control and "Do not ask", whichever came first, so a person knows what they have chosen.</summary>
    public const string ScriptsRunLine = "Scripts run without asking in this chat, and one Ctrl-Z may not undo everything they touch.";

    /// <summary>Did this change make scripts run without asking in this chat? True only on the step into it, in either order, so it is said once and not on every toggle.</summary>
    public static bool ScriptsRunNewly(bool wasFullControl, string wasMode, bool isFullControl, string isMode) =>
      FullControl.RunsWithoutAsking(isMode, isFullControl) && !FullControl.RunsWithoutAsking(wasMode, wasFullControl);

    /// <summary>What a chat opened with both already on says under its greeting, or nothing.</summary>
    public static string OpeningScriptsHtml(string mode, bool fullControl) =>
      FullControl.RunsWithoutAsking(mode, fullControl) ? "<p>" + System.Net.WebUtility.HtmlEncode(ScriptsRunLine) + "</p>" : "";

    // ---- the document the shell draws -----------------------------------------------------------------------------

    public static string Json(ChatHeaderInput i)
    {
      var choices = new JsonArray();
      foreach (var c in ModelChoices(i.ProviderModels, i.Balanced, i.PostureModels, i.SettingsPinned))
      {
        var on = string.Equals(c.Id, CurrentChoice(i.ThreadModelId, i.Posture), StringComparison.Ordinal);
        var choice = new JsonObject { ["id"] = c.Id, ["label"] = c.Label, ["group"] = c.Group, ["on"] = on };
        if (!string.IsNullOrEmpty(c.Title)) choice["title"] = c.Title;
        choices.Add(choice);
      }

      var mode = ApprovalMode.Normalise(i.ApprovalMode);
      var modes = new JsonArray();
      foreach (var m in ApprovalMode.All)
        modes.Add(new JsonObject { ["id"] = m, ["label"] = ApprovalChip(m), ["title"] = ApprovalMode.Description(m), ["on"] = m == mode });

      return new JsonObject
      {
        ["model"] = new JsonObject { ["text"] = ModelChip(i), ["title"] = ModelTip(i), ["choices"] = choices },
        ["approval"] = new JsonObject { ["text"] = ApprovalChip(mode), ["title"] = ApprovalTip(mode), ["mode"] = mode, ["choices"] = modes },
        ["full"] = new JsonObject { ["on"] = i.FullControl, ["text"] = FullControlChip(i.FullControl), ["tip"] = FullControlTip(i.FullControl), ["title"] = FullControl.SettingSentence },
        ["cost"] = new JsonObject { ["text"] = CostChip(i.Spend) + " \u00B7 " + StepsChip(i.StepsPerTurn), ["title"] = i.Receipt ?? "" },
      }.ToJsonString();
    }

    // ---- the Settings hint -----------------------------------------------------------------------------------------------

    public const string SettingsHint =
      "A chat keeps its own model, approval mode and Full control; change them under the chat's text box. These are the defaults for a new chat.";
  }
}
