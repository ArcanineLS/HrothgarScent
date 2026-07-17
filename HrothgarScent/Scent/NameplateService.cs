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
  /// <summary>
  /// How far the outline is darkened below the name it surrounds. See <see cref="Paint"/>.
  ///
  /// Multiplied, not subtracted: a fixed offset would clip a dark colour to black and leave a bright one barely
  /// distinguishable from its own fill, so the outline would only work for colours near the middle. Scaling
  /// keeps the gap proportional, so every colour gets the same relative separation.
  /// </summary>
  private const float EdgeShade = 0.25f;

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
    // EITHER half is reason enough to attach, and that OR is load-bearing now that marks paint here too. A bare
    // EnableWatchers would leave the handler detached for a user running the list with the watcher half off —
    // so their mark colours would never appear at all, and the switch that killed them is the one switch that
    // has nothing to do with marks. The handler decides WHAT to paint; this decides only whether there is
    // anything to paint at all.
    var config = Plugin.Configuration;
    var wanted = config.NameplateMode != NameplateMode.Off
              && (config.EnableWatchers || (config.NameplateMarkColors && config.EnableNearbyList))
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

    // The two halves are gated SEPARATELY, on the same switches that gate them everywhere else: watchers belong
    // to the Watchers half, marks belong to the nearby list. One combined test was correct while watchers were
    // the only thing painted here — it is not now. A user running the list with the Watchers half off would
    // otherwise lose their mark colours to a switch that has nothing to do with marks.
    var paintWatchers = config.NameplateMode != NameplateMode.Off && config.EnableWatchers;
    var paintMarks = config.NameplateMode != NameplateMode.Off && config.NameplateMarkColors
                  && config.EnableNearbyList;

    if (!paintWatchers && !paintMarks)
      return;

    // One volatile read of an immutable value for the whole frame, exactly as Draw does. No object table, no
    // pointer, nothing kept past this method.
    var snapshot = Plugin.Scanner.Snapshot;
    if (!snapshot.Valid)
      return;

    // Each half's own "nothing to do" test, ANDed. WatcherCount == 0 alone used to be a sound early-out because
    // a watcher was all this painted; with marks it would drop every mark colour on screen the moment nobody
    // happened to be staring — the colours would flicker in and out with a stranger's attention, which is the
    // one thing they have nothing to do with.
    var anyWatchers = paintWatchers && snapshot.WatcherCount > 0;
    var anyMarks = paintMarks && snapshot.MarkedNearby;
    if (!anyWatchers && !anyMarks)
      return;

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
      if (row.IsSelf)
        continue;

      // IsIgnored: "never shown or announced" is the oldest promise this plugin makes, and THIS is the surface
      // where breaking it is loudest — a coloured name over a harasser's head, in the world, in front of
      // everyone, while the table has dropped them and every other readout is silent. The snapshot deliberately
      // carries ignored players: rows is unfiltered, so ById is too, and they count toward WatcherCount. Every
      // consumer filters at its OWN edge, and this is that edge. Do not "simplify" this back into the scanner —
      // WatcherCount feeds the eye column and the info bar, and filtering there would change both.
      //
      // ABOVE the whole chain, so it cannot be outranked. An ignored player who is also focused, or who is
      // staring right now, must still be painted nothing: ignore beats everything, everywhere.
      var mark = marks.Find(row.Key);
      if (mark is { IsIgnored: true })
        continue;

      // THE PRIORITY, top down. Targeting you is the most urgent fact this plugin ever has and outranks any
      // colour chosen at leisure; a colour you gave them outranks the game's default; the default is what is
      // left when neither applies, and is expressed by painting NOTHING rather than by painting a default.
      if (paintWatchers && row.IsWatching)
      {
        Paint(handler, config.ColorWatcher);
        continue;
      }

      // FOCUSED ONLY, matching the list's own rule at ScentWindow's name-colour chain: "a colour on an unfocused
      // player therefore shows nothing here, deliberately". A nameplate that painted an unfocused player's
      // colour would assert, in the world and in front of everyone, a mark the list itself denies.
      //
      // Color ?? ColorFocused, again matching the list: an unset colour is not "no colour", it is the default
      // focus colour, and the profile's own swatch shows it as such.
      if (paintMarks && mark is { IsFocused: true })
        Paint(handler, mark.Color ?? config.ColorFocused);
    }
  }

  /// <summary>
  /// Colours one plate — BOTH the fill and its outline.
  ///
  /// The outline is not decoration and not optional. A nameplate is drawn as coloured text inside a contrasting
  /// edge, and the game's default edge is tuned for the white text it expects; leave it alone and a chosen
  /// colour is rendered inside an outline picked for a different colour entirely. The result reads as washed
  /// out or haloed rather than as the colour the user picked, on the surface whose entire job is showing them
  /// that colour.
  ///
  /// The edge is the SAME hue, darkened — not black, and not a second choice for the user to make. Black would
  /// fight a dark colour and vanish behind it; a second setting would be two knobs for one decision, and the
  /// wrong pairing is a legibility bug the user gets to inflict on themselves. Deriving it means any colour they
  /// pick arrives already legible.
  ///
  /// Alpha is forced opaque on the edge and taken from the colour on the fill. A translucent outline stops being
  /// an outline — the world shows through the one part whose whole purpose is separating the text FROM the
  /// world — and this plugin's colours are all alpha 1 in practice anyway.
  /// </summary>
  private static void Paint(INamePlateUpdateHandler handler, Vector4 color)
  {
    handler.TextColor = PackAbgr(color);
    handler.EdgeColor = PackAbgr(new Vector4(color.X * EdgeShade, color.Y * EdgeShade, color.Z * EdgeShade, 1f));
  }

  /// <summary>
  /// Packs a colour for <see cref="INamePlateUpdateHandler.TextColor"/>.
  ///
  /// BY HAND, never ImGui.GetColorU32: that reads the global ImGui context and folds in the current style's
  /// alpha, and this runs outside any ImGui frame — on the one path whose entire pitch is thread discipline.
  /// This is context-free arithmetic and nothing else.
  ///
  /// ABGR — 0xAABBGGRR — VERIFIED IN GAME, and it is the opposite of what this originally assumed. TextColor is
  /// a raw uint off the game's number array with no typed colour behind it, so the layout cannot be read out of
  /// the assemblies at all; it took a screenshot to settle. Packing RGBA and picking #EF8A0B (a deliberately
  /// asymmetric orange: R239 G138 B11) rendered the plate HOT PINK — R255 G11 B138 — which is that same uint
  /// read back low-byte-first, and is the only one of the four candidate orders that produces it.
  ///
  /// The failure hid for as long as it did because the default ColorWatcher is red, and red packs to the same
  /// bytes under both readings. Nothing was wrong until someone chose a colour with two channels in it.
  /// </summary>
  private static uint PackAbgr(Vector4 color)
  {
    static uint Channel(float v) => (uint)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);

    return (Channel(color.W) << 24) | (Channel(color.Z) << 16) | (Channel(color.Y) << 8) | Channel(color.X);
  }
}
