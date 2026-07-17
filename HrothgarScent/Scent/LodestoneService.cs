using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Interface.Textures.TextureWraps;

namespace HrothgarScent.Scent;

/// <summary>What the portrait lookup has to say about one player.</summary>
public enum PortraitState : byte
{
  /// <summary>Nobody has asked. No request has been made and none will be until someone does.</summary>
  Idle,

  /// <summary>In flight.</summary>
  Looking,

  /// <summary>Found, downloaded, and ready to draw.</summary>
  Ready,

  /// <summary>
  /// The Lodestone has no character by that name on that world.
  ///
  /// Distinct from <see cref="Failed"/> because it is not an error and the user can act on it: the overwhelming
  /// cause is a mark whose player has renamed or transferred, which is exactly what the Renamed? box exists to
  /// repair. Collapsing the two would report a fixable staleness as a network fault.
  /// </summary>
  Missing,

  /// <summary>The lookup broke — offline, blocked, or the page changed shape. Says nothing about the player.</summary>
  Failed,
}

/// <summary>One player's portrait, and how it got that way.</summary>
public sealed class Portrait
{
  public PortraitState State { get; set; } = PortraitState.Idle;
  public IDalamudTextureWrap? Texture { get; set; }

  /// <summary>The Lodestone character id, once known. Display only — the identity this plugin keys on is
  /// still Name+HomeWorld, and this is never used to match anybody.</summary>
  public uint CharacterId { get; set; }
}

/// <summary>
/// Fetches a player's face from the Lodestone, and nothing else.
///
/// WHY THIS IS NOT A PRIVACY REGRESSION, stated plainly because the plugin's other refusals are strict enough
/// that this needs justifying. The Lodestone is the player's OWN public profile, published by them, on Square
/// Enix's own site. This plugin already ships a "Search on Lodestone" row action that sends the user's browser
/// to precisely this page — so the line was crossed deliberately, long before this file existed, and all this
/// does is render one image from it in-window instead of making the user go and look. Every request here is
/// user-initiated: <see cref="Request"/> is called only when a profile is OPENED, one player at a time, never
/// for a list, never on a timer, never in the background. Nothing is written to disk; the cache dies at logout,
/// on the same terms as the watcher log and for the same reason — the stranger did not consent to a file.
///
/// NO NETSTONE. The dependency was refused for this project on dependency-count grounds, and it stays refused:
/// what it offers over one HttpClient and one Regex is a full scraper for pages this plugin will never read.
///
/// THE SEARCH CAN RETURN THE WRONG PERSON, and that is the whole reason the matching below is as strict as it
/// is. A zero-result Lodestone search still renders a sidebar of unrelated characters — verified: searching a
/// real character against the wrong world returns "Your search yielded no results" AND a page containing four
/// other people's names, ids and faces. Anything that took "the first character on the page" would put a
/// stranger's face on a watcher's profile. In a plugin whose entire product is telling you WHO is looking at
/// you, showing the wrong face is worse than showing none, so a result is accepted only when the Lodestone's own
/// total is non-zero, the entry came out of a real result anchor, and BOTH the name and the home world match
/// what was asked for.
/// </summary>
public sealed class LodestoneService : IDisposable
{
  /// <summary>
  /// One client for the plugin's lifetime. A per-request HttpClient exhausts sockets under TIME_WAIT — the
  /// classic .NET leak — and this makes at most a handful of requests a session anyway.
  /// </summary>
  private static readonly HttpClient Http = new(new HttpClientHandler { UseCookies = false })
  {
    Timeout = TimeSpan.FromSeconds(10),
  };

  /// <summary>
  /// A real result anchor, and the ONLY thing here that may be treated as a search hit.
  ///
  /// The class="entry__link" href is what separates a result from the sidebar the Lodestone renders on a
  /// zero-result page. Non-greedy to the closing tag so one match is one character, never a run of them.
  /// </summary>
  private static readonly Regex EntryPattern = new(
    """<a href="/lodestone/character/(\d+)/"[^>]*class="entry__link"[^>]*>(.*?)</a>""",
    RegexOptions.Singleline | RegexOptions.Compiled);

