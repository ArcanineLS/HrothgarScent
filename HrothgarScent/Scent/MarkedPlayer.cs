using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Numerics;
using System.Text.Json.Serialization;

namespace HrothgarScent.Scent;

/// <summary>
/// What the user has deliberately declared about a player.
///
/// Focus and Ignore are INDEPENDENT flags, never a tri-state. Collapsing them into one exclusive value is
/// tempting — it would make "ignore beats focus" unrepresentable rather than merely documented — and it is
/// lossy: ignoring a focused player would destroy the focus, so un-ignoring could not put it back. Today that
/// round trip is recoverable, and it stays recoverable. The precedence rule lives in
/// <see cref="Configuration.FocusedPlayers"/>'s remarks and is applied at the point of use, exactly like
/// watcher tint beating focus tint.
///
/// There is deliberately no Note member here. See <see cref="MarkedPlayer.HasNote"/>.
/// </summary>
[Flags]
public enum MarkKind : byte
{
  None = 0,
  Focus = 1,
  Ignore = 2,
}

/// <summary>
/// One player the user pointed at, and everything they said about them.
///
/// Immutable, and keyed on Name+HomeWorldId like every other durable identity in the plugin — see
/// <see cref="WatcherKey"/> for why an id cannot serve. Holds no pointer and no GameObjectId, so it is safe
/// to read from any thread and to outlive any scan; the same discipline, and the same reason, as
/// <see cref="ScentRow"/>.
///
/// This record is the whole durable surface, and all of it but two fields is something the user typed or
/// clicked.
///
/// THE TWO EXCEPTIONS ARE <see cref="LastSeen"/> AND <see cref="LastSeenZone"/>, and they are the one place
/// this plugin writes down an observation of its own rather than a thing the user said. They are worth their
/// own paragraph precisely because that is the line everything else here is built to hold — see the remarks on
/// <see cref="LastSeen"/>, and do not add a third without making the same argument.
/// </summary>
public sealed record MarkedPlayer(
  string Name,
  uint HomeWorldId,
  string HomeWorldName,
  MarkKind Marks,
  string Note,
  Vector4? Color,
  DateTimeOffset MarkedOn,
  DateTimeOffset? LastSeen = null,
  string LastSeenZone = "")
{
  /// <summary>Identity, shared with <see cref="ScentRow.Key"/> and the watcher log. Not serialised: it is the
  /// two fields above it, and a stored copy is one more thing that can disagree with them.</summary>
  [JsonIgnore]
  public WatcherKey Key => new(Name, HomeWorldId);

  /// <summary>
  /// When the plugin last saw this player near you. NOT when they last looked at you — that is the watcher
  /// log's, and it stays in memory.
  ///
  /// THE ONE OBSERVATION THIS PLUGIN WRITES DOWN, and it needs its argument made rather than assumed, because
  /// "when did I last physically see this person" is exactly the datum <see cref="WatcherLog.LastSeen"/>
  /// refuses to persist. Three things make it survive that refusal, and it needs all three:
  ///
  ///   1. It exists ONLY for players the user already pointed at. Proximity never creates a record — see
  ///      <see cref="MarkStore.RecordSeen"/>, which returns rather than create. So the set is the marks: bounded
  ///      by human effort, not by hours played.
  ///   2. It is OVERWRITTEN, never appended. One timestamp and one place name, not a history. There is no row
  ///      per encounter, so there is nothing to grow — which is the whole of what went wrong with the prior
  ///      art's 3.25 GB of encounter rows.
  ///   3. It is switchable off (<see cref="Configuration.RememberLastSeen"/>) and it does not keep a record
  ///      alive: see <see cref="IsEmpty"/>. Unmark someone and the fact goes with them.
  ///
  /// It answers the question users actually ask of a notes plugin — "when did I last run into this guy, and
  /// where" — for one field rather than a join table. If a fourth reason is ever needed to justify a fourth
  /// observational field, that is the signal to stop.
  /// </summary>
  public DateTimeOffset? LastSeen { get; init; } = LastSeen;

  /// <summary>Where <see cref="LastSeen"/> happened, resolved at scan time. Empty when the zone sheet could not
  /// be read; see ScentScanner.ZoneName for why that is a real state and not a failure.</summary>
  public string LastSeenZone { get; init; } = LastSeenZone;

  private readonly IReadOnlyList<string> _tags = [];

  /// <summary>
  /// Custom category tags the user filed this player under — "static", "gil buyer", "cool crafter", anything.
  ///
  /// USER-AUTHORED, so unlike every other list here they KEEP a record alive (see <see cref="IsEmpty"/>): a
  /// player the user categorised is a player the user pointed at, exactly as a note or a colour is. That is the
  /// line that separates this one member from the four observational ones below it. Ordinal-distinct and
  /// insertion-ordered; the store dedupes and trims on the way in, so nothing downstream has to.
  ///
  /// Null-coalescing init setter because marks.json is hand-editable: a literal <c>"Tags": null</c> would
  /// otherwise NRE the first time anything read Count. Same defence the store's duplicate-key handling is.
  /// </summary>
  public IReadOnlyList<string> Tags
  {
    get => _tags;
    init => _tags = value ?? [];
  }

  /// <summary>
  /// When the plugin FIRST saw this marked player near you. The sibling of <see cref="LastSeen"/>, and it lives
  /// under the same three-part licence: it exists only for players already marked, it is one timestamp rather
  /// than a history, and it is switchable off with the same setting. OBSERVATION, not word — so like LastSeen it
  /// is explicitly NOT a reason a record survives (<see cref="IsEmpty"/>).
  /// </summary>
  public DateTimeOffset? FirstSeen { get; init; }

  /// <summary>
  /// How many distinct times — reunions, not scans — this marked player has been seen near you.
  ///
  /// A single counter overwritten in place, NOT a per-sighting log: the thing that must never grow is a row per
  /// encounter, and this is one integer that steps by one on the same reunion boundary <see cref="LastSeen"/>
  /// already throttles to (see MarkStore.RecordSeen). Observation, so it does not keep a record alive.
  /// </summary>
  public int SeenCount { get; init; }

  private readonly IReadOnlyList<DutyEncounter> _encounters = [];

  /// <summary>
  /// Duties cleared together, aggregated one row per fight. The STRUCTURED home of what used to be appended to
  /// the note as "Cleared X together." lines — moved out so the note is purely what the user typed, and so the
  /// data can be counted and sorted instead of read as prose.
  ///
  /// Observation, not a deliberate mark — a duty completing is not the user pointing at anyone — so it never
  /// keeps a record alive, and DutyService still writes here ONLY for players already marked. Aggregated per
  /// distinct duty (see <see cref="DutyEncounter"/>) so a static farmed for a year stays bounded by the number
  /// of fights, never by the number of clears: the same refusal-to-grow the whole store is built on.
  /// </summary>
  public IReadOnlyList<DutyEncounter> Encounters
  {
    get => _encounters;
    init => _encounters = value ?? [];
  }

  private readonly IReadOnlyList<PastIdentity> _nameHistory = [];

  /// <summary>
  /// Names and worlds this player went by before the user repaired the mark with Renamed?.
  ///
  /// The honest half of the rename story the store could not tell before: <see cref="MarkStore.Rename"/> only
  /// ever moved a mark forward and forgot where it came from. Written ONLY by that deliberate repair, so it is
  /// bounded by human effort — a handful of entries at the very most — never by anything automatic. Observation,
  /// so it does not keep a record alive.
  /// </summary>
  public IReadOnlyList<PastIdentity> NameHistory
  {
    get => _nameHistory;
    init => _nameHistory = value ?? [];
  }

  /// <summary>Whether the user has filed this player under any tag. Drives <see cref="IsEmpty"/> and the header's
  /// tag line.</summary>
  [JsonIgnore]
  public bool HasTags => _tags.Count > 0;

  /// <summary>
  /// Whether there is a note. DERIVED, never stored as a <see cref="MarkKind"/> flag.
  ///
  /// Two representations of "has a note" can disagree, and the disagreement is unfixable from the UI: clear the
  /// text without clearing the flag and the record answers "not empty" forever — an immortal row the store's own
  /// delete-when-empty rule can never collect. One source of truth, so the question cannot arise.
  ///
  /// IsNullOrWhiteSpace, not IsNullOrEmpty: a note trimmed down to a single space is not a note, and would
  /// otherwise pin a record for good.
  /// </summary>
  [JsonIgnore]
  public bool HasNote => !string.IsNullOrWhiteSpace(Note);

  [JsonIgnore]
  public bool IsFocused => (Marks & MarkKind.Focus) != 0;

  [JsonIgnore]
  public bool IsIgnored => (Marks & MarkKind.Ignore) != 0;

  /// <summary>
  /// Nothing the user said is left, so there is nothing to remember. <see cref="MarkStore.Update"/> deletes
  /// rows that answer true rather than keeping a row of blanks — that deletion is the whole of why this store
  /// cannot grow without the user, and why it needs no retention policy, no purge screen and no size cap.
  ///
  /// A colour counts. It is as user-authored as a note, even though it only renders on a focused row, and
  /// silently discarding it would be data loss on a rule the user never agreed to.
  ///
  /// <see cref="LastSeen"/> DELIBERATELY DOES NOT COUNT, and this is the load-bearing half of the rule. It is
  /// the plugin's observation, not the user's word, so it must never be the reason a record survives: if it
  /// were, un-ticking the last mark would leave behind a row that says only "you were near this person at this
  /// time, here" — a durable record of a stranger's movements, authored by nobody, which is precisely the
  /// artifact this whole store exists to refuse. Untick everything and the observation goes with the record.
  ///
  /// A TAG counts, on the same footing as a note or a colour: it is a thing the user typed about this person,
  /// so a player filed under only a tag is a player deliberately kept. <see cref="FirstSeen"/>,
  /// <see cref="SeenCount"/>, <see cref="Encounters"/> and <see cref="NameHistory"/> DO NOT count, for exactly
  /// the reason LastSeen does not: they are all observations, and an observation is never the reason a record
  /// outlives the mark that authored it. Forget everything the user said and every observation goes too.
  /// </summary>
  [JsonIgnore]
  public bool IsEmpty => Marks == MarkKind.None && !HasNote && Color is null && !HasTags;

  /// <summary>
  /// Whether this record has anything to SHOW for itself — a glyph on the row, a marker on the info bar.
  ///
  /// Not the same question as <see cref="IsEmpty"/>, and the gap between them is the whole reason this exists.
  /// A record holding only a colour is deliberately kept (a colour is user-authored, and discarding it silently
  /// would be data loss) but has nothing to draw: it is not focused, so the colour it carries renders nowhere,
  /// and a glyph claiming otherwise would assert a mark the list itself denies.
  ///
  /// ONE PREDICATE, because three readers already drifted apart over it. The row glyph, the sort key and the
  /// info bar's marker must agree exactly or a readout will say a thing the list beside it does not — and the
  /// info bar is the one the user cannot cross-check, because it is on screen when the window is shut.
  ///
  /// Ignore is not consulted here. It is a suppression, and its holders each apply it at their own edge; see
  /// NameplateService.OnNamePlateUpdate for why that stays true even when it is inconvenient.
  /// </summary>
  [JsonIgnore]
  public bool HasVisibleMark => HasNote || IsFocused || IsIgnored;

  /// <summary>Display form, matching <see cref="ScentRow.FullName"/>.</summary>
  [JsonIgnore]
  public string FullName => string.IsNullOrEmpty(HomeWorldName) ? Name : $"{Name}@{HomeWorldName}";
}

