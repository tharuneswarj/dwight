using System;
using System.Collections.Generic;
using System.Linq;

namespace Dwight.Agent
{
  /// <summary>
  /// A FACT: one component Dwight placed, and the state it left it in. A FACT MAY HAVE A LONG LIFE -
  /// "Dwight placed this" does not stop being true when the user sends another message, or opens another
  /// conversation, or comes back next week.
  ///
  /// Paired deliberately with <see cref="TurnPermission"/>, and the two are separate TYPES rather than
  /// two fields with different reset rules, because the whole of #352's unanswerable-feeling worry came
  /// from their not being separated: WIDENING A FACT'S LIFETIME MUST NEVER WIDEN A PERMISSION'S.
  /// </summary>
  internal sealed class PlacedFact
  {
    public Guid Guid { get; set; }

    /// <summary>The as-left token, taken post-wire and post-solve. Null when it could not be read, which
    /// is treated as "changed" - the safe direction is to leave the component alone.</summary>
    public string AsLeft { get; set; }

    /// <summary>
    /// The ORDERED sources of each of its inputs as Dwight left them, by input guid - or null for a fact recorded
    /// without them (#625). The token is a hash, so it can say THAT something changed but never WHAT; the narrow
    /// sibling needs to know that the only change is one lost source, and this is what lets it.
    /// </summary>
    public IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> Sources { get; set; }

    /// <summary>The params a wire from it carries (its output params, or itself for a floating param) - or null
    /// when not recorded. What lets a lost source be recognised as THIS component's (#625).</summary>
    public IReadOnlyList<Guid> Outputs { get; set; }
  }

  /// <summary>
  /// One component's as-left state as the shim reads it off the canvas: the token, and the structure the token
  /// hashes that a later diff needs (#625). Sources and Outputs may be null for a caller that has only a token.
  /// </summary>
  internal sealed class AsLeftState
  {
    public string Token { get; set; }
    public IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> Sources { get; set; }
    public IReadOnlyList<Guid> Outputs { get; set; }

    public static AsLeftState OfToken(string token) => new AsLeftState { Token = token };
  }

  /// <summary>
  /// A PERMISSION: what ONE TURN may do. A PERMISSION MUST HAVE A SHORT LIFE.
  ///
  /// Both fields die with the turn, for different reasons. The attempt cap bounds a model's
  /// self-correction loop inside a single turn - carried further it would retire a region permanently
  /// after three tries, which is a different and much worse rule. The authorisation is the more
  /// important: an approval that survived the turn it was given in would be a STANDING permission, which
  /// is the privilege concern in its purest form and the one thing the consent gate exists to prevent.
  /// </summary>
  internal sealed class TurnPermission
  {
    public HashSet<string> Authorised { get; set; }
    public Dictionary<string, int> Attempts { get; } = new Dictionary<string, int>(StringComparer.Ordinal);

    /// <summary>U89: the regions whose script was built from the card's source rather than the one the model sent, this turn.</summary>
    public HashSet<string> CardSourceRegions { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>The spot this turn was approved to make, or null (U18). It lives HERE, on the turn's
    /// permission, for the same reason the approved scope does: it is a fact about what the user agreed
    /// to, it must die with the turn, and it must be somewhere the MODEL cannot reach.</summary>
    public string Spot { get; set; }

    /// <summary>Brief CM: the existing group the card said this spot lands in, or null. On the permission for the same reason as <see cref="Spot"/>: it is what the user agreed to, and the model cannot reach it.</summary>
    public Guid? SpotInto { get; set; }

    /// <summary>
    /// Did the user choose the GROUPED option on the spot card (#770)?
    ///
    /// IT TRAVELS ON THE PERMISSION, NEVER IN THE TOOL'S ARGUMENTS, and that is the #346 guard. The mode
    /// pre-select was retired because the model was asked to nominate something it never nominated; here
    /// the plugin offers both buttons from the kind alone, the user picks one, and the model is not
    /// consulted and cannot influence it. A `grouped` field on the build spec would rebuild #346 exactly.
    /// </summary>
    public bool SpotGrouped { get; set; }

    /// <summary>The scripts the approving click granted this turn (U65): shown whole on the region build card and approved with
    /// it. Here for the reason the scope is - a fact about what the user agreed to, dying with the turn, somewhere the model
    /// cannot reach.</summary>
    public List<ApprovedScript> Scripts { get; set; }
  }

  /// <summary>
  /// What a rebuild of one region may do, with every component in it sorted into one of three fates.
  ///
  /// The three-way split IS the consent card: "Dwight will remove the 6 it placed and left alone, leave
  /// the 3 you have since changed, and leave the 2 you added." A count alone is a number; this is a scope
  /// the user can act on.
  /// </summary>
  internal sealed class RebuildClaim
  {
    public RebuildDecision Decision { get; set; }

    /// <summary>Ours, and still exactly as Dwight left it. The only things a rebuild removes.</summary>
    public IReadOnlyList<Guid> Remove { get; set; } = new Guid[0];

    /// <summary>Ours, but the user has since changed it - ADOPTED. Left alone, always. There is
    /// deliberately no option to remove these: leaving them is the safe default, the user can delete by
    /// hand, and a capability to remove work Dwight knows they touched would need justifying and nobody
    /// has asked for it.</summary>
    public IReadOnlyList<Guid> Adopted { get; set; } = new Guid[0];

    /// <summary>In the region and not ours - as far as we can tell. Never touched, and counted so the
    /// card can say so.</summary>
    public int Theirs { get; set; }

