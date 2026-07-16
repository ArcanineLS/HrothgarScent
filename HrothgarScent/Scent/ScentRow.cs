using System.Numerics;

namespace HrothgarScent.Scent;

/// <summary>
/// One nearby player, frozen at scan time. Every member is a value type or a string, and the record is
/// never mutated after construction — that is what lets the render thread read a published snapshot with
/// no lock. Adding a reference-typed or lazily-computed member reintroduces the data race.
///
/// There is deliberately no Address here. A pointer captured at scan time can be freed before the user
/// clicks (they may have zoned), and dereferencing it is a use-after-free. Actions re-resolve by
/// GameObjectId on the framework thread instead — see <see cref="PlayerActions"/>.
/// </summary>
public sealed record ScentRow(
  ulong GameObjectId,
  uint EntityId,
  ushort ObjectIndex,
  string Name,
  uint HomeWorldId,
  string HomeWorldName,
  uint JobId,
  string JobAbbreviation,
  string JobName,
  byte Level,
  byte RaceId,
  string RaceName,
  byte Sex,
  string CompanyTag,
  uint OnlineStatusId,
  float Distance,
  Vector3 Position,
  bool IsWatching,
  bool IsFriend,
  bool IsParty,
  bool IsAlliance,
  bool IsInCombat,
  bool IsDead,
  bool IsSelf,
  bool IsSameFreeCompany)
{
  /// <summary>
  /// Stable across object-table churn, unlike <see cref="GameObjectId"/>, which is recycled and changes on
  /// a zone. This is what keys the watcher history and the ignore list.
  /// </summary>
  public WatcherKey Key => new(Name, HomeWorldId);

  /// <summary>Name in the game's own "Name@World" form, for the clipboard and Lodestone.</summary>
  public string FullName => string.IsNullOrEmpty(HomeWorldName) ? Name : $"{Name}@{HomeWorldName}";
}

/// <summary>
/// Identity of a player across sessions. Name+HomeWorld rather than GameObjectId because the history must
/// outlive the game object: an id is recycled the moment its slot is reused, so an id-keyed history would
/// attribute a stranger's visit to whoever previously held the slot.
/// </summary>
public readonly record struct WatcherKey(string Name, uint HomeWorldId);
