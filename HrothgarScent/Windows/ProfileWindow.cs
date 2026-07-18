using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using HrothgarScent.Scent;

namespace HrothgarScent.Windows;

/// <summary>
/// One player, in full: who they are, what you wrote about them, and what they have done to you.
///
/// A WINDOW, NOT A POPUP, and that is the whole reason this file exists instead of a bigger DrawMarkEditor. The
/// editor it replaces lived inside the Scent list's draw scope, which was fine while the list was the only way
/// in. The game's own right-click menus are not: they name players in the friend list, Party Finder, the chat
/// log and the FC roster — people the scanner has never seen and the list cannot show — and they open with the
/// Scent window shut. An ImGui popup owned by a closed window never appears, so a profile reachable from there
/// has to stand on its own.
///
/// KEYED BY <see cref="WatcherKey"/>, never by a row or an object id. The window outlives the frame it was
/// opened from, and by design it opens for people who are not on screen at all. The key still names the same
/// person after they zone, walk out of range, or get filtered away, which is why identity is Name+HomeWorld
/// everywhere in this plugin.
///
/// EVERY EDIT WRITES THROUGH IMMEDIATELY, exactly as the editor did. There is no Save button and no draft: the
/// mark store debounces its own writes, and a profile that held changes hostage to a button would lose them to
/// the unload the store is careful to survive.
/// </summary>
public sealed class ProfileWindow : Window
{
  /// <summary>
  /// Cap on a note. Not a storage worry — the whole file is kilobytes — but marks.json is written whole on
  /// every edit, and an unbounded field is an invitation to paste a novel into a per-frame tooltip.
  ///
  /// int, not uint, and not by accident: InputTextMultiline's maxLength is Int32, and a uint here silently
  /// resolves the call to the Span&lt;byte&gt; overload instead, which rejects the ref and reports the error
  /// against the wrong argument entirely. Same family of trap as InputInt's format-before-flags.
  /// </summary>
  private const int NoteMaxLength = 512;

  /// <summary>Side of the square avatar. The Lodestone's face thumbnail is square, and a job icon is too.</summary>
  private const float AvatarSize = 84f;

  /// <summary>Breathing room between the avatar and the banner's edges. Also sets the banner's height.</summary>
  private const float AvatarInset = 10f;

  /// <summary>
  /// Height of the coloured header. DERIVED from the avatar rather than a constant beside it, because the banner
  /// exists to CONTAIN the avatar and the name — and two independent numbers is exactly how it stopped.
  ///
  /// It used to be its own 96, with the avatar hung 55% below the banner's edge. That put the colour and the
  /// content in different places: a coloured strip with nothing in it, and then the name in the dark underneath
  /// it. Every fix to one of them moved the other.
  /// </summary>
  private const float BannerHeight = AvatarSize + AvatarInset * 2f;

  /// <summary>Who this profile is about, or null when nothing has opened it yet.</summary>
  private WatcherKey? _key;

  /// <summary>
  /// The world name to key a brand-new mark with, captured when the profile opened.
  ///
  /// Needed because a mark may not exist yet — the store cannot build a record without a world — and the row or
  /// menu it came from may be long gone by the time the user ticks anything. An existing mark carries its own,
  /// which is authoritative; this is only the fallback for the first write.
  /// </summary>
  private string _worldName = string.Empty;

  /// <summary>The note buffer, held across frames so typing is not fighting the store. Committed on change.</summary>
  private string _note = string.Empty;

  public ProfileWindow() : base("Profile##hrothgarscent-profile")
  {
    // Wider than MinimumSize below, deliberately: a first-use size UNDER the minimum is a window that opens
    // already being clamped, i.e. a default the constraint immediately overrules.
    Size = new Vector2(540, 520);
    SizeCondition = ImGuiCond.FirstUseEver;
    // The minimum is set by the ACTION ROW, not by the text: three icon buttons, the Lodestone button, both
    // ticks, the swatch and its reset all sit on one line, and ImGui does not wrap a SameLine chain — it runs
    // off the edge. Everything scales together (Dalamud multiplies these by GlobalScale), so this holds at any
    // UI scale; it is the row's content that pins it, and anything added to that row has to be paid for here.
    SizeConstraints = new WindowSizeConstraints
    {
      MinimumSize = new Vector2(500, 360),
      MaximumSize = new Vector2(900, 1200),
    };
  }

  /// <summary>
  /// Opens the profile on one player, and asks for their face.
  ///
  /// The ONE place a Lodestone request is started, and it is here rather than in Draw for a reason that matters:
  /// Draw runs sixty times a second, and a request started there would be a request per frame at Square Enix's
  /// web server for as long as the window stayed open. LodestoneService single-flights it anyway — belt and
  /// braces — but the correct shape is "asked once, when a person deliberately opened this".
  /// </summary>
  public void Open(WatcherKey key, string worldName)
  {
    _key = key;

    // The mark's own world wins where there is one: the caller's may be a stale row, and the record is what the
    // store will key any write by.
    var mark = Plugin.Marks.Find(key);
    _worldName = mark?.HomeWorldName is { Length: > 0 } stored ? stored : worldName;

    // Seeded once, on open. Re-reading the store every frame would fight the user's own typing.
    _note = mark?.Note ?? string.Empty;

    Plugin.Lodestone.Request(key, _worldName);
    IsOpen = true;
  }

  /// <summary>
  /// Gate #9 of the PvP defence, and the reason it is here rather than only on the surfaces that open this: a
  /// window already open when the match loads would otherwise keep drawing a name, a world and a face at the
  /// player through the entire fight. Its own statement rather than a clause on something else, on the same
  /// terms as every other gate — see ScentScanner's gate #1.
  ///
  /// Unbackstopped, like #5 and #8: this reads the MARK STORE and a cached face, both of which are fully
  /// populated in PvP like anywhere else. There is no empty snapshot behind it to save it.
  /// </summary>
  public override bool DrawConditions() => !Plugin.ClientState.IsPvP;

  public override void Draw()
  {
    if (_key is not { } key)
    {
      // Reachable: Dalamud restores IsOpen from its own window state before anything calls Open.
      UiTheme.TextWrappedColored(UiTheme.Muted, "Nobody selected. Right-click a player and pick Profile.");
      return;
    }

    var mark = Plugin.Marks.Find(key);
    var scale = ImGuiHelpers.GlobalScale;

    // Resolved ONCE, at the top, and passed down. Null means "not standing near you right now" — never "no such
    // person" — and every consumer below has to say which of those it means. Re-reading it per section would let
    // two halves of one window disagree about whether they are here.
    var row = Plugin.Scanner.Snapshot.Rows.FirstOrDefault(r => r.Key == key);

    DrawHeader(key, mark, row, scale);
    ImGui.Dummy(new Vector2(0, 4f * scale));

    // Three tabs, one thing each, so the window is not a single scroll of everything at once. Info is who they
    // are; Jobs is what they have levelled; Notes is what you wrote. The header above — face, name, and the live
    // "looking at you right now" — stays out of the tabs, because the one fact this plugin exists for must never
    // be a click away.
    if (ImGui.BeginTabBar("##profileTabs"))
    {
      if (ImGui.BeginTabItem("Info"))
      {
        DrawInfoTab(key, mark, row, scale);
        ImGui.EndTabItem();
      }

      if (ImGui.BeginTabItem("Jobs"))
      {
        DrawJobsTab(key, scale);
        ImGui.EndTabItem();
      }

      if (ImGui.BeginTabItem("Notes"))
      {
        DrawNotesTab(key, mark, scale);
        ImGui.EndTabItem();
      }

      ImGui.EndTabBar();
    }
  }

