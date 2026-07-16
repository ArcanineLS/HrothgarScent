using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace HrothgarScent.Scent;

/// <summary>
/// The players the user pointed at, and the only durable record this plugin keeps about anybody.
///
/// THE RULE, and the reason this class exists at all: a player enters this store ONLY by a deliberate user
/// act — a right-click, a note, a colour. Never by proximity, never by looking at you, never by zone policy.
/// Removing the last thing the user said deletes the row outright (<see cref="MarkedPlayer.IsEmpty"/>), so the
/// store is bounded by human effort rather than by hours played. Nobody hand-annotates forty thousand
/// strangers, which is why there is no retention policy here, no purge screen, no size cap and no vacuum pass.
///
/// That is the whole difference from the prior art, which captures every player you walk past by default and
/// spends an entire subsystem — bulk deletes with keep-predicate matrices, merge, archive, and a raw SQL box
/// shipped in its settings window — trying to claw the result back. Its own issue tracker measures where that
/// ends: a 3.25 GB config folder and ten-second freezes, 453 days in.
///
/// <see cref="WatcherLog"/> is the other half of the same rule and stays on the far side of it: it captures
/// people for what THEY did, so it is in-memory only, dies at logout, and must never be given an option to
/// persist. Durable because the user authored it; ephemeral because the stranger did not consent to it.
///
/// THREADING. Writes come from the render thread (the row menu, the editor, the config window) and reads come
/// from both — <see cref="ScentScanner"/> and <see cref="AlertService"/> read <see cref="Index"/> on the
/// framework thread. Readers never take the gate: they take one volatile read of an immutable
/// <see cref="MarksIndex"/>, exactly as the UI reads <see cref="ScentSnapshot"/>. The gate guards the mutable
/// dictionary behind it, under the same discipline and for the same reason as <see cref="WatcherLog"/>: a
/// Dictionary does not tolerate concurrent mutation, and a torn resize corrupts it or throws.
/// </summary>
public sealed class MarkStore
{
  /// <summary>Schema version this build writes into marks.json. Bump only when a stored field's MEANING
  /// changes; a new property with a sensible default needs no bump and no migration.</summary>
  public const int CurrentFileVersion = 1;

  private const string FileName = "marks.json";

  /// <summary>
  /// Guards <see cref="_entries"/>. Uncontended — a handful of user edits a minute — so the cost is nil, and
  /// the alternative is a rare, unreproducible crash. See the threading remarks on the class.
  /// </summary>
  private readonly object _gate = new();

  private readonly Dictionary<WatcherKey, MarkedPlayer> _entries = [];

  private MarksIndex _index = MarksIndex.Empty;

  /// <summary>Bumped inside <see cref="_gate"/> on every mutation and published as part of
  /// <see cref="MarksIndex"/>; ScentWindow.RebuildViewIfStale hashes it. Unlike a Count it survives an in-place
  /// edit — a note changed, a colour picked, a flag toggled on a record that already existed — which is exactly
  /// what this store adds and what the Counts cannot see.</summary>
  private int _revision;

  /// <summary>
  /// How long the store waits for the edits to stop before writing. The note box reports a change on every
  /// KEYSTROKE, so an undebounced write is one whole-file serialise and one fsync per character typed. Two
  /// seconds is far longer than the gap between keystrokes and far shorter than the gap before anything can
  /// go wrong; both teardown paths force a write anyway, so the window is only ever lost to a hard crash.
  /// </summary>
  private const int FlushDebounceMs = 2000;

  /// <summary>Guards <see cref="_dirty"/> and <see cref="_flushPending"/>. Separate from <see cref="_gate"/>
  /// so a slow disk can never block a reader.</summary>
  private readonly object _flushGate = new();

  /// <summary>Held for the duration of a write so two of them cannot interleave and land out of order, which
  /// would persist an older state over a newer one. The serialise happens INSIDE it for the same reason: the
  /// later writer then necessarily serialises the later state, so last-write-wins is also freshest-wins.</summary>
  private readonly object _writeLock = new();

  private bool _dirty;

  /// <summary>Whether a debounce wait is already open. Keeps the sleeping pool thread to exactly one no matter
  /// how fast the user types.</summary>
  private bool _flushPending;

