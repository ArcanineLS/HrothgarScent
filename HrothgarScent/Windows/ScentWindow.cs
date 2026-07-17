using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using HrothgarScent.Scent;

namespace HrothgarScent.Windows;

/// <summary>
/// The fusion: one nearby-player list in which "is this player looking at you" is a first-class, sortable
/// column rather than a second window. The radar and the stalker-detector are the same list.
///
/// Everything drawn here comes out of <see cref="ScentScanner.Snapshot"/>. Not one line touches the object
/// table or a live IGameObject — the table asserts the main thread and throws off it, and this runs on the
/// render thread. Row actions marshal themselves; see <see cref="PlayerActions"/>.
/// </summary>
public sealed class ScentWindow : Window
{
  /// <summary>
  /// Sort keys, doubling as the ImGui columns' user ids. Starts at 1 on purpose: 0 is ImGui's "no user id",
  /// and a column carrying it silently refuses to sort.
  ///
  /// Values are persisted in Configuration.HiddenColumnMask and Configuration.SortColumn; ScentColumns mirrors
  /// them and must never drift.
  /// </summary>
  private enum ScentColumn : uint
  {
    Watching = 1,
    Name = 2,
    Job = 3,
    Level = 4,
    World = 5,
    Company = 6,
    Distance = 7,
    Race = 8,
    Mark = 9,
  }

  /// <summary>
  /// Bumped from 100: a grammar with several terms in it eats a hundred characters fast, and
  /// <c>fc:Some Long Company Name world:Sargatanas !bob</c> is an ordinary query rather than an abusive one.
  /// Still a bound, because this is a text box and unbounded input in one is how you find out about the
  /// allocator.
  /// </summary>
  private const int SearchMaxLength = 256;

  /// <summary>The help marker's glyph, in one place because its width is measured against it in DrawToolbar and
  /// two spellings would silently mis-size the search box.</summary>
  private const string SearchHelpGlyph = "(?)";

  /// <summary>
  /// The grammar, on a hover.
  ///
  /// Here rather than in a config panel, which is where the prior art puts its "How to Search" page — a search
  /// syntax you have to open a settings window to read is a search syntax nobody reads. It sits one hover from
  /// the box it describes.
  /// </summary>
  private const string SearchHelp =
    "Bare words match the name, anywhere in the word unless you say otherwise.\r\n\r\n" +
    "world:sarg     home world\r\n" +
    "fc:free co     free company tag\r\n" +
    "job:whm        job, short or long name\r\n" +
    "race:lala      race\r\n" +
    "note:griefer   what you wrote about them\r\n" +
    "note:*         anyone you wrote a note about\r\n" +
    "note:!         anyone you did not\r\n\r\n" +
    "sarg*  starts with      *sarg  ends with      =Bob Smith  exactly\r\n" +
    "!bob   not              !world:sarg  not on that world\r\n\r\n" +
    "Values can have spaces: fc:Free Company Name works.";

  /// <summary>Ceiling for the "hide low level" filter — the throwaway alts and bots milling around aetherytes.</summary>
  private const byte LowLevelThreshold = 3;

  /// <summary>First row of the game's job-icon block; the icon for job N is this plus N. Undocumented, and safe
  /// only because <see cref="DrawJobIcon"/> asks for it with the non-throwing lookup.</summary>
  private const uint JobIconBase = 62100;

  private const float HistoryTableHeight = 120f;

  /// <summary>
  /// How long <see cref="Draw"/> may go unrun before <see cref="ReleaseStrandedHoverFocus"/> calls the hover
  /// focus stranded. Far clear of a hitch or a slow frame: a false positive drops a focus target the cursor is
  /// still resting on, while the cost of waiting is a fraction of a second of held focus behind a hidden UI.
  /// </summary>
  private const long HoverFocusGraceMs = 250;

  /// <summary>
  /// The one sentence for "not scanning", shared by every readout that has to refuse to answer with a number.
  /// Three copies of it is how one of them ends up saying something subtly different about the same state.
  /// </summary>
  private const string NoseClosed =
    "Not scanning right now. This means I'm not looking — not that nobody is there.";

  private List<ScentRow> _view = [];
  private long _viewVersion = -1;
  private int _viewSignature;

  /// <summary>
  /// Keys of the focus-list players in <see cref="_view"/>, rebuilt with the view.
  ///
  /// A set beside the view rather than a flag on the row. Baking IsFocused into ScentRow at scan time — the way
  /// IsSameFreeCompany is baked — would be the obvious mirror and is the wrong trade twice over: the flag would
  /// be as stale as the last scan, up to a 2000ms configured rescan interval, and FilterSignature cannot fix
  /// that, because rebuilding the view re-reads the SAME snapshot rows and their SAME stale flag. Rebuilding
  /// here instead costs one O(rows x focused) pass on the frame a filter or a snapshot actually moves — the
  /// same shape and the same place as BuildView's existing ignore scan — and buys an O(1) lookup per row per
  /// frame with no staleness at all. The scanner tests the focus list directly for its arrival alert, so
  /// nothing on the framework side needs the flag either.
  /// </summary>
  private HashSet<WatcherKey> _focusedKeys = [];

  /// <summary>
  /// Rows in <see cref="_view"/> that are not self, cached because the footer reads it every frame.
  ///
  /// Self is excluded on both sides: ScentScanner counts others into NearbyCount (<c>if (!isSelf) nearby++</c>)
  /// while the table shows everyone, so a shown self would make the two halves of the footer's one line
  /// disagree by one — which is precisely the "Nearby: 0 with a row on screen" the footer used to print.
  /// </summary>
  private int _shownOthers;

  /// <summary>Not persisted: a filter that silently hides everyone on next login, with no visible cause, is
  /// a support ticket.</summary>
  private string _search = string.Empty;

  /// <summary>
  /// Field names in the search box that do not exist, as of the last rebuild.
  ///
  /// Kept so the toolbar can say so. A misspelled field matches everything rather than nothing — see
  /// ScentQuery — which is the safe failure, but a silent one: the box would look like it worked and the filter
  /// would look like it did nothing. This is what turns that into a sentence.
  /// </summary>
  private IReadOnlyList<string> _queryUnknownFields = [];

  private ScentColumn _sortColumn;
  private bool _sortAscending;

  /// <summary>
  /// The row the hover-focus feature last handed to the game, or 0 if it holds nothing.
  ///
  /// The one piece of this window's state two threads write: <see cref="HoverFocus"/> on the render thread and
  /// <see cref="ReleaseStrandedHoverFocus"/> on the framework thread. Every access below goes through Interlocked
  /// or Volatile for that reason, and the release is an exchange so that two threads reaching for the same id
  /// cannot both win it and queue the clear twice.
  /// </summary>
  private ulong _hoverFocusId;

  /// <summary>Whether any row claimed the cursor this frame. Reset at the top of every <see cref="Draw"/>.</summary>
  private bool _hoverFocusSeen;

  /// <summary>
  /// When <see cref="Draw"/> last ran. Written on the render thread and read on the framework thread by
  /// <see cref="ReleaseStrandedHoverFocus"/>, which is the whole of how that watchdog notices Draw has stopped.
  /// </summary>
  private long _lastDrawTicks;

  /// <summary>
  /// Height this window wants, or null while it must not pin one. Written at the end of every <see cref="Draw"/>
  /// and read by the next <see cref="PreDraw"/>, one frame later, because ImGui fixes a window's size at Begin —
  /// which Dalamud has already called by the time Draw can see how much room the list needed. The lag is one
  /// frame on a resize and is not visible.
  /// </summary>
  private float? _desiredHeight;

  public ScentWindow()
    : base("Scent##hrothgarscent-main")
  {
    SizeConstraints = new WindowSizeConstraints
    {
      MinimumSize = new Vector2(420, 240),
      MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
    };
    Size = new Vector2(560, 380);
    SizeCondition = ImGuiCond.FirstUseEver;

    // Restore the persisted sort. Guarded rather than cast blindly: the value round-trips through a JSON file
    // the user can hand-edit, and an out-of-range column would fall through the sort switch to the default
    // while the header arrow claimed something else.
    var stored = (ScentColumn)Plugin.Configuration.SortColumn;
    _sortColumn = Enum.IsDefined(stored) ? stored : ScentColumn.Name;
    _sortAscending = Plugin.Configuration.SortAscending;
  }

  /// <summary>
  /// Gate #2 of the eight-way PvP defence, plus the user's own hide-while-busy choices.
  ///
  /// The PvP check is a competitive-integrity requirement and a condition of Dalamud plugin acceptance. It
  /// is deliberately not user-configurable and must never be given an option.
  /// </summary>
  public override bool DrawConditions()
  {
    if (Plugin.ClientState.IsPvP)
      return false;
    if (!Plugin.ClientState.IsLoggedIn)
      return false;

    var config = Plugin.Configuration;
    if (config.HideInCombat && Plugin.Condition[ConditionFlag.InCombat])
      return false;
    if (config.HideInDuty && IsBoundByDuty())
      return false;
    if (config.HideInCutscene && IsInCutscene())
      return false;

    return true;
  }

  /// <summary>
  /// HUD mode, applied through the flags Dalamud's Window already exposes rather than a second window class.
  /// Set here rather than in Draw because ImGui reads window flags at Begin, which the WindowSystem has
  /// already called by the time Draw runs.
  /// </summary>
  public override void PreDraw()
  {
    var config = Plugin.Configuration;
    var flags = ImGuiWindowFlags.None;

    if (config.HudMode)
    {
      flags |= ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar
             | ImGuiWindowFlags.NoScrollWithMouse;
      if (config.HudLocked)
        flags |= ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove;

      // NoInputs makes the whole window unclickable, including any control that would turn it back off. The
      // escape hatches are /hscent hud and the config window's own checkbox — that window is a separate
      // window and stays clickable. Do not ship this flag without both of them.
      if (config.HudClickThrough)
        flags |= ImGuiWindowFlags.NoInputs;

      BgAlpha = config.HudOpacity;
    }
    else
    {
      // float? — null restores the Dalamud/ImGui default rather than pinning it at fully opaque.
      BgAlpha = null;
    }

    // Relaxed with the chrome. A minimal overlay that refuses to go below 420x240 is not minimal, and the floor
    // was most of why HUD mode still showed acres of grey. Set here for the same reason BgAlpha is: Dalamud's
    // Window.DrawInternal calls PreDraw and then ApplyConditionals, which reads Size, BgAlpha AND
    // SizeConstraints — and multiplies the constraints by GlobalScale itself, so these must NOT be pre-scaled.
    var constraints = new WindowSizeConstraints
    {
      MinimumSize = config.HudMode ? new Vector2(160, 40) : new Vector2(420, 240),
      MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
    };

    // Auto-height: pin Y to what Draw measured last frame, leave X alone so the user keeps their width.
    //
    // Min.Y == Max.Y IS the pin, and it is supported rather than degenerate: Dalamud's GetValidatedConstraints
    // throws the maximum away only when it is STRICTLY below the minimum. They must move together all the same —
    // a Max.Y that ever fell under Min.Y would reset the maximum on BOTH axes to float.MaxValue and the pin
    // would vanish with no error.
    //
    // Divided by GlobalScale, and this is the whole ballgame. ApplyConditionals multiplies the constraints by it
    // before ImGui sees them, while _desiredHeight is already in real pixels because every font and style figure
    // it was measured from is. Skip the divide and it is perfect at 100% UI scale and 25% too tall — a fresh
    // slab — for everyone who moved the slider.
    //
    // Clamped to the viewport, because "cap then scroll" means nothing if the cap can push the footer off the
    // bottom of the screen. bodyMax re-clamps the table from there, which is what the cap was for anyway.
    //
    // The 240 floor goes with it, and that is the point: one row must mean a small window, and 240 was most of
    // what stopped it being one.
    if (!config.HudMode && _desiredHeight is { } desired)
    {
      var pinned = MathF.Min(desired, ImGuiHelpers.MainViewport.WorkSize.Y) / ImGuiHelpers.GlobalScale;
      constraints.MinimumSize = new Vector2(420, pinned);
      constraints.MaximumSize = new Vector2(float.MaxValue, pinned);
    }

    SizeConstraints = constraints;

    Flags = flags;

    // Escape would close a window that has no visible close button in HUD mode, which reads as the plugin
    // vanishing.
    RespectCloseHotkey = !config.HudMode;
  }

  /// <summary>
  /// Drops a focus target the hover feature set. Draw — where it would otherwise be cleared — stops running
  /// the moment the window closes, and leaving the game holding a focus target the user never chose, with
  /// nothing on screen to explain it, is worse than a wasted call.
  /// </summary>
  public override void OnClose() => ReleaseHoverFocus();

