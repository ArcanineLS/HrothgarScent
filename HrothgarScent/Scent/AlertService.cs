using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace HrothgarScent.Scent;

/// <summary>
/// Chat and sound alerts for a new watcher and for a focus-list arrival, sharing one cooldown that cannot be
/// starved.
/// </summary>
public sealed class AlertService
{
  /// <summary>
  /// UIColor row id for the alert text — a red, so a stare reads differently from ordinary chatter.
  /// Cosmetic only: this row id could not be verified against the live sheet from the shipped assemblies,
  /// and a wrong one costs the wrong colour and nothing else.
  /// </summary>
  private const ushort UiForegroundWatcher = 17;

  /// <summary>
  /// UIColor row id for the arrival line — a blue, so an arrival does not read as a stare. Cosmetic only, on
  /// the same terms as <see cref="UiForegroundWatcher"/>: unverified against the live sheet, and a wrong one
  /// costs the wrong colour and nothing else.
  /// </summary>
  private const ushort UiForegroundFocus = 37;

  /// <summary>Chat SFX ids. Only 1..16 have a <c>&lt;se.N&gt;</c> behind them; outside the range there is no sound.</summary>
  private const int MinSoundId = 1;
  private const int MaxSoundId = 16;

  /// <summary>
  /// Ticks of the last alert, or null if none has fired yet.
  ///
  /// Nullable rather than seeded to 0: Environment.TickCount64 counts from system start, so a plain 0 would
  /// mean "an alert fired at boot" and would swallow the very first watcher for one cooldown's worth of
  /// system uptime. Reachable only on a freshly booted machine, and free to rule out.
  /// </summary>
  private long? _lastAlertTicks;

  /// <summary>
  /// Announces watchers that were not watching on the previous scan. Framework thread only — the scanner is
  /// the sole caller, and the chat and sound calls below are game-side.
  /// </summary>
  public void NotifyNewWatchers(IReadOnlyList<ScentRow> fresh)
  {
    var config = Plugin.Configuration;

    // The watcher half's own output. Suppressed with the half, here rather than at the call site, so every
    // gate an alert passes lives in one place — and so the scanner keeps recording history either way, which
    // is the whole point of the toggle being UI-only.
    if (!config.EnableWatchers)
      return;
    if (!config.AlertInChat && !config.AlertWithSound)
      return;

    // Read the ignore list's reference once. It is copy-on-write precisely because the render thread edits
    // it while this runs (see Configuration.IgnoredPlayers), so one read pins one whole coherent version.
    var ignoredPlayers = config.IgnoredPlayers;

    // Filter first, cooldown second, and never the reverse. A party member the user chose not to be alerted
    // about would otherwise still burn the cooldown, swallowing the stranger who targeted them in the same
    // scan — the one alert that actually mattered.
    var subjects = new List<ScentRow>(fresh.Count);
    foreach (var row in fresh)
    {
      if (row.IsParty && !config.AlertForParty)
        continue;
      if (row.IsFriend && !config.AlertForFriends)
        continue;
      if (row.IsAlliance && !config.AlertForAlliance)
        continue;
      if (ignoredPlayers.Any(ignored => ignored.Matches(row)))
        continue;
      subjects.Add(row);
    }

    if (subjects.Count == 0)
      return;

    // The config stores seconds because that is what the slider shows; this is the one place it becomes
    // milliseconds, so the unit can only be got wrong once.
    var now = Environment.TickCount64;
    var cooldownMs = (long)(Math.Max(0f, config.AlertCooldownSeconds) * 1000f);
    if (_lastAlertTicks is { } last && now - last < cooldownMs)
      return;
    _lastAlertTicks = now;

    if (config.AlertInChat)
    {
      var message = new SeStringBuilder()
        .AddUiForeground(UiForegroundWatcher)
        .AddText(subjects.Count == 1
          ? $"Hrothgar smell {subjects[0].FullName} watching you."
          : $"Hrothgar smell {subjects.Count} eyes on you.")
        .AddUiForegroundOff()
        .Build();
      Plugin.ChatGui.Print(message, Plugin.ChatTag, Plugin.ChatTagColor);
    }

    if (config.AlertWithSound)
      PlaySound(config.AlertSoundId);
  }

