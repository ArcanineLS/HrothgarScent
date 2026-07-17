using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace HrothgarScent.Scent;

/// <summary>
/// The only place in the plugin that touches <see cref="IObjectTable"/>.
///
/// The object table asserts the main thread on every access and throws off it, and UiBuilder.Draw is not
/// the main thread. So the split is absolute: this class writes on the framework thread, the UI reads a
/// published snapshot, and the two never share a mutable object. Everything the UI needs is projected into
/// <see cref="ScentRow"/> here, while the access is still legal.
/// </summary>
public sealed class ScentScanner : IDisposable
{
  /// <summary>
  /// Floor on the rescan interval. A configured 0 would rebuild every row every frame, and below roughly
  /// 100ms there is nothing left to gain — the game only moves players once per frame either way.
  /// </summary>
  private const int MinRescanIntervalMs = 50;

  /// <summary>
  /// The game's real player slots. Dalamud's own enumerator already stops well short of this, so the guard
  /// is belt-and-braces against the object table's alias ranges ever reaching the player enumeration.
  /// </summary>
  private const ushort MaxPlayerObjectIndex = 240;

  /// <summary>The "no target" sentinel in the EntityId space, which the target id can surface as.</summary>
  private const ulong NoTargetSentinel = 0xE0000000;

  /// <summary>Shared empty map for <see cref="Reset"/>, so tearing down allocates nothing.</summary>
  private static readonly Dictionary<WatcherKey, StareState> NoWatchers = [];

  private readonly WatcherLog _log;
  private readonly AlertService _alerts;

  private ScentSnapshot _snapshot = ScentSnapshot.Empty;
  private long _version;
  private long _lastScanTicks;
  /// <summary>
  /// Who was watching as of the previous scan, and how long each has held it.
  ///
  /// A map rather than a set because the value is the episode's accrued time — see <see cref="StareState"/>.
  /// Presence still means exactly what it did (they were watching last scan, so they are not a fresh episode);
  /// the value simply rides along, carried forward in place by the edge pass instead of a set being rebuilt
  /// every tick. Framework-thread-owned, like everything else the scan mutates.
  /// </summary>
  /// <summary>
  /// The mark revision this scanner last told the nameplates about, so an edit fires exactly one redraw.
  /// </summary>
  /// <remarks>
  /// -1 rather than 0: MarksIndex.Empty starts at revision 0, so a 0 here would make the first scan of a session
  /// with no marks at all look like a change and ask the game to rebuild every plate for nothing.
  /// </remarks>
  private int _previousMarkRevision = -1;

  private Dictionary<WatcherKey, StareState> _previousWatchers = [];

  /// <summary>The territory <see cref="_zoneName"/> was resolved for. Framework-thread-owned; see
  /// <see cref="ZoneName"/>.</summary>
  private uint _zoneId;

  private string _zoneName = string.Empty;

  /// <summary>
  /// Every nearby identity as of the previous scan — not just the focused ones.
  ///
  /// The whole crowd, because "arrived" must mean "was not in range a moment ago". A set of only the focused
  /// would answer the wrong question: unfocusing a player standing in front of you and focusing them again
  /// would drop them out and put them back, and the next scan would call that an arrival and announce a player
  /// who never moved. Keyed on nearby-ness, the same edit is silent, which is correct — editing a list is not
  /// an arrival.
  ///
  /// Framework-thread-owned, exactly like <see cref="_previousWatchers"/>.
  /// </summary>
  private HashSet<WatcherKey> _previousNearby = [];

  /// <summary>
  /// Guards <see cref="_forgetKeys"/> and <see cref="_forgetAllKeys"/>, the queue by which the render thread
  /// asks for <see cref="_previousWatchers"/> to be re-armed.
  ///
  /// A queue rather than a direct edit because <see cref="_previousWatchers"/> is owned by the framework
  /// thread: the Forget and Clear buttons call straight through from Draw, and clearing a HashSet from under
  /// the Contains below is a torn read, not a missed update. So the render thread only ever writes here, and
  /// <see cref="ApplyPendingForgets"/> drains it at the top of the next scan, where the set is legal to
  /// touch. Uncontended and taken once per scan, so the cost is nil.
  /// </summary>
  private readonly object _forgetGate = new();

  private readonly HashSet<WatcherKey> _forgetKeys = [];
  private bool _forgetAllKeys;

