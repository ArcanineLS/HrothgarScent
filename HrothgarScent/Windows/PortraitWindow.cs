using System;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using HrothgarScent.Scent;

namespace HrothgarScent.Windows;

/// <summary>
/// One player's in-game portrait, on its own — the Adventurer Plate face, shown big.
///
/// A window of its own, opened ONLY by clicking a profile picture (see ProfileWindow.DrawAvatar). Loading is that
/// click's job, not a tab's and not a timer's: <see cref="Open"/> asks <see cref="PortraitService.Load"/> for the
/// saved PNG, or a capture from the plate if it is the one open. This window then just draws whatever that
/// settled, so the network/GPU work never happens on a per-frame path.
///
/// SIZED TO FIT, NOT AlwaysAutoResize. Auto-resize hugs the portrait but fights the custom title bar's drag —
/// its drag handle is measured from GetContentRegionAvail, which is ill-defined for an auto-resizing window, so
/// dragging the bar GREW the window instead of moving it. Instead this measures its own content each frame and
/// LOCKS the window to that size (min == max), which fits just as snugly, cannot be resized, and moves cleanly.
///
/// KEYED BY <see cref="WatcherKey"/> like the profile, and PvP-gated like it too: it names a person and shows
/// their face, so a match must not leave it drawing one.
/// </summary>
public sealed class PortraitWindow : Window
{
  /// <summary>The display box the portrait is fitted into (before UI scale). The plate texture can be large, so
  /// it is capped rather than shown at native size; the window is then locked to whatever this produces. The
  /// width also bounds the status messages so a text state does not force an over-wide window.</summary>
  private const float MaxWidth = 300f;

  private const float MaxHeight = 460f;

  /// <summary>Floor on the content width, so a very tall (narrow) portrait or a short message still leaves the
  /// title bar room for the name and its two buttons.</summary>
  private const float MinWidth = 220f;

  private WatcherKey? _key;
  private string _worldName = string.Empty;
  private bool _collapsed;

  public PortraitWindow() : base("Portrait##hrothgarscent-portrait",
      ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoResize)
  {
    // A first-use size only; every frame after, Draw locks the window to exactly fit its content (see the class
    // note). NoResize because the size is ours to set — the fit would just be dragged back open otherwise.
    Size = new Vector2(320, 400);
    SizeCondition = ImGuiCond.FirstUseEver;
  }

  /// <summary>Gate #12 of the PvP defence — the portrait window's own statement, on the same terms as the
  /// profile's (ProfileWindow.DrawConditions).</summary>
  public override bool DrawConditions() => !Plugin.ClientState.IsPvP;

  /// <summary>Opens on one player and asks for their portrait — the click on a profile picture lands here.</summary>
  public void Open(WatcherKey key, string worldName)
  {
    _key = key;
    _worldName = worldName;
    Plugin.Portraits.Load(key);
    IsOpen = true;
  }

  public override void Draw()
  {
    var scale = ImGuiHelpers.GlobalScale;

    // A bare bar: close only, no nav trio and no repo link. This window is a single face opened from the profile,
    // not a place you launch the plugin's other windows from, so the launcher buttons are just clutter here.
    if (UiTheme.DrawWindowTitleBar(_key is { } named ? $"{named.Name} — Portrait" : "Portrait", scale,
          () => IsOpen = false, linkUrl: null, showNav: false))
      _collapsed = !_collapsed;

    if (_collapsed)
    {
      // Collapsed: skip the body. The window is content-sized, so LockSizeToContent measuring the cursor just
      // below the bar shrinks it to the bar alone — no fixed-constraint dance needed here.
      LockSizeToContent(MinWidth * scale, scale);
      return;
    }

    // The widest thing drawn below, so the window can be locked to fit it. Starts at the title-bar floor and is
    // raised to the portrait's own width where there is one.
    var contentWidth = MinWidth * scale;

    if (_key is not { } key)
    {
      Message(scale, "Click a player's profile picture to load their in-game portrait.");
      LockSizeToContent(contentWidth, scale);
      return;
    }

    var portrait = Plugin.Portraits.Get(key);
    var thisPlateOpen = Plugin.Portraits.OpenPlateKey() == key;

    switch (portrait.State)
    {
      case PortraitCaptureState.Ready when portrait.Texture is { } texture:
        try
        {
          contentWidth = MathF.Max(contentWidth, DrawFitted(texture, scale));
        }
        catch (ObjectDisposedException)
        {
          // A texture disposed under us — belt and braces now that the displayed texture is a plugin-owned
          // CreateFromImageAsync one and should never be. Drop it and reload rather than take the window down.
          Plugin.Portraits.Forget(key);
          Plugin.Portraits.Load(key);
          Message(scale, "Refreshing…");
          break;
        }

        // Ready is JUST the portrait — no button, so the window is only the face. Right-click reveals the saved
        // PNG in the file explorer; to refresh, reopen their Adventurer Plate (recapture happens on open).
        DrawImageContextMenu(key);
        break;

      case PortraitCaptureState.Loading:
        Message(scale, "Loading their portrait…");
        contentWidth = MaxWidth * scale;
        break;

      case PortraitCaptureState.Failed:
        Message(scale, "Couldn't load their portrait. If it was a bad save, capture a fresh one from their plate.");
        DrawCaptureButton(key, thisPlateOpen);
        contentWidth = MaxWidth * scale;
        break;

      default: // Idle / Missing
        Message(scale, thisPlateOpen
          ? "Their Adventurer Plate is open — capture it below, or click their profile picture again."
          : "No saved portrait yet. Open their Adventurer Plate in-game — search them on the plate window, or "
            + "right-click their name and pick View Adventurer Plate — then click their profile picture again.");
        DrawCaptureButton(key, thisPlateOpen);
        contentWidth = MaxWidth * scale;
        break;
    }

    LockSizeToContent(contentWidth, scale);
  }