  /// <summary>
  /// Set when the file on disk was written by a NEWER build than this one. Suppresses every write, so an old
  /// build cannot truncate fields it does not understand and hand the user's notes back mutilated.
  ///
  /// Reads keep working: the entries loaded before the version check are simply what this build could
  /// understand. Refusing to load at all would be worse — see the remarks on <see cref="Load"/>.
  /// </summary>
  private bool _readOnly;

  /// <summary>Whether the store refused to write. Surfaced in the config window; a silent read-only store is a
  /// store that loses the user's next note without saying so.</summary>
  public bool IsReadOnly => Volatile.Read(ref _readOnly);

  /// <summary>
  /// Why the store is read-only, or null while it is writable.
  ///
  /// Two very different states reach read-only and the user's next move differs completely between them: a file
  /// from a newer build means "downgrade or update, your data is fine", while an unreadable file means "your
  /// data may be damaged, go look at it". One shared banner would have to be wrong about one of them.
  /// </summary>
  public string? ReadOnlyReason => Volatile.Read(ref _readOnlyReason);

  private string? _readOnlyReason;

  /// <summary>
  /// The published entries. Safe from any thread: the value is immutable and the volatile read supplies the
  /// fence that stops the JIT hoisting it out of a loop. Same contract as
  /// <see cref="ScentScanner.Snapshot"/>.
  /// </summary>
  public MarksIndex Index => Volatile.Read(ref _index);

  private static string Path => System.IO.Path.Combine(
    Plugin.PluginInterface.ConfigDirectory.FullName, FileName);

  /// <summary>
  /// Reads marks.json, or leaves the store empty if there isn't one yet.
  ///
  /// Blocking, and called from the plugin constructor before any window exists. That is deliberate: without it
  /// there is a window between construction and load in which every ignored player is VISIBLE and every focused
  /// player unhighlighted, and a mark made during it would be clobbered when the load landed. GetPluginConfig
  /// already blocks on I/O two lines earlier, so this matches the precedent rather than inventing one.
  ///
  /// THE CALLBACK OVERLOAD IS THE WHOLE POINT — do not "simplify" this to the Task&lt;string&gt; one. That
  /// overload falls back to the backup only when the read itself fails, so a corrupt-but-READABLE file (half a
  /// line, power loss mid-write, a byte flipped) returns cleanly, throws in our deserialiser afterwards, and
  /// the good backup is never consulted. Here the reader throwing is the documented signal to retry from the
  /// backup database — so a throw is the recovery path, not the failure. The prior art closed "add DB integrity
  /// checks" as wontfix and shipped databases that could not boot; here it is one overload.
  /// </summary>
  public void Load()
  {
    try
    {
      Directory.CreateDirectory(Plugin.PluginInterface.ConfigDirectory.FullName);

      Plugin.FileStorage.ReadAllTextAsync(Path, json =>
      {
        // Throwing in here is not an error path. It tells the storage service this content is unusable and to
        // come back with the backup copy — which is the only integrity check this store needs.
        var file = JsonSerializer.Deserialize(json, MarkJson.Default.MarkFileV1)
                   ?? throw new InvalidDataException("marks.json deserialised to null");

        lock (_gate)
        {
          _entries.Clear();

          // Last-wins on a duplicate key, and never ToFrozenDictionary's throwing Add path: this file is one a
          // user can hand-edit, and two entries for one player would otherwise throw here — on every load,
          // permanently, with the file unreachable to fix from inside the plugin.
          foreach (var player in file.Players)
          {
            if (string.IsNullOrEmpty(player.Name) || player.IsEmpty)
              continue;
            _entries[player.Key] = player;
          }

          // A file from the future keeps its data but stops taking writes. Load what we understand, refuse to
          // write back what we would flatten.
          if (file.Version > CurrentFileVersion)
            GoReadOnly(
              "marks.json was written by a newer version of HrothgarScent, so Hrothgar not writing to it. You " +
              "can look, but changes will be lost when you restart. Update the plugin to edit them again.");

          Publish();
        }
      }).GetAwaiter().GetResult();
    }
    // FIRST RUN ONLY, and the filter is what makes that true. The storage service throws a bare
    // FileNotFoundException for "no backup row exists" as well — and it reaches that from inside its own catch
    // around the real read, i.e. when the file IS on disk and merely could not be read (a transient lock from an
    // antivirus or a backup agent is enough). Catching both as "first run" would leave the store empty AND
    // writable, and the next mark the user made would overwrite their real marks.json with a one-entry file.
    // Exists answers true if either the file or a backup row is there, so this admits only the genuine first
    // run; everything else falls through to the catch below, which refuses to write.
    catch (FileNotFoundException) when (!Plugin.FileStorage.Exists(Path))
    {
      // Nothing anywhere: a fresh install, or a user who has never marked anybody. Not an error, and not worth
      // a log line.
    }
    catch (Exception ex)
    {
      // The file is there but neither it nor its backup could be read. Start from the legacy lists rather than
      // empty, and go read-only so this degraded state cannot overwrite a file that is still on disk and that a
      // later build, or the user with a text editor, might yet salvage.
      GoReadOnly(
        "Hrothgar could not read marks.json, so nothing is being written to it. Your focus and ignore ticks " +
        "were rebuilt from the old config lists; notes and colours are only in the file. Look at it before " +
        "restarting — this session will not overwrite it.");
      Plugin.Log.Error(ex, "Could not read {File}; falling back to the legacy lists and writing nothing",
        FileName);
      SeedFromLegacyLists();
    }
  }

