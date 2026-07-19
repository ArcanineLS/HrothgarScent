using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace HrothgarScent.Scent;

/// <summary>
/// One player the plugin has seen near you, when <see cref="Configuration.RecordAllNearby"/> is on. Purely
/// observational — no note, no colour, no flags — which is the whole difference from <see cref="MarkedPlayer"/>:
/// this is a stranger logged by proximity, kept only because the user opted IN to that, and evictable.
/// </summary>
public sealed record SeenPlayer(
  string Name,
  uint HomeWorldId,
  string HomeWorldName,
  DateTimeOffset FirstSeen,
  DateTimeOffset LastSeen,
  string LastSeenZone,
  int SeenCount,
  string Race = "")
{
  [JsonIgnore]
  public WatcherKey Key => new(Name, HomeWorldId);

  [JsonIgnore]
  public string FullName => string.IsNullOrEmpty(HomeWorldName) ? Name : $"{Name}@{HomeWorldName}";
}

/// <summary>A revision and the entries it describes, published as one immutable reference — the same atomic-swap
/// shape as <see cref="MarksIndex"/> and <see cref="ScentSnapshot"/>, so a reader takes one volatile read.</summary>
public sealed record SeenIndex(int Revision, FrozenDictionary<WatcherKey, SeenPlayer> Entries)
{
  public static readonly SeenIndex Empty = new(0, FrozenDictionary<WatcherKey, SeenPlayer>.Empty);

  public SeenPlayer? Find(WatcherKey key) => Entries.TryGetValue(key, out var player) ? player : null;
}

/// <summary>
/// The durable, BOUNDED log of everyone you have been near — the opt-in opposite of <see cref="MarkStore"/>.
///
/// WHY THIS IS A SEPARATE STORE, and not a flag on a mark: the mark store's every invariant depends on it holding
/// only players the user pointed at — bounded by human effort, so no cap, no purge, no vacuum. Pouring strangers
/// into it by proximity would break all of that. This store instead OWNS the growth it invites: it is capped at
/// <see cref="Configuration.NearbyLogLimit"/> and evicts the least-recently-seen, so it can never become the
/// prior art's 3.25 GB. It writes nothing while <see cref="Configuration.RecordAllNearby"/> is off — the scanner
/// simply never calls it.
///
/// THREADING mirrors <see cref="MarkStore"/> exactly: writes from the framework thread (the scanner) and reads
/// from the render thread (the journal), a gate over the mutable dictionary, and readers taking one volatile read
/// of an immutable <see cref="SeenIndex"/>. Losing this file is not the loss losing marks.json is — it is
/// regenerable — so its Load starts empty on any read error rather than going read-only.
/// </summary>
public sealed class SeenLog
{
  public const int CurrentFileVersion = 1;

  private const string FileName = "seen.json";

  private readonly object _gate = new();
  private readonly Dictionary<WatcherKey, SeenPlayer> _entries = [];
  private SeenIndex _index = SeenIndex.Empty;
  private int _revision;

  /// <summary>Set by <see cref="RecordSeen"/> when it mutated the log, cleared by <see cref="CommitScan"/> which
  /// does the one publish/evict/flush per scan. Guarded by <see cref="_gate"/>. See CommitScan for why the publish
  /// is batched rather than done per recorded player.</summary>
  private bool _pendingPublish;

  /// <summary>Same debounce and reasoning as <see cref="MarkStore.FlushDebounceMs"/>: proximity writes come in
  /// bursts as a crowd loads, and coalescing them keeps this off the hot path.</summary>
  private const int FlushDebounceMs = 2000;

  private readonly object _flushGate = new();
  private readonly object _writeLock = new();
  private bool _dirty;
  private bool _flushPending;

  /// <summary>
  /// A reunion — the gap after which seeing someone again is a fresh visit rather than the same one. Identical to
  /// <see cref="MarkStore.ReunionThreshold"/> in purpose: without it a player standing beside you would be
  /// rewritten every scan. Wall clock against the stored value, so it survives zone changes and relogs.
  /// </summary>
  private static readonly TimeSpan ReunionThreshold = TimeSpan.FromMinutes(30);

  public SeenIndex Index => Volatile.Read(ref _index);

  private static string Path => System.IO.Path.Combine(
    Plugin.PluginInterface.ConfigDirectory.FullName, FileName);