  /// <summary>
  /// Locks the window to exactly fit <paramref name="contentWidth"/> and the height actually drawn this frame,
  /// applied on the next frame. A fixed min == max size rather than <see cref="ImGuiWindowFlags.AlwaysAutoResize"/>,
  /// so the fit does not fight the title bar's drag — see the class note. Height is measured from the cursor, so
  /// the title bar and whatever state drew below it are all counted without hand-summing their heights.
  /// </summary>
  private void LockSizeToContent(float contentWidth, float scale)
  {
    var padding = ImGui.GetStyle().WindowPadding;
    var height = ImGui.GetCursorScreenPos().Y - ImGui.GetWindowPos().Y + padding.Y;

    // DIVIDED by scale: Dalamud multiplies SizeConstraints by GlobalScale, so a raw pixel size would come out
    // double-scaled at any non-100% UI scale. Cancelling it keeps the window exactly the pixels measured.
    var size = new Vector2(contentWidth + padding.X * 2f, height) / scale;

    SizeConstraints = new WindowSizeConstraints { MinimumSize = size, MaximumSize = size };
  }

  /// <summary>Draws the portrait fitted into the <see cref="MaxWidth"/>×<see cref="MaxHeight"/> box, aspect
  /// preserved, and returns the width it used so the window can be locked to it. Aspect from the texture's own
  /// dimensions, guarded against a zero height.</summary>
  private static float DrawFitted(IDalamudTextureWrap texture, float scale)
  {
    var maxWidth = MaxWidth * scale;
    var maxHeight = MaxHeight * scale;

    var aspect = texture.Height > 0 ? (float)texture.Width / texture.Height : 1f;
    var width = maxWidth;
    var height = width / aspect;
    if (height > maxHeight)
    {
      height = maxHeight;
      width = height * aspect;
    }

    ImGui.Image(texture.Handle, new Vector2(width, height));
    return width;
  }

  /// <summary>Right-click the portrait to reveal its saved PNG in the file explorer. Attaches to the image — the
  /// last item drawn before this — so the right-click is on the picture itself, as asked.</summary>
  private static void DrawImageContextMenu(WatcherKey key)
  {
    if (Plugin.Portraits.SavedPath(key) is not { } path)
      return;

    if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
      ImGui.OpenPopup("##portraitFileMenu");

    if (!ImGui.BeginPopup("##portraitFileMenu"))
      return;

    if (ImGui.MenuItem("Open file location"))
      OpenFileLocation(path);

    ImGui.EndPopup();
  }

  /// <summary>Opens the OS file explorer with the saved PNG selected. Guarded — a shell that will not launch is a
  /// warning in the log, never a fault the window has to handle.</summary>
  private static void OpenFileLocation(string path)
  {
    try
    {
      if (!File.Exists(path))
        return;

      // explorer /select opens the containing folder AND highlights the file, which is what "open file location"
      // means on Windows. UseShellExecute so it launches through the shell rather than as a child we own.
      Process.Start(new ProcessStartInfo
      {
        FileName = "explorer.exe",
        Arguments = $"/select,\"{path}\"",
        UseShellExecute = true,
      });
    }
    catch (Exception ex)
    {
      Plugin.Log.Warning(ex, "Could not open the portrait's file location");
    }
  }

  /// <summary>A status line wrapped at <see cref="MaxWidth"/>, so a text state does not force an over-wide window.
  /// <see cref="UiTheme.TextWrappedColored"/> wraps at the window edge, which is circular while the window is being
  /// sized from its content — hence the explicit wrap position here.</summary>
  private static void Message(float scale, string text)
  {
    ImGui.PushStyleColor(ImGuiCol.Text, UiTheme.Muted);
    ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + MaxWidth * scale);
    ImGui.TextUnformatted(text);
    ImGui.PopTextWrapPos();
    ImGui.PopStyleColor();
  }

  /// <summary>The capture button, shown only in the empty/failed states — disabled unless THIS player's plate is
  /// the one open, since the capture reads whatever plate is on screen. The Ready state carries no button.</summary>
  private static void DrawCaptureButton(WatcherKey key, bool thisPlateOpen)
  {
    using (ImRaii.Disabled(!thisPlateOpen))
      if (ImGui.Button("Capture from plate"))
        Plugin.Portraits.CaptureOpenPlate(force: true);

    UiTheme.TooltipEvenIfDisabled(thisPlateOpen
      ? "Copy the portrait from their Adventurer Plate, open right now, and save it to disk."
      : "Open their Adventurer Plate in-game to capture it.");
  }
}