  /// <summary>
  /// Covers the hide conditions, which skip Draw without ever closing the window.
  /// </summary>
  /// <remarks>
  /// Dalamud runs this every frame the window is open, and — this is the part that matters — before it tests
  /// <see cref="DrawConditions"/> and returns early. That early return skips Draw without calling
  /// <see cref="OnClose"/>, because IsOpen is still true, so the hide-while-busy options and the PvP gate
  /// would otherwise strand the hover focus for a whole fight or duty: the same end state OnClose exists to
  /// prevent, reached by the one route it cannot see.
  ///
  /// It does NOT cover the draw loop itself stopping, and cannot: Dalamud calls this from WindowSystem.Draw,
  /// which is raised from the UiBuilder.Draw event, so every frame that event is skipped is a frame this was
  /// never called on either. <see cref="ReleaseStrandedHoverFocus"/> owns that half, from the framework thread.
  /// </remarks>
  public override void Update()
  {
    if (!DrawConditions())
      ReleaseHoverFocus();
  }

  public override void Draw()
  {
    // One read per frame. The snapshot is immutable, so every line below sees a single coherent world even
    // if the scanner publishes a new one halfway through.
    var snapshot = Plugin.Scanner.Snapshot;
    var config = Plugin.Configuration;
    var scale = ImGuiHelpers.GlobalScale;
    var hud = config.HudMode;

    // Above every early return below, because the watchdog's question is "is the draw loop still running at
    // all", not "did this frame draw a list".
    Volatile.Write(ref _lastDrawTicks, Environment.TickCount64);

    _hoverFocusSeen = false;

    // Both halves off is a real state and must not render a blank rectangle: an empty window with no title bar
    // is indistinguishable from the plugin having broken.
    if (!config.EnableNearbyList && !config.EnableWatchers)
    {
      DrawBothHalvesOff(hud, scale);
      MeasureDesiredHeight(hud, 0f, 0f);
      ClearHoverFocusIfIdle();
      return;
    }

    // Once per frame, handed to every reader below rather than recomputed by each of them. RowHeight cannot move
    // within a frame — it is two fonts' line heights and the scale, all fixed for its duration — and it is not a
    // field read: the icon-font push behind it is a font-stack round trip and a handful of allocations. Per row
    // that was up to two hundred of them a frame on a crowded map, most of them for rows the cap clips away, to
    // recompute one constant.
    var rowHeight = RowHeight(scale);

    if (!hud)
      DrawToolbar(config, scale);

    RebuildViewIfStale(snapshot);

    // Read once, up here, and hand the same list to both the reserve and the section below. Calling
    // Snapshot() twice would let the two disagree about whether there is a table to leave room for.
    var history = !hud && config.EnableWatchers && config.ShowWatcherHistory
      ? Plugin.WatcherLog.Snapshot()
      : null;

    // Everything below the player table's BOTTOM EDGE, which is what bodyMax has to keep free — it subtracts
    // this figure from a content region measured at the table's TOP.
    //
    // The first term belongs to no block in the list: it is the player table's OWN trailing gap. EndTable's
    // inner EndChild is an item like any other, so ItemSize advances the cursor past the table by its height
    // PLUS ItemSpacing.Y, and that gap is space under the table's bottom edge exactly as the footer is. It used
    // to be spelled GetTextLineHeightWithSpacing() against the footer — the same number, read as the footer's
    // own trailing spacing, which the footer does not have: ItemSize keeps the LAST item's trailing spacing out
    // of the content extent, so the footer costs its height alone and has no spare to lend anyone. Believing in
    // that spare is what let HistoryReserve budget its Dummy bare against it.
    //
    // Every term is now one block's real cost and no two of them cancel. Do not reintroduce an argument that
    // they do: the arithmetic is only ever exercised when the clamp binds, so a term that cancels on paper is a
    // term nothing on screen will contradict until a user with a tall list and a short viewport finds it.
    var reserve = 0f;
    if (!hud)
    {
      reserve += ImGui.GetStyle().ItemSpacing.Y + IconLineHeight();
      if (history is not null)
        reserve += HistoryReserve(scale, history.Count > 0);
    }

    // Both stay 0 unless a table actually draws. MeasureDesiredHeight subtracts one from the other, so every
    // branch that draws no table must contribute no correction.
    var wanted = 0f;
    var drawn = 0f;

    if (config.EnableNearbyList)
    {
      if (_view.Count > 0)
      {
        // The cap applied to the COUNT, through one call, so "exactly at the cap" cannot become two figures that
        // have to be argued into agreeing: the height is monotonic in the row count, so the smaller of the two
        // heights is just the height of the smaller count. Cap+1 is the first row the table scrolls.
        wanted = PlayerTableHeight(Math.Min(_view.Count, VisibleRowCap(config)), !hud, scale, rowHeight);

        // Binds on the frame a crowd arrives, before PreDraw has pinned the taller window — and then binds
        // PERMANENTLY once that pin hits the viewport clamp, which is a state PreDraw deliberately produces
        // rather than an edge case. Either way the table must not overflow the window it is in.
        // MeasureDesiredHeight adds back exactly what this took, or the window could never grow.
        var bodyMax = ImGui.GetContentRegionAvail().Y - reserve;
        drawn = MathF.Min(wanted, MathF.Max(rowHeight, bodyMax));
      }

      // Both zero on the empty state, which draws one line of text and ignores the height entirely.
      DrawPlayerTable(snapshot, config, scale, drawn, hud, rowHeight);
    }
    else
    {
      DrawNearbyListOff(snapshot, config, hud, scale);
    }

    if (history is not null)
    {
      ImGui.Dummy(new Vector2(0, 6f * scale));
      DrawWatcherHistory(history, config, scale);
    }

    if (!hud)
      DrawFooter(snapshot, config, scale);

    MeasureDesiredHeight(hud, wanted, drawn);

    // Cleared here, not in the row loop: "nothing is hovered" is only knowable once every row has had its
    // chance to claim the cursor.
    ClearHoverFocusIfIdle();
  }

  /// <summary>The configured row cap, clamped where it is read. The value round-trips through a JSON file the
  /// user can hand-edit — the same reason the constructor guards SortColumn rather than casting it blindly.</summary>
  private static int VisibleRowCap(Configuration config)
    => Math.Clamp(config.MaxVisibleRows, Configuration.VisibleRowsMin, Configuration.VisibleRowsMax);

  /// <summary>
  /// Records the height this window wants, for <see cref="PreDraw"/> to pin one frame later.
  ///
  /// Measured at the cursor rather than re-derived from the blocks above, so the title bar, the toolbar, the
  /// wrapped empty-state line and the history section cannot drift out of step with a second copy of their own
  /// arithmetic — the trap <see cref="HistoryReserve"/> documents at length. GetCursorPosY is window-space and
  /// so already carries the title bar; WindowPadding.Y closes the bottom. ItemSpacing.Y comes off because
  /// ItemSize leaves the last item's trailing spacing out of the content extent, which is the rule ImGui's own
  /// auto-fit measures by.
  ///
  /// The table is the ONE block this cannot measure. It was handed a height clamped by the very window this
  /// figure pins, so measuring what it drew would latch the window at whatever size a crowd found it and never
  /// let it grow again. wanted minus drawn puts the clamp back: both are 0 wherever no table drew, and the
  /// difference is 0 on every frame the pin already fits. That makes it converge in exactly one frame in both
  /// directions — and it keeps this figure honest in the one state that never converges, a list taller than the
  /// viewport, where PreDraw's clamp holds the window short for good and the difference is what it is short by.
  ///
  /// HUD pins nothing — it is sized by hand and truncates to fit, and the two mechanisms must never both be
  /// live on one window. See the truncation in <see cref="DrawPlayerTable"/>.
  /// </summary>
  private void MeasureDesiredHeight(bool hud, float wanted, float drawn)
  {
    if (hud)
    {
      _desiredHeight = null;
      return;
    }

    var style = ImGui.GetStyle();
    _desiredHeight = ImGui.GetCursorPosY() - style.ItemSpacing.Y + style.WindowPadding.Y + (wanted - drawn);
  }

  /// <summary>
  /// Both halves off. Says so and says where to undo it, rather than rendering the blank rectangle that reads
  /// as the plugin having died. HUD mode gets the short form for the same reason DrawEmptyState does, and keeps
  /// the sentence in a tooltip — dead under HudClickThrough, which is the correct place for it to be dead.
  /// </summary>
  private static void DrawBothHalvesOff(bool hud, float scale)
  {
    const string message = "Both halves off — no list, no watchers. I'm still scanning; turn one back on in " +
                           "settings, Filters tab.";

    UiTheme.Icon(FontAwesomeIcon.PowerOff, UiTheme.Muted);
    ImGui.SameLine(0, 4f * scale);

    if (hud)
    {
      ImGui.TextColored(UiTheme.Muted, "Both halves off.");
      UiTheme.Tooltip(message);
      return;
    }

    UiTheme.TextWrappedColored(UiTheme.Muted, message);
    if (ImGui.SmallButton("Open settings"))
      Plugin.ToggleConfigWindow();
  }

  /// <summary>
  /// The nearby half off. The same job <see cref="DrawBothHalvesOff"/> does — say so, say where to undo it,
  /// never render the blank rectangle that reads as the plugin having died — for the state that is not both-off
  /// and that its guard therefore cannot cover. That guard has already returned by the time this runs, so the
  /// watcher half is on here by construction.
  ///
  /// HUD gets the watcher count instead of the apology, and that is not decoration: it is the watcher half's
  /// ONLY surface in HUD mode. The eye lives in the player table this branch stands in for, and the history
  /// section and the footer both go with the chrome — so a watchers-only HUD had nothing left to submit and
  /// drew an empty, title-bar-less, translucent box, unmoveable under HudLocked, at whatever size it was left.
  /// The sentence keeps its place in a tooltip, dead under HudClickThrough, which is the correct place for it
  /// to be dead.
  /// </summary>
  private static void DrawNearbyListOff(ScentSnapshot snapshot, Configuration config, bool hud, float scale)
  {
    const string message = "Nearby list off. I'm still scanning — turn it back on in settings, Filters tab.";

    if (hud)
    {
      DrawWatcherCount(snapshot, config, scale);

      // One tooltip carrying both facts. DrawWatcherCount deliberately draws none of its own so that this can
      // compose: two Tooltip calls against the same item are two stacked tooltip windows, not one.
      UiTheme.Tooltip(snapshot.Valid ? message : $"{NoseClosed}\n{message}");
      return;
    }

    UiTheme.Icon(FontAwesomeIcon.ListUl, UiTheme.Muted);
    ImGui.SameLine();
    UiTheme.TextWrappedColored(UiTheme.Muted, message);
  }

  private static bool IsBoundByDuty()
    => Plugin.Condition[ConditionFlag.BoundByDuty]
    || Plugin.Condition[ConditionFlag.BoundByDuty56]
    || Plugin.Condition[ConditionFlag.BoundByDuty95];

  private static bool IsInCutscene()
    => Plugin.Condition[ConditionFlag.OccupiedInCutSceneEvent]
    || Plugin.Condition[ConditionFlag.WatchingCutscene]
    || Plugin.Condition[ConditionFlag.WatchingCutscene78];

  /// <summary>
  /// The search term as it actually applies.
  ///
  /// With the search bar hidden the box cannot be cleared, so its contents must stop counting — a filter with
  /// no visible cause hides players for reasons the user cannot see or undo. Routing the signature through
  /// here too means toggling the bar off with text in it re-filters immediately, and toggling it off while
  /// empty costs nothing.
  ///
  /// HUD mode counts as hidden: it drops the whole toolbar, taking the box with it, and the footer that
  /// would have shown 60 nearby against one visible row. So does the nearby half being off, which leaves the
  /// box driving a table <see cref="DrawPlayerTable"/> is never called to draw — a live control every keystroke
  /// of which reaches nothing on screen. Every way the box leaves the screen must come through here, or the term
  /// goes on filtering with nothing left to explain or undo it; and every state named here must be one
  /// <see cref="DrawToolbar"/> also refuses to draw the box in, or the pair drift into a visible box that has
  /// stopped counting.
  ///
  /// EnableNearbyList is already in <see cref="Configuration.FilterSignature"/>, so the view rebuilds on the
  /// frame the toggle flips and the term reapplies with the list.
  /// </summary>
  private string EffectiveSearch
    => Plugin.Configuration is { ShowSearchBar: true, HudMode: false, EnableNearbyList: true }
      ? _search
      : string.Empty;