  /// <summary>Reads seen.json, or leaves the log empty. Blocking, called from the plugin constructor before the
  /// scanner runs — the scanner would otherwise write a one-entry file over the real one, the same window
  /// <see cref="MarkStore.Load"/> guards against. Starts empty on ANY read failure: this log is regenerable, so
  /// there is nothing here worth the read-only ceremony marks.json needs.</summary>
  public void Load()
  {
    try
    {
      Directory.CreateDirectory(Plugin.PluginInterface.ConfigDirectory.FullName);

      Plugin.FileStorage.ReadAllTextAsync(Path, json =>
      {
        var file = JsonSerializer.Deserialize(json, SeenJson.Default.SeenFileV1)
                   ?? throw new InvalidDataException("seen.json deserialised to null");

        lock (_gate)
        {
          _entries.Clear();
          foreach (var player in file.Players)
          {
            if (string.IsNullOrEmpty(player.Name))
              continue;
            _entries[player.Key] = player;
          }

          // The file is hand-editable and the cap can be lowered between runs, so trim on load too.
          EvictToCap();
          Publish();
        }
      }).GetAwaiter().GetResult();
    }
    catch (FileNotFoundException) when (!Plugin.FileStorage.Exists(Path))
    {
      // First run, or nobody has ever recorded anyone. Not an error.
    }
    catch (Exception ex)
    {
      Plugin.Log.Warning(ex, "Could not read {File}; starting the nearby log empty", FileName);
    }
  }

  /// <summary>
  /// Notes that a player is near you, if enough has changed to be worth writing down. FRAMEWORK THREAD, called
  /// once per nearby player per scan while <see cref="Configuration.RecordAllNearby"/> is on — so the fast path
  /// is one lock-free probe of the published index and a couple of compares, exactly like
  /// <see cref="MarkStore.RecordSeen"/>. UNLIKE that one, it DOES create a record: creating the record is the
  /// whole point here, which is why it is behind an off-by-default switch and a hard cap.
  ///
  /// Only MUTATES; it does not publish, evict or flush — <see cref="CommitScan"/> does those once, after the
  /// scanner's per-player loop. That batching is what keeps a zone-in cheap: see CommitScan.
  /// </summary>
  public void RecordSeen(WatcherKey key, string worldName, string race, string zoneName, DateTimeOffset now)
  {
    var existing = Index.Find(key);

    // Also touch to FILL IN a race we did not have: a player often loads in with no race resolved yet, so the
    // first sighting stores it blank and a later scan — which the reunion throttle would otherwise skip — carries
    // the real one. Auto-tagging race is the point of the extra field, so it is worth this one extra write.
    var fillRace = existing is { Race.Length: 0 } && race.Length > 0;
    if (existing is not null && !NeedsTouch(existing, zoneName, now) && !fillRace)
      return;

    lock (_gate)
    {
      if (_entries.TryGetValue(key, out var entry))
      {
        // A reunion advances the count; a mere zone change updates the location without inflating it — the same
        // rule marks follow, so "times seen" means visits, not scans.
        var reunion = now - entry.LastSeen >= ReunionThreshold || now < entry.LastSeen;
        _entries[key] = entry with
        {
          LastSeen = now,
          LastSeenZone = zoneName,
          SeenCount = reunion ? entry.SeenCount + 1 : entry.SeenCount,

          // Keep a race we already knew if this read is blank (they walked out of resolve range); otherwise take
          // the fresh one.
          Race = race.Length > 0 ? race : entry.Race,
        };
      }
      else
      {
        _entries[key] = new SeenPlayer(key.Name, key.HomeWorldId, worldName, now, now, zoneName, 1, race);
      }

      _pendingPublish = true;
    }
  }

  /// <summary>
  /// Publishes, evicts and schedules a flush for everything <see cref="RecordSeen"/> accumulated this scan — ONCE.
  /// FRAMEWORK THREAD; the scanner calls it after its per-player record loop.
  ///
  /// WHY THE BATCH. RecordSeen used to publish and evict per player, and on a zone-in EVERY nearby player is a
  /// create-or-zone-touch — so a crowd of N players each triggered a whole-dictionary rebuild (the
  /// <see cref="Publish"/> ToFrozenDictionary, O(entries)) and, under a positive cap, an O(entries log entries)
  /// eviction sort. That made one framework-thread scan O(players × entries) and grew without bound as the
  /// UNLIMITED log did, hitching the frame you zoned into a crowd. Doing it once per scan is O(entries) total, and
  /// it also collapses the journal's rebuild (keyed on the published revision) from once-per-player to once-per-scan.
  /// A no-op when nothing was recorded, so calling it every scan is free.
  /// </summary>
  public void CommitScan()
  {
    lock (_gate)
    {
      if (!_pendingPublish)
        return;

      _pendingPublish = false;
      EvictToCap();
      Publish();
    }

    RequestFlush();
  }

  /// <summary>Whether a sighting is worth a write: a new zone, a reunion, or a clock that moved backwards. Mirrors
  /// <see cref="MarkStore.NeedsTouch"/>, minus the null-LastSeen branch — a null entry is handled as a create by
  /// the caller.</summary>
  private static bool NeedsTouch(SeenPlayer seen, string zoneName, DateTimeOffset now)
    => !string.Equals(seen.LastSeenZone, zoneName, StringComparison.Ordinal)
    || now - seen.LastSeen >= ReunionThreshold
    || now < seen.LastSeen;

