using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Dwight.Agent
{
  /// <summary>
  /// THE HOST TOOL SET (Phase-8 WP-3 Stage B, ruled at #230 Fork 1).
  ///
  /// THE LAW THIS FILE EXISTS TO KEEP: <see cref="AgentToolCatalog"/> is what you can do TO THE CANVAS -
  /// and it IS the MCP <c>list_tools</c> response (ADR-069/098). This set is what you can do IN THIS
  /// SURFACE. Putting a card-rendering tool in the catalogue would export Dwight's consent model as part
  /// of the canvas contract, and that category error would reach v1.1 as a broken tool: an MCP client
  /// brings its own consent model, and asking a human is the host's job under the MCP spec, not a
  /// capability of the canvas.
  ///
  /// A HOST TOOL MAY NEVER BE A WRITE PATH (#230). It is UI-only by construction, and the construction is
  /// the point rather than the promise: the loop routes a host tool to the HOST HANDLER, which is handed
  /// no <see cref="AgentScope"/> at all, so there is no reference through which it could reach the
  /// document. If anything in this set ever needs to touch the canvas it belongs in the catalogue, where
  /// the verdict law applies (ADR-098). The tests hold both halves - no host tool declares
  /// <c>Mutates</c> or <c>Solves</c>, and no host name collides with a catalogue name.
  /// </summary>
  internal static class AgentHostTools
  {
    public const string RequestMutation = "request_mutation";

    /// <summary>
    /// The changes the model may ASK for, each routing to a flow that already exists and already carries
    /// its own consent surface (ADR-075's routing half).
    ///
    /// <c>scaffold</c> JOINED THE SET AT STAGE C (#232's held enumeration opening), the moment
    /// <c>propose_scaffold</c> existed to honour it - not before. A card whose Approve leads nowhere is
    /// the dead-button class the WP-1 gate caught (#174), so the enum grows only when the flow behind it
    /// does. A kind this build cannot honour is still refused BY NAME with the list of what is available
    /// (#208's shape, ADR-092's repair payload) - never silently ignored, never a rendered dead card.
    /// </summary>
    public static readonly IReadOnlyList<string> Kinds = new[] { "reorganise", "simplify", "solve", "scaffold", "build", "set_value", "edit_script", "edit" };

    /// <summary>
    /// What each kind CAN do and CANNOT do, in the user's terms - BOTH HALVES, FOR EVERY KIND (#574, #577).
    ///
    /// THE HISTORY, because the shape is the fix. F6 (the WP-4 gate) wrote only the "does NOT" half, on
    /// purpose: the model had reached for the nearest-sounding kind twice - `solve`, then `simplify` - and
    /// the missing fact was what each kind was NOT for. It worked for those. And it produced the next three
    /// instances, which is how the cause became visible:
    ///
    ///   - `build`'s entry was one line, saying nothing about initial values or rebuilding. A request to
    ///     replace a region's contents pattern-matched simplify's "refactors the LOGIC" and routed out to
    ///     Simplify again - and asked for sliders at 300 and 400, the agent said it "can only place
    ///     default-valued components", minutes after placing sliders at 10 and 6.
    ///   - `simplify`'s entry described only logic refactoring. Asked for a C# script, the agent claimed
    ///     simplify "explicitly does not write scripts" - false, to-script is its second mode - and missed
    ///     the one thing it could honestly have offered.
    ///
    /// A DESCRIPTION THAT STATES ONLY WHAT A KIND DOES NOT DO INVITES THE MODEL TO INVENT WHAT IT DOES. And
    /// under capability-by-construction (ADR-075) the model sees build_region's full schema only during a
    /// build turn; in a planning turn it reasons from these entries and nothing else.
    ///
    /// So each kind is a <see cref="KindCapability"/> with a CAN half and a CANNOT half, and a test fails if
    /// either is empty for any kind. Writing only one half is no longer something a later edit can do by
    /// accident - which is the difference between fixing five strings and fixing the cause.
    ///
    /// Each half is bounded in BOTH directions. simplify's CAN half names to-script, and its CANNOT half
    /// says to-script needs existing components and cannot write a script from a plan - so the correction
    /// does not over-claim the other way.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, KindCapability> KindScope =
      new Dictionary<string, KindCapability>(StringComparer.OrdinalIgnoreCase)
      {
        ["reorganise"] = new KindCapability(
          can: "re-lays-out and groups what is already on the canvas so it reads clearly - flat labelled " +
               "groups in a readable flow, as one Ctrl-Z.",
          cannot: "change logic, values, or what anything computes."),

        ["simplify"] = new KindCapability(
          can: "opens the Simplify flow on EXISTING components, where the user chooses between Better logic " +
               "(a cleaner, more robust equivalent with the design levers exposed) and collapsing the " +
               "selection into ONE C# or Python script component - experimental, checked against the " +
               "originals, and a verified script when it can be, a labelled draft when it cannot.",
          // #685: the value clause points at the kind that DOES it, so the model is not left to invent where it lives
          // (#574's lesson; the 2026-09-08 re-gate routed a slider's domain and rounding to the Simplify panel).
          cannot: "set values, domains, or rounding on existing components - that is set_value - or rename or " +
                  "re-parameterise them. And it cannot write a script from a PLAN or from nothing: a script " +
                  "collapse needs existing components to check itself against."),

        ["solve"] = new KindCapability(
          can: "re-runs the definition so stale outputs refresh and can be read.",
          cannot: "apply any change. It CHANGES NOTHING, so it is never a way to make a change you want made."),

        ["scaffold"] = new KindCapability(
          can: "lays down a map of named regions for a new design, each with its purpose, pseudo-code and " +
               "the inputs and outputs it expects - the regions build then fills.",
          cannot: "place any components. The regions it lays down are EMPTY."),

        ["build"] = new KindCapability(
          can: "places and wires real components into scaffold regions the user approved, WITH INITIAL " +
               "VALUES - a Number Slider's domain, value and rounding, and a Panel's text. It can also place " +
               "up to " + SpotPlacement.MaxComponents + " components WITHOUT a map at all, and it names every one on the " +
               "card: the user approves that placement on its own, and Dwight gives it a place of its own, " +
               "clear of everything on the canvas, or lands it inside a region or group already there when you name one. That placement can include a C# or Python script WITH ITS SOURCE: the card shows the " +
               "complete code before it runs. More than " + SpotPlacement.MaxComponents + " needs a map first - ask for propose_scaffold. It can REBUILD a " +
               "region: it removes the components Dwight placed there that the user has not edited since, " +
               "names each one on the card before anything is removed, and builds the region again, " +
               "reconnecting, each in its own place, anything outside the region those components fed - which " +
               "is how a region's contents are replaced with different logic. A FRESH build of a region can also " +
               "restore an input on a component Dwight placed whose only change is losing its source when that " +
               "region's earlier components were removed.",
          // #628/#633, made true at #643: the wiring clause says exactly what BuildPreservation's rule 3 rejects.
          // A new wire INTO an existing component is accepted from any SOURCE the build places - since #643 that
          // includes a component's output, which the check used to misread as an existing component's. And a
          // rebuild still cannot cut a wire from what it removes into anything it keeps: it reconnects each one in
          // place, and one nothing new should feed is the user's to delete first (#625, #647).
          cannot: "connect two components that already exist to each other - a new wire into an existing " +
                  "component may come from anything that same build places, component outputs included - or " +
                  "remove, reorder or replace any wire that already exists, which means a rebuild cannot cut a " +
                  "wire from the components it removes into anything outside what it removes: it reconnects each " +
                  "one, in place, to an output of the new build, and a wire nothing new should feed must be " +
                  "removed by the user by hand first; remove anything " +
                  "Dwight did not place or the user has since edited; build outside the regions the user " +
                  "approved; or change values on components that already existed - that is set_value."),

        // Phase-9 U4, text ruled at #685. One component per card, applied by the plugin from the click (Q1); the
        // one re-read turn is what the approval grants (Q2); the restore rule's refusal names the steps. "remove", not
        // the ruled "delete": the generated sentence may not contain "delete" at all, because no kind backs one and the
        // gate caught the model promising a delete card (BuildScopeTests) - the same one-word change Unit 2 made.
        ["set_value"] = new KindCapability(
          can: "changes what ONE existing Number Slider holds - its domain, rounding and value - or ONE existing " +
               "Panel's text, exactly as the card states it, as one Ctrl-Z; the plugin applies it on the click, " +
               "and you are then given one turn to re-read the result.",
          cannot: "place, wire, rename or remove anything; change more than one component per card, or any type " +
                  "other than a Number Slider or a Panel; or, in one step, change a slider's rounding to a grid " +
                  "its current value is not on (the refusal names the steps)."),

        // Phase-9 U20. The CAN half names the two claims by their EXACT words, because the claim is the one
        // field here whose value decides which PROOF runs - a model that does not know "preserves" and
        // "changes" are the vocabulary will reach for a third word and be refused for it.
        ["edit_script"] = new KindCapability(
          can: "rewrites the source of ONE existing C# or Python script component, and may APPEND new inputs " +
               "or outputs to it, or change how an existing input receives its data (item, list or tree). You declare what the edit does - \"preserves\" (the component still produces " +
               "the same outputs: Dwight runs your version beside the original on the same data and compares " +
               "them, and will not offer it if they differ) or \"changes\" (it deliberately produces something " +
               "different: Dwight checks the definition still solves and the user reads the diff). The user " +
               "sees the diff AND the complete new source before anything runs; one Ctrl-Z undoes it.",
          cannot: "remove, reorder or rename the ports it already has - those carry the user's existing wires, " +
                  "so only APPENDING is allowed; edit more than one component per card; edit a script whose " +
                  "source Dwight cannot read; or claim \"preserves\" for a component that is currently errored " +
                  "or disabled, which has no working behaviour to preserve."),

        // Phase-9 U6 + U21, ruled at #760: consented edits, BATCHED. The one kind that DELETES, which is why the
        // generated sentence now contains the word (BuildScopeTests holds that the word appears if and only if this
        // kind is offered - the gate caught the model promising a delete card no kind backed, #926). Every clause
        // below is a claim about the plugin's behaviour and carries a test on that behaviour's side (#315).
        ["edit"] = new KindCapability(
          can: "changes the WIRING of EXISTING components, batched on ONE card: add a wire from one component's " +
               "output to another's input, remove an existing wire, or delete an existing component (and with it " +
               "every wire on it). Name each by id from a previous read, and a port by its name where the " +
               "component has several. The user sees every change as its own row and ticks the ones they accept; " +
               "a delete names the inputs it leaves without a source. Dwight applies only the ticked rows on the " +
               "click, checks that nothing else on the canvas changed (and puts everything back if it did), as one " +
               "Ctrl-Z, and you are then given one turn to re-read the result.",
          cannot: "place, rename or move anything, set a value, or change a script's source - those are build, " +
                  "set_value and edit_script; wire a component into itself or make a loop; touch a group or a note " +
                  "(except that a delete which empties a group Dwight made removes that group, and the report says so); " +
                  "or REPLACE a wire in one row - a wire is ADDED alongside what an input already has, so replacing " +
                  "one is an unwire row and a wire row. At most 40 changes go on one card, and every row must name " +
                  "something that exists or the whole card is refused with the failing rows named."),
      };

    /// <summary>
    /// The capability sentence for the master prompt, GENERATED from <see cref="Kinds"/> (F3).
    ///
    /// #316 in its strongest available form: the claim is generated from the thing it describes, so it
    /// CANNOT claim a capability that does not exist or omit one that does. The prompt used to hand-write
    /// "exactly five things: reorganise, simplify, solve, scaffold and BUILD" - true when written, and
    /// one enum change away from being false in either direction.
    ///
    /// It matters because of what the gate caught: asked to delete, the model said deletion "has to be
    /// routed through an approval card" and produced none, because there is no delete kind. Had it simply
    /// CALLED request_mutation with kind "delete" it would have been refused by name with the real list -
    /// the honest answer existed and it talked itself out of asking. This puts that list where it is
    /// composing prose rather than only where it is choosing a tool.
    /// </summary>
    public static string CapabilitySentence()
    {
      var lines = Kinds.Select(k => "   - " + k + ": " + Described(k));

      return "You may ask the user to approve exactly " + Kinds.Count + " things, and nothing else:" +
             Environment.NewLine + string.Join(Environment.NewLine, lines) + Environment.NewLine +
             "   If what you need is not one of these, DO NOT pick the nearest one. Say plainly that you " +
             "cannot do it and NAME the missing capability." + Environment.NewLine +
             "   To point at components while you explain, call show_on_canvas with their ids.";
    }

    public static IReadOnlyList<ToolDescriptor> All() => new List<ToolDescriptor>
    {
      new ToolDescriptor
      {
        // UI-only: it changes nothing and solves nothing. It puts a question to the user and returns.
        Name = RequestMutation, Mutates = false, Solves = false,
        // F6: each kind says what it does AND what it does not, from the ONE table - so the schema and
        // the master prompt cannot describe the same kind differently. set_value's wording lives in that
        // table too (#685).
        //
        // THIS IS THE SCHEMA #820 BROKE, AND THE ONE THE OBJECT FORM EXISTS FOR (#823 Part 2). It is the
        // only schema we compose at RUNTIME: SchemaKindText() is built from the kind table, and until U20
        // no kind description contained a quotation mark. edit_script's does - it names its two claims as
        // "preserves" and "changes", because those are the literal values the model must send - and as a
        // hand-quoted string that raw quote closed the description and made the whole published descriptor
        // unparseable. It needed a JsonString() call at the point the text entered JSON; built as an
        // object it needs nothing, because serialisation escapes at the destination (#335) and there is no
        // longer a hand-written quotation mark for a value to collide with.
        InputSchema = Sch.Object(Sch.Props(
          ("kind", Sch.Enum("Which flow to offer the user. " + SchemaKindText(),
            "reorganise", "simplify", "solve", "scaffold", "build", "set_value", "edit_script", "edit")),
          ("why", Sch.Str("One sentence, in the user's language, on what this " +
            "would do for them and why you are suggesting it.")),
          ("components", Sch.Arr(Sch.Int(),
            "Optional. The component ids this is about, so the flow opens on them instead " +
            "of on whatever the user happens to have selected. Ids only, from what you have read.")),
          ("mode", Sch.Str("Optional. Which option to pre-select. The " +
            "user can still change it.")),
          ("regions", Sch.Arr(Sch.Str(),
            "For kind=build INTO A MAP: the region ADDRESSES to build (A1, A2 ...), from " +
            "get_scaffold_map. The user approves exactly these and you can build no others, so ask for " +
            "what you actually mean to build. Every address named must already exist - a region that is not " +
            "on the canvas is refused, never swapped for somewhere else. To place up to " + SpotPlacement.MaxComponents + " components with no " +
            "map at all, LEAVE THIS OUT and give \"place\" instead.")),
          // U65: THE CARRIER FOR CODE IN A REGION BUILD. The card shows every script's complete source before the user approves,
          // and the later build_region must send the same source unchanged or it is refused (BuildScriptGate).
          ("scripts", Sch.Arr(Sch.Sub(null, Sch.Props(
            ("region", Sch.Str("One of the addresses in \"regions\" - the region this script is built into.")),
            ("type", Sch.Enum("The script component's language.", BuildScriptTypes.PythonTypeName, BuildScriptTypes.CSharpTypeName)),
            ("source", Sch.Str("The COMPLETE script, not a fragment: the user is shown all of it, untruncated, on the card, and THE CODE " +
              "RUNS ON THEIR MACHINE when they approve. Send the identical text to build_region afterwards; a changed script is refused.")),
            ("inputs", BuildSpecSchema.ScriptInputs("Inputs that REPLACE x and y, as build_region takes them. The card shows each one's access, and the build takes it from the card.")),
            ("outputs", Sch.Arr(Sch.Str(), "Output names that REPLACE a, as build_region takes them."))),
            "region", "type", "source"),
            "For kind=build INTO A MAP ONLY, and only when a region needs a script (a loop, a recursion or a formula). One entry per " +
            "script; at most " + ScriptsAsk.MaxScripts + " on a card. Leave out for a build of components alone. A placement without a " +
            "map puts its source on the component in \"place\" instead.")),
          // U18: THE CARRIER. A spot card must NAME EVERY COMPONENT - a spot has no region name standing in for
          // its contents, so the components ARE the scope (#338) - and the plugin can only name what it has
          // been told, so the placement rides the ASK, as set_value's change does. What is published here is
          // BuildSpecSchema's declaration, the same one build_region publishes, held equal by a semantic test,
          // because both are parsed by one BuildSpecParser and the model must not be told two things about it.
          //
          // "region" here is a place that EXISTS (Brief CM): the plugin still mints the address of anything new, and a name that is not on the canvas
          // is refused with the ones that are. No "reconnect": a fresh placement has nothing to cut.
          ("place", Sch.Sub("For kind=build WITHOUT A MAP ONLY - leave \"regions\" out. The " +
            "components to put down, at most " + SpotPlacement.MaxComponents + " and no more than " + ScriptsAsk.MaxScripts + " of them scripts with source, and how they wire. NAME EVERY ONE: the user approves exactly what you " +
            "list and nothing else, and Dwight picks a clear place near what it wires to and what it is called. To ADD to a region or group " +
            "already on the canvas without rebuilding it, name it in \"region\" here. More than " + SpotPlacement.MaxComponents + " is " +
            "work that wants a map - ask for kind=scaffold instead.", Sch.Props(
            ("components", BuildSpecSchema.Components()),
            ("wires", BuildSpecSchema.Wires()),
            ("region", Sch.Str("The name or address of a region or group ALREADY on the canvas that these belong in: they land inside it, it grows to hold them, and what it " +
              "already holds stays. A name that is not there is refused with the ones that are. Leave it out for a new place of its own.")),
            ("separate", Sch.Bool("Only when the user's message names a region on the canvas and they want this apart from it. The card then says it is not in that region."))),
            "components")),
          // #907: this used to say "For kind=set_value ONLY", which told the model the field belongs to a
          // kind other than the one it also demands it for - so a model following the contract exactly, for
          // edit_script, never sent it and was refused for obeying the contract. Named for BOTH now.
          ("target", Sch.Int("For kind=set_value or kind=edit_script, and REQUIRED for both: the id of the " +
            "EXISTING component to change - a Number Slider or Panel for set_value, a C# or Python script " +
            "component for edit_script - from a previous read.")),
          // Phase-9 U20. "claim" is REQUIRED for this kind and has no default: it decides which PROOF runs,
          // so a missing or unrecognised value is refused by name rather than resolved to the weaker one.
          // The quotes below are ordinary characters now; as a string they needed \\" and that is the
          // shape that broke.
          ("claim", Sch.Enum("For kind=edit_script ONLY, and REQUIRED there. What your edit does: " +
            "\"preserves\" = the component still produces the same outputs (Dwight runs your version beside " +
            "the original on the same data and compares them, and will NOT offer it to the user if they differ), " +
            "\"changes\" = it deliberately produces something different (Dwight checks the definition still " +
            "solves and the user reads the diff). Declare what is true; Dwight verifies the one you declare.",
            "preserves", "changes")),
          ("source", Sch.Str("For kind=edit_script ONLY: the COMPLETE new source " +
            "for the component - not a fragment and not a patch. The user is shown the whole thing before it runs.")),
          ("add_inputs", BuildSpecSchema.ScriptInputs(
            "For kind=edit_script ONLY. NEW inputs to append. You cannot remove, " +
            "reorder or rename the ports that already exist - they carry the user's wires. Dwight computes each " +
            "port's type; the name, and its access where it is not item, are the part only you can supply.")),
          ("set_access", BuildSpecSchema.ScriptInputs(
            "For kind=edit_script ONLY. EXISTING inputs whose access should change, by the names a read gives. Send the complete source with it, unchanged if only the access changes. " +
            "This changes what the component produces, so declare \"changes\".")),
          ("add_outputs", Sch.Arr(Sch.Str(),
            "For kind=edit_script ONLY. Names of NEW outputs to append.")),
          // Phase-9 U6 + U21. The published descriptor IS the contract (#316), so the shape the parser reads is the
          // shape written here: each row an op plus the ends it needs. Ids only - the plugin resolves every one against
          // the live canvas at the ask and again at the click, and refuses the whole card, naming the failing rows,
          // if any row does not resolve to exactly one existing target.
          ("ops", Sch.Arr(Sch.Sub("One change. wire/unwire need \"from\" and \"to\"; delete needs \"id\".", Sch.Props(
            ("op", Sch.Enum("wire = add a wire (appended alongside what the input already has); unwire = remove one " +
              "existing wire; delete = delete an existing component and every wire on it.", "wire", "unwire", "delete")),
            ("from", Sch.Sub("wire/unwire: the OUTPUT end - an output of an existing component.", Sch.Props(
              ("id", Sch.Int("The component's id, from a previous read.")),
              ("param", Sch.Str("The output's name, when the component has more than one output; a parameter has none to name."))),
              "id")),
            ("to", Sch.Sub("wire/unwire: the INPUT end - an input of an existing component.", Sch.Props(
              ("id", Sch.Int("The component's id, from a previous read.")),
              ("param", Sch.Str("The input's name, when the component has more than one input; a parameter has none to name."))),
              "id")),
            ("id", Sch.Int("delete: the id of the existing component to delete."))),
            "op"),
            "For kind=edit ONLY, and REQUIRED there: the changes to put on ONE card, at most 40. The user ticks the " +
            "rows they accept and only those are applied. Every row must name an existing component; a row that " +
            "does not refuses the whole card, naming it. To replace a wire, send an unwire row and a wire row.")),
          ("state", Sch.Sub("For kind=set_value ONLY: the state to set. " +
            "Number Slider: min, max, value together and optional rounding (float/integer/even/odd); the " +
            "value must lie in the domain and on the rounding's grid. Panel: text.", Sch.Props(
            ("min", Sch.Num()),
            ("max", Sch.Num()),
            ("value", Sch.Num()),
            ("rounding", Sch.Enum(null, "float", "integer", "even", "odd")),
            ("text", Sch.Str()))))),
          "kind", "why"),
        Description =
          "Ask the user to approve a change you cannot make yourself. You never get to change the canvas " +
          "directly - this puts the question to them, and they decide. " +
          "THIS ENDS YOUR TURN. The card is shown to the user and their answer arrives as a NEW message " +
          "later, never in this reply. Calling it again cannot produce an answer and only asks twice. " +
          "IF THE REQUEST IS REFUSED, no card is shown and your turn does NOT end: read the refusal and " +
          "do what it says. A second refused request ends the turn. " +
          "EXPLAIN FIRST, THEN CALL THIS LAST, IN ONE TURN. Write your findings and what you propose as " +
          "ordinary text BEFORE calling this - anything you plan to say afterwards is never sent, because " +
          "the turn ends here, and the user would see a request with no reasoning behind it. " +
          "AND DO NOT ASK PERMISSION TO ASK: never write \"shall I request this?\" and wait. THE CARD IS " +
          "THE ASK. Explain, call this once, stop. " +
          "Use \"solve\" ONLY when you need Grasshopper to recompute so you can READ a fresh result. It " +
          "changes nothing, so it can never be a way to apply a change you want made. " +
          "IF THE CHANGE YOU NEED IS NOT ONE OF THESE KINDS, DO NOT PICK THE NEAREST ONE. Say plainly, " +
          "in your reply, that you cannot make that change, and NAME the capability that is missing. " +
          // PRESENT TENSE ON PURPOSE (lane B's copy-truth sweep). This said "what Dwight cannot do YET", which
          // coached the model to promise the user a future of the product in its own words - routing around the
          // rule that no user-visible text promises one. What Dwight cannot do is a fact; when it will is not.
          "Being told plainly what Dwight cannot do is useful to the user; a card that does something " +
          "else is not.",
      },
    };

    /// <summary>The kind scopes as one line, for the published schema. Same source as the prompt's
    /// sentence, so the two documents cannot disagree about what a verb does.</summary>
    private static string SchemaKindText() =>
      string.Join(" ", Kinds.Select(k => k + " = " + Described(k)));

    /// <summary>
    /// One JSON string literal, quotes included, properly escaped - for text that is composed elsewhere and
    /// then interpolated into this hand-built schema.
    ///
    /// It exists because the schema is assembled by string concatenation, which is fine for the literals
    /// written here and NOT fine for anything computed: a quotation mark in composed text closes the string
    /// it was interpolated into and the whole published schema stops being JSON. Escaping belongs to the
    /// destination (#335), and the destination of this text is a JSON document - while the SAME text also
    /// goes to the master prompt, where escaping it would be wrong. So the escape is here rather than in
    /// the table.
    /// </summary>
    private static string JsonString(string text) =>
      System.Text.Json.JsonSerializer.Serialize(text ?? "");

    /// <summary>One kind, both halves, in one sentence - the single place the prompt and the schema get their
    /// wording, so the two documents cannot describe a verb differently.</summary>
    private static string Described(string kind) =>
      KindScope.TryGetValue(kind, out var c)
        ? "CAN: " + c.Can + " CANNOT: " + c.Cannot
        : "available.";

    /// <summary>One kind's description, by name - string in, string out, so the external deploy probe can
    /// read it without touching an internal type (#374, #568). Null for a kind that does not exist.</summary>
    public static string ScopeOf(string kind) => KindScope.ContainsKey(kind ?? "") ? Described(kind) : null;

    /// <summary>
    /// The tool that CARRIES OUT an approved kind whose flow is a model turn - the call that means the
    /// approval ran. Null for a kind whose flow is not a model turn (reorganise, simplify open a verb).
    /// </summary>
    public static string ActingToolFor(string kind)
    {
      switch ((kind ?? "").Trim().ToLowerInvariant())
      {
        case "build": return AgentToolCatalog.BuildRegion;
        case "solve": return AgentToolCatalog.SolveAndCheck;
        case "scaffold": return AgentToolCatalog.ProposeScaffold;
        case "run_script": return FullControl.ToolName;
        default: return null;
      }
    }

    /// <summary>
    /// The kind an APPROVED turn is carrying out, from the capability the click granted - null for a
    /// read-only turn, which holds no approval. The inverse of the consent route, kept beside
    /// <see cref="ActingToolFor"/> so the pair cannot disagree.
    /// </summary>
    public static string KindApprovedBy(TurnCapability capability)
    {
      switch (capability)
      {
        case TurnCapability.Build: return "build";
        case TurnCapability.ReadAndSolve: return "solve";
        case TurnCapability.Scaffold: return "scaffold";
        case TurnCapability.RunScript: return "run_script";
        default: return null;
      }
    }

    /// <summary>Is this the consent request - the one tool whose result can end a turn?</summary>
    public static bool IsRequest(string name) =>
      string.Equals(name, RequestMutation, StringComparison.OrdinalIgnoreCase);

    /// <summary>What a raised card answers the model. Here rather than in the panel so a dispatcher that is not GH-bound sends the same bytes (Brief BY).</summary>
    public static string CardShownResult(string kind) =>
      ToolResult.Success(new JsonObject
      {
        ["cardShown"] = true,
        ["kind"] = kind,
        ["approved"] = false,
        ["turnEnds"] = true,
        ["whereItIs"] = "The card is in this chat, directly below your message. It is not a Grasshopper " +
                        "dialog and there is no window to find - do not send the user looking for one.",
        ["howTheAnswerArrives"] = "As a NEW message in this conversation if they approve. It cannot arrive " +
                                  "in this reply.",
        ["doNotCallAgain"] = "Calling request_mutation again cannot produce an answer; it only asks the " +
                             "same question twice. Stop here.",
      }).ToJson();

    /// <summary>
    /// Did this call actually put a card in front of the user? That, and only that, ENDS the turn (#256).
    ///
    /// A tool that opens a HUMAN gate cannot return the answer - the human has not answered yet - so the
    /// model is left with no state at all and cannot tell "card displayed, waiting" from "nothing
    /// happened". In the field it filled that silence with fiction: four billed calls to this tool, then
    /// it sent the user hunting a Grasshopper popup that has never existed. So a shown card ends the turn.
    ///
    /// IT IS A FACT ABOUT THE RESULT, NOT THE NAME (#609 link 3, #611a). This used to be decided by the
    /// tool's name, so a REFUSED request ended the turn exactly as a shown card did - and the refusal,
    /// written to tell the model its way forward, was never read by the model it addressed. A refusal
    /// opened no gate, so there is nothing to wait for and the model reads it inside the turn.
    ///
    /// Anything other than a success carrying <c>cardShown: true</c> is not a shown card - an envelope
    /// that cannot be read included, because the host builds the card before it builds that envelope.
    /// </summary>
    public static bool ShowedCard(string name, string envelope)
    {
      if (!IsRequest(name) || string.IsNullOrWhiteSpace(envelope)) return false;
      try
      {
        var root = JsonNode.Parse(envelope) as JsonObject;
        return root?["ok"]?.GetValue<bool>() == true
               && root["result"]?["cardShown"]?.GetValue<bool>() == true;
      }
      catch { return false; }
    }

    /// <summary>The detail marker on the loop-breaker's refusal, so the user-facing note can say which
    /// refusal ended a turn without reading a sentence written for the model (#615).</summary>
    public const string RepeatUnchangedReason = "repeat_unchanged";

    /// <summary>Brief CU: the detail marker on a request for a kind Dwight does not have, for the same reason.</summary>
    public const string UnknownKindReason = "unknown_kind";

    /// <summary>One host tool's published schema and description, BY NAME - string in, string out, for
    /// the same reason <see cref="AgentToolCatalog.SchemaOf"/> exists: the external probe cannot read a
    /// property off an internal ToolDescriptor, and trying once produced a false red (#374).</summary>
    public static string SchemaOf(string name) =>
      All().FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))?.InputSchemaJson;

    public static string DescriptionOf(string name) =>
      All().FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))?.Description;

    public static bool Knows(string name)
    {
      if (string.IsNullOrWhiteSpace(name)) return false;
      return All().Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Validate a <c>request_mutation</c> call. Either it names a kind this build can actually honour, or
    /// it is refused with the real list - the published enum and the parser held to the same contract,
    /// which is the class the WP-2 conformance test closed (#219) arriving in a new tool on its first day.
    /// </summary>
    public static bool TryParseRequest(JsonObject args, out string kind, out string why, out ToolResult error) =>
      TryParseRequest(args, out kind, out why, out _, out error);

    /// <summary>
    /// D1: the same parse, plus the optional HAND-OFF SEED - the components the agent was actually talking
    /// about and the option it thinks fits.
    ///
    /// It is parsed here and validated NOWHERE here: <see cref="SeedPlan"/> owns the boundary, and it does
    /// so after the route has already been decided from <c>kind</c> alone. Nothing on this path can widen
    /// what the user is offered (#287) - a seed that is absent, empty, hostile or nonsense all produce the
    /// same set of actions, and differ only in what is pre-filled.
    /// </summary>
    public static bool TryParseRequest(JsonObject args, out string kind, out string why,
                                       out HandoffSeed seed, out ToolResult error)
      => TryParseRequest(args, null, null, out kind, out why, out seed, out error);

    /// <summary>
    /// The same parse, plus THE LOOP-BREAKER (#378b).
    ///
    /// THE FIELD FAILURE. The model needed a change no <see cref="Kinds"/> entry offers, picked the
    /// nearest one - <c>solve</c> - and used it as a proxy for making that change. The user approved, the
    /// definition re-solved, nothing changed, and the model asked again. Nothing in the loop could stop
    /// it, because every individual call was well-formed and every card was honestly rendered.
    ///
    /// IT IS BROKEN DETERMINISTICALLY, NOT BY PROMPT (#146). "Has an approved request of this kind
    /// already run without changing the canvas" is a fact the plugin holds, so the plugin decides it -
    /// never a billed round-trip, and never a plea in a description that a model may or may not honour.
    /// The prompt half exists too, one screen up, but it is the belt and this is the braces.
    ///
    /// THE REFUSAL CARRIES THE WAY FORWARD (#383, and ADR-092's shape). A bare "no" leaves the model
    /// exactly where it was, which is what produced the loop; being told to say what it cannot do, and to
    /// name the missing capability, gives it a move that is actually useful to the user.
    ///
    /// THE STAMP IS SUPPLIED, NEVER COMPUTED HERE (ADR-091): this file is GH-free, so the caller passes
    /// an opaque canvas stamp and the rule compares two strings. That is also why the un-refusal is
    /// testable - a stamp that differs is a canvas that changed, whoever changed it. A USER edit between
    /// two same-kind requests therefore clears the breaker, which matters: the model is being stopped
    /// from repeating itself, not from ever asking twice.
    /// </summary>
    public static bool TryParseRequest(JsonObject args, MutationRequestLog log, string canvasStamp,
                                       out string kind, out string why,
                                       out HandoffSeed seed, out ToolResult error)
      => TryParseRequest(args, log, canvasStamp, null, out kind, out why, out seed, out error);

    /// <summary>
    /// The same parse, inside a turn that is ALREADY CARRYING OUT an approval of
    /// <paramref name="approvedThisTurn"/> (#611d).
    ///
    /// The build that never ran asked for its own approval again (#609 link 1). A request of the kind this
    /// turn holds is refused before anything else can answer it - ahead of the breaker, whose sentence
    /// ("you cannot make this change") would be the wrong way forward here - with the one thing that is:
    /// call the tool. #611a is what makes this refusal readable at all: before it, a refusal ended the turn.
    ///
    /// Only the SAME kind. A build turn that finds it needs a Reorganise is asking a different question,
    /// and it is parsed exactly as it would be anywhere else.
    /// </summary>
    public static bool TryParseRequest(JsonObject args, MutationRequestLog log, string canvasStamp,
                                       string approvedThisTurn,
                                       out string kind, out string why,
                                       out HandoffSeed seed, out ToolResult error)
      => TryParseRequest(args, log, canvasStamp, approvedThisTurn, null, out kind, out why, out seed, out error);

    /// <summary>
    /// The same parse, knowing WHICH REGIONS this build turn was approved for (#617).
    ///
    /// "Already approved - call build_region" is true only for regions the turn holds. Inside a turn
    /// approved for A1, a request for A2 is not already approved, and build_region would refuse it at the
    /// ledger - so that sentence would send the model into a second refusal, and the bound's note would then
    /// tell the user it asked for "a build this turn was already carrying out", which is false too. A request
    /// naming any region outside the approved set gets the refusal that is true for it: build what you hold,
    /// and the rest needs the user's own approval.
    ///
    /// Both sets are named from the plugin's own grammar, never the model's raw strings: the approved list was
    /// validated against the canvas and re-resolved at the click, and a requested region is named only if it
    /// is an address (<see cref="ScaffoldAddress.IsAddress"/>), which cannot carry anything but letters and
    /// digits.
    /// </summary>
    public static bool TryParseRequest(JsonObject args, MutationRequestLog log, string canvasStamp,
                                       string approvedThisTurn, IReadOnlyList<string> approvedRegionsThisTurn,
                                       out string kind, out string why,
                                       out HandoffSeed seed, out ToolResult error)
      => TryParseRequest(args, log, canvasStamp, approvedThisTurn, approvedRegionsThisTurn, null,
                         out kind, out why, out seed, out error);

    /// <summary>
    /// The same parse, knowing which approved regions have SPENT THEIR ATTEMPTS in this turn (#626).
    ///
    /// "Already approved - call build_region" is true only while build_region would accept the call. The model
    /// used A1's attempts, found its own bug, asked to rebuild A1, and was pointed twice at a tool that would
    /// refuse on attempts - the third refusal in this arc pointed at a tool that would refuse (#613, #617). So:
    ///   - every requested region exhausted: the request is not refused at all - it mints a card, because a new
    ///     approval is a new turn with a fresh budget, and a click bounds it;
    ///   - some exhausted: the pointer is kept, naming only the regions it is true for;
    ///   - none named, and every region the turn holds exhausted (#635): told to name the regions to rebuild.
    /// <paramref name="exhaustedThisTurn"/> null means the caller cannot tell, and nothing changes.
    /// </summary>
    public static bool TryParseRequest(JsonObject args, MutationRequestLog log, string canvasStamp,
                                       string approvedThisTurn, IReadOnlyList<string> approvedRegionsThisTurn,
                                       Func<string, bool> exhaustedThisTurn,
                                       out string kind, out string why,
                                       out HandoffSeed seed, out ToolResult error)
    {
      kind = null; why = null; error = null;
      seed = new HandoffSeed();

      try
      {
        if (args != null && args["components"] is JsonArray arr)
          foreach (var n in arr)
          {
            try { seed.ComponentIds.Add(n.GetValue<int>()); } catch { }
          }
        seed.Mode = Str(args, "mode");

        // Stage C: the ADDRESSES a build asks to be approved for. Parsed here and validated NOWHERE
        // here - SeedPlan owns the boundary, and it does so after the route is already decided from the
        // kind alone, so nothing on this path can widen what the user is offered (#287).
        if (args != null && args["regions"] is JsonArray regions)
          foreach (var n in regions)
          {
            try { seed.Regions.Add(n.GetValue<string>()); } catch { }
          }

        // Phase-9 U4: set_value's target and state, carried leniently - a malformed target is NULL, never
        // another integer - and validated at the ask against the live canvas (SetValueAsk), where the
        // refusal can name the real type and the real state.
        if (args != null)
        {
          try { seed.Target = args["target"]?.GetValue<int>(); } catch { seed.Target = null; }
          seed.State = args["state"] as JsonObject;

          // U18: the placement, carried leniently like `state` - anything that is not an object is null, never
          // coerced - and validated at the ask by SpotAsk, through the build's own resolvers. It is the raw
          // NOMINATION only until then: the ask overwrites it with the plugin's resolved spec, exactly as it
          // overwrites Regions with the validated set, so the record holds what will be built (#862).
          seed.Place = args["place"] as JsonObject;

          // U65: the scripts a region build card shows, carried leniently like `place` and resolved at the ask by ScriptsAsk.
          seed.Scripts = args["scripts"] as JsonArray;
        }

        // Phase-9 U6 + U21: edit's changes, carried leniently like `place` - anything that is not an array is null, never
        // coerced - and validated row by row at the ask (EditOpsAsk) against the live canvas, which then OVERWRITES this with
        // the plugin's resolved record.
        if (args != null) seed.Ops = args["ops"] as JsonArray;

        // #907's second half: edit_script's OWN fields. Published in the schema, read by NOTHING here
        // until now - SeedPlan.From (HandoffSeed.cs) already consumed seed.Claim/Source/AddInputs/
        // AddOutputs assuming an upstream parse had populated them, and none ever did, so every edit_script
        // request was refused at EditScriptAsk's claim check regardless of what the model sent. Carried the
        // same leniently-parsed way as target/state: absent or malformed is simply absent, never guessed,
        // and EditScriptAsk names what is missing.
        if (args != null)
        {
          seed.Claim = Str(args, "claim");
          seed.Source = Str(args, "source");
          if (args["add_inputs"] is JsonArray addIn)
            foreach (var n in addIn)
            {
              try
              {
                if (n is JsonObject declared)
                {
                  var name = declared["name"]?.GetValue<string>();
                  seed.AddInputs.Add(name);
                  if (declared["access"] != null && !string.IsNullOrWhiteSpace(name)) seed.InputAccess[name.Trim()] = ReadWord(declared["access"]);
                }
                else seed.AddInputs.Add(n.GetValue<string>());
              }
              catch { }
            }
          // Brief CP: an access change on an input that exists. Carried as sent, and EditScriptAsk refuses a word it does not know by name.
          if (args["set_access"] is JsonArray setAccess)
            foreach (var n in setAccess.OfType<JsonObject>())
            {
              try
              {
                var name = n["name"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(name)) seed.InputAccess[name.Trim()] = ReadWord(n["access"]);
              }
              catch { }
            }
          if (args["add_outputs"] is JsonArray addOut)
            foreach (var n in addOut)
            {
              try { seed.AddOutputs.Add(n.GetValue<string>()); } catch { }
            }
        }
      }
      catch { seed = new HandoffSeed(); }

      var rawKind = Str(args, "kind");
      var rawWhy = Str(args, "why");

      if (string.IsNullOrWhiteSpace(rawKind))
      {
        error = Refuse("Requires a \"kind\" - which flow to offer.", rawKind);
        return false;
      }

      var match = Kinds.FirstOrDefault(k => string.Equals(k, rawKind, StringComparison.OrdinalIgnoreCase));
      if (match == null)
      {
        error = Refuse("\"" + rawKind + "\" is not something Dwight can offer.", rawKind, UnknownKindReason);
        return false;
      }

      // #611d: the approval this turn is carrying out cannot be asked for again - point at the tool instead.
      var acting = ActingToolFor(match);
      if (acting != null && string.Equals(match, approvedThisTurn, StringComparison.OrdinalIgnoreCase))
      {
        var outside = OutsideApproval(seed.Regions, approvedRegionsThisTurn, out var named, out var held);
        if (outside)
        {
          var one = held.Count == 1;
          var rest = named.Count == 0
            ? "Any other region needs its own approval"
            : string.Join(", ", named) + (named.Count == 1 ? " needs its own approval" : " need their own approval");
          error = ToolResult.Fail(ToolErrors.RegionNotApproved,
            "This turn is approved to " + match + " " + string.Join(", ", held) + " only. Build " +
            (one ? "it" : "them") + " now with " + acting + ". " + rest + ": tell the user, and they can ask " +
            "for " + (named.Count > 1 ? "them" : "it") + " after this build.",
            new JsonObject
            {
              ["kind"] = match,
              ["tool"] = acting,
              ["approved"] = new JsonArray(held.Select(r => (JsonNode)r).ToArray()),
              ["outside"] = new JsonArray(named.Select(r => (JsonNode)r).ToArray()),
            });
          return false;
        }

        // #626: the pointer is true only while build_region would accept the call. Everything requested is
        // exhausted -> no refusal: fall through, and the request is put to the user as a card.
        var spent = Exhaustion(seed.Regions, approvedRegionsThisTurn, exhaustedThisTurn, out var live, out var dead);
        if (spent == ExhaustionState.NoneNamedAllSpent)
        {
          error = ToolResult.Fail(ToolErrors.InvalidArgs,
            "Every region this turn is approved for has used all its attempts, so name the regions to rebuild in " +
            "\"regions\" - the user is then asked to approve a fresh attempt at exactly those.",
            new JsonObject { ["kind"] = match, ["exhausted"] = new JsonArray(dead.Select(r => (JsonNode)r).ToArray()) });
          return false;
        }

        if (spent == ExhaustionState.Mixed)
        {
          var oneLive = live.Count == 1;
          var oneDead = dead.Count == 1;
          error = ToolResult.Fail(ToolErrors.AlreadyApproved,
            string.Join(", ", live) + (oneLive ? " is" : " are") + " already approved and still " +
            (oneLive ? "has" : "have") + " attempts in this turn: call " + acting + " for " + (oneLive ? "it" : "them") +
            " now. " + string.Join(", ", dead) + (oneDead ? " has" : " have") + " used all " +
            (oneDead ? "its" : "their") + " attempts in this turn - after " + string.Join(", ", live) + ", request " +
            string.Join(", ", dead) + " on " + (oneDead ? "its" : "their") + " own and the user gets a card for a " +
            "fresh attempt.",
            new JsonObject
            {
              ["kind"] = match,
              ["tool"] = acting,
              ["live"] = new JsonArray(live.Select(r => (JsonNode)r).ToArray()),
              ["exhausted"] = new JsonArray(dead.Select(r => (JsonNode)r).ToArray()),
            });
          return false;
        }

        if (spent != ExhaustionState.AllRequestedSpent)
        {
          error = ToolResult.Fail(ToolErrors.AlreadyApproved,
            "That " + match + " is already approved - this turn is carrying it out. Call " + acting +
            " now; do not ask for it again.",
            new JsonObject { ["kind"] = match, ["tool"] = acting });
          return false;
        }
      }

      if (string.IsNullOrWhiteSpace(rawWhy))
      {
        error = ToolResult.Fail(ToolErrors.InvalidArgs,
          "Requires a \"why\" - the user is being asked to approve this, so they need to read what it is for.");
        return false;
      }

      // The breaker runs LAST, so a malformed call is still corrected in our own words first (#297).
      if (log != null && log.RepeatWithNothingChanged(match, canvasStamp))
      {
        error = ToolResult.Fail(ToolErrors.PreconditionRefused,
          "You already had a \"" + match + "\" approved in this conversation, and nothing on the canvas " +
          "has changed since it ran. Asking again cannot produce a different result. Say plainly that " +
          "you cannot make this change yourself, and name the capability that is missing - that is worth " +
          "more to the user than a second card.",
          new JsonObject { ["kind"] = match, ["reason"] = RepeatUnchangedReason });
        return false;
      }

      kind = match;
      why = rawWhy.Trim();
      seed.Why = why;
      return true;
    }

    /// <summary>
    /// Does this request name any region outside what the turn holds? <paramref name="held"/> is the
    /// approved set as the plugin validated it; <paramref name="named"/> is each requested region outside it
    /// that is an ADDRESS, so it can be named back. A requested entry that is not an address is still outside,
    /// just never echoed. With no approved set known (not a build turn) nothing is outside.
    /// </summary>
    internal static bool OutsideApproval(IReadOnlyList<string> requested, IReadOnlyList<string> approved,
                                        out List<string> named, out List<string> held)
    {
      named = new List<string>();
      held = (approved ?? new string[0]).Where(ScaffoldAddress.IsRegionAddress).Distinct(StringComparer.Ordinal).ToList();
      if (approved == null || held.Count == 0) return false;

      var outside = false;
      foreach (var r in requested ?? new string[0])
      {
        if (r != null && held.Contains(r, StringComparer.Ordinal)) continue;
        outside = true;
        if (ScaffoldAddress.IsRegionAddress(r) && !named.Contains(r, StringComparer.Ordinal)) named.Add(r);
      }
      return outside;
    }

    internal enum ExhaustionState { Unknown, NoneSpent, Mixed, AllRequestedSpent, NoneNamedAllSpent }

    /// <summary>
    /// Which requested regions still have attempts in this turn (#626). Called only for a request inside the
    /// approval (the outside-the-approval refusal has already answered anything else), so every region named
    /// back is one the plugin validated. With no way to tell (<paramref name="exhausted"/> null, or no approved
    /// set) the answer is <see cref="ExhaustionState.Unknown"/>, and the pointer stands exactly as before.
    /// </summary>
    internal static ExhaustionState Exhaustion(IReadOnlyList<string> requested, IReadOnlyList<string> approved,
                                               Func<string, bool> exhausted, out List<string> live, out List<string> dead)
    {
      live = new List<string>();
      dead = new List<string>();
      var held = (approved ?? new string[0]).Where(ScaffoldAddress.IsRegionAddress).Distinct(StringComparer.Ordinal).ToList();
      if (exhausted == null || held.Count == 0) return ExhaustionState.Unknown;

      var asked = (requested ?? new string[0]).Where(r => held.Contains(r, StringComparer.Ordinal))
        .Distinct(StringComparer.Ordinal).ToList();

      if (asked.Count == 0)
      {
        // #635: nothing named. The pointer is false only if it is false for every region the turn holds.
        if (held.All(r => Safe(exhausted, r))) { dead.AddRange(held); return ExhaustionState.NoneNamedAllSpent; }
        return ExhaustionState.NoneSpent;
      }

      foreach (var r in asked) (Safe(exhausted, r) ? dead : live).Add(r);
      if (dead.Count == 0) return ExhaustionState.NoneSpent;
      return live.Count == 0 ? ExhaustionState.AllRequestedSpent : ExhaustionState.Mixed;
    }

    /// <summary>A check that cannot answer says "not exhausted": the pointer it keeps is today's behaviour, and
    /// build_region's own cap still refuses a real over-spend (#208 - fail open on knowledge).</summary>
    private static bool Safe(Func<string, bool> exhausted, string region)
    {
      try { return exhausted(region); } catch { return false; }
    }

    private static ToolResult Refuse(string message, string asked, string reason = null)
    {
      var detail = new JsonObject
      {
        ["asked"] = asked ?? "",
        ["available"] = new JsonArray(Kinds.Select(k => (JsonNode)k).ToArray()),
      };
      if (reason != null) detail["reason"] = reason;
      return ToolResult.Fail(ToolErrors.InvalidArgs, message, detail);
    }

    /// <summary>A word as the model sent it, or its JSON when it is not a string, so the refusal can quote what came.</summary>
    private static string ReadWord(JsonNode node)
    {
      try { return node?.GetValue<string>() ?? ""; }
      catch { return node?.ToJsonString() ?? ""; }
    }

    private static string Str(JsonObject args, string field)
    {
      var node = args?[field];
      if (node == null) return null;
      try { return node.GetValue<string>(); } catch { return null; }
    }
  }

  /// <summary>
  /// What one kind CAN and CANNOT do. Both halves are constructor arguments, so there is no way to build an
  /// entry that states only one of them - and a test holds them non-empty (#578).
  /// </summary>
  internal sealed class KindCapability
  {
    public KindCapability(string can, string cannot)
    {
      Can = can;
      Cannot = cannot;
    }

    public string Can { get; }
    public string Cannot { get; }
  }

  /// <summary>When an approval of a kind ARMS the loop-breaker - or whether it ever does (#613).</summary>
  internal enum BreakerArming
  {
    /// <summary>The approved flow is deterministic and runs from the click itself (Reorganise, Simplify),
    /// so the click is the flow having run.</summary>
    AtRedemption,

    /// <summary>The approved flow is a MODEL turn that may never call its tool. Armed only when the kind's
    /// acting tool is dispatched - "approved" and "ran" are different facts (#609 link 2).</summary>
    AtDispatch,

    /// <summary>Never armed. A landed attempt moves the canvas stamp, so the breaker could only ever fire
    /// after a REVERT - and its sentence ("you cannot make this change") would then be false.</summary>
    Never,
  }

  /// <summary>
  /// WHAT HAS ALREADY BEEN APPROVED IN THIS CONVERSATION, AND WHAT THE CANVAS LOOKED LIKE WHEN IT WAS
  /// (#378b's substrate).
  ///
  /// The record is written when a card is REDEEMED, not when it is minted: a card the user declined, or
  /// never answered, must not stop the model asking again - they may simply have meant "not now". Only an
  /// approval that RAN and left the canvas exactly as it found it is evidence that asking again is futile.
  ///
  /// "RAN" IS NOT "WAS CLICKED" (#609 link 2, ruled at #613). This used to arm on the click for every kind,
  /// on the reasoning that the next request arrives in a later turn, after the flow has finished. That
  /// holds for Reorganise and Simplify, whose flows run from the click. It does not hold for a flow that is
  /// a MODEL turn: an approved build that never called build_region left the stamp unchanged, and the
  /// breaker refused the build in that turn and every later one. So each kind declares when it arms:
  ///   - reorganise, simplify: at redemption, as before;
  ///   - solve: at dispatch of solve_and_check. Solve changes nothing by definition, so it is the only
  ///     model-turn kind where "approved, ran, nothing changed" proves a repeat is futile - #378b's loop;
  ///   - build, scaffold: never. A landed attempt moves the stamp, so arming could only ever refuse a retry
  ///     after a revert, with a sentence that is false: Dwight can build, that attempt failed. A retry
  ///     carries a new spec, the attempt cap bounds it within a turn, and every new card is a user click.
  ///
  /// The stamp recorded is still the one taken AT APPROVAL, before the approved flow does its work.
  ///
  /// GH-free and string-keyed by design (ADR-091). It holds no document, no guid and no geometry; the
  /// caller decides what a "stamp" means and this only ever asks whether two of them are equal. Locked,
  /// because approval arrives on the UI thread and dispatch on the agent's worker.
  /// </summary>
  internal sealed class MutationRequestLog
  {
    private readonly object _lock = new object();

    private readonly Dictionary<string, string> _approved =
      new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Approved AT_DISPATCH kinds whose acting tool has not been called yet, with their stamp.</summary>
    private readonly Dictionary<string, string> _pending =
      new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>When each kind arms the breaker. A kind not listed never arms it - an unlisted kind is a
    /// kind nobody decided about, and a breaker that fires on no decision refuses on no evidence (#208).</summary>
    public static BreakerArming ArmingOf(string kind)
    {
      switch ((kind ?? "").Trim().ToLowerInvariant())
      {
        case "reorganise":
        case "simplify":
          return BreakerArming.AtRedemption;
        case "solve":
          return BreakerArming.AtDispatch;
        // #679: set_value runs from the click, yet it never arms. The breaker's sentence must be true in every state
        // that arms it (#613), and two states leave the stamp unchanged after a set_value the user approved: a REVERT
        // (a corrected retry carries a new state), and an applied change the stamp cannot see - the stamp read a
        // slider's VALUE only until #680. Armed, the next set_value for any slider would be told "nothing changed...
        // you cannot make this change": false. Every set_value card is a click, so nothing loops unbounded.
        case "set_value":
          return BreakerArming.Never;

        // Phase-9 U20: edit_script never arms either, and ONE of set_value's two reasons carries it.
        //
        // THE REASON THAT IS NOW DEAD, named as dead so nobody re-derives it: "an applied change the stamp
        // cannot see" does NOT apply here. #779 put a script's SOURCE DIGEST into the behaviour fingerprint,
        // and CanvasStamp composes that fingerprint - so an applied edit DOES move the stamp. Anyone
        // reasoning from set_value's comment would reach the wrong conclusion for this kind.
        //
        // THE REASON THAT STANDS, and it is sufficient alone: a REVERTED edit leaves the stamp unchanged.
        // Armed, the next ask would be told "nothing changed ... you cannot make this change" - FALSE, since
        // a corrected retry is exactly what should follow a revert, and the refusal must be true in every
        // state that arms it (#613). Every edit_script card is a click, so nothing loops unbounded without
        // the user; the per-target attempt budget is what bounds the retries.
        case "edit_script":
          return BreakerArming.Never;

        // Phase-9 U6 + U21: edit never arms, for the reason edit_script's REVERT case gives and one more. A reverted edit
        // leaves the stamp unchanged, so an armed breaker would tell the next ask "nothing changed ... you cannot make
        // this change" - false, since a corrected retry is exactly what should follow a revert (#613). And a card with a
        // user's ticks is a click each time, so nothing loops unbounded without them.
        case "edit":
          return BreakerArming.Never;

        default:
          return BreakerArming.Never;
      }
    }

    /// <summary>Record that a card of this kind was approved, with the canvas stamp at that moment. Whether
    /// that arms anything is the kind's own declaration (<see cref="ArmingOf"/>).</summary>
    public void RecordApproved(string kind, string canvasStamp)
    {
      if (string.IsNullOrWhiteSpace(kind)) return;
      var k = kind.Trim();
      lock (_lock)
      {
        switch (ArmingOf(k))
        {
          case BreakerArming.AtRedemption:
            _approved[k] = canvasStamp;
            break;
          case BreakerArming.AtDispatch:
            _approved.Remove(k);   // a fresh approval supersedes the last run: it has not run yet
            _pending[k] = canvasStamp;
            break;
        }
      }
    }

    /// <summary>
    /// A tool was dispatched in a turn that holds an approval of <paramref name="approvedKind"/>. If it is
    /// that kind's own acting tool, the approval has now RUN, and the breaker is armed with the stamp taken
    /// at approval. Any other tool arms nothing.
    /// </summary>
    public void ToolDispatched(string approvedKind, string toolName)
    {
      if (string.IsNullOrWhiteSpace(approvedKind) || string.IsNullOrWhiteSpace(toolName)) return;
      var k = approvedKind.Trim();
      if (ArmingOf(k) != BreakerArming.AtDispatch) return;
      if (!string.Equals(AgentHostTools.ActingToolFor(k), toolName, StringComparison.OrdinalIgnoreCase)) return;

      lock (_lock)
      {
        if (!_pending.TryGetValue(k, out var stamp)) return;
        _pending.Remove(k);
        _approved[k] = stamp;
      }
    }

    /// <summary>
    /// Would this be a repeat of an approval that RAN and changed nothing? True only when this kind has
    /// been armed AND the canvas is byte-identical to what it was at approval.
    ///
    /// An unknown stamp on either side answers FALSE. A breaker that fires on ignorance would refuse a
    /// legitimate request on no evidence - fail open on KNOWLEDGE, never on identity (#208).
    /// </summary>
    public bool RepeatWithNothingChanged(string kind, string canvasStamp)
    {
      if (string.IsNullOrWhiteSpace(kind) || string.IsNullOrEmpty(canvasStamp)) return false;
      lock (_lock)
        return _approved.TryGetValue(kind.Trim(), out var then)
               && !string.IsNullOrEmpty(then)
               && string.Equals(then, canvasStamp, StringComparison.Ordinal);
    }
  }
}