  /// <summary>
  /// INFO — who they are, published and observed. The Lodestone character page's fields, then what they have
  /// actually done to you this session.
  ///
  /// The history sits at the BOTTOM here rather than in its own surface, because "have they targeted you" is
  /// information about this person and this is the information tab — and the header already carries the live
  /// "looking at you right now", so the urgent case is never behind a tab. Jobs is split out because a wall of
  /// 34 levels would bury these four fields.
  /// </summary>
  private void DrawInfoTab(WatcherKey key, MarkedPlayer? mark, ScentRow? row, float scale)
  {
    ImGui.Dummy(new Vector2(0, 2f * scale));

    if (DrawLodestoneState(key, scale) is { } profile)
      DrawProfileFields(row, profile, scale);

    ImGui.Dummy(new Vector2(0, 6f * scale));
    DrawHistory(BuildHistory(key, mark));
  }

  /// <summary>JOBS — every class and its level. Shares the Lodestone state flow with Info: both need the same
  /// loaded page, so the Load button and every "looking / failed" line live in one place and cannot drift.</summary>
  private void DrawJobsTab(WatcherKey key, float scale)
  {
    ImGui.Dummy(new Vector2(0, 2f * scale));

    if (DrawLodestoneState(key, scale) is not { } profile)
      return;

    // Field-op levels ride at the TOP, from a SECOND, independent fetch fired ONLY from here — a profile opened
    // to Info or Notes never spends the request. Reached only once the main page is Ready (DrawLodestoneState
    // returned non-null), so this extra request is gated behind the profile already being loaded.
    DrawFieldOps(key, scale);

    DrawJobs(profile, scale);
  }

  /// <summary>
  /// The banner, the avatar and the name — the identity block.
  ///
  /// Drawn with an explicit cursor rather than ImGui's layout, because the avatar deliberately OVERHANGS the
  /// banner's bottom edge, and nothing in ImGui's flow model expresses "half in, half out".
  /// </summary>
  private void DrawHeader(WatcherKey key, MarkedPlayer? mark, ScentRow? row, float scale)
  {
    var config = Plugin.Configuration;
    var draw = ImGui.GetWindowDrawList();
    var origin = ImGui.GetCursorScreenPos();
    var width = ImGui.GetContentRegionAvail().X;
    var banner = BannerHeight * scale;
    var avatar = AvatarSize * scale;

    // The banner takes the mark's colour where there is one, so a player the user has already colour-coded is
    // recognisable from the header alone rather than from a swatch further down. Focus only: the colour folds
    // into the focus slot everywhere else in the plugin, and a banner claiming a colour the list ignores would
    // be the DTR's old lie in a different window.
    // Fades ACROSS, not down, and only to 0.30. Down-and-out was what made the colour look stunted: the bottom
    // of the strip was 8% alpha, i.e. the window background, so the eye read the banner as ending halfway and
    // the rest as dead space. Left-to-right keeps every row of the header on colour — the avatar sits in the
    // strong end, the text in the calm end where it stays legible.
    var tint = mark is { IsFocused: true, Color: not null } ? mark.Color.Value : UiTheme.AccentPurple;
    var left = ImGui.ColorConvertFloat4ToU32(tint with { W = 0.55f });
    var right = ImGui.ColorConvertFloat4ToU32(tint with { W = 0.30f });

    draw.AddRectFilledMultiColor(origin, origin + new Vector2(width, banner), left, right, right, left);

    // Reserve the banner in the layout so everything below flows under it.
    ImGui.Dummy(new Vector2(width, banner));

    // INSIDE the banner now, not hung off its edge. The overhang is what created the empty area: it forced the
    // name into the dark below while the colour sat above with nothing in it. The banner's height is derived
    // from this inset (see BannerHeight), so the avatar cannot drift out of the block that exists to hold it.
    var avatarPos = origin + new Vector2(AvatarInset * scale, AvatarInset * scale);
    DrawAvatar(key, mark, avatarPos, avatar, scale);

    // Centred on the avatar, measured from the lines actually about to be drawn — so adding or dropping the
    // watching line keeps it centred instead of nudging everything down.
    var lines = 3 + (row?.IsWatching == true ? 1 : 0);
    var textHeight = lines * ImGui.GetTextLineHeightWithSpacing();

    ImGui.SetCursorScreenPos(new Vector2(
      avatarPos.X + avatar + 12f * scale,
      avatarPos.Y + (avatar - textHeight) * 0.5f));

    using (ImRaii.Group())
    {
      ImGui.TextColored(UiTheme.AccentBlue, key.Name);

      // The Free Company sits beside the name, where the game itself puts it. The TAG, never a name: the client
      // only ever hands over five characters, and the full name lives on the Lodestone.
      if (row?.CompanyTag is { Length: > 0 } tag)
      {
        ImGui.SameLine(0, 6f * scale);
        UiTheme.TextWrappedColored(UiTheme.Muted, $"«{tag}»");
      }

      // World, then race where it is known. OMITTED rather than dashed when it is not: a labelled "Race: —"
      // would claim they have no race, when the truth is only that they are not standing in front of you. An
      // absent word claims nothing, which is the honest shape once the live section's caption is gone.
      var world = mark?.HomeWorldName is { Length: > 0 } w ? w : _worldName;
      var line = world.Length > 0 ? world : "Unknown world";
      if (row?.RaceName is { Length: > 0 } race)
        line += $" · {race}";
      UiTheme.TextWrappedColored(UiTheme.Muted, line);

      // The tag line, in the shape the mark actually carries. "No tags set" rather than an empty row, because
      // an unmarked player is the common case and a blank gap reads as a rendering fault.
      var tags = Tags(mark);
      UiTheme.TextWrappedColored(tags is null ? UiTheme.Muted : UiTheme.AccentPurple, tags ?? "— No tags set —");

      if (row?.IsWatching == true)
        UiTheme.TextWrappedColored(Plugin.Configuration.ColorWatcher, "Looking at you right now.");
    }

    // Past the banner OR past the text, whichever ran longer. The banner contains the avatar by construction now,
    // so it is only the text that can overrun — a wrapped world+race line, or a long name — and clearing the
    // banner alone would let that text collide with the action row.
    var below = Math.Max(ImGui.GetCursorScreenPos().Y + 4f * scale, origin.Y + banner + 6f * scale);
    ImGui.SetCursorScreenPos(new Vector2(origin.X, below));

    DrawActions(key, mark, row, scale);
  }

