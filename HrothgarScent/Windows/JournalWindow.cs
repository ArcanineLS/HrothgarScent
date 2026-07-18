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
/// The journal: everyone the plugin knows about, in one browsable place — searched, filtered by tag, and one
/// double-click from a full profile.
///
/// TWO SOURCES, TWO TABS. "Remembered" is the mark store — the people the user deliberately pointed at, which is
/// all the journal ever showed. "All" adds the opt-in <see cref="SeenLog"/> on top: everyone recorded by
/// proximity when <see cref="Configuration.RecordAllNearby"/> is on. A marked player who was also seen appears
/// once, as marked — the mark wins and supplies the tags and colour, the seen entry only supplements a
/// last-seen the mark may lack. Nothing here writes a stranger to disk; the scanner does that, gated, and this is
/// only the reader.
///
/// The rows are cached and rebuilt only when something they depend on changes — the All tab can hold thousands.
/// </summary>
public sealed class JournalWindow : Window
{
  private const int SearchMaxLength = 64;

  private enum JournalSort
  {
    Name,
    LastSeen,
    SeenCount,
  }

  private static readonly string[] SortNames = ["Name", "Last seen", "Times seen"];

  private const string TagPopupId = "##journal-tag-editor";
  private const string ClearPopupId = "##journal-clear-seen";

  private string _search = string.Empty;
  private JournalSort _sort = JournalSort.Name;

  /// <summary>All tab only: show only players who are NOT marked — the ones recorded purely by proximity. The
  /// concrete "filter for the recorded-nearby": All shows both, Remembered shows marked, this shows seen-only.</summary>
  private bool _unmarkedOnly;

  private readonly HashSet<string> _activeTags = new(StringComparer.OrdinalIgnoreCase);
  private string _tagInput = string.Empty;

  /// <summary>The cached, filtered, sorted rows and the signature that produced them. Rebuilt only when a term of
  /// that signature moves — the same staleness discipline ScentWindow.RebuildViewIfStale uses, and the reason the
  /// All tab can hold a large nearby log without rebuilding it sixty times a second.</summary>
  private List<Row> _rows = [];
  private List<string> _filterChips = [];
  private int _rowsSignature;

  /// <summary>A unified journal row over EITHER a mark or a seen entry, so the table draws one shape.</summary>
  private readonly record struct Row(
    WatcherKey Key,
    string FullName,
    string HomeWorldName,
    IReadOnlyList<string> Tags,
    DateTimeOffset? LastSeen,
    string LastSeenZone,
    int SeenCount,
    bool Marked,
    Vector4? Color,
    string Note,
    string Race)
  {
    /// <summary>Race and world as AUTO tags — computed, never stored on the mark, so they stay accurate and do not
    /// pollute the tag store. They filter, search and show alongside the real tags. Empty parts are dropped.</summary>
    public IEnumerable<string> AutoTags
    {
      get
      {
        if (Race.Length > 0) yield return Race;
        if (HomeWorldName.Length > 0) yield return HomeWorldName;
      }
    }
  }

  /// <summary>Double-click-to-collapse state; remembers the expanded size across a collapse. See
  /// <see cref="UiTheme.CollapseController"/>.</summary>
  private readonly UiTheme.CollapseController _collapse = new();

  private readonly WindowSizeConstraints _normalConstraints = new()
  {
    MinimumSize = new Vector2(540, 400),
    MaximumSize = new Vector2(1400, 2200),
  };

  public JournalWindow()
    : base("Scent Journal##hrothgarscent-journal",
        ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoScrollbar)
  {
    Size = new Vector2(660, 580);
    SizeCondition = ImGuiCond.FirstUseEver;
    SizeConstraints = _normalConstraints;
  }

  /// <summary>Gate #10 of the PvP defence, on the same terms as the profile's: the journal names people and is
  /// reachable with a keybind, so a window left open into a match would keep a roster of names on screen. It reads
  /// stores that are fully populated in PvP, so there is no empty snapshot behind it to save it.</summary>
  public override bool DrawConditions() => !Plugin.ClientState.IsPvP;

