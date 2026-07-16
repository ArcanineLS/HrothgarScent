namespace HrothgarScent.Scent;

/// <summary>
/// How hard someone is looking at you.
///
/// Ordered, and compared as such — an episode only ever climbs, so <see cref="StareState.Level"/> is a
/// one-byte guard that makes each rung announce exactly once. That guard is the whole dedup: no cooldown, no
/// timestamp bookkeeping, no per-alert throttle table. The prior art's equivalent is a hardcoded four-hour
/// window per player, and its issue tracker is the case against that.
/// </summary>
public enum StareLevel : byte
{
  /// <summary>They have you targeted. Might be cycling targets, might be about to talk to you.</summary>
  Glance = 0,

  /// <summary>Long enough that it was not an accident.</summary>
  Stare = 1,

  /// <summary>Long enough to be the reason this plugin exists.</summary>
  Fixation = 2,
}

/// <summary>
/// One targeting episode's accrued time, owned by <see cref="ScentScanner"/> and mutated only on the framework
/// thread.
///
/// Mutable and by-reference on purpose: it is carried forward across scans by the edge pass, which finds it
/// with one TryGetValue and updates it in place rather than rebuilding a set every tick. Nothing here is ever
/// handed to the render thread — the UI reads <see cref="WatcherEntry.TotalStareMs"/> off the published clones
/// instead, which is why this can be a plain class with no discipline attached.
///
/// EPISODE-SCOPED, all of it. A player who looks away and back starts a fresh instance, so
/// <see cref="AccumulatedMs"/> resets and they can be announced again — which is correct, because that is a new
/// event. The cumulative-across-episodes figure is <see cref="WatcherEntry.TotalStareMs"/>, and the two must
/// never be conflated: one drives the escalation, the other is a fact about a person.
/// </summary>
public sealed class StareState
{
  /// <summary>When this episode began. Never revised — that is the point of a first sighting.</summary>
  public long FirstTicks { get; init; }

  /// <summary>The scan that last saw them looking. The gap to the next one is <see cref="DeltaMs"/>.</summary>
  public long LastTicks { get; set; }

  /// <summary>
  /// Time held on you in THIS episode.
  ///
  /// Accrued from the gaps between scans rather than from <c>now - FirstTicks</c>, and the difference is
  /// load-bearing: the scanner does not run while zoning, logged out, or in PvP, so wall-clock since the first
  /// sighting would count minutes on a loading screen as staring.
  /// </summary>
  public long AccumulatedMs { get; set; }

  /// <summary>What this scan added. Read once, by WatcherLog.Sync, to accrue the lifetime figure.</summary>
  public long DeltaMs { get; set; }

  /// <summary>The highest rung announced for this episode. Monotonic — see <see cref="StareLevel"/>.</summary>
  public StareLevel Level { get; set; }
}