  public ScentScanner(WatcherLog log, AlertService alerts)
  {
    _log = log;
    _alerts = alerts;
    Plugin.Framework.Update += OnFrameworkUpdate;
  }

  /// <summary>
  /// The latest published snapshot. Safe to read from any thread: the value is immutable, and the volatile
  /// read supplies the fence that stops the JIT hoisting it out of the draw loop.
  /// </summary>
  public ScentSnapshot Snapshot => Volatile.Read(ref _snapshot);

  public void Dispose()
  {
    Plugin.Framework.Update -= OnFrameworkUpdate;
  }

  /// <summary>
  /// Re-arms the edge detector for one player, so the next scan counts them as a new episode.
  ///
  /// Called by <see cref="WatcherLog.Remove"/>, and not optional. The edge state below is this class's
  /// private mirror, not a view of the log, so dropping a log entry leaves this still believing it already
  /// announced them — and <see cref="WatcherLog.Sync"/> deliberately never creates entries. Without this, a
  /// player who never looks away is never re-recorded: the history pane silently omits the one row it exists
  /// to show, for as long as they keep staring, while the main table's eye still flags them.
  ///
  /// Safe from any thread; see <see cref="_forgetGate"/>.
  /// </summary>
  public void ForgetWatcherEdge(WatcherKey key)
  {
    lock (_forgetGate)
      _forgetKeys.Add(key);
  }

  /// <summary>
  /// Re-arms the edge detector for everyone, so the next scan counts every live watcher as a new episode.
  /// Called by <see cref="WatcherLog.Clear"/>; see <see cref="ForgetWatcherEdge"/> for why. Safe from any
  /// thread.
  /// </summary>
  public void ForgetAllWatcherEdges()
  {
    lock (_forgetGate)
      _forgetAllKeys = true;
  }

  private void OnFrameworkUpdate(IFramework framework)
  {
    try
    {
      var now = Environment.TickCount64;
      if (now - _lastScanTicks < Math.Max(MinRescanIntervalMs, Plugin.Configuration.RescanIntervalMs))
        return;
      _lastScanTicks = now;

      // Competitive integrity, and a condition of Dalamud plugin acceptance: in PvP the data is not merely
      // hidden, it is never collected. Gate #1 of eight, kept separate from the login gate below so that no
      // single edit can collapse both.
      if (Plugin.ClientState.IsPvP)
      {
        Reset();
        return;
      }

      var me = Plugin.Objects.LocalPlayer;
      if (me is null || !Plugin.ClientState.IsLoggedIn)
      {
        Reset();
        return;
      }

      // now, not a fresh read inside Scan: the throttle above already decided this tick's instant, and the
      // stare accrual must measure against the same one or the gaps it banks disagree with the gaps the
      // throttle allowed.
      Scan(me, now);
    }
    catch (Exception ex)
    {
      // An escaping throw in a Framework.Update handler repeats every frame, which turns one bad scan into
      // a log flood and a stutter. Swallow it here, drop to the empty snapshot, and try again next tick.
      Plugin.Log.Error(ex, "Scan failed");
      Reset();
    }
  }