    /// <summary>In this region and recorded at a spot: Dwight placed it there by name (Brief CM), or placed it loose and the user moved it in. It is not this region's own work,
    /// so it is never in <see cref="Remove"/>, and it is not a stranger's, so it is not in <see cref="Theirs"/>. It stays, as an adopted component stays.</summary>
    public IReadOnlyList<Guid> PlacedEarlier { get; set; } = new Guid[0];

    /// <summary>
    /// DOES DWIGHT HAVE A RECORD FOR THIS REGION AT ALL? The third answer, and leaving it out is how a
    /// guard starts lying.
    ///
    /// An empty ledger cannot tell "I did not place these" from "I do not remember placing these" - and
    /// after a Rhino restart the ledger IS empty, so a region Dwight really did build would be reported
    /// as one it never touched. That is F1's false accusation returning, moved from after-a-turn to
    /// after-a-restart. Present, absent and NOT KNOWN are three answers (#375), and collapsing the third
    /// into the second is exactly the collapse that made the original refusal unhelpful.
    /// </summary>
    public bool HasRecordForRegion { get; set; }

    /// <summary>
    /// WHAT A REBUILD REMOVES - the claim's <see cref="Remove"/>, kept to what is still on the canvas at the
    /// moment of removal, and NOTHING ELSE (#631).
    ///
    /// The consent card is drawn from this same claim and names what goes and what stays. The build used to
    /// remove every recorded component still on the canvas instead - Remove AND Adopted - so it deleted the
    /// component the card had just promised to LEAVE, and preservation certified the deletion because the
    /// adopted guid was in the replaced set. So the removal exists only here, as a function of the claim,
    /// and the ledger exposes no guid list a caller could remove from instead.
    ///
    /// A null <paramref name="onCanvas"/> answers EMPTY: a check that decides what gets deleted fails
    /// towards leaving things standing.
    /// </summary>
    public IReadOnlyList<Guid> RemovalPresentIn(Func<Guid, bool> onCanvas) =>
      onCanvas == null ? new Guid[0] : Remove.Where(onCanvas).ToList();

    public static RebuildClaim Of(RebuildDecision decision, IReadOnlyList<Guid> remove,
      IReadOnlyList<Guid> adopted, int theirs, bool hasRecord = false, IReadOnlyList<Guid> placedEarlier = null) =>
      new RebuildClaim
      {
        Decision = decision,
        Remove = remove ?? new Guid[0],
        Adopted = adopted ?? new Guid[0],
        Theirs = theirs,
        HasRecordForRegion = hasRecord,
        PlacedEarlier = placedEarlier ?? new Guid[0],
      };
  }

  /// <summary>What a build attempt on one region may do next.</summary>
  internal enum RebuildDecision
  {
    /// <summary>Nothing is in the region: build it.</summary>
    Fresh = 0,
    /// <summary>This turn built here before: remove exactly what it created, then build again. Or the region holds only what Dwight placed there earlier: remove nothing, keep those, and build beside them.</summary>
    Replace = 1,
    /// <summary>Something is in the region that this turn did not put there. REFUSE.</summary>
    Occupied = 2,
    /// <summary>The attempt budget for this region is spent. REFUSE.</summary>
    Exhausted = 3,
  }

  /// <summary>
  /// WHAT DWIGHT PLACED, PER REGION - and therefore what a rebuild is allowed to remove
  /// (Phase-8 WP-4 Stage B, the four constraints of #352, with constraint 2 superseded at the gate).
  ///
  /// WHY IT EXISTS. <c>build_region</c> only ADDS. A build that lands, preserves and completes but
  /// produces new runtime errors is left STANDING (that is deliberate and correct - a region may
  /// legitimately error until its neighbours exist), so the self-correction loop's subject is a region
  /// that is built and broken. Correcting it means replacing what is there, and replacing means
  /// DELETING - which is the most dangerous capability WP-4 introduces.
  ///
  /// THE FOUR CONSTRAINTS, each one a way the delete could go wrong:
  ///
  ///   1. IT HOLDS GUIDS, KEYED BY ADDRESS. A rebuild removes exactly the objects this turn's builds
  ///      created in that region - never "whatever is in that group". The difference is the whole safety
  ///      property: a group is a place, and other things can be in a place.
  ///   2. AS ORIGINALLY WRITTEN: "IT IS IN MEMORY FOR THE TURN ONLY", because a surviving delete list is
  ///      privilege-shaped (#220's instinct) - a list of things-we-may-delete that outlived the work it
  ///      was for would let a later turn remove components the user has since decided to keep.
  ///
  ///      SUPERSEDED AT THE WP-4 GATE, and the reason it had to be is worth more than the rule was. The
  ///      constraint was correct about its own purpose and excluded THE COMMONEST REQUEST THERE IS: a
  ///      user who built A1 and then asked, in their next message, to rebuild it was told Dwight had not
  ///      placed components Dwight had just placed. A constraint that is right about its reason and
  ///      wrong about a user is a defect no correctness review catches, because every reviewer checks it
  ///      against the reason.
  ///
  ///      WHAT REPLACES THE PROTECTION rather than merely relaxing it: the facts are DOCUMENT-scoped
  ///      ("Dwight placed this" is a fact about the document, not about a conversation - thread scope
  ///      created a cliff the user could not see, where "rebuild A1" worked in one chat and refused in
  ///      another); the claim is RE-DERIVED against the canvas at the moment of use rather than trusted
  ///      from storage; and THE CONSENT CARD NAMES THE REMOVAL, by nickname and type. A stored privilege
  ///      exercised later is what #352 feared. A consented act with its scope in front of the user at the
  ///      moment of consent is not, because they read it and decide then.
  ///
  ///      AND THE PERMISSION DID NOT WIDEN WITH IT. See <see cref="PlacedFact"/> and
  ///      <see cref="TurnPermission"/>: a fact may have a long life, a permission must have a short one,
  ///      and they are separate TYPES so that widening one cannot widen the other by accident.
  ///   3. AN EMPTY LEDGER REFUSES; IT NEVER FALLS BACK TO GROUP MEMBERSHIP. If the region holds
  ///      components and this turn did not place them, we do not know whose they are - and by then they
  ///      may be the USER'S. Falling back to "delete what is in the group" is the silent-fallback class
  ///      (#208) pointed at a delete, which is the worst place it could possibly sit.
  ///   4. A REBUILD COUNTS AGAINST THE CAP. Otherwise a model that can always replace its own work can
  ///      loop on one region until the turn's step budget runs out, paying for every attempt.
  ///
  /// GH-free (ADR-091): every decision above is a pure function and is tested, because the shim that
  /// executes them cannot be.
  /// </summary>
  internal sealed class BuildLedger
  {
    /// <summary>
    /// The unit of work this ledger belongs to - the object identity of the turn that opened it.
    ///
    /// Turn-scoping is held by IDENTITY rather than by a clear() somebody has to remember: a caller asks
    /// the scope for the ledger belonging to ITS turn, and a different turn is handed a fresh empty one
    /// (see <c>AgentScope.LedgerFor</c>). An empty ledger refuses a rebuild by rule 3, so the failure
    /// direction of "the turn changed" is REFUSE, never "delete something we no longer have a claim to".
    /// </summary>
    public object Owner { get; }

