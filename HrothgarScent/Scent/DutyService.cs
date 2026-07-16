using System;
using Dalamud.Game.DutyState;
using Lumina.Excel.Sheets;

namespace HrothgarScent.Scent;

/// <summary>
/// "You cleared this together."
///
/// The prior art cannot say this, and not for want of trying: its codebase is current — it injects nameplates,
/// context menus and interop — but it does not inject IDutyState at all. Its encounter is opened on a TERRITORY
/// change and stores a territory id, so it structurally cannot tell "we cleared an Ultimate together" from "we
/// stood in Limsa". DutyCompleted means CLEARED, and it carries the ContentFinderCondition, so the record can
/// name the fight. The noise problem solves itself: the event IS the filter, so there is no per-zone capture
/// policy to configure and no matrix to get wrong.
///
/// DECORATION, NOT CAPTURE. This appends a line to the note of a player ALREADY IN THE MARK STORE, and creates
/// nothing. A duty completing is not a deliberate user act, so it cannot be the reason a record exists — that
/// is the store's one rule. Everyone else in the party is a stranger who happened to be there, and they stay
/// strangers.
/// </summary>
public sealed class DutyService : IDisposable
{
  /// <summary>
  /// The last territory a clear was recorded for, so a second raise cannot double-write.
  ///
  /// Belt-and-braces rather than the guard: Dalamud already de-dupes this event — it raises from two ActorControl
  /// cases, both behind a flag it resets on a territory change — so DutyCompleted fires at most once per
  /// territory no matter how the fight went. This exists because that is THEIR invariant, not ours, and a
  /// duplicated "cleared X" line in a user's note is not something a log line would ever reveal.
  /// </summary>
  private uint _lastClearedTerritory;

  public void Dispose() => Plugin.DutyState.DutyCompleted -= OnDutyCompleted;

  public void Subscribe() => Plugin.DutyState.DutyCompleted += OnDutyCompleted;

  /// <summary>
  /// The clear.
  ///
  /// THREADING IS NOT A GIVEN HERE, and the comment must not pretend otherwise. This is raised from inside a
  /// hook on the game's ActorControl packet handler — inline, synchronously, before the original call — and NOT
  /// from Framework.Update. Whether the game happens to dispatch that packet on the main thread is not
  /// knowable from the assemblies; it is probably true and it is not verified. So this method touches nothing
  /// that cares: it reads two plain struct fields off the args, which cannot throw and cannot reach game
  /// memory, and hands everything else to the framework thread. RunOnFrameworkThread costs nothing when we are
  /// already on it.
  ///
  /// Do not write "framework thread" in a comment near this method. That is the false premise that licenses a
  /// future object-table read, and the crash it buys will be someone else's to find.
  ///
  /// Dalamud swallows what escapes here — it wraps each handler in a catch that logs and moves on — so a throw
  /// does not crash the game. It fails SILENTLY into a log nobody is reading, which is worse.
  /// </summary>
  private void OnDutyCompleted(IDutyStateEventArgs args)
  {
    // Pure field reads off a snapshot the event already took. No sheet, no object table, nothing that throws.
    var territory = args.TerritoryType.RowId;
    var contentId = args.ContentFinderCondition.RowId;

    Plugin.Framework.RunOnFrameworkThread(() => RecordClear(territory, contentId));
  }