  private void Scan(IPlayerCharacter me, long now)
  {
    // Before anything reads the edge set, and on the thread that owns it.
    ApplyPendingForgets();

    var myPos = me.Position;
    var myGid = me.GameObjectId;
    var myFc = me.CompanyTag.TextValue;
    var myWorld = me.HomeWorld.RowId;

    var rows = new List<ScentRow>(64);
    var nearby = 0;

    foreach (var pc in Plugin.Objects.PlayerObjects.OfType<IPlayerCharacter>())
    {
      if (pc.ObjectKind != ObjectKind.Pc)
        continue;
      if (pc.ObjectIndex >= MaxPlayerObjectIndex)
        continue;

      var flags = pc.StatusFlags;
      var targetId = pc.TargetObjectId;
      var isSelf = pc.GameObjectId == myGid;
      var tag = pc.CompanyTag.TextValue;

      // Customize is a Span straight over the character's draw data, so reading it allocates nothing at all.
      // Its neighbour CustomizeData builds and boxes a fresh struct on every single access — a few hundred
      // boxes a second in a crowd, for two bytes. Do not "simplify" this back to CustomizeData; the two read
      // alike and only one of them is free.
      var customize = pc.Customize;
      var raceId = ReadCustomize(customize, CustomizeIndex.Race);
      var sex = ReadCustomize(customize, CustomizeIndex.Gender);

      // Same-FC needs the world too: FC tags are not unique across worlds, so tag-only matching would paint
      // a same-named FC from another world as your own.
      var sameFc = !isSelf
                && !string.IsNullOrEmpty(tag)
                && tag == myFc
                && pc.HomeWorld.RowId == myWorld;

      // ValueNullable, never .Value: .Value throws on a row that has not loaded, and a missing job name is
      // not worth taking the whole scan down for.
      rows.Add(new ScentRow(
        GameObjectId: pc.GameObjectId,
        EntityId: pc.EntityId,
        ObjectIndex: pc.ObjectIndex,
        Name: pc.Name.TextValue,
        HomeWorldId: pc.HomeWorld.RowId,
        HomeWorldName: pc.HomeWorld.ValueNullable?.Name.ExtractText() ?? string.Empty,
        JobId: pc.ClassJob.RowId,
        JobAbbreviation: pc.ClassJob.ValueNullable?.Abbreviation.ExtractText() ?? "???",
        JobName: pc.ClassJob.ValueNullable?.Name.ExtractText() ?? "Unknown",
        Level: pc.Level,
        RaceId: raceId,
        RaceName: RacePalette.NameOf(raceId, sex),
        Sex: sex,
        CompanyTag: tag,
        OnlineStatusId: pc.OnlineStatus.RowId,
        Distance: Vector3.Distance(pc.Position, myPos),
        Position: pc.Position,
        IsWatching: IsWatching(targetId, myGid),
        IsFriend: (flags & StatusFlags.Friend) != 0,
        IsParty: (flags & StatusFlags.PartyMember) != 0,
        IsAlliance: (flags & StatusFlags.AllianceMember) != 0,
        IsInCombat: (flags & StatusFlags.InCombat) != 0,
        IsDead: pc.IsDead,
        IsSelf: isSelf,
        IsSameFreeCompany: sameFc));

      if (!isSelf)
        nearby++;
    }

    // Edge-detect per identity, not on the count of watchers. A count only rises when the crowd grows, so
    // one watcher dropping as another appears in the same scan nets to zero and announces nobody — which is
    // exactly the arrival worth announcing.
    var current = new Dictionary<WatcherKey, StareState>();
    var nearbyKeys = new HashSet<WatcherKey>(rows.Count);
    List<ScentRow>? fresh = null;
    List<ScentRow>? arrived = null;
    List<(ScentRow Row, StareLevel Level, long HeldMs, StareState State)>? escalated = null;
    var config = Plugin.Configuration;

    // One read of the published index for the whole loop; the render thread republishes it whenever the user
    // edits a mark, and testing against whichever whole version we started with is exactly right. The lookup
    // per row is O(1) against the frozen dictionary, where the list it replaced was a linear scan per row.
    var marks = Plugin.Marks.Index;
    var wantFocusAlerts = config.AlertOnFocusArrival && config.EnableNearbyList && marks.Entries.Count > 0;

    // Both hoisted out of the loop rather than read per row. The wall clock because every player seen in one
    // scan was seen at one moment, and DateTimeOffset.Now per row would say otherwise; the zone name because
    // it is one string for the whole scan by definition.
    //
    // DateTimeOffset, not the TickCount64 the stare accrual uses two lines up: that one counts milliseconds
    // since the machine booted, which is the right clock for measuring a gap and useless for anything written
    // to a file — it would read as nonsense the moment the machine restarts. And an offset rather than a bare
    // DateTime, so a record written in one time zone still means what it said in another.
    var wantLastSeen = config.RememberLastSeen && marks.Entries.Count > 0;
    var seenAt = wantLastSeen ? DateTimeOffset.Now : default;
    var zoneName = wantLastSeen ? ZoneName() : string.Empty;

    // The two facts the info bar needs but cannot afford to work out for itself. See the loop below.
    var markedNearby = false;
    var maxStare = StareLevel.Glance;

    foreach (var row in rows)
    {
      if (row.IsSelf)
        continue;

      nearbyKeys.Add(row.Key);

      // Nearby, not watching: this asks "did I run into them", which is a question about being in the same
      // place. Cheap for everyone who is not marked — one frozen-dictionary probe that answers no — and it
      // cannot create a record for anyone. See MarkStore.RecordSeen.
      if (wantLastSeen)
        Plugin.Marks.RecordSeen(row.Key, zoneName, seenAt);

      // Resolved HERE, in the loop that already walks every row, and published on the snapshot — never worked
      // out by the info bar. UpdateDtr runs on EVERY frame, unthrottled, and ahead of its own change guard, so
      // an O(rows x marks) probe there would be paid sixty times a second and the guard could not amortise a
      // penny of it. This loop is already paying for the walk.
      //
      // NOT ignored, and this is the ignore promise rather than a nicety: an ignored player IS a marked one, so
      // a plain "is there a mark" test would put "one you marked is here" on the info bar about the very person
      // the user said to never show or announce again. Ignore beats focus here as everywhere.
      //
      // HasVisibleMark, not mere existence: a record can hold only a colour, which draws nothing and sorts with
      // the unmarked — so counting it here would have the info bar assert a mark the list denies, on the one
      // surface the user cannot cross-check because the window is shut. One predicate, three readers.
      markedNearby |= marks.Find(row.Key) is { IsIgnored: false, HasVisibleMark: true };

      if (row.IsWatching)
      {
        // Carried forward in place, or begun. Presence in the previous map is still exactly the old edge test —
        // absent means this is a new episode — and the value that rides along is what turns "someone looked at
        // you" into "someone has been looking at you for thirty seconds".
        if (_previousWatchers.TryGetValue(row.Key, out var state))
        {
          // Accrued from the gap between scans, never from now - FirstTicks: the scanner does not run while
          // zoning, logged out or in PvP, so wall clock would bank a loading screen as staring.
          state.DeltaMs = now - state.LastTicks;
          state.LastTicks = now;
          state.AccumulatedMs += state.DeltaMs;

          // Monotonic: a rung climbs once per episode and only upward. That one byte IS the dedup, and it is
          // why no throttle table is needed to keep a long stare from repeating itself.
          //
          // The rung is NOT spent here, and that is the whole subtlety. Committing it at the moment it is
          // detected would burn it even when the alert never got said — a cooldown drop would silence that rung
          // for the rest of the episode, permanently, because the level never climbs to it again. So the state
          // rides along and AlertService spends it only once it knows the outcome. See NotifyStareEscalations.
          var level = config.StareLevelOf(state.AccumulatedMs);
          if (level > state.Level)
            (escalated ??= []).Add((row, level, state.AccumulatedMs, state));
        }
        else
        {
          state = new StareState { FirstTicks = now, LastTicks = now };
          (fresh ??= []).Add(row);
        }

        // The worst rung anyone has reached RIGHT NOW, for the info bar. Off the live threshold rather than
        // state.Level: Level is a record of what has been ANNOUNCED, so a rung waiting on the cooldown would
        // leave the bar reporting a calm it cannot see. The bar is a readout, not an alert — it owes the truth
        // now, not the truth that was spoken.
        //
        // Ignored watchers are not counted. "One of them fixed on you" is a sentence about a person, and the
        // ignore promise covers sentences: the alerts drop them, the table drops them, and the bar's marked
        // glyph drops them, so this drops them too. The bare COUNT still includes them — that is older
        // behaviour, shared with the eye column, and not this feature's to change.
        if (!marks.IsIgnored(row.Key))
        {
          var reached = config.StareLevelOf(state.AccumulatedMs);
          if (reached > maxStare)
            maxStare = reached;
        }

        current[row.Key] = state;
      }

      // Arrival, not focus-change: the test is against who was NEARBY last scan, so re-focusing someone already
      // standing there stays quiet. See _previousNearby.
      if (wantFocusAlerts && !_previousNearby.Contains(row.Key) && marks.IsFocused(row.Key))
        (arrived ??= []).Add(row);
    }

    _log.Sync(rows, current);

    // RecordWhileClosed is honoured here rather than inside WatcherLog: the scan itself must keep running even
    // with the window closed, because the info bar count comes from it. Only the remembering is decided here —
    // the announcing is AlertService's, where every other alert gate lives, and it applies the same setting.
    //
    // The log records regardless of EnableWatchers, because that toggle is UI only and switching the half back
    // on has to bring its history with it.
    if (fresh is not null && (config.RecordWhileClosed || Plugin.IsMainWindowOpen))
      foreach (var row in fresh)
        _log.RecordSighting(row);

    // Offered, not announced, and the ORDER HERE MEANS NOTHING — which is the entire point of the bus. It used
    // to be the priority rule: three Notify calls in a fixed sequence, first one to speak took the cooldown,
    // and the losers vanished with no trace. Priority now lives in SignalClass, where it can be read, and Pump
    // decides once with all three in hand.
    if (escalated is not null)
      _alerts.RaiseStareEscalations(escalated);

    if (fresh is not null)
      _alerts.RaiseNewWatchers(fresh);

    if (arrived is not null)
      _alerts.RaiseFocusArrivals(arrived);

    // One decision per scan, with this scan's world handed to it: a signal raised a moment ago is re-checked
    // against who is still watching and who is still here before it is allowed to speak.
    _alerts.Pump(current, nearbyKeys);

    // BEFORE _previousWatchers is replaced, while both sides of the edge are still in hand, and keyed on the
    // whole SET rather than on `fresh`: fresh only ever holds arrivals, and a DEPARTURE is the case that
    // matters — nothing else would ever tell the plates to drop the colour off someone who looked away. The
    // game never dirties a plate because its owner changed target, so without this the eye never moves.
    //
    // Gated on the SAME conditions the handler checks, because RequestRedraw is a game call and asking the
    // game to rebuild every plate four times a second, to paint nothing, is rude to everyone else drawing on
    // them. EnableWatchers included: without it, a user with the watcher half off but the nameplate mode on
    // would force a full rebuild on every target change in a crowd, for a handler that returns on its second
    // line. Turning the half back on needs no redraw from here — NameplateService.Sync re-attaches and its
    // Subscribe fires one itself.
    if (config.NameplateMode != NameplateMode.Off && config.EnableWatchers
        && !SameWatchers(current, _previousWatchers))
      Plugin.Nameplates.Redraw();

    // The mark half's own trigger, on the same terms and for the same reason: the game will not dirty a plate
    // because the user picked a colour, so without this the feature is inert — you choose cyan in the profile
    // and the plate keeps whatever it had until something unrelated forces a rebuild.
    //
    // The REVISION, not the set. The watcher test above compares who is watching, because that is what changes
    // it; here the set can be identical while the answer is completely different — recolouring or unfocusing
    // someone already standing there changes no membership at all. MarksIndex is published whole on every edit
    // and carries its own revision, so this notices any of them without the scanner learning what a mark is.
    //
    // Its own statement rather than a clause on the one above, because the two are independently switchable and
    // an && between them would let either half silently disable the other's redraw.
    var markRevision = Plugin.Marks.Index.Revision;
    if (config.NameplateMode != NameplateMode.Off && config.NameplateMarkColors && config.EnableNearbyList
        && markRevision != _previousMarkRevision)
      Plugin.Nameplates.Redraw();

    // Updated unconditionally, like _previousWatchers below and for the same reason: tracking it only while the
    // feature is on would make the first scan after switching it back on compare against a revision from
    // whenever it was switched off, and fire a redraw that Sync's own Subscribe has already fired.
    _previousMarkRevision = markRevision;

    // Updated unconditionally, including when we chose not to record: otherwise every watcher already
    // present would re-fire the moment the window opened.
    _previousWatchers = current;
    _previousNearby = nearbyKeys;

    // ToArray freezes the projection — the List must not escape, or a later scan could grow it under a
    // reader mid-draw. One reference write publishes the whole thing; reference assignment is atomic, so no
    // lock is needed and no reader can observe a partial snapshot.
    // INDEXER ASSIGNMENT, never ToDictionary or ToFrozenDictionary — both THROW on a duplicate key, and a throw
    // here is caught by OnFrameworkUpdate, which answers it with Reset(). One duplicated object id would
    // therefore empty the list on every scan, forever: the whole plugin switched off, silently, by a decoration.
    // Today a duplicate costs one redundant table row. Last-wins is the same answer the table already gives.
    // MaxPlayerObjectIndex is the same belt-and-braces instinct, one layer up.
    //
    // A plain Dictionary rather than a frozen one: this is rebuilt four times a second, and FrozenDictionary
    // trades expensive construction for fast reads — the wrong side of that trade at this lifetime.
    var byId = new Dictionary<ulong, ScentRow>(rows.Count);
    foreach (var row in rows)
      byId[row.GameObjectId] = row;

    Volatile.Write(ref _snapshot, new ScentSnapshot(
      Version: ++_version,
      Rows: rows.ToArray(),
      ById: byId,
      NearbyCount: nearby,
      WatcherCount: current.Count,
      Valid: true,
      MaxStareLevel: maxStare,
      MarkedNearby: markedNearby));
  }

