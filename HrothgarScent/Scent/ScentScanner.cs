using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;

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

  /// <summary>Shared empty set for <see cref="Reset"/>, so tearing down allocates nothing.</summary>
  private static readonly HashSet<WatcherKey> NoWatchers = [];

  private readonly WatcherLog _log;
  private readonly AlertService _alerts;

  private ScentSnapshot _snapshot = ScentSnapshot.Empty;
  private long _version;
  private long _lastScanTicks;
  private HashSet<WatcherKey> _previousWatchers = [];

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
      // hidden, it is never collected. Gate #1 of four, kept separate from the login gate below so that no
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

      Scan(me);
    }
    catch (Exception ex)
    {
      // An escaping throw in a Framework.Update handler repeats every frame, which turns one bad scan into
      // a log flood and a stutter. Swallow it here, drop to the empty snapshot, and try again next tick.
      Plugin.Log.Error(ex, "Scan failed");
      Reset();
    }
  }

  private void Scan(IPlayerCharacter me)
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

      // Read once and reuse. The property builds a fresh CustomizeData over the character's draw data and
      // boxes it on every single access, so reaching through it twice would allocate twice per player per
      // scan — a few hundred boxes a second in a crowd, for two bytes.
      var customize = pc.CustomizeData;
      var raceId = customize.Race;
      var sex = customize.Sex;

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
    var current = new HashSet<WatcherKey>();
    var nearbyKeys = new HashSet<WatcherKey>(rows.Count);
    List<ScentRow>? fresh = null;
    List<ScentRow>? arrived = null;
    var config = Plugin.Configuration;

    // One read of the copy-on-write reference for the whole loop; the render thread's Focus item can swap the
    // list out from under it, and testing against whichever whole version we started with is exactly right.
    var focusedPlayers = config.FocusedPlayers;
    var wantFocusAlerts = config.AlertOnFocusArrival && config.EnableNearbyList && focusedPlayers.Count > 0;

    foreach (var row in rows)
    {
      if (row.IsSelf)
        continue;

      nearbyKeys.Add(row.Key);

      if (row.IsWatching)
      {
        current.Add(row.Key);
        if (!_previousWatchers.Contains(row.Key))
          (fresh ??= []).Add(row);
      }

      // Arrival, not focus-change: the test is against who was NEARBY last scan, so re-focusing someone already
      // standing there stays quiet. See _previousNearby.
      if (wantFocusAlerts && !_previousNearby.Contains(row.Key) && IsFocused(focusedPlayers, row))
        (arrived ??= []).Add(row);
    }

    _log.Sync(rows, current);

    // RecordWhileClosed is honoured here rather than inside WatcherLog: the scan itself must keep running
    // even with the window closed, because the info bar count comes from it. Only the remembering and the
    // announcing are suppressed.
    if (fresh is not null && (Plugin.Configuration.RecordWhileClosed || Plugin.IsMainWindowOpen))
    {
      foreach (var row in fresh)
        _log.RecordSighting(row);

      // The log records regardless of EnableWatchers — the toggle is UI only, so switching the half back on has
      // to bring its history with it. Only the announcement is the half's output, so only the announcement is
      // suppressed, and it is suppressed inside AlertService where every other alert gate already lives.
      _alerts.NotifyNewWatchers(fresh);
    }

    // Watchers first, and always: the two share one cooldown, so whichever is announced first wins a tie. A
    // stranger staring at you is the fact this plugin exists for; a friend walking into view is not.
    if (arrived is not null)
      _alerts.NotifyFocusArrivals(arrived);

    // Updated unconditionally, including when we chose not to record: otherwise every watcher already
    // present would re-fire the moment the window opened.
    _previousWatchers = current;
    _previousNearby = nearbyKeys;

    // ToArray freezes the projection — the List must not escape, or a later scan could grow it under a
    // reader mid-draw. One reference write publishes the whole thing; reference assignment is atomic, so no
    // lock is needed and no reader can observe a partial snapshot.
    Volatile.Write(ref _snapshot, new ScentSnapshot(
      Version: ++_version,
      Rows: rows.ToArray(),
      NearbyCount: nearby,
      WatcherCount: current.Count,
      Valid: true));
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
  /// comparing it against one silently never matches. The sentinel guards are belt-and-braces: which value
  /// the game writes for "no target" could not be settled from the assemblies, but neither guard can cause
  /// a false positive, because an exact match against our own id is still required.
  /// </summary>
  private static bool IsWatching(ulong targetId, ulong myGameObjectId)
    => targetId != 0 && targetId != NoTargetSentinel && targetId == myGameObjectId;

  /// <summary>
  /// Whether a scanned row is on the focus list. Takes the caller's already-read list reference rather than
  /// reading Configuration again: the list is copy-on-write, and two reads inside one scan are two chances to
  /// answer from two different versions of it.
  /// </summary>
  private static bool IsFocused(List<FocusedPlayer> focused, ScentRow row)
  {
    foreach (var entry in focused)
    {
      if (entry.Matches(row))
        return true;
    }

    return false;
  }

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
    if (ReferenceEquals(Volatile.Read(ref _snapshot), ScentSnapshot.Empty))
      return;

    _previousWatchers.Clear();
    _previousNearby.Clear();
    _log.Sync([], NoWatchers);
    Volatile.Write(ref _snapshot, ScentSnapshot.Empty);
  }
}