  /// <summary>Stops the store writing, and records why in the user's own words. The first reason wins: the
  /// earliest failure is the one that explains the rest.</summary>
  private void GoReadOnly(string reason)
  {
    Volatile.Write(ref _readOnlyReason, ReadOnlyReason ?? reason);
    Volatile.Write(ref _readOnly, true);
  }

  /// <summary>
  /// Rebuilds what can be rebuilt from <see cref="Configuration.IgnoredPlayers"/> and
  /// <see cref="Configuration.FocusedPlayers"/>, for when marks.json could not be read at all.
  ///
  /// SUPPRESSION MUST FAIL SAFE, and that is the whole reason this exists. Notes and colours are simply gone
  /// in this state and there is nothing to be done about it — but ignore is not an ornament, it is a promise
  /// that a specific person is never shown or announced again. Failing it does not mean a missing note; it
  /// means the player someone ignored is suddenly back in their list and firing alerts at them, on the one day
  /// their disk went wrong. An empty store would do exactly that, silently.
  ///
  /// This is precisely why Migrate leaves those lists populated on disk instead of clearing them once imported.
  /// They are stale — anything marked since the upgrade is not in them — but stale suppression beats none.
  /// Callers must hold <see cref="_gate"/>... except this one, which runs during Load before anything else can
  /// see the store.
  /// </summary>
  private void SeedFromLegacyLists()
  {
    var config = Plugin.Configuration;
    if (config is null)
      return;

    foreach (var entry in config.IgnoredPlayers)
      Merge(entry.Name, entry.HomeWorldId, entry.HomeWorldName, MarkKind.Ignore);

    foreach (var entry in config.FocusedPlayers)
      Merge(entry.Name, entry.HomeWorldId, entry.HomeWorldName, MarkKind.Focus);

    if (_entries.Count > 0)
      Plugin.Log.Warning("Recovered {Count} marks from the legacy lists; notes and colours are not recoverable",
        _entries.Count);

    Publish();

    void Merge(string name, uint worldId, string worldName, MarkKind kind)
    {
      if (string.IsNullOrEmpty(name))
        return;

      var key = new WatcherKey(name, worldId);
      _entries[key] = _entries.TryGetValue(key, out var found)
        ? found with { Marks = found.Marks | kind }
        : new MarkedPlayer(name, worldId, worldName, kind, string.Empty, null, DateTimeOffset.Now);
    }
  }

  /// <summary>The mark for a player, or null. One published read; see <see cref="Index"/>.</summary>
  public MarkedPlayer? Find(WatcherKey key) => Index.Find(key);

