// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 Bancho
//
// HrothgarScent — an original implementation written against the public Dalamud API.
//
// Prior art (ideas only; no code, assets, or icons are derived from these works,
// and no license of theirs applies to this file):
//   Wholist     — Blooym                        (AGPL-3.0)
//                 https://github.com/Blooym/Dalamud.Wholist
//   PeepingTom  — thakyZ; orig. ascclemens      (EUPL-1.2)
//                 https://github.com/thakyZ/PeepingTom

using System;
using Dalamud.Game.Command;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using HrothgarScent.Scent;
using HrothgarScent.Windows;

namespace HrothgarScent;

public sealed class Plugin : IDalamudPlugin
{
  private const string CommandMain = "/hrothgarscent";
  private const string CommandShort = "/hscent";
  private const string CommandAlias = "/scent";

  /// <summary>The "[HrothgarScent]" prefix on every line this plugin prints, and its colour.</summary>
  internal const string ChatTag = "HrothgarScent";
  internal const ushort ChatTagColor = 45;

  [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
  [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
  [PluginService] public static IObjectTable Objects { get; private set; } = null!;
  [PluginService] public static IClientState ClientState { get; private set; } = null!;
  [PluginService] public static IFramework Framework { get; private set; } = null!;
  [PluginService] public static ICondition Condition { get; private set; } = null!;
  [PluginService] public static ITargetManager TargetManager { get; private set; } = null!;
  [PluginService] public static IChatGui ChatGui { get; private set; } = null!;
  [PluginService] public static IDtrBar DtrBar { get; private set; } = null!;
  [PluginService] public static IDataManager DataManager { get; private set; } = null!;
  [PluginService] public static IPluginLog Log { get; private set; } = null!;

  public static Configuration Configuration { get; private set; } = null!;
  public static ScentScanner Scanner { get; private set; } = null!;
  public static WatcherLog WatcherLog { get; private set; } = null!;
  public static AlertService Alerts { get; private set; } = null!;

  public readonly WindowSystem WindowSystem = new("HrothgarScent");
  private ScentWindow ScentWindow { get; init; }
  private ConfigWindow ConfigWindow { get; init; }

  /// <summary>
  /// Static mirror of <see cref="ScentWindow"/>, backing <see cref="IsMainWindowOpen"/>. The scanner is
  /// built before the windows are and must not hold a render-thread type, so the reference lives here
  /// instead of being handed to it.
  /// </summary>
  private static ScentWindow? _mainWindow;

  /// <summary>
  /// Static mirror of <see cref="ConfigWindow"/>, backing <see cref="ToggleConfigWindow"/>. Same reason as
  /// <see cref="_mainWindow"/>: the Scent window's own toolbar has a settings button and no reference to the
  /// plugin instance to reach the instance method with.
  /// </summary>
  private static ConfigWindow? _configWindow;

  /// <summary>
  /// The server info bar entry, or null if the bar refused us one. DtrBar.Get throws when the title is
  /// already taken, which a botched unload can leave behind; losing a count readout is a far better outcome
  /// than the whole plugin failing to load over a decoration.
  /// </summary>
  private readonly IDtrBarEntry? _dtrEntry;

  /// <summary>
  /// The inputs <see cref="UpdateDtr"/> last rendered the info bar from, or null before the first update.
  ///
  /// Keyed on the inputs, never on the rendered text. The text is a lossy view of them — it drops the watcher
  /// term entirely while WatcherCount is 0, which is most of the time — so turning the watcher half off in a
  /// quiet zone moves the tooltip and leaves the text identical, and a text-keyed guard returned before the
  /// tooltip was ever assigned. It then went on naming a half the user had just switched off until NearbyCount
  /// happened to move, which in a housing ward or an inn is never. Same discipline, and the same reason, as
  /// <see cref="Configuration.FilterSignature"/>: key on what the output is made of, not on the output.
  /// </summary>
  private (bool Nearby, bool Watchers, int NearbyCount, int WatcherCount)? _dtrLast;

  public Plugin()
  {
    Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

    // Before anything reads it. The windows are built below and DrawPlayerTable honours HiddenColumnMask on its
    // first frame, so a config still holding only the legacy ShowRaceColumn would show the wrong columns once and
    // then persist that as the user's choice.
    Configuration.Migrate();

    WatcherLog = new WatcherLog();
    Alerts = new AlertService();
    Scanner = new ScentScanner(WatcherLog, Alerts);

    ScentWindow = new ScentWindow();
    ConfigWindow = new ConfigWindow();
    _mainWindow = ScentWindow;
    _configWindow = ConfigWindow;
    WindowSystem.AddWindow(ScentWindow);
    WindowSystem.AddWindow(ConfigWindow);

    CommandManager.AddHandler(CommandMain, new CommandInfo(OnCommand)
    {
      HelpMessage = "Opens the Scent window. '/hrothgarscent config' opens settings.",
      AllowedInMacros = true,
    });
    CommandManager.AddHandler(CommandShort, new CommandInfo(OnCommand)
    {
      HelpMessage = "Alias for /hrothgarscent",
      AllowedInMacros = true,
    });
    CommandManager.AddHandler(CommandAlias, new CommandInfo(OnCommand)
    {
      HelpMessage = "Alias for /hrothgarscent",
      ShowInHelp = false,
      AllowedInMacros = true,
    });

    PluginInterface.UiBuilder.Draw += DrawUI;
    PluginInterface.UiBuilder.OpenMainUi += ToggleMainUI;
    PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUI;

    ClientState.Login += OnLogin;
    ClientState.Logout += OnLogout;

    _dtrEntry = CreateDtrEntry();
    if (_dtrEntry is not null)
      _dtrEntry.OnClick = OnDtrClick;

    // Unconditional, and deliberately NOT folded back into the guard above: the hover-focus watchdog in here is
    // the one focus-release path that outlives Dalamud suspending the draw loop, and gating it on whether the
    // info bar happened to grant us a decoration would put it back on the loop it exists to outlive. UpdateDtr
    // does its own null check.
    Framework.Update += OnFrameworkUpdate;

    // Crisp, larger font for section headers (baked at size, not upscaled). GameFontStyle's size is in RAW PIXELS
    // and Dalamud does not scale it, while body text is ~12pt * GlobalScale — so a flat 16f is barely larger than
    // body text at 150% and SMALLER than it at 200%, inverting the hierarchy on every section header. Baked
    // inside the atlas callback so it re-bakes when the user changes scale rather than freezing at load-time
    // scale.
    UiTheme.HeaderFont = PluginInterface.UiBuilder.FontAtlas.NewDelegateFontHandle(tk => tk.OnPreBuild(e =>
      e.Font = e.AddGameGlyphs(new GameFontStyle(GameFontFamily.Axis, 16f * ImGuiHelpers.GlobalScale),
        glyphRanges: null, mergeFont: default)));

    // The IsLoggedIn check covers a hot reload while already logged in, when Login never fires.
    if (Configuration.OpenOnLogin && ClientState.IsLoggedIn)
      ScentWindow.IsOpen = true;
  }

  public void Dispose()
  {
    Framework.Update -= OnFrameworkUpdate;
    _dtrEntry?.Remove();

    ClientState.Login -= OnLogin;
    ClientState.Logout -= OnLogout;

    PluginInterface.UiBuilder.Draw -= DrawUI;
    PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUI;
    PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUI;

    CommandManager.RemoveHandler(CommandMain);
    CommandManager.RemoveHandler(CommandShort);
    CommandManager.RemoveHandler(CommandAlias);

    // Before the windows go, and explicitly: RemoveAllWindows just empties the list, and OnClose fires only
    // from the draw loop, which the Draw unsubscribe above has already ended. So an unload mid-hover — a
    // /xltoggleplugin, or an unattended auto-update, neither of which needs the cursor to move — would leave
    // the game focus-targeting a passerby with the plugin gone and nothing left to clear it.
    ScentWindow.ReleaseHoverFocus();

    WindowSystem.RemoveAllWindows();
    _mainWindow = null;
    _configWindow = null;

    Scanner.Dispose();

    UiTheme.HeaderFont?.Dispose();
    UiTheme.HeaderFont = null;
  }

  /// <summary>
  /// Whether the Scent window is open.
  ///
  /// Window.IsOpen is a plain bool, so a cross-thread read can only ever be one frame stale — never a crash,
  /// and one stale frame costs at most a single extra recorded sighting. True while the window does not
  /// exist yet, so a scan landing between the scanner subscribing to Update and the windows being built
  /// records rather than silently drops a watcher.
  /// </summary>
  internal static bool IsMainWindowOpen => _mainWindow?.IsOpen ?? true;

  public void ToggleMainUI() => ScentWindow.Toggle();

  public void ToggleConfigUI() => ConfigWindow.Toggle();

  /// <summary>
  /// The same action as <see cref="ToggleConfigUI"/>, reachable without the plugin instance. Render thread
  /// only, like every other Window.Toggle call: the Scent window's toolbar cog is the caller.
  /// </summary>
  internal static void ToggleConfigWindow() => _configWindow?.Toggle();

  private void DrawUI() => WindowSystem.Draw();

  private static IDtrBarEntry? CreateDtrEntry()
  {
    try
    {
      return DtrBar.Get(ChatTag);
    }
    catch (Exception ex)
    {
      Log.Warning(ex, "Could not claim a server info bar entry; continuing without one");
      return null;
    }
  }

  /// <summary>
  /// The plugin's framework-thread tick, and the only one it subscribes. Framework thread, every frame.
  /// </summary>
  private void OnFrameworkUpdate(IFramework framework)
  {
    // First, and outside anything that could return early: once Dalamud stops raising the Draw event this is
    // the hover focus's only way back to the game. See ScentWindow.ReleaseStrandedHoverFocus.
    ScentWindow.ReleaseStrandedHoverFocus();

    UpdateDtr();
  }

  /// <summary>
  /// Keeps the info bar in step with the latest snapshot. Framework thread, every frame — hence the
  /// change guard below.
  /// </summary>
  private void UpdateDtr()
  {
    if (_dtrEntry is null)
      return;

    // Gate #3 of the PvP defence. Also covers logged out, the user turning it off, and both halves being off —
    // an info bar entry reading "Scent:" with nothing after it is worse than no entry.
    var show = Configuration.ShowDtr && ClientState.IsLoggedIn && !ClientState.IsPvP
            && (Configuration.EnableNearbyList || Configuration.EnableWatchers);
    if (_dtrEntry.Shown != show)
      _dtrEntry.Shown = show;
    if (!show)
      return;

    var snapshot = Scanner.Snapshot;

    // Assign only on change: each setter rebuilds the payload, and this runs every single frame. ADD EVERY NEW
    // INPUT OF THE TWO STRINGS BELOW HERE. Leaving one out does not break the build and does not look like a
    // bug — the info bar simply keeps the previous answer, with nothing on screen to explain why.
    var inputs = (Configuration.EnableNearbyList, Configuration.EnableWatchers,
      snapshot.NearbyCount, snapshot.WatcherCount);
    if (_dtrLast == inputs)
      return;

    _dtrLast = inputs;

    // Each half prints only its own count. The scanner keeps both up to date either way — the halves are UI
    // only — so this is the one place the toggles reach the info bar.
    _dtrEntry.Text = Configuration.EnableNearbyList
      ? Configuration.EnableWatchers && snapshot.WatcherCount > 0
        ? $"Scent: {snapshot.NearbyCount} ({snapshot.WatcherCount})"
        : $"Scent: {snapshot.NearbyCount}"
      : $"Scent: ({snapshot.WatcherCount})";

    _dtrEntry.Tooltip = (Configuration.EnableNearbyList, Configuration.EnableWatchers) switch
    {
      (true, true) => $"Hrothgar smell {snapshot.NearbyCount} nearby, {snapshot.WatcherCount} watching you.\n",
      (true, false) => $"Hrothgar smell {snapshot.NearbyCount} nearby.\n",
      _ => $"Hrothgar smell {snapshot.WatcherCount} watching you.\n",
    } + "Left-click: open Scent. Right-click: settings.";
  }

  private void OnDtrClick(DtrInteractionEvent ev)
  {
    if (ev.ClickType == MouseClickType.Left)
      ToggleMainUI();
    else if (ev.ClickType == MouseClickType.Right)
      ToggleConfigUI();
  }

  private void OnCommand(string command, string args)
  {
    var arg = args.Trim().ToLowerInvariant();
    switch (arg)
    {
      case "config" or "c" or "settings":
        ToggleConfigUI();
        return;

      case "hud":
        // Also the escape hatch out of click-through, which makes the Scent window itself unclickable —
        // including the control that would turn it back off.
        Configuration.HudMode = !Configuration.HudMode;
        Configuration.Save();
        ChatGui.Print($"Hrothgar {(Configuration.HudMode ? "go quiet" : "come back")}.", ChatTag, ChatTagColor);
        return;

      default:
        // Gate #4 of the PvP defence: refuse rather than open a window that is guaranteed to be empty.
        if (ClientState.IsPvP)
        {
          ChatGui.PrintError("Hrothgar no sniff in PvP.");
          return;
        }

        ToggleMainUI();
        return;
    }
  }

  private void OnLogin()
  {
    if (Configuration.OpenOnLogin)
      ScentWindow.IsOpen = true;
  }

  /// <summary>
  /// The watcher log is per-session by design, so it dies with the session. The type and code arguments are
  /// the game's logout reason; nothing here varies by it.
  /// </summary>
  private void OnLogout(int type, int code)
  {
    WatcherLog.Clear();
    ScentWindow.IsOpen = false;
  }
}
