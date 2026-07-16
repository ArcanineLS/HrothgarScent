using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Configuration;
using HrothgarScent.Scent;

namespace HrothgarScent;

/// <summary>Whether job colours come from four role buckets or from a per-job override table.</summary>
public enum JobColorMode
{
  Role,
  Job,
}

public enum LodestoneRegion
{
  Europe,
  Germany,
  France,
  NorthAmerica,
  Japan,
}

/// <summary>
/// The column ids <see cref="Configuration.HiddenColumnMask"/> addresses, mirroring ScentWindow.ScentColumn.
/// Duplicated rather than shared because ScentColumn is the window's private sort key and this is persisted
/// config; the two must agree, so each names the other. Starts at 1 because 0 is ImGui's "no user id".
/// </summary>
public static class ScentColumns
{
  public const uint Watching = 1;
  public const uint Name = 2;
  public const uint Job = 3;
  public const uint Level = 4;
  public const uint World = 5;
  public const uint Company = 6;
  public const uint Distance = 7;
  public const uint Race = 8;
}

/// <summary>
/// A player the user never wants to see.
///
/// Keyed by name+world, not GameObjectId: an id is recycled and changes on a zone, so an id-keyed ignore
/// would expire the moment they walked away, and would eventually hide an innocent stranger who inherited
/// the slot.
/// </summary>
[Serializable]
public sealed class IgnoredPlayer
{
  public string Name { get; set; } = string.Empty;
  public uint HomeWorldId { get; set; }
  public string HomeWorldName { get; set; } = string.Empty;

  public IgnoredPlayer()
  {
  }

  public IgnoredPlayer(string name, uint homeWorldId, string homeWorldName)
  {
    Name = name;
    HomeWorldId = homeWorldId;
    HomeWorldName = homeWorldName;
  }

  /// <summary>Ordinal, not case-insensitive: character names are case-exact, and two players on one world
  /// can differ only in case.</summary>
  public bool Matches(ScentRow row)
    => row.HomeWorldId == HomeWorldId
    && string.Equals(row.Name, Name, StringComparison.Ordinal);

  /// <summary>Display form for the ignore-list table.</summary>
  public string FullName => string.IsNullOrEmpty(HomeWorldName) ? Name : $"{Name}@{HomeWorldName}";
}

/// <summary>
/// A player the user wants picked out of the crowd.
///
/// Keyed by name+world for the same reason <see cref="IgnoredPlayer"/> is: an id is recycled and changes on
/// a zone, so an id-keyed focus would expire the moment they walked away, and would eventually highlight an
/// innocent stranger who inherited the slot.
/// </summary>
[Serializable]
public sealed class FocusedPlayer
{
  public string Name { get; set; } = string.Empty;
  public uint HomeWorldId { get; set; }
  public string HomeWorldName { get; set; } = string.Empty;

  public FocusedPlayer() { }

  public FocusedPlayer(string name, uint homeWorldId, string homeWorldName)
  {
    Name = name;
    HomeWorldId = homeWorldId;
    HomeWorldName = homeWorldName;
  }

  /// <summary>Ordinal, not case-insensitive: character names are case-exact, and two players on one world
  /// can differ only in case.</summary>
  public bool Matches(ScentRow row)
    => row.HomeWorldId == HomeWorldId
    && string.Equals(row.Name, Name, StringComparison.Ordinal);

  /// <summary>Matches a <see cref="WatcherKey"/> rather than a row, for the scanner's arrival edge check,
  /// which owns keys and not rows.</summary>
  public bool Matches(WatcherKey key)
    => key.HomeWorldId == HomeWorldId
    && string.Equals(key.Name, Name, StringComparison.Ordinal);

