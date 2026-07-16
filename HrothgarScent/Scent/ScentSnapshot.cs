using System.Collections.Generic;

namespace HrothgarScent.Scent;

/// <summary>
/// A complete frozen view of the nearby world, published to the render thread by one reference swap.
/// Immutable: <see cref="Rows"/> is fully populated before construction and never touched again, so a
/// reader sees either the whole old snapshot or the whole new one and never a half-written mixture.
/// </summary>
public sealed record ScentSnapshot(
  long Version,
  IReadOnlyList<ScentRow> Rows,
  IReadOnlyDictionary<ulong, ScentRow> ById,
  int NearbyCount,
  int WatcherCount,
  bool Valid,
  StareLevel MaxStareLevel,
  bool MarkedNearby)
{
  /// <summary>
  /// Published whenever there is nothing legitimate to show: logged out, zoning, or PvP. Valid=false lets
  /// the UI tell "no players nearby" apart from "not scanning" — the two look identical otherwise, and
  /// telling someone the coast is clear when we simply stopped looking is the one lie this plugin can't
  /// afford. Also the identity the scanner compares against to keep its reset idempotent.
  ///
  /// Every member spelled out rather than left to a default, and that is not style: this value is the PvP
  /// defence's second layer — the gates hide the surfaces, and this is what guarantees there is nothing behind
  /// them to hide. A member that silently defaulted would be one the next reader has to go and check.
  /// </summary>
  public static readonly ScentSnapshot Empty =
    new(0, [], new Dictionary<ulong, ScentRow>(), 0, 0, false, StareLevel.Glance, false);
}