  /// <summary>
  /// The toolbar. Every control on it but the cog drives the player table, so each one has to answer for itself
  /// in the state where there is no table: <see cref="DrawNearbyListOff"/> stands in for it, and a control still
  /// driving <see cref="_view"/> from above that message is a control nothing on screen responds to. The cog is
  /// the exception and stays unconditional — it is the way back.
  /// </summary>
  private void DrawToolbar(Configuration config, float scale)
  {
    var cogWidth = SettingsButtonWidth();
    var style = ImGui.GetStyle();

    // Gone with the list rather than disabled, unlike the checkbox below: the term is not persisted config with
    // a home in the config window — see the remarks on _search — so there is no setting here to silently vanish
    // and be reported as lost. EffectiveSearch has to blank a term whose box is off screen anyway, so hiding the
    // box is the only option that keeps the two in step. It comes back with its text when the list does.
    if (config.ShowSearchBar && config.EnableNearbyList)
    {
      // The flexible element takes the punishment, which is the whole point of measuring here. With the Races
      // button gone the only fixed items left on this line are the checkbox and the cog, so the box can have
      // whatever is left — and the cog can never be the thing that overruns.
      //
      // Measured, never assumed: GlobalScale and the Dalamud font size are INDEPENDENT user settings, so
      // "Watchers first" can be arbitrarily wide relative to any `N * scale` figure.
      // The marker comes out of the box's budget, measured rather than guessed, and its leading gap with it:
      // HelpMarker opens with its own SameLine(0, 4 * scale), so forgetting that term pushes the cog off the
      // end of the line, where ImGui culls it silently — and a culled cog is the way back to the settings.
      var marker = ImGui.CalcTextSize(SearchHelpGlyph).X + 4f * scale;
      var checkbox = ImGui.CalcTextSize("Watchers first").X + ImGui.GetFrameHeight() + style.ItemInnerSpacing.X;
      var boxWidth = ImGui.GetContentRegionAvail().X - checkbox - cogWidth - marker
        - style.ItemSpacing.X * 2f - 12f * scale;
      ImGui.SetNextItemWidth(MathF.Max(90f * scale, boxWidth));
      ImGui.InputTextWithHint("##search", "Search... (try world:)", ref _search, SearchMaxLength);

      // Amber when the box names a field that does not exist, muted otherwise. An unknown field matches
      // everyone rather than nobody — the safe failure — but it is a SILENT one: the filter looks like it did
      // nothing and the box looks like it worked. This is the only thing on screen that says why.
      if (_queryUnknownFields.Count > 0)
        UiTheme.HelpMarker(UiTheme.Warn, SearchHelpGlyph,
          $"I don't know {(_queryUnknownFields.Count == 1 ? "this" : "these")}: " +
          $"{string.Join(", ", _queryUnknownFields)}:\r\n\r\nIgnored, so everyone still shows.\r\n\r\n" +
          SearchHelp);
      else
        UiTheme.HelpMarker(SearchHelp);

      ImGui.SameLine();
    }

    var watchersFirst = config.WatchersFirst;

    // Dead unless BOTH halves are on, for two separate reasons and either one sufficient. With the watcher half
    // off it sorts by a fact the list is not allowed to show. With the nearby half off there is no list at all:
    // it reorders _view, which nothing renders in that state, under a tooltip promising to float people to the
    // top of something. Disabled rather than hidden, unlike the box above, because this one IS persisted config.
    var sortDead = !config.EnableWatchers || !config.EnableNearbyList;
    if (sortDead)
      ImGui.BeginDisabled();

    if (ImGui.Checkbox("Watchers first", ref watchersFirst))
    {
      config.WatchersFirst = watchersFirst;
      config.Save();
    }

    if (sortDead)
      ImGui.EndDisabled();

    // TooltipEvenIfDisabled, not Tooltip, and only because of the branch above: ImGui records the disabled flag
    // onto the item as it is submitted, so the plain helper answers false forever after and would kill this
    // tooltip in exactly the state whose whole job is saying where the switch is.
    //
    // Both halves off never reaches here — Draw has already returned through DrawBothHalvesOff — so the two
    // disabled arms are mutually exclusive and the order between them decides nothing.
    UiTheme.TooltipEvenIfDisabled(
      !config.EnableWatchers
        ? "Needs the watcher half. Turn it on in settings, Filters tab."
        : !config.EnableNearbyList
          ? "Needs the nearby list. Turn it on in settings, Filters tab."
          : "Float everyone looking at you to the top, on top of whatever column you sorted by.\n" +
            "It is an extra key, not a sort mode — you keep your column sort.");

    // The gap goes in before the measurement below, not after it: GetContentRegionAvail measures from the
    // cursor, and until SameLine puts the cursor back on this line it is already at the start of the next one,
    // where it reports a full window's width of room and nothing would ever wrap.
    ImGui.SameLine(0, 12f * scale);
    DrawSettingsButton(cogWidth);

    ImGui.Dummy(new Vector2(0, 2f * scale));
  }

  /// <summary>The cog's width. Measured under the icon font it is drawn with, because a glyph the text font
  /// does not have measures as a fallback box or as nothing.</summary>
  private static float SettingsButtonWidth()
  {
    Vector2 glyphSize;
    using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
      glyphSize = ImGui.CalcTextSize(FontAwesomeIcon.Cog.ToIconString());

    return glyphSize.X + ImGui.GetStyle().FramePadding.X * 2f;
  }

