using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using CSGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace HrothgarScent.Scent;

/// <summary>
/// Row actions. Two rules hold for every method here that touches the game:
///
///   1. The object table and every game function are framework-thread-only, and these are invoked from
///      Draw, so each one marshals via RunOnFrameworkThread.
///   2. The snapshot's GameObjectId is re-resolved at click time, never cached as a pointer. A row can be a
///      full rescan interval old before the user even sees it, plus however long they took to click, so the
///      object may be long gone; a stale Address would be a use-after-free and a game crash.
///
/// The two methods that do not touch the game — <see cref="CopyName"/> and <see cref="OpenLodestone"/> —
/// say so and run inline.
/// </summary>
public static class PlayerActions
{
  /// <summary>
  /// UIColor row id for the chat link. 500 is what Dalamud itself uses to tint its own clickable chat text,
  /// so a link posted by this plugin looks like every other link the user already clicks.
  /// </summary>
  private const ushort UiForegroundLink = 500;

  public static void Target(ulong gameObjectId)
    => WithPlayer(gameObjectId, pc => Plugin.TargetManager.Target = pc, "Target");

  public static void FocusTarget(ulong gameObjectId)
    => WithPlayer(gameObjectId, pc => Plugin.TargetManager.FocusTarget = pc, "FocusTarget");

  /// <summary>
  /// Drops the focus target outright rather than restoring whatever was there before. Reading the previous
  /// focus target needs the object table, which Draw may not touch, so honest clearing beats a restore this
  /// side of the split cannot actually perform.
  /// </summary>
  public static void ClearFocusTarget()
  {
    Plugin.Framework.RunOnFrameworkThread(() =>
    {
      try
      {
        Plugin.TargetManager.FocusTarget = null;
      }
      catch (Exception ex)
      {
        Plugin.Log.Warning(ex, "ClearFocusTarget failed");
      }
    });
  }

  /// <summary>
  /// Opens the game's examine window. Takes EntityId, not GameObjectId — they are different id spaces and
  /// passing the wrong one examines a stranger or nobody.
  /// </summary>
  public static unsafe void Examine(ulong gameObjectId)
    => WithPlayer(gameObjectId, pc => AgentInspect.Instance()->ExamineCharacter(pc.EntityId), "Examine");

  public static unsafe void OpenAdventurerPlate(ulong gameObjectId)
    => WithPlayer(gameObjectId, pc => AgentCharaCard.Instance()->OpenCharaCard((CSGameObject*)pc.Address), "AdventurerPlate");

  /// <summary>
  /// Posts the game's own clickable player link to chat.
  ///
  /// This is the plugin's answer to "send a tell", and the reason there is no Send Tell item in the menu:
  /// Dalamud's ICommandManager dispatches only to plugin commands and silently returns false for game
  /// commands like /tell, and no public API sets a chat target. Rather than fake it with an unsupported hook
  /// — which is how plugins get their users banned — the link hands the interaction back to the game.
  /// Clicking it opens the game's real player menu, Tell included.
  ///
  /// Marshalled like the rest even though chat is not the object table: it costs one frame of latency and
  /// removes any need to reason about the chat queue's threading guarantees from Draw.
  /// </summary>
  public static void LinkInChat(ScentRow row)
  {
    Plugin.Framework.RunOnFrameworkThread(() =>
    {
      try
      {
        // PlayerPayload is self-contained: it encodes the link chunk, its own visible text (the bare name,
        // never the world) and its own terminator. So it takes neither the AddText nor the LinkTerminator
        // that AddItemLinkRaw next door documents as required — those would render the name a second time,
        // outside the closed link and tinted to match it, i.e. dead text that looks clickable.
        //
        // The world therefore has to sit outside the link, as plain text. Only the name is the link target,
        // which is honest: the untinted suffix is exactly the part the game will not respond to.
        var message = new SeStringBuilder()
          .AddUiForeground(UiForegroundLink)
          .Add(new PlayerPayload(row.Name, row.HomeWorldId))
          .AddUiForegroundOff();

        if (!string.IsNullOrEmpty(row.HomeWorldName))
          message.AddText($"@{row.HomeWorldName}");

        message.AddText(" — Hrothgar know who watching.");
        var built = message.Build();
        Plugin.ChatGui.Print(built, Plugin.ChatTag, Plugin.ChatTagColor);
      }
      catch (Exception ex)
      {
        Plugin.Log.Warning(ex, "LinkInChat failed");
      }
    });
  }

  /// <summary>Pure ImGui, no game state: safe to call straight from Draw with no marshalling.</summary>
  public static void CopyName(ScentRow row) => ImGui.SetClipboardText(row.FullName);

  /// <summary>Opens the default browser. No game state, so no marshalling.</summary>
  public static void OpenLodestone(ScentRow row)
  {
    try
    {
      var domain = Plugin.Configuration.LodestoneRegion switch
      {
        LodestoneRegion.NorthAmerica => "na",
        LodestoneRegion.Japan => "jp",
        LodestoneRegion.Germany => "de",
        LodestoneRegion.France => "fr",
        _ => "eu",
      };

      var url = $"https://{domain}.finalfantasyxiv.com/lodestone/character/?q={Uri.EscapeDataString(row.Name)}";

      // An empty worldname parameter matches nothing rather than everything, so omit it entirely when the
      // world sheet row had not loaded at scan time.
      if (!string.IsNullOrEmpty(row.HomeWorldName))
        url += $"&worldname={Uri.EscapeDataString(row.HomeWorldName)}";

      Util.OpenLink(url);
    }
    catch (Exception ex)
    {
      Plugin.Log.Warning(ex, "OpenLodestone failed");
    }
  }

  /// <summary>
  /// Marshals to the framework thread, re-resolves the id, and runs <paramref name="action"/> against a
  /// pointer that is known live for exactly as long as the call.
  ///
  /// The try/catch is not defensive style. Examine and OpenCharaCard are member functions resolved by
  /// byte-signature scan, and a scan that fails after a game patch makes them throw rather than return null
  /// — this is the only thing standing between a patch day and an exception in a click handler. Never hoist
  /// those calls out of here.
  /// </summary>
  private static void WithPlayer(ulong gameObjectId, Action<IPlayerCharacter> action, string what)
  {
    Plugin.Framework.RunOnFrameworkThread(() =>
    {
      try
      {
        if (Plugin.Objects.SearchById(gameObjectId) is not IPlayerCharacter pc)
        {
          // Routine, not exceptional: they zoned, despawned, or the id was recycled between the scan and
          // the click. Degrade silently — a popup for this would fire on ordinary use.
          Plugin.Log.Debug("{What}: object {Id} is gone", what, gameObjectId);
          return;
        }

        action(pc);
      }
      catch (Exception ex)
      {
        Plugin.Log.Warning(ex, "{What} failed", what);
      }
    });
  }
}
