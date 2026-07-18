using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using Lumina.Excel.Sheets;

namespace HrothgarScent;

/// <summary>
/// Resolves a Lodestone city-state NAME to the game's own local icon id, by reading the Town sheet at runtime.
///
/// The Town sheet carries a clean Name -> Icon column (unlike GrandCompany, which has no icon column at all and
/// so stays text), so the starting city-state a profile prints — "Limsa Lominsa", "Gridania", "Ul'dah" and the
/// rest — maps to a local game icon with no hardcoded ids, the same discipline as <see cref="WorldPalette"/>.
/// A name that resolves to nothing draws NO icon and the caller falls back to text: an invented icon id is never
/// the answer.
///
/// Render thread; the sheet read is not free, so the table is built once and frozen.
/// </summary>
public static class TownPalette
{
  private static FrozenDictionary<string, uint>? _iconByName;

  /// <summary>
  /// The local game-icon id for a city-state name, or null when it does not resolve — in which case the caller
  /// shows the name as text rather than a guessed icon. Case-insensitive to match the sheet against the
  /// Lodestone's own casing.
  /// </summary>
  public static uint? IconIdOf(string cityStateName)
    => !string.IsNullOrWhiteSpace(cityStateName) && Table().TryGetValue(cityStateName.Trim(), out var icon)
       ? icon
       : null;

  private static FrozenDictionary<string, uint> Table() => _iconByName ??= Resolve();

  /// <summary>
  /// Reads the Town sheet, once.
  ///
  /// The whole build is wrapped rather than each row, for the reason RacePalette states at length: GetExcelSheet
  /// throws on a missing sheet, an unsupported language, or a post-patch column-hash mismatch, and an empty table
  /// costs the city-state icon and nothing else (the name still draws). Failure is not retried, because the
  /// result is cached either way.
  /// </summary>
  private static FrozenDictionary<string, uint> Resolve()
  {
    try
    {
      var sheet = Plugin.DataManager.GetExcelSheet<Town>();
      if (sheet is null)
        return FrozenDictionary<string, uint>.Empty;

      var towns = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
      foreach (var town in sheet)
      {
        // Row 0 is the empty placeholder ("Nowheresville", Icon 0). A zero icon is no icon and must not be keyed.
        if (town.Icon <= 0)
          continue;

        var name = town.Name.ExtractText();
        if (!string.IsNullOrWhiteSpace(name))
          towns[name] = (uint)town.Icon;
      }

      return towns.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }
    catch (Exception ex)
    {
      Plugin.Log.Warning(ex, "Town sheet unreadable; the city-state icon will fall back to text");
      return FrozenDictionary<string, uint>.Empty;
    }
  }
}
