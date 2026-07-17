using System;
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

  /// <summary>Height of the banner strip behind the name. Scaled at use; this is the 100% figure.</summary>
  private const float BannerHeight = 96f;

  /// <summary>Side of the square avatar. The Lodestone's face thumbnail is square, and a job icon is too.</summary>
  private const float AvatarSize = 84f;

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
    Size = new Vector2(460, 520);
    SizeCondition = ImGuiCond.FirstUseEver;
    SizeConstraints = new WindowSizeConstraints
    {
      MinimumSize = new Vector2(420, 360),
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

    DrawLive(row, scale);
    ImGui.Dummy(new Vector2(0, 4f * scale));

    DrawMarkControls(key, mark, scale);
    ImGui.Dummy(new Vector2(0, 4f * scale));

    DrawHistory(key, mark);

    if (mark is null)
      return;

    ImGui.Dummy(new Vector2(0, 6f * scale));
    ImGui.Separator();

    if (ImGui.SmallButton("Forget this player"))
    {
      Plugin.Marks.Remove(key);

      // Stays open on purpose. Forget deletes the record, not the person — they may still be standing in front
      // of the user, and the window is still the answer to "who is this". Closing would also make the button
      // feel like it dismissed something rather than deleted something.
      _note = string.Empty;
    }
    UiTheme.Tooltip("Deletes the note, the colour and both ticks. Does not close this.");
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
    var tint = mark is { IsFocused: true, Color: not null } ? mark.Color.Value : UiTheme.AccentPurple;
    var top = ImGui.ColorConvertFloat4ToU32(tint with { W = 0.55f });
    var bottom = ImGui.ColorConvertFloat4ToU32(tint with { W = 0.08f });

    draw.AddRectFilledMultiColor(origin, origin + new Vector2(width, banner), top, top, bottom, bottom);

    // Reserve the banner in the layout so everything below flows under it.
    ImGui.Dummy(new Vector2(width, banner));

    var avatarPos = origin + new Vector2(12f * scale, banner - avatar * 0.55f);
    DrawAvatar(key, mark, avatarPos, avatar, scale);

    // The text block sits to the right of the avatar and below the banner's baseline.
    ImGui.SetCursorScreenPos(new Vector2(avatarPos.X + avatar + 12f * scale, origin.Y + banner + 4f * scale));

    using (ImRaii.Group())
    {
      ImGui.TextColored(UiTheme.AccentBlue, key.Name);

      var world = mark?.HomeWorldName is { Length: > 0 } w ? w : _worldName;
      UiTheme.TextWrappedColored(UiTheme.Muted, world.Length > 0 ? world : "Unknown world");

      // The tag line, in the shape the mark actually carries. "No tags set" rather than an empty row, because
      // an unmarked player is the common case and a blank gap reads as a rendering fault.
      var tags = Tags(mark);
      UiTheme.TextWrappedColored(tags is null ? UiTheme.Muted : UiTheme.AccentPurple, tags ?? "— No tags set —");
    }

    // Past the taller of the two columns, whichever it was.
    var below = Math.Max(ImGui.GetCursorScreenPos().Y, avatarPos.Y + avatar + 6f * scale);
    ImGui.SetCursorScreenPos(new Vector2(origin.X, below));

    DrawActions(key, row);
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
  private void DrawActions(WatcherKey key, ScentRow? row)
  {
    if (ImGui.SmallButton("Open on Lodestone"))
      PlayerActions.OpenLodestone(key.Name, WorldNameOf(key, row));
    UiTheme.Tooltip("Opens their public profile in your browser. Works whether or not they are nearby.");

    ImGui.SameLine();

    using (ImRaii.Disabled(row is null))
    {
      if (ImGui.SmallButton("Target"))
        PlayerActions.Target(row!.GameObjectId);

      ImGui.SameLine();

      if (ImGui.SmallButton("Examine"))
        PlayerActions.Examine(row!.GameObjectId);

      ImGui.SameLine();

      if (ImGui.SmallButton("Link in chat"))
        PlayerActions.LinkInChat(row!);
    }

    // TooltipEvenIfDisabled, not Tooltip: ImGui stamps the disabled flag onto the item at submission, so the
    // plain helper answers "not hovered" forever after — killing the tooltip in the one state whose entire job
    // is to explain where the control went.
    if (row is null)
      UiTheme.TooltipEvenIfDisabled("Needs them nearby — these act on the character standing in front of you.");
  }

  /// <summary>The best world name available: the mark's is authoritative, the row's is live, and the field
  /// captured at open is the last resort.</summary>
  private string WorldNameOf(WatcherKey key, ScentRow? row)
  {
    if (Plugin.Marks.Find(key)?.HomeWorldName is { Length: > 0 } stored)
      return stored;
    return row?.HomeWorldName is { Length: > 0 } live ? live : _worldName;
  }

  /// <summary>
  /// Who is standing there right now.
  ///
  /// THE HEADING IS THE STATE. Everything in here evaporates the moment they walk away, so the section says so
  /// in its own caption rather than leaving each empty field to be misread as "this person has no race". When
  /// there is no row there are no fields at all — a section of dashes would invite exactly the reading the
  /// caption exists to prevent.
  /// </summary>
  private static void DrawLive(ScentRow? row, float scale)
  {
    UiTheme.SectionHeader("Right now", FontAwesomeIcon.Eye);
    UiTheme.TextWrappedColored(UiTheme.Muted, "From the live scan. Gone the moment they walk away.");
    ImGui.Dummy(new Vector2(0, 2f * scale));

    if (row is null)
    {
      UiTheme.TextWrappedColored(UiTheme.Muted,
        "Not nearby. Their job, race and Free Company are only readable while they are in range — this says "
        + "nothing about whether they have any.");
      return;
    }

    ImGui.TextUnformatted($"{row.JobAbbreviation} · Lv {row.Level}");

    if (row.RaceName is { Length: > 0 } race)
      ImGui.TextUnformatted(race);

    // The TAG, not the name, and the label says so. The client only ever hands over the five-character tag; the
    // full Free Company name lives on the Lodestone. Calling this "Free Company" flat would make the two look
    // like the same fact reported twice, one of them truncated.
    if (row.CompanyTag is { Length: > 0 } tag)
      ImGui.TextUnformatted($"«{tag}»");
    else
      UiTheme.TextWrappedColored(UiTheme.Muted, "No Free Company tag showing.");

    if (row.IsWatching)
      UiTheme.TextWrappedColored(Plugin.Configuration.ColorWatcher, "Looking at you right now.");
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
      PortraitState.Missing => "The Lodestone has nobody by this name on this world. They may have renamed or "
                             + "transferred — use Renamed? in settings to point the mark at who they are now.",
      PortraitState.Failed => "Could not reach the Lodestone. Showing their job instead.",
      _ => Plugin.Configuration.ShowLodestonePortraits
        ? "Showing their job."
        : "Lodestone portraits are switched off in settings.",
    };
    UiTheme.Tooltip(tip);
  }

  /// <summary>The ticks, the note and the colour. Carried over from the editor this window replaces, with its
  /// reasoning intact — see each comment.</summary>
  private void DrawMarkControls(WatcherKey key, MarkedPlayer? mark, float scale)
  {
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

    // Both at once is a contradiction the user is allowed to hold — the two flags stay independent so that
    // un-ignoring gives the focus back — but it must not be silent, or the row simply vanishes and the Focus
    // tick above looks broken.
    if (focus && ignore)
      UiTheme.TextWrappedColored(UiTheme.Muted, "Ignored, so Focus does nothing while both are ticked.");

    ImGui.Dummy(new Vector2(0, 4f * scale));
    ImGui.TextUnformatted("Note");

    ImGui.SetNextItemWidth(-1);
    if (ImGui.InputTextMultiline("##profilenote", ref _note, NoteMaxLength,
          new Vector2(0, ImGui.GetTextLineHeight() * 5f)))
      Plugin.Marks.Update(key, _worldName, m => m with { Note = _note });
    UiTheme.Tooltip("Only you ever see this. It is kept on disk until you delete it.");

    ImGui.Dummy(new Vector2(0, 4f * scale));

    // Colour FOLDS INTO the focus slot rather than becoming a sixth colour competing for the same cell — see
    // DrawRow's name-colour chain. So it is only meaningful on a focused player, and saying so beats a swatch
    // that silently does nothing.
    var color = mark?.Color ?? config.ColorFocused;
    if (ImGui.ColorEdit4("Colour", ref color, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaPreview))
      Plugin.Marks.Update(key, _worldName, m => m with { Color = color });

    ImGui.SameLine();

    // Mandatory, not a nicety: ColorEdit4 takes a non-null Vector4, so there is no path back to "no colour"
    // through the widget itself. Without this the default is unreachable the moment the user touches the swatch.
    using (ImRaii.Disabled(mark?.Color is null))
    {
      if (ImGui.SmallButton("Reset"))
        Plugin.Marks.Update(key, _worldName, m => m with { Color = null });
    }
    UiTheme.Tooltip("Back to the default focus colour.");

    if (!focus)
      UiTheme.TextWrappedColored(UiTheme.Muted, "Colour shows on focused players only.");
  }

  /// <summary>
  /// What they have actually done, as opposed to what the user wrote about them.
  ///
  /// Reads the live in-memory log, and does NOT persist it. How often someone stared at you is an observation
  /// about them, not something the user wrote — so it is shown here and forgotten at logout, exactly like the
  /// history pane it comes from. See <see cref="WatcherLog"/>.
  /// </summary>
  private static void DrawHistory(WatcherKey key, MarkedPlayer? mark)
  {
    ImGui.Separator();

    var stares = Plugin.WatcherLog.Snapshot().FirstOrDefault(entry => entry.Key == key);
    if (stares is null)
      DrawNoSightings();
    else
    {
      UiTheme.TextWrappedColored(UiTheme.Muted, $"Looked at you {stares.Count}x this session.");

      // One episode has no "first" and "most recently" — the two timestamps would be the same clock printed
      // twice, which reads as a rendering fault rather than as a single sighting.
      UiTheme.TextWrappedColored(UiTheme.Muted, stares.Count == 1
        ? $"At {stares.FirstSeen:HH:mm:ss}."
        : $"First at {stares.FirstSeen:HH:mm:ss}, most recently at {stares.LastSeen:HH:mm:ss}.");

      // Not "0s": the dwell is sampled per scan, so a glance shorter than one interval genuinely measures zero.
      // Printing 0s would report "they looked for no time at all" about someone who did look.
      UiTheme.TextWrappedColored(UiTheme.Muted, stares.TotalStareMs <= 0
        ? "Too briefly to time."
        : $"Held you as their target for {FormatDwell(stares.TotalStareMs)} in total.");
    }

    // The one durable observation, shown where it is made rather than only in a file. See MarkedPlayer.LastSeen.
    if (mark?.LastSeen is { } lastSeen)
      UiTheme.TextWrappedColored(UiTheme.Muted, ScentWindow.FormatLastSeen(lastSeen, mark.LastSeenZone));
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
  private static void DrawNoSightings()
  {
    var config = Plugin.Configuration;

    if (!config.KeepHistory)
    {
      UiTheme.TextWrappedColored(UiTheme.Warn,
        "Not remembering. History is switched off, so only people looking at you right now appear here — this "
        + "cannot tell you whether they did earlier.");
      return;
    }

    if (Plugin.WatcherLog.Forgotten is > 0 and var forgotten)
    {
      UiTheme.TextWrappedColored(UiTheme.Warn,
        $"{forgotten} {(forgotten == 1 ? "watcher has" : "watchers have")} already been dropped from this "
        + $"session's log — it keeps {Math.Max(1, config.HistoryLimit)}. If they were one of them, this says "
        + "nothing about them.");
      return;
    }

    if (!config.RecordWhileClosed && !Plugin.IsMainWindowOpen)
    {
      UiTheme.TextWrappedColored(UiTheme.Muted,
        "They have never targeted you while the Scent window was open — and staring is only recorded while it "
        + "is open.");
      return;
    }

    // The true never. The second sentence is mandatory: FirstSeen is the first time they TARGETED you, so a
    // null here must never be read as "never seen".
    UiTheme.TextWrappedColored(UiTheme.Muted,
      "They have never targeted you this session. That is not the same as never being near you — nothing here "
      + "records who was merely around.");
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
