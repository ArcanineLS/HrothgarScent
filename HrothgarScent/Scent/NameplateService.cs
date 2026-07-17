using System;
using System.Numerics;
using Dalamud.Game.Gui.NamePlate;

namespace HrothgarScent.Scent;

/// <summary>
/// The eye, over their head.
///
/// The one thing this plugin knows that nothing else does — who is looking at you RIGHT NOW — reaching the
/// world instead of dying inside a table cell that only exists while a window is open. The prior art's
/// nameplate code is the best-engineered thing it has and its most-loved feature, and it can only ever show
/// facts the USER typed: a colour, a title, a category. It has no notion of being looked at. This does.
///
/// THE ARCHITECTURE IS ALREADY HERE, which is why this is small. INamePlateUpdateHandler hands over a
/// GameObjectId, and the scanner already publishes a snapshot keyed by exactly that — so the callback needs no
/// object-table read at all, and the cache-invalidation problem the prior art solves with a ConcurrentDictionary
/// and event-driven precompute simply does not exist here: the snapshot IS the cache. Dalamud's own contract
/// for these handlers — "only valid for a single frame and should not be kept across frames" — is verbatim
/// <see cref="ScentRow"/>'s no-pointer rule.
///
/// Framework thread; Dalamud raises the update on the main thread. Cosmetic only: a colour on a name. Nothing
/// is added, nothing is hidden, no text is replaced — the least this can do and still be the feature.
/// </summary>
public sealed class NameplateService : IDisposable
{
  /// <summary>Whether the handler is currently attached. The PvP defence's second half; see
  /// <see cref="Sync"/>.</summary>
  private bool _subscribed;

  public void Dispose() => Unsubscribe();

  /// <summary>
  /// Attaches or detaches the handler to match the world, once per framework tick.
  ///
  /// GATE #6 OF THE PvP DEFENCE, and the reason it is a subscription rather than an `if`. Gates #1-#4 are
  /// backstopped by the scanner publishing an empty snapshot in PvP — there is no data behind them to leak.
  /// Gates #5, #6 and #8 are the unbackstopped set: #5 reads the mark store, #8 reads the object table, and both
  /// are fully populated in PvP like anywhere else, so each is the ONLY thing standing between its surface and
  /// an enemy roster. This one is different in kind again: it is the game's own world UI, in front of other
  /// players, in the one place where competitive integrity is actually judged. So it gets BOTH an early return
  /// in the handler AND this, exactly as ScentScanner keeps its own two gates apart "so that no single edit can
  /// collapse both".
  ///
  /// Driven from the framework tick rather than from TerritoryChanged, and that is not laziness — it is the
  /// documented trap. TerritoryChanged is raised from inside the TerritoryType property setter, which the game
  /// runs BEFORE it assigns IsPvP, so IsPvP read from that handler is the PREVIOUS zone's answer: it fails OPEN
  /// on the way in, which is the one direction that matters. Do not "improve" this into an event.
  /// </summary>
  public void Sync()
  {
    // EnableWatchers belongs in HERE rather than only in the handler, and that is the difference between the
    // half going quiet and the half going quiet everywhere. Detaching is what triggers the scrub: leave the
    // handler attached and merely early-returning, and nothing ever asks the game to rebuild the plate — so the
    // last frame's red name sits over a watcher's head for as long as they keep staring, while the eye column,
    // the history and the info bar have all correctly gone silent. Subscribe and Unsubscribe own the redraw,
    // and this is the only thing that reaches them.
    var wanted = Plugin.Configuration.NameplateMode != NameplateMode.Off
              && Plugin.Configuration.EnableWatchers
              && !Plugin.ClientState.IsPvP
              && Plugin.ClientState.IsLoggedIn;

    if (wanted == _subscribed)
      return;

    if (wanted)
      Subscribe();
    else
      Unsubscribe();
  }

  private void Subscribe()
  {
    Plugin.NamePlateGui.OnNamePlateUpdate += OnNamePlateUpdate;
    _subscribed = true;
    Redraw();
  }

  private void Unsubscribe()
  {
    if (!_subscribed)
      return;

    Plugin.NamePlateGui.OnNamePlateUpdate -= OnNamePlateUpdate;
    _subscribed = false;

    // Scrubs what is already painted. Detaching alone leaves the last frame's colours sitting on the plates
    // until the game happens to redraw them itself — which, on the way into PvP, is precisely the window that
    // must not exist.
    Redraw();
  }