/// <summary>
/// One kind of duty cleared with a marked player, aggregated over every time it was cleared.
///
/// A row per DISTINCT fight, never a row per clear — <see cref="Count"/> carries "how many times" so the list
/// cannot grow past the number of duties in the game no matter how many times a static runs one. This is the
/// structured replacement for DutyService's old "Cleared X together." note line: same fact, but countable and
/// sortable rather than prose the user then could not do anything with. <see cref="FirstCleared"/> and
/// <see cref="LastCleared"/> bracket the run; on a single clear they are equal.
/// </summary>
public sealed record DutyEncounter(string DutyName, int Count, DateTimeOffset FirstCleared, DateTimeOffset LastCleared);

/// <summary>
/// A name and world this player went by before the user re-pointed the mark with Renamed?.
///
/// The OLD identity, captured at the moment <see cref="MarkStore.Rename"/> moves the record — so the History
/// tab can show where a mark came from, which the store used to simply forget. Bounded by the number of times a
/// human repaired this one record. <see cref="ChangedOn"/> is when the repair happened, not when the rename did:
/// the game never tells us the latter, and claiming a precise date we cannot know would be the kind of confident
/// wrong answer the rest of this plugin refuses.
/// </summary>
public sealed record PastIdentity(string Name, uint HomeWorldId, string HomeWorldName, DateTimeOffset ChangedOn)
{
  /// <summary>Display form, matching <see cref="MarkedPlayer.FullName"/>.</summary>
  [JsonIgnore]
  public string FullName => string.IsNullOrEmpty(HomeWorldName) ? Name : $"{Name}@{HomeWorldName}";
}

/// <summary>
/// A revision and the entries that revision describes, published together as ONE immutable reference.
///
/// Two fields — a counter and a dictionary — cannot be published atomically without an ordering argument, and
/// the obvious orderings are both wrong in one direction: bump-then-swap lets a reader see a new revision over
/// old entries, which is precisely the stale-view bug the revision exists to prevent. Bundling them removes the
/// question rather than answering it. This is the shape <see cref="ScentSnapshot"/> already uses, three lines
/// from where the scanner publishes it — one record, one Volatile.Write, one read.
/// </summary>
public sealed record MarksIndex(int Revision, FrozenDictionary<WatcherKey, MarkedPlayer> Entries)
{
  public static readonly MarksIndex Empty = new(0, FrozenDictionary<WatcherKey, MarkedPlayer>.Empty);

  public MarkedPlayer? Find(WatcherKey key) => Entries.TryGetValue(key, out var mark) ? mark : null;

  public bool IsFocused(WatcherKey key) => Entries.TryGetValue(key, out var mark) && mark.IsFocused;

  public bool IsIgnored(WatcherKey key) => Entries.TryGetValue(key, out var mark) && mark.IsIgnored;
}
