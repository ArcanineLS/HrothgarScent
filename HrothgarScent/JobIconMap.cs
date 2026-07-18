using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using Lumina.Excel.Sheets;

namespace HrothgarScent;

/// <summary>
/// Resolves a Lodestone job NAME to the game's own job-icon id, by reading the ClassJob sheet at runtime.
///
/// No ClassJob row ids are hardcoded — the same discipline as <see cref="RacePalette"/> and
/// <see cref="WorldPalette"/> — because the profile draws the icon from a name the page prints, not from a
/// number the plugin carries. The Lodestone prints the full job name ("Paladin") on a levelled slot and the
/// BASE-CLASS name ("Gladiator") on an unlevelled one, so the map is keyed on the sheet's Name, NameEnglish AND
/// Abbreviation: Name/NameEnglish cover both the job and its base class, and keying English too keeps English
/// pages resolving against a non-English client (the one overlap those two languages still share). A name that
/// resolves to nothing draws NO icon — a wrong icon is worse than none in a plugin about who is near you.
///
/// Render thread, and the sheet read is not free, so the table is built once and frozen — exactly as
/// <see cref="WorldPalette"/> does.
/// </summary>
public static class JobIconMap
{
  /// <summary>
  /// The base of the game's job-icon range: the icon id is this plus the ClassJob RowId. NOT a guess — it is the
  /// exact shape ProfileWindow.DrawAvatar and ScentWindow.DrawJobIcon already draw the live job with, against a
  /// RowId, and the non-throwing TryGetFromGameIcon is what makes a wrong id draw nothing rather than crash.
  /// </summary>
  private const uint JobIconBase = 62100;

  private static FrozenDictionary<string, uint>? _rowByName;

  /// <summary>
  /// The game job-icon id for a Lodestone job name, or null when the name does not resolve.
  ///
  /// Null means "draw no icon", never "draw a default": the caller keeps the row's text and simply omits the
  /// picture. Case-insensitive because the sheet stores names lowercase ("paladin") while the Lodestone prints
  /// them title-case ("Paladin").
  /// </summary>
  public static uint? IconIdFor(string lodestoneName)
    => !string.IsNullOrWhiteSpace(lodestoneName) && Table().TryGetValue(lodestoneName.Trim(), out var rowId)
       ? JobIconBase + rowId
       : null;

  private static FrozenDictionary<string, uint> Table() => _rowByName ??= Resolve();

  /// <summary>
  /// Reads the ClassJob sheet, once.
  ///
  /// The whole build is wrapped rather than each row, for the reason RacePalette states at length: GetExcelSheet
  /// itself throws — a missing sheet, an unsupported language, a column-hash mismatch after a patch — and an
  /// empty table costs the job icons and nothing else (the names still draw), while an escaping throw would cost
  /// the profile window its frame. Failure is not retried, because the result is cached either way.
  /// </summary>
  private static FrozenDictionary<string, uint> Resolve()
  {
    try
    {
      var sheet = Plugin.DataManager.GetExcelSheet<ClassJob>();
      if (sheet is null)
        return FrozenDictionary<string, uint>.Empty;

      var map = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
      foreach (var row in sheet)
      {
        // Row 0 is the "no class" sentinel — it has no name and no icon, and must never claim one.
        if (row.RowId == 0)
          continue;

        Add(map, row.Name.ExtractText(), row.RowId);
        Add(map, row.NameEnglish.ExtractText(), row.RowId);
        Add(map, row.Abbreviation.ExtractText(), row.RowId);
      }

      return map.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }
    catch (Exception ex)
    {
      Plugin.Log.Warning(ex, "ClassJob sheet unreadable; job icons will be absent");
      return FrozenDictionary<string, uint>.Empty;
    }
  }

  /// <summary>Keys a name onto a row id, skipping blanks. Last write wins, which is a non-issue: names and
  /// abbreviations are unique per row and never collide across the three forms.</summary>
  private static void Add(Dictionary<string, uint> map, string key, uint rowId)
  {
    if (!string.IsNullOrWhiteSpace(key))
      map[key.Trim()] = rowId;
  }
}