  /// <summary>
  /// Drains the forget queue into <see cref="_previousWatchers"/>.
  ///
  /// A re-armed key is re-recorded AND re-alerted, and the pairing is deliberate: the log will call it
  /// episode one with a fresh FirstSeen, so an alert that stayed quiet would leave the two disagreeing about
  /// the same event. Only the user's own Forget and Clear ever queue here — never <see cref="WatcherLog"/>'s
  /// automatic trim — so there is no loop for the alert cooldown to lose.
  ///
  /// Watcher edges only. The focus-arrival set is keyed on who was nearby rather than on who is focused, and
  /// nothing in the UI can make a player un-arrive, so there is nothing there to re-arm.
  /// </summary>
  private void ApplyPendingForgets()
  {
    lock (_forgetGate)
    {
      if (_forgetAllKeys)
        _previousWatchers.Clear();
      else
        foreach (var key in _forgetKeys)
          _previousWatchers.Remove(key);

      _forgetAllKeys = false;
      _forgetKeys.Clear();
    }
  }

  /// <summary>
  /// Whether a target id refers to us.
  ///
  /// Both ids live in the GameObjectId space — TargetObjectId is not an EntityId, despite the name, and
  /// comparing it against one silently never matches.
  ///
  /// <see cref="NoTargetSentinel"/> is confirmed, not guessed: Dalamud's own NamePlateUpdateHandler treats
  /// exactly 0xE0000000 as the invalid-object id, skipping its lookup for a nameplate whose ObjectId is that
  /// value. The 0 guard stays alongside it as belt-and-braces; neither can cause a false positive, because an
  /// exact match against our own id is still required.
  /// </summary>
  private static bool IsWatching(ulong targetId, ulong myGameObjectId)
    => targetId != 0 && targetId != NoTargetSentinel && targetId == myGameObjectId;