  /// <summary>
  /// The verbs, in one row under the identity block.
  ///
  /// Open on Lodestone is ALWAYS live: it needs only a name and a world, both of which this window has by
  /// definition, and it opens the user's own browser rather than reaching out itself. The rest need the player
  /// to actually be standing there — they resolve a game object — so they are disabled with a reason rather than
  /// hidden. Hiding them would make the row's contents jump as someone walks in and out of range, and a control
  /// that appears and disappears is harder to trust than one that greys out and says why.
  /// </summary>
  private void DrawActions(WatcherKey key, MarkedPlayer? mark, ScentRow? row, float scale)
  {
    // Icons for the three that act on a character, words for the one that opens a browser. Not a style choice:
    // the icon row is disabled as a block whenever they walk away, and a text button sitting inside that block
    // would grey out with it despite needing nothing from the game.
    using (ImRaii.Disabled(row is null))
    {
      if (IconButton(FontAwesomeIcon.PaperPlane, "target"))
        PlayerActions.Target(row!.GameObjectId);
      ActionTip(row, "Target them.");

      ImGui.SameLine();

      if (IconButton(FontAwesomeIcon.Search, "examine"))
        PlayerActions.Examine(row!.GameObjectId);
      ActionTip(row, "Examine them.");

      ImGui.SameLine();

      if (IconButton(FontAwesomeIcon.Paste, "link"))
        PlayerActions.LinkInChat(row!);
      ActionTip(row, "Post their name to chat as a clickable link.");
    }

    ImGui.SameLine();

    // Never disabled: it needs only a name and a world, both of which this window has by definition, and it
    // opens the user's own browser rather than reaching out itself. It is the ONE action that still works on
    // someone the scanner has never seen — which is exactly when looking them up is most useful.
    if (ImGui.Button("Open on Lodestone"))
      PlayerActions.OpenLodestone(key.Name, WorldNameOf(key, row));
    UiTheme.Tooltip("Opens their public profile in your browser. Works whether or not they are nearby.");

    // The mark's controls ride the same row, past a wider gap. The gap is the only thing separating two kinds of
    // control that must not be confused: everything to the left DOES something once and is forgotten, while
    // everything from here rightwards CHANGES WHAT IS ON DISK the instant it is clicked. Same line, because they
    // are all "act on this person"; visibly spaced, because only half of them are permanent.
    ImGui.SameLine(0, 20f * scale);

    var config = Plugin.Configuration;

    var focus = mark?.IsFocused ?? false;
    if (ImGui.Checkbox("Focus", ref focus))
      Plugin.Marks.Update(key, _worldName, m => m with
      {
        Marks = focus ? m.Marks | MarkKind.Focus : m.Marks & ~MarkKind.Focus,
      });
    UiTheme.Tooltip("Colour them and float them near the top of the list.");

    ImGui.SameLine();

    var ignore = mark?.IsIgnored ?? false;
    if (ImGui.Checkbox("Ignore", ref ignore))
      Plugin.Marks.Update(key, _worldName, m => m with
      {
        Marks = ignore ? m.Marks | MarkKind.Ignore : m.Marks & ~MarkKind.Ignore,
      });
    UiTheme.Tooltip("Never show or announce them again. Beats Focus if they carry both.");

    // The colour rides beside Focus rather than owning a row. It FOLDS INTO the focus slot everywhere else in
    // the plugin — see DrawRow's name-colour chain — so it is only meaningful on a focused player, and sitting
    // here says that by placement instead of by a caveat under a lonely swatch.
    ImGui.SameLine(0, 10f * scale);

    var color = mark?.Color ?? config.ColorFocused;
    if (ImGui.ColorEdit4("##markcolour", ref color,
          ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaPreview | ImGuiColorEditFlags.NoLabel))
      Plugin.Marks.Update(key, _worldName, m => m with { Color = color });
    UiTheme.Tooltip(focus
      ? "Their colour in the list, on the eye, and on the nameplate."
      : "Their colour — shows on focused players only, so this does nothing until Focus is ticked.");

    ImGui.SameLine(0, 4f * scale);

    // Mandatory, not a nicety: ColorEdit4 takes a non-null Vector4, so there is no path back to "no colour"
    // through the widget itself. Without this the default is unreachable the moment the user touches the swatch.
    using (ImRaii.Disabled(mark?.Color is null))
    {
      if (IconButton(FontAwesomeIcon.Undo, "resetcolour"))
        Plugin.Marks.Update(key, _worldName, m => m with { Color = null });
    }
    UiTheme.TooltipEvenIfDisabled(mark?.Color is null
      ? "Already the default focus colour."
      : "Back to the default focus colour.");

    // Its own line, because it is a sentence about the row above rather than another control in it. Both at once
    // is a contradiction the user is allowed to hold — the flags stay independent so that un-ignoring gives the
    // focus back — but it must not be silent, or the row simply vanishes and the Focus tick looks broken.
    if (focus && ignore)
      UiTheme.TextWrappedColored(UiTheme.Muted, "Ignored, so Focus does nothing while both are ticked.");
  }

  /// <summary>
  /// A square button drawn from the icon font.
  ///
  /// The id is pushed rather than baked into the label with ##, because the label IS the glyph — two icon
  /// buttons whose glyphs happened to collide would share an ImGui id and fight over their click state.
  /// </summary>
  private static bool IconButton(FontAwesomeIcon icon, string id)
  {
    using var pushed = ImRaii.PushId(id);
    using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
      return ImGui.Button(icon.ToIconString());
  }

  /// <summary>
  /// TooltipEvenIfDisabled, not Tooltip: ImGui stamps the disabled flag onto the item at submission, so the
  /// plain helper answers "not hovered" forever after — killing the tooltip in the one state whose entire job is
  /// to explain where the control went.
  /// </summary>
  private static void ActionTip(ScentRow? row, string what)
    => UiTheme.TooltipEvenIfDisabled(row is null
      ? $"{what}\r\n\r\nNeeds them nearby — this acts on the character standing in front of you."
      : what);

  /// <summary>The best world name available: the mark's is authoritative, the row's is live, and the field
  /// captured at open is the last resort.</summary>
  private string WorldNameOf(WatcherKey key, ScentRow? row)
  {
    if (Plugin.Marks.Find(key)?.HomeWorldName is { Length: > 0 } stored)
      return stored;
    return row?.HomeWorldName is { Length: > 0 } live ? live : _worldName;
  }

