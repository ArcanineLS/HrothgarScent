using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Dalamud.Utility;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using HrothgarScent.Scent;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace HrothgarScent.Windows;

public sealed class ConfigWindow : Window
{
  private const string RepoUrl = "https://github.com/ArcanineLS/HrothgarScent";

  /// <summary>
  /// The chat SFX the game exposes as &lt;se.1&gt;..&lt;se.16&gt;. Built once: Combo wants a string array,
  /// and rebuilding one every frame would allocate sixteen strings for a menu that never changes.
  /// </summary>
  private static readonly string[] _soundNames = [.. Enumerable.Range(1, 16).Select(i => $"<se.{i}>")];

  private static readonly string[] _lodestoneRegionStrings = Enum.GetNames<LodestoneRegion>();

  /// <summary>Role buckets in party order, for the per-job colour grid. Other is last because it is the
  /// fallback bucket, not a role anyone sorts by.</summary>
  private static readonly JobRole[] _roleOrder =
    [JobRole.Tank, JobRole.Healer, JobRole.MeleeDps, JobRole.RangedDps, JobRole.Other];

  /// <summary>Per-job swatches per line. Ranged DPS is the widest bucket at seven, which overflows a
  /// default-width window on one line.</summary>
  private const int JobColorsPerRow = 5;

  /// <summary>Race ticks per line. Four splits the eight evenly and still fits the longest names at the
  /// window's minimum width.</summary>
  private const int RacesPerRow = 4;

  /// <summary>ImGui id of the repair box. One constant, because OpenPopup and BeginPopup must agree exactly and
  /// they sit in different methods.</summary>
  private const string RepairPopupId = "##hrothgarscent-repair";

  /// <summary>32 is the game's own character-name limit ("Firstname Lastname", 15 each plus the space); worlds
  /// are far shorter. Bounds on a text box, not validation — WorldPalette does the validating.</summary>
  private const int MarkNameMaxLength = 32;

  private const int MarkWorldMaxLength = 32;

  /// <summary>What a dimmed row means, in one place: the tooltip says it on the name, and both arms of that
  /// tooltip need the same sentence.</summary>
  private const string StaleHelp =
    "Hrothgar not smell this one in a while. Maybe they stopped playing — or maybe they renamed or moved " +
    "world, which loses the trail. Hit Renamed? to say who they are now.";

  private int _selectedTab;

  /// <summary>Who the open repair box is about, and the two fields it is editing. Held across frames while the
  /// popup is up; the key is what identifies the record, since the name being edited is the thing changing.</summary>
  private WatcherKey _repairKey;

  private string _repairName = string.Empty;
  private string _repairWorld = string.Empty;

  public ConfigWindow()
    : base("HrothgarScent",
        ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoScrollbar)
  {
    SizeConstraints = new WindowSizeConstraints
    {
      MinimumSize = new Vector2(540, 500),
      MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
    };
  }

  public override void Draw()
  {
    var scale = ImGuiHelpers.GlobalScale;

    DrawCustomTitleBar(scale);

    var railWidth = 48f * scale;
    var barHeight = ImGui.GetTextLineHeightWithSpacing() + 8f * scale;
    var bodyHeight = ImGui.GetContentRegionAvail().Y - barHeight;

    // Left icon navigation rail (LightlessClient-style).
    ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(1f, 1f, 1f, 0.03f));
    ImGui.BeginChild("##nav", new Vector2(railWidth, bodyHeight), true);
    ImGui.PopStyleColor();
    DrawNavButton(FontAwesomeIcon.Eye, 0, "General");
    DrawNavButton(FontAwesomeIcon.Filter, 1, "Filters");
    DrawNavButton(FontAwesomeIcon.Palette, 2, "Colours");
    DrawNavButton(FontAwesomeIcon.Bell, 3, "Alerts");
    DrawNavButton(FontAwesomeIcon.History, 4, "Watchers");
    DrawNavButton(FontAwesomeIcon.Desktop, 5, "HUD");
    ImGui.EndChild();

    ImGui.SameLine();

    // Right content pane.
    ImGui.BeginChild("##content", new Vector2(0, bodyHeight));
    UiTheme.TextWrappedColored(UiTheme.Muted, "Hrothgar smell you. Hrothgar know who watching.");
    ImGui.Dummy(new Vector2(0, 3f * scale));
    switch (_selectedTab)
    {
      case 1:
        DrawFiltersTab();
        break;
      case 2:
        DrawColoursTab();
        break;
      case 3:
        DrawAlertsTab();
        break;
      case 4:
        DrawWatchersTab();
        break;
      case 5:
        DrawHudTab();
        break;
      default:
        DrawGeneralTab();
        break;
    }
    ImGui.EndChild();