  /// <summary>
  /// Whether the same people are watching, ignoring how long each has been at it.
  ///
  /// Equal counts plus one-way containment IS set equality, which is why one loop suffices. Written out rather
  /// than reached for via SetEquals so that nothing allocates: this runs on every scan for as long as anyone is
  /// looking at you, and the answer is almost always "yes, nothing changed".
  /// </summary>
  private static bool SameWatchers(Dictionary<WatcherKey, StareState> a, Dictionary<WatcherKey, StareState> b)
  {
    if (a.Count != b.Count)
      return false;

    foreach (var key in a.Keys)
    {
      if (!b.ContainsKey(key))
        return false;
    }

    return true;
  }

  /// <summary>
  /// The current zone's name, resolved once per zone rather than once per scan.
  ///
  /// CACHED, AND THE CACHING IS NOT ABOUT SPEED. GetExcelSheet itself throws — on a missing sheet, an
  /// unsupported language, or a column-hash mismatch after a patch — and this is called from inside
  /// <see cref="Scan"/>, whose handler answers a throw by dropping to the empty snapshot. Resolved per scan and
  /// unguarded, one bad patch day would empty the nearby list on every tick, forever, over a cosmetic place
  /// name. That is the trap RacePalette documents at length and guards the same way.
  ///
  /// So: try/catch, resolve only when the territory id actually moves, and fall back to empty. An empty zone
  /// name is a real state the store handles, not a failure — see <see cref="MarkedPlayer.LastSeenZone"/>.
  /// </summary>
  private string ZoneName()
  {
    var id = Plugin.ClientState.TerritoryType;
    if (id == _zoneId)
      return _zoneName;

    _zoneId = id;
    _zoneName = ResolveZoneName(id);
    return _zoneName;
  }