  public override void Draw()
  {
    var scale = ImGuiHelpers.GlobalScale;

    if (_collapse.Handle(this,
          UiTheme.DrawWindowTitleBar("Scent Journal", scale, () => IsOpen = false), _normalConstraints))
      return;

    if (!ImGui.BeginTabBar("##journalTabs"))
      return;

    if (ImGui.BeginTabItem("All"))
    {
      DrawTab(all: true, scale);
      ImGui.EndTabItem();
    }

    if (ImGui.BeginTabItem("Remembered"))
    {
      DrawTab(all: false, scale);
      ImGui.EndTabItem();
    }

    ImGui.EndTabBar();
  }

  private void DrawTab(bool all, float scale)
  {
    RebuildIfStale(all);

    var total = all ? Plugin.Marks.Count + Plugin.SeenLog.Count : Plugin.Marks.Count;
    if (total == 0)
    {
      DrawEmptyState(all);
      return;
    }

    DrawControls(all, scale);
    DrawTable(all, scale);
  }

  private static void DrawEmptyState(bool all)
  {
    if (all)
      UiTheme.TextWrappedColored(UiTheme.Muted, Plugin.Configuration.RecordAllNearby
        ? "Nobody yet. As players come near you they will be recorded here, and anyone you mark shows up too."
        : "Nobody yet. Mark players to remember them — or turn on \"Record every nearby player\" in settings to "
          + "log everyone you meet.");
    else
      UiTheme.TextWrappedColored(UiTheme.Muted,
        "Nobody remembered yet. Right-click a player in the Scent window — or their name anywhere the game shows "
        + "it, the friend list, Party Finder, the chat log — and pick Remember this Player.");
  }

  // ── Row building ──────────────────────────────────────────────────────────────────────────────────────────

  /// <summary>Rebuilds the filtered/sorted rows if any input has changed since last frame. The signature folds in
  /// both stores' revisions, the tab, and every filter/sort control — leaving one out would make a change appear
  /// to lag, the exact trap Configuration.FilterSignature documents.</summary>
  private void RebuildIfStale(bool all)
  {
    // The seen revision is folded in on BOTH tabs, not just All: the Remembered tab supplements a mark's
    // last-seen from the seen log (see FromMark), so it too goes stale if the log moves and this does not notice.
    var tagHash = _activeTags.Aggregate(17, (h, t) => h ^ StringComparer.OrdinalIgnoreCase.GetHashCode(t));
    var signature = HashCode.Combine(
      HashCode.Combine(all, Plugin.Marks.Index.Revision, Plugin.SeenLog.Index.Revision),
      HashCode.Combine(_search, (int)_sort, _unmarkedOnly && all, tagHash, _activeTags.Count));

    if (signature == _rowsSignature)
      return;

    _rowsSignature = signature;
    var built = BuildRows(all);
    _filterChips = GatherFilterChips(built);
    _rows = Filter(built, all);

    // Drop any active filter that no longer exists (its last holder was forgotten, retagged, or the tab changed),
    // so the filter cannot silently hide everyone on a chip nothing carries.
    _activeTags.RemoveWhere(t => !_filterChips.Contains(t, StringComparer.OrdinalIgnoreCase));
  }

  /// <summary>Every value that can filter the list — real tags plus the auto tags (race, world) — distinct and
  /// sorted, cached with the rows so the chip row is not recomputed per frame.</summary>
  private static List<string> GatherFilterChips(List<Row> rows)
  {
    var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var row in rows)
    {
      foreach (var tag in row.Tags)
        set.Add(tag);
      foreach (var auto in row.AutoTags)
        set.Add(auto);
    }

