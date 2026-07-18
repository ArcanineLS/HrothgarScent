using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Dalamud.Utility;

namespace HrothgarScent.Windows;

/// <summary>
/// Small set of drawing helpers that give HrothgarScent a modern, cohesive look
/// (accent-colored underlined section headers, accent separators, FontAwesome status icons,
/// help markers) inspired by the LightlessClient UI. All helpers are built on vanilla
/// Dalamud ImGui + FontAwesome, so there is no global style state to leak.
/// </summary>
internal static class UiTheme
{
  /// <summary>
  /// A larger game font (AXIS 16px) actually baked at that size, used for section headers. Set by
  /// <see cref="Plugin"/> at startup and disposed on shutdown. Scaling the normal font up instead
  /// (font.Scale) upscales the baked glyph bitmaps and looks blurry, so we use a real font here.
  /// </summary>
  public static IFontHandle? HeaderFont { get; set; }

  // Accent palette (matches the LightlessClient family: soft pastel purple/blue with semantic warn/error).
  public static readonly Vector4 AccentPurple = new(0.678f, 0.541f, 0.961f, 1f); // #ad8af5
  public static readonly Vector4 AccentBlue = new(0.651f, 0.761f, 1.000f, 1f);   // #a6c2ff
  public static readonly Vector4 Warn = new(1.000f, 0.914f, 0.478f, 1f);         // #ffe97a
  public static readonly Vector4 Bad = new(0.831f, 0.267f, 0.267f, 1f);          // #d44444
  public static readonly Vector4 Good = new(0.400f, 0.800f, 0.400f, 1f);
  public static readonly Vector4 Muted = new(0.651f, 0.651f, 0.651f, 1f);

  public static Vector4 BoolColor(bool value) => value ? AccentBlue : Bad;

  /// <summary>The plugin's repository, opened by the custom title bar's link button.</summary>
  public const string RepoUrl = "https://github.com/ArcanineLS/HrothgarScent";

  /// <summary>
  /// The plugin's shared custom title bar: a purple header band with an eye icon, a left-aligned title, a
  /// draggable region, and reimplemented link + close buttons. Drawn at the top of any window that sets
  /// <see cref="ImGuiWindowFlags.NoTitleBar"/>, so the config, journal and profile read as one plugin rather
  /// than three different chromes.
  ///
  /// Lifted verbatim from the config window — which was the only window that had it — because the profile and
  /// journal were asked for "the same top bar as config", and one copy is the only thing that keeps that true
  /// as the bar changes. The window itself owns close (a window cannot flip its own IsOpen from a static
  /// helper), so it passes an <paramref name="onClose"/>.
  /// </summary>
  /// <param name="title">The title text, drawn beside the eye icon.</param>
  /// <param name="scale"><see cref="ImGuiHelpers.GlobalScale"/>, threaded in so this makes no second read of it.</param>
  /// <param name="onClose">Invoked when the × is clicked — typically <c>() =&gt; IsOpen = false</c>.</param>
  /// <param name="titleColor">Colour of the title text; defaults to <see cref="AccentBlue"/> as the config bar used.</param>
  /// <param name="linkUrl">Opened by the link button. Null draws no link button.</param>
  /// <param name="showNav">Whether to draw the Journal/Config/HUD nav trio. False leaves ONLY close (plus the link
  /// if <paramref name="linkUrl"/> is set) — the portrait window wants a bare bar, not a launcher.</param>
  /// <returns>Whether the draggable region was double-clicked this frame — the window's cue to toggle collapse.
  /// The custom bar cannot use ImGui's native collapse (NoTitleBar leaves nothing to un-collapse from), so the
  /// window owns the collapsed state and skips its body; see <see cref="CollapsedConstraints"/>.</returns>
  public static bool DrawWindowTitleBar(string title, float scale, Action onClose, Vector4? titleColor = null,
    string? linkUrl = RepoUrl, bool showNav = true)
  {
    var drawList = ImGui.GetWindowDrawList();
    var style = ImGui.GetStyle();

    var origin = ImGui.GetCursorScreenPos();            // content top-left (inside window padding)
    var winPos = ImGui.GetWindowPos();
    var winSize = ImGui.GetWindowSize();
    var contentWidth = ImGui.GetContentRegionAvail().X;

    // Compact title row. The band spans flush to the window's top and side edges (up into the padding) so it
    // reads as a real title bar; interactive elements are centred in the full bar.
    var contentRowHeight = ImGui.GetTextLineHeight() + 4f * scale;
    var bandMin = winPos;
    var bandMax = new Vector2(winPos.X + winSize.X, origin.Y + contentRowHeight);
    var barTop = winPos.Y;
    var barHeight = bandMax.Y - barTop;

    drawList.PushClipRect(winPos, new Vector2(winPos.X + winSize.X, winPos.Y + winSize.Y), false);
    drawList.AddRectFilled(bandMin, bandMax, ImGui.GetColorU32(new Vector4(0.18f, 0.14f, 0.27f, 1f)),
      style.WindowRounding, ImDrawFlags.RoundCornersTop);
    drawList.AddLine(new Vector2(bandMin.X, bandMax.Y), new Vector2(bandMax.X, bandMax.Y),
      ImGui.GetColorU32(AccentPurple), 1.5f * scale);
    drawList.PopClipRect();

    var btnSize = barHeight - 6f * scale;
    var spacing = 4f * scale;
    // close, plus the optional repo link, plus the optional journal/config/hud nav trio. The trio is on every
    // window's bar so any of them is one click away from anywhere in the plugin (the repo-button pattern,
    // extended) — except where a window asks for a bare bar (the portrait), which drops both the trio and the link.
    var buttonCount = 1 + (linkUrl is null ? 0 : 1) + (showNav ? 3 : 0);
    var buttonsWidth = (btnSize + spacing) * buttonCount + spacing;

    // Drag handle across the bar (excluding the right button cluster). A single drag moves the window; a
    // double-click on it (no movement, so the drag branch never fires) asks the window to collapse.
    ImGui.SetCursorScreenPos(new Vector2(origin.X, barTop));
    ImGui.InvisibleButton("##titleDrag", new Vector2(MathF.Max(1f, contentWidth - buttonsWidth), barHeight));
    if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
      ImGui.SetWindowPos(ImGui.GetWindowPos() + ImGui.GetIO().MouseDelta);
    var doubleClicked = ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left);