  private static string ResolveZoneName(uint id)
  {
    try
    {
      // GetRowOrDefault, never GetRow: GetRow throws on a row the sheet does not have. ValueNullable, never
      // .Value, for the same reason the scan's world and job reads use it — a row that has not loaded is not an
      // exception, it is a Tuesday.
      return Plugin.DataManager.GetExcelSheet<TerritoryType>()
        .GetRowOrDefault(id)?.PlaceName.ValueNullable?.Name.ExtractText() ?? string.Empty;
    }
    catch (Exception ex)
    {
      Plugin.Log.Warning(ex, "Could not resolve the name of territory {Id}", id);
      return string.Empty;
    }
  }

  /// <summary>
  /// One byte of a character's appearance, addressed by name rather than by offset.
  ///
  /// The bounds check is not defensive style. Customize answers a short or empty span for a character whose
  /// draw data the client has not finished loading, and an unguarded index would throw inside
  /// <see cref="Scan"/> — which <see cref="OnFrameworkUpdate"/> answers by resetting to the empty snapshot.
  /// So one passer-by whose appearance is a frame late would blank the entire list, every scan, for as long
  /// as they stood there.
  ///
  /// 0 rather than a guess, and rather than skipping the row: race 0 is the not-yet-loaded sentinel the rest
  /// of the plugin already expects and deliberately never hides (see <see cref="Configuration.IsRaceHidden"/>).
  /// Dropping the player instead would make someone who is merely still loading vanish from the list entirely,
  /// which is a worse answer than "race unknown" and a change in behaviour rather than a fix.
  /// </summary>
  private static byte ReadCustomize(ReadOnlySpan<byte> customize, CustomizeIndex index)
    => (int)index < customize.Length ? customize[(int)index] : (byte)0;