    return [.. set];
  }

  /// <summary>Marks, plus (on the All tab) the seen log beneath them — a marked player who was also seen appears
  /// once, the mark overwriting the seen row.</summary>
  private static List<Row> BuildRows(bool all)
  {
    var byKey = new Dictionary<WatcherKey, Row>();

    // The All tab always shows the seen log, whether or not recording is on RIGHT NOW — the file may hold players
    // from when it was. Recording governs writing, not viewing.
    if (all)
      foreach (var seen in Plugin.SeenLog.All())
        byKey[seen.Key] = FromSeen(seen);

    foreach (var mark in Plugin.Marks.All())
      byKey[mark.Key] = FromMark(mark, Plugin.SeenLog.Find(mark.Key));

    return [.. byKey.Values];
  }

  private static Row FromSeen(SeenPlayer seen) => new(
    seen.Key, seen.FullName, seen.HomeWorldName, [],
    seen.LastSeen, seen.LastSeenZone, seen.SeenCount,
    Marked: false, Color: null, Note: string.Empty, Race: seen.Race);

  private static Row FromMark(MarkedPlayer mark, SeenPlayer? seen) => new(
    mark.Key, mark.FullName, mark.HomeWorldName, mark.Tags,
    // Supplement from the seen log where the mark itself has nothing — e.g. RememberLastSeen off but
    // RecordAllNearby on, so the mark has no LastSeen while the seen log does. Race only ever comes from the seen
    // log (marks do not store it), so a marked player never seen has no race auto-tag, which is honest.
    mark.LastSeen ?? seen?.LastSeen,
    string.IsNullOrEmpty(mark.LastSeenZone) ? seen?.LastSeenZone ?? string.Empty : mark.LastSeenZone,
    mark.SeenCount > 0 ? mark.SeenCount : seen?.SeenCount ?? 0,
    Marked: true, mark.Color, mark.Note, Race: seen?.Race ?? string.Empty);

  private List<Row> Filter(List<Row> rows, bool all)
  {
    var term = _search.Trim();
    var query = rows.Where(r =>
      (!(_unmarkedOnly && all) || !r.Marked)
      && MatchesTags(r)
      && MatchesSearch(r, term));

    query = _sort switch
    {
      JournalSort.LastSeen => query.OrderByDescending(r => r.LastSeen ?? DateTimeOffset.MinValue),
      JournalSort.SeenCount => query.OrderByDescending(r => r.SeenCount).ThenBy(r => r.FullName, StringComparer.Ordinal),
      _ => query.OrderBy(r => r.FullName, StringComparer.Ordinal),
    };

    return [.. query];
  }

  // Real tags AND the auto tags (race, world) both satisfy a filter chip, so clicking a race or world chip
  // narrows to it just like a real tag.
  private bool MatchesTags(Row row)
    => _activeTags.Count == 0 || row.Tags.Any(_activeTags.Contains) || row.AutoTags.Any(_activeTags.Contains);

  private static bool MatchesSearch(Row row, string term)
    => term.Length == 0
    || row.FullName.Contains(term, StringComparison.OrdinalIgnoreCase)
    || row.Note.Contains(term, StringComparison.OrdinalIgnoreCase)
    || row.Race.Contains(term, StringComparison.OrdinalIgnoreCase)
    || row.Tags.Any(t => t.Contains(term, StringComparison.OrdinalIgnoreCase));

  // ── Controls ──────────────────────────────────────────────────────────────────────────────────────────────

  private void DrawControls(bool all, float scale)
  {
    UiTheme.TextWrappedColored(UiTheme.Muted, all
      ? $"{_rows.Count} shown. Marked players are coloured; the rest are logged by proximity. Double-click to open a profile."
      : $"{_rows.Count} remembered. Double-click anyone to open their profile.");
    ImGui.Dummy(new Vector2(0, 3f * scale));

    var sortWidth = 140f * scale;
    ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - sortWidth - 8f * scale);
    ImGui.InputTextWithHint("##journalSearch", "Search name, world, note, or tag…", ref _search, SearchMaxLength);

    ImGui.SameLine(0, 8f * scale);
    ImGui.SetNextItemWidth(sortWidth);
    var sortIndex = (int)_sort;
    if (ImGui.Combo("##journalSort", ref sortIndex, SortNames, SortNames.Length))
      _sort = (JournalSort)sortIndex;
    UiTheme.Tooltip("Order the list.");

    if (all)
      DrawSeenControls(scale);

    DrawTagFilter(scale);

    ImGui.Dummy(new Vector2(0, 2f * scale));
  }

  /// <summary>All tab only: the "unmarked only" filter, and the button to clear the recorded-nearby log.</summary>
  private void DrawSeenControls(float scale)
  {
    ImGui.Dummy(new Vector2(0, 2f * scale));

    ImGui.Checkbox("Unmarked only", ref _unmarkedOnly);
    UiTheme.Tooltip("Show only players recorded by proximity — the ones you have not marked.");

    if (Plugin.SeenLog.Count == 0)
      return;

    ImGui.SameLine(0, 16f * scale);
    if (ImGui.SmallButton($"Clear recorded ({Plugin.SeenLog.Count})"))
      ImGui.OpenPopup(ClearPopupId);
    UiTheme.Tooltip("Forget every player recorded by proximity. Your marked players are not touched.");

    DrawClearPopup();
  }

  private static void DrawClearPopup()
  {
    if (!ImGui.BeginPopup(ClearPopupId))
      return;

    UiTheme.TextWrappedColored(UiTheme.Warn,
      $"Forget all {Plugin.SeenLog.Count} players recorded by proximity? Marked players are kept.");
    ImGui.Dummy(new Vector2(0, 4f * ImGuiHelpers.GlobalScale));

    if (ImGui.Button("Clear them"))
    {
      Plugin.SeenLog.Clear();
      ImGui.CloseCurrentPopup();
    }

    ImGui.SameLine();
    if (ImGui.Button("Cancel"))
      ImGui.CloseCurrentPopup();

    ImGui.EndPopup();
  }

  /// <summary>
  /// The filter chips — real tags AND the auto tags (race, world) — each toggling a filter. Wrapped, because
  /// "record everyone" means the world set alone can run to dozens; a non-wrapping row would run them off the
  /// edge where they cannot be clicked. Absent entirely when there is nothing to filter by.
  /// </summary>
  private void DrawTagFilter(float scale)
  {
    if (_filterChips.Count == 0)
      return;

    ImGui.Dummy(new Vector2(0, 2f * scale));
    ImGui.AlignTextToFramePadding();
    UiTheme.TextWrappedColored(UiTheme.Muted, "Filter:");

    var windowRight = ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X;
    var gap = 4f * scale;

    for (var i = 0; i < _filterChips.Count; i++)
    {
      // The first chip rides the label's line; the rest are placed by the fit test at the end of the previous
      // iteration, so nothing here starts a line — that is the wrap.
      if (i == 0)
        ImGui.SameLine(0, gap);

      DrawFilterChip(_filterChips[i]);

      if (NextFitsOnLine(i + 1 < _filterChips.Count ? _filterChips[i + 1] : "Clear", windowRight, gap))
        ImGui.SameLine(0, gap);
    }

    if (_activeTags.Count > 0 && ImGui.SmallButton("Clear##clearTags"))
      _activeTags.Clear();
  }

  /// <summary>One filter chip — filled when active. Clicking toggles it in the active set.</summary>
  private void DrawFilterChip(string label)
  {
    var active = _activeTags.Contains(label);
    ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(UiTheme.AccentPurple.X, UiTheme.AccentPurple.Y,
      UiTheme.AccentPurple.Z, active ? 0.55f : 0.14f));
    ImGui.PushStyleColor(ImGuiCol.Text, active ? new Vector4(1f, 1f, 1f, 1f) : UiTheme.Muted);

    if (ImGui.SmallButton($"{label}##filter{label}"))
    {
      if (!_activeTags.Add(label))
        _activeTags.Remove(label);
    }

    ImGui.PopStyleColor(2);
  }

  /// <summary>Whether a chip of the given label would still fit on the current line after the item just drawn —
  /// the wrap decision, made against the last item's right edge (the ImGui idiom for wrapping a button row).</summary>
  private static bool NextFitsOnLine(string label, float windowRight, float gap)
  {
    var style = ImGui.GetStyle();
    var width = ImGui.CalcTextSize(label).X + style.FramePadding.X * 2f + gap;
    return ImGui.GetItemRectMax().X + width < windowRight;
  }

  // ── Table ─────────────────────────────────────────────────────────────────────────────────────────────────

  private void DrawTable(bool all, float scale)
  {
    if (_rows.Count == 0)
    {
      UiTheme.TextWrappedColored(UiTheme.Muted, "Nobody matches. Clear the search, the tag filter or the toggle.");
      return;
    }

    var tableFlags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY
                   | ImGuiTableFlags.NoSavedSettings | ImGuiTableFlags.SizingStretchProp;

    if (!ImGui.BeginTable("##journalTable", 4, tableFlags, ImGui.GetContentRegionAvail()))
      return;

    ImGui.TableSetupScrollFreeze(0, 1);
    ImGui.TableSetupColumn("Player", ImGuiTableColumnFlags.WidthStretch, 0.42f);
    ImGui.TableSetupColumn("Tags", ImGuiTableColumnFlags.WidthStretch, 0.34f);
    ImGui.TableSetupColumn("Seen", ImGuiTableColumnFlags.WidthStretch, 0.24f);
    ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 28f * scale);
    ImGui.TableHeadersRow();

    // The row LIST is cached (RebuildIfStale), so this loop is the only per-frame cost; the table's ScrollY clips
    // the rendering of off-screen rows. At the default cap this is nothing; a user who raises the cap into the
    // thousands pays for the walk, which is their trade to make.
    foreach (var row in _rows)
      DrawRow(row, scale);

    ImGui.EndTable();
  }

  private void DrawRow(Row row, float scale)
  {
    ImGui.TableNextRow();
    ImGui.PushID($"{row.Key.Name}#{row.Key.HomeWorldId}");

    // Player — coloured by mark (its own colour, else the default) or muted for a proximity-only row, so the two
    // sources read apart at a glance. Double-click opens the profile.
    ImGui.TableNextColumn();
    ImGui.AlignTextToFramePadding();
    var nameColor = row.Marked ? row.Color ?? Plugin.Configuration.ColorDefault : UiTheme.Muted;
    ImGui.PushStyleColor(ImGuiCol.Text, nameColor);
    ImGui.Selectable(row.FullName, false, ImGuiSelectableFlags.AllowDoubleClick);
    ImGui.PopStyleColor();
    if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
      Plugin.Profile?.Open(row.Key, row.HomeWorldName);
    DrawRowContextMenu(row);
    DrawRowTooltip(row);

    // Tags.
    ImGui.TableNextColumn();
    DrawTagCell(row, scale);

    // Seen.
    ImGui.TableNextColumn();
    ImGui.AlignTextToFramePadding();
    UiTheme.TextWrappedColored(UiTheme.Muted, SeenSummary(row));

    // Open profile.
    ImGui.TableNextColumn();
    if (IconButton(FontAwesomeIcon.ExternalLinkAlt, "open"))
      Plugin.Profile?.Open(row.Key, row.HomeWorldName);
    UiTheme.Tooltip("Open their profile.");

    ImGui.PopID();
  }

  /// <summary>Right-click a row to remove that player from the journal — the mark (if any) and the proximity
  /// entry both. The one place a single seen-only player can be dropped without waiting for eviction.</summary>
  private static void DrawRowContextMenu(Row row)
  {
    if (!ImGui.BeginPopupContextItem("##rowctx"))
      return;

    if (ImGui.MenuItem("Open profile"))
      Plugin.Profile?.Open(row.Key, row.HomeWorldName);

    if (ImGui.MenuItem("Remove from journal"))
    {
      // Both stores: a player can be in either or both, and "remove from journal" means gone from the view.
      Plugin.Marks.Remove(row.Key);
      Plugin.SeenLog.Remove(row.Key);
    }

    ImGui.EndPopup();
  }

  private void DrawTagCell(Row row, float scale)
  {
    ImGui.AlignTextToFramePadding();

    // Auto tags (race, world) in blue, then the user's real tags in purple, so the two read apart. A dash only
    // when there is genuinely neither.
    var auto = string.Join(", ", row.AutoTags);
    var real = string.Join(", ", row.Tags);

    if (auto.Length == 0 && real.Length == 0)
    {
      UiTheme.TextWrappedColored(UiTheme.Muted, "—");
    }
    else
    {
      if (auto.Length > 0)
        UiTheme.TextWrappedColored(UiTheme.AccentBlue, auto);
      if (real.Length > 0)
      {
        if (auto.Length > 0)
          ImGui.SameLine(0, 4f * scale);
        UiTheme.TextWrappedColored(UiTheme.AccentPurple, real);
      }
    }

    ImGui.SameLine(0, 6f * scale);
    if (IconButton(FontAwesomeIcon.Tag, "edittags"))
    {
      _tagInput = string.Empty;
      ImGui.OpenPopup(TagPopupId);
    }
    UiTheme.Tooltip(row.Marked ? "Add or remove tags." : "Add a tag — this also remembers them.");

    DrawTagEditor(row);
  }

  /// <summary>The tag editor popup. Adding a tag to a proximity-only row MARKS the player (Marks.AddTag creates a
  /// record), which then moves them into Remembered — filing someone is a deliberate act, exactly like a note.</summary>
  private void DrawTagEditor(Row row)
  {
    if (!ImGui.BeginPopup(TagPopupId))
      return;

    var scale = ImGuiHelpers.GlobalScale;

    ImGui.TextColored(UiTheme.AccentBlue, $"Tags for {row.Key.Name}");
    ImGui.Separator();

    ImGui.SetNextItemWidth(200f * scale);
    var entered = ImGui.InputTextWithHint("##newtag", "New tag…", ref _tagInput,
      MarkStore.TagMaxLength, ImGuiInputTextFlags.EnterReturnsTrue);

    ImGui.SameLine();
    using (ImRaii.Disabled(string.IsNullOrWhiteSpace(_tagInput) || row.Tags.Count >= MarkStore.MaxTagsPerPlayer))
    {
      if ((ImGui.Button("Add") || entered) && !string.IsNullOrWhiteSpace(_tagInput))
      {
        Plugin.Marks.AddTag(row.Key, row.HomeWorldName, _tagInput);
        _tagInput = string.Empty;
      }
    }

    if (row.Tags.Count >= MarkStore.MaxTagsPerPlayer)
      UiTheme.TextWrappedColored(UiTheme.Muted, $"That's the most tags one player can carry ({MarkStore.MaxTagsPerPlayer}).");

    if (row.Tags.Count > 0)
    {
      ImGui.Dummy(new Vector2(0, 4f * scale));
      foreach (var tag in row.Tags)
      {
        ImGui.PushID($"tag{tag}");
        if (IconButton(FontAwesomeIcon.Times, "removetag"))
          Plugin.Marks.RemoveTag(row.Key, tag);
        ImGui.SameLine(0, 6f * scale);
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(tag);
        ImGui.PopID();
      }
    }

    ImGui.EndPopup();
  }

  private static void DrawRowTooltip(Row row)
  {
    if (!ImGui.IsItemHovered())
      return;

    var seen = row.LastSeen is { } lastSeen
      ? ScentWindow.FormatLastSeen(lastSeen, row.LastSeenZone)
      : "Not seen near you.";
    var source = row.Marked ? "Remembered." : "Recorded by proximity — not marked.";
    UiTheme.Tooltip($"{seen}\r\n{source}\r\n\r\nDouble-click to open their profile, right-click for more.");
  }

  private static string SeenSummary(Row row)
  {
    if (row.LastSeen is not { } lastSeen)
      return "—";

    var ago = ScentWindow.FormatAgo(lastSeen);
    return row.SeenCount > 1 ? $"{row.SeenCount}× · {ago}" : ago;
  }

  private static bool IconButton(FontAwesomeIcon icon, string id)
  {
    using var pushed = ImRaii.PushId(id);
    using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
      return ImGui.Button(icon.ToIconString());
  }
}