  /// <summary>The mark's flags as one line, or null when there is nothing to say.</summary>
  private static string? Tags(MarkedPlayer? mark)
  {
    if (mark is null)
      return null;

    // Ignore first, and alone. It beats Focus everywhere else in the plugin, so a line reading "Focused ·
    // Ignored" would present as equals two flags that are not — and the one that wins is the one the user needs
    // to see.
    if (mark.IsIgnored)
      return "Ignored";

    return mark switch
    {
      { IsFocused: true, HasNote: true } => "Focused · Noted",
      { IsFocused: true } => "Focused",
      { HasNote: true } => "Noted",
      _ => null,
    };
  }

  /// <summary>
  /// The face, or the best thing available in its place.
  ///
  /// FOUR states, and each says something different. A ready portrait is the player's own published face. While
  /// it is in flight the frame stands empty rather than flickering a placeholder in and out. "Missing" is the
  /// interesting one: the Lodestone has nobody by that name on that world, which almost always means a rename or
  /// a transfer — so it points at the repair box instead of shrugging. Anything else falls back to the job icon,
  /// which is real game art and always there.
  /// </summary>
  private void DrawAvatar(WatcherKey key, MarkedPlayer? mark, Vector2 pos, float size, float scale)
  {
    var draw = ImGui.GetWindowDrawList();
    var box = new Vector2(size, size);
    var rounding = 6f * scale;

    draw.AddRectFilled(pos, pos + box, ImGui.GetColorU32(ImGuiCol.FrameBg), rounding);

    var portrait = Plugin.Lodestone.Get(key);
    if (portrait is { State: PortraitState.Ready, Texture: { } texture })
    {
      draw.AddImageRounded(texture.Handle, pos, pos + box, Vector2.Zero, Vector2.One,
        ImGui.GetColorU32(Vector4.One), rounding);
    }
    else if (portrait.State is not PortraitState.Looking)
    {
      // The job icon of whoever is standing there right now — read from the live snapshot, not the mark, because
      // a job is a fact about this moment and the store deliberately holds no such thing.
      var row = Plugin.Scanner.Snapshot.Rows.FirstOrDefault(r => r.Key == key);
      if (row is not null && row.JobId != 0
          && Plugin.Textures.TryGetFromGameIcon(new(62100 + row.JobId), out var icon)
          && icon.TryGetWrap(out var wrap, out _))
      {
        var pad = size * 0.14f;
        draw.AddImage(wrap.Handle, pos + new Vector2(pad), pos + box - new Vector2(pad));
      }
      else
      {
        // Nothing at all to draw: not nearby, no job, no face. An initial beats an empty box, which reads as
        // broken rather than as unknown.
        var glyph = key.Name.Length > 0 ? key.Name[..1] : "?";
        var at = pos + (box - ImGui.CalcTextSize(glyph)) * 0.5f;
        draw.AddText(at, ImGui.GetColorU32(UiTheme.Muted), glyph);
      }
    }

    draw.AddRect(pos, pos + box, ImGui.GetColorU32(UiTheme.AccentPurple with { W = 0.6f }), rounding, 0, 2f * scale);

    // An invisible button over the frame so the state has somewhere to explain itself. Hover only — there is
    // nothing to click, and a portrait that opened something on click would be a trap.
    ImGui.SetCursorScreenPos(pos);
    ImGui.InvisibleButton("##avatar", box);

    var tip = portrait.State switch
    {
      PortraitState.Ready => "Their Lodestone portrait.",
      PortraitState.Looking => "Looking them up on the Lodestone…",
      // THREE causes, and the search cannot tell them apart — a private profile, a rename and a transfer all
      // return the same zero results. Naming only the rename would send the user to repair a mark that is not
      // broken, which is a precise explanation of the wrong nothing: worse than a vague one, because it carries
      // authority. Showing their job instead is stated first so the fallback does not read as a failure.
      PortraitState.Missing => "Showing their job — the Lodestone lists nobody by this name on this world.\r\n\r\n"
                             + "Their profile may be private, or they may have renamed or transferred. If you "
                             + "know they renamed, use Renamed? in settings to point the mark at who they are "
                             + "now.",
      PortraitState.Failed => "Could not reach the Lodestone. Showing their job instead.",
      _ => Plugin.Configuration.ShowLodestonePortraits
        ? "Showing their job."
        : "Lodestone portraits are switched off in settings.",
    };
    UiTheme.Tooltip(tip);
  }