  private static readonly Regex TotalPattern = new("""parts__total">(\d+)\s*Total""", RegexOptions.Compiled);
  private static readonly Regex NamePattern = new("""entry__name">([^<]*)""", RegexOptions.Compiled);
  private static readonly Regex WorldPattern = new("""entry__world">(?:<i[^>]*></i>)?\s*([^<]*)""", RegexOptions.Compiled);

  /// <summary>
  /// The face thumbnail. The Lodestone serves it from img1..imgN, so the digit is not pinned.
  ///
  /// The closing quote is not in the pattern: [^"]+ already stops at it, and spelling it out would put four
  /// consecutive quotes against the raw literal's own delimiter.
  /// </summary>
  private static readonly Regex FacePattern = new(
    """src="(https://img\d\.finalfantasyxiv\.com/f/[^"]+)""", RegexOptions.Compiled);

  /// <summary>
  /// Guards <see cref="_cache"/>. Written by Draw (which calls <see cref="Request"/>) and by the completion of
  /// an async lookup on a thread-pool thread, so the two genuinely race. Uncontended in practice — a handful of
  /// entries, touched on a click.
  /// </summary>
  private readonly object _gate = new();

  /// <summary>
  /// Every player asked about this session. In memory, never persisted: see the type's own note.
  ///
  /// Doubles as the single-flight record. An entry moves to <see cref="PortraitState.Looking"/> BEFORE the
  /// request starts, so a profile window redrawing at sixty frames a second asks once rather than sixty times —
  /// which would be the plugin hammering Square Enix's web server for as long as a window stayed open.
  /// </summary>
  private readonly Dictionary<WatcherKey, Portrait> _cache = [];

  private bool _disposed;

  /// <summary>
  /// What is known about this player's face right now. Safe to call every frame; never starts anything.
  /// </summary>
  public Portrait Get(WatcherKey key)
  {
    lock (_gate)
      return _cache.TryGetValue(key, out var portrait) ? portrait : new Portrait();
  }

  /// <summary>
  /// Asks the Lodestone for this player's face, once.
  ///
  /// The ONLY entry point that touches the network, and it is called from exactly one place: a profile being
  /// opened by a user. Do not call it from a list, a scan, or anything that runs without a click behind it.
  /// </summary>
  /// <param name="worldName">
  /// The home world to match on. Required, and not merely for the query: a name alone is not an identity — two
  /// players on different worlds can share one, and this plugin already keys on Name+HomeWorld everywhere else
  /// for that reason. No world, no lookup.
  /// </param>
  public void Request(WatcherKey key, string worldName)
  {
    if (!Plugin.Configuration.ShowLodestonePortraits || string.IsNullOrEmpty(worldName))
      return;

    lock (_gate)
    {
      // Anything but Idle means it has been asked already — in flight, answered, or answered badly. A retry
      // would need a user gesture, not a repaint.
      if (_cache.TryGetValue(key, out var existing) && existing.State != PortraitState.Idle)
        return;

      _cache[key] = new Portrait { State = PortraitState.Looking };
    }

    // Fire-and-forget on purpose: nothing waits for a face. The continuation writes the cache and Draw picks it
    // up on whatever frame it lands. Exceptions cannot escape — Fetch catches its own.
    _ = Task.Run(() => Fetch(key, worldName));
  }