    /// <summary>
    /// The turn currently in progress. Changing it resets the TURN-lifetime state and keeps the
    /// THREAD-lifetime state - see <see cref="BeginTurn"/>.
    /// </summary>
    private object _turn;

    /// <summary>THE FACTS, by region: what Dwight placed and the state it left it in. Long-lived.</summary>
    private readonly Dictionary<string, List<PlacedFact>> _placed =
      new Dictionary<string, List<PlacedFact>>(StringComparer.Ordinal);

    /// <summary>THE PERMISSION for the turn in progress. Replaced wholesale by <see cref="BeginTurn"/>,
    /// so there is no field-by-field reset for anyone to forget half of.</summary>
    private TurnPermission _turnPermission = new TurnPermission();

    /// <summary>
    /// The regions this turn was AUTHORISED to build, or null for a caller that does not use Dwight's
    /// consent gate at all.
    ///
    /// WHY IT LIVES HERE. The approved scope must BOUND what the turn can write - telling the model
    /// "you may build A1 to A3" is a prompt plea, and #195/#141 say a deterministic fact the plugin can
    /// compute is the plugin's job. So `build_region` asks this before it touches anything, and a region
    /// outside the set is refused however convincingly it was requested.
    ///
    /// And it lives on the LEDGER because the ledger already has exactly the right lifetime: turn-scoped
    /// by the identity of the call (#352's constraint 2), never persisted, and absent by construction
    /// after a restart. That is #220's shape - "there is no variable holding a privilege for a restart to
    /// restore" - reaching the one privilege WP-4 adds.
    ///
    /// NULL IS NOT "ANYTHING GOES BY ACCIDENT". It means this caller has no chat consent gate to honour:
    /// the DEBUG harness, and the v1.1 MCP face, where the client brings its own human-in-the-loop
    /// (#231). The CHAT can never reach it - a Build-capability turn refuses to start without an
    /// authority - so the fail-open is confined to callers that were never inside this gate.
    /// </summary>
    

    public BuildLedger(object owner) { Owner = owner; }

    /// <summary>
    /// Start a turn on this ledger, resetting everything whose lifetime is a TURN and keeping what
    /// belongs to the THREAD (F1(b), from the WP-4 gate).
    ///
    /// WHY THE LEDGER OUTLIVES A TURN AT ALL. #352 constraint 2 scoped the whole thing to one turn, for a
    /// good reason - a surviving delete list is privilege-shaped. It was correct for its stated purpose
    /// and it excluded the commonest request there is: a user who builds A1 and then, in their NEXT
    /// message, asks to rebuild it was told Dwight had not placed components Dwight had just placed. A
    /// constraint that is right about its own reason and wrong about a user is a defect no correctness
    /// review catches, because every reviewer checks it against the reason.
    ///
    /// WHAT REPLACES THE OLD PROTECTION, and it dissolves the objection rather than managing it: the
    /// claim is RE-DERIVED at the moment of use (see <see cref="Resolve"/>) rather than trusted from
    /// storage, and THE CONSENT CARD NAMES THE REMOVAL - how many will go, and how many will be left. A
    /// stored privilege exercised later is exactly what #352 feared; a consented act with its scope in
    /// front of the user at the moment of consent is not, because the user reads it and decides then.
    ///
    /// TWO LIFETIMES, DELIBERATELY DIFFERENT:
    ///   - what Dwight CREATED is the thread's, because "Dwight placed this" does not stop being true
    ///     when the user sends another message;
    ///   - the ATTEMPT CAP and the AUTHORISATION are the turn's. The cap bounds a self-correction loop
    ///     inside one turn. The authorisation is the more important of the two: carrying an approval
    ///     across turns would be a STANDING permission, which is the privilege concern in its purest
    ///     form and the one thing the gate exists to prevent.
    /// </summary>
    public void BeginTurn(object turn)
    {
      if (ReferenceEquals(_turn, turn)) return;
      _turn = turn;
      // The whole permission is replaced, never reset field by field - a partial reset is how an
      // authorisation survives a turn by accident.
      _turnPermission = new TurnPermission();
    }