    // Title: eye icon + name, centred in the full bar height.
    var iconStr = FontAwesomeIcon.Eye.ToIconString();
    Vector2 iconSize;
    using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
      iconSize = ImGui.CalcTextSize(iconStr);
    var titleSize = ImGui.CalcTextSize(title);

    var iconPos = new Vector2(origin.X + 2f * scale, barTop + (barHeight - iconSize.Y) * 0.5f);
    using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
      drawList.AddText(iconPos, ImGui.GetColorU32(AccentPurple), iconStr);

    var titlePos = new Vector2(iconPos.X + iconSize.X + 8f * scale, barTop + (barHeight - titleSize.Y) * 0.5f);
    drawList.AddText(titlePos, ImGui.GetColorU32(titleColor ?? AccentBlue), title);

    // Right-side buttons, laid out RIGHT TO LEFT: close, [repo link], hud, config, journal. Each step walks the
    // cursor left by one button + gap, so adding or dropping the link needs no other arithmetic.
    var purpleHover = new Vector4(AccentPurple.X, AccentPurple.Y, AccentPurple.Z, 0.45f);
    var btnY = barTop + (barHeight - btnSize) * 0.5f;
    var x = origin.X + contentWidth - btnSize;

    if (TitleIconButton("##titleClose", FontAwesomeIcon.Times, new Vector2(x, btnY), btnSize,
          new Vector4(Bad.X, Bad.Y, Bad.Z, 0.6f)))
      onClose();
    Tooltip("Close");
    x -= btnSize + spacing;

    if (linkUrl is not null)
    {
      if (TitleIconButton("##titleLink", FontAwesomeIcon.Link, new Vector2(x, btnY), btnSize, purpleHover))
        Util.OpenLink(linkUrl);
      Tooltip("Open the repository");
      x -= btnSize + spacing;
    }

    if (showNav)
    {
      if (TitleIconButton("##titleHud", FontAwesomeIcon.Desktop, new Vector2(x, btnY), btnSize, purpleHover))
        Plugin.ToggleHud();
      Tooltip("HUD mode — the quiet, chrome-less Scent window.");
      x -= btnSize + spacing;

      if (TitleIconButton("##titleConfig", FontAwesomeIcon.Cog, new Vector2(x, btnY), btnSize, purpleHover))
        Plugin.ToggleConfigWindow();
      Tooltip("Settings.");
      x -= btnSize + spacing;

      if (TitleIconButton("##titleJournal", FontAwesomeIcon.Book, new Vector2(x, btnY), btnSize, purpleHover))
        Plugin.ToggleJournalWindow();
      Tooltip("The journal — everyone you've marked (and, if you record them, met).");
    }