  /// <summary>
  /// "Their Lodestone" — the published character page, and the ONE user-initiated fetch of it.
  ///
  /// Seven outer states, and the order is load-bearing: the empty-world and settings checks come BEFORE the face
  /// state, because a lookup that was never started (an empty world early-returns in the service) must not read
  /// as "not looked up" beside a dead button. Only once the face is Ready — the face is what carries the
  /// verified character id — does the character page become reachable, behind an explicit button (spec 2.5).
  /// </summary>
  /// <summary>
  /// Resolves the Lodestone character page to a ready profile, or draws WHY it is not ready and returns null.
  ///
  /// Shared by the Info and Jobs tabs, which both stand on the same loaded page — so the Load button and every
  /// "looking / private / failed" line live in exactly one place and cannot drift between the two. No section
  /// header: the tab is the header now, which is the whole reason "Their Lodestone" and its caption are gone.
  ///
  /// Two state machines in sequence: the FACE lookup gates the character-page lookup, because the page is
  /// fetched by the id the face already verified. A page 404 is <see cref="ProfileFetchState.Gone"/>, never the
  /// face's Missing, so a second failure never erases the face already on screen above (spec 5.4).
  /// </summary>
  private CharacterProfile? DrawLodestoneState(WatcherKey key, float scale)
  {
    if (_worldName.Length == 0)
    {
      UiTheme.TextWrappedColored(UiTheme.Muted,
        "No home world known for them, and a name alone is not an identity — two players on different worlds "
        + "can share one.");
      return null;
    }

    if (!Plugin.Configuration.ShowLodestonePortraits)
    {
      UiTheme.TextWrappedColored(UiTheme.Muted, "Lodestone lookups are switched off in settings.");
      return null;
    }

    var portrait = Plugin.Lodestone.Get(key);
    switch (portrait.State)
    {
      case PortraitState.Idle:
        // Reachable only if Open() skipped the face request. Open() always requests today, so this is a
        // defensive branch; it offers to start the lookup rather than showing a dead "not looked up".
        if (ImGui.Button("Look them up"))
          Plugin.Lodestone.Request(key, _worldName);
        return null;

      case PortraitState.Looking:
        UiTheme.TextWrappedColored(UiTheme.Muted, "Looking them up…");
        return null;

      case PortraitState.Missing:
        // THREE indistinguishable causes — private, renamed, transferred — all return the same zero results.
        // Name them all and claim none: a precise explanation of the WRONG nothing carries false authority and
        // is worse than a vague one. (This is the bug the portrait tooltip already had to fix; do not reintroduce
        // it by picking one cause.)
        UiTheme.TextWrappedColored(UiTheme.Warn,
          $"The Lodestone lists nobody called {key.Name} on {_worldName}. Their profile may be private, or they "
          + "may have renamed or transferred — a search cannot tell these apart. If you know they renamed, use "
          + "Renamed? in settings to point the mark at who they are now.");
        return null;

      case PortraitState.Failed:
        UiTheme.TextWrappedColored(UiTheme.Muted,
          "Could not reach the Lodestone. This says nothing about them — it is our network, not their profile.");
        return null;
    }

    // Face Ready → the verified id exists, so the character page may be fetched.
    var profile = Plugin.Lodestone.GetProfile(key);
    switch (profile.State)
    {
      case ProfileFetchState.Idle:
        // Auto-load, when the user opted into the extra request. Fired from HERE rather than Open() for two
        // reasons: RequestProfile refuses until the face is Ready, which Open() cannot wait for; and firing from
        // the tab's own draw means a profile only ever opened to the Notes tab spends no request on a page the
        // user never looked at. Single-flight makes the per-frame call safe — it launches once, then the state
        // is Looking and this branch is not reached again.
        if (Plugin.Configuration.AutoLoadLodestoneProfile)
        {
          Plugin.Lodestone.RequestProfile(key, _worldName);
          UiTheme.TextWrappedColored(UiTheme.Muted, "Loading their profile…");
          return null;
        }

        // The one deliberate fetch, when auto-load is off. Never on a timer, never for a list (spec 4-E).
        if (ImGui.Button("Load their full profile"))
          Plugin.Lodestone.RequestProfile(key, _worldName);
        UiTheme.Tooltip("Fetches their public character page: Free Company, race, title, Grand Company and every "
          + "job's level. None of this is in the game client.");
        UiTheme.TextWrappedColored(UiTheme.Muted,
          "One more request to Square Enix. There is no API here, so this stays a click unless you turn on "
          + "automatic loading in settings.");
        return null;

      case ProfileFetchState.Looking:
        UiTheme.TextWrappedColored(UiTheme.Muted, "Loading their profile…");
        return null;

      case ProfileFetchState.Gone:
        // The face stays on screen above — it was still found; only the full page is gone.
        UiTheme.TextWrappedColored(UiTheme.Warn,
          "Their Lodestone page is gone. The character has been deleted, or renamed far enough that Square Enix "
          + "dropped the page. If you know they renamed, use Renamed? in settings.");
        return null;

      case ProfileFetchState.Failed:
        UiTheme.TextWrappedColored(UiTheme.Muted,
          "Could not read their profile page. This says nothing about them — it is our network, or Square Enix "
          + "changed the page.");
        // The ONLY retry path: RequestProfile refuses anything but Idle, so without this a transient failure is
        // permanent for the session. A click, so it stays inside the user-initiated rule; a repaint never retries.
        if (ImGui.Button("Try again"))
          Plugin.Lodestone.RetryProfile(key, _worldName);
        return null;

      case ProfileFetchState.Ready:
        return profile;
    }

    return null;
  }

  /// <summary>
  /// The parsed fields under Ready. Every absence is spelled out as its own sentence naming WHICH nothing it is,
  /// never left blank — a blank in a labelled section reads as a rendering fault (spec 5.4).
  /// </summary>
  private void DrawProfileFields(ScentRow? row, CharacterProfile profile, float scale)
  {
    var labelWidth = 130f * scale;

    UiTheme.SectionHeader("Character Profile");

    // Free Company — the only source of the FC NAME (the live row carries only a ≤5-char tag), with its 3-layer
    // Lodestone crest beside it. A null name is a verified "no FC", drawn as its own sentence.
    DrawFreeCompanyField(profile, labelWidth, scale);

    // Race is the page sentinel, so it is never absent under Ready. Built from the three glyph-decoded parts.
    var race = Join(" · ", profile.Race, profile.Clan, profile.Gender);
    Field("Race", race.Length > 0 ? race : null, "Not shown on their Lodestone page.", labelWidth);

    // THE CONTRADICTION RULE: the live scan and the Lodestone can legitimately disagree — a Fantasia between
    // their last logout and now. Live wins, and the window says so out loud rather than silently picking one.
    // Branch on RaceId, never the string: RaceName is "Unknown" for both an unloaded and an unrecognised race.
    if (row is { RaceId: not 0 } && profile.Race is { Length: > 0 } lodeRace
        && !row.RaceName.StartsWith(lodeRace, StringComparison.Ordinal))
    {
      ImGui.SetCursorPosX(ImGui.GetCursorPosX() + labelWidth);
      UiTheme.TextWrappedColored(UiTheme.Muted,
        $"The live scan says {row.RaceName}. The Lodestone updates when they log out.");
    }

    Field("Title", profile.Title, "No title equipped.", labelWidth);

    // Grand Company — text only. The GrandCompany sheet carries no icon column (verified), so there is no local
    // icon to resolve, and inventing one is exactly what part 5 forbids; "company · rank" is the honest render.
    var gc = profile.GrandCompany is { Length: > 0 } company
      ? profile.GrandCompanyRank is { Length: > 0 } rank ? $"{company} · {rank}" : company
      : null;
    // NOT "None" and never a dash: an absent GC could equally be a non-NA region hiding an enlisted player.
    Field("Grand Company", gc, "Not shown on their Lodestone page.", labelWidth);

    // Nameday, Guardian and City-state are ORDINARY inline fields now, no longer folded away — they are the
    // character page's own fields alongside the rest. City-state carries its local Town game icon; the other two
    // are plain text. (City-state is still where they STARTED, not where they are, and nothing on this window
    // pretends otherwise — the old caveat held that line by wording, not by hiding the field.)
    Field("Nameday", profile.Nameday, "Not shown on their Lodestone page.", labelWidth);
    Field("Guardian", profile.Guardian, "Not shown on their Lodestone page.", labelWidth);
    DrawCityStateField(profile, labelWidth, scale);

    // Jobs are NOT here — they are their own tab, because thirty-four levels under these fields would bury them.
  }

  /// <summary>
  /// The Free Company row: the 3-layer Lodestone crest, then the name — or the verified "no FC" sentence.
  ///
  /// The crest is three PNGs (background, emblem, frame) drawn OVERLAID on one line-high rect via the draw list,
  /// the same AddImage path the avatar uses — ImGui.Image would lay them side by side. They exist only once the
  /// profile is loaded and only when there actually is an FC, so nothing here is ever speculative.
  /// </summary>
  private static void DrawFreeCompanyField(CharacterProfile profile, float labelWidth, float scale)
  {
    UiTheme.TextWrappedColored(UiTheme.Muted, "Free Company");
    ImGui.SameLine(labelWidth);

    if (profile.FreeCompanyName is not { Length: > 0 } name)
    {
      UiTheme.TextWrappedColored(UiTheme.Muted, "Not in a Free Company.");
      return;
    }

    if (profile.FreeCompanyCrest is { Count: > 0 } crest)
    {
      DrawCrest(crest, scale);
      ImGui.SameLine(0, 6f * scale);
    }

    ImGui.TextUnformatted(name);
  }

