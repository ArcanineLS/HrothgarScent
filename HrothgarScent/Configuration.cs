using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Configuration;
using HrothgarScent.Scent;
using Newtonsoft.Json;

namespace HrothgarScent;

/// <summary>Whether job colours come from four role buckets or from a per-job override table.</summary>
public enum JobColorMode
{
  Role,
  Job,
}

/// <summary>How much of the plugin reaches the nameplates over players' heads.</summary>
public enum NameplateMode
{
  /// <summary>Nothing. The default, and deliberately: writing on the game's own world UI is somebody else's
  /// screen, and a plugin that starts doing it uninvited is a bad guest.</summary>
  Off,

  /// <summary>Colour the name of whoever is targeting you, and nothing else.</summary>
  Watchers,
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
  public const uint Mark = 9;
}

/// <summary>
/// LEGACY. A player the user never wants to see, as pre-marks builds stored it.
///
/// A pure deserialisation carrier now: <see cref="Plugin.Marks"/> owns this fact, as one flag on a
/// <see cref="MarkedPlayer"/>. Configuration.Migrate reads these once into the store and then nothing ever
/// looks at them again — see the remarks on <see cref="Configuration.IgnoredPlayers"/> for why they are kept
/// on disk rather than emptied.
///
/// Deliberately down to fields and a parameterless constructor. Its Matches and FullName went with the code
/// that called them; MarkedPlayer carries both now. Do not add behaviour back to a carrier.
/// </summary>
[Serializable]
public sealed class IgnoredPlayer
{
  public string Name { get; set; } = string.Empty;
  public uint HomeWorldId { get; set; }
  public string HomeWorldName { get; set; } = string.Empty;
}

/// <summary>
/// LEGACY. A player the user wanted picked out of the crowd, as pre-marks builds stored it. A pure
/// deserialisation carrier; see <see cref="IgnoredPlayer"/>.
/// </summary>
[Serializable]
public sealed class FocusedPlayer
{
  public string Name { get; set; } = string.Empty;
  public uint HomeWorldId { get; set; }
  public string HomeWorldName { get; set; } = string.Empty;
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
  public const int CurrentVersion = 2;

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

    if (Version < 2)
    {
      // The store refuses to write when marks.json was left by a newer build, or could not be read. Importing
      // into memory anyway would look like it worked and evaporate on restart — and stamping the new version
      // would make that permanent, because Migrate runs once and never again. Leave the version alone and retry
      // next launch; everything above this point is idempotent, so re-running costs nothing.
      if (Plugin.Marks.IsReadOnly)
      {
        Plugin.Log.Warning(
          "Marks are read-only, so the focus and ignore lists were not imported; leaving config at version {Version} to retry",
          Version);
        return;
      }

      ImportListsIntoMarks();

      // THE ORDER HERE IS THE WHOLE POINT, and it is not obvious. Save() below is synchronous, while the
      // store's own writes are debounced by seconds — so stamping the version first would record "the
      // migration happened" a full two seconds before marks.json existed. A crash in that window (a client
      // crash right after a plugin update is a routine event) leaves a config that will never migrate again and
      // no file to have migrated into: every focus and every ignore gone for good, and the players the user
      // ignored back in their list, firing alerts at them.
      //
      // Gated on the result rather than fired and forgotten, because a write can also simply fail — a full
      // disk, a permissions problem — and Flush swallows that to a log line. Refusing the stamp means the same
      // thing as the IsReadOnly branch above: the legacy lists are still on disk, still authoritative, and the
      // import is retried next launch. The import is additive, so a retry merges rather than duplicates.
      if (!Plugin.Marks.Flush())
      {
        Plugin.Log.Warning(
          "marks.json could not be written, so the import is not durable; leaving config at version {Version} to retry",
          Version);
        return;
      }
    }