    // Content starts below the band.
    ImGui.SetCursorScreenPos(new Vector2(origin.X, bandMax.Y + 6f * scale));

    return doubleClicked;
  }

  /// <summary>
  /// Window size constraints that lock the window to just the title bar at its current width — a window's
  /// collapsed state. Call it right after <see cref="DrawWindowTitleBar"/> returns true, assign it to
  /// SizeConstraints, skip the body and return; the constraint takes effect on the next frame's Begin, a
  /// one-frame settle that is invisible. Height is measured from the cursor (which sits just below the bar), and
  /// the width is held at the window's current width so collapsing shrinks height alone. Pixel units, matching
  /// the portrait window's own size-lock.
  /// </summary>
  public static WindowSizeConstraints CollapsedConstraints()
  {
    // DIVIDED by GlobalScale: Dalamud multiplies SizeConstraints by it before handing them to ImGui, so a raw
    // pixel measurement here would come out double-scaled at any non-100% UI scale. Cancelling it now keeps the
    // collapsed window exactly the pixel height measured.
    var scale = ImGuiHelpers.GlobalScale;
    var padding = ImGui.GetStyle().WindowPadding;
    var height = (ImGui.GetCursorScreenPos().Y - ImGui.GetWindowPos().Y + padding.Y) / scale;
    var width = ImGui.GetWindowSize().X / scale;
    return new WindowSizeConstraints
    {
      MinimumSize = new Vector2(width, height),
      MaximumSize = new Vector2(width, height),
    };
  }

  /// <summary>
  /// Double-click-to-collapse for a window with a RANGE size constraint (config, journal, profile). One instance
  /// per window — it replaces a bare <c>bool</c> because doing this correctly means REMEMBERING the expanded size.
  ///
  /// The bug it exists to fix: a window has ONE stored size in ImGui, and collapsing (min == max == title bar)
  /// squashes it. Expanding then restored a RANGE constraint (min..max), against which ImGui clamps the now-tiny
  /// stored size UP to the range MINIMUM — so the window sprang back to its default size, never the size it had
  /// before collapsing. This captures the size on the way down and forces it back for a single frame on the way
  /// up, after which the window resizes freely again. A FIXED (min == max) window never had the bug — its
  /// constraint alone restores it — and this stays correct for those too.
  ///
  /// The portrait window does NOT use this: it is content-sized, so skipping its body already shrinks it, with no
  /// stored range to spring back from.
  /// </summary>
  internal sealed class CollapseController
  {
    private bool _collapsed;

    /// <summary>The size to put back on expand, unscaled (Dalamud multiplies <see cref="Window.Size"/> by
    /// GlobalScale), captured the instant before collapse squashes it.</summary>
    private Vector2 _expandedSize;

    /// <summary>Set on expand; forces <see cref="_expandedSize"/> back for exactly the next frame, then clears.</summary>
    private bool _restore;

    public bool Collapsed => _collapsed;

    /// <summary>
    /// Call right after <see cref="DrawWindowTitleBar"/>, feeding its return value as <paramref name="doubleClicked"/>
    /// — <c>if (_collapse.Handle(this, DrawWindowTitleBar(...), _normal)) return;</c>. Arguments evaluate left to
    /// right, so the bar is drawn (moving the cursor below it) before the collapsed height is measured. Returns
    /// whether the window is now collapsed; the caller skips its body and returns on true.
    /// </summary>
    public bool Handle(Window window, bool doubleClicked, WindowSizeConstraints normal)
    {
      var scale = ImGuiHelpers.GlobalScale;

      if (doubleClicked)
      {
        if (!_collapsed)
        {
          // The collapse constraint only takes effect next frame, so the window is still at its expanded size
          // right now — the one moment to record it. Stored unscaled to match how Window.Size is applied.
          _expandedSize = ImGui.GetWindowSize() / scale;
          _collapsed = true;
        }
        else
        {
          _collapsed = false;
          _restore = true;
        }
      }

      if (_collapsed)
      {
        window.SizeConstraints = CollapsedConstraints();
        return true;
      }

      window.SizeConstraints = normal;
      if (_restore)
      {
        // One frame of ImGuiCond.Always to overwrite the squashed stored size with the real one; the range
        // constraint alone would clamp the tiny window up to its minimum instead. Cleared immediately so the very
        // next frame stops forcing and the user can resize again.
        window.Size = _expandedSize;
        window.SizeCondition = ImGuiCond.Always;
        _restore = false;
      }
      else
      {
        // Not forcing any size: the window keeps whatever ImGui stored, and stays freely resizable. (This also
        // retires the constructor's FirstUseEver seed, which has already been applied by the time Handle runs.)
        window.Size = null;
      }

      return false;
    }
  }

  /// <summary>
  /// Draw-list based icon button for <see cref="DrawWindowTitleBar"/>, so the glyph is centred by its real size
  /// and never clipped by frame padding (which is what cut off the icon when using <see cref="ImGui.Button"/> at
  /// a small size).
  /// </summary>
  private static bool TitleIconButton(string id, FontAwesomeIcon icon, Vector2 pos, float size, Vector4 hoverColor)
  {
    ImGui.SetCursorScreenPos(pos);
    ImGui.InvisibleButton(id, new Vector2(size, size));
    var hovered = ImGui.IsItemHovered();
    var clicked = ImGui.IsItemClicked(ImGuiMouseButton.Left);

    var drawList = ImGui.GetWindowDrawList();
    if (hovered)
      drawList.AddRectFilled(pos, new Vector2(pos.X + size, pos.Y + size), ImGui.GetColorU32(hoverColor), 4f);

    var iconStr = icon.ToIconString();
    Vector2 glyphSize;
    using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
      glyphSize = ImGui.CalcTextSize(iconStr);

    var glyphPos = new Vector2(pos.X + (size - glyphSize.X) * 0.5f, pos.Y + (size - glyphSize.Y) * 0.5f);
    var iconColor = hovered ? new Vector4(1f, 1f, 1f, 1f) : Muted;
    using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
      drawList.AddText(glyphPos, ImGui.GetColorU32(iconColor), iconStr);

    return clicked;
  }

  /// <summary>Renders a FontAwesome glyph inline using the icon font.</summary>
  public static void Icon(FontAwesomeIcon icon, Vector4? color = null)
  {
    var str = icon.ToIconString();
    using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
    {
      if (color.HasValue)
      {
        ImGui.PushStyleColor(ImGuiCol.Text, color.Value);
        ImGui.TextUnformatted(str);
        ImGui.PopStyleColor();
      }
      else
        ImGui.TextUnformatted(str);
    }
  }

  /// <summary>Inline colored check/cross icon for boolean state.</summary>
  public static void BoolIcon(bool value)
  {
    Icon(value ? FontAwesomeIcon.Check : FontAwesomeIcon.Times, BoolColor(value));
  }

  /// <summary>
  /// A square FontAwesome icon button for the left navigation rail. The selected item gets a
  /// filled purple accent; the rest are transparent with a subtle purple hover.
  /// </summary>
  public static bool NavButton(FontAwesomeIcon icon, bool selected, float size, string tooltip, int id)
  {
    if (selected)
    {
      ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(AccentPurple.X, AccentPurple.Y, AccentPurple.Z, 0.40f));
      ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(AccentPurple.X, AccentPurple.Y, AccentPurple.Z, 0.55f));
      ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(AccentPurple.X, AccentPurple.Y, AccentPurple.Z, 0.65f));
      ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 1f, 1f, 1f));
    }
    else
    {
      ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0f, 0f, 0f, 0f));
      ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(AccentPurple.X, AccentPurple.Y, AccentPurple.Z, 0.25f));
      ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(AccentPurple.X, AccentPurple.Y, AccentPurple.Z, 0.35f));
      ImGui.PushStyleColor(ImGuiCol.Text, Muted);
    }

    ImGui.PushID(id);
    bool clicked;
    using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
      clicked = ImGui.Button(icon.ToIconString(), new Vector2(size, size));
    ImGui.PopID();
    ImGui.PopStyleColor(4);

    if (!string.IsNullOrEmpty(tooltip))
      Tooltip(tooltip);

    return clicked;
  }

  /// <summary>
  /// The signature section header: an accent icon + accent-colored label with a full-width
  /// accent underline. Use this instead of plain <c>ImGui.Separator()</c> + text.
  /// </summary>
  public static void SectionHeader(string label, FontAwesomeIcon? icon = null, Vector4? color = null)
  {
    var accent = color ?? AccentBlue;

    ImGui.Dummy(new Vector2(0, 3f * ImGuiHelpers.GlobalScale));

    var headerStart = ImGui.GetCursorScreenPos();
    var fullWidth = ImGui.GetContentRegionAvail().X;
    var gap = 8f * ImGuiHelpers.GlobalScale;
    var useBigFont = HeaderFont is { Available: true };

    // Measure the icon (icon font) and label (header font) so the icon+text group can be centered.
    var iconSize = Vector2.Zero;
    if (icon.HasValue)
    {
      using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
        iconSize = ImGui.CalcTextSize(icon.Value.ToIconString());
    }

    var measurePush = useBigFont ? HeaderFont!.Push() : null;
    var labelSize = ImGui.CalcTextSize(label);
    measurePush?.Dispose();

    var totalWidth = labelSize.X + (icon.HasValue ? iconSize.X + gap : 0f);
    var offsetX = MathF.Max(0f, (fullWidth - totalWidth) * 0.5f);
    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + offsetX);

    if (icon.HasValue)
    {
      Icon(icon.Value, accent);
      ImGui.SameLine(0, gap);
    }

    // Crisp accent-colored heading (falls back to the normal font until the larger one builds).
    var fontPush = useBigFont ? HeaderFont!.Push() : null;
    try
    {
      ImGui.PushStyleColor(ImGuiCol.Text, accent);
      ImGui.TextUnformatted(label);
      ImGui.PopStyleColor();
    }
    finally
    {
      fontPush?.Dispose();
    }

    // Full-width underline beneath the centered header.
    var y = ImGui.GetItemRectMax().Y + 2f * ImGuiHelpers.GlobalScale;
    ImGui.GetWindowDrawList().AddLine(new Vector2(headerStart.X, y), new Vector2(headerStart.X + fullWidth, y),
      ImGui.GetColorU32(accent), 1.5f * ImGuiHelpers.GlobalScale);

    ImGui.Dummy(new Vector2(0, 7f * ImGuiHelpers.GlobalScale));
  }

  /// <summary>
  /// Exactly what <see cref="SectionHeader"/> is about to consume vertically. Derived from the same pushes and
  /// the same style the drawing code uses, because a caller reserving space for this block cannot see the
  /// header font's line height or the ItemSpacing between the three items it emits — and a reserve that guesses
  /// them creeps the footer down the window or forces a scrollbar.
  ///
  /// Pass the same <paramref name="icon"/> the <see cref="SectionHeader"/> call passes, or this is short by
  /// however far the icon font's line height exceeds the header font's: the icon and the label sit on ONE line,
  /// so the line is the taller of the two, and nothing visible from in here says whether there is an icon on it.
  /// </summary>
  public static float SectionHeaderHeight(FontAwesomeIcon? icon = null)
  {
    var scale = ImGuiHelpers.GlobalScale;

    var iconHeight = 0f;
    if (icon.HasValue)
    {
      using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
        iconHeight = ImGui.CalcTextSize(icon.Value.ToIconString()).Y;
    }

    var push = HeaderFont is { Available: true } ? HeaderFont.Push() : null;
    var line = ImGui.GetTextLineHeight();
    push?.Dispose();

    return 3f * scale + MathF.Max(line, iconHeight) + 7f * scale + ImGui.GetStyle().ItemSpacing.Y * 3f;
  }

  /// <summary>Full-width accent-colored separator with a little vertical breathing room.</summary>
  public static void AccentSeparator(Vector4? color = null, float thickness = 1f)
  {
    var drawList = ImGui.GetWindowDrawList();
    var min = ImGui.GetCursorScreenPos();
    var width = ImGui.GetContentRegionAvail().X;
    drawList.AddLine(min, new Vector2(min.X + width, min.Y),
      ImGui.GetColorU32(color ?? Muted), thickness * ImGuiHelpers.GlobalScale);
    ImGui.Dummy(new Vector2(0, (thickness + 3f) * ImGuiHelpers.GlobalScale));
  }

  /// <summary>Grey "(?)" hint that shows <paramref name="text"/> on hover.</summary>
  public static void HelpMarker(string text) => HelpMarker(Muted, "(?)", text);

  /// <summary>
  /// A help marker that can raise its voice.
  ///
  /// Same glyph, different colour: a marker that is merely available and one that is reporting a problem are
  /// the same control in two states, not two controls. Callers measuring the line must measure
  /// <paramref name="glyph"/> AND the leading gap this opens with — see DrawToolbar.
  /// </summary>
  public static void HelpMarker(Vector4 color, string glyph, string text)
  {
    ImGui.SameLine(0, 4f * ImGuiHelpers.GlobalScale);
    ImGui.PushStyleColor(ImGuiCol.Text, color);
    ImGui.TextUnformatted(glyph);
    ImGui.PopStyleColor();
    Tooltip(text);
  }

  /// <summary>
  /// Greys out everything until the matching <see cref="PopDimmed"/>, without disabling it.
  ///
  /// BeginDisabled is the obvious call and the wrong one for anything that only needs to look inactive:
  /// besides the alpha, it pushes ImGui's disabled item flag, and IsItemHovered answers false for any item
  /// carrying that flag unless explicitly asked otherwise. Tooltips on the greyed-out text therefore go
  /// silently dead — on exactly the text whose tooltip is worth reading. This pushes the alpha alone, which
  /// is the whole of what BeginDisabled does visually. Use it for text; keep BeginDisabled for controls that
  /// genuinely must refuse input.
  /// </summary>
  public static void PushDimmed()
  {
    var style = ImGui.GetStyle();
    ImGui.PushStyleVar(ImGuiStyleVar.Alpha, style.Alpha * style.DisabledAlpha);
  }

  /// <summary>Ends the span opened by <see cref="PushDimmed"/>.</summary>
  public static void PopDimmed() => ImGui.PopStyleVar();

  /// <summary>Shows a wrapped tooltip for the previous item when hovered.</summary>
  public static void Tooltip(string text)
  {
    if (!ImGui.IsItemHovered())
      return;

    DrawTooltip(text);
  }

  /// <summary>
  /// A tooltip that survives <see cref="ImGui.BeginDisabled(bool)"/>, for the one shape where the control must
  /// genuinely refuse input AND its tooltip is the thing explaining why it is refusing.
  ///
  /// <see cref="Tooltip"/> goes silently dead on a disabled item — the same trap <see cref="PushDimmed"/>
  /// documents, reached from the other side. ImGui records the disabled flag onto the item as it is submitted,
  /// and IsItemHovered answers false for it unless explicitly asked otherwise. Calling this AFTER EndDisabled
  /// does not help by itself: the flag lives in the item's own captured state, not in the current stack, so the
  /// plain answer stays false. Call it after EndDisabled anyway, so the tooltip's own text draws at full alpha
  /// rather than greyed out.
  ///
  /// Deliberately not the default. A tooltip that ignores the disabled flag on a control disabled for a reason
  /// the user can already see is just noise; this is for the ones that have to say where the switch is.
  /// </summary>
  public static void TooltipEvenIfDisabled(string text)
  {
    if (!ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
      return;

    DrawTooltip(text);
  }

  /// <summary>The tooltip body itself, shared so the wrap width cannot drift between the two hover tests.</summary>
  private static void DrawTooltip(string text)
  {
    ImGui.BeginTooltip();
    ImGui.PushTextWrapPos(ImGui.GetFontSize() * 35f);
    ImGui.TextUnformatted(text);
    ImGui.PopTextWrapPos();
    ImGui.EndTooltip();
  }

  /// <summary>Colored, wrapped body text.</summary>
  public static void TextWrappedColored(Vector4 color, string text)
  {
    ImGui.PushStyleColor(ImGuiCol.Text, color);
    ImGui.PushTextWrapPos(0f);
    ImGui.TextUnformatted(text);
    ImGui.PopTextWrapPos();
    ImGui.PopStyleColor();
  }

  /// <summary>
  /// Scratch text-entry state for <see cref="SliderIntManual"/>, keyed by ImGui widget ID. An entry
  /// exists only while that slider is in type-a-value mode. The value is held here (not written back to
  /// the config) until it is committed, so a half-typed number never reaches the caller.
  /// </summary>
  private sealed class SliderManualEdit
  {
    /// <summary>Live text-box contents. Held unclamped until commit — see the note in the draw code.</summary>
    public int Buffer;

    /// <summary>SetKeyboardFocusHere has been issued; don't re-issue it every frame.</summary>
    public bool Focused;

    /// <summary>The box has been observed active at least once, so deactivation now means something.</summary>
    public bool WasActive;
  }

  private static readonly Dictionary<uint, SliderManualEdit> _sliderManualEdit = [];

  /// <summary>
  /// The slider's value as of the frame the user grabbed it, before the click moved it. Kept so that a
  /// double-click can put back what the first of its two clicks knocked over.
  /// </summary>
  private static readonly Dictionary<uint, int> _sliderPreDrag = [];

  /// <summary>
  /// A slider that can also be typed into: double-click it to swap to a text box, Enter or click-away
  /// to commit, Escape to cancel. (ImGui's built-in Ctrl+Click still works too.) Returns true whenever
  /// <paramref name="value"/> changed and should be persisted, so callers use it exactly like a slider.
  ///
  /// Set the item width with <c>ImGui.SetNextItemWidth</c> before calling, exactly like a bare slider.
  /// </summary>
  public static bool SliderIntManual(string label, ref int value, int min, int max, string? format = null)
  {
    var id = ImGui.GetID(label);

    if (_sliderManualEdit.TryGetValue(id, out var edit))
    {
      // Take focus once, so the double-click flows straight into typing.
      if (!edit.Focused)
      {
        ImGui.SetKeyboardFocusHere();
        edit.Focused = true;
      }

      // Own ID ("##manual"), NOT the slider's. Reusing the slider's ID hands the box the slider's
      // just-clicked ActiveId history, and ImGui clearing that stale state reads here as an immediate
      // deactivation — which would close the box on its first frame and make this a dead feature.
      //
      // Step buttons off (0, 0) — they'd eat the width and this is a type-a-number box.
      // Deliberately NOT clamped per keystroke: that would rewrite the number as it's typed (with min
      // 200, typing "1500" would snap to 200 the moment "1" landed). Clamp once, on commit.
      var buffer = edit.Buffer;
      ImGui.InputInt(label + "##manual", ref buffer, 0, 0, "%d", ImGuiInputTextFlags.AutoSelectAll);
      edit.Buffer = buffer;

      if (ImGui.IsItemActive())
        edit.WasActive = true;

      // Until the box has actually been active, a deactivation signal is left over from the slider and
      // must not be believed. This also covers the frames between submitting the focus request and ImGui
      // granting it.
      if (!edit.WasActive)
        return false;

      if (ImGui.IsItemDeactivatedAfterEdit())
      {
        // Committed: Enter, Tab, or clicking away after editing. Escape can also land here, but ImGui has
        // already restored the pre-edit text by then, so it settles as a no-op on the equality check.
        _sliderManualEdit.Remove(id);
        var committed = Math.Clamp(buffer, min, max);
        if (committed == value)
          return false;

        value = committed;
        return true;
      }

      // Cancelled (Escape, or focus lost without an edit), or abandoned because the tab/window changed
      // out from under an open box — without this it would still be a text box on the way back.
      if (ImGui.IsItemDeactivated() || !ImGui.IsItemActive())
        _sliderManualEdit.Remove(id);

      return false;
    }

    var before = value;
    var changed = ImGui.SliderInt(label, ref value, min, max, format ?? "%d", ImGuiSliderFlags.AlwaysClamp);

    // A double-click reaches us as two separate clicks, and ImGui's slider jumps the value to the cursor
    // on the FIRST one — which the caller has already saved by the time the second click tells us this
    // was a double-click. So restore the value from before that first click and return true, which makes
    // the caller persist the restore: double-clicking to type leaves the setting where it started.
    if ((ImGui.IsItemActive() || ImGui.IsItemHovered()) && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
    {
      var seed = _sliderPreDrag.TryGetValue(id, out var pre) ? pre : before;
      _sliderPreDrag.Remove(id);

      _sliderManualEdit[id] = new SliderManualEdit { Buffer = seed };
      if (seed == value)
        return false;

      value = seed;
      return true;
    }

    // Grabbing the slider is the last moment its pre-click value still exists; `before` is this frame's
    // value as read by the caller, i.e. from before ImGui moved it. (Left in place afterwards rather than
    // cleared: the next grab overwrites it, and it is only ever read on a double-click's second click.)
    if (ImGui.IsItemActivated())
      _sliderPreDrag[id] = before;

    return changed;
  }
}