  /// <summary>Display form for the focus-list table.</summary>
  public string FullName => string.IsNullOrEmpty(HomeWorldName) ? Name : $"{Name}@{HomeWorldName}";
}

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
  /// <summary>
  /// Online status row id for AFK. NOT verified against the live sheet — which is why
  /// <see cref="HideAfk"/> defaults off, so a wrong id here can never silently hide players. Confirm
  /// in-game before making that filter default on.
  /// </summary>
  public const uint AFK_ONLINE_STATUS_ID = 17;

  /// <summary>Config schema this build writes. Bumped when a persisted field's meaning changes, never for a
  /// plain addition — a new property with a default needs no migration.</summary>
  public const int CurrentVersion = 1;

  public int Version { get; set; } = 0;

  /// <summary>
  /// Brings a config forward to <see cref="CurrentVersion"/>. Called once, from the Plugin constructor,
  /// before any window exists.
  ///
  /// Version 0 -> 1 carries <see cref="ShowRaceColumn"/> into <see cref="HiddenColumnMask"/>. Honoured rather
  /// than dropped because Race defaulted hidden, so dropping it would un-hide the column for every existing
  /// user at once — the exact "your list rearranged itself because you updated" that ShowRaceColumn existed to
  /// prevent. A fresh install migrates identically: a default Configuration is Version 0 with
  /// ShowRaceColumn=false, and lands on the Race bit set, which is the same first-run look as before.
  ///
  /// Only Race is carried. The other columns had no persisted state to carry — NoSavedSettings meant they came
  /// back visible on every restart — so a mask with only the Race bit set reproduces what a Version 0 user saw
  /// on login, exactly.
  /// </summary>
  public void Migrate()
  {
    if (Version >= CurrentVersion)
      return;

    if (Version < 1)
      SetColumnHidden(ScentColumns.Race, !ShowRaceColumn);

    Version = CurrentVersion;
    Save();
  }

  // ---- Window ----

  public bool OpenOnLogin { get; set; } = false;
  public bool HideInCombat { get; set; } = false;
  public bool HideInDuty { get; set; } = false;
  public bool HideInCutscene { get; set; } = true;
  public bool ShowSearchBar { get; set; } = true;
  public bool ShowWatcherHistory { get; set; } = true;
  public bool ShowDtr { get; set; } = true;

  /// <summary>
  /// Rows the player list shows before it scrolls. The window grows and shrinks with the list up to this
  /// many and then stops; the rest go behind the table's own scrollbar.
  ///
  /// 12 because that is what the shipped 380px default window already showed with an empty history — so an
  /// existing install that walks into a crowd lands on the window size it has always had, and the only case
  /// that visibly changes is the short list that used to sit above a slab of grey.
  ///
  /// Deliberately NOT in <see cref="FilterSignature"/>. It sizes the viewport; it never changes which rows
  /// survive or what order they are in, which is the same test <see cref="HiddenColumnMask"/> fails there.
  /// Contrast <see cref="MaxPlayersShown"/>, which IS in the signature because it drops rows from the view —
  /// the two read alike and are not.
  /// </summary>
  public int MaxVisibleRows { get; set; } = 12;

  /// <summary>Bounds for <see cref="MaxVisibleRows"/>, enforced where it is read as well as at the slider:
  /// the value round-trips through a JSON file the user can hand-edit, and a 0 there is a table with a
  /// header and no room for a single row. Below 3 the window is a stub; past 40 the list is taller than the
  /// screen and the cap has stopped meaning anything.</summary>
  public const int VisibleRowsMin = 3;

  /// <summary>Upper bound for <see cref="MaxVisibleRows"/>; see <see cref="VisibleRowsMin"/>.</summary>
  public const int VisibleRowsMax = 40;

  /// <summary>
  /// LEGACY. Superseded by <see cref="HiddenColumnMask"/>, which covers every column rather than Race alone.
  ///
  /// Kept as a property solely so <see cref="Migrate"/> can read it off a Version 0 config: the deserialiser
  /// discards JSON fields with no property behind them, so removing this would destroy the value before the
  /// migration ever saw it. Nothing reads it after the migration has run. Do not wire new code to it.
  /// </summary>
  public bool ShowRaceColumn { get; set; } = false;

  /// <summary>
  /// Bit N set = the column whose ScentColumn id is N is hidden. 0 = show every column.
  ///
  /// A mask rather than a list or a dictionary for the same reason <see cref="HiddenRaceMask"/> is one: it is a
  /// single atomic value the config serialiser round-trips as a number, and hiding one column while un-hiding
  /// another in the same pass moves it, which a Count could not.
  ///
  /// Persisted here rather than left to the header's ticks because the player table sets NoSavedSettings — the
  /// same flag that forces <see cref="SortColumn"/> to be hand-rolled — so ImGui writes no column layout to
  /// imgui.ini and forgets every tick on the way out.
  ///
  /// It has to be honoured before ImGui sees the column rather than corrected afterwards, because a disabled
  /// column cannot hold a sort order and ImGui answers one that does by substituting its own pick. See
  /// ScentWindow.DrawPlayerTable and ScentWindow.ApplyColumnVisibility.
  /// </summary>
  public uint HiddenColumnMask { get; set; } = 0;

  /// <summary>Column ids the mask can address. It is a uint, so bit 31 is the last.</summary>
  private const byte ColumnMaskBits = 32;

  /// <summary>Whether column id <paramref name="columnId"/> is hidden. Ids past bit 31 answer false rather than
  /// wrapping, for the shift-aliasing reason on <see cref="IsRaceHidden"/>.</summary>
  public bool IsColumnHidden(uint columnId)
    => columnId < ColumnMaskBits && (HiddenColumnMask & (1u << (int)columnId)) != 0;

  /// <summary>Sets or clears column id <paramref name="columnId"/>'s bit. Does not save. Ids past bit 31 are
  /// ignored, for the aliasing reason on <see cref="IsColumnHidden"/>.</summary>
  public void SetColumnHidden(uint columnId, bool hidden)
  {
    if (columnId >= ColumnMaskBits)
      return;

    if (hidden)
      HiddenColumnMask |= 1u << (int)columnId;
    else
      HiddenColumnMask &= ~(1u << (int)columnId);
  }

  /// <summary>"WAR" rather than "Warrior".</summary>
  public bool UseJobAbbreviations { get; set; } = true;

  /// <summary>
  /// Focus-target whoever's name the cursor is resting on, so the list can be read against the world.
  ///
  /// Off by default because it clobbers the focus target — the previous one is not restored on the way out,
  /// it is simply cleared. Reading it back would need the object table, which Draw may not touch, and an
  /// honest clear beats a restore this side of the thread split cannot actually perform.
  /// </summary>
  public bool FocusTargetOnHover { get; set; } = false;

  // ---- Halves ----

  /// <summary>
  /// Whether the nearby-player list is shown. UI only: the scanner keeps sniffing and the watcher log keeps
  /// recording either way, so switching a half back on brings its history and counts back intact rather than
  /// starting from nothing. Gating the scanner would make these toggles destructive, and they are not offered
  /// as such.
  /// </summary>
  public bool EnableNearbyList { get; set; } = true;

  /// <summary>
  /// Whether the watcher half is shown. UI only; see <see cref="EnableNearbyList"/>.
  ///
  /// Hides the eye column, the history section and the info bar's watcher count, and suppresses watcher alerts
  /// — an alert is user-facing output of a feature the user switched off, and a chat line from a half that is
  /// not on screen has nothing to explain it.
  /// </summary>
  public bool EnableWatchers { get; set; } = true;

  // ---- Scanning ----

  /// <summary>How often the scanner sniffs. Floored at 50ms in ScentScanner regardless of what is stored here.</summary>
  public int RescanIntervalMs { get; set; } = 250;

  // ---- Sort ----

  /// <summary>
  /// Watchers float to the top of whatever column sort is active. A primary key layered over the column
  /// sort, not a mutually exclusive sort mode — that layering is the whole point of the fusion.
  /// </summary>
  public bool WatchersFirst { get; set; } = true;

  /// <summary>Persisted ScentColumn. 2 is Name; ScentColumn starts at 1 because 0 is ImGui's "unset".</summary>
  public uint SortColumn { get; set; } = 2;

  public bool SortAscending { get; set; } = true;

  // ---- Filters ----

  public bool HideSelf { get; set; } = true;
  public bool HideParty { get; set; } = false;
  public bool HideFriends { get; set; } = false;
  public bool HideDead { get; set; } = false;

  /// <summary>Off by default because <see cref="AFK_ONLINE_STATUS_ID"/> is unverified.</summary>
  public bool HideAfk { get; set; } = false;

  /// <summary>Hides level 3 and below — the throwaway alts and bots milling around city aetherytes.</summary>
  public bool HideLowLevel { get; set; } = true;

  /// <summary>Bit N set = race N is hidden. 0 = show every race.</summary>
  /// <remarks>
  /// A mask rather than a list of hidden races because <see cref="FilterSignature"/> has to hash it exactly.
  /// A list could only contribute its Count — see the note there — and hiding one race while un-hiding another
  /// in the same pass leaves a Count unmoved, so the cached view would never rebuild and the filter would
  /// silently lag until something else happened to invalidate it.
  ///
  /// Unlike the search term, this keeps applying in HUD mode. It is persistent config with its own home in the
  /// config window's Filters tab, so a user who cannot reach the toolbar popup can still see and undo it;
  /// ScentWindow.EffectiveSearch blanks the search box precisely because a typed term has no such home.
  /// </remarks>
  public uint HiddenRaceMask { get; set; } = 0;

  /// <summary>Races the mask can address. It is a uint, so bit 31 is the last.</summary>
  private const byte RaceMaskBits = 32;

  /// <summary>
  /// Whether race <paramref name="raceId"/> is hidden.
  ///
  /// Ids past bit 31 answer false rather than wrapping: C# masks a shift count to the operand's width, so an
  /// unguarded race 32 would alias onto race 0's bit — and race 0 is the not-yet-loaded sentinel that must
  /// never be hidden.
  /// </summary>
  public bool IsRaceHidden(byte raceId) => raceId < RaceMaskBits && (HiddenRaceMask & (1u << raceId)) != 0;

  /// <summary>Sets or clears race <paramref name="raceId"/>'s bit. Does not save. Ids past bit 31 are ignored,
  /// for the aliasing reason on <see cref="IsRaceHidden"/>.</summary>
  public void SetRaceHidden(byte raceId, bool hidden)
  {
    if (raceId >= RaceMaskBits)
      return;

    if (hidden)
      HiddenRaceMask |= 1u << raceId;
    else
      HiddenRaceMask &= ~(1u << raceId);
  }

  /// <summary>0 = unlimited.</summary>
  public float MaxDistanceYalms { get; set; } = 0f;

  /// <summary>0 = unlimited. Truncation keeps the nearest, never "whoever sorted last".</summary>
  public int MaxPlayersShown { get; set; } = 100;

  /// <summary>
  /// Players the user never wants to see or hear about.
  ///
  /// Treat this list as immutable and swap the whole reference to change it — use
  /// <see cref="AddIgnoredPlayer"/> and <see cref="RemoveIgnoredPlayer"/>, never Add or Remove directly.
  /// The render thread edits the ignore list from the row menu and the config window, while the framework
  /// thread enumerates it in AlertService, and List throws mid-enumeration the moment the other thread adds.
  /// Reference assignment is atomic, so a reader always sees one whole version or the other. The setter is
  /// public only because the config deserializer needs it.
  /// </summary>
  public List<IgnoredPlayer> IgnoredPlayers { get; set; } = [];

  /// <summary>Copy-on-write; see the remarks on <see cref="IgnoredPlayers"/>. Does not save.</summary>
  public void AddIgnoredPlayer(IgnoredPlayer player) => IgnoredPlayers = [.. IgnoredPlayers, player];

  /// <summary>Copy-on-write; see the remarks on <see cref="IgnoredPlayers"/>. Does not save.</summary>
  public void RemoveIgnoredPlayer(IgnoredPlayer player)
    => IgnoredPlayers = [.. IgnoredPlayers.Where(existing => !ReferenceEquals(existing, player))];

  /// <summary>
  /// Players the user wants to spot immediately.
  ///
  /// Treat this list as immutable and swap the whole reference to change it — use
  /// <see cref="AddFocusedPlayer"/> and <see cref="RemoveFocusedPlayer"/>, never Add or Remove directly, under
  /// the same discipline and for the same reason as <see cref="IgnoredPlayers"/>: the render thread edits it
  /// from the row menu and the config window while the framework thread enumerates it in ScentScanner's
  /// arrival edge check. Reference assignment is atomic, so a reader always sees one whole version or the
  /// other. The setter is public only because the config deserializer needs it.
  ///
  /// Ignore beats focus wherever the two disagree. A player on both lists is not shown and not announced:
  /// ScentWindow.BuildView drops ignored rows before focus is ever consulted, and AlertService filters them
  /// out. One rule, stated once, rather than a precedence the user has to guess.
  /// </summary>
  public List<FocusedPlayer> FocusedPlayers { get; set; } = [];

  /// <summary>Copy-on-write; see the remarks on <see cref="FocusedPlayers"/>. Does not save.</summary>
  public void AddFocusedPlayer(FocusedPlayer player) => FocusedPlayers = [.. FocusedPlayers, player];

  /// <summary>Copy-on-write; see the remarks on <see cref="FocusedPlayers"/>. Does not save.</summary>
  public void RemoveFocusedPlayer(FocusedPlayer player)
    => FocusedPlayers = [.. FocusedPlayers.Where(existing => !ReferenceEquals(existing, player))];

  /// <summary>Whether <paramref name="row"/> is on the focus list. Reads the reference once, so a concurrent
  /// swap cannot tear the scan.</summary>
  public bool IsFocused(ScentRow row)
  {
    var focused = FocusedPlayers;
    foreach (var entry in focused)
    {
      if (entry.Matches(row))
        return true;
    }

    return false;
  }

  // ---- Colours ----

  public Vector4 ColorDefault { get; set; } = new(1f, 1f, 1f, 1f);
  public Vector4 ColorFriend { get; set; } = new(1.0f, 0.5f, 0.0f, 1f);
  public Vector4 ColorParty { get; set; } = new(0.0f, 0.7f, 1.0f, 1f);
  public Vector4 ColorSameFc { get; set; } = new(0.85f, 0.75f, 0.35f, 1f);

  /// <summary>The eye.</summary>
  public Vector4 ColorWatcher { get; set; } = new(0.90f, 0.20f, 0.20f, 1f);

  public bool HighlightWatcherRow { get; set; } = true;
  public float WatcherRowTintAlpha { get; set; } = 0.18f;

  /// <summary>The focus-list highlight. Deliberately not a red or an orange: it shares rows with
  /// <see cref="ColorWatcher"/>'s eye and tint, with <see cref="ColorFriend"/>'s orange and with
  /// <see cref="ColorSameFc"/>'s yellow, and a fourth warm colour on one line is four things shouting.</summary>
  public Vector4 ColorFocused { get; set; } = new(0.45f, 0.85f, 0.95f, 1f);

  /// <summary>Tint the whole row of a focus-list player, like <see cref="HighlightWatcherRow"/> does for
  /// watchers. Watcher tint wins on a row that is both: someone looking at you is the more urgent fact, and
  /// two washes on one row average into a colour that is neither.</summary>
  public bool HighlightFocusedRow { get; set; } = true;

  public float FocusedRowTintAlpha { get; set; } = 0.14f;

  public JobColorMode JobColorMode { get; set; } = JobColorMode.Role;

  public Vector4 RoleColorTank { get; set; } = new(0.0f, 0.6f, 1.0f, 1f);
  public Vector4 RoleColorHealer { get; set; } = new(0.0f, 0.8f, 0.133f, 1f);
  public Vector4 RoleColorMelee { get; set; } = new(0.706f, 0.0f, 0.0f, 1f);

  /// <summary>Deliberately not identical to <see cref="RoleColorMelee"/>: shipping both as the same red
  /// makes the setting a no-op that looks broken.</summary>
  public Vector4 RoleColorRanged { get; set; } = new(0.827f, 0.4f, 0.4f, 1f);

  public Vector4 RoleColorOther { get; set; } = new(0.5f, 0.5f, 0.5f, 1f);

  /// <summary>Per-job overrides keyed by ClassJob row id. Empty means fall back to the role colour.</summary>
  public Dictionary<uint, Vector4> JobColors { get; set; } = [];

  // ---- Alerts ----

  public bool AlertInChat { get; set; } = true;
  public bool AlertWithSound { get; set; } = false;

  /// <summary>&lt;se.1&gt;..&lt;se.16&gt;. Clamped to that range in AlertService.</summary>
  public int AlertSoundId { get; set; } = 1;

  /// <summary>Named for its unit. Multiplied by 1000 exactly once, in AlertService.</summary>
  public float AlertCooldownSeconds { get; set; } = 10f;

  public bool AlertForParty { get; set; } = false;
  public bool AlertForFriends { get; set; } = false;
  public bool AlertForAlliance { get; set; } = false;

  /// <summary>
  /// Announce a focus-list player arriving in range, once per visit.
  ///
  /// Off by default, and not negotiable: an alert that starts firing on its own after an update, for a list the
  /// user has not built yet, is a nasty surprise. Belongs to the nearby half — <see cref="EnableNearbyList"/>
  /// suppresses it, because arriving in range is a fact about the nearby list and not about who is watching.
  /// </summary>
  public bool AlertOnFocusArrival { get; set; } = false;

  // ---- History ----

  public bool KeepHistory { get; set; } = true;

  /// <summary>Current watchers are excluded from the trim — see WatcherLog.Trim.</summary>
  public int HistoryLimit { get; set; } = 10;

  public bool ShowTimestamps { get; set; } = true;
  public bool RecordWhileClosed { get; set; } = true;

  // ---- HUD ----

  public bool HudMode { get; set; } = false;
  public bool HudLocked { get; set; } = true;

  /// <summary>Adds NoInputs, which makes the Scent window unclickable including the control that would turn
  /// this back off. The /hscent hud command and the config window's own checkbox are the escape hatches;
  /// do not ship this without both.</summary>
  public bool HudClickThrough { get; set; } = false;

  public float HudOpacity { get; set; } = 0.65f;

  // ---- Misc ----

  public LodestoneRegion LodestoneRegion { get; set; } = LodestoneRegion.Europe;

  /// <summary>
  /// Hash of every option the view is filtered or sorted by. ScentWindow rebuilds its cached view when this
  /// changes, so a filter toggle applies on the next frame instead of whenever the next rescan happens to
  /// land.
  ///
  /// ADD EVERY NEW FILTER OPTION HERE. Leaving one out does not break the build and does not look like a
  /// bug — the setting simply appears to lag by up to a rescan interval, which is exactly the kind of thing
  /// that gets diagnosed as "ImGui being weird".
  ///
  /// EnableWatchers and EnableNearbyList are in here because they change the sort, not just the chrome:
  /// WatchersFirst is ignored while the watcher half is off, and a stale view would keep watchers floated to
  /// the top of a list that no longer admits they exist.
  ///
  /// The Count hashes catch adds and removes but not an in-place edit of an existing entry. That is sound only
  /// because the UI can only add or remove from either list; if an edit affordance is ever added, bump a
  /// revision counter on every mutation and hash that instead.
  ///
  /// HiddenColumnMask is deliberately NOT here. It changes which cells are drawn, never which rows survive or
  /// what order they are in, so rebuilding on it would be pure waste.
  /// </summary>
  public int FilterSignature() => HashCode.Combine(
    HashCode.Combine(HideSelf, HideParty, HideFriends, HideDead, HideAfk, HideLowLevel, MaxDistanceYalms, MaxPlayersShown),
    HashCode.Combine(WatchersFirst, UseJobAbbreviations, IgnoredPlayers.Count, HiddenRaceMask),
    HashCode.Combine(FocusedPlayers.Count, EnableNearbyList, EnableWatchers));

  public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
