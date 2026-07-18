using System;
using System.Collections.Generic;
using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;

namespace HrothgarScent.Scent;

/// <summary>
/// How many times each player has emoted AT you this session — /pet, /hug, /dote, /embrace and the rest.
///
/// SESSION-ONLY, like the watcher log and for the same reason: it is a thing OTHER people did, gathered without
/// their say, so it dies at logout and is never written down. It is the social-observation sibling of the watcher
/// log (who targeted you) — this is who was affectionate at you.
///
/// READS THE EMOTE CHAT LOG, not a game hook. A hook on the emote packet would be the other option, but it is a
/// reverse-engineered signature that cannot be verified without the running game and can crash it if wrong; the
/// log is pure public Dalamud API and fails soft — a miss is a wrong count, never a crash. And it is not fragile:
/// the target is read from the message's own <see cref="XivChatRelationKind"/>, so "aimed at me" is
/// <see cref="XivChatRelationKind.LocalPlayer"/> outright — no parsing of "you", and it works in any language.
/// </summary>
public sealed class EmoteLog : IDisposable
{
  /// <summary>Guards <see cref="_counts"/>. ChatMessage is raised on the framework thread and the profile reads
  /// from the render thread, so the two genuinely race over a plain Dictionary.</summary>
  private readonly object _gate = new();

  private readonly Dictionary<WatcherKey, int> _counts = [];

  public void Subscribe() => Plugin.ChatGui.ChatMessage += OnChatMessage;

  public void Dispose() => Plugin.ChatGui.ChatMessage -= OnChatMessage;

  /// <summary>How many times this player has emoted at you this session. Off the gate; safe from the render thread.</summary>
  public int CountFor(WatcherKey key)
  {
    lock (_gate)
      return _counts.GetValueOrDefault(key);
  }

  /// <summary>Forgets the session's counts. Called at logout, on the same rule as the watcher log.</summary>
  public void Clear()
  {
    lock (_gate)
      _counts.Clear();
  }

  private void OnChatMessage(IHandleableChatMessage message)
  {
    // Dalamud wraps this handler, but a throw here would spam the log per emote in a busy zone, so it catches its
    // own — the same posture as OnMenuOpened.
    try
    {
      // The two emote channels: /pet and friends are StandardEmote; /em freeform is CustomEmote.
      if (message.LogKind != XivChatType.StandardEmote && message.LogKind != XivChatType.CustomEmote)
        return;

      // Aimed at the LOCAL PLAYER specifically — read from the game's own relation kind, so this is exact and
      // language-agnostic, never a guess at the word "you". An emote at someone else or at nobody is not counted.
      if (message.TargetKind != XivChatRelationKind.LocalPlayer)
        return;

      // Never your own emote (which also covers the /emote-at-self case: source AND target would be you).
      if (message.SourceKind == XivChatRelationKind.LocalPlayer)
        return;

      // The emoter. Emote lines carry the actor as a PlayerPayload; look in the sender first, then the message
      // body, since the game puts it in different halves for different emotes.
      if ((FindPlayer(message.Sender) ?? FindPlayer(message.Message)) is not { } key)
        return;

      lock (_gate)
        _counts[key] = _counts.GetValueOrDefault(key) + 1;
    }
    catch (Exception ex)
    {
      Plugin.Log.Warning(ex, "Recording an emote failed");
    }
  }

  /// <summary>The first player named in a string as a <see cref="WatcherKey"/>, or null if none. World comes from
  /// the payload's own home-world row, matching identity everywhere else in the plugin.</summary>
  private static WatcherKey? FindPlayer(SeString text)
  {
    foreach (var payload in text.Payloads)
      if (payload is PlayerPayload { PlayerName: { Length: > 0 } name } player)
        return new WatcherKey(name, player.World.RowId);

    return null;
  }
}
