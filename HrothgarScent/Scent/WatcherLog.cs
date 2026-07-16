using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace HrothgarScent.Scent;

/// <summary>
/// One remembered watcher.
///
/// Mutable, and the live instances are owned by the framework thread. The UI never sees those: it reads
/// frozen clones handed out by <see cref="WatcherLog.Snapshot"/>.
/// </summary>
public sealed class WatcherEntry
{
  public WatcherKey Key { get; init; }
  public string Name { get; init; } = string.Empty;
  public string WorldName { get; init; } = string.Empty;
  public uint JobId { get; set; }
  public string JobAbbreviation { get; set; } = "???";
  public byte Level { get; set; }

  /// <summary>When this player first targeted us. Never revised — that is the point of a first sighting.</summary>
  public DateTime FirstSeen { get; init; }

  /// <summary>The last time they were observed <em>targeting us</em>, not the last time they were nearby.</summary>
  public DateTime LastSeen { get; set; }

  /// <summary>Separate targeting episodes, not scans. Re-targeting inside one episode does not count twice.</summary>
  public int Count { get; set; }

  /// <summary>Still targeting us as of the most recent scan.</summary>
  public bool IsCurrent { get; set; }
}

/// <summary>
/// The watcher history: who has targeted you, how often, and when. Survives the player walking away, which
/// is the whole reason it exists separately from the snapshot.
///
/// Deliberately in-memory only and never written to the config. A durable, ever-growing record of every
/// stranger who ever glanced at you is both privacy-hostile and unbounded config growth; it dies with the
/// session, and <see cref="Clear"/> runs on logout.
/// </summary>
public sealed class WatcherLog
{
  /// <summary>
  /// Guards <see cref="_entries"/>. The dictionary is mutated by the framework thread (Sync, RecordSighting)
  /// and by the render thread (the Forget and Clear buttons call straight through from Draw), and
  /// Dictionary does not tolerate concurrent mutation — a torn resize corrupts it or throws. Uncontended at
  /// a handful of scans per second, so the cost is nil and the alternative is a rare, unreproducible crash.
  ///
  /// Reads do not take it: <see cref="Snapshot"/> serves the published list, which is never mutated.
  /// </summary>
  private readonly object _gate = new();

  private readonly Dictionary<WatcherKey, WatcherEntry> _entries = [];

  private IReadOnlyList<WatcherEntry> _published = [];

  /// <summary>
  /// Marks who is still watching and refreshes their live data. Called on every scan, from the framework
  /// thread. Never creates entries — a player with no history who starts watching arrives via
  /// <see cref="RecordSighting"/>, which is what distinguishes a new episode from an ongoing one.
  /// </summary>
  public void Sync(IReadOnlyList<ScentRow> rows, IReadOnlySet<WatcherKey> currentWatchers)
  {
    lock (_gate)
    {
      var dirty = false;
      var now = DateTime.Now;

      foreach (var entry in _entries.Values)
      {
        var current = currentWatchers.Contains(entry.Key);
        if (entry.IsCurrent != current)
        {
          entry.IsCurrent = current;
          dirty = true;
        }

        // Only a live watcher advances LastSeen. Refreshing it for anyone merely standing nearby would make
        // the history's "when" column report the last time we saw the player at all, rather than the last
        // time they were looking at us — the only thing this log is about.
        if (current)
        {
          entry.LastSeen = now;
          dirty = true;
        }
      }

      // Job and level are display-only, but a watcher who switches job mid-stare would otherwise stay listed
      // under whatever they were wearing the first time they looked.
      foreach (var row in rows)
      {
        if (!_entries.TryGetValue(row.Key, out var entry))
          continue;
        if (entry.JobId == row.JobId && entry.Level == row.Level)
          continue;
        entry.JobId = row.JobId;
        entry.JobAbbreviation = row.JobAbbreviation;
        entry.Level = row.Level;
        dirty = true;
      }

      if (!Plugin.Configuration.KeepHistory)
        dirty |= DropNonCurrent();

      dirty |= Trim();

      if (dirty)
        Publish();
    }
  }

  /// <summary>
  /// Records that a player who was not watching on the previous scan now is.
  ///
  /// A re-target moves the existing entry rather than adding a second one: identity is Name+HomeWorld, so
  /// the same player across two visits, two zones, or two sessions is one row with a count, not a wall of
  /// near-duplicates that buries everyone else.
  /// </summary>
  public void RecordSighting(ScentRow row)
  {
    lock (_gate)
    {
      var now = DateTime.Now;
      if (_entries.TryGetValue(row.Key, out var entry))
      {
        entry.Count++;
        entry.LastSeen = now;
        entry.IsCurrent = true;
        entry.JobId = row.JobId;
        entry.JobAbbreviation = row.JobAbbreviation;
        entry.Level = row.Level;
      }
      else
      {
        _entries[row.Key] = new WatcherEntry
        {
          Key = row.Key,
          Name = row.Name,
          WorldName = row.HomeWorldName,
          JobId = row.JobId,
          JobAbbreviation = row.JobAbbreviation,
          Level = row.Level,
          FirstSeen = now,
          LastSeen = now,
          Count = 1,
          IsCurrent = true,
        };
      }

      Trim();
      Publish();
    }
  }

