using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Dwight.Agent
{
  /// <summary>
  /// The advertised tool surface, as DATA (ADR-091). The descriptors are the contract the model reads -
  /// names, JSON Schemas, and which tools mutate - so they live GH-free where their shape can be
  /// asserted: schemas that parse, names that are unique, and a mutation flag that cannot silently
  /// disagree with what the dispatcher actually routes.
  ///
  /// This list IS the MCP <c>list_tools</c> response (ADR-069/098). Write tools join it as they land.
  /// </summary>
  internal static class AgentToolCatalog
  {
    public const string GetOverview = "get_overview";
    public const string GetRegion = "get_region";
    public const string GetComponent = "get_component";
    public const string GetConnections = "get_connections";
    public const string GetTreeSignature = "get_tree_signature";
    public const string Search = "search";
    public const string KbLookup = "kb_lookup";
    public const string Diagnose = "diagnose";
    public const string GetScaffoldMap = "get_scaffold_map";
    public const string SolveAndCheck = "solve_and_check";
    public const string BuildRegion = "build_region";
    public const string ProposeScaffold = "propose_scaffold";

    // Phase-9 U2 - the geometry read set (#381 R3, study §6): facts about the geometry on a named
    // output. Scalars, short strings, one bounding box, one centroid, at most ten sample points.
    public const string GetGeometrySummary = "get_geometry_summary";
    public const string CheckMesh = "check_mesh";
    public const string CheckBrep = "check_brep";
    public const string GetNakedEdges = "get_naked_edges";
    public const string FindIntersecting = "find_intersecting";
    public const string GetMassProperties = "get_mass_properties";
    public const string SpatialQuery = "spatial_query";

    // Phase-9 U1(c): a cluster's internals, by the cluster's COMPONENT id (its own tool - Q3).
    public const string GetCluster = "get_cluster";

    // Phase-9 U4: a consented edit to an EXISTING slider or panel (#377, #381 R4).
    public const string SetValue = "set_value";

    // Phase-9 U23 (lane C): where the solve time goes, from solves that already happened.
    public const string GetSolveTiming = "get_solve_timing";

    // Phase-9 U23 (lane C): the units every geometry fact is in, and the installed plugins as an inventory.
    public const string GetDocumentUnits = "get_document_units";
    public const string GetInstalledPlugins = "get_installed_plugins";

    // Phase-9 U23 (lane C): whether the definition is saved (never its name or path), and the canvas layout as fact.
    public const string GetFileState = "get_file_state";
    public const string GetCanvasLayout = "get_canvas_layout";
    public const string ShowOnCanvas = "show_on_canvas";

    /// <summary>The one target shape every geometry tool takes: {id, param?, skip?, limit?}. Built fresh
    /// per call - a shared JsonObject would be one node with several parents, and editing one tool's
    /// schema would silently edit six others.</summary>
    private static (string, JsonObject)[] GeometryTargetProps() => new[]
    {
      ("id", Sch.Int("The component whose OUTPUT holds the geometry.")),
      ("param", Sch.Str("Which output port. Needed only when the component " +
                        "has more than one output; where given it must match a real port name.")),
      ("skip", Sch.Int("Objects to skip, for paging.")),
      ("limit", Sch.Int("Objects per call, at most 25 - the report says how many more there are.")),
    };

    private static JsonObject GeometrySchema(params (string, JsonObject)[] extraProps)
    {
      var all = new List<(string, JsonObject)>(GeometryTargetProps());
      if (extraProps != null) all.AddRange(extraProps);
      return Sch.Object(Sch.Props(all.ToArray()), "id");
    }

    private const string GeometryBoundNote =
      " Returns FACTS, never geometry: no vertex lists, no control points, nothing you could rebuild the " +
      "shape from. Reads the last solution's data; it does not re-solve.";

    /// <summary>A tool that takes nothing. A method, not a constant: each descriptor needs its own node.</summary>
    private static JsonObject NoArgs() => Sch.Object(Sch.Props());

    /// <summary>
    /// One tool's published input schema, BY NAME - string in, string out.
    ///
    /// It exists for the external deploy probe (#374). ToolDescriptor is an internal type, so a probe
    /// running in IronPython cannot read a property off one: the first attempt to do so raised, scored
    /// false, and printed STALE DEPLOY over a perfectly current build - a false red on the gate's own
    /// first step. The lesson generalised: anything the probe must READ is exposed as a primitive here,
    /// so nothing internal ever has to cross that boundary.
    /// </summary>
    public static string SchemaOf(string name) =>
      All().FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))?.InputSchemaJson;

    /// <summary>One tool's published description, by name. Same reason as <see cref="SchemaOf"/>.</summary>
    public static string DescriptionOf(string name) =>
      All().FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))?.Description;

    private static JsonObject IdSchema(string what) =>
      Sch.Object(Sch.Props(("id", Sch.Int(what))), "id");

    public static IReadOnlyList<ToolDescriptor> All() => new List<ToolDescriptor>
    {
      new ToolDescriptor
      {
        // Brief EE unit 4. A read: it points, it never changes the definition or the person's selection.
        Name = ShowOnCanvas, Mutates = false, Solves = false,
        InputSchema = Sch.Object(Sch.Props(
          ("ids", Sch.Arr(Sch.Int(), "Component ids from this session's reads, at most 60.")),
          ("dim", Sch.Bool("True to wash the rest of the canvas so the named components stand out. Default false."))),
          "ids"),
        Description = "Light the named components on the canvas so the person can see what you are talking about, optionally dimming the rest. " +
                      "Use it when you explain a part of the definition. It changes nothing.",
      },

      new ToolDescriptor
      {
        Name = GetOverview, Mutates = false, InputSchema = NoArgs(),
        Description = "Counts, the definition's regions with their labels, and the flow between them. " +
                      "Start here. Component ids are stable for this session; REGION ids are valid " +
                      "only until the canvas changes. " +
                      // U29 (#918): the noun is said before the model meets it, because the risk is not a tool
                      // refusing a region id but a model handing one to get_cluster, or reading a region as a
                      // Grasshopper Cluster the user must explode.
                      "A region is a group of components Dwight cut from the dataflow for reading - it is NOT a " +
                      "Grasshopper Cluster component (get_cluster is for those, by the component's id). An unnamed " +
                      "region is labelled \"Region N\".",
      },
      new ToolDescriptor
      {
        Name = GetRegion, Mutates = false, InputSchema = IdSchema("A region id from get_overview."),
        Description = "One region's components, its internal wires, and the wires crossing its boundary. " +
                      "A component that is a script carries its language, and one that holds a constant " +
                      "(a slider, a panel) carries its value - drill in with get_component. A region is Dwight's " +
                      "reading group, not a Grasshopper Cluster component.",
      },
      new ToolDescriptor
      {
        // Phase-9 U1 (#382(9)): the reads the verbs have had since Phase 7, on the agent's surface. The
        // description names what comes back because the model has to be TOLD the facts exist, or it keeps
        // inferring a slider's value from item counts (#377).
        Name = GetComponent, Mutates = false, InputSchema = IdSchema("A component id from a previous read."),
        Description = "One component: type, nickname, port names, runtime messages, per-output item counts " +
                      "(flat, summed across branches - the tree structure is get_tree_signature's) - " +
                      "and what it HOLDS: a slider's value and domain, a panel's text - or, for a panel FED by a " +
                      "wire, the values it displays (as text, bounded, said to be displayed values) - a param's " +
                      "internalised data (typed and counted, never the geometry), a script component's source " +
                      "(bounded, with the omission stated), and for a plugin component its library and what it does. " +
                      "Every figure's meaning is stated beside it under \"means\".",
      },
      new ToolDescriptor
      {
        Name = GetConnections, Mutates = false, InputSchema = IdSchema("A component id from a previous read."),
        Description = "The wires into and out of one component, with their data-tree signatures.",
      },
      new ToolDescriptor
      {
        Name = GetTreeSignature, Mutates = false,
        InputSchema = Sch.Object(Sch.Props(
          ("from", Sch.Sub(null, Sch.Props(("node", Sch.Int()), ("param", Sch.Str())), "node", "param")),
          ("to", Sch.Sub(null, Sch.Props(("node", Sch.Int()), ("param", Sch.Str())), "node", "param"))),
          "from", "to"),
        Description = "The data-tree signature on one wire - branch count, depth, items per branch. " +
                      "Structure only; never the values.",
      },
      new ToolDescriptor
      {
        Name = Search, Mutates = false,
        // NEITHER FIELD IS REQUIRED IN THE SCHEMA, AND THE PARSER STILL REQUIRES ONE (#247 F1).
        //
        // A top-level "anyOf" said this precisely - and Anthropic REJECTS it: input_schema does not
        // support oneOf/allOf/anyOf at the top level, so the whole tool list 400'd and no agent turn
        // could run at all. The descriptor is a contract with the PARSER and with the TRANSPORT, and it
        // has to be valid at both.
        //
        // The split is also the right one (ADR-056): the schema states what is LEGAL to send, the
        // plugin decides what is SUFFICIENT. A bare {} would ask for the whole definition, so
        // AgentToolCore.SearchNodes refuses it with the ordinary invalid_args shape, and the
        // description tells the model the rule. Hence no required names below.
        InputSchema = Sch.Object(Sch.Props(
          ("query", Sch.Str("Substring over component type and nickname.")),
          ("type", Sch.Str("Exact component type name - or its qualified form Category > SubCategory > Name, " +
                           "which matches only the components of that one installed type.")))),
        Description = "Find components by type or nickname - give at least one. Returns ids usable with " +
                      "the other tools. " +
                      // U25 (#915): the model is told the form exists before it meets one, and that it is the
                      // form to use wherever a type is asked for.
                      "Where a type name belongs to more than one installed component (Rectangle is both a param " +
                      "and a component), each match carries \"qualified\": the form Category > SubCategory > Name " +
                      "that names this one - use that form wherever a type is asked for.",
      },
      new ToolDescriptor
      {
        Name = KbLookup, Mutates = false,
        InputSchema = Sch.Object(Sch.Props(
          ("type", Sch.Str("One component type name - bare, or qualified as Category > SubCategory > Name. " +
            "Send this or \"types\", not both.")),
          ("types", Sch.Arr(Sch.Str("A component type name, bare or qualified."),
            "Up to 25 type names to check in one call. Send this or \"type\", not both.")))),
        Description = "What an INSTALLED component does, from its own SDK description. Use this to check " +
                      "a component exists before asking for it to be built. " +
                      "To check several types, send them all in `types` in ONE call rather than one call each; " +
                      "you get one row per name, in the order given, each exactly what a single lookup returns. " +
                      "The answer carries the component's qualified form Category > SubCategory > Name. A bare " +
                      "name that belongs to more than one installed component returns EACH of them with its " +
                      "qualified form and says the bare name is ambiguous - build with the qualified form, " +
                      "never the bare one, or the wrong one is placed. " +
                      "The answer also gives the component's ports, read from an instance on the canvas or, when " +
                      "there is none, from a temporary instance that never joins the definition and is never solved; " +
                      "`portsFrom` says which: canvas, instantiated or unavailable. Use those names. When ports are " +
                      "\"unavailable\", do not state port names from memory.",
      },
      new ToolDescriptor
      {
        // A PURE READ (#190): it reports the runtime messages and output counts Grasshopper already
        // holds. It does not solve, so it reports the last solution's facts.
        Name = Diagnose, Mutates = false, Solves = false,
        InputSchema = Sch.Object(Sch.Props(
          ("ids", Sch.Arr(Sch.Int(),
            "Component ids to scope to, with everything upstream of them. Omit for the whole definition.")))),
        Description =
          "What is BROKEN, found deterministically: every runtime error and warning, plus silent " +
          "failures (components that solve green but output nothing), with the root causes marked. " +
          "This is the COMPLETE list for the scope, not a sample - use it instead of checking " +
          "components one by one, which will miss things. It reports the LAST solution's state and does " +
          "not re-run anything.",
      },
      new ToolDescriptor
      {
        // A PURE READ. It reads GROUPS on the canvas and Dwight's own per-file record - no model, no
        // solve, no mutation (#190).
        //
        // IT IS A CATALOGUE TOOL, not a host tool (#355, sharpening #231): the catalogue is anything whose
        // SUBJECT IS THE CANVAS, and a scaffold map's regions are groups on it. An MCP client driving a
        // build needs this exactly as Dwight's own chat does.
        Name = GetScaffoldMap, Mutates = false, Solves = false, InputSchema = NoArgs(),
        Description =
          "The scaffold map on this canvas: each region's ADDRESS (A1, A2, B1 ...), what it is for, its " +
          "pseudo-code, what it declares it takes and gives, which regions it depends on, the existing " +
          "components it anchors to, and how many components are inside it now - so you can tell a region " +
          "that has been built from one that is still just a plan. " +
          "Which regions EXIST is read from the canvas itself, so it is right even if the stored plan is " +
          "lost; anything it could not reconcile is stated rather than left out. Call this before building " +
          "or discussing a region by address. " +
          "TWO SEPARATE LISTS. \"regions\" are ON THE CANVAS - the only ones that can be built into or " +
          "described as present. \"absentFromCanvas\" are in the stored plan and NOT on the canvas: " +
          "undone, deleted, or never laid. Never describe an absent region to the user as though it is " +
          "there, and never merge the two lists when answering what the map holds.",
      },
      new ToolDescriptor
      {
        // Not a read: it runs the solution. It does not change the definition, so it carries no verdict
        // and Mutates stays false - but it is the one tool that does observable work (#190).
        Name = SolveAndCheck, Mutates = false, Solves = true, InputSchema = NoArgs(),
        Description = "Run the definition and report what Grasshopper says: runtime errors and warnings " +
                      "by component, and per-output item counts. Use it to check whether a change worked. " +
                      "This recomputes the definition, so it is not free - the other reads are. " +
                      // U27 (#917): the meaning of every figure is in the payload under "means", and the two
                      // the model got wrong are stated here too, where it reads before it calls.
                      "\"solved\" says whether THIS CALL ran a solve: false means nothing needed solving and the " +
                      "current state is reported - never that solving failed, and nothing about what is wired. " +
                      "outCounts are flat item counts per output, summed across branches. Every figure's meaning " +
                      "is stated beside it under \"means\".",
      },
      new ToolDescriptor
      {
        Name = BuildRegion, Mutates = true, Solves = true,
        InputSchema = Sch.Object(Sch.Props(
          // U18: this used to end "Omit to add to the definition without a region", which invited exactly
          // the build this unit closed. An un-addressed build has no scope the plugin can bound, because
          // the scope IS the address - so every build now names a place.
          ("region", Sch.Str("Where this build goes. Either a scaffold " +
            "region ADDRESS from get_scaffold_map (A1, A2, B1 ...), whose group the components are placed " +
            "inside; or the SPOT the user has just approved for a small placement made without a map " +
            "(Spot 1, Spot 2 ...), which the approval names for you. Never invent either: a place the user " +
            "did not approve is refused before anything is built.")),
          // U30: THE ONE PUBLISHED DECLARATION. This used to be an inline copy of BuildSpecSchema, held equal to it by
          // BuildSpecSchemaTests - and the day a field was added to the shared one (a constant's value), the copy went
          // red by design. There is one parser (BuildSpecParser), so there is one published shape; request_mutation's
          // `place` already publishes it from here.
          ("components", BuildSpecSchema.Components()),
          ("wires", BuildSpecSchema.Wires()),
          // #625: a declared reconnection is its own operation (it REPLACES a cut source in place; a wire APPENDS).
          ("reconnect", Sch.Arr(Sch.Sub(null, Sch.Props(
            ("to", Sch.Sub("The existing component the wire was cut from, by id, and " +
              "its input; at is needed only when that input had more than one cut wire.", Sch.Props(
              ("id", Sch.Int()), ("param", Sch.Str()), ("at", Sch.Int())))),
            ("from", Sch.Sub("A component in this spec, by ref, and its output.", Sch.Props(
              ("ref", Sch.Str()), ("param", Sch.Str()))))),
            "to", "from"),
            "REBUILD ONLY. For each wire the rebuild cuts - a " +
            "component it removes feeding something outside what it removes - which NEW output feeds it now. The " +
            "plugin puts it back in the same place. Omit when the rebuild cuts nothing."))),
          "components"),
        Description = "Place components and wire them, as ONE undoable change. " +
                      "Give \"region\" to build INTO a scaffold region - they land inside that group. " +
                      "If that region already holds components Dwight placed there and the user has not edited since, " +
                      "this REPLACES them - the user approved that removal on the card - so send the whole " +
                      "corrected region rather than the difference. Anything else in the region stays standing, and " +
                      "if nothing there is Dwight's to remove the build is refused: work that is not Dwight's is not " +
                      "Dwight's to remove. " +
                      "You get a limited number of attempts per region in each turn; when they are spent, whatever is " +
                      "standing stays standing and you should report what is wrong rather than retry. " +
                      "The count starts again on the next turn. " +
                      "GIVE A SLIDER ITS \"state\". One placed without it is born at 0-to-1 / 0.250, which " +
                      "is almost never what you meant, and the build will still verify - it was placed and " +
                      "wired exactly as you asked, just useless. " +
                      "Types must be installed " +
                      "(check them all with one kb_lookup call); wire ends are either a \"ref\" from this spec or the \"id\" " +
                      "of an existing component, and at least one end of every wire is a \"ref\": you can wire an " +
                      "existing component into a new one, or anything this build places - a component's output " +
                      "included - into an existing one (after the sources it already has), but never two existing " +
                      "components to each other, and no wire that already exists is removed or reordered - a " +
                      "build that does any of these is reverted. " +
                      "A REBUILD KEEPS ITS BOUNDARY: if a component it would remove feeds anything outside what it " +
                      "removes, declare in \"reconnect\" which new output feeds each of those wires, and the plugin " +
                      "puts it back in the same place. A rebuild that leaves such a wire undeclared is refused " +
                      "before anything is changed, and the refusal lists every wire it would cut. A build cannot " +
                      "delete such a wire: if nothing in the new build should feed it, tell the user to delete that " +
                      "wire first. " +
                      "A FRESH build of a region may also restore, the same way, an input that lost only its source " +
                      "when that region's earlier components were removed - the build turn is told any such input, " +
                      "and declaring it is optional. " +
                      "Give \"param\" only when that end has more than one port - " +
                      "otherwise the plugin fills it in, and a param (a Panel, a Relay, a slider) has no " +
                      "port name at all. Where a name IS needed it must match a real port; it is never " +
                      "guessed at. Returns a VERDICT: whether it landed as specified and " +
                      "whether the existing definition was left untouched. If either fails it is reverted " +
                      "automatically. The verdict does NOT say the result is what you wanted - check that yourself.",
      },
      new ToolDescriptor
      {
        // Mutates, but does NOT solve: a scaffold lays down groups and scribbles, which are not part of
        // the computation - so its verdict is the byte-identical fingerprint (#190 Q1) and no solve is
        // needed to reach it.
        Name = ProposeScaffold, Mutates = true, Solves = false,
        InputSchema =
          Sch.Object(Sch.Props(
            ("regions", Sch.Arr(Sch.Sub(null, Sch.Props(
              ("ref", Sch.Str("Your own label for this region, used by dependsOn in this plan.")),
              ("name", Sch.Str("Two to four words - it becomes the group's label on the canvas.")),
              ("purpose", Sch.Str("One line saying what this region should do.")),
              ("pseudo", Sch.Arr(Sch.Str(), "The steps, in plain language.")),
              // The quotes in these two examples are now ORDINARY CHARACTERS. As a hand-built schema
              // string they needed \\\" - a C# escape wrapping a JSON escape - which is the shape #820
              // broke on the first computed one.
              ("inputs", Sch.Arr(Sch.Str(), "What this region TAKES, " +
                "in words - name where it comes from, e.g. \"curves from Divide (42)\". Declared only; nothing is wired yet.")),
              ("outputs", Sch.Arr(Sch.Str(), "What this region GIVES, " +
                "in words, e.g. \"an ordered point grid\".")),
              ("dependsOn", Sch.Arr(Sch.Str(), "The refs of regions whose output this one needs.")),
              ("anchorIds", Sch.Arr(Sch.Int(), "Existing component ids this region attaches to."))),
              "ref", "name", "purpose")))),
          "regions"),
        Description =
          "Lay down a MAP of work to be done: named, numbered, spaced regions with their intent, and what " +
          "each takes and gives, written on the canvas. The map sits CLEAR of the existing definition, so " +
          "say in \"inputs\" where a region's data comes from - that sentence is what connects it to the " +
          "work it attaches to. Nothing is built - this is the plan the user reads and then works through. " +
          "You decide the regions and what each is for; the plugin decides where they sit and what they " +
          "are CALLED - addresses are A1, A2, B1 ... assigned from the flow you describe with dependsOn, " +
          "so \"A1\" always means the start. Returns the addresses it assigned; use them when you talk " +
          "about the plan in conversation. INSIDE THE PLAN ITSELF - names, purposes, pseudo-code, inputs " +
          "and outputs - refer to other regions by their NAME, never by an address such as A1 or B2: the " +
          "addresses do not exist until this call returns, so one written into the plan is a guess, and a " +
          "plan that contains one is refused. It changes nothing about what the definition computes, and " +
          "one Ctrl-Z removes it.",
      },

      new ToolDescriptor
      {
        // Phase-9 U1(c). Its OWN tool, never an overload of get_region: component ids are stable for the
        // session, region ids die with the next wiring change, and one integer meaning two things by
        // context is how a read lands somewhere plausible and wrong (#208, #195).
        Name = GetCluster, Mutates = false, Solves = false,
        InputSchema = IdSchema("A CLUSTER's component id (get_component says cluster on it)."),
        Description = "What is INSIDE a cluster: its internal components and wires as a region, its input and " +
                      "output hooks by id and name, and how many objects it holds. The internals' ids work with " +
                      "get_component and get_connections like any other. One level per read: a cluster inside " +
                      "a cluster is listed, not opened. A password-protected cluster is reported, never read. " +
                      "A component that is not a cluster is refused.",
      },

      new ToolDescriptor
      {
        // Phase-9 U4. THE WRITE PATH the MCP face will call (refused by stream C's read-only preview by
        // name) and the panel's consent click reaches directly - in the panel loop it is granted on NO
        // turn (AgentLoopCore.Ungranted): the user approves a card naming the exact change and the plugin
        // applies exactly that, with no model call between the approval and the execution (Q1).
        Name = SetValue, Mutates = true, Solves = true,
        InputSchema = Sch.Object(Sch.Props(
          ("id", Sch.Int("The EXISTING Number Slider or Panel to change, by id from a previous read.")),
          ("state", Sch.Sub("The state to set. Number Slider: min, max, value and " +
            "optional rounding (float/integer/even/odd) - all three numbers together; the value must lie within " +
            "the domain and on the rounding's grid. Panel: text. No other type takes a state.", Sch.Props(
            ("min", Sch.Num()),
            ("max", Sch.Num()),
            ("value", Sch.Num()),
            ("rounding", Sch.Enum(null, "float", "integer", "even", "odd")),
            ("text", Sch.Str("Panel only.")))))),
          "id", "state"),
        Description = "Set what an EXISTING Number Slider holds (its domain, rounding and value) or an EXISTING " +
                      "Panel's text, as ONE undoable change to that one component and nothing else. The user " +
                      "approves the exact change first. A rounding change the slider's current value is not on " +
                      "(0.25 to integer) is refused with the two-step way through, because one undo could not " +
                      "restore it. Returns a VERDICT: complete (the component reads back exactly the declared " +
                      "state) and preserved (nothing else on the canvas changed); if either fails the change is " +
                      "reverted. It does not say the new value is what the user wanted.",
      },

      // ---- Phase-9 U2: the geometry read set. Pure reads (#190): no model, no solve, no mutation. ----
      new ToolDescriptor
      {
        Name = GetGeometrySummary, Mutates = false, Solves = false, InputSchema = GeometrySchema(),
        Description = "What is ON a component's output, object by object: type, bounding box, valid, " +
                      "closed/solid, face/vertex/edge counts, length/area/volume. Points, lines, boxes and " +
                      "rectangles count as geometry; numbers and text are counted as non-geometry." + GeometryBoundNote,
      },
      new ToolDescriptor
      {
        Name = CheckMesh, Mutates = false, Solves = false, InputSchema = GeometrySchema(),
        Description = "WHY a mesh is wrong, deterministically: Rhino's own validity log, and a COUNT per " +
                      "defect class (naked edges, non-manifold edges, degenerate or duplicate faces, unused " +
                      "vertices, bad normals, disjoint pieces, short edges, self-intersecting pairs...) with " +
                      "every check switched on and the switched-on checks listed. The counts are the facts: " +
                      "Mesh.Check's own true/false is deliberately ignored (measured: false on a clean box, true " +
                      "on an empty mesh). Rhino documents that self-intersection is not reliably detected, so a " +
                      "zero there is not proof of none. Non-mesh objects are reported as such, not checked." + GeometryBoundNote,
      },
      new ToolDescriptor
      {
        Name = CheckBrep, Mutates = false, Solves = false, InputSchema = GeometrySchema(),
        Description = "Whether a brep is sound: Rhino's validity log, manifold, solid, face and edge " +
                      "counts, and how many edges are naked (open). Surfaces and extrusions are checked as " +
                      "breps; meshes and curves are reported as such, not checked." + GeometryBoundNote,
      },
      new ToolDescriptor
      {
        Name = GetNakedEdges, Mutates = false, Solves = false,
        InputSchema = GeometrySchema(
          ("k", Sch.Int("Representative locations per object to return, at most 10. Default 5."))),
        Description = "Where a mesh or brep is OPEN: the naked-edge count per object plus up to k " +
                      "representative locations (edge midpoints), never the edge curves themselves. Use " +
                      "check_mesh for the full defect count." + GeometryBoundNote,
      },
      new ToolDescriptor
      {
        Name = FindIntersecting, Mutates = false, Solves = false,
        InputSchema = GeometrySchema(
          ("against", Sch.Sub("Optional. Another component's output to test " +
            "against. Omit to test the objects on this output against each other.", Sch.Props(
            ("id", Sch.Int()), ("param", Sch.Str())), "id"))),
        Description = "Which objects intersect: every candidate pair is pre-filtered by bounding box, and " +
                      "the survivors (at most 200 per call, and it says if it stopped there) get the true " +
                      "test - mesh/mesh, brep/brep, curve/curve and curve/brep. Other kinds of pair are " +
                      "reported as untested with their bounding-box overlap, never guessed. Each tested " +
                      "pair says intersects true or false; an untested pair carries the reason instead. " +
                      "Indices refer to object order on the output, the same as get_geometry_summary." + GeometryBoundNote,
      },
      new ToolDescriptor
      {
        Name = GetMassProperties, Mutates = false, Solves = false, InputSchema = GeometrySchema(),
        Description = "Length, area, volume and centroid per object (what applies to its type). " +
                      "Computed on request, so ask for the page you need." + GeometryBoundNote,
      },
      new ToolDescriptor
      {
        Name = SpatialQuery, Mutates = false, Solves = false,
        InputSchema = GeometrySchema(
          ("near", Sch.Arr(Sch.Num(), "A point [x,y,z]: the objects whose bounding boxes are nearest to it.")),
          ("within", Sch.Sub("A box: the objects whose bounding boxes overlap it.", Sch.Props(
            ("min", Sch.Arr(Sch.Num())), ("max", Sch.Arr(Sch.Num()))))),
          ("overlapping", Sch.Sub("Another object, by component id, output and object index (default 0): the objects whose bounding boxes overlap its box.", Sch.Props(
            ("id", Sch.Int()), ("param", Sch.Str()), ("index", Sch.Int()))))),
        Description = "Find objects on an output by WHERE they are, over an R-tree of their bounding boxes. " +
                      "Give exactly one of \"near\", \"within\" or \"overlapping\" - at least one is required " +
                      "and two are refused. Returns object indices with a distance (from the point to the " +
                      "nearest box face for near; between box centres otherwise), ranked and capped by " +
                      "\"limit\", with the number omitted stated." + GeometryBoundNote,
      },

      // ---- Phase-9 U23 (lane C): the session's inert facts. Pure reads (#190): no model, no solve, no mutation. ----
      new ToolDescriptor
      {
        // THE DESCRIPTION CARRIES THE READING'S MEANING because the likeliest wrong inference - that the figures
        // add up to a solve's duration - is exactly the one this read must not invite. And it says it never
        // solves, so a model that finds nothing recorded asks the user rather than looking for a way to make it.
        Name = GetSolveTiming, Mutates = false, Solves = false,
        InputSchema = Sch.Object(Sch.Props(
          ("limit", Sch.Int("How many components to list, from 1 to " + SolveTimingReport.MaxLimit +
                            " (default " + SolveTimingReport.DefaultLimit + "). The report says how many more have measurable time.")))),
        Description = "WHERE THE SOLVE TIME GOES: the components that took longest, and each region's share of " +
                      "the recorded time. Each figure is the time that component took THE LAST TIME IT COMPUTED, " +
                      "rounded to 5 ms - so the figures do not come from one solve and do NOT add up to how long a " +
                      "solve takes. It never runs a solve: if the definition has not been solved yet it says so, " +
                      "and you should ask the user to solve it rather than try to. Components too quick to measure " +
                      "are counted, not listed; components that have not computed are counted separately, because " +
                      "a zero from those means 'never ran', not 'fast'. Viewport preview time is not measured. " +
                      "Region ids work with get_region until the canvas changes.",
      },
      new ToolDescriptor
      {
        // It says what the numbers are FOR, because a unit read the model does not connect to the geometry reads
        // is a fact it will not think to ask for.
        Name = GetDocumentUnits, Mutates = false, Solves = false, InputSchema = NoArgs(),
        Description = "The UNITS and TOLERANCES of Rhino's active document - the one Grasshopper's components take " +
                      "their tolerance from. Every length the geometry reads report is in these units (areas " +
                      "squared, volumes cubed), and intersection and nearness checks measure against the absolute " +
                      "tolerance. Read it before saying how big something is. Never the document's name or path.",
      },
      new ToolDescriptor
      {
        Name = GetInstalledPlugins, Mutates = false, Solves = false, InputSchema = NoArgs(),
        Description = "The Grasshopper plugins installed and loaded, as an inventory: each one's name, version, how " +
                      "many components it provides and how many objects on this canvas come from it - the ones " +
                      "used here first. Grasshopper's own libraries are one entry. A plugin installed as user objects " +
                      "(Ladybug Tools is) is listed by its ribbon category. Rhino plug-ins (.rhp) and plugins " +
                      "that failed to load are not listed. For one component by name, use kb_lookup.",
      },
      new ToolDescriptor
      {
        Name = GetFileState, Mutates = false, Solves = false, InputSchema = NoArgs(),
        Description = "Whether the definition is SAVED, and whether it has changes since it was last saved. Nothing " +
                      "else about its file: never its name, never its path.",
      },
      new ToolDescriptor
      {
        // It tells the model the group numbers are not ids BEFORE it sees one, because the risk is not a tool
        // refusing a number but a model trying to pass it.
        Name = GetCanvasLayout, Mutates = false, Solves = false, InputSchema = NoArgs(),
        Description = "The canvas LAYOUT as facts, never coordinates: each group's name and member component ids " +
                      "(Dwight's own regions with their address), how the groups sit relative to one another (the " +
                      "nearest group left, right, above and below), which components overlap, which groups overlap, how " +
                      "many components are in no group, and which wires run BACKWARDS (right to left, against the flow). " +
                      "Group numbers are positions in that answer only - they are not ids and no tool takes them.",
      },
    };

    /// <summary>
    /// Does calling this tool TRY something on the canvas - mutate it or solve it - rather than read it? Read
    /// off the descriptors' own Mutates and Solves flags, so there is no second list to drift. False for a
    /// name the catalogue does not know, which includes every host tool.
    /// </summary>
    public static bool Acts(string name)
    {
      var d = All().FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
      return d != null && (d.Mutates || d.Solves);
    }

    /// <summary>Is this a tool the surface advertises? Case-insensitive, so a model that shouts still routes.</summary>
    public static bool Knows(string name)
    {
      if (string.IsNullOrWhiteSpace(name)) return false;
      foreach (var t in All())
        if (string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) return true;
      return false;
    }
  }
}