  /// <summary>
  /// Applies <paramref name="mutate"/> to a player's mark and republishes.
  ///
  /// The single write path, so the delete-when-empty rule has exactly one place to live and cannot be bypassed
  /// by a caller that forgot it. <paramref name="mutate"/> receives the current mark, or a blank one keyed to
  /// this player if there is none yet, and returns what it should become.
  /// </summary>
  public void Update(WatcherKey key, string homeWorldName, Func<MarkedPlayer, MarkedPlayer> mutate)
  {
    lock (_gate)
    {
      var existing = _entries.TryGetValue(key, out var found)
        ? found
        : new MarkedPlayer(key.Name, key.HomeWorldId, homeWorldName, MarkKind.None, string.Empty, null,
          DateTimeOffset.Now);

      var updated = mutate(existing);

      // No mark, no record. The bound is enforced here, structurally, rather than by a policy someone has to
      // remember — see the remarks on the class.
      if (updated.IsEmpty)
        _entries.Remove(key);
      else
        _entries[key] = updated;

      Publish();
    }

    RequestFlush();
  }

  /// <summary>
  /// How long before seeing a marked player again counts as seeing them again.
  ///
  /// A reunion, not a sighting. Without it, standing next to a marked friend would rewrite the record every
  /// scan — four disk flushes a second, for a figure whose entire point is to be read months later. Half an
  /// hour is far longer than any gap that matters to "when did I last run into this guy" and far shorter than
  /// the answer's useful resolution.
  ///
  /// Wall clock against the STORED value, never a per-session set of who has been seen. A zone change, a
  /// relog and a PvP match all run ScentScanner.Reset, so any such set would be cleared at exactly the
  /// boundaries it needed to survive — eight dungeons in an evening would each look like a fresh reunion. This
  /// needs no state at all and cannot be cleared by anything.
  /// </summary>
  private static readonly TimeSpan ReunionThreshold = TimeSpan.FromMinutes(30);

  /// <summary>
  /// Notes that a marked player is near you, if enough has changed to be worth writing down.
  ///
  /// FRAMEWORK THREAD, called once per marked player per scan, so the fast path must cost nothing: it is one
  /// lock-free probe of the published index and two compares, and it answers "nothing to do" for every player
  /// on every scan but one in half an hour.
  ///
  /// NEVER CREATES A RECORD. That is the consent line, in one return statement: walking past someone is not
  /// pointing at them, and a store that grew by proximity is the thing this design exists to refuse. Absent
  /// means absent.
  /// </summary>
  public void RecordSeen(WatcherKey key, string zoneName, DateTimeOffset now)
  {
    // Off the published index: no gate, no allocation. Taking the lock per marked player per scan to answer
    // "no" would be a lock 80 times a second for nothing.
    var existing = Index.Find(key);
    if (existing is null || !NeedsTouch(existing, zoneName, now))
      return;

    Update(key, existing.HomeWorldName, mark => mark with { LastSeen = now, LastSeenZone = zoneName });
  }

  /// <summary>
  /// Whether a sighting is worth a write.
  ///
  /// The zone half is not an optimisation, it is a correctness fix: without it, a marked player standing beside
  /// you in Limsa who follows you to Ul'dah and logs off would be recorded as last seen in LIMSA, forever —
  /// a wrong answer to the exact question this field exists to answer, produced by the throttle meant to
  /// protect it.
  /// </summary>
  private static bool NeedsTouch(MarkedPlayer mark, string zoneName, DateTimeOffset now)
    => mark.LastSeen is not { } last
    || !string.Equals(mark.LastSeenZone, zoneName, StringComparison.Ordinal)
    || now - last >= ReunionThreshold

    // A clock that went backwards — a manual change, a DST shift, a hand-edited file — must not freeze the
    // field until real time catches up. Rewriting is the cheap, self-healing answer.
    || now < last;

  /// <summary>
  /// Whether a mark has gone quiet — nothing has matched it in <see cref="Configuration.MarkStaleDays"/>.
  ///
  /// This is the honest answer to renames, and it is a REPORT rather than an action. A mark keyed on name and
  /// world stops matching the moment either changes, and the only symptom is a highlight that never comes back;
  /// nothing here can tell that apart from a player who simply stopped logging in. So it says "this one has
  /// gone quiet" and hands the user a pencil, instead of guessing.
  ///
  /// Guessing is exactly what the prior art does, and its issue tracker is the case against it: the game itself
  /// lies about names — a recording plugin swaps them for job names — so it fires rename alerts for every
  /// player it has ever seen and writes "White Mage" into permanent history. A human confirming a rename cannot
  /// do that.
  ///
  /// MEASURED FROM LastSeen ?? MarkedOn. A mark made from the friend list has never been seen at all, and
  /// measuring a null as "1 January year 1" would make every such mark two thousand years stale the instant it
  /// was created — dimmed on the very screen that made it. Falling back to when the user marked them reads
  /// correctly for both: "you said to watch for this person a month ago and they have not turned up".
  ///
  /// The <c>&lt;= 0</c> guard is not defensive style. Without it, 0 means "stale after no days at all" — every
  /// mark, always — which is the exact inverse of the "0 = never" the setting advertises.
  /// </summary>
  public static bool IsStale(MarkedPlayer mark)
  {
    var days = Plugin.Configuration.MarkStaleDays;
    if (days <= 0)
      return false;

    return DateTimeOffset.Now - (mark.LastSeen ?? mark.MarkedOn) > TimeSpan.FromDays(days);
  }