    /// <summary>Grant this turn the regions the user approved. Once only: a second grant would be a turn
    /// widening its own authority, which is the one thing the whole gate exists to prevent.</summary>
    public void Authorise(IEnumerable<string> regions)
    {
      if (_turnPermission.Authorised != null) return;
      _turnPermission.Authorised = new HashSet<string>(
        (regions ?? Enumerable.Empty<string>()).Where(r => !string.IsNullOrEmpty(r)), StringComparer.Ordinal);
    }

    /// <summary>
    /// Grant this turn the ONE SPOT the user approved, and whether they chose to have it grouped (U18).
    ///
    /// Once only, exactly as <see cref="Authorise"/> is: the spot joins the authorised set, so
    /// <see cref="MayBuild"/> needs no special case and a build aimed anywhere else is refused by the
    /// path that already refuses it.
    /// </summary>
    public void AuthoriseSpot(string spot, bool grouped, Guid? into = null)
    {
      if (_turnPermission.Authorised != null) return;
      if (string.IsNullOrEmpty(spot)) return;
      _turnPermission.Authorised = new HashSet<string>(new[] { spot }, StringComparer.Ordinal);
      _turnPermission.Spot = spot;
      _turnPermission.SpotGrouped = grouped;
      _turnPermission.SpotInto = into;
    }

    /// <summary>The existing group this turn's approved spot lands in, or null. Null for every other address, so nothing else can be answered by it.</summary>
    public Guid? SpotInto(string address) => IsApprovedSpot(address) ? _turnPermission.SpotInto : null;

    /// <summary>
    /// Grant this turn the scripts the approving card SHOWED (U65). Once only, as <see cref="Authorise"/> is: a turn cannot add
    /// to the code it was approved to run. Null or empty grants nothing, which is the default for every other turn.
    /// </summary>
    public void AuthoriseScripts(IEnumerable<ApprovedScript> scripts)
    {
      if (_turnPermission.Scripts != null) return;
      _turnPermission.Scripts = (scripts ?? Enumerable.Empty<ApprovedScript>()).Where(s => s != null).ToList();
    }

    /// <summary>The scripts this turn's card showed and the user approved - empty when it showed none.</summary>
    public IReadOnlyList<ApprovedScript> ApprovedScripts =>
      (IReadOnlyList<ApprovedScript>)_turnPermission.Scripts ?? new ApprovedScript[0];

    /// <summary>The spot this turn may make, or null. A build at any other address is not this.</summary>
    public string ApprovedSpot => _turnPermission.Spot;

    /// <summary>Is <paramref name="address"/> this turn's approved spot, and was it approved WITH a group?
    /// False for every other address, so a plan region can never be answered by this.</summary>
    public bool SpotIsGrouped(string address) =>
      !string.IsNullOrEmpty(address) &&
      string.Equals(address, _turnPermission.Spot, StringComparison.Ordinal) &&
      _turnPermission.SpotGrouped;

    /// <summary>Is <paramref name="address"/> this turn's approved spot at all (grouped or loose)?</summary>
    public bool IsApprovedSpot(string address) =>
      !string.IsNullOrEmpty(address) &&
      string.Equals(address, _turnPermission.Spot, StringComparison.Ordinal);

    /// <summary>Has an authority been granted at all? False for a caller outside the consent gate.</summary>
    public bool IsAuthorityScoped => _turnPermission.Authorised != null;

    /// <summary>
    /// May this turn build into <paramref name="address"/>?
    ///
    /// An un-addressed build (#354 Q3) is governed by the CARD, not by an address: the user approved a
    /// build and there is no region to check it against, so it is allowed wherever a build is allowed at
    /// all. What bounds it is the turn's step ceiling and the ordinary verdict.
    /// </summary>
    /// <summary>
    /// May this turn build at this address?
    ///
    /// THE UN-ADDRESSED CASE USED TO RETURN TRUE, with the comment "the card is the scope" (U18 closed
    /// it). That sentence was ASPIRATIONAL: nothing anywhere made the card the scope for a build with no
    /// address, so had one ever been reachable from the chat, a card saying "place a Panel" would have
    /// admitted forty components with every guard passing. It was safe only by accident - three gates
    /// stopped the chat reaching an un-addressed build at all - and U18 opens exactly those gates.
    ///
    /// THE CARD IS NOT A SCOPE MECHANISM. THE ADDRESS IS. A card names a place, the plugin mints or
    /// validates that place, the turn is authorised for it, and this refuses everything else. That is why
    /// U18's small placements get a real address (a spot) rather than being un-addressed: the enforcement
    /// IS the address, and a build with none cannot be bounded by anything.
    ///
    /// The <c>Authorised == null</c> path is UNCHANGED and deliberately so - it is the DEBUG harness and
    /// the v1.1 MCP face, which were never inside this gate (see the type comment above). Narrowing it is
    /// a separate question about a different caller, not a tidy-up to fold in here.
    /// </summary>
    public bool MayBuild(string address)
    {
      if (_turnPermission.Authorised == null) return true;                  // no gate to honour - see above
      if (string.IsNullOrEmpty(address)) return false;   // a scope exists and this names no place in it
      return _turnPermission.Authorised.Contains(address);
    }

    /// <summary>What the user approved, for a refusal that can name it.</summary>
    public IReadOnlyList<string> Authorised =>
      _turnPermission.Authorised == null ? new string[0]
        : _turnPermission.Authorised.OrderBy(a => a, StringComparer.Ordinal).ToArray();