  /// <summary>Draws the crest layers overlaid on one text-line-high square and advances the cursor past it. Layer
  /// 0 (background) first, on the SAME rect — three AddImage calls, because ImGui.Image would place them in a row.
  /// This is DrawAvatar's own draw-list path, so it leans on no ImGui.Image overload.</summary>
  private static void DrawCrest(IReadOnlyList<IDalamudTextureWrap> layers, float scale)
  {
    var size = ImGui.GetTextLineHeight();
    var pos = ImGui.GetCursorScreenPos();
    var draw = ImGui.GetWindowDrawList();

    foreach (var layer in layers)
      draw.AddImage(layer.Handle, pos, pos + new Vector2(size, size));

    // Claim the square in the layout so the name SameLines correctly after it.
    ImGui.Dummy(new Vector2(size, size));
  }

  /// <summary>The City-state row: its local Town game icon, then the name — or the "not shown" sentence. On a
  /// name that does not resolve to a Town row (or whose texture is not ready yet) the icon is silently omitted
  /// and the name draws alone; never a guessed icon.</summary>
  private static void DrawCityStateField(CharacterProfile profile, float labelWidth, float scale)
  {
    UiTheme.TextWrappedColored(UiTheme.Muted, "City-state");
    ImGui.SameLine(labelWidth);

    if (profile.CityState is not { Length: > 0 } city)
    {
      UiTheme.TextWrappedColored(UiTheme.Muted, "Not shown on their Lodestone page.");
      return;
    }

    if (TownPalette.IconIdOf(city) is { } iconId
        && Plugin.Textures.TryGetFromGameIcon(new(iconId), out var shared)
        && shared.TryGetWrap(out var wrap, out _))
    {
      var size = ImGui.GetTextLineHeight();
      ImGui.Image(wrap.Handle, new Vector2(size, size));
      ImGui.SameLine(0, 6f * scale);
    }

    ImGui.TextUnformatted(city);
  }

  /// <summary>
  /// The Jobs tab's role grouping, as SLOT-INDEX templates over the Lodestone's fixed 34-slot order (verified
  /// across both captures — slots 0-33 in document order across the four character__level__list blocks).
  ///
  /// SLOT INDEX, NOT resolved role, is the grouping key, for one concrete reason: the only base class shared by
  /// two jobs is Arcanist (Summoner AND Scholar), and they sit in different buckets — a character with neither
  /// levelled shows "Arcanist" in BOTH slots, indistinguishable by name. Slot is <see cref="JobSlot"/>'s own
  /// declared stable key; the tooltip text is explicitly not. Icons still come from the sheet name-map, so the
  /// "no hardcoded ClassJob ids" rule — which is about the icon ids — is kept.
  ///
  /// The order within each group is the task's requested job order, which the page's own slot order already
  /// matches — EXCEPT that Beastmaster sits inside the melee block (slot 14) and Blue Mage inside the mag-ranged
  /// block (slot 22), so Limited pulls those two out by index; and the page lists crafters (23-30) before
  /// gatherers (31-33), so this lists gatherers first, as asked.
  /// </summary>
  private static readonly (string Role, int[] Slots)[] JobGroups =
  [
    ("Tank",            [0, 1, 2, 3]),
    ("Healer",          [4, 5, 6, 7]),
    ("Melee",           [8, 9, 10, 11, 12, 13]),
    ("Physical Ranged", [15, 16, 17]),
    ("Magical Ranged",  [18, 19, 20, 21]),
    ("Limited",         [22, 14]),
    ("Gatherer",        [31, 32, 33]),
    ("Crafter",         [23, 24, 25, 26, 27, 28, 29, 30]),
  ];

  /// <summary>
  /// Jobs and levels, grouped by role in game order, each with its game job icon.
  ///
  /// Zero jobs is Failed-shaped, not a fact about them, so it still says the page changed shape. Every slot is
  /// shown, levelled or not — an unlevelled slot is a REAL state ("——"), the whole reason "-" is captured rather
  /// than dropped — and the role/game order is kept as-is, NOT sorted levelled-first (spec 2).
  /// </summary>
  private static void DrawJobs(CharacterProfile profile, float scale)
  {
    if (profile.Jobs.Count == 0)
    {
      UiTheme.TextWrappedColored(UiTheme.Muted,
        "Their Lodestone did not list any jobs — the page has probably changed shape.");
      return;
    }

    // Index by Slot so each group pulls its members by position — the disambiguator the tooltip name cannot be
    // (see JobGroups). A shape change that drops a slot leaves a gap the group skips rather than crashes on.
    var bySlot = new Dictionary<int, JobSlot>(profile.Jobs.Count);
    foreach (var job in profile.Jobs)
      bySlot[job.Slot] = job;

    var levelled = profile.Jobs.Count(j => j.Level.HasValue);
    UiTheme.TextWrappedColored(UiTheme.Muted, $"{levelled} of {profile.Jobs.Count} levelled");
    ImGui.Dummy(new Vector2(0, 4f * scale));

    // Where the level number sits, measured from the line's left edge — past the icon and the widest job name —
    // so the numbers line up down the whole tab regardless of name length.
    var levelX = 210f * scale;

    foreach (var (role, slots) in JobGroups)
    {
      ImGui.TextColored(UiTheme.AccentPurple, role);

      foreach (var slot in slots)
      {
        if (bySlot.TryGetValue(slot, out var job))
          DrawJobRow(job, levelX, scale);
      }

      ImGui.Dummy(new Vector2(0, 4f * scale));
    }

    UiTheme.TextWrappedColored(UiTheme.Muted, "—— means they have not unlocked or levelled it.");
  }

  /// <summary>
  /// One job: its game icon, name and level on a line.
  ///
  /// The icon is resolved from the ClassJob sheet by NAME — a levelled slot's job name OR an unlevelled slot's
  /// base-class name, both of which the sheet map covers — via ITextureProvider.TryGetFromGameIcon(62100 + id).
  /// On a name that does not resolve, NO icon is drawn (a wrong icon is worse than none); the square is reserved
  /// either way so the names stay aligned. "——" for a null level, never "0" or blank: the Lodestone printed "-".
  /// </summary>
  private static void DrawJobRow(JobSlot job, float levelX, float scale)
  {
    var size = ImGui.GetTextLineHeight();

    if (JobIconMap.IconIdFor(job.Name) is { } iconId
        && Plugin.Textures.TryGetFromGameIcon(new(iconId), out var shared)
        && shared.TryGetWrap(out var wrap, out _))
      ImGui.Image(wrap.Handle, new Vector2(size, size));
    else
      ImGui.Dummy(new Vector2(size, size));

    ImGui.SameLine(0, 6f * scale);
    ImGui.TextUnformatted(job.Name);

    ImGui.SameLine(levelX);
    if (job.Level is { } level)
      ImGui.TextUnformatted(level.ToString());
    else
      // "——", never "0" and never blank: the Lodestone printed "-", meaning not unlocked or not levelled.
      UiTheme.TextWrappedColored(UiTheme.Muted, "——");
  }