  /// <summary>
  /// The cog, right-aligned on the current toolbar line. Drawn with the icon font so it reads as the same
  /// control as Dalamud's own plugin cog.
  ///
  /// The Max clamp is a floor against the cursor walking backwards over the widget to its left, NOT a fit
  /// guard: it turns an overrun into a cog placed past the content region, outside the clip rect, which ImGui
  /// culls entirely — silently, since a culled button cannot draw the tooltip that would have explained it.
  /// Whether the line has room is the caller's question to answer, and <see cref="DrawToolbar"/> answers it
  /// with <see cref="SettingsButtonWidth"/>, whose result is passed back in here as <paramref name="width"/>.
  /// </summary>
  private static void DrawSettingsButton(float width)
  {
    ImGui.SameLine(0, 0);
    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0f, ImGui.GetContentRegionAvail().X - width));

    bool clicked;
    using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
      clicked = ImGui.Button(FontAwesomeIcon.Cog.ToIconString());
    if (clicked)
      Plugin.ToggleConfigWindow();
    UiTheme.Tooltip("Settings");
  }

  /// <summary>
  /// Filtering and sorting up to a couple of hundred rows every frame at render rate is pure waste: the data
  /// only changes once per rescan, four times a second by default. Rebuild when the snapshot version moves or
  /// the user changes a filter or a sort key, and render the cached list on every other frame.
  ///
  /// The mark index's revision is folded in HERE rather than inside FilterSignature, so that the revision and
  /// the entries it describes arrive as one immutable reference — see MarksIndex. Hashing it from inside
  /// Configuration would split the two reads across a class boundary, which is the tear the bundling exists to
  /// prevent. It catches what a Count cannot: a note edited, a colour picked, a flag toggled on a record that
  /// already existed, none of which move any count.
  /// </summary>
  private void RebuildViewIfStale(ScentSnapshot snapshot)
  {
    var signature = HashCode.Combine(EffectiveSearch, _sortColumn, _sortAscending,
      Plugin.Configuration.FilterSignature(), Plugin.Marks.Index.Revision);
    if (snapshot.Version == _viewVersion && signature == _viewSignature)
      return;

    _viewVersion = snapshot.Version;
    _viewSignature = signature;
    _view = BuildView(snapshot);
  }

  private List<ScentRow> BuildView(ScentSnapshot snapshot)
  {
    var config = Plugin.Configuration;

    // Parsed here rather than cached beside _search, and that is the cheap correct place: BuildView runs only
    // when RebuildViewIfStale sees the signature move, i.e. once per change to the box — so this is already
    // once-per-keystroke, with no second cache to fall out of step with the first.
    var query = ScentQuery.Parse(EffectiveSearch);
    _queryUnknownFields = query.UnknownFields;

    // One read of the published index for the whole rebuild. The row menu and the editor republish it, and
    // working from whichever whole version we started with is exactly right — the same discipline the two
    // copy-on-write lists this replaced needed, with one reference instead of two that could disagree.
    var marks = Plugin.Marks.Index;

    var filtered = new List<ScentRow>(snapshot.Rows.Count);
    foreach (var row in snapshot.Rows)
    {
      // Ordered so the cheapest predicates reject first; the ignore scan and the substring search are the
      // only two that are not a field read.
      if (row.IsSelf && config.HideSelf)
        continue;
      if (row.IsParty && config.HideParty)
        continue;
      if (row.IsFriend && config.HideFriends)
        continue;
      if (row.IsDead && config.HideDead)
        continue;
      if (config.HideLowLevel && row.Level <= LowLevelThreshold)
        continue;
      if (config.HideAfk && row.OnlineStatusId == Configuration.AFK_ONLINE_STATUS_ID)
        continue;

      // Race 0 is exempt from the mask no matter what it holds. It is not a race the user chose to hide, it is
      // a character whose appearance has not arrived yet — the state of everyone who is still loading in at
      // render distance. Hiding those would blink strangers out of the list as they resolve, which reads as the
      // plugin dropping people at random rather than as a filter doing its job.
      if (row.RaceId != RacePalette.UnknownRaceId && config.IsRaceHidden(row.RaceId))
        continue;
      if (config.MaxDistanceYalms > 0f && row.Distance > config.MaxDistanceYalms)
        continue;

      // Was a linear scan of the ignore list per row, i.e. O(rows x ignored) every rebuild. One frozen-dictionary
      // probe now, and it is still ahead of the search because it is a promise rather than a preference.
      if (marks.IsIgnored(row.Key))
        continue;

      // Last, and the only predicate here that is not a field read. The mark lookup is skipped entirely unless
      // a note: term actually needs one.
      if (query.IsActive && !query.Matches(row, query.NeedsMark ? marks.Find(row.Key) : null))
        continue;

      filtered.Add(row);
    }

    // Truncation is by distance regardless of the display sort. Dropping "whoever sorted last" would silently
    // hide a watcher standing next to you the moment you sorted by name — the one row the plugin exists for.
    if (config.MaxPlayersShown > 0 && filtered.Count > config.MaxPlayersShown)
      filtered = [.. filtered.OrderBy(row => row.Distance).Take(config.MaxPlayersShown)];

    // Watchers-first is a PRIMARY key layered over whatever column is selected, not a mutually exclusive sort
    // mode. That layering is the whole point of the fusion.
    //
    // Gated on EnableWatchers: the watcher half being off must not leave the list sorted by a fact it refuses
    // to display. EnableWatchers is in FilterSignature precisely so this re-sorts on the frame the toggle flips.
    IOrderedEnumerable<ScentRow> ordered = config is { WatchersFirst: true, EnableWatchers: true }
      ? filtered.OrderByDescending(row => row.IsWatching)
      : filtered.OrderBy(_ => 0);

    // Focused-first is a SECOND primary key, layered under watchers-first and over the column sort — the same
    // shape as WatchersFirst and for the same reason. Under, not over: someone looking at you outranks someone
    // you asked to be told about, and a focused watcher satisfies both keys anyway. Costs nothing when nobody is
    // marked, which is every install's default, because the key is constant and OrderBy is stable.
    ordered = marks.Entries.Count > 0
      ? ordered.ThenByDescending(row => marks.IsFocused(row.Key))
      : ordered;

    ordered = _sortColumn switch
    {
      ScentColumn.Watching => Then(ordered, row => row.IsWatching),

      // Keyed on whichever string DrawRow will actually render. Sorting the abbreviation while the cell shows
      // the full name puts "black mage" above "bard" under an ascending arrow — an order that is not sorted
      // by anything on screen. UseJobAbbreviations is already in FilterSignature, so the cached view
      // invalidates the frame the toggle flips; that hash input has been dead weight until now.
      ScentColumn.Job => Then(ordered, row => config.UseJobAbbreviations ? row.JobAbbreviation : row.JobName),

      // Keyed on the rendered string, like Job above and for the same reason. RaceId would sort by the sheet's
      // own row order — Hyur, Elezen, Lalafell — which is neither alphabetical nor anything the eight cells on
      // screen show, so the header arrow would be claiming an order that is not there.
      ScentColumn.Race => Then(ordered, row => row.RaceName),
      ScentColumn.Level => Then(ordered, row => row.Level),
      ScentColumn.World => Then(ordered, row => row.HomeWorldName),
      ScentColumn.Company => Then(ordered, row => row.CompanyTag),
      ScentColumn.Distance => Then(ordered, row => row.Distance),

      // Keyed on the glyph bucket, off the same index this rebuild read once. See MarkSortKey.
      ScentColumn.Mark => Then(ordered, row => MarkSortKey(marks.Find(row.Key))),
      _ => Then(ordered, row => row.Name),
    };

    // Name is the final tiebreak on every sort. Without it, equal keys — everyone at level 100 — leave rows
    // free to swap places between snapshots as players move, and the list visibly jitters four times a second.
    var result = new List<ScentRow>(ordered.ThenBy(row => row.Name, StringComparer.Ordinal));

    // Rebuilt with the view, not per frame and not at scan time; see the remarks on _focusedKeys.
    _focusedKeys = [];
    _shownOthers = 0;
    foreach (var row in result)
    {
      if (!row.IsSelf)
        _shownOthers++;
      if (marks.IsFocused(row.Key))
        _focusedKeys.Add(row.Key);
    }

    return result;
  }

  /// <summary>
  /// Applies the current sort direction to a secondary key. Exists because IOrderedEnumerable has no
  /// direction-parameterised ThenBy, and spelling the ternary out at all seven call sites is how one of them
  /// eventually ends up sorting the wrong way.
  /// </summary>
  private IOrderedEnumerable<ScentRow> Then<TKey>(IOrderedEnumerable<ScentRow> source, Func<ScentRow, TKey> key)
    => _sortAscending ? source.ThenBy(key) : source.ThenByDescending(key);

  /// <summary>
  /// The columns the table registers, in TableSetupColumn order.
  ///
  /// The single source of truth for both halves of the column round-trip: DrawPlayerTable walks it to register,
  /// ApplyColumnVisibility walks the same list to read the ticks back. ImGui addresses a column by its index
  /// and offers no lookup by user id, so any two lists would eventually disagree by one and persist the wrong
  /// column's tick — which is exactly what the old RaceColumnIndex constant was one edit away from.
  ///
  /// EVERY column, in EVERY state, and the COUNT is the load-bearing part. The eye used to leave this list with
  /// the watcher half, which changed the count handed to BeginTable — and BeginTable answers a changed count by
  /// re-initialising the table and copying the surviving columns' state BY INDEX, so every column inherited its
  /// left neighbour's: the eye's onto Name, Name's onto Job, hidden Race's onto Lv. TableSetupColumn can only
  /// ever push a column's tick OFF (DefaultHide) and never back on, so nothing put Lv back, and
  /// ApplyColumnVisibility read "Lv is hidden" off the table and saved it as the user's own choice — one more
  /// column eaten by every toggle, persisted, with no cause the user could see. The eye goes off with
  /// <see cref="IsColumnDisabled"/> instead, which leaves the count alone. Do not make this vary again.
  ///
  /// Reordering moves a column's display order, never its index, so the header stays free to drag.
  /// </summary>
  /// <remarks>
  /// APPENDED, never inserted. BeginTable copies column state BY INDEX when the count changes, so a column added
  /// at the end leaves indices 0..7 untouched and only the new one gets fresh state — benign. Inserted in the
  /// middle it would shift every column's state onto its neighbour, which is the exact failure the paragraph
  /// above describes. Mark reads last on the row; it is the least urgent thing on it.
  /// </remarks>
  private static readonly ScentColumn[] ColumnLayout =
  [
    ScentColumn.Watching, ScentColumn.Name, ScentColumn.Job, ScentColumn.Race,
    ScentColumn.Level, ScentColumn.World, ScentColumn.Company, ScentColumn.Distance,
    ScentColumn.Mark,
  ];

  /// <summary>Columns that cannot be hidden, and therefore can never own a bit in HiddenColumnMask: the list
  /// with no names in it is not a list.</summary>
  private static bool IsColumnFixed(ScentColumn column)
    => column is ScentColumn.Name or ScentColumn.Watching;

  /// <summary>
  /// Columns switched off wholesale by a half toggle rather than by the user's own tick — the eye, and only the
  /// eye, because the watcher half being off must not leave the list showing a fact it refuses to admit.
  ///
  /// Any column that ever answers true here MUST also be <see cref="IsColumnFixed"/>. ImGui reports a Disabled
  /// column as not enabled, exactly as it reports one the user unticked, and the two are indistinguishable from
  /// <see cref="ApplyColumnVisibility"/> — which skips fixed columns rather than reading them, and that skip is
  /// the only thing keeping a half toggle out of HiddenColumnMask. Record it there and turning the half back on
  /// would leave the column hidden, permanently, with nothing on screen to say why.
  /// </summary>
  private static bool IsColumnDisabled(ScentColumn column, Configuration config)
    => column == ScentColumn.Watching && !config.EnableWatchers;

  /// <summary>
  /// The CellPadding the player table draws with, stated here rather than read off the live style.
  ///
  /// One source of truth for two readers that must agree exactly: the push in <see cref="DrawPlayerTable"/>,
  /// and <see cref="RowHeight"/>, which is called both inside that push and outside it — from <see cref="Draw"/>,
  /// before the table exists. ImGui.GetStyle() answers differently either side of the push, so a RowHeight that
  /// read the style would size the window off one number and pin the rows with another, and that difference
  /// times sixty rows is the slab this rework exists to delete.
  ///
  /// Y is the dense half: 2 is Dalamud's default and 1 leaves one pixel of air over a ~17px glyph, which is as
  /// tight as a row goes before the text touches the stripe. X stays at Dalamud's 4 — the columns want the
  /// horizontal room and nothing about vertical density depends on it. These are the numbers HUD mode already
  /// used; "compact and dense" is what HUD was doing, so the branch between them is gone.
  /// </summary>
  private static Vector2 CellPadding(float scale) => new(4f * scale, 1f * scale);

  /// <summary>
  /// One row's height, pinned rather than inherited.
  ///
  /// The eye cell draws in the icon font and every other cell draws in the text font, and the two do not share
  /// a line height — so an unpinned row is a different height depending on whether that player happens to be
  /// looking at you, and the list re-flows vertically as people glance at you and die. In a list whose entire
  /// job is being glanced at. It also makes PlayerTableHeight an exact figure rather than an approximation.
  ///
  /// Call this ONCE per frame and pass the answer down; see <see cref="Draw"/>. It is frame-invariant — the two
  /// fonts and the scale cannot move inside one frame, and <see cref="CellPadding"/> is a literal rather than a
  /// style read for the reasons on it — but it is not cheap: the push is a font-stack round trip and several
  /// allocations, so a call per row is a couple of hundred of them a frame on a crowded map, every one of them
  /// recomputing the same number.
  /// </summary>
  private static float RowHeight(float scale)
  {
    Vector2 icon;
    using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
      icon = ImGui.CalcTextSize(FontAwesomeIcon.Eye.ToIconString());

    return MathF.Max(ImGui.GetTextLineHeight(), icon.Y) + CellPadding(scale).Y * 2f;
  }

  /// <summary>
  /// What the table would take if it drew <paramref name="rows"/> rows and no more. The header measures the way
  /// TableGetHeaderRowHeight does — one text line plus CellPadding.Y twice, evaluated inside the same push, which
  /// is why it takes the figure from <see cref="CellPadding"/> and not from the style. The trailing pixels are the
  /// outer border line, which is drawn outside the rows' own extents and stays a hairline at any scale.
  ///
  /// Strictly increasing in <paramref name="rows"/>, which is what lets <see cref="Draw"/> apply the row cap to
  /// the count rather than to two heights. <paramref name="rowHeight"/> is <see cref="RowHeight"/>'s answer for
  /// this frame, passed in rather than re-measured for the reasons on it.
  /// </summary>
  private static float PlayerTableHeight(int rows, bool header, float scale, float rowHeight)
    => (header ? ImGui.GetTextLineHeight() + CellPadding(scale).Y * 2f : 0f)
     + rows * rowHeight + 2f;

  /// <summary>
  /// One line's height with an icon on it: the taller of the icon font's line and the text font's — the same
  /// Max, and the same reason, as <see cref="RowHeight"/>. Both blocks <see cref="Draw"/> reserves room for are
  /// this shape, every arm of them: <see cref="DrawFooter"/>, and the line the history section stands in for its
  /// table with.
  ///
  /// Measured rather than assumed to be one text line, because the reserve is exact and the two fonts do not
  /// share a line height — the whole reason RowHeight exists. CalcTextSize answers with the font's line height
  /// and not the glyph's ink, so which FontAwesome glyph is measured here does not matter and no caller has to
  /// pass one.
  ///
  /// ONE line. A message that wraps to two is 2 * GetTextLineHeight and this is short by one — see the remarks
  /// in <see cref="DrawWatcherHistory"/> on why those strings stay short.
  /// </summary>
  private static float IconLineHeight()
  {
    Vector2 icon;
    using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
      icon = ImGui.CalcTextSize(FontAwesomeIcon.Users.ToIconString());

    return MathF.Max(ImGui.GetTextLineHeight(), icon.Y);
  }

  private void DrawPlayerTable(ScentSnapshot snapshot, Configuration config, float scale, float height, bool hud,
    float rowHeight)
  {
    // One read for the whole table, handed down to every row — the same discipline as rowHeight above it, and
    // for a sharper reason: the render thread is this store's own writer, so a tick in the editor or the row
    // menu republishes the index MID-FRAME. Reading it per row per cell would let one row's colour disagree
    // with the next row's, and with the glyph in its own Mark cell.
    var marks = Plugin.Marks.Index;

    if (_view.Count == 0)
    {
      DrawEmptyState(snapshot, config, hud, scale);
      return;
    }

    var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.NoSavedSettings | ImGuiTableFlags.SizingStretchProp
              | ImGuiTableFlags.Sortable | ImGuiTableFlags.Hideable | ImGuiTableFlags.Reorderable;

    // Hideable and Reorderable stay in HUD mode even with no header to right-click, and this is not laziness.
    // TableUpdateLayout force-enables every column of a table that is not Hideable — so dropping the flag would
    // un-hide the user's hidden columns the moment HUD came on, and ApplyColumnVisibility would then persist
    // that as their choice. Resizable is the one that is safe to drop, and the one worth dropping: it is the
    // only one with a live hit-test, and invisible column-resize handles in a chrome-less overlay steal the
    // window drag. Borders and ScrollY go for the reasons at their own lines below.
    //
    // BordersOuter | BordersInnerV is Borders minus BordersInnerH, and dropping that one flag is the whole of
    // the density work. The rule between every row is drawn in TableBorderLight, which is far louder than the
    // 0.06 alpha zebra stripe RowBg already draws underneath it — so the stripes read as absent while the rules
    // read as clutter, and deleting the rules reveals the striping that was always there. The outer frame and
    // the column separators stay: the separators are also the visible affordance for Resizable's drag zones.
    // Borders are drawn on cell boundaries and cost no layout height, so this is free vertically.
    if (!hud)
      flags |= ImGuiTableFlags.BordersOuter | ImGuiTableFlags.BordersInnerV
             | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable;

    var columns = ColumnLayout;

    // Before BeginTable sees the sort flags: a column this frame switches off cannot hold the sort, and ImGui
    // answers one that does by stripping it during sanitize and substituting its own pick — which ApplySortSpecs
    // would dutifully save over the user's choice. Reached only by turning the watcher half off while sorted by
    // the eye, which is a two-click sequence, not a hand-edited config.
    ReconcileSortColumn(config, snapshot);

    // Pushed before BeginTable and popped after EndTable — CellPadding is read at BeginTable and again per row.
    // Unconditional now: both modes take the same dense padding, so there is no null arm left to leak through.
    // A using, not a Push/Pop pair, because the BeginTable below returns early on false and that path must pop
    // too.
    using var pad = ImRaii.PushStyle(ImGuiStyleVar.CellPadding, CellPadding(scale));

    // The height means two different things either side of ScrollY, and only one of them is the one we want.
    //
    // With ScrollY (GUI) a positive outer_size.y is an EXACT height, which is what makes the Min() in Draw hug
    // the content instead of parking a bordered void under one row.
    //
    // Without ScrollY (HUD) the very same number is a MINIMUM the table auto-extends from — so handing it
    // `height` would pin the table at the full available height even while the truncation below is drawing
    // three rows of sixty-one, and the "+N more" line appended after EndTable would land past the window's
    // bottom edge and be clipped: invisible, in precisely the case it exists to report. 0 is "no minimum, fit
    // the content", so the table ends where the last row does and the line lands under it.
    if (!ImGui.BeginTable("##scentTable", columns.Length, flags, new Vector2(0f, hud ? 0f : height)))
      return;

    // 0 in HUD: this freezes the first ROW, which is the header only because there is one. With the header
    // gone it would pin whoever sorts first and scroll everyone else under them.
    ImGui.TableSetupScrollFreeze(0, hud ? 0 : 1);

    foreach (var column in columns)
      SetupColumn(column, config, scale, rowHeight);

    if (!hud)
      ImGui.TableHeadersRow();

    // ORDER IS LOAD-BEARING, and only one of the two reasons is obvious.
    //
    // ApplySortSpecs first because a sort change must land this frame rather than showing one frame of the old
    // order under a header arrow that already claims the new one. And ApplyColumnVisibility second because it
    // requires a locked layout — TableSetupColumn overwrites every column's flags each frame and it is
    // TableUpdateLayout that puts the status bits back, so asked any earlier every column answers that it is
    // disabled, and this would write that away as the user's choice on the first frame, every frame.
    //
    // In HUD mode TableHeadersRow is gone and ImGui.TableGetSortSpecs is the ONLY thing left that locks the
    // layout (it calls TableUpdateLayout when the layout is not locked). That is why Sortable stays in both
    // modes and why these two cannot be swapped: TableGetSortSpecs returns NULL before the lock if Sortable is
    // absent, ApplySortSpecs would short-circuit on specs.IsNull, the layout would never lock, and
    // ApplyColumnVisibility would silently persist every column as hidden.
    ApplySortSpecs(snapshot);
    ApplyColumnVisibility(config, columns);

    // HUD has no ScrollY, so the table draws exactly its rows and anything past the window's edge is simply
    // gone. Show what fits and admit the rest: under HudClickThrough (NoInputs) a scrollbar cannot be dragged
    // AND the wheel is dead, so overflow rows would be permanently unreachable behind a decorative scrollbar.
    //
    // This truncation follows the DISPLAY sort, which is the thing BuildView argues at length against for
    // MaxPlayersShown ("dropping whoever sorted last would silently hide a watcher standing next to you"). It
    // is safe here for two reasons and only these two: WatchersFirst defaults on and floats every watcher above
    // the cut, and "+N more" makes the truncation visible, which MaxPlayersShown never does. If either of those
    // stops being true, this has to go back to distance.
    var rows = _view.Count;
    if (hud)
    {
      var fits = (int)MathF.Floor(ImGui.GetContentRegionAvail().Y / rowHeight);
      if (rows > fits)
        rows = Math.Max(1, fits - 1);  // one line spent on "+N more"
    }

    for (var i = 0; i < rows; i++)
      DrawRow(_view[i], config, scale, columns, rowHeight, marks, hud);

    ImGui.EndTable();

    if (hud && _view.Count > rows)
      ImGui.TextColored(UiTheme.Muted, $"+{_view.Count - rows} more");
  }

  /// <summary>
  /// Registers one column, honouring the persisted tick.
  ///
  /// Never hidden while it owns the sort, whatever the mask says, and this guarantee now covers EVERY column
  /// rather than Race alone. ImGui strips a disabled column's sort order during sanitize and falls back to its
  /// own pick — the first sortable column, ascending — which ApplySortSpecs would then dutifully save over the
  /// user's choice: exactly what SortFlagsFor exists to prevent, reached by the one route its flags cannot
  /// cover, because DefaultSort on a disabled column is inert. ApplyColumnVisibility keeps the mask and the
  /// ticks in step, so only a hand-edited config arrives here with them disagreeing.
  /// </summary>
  private void SetupColumn(ScentColumn column, Configuration config, float scale, float rowHeight)
  {
    var flags = SortFlagsFor(column);

    // Disabled, never absent — see ColumnLayout for what dropping a column costs. It beats the NoHide below
    // rather than fighting it: ImGui computes IsEnabled as "IsUserEnabled AND not Disabled", and NoHide only
    // forces the first of those. So the eye leaves the table and the header's right-click menu, which skips
    // Disabled columns, while the column count stays put.
    if (IsColumnDisabled(column, config))
      flags |= ImGuiTableColumnFlags.Disabled;

    if (IsColumnFixed(column))
      flags |= ImGuiTableColumnFlags.NoHide;
    else if (config.IsColumnHidden((uint)column) && _sortColumn != column)
      flags |= ImGuiTableColumnFlags.DefaultHide;

    switch (column)
    {
      // "Watching", not "##eye". TableSetupColumn stores a label starting with '#' verbatim — its only guard is
      // label[0] == 0 — and TableDrawContextMenu hands that string straight to MenuItem, which renders
      // everything before the "##" and uses the rest as an ID. Everything before it is nothing, so the header's
      // right-click menu grew a ticked, greyed-out entry with no text at all: the "invisible tab".
      // NoHeaderLabel suppresses the label on the path into TableHeader ONLY; TableDrawContextMenu calls
      // TableGetColumnName independently and is unaffected. So the header cell stays blank exactly as it looks
      // today, and the menu reads "Watching" — greyed and ticked, which is honest, because NoHide means it is.
      // The column's ImGui ID changes from "eye"; inert, because the table sets NoSavedSettings and
      // TableHeadersRow scopes each header with PushID(column_n) anyway.
      case ScentColumn.Watching:
        ImGui.TableSetupColumn("Watching",
          ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoHeaderLabel | flags,
          WatchingColumnWidth(scale), (uint)column);
        break;

      case ScentColumn.Name:
        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch | flags, 0f, (uint)column);
        break;

      // Widened by exactly what the icon takes when it is on, measured rather than guessed. A fixed column
      // does not grow to fit its content — it clips it, with no ellipsis to admit it — so an icon added to a
      // width chosen for "WAR" would silently eat the text it sits beside.
      //
      // NoResize is what makes that width take effect, and it is the only column that carries it. The table is
      // Resizable, and ImGui honours TableSetupColumn's width ONLY while a resizable column is initialising —
      // after that the width is latched and this argument is ignored. So ticking the icons on would widen
      // nothing and the abbreviation would clip, permanently, recoverable only by restarting the game. NoResize
      // opts this one column out of that latch, at the cost of a drag handle on a 46px column that has nothing
      // to drag. The other eight stay resizable.
      case ScentColumn.Job:
        ImGui.TableSetupColumn("Job",
          ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize | flags,
          46f * scale + (config.ShowJobIcons ? JobIconSize(scale, rowHeight) + 4f * scale : 0f), (uint)column);
        break;

      case ScentColumn.Race:
        ImGui.TableSetupColumn("Race", ImGuiTableColumnFlags.WidthFixed | flags, 70f * scale, (uint)column);
        break;

      case ScentColumn.Level:
        ImGui.TableSetupColumn("Lv", ImGuiTableColumnFlags.WidthFixed | flags, 30f * scale, (uint)column);
        break;

      case ScentColumn.World:
        ImGui.TableSetupColumn("World", ImGuiTableColumnFlags.WidthFixed | flags, 90f * scale, (uint)column);
        break;

      case ScentColumn.Company:
        ImGui.TableSetupColumn("FC", ImGuiTableColumnFlags.WidthFixed | flags, 60f * scale, (uint)column);
        break;

      // 62, not 52: "128.6y" at a large Dalamud font clipped silently at the old width, and there is no
      // ellipsis on a table cell to say so.
      case ScentColumn.Distance:
        ImGui.TableSetupColumn("Dist", ImGuiTableColumnFlags.WidthFixed | flags, 62f * scale, (uint)column);
        break;

      // Measured under the icon font like the eye, and for the same reason: GlobalScale and the Dalamud font
      // size are independent settings, so any literal here is a width that shrinks relative to its own glyph.
      // NoHeaderLabel for the eye's reason too — the glyph needs no title over it, and the header's right-click
      // menu still reads "Mark" via TableGetColumnName.
      case ScentColumn.Mark:
        ImGui.TableSetupColumn("Mark",
          ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoHeaderLabel | flags,
          MarkColumnWidth(scale), (uint)column);
        break;
    }
  }

  /// <summary>The mark column's width; measured under the icon font, exactly like
  /// <see cref="WatchingColumnWidth"/> and for the reason stated there.</summary>
  private static float MarkColumnWidth(float scale)
  {
    Vector2 glyph;
    using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
      glyph = ImGui.CalcTextSize(FontAwesomeIcon.StickyNote.ToIconString());

    return glyph.X + CellPadding(scale).X * 2f;
  }

  /// <summary>
  /// The eye column's width: the glyph itself plus the cell padding either side, measured under the icon font
  /// it is drawn with — a glyph the text font does not have measures as a fallback box or as nothing.
  ///
  /// Measured, never a literal, for the reason <see cref="DrawToolbar"/> measures the cog: GlobalScale and the
  /// Dalamud font size are INDEPENDENT user settings, so the old `22f * scale` was a width that shrank relative
  /// to its own glyph as the font grew, and a table cell has no ellipsis to admit it clipped. This is the Dist
  /// column's "62, not 52" with no fixed number left to get wrong.
  /// </summary>
  private static float WatchingColumnWidth(float scale)
  {
    Vector2 glyph;
    using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
      glyph = ImGui.CalcTextSize(FontAwesomeIcon.Eye.ToIconString());

    return glyph.X + CellPadding(scale).X * 2f;
  }

  /// <summary>
  /// Marks the persisted sort column as the table's default.
  ///
  /// ImGui picks a Sortable table's initial sort from these flags on the frame it first builds the table, and
  /// then reports that choice back as a dirty spec. Without this it would hand us its own pick — the first
  /// sortable column, ascending — and <see cref="ApplySortSpecs"/> would dutifully save it over whatever the
  /// user had chosen.
  /// </summary>
  private ImGuiTableColumnFlags SortFlagsFor(ScentColumn column)
  {
    if (column != _sortColumn)
      return ImGuiTableColumnFlags.None;

    return ImGuiTableColumnFlags.DefaultSort
      | (_sortAscending ? ImGuiTableColumnFlags.PreferSortAscending : ImGuiTableColumnFlags.PreferSortDescending);
  }

  /// <summary>
  /// Moves the sort off a column this frame switches off.
  ///
  /// Only the eye can be switched off, and only by the user turning the watcher half off while sorted by it.
  /// Left alone, ImGui's sanitize pass strips a disabled column's sort order and falls back to its own pick,
  /// which ApplySortSpecs would then save — so the sort would silently become "whatever was leftmost" and STAY
  /// there after the half came back on. Falling back to Name, and persisting it, leaves the user somewhere they
  /// can see, and lands this frame rather than one frame after ImGui notices.
  ///
  /// Asks <see cref="IsColumnDisabled"/> rather than testing membership of <see cref="ColumnLayout"/>: the
  /// layout is constant, so every column is always in it and a membership test answers true forever.
  ///
  /// Runs before BeginTable because DefaultSort is read when the table is built.
  /// </summary>
  private void ReconcileSortColumn(Configuration config, ScentSnapshot snapshot)
  {
    if (!IsColumnDisabled(_sortColumn, config))
      return;

    _sortColumn = ScentColumn.Name;
    _sortAscending = true;
    Plugin.Configuration.SortColumn = (uint)ScentColumn.Name;
    Plugin.Configuration.SortAscending = true;
    Plugin.Configuration.Save();
    RebuildViewIfStale(snapshot);
  }

  /// <summary>
  /// Persists which columns are on screen, so the header menu's ticks outlive the session.
  ///
  /// The other half of the persistence NoSavedSettings forces this window to do by hand; see
  /// <see cref="Configuration.HiddenColumnMask"/>. Generalised from the Race-only special case it replaces:
  /// every hideable column now survives a relaunch, which is what the old code's own comment admitted it was
  /// not doing ("the rest come back visible, where the user left them" was only true because nothing had ever
  /// moved them).
  ///
  /// Must run with the table's layout locked — see the ordering note in DrawPlayerTable, which is the one place
  /// that can get this wrong and the one place it is explained.
  ///
  /// Fixed columns are skipped rather than read, and the skip is load-bearing twice over. NoHide makes them
  /// answer IsEnabled forever, so recording them is a bit that can only ever be 0 — and the eye is fixed AND the
  /// one column a half toggle can switch off, so the skip is also the only thing standing between "the watcher
  /// half is off" and that being written down as "the user unticked the eye". See <see cref="IsColumnDisabled"/>.
  ///
  /// Every column is in every frame's layout — see <see cref="ColumnLayout"/> — so this reads a complete answer
  /// and the index it reads at is the index the column was registered at.
  /// </summary>
  private static void ApplyColumnVisibility(Configuration config, ScentColumn[] columns)
  {
    var mask = config.HiddenColumnMask;
    for (var i = 0; i < columns.Length; i++)
    {
      var column = columns[i];
      if (IsColumnFixed(column))
        continue;

      var shown = (ImGui.TableGetColumnFlags(i) & ImGuiTableColumnFlags.IsEnabled) != 0;
      if (shown)
        mask &= ~(1u << (int)column);
      else
        mask |= 1u << (int)column;
    }

    // On change only. The table reports its ticks every single frame, and an unconditional save would rewrite
    // the config file at render rate.
    if (mask == config.HiddenColumnMask)
      return;

    config.HiddenColumnMask = mask;
    config.Save();
  }

  private void ApplySortSpecs(ScentSnapshot snapshot)
  {
    var specs = ImGui.TableGetSortSpecs();
    if (specs.IsNull || !specs.SpecsDirty)
      return;

    if (specs.SpecsCount > 0)
    {
      var column = (ScentColumn)specs.Specs[0].ColumnUserID;
      var ascending = specs.Specs[0].SortDirection != ImGuiSortDirection.Descending;

      // Only on an actual change. The table hands us its dirty specs on the first frame it exists, so an
      // unconditional save would rewrite the config file every single time the window opened.
      if (Enum.IsDefined(column) && (column != _sortColumn || ascending != _sortAscending))
      {
        _sortColumn = column;
        _sortAscending = ascending;
        Plugin.Configuration.SortColumn = (uint)column;
        Plugin.Configuration.SortAscending = ascending;
        Plugin.Configuration.Save();

        // The signature moved, so re-sort now rather than render one frame of the old order under a header
        // arrow that already claims the new one.
        RebuildViewIfStale(snapshot);
      }
    }

    // Must be cleared, or ImGui re-raises it and this runs every frame forever.
    specs.SpecsDirty = false;
  }

  private void DrawRow(ScentRow row, Configuration config, float scale, ScentColumn[] columns, float rowHeight,
    MarksIndex marks, bool hud)
  {
    // Height pinned, not inherited. The eye cell is icon-font and the rest are text-font, and the two line
    // heights do not match — so an unpinned row changed height depending on whether that player was looking at
    // you, and the list re-flowed as people glanced and died. row_min_height is the whole row including cell
    // padding, which is exactly what RowHeight() measures — measured once for the frame and passed down, since
    // it is the same number for every row and an icon-font push is not free.
    ImGui.TableNextRow(ImGuiTableRowFlags.None, rowHeight);

    var focused = _focusedKeys.Count > 0 && _focusedKeys.Contains(row.Key);

    // One probe for the whole row, off the index this frame was handed. Every cell below that cares about the
    // mark reads THIS, so the name colour, the row tint and the Mark glyph can never describe three different
    // versions of the same record.
    var mark = marks.Find(row.Key);

    // RowBg1 sits above the zebra stripe, so the tint is visible on both stripe phases; on RowBg0 it would
    // disappear entirely under every other row.
    //
    // Watcher tint wins on a row that is both. Two washes on one row average into a colour that is neither, and
    // of the two facts, "this one is looking at you" is the one worth the pixels.
    if (row.IsWatching && config.HighlightWatcherRow && config.EnableWatchers)
    {
      var tint = config.ColorWatcher;
      tint.W = config.WatcherRowTintAlpha;
      ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, ImGui.GetColorU32(tint));
    }
    else if (focused && config.HighlightFocusedRow)
    {
      // The marked colour folds in here too, for the same reason and with the same default; see the name-colour
      // chain below. Inserted into the existing if/else-if rather than added as a third TableSetBgColor call —
      // two washes on one row average into a colour that is neither.
      var tint = mark?.Color ?? config.ColorFocused;
      tint.W = config.FocusedRowTintAlpha;
      ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, ImGui.GetColorU32(tint));
    }

    // Row-scoped ID so two players never share a popup or a widget. Truncating the id is safe: the low half
    // is what distinguishes objects, and this only has to be unique among one frame's rows.
    ImGui.PushID(unchecked((int)row.GameObjectId));

    // Driven by the same list the table registered, so the cells cannot drift out of step with the columns.
    // Hidden and disabled columns still get their TableNextColumn: ImGui addresses cells by index, and skipping
    // one would shift every cell after it into the wrong column.
    foreach (var column in columns)
    {
      switch (column)
      {
        // The eye — the whole reason this plugin exists as one window instead of two.
        case ScentColumn.Watching:
          ImGui.TableNextColumn();

          // EnableWatchers as well as IsWatching. The eye's column stays registered while the half is off — see
          // ColumnLayout — and its cell swallows what is submitted into it only because ImGui happens to set
          // SkipItems on a disabled column. Not worth an icon-font push per watcher to lean on that.
          if (row.IsWatching && config.EnableWatchers)
          {
            UiTheme.Icon(FontAwesomeIcon.Eye, config.ColorWatcher);
            UiTheme.Tooltip("This one is targeting you.");
          }

          break;

        case ScentColumn.Name:
        {
          ImGui.TableNextColumn();

          // Focus sits ABOVE the relationship colours and BELOW nothing: it is the one colour on this row the
          // user chose per player, by hand. The eye column already carries watcher state, so the name colour
          // stays free of it and no precedence fight arises between the two.
          //
          // A per-player colour FOLDS INTO this slot rather than adding a level to the chain: ColorFocused is
          // the default for the slot, and a marked player's own colour replaces it. It does not become a sixth
          // contender — "a fourth warm colour on one line is four things shouting", and this row already has
          // four. A colour on an unfocused player therefore shows nothing here, deliberately; the editor says so.
          var nameColor = focused ? mark?.Color ?? config.ColorFocused
            : row.IsParty ? config.ColorParty
            : row.IsFriend ? config.ColorFriend
            : row.IsSameFreeCompany ? config.ColorSameFc
            : config.ColorDefault;
          ImGui.TextColored(nameColor, row.Name);

          // Captured before the popup call: an open popup runs Begin/End, which is free to clobber ImGui's
          // last-item state, and the hover answer is still needed after it.
          var nameHovered = ImGui.IsItemHovered();

          // Attached to the name, and attached HERE — before the skull below becomes the last item and shrinks
          // a dead player's right-click target down to one small icon.
          if (ImGui.BeginPopupContextItem("##rowctx"))
          {
            DrawRowContextMenu(row, focused, hud);
            ImGui.EndPopup();
          }

          if (row.IsDead)
          {
            ImGui.SameLine(0, 4f * scale);
            UiTheme.Icon(FontAwesomeIcon.Skull, UiTheme.Muted);
          }

          if (config.FocusTargetOnHover && nameHovered)
            HoverFocus(row.GameObjectId);

          break;
        }

        case ScentColumn.Job:
          ImGui.TableNextColumn();
          DrawJobIcon(row, config, scale, rowHeight);
          ImGui.TextColored(JobPalette.JobColor(row.JobId),
            config.UseJobAbbreviations ? row.JobAbbreviation : row.JobName);
          break;

        case ScentColumn.Race:
          ImGui.TableNextColumn();
          ImGui.TextUnformatted(row.RaceName);
          break;

        case ScentColumn.Level:
          ImGui.TableNextColumn();
          TextRight(row.Level.ToString());
          break;

        case ScentColumn.World:
          ImGui.TableNextColumn();
          ImGui.TextUnformatted(row.HomeWorldName);
          break;

        case ScentColumn.Company:
          ImGui.TableNextColumn();
          ImGui.TextUnformatted(row.CompanyTag);
          break;

        case ScentColumn.Distance:
          ImGui.TableNextColumn();
          TextRight($"{row.Distance:F1}y");
          break;

        // Probed here, per row, per frame, straight off the published index — never mirrored into a set beside
        // the view the way _focusedKeys is. That pattern is for a render-only lookup; this one is ALSO the sort
        // key, and a key that disagreed with the glyph beside it would sort by something not on screen. One
        // read, one truth, and a frozen-dictionary probe is cheaper than the mirror would be.
        case ScentColumn.Mark:
        {
          ImGui.TableNextColumn();
          if (mark is null)
            break;

          // Note beats ignore beats focus — most specific first, and only one glyph either way. Focus already
          // says itself in the name colour, so it only claims this cell when nothing louder is on the record.
          // Null when the record has nothing to show on a row; see MarkGlyph.
          if (MarkGlyph(mark, config) is not { } badge)
            break;

          UiTheme.Icon(badge.Glyph, badge.Color);
          UiTheme.Tooltip(mark.HasNote ? $"{badge.Tip}\n{mark.Note}" : badge.Tip);
          break;
        }
      }
    }

    ImGui.PopID();
  }

  /// <summary>
  /// The game's own icon for a job, drawn before its name. Draws nothing at all if the texture is not ready.
  ///
  /// NO CACHE, NO DISPOSE, NO LIFETIME. ISharedImmediateTexture is owned by Dalamud, and TryGetWrap hands back
  /// a wrap valid for the rest of the frame — so the correct amount of bookkeeping here is none. The prior art
  /// ships no textures at all and instead has a settings SCREEN whose only job is to populate a dropdown on a
  /// different settings screen: you tick FontAwesome glyphs into a pool, then pick from that pool per player.
  /// None of that has to exist, because the icon is DERIVED from a field the row already carries.
  ///
  /// TryGetFromGameIcon, never GetFromGameIcon: the latter resolves the path eagerly and THROWS on an icon that
  /// does not exist, which would put an exception in a table cell for a decoration. The Try form is documented
  /// not to throw, and that is what makes the undocumented 62100 base safe to ship — a wrong id draws nothing
  /// and the job's name is still right there.
  ///
  /// 62100 is the job-icon block; job 0 is "not loaded yet", which has no icon and no business claiming one.
  /// </summary>
  private static void DrawJobIcon(ScentRow row, Configuration config, float scale, float rowHeight)
  {
    if (!config.ShowJobIcons || row.JobId == 0)
      return;

    var size = JobIconSize(scale, rowHeight);

    // The failure path RESERVES THE SPACE rather than collapsing it. The column has already paid for the icon's
    // width — SetupColumn asks only whether the feature is on, never whether a texture arrived — so returning
    // early would put this one name hard against the cell's left edge while every other name in the column sits
    // indented. Two ways that happens: for a beat while a texture streams in, and FOREVER for any job id with
    // no icon behind it, which is exactly the case the non-throwing lookup was chosen to tolerate.
    if (Plugin.Textures.TryGetFromGameIcon(new GameIconLookup(JobIconBase + row.JobId), out var shared)
        && shared.TryGetWrap(out var wrap, out _))
      ImGui.Image(wrap.Handle, new Vector2(size, size));
    else
      ImGui.Dummy(new Vector2(size, size));

    ImGui.SameLine(0, 4f * scale);
  }

  /// <summary>
  /// The job icon's edge.
  ///
  /// Sized to the CONTENT, not to rowHeight: the row's height already includes its padding, so drawing at
  /// rowHeight would make each row exactly two paddings taller than the height the window was measured and
  /// pinned against — and rows would re-flow as textures streamed in. If it reads too small, raise it inside
  /// <see cref="RowHeight"/>, the one place where the pin, the reserve and the icon all move together.
  ///
  /// Shared by the drawing and by the column's width so the two cannot drift; a column narrower than its own
  /// icon clips the text beside it, silently.
  /// </summary>
  private static float JobIconSize(float scale, float rowHeight) => rowHeight - CellPadding(scale).Y * 2f;

  /// <summary>
  /// Which single glyph stands for a mark, and what it says. Null when the record has nothing to show for
  /// itself on this row.
  ///
  /// One glyph, never a row of them. This cell is a column in a nine-column table on a window whose whole
  /// argument is that the list is readable at a glance; a badge per flag would make it a second list.
  ///
  /// FOUR outcomes, not three, and the fourth is the one that is easy to miss: a record can hold a colour and
  /// nothing else — <see cref="MarkedPlayer.IsEmpty"/> deliberately treats a colour as worth keeping, and
  /// unticking Focus on a coloured player leaves exactly that. It is not focused, not ignored, has no note, and
  /// a star tooltipped "Focused." would be the row asserting something false. It gets no glyph; the editor is
  /// where a colour with nothing to show is explained.
  ///
  /// Shared by the cell and the sort key so the two cannot drift — see the sort switch in
  /// <see cref="BuildView"/>, and the rule at ScentColumn.Job on keying a sort by what is rendered.
  /// </summary>
  private static (FontAwesomeIcon Glyph, Vector4 Color, string Tip)? MarkGlyph(MarkedPlayer mark, Configuration config)
    => !mark.HasVisibleMark ? null
     : mark.HasNote ? (FontAwesomeIcon.StickyNote, mark.Color ?? config.ColorFocused, "You wrote this one down.")
     : mark.IsIgnored ? (FontAwesomeIcon.EyeSlash, UiTheme.Muted, "Ignored. Never shown or announced.")
     : (FontAwesomeIcon.Star, mark.Color ?? config.ColorFocused, "Focused.");

  /// <summary>
  /// The mark column's sort bucket: higher sorts first under a descending arrow.
  ///
  /// Keyed on the GLYPH, not on the note's text — the same rule as Job and Race, for the same reason. The cell
  /// renders one icon or none, so those buckets are the only order that is actually on screen; sorting by note
  /// text would order rows by a string the column never shows. A colour-only record draws nothing and therefore
  /// sorts with the unmarked, exactly as it looks.
  /// </summary>
  private static int MarkSortKey(MarkedPlayer? mark)
    => mark is not { HasVisibleMark: true } ? 0 : mark.HasNote ? 3 : mark.IsIgnored ? 2 : 1;

  /// <summary>
  /// Right-aligns text within the current cell. GetContentRegionAvail inside a table cell reports the cell's
  /// own remaining width, so no column arithmetic is needed here.
  ///
  /// For the numbers only. Lv and Dist are the columns you scan DOWN, and "5.0y" / "12.4y" / "128.6y"
  /// left-aligned makes the magnitudes ragged. Their headers stay left-aligned: ImGui has no per-column header
  /// alignment and this binding predates angled headers, so the mismatch is the price and it is the cheap half.
  /// </summary>
  private static void TextRight(string text)
  {
    var slack = ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(text).X;
    if (slack > 0f)
      ImGui.SetCursorPosX(ImGui.GetCursorPosX() + slack);
    ImGui.TextUnformatted(text);
  }

  /// <summary>
  /// Hands the hovered row to the game as a focus target, on change only.
  ///
  /// FocusTarget marshals a delegate to the framework thread, and re-queuing that every frame for as long as
  /// a cursor rests on one row is pure garbage; the id guard buys one call per row the cursor actually
  /// crosses.
  /// </summary>
  private void HoverFocus(ulong gameObjectId)
  {
    _hoverFocusSeen = true;
    if (Volatile.Read(ref _hoverFocusId) == gameObjectId)
      return;

    // Recorded BEFORE the call goes out, so a watchdog tick landing between the two still finds an id to
    // release rather than a focus target nothing admits to holding.
    Volatile.Write(ref _hoverFocusId, gameObjectId);
    PlayerActions.FocusTarget(gameObjectId);
  }

  /// <summary>
  /// Releases the focus target once the cursor has left every row. Also covers the user switching the option
  /// off mid-hover, since no row can claim the cursor after that.
  /// </summary>
  private void ClearHoverFocusIfIdle()
  {
    if (_hoverFocusSeen)
      return;

    ReleaseHoverFocus();
  }

  /// <summary>
  /// Gives the game back the focus target this window took, if it took one.
  ///
  /// The single exit. Every way a hover can end funnels here — the cursor leaving the row, a hide condition or
  /// PvP gating Draw off, Dalamud suspending the draw loop, the window closing, the plugin unloading — because
  /// they can fire in any order and in sequence, and five copies of a two-line release is how one of them ends
  /// up missing. Idempotent for the same reason.
  ///
  /// Callable from either thread, and called from both. The exchange is what makes "read an id, clear it" a
  /// single step, so the render thread and <see cref="ReleaseStrandedHoverFocus"/> reaching for the same id
  /// cannot both win it and queue the clear twice.
  /// </summary>
  internal void ReleaseHoverFocus()
  {
    if (Interlocked.Exchange(ref _hoverFocusId, 0UL) == 0)
      return;

    PlayerActions.ClearFocusTarget();
  }

  /// <summary>
  /// Releases a hover focus that <see cref="Draw"/> is no longer running to release. Framework thread; called
  /// every tick by <see cref="Plugin"/>.
  /// </summary>
  /// <remarks>
  /// Every other release path rides the draw loop, and Dalamud stops calling that loop outright. UiBuilder's own
  /// draw handler returns BEFORE it raises the Draw event whenever the game UI is hidden (Scroll Lock), a
  /// cutscene is playing, or GPose is open; all three of those Dalamud options default on and this plugin sets
  /// none of the opt-outs. On those frames <see cref="Update"/> is never called either — WindowSystem.Draw is
  /// what calls it — and <see cref="OnClose"/> does not fire, because IsOpen never changed. As far as this window
  /// knows the cursor is still on a row, so nothing releases and the game holds a focus target the user never
  /// chose with no window on screen to explain it.
  ///
  /// Scroll Lock is the case that bites: play carries on with the HUD hidden, so &lt;f&gt; macros keep resolving
  /// to whichever passerby the cursor last crossed. <see cref="DrawConditions"/> cannot take this over — it is
  /// only ever consulted from the same loop, and Dalamud hides the UI without telling the window anything.
  ///
  /// Deliberately NOT a replacement for Update, which releases on the same frame its hide condition trips. This
  /// one has to wait out <see cref="HoverFocusGraceMs"/> to tell a stopped loop from a slow frame.
  /// </remarks>
  internal void ReleaseStrandedHoverFocus()
  {
    // Holding nothing is the overwhelmingly common case, and saying so costs one volatile read a frame.
    if (Volatile.Read(ref _hoverFocusId) == 0)
      return;

    if (Environment.TickCount64 - Volatile.Read(ref _lastDrawTicks) < HoverFocusGraceMs)
      return;

    ReleaseHoverFocus();
  }

  /// <summary>
  /// There is deliberately no Send Tell and no Invite to Party here. Dalamud's ICommandManager dispatches only
  /// to plugin commands and returns false for game commands like /tell, and no public API sends chat or sets
  /// a chat target — so both would be a button that does nothing, or an unsupported hook that gets the user
  /// banned. Link in chat posts the game's own player link instead, and the game's real menu has Tell in it.
  /// </summary>
  private void DrawRowContextMenu(ScentRow row, bool focused, bool hud)
  {
    ImGui.TextColored(UiTheme.AccentBlue, row.Name);
    ImGui.Separator();

    if (ImGui.Selectable("Target"))
      PlayerActions.Target(row.GameObjectId);

    if (ImGui.Selectable("Focus Target"))
      PlayerActions.FocusTarget(row.GameObjectId);

    if (ImGui.Selectable("Examine"))
      PlayerActions.Examine(row.GameObjectId);

    if (ImGui.Selectable("Adventurer Plate"))
      PlayerActions.OpenAdventurerPlate(row.GameObjectId);

    if (ImGui.Selectable("Link in chat"))
      PlayerActions.LinkInChat(row);
    UiTheme.Tooltip("Posts a clickable link. Click it in chat for the game's own menu, including Tell.");

    if (ImGui.Selectable("Copy name"))
      PlayerActions.CopyName(row);

    if (ImGui.Selectable("Search on Lodestone"))
      PlayerActions.OpenLodestone(row);

    ImGui.Separator();

    // Marks, not two lists. Every item below writes one record through MarkStore.Update, which deletes the row
    // outright once nothing is left on it — that is what keeps the durable store bounded by what the user
    // actually did, and it is why none of this needs a Save() call: the store owns its own file.
    if (focused)
    {
      if (ImGui.Selectable("Unfocus"))
        Plugin.Marks.Update(row.Key, row.HomeWorldName, mark => mark with { Marks = mark.Marks & ~MarkKind.Focus });
      UiTheme.Tooltip("Stop picking them out. Any note or colour stays — manage everything in the config " +
                      "window's Filters tab.");
    }
    else
    {
      if (ImGui.Selectable("Focus this player"))
        Plugin.Marks.Update(row.Key, row.HomeWorldName, mark => mark with { Marks = mark.Marks | MarkKind.Focus });
      UiTheme.Tooltip("Colour them, float them near the top, and — if you switch it on in Alerts — say so when " +
                      "they come into range. Undo it here or in the config window's Filters tab.");
    }

    if (ImGui.Selectable("Ignore this player"))
      Plugin.Marks.Update(row.Key, row.HomeWorldName, mark => mark with { Marks = mark.Marks | MarkKind.Ignore });
    UiTheme.Tooltip("Never show or announce them again. Beats Focus if they carry both. Undo it in the config " +
                    "window's Filters tab.");

    ImGui.Separator();

    // LIVE IN HUD, unlike the mark editor it replaces, and that is the refactor paying for itself rather than
    // an oversight. That item was disabled here because a popup cannot open on a chrome-less, sometimes
    // click-through overlay — it would have swallowed clicks in silence while Target and Ignore beside it
    // worked. The profile is a Window: it brings its own title bar and its own close button, so it opens over
    // HUD as readably as over anything else.
    //
    // Opened straight from here for the same reason. A Window has no "OpenPopup must share the BeginPopup ID
    // scope" rule, which is what forced the old deferral through a request field. The world name still goes with
    // it — a brand-new record needs one, and this row may be long gone from the snapshot by the time the user
    // ticks anything.
    if (ImGui.Selectable("Profile"))
      Plugin.Profile?.Open(row.Key, row.HomeWorldName);
    UiTheme.Tooltip("Their face, their note, their colour, and what they have done to you — all in one place.");
  }

  /// <summary>
  /// Says which kind of nothing this is. "Nobody nearby", "everything filtered out" and "not scanning at all"
  /// look identical from the outside, and telling someone the coast is clear when we simply stopped looking
  /// is the one lie this plugin cannot afford.
  ///
  /// HUD keeps all three branches and shortens each to one line, with the sentence demoted to a tooltip. Not
  /// two of three: the !Valid case is the one that must still speak, and it is the one a minimal overlay is
  /// most likely to be showing. The tooltip is dead under HudClickThrough, which is correct — there is nothing
  /// to hover with.
  /// </summary>
  private static void DrawEmptyState(ScentSnapshot snapshot, Configuration config, bool hud, float scale)
  {
    var (icon, brief, full) = !snapshot.Valid
      ? (FontAwesomeIcon.EyeSlash, "Not scanning.", NoseClosed)
      : snapshot.NearbyCount == 0
        ? (FontAwesomeIcon.UserSlash, "Nobody around.", "Nobody around. Just you.")
        : (FontAwesomeIcon.Filter, $"{snapshot.NearbyCount} hidden.",
           $"{snapshot.NearbyCount} out there, but your filters hide every one.");

    UiTheme.Icon(icon, UiTheme.Muted);

    // -1 is ImGui's "use the default spacing", which is what the non-HUD line has always had. HUD tightens it.
    ImGui.SameLine(0, hud ? 4f * scale : -1f);

    if (hud)
    {
      ImGui.TextColored(UiTheme.Muted, brief);
      UiTheme.Tooltip(full);
      return;
    }

    UiTheme.TextWrappedColored(UiTheme.Muted, full);
  }

  /// <summary>
  /// The one datum that lets a user catch "my filters are hiding people", and the replacement for the Races
  /// button's "(N hidden)" caption that removing the toolbar filter deleted.
  ///
  /// It is strictly better than what it replaces, which is the only reason deleting that caption is acceptable:
  /// it covers EVERY filter at once — races, distance, level, AFK, the ignore list, the search box,
  /// MaxPlayersShown, the half toggles — rather than races alone, and it needs no new config.
  ///
  /// It used to be a lie. The old line printed snapshot.NearbyCount ("Nearby: 0") beside a table showing a self
  /// row, because the scanner counts others and the table showed everyone; see the remarks on _shownOthers.
  ///
  /// The count is deliberately NOT what is on screen: it is other players in range, excluding self, whatever the
  /// filters are doing — so it and the list are free to disagree, and that disagreement is the one signal that
  /// filters are hiding people. "Others near" is what stops that reading as a contradiction. "Nearby: 0" beside
  /// a visible self row looked like an off-by-one; "Others near: 0" is an answer to the question the label now
  /// actually asks, and self is excluded from BOTH sides of the hiding arm, so it is accurate there too. The
  /// wording is the only thing that changed here — do not "fix" the count to match the list.
  ///
  /// <see cref="ScentSnapshot.Valid"/> gates both halves, and that is not a change to the count: a snapshot from
  /// ScentScanner.Reset carries 0 and 0 because nothing was scanned, not because the room was empty, and this
  /// line prints directly under DrawEmptyState's "I'm not looking, not that nobody is there". An em dash, so
  /// the footer refuses to answer rather than answering wrong. What the counts mean is untouched; only whether
  /// there is one to print.
  /// </summary>
  private void DrawFooter(ScentSnapshot snapshot, Configuration config, float scale)
  {
    // Icon + TextColored, matching ConfigWindow.DrawStatusBar, which has always drawn exactly this pair of
    // counts this way. The plugin's two status lines should not look like they came from two plugins.
    if (config.EnableNearbyList)
    {
      // Valid decides the glyph as well as the string: Users over a dash reads as a count that came up empty,
      // which is the reading this arm exists to refuse. It also gates `hiding`, so a dash can never be tinted
      // Warn — that would be claiming filters are hiding people we never went looking for.
      var hiding = snapshot.Valid && _shownOthers < snapshot.NearbyCount;

      UiTheme.Icon(snapshot.Valid ? FontAwesomeIcon.Users : FontAwesomeIcon.EyeSlash, UiTheme.Muted);
      ImGui.SameLine(0, 4f * scale);

      ImGui.TextColored(hiding ? UiTheme.Warn : UiTheme.Muted,
        !snapshot.Valid
          ? "Others near: —"
          : hiding
            ? $"Others near: {_shownOthers} of {snapshot.NearbyCount} shown"
            : $"Others near: {snapshot.NearbyCount}");
      UiTheme.Tooltip(snapshot.Valid
        ? "Other people in range — you are not counted. I see everyone; filters only " +
          "change what the list shows."
        : NoseClosed);
    }

    if (!config.EnableWatchers)
      return;

    if (config.EnableNearbyList)
      ImGui.SameLine(0, 10f * scale);

    DrawWatcherCount(snapshot, config, scale);

    // The caller's job, because DrawWatcherCount owns no tooltip; see its remarks. Nothing else on screen
    // explains this dash: DrawEmptyState would, but it only draws with the nearby half ON and the view empty,
    // and this line is reached with neither guaranteed.
    if (!snapshot.Valid)
      UiTheme.Tooltip(NoseClosed);
  }

  /// <summary>
  /// The eye + "Watching you: N" pair, in the watcher colour once there is one. Shared by <see cref="DrawFooter"/>
  /// and <see cref="DrawNearbyListOff"/> rather than written twice, because the second is the watcher half's only
  /// HUD surface and two copies of one readout is how the two end up disagreeing about what the half is called.
  ///
  /// An em dash while the snapshot is invalid, never a 0. "Watching you: 0" is the strongest claim this plugin
  /// makes — nobody is staring at you — and a Reset snapshot carries 0 because nothing was scanned. Asserting it
  /// at the one moment we cannot know is the lie <see cref="ScentSnapshot.Valid"/> exists to prevent.
  ///
  /// Draws NO tooltip, and must not start: both callers attach their own, and two Tooltip calls against one item
  /// are two stacked tooltip windows rather than one merged one. That is why the caller composes.
  /// </summary>
  private static void DrawWatcherCount(ScentSnapshot snapshot, Configuration config, float scale)
  {
    if (!snapshot.Valid)
    {
      UiTheme.Icon(FontAwesomeIcon.EyeSlash, UiTheme.Muted);
      ImGui.SameLine(0, 4f * scale);
      ImGui.TextColored(UiTheme.Muted, "Watching you: —");
      return;
    }

    var color = snapshot.WatcherCount > 0 ? config.ColorWatcher : UiTheme.Muted;
    UiTheme.Icon(FontAwesomeIcon.Eye, color);
    ImGui.SameLine(0, 4f * scale);
    ImGui.TextColored(color, $"Watching you: {snapshot.WatcherCount}");
  }

  /// <summary>
  /// Vertical space <see cref="DrawWatcherHistory"/> needs: the gap above it, its section header, and then
  /// either the fixed-height table or the one line that stands in for it. Sized from the same list the section
  /// draws, because an empty log is the default state and reserving a table's worth of room for it would park
  /// 120px of nothing under the player list until the first watcher ever showed up.
  ///
  /// The header's height comes from <see cref="UiTheme.SectionHeaderHeight"/> rather than the literals this
  /// used to carry: SectionHeader draws its label in HeaderFont and emits three items with ItemSpacing between
  /// them, none of which is visible from here, so two sets of literals in two files could only ever agree by
  /// luck — and disagreeing creeps the footer down the window or forces a scrollbar. It is handed the icon the
  /// section below actually draws, because that icon shares the header's line and only the caller knows it is
  /// there.
  ///
  /// EVERY term is one block's height PLUS the ItemSpacing.Y that follows it, and no two of them are allowed to
  /// cancel: the Dummy's is spelled out beside it, and the table's is added by hand because HistoryTableHeight is
  /// outer_size.y and EndTable's inner EndChild spends ItemSpacing.Y on top of it. This block is never the last
  /// thing on the window, so no arm here may drop its trailing term — the footer below it is the one that does,
  /// and <see cref="Draw"/> budgets the footer bare for exactly that reason.
  ///
  /// The message arm goes through <see cref="IconLineHeight"/> and not GetTextLineHeightWithSpacing, because the
  /// line it stands for has an icon on it and the icon font is the taller of the two.
  ///
  /// The Dummy's term used to be argued away against a "spare" ItemSpacing.Y in the footer's reserve. There is
  /// no spare: that ItemSpacing.Y is the player table's own trailing gap and was always spoken for, which is why
  /// the sums looked right with this section switched off and were short by one gap with it on. The shortfall
  /// only shows once the window meets the viewport clamp, and then it is permanent — an outer scrollbar wrapped
  /// around the table's own, which is the very thing this reserve exists to prevent.
  /// </summary>
  private static float HistoryReserve(float scale, bool hasEntries)
    => 6f * scale + ImGui.GetStyle().ItemSpacing.Y
     + UiTheme.SectionHeaderHeight(FontAwesomeIcon.History)
     + (hasEntries
       ? HistoryTableHeight * scale + ImGui.GetStyle().ItemSpacing.Y
       : IconLineHeight() + ImGui.GetStyle().ItemSpacing.Y);

  private static void DrawWatcherHistory(IReadOnlyList<WatcherEntry> entries, Configuration config, float scale)
  {
    UiTheme.SectionHeader("Remembered", FontAwesomeIcon.History);

    if (entries.Count == 0)
    {
      // KeepHistory drops non-current watchers, so with it OFF this branch is reached whenever nobody is
      // looking at you RIGHT NOW — regardless of how many people watched you a minute ago. So the OFF string
      // has to say that it is not remembering, or it is exactly the lie DrawEmptyState goes to such lengths to
      // avoid: the coast reported clear because we stopped remembering, not because nothing happened.
      //
      // The subject is YOU, never the plugin: every other string here is about who is watching YOU ("Watching
      // you: 0", "This one is targeting you.", "First watched you"), and this is the easiest place to flip it
      // by accident.
      //
      // Both strings stay SHORT deliberately. HistoryReserve budgets exactly one IconLineHeight for this message
      // and it is drawn wrapped — a longer string, or a large Dalamud font at the 420px minimum width, wraps to
      // two lines and pushes the footer past the window's bottom edge.
      UiTheme.Icon(FontAwesomeIcon.EyeSlash, UiTheme.Muted);
      ImGui.SameLine();
      UiTheme.TextWrappedColored(UiTheme.Muted, config.KeepHistory
        ? "Nobody has watched you yet."
        : "Nobody watching right now. Not remembering.");
      return;
    }

    var showWhen = config.ShowTimestamps;
    const ImGuiTableFlags flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg
      | ImGuiTableFlags.NoSavedSettings | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY;

    WatcherKey? toRemove = null;
    if (ImGui.BeginTable("##watcherHistory", showWhen ? 4 : 3, flags, new Vector2(0f, HistoryTableHeight * scale)))
    {
      ImGui.TableSetupScrollFreeze(0, 1);
      ImGui.TableSetupColumn("Who", ImGuiTableColumnFlags.WidthStretch);
      ImGui.TableSetupColumn("Times", ImGuiTableColumnFlags.WidthFixed, 46f * scale);
      if (showWhen)
        ImGui.TableSetupColumn("When", ImGuiTableColumnFlags.WidthFixed, 88f * scale);
      ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 60f * scale);
      ImGui.TableHeadersRow();

      // Current watchers above history, then most recent first: the answer to "who is looking at me right
      // now" must never be buried under a log that happens to sort above it.
      foreach (var entry in entries.OrderByDescending(e => e.IsCurrent).ThenByDescending(e => e.LastSeen))
      {
        ImGui.TableNextRow();
        ImGui.PushID(entry.Key.GetHashCode());

        var dim = !entry.IsCurrent;

        // Dimmed, not disabled, and on the informational cells ONLY — two separate traps in one line.
        //
        // Dimmed because ImGui's disabled flag also suppresses hover, which kills the tooltips below on
        // precisely the rows that carry them: FirstSeen is surfaced nowhere else in the UI, and a watcher
        // who has looked away is the only kind whose "first/last watched you" is interesting. Cells only,
        // because greying the whole row — the naive reading of "historical rows look inactive" — would
        // swallow the Forget button, and a disabled button refuses clicks, which would make historical
        // entries the one thing you cannot forget.
        if (dim)
          UiTheme.PushDimmed();

        ImGui.TableNextColumn();
        UiTheme.Icon(FontAwesomeIcon.Eye, entry.IsCurrent ? config.ColorWatcher : UiTheme.Muted);
        ImGui.SameLine(0, 4f * scale);
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(string.IsNullOrEmpty(entry.WorldName) ? entry.Name : $"{entry.Name}@{entry.WorldName}");

        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(entry.Count.ToString());

        // The dwell goes in the count's tooltip rather than in a column of its own. A fifth column is what the
        // eye wants and the layout cannot pay for: this table is SizingStretchProp inside a window whose minimum
        // is 420px, and the fixed columns already claim most of it — one more would collapse the stretching Who
        // column into an ellipsis at large font scales. The two facts belong together anyway: how many times,
        // and for how long.
        UiTheme.Tooltip(
          "Separate times they started watching you. Re-targeting without looking away does not count twice." +
          (entry.TotalStareMs >= 1000 ? $"\nWatching you {FormatDwell(entry.TotalStareMs)} in total." : string.Empty));

        if (showWhen)
        {
          ImGui.TableNextColumn();
          ImGui.AlignTextToFramePadding();
          ImGui.TextUnformatted(FormatWhen(entry.LastSeen));
          UiTheme.Tooltip($"First watched you: {entry.FirstSeen:dd MMM HH:mm:ss}\n" +
                          $"Last watched you: {entry.LastSeen:dd MMM HH:mm:ss}");
        }

        if (dim)
          UiTheme.PopDimmed();

        ImGui.TableNextColumn();
        if (ImGui.SmallButton("Forget"))
          toRemove = entry.Key;
        UiTheme.Tooltip("Drop this one from the list. Someone still watching you comes straight back as a " +
                        "new sighting — I won't pretend nobody is there.");

        ImGui.PopID();
      }

      ImGui.EndTable();
    }

    // Deferred: removal republishes the log's list, and mutating what is being iterated is how that becomes
    // an exception one frame later.
    if (toRemove is not null)
      Plugin.WatcherLog.Remove(toRemove.Value);
  }

  /// <summary>
  /// Age-dependent format: anything from the last day is a clock time, older is a date. A bare clock time on
  /// a three-day-old sighting reads as "just now", which is actively misleading in a log whose entire job is
  /// telling you when.
  /// </summary>
  private static string FormatWhen(DateTime when)
    => (DateTime.Now - when).TotalHours >= 24 ? when.ToString("dd MMM") : when.ToString("HH:mm:ss");

  /// <summary>
  /// When and where a marked player was last around, in words.
  ///
  /// Relative near the present and absolute past a week, because that is how the answer is actually used:
  /// "yesterday" is what you want to hear about someone you nearly remember, and "14 Mar" is what you want
  /// about someone you do not. A bare date for this morning reads as ancient history; a bare "412 days ago"
  /// reads as a machine.
  /// </summary>
  internal static string FormatLastSeen(DateTimeOffset lastSeen, string zone)
  {
    var ago = DateTimeOffset.Now - lastSeen;
    var when = ago < TimeSpan.Zero ? "just now"                       // a clock that moved; do not print "in -3h"
      : ago.TotalMinutes < 2 ? "just now"
      : ago.TotalHours < 1 ? $"{(int)ago.TotalMinutes} minutes ago"
      : ago.TotalHours < 2 ? "an hour ago"
      : ago.TotalDays < 1 ? $"{(int)ago.TotalHours} hours ago"
      : ago.TotalDays < 2 ? "yesterday"
      : ago.TotalDays < 7 ? $"{(int)ago.TotalDays} days ago"
      : lastSeen.ToString("dd MMM yyyy");

    return string.IsNullOrEmpty(zone) ? $"Last seen {when}." : $"Last seen {when}, in {zone}.";
  }

  /// <summary>
  /// A total stare time, in words. Coarse on purpose, like <see cref="FormatWhen"/>: this is a sentence in a
  /// tooltip, not a stopwatch, and the figure is only ever accurate to a rescan interval anyway.
  /// </summary>
  private static string FormatDwell(long totalMs)
  {
    var seconds = totalMs / 1000;
    if (seconds < 60)
      return $"{seconds}s";

    var minutes = seconds / 60;
    return minutes < 60 ? $"{minutes}m {seconds % 60}s" : $"{minutes / 60}h {minutes % 60}m";
  }
}