    Version = CurrentVersion;
    Save();
  }

  /// <summary>
  /// Folds the legacy focus and ignore lists into <see cref="Plugin.Marks"/>, which now owns both.
  ///
  /// THE LISTS ARE LEFT POPULATED, and that is not an oversight. This runs before <see cref="Save"/> writes
  /// Version 2, while marks.json is written asynchronously — so emptying them here, saving, and then dying
  /// before that write lands would destroy the user's focus and ignore lists permanently: Version would already
  /// read 2, so Migrate would never run again, and there would be no marks.json to have imported. Leaving them
  /// costs a few dead entries in the config file and makes both a crash and a downgrade non-events. It is
  /// exactly what <see cref="ShowRaceColumn"/> already does one field up — read once, then simply never read
  /// again.
  ///
  /// Additive rather than overwriting: a mark already carrying a note keeps it and merely gains the flag. A
  /// player on both legacy lists lands as one record with both flags, which is what the two lists always meant
  /// and could never say.
  /// </summary>
  private void ImportListsIntoMarks()
  {
    foreach (var entry in FocusedPlayers)
      Plugin.Marks.Update(new WatcherKey(entry.Name, entry.HomeWorldId), entry.HomeWorldName,
        mark => mark with { Marks = mark.Marks | MarkKind.Focus });

    foreach (var entry in IgnoredPlayers)
      Plugin.Marks.Update(new WatcherKey(entry.Name, entry.HomeWorldId), entry.HomeWorldName,
        mark => mark with { Marks = mark.Marks | MarkKind.Ignore });

    if (FocusedPlayers.Count + IgnoredPlayers.Count > 0)
      Plugin.Log.Information("Imported {Focus} focused and {Ignore} ignored players into marks.json",
        FocusedPlayers.Count, IgnoredPlayers.Count);
  }

  // ---- Window ----

  public bool OpenOnLogin { get; set; } = false;
  public bool HideInCombat { get; set; } = false;
  public bool HideInDuty { get; set; } = false;
  public bool HideInCutscene { get; set; } = true;
  public bool ShowSearchBar { get; set; } = true;

  /// <summary>
  /// Adds "Remember this Player" to the game's own right-click menu wherever it names a player.
  ///
  /// On by default — it is the one surface that reaches players the scanner cannot see at all, and a durable
  /// mark only ever happens because the user deliberately clicked it. Opt-OUT rather than opt-in because
  /// nothing is captured until that click; the toggle exists because injecting into the game's menus is
  /// somebody else's screen real estate, and a plugin that does it without an off switch is a bad guest.
  /// </summary>
  public bool ShowContextMenuMark { get; set; } = true;
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
  /// The game's own icon beside each job name.
  ///
  /// Deliberately NOT in <see cref="FilterSignature"/>, for exactly the reason <see cref="HiddenColumnMask"/>
  /// is not: it changes which cells are drawn, never which rows survive or what order they are in, so rebuilding
  /// the view on it would be pure waste. It does not touch the sort either — ScentColumn.Job keys on the
  /// rendered STRING, and an icon has no string.
  /// </summary>
  public bool ShowJobIcons { get; set; } = true;

  /// <summary>
  /// Whether the watcher eye reaches the nameplate over a player's head.
  ///
  /// OFF by default and staying that way. Nameplate modification is the most visible thing a plugin can do and
  /// is scrutinised at Dalamud review; it is also the one surface where being wrong is wrong in front of other
  /// people. The user asks for this or it does not happen.
  ///
  /// Cosmetic only — a colour on a name, nothing added, nothing hidden. See NameplateService.
  /// </summary>
  public NameplateMode NameplateMode { get; set; } = NameplateMode.Off;

  /// <summary>
  /// Whether a focused player's own colour reaches their nameplate too.
  ///
  /// A SEPARATE FLAG rather than a third <see cref="NameplateMode"/>, and the reason is the master switch above
  /// it. That one is a checkbox that writes its enum on every tick, so a mode carrying this choice would be
  /// silently reset to plain Watchers the first time anyone unticked "show the eye" and ticked it back. A
  /// preference the UI destroys by accident is worse than one that is merely off.
  ///
  /// SUBORDINATE to it in effect, though: NameplateMode is the answer to "may this plugin write on the game's
  /// world UI at all", and nothing here is painted while that is Off.
  ///
  /// Off by default, on the same reasoning as its master — this is other people's screens.
  /// </summary>
  public bool NameplateMarkColors { get; set; } = false;

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
  /// Hides the eye column, the history section, the info bar's watcher count and the nameplate colour, and
  /// suppresses watcher alerts — an alert is user-facing output of a feature the user switched off, and a chat
  /// line from a half that is not on screen has nothing to explain it. ADD EVERY NEW WATCHER SURFACE HERE:
  /// this doc is the list, and a surface missing from it is one that keeps talking after the half goes quiet.
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
  /// LEGACY. Superseded by <see cref="Plugin.Marks"/>, which now owns focus and ignore as two flags on one
  /// record — see <see cref="MarkKind"/> for why they stayed independent flags rather than becoming a
  /// tri-state.
  ///
  /// Read by <see cref="ImportListsIntoMarks"/> on the one launch that migrates, and thereafter by exactly one
  /// other caller: MarkStore.SeedFromLegacyLists, which rebuilds what it can when marks.json cannot be read at
  /// all. That second reader is the reason these are kept — and deliberately left POPULATED after the import.
  /// They go stale the moment anything is marked afterwards, but a stale ignore list still suppresses the
  /// person the user asked never to see, and an empty one does not. Suppression fails safe or it is not a
  /// promise. Leaving them also makes a downgrade to a pre-marks build harmless, since that build finds exactly
  /// the lists it expects.
  ///
  /// The corollary is a real limitation, and it is the price of that safety: a player forgotten AFTER the
  /// migration is still named here, so the recovery path can resurrect them. That only happens on a launch that
  /// already told the user their marks file is unreadable, which is the one moment over-restoring beats
  /// under-restoring.
  ///
  /// Do not wire new code to it. The setter is public only because the config deserialiser needs it.
  /// </summary>
  public List<IgnoredPlayer> IgnoredPlayers { get; set; } = [];

  /// <summary>
  /// LEGACY. Superseded by <see cref="Plugin.Marks"/>; see the remarks on <see cref="IgnoredPlayers"/>.
  ///
  /// The old note here explained that ignore beats focus wherever the two disagree. That rule still holds and
  /// now lives where it is applied — ScentWindow.BuildView drops ignored rows before focus is consulted, and
  /// AlertService filters them out — but the two facts are one record's two flags rather than two lists.
  /// </summary>
  public List<FocusedPlayer> FocusedPlayers { get; set; } = [];

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
  /// Say something again when a watcher has held you this long without looking away.
  ///
  /// 15 rather than something tighter, and the number is NOT free: a fresh watcher fires an alert at the
  /// instant they target you, which burns <see cref="AlertCooldownSeconds"/> (default 10). An escalation
  /// arriving inside that window is dropped, silently — so any default at or under the cooldown ships a
  /// feature that never fires on a stock install and looks broken rather than absent. 15 clears the default
  /// cooldown with room to spare.
  ///
  /// The cooldown is user-configurable up to 60s, so a user who raises it can still starve this. That is the
  /// one-shared-cooldown design working as documented (see AlertService), not a bug here — the fix is a
  /// priority-ordered alert bus, not a second cooldown.
  ///
  /// 0 disables the rung entirely; see <see cref="StareLevelOf"/>.
  /// </summary>
  public int StareSeconds { get; set; } = 15;

  /// <summary>
  /// Say something one last time when a watcher has held you this long. 0 disables the rung.
  ///
  /// Set BELOW <see cref="StareSeconds"/> it simply wins: <see cref="StareLevelOf"/> tests it first, so an
  /// inverted pair costs the middle rung — the top one fires, at the lower number, and Stare becomes
  /// unreachable. That is a strange thing to have configured but not a broken one, and it is why the check is
  /// ordered top-down rather than trying to police the pair.
  /// </summary>
  public int FixateSeconds { get; set; } = 45;

  /// <summary>Announce a watcher's escalation, not merely their arrival.</summary>
  public bool AlertOnStareEscalation { get; set; } = true;

  /// <summary>
  /// Which rung <paramref name="accumulatedMs"/> has reached.
  ///
  /// Thresholds, not a range, and each guarded on being positive: 0 means "never fire this rung", and an
  /// unguarded 0 would mean "fire immediately" — the exact inversion the design brief flagged in the stale-mark
  /// sentinel. Read in descending order so the top rung wins when the two are set inside out.
  /// </summary>
  public StareLevel StareLevelOf(long accumulatedMs)
    => FixateSeconds > 0 && accumulatedMs >= FixateSeconds * 1000L ? StareLevel.Fixation
     : StareSeconds > 0 && accumulatedMs >= StareSeconds * 1000L ? StareLevel.Stare
     : StareLevel.Glance;

  /// <summary>
  /// Announce a focus-list player arriving in range, once per visit.
  ///
  /// Off by default, and not negotiable: an alert that starts firing on its own after an update, for a list the
  /// user has not built yet, is a nasty surprise. Belongs to the nearby half — <see cref="EnableNearbyList"/>
  /// suppresses it, because arriving in range is a fact about the nearby list and not about who is watching.
  /// </summary>
  public bool AlertOnFocusArrival { get; set; } = false;

  /// <summary>
  /// Note when and where you last saw a player you have marked.
  ///
  /// On by default, and the default is the argument: you already told Hrothgar to remember this person, and
  /// "when did I last run into them" is what remembering someone means. It only ever applies to players you
  /// pointed at, it overwrites one line rather than keeping a history, and it never creates a record on its own
  /// — see MarkStore.RecordSeen.
  ///
  /// The switch exists anyway, because this is the one thing the plugin writes down that you did not type. A
  /// user who wants notes and nothing else is entitled to notes and nothing else. Off does not delete what is
  /// already stored — see the Marks table's own button for that, since silently erasing data on a checkbox is
  /// worse than keeping it.
  /// </summary>
  public bool RememberLastSeen { get; set; } = true;

  /// <summary>
  /// Record EVERY player who comes near you into a durable log the journal can browse — not just the ones you
  /// deliberately marked.
  ///
  /// OFF by default, and that default is the plugin's whole ethos in one switch. Everywhere else this plugin
  /// refuses to write a stranger to disk by proximity — it is the anti-pattern the design was built against (see
  /// <see cref="Scent.MarkStore"/>). This is the deliberate opt-OUT of that refusal, for a user who wants an
  /// "everyone I've met" log and accepts the trade: the names and worlds of strangers, kept on their own disk. It
  /// is a SEPARATE store (<see cref="Scent.SeenLog"/>, seen.json). By default it is UNLIMITED — it keeps everyone,
  /// PlayerTrack-style — but <see cref="NearbyLogLimit"/> can put a cap back on. The marks store is untouched by it.
  /// </summary>
  public bool RecordAllNearby { get; set; } = false;

  /// <summary>
  /// A cap on how many nearby players <see cref="Scent.SeenLog"/> keeps, or 0 for UNLIMITED (the default). When
  /// positive, the least-recently-seen are evicted past it; enforced on write as well as at the slider, since it
  /// round-trips through a hand-editable file. A positive value is floored at <see cref="NearbyLogLimitMin"/> so a
  /// tiny cap cannot thrash; 0 disables eviction entirely.
  /// </summary>
  public int NearbyLogLimit { get; set; } = 0;

  /// <summary>Floor for a POSITIVE cap; 0 means unlimited and bypasses this.</summary>
  public const int NearbyLogLimitMin = 50;

  public const int NearbyLogLimitMax = 20000;

  /// <summary>
  /// Dim a mark Hrothgar has not seen in this many days, so a record that has quietly stopped matching anybody
  /// can be found and fixed. 0 never dims.
  ///
  /// 30 because that is well past "they are on holiday" and well short of "I have forgotten who this is". The
  /// point is not to expire anything — nothing is ever deleted by time here — it is to make the one failure
  /// marks CAN suffer visible: a rename or a world transfer silently orphans the record, and without this the
  /// only symptom is a highlight that never appears again.
  /// </summary>
  public int MarkStaleDays { get; set; } = 30;

  /// <summary>
  /// Record a duty you cleared together on a marked player's Encounters, aggregated one row per fight.
  ///
  /// ONLY players already marked, and it never creates a record: a duty completing is not a deliberate user
  /// act, so it cannot be the reason someone is remembered. Everyone else in the party stays a stranger.
  ///
  /// On by default, on the same argument as LastSeen: you already told Hrothgar to remember this person, and
  /// "we cleared this together" is the kind of thing remembering someone is FOR. It lands as a structured
  /// <see cref="Scent.DutyEncounter"/> — one row per fight, so it stays bounded no matter how many times you run
  /// it — read by the profile's Encounters tab. The note is left alone for what the user actually typed.
  /// </summary>
  public bool RememberDutyClears { get; set; } = true;

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
  /// Whether a profile may fetch that player's face from the Lodestone.
  ///
  /// The ONLY setting in this plugin that governs a network request, which is why it exists at all rather than
  /// being implied by the profile: everything else here reads the game client and nothing leaves the machine.
  /// Some users will not want their client talking to a web server about who they looked up, and that is a
  /// legitimate position even though the page is the player's own public profile and the plugin already ships a
  /// button that opens it in their browser.
  ///
  /// On by default, on the same reasoning as that button: it is public data the player published, one request
  /// per profile the user deliberately opened. Off costs the face and nothing else — the profile draws the job
  /// icon instead and says nothing about why.
  /// </summary>
  public bool ShowLodestonePortraits { get; set; } = true;

  /// <summary>
  /// Whether opening a profile also loads the full character page, without waiting for a click.
  ///
  /// OFF by default, and that default is the whole reason it is a setting. Opening a profile ALREADY fires one
  /// Lodestone request — the face lookup — automatically. This adds a second per open, and the Lodestone has no
  /// API and rate-limits scraping: trip the limit and the face stops loading too, so over-fetching does not
  /// degrade gracefully, it goes dark. The click keeps the expensive full page opt-in for the many opens that
  /// are a quick glance to jot a note. On, for the user who opens few profiles and wants them complete.
  ///
  /// SUBORDINATE to <see cref="ShowLodestonePortraits"/>: that switch answers "may this plugin reach the
  /// Lodestone at all", and nothing is fetched while it is off. Also naturally conservative even when on — the
  /// fetch fires from the Info/Jobs tab's own draw, so a profile only ever opened to Notes spends no request.
  /// </summary>
  public bool AutoLoadLodestoneProfile { get; set; } = false;

  /// <summary>
  /// Automatically capture a player's rendered portrait from their Adventurer Plate when you open it, and save
  /// it, so clicking their profile picture later shows the face they published instead of only the flat
  /// Lodestone thumbnail.
  ///
  /// OFF by default, and that default is load-bearing twice over. It reads the game's rendered plate texture —
  /// deeper into the client than anything else here — so it is opt-in on the same "your client, your call"
  /// reasoning as <see cref="ShowLodestonePortraits"/>. And it is the one feature in the plugin that could not be
  /// exercised without the running game before shipping, so it stays experimental until the user turns it on and
  /// confirms it. Nothing is captured, and the plate is not even read, while this is off.
  ///
  /// Clicking a profile picture captures on demand whenever a plate is open regardless of this switch — that is
  /// the deliberate per-click path; this only governs the automatic "grab it as I open plates" convenience.
  ///
  /// UNLIKE the Lodestone face, a captured portrait IS written to disk (a PNG per player under the config
  /// folder), so it survives restarts — a durable record, but one authored by the user's own click, exactly as a
  /// mark is. See <see cref="Scent.PortraitService"/>.
  /// </summary>
  public bool CaptureInGamePortraits { get; set; } = false;

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
  /// The focus and ignore Counts used to be hashed here. They are gone because the lists are gone — marks own
  /// both facts now, and a mark's note, colour and flags are all edited IN PLACE, which no Count can see. This
  /// note used to ask for a revision counter for exactly that; MarksIndex.Revision is it, and it is hashed by
  /// ScentWindow.RebuildViewIfStale rather than here, so the revision and the entries it describes arrive as one
  /// reference and cannot be read at two different versions. Do not add a mark term back to this method.
  ///
  /// HiddenColumnMask is deliberately NOT here. It changes which cells are drawn, never which rows survive or
  /// what order they are in, so rebuilding on it would be pure waste.
  /// </summary>
  public int FilterSignature() => HashCode.Combine(
    HashCode.Combine(HideSelf, HideParty, HideFriends, HideDead, HideAfk, HideLowLevel, MaxDistanceYalms, MaxPlayersShown),
    HashCode.Combine(WatchersFirst, UseJobAbbreviations, HiddenRaceMask),
    HashCode.Combine(EnableNearbyList, EnableWatchers));

  public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