  /// <summary>Framework thread. Everything that can throw, or that reads the world, happens here.</summary>
  private void RecordClear(uint territory, uint contentId)
  {
    try
    {
      if (!Plugin.Configuration.RememberDutyClears)
        return;

      // Gate #7 of the PvP defence. Frontlines, Crystalline Conflict and Rival Wings are instanced duties WITH
      // a ContentFinderCondition, so they raise this event like anything else — and the scanner has already
      // published an empty snapshot for them, which means without this the line would read "cleared with 0 of
      // your marks" and the plugin would be announcing its own existence in a PvP match.
      if (Plugin.ClientState.IsPvP)
        return;

      if (territory == _lastClearedTerritory)
        return;

      var name = DutyName(contentId);
      if (name.Length == 0)
        return;

      // The roster as it stands at the moment of the clear. Rows is unfiltered, so ignored players are in it —
      // which is exactly why the mark probe below has to reject them itself.
      var snapshot = Plugin.Scanner.Snapshot;
      if (!snapshot.Valid)
        return;

      var marks = Plugin.Marks.Index;
      var cleared = 0;

      foreach (var row in snapshot.Rows)
      {
        if (row.IsSelf)
          continue;

        // ONLY someone already marked, and never someone ignored. Creating a record here would make a duty
        // completing into a reason to remember a stranger, which is the one thing the store refuses; writing to
        // an ignored player's note would be announcing them to themselves in the user's own file.
        if (marks.Find(row.Key) is not { IsIgnored: false })
          continue;

        Plugin.Marks.Update(row.Key, row.HomeWorldName, mark => Append(mark, name));
        cleared++;
      }

      if (cleared == 0)
        return;

      // Burned only by a clear that actually wrote something, and only here at the end — never up front beside
      // the check it feeds. This guard exists to stop ONE completion double-writing, so a run that wrote nothing
      // has nothing to protect and must not spend the territory: the user who clears a dungeon with an unmarked
      // friend, marks them on the spot and immediately requeues would otherwise get silence the second time, for
      // a reason no window explains, and the plugin would look like it had simply stopped remembering.
      _lastClearedTerritory = territory;

      Plugin.Log.Debug("Noted {Name} on {Count} marked players", name, cleared);
    }
    catch (Exception ex)
    {
      Plugin.Log.Error(ex, "Recording a duty clear failed");
    }
  }

  /// <summary>
  /// Adds a clear to a note, once.
  ///
  /// APPENDS TO THE NOTE rather than adding a field, and that is the whole design in one line: a field would be
  /// a second observational member on the record, and MarkedPlayer's own rule is that a third needs the same
  /// argument the first two made — plus a list of clears is a history, which is the shape that has to grow. A
  /// note is a string the user already owns and can edit or delete like any other. It is their file.
  ///
  /// Never twice for the same fight: the note is the user's, and filling it with repeats of one duty name is
  /// vandalism with extra steps.
  /// </summary>
  private static MarkedPlayer Append(MarkedPlayer mark, string dutyName)
  {
    var line = $"Cleared {dutyName} together.";
    if (mark.Note.Contains(line, StringComparison.Ordinal))
      return mark;

    var note = mark.HasNote ? $"{mark.Note}\n{line}" : line;
    return mark with { Note = note };
  }

  /// <summary>
  /// A fight's name, or empty.
  ///
  /// GUARDED AND CACHED for the reason RacePalette and ScentScanner.ZoneName both spell out: GetExcelSheet
  /// ITSELF throws — a missing sheet, an unsupported language, a column-hash mismatch after a patch. And the
  /// tempting one-liner is a trap: RowRef&lt;T&gt;.ValueNullable reads like it is safe and is not, because its
  /// Sheet getter reaches for the same sheet and throws from the same family. The null-conditional guards a
  /// missing row; nothing about it guards the throw.
  ///
  /// Empty on failure, and the caller does nothing at all with an empty name — a note reading "Cleared
  /// together." with no fight in it is worse than silence.
  /// </summary>
  private static string DutyName(uint contentId)
  {
    if (contentId == 0)
      return string.Empty;

    try
    {
      var sheet = Plugin.DataManager.GetExcelSheet<ContentFinderCondition>();

      // GetRowOrDefault, never GetRow: GetRow throws on a row the sheet does not have.
      return sheet?.GetRowOrDefault(contentId)?.Name.ExtractText() ?? string.Empty;
    }
    catch (Exception ex)
    {
      Plugin.Log.Warning(ex, "Could not read the name of duty {Id}", contentId);
      return string.Empty;
    }
  }
}
