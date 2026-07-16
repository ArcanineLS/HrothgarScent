using System;
using System.Collections.Generic;
using System.Threading;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;

namespace HrothgarScent;

/// <summary>
/// One race as offered by the race filter. Both name forms are resolved up front, so picking between them
/// costs a field read rather than a sheet lookup.
/// </summary>
public readonly record struct RaceInfo(byte RaceId, string Masculine, string Feminine)
{
  /// <summary>
  /// The label for a per-race control. The filter is keyed by race alone and not by race+sex, so it needs one
  /// form; masculine is the conventional citation form, and in English the two are identical anyway.
  /// </summary>
  public string Name => Masculine;

  /// <summary>The form matching a character's Sex byte. Every value other than
  /// <see cref="RacePalette.FeminineSex"/> reads as masculine, including the 0 an unloaded character carries.</summary>
  public string NameFor(byte sex) => sex == RacePalette.FeminineSex ? Feminine : Masculine;
}

/// <summary>
/// Maps a Race row id to a display name, with no Excel lookups in Draw or in the scan loop.
/// </summary>
public static class RacePalette
{
  /// <summary>
  /// The row id of a character whose appearance has not reached the client yet — the state of anyone still
  /// loading in at render distance, and by far the most common non-match here. It is not a race, and it is
  /// deliberately never hidden by the race filter; see ScentWindow.BuildView.
  /// </summary>
  public const byte UnknownRaceId = 0;

  /// <summary>The Sex byte's feminine value.</summary>
  public const byte FeminineSex = 1;

  /// <summary>Label for <see cref="UnknownRaceId"/> and for any row id outside <see cref="RaceTable"/>.</summary>
  private const string UnknownName = "Unknown";

  /// <summary>
  /// The eight playable races, table-driven for the same reason JobPalette's jobs are: the row ids are well
  /// known, but the sheet is not guaranteed to be readable, and a hardcoded English name means a sheet miss
  /// costs a wrong label rather than a crash. These names are the fallback only — <see cref="Resolve"/>
  /// replaces them with the sheet's own, in the client's language, on first use.
  /// </summary>
  private static readonly RaceInfo[] RaceTable =
  [
    new(1, "Hyur", "Hyur"),
    new(2, "Elezen", "Elezen"),
    new(3, "Lalafell", "Lalafell"),
    new(4, "Miqo'te", "Miqo'te"),
    new(5, "Roegadyn", "Roegadyn"),
    new(6, "Au Ra", "Au Ra"),
    new(7, "Hrothgar", "Hrothgar"),
    new(8, "Viera", "Viera"),
  ];

  /// <summary>The resolved table, or null until first use.</summary>
  private static RaceInfo[]? _resolved;

  /// <summary>
  /// Guards the one-time <see cref="Resolve"/>. The scanner (framework thread) and the config window (render
  /// thread) can each be the first to ask, and whichever loses the race must wait for the winner's table
  /// rather than read a half-built one.
  /// </summary>
  private static readonly object _resolveGate = new();

  /// <summary>
  /// Every race the filter offers, in row-id order. Exposed so the config window and the toolbar popup can
  /// build their checkboxes without reaching for the Race sheet from Draw.
  /// </summary>
  public static IReadOnlyList<RaceInfo> Races => Resolved;

  /// <summary>
  /// The resolved table, built on first use and read lock-free forever after.
  ///
  /// Double-checked: the volatile read is the whole of the steady-state cost, and the lock is only ever
  /// contended by the one pair of threads that arrive before the first publication. The array is immutable
  /// once published, so no reader needs the lock to hold it.
  /// </summary>
  private static RaceInfo[] Resolved
  {
    get
    {
      // Volatile: the strings must not be reordered after the reference that publishes them, or another
      // thread could hold the array while its contents are still being written.
      var cached = Volatile.Read(ref _resolved);
      if (cached is not null)
        return cached;

      lock (_resolveGate)
      {
        cached = _resolved;
        if (cached is null)
        {
          cached = Resolve();
          Volatile.Write(ref _resolved, cached);
        }

        return cached;
      }
    }
  }

  /// <summary>
  /// Display name for a race row id, in the form matching <paramref name="sex"/>.
  ///
  /// An unrecognised id answers <see cref="UnknownName"/> rather than an empty string: this renders straight
  /// into a table cell, and a blank there reads as a bug rather than as missing data.
  /// </summary>
  public static string NameOf(byte raceId, byte sex)
  {
    foreach (var race in Resolved)
    {
      if (race.RaceId == raceId)
        return race.NameFor(sex);
    }

    return UnknownName;
  }

  /// <summary>
  /// Reads the sheet's own names over the fallback table, once.
  ///
  /// The whole build is wrapped rather than each row: GetExcelSheet throws — on a missing sheet, an
  /// unsupported language, a column-hash mismatch after a patch — and the first caller is normally the scan,
  /// whose handler answers a throw by dropping to the empty snapshot. Unguarded, an unreadable sheet would
  /// therefore empty the window on every tick forever. Falling back costs a wrong label in one language and
  /// keeps the plugin working. Failure is not retried, because the result is cached either way.
  /// </summary>
  private static RaceInfo[] Resolve()
  {
    try
    {
      var sheet = Plugin.DataManager.GetExcelSheet<Race>();
      var resolved = new RaceInfo[RaceTable.Length];

      for (var i = 0; i < RaceTable.Length; i++)
      {
        var fallback = RaceTable[i];

        // GetRowOrDefault, never GetRow: GetRow throws on a row the sheet does not have, and one missing race
        // is not worth losing the other seven to.
        var row = sheet?.GetRowOrDefault(fallback.RaceId);
        resolved[i] = new RaceInfo(
          fallback.RaceId,
          Text(row?.Masculine, fallback.Masculine),
          Text(row?.Feminine, fallback.Feminine));
      }

      return resolved;
    }
    catch (Exception ex)
    {
      Plugin.Log.Error(ex, "Race sheet unreadable; falling back to built-in race names");
      return RaceTable;
    }
  }

  /// <summary>
  /// A sheet string, or <paramref name="fallback"/> if the row is missing or its cell is blank. An empty name
  /// would reach the UI as an unlabelled checkbox and an empty table cell, both of which are worse than the
  /// English word.
  /// </summary>
  private static string Text(ReadOnlySeString? value, string fallback)
  {
    if (value is null)
      return fallback;

    var text = value.Value.ExtractText();
    return string.IsNullOrWhiteSpace(text) ? fallback : text;
  }
}