  /// <summary>
  /// Re-points a mark at who that player is now, keeping everything the user ever said about them.
  ///
  /// REMOVE THEN REINSERT, never mutate a key in place — which is free here only because
  /// <see cref="MarkedPlayer"/> is immutable and the key is derived from its fields, so there is no way to
  /// write a name without producing a new record. The prior art mutates the name on a cached instance and then
  /// asks a sorted set to remove it: the search walks to where the NEW key belongs, finds a node placed by the
  /// OLD one, fails, and the discarded return value means nobody notices. The stale node leaks, the re-add
  /// inserts a duplicate, and its two indexes disagree about how many players exist — on the headline feature.
  ///
  /// Merges rather than overwrites if the new identity already has a mark: the user is saying these are one
  /// person, so the flags union and the note that exists wins. Losing a note to a rename would be the one
  /// unrecoverable outcome here, since the note is the only thing in the record nothing else could reproduce.
  /// </summary>
  public void Rename(WatcherKey key, string newName, uint newWorldId, string newWorldName)
  {
    if (string.IsNullOrWhiteSpace(newName))
      return;

    var newKey = new WatcherKey(newName, newWorldId);

    lock (_gate)
    {
      if (!_entries.TryGetValue(key, out var mark))
        return;

      // Nothing moved. Bail before touching the dictionary rather than remove-and-reinsert the same row, which
      // would work but would republish and dirty the file for nothing.
      if (newKey == key)
        return;

      var moved = mark with { Name = newName, HomeWorldId = newWorldId, HomeWorldName = newWorldName };

      if (_entries.TryGetValue(newKey, out var existing))
        moved = moved with
        {
          Marks = existing.Marks | moved.Marks,
          Note = existing.HasNote ? existing.Note : moved.Note,
          Color = existing.Color ?? moved.Color,

          // The earlier of the two: the user has been watching for this person since whichever came first.
          MarkedOn = existing.MarkedOn < moved.MarkedOn ? existing.MarkedOn : moved.MarkedOn,

          // The later, and the one whose zone goes with it: the freshest sighting is the true one.
          LastSeen = Later(existing, moved)?.LastSeen,
          LastSeenZone = Later(existing, moved)?.LastSeenZone ?? string.Empty,
        };

      _entries.Remove(key);
      _entries[newKey] = moved;
      Publish();
    }

    RequestFlush();
    return;

    static MarkedPlayer? Later(MarkedPlayer a, MarkedPlayer b)
      => (a.LastSeen, b.LastSeen) switch
      {
        (null, null) => null,
        (null, not null) => b,
        (not null, null) => a,
        var (x, y) => x >= y ? a : b,
      };
  }

  /// <summary>Forgets a player outright, whatever they were marked with. The config window's delete button.</summary>
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

  /// <summary>Forgets everybody.</summary>
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

  /// <summary>Every mark, ordered for display. Off the published index, so no gate and no tearing.</summary>
  public IReadOnlyList<MarkedPlayer> All()
    => [.. Index.Entries.Values.OrderBy(mark => mark.Name, StringComparer.Ordinal)];

  /// <summary>How many players are remembered. Off the published index — never <c>_entries.Count</c>, which is
  /// a torn read from the render thread.</summary>
  public int Count => Index.Entries.Count;

  /// <summary>
  /// Freezes the live entries into a new immutable index and swaps it in with one atomic reference write.
  /// Callers must hold <see cref="_gate"/>.
  ///
  /// The records themselves need no cloning, unlike <see cref="WatcherLog.Publish"/>'s: MarkedPlayer is an
  /// immutable record, so the dictionary is the only mutable thing here and copying it is the whole job.
  /// </summary>
  private void Publish()
  {
    _revision++;
    Volatile.Write(ref _index, new MarksIndex(_revision, _entries.ToFrozenDictionary()));
  }