    DrawStatusBar();
  }

  private static void DrawStatusBar()
  {
    UiTheme.AccentSeparator(UiTheme.AccentPurple, 1f);

    var scale = ImGuiHelpers.GlobalScale;

    // Straight off the published snapshot, like every other reader — no object table from Draw, ever.
    var snapshot = Plugin.Scanner.Snapshot;

    // Users, not Eye, for the nearby count. The eye means "this one is looking at you" everywhere else in
    // this plugin, and spending it on a plain headcount would dilute the one symbol that carries the point.
    UiTheme.Icon(FontAwesomeIcon.Users, UiTheme.AccentBlue);
    ImGui.SameLine(0, 4f * scale);
    ImGui.TextColored(UiTheme.AccentBlue, $"{snapshot.NearbyCount} nearby");

    ImGui.SameLine(0, 10f * scale);
    ImGui.TextColored(UiTheme.Muted, "|");
    ImGui.SameLine(0, 10f * scale);

    var watched = snapshot.WatcherCount > 0;
    var watcherColor = watched ? Plugin.Configuration.ColorWatcher : UiTheme.Muted;
    UiTheme.Icon(FontAwesomeIcon.Eye, watcherColor);
    ImGui.SameLine(0, 4f * scale);
    ImGui.TextColored(watcherColor, $"{snapshot.WatcherCount} watching");

    // Only shown while it is true, so it reads as an active state rather than a permanent label.
    if (Plugin.ClientState.IsPvP)
    {
      ImGui.SameLine(0, 10f * scale);
      ImGui.TextColored(UiTheme.Muted, "|");
      ImGui.SameLine(0, 10f * scale);
      UiTheme.Icon(FontAwesomeIcon.ShieldAlt, UiTheme.Warn);
      ImGui.SameLine(0, 4f * scale);
      ImGui.TextColored(UiTheme.Warn, "PvP — hidden");
    }

    // Right: version, right-aligned.
    var version = $"v{Plugin.PluginInterface.Manifest.AssemblyVersion}";
    var textWidth = ImGui.CalcTextSize(version).X;
    ImGui.SameLine(0, 0);
    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - textWidth);
    ImGui.TextColored(UiTheme.Muted, version);
  }

  private void DrawNavButton(FontAwesomeIcon icon, int index, string label)
  {
    var size = ImGui.GetContentRegionAvail().X;
    if (UiTheme.NavButton(icon, _selectedTab == index, size, label, index))
      _selectedTab = index;
    ImGui.Spacing();
  }

  /// <summary>
  /// Fully custom title bar (the window uses <see cref="ImGuiWindowFlags.NoTitleBar"/>). Draws a
  /// purple header band with the title, a draggable region, and reimplemented link + close buttons.
  /// </summary>
  private void DrawCustomTitleBar(float scale)
  {
    var drawList = ImGui.GetWindowDrawList();
    var style = ImGui.GetStyle();

    var origin = ImGui.GetCursorScreenPos();            // content top-left (inside window padding)
    var winPos = ImGui.GetWindowPos();
    var winSize = ImGui.GetWindowSize();
    var contentWidth = ImGui.GetContentRegionAvail().X;

    // Compact title row. The band spans flush to the window's top and side edges (up into the
    // padding) so it reads as a real title bar; interactive elements are centered in the full bar.
    var contentRowHeight = ImGui.GetTextLineHeight() + 4f * scale;
    var bandMin = winPos;
    var bandMax = new Vector2(winPos.X + winSize.X, origin.Y + contentRowHeight);
    var barTop = winPos.Y;
    var barHeight = bandMax.Y - barTop;

    drawList.PushClipRect(winPos, new Vector2(winPos.X + winSize.X, winPos.Y + winSize.Y), false);
    drawList.AddRectFilled(bandMin, bandMax, ImGui.GetColorU32(new Vector4(0.18f, 0.14f, 0.27f, 1f)),
      style.WindowRounding, ImDrawFlags.RoundCornersTop);
    drawList.AddLine(new Vector2(bandMin.X, bandMax.Y), new Vector2(bandMax.X, bandMax.Y),
      ImGui.GetColorU32(UiTheme.AccentPurple), 1.5f * scale);
    drawList.PopClipRect();

    var btnSize = barHeight - 6f * scale;
    var spacing = 4f * scale;
    var buttonsWidth = (btnSize + spacing) * 2f + spacing;

    // Drag handle across the bar (excluding the right button cluster).
    ImGui.SetCursorScreenPos(new Vector2(origin.X, barTop));
    ImGui.InvisibleButton("##titleDrag", new Vector2(MathF.Max(1f, contentWidth - buttonsWidth), barHeight));
    if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
      ImGui.SetWindowPos(ImGui.GetWindowPos() + ImGui.GetIO().MouseDelta);

    // Title: eye icon + name, centered in the full bar height.
    var iconStr = FontAwesomeIcon.Eye.ToIconString();
    Vector2 iconSize;
    using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
      iconSize = ImGui.CalcTextSize(iconStr);
    var titleSize = ImGui.CalcTextSize("HrothgarScent");

    var iconPos = new Vector2(origin.X + 2f * scale, barTop + (barHeight - iconSize.Y) * 0.5f);
    using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
      drawList.AddText(iconPos, ImGui.GetColorU32(UiTheme.AccentPurple), iconStr);

    var titlePos = new Vector2(iconPos.X + iconSize.X + 8f * scale, barTop + (barHeight - titleSize.Y) * 0.5f);
    drawList.AddText(titlePos, ImGui.GetColorU32(UiTheme.AccentBlue), "HrothgarScent");

    // Right-side buttons: link, then close (right-aligned, centered vertically in the bar).
    var contentRight = origin.X + contentWidth;
    var btnY = barTop + (barHeight - btnSize) * 0.5f;
    var closePos = new Vector2(contentRight - btnSize, btnY);
    var linkPos = new Vector2(closePos.X - btnSize - spacing, btnY);

    if (TitleIconButton("##titleLink", FontAwesomeIcon.Link, linkPos, btnSize,
          new Vector4(UiTheme.AccentPurple.X, UiTheme.AccentPurple.Y, UiTheme.AccentPurple.Z, 0.45f)))
      Util.OpenLink(RepoUrl);
    UiTheme.Tooltip("Open the repository");

    if (TitleIconButton("##titleClose", FontAwesomeIcon.Times, closePos, btnSize,
          new Vector4(UiTheme.Bad.X, UiTheme.Bad.Y, UiTheme.Bad.Z, 0.6f)))
      IsOpen = false;
    UiTheme.Tooltip("Close");

    // Content starts below the band.
    ImGui.SetCursorScreenPos(new Vector2(origin.X, bandMax.Y + 6f * scale));
  }

  // Draw-list based icon button so the glyph is centered by its real size and never clipped by
  // frame padding (which is what cut off the icon when using ImGui.Button at a small size).
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
    var iconColor = hovered ? new Vector4(1f, 1f, 1f, 1f) : UiTheme.Muted;
    using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
      drawList.AddText(glyphPos, ImGui.GetColorU32(iconColor), iconStr);

    return clicked;
  }

  /// <summary>Config-bound checkbox that saves on change and shows a hover tooltip.</summary>
  private static void ConfigCheckbox(string label, Func<bool> get, Action<bool> set, string tooltip)
  {
    bool value = get();
    if (ImGui.Checkbox(label, ref value))
    {
      set(value);
      Plugin.Configuration.Save();
    }
    UiTheme.Tooltip(tooltip);
  }

  /// <summary>Config-bound colour picker that saves on change.</summary>
  private static void ConfigColor(string label, Func<Vector4> get, Action<Vector4> set, string tooltip)
  {
    var value = get();
    ImGui.SetNextItemWidth(200f * ImGuiHelpers.GlobalScale);
    if (ImGui.ColorEdit4(label, ref value, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaBar))
    {
      set(value);
      Plugin.Configuration.Save();
    }
    UiTheme.Tooltip(tooltip);
  }

  // ---- General ----

  private static void DrawGeneralTab()
  {
    DrawWindowSection();
    DrawScanningSection();
    DrawLodestoneSection();
  }

  private static void DrawWindowSection()
  {
    UiTheme.SectionHeader("Window", FontAwesomeIcon.WindowMaximize);

    ConfigCheckbox("Open on login",
      () => Plugin.Configuration.OpenOnLogin,
      v => Plugin.Configuration.OpenOnLogin = v,
      "Open the Scent window automatically when you log in.");

    ConfigCheckbox("Hide in combat",
      () => Plugin.Configuration.HideInCombat,
      v => Plugin.Configuration.HideInCombat = v,
      "Hide the Scent window while you are in combat.");

    ImGui.SameLine(0, 30f * ImGuiHelpers.GlobalScale);

    ConfigCheckbox("Hide in duty",
      () => Plugin.Configuration.HideInDuty,
      v => Plugin.Configuration.HideInDuty = v,
      "Hide the Scent window while you are bound by duty.");

    ConfigCheckbox("Hide in cutscenes",
      () => Plugin.Configuration.HideInCutscene,
      v => Plugin.Configuration.HideInCutscene = v,
      "Hide the Scent window during cutscenes.");

    ConfigCheckbox("Show search bar",
      () => Plugin.Configuration.ShowSearchBar,
      v => Plugin.Configuration.ShowSearchBar = v,
      "Show the search box on the Scent window's toolbar. Hiding it also stops whatever is typed in it from " +
      "filtering, so the list can never be filtered by a box you cannot see.");

    ConfigCheckbox("Show job icons",
      () => Plugin.Configuration.ShowJobIcons,
      v => Plugin.Configuration.ShowJobIcons = v,
      "The game's own job icon beside each job name in the list.");

    // Off by default and asked for explicitly: this is the game's own world UI, in front of other people.
    // Toggling it only writes config — NameplateService.Sync notices on the next framework tick and attaches or
    // detaches itself. Calling the game from a checkbox in Draw is the thing this plugin does not do.
    var nameplates = Plugin.Configuration.NameplateMode != NameplateMode.Off;
    if (ImGui.Checkbox("Show the eye over their head", ref nameplates))
    {
      Plugin.Configuration.NameplateMode = nameplates ? NameplateMode.Watchers : NameplateMode.Off;
      Plugin.Configuration.Save();
    }
    UiTheme.Tooltip("Colours the nameplate of anyone targeting you, so you can see it without the window open. " +
                    "Nothing is added or hidden — just the colour, and only in the open world. Never in PvP.");

    ConfigCheckbox("Add 'Hrothgar remember' to the game's right-click menu",
      () => Plugin.Configuration.ShowContextMenuMark,
      v => Plugin.Configuration.ShowContextMenuMark = v,
      "Adds an entry wherever the game shows a player's name — friend list, Party Finder, chat log, FC roster. " +
      "It is the only way to mark someone the Scent window cannot see. Nothing is ever written down until you " +
      "click it.");

    ConfigCheckbox("Show watcher history",
      () => Plugin.Configuration.ShowWatcherHistory,
      v => Plugin.Configuration.ShowWatcherHistory = v,
      "Show the 'Hrothgar remember' section under the player list. The history is still recorded either way.");

    ConfigCheckbox("Use job abbreviations",
      () => Plugin.Configuration.UseJobAbbreviations,
      v => Plugin.Configuration.UseJobAbbreviations = v,
      "'WAR' rather than 'Warrior'.");

    ConfigCheckbox("Show server info bar entry",
      () => Plugin.Configuration.ShowDtr,
      v => Plugin.Configuration.ShowDtr = v,
      "Show the nearby and watching counts in the server info bar. Left-click it to open Scent, right-click " +
      "for these settings.");

    ConfigCheckbox("Focus target on hover",
      () => Plugin.Configuration.FocusTargetOnHover,
      v => Plugin.Configuration.FocusTargetOnHover = v,
      "Focus-target whoever's name your cursor rests on, so you can pick them out in the world.\n\n" +
      "It does NOT put your old focus target back afterwards — it simply clears it. Reading the previous one " +
      "needs game data the window is not allowed to touch while drawing, so clearing is the honest option.");

    // After the checkboxes, not among them: a slider mid-run would break the SameLine pairing above. Label +
    // SameLine + 200px is the shape DrawWhoToShowSection's two sliders already use.
    ImGui.Dummy(new Vector2(0, 4f * ImGuiHelpers.GlobalScale));

    ImGui.AlignTextToFramePadding();
    ImGui.Text("Rows before scrolling:");
    UiTheme.HelpMarker(
      "How tall the Scent window may grow. It shrinks and grows with the list up to this many rows, then the " +
      "list scrolls instead.\n\n" +
      "The height is not yours to drag — the window follows the list. Drag the corner for width.");
    ImGui.SameLine();
    int maxVisibleRows = Plugin.Configuration.MaxVisibleRows;
    ImGui.SetNextItemWidth(200f * ImGuiHelpers.GlobalScale);
    if (UiTheme.SliderIntManual("##maxVisibleRows", ref maxVisibleRows,
          Configuration.VisibleRowsMin, Configuration.VisibleRowsMax))
    {
      Plugin.Configuration.MaxVisibleRows = maxVisibleRows;
      Plugin.Configuration.Save();
    }
    UiTheme.Tooltip("Rows the list shows before it starts scrolling.\r\n\r\n" +
            "Double-click to type an exact value.");
  }

  private static void DrawScanningSection()
  {
    UiTheme.SectionHeader("Scanning", FontAwesomeIcon.Clock);

    int rescan = Plugin.Configuration.RescanIntervalMs;
    ImGui.Text("Rescan interval (ms)");
    ImGui.SetNextItemWidth(-1);
    if (UiTheme.SliderIntManual("###rescanInterval", ref rescan, 50, 2000))
    {
      Plugin.Configuration.RescanIntervalMs = rescan;
      Plugin.Configuration.Save();
    }
    UiTheme.Tooltip("How often Hrothgar sniff. Lower = snappier watcher detection, more CPU.\r\n" +
            "250ms is imperceptible in use. Below ~100ms buys nothing.\r\n\r\n" +
            "Double-click to type an exact value.");
  }

  private static void DrawLodestoneSection()
  {
    UiTheme.SectionHeader("Lodestone", FontAwesomeIcon.Globe);

    ImGui.AlignTextToFramePadding();
    ImGui.Text("Region:");
    ImGui.SameLine();
    int index = Array.IndexOf(_lodestoneRegionStrings, Plugin.Configuration.LodestoneRegion.ToString());
    ImGui.SetNextItemWidth(200f * ImGuiHelpers.GlobalScale);
    if (ImGui.Combo("##lodestoneRegionCombo", ref index, _lodestoneRegionStrings, _lodestoneRegionStrings.Length))
    {
      Plugin.Configuration.LodestoneRegion = Enum.Parse<LodestoneRegion>(_lodestoneRegionStrings[index]);
      Plugin.Configuration.Save();
    }
    UiTheme.Tooltip("Which Lodestone the 'Search on Lodestone' row action opens. They all hold the same " +
            "characters; this only picks the language.");
  }

  // ---- Filters ----

  private void DrawFiltersTab()
  {
    DrawHalvesSection();
    DrawWhoToShowSection();
    DrawRacesSection();
    DrawMarksSection();
  }

  private static void DrawHalvesSection()
  {
    UiTheme.SectionHeader("Halves", FontAwesomeIcon.ToggleOn);

    UiTheme.TextWrappedColored(UiTheme.Muted,
      "Hrothgar is two noses in one window: who is nearby, and who is looking at you. Turn either off to hide " +
      "it. Hrothgar keeps sniffing either way — counts and history survive, so switching a half back on brings " +
      "everything it learned while it was off.");
    ImGui.Dummy(new Vector2(0, 4f * ImGuiHelpers.GlobalScale));

    ConfigCheckbox("Nearby list",
      () => Plugin.Configuration.EnableNearbyList,
      v => Plugin.Configuration.EnableNearbyList = v,
      "The player table, and the nearby count in the server info bar. Focus-list arrival alerts belong to this " +
      "half and go quiet with it — arriving in range is a fact about the nearby list.");

    ImGui.SameLine(0, 30f * ImGuiHelpers.GlobalScale);

    ConfigCheckbox("Watchers",
      () => Plugin.Configuration.EnableWatchers,
      v => Plugin.Configuration.EnableWatchers = v,
      "The eye column, the 'Hrothgar remember' history, the watching count in the server info bar, and watcher " +
      "alerts. Hrothgar keeps recording the history while it is off.");
  }

  private static void DrawRacesSection()
  {
    UiTheme.SectionHeader("Races", FontAwesomeIcon.Users);

    UiTheme.TextWrappedColored(UiTheme.Muted, "Untick a race to leave it out of the list.");
    UiTheme.HelpMarker(
      "Players who have not finished loading in have no race for Hrothgar to smell yet, and are always shown — " +
      "otherwise they would blink out of the list as they arrive, which looks like people going missing.\n\n" +
      "This is the only home for the race filter. The Scent window's footer says when filters are hiding people, " +
      "so a filter set here can never go unexplained over there.");
    ImGui.Dummy(new Vector2(0, 4f * ImGuiHelpers.GlobalScale));

    var column = 0;
    foreach (var race in RacePalette.Races)
    {
      if (column > 0 && column % RacesPerRow != 0)
        ImGui.SameLine();
      column++;

      var shown = !Plugin.Configuration.IsRaceHidden(race.RaceId);
      if (ImGui.Checkbox($"{race.Name}##race{race.RaceId}", ref shown))
      {
        Plugin.Configuration.SetRaceHidden(race.RaceId, !shown);
        Plugin.Configuration.Save();
      }
    }

    ImGui.Dummy(new Vector2(0, 4f * ImGuiHelpers.GlobalScale));

    if (ImGui.Button("Show all races"))
    {
      Plugin.Configuration.HiddenRaceMask = 0;
      Plugin.Configuration.Save();
    }
    UiTheme.Tooltip("Puts every tick back.");
  }

  private static void DrawWhoToShowSection()
  {
    UiTheme.SectionHeader("Who to show", FontAwesomeIcon.Filter);

    UiTheme.TextWrappedColored(UiTheme.Muted,
      "Filters only change what the list shows. Hrothgar still smell everyone, so the info bar counts and the " +
      "watcher history stay honest.");
    ImGui.Dummy(new Vector2(0, 4f * ImGuiHelpers.GlobalScale));

    ConfigCheckbox("Hide self",
      () => Plugin.Configuration.HideSelf,
      v => Plugin.Configuration.HideSelf = v,
      "Leave your own character out of the list.");

    ImGui.SameLine(0, 30f * ImGuiHelpers.GlobalScale);

    ConfigCheckbox("Hide party members",
      () => Plugin.Configuration.HideParty,
      v => Plugin.Configuration.HideParty = v,
      "Leave your party out of the list. They are already on your party list.");

    ConfigCheckbox("Hide friends",
      () => Plugin.Configuration.HideFriends,
      v => Plugin.Configuration.HideFriends = v,
      "Leave your friends out of the list.");

    ImGui.SameLine(0, 30f * ImGuiHelpers.GlobalScale);

    ConfigCheckbox("Hide dead",
      () => Plugin.Configuration.HideDead,
      v => Plugin.Configuration.HideDead = v,
      "Leave dead players out of the list.");

    ConfigCheckbox("Hide AFK",
      () => Plugin.Configuration.HideAfk,
      v => Plugin.Configuration.HideAfk = v,
      "Leave players flagged as away out of the list.\n\n" +
      "Off by default on purpose: the online-status id this matches on has not been confirmed against the " +
      "live game, so switch it on and check it actually hides the right people before you rely on it.");

    ImGui.SameLine(0, 30f * ImGuiHelpers.GlobalScale);

    ConfigCheckbox("Hide low level",
      () => Plugin.Configuration.HideLowLevel,
      v => Plugin.Configuration.HideLowLevel = v,
      "Leave out level 3 and below — the throwaway alts and bots milling around city aetherytes.");

    ImGui.Dummy(new Vector2(0, 4f * ImGuiHelpers.GlobalScale));

    ImGui.AlignTextToFramePadding();
    ImGui.Text("Max distance:");
    ImGui.SameLine();
    float maxDistance = Plugin.Configuration.MaxDistanceYalms;
    ImGui.SetNextItemWidth(200f * ImGuiHelpers.GlobalScale);
    if (ImGui.SliderFloat("##maxDistance", ref maxDistance, 0f, 200f, "%.0f"))
    {
      Plugin.Configuration.MaxDistanceYalms = maxDistance;
      Plugin.Configuration.Save();
    }
    ImGui.SameLine();
    ImGui.Text("yalms");
    UiTheme.Tooltip("Hide anyone further away than this. 0 = no limit.");

    ImGui.AlignTextToFramePadding();
    ImGui.Text("Max players shown:");
    ImGui.SameLine();
    int maxPlayers = Plugin.Configuration.MaxPlayersShown;
    ImGui.SetNextItemWidth(200f * ImGuiHelpers.GlobalScale);
    if (UiTheme.SliderIntManual("##maxPlayersShown", ref maxPlayers, 5, 200))
    {
      Plugin.Configuration.MaxPlayersShown = maxPlayers;
      Plugin.Configuration.Save();
    }
    UiTheme.Tooltip("How many rows at most. The NEAREST are kept, whatever you sorted by — otherwise sorting " +
            "by name would quietly drop a watcher standing next to you.\r\n\r\n" +
            "Double-click to type an exact value.");
  }

  /// <summary>
  /// The marks table: one row per player the user pointed at, replacing the two near-identical Focus and Ignore
  /// tables that stood here.
  ///
  /// One table, because there was only ever one kind of thing: both old lists were Name+HomeWorld records with
  /// a single boolean of meaning, and their code was duplicated line for line. Focus and Ignore are two ticks on
  /// one row now, which also makes the contradiction — both at once — visible in one place instead of being a
  /// rule stated in a comment on two lists that could not see each other.
  /// </summary>
  private void DrawMarksSection()
  {
    UiTheme.SectionHeader("Marks", FontAwesomeIcon.Star);

    // Says so out loud rather than failing silently: a note typed into a read-only store vanishes at restart,
    // and nothing else on screen would explain it. The store supplies the reason rather than this guessing one —
    // a file from a newer build and a file that could not be read both land here, and the user's next move is
    // completely different between them.
    if (Plugin.Marks.ReadOnlyReason is { } reason)
    {
      UiTheme.TextWrappedColored(UiTheme.Warn, reason);
      ImGui.Dummy(new Vector2(0, 4f * ImGuiHelpers.GlobalScale));
    }

    // ABOVE the empty-marks return, deliberately. This is the plugin's one switch over data the user did not
    // type, and it must be findable and settable BEFORE they have marked anybody — a privacy control you can
    // only reach once the thing it governs has already happened is not a control.
    ConfigCheckbox("Note when and where you last saw them",
      () => Plugin.Configuration.RememberLastSeen,
      v => Plugin.Configuration.RememberLastSeen = v,
      "Only for players you have marked, and only one line per person — overwritten, never a history. This is " +
      "the one thing Hrothgar writes down that you did not type, so it has its own switch. Turning it off " +
      "keeps what is already stored; use Forget to delete a person outright.");

    ConfigCheckbox("Note duties you clear together",
      () => Plugin.Configuration.RememberDutyClears,
      v => Plugin.Configuration.RememberDutyClears = v,
      "When you clear a duty, Hrothgar adds a line to the note of anyone in it you had already marked. Only " +
      "them — clearing a duty with a stranger is not a reason to remember them. It goes in the note, so you " +
      "can edit or delete it like anything else you wrote.");

    ImGui.AlignTextToFramePadding();
    ImGui.Text("Dim a mark unseen for:");
    ImGui.SameLine();
    var staleDays = Plugin.Configuration.MarkStaleDays;
    ImGui.SetNextItemWidth(160f * ImGuiHelpers.GlobalScale);
    if (UiTheme.SliderIntManual("##markStaleDays", ref staleDays, 0, 180))
    {
      Plugin.Configuration.MarkStaleDays = staleDays;
      Plugin.Configuration.Save();
    }
    UiTheme.Tooltip("A mark Hrothgar has not matched in this long is dimmed, because it may have been orphaned " +
                    "by a rename or a world transfer. Nothing is ever deleted by time. 0 never dims." +
                    "\r\n\r\nDouble-click to type an exact value.");
    ImGui.SameLine();
    ImGui.Text("days");

    ImGui.Dummy(new Vector2(0, 4f * ImGuiHelpers.GlobalScale));

    var marks = Plugin.Marks.All();
    if (marks.Count == 0)
    {
      UiTheme.TextWrappedColored(UiTheme.Muted,
        "Right-click a player in the Scent window -> Remember this player. Or right-click their name anywhere " +
        "the game shows it — friend list, Party Finder, chat — and pick Hrothgar remember.");
      return;
    }

    UiTheme.TextWrappedColored(UiTheme.Muted,
      "Everyone Hrothgar wrote down, because you said so. Matched on name and home world, so they stay marked " +
      "across zones and sessions. Ignore beats Focus if a player carries both. Hover a name for when you last " +
      "ran into them.");
    ImGui.Dummy(new Vector2(0, 4f * ImGuiHelpers.GlobalScale));

    WatcherKey? toRemove = null;
    var scale = ImGuiHelpers.GlobalScale;
    var tableFlags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.NoSavedSettings | ImGuiTableFlags.SizingStretchProp;
    if (ImGui.BeginTable("##marksTable", 5, tableFlags))
    {
      ImGui.TableSetupColumn("Player", ImGuiTableColumnFlags.WidthStretch);
      ImGui.TableSetupColumn("Focus", ImGuiTableColumnFlags.WidthFixed, 44f * scale);
      ImGui.TableSetupColumn("Ignore", ImGuiTableColumnFlags.WidthFixed, 48f * scale);
      ImGui.TableSetupColumn("Note", ImGuiTableColumnFlags.WidthStretch);
      ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 70f * scale);
      ImGui.TableHeadersRow();

      foreach (var mark in marks)
      {
        ImGui.TableNextRow();

        // Scope by name AND world: the same character name on two worlds is two different people, and an
        // ImGui ID shared between two rows makes their ticks the same tick.
        ImGui.PushID($"{mark.Name}#{mark.HomeWorldId}");

        // Alpha only, never BeginDisabled: a stale row is the one row whose controls the user most needs —
        // the pencil that fixes it is right there. Dimming says "this has gone quiet"; disabling would say
        // "and you may not do anything about it".
        var stale = MarkStore.IsStale(mark);
        if (stale)
          UiTheme.PushDimmed();

        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(mark.Color ?? Plugin.Configuration.ColorDefault, mark.FullName);

        // On the name, not in a column of its own: the table is already five wide in a 540px window, and this
        // is the answer to a question you only ask about one person at a time.
        if (mark.LastSeen is { } lastSeen)
          UiTheme.Tooltip(stale
            ? $"{ScentWindow.FormatLastSeen(lastSeen, mark.LastSeenZone)}\r\n\r\n" + StaleHelp
            : ScentWindow.FormatLastSeen(lastSeen, mark.LastSeenZone));
        else if (stale)
          UiTheme.Tooltip($"Hrothgar never smell this one since you marked them.\r\n\r\n{StaleHelp}");

        ImGui.TableNextColumn();
        var focus = mark.IsFocused;
        if (ImGui.Checkbox("##focus", ref focus))
          Plugin.Marks.Update(mark.Key, mark.HomeWorldName, m => m with
          {
            Marks = focus ? m.Marks | MarkKind.Focus : m.Marks & ~MarkKind.Focus,
          });

        ImGui.TableNextColumn();
        var ignore = mark.IsIgnored;
        if (ImGui.Checkbox("##ignore", ref ignore))
          Plugin.Marks.Update(mark.Key, mark.HomeWorldName, m => m with
          {
            Marks = ignore ? m.Marks | MarkKind.Ignore : m.Marks & ~MarkKind.Ignore,
          });

        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();

        // One line, never the whole note: this is a roster, not a reader. The cell clips it, the full text is
        // one hover away, and the editor is where it can actually be changed.
        ImGui.TextUnformatted(FirstLine(mark.Note));
        if (mark.HasNote)
          UiTheme.Tooltip(mark.Note);

        ImGui.TableNextColumn();

        // Popped BEFORE the buttons: the dim is a statement about the record, not about the controls that fix
        // it, and a greyed-out Forget on a row you want gone reads as broken.
        if (stale)
          UiTheme.PopDimmed();

        // A word, not a glyph. FontAwesomeIcon.Pencil does not exist — Dalamud ships FontAwesome 5 naming, where
        // it is PencilAlt — and an icon button here would need its own font push and width measurement to sit
        // level with the text button beside it. "Renamed?" also asks the question the row is posing.
        if (ImGui.SmallButton("Renamed?"))
        {
          _repairKey = mark.Key;
          _repairName = mark.Name;
          _repairWorld = mark.HomeWorldName;
          ImGui.OpenPopup(RepairPopupId);
        }
        UiTheme.Tooltip("They renamed, or moved world. Point this mark at whoever they are now.");

        DrawRepairPopup(mark);

        ImGui.SameLine();
        if (ImGui.SmallButton("Forget"))
          toRemove = mark.Key;

        ImGui.PopID();
      }

      ImGui.EndTable();
    }

    // Deferred out of the loop: All() hands back a snapshot, but removing mid-draw would leave the table's own
    // row count disagreeing with what it already told BeginTable this frame.
    if (toRemove is { } key)
      Plugin.Marks.Remove(key);
  }

  /// <summary>
  /// The repair box: says who this mark is about now.
  ///
  /// Inside the row's PushID, so each row's pencil opens its own — unlike the Scent window's note editor, which
  /// is hoisted out of its table because its anchor row can vanish from a live snapshot mid-edit. These rows
  /// come from the store, and the store does not change under the user's own hands.
  ///
  /// Both halves of the key, and that is the point rather than a courtesy: a mark is (name, world), so a box
  /// that could only fix a name would leave a world transfer orphaned forever — while cheerfully claiming to
  /// repair renames.
  /// </summary>
  private void DrawRepairPopup(MarkedPlayer mark)
  {
    if (!ImGui.BeginPopup(RepairPopupId))
      return;

    var scale = ImGuiHelpers.GlobalScale;

    ImGui.TextColored(UiTheme.AccentBlue, $"Who is {mark.FullName} now?");
    ImGui.Separator();
    UiTheme.TextWrappedColored(UiTheme.Muted,
      "Hrothgar match on name and home world, so a rename or a transfer loses the trail. Tell Hrothgar the new " +
      "one and everything you wrote comes with it.");
    ImGui.Dummy(new Vector2(0, 4f * scale));

    ImGui.SetNextItemWidth(220f * scale);
    ImGui.InputTextWithHint("##repairName", "Name", ref _repairName, MarkNameMaxLength);

    ImGui.SetNextItemWidth(220f * scale);
    ImGui.InputTextWithHint("##repairWorld", "Home world", ref _repairWorld, MarkWorldMaxLength);

    // Resolved rather than trusted: the key stores a world ID, and a name that resolves to nothing would make a
    // mark that can never match anybody — the very failure this box exists to undo.
    var worldId = WorldPalette.IdOf(_repairWorld);
    var named = !string.IsNullOrWhiteSpace(_repairName);

    if (!named)
      UiTheme.TextWrappedColored(UiTheme.Warn, "Hrothgar need a name.");
    else if (worldId is null)
      UiTheme.TextWrappedColored(UiTheme.Warn, _repairWorld.Trim().Length == 0
        ? "Hrothgar need a home world."
        : $"Hrothgar not know a world called '{_repairWorld.Trim()}'.");

    ImGui.Dummy(new Vector2(0, 4f * scale));

    using (ImRaii.Disabled(!named || worldId is null))
    {
      if (ImGui.SmallButton("This is them") && worldId is { } id)
      {
        Plugin.Marks.Rename(_repairKey, _repairName.Trim(), id, _repairWorld.Trim());
        ImGui.CloseCurrentPopup();
      }
    }

    ImGui.SameLine();
    if (ImGui.SmallButton("Cancel"))
      ImGui.CloseCurrentPopup();

    ImGui.EndPopup();
  }

  /// <summary>The first line of a note, for a one-line cell. A note is multi-line by design and a raw newline in
  /// a table cell renders as a box.</summary>
  private static string FirstLine(string note)
  {
    if (string.IsNullOrEmpty(note))
      return string.Empty;

    var end = note.IndexOfAny(['\r', '\n']);
    return end < 0 ? note : note[..end];
  }

  // ---- Colours ----

  private static void DrawColoursTab()
  {
    DrawNameColoursSection();
    DrawJobColoursSection();
  }

  private static void DrawNameColoursSection()
  {
    UiTheme.SectionHeader("Name colours", FontAwesomeIcon.Palette);

    ConfigColor("Default##colorDefault",
      () => Plugin.Configuration.ColorDefault,
      v => Plugin.Configuration.ColorDefault = v,
      "Everyone with no other relationship to you.");

    ConfigColor("Friend##colorFriend",
      () => Plugin.Configuration.ColorFriend,
      v => Plugin.Configuration.ColorFriend = v,
      "Players on your friend list.");

    ConfigColor("Party##colorParty",
      () => Plugin.Configuration.ColorParty,
      v => Plugin.Configuration.ColorParty = v,
      "Players in your party. Takes precedence over friend and free company.");

    ConfigColor("Same free company##colorSameFc",
      () => Plugin.Configuration.ColorSameFc,
      v => Plugin.Configuration.ColorSameFc = v,
      "Players in your free company. Matched on tag AND home world, so a same-named FC from another world " +
      "is not painted as yours.");

    ConfigColor("Watcher##colorWatcher",
      () => Plugin.Configuration.ColorWatcher,
      v => Plugin.Configuration.ColorWatcher = v,
      "The eye, and the row tint below. Names stay coloured by relationship — the eye column already says " +
      "who is watching, so the two never fight over one row.");

    ConfigColor("Focused##colorFocused",
      () => Plugin.Configuration.ColorFocused,
      v => Plugin.Configuration.ColorFocused = v,
      "Players on your focus list. Takes precedence over party, friend and free company — it is the one colour " +
      "you picked per player, by hand.");

    ImGui.Dummy(new Vector2(0, 4f * ImGuiHelpers.GlobalScale));

    ConfigCheckbox("Highlight watcher rows",
      () => Plugin.Configuration.HighlightWatcherRow,
      v => Plugin.Configuration.HighlightWatcherRow = v,
      "Tint the whole row of anyone targeting you, not just their eye.");

    var highlight = Plugin.Configuration.HighlightWatcherRow;
    if (!highlight)
      ImGui.BeginDisabled();

    ImGui.AlignTextToFramePadding();
    ImGui.Text("Tint strength:");
    ImGui.SameLine();
    float tintAlpha = Plugin.Configuration.WatcherRowTintAlpha;
    ImGui.SetNextItemWidth(200f * ImGuiHelpers.GlobalScale);
    if (ImGui.SliderFloat("##watcherRowTintAlpha", ref tintAlpha, 0.05f, 0.6f, "%.2f"))
    {
      Plugin.Configuration.WatcherRowTintAlpha = tintAlpha;
      Plugin.Configuration.Save();
    }
    UiTheme.Tooltip("How strongly the watcher colour washes over the row.");

    if (!highlight)
      ImGui.EndDisabled();

    ConfigCheckbox("Highlight focused rows",
      () => Plugin.Configuration.HighlightFocusedRow,
      v => Plugin.Configuration.HighlightFocusedRow = v,
      "Tint the whole row of a focus-list player. A watcher who is also focused gets the watcher tint — two " +
      "washes on one row average into a colour that is neither.");

    var highlightFocused = Plugin.Configuration.HighlightFocusedRow;
    if (!highlightFocused)
      ImGui.BeginDisabled();

    ImGui.AlignTextToFramePadding();
    ImGui.Text("Tint strength:");
    ImGui.SameLine();
    float focusedTintAlpha = Plugin.Configuration.FocusedRowTintAlpha;
    ImGui.SetNextItemWidth(200f * ImGuiHelpers.GlobalScale);
    if (ImGui.SliderFloat("##focusedRowTintAlpha", ref focusedTintAlpha, 0.05f, 0.6f, "%.2f"))
    {
      Plugin.Configuration.FocusedRowTintAlpha = focusedTintAlpha;
      Plugin.Configuration.Save();
    }
    UiTheme.Tooltip("How strongly the focus colour washes over the row.");

    if (!highlightFocused)
      ImGui.EndDisabled();
  }

  private static void DrawJobColoursSection()
  {
    UiTheme.SectionHeader("Job colours", FontAwesomeIcon.UserTag);

    var perJob = Plugin.Configuration.JobColorMode == JobColorMode.Job;
    if (ImGui.Checkbox("Use per-job colours", ref perJob))
    {
      Plugin.Configuration.JobColorMode = perJob ? JobColorMode.Job : JobColorMode.Role;
      Plugin.Configuration.Save();
    }
    UiTheme.Tooltip("Off: four role buckets. On: a colour per job, falling back to the role colour for any " +
            "job you have not picked one for.");

    ImGui.Dummy(new Vector2(0, 4f * ImGuiHelpers.GlobalScale));

    if (!perJob)
    {
      ConfigColor("Tank##roleColorTank",
        () => Plugin.Configuration.RoleColorTank,
        v => Plugin.Configuration.RoleColorTank = v,
        "PLD, WAR, DRK, GNB.");

      ConfigColor("Healer##roleColorHealer",
        () => Plugin.Configuration.RoleColorHealer,
        v => Plugin.Configuration.RoleColorHealer = v,
        "WHM, SCH, AST, SGE.");

      ConfigColor("Melee DPS##roleColorMelee",
        () => Plugin.Configuration.RoleColorMelee,
        v => Plugin.Configuration.RoleColorMelee = v,
        "MNK, DRG, NIN, SAM, RPR, VPR.");

      ConfigColor("Ranged DPS##roleColorRanged",
        () => Plugin.Configuration.RoleColorRanged,
        v => Plugin.Configuration.RoleColorRanged = v,
        "BRD, MCH, DNC, BLM, SMN, RDM, PCT.");

      ConfigColor("Other##roleColorOther",
        () => Plugin.Configuration.RoleColorOther,
        v => Plugin.Configuration.RoleColorOther = v,
        "Base classes, limited jobs, and anything Hrothgar does not recognise.");
    }
    else
    {
      UiTheme.TextWrappedColored(UiTheme.Muted,
        "Untouched jobs follow their role colour, so switching this on changes nothing until you pick one.");
      ImGui.Dummy(new Vector2(0, 4f * ImGuiHelpers.GlobalScale));

      foreach (var role in _roleOrder)
        DrawJobColorRow(role);
    }

    ImGui.Dummy(new Vector2(0, 6f * ImGuiHelpers.GlobalScale));
    if (ImGui.Button("Reset colours to default"))
      ResetColours();
    UiTheme.Tooltip("Puts every colour on this tab back, and forgets every per-job override.");
  }

  private static void DrawJobColorRow(JobRole role)
  {
    ImGui.TextColored(UiTheme.Muted, RoleLabel(role));

    var column = 0;
    foreach (var job in JobPalette.Jobs)
    {
      if (job.Role != role)
        continue;

      if (column > 0 && column % JobColorsPerRow != 0)
        ImGui.SameLine();
      column++;

      var color = Plugin.Configuration.JobColors.TryGetValue(job.JobId, out var custom)
        ? custom
        : JobPalette.RoleColor(job.Role);

      if (ImGui.ColorEdit4($"{job.Abbreviation}##job{job.JobId}", ref color,
            ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaBar))
      {
        Plugin.Configuration.JobColors[job.JobId] = color;
        Plugin.Configuration.Save();
      }
    }

    ImGui.Dummy(new Vector2(0, 4f * ImGuiHelpers.GlobalScale));
  }

  private static string RoleLabel(JobRole role) => role switch
  {
    JobRole.Tank => "Tanks",
    JobRole.Healer => "Healers",
    JobRole.MeleeDps => "Melee DPS",
    JobRole.RangedDps => "Ranged DPS",
    _ => "Other",
  };

  /// <summary>
  /// Reads the defaults off a throwaway Configuration rather than repeating the literals here, so the reset
  /// can never drift away from what a first-run install actually gets. Nothing in the constructor touches
  /// plugin state, so building one costs an allocation and nothing else.
  /// </summary>
  private static void ResetColours()
  {
    var defaults = new Configuration();
    var config = Plugin.Configuration;

    config.ColorDefault = defaults.ColorDefault;
    config.ColorFriend = defaults.ColorFriend;
    config.ColorParty = defaults.ColorParty;
    config.ColorSameFc = defaults.ColorSameFc;
    config.ColorWatcher = defaults.ColorWatcher;
    config.HighlightWatcherRow = defaults.HighlightWatcherRow;
    config.WatcherRowTintAlpha = defaults.WatcherRowTintAlpha;
    config.ColorFocused = defaults.ColorFocused;
    config.HighlightFocusedRow = defaults.HighlightFocusedRow;
    config.FocusedRowTintAlpha = defaults.FocusedRowTintAlpha;
    config.RoleColorTank = defaults.RoleColorTank;
    config.RoleColorHealer = defaults.RoleColorHealer;
    config.RoleColorMelee = defaults.RoleColorMelee;
    config.RoleColorRanged = defaults.RoleColorRanged;
    config.RoleColorOther = defaults.RoleColorOther;
    config.JobColors.Clear();
    config.Save();
  }

  /// <summary>
  /// The escalation ladder's two thresholds.
  ///
  /// Placed after the cooldown slider, deliberately: the two interact, and the interaction is the one thing a
  /// user cannot deduce from either control alone. An escalation that lands inside the cooldown a fresh watcher
  /// just spent is dropped, so a threshold under the cooldown is usually swallowed. The warning below says so
  /// at the moment it becomes true, rather than in a tooltip nobody opens.
  /// </summary>
  private static void DrawStareSection()
  {
    var config = Plugin.Configuration;

    ConfigCheckbox("Say again if they keep watching",
      () => config.AlertOnStareEscalation,
      v => config.AlertOnStareEscalation = v,
      "One line when someone starts watching you is a glance. This adds a line when they are still watching " +
      "later — the difference between someone cycling targets and someone fixed on you.");

    using (ImRaii.Disabled(!config.AlertOnStareEscalation))
    {
      ImGui.AlignTextToFramePadding();
      ImGui.Text("Say 'watching you':");
      ImGui.SameLine();
      var stare = config.StareSeconds;
      ImGui.SetNextItemWidth(160f * ImGuiHelpers.GlobalScale);
      if (UiTheme.SliderIntManual("##stareSeconds", ref stare, 0, 120))
      {
        config.StareSeconds = stare;
        config.Save();
      }

      // Attached to the SLIDER, before the unit label takes the last-item slot. Tooltip helpers test ImGui's
      // "was the last item hovered", so placing this after the SameLine/Text pair below would hang the only
      // explanation of the 0 value — and of the double-click entry — off the two narrow words nobody hovers.
      // TooltipEvenIfDisabled because the slider inherits the disabled flag from the scope above, which the
      // plain helper answers "not hovered" to, forever.
      UiTheme.TooltipEvenIfDisabled("How long they have to hold you before Hrothgar mentions it again. " +
                                    "0 turns this rung off.\r\n\r\nDouble-click to type an exact value.");
      ImGui.SameLine();
      ImGui.Text("seconds in");

      ImGui.AlignTextToFramePadding();
      ImGui.Text("Say 'still watching':");
      ImGui.SameLine();
      var fixate = config.FixateSeconds;
      ImGui.SetNextItemWidth(160f * ImGuiHelpers.GlobalScale);
      if (UiTheme.SliderIntManual("##fixateSeconds", ref fixate, 0, 300))
      {
        config.FixateSeconds = fixate;
        config.Save();
      }

      UiTheme.TooltipEvenIfDisabled("The last thing Hrothgar says about one stare. 0 turns this rung off. " +
                                    "Set below the one above and it simply wins, at the lower number." +
                                    "\r\n\r\nDouble-click to type an exact value.");
      ImGui.SameLine();
      ImGui.Text("seconds in");
    }

    // The trap, named where it happens. Both rungs share the one cooldown with the alert that fires the moment
    // someone starts watching, so a threshold UNDER it is usually eaten by the alert that came first — the
    // feature then looks broken rather than switched off.
    //
    // BOTH rungs are checked, not just the first. They are swallowed on identical terms, and warning about only
    // one leaves the "turn the stare rung off and keep the fixation rung" configuration silently dead.
    //
    // Strictly less-than, matching AlertService's own `now - last < cooldownMs`: at exactly equal the alert
    // DOES fire, so <= would warn about a configuration that works. "Usually", though — this is a prediction,
    // not a proof. The cooldown is measured from the last alert of ANY kind, so in a crowd a rung under it can
    // still slip through when the alert before it was itself swallowed. Hence "may never", not "will never".
    if (config.AlertOnStareEscalation)
    {
      var dead = new List<string>(2);
      if (config.StareSeconds > 0 && config.StareSeconds < config.AlertCooldownSeconds)
        dead.Add($"'watching you' at {config.StareSeconds}s");
      if (config.FixateSeconds > 0 && config.FixateSeconds < config.AlertCooldownSeconds)
        dead.Add($"'still watching' at {config.FixateSeconds}s");

      if (dead.Count > 0)
        UiTheme.TextWrappedColored(UiTheme.Warn,
          $"Hrothgar may stay quiet: {string.Join(" and ", dead)} sits inside the " +
          $"{config.AlertCooldownSeconds:0}s cooldown that the first alert already spent, so it will usually be " +
          "swallowed. Raise it above the cooldown, or lower the cooldown.");
    }

    ImGui.Dummy(new Vector2(0, 4f * ImGuiHelpers.GlobalScale));
  }

  // ---- Alerts ----

  private static void DrawAlertsTab()
  {
    UiTheme.SectionHeader("When someone watch you", FontAwesomeIcon.Bell);

    ConfigCheckbox("Announce in chat",
      () => Plugin.Configuration.AlertInChat,
      v => Plugin.Configuration.AlertInChat = v,
      "Print a line to chat when someone new starts targeting you.");

    ConfigCheckbox("Play a sound",
      () => Plugin.Configuration.AlertWithSound,
      v => Plugin.Configuration.AlertWithSound = v,
      "Play one of the game's own chat sound effects. It follows your in-game sound settings, so it goes " +
      "quiet when the game does.");

    ImGui.AlignTextToFramePadding();
    ImGui.Text("Sound:");
    ImGui.SameLine();
    int soundIndex = Math.Clamp(Plugin.Configuration.AlertSoundId, 1, _soundNames.Length) - 1;
    ImGui.SetNextItemWidth(200f * ImGuiHelpers.GlobalScale);
    if (ImGui.Combo("##alertSoundCombo", ref soundIndex, _soundNames, _soundNames.Length))
    {
      Plugin.Configuration.AlertSoundId = soundIndex + 1;
      Plugin.Configuration.Save();
    }
    UiTheme.Tooltip("The same sounds you can play in chat with <se.1> through <se.16>.");
    ImGui.SameLine();
    if (ImGui.Button("Test##alertSoundTest"))
      Plugin.Alerts.TestSound();
    UiTheme.Tooltip("Play it once, ignoring the cooldown.");

    ImGui.AlignTextToFramePadding();
    ImGui.Text("Cooldown:");
    ImGui.SameLine();
    float cooldown = Plugin.Configuration.AlertCooldownSeconds;
    ImGui.SetNextItemWidth(200f * ImGuiHelpers.GlobalScale);
    if (ImGui.SliderFloat("##alertCooldown", ref cooldown, 0f, 60f, "%.0f"))
    {
      Plugin.Configuration.AlertCooldownSeconds = cooldown;
      Plugin.Configuration.Save();
    }
    ImGui.SameLine();
    ImGui.Text("seconds");
    UiTheme.Tooltip("Shortest gap between two alerts, so a crowd cannot spam you.");

    ImGui.Dummy(new Vector2(0, 4f * ImGuiHelpers.GlobalScale));
    DrawStareSection();

    ConfigCheckbox("Alert for party members",
      () => Plugin.Configuration.AlertForParty,
      v => Plugin.Configuration.AlertForParty = v,
      "Your party targets you constantly. Off unless you want to know.");

    ConfigCheckbox("Alert for friends",
      () => Plugin.Configuration.AlertForFriends,
      v => Plugin.Configuration.AlertForFriends = v,
      "Announce friends who target you.");

    ImGui.SameLine(0, 30f * ImGuiHelpers.GlobalScale);

    ConfigCheckbox("Alert for alliance members",
      () => Plugin.Configuration.AlertForAlliance,
      v => Plugin.Configuration.AlertForAlliance = v,
      "Announce alliance members who target you.");

    ImGui.Dummy(new Vector2(0, 4f * ImGuiHelpers.GlobalScale));

    ConfigCheckbox("Announce focus-list arrivals",
      () => Plugin.Configuration.AlertOnFocusArrival,
      v => Plugin.Configuration.AlertOnFocusArrival = v,
      "Say so when someone on your focus list comes into range. Once per visit — they have to leave and come " +
      "back to say it again, and re-focusing someone already standing there says nothing.\n\n" +
      "Off by default: an alert that started firing on its own after an update would be a nasty surprise.\n\n" +
      "Shares the cooldown with watcher alerts, and loses the tie — a stranger staring at you is the more urgent " +
      "line. Goes quiet with the nearby half.");

    ConfigCheckbox("Record history while the window is closed",
      () => Plugin.Configuration.RecordWhileClosed,
      v => Plugin.Configuration.RecordWhileClosed = v,
      "Keep remembering and announcing watchers while the Scent window is shut. Off means Hrothgar only " +
      "remember what you were there to see.");

    ImGui.Dummy(new Vector2(0, 6f * ImGuiHelpers.GlobalScale));
    UiTheme.TextWrappedColored(UiTheme.Muted,
      "Alerts fire once per person, not per glance — someone who keeps re-targeting you inside the cooldown " +
      "stays quiet. Whoever you have chosen not to hear about is filtered out BEFORE the cooldown, so a " +
      "party member can never burn the alert that a stranger in the same moment deserved.");

    DrawJournalSection();
  }

  /// <summary>
  /// The last few things the alert path decided, and why.
  ///
  /// HERE, at the bottom of the Alerts tab, directly under the dead-rung warning — which is a static
  /// PREDICTION of silence that admits its own limits ("a prediction, not a proof... hence 'may never'"). This
  /// is the observation that closes that loop, so it belongs beside it rather than on a rail entry of its own:
  /// a diagnostic is not a peer of Filters and Colours.
  ///
  /// Not the Watchers tab, though it is tempting — the journal carries focus arrivals, which belong to the
  /// NEARBY half, and that tab is the watcher half's territory.
  /// </summary>
  private static void DrawJournalSection()
  {
    ImGui.Dummy(new Vector2(0, 6f * ImGuiHelpers.GlobalScale));
    UiTheme.SectionHeader("What Hrothgar decided", FontAwesomeIcon.ListUl);

    var entries = Plugin.Journal.Snapshot();
    if (entries.Count == 0)
    {
      UiTheme.TextWrappedColored(UiTheme.Muted,
        "Nothing yet. When Hrothgar says something — or stays quiet when you expected otherwise — the reason " +
        "shows up here. Kept in memory for this session only.");
      return;
    }

    UiTheme.TextWrappedColored(UiTheme.Muted,
      "Newest first. A missing alert usually means a rule you forgot you set, and this is the rule saying so. " +
      "Kept in memory for this session only, and never the name of anyone you ignored.");
    ImGui.Dummy(new Vector2(0, 4f * ImGuiHelpers.GlobalScale));

    var scale = ImGuiHelpers.GlobalScale;
    var tableFlags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.NoSavedSettings
                   | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY;

    if (ImGui.BeginTable("##journal", 4, tableFlags, new Vector2(0f, 160f * scale)))
    {
      ImGui.TableSetupColumn("When", ImGuiTableColumnFlags.WidthFixed, 62f * scale);
      ImGui.TableSetupColumn("What", ImGuiTableColumnFlags.WidthFixed, 96f * scale);
      ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 30f * scale);
      ImGui.TableSetupColumn("Why", ImGuiTableColumnFlags.WidthStretch);
      ImGui.TableHeadersRow();

      foreach (var entry in entries)
      {
        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(UiTheme.Muted, entry.When.ToString("HH:mm:ss"));

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(ClassLabel(entry.Class));

        // The count IS the payload, and deliberately so: it is the whole of what a suppressed entry may say.
        // A name here would put an ignored player back on screen, one tab from where the user erased them.
        ImGui.TableNextColumn();
        ImGui.TextColored(UiTheme.Muted, entry.Subjects.ToString());

        ImGui.TableNextColumn();
        var (label, color) = OutcomeLabel(entry.Outcome);
        ImGui.TextColored(color, entry.Detail.Length > 0 ? $"{label} — {entry.Detail}" : label);
      }

      ImGui.EndTable();
    }
  }

  private static string ClassLabel(SignalClass signalClass) => signalClass switch
  {
    SignalClass.StareEscalation => "Still watching",
    SignalClass.NewWatcher => "New watcher",
    _ => "Marked arrived",
  };

  private static (string Label, Vector4 Color) OutcomeLabel(SignalOutcome outcome) => outcome switch
  {
    SignalOutcome.Said => ("said", UiTheme.Good),
    SignalOutcome.Waiting => ("waiting", UiTheme.Warn),
    SignalOutcome.SwitchedOff => ("not said", UiTheme.Muted),
    SignalOutcome.Filtered => ("not said", UiTheme.Muted),
    SignalOutcome.NoLongerTrue => ("dropped", UiTheme.Muted),
    _ => ("dropped", UiTheme.Muted),
  };

  // ---- Watchers ----

  private static void DrawWatchersTab()
  {
    UiTheme.SectionHeader("History", FontAwesomeIcon.History);

    UiTheme.TextWrappedColored(UiTheme.Muted,
      "Who watched you, how often, and when. Kept in memory for this session only and dropped on logout — a " +
      "permanent file of every stranger who ever glanced at you is nobody's idea of a good time.");
    ImGui.Dummy(new Vector2(0, 4f * ImGuiHelpers.GlobalScale));

    // "Keep history" read as a durability promise this setting does not make: it governs whether a watcher
    // survives looking away, within the session, and nothing here outlives logout. The label now says the
    // behaviour rather than a noun that implies a file.
    ConfigCheckbox("Keep watchers after they look away",
      () => Plugin.Configuration.KeepHistory,
      v => Plugin.Configuration.KeepHistory = v,
      "Off keeps only the ones watching you right now. Either way the list is dropped on logout.");

    ImGui.AlignTextToFramePadding();
    ImGui.Text("Entries to keep:");
    ImGui.SameLine();
    int historyLimit = Plugin.Configuration.HistoryLimit;
    ImGui.SetNextItemWidth(200f * ImGuiHelpers.GlobalScale);
    if (UiTheme.SliderIntManual("##historyLimit", ref historyLimit, 1, 50))
    {
      Plugin.Configuration.HistoryLimit = historyLimit;
      Plugin.Configuration.Save();
    }
    UiTheme.Tooltip("Oldest entries are dropped past this. Anyone watching you RIGHT NOW is never dropped, " +
            "so the limit can be briefly exceeded by a crowd all staring at once — dropping a live watcher " +
            "would hide them until they looked away and back.\r\n\r\n" +
            "Double-click to type an exact value.");

    ConfigCheckbox("Show timestamps",
      () => Plugin.Configuration.ShowTimestamps,
      v => Plugin.Configuration.ShowTimestamps = v,
      "Show the 'When' column in the history table.");

    ImGui.Dummy(new Vector2(0, 6f * ImGuiHelpers.GlobalScale));
    if (ImGui.Button("Clear history"))
      Plugin.WatcherLog.Clear();
    UiTheme.Tooltip("Forget everyone. Anyone still watching you reappears on the next sniff.");
  }

  // ---- HUD ----

  private static void DrawHudTab()
  {
    UiTheme.SectionHeader("Minimal mode", FontAwesomeIcon.Desktop);

    UiTheme.TextWrappedColored(UiTheme.Muted,
      "Strips the Scent window down to the list itself: no title bar, no toolbar, no column headers, no borders, " +
      "no scrollbar, no history, no footer. The window shrinks to what you size it to, and says '+N more' rather " +
      "than hiding the overflow behind a scrollbar you cannot reach.");
    ImGui.Dummy(new Vector2(0, 4f * ImGuiHelpers.GlobalScale));

    ConfigCheckbox("HUD mode",
      () => Plugin.Configuration.HudMode,
      v => Plugin.Configuration.HudMode = v,
      "Title bar, toolbar, history and footer off. /hscent hud toggles this too.");

    var hud = Plugin.Configuration.HudMode;
    if (!hud)
      ImGui.BeginDisabled();

    ConfigCheckbox("Lock position and size",
      () => Plugin.Configuration.HudLocked,
      v => Plugin.Configuration.HudLocked = v,
      "Stop the window being moved or resized by accident while it has no title bar to grab.");

    ConfigCheckbox("Click-through",
      () => Plugin.Configuration.HudClickThrough,
      v => Plugin.Configuration.HudClickThrough = v,
      "The Scent window stops responding to the mouse entirely. Use /hscent hud or this checkbox to get it back.\n\n" +
      "Focus target on hover also stops working while this is on — the window cannot see the mouse at all.");

    ImGui.AlignTextToFramePadding();
    ImGui.Text("Opacity:");
    ImGui.SameLine();
    float opacity = Plugin.Configuration.HudOpacity;
    ImGui.SetNextItemWidth(200f * ImGuiHelpers.GlobalScale);
    if (ImGui.SliderFloat("##hudOpacity", ref opacity, 0f, 1f, "%.2f"))
    {
      Plugin.Configuration.HudOpacity = opacity;
      Plugin.Configuration.Save();
    }
    UiTheme.Tooltip("Background opacity of the Scent window in HUD mode. 0 leaves the text floating on nothing.");

    if (!hud)
      ImGui.EndDisabled();

    // The escape hatch, spelled out while it matters. This window is a separate window and stays clickable,
    // which is the only reason the checkbox above can undo the flag it sets.
    if (Plugin.Configuration.HudMode && Plugin.Configuration.HudClickThrough)
    {
      ImGui.Dummy(new Vector2(0, 6f * ImGuiHelpers.GlobalScale));
      UiTheme.Icon(FontAwesomeIcon.ExclamationTriangle, UiTheme.Warn);
      ImGui.SameLine();
      UiTheme.TextWrappedColored(UiTheme.Warn,
        "The Scent window ignores the mouse entirely. Use /hscent hud or the checkbox above to get it back.");
    }
  }
}