  /// <summary>
  /// Asks the game to rebuild the plates.
  ///
  /// MANDATORY, or the feature is inert. The game dirties a nameplate when its owner's name, title or level
  /// changes — never because they changed TARGET, which is the only thing this cares about. Nothing would ever
  /// call the handler at the moment there is something new to say.
  /// </summary>
  public void Redraw()
  {
    try
    {
      Plugin.NamePlateGui.RequestRedraw();
    }
    catch (Exception ex)
    {
      Plugin.Log.Warning(ex, "Nameplate redraw failed");
    }
  }

  /// <summary>
  /// Paints one frame's plates.
  ///
  /// OnNamePlateUpdate, never OnDataUpdate: Dalamud's own doc says the latter is "likely to fire every frame
  /// even when no nameplates are actually updated", and this one is handed only the plates the game is already
  /// rebuilding.
  /// </summary>
  private void OnNamePlateUpdate(INamePlateUpdateContext context,
    System.Collections.Generic.IReadOnlyList<INamePlateUpdateHandler> handlers)
  {
    // Gate #6's other half, first line, its own statement. See Sync for why one of the two is not enough.
    if (Plugin.ClientState.IsPvP)
      return;

    var config = Plugin.Configuration;
    if (config.NameplateMode == NameplateMode.Off || !config.EnableWatchers)
      return;

    // One volatile read of an immutable value for the whole frame, exactly as Draw does. No object table, no
    // pointer, nothing kept past this method.
    var snapshot = Plugin.Scanner.Snapshot;
    if (!snapshot.Valid || snapshot.WatcherCount == 0)
      return;

    var color = PackRgba(config.ColorWatcher);

    // One published read for the frame, exactly as the scan does. Immutable and volatile-read, so it is safe
    // from here and cannot tear.
    var marks = Plugin.Marks.Index;

    foreach (var handler in handlers)
    {
      // BEFORE touching anything else on the handler. PlayerCharacter and GameObject are documented to reach
      // the object table, and a retainer or an NPC has no business being probed for whether it is staring.
      if (handler.NamePlateKind != NamePlateKind.PlayerCharacter)
        continue;

      if (!snapshot.ById.TryGetValue(handler.GameObjectId, out var row))
        continue;

      // IsSelf: rows includes you, and IsWatching is TRUE for your own row whenever you target yourself, which
      // is ordinary play and would paint your own name red for it. Every other consumer of rows guards this.
      //
      // IsIgnored: "never shown or announced" is the oldest promise this plugin makes, and THIS is the surface
      // where breaking it is loudest — a red name over a harasser's head, in the world, in front of everyone,
      // while the table has dropped them and every other readout is silent. The snapshot deliberately carries
      // ignored players: rows is unfiltered, so ById is too, and they count toward WatcherCount. Every
      // consumer filters at its OWN edge, and this is that edge. Do not "simplify" this back into the scanner —
      // WatcherCount feeds the eye column and the info bar, and filtering there would change both.
      if (row.IsSelf || !row.IsWatching || marks.IsIgnored(row.Key))
        continue;

      handler.TextColor = color;
    }
  }

  /// <summary>
  /// Packs a colour for <see cref="INamePlateUpdateHandler.TextColor"/>.
  ///
  /// BY HAND, never ImGui.GetColorU32: that reads the global ImGui context and folds in the current style's
  /// alpha, and this runs outside any ImGui frame — on the one path whose entire pitch is thread discipline.
  /// This is context-free arithmetic and nothing else.
  ///
  /// UNVERIFIED: THE BYTE ORDER. TextColor is a raw uint off the game's number array with no typed colour
  /// behind it — it is not a ByteColor, so the layout cannot be read out of the assemblies, and settling it
  /// needs the game. RGBA is assumed because that is the convention for the game's own number-array colours;
  /// ImGui packs the other way (ABGR). The default ColorWatcher is red, which packs to the same bytes under
  /// BOTH readings, so a mistake here is invisible until someone picks green or blue — test with a deliberately
  /// asymmetric colour, pure blue at half alpha, and if the channels are swapped this method is the whole fix.
  /// </summary>
  private static uint PackRgba(Vector4 color)
  {
    static uint Channel(float v) => (uint)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);

    return (Channel(color.X) << 24) | (Channel(color.Y) << 16) | (Channel(color.Z) << 8) | Channel(color.W);
  }
}