    // THERE IS DELIBERATELY NO "WHAT DID DWIGHT PLACE HERE" GUID LIST ON THIS TYPE (#631). The one that
    // existed (CreatedIn) returned every recorded component still on the canvas, adopted ones included, and
    // the rebuild deleted from it - so it removed what the consent card promised to leave. What a rebuild
    // may remove is RebuildClaim.RemovalPresentIn, from the resolved claim, and nothing else.

    private List<PlacedFact> Facts(string address) =>
      address != null && _placed.TryGetValue(address, out var list) ? list : new List<PlacedFact>();

    /// <summary>
    /// How many facts one region keeps (#632). Facts are never destroyed by a rebuild, so they are bounded
    /// instead. A build places at most 40 components, so this holds five full builds of one region; past it
    /// the OLDEST go first. A dropped fact makes its component read as not-Dwight's, which is the safe
    /// direction for a record that licenses deletion: it is left standing, never removed.
    /// </summary>
    public const int MaxFactsPerRegion = 200;

    /// <summary>
    /// RECORD WHAT A VERIFIED BUILD PLACED - the ledger's only writer, called only on the committed-and-
    /// verified path (#623/#632). It FORGETS NOTHING.
    ///
    /// #623: the rebuild used to forget the components it replaced INSIDE the mutation, before the verdict.
    /// Every failure path reverts with an undo that restores those components under their own guids - and
    /// nothing restored the record, so the next attempt refused with "NO RECORD ... restarted". A revert that
    /// restores the subject and not the bookkeeping about it is half a revert. There is now no forget for a
    /// failure path to call, and nothing to call before the verdict: a revert leaves the record exactly as it
    /// was, because nothing wrote to it.
    ///
    /// #632, the same class from the user's side: a Ctrl-Z of a VERIFIED rebuild brings the old components
    /// back, and a commit that forgot them had made them read as never Dwight's. So what a rebuild replaced
    /// stays recorded as a DORMANT fact - still true ("Dwight placed this"), simply not on the canvas - and
    /// <see cref="Resolve"/> filters to what is present. Bounded by <see cref="MaxFactsPerRegion"/>.
    /// </summary>
    public void CommitBuild(string address, IReadOnlyDictionary<Guid, string> placed) =>
      CommitBuild(address, (placed ?? new Dictionary<Guid, string>())
        .ToDictionary(kv => kv.Key, kv => AsLeftState.OfToken(kv.Value)));

    /// <summary>The same commit, with the structured as-left state the reconnection and the sibling need (#625).</summary>
    public void CommitBuild(string address, IReadOnlyDictionary<Guid, AsLeftState> placed)
    {
      if (string.IsNullOrEmpty(address)) return;
      if (!_placed.TryGetValue(address, out var list)) _placed[address] = list = new List<PlacedFact>();
      _lastBuilt[address] = new HashSet<Guid>((placed ?? new Dictionary<Guid, AsLeftState>()).Keys);
      foreach (var kv in placed ?? new Dictionary<Guid, AsLeftState>())
      {
        if (kv.Key == Guid.Empty) continue;
        var existing = list.FirstOrDefault(f => f.Guid == kv.Key);
        if (existing == null) list.Add(existing = new PlacedFact { Guid = kv.Key });
        Apply(existing, kv.Value);   // re-placed: the as-left state is the NEW one
      }
      if (list.Count > MaxFactsPerRegion) list.RemoveRange(0, list.Count - MaxFactsPerRegion);
    }

    /// <summary>
    /// REFRESH ONE COMPONENT'S AS-LEFT STATE AFTER DWIGHT'S OWN RECONNECTION CHANGED IT - COMPARE-AND-SET (#625,
    /// #648 ruling v).
    ///
    /// A rebuild that reconnects B1's XY Plane changes the XY Plane's sources, and so its as-left token. Left
    /// alone, Dwight's own rebuild would make its downstream read as "edited by the user since", and the next
    /// rebuild of B1 would LEAVE it. So the record follows the reconnection - but ONLY IF the component was still
    /// exactly as Dwight left it immediately before this build (<paramref name="expectedBefore"/> equals what is
    /// stored). A component the user had ALREADY edited stays adopted. Refreshing it unconditionally would launder
    /// a user's edit into "untouched since Dwight left it", which is a licence to delete their work at the next
    /// rebuild.
    ///
    /// Looks across every region: the consumer of a rebuild of A1 usually lives in B1. Returns whether it refreshed.
    /// </summary>
    public bool RefreshAsLeft(Guid guid, string expectedBefore, AsLeftState after)
    {
      if (guid == Guid.Empty || string.IsNullOrEmpty(expectedBefore) || after == null) return false;
      foreach (var list in _placed.Values)
      {
        var fact = list.FirstOrDefault(f => f.Guid == guid);
        if (fact == null) continue;
        if (!AsLeftRecord.Unchanged(fact.AsLeft, expectedBefore)) return false;   // already the user's: never laundered
        Apply(fact, after);
        return true;
      }
      return false;
    }

    /// <summary>The as-left state Dwight holds for one component, or null when it holds no fact for it. A read of
    /// one component's recorded state, never a list anything could remove from (#631).</summary>
    public AsLeftState AsLeftOf(Guid guid)
    {
      foreach (var list in _placed.Values)
      {
        var fact = list.FirstOrDefault(f => f.Guid == guid);
        if (fact != null) return new AsLeftState { Token = fact.AsLeft, Sources = fact.Sources, Outputs = fact.Outputs };
      }
      return null;
    }

