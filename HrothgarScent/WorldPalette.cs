using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using Lumina.Excel.Sheets;

namespace HrothgarScent;

/// <summary>
/// Home worlds, by name.
///
/// Exists for exactly one caller: the pencil that repairs a mark whose player transferred world. Everywhere
/// else the plugin already has a world name in hand — <see cref="Scent.ScentRow"/> resolves it at scan time
/// precisely so nothing downstream has to touch a sheet — so this is a NEW access pattern rather than a
/// shortcut to an existing one, and it is here because a mark's key is (name, world) and a repair that could
/// only fix the name would leave a transfer unrepairable forever.
///
/// Render thread. Excel reads do not need the framework thread, but they are not free either, so the table is
/// built once and frozen — the same discipline as <see cref="JobPalette"/> and <see cref="RacePalette"/>.
/// </summary>
public static class WorldPalette
{
  private static FrozenDictionary<string, uint>? _byName;

  /// <summary>
  /// The RowId of a world, or null if there is no such public world.
  ///
  /// Case-insensitive, unlike the plugin's name comparisons: a world is a place rather than a person, "sarg"
  /// and "Sarg" are the same server, and this is a thing the user types by hand rather than something the game
  /// hands over.
  /// </summary>
  public static uint? IdOf(string worldName)
    => !string.IsNullOrWhiteSpace(worldName) && Table().TryGetValue(worldName.Trim(), out var id) ? id : null;

  /// <summary>Every public world's name, for suggesting one back.</summary>
  public static IEnumerable<string> Names() => Table().Keys;

  private static FrozenDictionary<string, uint> Table() => _byName ??= Resolve();

  /// <summary>
  /// Reads the world sheet, once.
  ///
  /// The whole build is wrapped rather than each row, for the reason RacePalette states at length: GetExcelSheet
  /// itself throws — a missing sheet, an unsupported language, a column-hash mismatch after a patch — and an
  /// empty table costs the repair box its validation, while an escaping throw would cost the config window its
  /// frame. Failure is not retried, because the result is cached either way.
  /// </summary>
  private static FrozenDictionary<string, uint> Resolve()
  {
    try
    {
      var sheet = Plugin.DataManager.GetExcelSheet<World>();
      if (sheet is null)
        return FrozenDictionary<string, uint>.Empty;

      var worlds = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
      foreach (var world in sheet)
      {
        // IsPublic drops the test and dev worlds the sheet is full of. Without it the box would happily accept
        // a world nobody can log into, and hand back an id that will never match a scanned player.
        if (!world.IsPublic)
          continue;

        var name = world.Name.ExtractText();
        if (!string.IsNullOrWhiteSpace(name))
          worlds[name] = world.RowId;
      }

      return worlds.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }
    catch (Exception ex)
    {
      Plugin.Log.Warning(ex, "Could not read the world sheet; the mark repair box cannot check world names");
      return FrozenDictionary<string, uint>.Empty;
    }
  }
}
