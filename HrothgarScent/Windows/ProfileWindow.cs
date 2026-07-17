using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
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
    ImGui.Dummy(new Vector2(0, 6f * scale));

    // BUILT BEFORE IT IS DRAWN, because the note stretches to meet it and therefore has to know how tall it will
    // be. Its height is not a constant: a sighting is two short lines, but every "why there is no sighting"
    // branch is a wrapped paragraph, and reserving two lines for four would push the answer below the fold in
    // exactly the case the paragraph exists to explain.
    var history = BuildHistory(key, mark);

    DrawNote(key, mark, scale, HeightOf(history, scale));
    DrawHistory(history);
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
  private void DrawNote(WatcherKey key, MarkedPlayer? mark, float scale, float reserveBelow)
  {
    ImGui.Separator();
    ImGui.Dummy(new Vector2(0, 2f * scale));

    // MULTILINE, and the hint is painted by hand because of it. The note is the one thing on this window the
    // user authors and it holds sentences, so InputTextWithHint — which is single-line only — would have traded
    // the label for the room to write, which is not an improvement. There is no multiline overload that takes a
    // hint, so the placeholder is drawn into the empty box instead: same effect, and the box keeps its width
    // rather than spending a line on the word "Note".
    var notePos = ImGui.GetCursorScreenPos();

    // STRETCHES to meet the history below. Everything else on this window is sized by its content; the note has
    // no natural height — it is an empty box the user may put one line or ten in — so it is the only thing here
    // that can absorb a resize. A fixed four lines left a dead gap under it that grew with every drag, on the
    // one field whose whole purpose is room to write.
    //
    // Floored at three lines rather than allowed to collapse: on a window dragged short the note would otherwise
    // shrink to nothing while the history it is yielding to stays fully drawn, which inverts the priority — the
    // history is two lines of recall, the note is the only thing here the user actually authors.
    var reserve = reserveBelow + (mark is null ? 0f : ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.Y)
                + 8f * scale;
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

  /// <summary>
  /// How tall <see cref="DrawHistory"/> will be, measured from the strings it is actually about to draw.
  ///
  /// Measured rather than assumed because the paragraphs wrap, and wrapping depends on a window width the user
  /// can drag. A constant would be right at one width and wrong at every other — pushing the history off the
  /// bottom on a narrow window, which is the one place it must not go.
  /// </summary>
  private static float HeightOf(List<(string Text, Vector4 Color)> lines, float scale)
  {
    var style = ImGui.GetStyle();
    var wrap = ImGui.GetContentRegionAvail().X;

    // The separator and the breathing room above it.
    var height = style.ItemSpacing.Y * 2f + 4f * scale;

    foreach (var (text, _) in lines)
      height += ImGui.CalcTextSize(text, false, wrap).Y + style.ItemSpacing.Y;

    return height;
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
