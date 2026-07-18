using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Interface.Textures.TextureWraps;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace HrothgarScent.Scent;

/// <summary>What the in-game portrait has to say about one player.</summary>
public enum PortraitCaptureState : byte
{
  /// <summary>Nobody has asked. Default.</summary>
  Idle,

  /// <summary>In flight — a disk read or a GPU copy is running.</summary>
  Loading,

  /// <summary>Loaded (from disk or a fresh capture) and ready to draw.</summary>
  Ready,

  /// <summary>Asked, but there is nothing saved and no plate open to capture from. A real state, not an error.</summary>
  Missing,

  /// <summary>The load or the copy broke. Says nothing about the player — it is our side that failed.</summary>
  Failed,
}

/// <summary>One player's in-game portrait, and how it got that way.</summary>
public sealed class InGamePortrait
{
  public PortraitCaptureState State { get; set; } = PortraitCaptureState.Idle;
  public IDalamudTextureWrap? Texture { get; set; }
}

/// <summary>
/// The player's rendered Adventurer Plate portrait — the face they published — captured on demand and SAVED TO
/// DISK so it survives the session, then shown in the portrait window when the user clicks a profile picture.
///
/// WHY SAVING TO DISK IS DEFENSIBLE HERE, where the Lodestone face and the watcher log deliberately are not: this
/// is written only by a DELIBERATE USER ACT — clicking a profile picture, or opening a plate with the opt-in
/// auto-capture on — exactly the consent line the mark store draws. The plate is the player's own public plate,
/// which the user is already looking at. It is bounded by human effort (one click per person) like the marks, so
/// it needs no retention policy. The in-memory texture cache still dies at logout like everything else; only the
/// PNG on disk persists, and Forgetting is a file the user can delete.
///
/// PUBLIC API, NO HAND-WRITTEN D3D. Capture reads the plate's rendered <c>PortraitTexture</c> (ClientStructs) and
/// hands its shader-resource view to Dalamud through a tiny <see cref="IDalamudTextureWrap"/> of our own;
/// <see cref="ITextureProvider.CreateFromExistingTextureAsync"/> does the GPU copy and
/// <see cref="ITextureReadbackProvider.SaveToFileAsync"/> writes the PNG. No immediate-context hook, no staging
/// readback — nothing that can fault the render thread from our code. It ships off by default (auto-capture) and
/// every branch fails soft.
/// </summary>
public sealed class PortraitService : IDisposable
{
  /// <summary>Guards <see cref="_cache"/>. Written from the framework/render thread (the capture/load triggers)
  /// and from the completion of an async load/copy on a pool thread. Uncontended — a click at a time.</summary>
  private readonly object _gate = new();

  /// <summary>Textures loaded or captured this session, keyed by player. In memory, dropped at logout — the
  /// durable copy is the PNG on disk. Doubles as the single-flight record.</summary>
  private readonly Dictionary<WatcherKey, InGamePortrait> _cache = [];

  private bool _disposed;

  /// <summary>Bumped on <see cref="Clear"/> so a load/copy that outlives its session cannot write into the next,
  /// and so its texture is disposed rather than leaked — the same generation guard <see cref="LodestoneService"/> uses.</summary>
  private int _generation;

  /// <summary>The WIC container id <see cref="ITextureReadbackProvider.SaveToFileAsync"/> writes PNG with,
  /// resolved from Dalamud's own encoder list at construction so a version change cannot leave us hardcoding a
  /// wrong id. Falls back to the well-known PNG GUID.</summary>
  private readonly Guid _pngContainer;

  /// <summary>
  /// The player we last auto-captured while their plate was open. It dedupes a plate that refreshes many times
  /// while it stays open — that would otherwise re-copy and re-save on every tick — down to one capture, WITHOUT
  /// suppressing the cases that should recapture: a DIFFERENT player shown in the same window (this key changes),
  /// or the plate CLOSED and reopened (<see cref="OnPlateClosed"/> resets this to null). So "recapture when I
  /// open a plate" holds, including reopening the same person after they edited their plate.
  /// </summary>
  private WatcherKey? _lastAutoKey;