    /// <summary>
    /// The output-param guids of a region's DORMANT facts - components Dwight placed there that are no longer on the
    /// canvas (#625's narrow sibling). A source lost from one of these is what a fresh build of that region may
    /// restore. Facts recorded without outputs contribute nothing.
    /// </summary>
    public ISet<Guid> DormantOutputs(string address, Func<Guid, bool> onCanvas)
    {
      var outputs = new HashSet<Guid>();
      if (onCanvas == null) return outputs;
      foreach (var f in Facts(address))
      {
        bool present;
        try { present = onCanvas(f.Guid); } catch { present = true; }   // unknown: never treated as gone
        if (present || f.Outputs == null) continue;
        foreach (var o in f.Outputs) outputs.Add(o);
      }
      return outputs;
    }

    private readonly Dictionary<string, HashSet<Guid>> _lastBuilt = new Dictionary<string, HashSet<Guid>>(StringComparer.Ordinal);

    /// <summary>Brief DP: what the last build that stood here placed. Everything else in the region stood before it, so a tidy can keep those first in their columns. Empty for an address never built in this session, and nothing here is saved, because a tidy only ever follows a build.</summary>
    public IReadOnlyCollection<Guid> LastBuilt(string address) =>
      address != null && _lastBuilt.TryGetValue(address, out var set) ? set : new HashSet<Guid>();

    /// <summary>Brief CL: what this ledger records placing at an address, on the canvas or not. A read: a loose spot has no group to ask.</summary>
    public IReadOnlyList<Guid> PlacedAt(string address) =>
      address != null && _placed.TryGetValue(address, out var list) ? list.Select(f => f.Guid).ToList() : new List<Guid>();

    /// <summary>
    /// Every fact that COULD carry a restorable loss: recorded with structured sources, in any region (#625). A
    /// read for the sibling's diff - which only ever RESTORES a source - and never a list anything is removed from
    /// (#631).
    /// </summary>
    public IReadOnlyList<(Guid Guid, string Region, AsLeftState State)> RestoreCandidates() =>
      _placed.SelectMany(kv => kv.Value
          .Where(f => f.Sources != null && !string.IsNullOrEmpty(f.AsLeft))
          .Select(f => (f.Guid, kv.Key, new AsLeftState { Token = f.AsLeft, Sources = f.Sources, Outputs = f.Outputs })))
        .ToList();

    private static void Apply(PlacedFact fact, AsLeftState state)
    {
      fact.AsLeft = state?.Token;
      fact.Sources = state?.Sources == null ? null
        : state.Sources.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<Guid>)(kv.Value ?? new Guid[0]).ToList());
      fact.Outputs = state?.Outputs?.ToList();
    }

    /// <summary>How many facts this ledger holds for a region, present or dormant - never a list anything could
    /// remove from (#631). Read by the bound's test, the drill, and U18's spot invariant: a spot with any fact
    /// has been placed, and a second placement there is refused at the click (<c>SpotAsk.RefuseIfPlaced</c>).</summary>
    public int FactCount(string address) => Facts(address).Count;

    /// <summary>The refusal a build gets when a region's attempts are spent. It states the cap's SCOPE as a fact the
    /// ledger stands behind (U55): attempts live on the turn permission, <see cref="BeginTurn"/> replaces it, so a
    /// later turn has a fresh count. Without the last sentence a model read "in this turn" as "ever" (OpenAI Terra,
    /// 25 October: "cannot retry B1 after its exhausted attempt").</summary>
    public static string ExhaustedMessage(string region, int cap, bool cardSourceBuilt = false) =>
      $"You have used all {cap} attempts at {region} in this turn. What is there now is left exactly as it is - " +
      "nothing was reverted. Tell the user what is still wrong with it rather than trying again. " +
      "The attempts start again on your next turn: tell the user what went wrong now, and on a later turn you may ask to build it again." +
      (cardSourceBuilt
        ? " Note that the attempts that failed ran the card's source, not yours: the script on the card the user approved was built each time."
        : "");

    /// <summary>U89: record that this turn built <paramref name="region"/>'s script from the card's source, so an exhausted
    /// refusal can say whose source the failed attempts ran.</summary>
    public void NoteCardSourceBuilt(string address)
    {
      if (!string.IsNullOrEmpty(address)) _turnPermission.CardSourceRegions.Add(address);
    }

    public bool CardSourceBuilt(string address) => address != null && _turnPermission.CardSourceRegions.Contains(address);

    public int Attempts(string address) =>
      address != null && _turnPermission.Attempts.TryGetValue(address, out var n) ? n : 0;

    /// <summary>
    /// Count one attempt against a region's budget.
    ///
    /// An ATTEMPT is a build that actually ran - placed objects and solved. A spec refused before the
    /// canvas was touched is NOT an attempt: it never tested anything, its refusal is itself actionable
    /// feedback, and counting it would spend the region's budget on malformed JSON. A model that loops
    /// on malformed specs is bounded by the TURN's step budget instead, which is the right instrument
    /// for that failure.
    /// </summary>
    public void CountAttempt(string address)
    {
      if (string.IsNullOrEmpty(address)) return;
      _turnPermission.Attempts[address] = Attempts(address) + 1;
    }