  /// <summary>
  /// An immutable copy for the render thread, under the same discipline as <see cref="ScentSnapshot"/>: one
  /// volatile read of a list that nothing will ever mutate again.
  /// </summary>
  public IReadOnlyList<WatcherEntry> Snapshot() => Volatile.Read(ref _published);

  /// <summary>
  /// Forgets everyone, and tells the scanner to forget it ever announced them.
  ///
  /// The second half is not optional. Edge detection reads the scanner's own set, not these entries, so
  /// emptying the dictionary alone leaves it convinced it has already announced everyone in it — and
  /// <see cref="Sync"/> never creates entries. A player who does not look away would then never be
  /// re-recorded, and this pane would sit on its empty state while the main table's eye still flagged them.
  /// </summary>
  public void Clear()
  {
    lock (_gate)
    {
      if (_entries.Count > 0)
      {
        _entries.Clear();
        Publish();
      }
    }

    // Outside the gate — nothing is called back into under it — and after the entries are gone: re-arming
    // first would let a scan slip in and record a player a moment before Clear wiped the entry it wrote.
    Plugin.Scanner.ForgetAllWatcherEdges();
  }

  /// <summary>Forgets one player. Re-arms them for the same reason <see cref="Clear"/> re-arms everyone.</summary>
  public void Remove(WatcherKey key)
  {
    lock (_gate)
    {
      if (!_entries.Remove(key))
        return;
      Publish();
    }

    // This key alone, never the whole set: re-arming watchers whose entries still stand would bump their
    // Count on the next scan and announce a second episode that never happened.
    Plugin.Scanner.ForgetWatcherEdge(key);
  }

  /// <summary>Drops everyone who is not watching right now, for when history is switched off entirely.</summary>
  private bool DropNonCurrent()
  {
    List<WatcherKey>? doomed = null;
    foreach (var entry in _entries.Values)
      if (!entry.IsCurrent)
        (doomed ??= []).Add(entry.Key);

    if (doomed is null)
      return false;

    foreach (var key in doomed)
      _entries.Remove(key);
    return true;
  }

  /// <summary>
  /// Evicts the least-recently-seen entries down to the configured limit, considering only entries that are
  /// not currently watching.
  ///
  /// A live watcher must never be evicted, because nothing would bring them back: eviction is silent, and
  /// only a deliberate <see cref="Clear"/> or <see cref="Remove"/> re-arms the scanner's edge detection.
  /// <see cref="Sync"/> never creates entries, so an evicted watcher who does not look away would simply be
  /// missing from the pane for as long as they kept staring. Deliberately NOT wired to
  /// <see cref="ScentScanner.ForgetWatcherEdge"/> as the manual paths are: an automatic eviction that
  /// re-armed its own trigger would evict, re-record, exceed the limit, and evict again, and the alert
  /// cooldown cannot save you from a loop that recreates its own trigger.
  ///
  /// So the limit can be exceeded, briefly, by a crowd that is all staring at once; that is the correct trade.
  /// </summary>
  private bool Trim()
  {
    var limit = Math.Max(1, Plugin.Configuration.HistoryLimit);
    if (_entries.Count <= limit)
      return false;

    var evictable = _entries.Values.Where(e => !e.IsCurrent).ToList();
    var take = Math.Min(_entries.Count - limit, evictable.Count);
    if (take <= 0)
      return false;

    foreach (var entry in evictable.OrderBy(e => e.LastSeen).Take(take))
      _entries.Remove(entry.Key);
    return true;
  }

  /// <summary>
  /// Freezes the live entries into a detached list and swaps it in with one atomic reference write.
  ///
  /// The clones matter as much as the copied list. Handing out the live WatcherEntry objects would let the
  /// framework thread bump Count and LastSeen under a render thread that is mid-draw, which is the same race
  /// the snapshot pattern exists to prevent — just hidden one level deeper. Callers must hold <see cref="_gate"/>.
  /// </summary>
  private void Publish()
  {
    var copy = new WatcherEntry[_entries.Count];
    var i = 0;
    foreach (var entry in _entries.Values)
    {
      copy[i++] = new WatcherEntry
      {
        Key = entry.Key,
        Name = entry.Name,
        WorldName = entry.WorldName,
        JobId = entry.JobId,
        JobAbbreviation = entry.JobAbbreviation,
        Level = entry.Level,
        FirstSeen = entry.FirstSeen,
        LastSeen = entry.LastSeen,
        Count = entry.Count,
        IsCurrent = entry.IsCurrent,
      };
    }

    Volatile.Write(ref _published, copy);
  }
}