  /// <summary>Drops the least-recently-seen entries down to the configured cap, or does nothing when the cap is 0
  /// (UNLIMITED — the default). Callers hold <see cref="_gate"/>. A positive cap is clamped where it is read,
  /// since it round-trips through a hand-editable file.</summary>
  private void EvictToCap()
  {
    var configured = Plugin.Configuration.NearbyLogLimit;
    if (configured <= 0)
      return;

    var limit = Math.Clamp(configured, Configuration.NearbyLogLimitMin, Configuration.NearbyLogLimitMax);
    if (_entries.Count <= limit)
      return;

    foreach (var key in _entries.Values
               .OrderBy(e => e.LastSeen)
               .Take(_entries.Count - limit)
               .Select(e => e.Key)
               .ToList())
      _entries.Remove(key);
  }

  public SeenPlayer? Find(WatcherKey key) => Index.Find(key);

  /// <summary>Every seen player, ordered by name for display. Off the published index, so no gate, no tearing.</summary>
  public IReadOnlyList<SeenPlayer> All()
    => [.. Index.Entries.Values.OrderBy(p => p.Name, StringComparer.Ordinal)];

  public int Count => Index.Entries.Count;

  /// <summary>Forgets one seen player — the journal's per-row delete.</summary>
  public void Remove(WatcherKey key)
  {
    lock (_gate)
    {
      if (!_entries.Remove(key))
        return;
      Publish();
    }

    RequestFlush();
  }

  /// <summary>Forgets everyone in the nearby log — the journal's "clear". Does not touch marks.</summary>
  public void Clear()
  {
    lock (_gate)
    {
      if (_entries.Count == 0)
        return;
      _entries.Clear();
      Publish();
    }

    RequestFlush();
  }

  private void Publish()
  {
    _revision++;
    Volatile.Write(ref _index, new SeenIndex(_revision, _entries.ToFrozenDictionary()));
  }

  private void RequestFlush()
  {
    lock (_flushGate)
    {
      _dirty = true;
      if (_flushPending)
        return;

      _flushPending = true;
      _ = Task.Run(FlushAfterQuietPeriod);
    }
  }

  private void FlushAfterQuietPeriod()
  {
    Thread.Sleep(FlushDebounceMs);

    lock (_flushGate)
    {
      _flushPending = false;
      if (!_dirty)
        return;
      _dirty = false;
    }

    WriteLatest();
  }

  private bool WriteLatest()
  {
    lock (_writeLock)
    {
      try
      {
        string json;
        lock (_gate)
          json = JsonSerializer.Serialize(
            new SeenFileV1 { Version = CurrentFileVersion, Players = [.. _entries.Values] },
            SeenJson.Default.SeenFileV1);

        Plugin.FileStorage.WriteAllTextAsync(Path, json).GetAwaiter().GetResult();
        return true;
      }
      catch (Exception ex)
      {
        Plugin.Log.Error(ex, "Could not write {File}", FileName);

        // Re-arm rather than swallow, exactly as MarkStore.WriteLatest does — the caller cleared _dirty before
        // handing over, so without this a failed write would look clean and drop the change for the session.
        lock (_flushGate)
          _dirty = true;

        return false;
      }
    }
  }

  /// <summary>Writes anything outstanding immediately, skipping the debounce — logout and unload. Mirrors
  /// <see cref="MarkStore.Flush"/>; does not wait on the sleeping debounce task.</summary>
  public bool Flush()
  {
    lock (_flushGate)
    {
      if (!_dirty)
        return true;
      _dirty = false;
    }

    return WriteLatest();
  }
}

/// <summary>The on-disk shape of seen.json — an array, for the same reason <see cref="MarkFileV1"/> is one:
/// System.Text.Json cannot key a dictionary by <see cref="WatcherKey"/>, and the index is rebuilt on load.</summary>
internal sealed class SeenFileV1
{
  public int Version { get; set; }

  public SeenPlayer[] Players { get; set; } = [];
}

/// <summary>
/// Compile-time serialisation for <see cref="SeenFileV1"/>, source-generated like <see cref="MarkJson"/>.
///
/// NOT indented, unlike marks.json — and that is deliberate, not an oversight. marks.json is pretty-printed
/// because the user may open it to read or repair their own notes; seen.json is a machine-generated proximity log
/// the user never hand-edits, so the whitespace is pure cost. Compact roughly HALVES the file and the whole-file
/// rewrite each flush does, which is the one thing that scales with the (capped) entry count. IncludeFields is
/// unnecessary here (no Vector4) but harmless.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false, IncludeFields = true)]
[JsonSerializable(typeof(SeenFileV1))]
[JsonSerializable(typeof(SeenPlayer))]
internal sealed partial class SeenJson : JsonSerializerContext;