  /// <summary>
  /// Notes that the store changed and makes sure it reaches the disk, eventually and off both hot threads.
  ///
  /// Coalescing rather than write-per-edit: see <see cref="FlushDebounceMs"/> for why a keystroke must not be
  /// an fsync. Anything changed during the wait rides along with the same write; anything changed during the
  /// write arrives back here and schedules the next pass, because the flag is always cleared before the write
  /// rather than after it.
  /// </summary>
  private void RequestFlush()
  {
    if (IsReadOnly)
      return;

    lock (_flushGate)
    {
      _dirty = true;

      // A wait is already open and has not taken its copy of the state yet, so it will include this edit.
      if (_flushPending)
        return;

      // Discarded, not held: nothing waits on it (see Flush), it cannot be collected while it runs, and
      // WriteLatest swallows its own exceptions — so there is no unobserved task to worry about.
      _flushPending = true;
      _ = Task.Run(FlushAfterQuietPeriod);
    }
  }

  /// <summary>Waits the edits out on a pool thread, then writes whatever the state is by then.</summary>
  private void FlushAfterQuietPeriod()
  {
    Thread.Sleep(FlushDebounceMs);

    lock (_flushGate)
    {
      // Cleared BEFORE the write, never after. An edit landing mid-write must be able to set the flag again and
      // schedule its own pass, rather than be swallowed by this one clearing it on the way out.
      _flushPending = false;
      if (!_dirty)
        return;
      _dirty = false;
    }

    WriteLatest();
  }

  /// <summary>
  /// Serialises the current state and writes it.
  ///
  /// Running this off the render thread is the whole point: IReliableFileStorage.WriteAllTextAsync is async in
  /// NAME only — it executes inline under a shared lock and hands back an already-completed task — so calling
  /// it from Draw would put an fsync in the middle of a frame.
  /// </summary>
  private bool WriteLatest()
  {
    lock (_writeLock)
    {
      try
      {
        string json;
        lock (_gate)
          json = JsonSerializer.Serialize(
            new MarkFileV1 { Version = CurrentFileVersion, Players = [.. _entries.Values] },
            MarkJson.Default.MarkFileV1);

        Plugin.FileStorage.WriteAllTextAsync(Path, json).GetAwaiter().GetResult();
        return true;
      }
      catch (Exception ex)
      {
        Plugin.Log.Error(ex, "Could not write {File}", FileName);

        // Re-arm rather than swallow. The caller cleared _dirty before handing the write over, so without this
        // a failed write would leave the store looking clean and quietly drop the edit for the rest of the
        // session — the next mutation flushes both.
        lock (_flushGate)
          _dirty = true;

        return false;
      }
    }
  }

  /// <summary>
  /// Writes anything outstanding immediately, skipping the debounce. Logout and unload.
  ///
  /// Deliberately does NOT wait on the pending debounce task. That task is almost entirely its own sleep, so
  /// waiting on it would stall every logout by up to <see cref="FlushDebounceMs"/> for nothing. Writing here
  /// instead makes the state durable at once and leaves the sleeper to wake into a clean flag and return.
  ///
  /// Ordering is safe without any coordination: <see cref="_writeLock"/> admits one writer at a time, and each
  /// serialises after taking it — so if the debounce did win the race, this one simply follows it with the same
  /// or newer state. One redundant write at logout is a fine price.
  ///
  /// A write already in flight needs no wait either: the storage service waits on its own pending writes when
  /// its scope tears down, which happens after the plugin's Dispose. What it cannot do is START a write nobody
  /// asked for, which is exactly what this is for.
  /// </summary>
  /// <returns>Whether the store is now safely on disk. True when there was nothing to write — nothing
  /// outstanding is a success. Callers that are about to record the write as HAVING happened, like
  /// Configuration.Migrate, must check this.</returns>
  public bool Flush()
  {
    if (IsReadOnly)
      return true;

    lock (_flushGate)
    {
      if (!_dirty)
        return true;
      _dirty = false;
    }

    return WriteLatest();
  }
}