  private async Task Fetch(WatcherKey key, string worldName)
  {
    try
    {
      var url = $"https://{Region()}.finalfantasyxiv.com/lodestone/character/"
              + $"?q={Uri.EscapeDataString(key.Name)}&worldname={Uri.EscapeDataString(worldName)}";

      var html = await Http.GetStringAsync(url).ConfigureAwait(false);

      if (Match(html, key, worldName) is not { } hit)
      {
        Settle(key, PortraitState.Missing, null, 0);
        return;
      }

      var bytes = await Http.GetByteArrayAsync(hit.Face).ConfigureAwait(false);

      // Dalamud owns the upload; this just hands it bytes. Not marshalled to the framework thread because
      // CreateFromImageAsync is explicitly the async, any-thread door.
      var texture = await Plugin.Textures.CreateFromImageAsync(bytes).ConfigureAwait(false);

      Settle(key, PortraitState.Ready, texture, hit.Id);
    }
    catch (Exception ex)
    {
      // Offline, blocked, rate-limited, or the page changed shape. Warning, not Error: a missing decoration is
      // not a fault the user has to do anything about, and the profile says so itself.
      Plugin.Log.Warning(ex, "Lodestone lookup failed");
      Settle(key, PortraitState.Failed, null, 0);
    }
  }

  /// <summary>
  /// Picks the one entry that is genuinely this player, or nothing.
  ///
  /// THREE independent conditions, and none is redundant. The total guards the zero-result page whose sidebar
  /// still carries strangers. The anchor pattern guards against reading that sidebar even so. The name and world
  /// comparison guards the case the other two cannot see: a real, non-empty result set that simply does not
  /// contain the person asked for, which is what a fuzzy name match returns.
  /// </summary>
  private static (uint Id, string Face)? Match(string html, WatcherKey key, string worldName)
  {
    if (TotalPattern.Match(html) is not { Success: true } total || total.Groups[1].Value == "0")
      return null;

    foreach (Match entry in EntryPattern.Matches(html))
    {
      var body = entry.Groups[2].Value;

      var name = NamePattern.Match(body);
      var world = WorldPattern.Match(body);
      var face = FacePattern.Match(body);
      if (!name.Success || !world.Success || !face.Success)
        continue;

      // Ordinal, never a culture compare: these are game names, and a Turkish-I fold on someone's name would
      // match the wrong human. Same rule the rest of the plugin's identity comparisons follow.
      if (!string.Equals(name.Groups[1].Value.Trim(), key.Name, StringComparison.Ordinal))
        continue;

      // The Lodestone prints "Shinryu [Meteor]" — world then data centre. Only the world is ours to check; the
      // DC is a fact about the world, not about the player, and matching it would break the day one moves.
      var listed = world.Groups[1].Value.Trim();
      var bracket = listed.IndexOf('[');
      if (bracket > 0)
        listed = listed[..bracket].Trim();

      if (!string.Equals(listed, worldName, StringComparison.Ordinal))
        continue;

      return uint.TryParse(entry.Groups[1].Value, out var id) ? (id, face.Groups[1].Value) : null;
    }

    return null;
  }

  private void Settle(WatcherKey key, PortraitState state, IDalamudTextureWrap? texture, uint id)
  {
    lock (_gate)
    {
      // Disposed while this was in flight — an unload, or a logout. The texture is ours and nothing will ever
      // draw it, so it goes here rather than into a dictionary nobody reads again.
      if (_disposed)
      {
        texture?.Dispose();
        return;
      }

      _cache[key] = new Portrait { State = state, Texture = texture, CharacterId = id };
    }
  }

  /// <summary>The Lodestone subdomain, from the same setting the Search on Lodestone action already uses, so one
  /// choice governs both and they cannot disagree about which region's site to read.</summary>
  private static string Region() => Plugin.Configuration.LodestoneRegion switch
  {
    LodestoneRegion.NorthAmerica => "na",
    LodestoneRegion.Japan => "jp",
    LodestoneRegion.Germany => "de",
    LodestoneRegion.France => "fr",
    _ => "eu",
  };

  /// <summary>
  /// Drops every face. Called at logout as well as at unload — the cache is session state about strangers, and
  /// it dies with the session for the same reason the watcher log does.
  /// </summary>
  public void Clear()
  {
    lock (_gate)
    {
      foreach (var portrait in _cache.Values)
        portrait.Texture?.Dispose();
      _cache.Clear();
    }
  }

  public void Dispose()
  {
    lock (_gate)
      _disposed = true;

    Clear();
  }
}