  /// <summary>
  /// Publishes the empty snapshot and forgets who was watching, for the states where nearby players are
  /// either unknowable or none of our business: logged out, mid-zone, or in PvP.
  ///
  /// Forgetting matters. The object table is rebuilt from scratch across a zone, so carrying the old zone's
  /// watchers forward would suppress the alert for someone standing in the new zone staring at you on
  /// arrival — precisely the moment worth knowing about. The nearby set is cleared for the same reason and
  /// with the same consequence, deliberately: a focus-list player standing in the zone you just arrived in HAS
  /// arrived, from your side of it, and is worth the line. The focus list is small and the alert cooldown
  /// bounds the burst, so the honest answer costs at most one line per zone. Marking the log's entries stale
  /// matters for the same reason: a history that still claims someone is watching you, in a zone you left, is
  /// just wrong.
  ///
  /// Idempotent, and cheaply so: this runs on every throttle tick for as long as the player stays logged
  /// out. ReferenceEquals, not ==, because ScentSnapshot is a record and == is value equality — which every
  /// empty snapshot would satisfy, making the guard fire on real resets it should not skip.
  /// </summary>
  private void Reset()
  {
    // ABOVE the guard, and that placement is the whole point. A signal waiting on the bus describes people in a
    // world that has just ended, and the journal describes decisions about them; nothing raised before a PvP
    // boundary may survive it, and the config window — which draws in PvP, banner and all — is where those
    // entries would be read.
    //
    // Behind the guard that promise had a hole exactly the shape of a scan that throws. Scan journals near its
    // end but publishes its snapshot LAST, so a throw in between leaves entries written and _snapshot untouched
    // — and if _snapshot was already Empty by reference, every Reset from then on short-circuits before
    // reaching this line and the entries live forever. The guard's real subjects are the log Sync, the snapshot
    // write and the Redraw; the journal has no business behind an identity check on an unrelated object.
    //
    // Free to hoist, which is why there is no reason not to: both dictionaries clear trivially when empty and
    // SignalJournal.Clear self-guards on a count, so the repeated logged-out ticks the guard exists to protect
    // pay nothing for this.
    _alerts.ClearPending();

    if (ReferenceEquals(Volatile.Read(ref _snapshot), ScentSnapshot.Empty))
      return;

    _previousWatchers.Clear();
    _previousNearby.Clear();

    _log.Sync([], NoWatchers);
    Volatile.Write(ref _snapshot, ScentSnapshot.Empty);

    // INSIDE the idempotence guard above, which is the whole reason this is safe to put here: Reset runs on
    // every throttle tick for as long as the player stays logged out, and an unguarded redraw would ask the
    // game to rebuild every nameplate four times a second, forever, for nothing. Past the guard it fires once,
    // on the transition — which is exactly when the plates are still wearing the last zone's colours.
    if (Plugin.Configuration.NameplateMode != NameplateMode.Off)
      Plugin.Nameplates.Redraw();
  }
}