    /// <summary>
    /// What a build on <paramref name="address"/> may do, given what is in the region now.
    ///
    /// <paramref name="componentsInRegion"/> counts the components the GROUP holds (excluding its own
    /// scribbles). The comparison against the ledger is what separates "our previous attempt" from
    /// "somebody else's work", and it is deliberately conservative in one direction only: anything we
    /// cannot account for makes this refuse.
    /// </summary>
    /// <summary>
    /// WHAT A REBUILD OF THIS REGION MAY DO, with the claim RE-DERIVED against what is actually there.
    ///
    /// THE LAW, and it is the whole of the capability:
    ///
    ///   DWIGHT MAY REMOVE WHAT DWIGHT PLACED, INSIDE A REGION THE USER APPROVED, WHEN THE USER ASKED
    ///   FOR A REBUILD, WITH THE REMOVAL NAMED ON THE CARD. REMOVING ANYTHING ELSE NEEDS A CAPABILITY
    ///   WE DO NOT HAVE.
    ///
    /// Re-derivation is what makes a thread-lived claim safe. The claim is the INTERSECTION of what this
    /// ledger recorded with what the region holds right now, so a claim on something the user has since
    /// deleted, or dragged out of the group, cannot cause a deletion - it simply is not part of this claim,
    /// and the card's counts are about objects that exist.
    ///
    /// IT FILTERS; IT NEVER PRUNES (#632). This used to delete the facts for absent components, on the
    /// reasoning that a claim that no longer describes the canvas is not a claim. But "Dwight placed this" is
    /// still TRUE of a component an undo is about to bring back under its own guid - and after a pruning
    /// resolve, a user's Ctrl-Z of a rebuild made Dwight's own components read as never Dwight's. A fact for
    /// an absent component is DORMANT, not false. So this reads the ledger and writes nothing.
    ///
    /// Everything in the region that is NOT ours is counted and never touched. That count is not
    /// bookkeeping - it is what the card shows the user under "and leave", and it is the number that
    /// makes the removal something they can consent to rather than discover.
    /// </summary>
    public RebuildClaim Resolve(string address, IReadOnlyDictionary<Guid, string> inRegionNow, int maxAttempts) =>
      Claim(address, inRegionNow, maxAttempts);

    /// <summary>
    /// WHAT A REBUILD OF THIS REGION WILL DO IN THE TURN A CARD OPENS - the consent card's preview (#626).
    ///
    /// The card previews the turn it OPENS, not the turn it is drawn in. A card minted after this turn spent a
    /// region's attempts is drawn inside the exhausted turn, where <see cref="Resolve"/> answers Exhausted with
    /// nothing to remove - so the card would name no removal while the approved turn, whose budget is fresh,
    /// removes all of it. This is <see cref="Resolve"/> without this turn's attempt budget, and nothing else:
    /// one claim, so the card and the build cannot state the rule two ways.
    /// </summary>
    public RebuildClaim ResolveForNextTurn(string address, IReadOnlyDictionary<Guid, string> inRegionNow) =>
      Claim(address, inRegionNow, maxAttempts: null);

    /// <summary>
    /// THE SAME CLAIM, FOR A PLACEMENT THAT HAS NO BOX - U18's loose spot (#770).
    ///
    /// THE RECORD IS THE ENFORCEMENT; THE GROUP IS A VIEW OF IT. A grouped region's extent is its group,
    /// and everything in it that is not ours is counted as theirs and left alone. A LOOSE SPOT HAS NO
    /// BOUNDARY, so its extent is its RECORD: the components this ledger placed, and nothing else can be
    /// "inside" it. That makes `theirs` zero by construction rather than by luck - there is no boundary
    /// for a stranger's component to be within - and it is an honest consequence of choosing no box, not
    /// a weakening of the claim.
    ///
    /// THE GUID LIST NEVER LEAVES THIS TYPE, and that is deliberate. #631 retired `CreatedIn` because a
    /// public "what did Dwight place here" list was used as a REMOVAL list and removed what the card had
    /// promised to leave. So this walks its own facts internally and asks the caller only "what is this
    /// one's as-left token now, or null if it is gone". The removal set is still produced solely by
    /// <see cref="RebuildClaim.RemovalPresentIn"/>, and the as-left comparison still decides ours from
    /// adopted. A caller cannot get a deletable list out of here by any route.
    /// </summary>
    public RebuildClaim ResolveWithoutBoundary(string address, Func<Guid, string> asLeftNow, int? maxAttempts)
    {
      var present = new Dictionary<Guid, string>();
      if (!string.IsNullOrEmpty(address) && asLeftNow != null && _placed.TryGetValue(address, out var facts))
        foreach (var fact in facts)
        {
          if (fact == null || present.ContainsKey(fact.Guid)) continue;
          // NULL MEANS "CANNOT BE ACCOUNTED FOR", WHICH IS WIDER THAN "GONE" - and the wording matters,
          // because this comment said "no longer on the canvas" and #777 made that false while leaving the
          // BEHAVIOUR right. A component can also be present and unreadable: a script whose source the
          // fingerprint cannot see reports null rather than a stable token, precisely so it does not
          // compare as unchanged and become removable.
          //
          // Either way it is NOT PART OF THIS CLAIM, and either way that is the safe answer: a rebuild
          // removes only what it can positively account for as its own and untouched.
          var now = asLeftNow(fact.Guid);
          if (now != null) present[fact.Guid] = now;
        }

      return Claim(address, present, maxAttempts);
    }