  /// <summary>
  /// The field-op levels at the TOP of the Jobs tab, and the state of the SECOND fetch that gets them.
  ///
  /// Mirrors the main page's Idle/Looking/Failed handling in DrawLodestoneState, driven from THIS tab's own draw
  /// so the /class_job/ request is only ever made when the Jobs tab is viewed — and honouring
  /// AutoLoadLodestoneProfile the same way the main page does: auto-fetch when on, a deliberate button when off.
  /// Single-flight makes the per-frame call safe: it launches once, then the state is Looking and this returns.
  /// </summary>
  private void DrawFieldOps(WatcherKey key, float scale)
  {
    var fieldOps = Plugin.Lodestone.GetFieldOps(key);
    switch (fieldOps.State)
    {
      case FieldOpFetchState.Idle:
        // Auto-load, when the user opted into the extra requests. The main page is already Ready by the time this
        // draws, so this adds exactly one request, and only for a profile whose Jobs tab was actually opened.
        if (Plugin.Configuration.AutoLoadLodestoneProfile)
        {
          Plugin.Lodestone.RequestFieldOps(key, _worldName);
          UiTheme.TextWrappedColored(UiTheme.Muted, "Loading field-op levels…");
          return;
        }

        // The deliberate second click, when auto-load is off — a THIRD Lodestone request in total, so it stays
        // opt-in exactly like the full page above it.
        if (ImGui.Button("Load field-op levels"))
          Plugin.Lodestone.RequestFieldOps(key, _worldName);
        UiTheme.Tooltip("Eureka, Bozja and Occult Crescent progress lives on a second Lodestone page — one more "
          + "request to Square Enix, only if you want it.");
        ImGui.Dummy(new Vector2(0, 6f * scale));
        return;

      case FieldOpFetchState.Looking:
        UiTheme.TextWrappedColored(UiTheme.Muted, "Loading field-op levels…");
        return;

      case FieldOpFetchState.Gone:
        // Rare — the sub-page exists whenever the main page did. Says nothing about them; a neutral line, no retry.
        UiTheme.TextWrappedColored(UiTheme.Muted, "Their field-op page could not be found.");
        ImGui.Dummy(new Vector2(0, 6f * scale));
        return;

      case FieldOpFetchState.Failed:
        UiTheme.TextWrappedColored(UiTheme.Muted,
          "Could not read their field-op levels. This says nothing about them — it is our network, or Square Enix "
          + "changed the page.");
        // The only retry path: RequestFieldOps refuses anything but Idle, so without this a transient failure is
        // permanent for the session. A click, so it stays inside the user-initiated rule; a repaint never retries.
        if (ImGui.Button("Try again"))
          Plugin.Lodestone.RetryFieldOps(key, _worldName);
        ImGui.Dummy(new Vector2(0, 6f * scale));
        return;

      case FieldOpFetchState.Ready:
        DrawFieldOpLevels(fieldOps, scale);
        return;
    }
  }

  /// <summary>The three field-op levels, each OMITTED when absent — a character with no field-op progress at all
  /// draws nothing here (they are supplementary, not a labelled slot that must show "none"). Label + number, no
  /// icon: the Lodestone's field-op glyphs are webfont, not game icons, so a word is the honest render.</summary>
  private static void DrawFieldOpLevels(FieldOpProgress fieldOps, float scale)
  {
    if (fieldOps.ElementalLevel is null && fieldOps.ResistanceRank is null && fieldOps.KnowledgeLevel is null)
      return;

    var labelWidth = 150f * scale;
    if (fieldOps.ElementalLevel is { } elemental)
      Field("Elemental Level", elemental.ToString(), string.Empty, labelWidth);
    if (fieldOps.ResistanceRank is { } resistance)
      Field("Resistance Rank", resistance.ToString(), string.Empty, labelWidth);
    if (fieldOps.KnowledgeLevel is { } knowledge)
      Field("Knowledge Level", knowledge.ToString(), string.Empty, labelWidth);

    ImGui.Dummy(new Vector2(0, 6f * scale));
  }

  /// <summary>A Muted label at a fixed column, then the value — or, when the value is null, the sentence naming
  /// which nothing it is. Absence is a sentence here, never a blank cell.</summary>
  private static void Field(string label, string? value, string absent, float labelWidth)
  {
    UiTheme.TextWrappedColored(UiTheme.Muted, label);
    ImGui.SameLine(labelWidth);

    if (string.IsNullOrEmpty(value))
    {
      UiTheme.TextWrappedColored(UiTheme.Muted, absent);
      return;
    }

    ImGui.PushTextWrapPos(0f);
    ImGui.TextUnformatted(value);
    ImGui.PopTextWrapPos();
  }

  /// <summary>Joins the non-empty parts with <paramref name="sep"/> — for the Race · Clan · Gender line, where
  /// any one part could be missing without the others.</summary>
  private static string Join(string sep, params string?[] parts)
    => string.Join(sep, parts.Where(p => !string.IsNullOrEmpty(p)));