  public PortraitService()
  {
    _pngContainer = ResolvePngContainer();

    // PostRefresh, not PostSetup: the portrait renders a little after the addon appears, so the texture is far
    // more likely to be present by a refresh. PreFinalize re-arms the dedupe when the plate closes.
    Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, "CharaCard", OnPlateRefresh);
    Plugin.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "CharaCard", OnPlateClosed);
  }

  public void Dispose()
  {
    Plugin.AddonLifecycle.UnregisterListener(OnPlateRefresh, OnPlateClosed);

    lock (_gate)
      _disposed = true;

    Clear();
  }

  /// <summary>What is known about this player's portrait right now. Safe every frame; never starts anything.</summary>
  public InGamePortrait Get(WatcherKey key)
  {
    lock (_gate)
      return _cache.TryGetValue(key, out var portrait) ? portrait : new InGamePortrait();
  }

  /// <summary>
  /// Drops one player's cached texture so the next <see cref="Load"/> re-fetches it. The window's recovery path
  /// if it ever finds a texture disposed under it — cheap self-healing rather than a crash. Disposing an
  /// already-disposed wrap is a no-op, so this is safe whatever state the texture is in.
  /// </summary>
  public void Forget(WatcherKey key)
  {
    lock (_gate)
      if (_cache.Remove(key, out var portrait))
        portrait.Texture?.Dispose();
  }

  /// <summary>Whether a saved PNG exists for this player on disk.</summary>
  public bool HasSaved(WatcherKey key)
  {
    try
    {
      return File.Exists(PathFor(key));
    }
    catch
    {
      return false;
    }
  }

  /// <summary>The on-disk PNG path for a player's saved portrait, or null if none is saved — for the portrait
  /// window's "open file location".</summary>
  public string? SavedPath(WatcherKey key) => HasSaved(key) ? PathFor(key) : null;

  /// <summary>
  /// Loads this player's portrait for viewing — the ONE thing the portrait window calls when a profile picture
  /// is clicked. From the saved PNG if there is one, else captured from their Adventurer Plate if it is the plate
  /// open right now, else settles <see cref="PortraitCaptureState.Missing"/>.
  ///
  /// Idempotent by state: a Ready or in-flight entry is left alone, so clicking a picture repeatedly does not
  /// stack loads; Missing and Failed retry, which is exactly what "open their plate and click the picture again"
  /// needs. FRAMEWORK/RENDER THREAD.
  /// </summary>
  public void Load(WatcherKey key)
  {
    if (Plugin.ClientState.IsPvP)
      return;

    int generation;
    string? diskPath = null;
    lock (_gate)
    {
      if (_cache.TryGetValue(key, out var have)
          && have.State is PortraitCaptureState.Ready or PortraitCaptureState.Loading)
        return;

      generation = _generation;

      if (HasSaved(key))
      {
        diskPath = PathFor(key);
        _cache[key] = new InGamePortrait { State = PortraitCaptureState.Loading };
      }
    }

    if (diskPath is not null)
    {
      _ = LoadFromDisk(key, diskPath, generation);
      return;
    }

    // Nothing saved. Capture if their plate is the one open; otherwise there is genuinely nothing to show yet.
    if (OpenPlateKey() == key)
    {
      CaptureOpenPlate(force: true);
      return;
    }

    lock (_gate)
      if (!_disposed)
        _cache[key] = new InGamePortrait { State = PortraitCaptureState.Missing };
  }

  private async Task LoadFromDisk(WatcherKey key, string path, int generation)
  {
    try
    {
      var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
      var wrap = await Plugin.Textures.CreateFromImageAsync(bytes).ConfigureAwait(false);
      Settle(key, PortraitCaptureState.Ready, wrap, generation);
    }
    catch (Exception ex)
    {
      Plugin.Log.Warning(ex, "Loading a saved portrait failed");
      Settle(key, PortraitCaptureState.Failed, null, generation);
    }
  }

  /// <summary>Whether an Adventurer Plate is open right now — <see cref="OpenPlateKey"/> is not null.</summary>
  public bool IsPlateOpen() => OpenPlateKey() is not null;

  /// <summary>
  /// The Adventurer Plate open right now, or null if none is.
  ///
  /// TRACKED FROM THE ADDON LIFECYCLE, not polled at call time — and that distinction is a real bug fix.
  /// AgentCharaCard.IsAgentActive answers false the instant ANOTHER window takes focus, so clicking a profile
  /// picture (which focuses the profile) made a poll report "no plate open" even with the plate right there, and
  /// the click-to-capture fell through to "no saved portrait". <see cref="OnPlateRefresh"/> records the identity
  /// while the addon is refreshing — when the agent is reliably populated — and <see cref="OnPlateClosed"/> clears
  /// it, so this answers correctly whoever has focus.
  /// </summary>
  public WatcherKey? OpenPlateKey() => _openPlate;

  /// <summary>The plate currently on screen, updated by the addon lifecycle. See <see cref="OpenPlateKey"/>.</summary>
  private WatcherKey? _openPlate;

  /// <summary>Reads the shown player's identity off the plate agent. Called only from the lifecycle handlers,
  /// where the agent is populated; unsafe pointer reads, guarded, cannot throw into the game.</summary>
  private unsafe WatcherKey? ReadPlateIdentity()
  {
    try
    {
      var agent = AgentCharaCard.Instance();
      if (agent == null || agent->Data == null)
        return null;

      var name = agent->Data->Name.ToString();
      var worldId = (uint)agent->Data->WorldId;
      return string.IsNullOrEmpty(name) || worldId == 0 ? null : new WatcherKey(name, worldId);
    }
    catch
    {
      return null;
    }
  }

  private void OnPlateRefresh(AddonEvent type, AddonArgs args)
  {
    // Track the open plate on EVERY refresh, BEFORE the config gate: the profile's "is this plate open" check and
    // the click-to-capture path both read OpenPlateKey, and neither depends on auto-capture being switched on.
    _openPlate = ReadPlateIdentity();

    if (!Plugin.Configuration.CaptureInGamePortraits)
      return;

    // Once per shown player per open — see _lastAutoKey. force: true so opening a plate REFRESHES a saved
    // portrait rather than skipping it, which is the whole point of "recapture on open". Marked done only once
    // the capture actually starts (CaptureOpenPlate returns non-null, i.e. the texture was ready), so a
    // not-yet-rendered plate simply retries on the next refresh instead of being marked and skipped.
    if (_openPlate is not { } key || key == _lastAutoKey)
      return;

    if (CaptureOpenPlate(force: true) is not null)
      _lastAutoKey = key;
  }

  /// <summary>Forgets the open plate and re-arms the auto-capture dedupe when the plate closes, so reopening the
  /// same player is a fresh capture. Framework/main thread, raised by Dalamud.</summary>
  private void OnPlateClosed(AddonEvent type, AddonArgs args)
  {
    _openPlate = null;
    _lastAutoKey = null;
  }

  /// <summary>
  /// Reads the Adventurer Plate open right now, captures its portrait, saves it to disk and caches it under that
  /// player's identity. Returns the identity captured, or null if no plate is open or the texture is not ready.
  /// FRAMEWORK/RENDER THREAD ONLY — it reads the agent and a game texture pointer.
  /// </summary>
  /// <param name="force">A manual capture (true) re-copies even a plate already cached; the auto path (false)
  /// leaves a Ready capture alone.</param>
  public unsafe WatcherKey? CaptureOpenPlate(bool force)
  {
    // Gate #11 of the PvP defence: a plate can be inspected from inside a match, and this reads a name and a
    // world. Refuse rather than cache an enemy's face.
    if (Plugin.ClientState.IsPvP)
      return null;

    try
    {
      var agent = AgentCharaCard.Instance();
      if (agent == null || agent->Data == null)
        return null;

      var data = agent->Data;

      var name = data->Name.ToString();
      var worldId = (uint)data->WorldId;
      if (string.IsNullOrEmpty(name) || worldId == 0)
        return null;

      var key = new WatcherKey(name, worldId);

      var texture = data->PortraitTexture;
      if (texture == null || texture->D3D11ShaderResourceView == null)
        return null;

      var srv = (nint)texture->D3D11ShaderResourceView;
      var width = (int)texture->ActualWidth;
      var height = (int)texture->ActualHeight;
      if (width <= 0 || height <= 0)
        return null;

      int generation;
      lock (_gate)
      {
        if (_cache.TryGetValue(key, out var existing))
        {
          // Already in flight, or already done and this is the passive path: leave it.
          if (existing.State == PortraitCaptureState.Loading)
            return key;
          if (existing.State == PortraitCaptureState.Ready && !force)
            return key;

          // A forced re-capture over a Ready entry: dispose the outgoing texture BEFORE we drop the only
          // reference to it by overwriting the dictionary slot, or it leaks until finalization — the exact
          // discipline Settle and Clear keep.
          if (existing.State == PortraitCaptureState.Ready)
            existing.Texture?.Dispose();
        }

        _cache[key] = new InGamePortrait { State = PortraitCaptureState.Loading };
        generation = _generation;
      }

      // Fire-and-forget: nothing waits for a portrait. CopyStoreAndSave catches its own exceptions.
      _ = CopyStoreAndSave(key, srv, width, height, generation);
      return key;
    }
    catch (Exception ex)
    {
      Plugin.Log.Warning(ex, "Reading the Adventurer Plate failed");
      return null;
    }
  }

  /// <summary>
  /// Copies the game's texture, writes it to disk, and caches a texture read back FROM that file for display.
  ///
  /// THE TWO TEXTURES ARE DELIBERATELY DIFFERENT, and that is the fix for a real crash. The copy from
  /// <see cref="ITextureProvider.CreateFromExistingTextureAsync"/> is an <c>UnknownTextureWrap</c> whose lifetime
  /// DALAMUD owns — it disposes it on a later frame, and a window still drawing it throws ObjectDisposedException
  /// from get_Handle. So that copy is used ONLY to save (then disposed here), and what the window draws is the
  /// disk read-back below: the same <see cref="ITextureProvider.CreateFromImageAsync"/> path the Lodestone faces
  /// use, which returns a texture WE own and dispose, and which Dalamud never pulls out from under us.
  ///
  /// The save is not best-effort here: it is the source of the thing we display. A save failure settles Failed
  /// and the window offers a retry, rather than showing a texture that will be disposed under it.
  /// </summary>
  private async Task CopyStoreAndSave(WatcherKey key, nint srv, int width, int height, int generation)
  {
    try
    {
      var path = PathFor(key);
      Directory.CreateDirectory(PortraitsDir);

      // Transient: copy the game's SRV into a Dalamud texture just long enough to write it out, then dispose it.
      // Never stored, never drawn. leaveWrapOpen on both calls only governs OUR source wrap (a no-op Dispose).
      using (var source = new GameSrvWrap(srv, width, height))
      using (var copy = await Plugin.Textures.CreateFromExistingTextureAsync(source, leaveWrapOpen: true)
        .ConfigureAwait(false))
        await Plugin.TextureReadback.SaveToFileAsync(copy, _pngContainer, path, leaveWrapOpen: true)
          .ConfigureAwait(false);

      // Display a stable, plugin-owned texture read back from the PNG we just wrote.
      var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
      var wrap = await Plugin.Textures.CreateFromImageAsync(bytes).ConfigureAwait(false);
      Settle(key, PortraitCaptureState.Ready, wrap, generation);
    }
    catch (Exception ex)
    {
      Plugin.Log.Warning(ex, "Capturing the in-game portrait failed");
      Settle(key, PortraitCaptureState.Failed, null, generation);
    }
  }

  private void Settle(WatcherKey key, PortraitCaptureState state, IDalamudTextureWrap? texture, int generation)
  {
    lock (_gate)
    {
      // Disposed at unload, or the session cleared under this load at logout (generation moved). Nothing will
      // ever draw it, and the texture is ours — dropping it here stops the logout race from carrying a
      // just-ended session's face forward AND leaking it. Same guard as LodestoneService.Settle.
      if (_disposed || generation != _generation)
      {
        texture?.Dispose();
        return;
      }

      _cache[key] = new InGamePortrait { State = state, Texture = texture };
    }
  }

  /// <summary>
  /// Drops every in-memory portrait texture. Called at logout as well as unload. The SAVED PNGs on disk are left
  /// alone — that is the whole point of saving them — so next session <see cref="Load"/> reads them straight back.
  /// </summary>
  public void Clear()
  {
    lock (_gate)
    {
      foreach (var portrait in _cache.Values)
        portrait.Texture?.Dispose();
      _cache.Clear();

      // Any load/copy launched before now belongs to a session that has ended: bumping the generation makes its
      // settle a no-op instead of a resurrection. Under the lock, so a settle cannot read the old value and write
      // between the clear and the bump.
      _generation++;
    }
  }

  private static string PortraitsDir =>
    Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "Portraits");

  /// <summary>The PNG path for a player. Name is stripped of anything a file name cannot hold, then keyed by the
  /// numeric home-world id — the same identity everything else keys on, and stable across renames of the world.</summary>
  private static string PathFor(WatcherKey key)
  {
    var safe = string.Concat(key.Name.Split(Path.GetInvalidFileNameChars()));
    if (safe.Length == 0)
      safe = "_";
    return Path.Combine(PortraitsDir, $"{safe}@{key.HomeWorldId}.png");
  }

  private static Guid ResolvePngContainer()
  {
    try
    {
      foreach (var info in Plugin.TextureReadback.GetSupportedImageEncoderInfos())
        if (info.Name.Contains("PNG", StringComparison.OrdinalIgnoreCase)
            || info.Extensions.Any(e => e.TrimStart('.').Equals("png", StringComparison.OrdinalIgnoreCase)))
          return info.ContainerGuid;
    }
    catch (Exception ex)
    {
      Plugin.Log.Warning(ex, "Could not resolve the PNG encoder; using the WIC default");
    }

    // WIC GUID_ContainerFormatPng — the stable fallback if the encoder list could not be read.
    return new Guid("1b7cfaf4-713f-473c-bbcd-6137425faeaf");
  }

  /// <summary>
  /// A minimal <see cref="IDalamudTextureWrap"/> over a shader-resource view WE DO NOT OWN — the game's plate
  /// texture. It only hands that view to <see cref="ITextureProvider.CreateFromExistingTextureAsync"/>, which
  /// copies out of it. <see cref="Dispose"/> is a no-op: releasing the game's own SRV would be reaching into
  /// memory that is not ours to free.
  /// </summary>
  private sealed class GameSrvWrap(nint srv, int width, int height) : IDalamudTextureWrap
  {
    public ImTextureID Handle { get; } = new ImTextureID(srv);
    public int Width { get; } = width;
    public int Height { get; } = height;

    public void Dispose()
    {
    }
  }
}