  /// <summary>
  /// Announces focus-list players who were not in range on the previous scan. Framework thread only — the
  /// scanner is the sole caller, and the chat and sound calls below are game-side.
  ///
  /// Shares <see cref="_lastAlertTicks"/> with <see cref="NotifyNewWatchers"/>, and shares it in second place:
  /// the scanner calls the watcher path first, so a watcher and an arrival in the same scan spend the cooldown
  /// on the watcher. One cooldown because the user configured one — "shortest gap between two alerts" is a
  /// promise about alerts, not about categories of alert, and two independent cooldowns would let a crowd
  /// deliver double what the slider says.
  /// </summary>
  public void NotifyFocusArrivals(IReadOnlyList<ScentRow> arrived)
  {
    var config = Plugin.Configuration;
    if (!config.AlertOnFocusArrival || !config.EnableNearbyList)
      return;
    if (!config.AlertInChat && !config.AlertWithSound)
      return;

    // Ignore beats focus, and the scanner cannot enforce it: it tests the focus list, not this one. A player on
    // both lists is a user contradiction, and the ignore list's promise ("never show or announce them again")
    // is the older and the stronger one.
    var ignoredPlayers = config.IgnoredPlayers;

    // Filter first, cooldown second, for the reason spelled out in NotifyNewWatchers.
    //
    // AlertForParty/Friends/Alliance are deliberately NOT consulted. Those exist because your party targets you
    // constantly and the watcher alert would be unusable without them; the focus list is hand-built, one name at
    // a time, so a focused friend is a friend the user explicitly asked to be told about.
    var subjects = new List<ScentRow>(arrived.Count);
    foreach (var row in arrived)
    {
      if (ignoredPlayers.Any(ignored => ignored.Matches(row)))
        continue;
      subjects.Add(row);
    }

    if (subjects.Count == 0)
      return;

    var now = Environment.TickCount64;
    var cooldownMs = (long)(Math.Max(0f, config.AlertCooldownSeconds) * 1000f);
    if (_lastAlertTicks is { } last && now - last < cooldownMs)
      return;
    _lastAlertTicks = now;

    if (config.AlertInChat)
    {
      var message = new SeStringBuilder()
        .AddUiForeground(UiForegroundFocus)
        .AddText(subjects.Count == 1
          ? $"Hrothgar smell {subjects[0].FullName} come close."
          : $"Hrothgar smell {subjects.Count} you watch for come close.")
        .AddUiForegroundOff()
        .Build();
      Plugin.ChatGui.Print(message, Plugin.ChatTag, Plugin.ChatTagColor);
    }

    if (config.AlertWithSound)
      PlaySound(config.AlertSoundId);
  }

  /// <summary>
  /// Plays the configured chat SFX once, ignoring the cooldown — a Test button that stays silent because
  /// something fired nine seconds ago is not a test.
  ///
  /// Marshalled because it is invoked from Draw and the sound is a game call.
  /// </summary>
  public void TestSound()
  {
    var soundId = Plugin.Configuration.AlertSoundId;
    Plugin.Framework.RunOnFrameworkThread(() => PlaySound(soundId));
  }

  /// <summary>
  /// The one FFXIVClientStructs call in this file, isolated so a game patch that moves the function costs
  /// the alert's sound and nothing else. The chat line still lands; that is the point of catching here
  /// rather than around the whole alert.
  /// </summary>
  private static void PlaySound(int soundId)
  {
    try
    {
      UIGlobals.PlayChatSoundEffect((uint)Math.Clamp(soundId, MinSoundId, MaxSoundId));
    }
    catch (Exception ex)
    {
      Plugin.Log.Warning(ex, "Chat sound effect {SoundId} failed", soundId);
    }
  }
}