    private RebuildClaim Claim(string address, IReadOnlyDictionary<Guid, string> inRegionNow, int? maxAttempts)
    {
      var present = inRegionNow ?? new Dictionary<Guid, string>();

      // An un-addressed build has no region state: it is governed by the card, not by an address.
      if (string.IsNullOrEmpty(address)) return RebuildClaim.Of(RebuildDecision.Fresh, null, null, 0);

      if (maxAttempts.HasValue && Attempts(address) >= maxAttempts.Value)
        return RebuildClaim.Of(RebuildDecision.Exhausted, null, null, present.Count);

      if (present.Count == 0) return RebuildClaim.Of(RebuildDecision.Fresh, null, null, 0);

      // ---- RE-DERIVE: ours is what we recorded AND what is still here, in placement order ----------
      var facts = Facts(address);

      // Any fact, present or dormant: "we once recorded building here" is what separates "not ours" from
      // "no record". A region whose components the user deleted still leaves us knowing we built there.
      var hadRecord = facts.Count > 0;

      // FILTER, never prune (#632): a fact for an absent component is dormant, and an undo can make it
      // present again under the same guid.
      var stillHere = facts.Where(f => present.ContainsKey(f.Guid)).ToList();

      // ---- AND SORT OURS BY WHETHER THE USER HAS ADOPTED IT ---------------------------------------
      //
      // Unchanged since Dwight left it: ours to remove. Changed since: theirs now, in every sense that
      // matters, and left alone. An unreadable token on either side counts as CHANGED, because the safe
      // direction is always to leave a component standing.
      var remove = new List<Guid>();
      var adopted = new List<Guid>();
      foreach (var f in stillHere)
        (AsLeftRecord.Unchanged(f.AsLeft, present[f.Guid]) ? remove : adopted).Add(f.Guid);

      // WHAT DWIGHT PLACED HERE BY NAME IS RECORDED AT ITS SPOT, because the spot stays the ledger's key (Brief CM), so this region's own facts do not know it. Counted as a
      // stranger's, a build here was refused with "no record" of components Dwight had just placed. Only a spot's facts count: another region's work in this group is not ours here.
      var ownHere = new HashSet<Guid>(stillHere.Select(f => f.Guid));
      var earlier = _placed
        .Where(kv => !string.Equals(kv.Key, address, StringComparison.Ordinal) && ScaffoldAddress.IsSpot(kv.Key))
        .SelectMany(kv => kv.Value)
        .Select(f => f.Guid)
        .Where(g => present.ContainsKey(g) && !ownHere.Contains(g))
        .Distinct()
        .ToList();

      var theirs = present.Count - stillHere.Count - earlier.Count;

      // With nothing adopted and nothing a stranger's, the region holds only its own untouched work and what was placed earlier. With none of its own that is a rebuild that removes nothing.
      var onlyOursOrEarlier = adopted.Count == 0 && theirs == 0;

      // Rule 3 stands, now MEASURED rather than assumed: only a claim we can name lets us remove
      // anything, and "can name" now means recorded it, it is still here, AND it is still as we left it.
      return remove.Count > 0 || onlyOursOrEarlier
        ? RebuildClaim.Of(RebuildDecision.Replace, remove, adopted, theirs, hadRecord, earlier)
        : RebuildClaim.Of(RebuildDecision.Occupied, null, adopted, theirs, hadRecord, earlier);
    }

    /// <summary>
    /// Why a rebuild found nothing of its own to remove, in the user's terms.
    ///
    /// The two cases are genuinely different and a single sentence for both is what made the old refusal
    /// unhelpful: "you have edited everything Dwight put here" and "none of this is Dwight's" call for
    /// different responses from the user.
    /// </summary>
    public static string OccupiedMessage(string region, RebuildClaim claim)
    {
      var earlier = claim.PlacedEarlier.Count == 0 ? ""
        : $" {region} also holds {claim.PlacedEarlier.Count} component(s) placed earlier, which stay.";

      // THREE ANSWERS, NOT TWO. "Dwight did not place these" is a claim Dwight can only make when it has
      // a record for this region. With no record it cannot tell "I did not place these" from "I do not
      // remember placing these" - and after a Rhino restart the ledger IS empty, so a region Dwight
      // really did build would be accused of being someone else's. That is F1's false accusation
      // returning, moved from after-a-turn to after-a-restart.
      //
      // So the no-record sentence says what Dwight KNOWS rather than what happened, which is true in
      // both cases and accuses nobody.
      if (claim.Adopted.Count > 0 && claim.Theirs > 0)
        return $"{region} holds {claim.Adopted.Count} component(s) Dwight placed but you have since " +
               $"edited, and {claim.Theirs} it has no record of placing. Dwight removes only its own " +
               "work that you have left alone, so there is nothing here for it to clear. Nothing was " +
               "changed. Delete what you want gone and ask again, or build somewhere else." + earlier;

      if (claim.Adopted.Count > 0)
        return $"Dwight placed the {claim.Adopted.Count} component(s) in {region}, but you have edited " +
               "them since - so they are yours now and Dwight will not remove them. Nothing was changed. " +
               "Delete them yourself if you want the region rebuilt from scratch." + earlier;

      if (!claim.HasRecordForRegion)
        return $"Dwight has NO RECORD of placing the {claim.Theirs} component(s) in {region}, so it will " +
               "not remove them. That may be because it did not place them - or because it did, in a " +
               "session before Grasshopper was restarted: Dwight's record of what it built does not " +
               "survive a restart. Nothing was changed. Clear the region yourself if you want it rebuilt " +
               "from scratch." + earlier;

      return $"{region} holds {claim.Theirs} component(s) Dwight did not place, so it will not remove " +
             "them to make room. Nothing was changed. Clear the region yourself if you want it rebuilt, " +
             "or build somewhere else." + earlier;
    }

    /// <summary>The same three-way distinction the sentence makes, for a model that must relay it.</summary>
    public static string OccupiedCause(RebuildClaim claim) =>
      claim.Adopted.Count > 0
        ? "every component Dwight placed here has been edited since, so none of it is Dwight's to remove"
        : claim.PlacedEarlier.Count > 0
          ? "what Dwight placed here earlier stays, and the rest of this region is not Dwight's to remove"
          : claim.HasRecordForRegion
            ? "nothing in this region was placed by Dwight"
            : "Dwight has no record for this region - it may never have built here, or its record " +
              "did not survive a Grasshopper restart";
  }
}