  /// <summary>
  /// The note, and the button that deletes the record it belongs to.
  ///
  /// Fenced off above and below. Everything else on this window is a fact ABOUT the player or a verb aimed AT
  /// them; this is the only place the user writes prose, and the rules run the other way — it is the one field
  /// with no correct value, no absent state to explain, and nothing to say back. The separators are what stop it
  /// reading as another readout that happens to be empty.
  /// </summary>
  /// <param name="reserveBelow">
  /// How much room the history underneath needs, so the note can take everything else.
  /// </param>
  /// <summary>NOTES — the one thing on this window the user writes, given the whole tab. The note box fills the
  /// body down to the Forget button, so a long note gets room and a short one is not squeezed by anything.</summary>
  private void DrawNotesTab(WatcherKey key, MarkedPlayer? mark, float scale)
  {
    ImGui.Dummy(new Vector2(0, 2f * scale));

    // MULTILINE, and the hint is painted by hand because of it. The note holds sentences, so InputTextWithHint
    // — which is single-line only — would have traded the room to write for a label, which is not an
    // improvement. There is no multiline overload that takes a hint, so the placeholder is drawn into the empty
    // box instead: same effect, and the box keeps its width rather than spending a line on the word "Note".
    var notePos = ImGui.GetCursorScreenPos();

    // FILLS the tab body, down to the Forget button below it. The note has no natural height — it is an empty
    // box the user may put one line or ten in — so it takes whatever the window gives it. Floored at three lines
    // so a window dragged short does not collapse the one field the user actually authors to nothing.
    var reserve = (mark is null ? 0f : ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.Y) + 8f * scale;
    var noteHeight = Math.Max(ImGui.GetTextLineHeight() * 3f, ImGui.GetContentRegionAvail().Y - reserve);

    ImGui.SetNextItemWidth(-1);
    if (ImGui.InputTextMultiline("##profilenote", ref _note, NoteMaxLength, new Vector2(0, noteHeight)))
      Plugin.Marks.Update(key, _worldName, m => m with { Note = _note });
    UiTheme.Tooltip("Kept on disk until you delete it. Nobody else can see it, and it never leaves your machine.");

    // Inside the frame, offset by ImGui's own padding so it lands exactly where the caret will. Drawn after the
    // widget so it sits on top of the empty background rather than under it.
    if (_note.Length == 0)
      ImGui.GetWindowDrawList().AddText(notePos + ImGui.GetStyle().FramePadding,
        ImGui.GetColorU32(UiTheme.Muted), "Write a note — only you ever see it");

    ImGui.Dummy(new Vector2(0, 4f * scale));

    // Only where there is something to delete. On an unmarked player it would be a button whose whole function
    // is to do nothing — and MarkStore.Remove silently returns on a key it does not hold, so it would not even
    // report its own uselessness.
    if (mark is null)
      return;

    if (IconButton(FontAwesomeIcon.TrashAlt, "forget"))
    {
      Plugin.Marks.Remove(key);

      // Stays open on purpose. Forget deletes the record, not the person — they may still be standing in front
      // of the user, and the window is still the answer to "who is this". Closing would also make the button
      // feel like it dismissed something rather than deleted something.
      _note = string.Empty;
    }
    UiTheme.Tooltip("Forget this player — deletes the note, the colour and both ticks. Does not close this.");

    ImGui.SameLine(0, 6f * scale);
    UiTheme.TextWrappedColored(UiTheme.Muted, "Forget this player");
  }

  /// <summary>
  /// What they have actually done, as opposed to what the user wrote about them.
  ///
  /// Reads the live in-memory log, and does NOT persist it. How often someone stared at you is an observation
  /// about them, not something the user wrote — so it is shown here and forgotten at logout, exactly like the
  /// history pane it comes from. See <see cref="WatcherLog"/>.
  /// </summary>
  private static List<(string Text, Vector4 Color)> BuildHistory(WatcherKey key, MarkedPlayer? mark)
  {
    var lines = new List<(string, Vector4)>(2);

    var stares = Plugin.WatcherLog.Snapshot().FirstOrDefault(entry => entry.Key == key);
    if (stares is null)
      lines.Add(NoSightings());
    else
    {
      // Not "0s": the dwell is sampled per scan, so a glance shorter than one interval genuinely measures zero.
      // Printing 0s would report "they looked for no time at all" about someone who did look.
      var held = stares.TotalStareMs <= 0
        ? "too briefly to time"
        : $"targeted for {FormatDwell(stares.TotalStareMs)}";

      // THE COUNT SURVIVES, in the only branch where it says anything. One episode has no "first" and no "most
      // recently", so it takes the short form. More than one and the bare form would print ONE clock time for
      // five separate episodes — reporting the last as if it were the whole story, and silently dropping four.
      // Count is episodes, not scans; see WatcherEntry.Count.
      lines.Add((stares.Count == 1
        ? $"Targeted @ {stares.FirstSeen:HH:mm:ss}  ({held})"
        : $"Targeted {stares.Count}x, last @ {stares.LastSeen:HH:mm:ss}  ({held} in total)", UiTheme.Muted));
    }

    // The one durable observation, shown where it is made rather than only in a file. See MarkedPlayer.LastSeen.
    if (mark?.LastSeen is { } lastSeen)
      lines.Add((string.IsNullOrEmpty(mark.LastSeenZone)
        ? $"Last Seen: {ScentWindow.FormatAgo(lastSeen)}"
        : $"Last Seen: {ScentWindow.FormatAgo(lastSeen)}, {mark.LastSeenZone}", UiTheme.Muted));

    return lines;
  }

  private static void DrawHistory(List<(string Text, Vector4 Color)> lines)
  {
    ImGui.Separator();
    foreach (var (text, color) in lines)
      UiTheme.TextWrappedColored(color, text);
  }

  /// <summary>
  /// Why there is no sighting — FOUR reasons, not one, and only the last of them is "they never looked at you".
  ///
  /// THE OTHER THREE ARE THE SAME LIE THIS PLUGIN EXISTS TO REFUSE. An absent entry does not mean an absent
  /// watcher: KeepHistory drops everyone not staring at this instant, HistoryLimit silently evicts the
  /// least-recent down to ten by DEFAULT — so the eleventh person to look at you this session erases the first —
  /// and with RecordWhileClosed off nothing is recorded while the window is shut. In a city plaza the eviction
  /// branch is an ordinary afternoon. Reporting any of those as "they have never targeted you" would be the
  /// coast-is-clear lie, told about the exact subject the plugin was built for, with total confidence.
  ///
  /// ScentWindow's own history pane already refuses this in this same log, and its comment says why. This is
  /// that refusal, in this window's voice, with the two branches it did not need and this one does.
  ///
  /// A precise explanation of the WRONG nothing is worse than a dash: a dash does not claim authority.
  /// </summary>
  private static (string Text, Vector4 Color) NoSightings()
  {
    var config = Plugin.Configuration;

    if (!config.KeepHistory)
    {
      return ("Not remembering. History is switched off, so only people looking at you right now appear here — "
        + "this cannot tell you whether they did earlier.", UiTheme.Warn);
    }

    if (Plugin.WatcherLog.Forgotten is > 0 and var forgotten)
    {
      return ($"{forgotten} {(forgotten == 1 ? "watcher has" : "watchers have")} already been dropped from this "
        + $"session's log — it keeps {Math.Max(1, config.HistoryLimit)}. If they were one of them, this says "
        + "nothing about them.", UiTheme.Warn);
    }

    if (!config.RecordWhileClosed && !Plugin.IsMainWindowOpen)
    {
      return ("They have never targeted you while the Scent window was open — and staring is only recorded "
        + "while it is open.", UiTheme.Muted);
    }

    // The true never. The second sentence is mandatory: FirstSeen is the first time they TARGETED you, so a
    // null here must never be read as "never seen".
    return ("They have never targeted you this session. That is not the same as never being near you — nothing "
      + "here records who was merely around.", UiTheme.Muted);
  }

  /// <summary>Cumulative dwell, in the coarsest unit that is still true. Its own formatter rather than
  /// AlertService's FormatHeld: that one describes ONE episode and tops out at "over N minutes", which would
  /// throw away the precision that makes a session total worth printing.</summary>
  private static string FormatDwell(long ms)
  {
    var seconds = ms / 1000;
    if (seconds < 60)
      return seconds == 1 ? "1 second" : $"{seconds} seconds";

    var minutes = seconds / 60;
    return $"{minutes}m {seconds % 60:00}s";
  }
}
