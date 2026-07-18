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
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Dalamud.Game.Command;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
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

  /// <summary>
  /// Identifies this plugin's one chat link. Scoped per plugin by Dalamud, so the value only has to be unique
  /// here — there is no registry to collide with.
  /// </summary>
  private const uint TargetLinkCommandId = 1;

  /// <summary>
  /// The payload that makes an alert's name clickable. One for the whole plugin, not one per player: Dalamud
  /// hands the CLICKED MESSAGE to the handler, not the payload, so identity travels in the link's own visible
  /// text and a second payload would buy nothing.
  ///
  /// Null only if registration failed, which the alert path treats as "print the name as plain text" rather
  /// than as a reason to say nothing.
  /// </summary>
  internal static DalamudLinkPayload? TargetLink { get; private set; }

  /// <summary>
  /// UIColor row id the info bar's count turns when someone is actually fixated on you — a red, matching the
  /// chat alert's. Cosmetic only, and unverified against the live sheet from the shipped assemblies, on exactly
  /// the terms AlertService already accepts for its own: a wrong row id costs the wrong colour and nothing else.
  /// The tooltip says the same thing in words, so nothing depends on this landing.
  /// </summary>
  private const ushort DtrStareColor = 17;

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

  /// <summary>Dalamud's own atomic, crash-recovering file service — the sanctioned way to keep a file beside
  /// the config without hand-rolling durability. Backs <see cref="Marks"/>; see MarkStore.Load.</summary>
  [PluginService] public static IReliableFileStorage FileStorage { get; private set; } = null!;

  /// <summary>The game's own right-click menu. Gate #5 of the PvP defence lives on this one — see
  /// <see cref="OnMenuOpened"/>.</summary>
  [PluginService] public static IContextMenu ContextMenu { get; private set; } = null!;

  /// <summary>The game's own icons. Needs no caching and no disposal of ours — see ScentWindow's job cell.</summary>
  [PluginService] public static ITextureProvider Textures { get; private set; } = null!;

  /// <summary>Reads a texture back to bytes and writes it to a file — the one that saves a captured portrait to
  /// disk. A separate service from <see cref="Textures"/>; see <see cref="PortraitService"/>.</summary>
  [PluginService] public static ITextureReadbackProvider TextureReadback { get; private set; } = null!;

  /// <summary>The nameplates over players' heads. Gate #6 of the PvP defence lives on this one; see
  /// <see cref="NameplateService.Sync"/>.</summary>
  [PluginService] public static INamePlateGui NamePlateGui { get; private set; } = null!;

  /// <summary>Duty starts, wipes and clears. Gate #7 of the PvP defence is on this one; see
  /// DutyService.RecordClear.</summary>
  [PluginService] public static IDutyState DutyState { get; private set; } = null!;

  /// <summary>Addon open/refresh/close events. Backs <see cref="PortraitService"/>, which watches the Adventurer
  /// Plate (CharaCard) to capture a player's rendered portrait.</summary>
  [PluginService] public static IAddonLifecycle AddonLifecycle { get; private set; } = null!;

  public static Configuration Configuration { get; private set; } = null!;
  public static ScentScanner Scanner { get; private set; } = null!;
  public static WatcherLog WatcherLog { get; private set; } = null!;
  public static AlertService Alerts { get; private set; } = null!;

  /// <summary>How many times each player has emoted at you this session — /pet, /hug and the rest. In memory,
  /// dies at logout; the social-observation sibling of <see cref="WatcherLog"/>. See <see cref="EmoteLog"/>.</summary>
  public static EmoteLog Emotes { get; private set; } = null!;

  /// <summary>The durable record of players the user pointed at. The counterpart to <see cref="WatcherLog"/>,
  /// which is the record of players who pointed at them, and which is never written down.</summary>
  public static MarkStore Marks { get; private set; } = null!;

  /// <summary>The OPT-IN durable log of everyone seen nearby — off by default (Configuration.RecordAllNearby),
  /// bounded, and NEVER the mark store. The one place the plugin writes a stranger to disk by proximity, and only
  /// because the user asked. See <see cref="SeenLog"/>.</summary>
  public static SeenLog SeenLog { get; private set; } = null!;

  /// <summary>The eye over their head. Off unless the user asks.</summary>
  public static NameplateService Nameplates { get; private set; } = null!;

  /// <summary>Why the alerts did or did not fire. In memory, dies at logout; see <see cref="SignalJournal"/>.</summary>
  public static SignalJournal Journal { get; private set; } = null!;

  /// <summary>Faces, from the player's own public profile, only when a profile is opened. In memory, dies at
  /// logout; see <see cref="LodestoneService"/> for why this is the one thing here that touches a network.</summary>
  public static LodestoneService Lodestone { get; private set; } = null!;

  /// <summary>Rendered portraits captured from a player's own Adventurer Plate, on a click or (opt-in) as the
  /// user opens plates. In memory, dies at logout; see <see cref="PortraitService"/>.</summary>
  public static PortraitService Portraits { get; private set; } = null!;

  /// <summary>The profile. A window of its own rather than a popup inside the list, because the game's own
  /// right-click menus reach players the list has never seen and can open this with the list shut.</summary>
  internal static ProfileWindow? Profile { get; private set; }

  /// <summary>The in-game portrait, on its own — opened only by clicking a profile picture. Static like
  /// <see cref="Profile"/> so the profile's avatar can reach it without a plugin-instance reference.</summary>
  internal static PortraitWindow? PortraitView { get; private set; }

  /// <summary>Notes a cleared duty on the marks who were there. Never creates one.</summary>
  private readonly DutyService _duties;

  public readonly WindowSystem WindowSystem = new("HrothgarScent");
  private ScentWindow ScentWindow { get; init; }
  private ConfigWindow ConfigWindow { get; init; }
  private JournalWindow JournalWindow { get; init; }

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

  /// <summary>Static mirror of <see cref="JournalWindow"/>, backing <see cref="ToggleJournalWindow"/> — the same
  /// reason as <see cref="_configWindow"/>: surfaces without a plugin-instance reference (a future toolbar
  /// button, the command's static handler) still need to reach it.</summary>
  private static JournalWindow? _journalWindow;

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
  private (bool Nearby, bool Watchers, int NearbyCount, int WatcherCount, StareLevel Stare, bool Marked)?
    _dtrLast;

  public Plugin()
  {
    Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

    // Before Migrate, which folds the legacy focus and ignore lists into it, and before the windows below read
    // either. Blocking on purpose: see MarkStore.Load.
    Marks = new MarkStore();
    Marks.Load();

    // Before anything reads it. The windows are built below and DrawPlayerTable honours HiddenColumnMask on its
    // first frame, so a config still holding only the legacy ShowRaceColumn would show the wrong columns once and
    // then persist that as the user's choice.
    Configuration.Migrate();

    // Beside the marks and before the scanner that feeds it. Its own bounded, opt-in store; blocking Load for
    // the same reason Marks.Load blocks — the scanner must not write a one-entry file over the real one.
    SeenLog = new SeenLog();
    SeenLog.Load();

    WatcherLog = new WatcherLog();

    // Session-only, like the watcher log. Subscribes to the emote chat log here; needs only ChatGui, which is up.
    Emotes = new EmoteLog();
    Emotes.Subscribe();

    // Before AlertService, which embeds it in the very first alert it prints.
    TargetLink = ChatGui.AddChatLinkHandler(TargetLinkCommandId, OnTargetLink);

    // Before AlertService, which records into it from its very first decision.
    Journal = new SignalJournal();
    Alerts = new AlertService();
    Nameplates = new NameplateService();
    Scanner = new ScentScanner(WatcherLog, Alerts);

    // After the scanner: the clear handler reads its published snapshot for the roster.
    _duties = new DutyService();
    _duties.Subscribe();

    // Before the windows, which ask it for a face on their first frame.
    Lodestone = new LodestoneService();

    // Before the windows too — the profile asks it for a captured portrait on its first frame. Registers its own
    // Adventurer Plate listener in its constructor.
    Portraits = new PortraitService();

    ScentWindow = new ScentWindow();
    ConfigWindow = new ConfigWindow();
    JournalWindow = new JournalWindow();
    Profile = new ProfileWindow();
    PortraitView = new PortraitWindow();
    _mainWindow = ScentWindow;
    _configWindow = ConfigWindow;
    _journalWindow = JournalWindow;
    WindowSystem.AddWindow(ScentWindow);
    WindowSystem.AddWindow(ConfigWindow);
    WindowSystem.AddWindow(JournalWindow);
    WindowSystem.AddWindow(Profile);
    WindowSystem.AddWindow(PortraitView);

    CommandManager.AddHandler(CommandMain, new CommandInfo(OnCommand)
    {
      HelpMessage = "Opens the Scent window. 'config' opens settings, 'journal' opens everyone you've marked.",
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

    ContextMenu.OnMenuOpened += OnMenuOpened;

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

    ContextMenu.OnMenuOpened -= OnMenuOpened;

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
    _journalWindow = null;
    Profile = null;
    PortraitView = null;

    // Owns GPU textures, so it goes explicitly rather than waiting on a finalizer. Also flips its disposed flag
    // under its own lock, so a lookup still in flight drops its texture instead of parking it in a dead cache.
    Lodestone.Dispose();

    // Same reasons as Lodestone — owns GPU textures and an addon listener — so it goes explicitly too.
    Portraits.Dispose();

    // The lines themselves stay in the log — chat cannot be unprinted — but the links in them go dead here, and
    // that is correct rather than a shortfall: after this there is no scanner, no PvP gate and no object table
    // access, so a link that still fired would be reaching into the game on behalf of a plugin that no longer
    // exists.
    ChatGui.RemoveChatLinkHandler();
    TargetLink = null;

    Scanner.Dispose();

    // Unsubscribes from the emote chat log. In-memory only, so nothing to flush.
    Emotes.Dispose();

    _duties.Dispose();

    // Detaches the handler and scrubs what it painted. Before the scanner's data goes, so the redraw it asks
    // for has something coherent to redraw against.
    Nameplates.Dispose();

    // After the scanner stops, so nothing can queue another write behind this one. Bounded internally: a hung
    // disk must not wedge a synchronous unload.
    Marks.Flush();
    SeenLog.Flush();

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

  public void ToggleJournalUI() => JournalWindow.Toggle();

  /// <summary>The same action as <see cref="ToggleJournalUI"/>, reachable without the plugin instance — the same
  /// reason <see cref="ToggleConfigWindow"/> exists.</summary>
  internal static void ToggleJournalWindow() => _journalWindow?.Toggle();

  /// <summary>Toggles HUD mode — the quiet, chrome-less Scent window. The one action shared by the /hscent hud
  /// command and the title bar's HUD button, so the two cannot drift.</summary>
  internal static void ToggleHud()
  {
    Configuration.HudMode = !Configuration.HudMode;
    Configuration.Save();
    ChatGui.Print(Configuration.HudMode ? "Going quiet." : "Coming back.", ChatTag, ChatTagColor);
  }

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

    // Every frame, unthrottled, and deliberately not driven by an event: the one event that looks right —
    // TerritoryChanged — reads IsPvP before the game has assigned it, and so fails open on the way IN. See
    // NameplateService.Sync. Two field reads and a comparison; it costs nothing to be sure.
    Nameplates.Sync();

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
    var stare = Configuration.EnableWatchers ? snapshot.MaxStareLevel : StareLevel.Glance;
    var marked = Configuration.EnableNearbyList && snapshot.MarkedNearby;

    var inputs = (Configuration.EnableNearbyList, Configuration.EnableWatchers,
      snapshot.NearbyCount, snapshot.WatcherCount, stare, marked);
    if (_dtrLast == inputs)
      return;

    _dtrLast = inputs;

    // Each half prints only its own count. The scanner keeps both up to date either way — the halves are UI
    // only — so this is the one place the toggles reach the info bar.
    //
    // The watcher count carries the INTENSITY as well as the volume: "42 (1)" reads the same whether that one
    // glanced for a frame or has held you for a minute, and intensity is the interesting half. This is the only
    // surface that is on screen with the window shut, so it is the cheapest place in the plugin to say so.
    var counts = Configuration.EnableNearbyList
      ? Configuration.EnableWatchers && snapshot.WatcherCount > 0
        ? $"Scent: {snapshot.NearbyCount} ({snapshot.WatcherCount})"
        : $"Scent: {snapshot.NearbyCount}"
      : $"Scent: ({snapshot.WatcherCount})";

    var text = new SeStringBuilder();

    // One glyph and one colour is the whole budget. The info bar is real estate shared with every other
    // plugin, and a third decoration would be antisocial.
    if (marked)
      text.AddIcon(BitmapFontIcon.Returner);

    if (stare > StareLevel.Glance)
      text.AddUiForeground(DtrStareColor).AddText(counts).AddUiForegroundOff();
    else
      text.AddText(counts);

    _dtrEntry.Text = text.Build();

    // The tier in WORDS as well as in colour. A colour-only escalation is invisible to a colourblind user, and
    // this is the one readout with no room to say it any other way.
    var stareLine = stare switch
    {
      StareLevel.Fixation => "\nOne of them fixed on you.",
      StareLevel.Stare => "\nOne of them not looking away.",
      _ => string.Empty,
    };

    _dtrEntry.Tooltip = (Configuration.EnableNearbyList, Configuration.EnableWatchers) switch
    {
      (true, true) => $"{snapshot.NearbyCount} nearby, {snapshot.WatcherCount} watching you.",
      (true, false) => $"{snapshot.NearbyCount} nearby.",
      _ => $"{snapshot.WatcherCount} watching you.",
    } + stareLine + (marked ? "\nOne you marked is here." : string.Empty)
      + "\nLeft-click: open Scent. Right-click: settings.";
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

      case "journal" or "j":
        // Not PvP-gated at the command like the default is: the journal's own DrawConditions refuses to draw in
        // a match, so a toggle here is harmless and a refusal would be a special case for no gain.
        ToggleJournalUI();
        return;

      case "hud":
        // Also the escape hatch out of click-through, which makes the Scent window itself unclickable —
        // including the control that would turn it back off.
        ToggleHud();
        return;

      default:
        // Gate #4 of the PvP defence: refuse rather than open a window that is guaranteed to be empty.
        if (ClientState.IsPvP)
        {
          ChatGui.PrintError("I don't scan in PvP.");
          return;
        }

        ToggleMainUI();
        return;
    }
  }

  /// <summary>
  /// Addons whose right-click menu is about a player we may mark.
  ///
  /// An ALLOWLIST, and not optional. OnMenuOpened fires for every context menu in the game, and
  /// MenuTargetDefault reads its fields lazily off a persistent AgentContext — so on a menu with no player in
  /// it, it happily reports whoever was right-clicked last. Without this, "Remember this Player" appears on an
  /// unrelated menu and records the wrong person.
  ///
  /// null is the world/nameplate menu — right-clicking a player in the world itself. The blacklist is
  /// deliberately absent: its target cannot be read as a MenuTargetDefault, so the item would never work there
  /// anyway, and the guards below make it silently not appear rather than appear and misfire.
  /// </summary>
  private static readonly string?[] PlayerMenuAddons =
  [
    null, "LookingForGroup", "PartyMemberList", "FriendList", "FreeCompany", "SocialList", "ContactList",
    "ChatLog", "_PartyList", "LinkShell", "CrossWorldLinkshell", "ContentMemberList", "BeginnerChatList",
  ];

  /// <summary>
  /// Adds "Remember this Player" to the game's own right-click menu, wherever it names a player.
  ///
  /// This is the one surface that reaches players the scanner never can — the friend list, Party Finder, the
  /// chat log, an FC roster — because they are not in the object table at all. It is also why the durable store
  /// is defensible: the deliberate right-click that creates a mark IS the consent that justifies keeping it.
  ///
  /// Framework thread, raised by Dalamud. Dalamud wraps both this handler and the click callback, so an
  /// exception here cannot take the game down; the try/catch is to stop a per-open log flood, not for safety.
  /// </summary>
  private void OnMenuOpened(IMenuOpenedArgs args)
  {
    // GATE #5 of the PvP defence, and its own statement rather than a clause on something else — see the note
    // at ScentScanner's gate #1 on why these are kept separate. This one cannot lean on the others: gates #1-#4
    // are backstopped by the scanner publishing an empty snapshot in PvP, and this reads the MARK STORE, which
    // is fully populated in PvP like anywhere else. The friend list and Party Finder are both reachable from
    // inside a PvP instance, and an unfiltered handler would reach the world menu — i.e. enemy players.
    if (ClientState.IsPvP)
      return;

    try
    {
      if (!Configuration.ShowContextMenuMark)
        return;

      // UiBuilder's own "should we be decorating the game right now" answer — respects the user hiding the UI.
      if (!PluginInterface.UiBuilder.ShouldModifyUi)
        return;

      if (args.MenuType != ContextMenuType.Default)
        return;

      if (!PlayerMenuAddons.Contains(args.AddonName))
        return;

      if (args.Target is not MenuTargetDefault target)
        return;

      if (target.TargetName is not { Length: > 0 } name)
        return;

      // ValueNullable, never .Value, and never a RowId == 0 test on its own. The game's own field here is a
      // SIGNED 16-bit id, so "none" arrives sign-extended as 4294967295 rather than as 0 — and .Value throws on
      // a row that has not loaded. One nullable check covers the zero, the sentinel and the unloaded row at
      // once. Same rule as ScentScanner's world and job reads.
      if (target.TargetHomeWorld.ValueNullable is not { } world)
        return;

      var worldId = target.TargetHomeWorld.RowId;
      var worldName = world.Name.ExtractText();

      // Marking yourself is never what anyone meant, and every other path already excludes self.
      if (Objects.LocalPlayer is { } me
          && me.HomeWorld.RowId == worldId
          && string.Equals(me.Name.TextValue, name, StringComparison.Ordinal))
        return;

      // TargetContentId is deliberately not read — not even to discard. Dalamud bans collecting ACCOUNT ids
      // outright; ContentId is per-character and is not banned, so this is conservative posture rather than
      // compliance, and it is stated as such in the README. Never touch TargetObject either: it calls into the
      // object table internally, which this thread may be on but this handler has no business doing.
      var key = new WatcherKey(name, worldId);

      // ONE entry at the top level, and a submenu behind it. The game's player menu is shared real estate —
      // screenshot any populated one and half of it is plugins — so a plugin that wants three verbs takes one
      // row and nests them, rather than three rows and a third of the menu.
      args.AddMenuItem(new MenuItem
      {
        Name = "Scent",
        PrefixChar = 'H',
        PrefixColor = ChatTagColor,
        IsSubmenu = true,

        // Primitives only. Capturing args or target would capture a pointer into a context the game reuses,
        // and every field would re-read whatever it holds by the time the user clicks — the same staleness
        // ScentRow's no-Address rule exists to prevent, one level out.
        OnClicked = clicked => OpenScentSubmenu(clicked, key, worldName),
      });
    }
    catch (Exception ex)
    {
      Log.Warning(ex, "Building the context menu item failed");
    }
  }

  /// <summary>
  /// Builds the Scent submenu, at click time rather than at open time.
  ///
  /// The mark is re-read HERE, not carried in from the parent. Between the menu opening and this submenu opening
  /// the user may have marked or forgotten this player from another surface, and a Remember item built from a
  /// stale read would delete a record it offered to create.
  ///
  /// Only the verb that applies is listed: an unmarked player gets Remember, a marked one gets Forget. A menu
  /// that offers both always has one entry that does nothing, and the mark is a one-click toggle precisely
  /// because it has no half-state to disambiguate.
  /// </summary>
  private static void OpenScentSubmenu(IMenuItemClickedArgs args, WatcherKey key, string worldName)
  {
    var marked = Marks.Find(key) is not null;

    args.OpenSubmenu("Scent", new List<MenuItem>
    {
      new()
      {
        Name = "Profile",
        PrefixChar = 'H',
        PrefixColor = ChatTagColor,
        OnClicked = _ => Profile?.Open(key, worldName),
      },
      new()
      {
        Name = marked ? "Forget this Player" : "Remember this Player",
        PrefixChar = 'H',
        PrefixColor = ChatTagColor,
        OnClicked = _ => ToggleMarkFromMenu(key, worldName),
      },
      new()
      {
        Name = "Return",
        IsReturn = true,
      },
    });
  }

  /// <summary>
  /// The context menu's click. Toggles the whole record: remember creates it, forget deletes it outright.
  ///
  /// Deliberately coarse. The menu is a one-click surface with no room to explain itself, and a half-state
  /// there would need a second click to mean anything; the note, the colour and the flags all live in the
  /// editor, which is where a choice belongs.
  /// </summary>
  /// <summary>
  /// Targets the player whose name was clicked in an alert.
  ///
  /// IDENTITY COMES OUT OF THE MESSAGE, which is not a workaround but the only shape Dalamud offers: the handler
  /// is given the clicked SeString and a command id, never the payload, and DalamudLinkPayload's Extra fields
  /// have no public setter. The alert writes exactly one link and the first text inside it is the player's
  /// "Name@World", so reading it back is deterministic rather than a parse of prose.
  ///
  /// Deliberately NOT gated on the ignore list. The promise is "never SHOW or ANNOUNCE them again", and it binds
  /// what the plugin does on its own initiative — a list it draws, a line it speaks. This is the user clicking a
  /// specific name on a line that was already printed and cannot be unprinted. Refusing would also be
  /// self-defeating: the only honest way to explain a dead link is to name them, which is the thing the promise
  /// actually forbids. So the click is obeyed, and nothing new about them reaches the screen.
  /// </summary>
  private static void OnTargetLink(uint commandId, SeString message)
  {
    try
    {
      if (ExtractLinkedName(message) is not { } name)
        return;

      // Written by ScentRow.FullName, so the separator is always there — a row with no world resolved never
      // gets a link in the first place, precisely because this identity would be incomplete.
      var at = name.LastIndexOf('@');
      if (at <= 0 || at == name.Length - 1)
        return;

      if (WorldPalette.IdOf(name[(at + 1)..]) is not { } worldId)
        return;

      PlayerActions.TargetByIdentity(new WatcherKey(name[..at], worldId));
    }
    catch (Exception ex)
    {
      Plugin.Log.Warning(ex, "OnTargetLink failed");
    }
  }

  /// <summary>
  /// Reads the player's "Name@World" back out of a clicked alert.
  ///
  /// Separated from <see cref="OnTargetLink"/> because it is the one part of this path that can be exercised
  /// without a running game, and the one most likely to break silently. The message does not arrive as it was
  /// built: it is encoded into the chat log and parsed back out, and SeString.Parse is free to split or merge
  /// text runs on its own terms. Everything here is an assumption about what survives that, so it is verified
  /// against the real encoder rather than reasoned about.
  ///
  /// Takes the text INSIDE the link — payloads after the DalamudLinkPayload, up to the terminator — rather than
  /// the message's TextValue, which would flatten name, verb and duration into one string to be picked apart
  /// with guesswork that breaks every time the copy changes.
  /// </summary>
  /// <returns>The linked text, or null if this message carries no usable link.</returns>
  internal static string? ExtractLinkedName(SeString message)
  {
    var payloads = message.Payloads;
    var link = payloads.FindIndex(p => p is DalamudLinkPayload);
    if (link < 0)
      return null;

    // Concatenated, not first-only: the round trip may return the name as several adjacent runs, and taking
    // just the first would silently target a player whose name is a PREFIX of the real one.
    var name = new StringBuilder();
    for (var i = link + 1; i < payloads.Count; i++)
    {
      if (payloads[i] is TextPayload text)
      {
        name.Append(text.Text);
        continue;
      }

      // Anything that is not text ends the link's own run. The terminator is the expected one; a colour change
      // would do just as well, and either way the name cannot continue past it.
      break;
    }

    return name.Length == 0 ? null : name.ToString();
  }

  private static void ToggleMarkFromMenu(WatcherKey key, string worldName)
  {
    if (Marks.Find(key) is not null)
    {
      Marks.Remove(key);
      ChatGui.Print($"I'll forget {key.Name}.", ChatTag, ChatTagColor);
      return;
    }

    Marks.Update(key, worldName, mark => mark with { Marks = mark.Marks | MarkKind.Focus });
    ChatGui.Print($"I'll remember {key.Name}.", ChatTag, ChatTagColor);
  }

  private void OnLogin()
  {
    if (Configuration.OpenOnLogin)
      ScentWindow.IsOpen = true;
  }

  /// <summary>
  /// The watcher log is per-session by design, so it dies with the session. The type and code arguments are
  /// the game's logout reason; nothing here varies by it.
  ///
  /// The two stores part company here, and the asymmetry IS the design: the watcher log — everyone who looked
  /// at you, gathered without their say — is destroyed, while the marks — the people you deliberately pointed
  /// at — are pushed to disk. Durable because the user authored it; ephemeral because the stranger did not
  /// consent to it.
  /// </summary>
  private void OnLogout(int type, int code)
  {
    WatcherLog.Clear();

    // Beside WatcherLog.Clear and on the same rule, not as tidying: a cached face is session state about a
    // stranger who did not consent to it, so it lives exactly as long as the log of who looked at you. It is
    // also the only state here bought over a network, which makes keeping it past the session harder to defend
    // rather than easier.
    Lodestone.Clear();

    // A captured plate is the same kind of session state about a stranger — it dies with the session too, on the
    // same rule as the Lodestone face beside it.
    Portraits.Clear();

    // The session-quiet judgements ("I've seen this stranger, I'm fine") go with the session that formed them,
    // on the same rule: a stranger the user merely stopped being pinged about earned no durable record.
    Alerts.ClearQuieted();

    // Emote tallies are session social observations about strangers — they die with the session, like the watcher
    // log they mirror.
    Emotes.Clear();

    Marks.Flush();

    // Persisted, NOT cleared: the nearby log is durable by design (that is the whole point of the opt-in), so it
    // survives logout exactly as the marks do. Only the ephemeral stores — the watcher log, the faces, the
    // portraits' textures, the session-quiet — die here.
    SeenLog.Flush();

    ScentWindow.IsOpen = false;

    // The profile names a person and shows their face. Left open, it would greet the next character to log in
    // with the last one's stranger still on screen.
    if (Profile is not null)
      Profile.IsOpen = false;

    // The portrait window shows a face too, and on the same reasoning it closes with the session.
    if (PortraitView is not null)
      PortraitView.IsOpen = false;
  }
}
