using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
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
/// State of the character-PAGE fetch — a machine of its OWN, and deliberately not <see cref="PortraitState"/>.
///
/// The face lookup and the character-page lookup are two independent network facts; collapsing them erases
/// data. Routing a page 404 into <see cref="PortraitState.Missing"/> would fail DrawAvatar's
/// { State: Ready, Texture: {} } test and wipe the face the plugin already downloaded and drew — the window
/// asserting a person it just proved exists does not. Hence a second enum, a second dictionary, a second
/// settle; <see cref="Portrait"/> and <see cref="LodestoneService.Settle"/> are never touched.
/// </summary>
public enum ProfileFetchState : byte
{
  /// <summary>Nobody has clicked Load. No request has been made and none will be until someone does.</summary>
  Idle,

  /// <summary>In flight.</summary>
  Looking,

  /// <summary>The page was fetched AND parsed.</summary>
  Ready,

  /// <summary>
  /// The character id 404s: the page is gone — the character deleted, or renamed hard enough that Square Enix
  /// dropped it. Its OWN state, never <see cref="PortraitState.Missing"/>, whose flip would erase the face.
  /// </summary>
  Gone,

  /// <summary>Network, rate limit, or the page changed shape. Says NOTHING about the player.</summary>
  Failed,
}

/// <summary>
/// One player's public Lodestone character page, parsed.
///
/// Every string is null until proven present, and a null under <see cref="ProfileFetchState.Ready"/> is a
/// MEANINGFUL absence, never a parse gap — the page sentinel and identity re-check (see
/// <see cref="LodestoneService"/>) guarantee that a Ready profile is a real page about the right person, so an
/// absent field genuinely was not on it. Each member says what its own null means.
/// </summary>
public sealed class CharacterProfile
{
  public ProfileFetchState State { get; set; } = ProfileFetchState.Idle;

  /// <summary>Never null under Ready: Race/Clan/Gender is the page sentinel, so no race would mean no page.</summary>
  public string? Race { get; set; }
  public string? Clan { get; set; }

  /// <summary>Mapped to "Female"/"Male" — never the ♀/♂ glyph, which is tofu in Dalamud's default font.</summary>
  public string? Gender { get; set; }

  /// <summary>null under Ready == genuinely NOT in a Free Company (verified: no FC renders no block at all, never
  /// an empty one). The only source of the FC NAME — the live row carries only a ≤5-char tag.</summary>
  public string? FreeCompanyName { get; set; }

  /// <summary>null under Ready == no title equipped (verified).</summary>
  public string? Title { get; set; }

  /// <summary>null under Ready == "did not say" — NOT "none", which the page cannot express, and which a non-NA
  /// region would also return for a fully enlisted player (the anchor is the English title).</summary>
  public string? GrandCompany { get; set; }
  public string? GrandCompanyRank { get; set; }

  public string? Nameday { get; set; }
  public string? Guardian { get; set; }

  /// <summary>Where they STARTED, not where they are. Kept away from anything about location for that reason.</summary>
  public string? CityState { get; set; }

  /// <summary>
  /// The player's own Lodestone self-introduction — their public bio. Free text they wrote, with &lt;br&gt;
  /// turned into newlines and entities decoded. Null under Ready == they left it blank (verified: the div is
  /// present but empty, or absent), so the Comment section omits itself rather than drawing an empty box.
  /// </summary>
  public string? SelfIntroduction { get; set; }

  /// <summary>Empty under Ready == the page changed shape; all 34 slots always render, or none do.</summary>
  public IReadOnlyList<JobSlot> Jobs { get; set; } = [];

  /// <summary>
  /// The three Free Company crest layer URLs — background, emblem, frame — in DOM order, which is also bottom-to-
  /// top draw order. Empty when the character has no Free Company.
  ///
  /// Scoped to the crest__image div at parse time, NOT captured page-wide: the character page also carries a
  /// "top Free Companies" ranking feed of stranger crests (verified: the company-less capture has nine such
  /// _40x40 PNGs and no crest block), and an unscoped scrape would paint one of THOSE on a company-less player.
  /// Same sidebar trap the rest of this parser is written against, in image form.
  /// </summary>
  public IReadOnlyList<string> FreeCompanyCrestUrls { get; set; } = [];

  /// <summary>
  /// The crest layers downloaded, or null until fetched (and null when there is no FC, or a layer failed). Drawn
  /// overlaid on ONE rect, layer 0 first. THE ONE THING <see cref="LodestoneService"/>'s profile cache holds that
  /// owns a GPU texture — so unlike every string here it must be disposed, in Clear and on a generation miss, or
  /// the logout race leaks it exactly as the face would.
  /// </summary>
  public IReadOnlyList<IDalamudTextureWrap>? FreeCompanyCrest { get; set; }
}

/// <summary>
/// State of the FIELD-OPERATION fetch — Eureka, Bozja and Occult Crescent progress, which lives ONLY on the
/// character's /class_job/ sub-page and never on the main page. A machine of its own, mirroring
/// <see cref="ProfileFetchState"/>, because it is a third independent network fact: a second on-demand request,
/// fired only when the Jobs tab is actually viewed, so a profile opened only to Info or Notes never spends it.
/// </summary>
public enum FieldOpFetchState : byte
{
  /// <summary>Nobody has viewed the Jobs tab (or clicked Load). No request made and none will be until they do.</summary>
  Idle,

  /// <summary>In flight.</summary>
  Looking,

  /// <summary>The sub-page was fetched AND its identity re-checked. Any of the three levels may still be null —
  /// that is a character with no field-op progress, a legitimate Ready, not a parse gap.</summary>
  Ready,

  /// <summary>The sub-page 404s. Rare — it exists whenever the main page did — and says nothing about them.</summary>
  Gone,

  /// <summary>Network, rate limit, the page changed shape, OR it failed the identity re-check. Says NOTHING about
  /// the player: a page that is about someone else settles here, never Ready-with-null-levels.</summary>
  Failed,
}

/// <summary>
/// One player's field-operation progress, parsed from the /class_job/ sub-page. Each level null == that content
/// not started, a real state omitted from the display, never a placeholder. Holds no textures — strings and ints
/// only — so its cache only needs clearing, not disposing.
/// </summary>
public sealed class FieldOpProgress
{
  public FieldOpFetchState State { get; set; } = FieldOpFetchState.Idle;

  /// <summary>Eureka's "Elemental Level".</summary>
  public int? ElementalLevel { get; set; }

  /// <summary>Bozja's "Resistance Rank".</summary>
  public int? ResistanceRank { get; set; }

  /// <summary>Occult Crescent's "Knowledge Level".</summary>
  public int? KnowledgeLevel { get; set; }

  /// <summary>
  /// Occult Crescent's Phantom Jobs, in page order. Empty when the character has never entered it, which is the
  /// same honest "no progress" as a null level — the section just shows nothing.
  /// </summary>
  public IReadOnlyList<PhantomJob> PhantomJobs { get; set; } = [];
}

/// <summary>
/// One Occult Crescent Phantom Job. <paramref name="Level"/> is the raw Lodestone string ("Lv. 6") rather than
/// an int, because the page prints it that way and it is display-only — nothing computes on it, so re-parsing to
/// a number and reformatting would just be a place to introduce a bug. <paramref name="Mastered"/> is the
/// page's own "MASTERED" flag.
/// </summary>
public readonly record struct PhantomJob(string Name, string Level, bool Mastered);

/// <summary>
/// One job slot from the character page. <paramref name="Level"/> null == the Lodestone printed "-": not
/// unlocked or not levelled, a REAL state distinct from a parse failure. <paramref name="Slot"/> is the stable
/// key — the tooltip text is not, reading "Gladiator" unlevelled and "Paladin / Gladiator" levelled.
/// </summary>
public readonly record struct JobSlot(int Slot, string Name, int? Level);

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
  /// Caps how many Lodestone requests are in flight AT ONCE, across every player and all three fetch paths.
  ///
  /// Opening one profile can burst up to ~7 requests — the face search and its image, the character page and its
  /// three FC-crest layers, and the class_job sub-page — and clicking through several profiles in a row would
  /// otherwise fire them all at once and trip the Lodestone's own rate limiter, which comes back as
  /// <see cref="ProfileFetchState.Failed"/> for everyone. Two at a time paces the burst without fixed delays: the
  /// requests queue on the semaphore and drain in order, and the ten-second <see cref="Http"/> timeout only starts
  /// once a request is actually let through, so waiting in the queue never counts as a timeout. Static, so the cap
  /// is plugin-wide.
  /// </summary>
  private static readonly SemaphoreSlim RequestGate = new(2, 2);

  /// <summary>A GET-string through <see cref="RequestGate"/> — the paced door every network read here goes through
  /// instead of calling <see cref="Http"/> directly.</summary>
  private static async Task<string> GetStringPaced(string url)
  {
    await RequestGate.WaitAsync().ConfigureAwait(false);
    try
    {
      return await Http.GetStringAsync(url).ConfigureAwait(false);
    }
    finally
    {
      RequestGate.Release();
    }
  }

  /// <summary>A GET-bytes through <see cref="RequestGate"/>. The texture upload that usually follows is NOT gated —
  /// only the network read is — so a slow upload never holds a request slot.</summary>
  private static async Task<byte[]> GetBytesPaced(string url)
  {
    await RequestGate.WaitAsync().ConfigureAwait(false);
    try
    {
      return await Http.GetByteArrayAsync(url).ConfigureAwait(false);
    }
    finally
    {
      RequestGate.Release();
    }
  }

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

  // ── The character page. Every pattern is anchored to a MAIN-CONTENT class, never a bare href or img: the page
  //    carries a global activity feed of ~5 stranger Free Companies and ~20 stranger faces, and an unanchored
  //    scrape returns one of them with full confidence. Verified against two live captures. ─────────────────

  /// <summary>
  /// Race / Clan / Gender — one &lt;p&gt; split by &lt;br&gt;, all three or none. This is the PAGE SENTINEL: it
  /// is present on every valid character page, so its absence means the page is not one, and nothing is read
  /// from a page we cannot identify. g1=Race, g2=Clan (trailing space before the '/', so a Trim is mandatory),
  /// g3=the gender glyph.
  /// </summary>
  private static readonly Regex RaceClanGenderPattern = new(
    """character-block__title">Race/Clan/Gender</p>\s*<p class="character-block__name">([^<]*)<br\s*/?>([^/<]*)/\s*([^<]*)</p>""",
    RegexOptions.Compiled);

  /// <summary>
  /// Free Company. Anchored on character__freecompany__name, NOT the title list — "Free Company" never appears
  /// there, so a title-keyed scrape returns nothing forever. The &lt;p&gt; label is matched as [^&lt;]* so a
  /// region change cannot break it. g2=name; the id (g1) is deliberately unused (spec 4-F, display-only ethos).
  /// </summary>
  private static readonly Regex FreeCompanyPattern = new(
    """character__freecompany__name">\s*<p>[^<]*</p>\s*<h4>\s*<a href="/lodestone/freecompany/(\d+)/">([^<]*)</a>""",
    RegexOptions.Compiled);

  /// <summary>
  /// Title. Anchored on the class, never the position: the box is name → title (only if set) → world, so an
  /// index-by-position read returns the WORLD as the title on a title-less character.
  /// </summary>
  private static readonly Regex TitlePattern = new(
    """<p class="frame__chara__title">([^<]*)</p>""", RegexOptions.Compiled);

  /// <summary>Grand Company, "Company / Rank". Anchor is the ENGLISH title — a non-NA region returns nothing on a
  /// fully enlisted player, which is why an absent GC is reported as "did not say", never "None".</summary>
  private static readonly Regex GrandCompanyPattern = new(
    """character-block__title">Grand Company</p>\s*<p class="character-block__name">([^<]*)</p>""",
    RegexOptions.Compiled);

  /// <summary>Nameday reads from character-block__birth — the ONE field that does. A generic "title then __name"
  /// loop would silently pair the Nameday label with the Guardian's value.</summary>
  private static readonly Regex NamedayPattern = new(
    """character-block__title">Nameday</p>\s*<p class="character-block__birth">([^<]*)</p>""",
    RegexOptions.Compiled);

  private static readonly Regex GuardianPattern = new(
    """character-block__title">Guardian</p>\s*<p class="character-block__name">([^<]*)</p>""",
    RegexOptions.Compiled);

  private static readonly Regex CityStatePattern = new(
    """character-block__title">City-state</p>\s*<p class="character-block__name">([^<]*)</p>""",
    RegexOptions.Compiled);

  /// <summary>The character's own name, for the identity re-check.</summary>
  private static readonly Regex ProfileNamePattern = new(
    """frame__chara__name">([^<]*)</p>""", RegexOptions.Compiled);

  /// <summary>The character's own world, for the identity re-check. Carries a home-world &lt;i&gt; icon first,
  /// then "World [DataCentre]" — the same shape the search page's world cell has.</summary>
  private static readonly Regex ProfileWorldPattern = new(
    """frame__chara__world">(?:<i[^>]*></i>)?\s*([^<]*)""", RegexOptions.Compiled);

  /// <summary>One role block's job list (there are four, one per role group). Scoping the job read to these
  /// blocks first keeps the ~57 non-job js__tooltip tooltips on the page from ever being read as a job.</summary>
  private static readonly Regex JobListPattern = new(
    """character__level__list">\s*<ul[^>]*>(.*?)</ul>""",
    RegexOptions.Singleline | RegexOptions.Compiled);

  /// <summary>
  /// One job inside a role block. The \s* is mandatory — the page ships BOTH class="js__tooltip"data-tooltip=
  /// (no space) and the spaced form, and requiring a space matches ZERO jobs. Capture (\d+|-), NEVER (\d*): "-"
  /// is "not levelled", a real state, and an empty capture would make it identical to a parse failure.
  /// </summary>
  private static readonly Regex JobEntryPattern = new(
    """class="js__tooltip"\s*data-tooltip="([^"]+)">(\d+|-)</li>""", RegexOptions.Compiled);

  /// <summary>
  /// The Free Company crest: the crest__image div and everything up to its close. Scoped FIRST, because the
  /// three layer &lt;img&gt;s inside it are the ONLY _40x40 crest PNGs on the page that belong to this player —
  /// the rest are a stranger "top FCs" ranking feed. Singleline so the capture spans the three img tags; g1 is
  /// then re-scanned by <see cref="CrestLayerPattern"/>. A character with no FC renders no such div, so the
  /// capture simply fails and the layer list stays empty.
  /// </summary>
  private static readonly Regex FreeCompanyCrestPattern = new(
    """character__freecompany__crest__image">(.*?)</div>""",
    RegexOptions.Singleline | RegexOptions.Compiled);

  /// <summary>One crest layer URL, read only from inside the <see cref="FreeCompanyCrestPattern"/> capture. The
  /// img digit is not pinned (img1..imgN), matching the face pattern's reasoning.</summary>
  private static readonly Regex CrestLayerPattern = new(
    """src="(https://img\d\.finalfantasyxiv\.com/c/[^"]+_40x40\.png)""", RegexOptions.Compiled);

  /// <summary>
  /// One field-op level on the /class_job/ sub-page: "Elemental Level" (Eureka), "Resistance Rank" (Bozja) or
  /// "Knowledge Level" (Occult Crescent). Anchored on the PLAIN character__job__name div — a real job on this
  /// same page carries character__job__name js__tooltip, so this can never catch one — and the alternation pins
  /// it to exactly the three field-op labels. g1 is the level, g2 the label.
  /// </summary>
  private static readonly Regex FieldOpPattern = new(
    """character__job__level">(\d+)</div>\s*<div class="character__job__name">(Elemental Level|Resistance Rank|Knowledge Level)</div>""",
    RegexOptions.Compiled);

  /// <summary>
  /// The player's self-introduction, anchored on its own class. Singleline so a multi-line bio is captured
  /// whole; the &lt;br&gt; tags inside are turned into newlines afterward, not matched here.
  /// </summary>
  private static readonly Regex SelfIntroPattern = new(
    """character__selfintroduction">(.*?)</div>""",
    RegexOptions.Singleline | RegexOptions.Compiled);

  /// <summary>Turns the self-introduction's &lt;br&gt; (with or without a slash or spaces) into a newline, before
  /// the remaining tags are stripped.</summary>
  private static readonly Regex BrPattern = new("""<br\s*/?>""", RegexOptions.Compiled);

  /// <summary>Any leftover HTML tag, stripped from the self-introduction after the breaks are converted — a bio
  /// can carry stray markup the game's own client renders as nothing.</summary>
  private static readonly Regex TagPattern = new("<[^>]+>", RegexOptions.Compiled);

  /// <summary>
  /// One Occult Crescent Phantom Job: name, "Lv. N", and optionally the "MASTERED" flag. On the /class_job/
  /// sub-page's support-job block, which sits in the main content BEFORE ldst__side, so the sliced page cannot
  /// let the stranger feed supply one. The master group is optional — a job below its cap has no such div.
  /// </summary>
  private static readonly Regex PhantomJobPattern = new(
    """character__support_job__name">([^<]+)</p>\s*<p class="character__support_job__level">([^<]+)</p>(?:\s*<p class="character__support_job__master">([^<]+)</p>)?""",
    RegexOptions.Compiled);

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

  /// <summary>
  /// Every player whose full character page was asked about this session. Its own dictionary under the same
  /// <see cref="_gate"/>, separate from <see cref="_cache"/> so a page failure can never touch the face. In
  /// memory, never persisted, wiped in <see cref="Clear"/> at logout — the same terms as the face and the
  /// watcher log, for the same reason: the stranger did not consent to a file. Doubles as the single-flight
  /// record, exactly as <see cref="_cache"/> does.
  /// </summary>
  private readonly Dictionary<WatcherKey, CharacterProfile> _profiles = [];

  /// <summary>
  /// Every player whose /class_job/ sub-page was asked about this session — the field-op levels. A THIRD
  /// dictionary under the same <see cref="_gate"/>, separate from <see cref="_profiles"/> so a sub-page failure
  /// can never touch the main page above it, and so the extra request it represents is only ever made for a
  /// profile whose Jobs tab was actually opened. In memory, never persisted, wiped in <see cref="Clear"/> at
  /// logout on the same terms as the rest. Doubles as the single-flight record, exactly as the others do.
  /// </summary>
  private readonly Dictionary<WatcherKey, FieldOpProgress> _fieldOps = [];

  private bool _disposed;

  /// <summary>
  /// Bumped every time the caches are cleared, so a fetch that outlives its session cannot write into the next.
  ///
  /// A fetch is fire-and-forget with a ten-second HTTP timeout, and <see cref="Clear"/> runs at logout — so a
  /// request started seconds before a logout completes AFTER it, and settling then would resurrect a
  /// just-ended session's stranger (and, on the face path, leak the texture it carried past the Dispose that
  /// should have freed it). Guarding the settles on `_disposed` alone catches only unload, not logout: logout
  /// clears but does not dispose. Each fetch captures this at launch and the settle refuses if it has moved on.
  /// </summary>
  private int _generation;

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

    int gen;
    lock (_gate)
    {
      // Anything but Idle means it has been asked already — in flight, answered, or answered badly. A retry
      // would need a user gesture, not a repaint.
      if (_cache.TryGetValue(key, out var existing) && existing.State != PortraitState.Idle)
        return;

      _cache[key] = new Portrait { State = PortraitState.Looking };
      gen = _generation;
    }

    // Fire-and-forget on purpose: nothing waits for a face. The continuation writes the cache and Draw picks it
    // up on whatever frame it lands. Exceptions cannot escape — Fetch catches its own.
    _ = Task.Run(() => Fetch(key, worldName, gen));
  }

  private async Task Fetch(WatcherKey key, string worldName, int gen)
  {
    try
    {
      var url = $"https://{Region()}.finalfantasyxiv.com/lodestone/character/"
              + $"?q={Uri.EscapeDataString(key.Name)}&worldname={Uri.EscapeDataString(worldName)}";

      var html = await GetStringPaced(url).ConfigureAwait(false);

      if (Match(html, key, worldName) is not { } hit)
      {
        Settle(key, PortraitState.Missing, null, 0, gen);
        return;
      }

      var bytes = await GetBytesPaced(hit.Face).ConfigureAwait(false);

      // Dalamud owns the upload; this just hands it bytes. Not marshalled to the framework thread because
      // CreateFromImageAsync is explicitly the async, any-thread door.
      var texture = await Plugin.Textures.CreateFromImageAsync(bytes).ConfigureAwait(false);

      Settle(key, PortraitState.Ready, texture, hit.Id, gen);
    }
    catch (Exception ex)
    {
      // Offline, blocked, rate-limited, or the page changed shape. Warning, not Error: a missing decoration is
      // not a fault the user has to do anything about, and the profile says so itself.
      Plugin.Log.Warning(ex, "Lodestone lookup failed");
      Settle(key, PortraitState.Failed, null, 0, gen);
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

  private void Settle(WatcherKey key, PortraitState state, IDalamudTextureWrap? texture, uint id, int gen)
  {
    lock (_gate)
    {
      // Disposed at unload, OR the session cleared out from under this fetch at logout (gen moved). Either way
      // nothing will ever draw it, and the texture is ours — dropping it here is what stops the logout race
      // from resurrecting a stranger AND leaking their portrait past the Dispose that should have freed it.
      if (_disposed || gen != _generation)
      {
        texture?.Dispose();
        return;
      }

      _cache[key] = new Portrait { State = state, Texture = texture, CharacterId = id };
    }
  }

  // ── The character page: a second lookup, on the id the face already verified. Its own state, dictionary,
  //    settle and regexes; the face path above is untouched. ──────────────────────────────────────────────

  /// <summary>What is known about this player's full profile right now. Safe every frame; never starts
  /// anything — mirrors <see cref="Get"/>.</summary>
  public CharacterProfile GetProfile(WatcherKey key)
  {
    lock (_gate)
      return _profiles.TryGetValue(key, out var profile) ? profile : new CharacterProfile();
  }

  /// <summary>
  /// Asks the Lodestone for this player's full character page, once, using the id the FACE match already
  /// verified.
  ///
  /// Refuses unless the face is <see cref="PortraitState.Ready"/> with a non-zero id: the page is fetched by
  /// that id and NEVER by a re-search, because a re-search re-opens the wrong-person hole the strict
  /// three-condition <see cref="Match"/> exists to close. Single-flighted exactly like the face — flip to
  /// Looking BEFORE the request starts, so a window redrawing at sixty frames a second asks Square Enix once
  /// rather than sixty times. Do not call it from a list, a scan, or a timer — only from a deliberate click.
  /// </summary>
  /// <param name="worldName">The world to re-check the returned page against. The key carries only a world id,
  /// and the identity re-check compares the page's printed world by name — see <see cref="ParseProfile"/>.</param>
  public void RequestProfile(WatcherKey key, string worldName)
  {
    uint characterId;
    int gen;
    lock (_gate)
    {
      // No verified face, no page. The id is the ONLY thing that reaches the network here, and it has to have
      // come out of the three-condition match, not from a fresh search.
      if (!_cache.TryGetValue(key, out var portrait)
          || portrait.State != PortraitState.Ready || portrait.CharacterId == 0)
        return;

      characterId = portrait.CharacterId;

      // Single-flight, verbatim from Request: anything but Idle has been asked already.
      if (_profiles.TryGetValue(key, out var existing) && existing.State != ProfileFetchState.Idle)
        return;

      _profiles[key] = new CharacterProfile { State = ProfileFetchState.Looking };
      gen = _generation;
    }

    // Fire-and-forget: nothing waits for a profile. FetchProfile catches its own exceptions.
    _ = Task.Run(() => FetchProfile(key, worldName, characterId, gen));
  }

  /// <summary>
  /// Retries a page fetch that FAILED — the one path back to Idle, and a click, never a repaint.
  ///
  /// <see cref="RequestProfile"/> refuses anything but Idle, so without this a transient network failure would
  /// be permanent for the session. Only a settled <see cref="ProfileFetchState.Failed"/> is reset; Gone is a
  /// fact about them, not a fault to retry.
  /// </summary>
  public void RetryProfile(WatcherKey key, string worldName)
  {
    lock (_gate)
    {
      if (_profiles.TryGetValue(key, out var existing) && existing.State == ProfileFetchState.Failed)
        _profiles[key] = new CharacterProfile { State = ProfileFetchState.Idle };
    }

    RequestProfile(key, worldName);
  }

  private async Task FetchProfile(WatcherKey key, string worldName, uint characterId, int gen)
  {
    try
    {
      var url = $"https://{Region()}.finalfantasyxiv.com/lodestone/character/{characterId}/";

      // UTF-8 explicitly, NOT GetStringAsync: a charset guess turns the ♀/♂ glyph into silent mojibake, and the
      // glyph is the only source of the character's gender.
      var bytes = await GetBytesPaced(url).ConfigureAwait(false);
      var html = Encoding.UTF8.GetString(bytes);

      if (ParseProfile(html, key, worldName) is not { } profile)
      {
        // Sentinel absent, or the page is about someone else. Failed, whose own contract is "says nothing about
        // the player" — correct, because the fault is a shape/identity problem our side, not a fact about them.
        SettleProfile(key, new CharacterProfile { State = ProfileFetchState.Failed }, gen);
        return;
      }

      // The FC crest is three MORE requests — the most any one profile load adds — so they fire only HERE, after
      // a parse that actually found crest URLs (a company-less character has none), never speculatively and never
      // for a list. They ride the same generation guard: SettleProfile disposes the wraps on a gen miss.
      if (profile.FreeCompanyCrestUrls.Count > 0)
        profile.FreeCompanyCrest = await FetchCrest(profile.FreeCompanyCrestUrls).ConfigureAwait(false);

      SettleProfile(key, profile, gen);
    }
    catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
    {
      // A 404 on an id resolved minutes ago says a great deal: the page is GONE. Its own state, never
      // PortraitState.Missing — that is the FACE lookup's word for "no such name on that world", and its flip
      // would erase the face already on screen. GetByteArrayAsync throws on 404, so without this `when` clause a
      // real deletion would land in Failed and read as a network hiccup.
      SettleProfile(key, new CharacterProfile { State = ProfileFetchState.Gone }, gen);
    }
    catch (Exception ex)
    {
      // Offline, blocked, rate-limited, or the page changed shape. Warning, not Error, and the section says so.
      Plugin.Log.Warning(ex, "Lodestone character page failed");
      SettleProfile(key, new CharacterProfile { State = ProfileFetchState.Failed }, gen);
    }
  }

  /// <summary>
  /// Downloads the crest layers and uploads them, or returns null if any layer fails.
  ///
  /// Catches its OWN exceptions rather than letting them fail the profile: a crest is a decoration and the FC
  /// name still shows without it, so a network hiccup on a 40x40 PNG must not turn a good page into Failed. Drops
  /// whatever loaded before the failure so a half-set of layers is never drawn — three or none, like the page.
  /// The uploads use the same any-thread CreateFromImageAsync door the face does.
  /// </summary>
  private static async Task<IReadOnlyList<IDalamudTextureWrap>?> FetchCrest(IReadOnlyList<string> urls)
  {
    var wraps = new List<IDalamudTextureWrap>(urls.Count);
    try
    {
      foreach (var url in urls)
      {
        var bytes = await GetBytesPaced(url).ConfigureAwait(false);
        wraps.Add(await Plugin.Textures.CreateFromImageAsync(bytes).ConfigureAwait(false));
      }

      return wraps;
    }
    catch (Exception ex)
    {
      Plugin.Log.Warning(ex, "Lodestone FC crest fetch failed");
      foreach (var wrap in wraps)
        wrap.Dispose();
      return null;
    }
  }

  /// <summary>Frees a profile's crest layers, if it carried any. The one texture-owning field on
  /// <see cref="CharacterProfile"/>, so this is called wherever a profile is dropped — Clear, and the
  /// SettleProfile generation-miss branch — exactly as the face texture is.</summary>
  private static void DisposeCrest(CharacterProfile profile)
  {
    if (profile.FreeCompanyCrest is not { } crest)
      return;

    foreach (var wrap in crest)
      wrap.Dispose();
  }

  /// <summary>
  /// Parses one character page, or returns null so the caller settles <see cref="ProfileFetchState.Failed"/>.
  ///
  /// TWO refusals guard every field below. The Race/Clan/Gender block is the PAGE SENTINEL — present on every
  /// real character page, so its absence means this is not one (an error page, or the activity-feed shell), and
  /// no field is read from a page we cannot identify. That single check is what makes THE SIDEBAR TRAP harmless:
  /// this page carries stranger FC ids and stranger faces in a global feed, and a naive scrape returns one with
  /// full confidence. Then the identity re-check: the id came from the verified face match, but the page's own
  /// printed name and world must still Ordinal-match the person asked for, or it is rejected as our fault.
  /// </summary>
  private static CharacterProfile? ParseProfile(string html, WatcherKey key, string worldName)
  {
    var rcg = RaceClanGenderPattern.Match(html);
    if (!rcg.Success)
      return null;

    var pageName = ProfileNamePattern.Match(html);
    var pageWorld = ProfileWorldPattern.Match(html);
    if (!pageName.Success || !pageWorld.Success)
      return null;

    // Ordinal, never a culture compare: a Turkish-I fold on a name matches the wrong human. Same rule the face
    // Match follows.
    var name = WebUtility.HtmlDecode(pageName.Groups[1].Value).Trim();
    if (!string.Equals(name, key.Name, StringComparison.Ordinal))
    {
      Plugin.Log.Warning("Lodestone character page name mismatch: page '{Page}', expected '{Expected}'",
        name, key.Name);
      return null;
    }

    // The page prints "Shinryu [Meteor]" — world then data centre. Only the world is ours to check; the DC is a
    // fact about the world, not the player, and matching it would break the day one transfers between DCs.
    var world = WebUtility.HtmlDecode(pageWorld.Groups[1].Value).Trim();
    var bracket = world.IndexOf('[');
    if (bracket > 0)
      world = world[..bracket].Trim();

    if (!string.Equals(world, worldName, StringComparison.Ordinal))
    {
      Plugin.Log.Warning("Lodestone character page world mismatch: page '{Page}', expected '{Expected}'",
        world, worldName);
      return null;
    }

    var profile = new CharacterProfile
    {
      State = ProfileFetchState.Ready,
      Race = WebUtility.HtmlDecode(rcg.Groups[1].Value).Trim(),
      Clan = WebUtility.HtmlDecode(rcg.Groups[2].Value).Trim(),
      Gender = MapGender(rcg.Groups[3].Value),
    };

    if (FreeCompanyPattern.Match(html) is { Success: true } fc)
      profile.FreeCompanyName = WebUtility.HtmlDecode(fc.Groups[2].Value).Trim();

    if (TitlePattern.Match(html) is { Success: true } title)
      profile.Title = WebUtility.HtmlDecode(title.Groups[1].Value).Trim();

    if (GrandCompanyPattern.Match(html) is { Success: true } gc)
    {
      var text = WebUtility.HtmlDecode(gc.Groups[1].Value).Trim();

      // The page prints "-" when not enlisted; treat that as absent, not as a company literally named "-".
      if (text.Length > 0 && text != "-")
      {
        var slash = text.LastIndexOf(" / ", StringComparison.Ordinal);
        if (slash > 0)
        {
          profile.GrandCompany = text[..slash].Trim();
          profile.GrandCompanyRank = text[(slash + 3)..].Trim();
        }
        else
          profile.GrandCompany = text;
      }
    }

    if (NamedayPattern.Match(html) is { Success: true } nd)
      profile.Nameday = WebUtility.HtmlDecode(nd.Groups[1].Value).Trim();
    if (GuardianPattern.Match(html) is { Success: true } gd)
      profile.Guardian = WebUtility.HtmlDecode(gd.Groups[1].Value).Trim();
    if (CityStatePattern.Match(html) is { Success: true } cs)
      profile.CityState = WebUtility.HtmlDecode(cs.Groups[1].Value).Trim();

    profile.SelfIntroduction = ParseSelfIntroduction(html);
    profile.Jobs = ParseJobs(html);
    profile.FreeCompanyCrestUrls = ParseCrestUrls(html);

    return profile;
  }

  /// <summary>
  /// The player's bio, or null when they left it blank.
  ///
  /// Breaks first, then tags: &lt;br&gt; becomes a newline so the shape the player wrote is kept, and any other
  /// stray markup is then stripped rather than shown raw. Decoded last, so an entity that decodes to '&lt;' is
  /// not itself mistaken for a tag. Empty after all that — the div exists but holds nothing — returns null, so
  /// the Comment section omits itself rather than drawing an empty box.
  /// </summary>
  private static string? ParseSelfIntroduction(string html)
  {
    if (SelfIntroPattern.Match(html) is not { Success: true } m)
      return null;

    var text = BrPattern.Replace(m.Groups[1].Value, "\n");
    text = TagPattern.Replace(text, string.Empty);
    text = WebUtility.HtmlDecode(text).Trim();

    return text.Length > 0 ? text : null;
  }

  /// <summary>
  /// The three Free Company crest layer URLs, in draw order, or empty when there is no FC.
  ///
  /// Scoped to the crest__image div FIRST and the layers read only from inside it — the page's stranger "top FCs"
  /// ranking feed carries the same _40x40 crest PNGs, and a page-wide scrape returns one of those on a company-
  /// less character. The div is absent when there is no FC, so the capture fails and this returns empty, matching
  /// <see cref="CharacterProfile.FreeCompanyName"/>'s own null in that case.
  /// </summary>
  private static IReadOnlyList<string> ParseCrestUrls(string html)
  {
    var block = FreeCompanyCrestPattern.Match(html);
    if (!block.Success)
      return [];

    var urls = new List<string>(3);
    foreach (Match layer in CrestLayerPattern.Matches(block.Groups[1].Value))
      urls.Add(layer.Groups[1].Value);

    return urls;
  }

  /// <summary>
  /// Every job slot, in page order, scoped to the job lists first.
  ///
  /// The scope matters: the page carries ~91 js__tooltip tooltips and only ~34 are jobs — the rest are sidebar
  /// and Grand Company icons. Reading the job &lt;li&gt;s only from inside the character__level__list blocks
  /// (four of them, one per role group) keeps a stray sidebar entry from ever being read as a job.
  /// </summary>
  private static List<JobSlot> ParseJobs(string html)
  {
    var jobs = new List<JobSlot>(34);
    var slot = 0;

    foreach (Match block in JobListPattern.Matches(html))
    {
      foreach (Match job in JobEntryPattern.Matches(block.Groups[1].Value))
      {
        var jobName = NormaliseJobName(WebUtility.HtmlDecode(job.Groups[1].Value).Trim());

        // "-" is "not levelled", null — a state as real as a number. int.TryParse guards a shape change.
        var raw = job.Groups[2].Value;
        int? level = raw != "-" && int.TryParse(raw, out var lv) ? lv : null;

        jobs.Add(new JobSlot(slot++, jobName, level));
      }
    }

    return jobs;
  }

  /// <summary>
  /// The stable job name from an unstable tooltip. The same slot reads "Gladiator" unlevelled and
  /// "Paladin / Gladiator" levelled, so the job is the text before the LAST " / "; the "(Limited Job)" suffix
  /// (Blue Mage, Beastmaster) is stripped first so it keys like any other slot.
  /// </summary>
  private static string NormaliseJobName(string tooltip)
  {
    var jobName = tooltip;

    const string limited = " (Limited Job)";
    if (jobName.EndsWith(limited, StringComparison.Ordinal))
      jobName = jobName[..^limited.Length];

    var slash = jobName.LastIndexOf(" / ", StringComparison.Ordinal);
    if (slash > 0)
      jobName = jobName[..slash];

    return jobName.Trim();
  }

  /// <summary>Maps the Lodestone's gender glyph to a word. NEVER renders ♀/♂ — Dalamud's default font has no
  /// glyph for either and draws tofu.</summary>
  private static string? MapGender(string glyph)
  {
    if (glyph.Contains('♀')) return "Female"; // Venus / female sign (U+2640)
    if (glyph.Contains('♂')) return "Male";   // Mars / male sign (U+2642)
    return null;
  }

  private void SettleProfile(WatcherKey key, CharacterProfile profile, int gen)
  {
    lock (_gate)
    {
      // Disposed at unload, or the session cleared out from under this fetch at logout (gen moved). Nothing will
      // ever draw it, and keeping it would carry a just-ended session's stranger into the next — the persistence
      // rule this whole cache is written to keep. See _generation. The crest layers this fetch just built are
      // ours; dropping them here is what stops that same race from leaking a stranger's crest past the Dispose
      // that should have freed it, exactly as the face Settle does with its texture.
      if (_disposed || gen != _generation)
      {
        DisposeCrest(profile);
        return;
      }

      _profiles[key] = profile;
    }
  }

  // ── The /class_job/ sub-page: a THIRD lookup, on the id the face already verified, for the field-op levels the
  //    main page does not carry. Its own state, dictionary, settle and regexes, sharing only the generation guard
  //    and Clear() with the two above; fired only from the Jobs tab so it is never spent on a profile not opened
  //    to Jobs. ──────────────────────────────────────────────────────────────────────────────────────────────

  /// <summary>What is known about this player's field-op levels right now. Safe every frame; never starts
  /// anything — mirrors <see cref="GetProfile"/>.</summary>
  public FieldOpProgress GetFieldOps(WatcherKey key)
  {
    lock (_gate)
      return _fieldOps.TryGetValue(key, out var progress) ? progress : new FieldOpProgress();
  }

  /// <summary>
  /// Asks the Lodestone for this player's /class_job/ sub-page, once, using the id the FACE match verified.
  ///
  /// Refuses unless the face is <see cref="PortraitState.Ready"/> with a non-zero id — same gate as
  /// <see cref="RequestProfile"/>, and for the same reason: the sub-page is fetched by that id and NEVER by a
  /// re-search. Single-flighted exactly like the others — flip to Looking BEFORE the request so a tab redrawing
  /// sixty times a second asks once. THE ONE ADDITIONAL REQUEST the Jobs tab makes; do not call it from a list,
  /// a scan, a timer, or Open — only from the Jobs tab's own draw.
  /// </summary>
  public void RequestFieldOps(WatcherKey key, string worldName)
  {
    uint characterId;
    int gen;
    lock (_gate)
    {
      // No verified face, no sub-page. The id is the only thing that reaches the network, and it must have come
      // from the three-condition face match, not a fresh search.
      if (!_cache.TryGetValue(key, out var portrait)
          || portrait.State != PortraitState.Ready || portrait.CharacterId == 0)
        return;

      characterId = portrait.CharacterId;

      // Single-flight: anything but Idle has been asked already.
      if (_fieldOps.TryGetValue(key, out var existing) && existing.State != FieldOpFetchState.Idle)
        return;

      _fieldOps[key] = new FieldOpProgress { State = FieldOpFetchState.Looking };
      gen = _generation;
    }

    // Fire-and-forget: nothing waits for this. FetchFieldOps catches its own exceptions.
    _ = Task.Run(() => FetchFieldOps(key, worldName, characterId, gen));
  }

  /// <summary>
  /// Retries a sub-page fetch that FAILED — the one path back to Idle, and a click, never a repaint. Mirrors
  /// <see cref="RetryProfile"/>: <see cref="RequestFieldOps"/> refuses anything but Idle, so a transient failure
  /// would otherwise be permanent for the session. Gone is a fact, not a fault, so it is not reset.
  /// </summary>
  public void RetryFieldOps(WatcherKey key, string worldName)
  {
    lock (_gate)
    {
      if (_fieldOps.TryGetValue(key, out var existing) && existing.State == FieldOpFetchState.Failed)
        _fieldOps[key] = new FieldOpProgress { State = FieldOpFetchState.Idle };
    }

    RequestFieldOps(key, worldName);
  }

  private async Task FetchFieldOps(WatcherKey key, string worldName, uint characterId, int gen)
  {
    try
    {
      var url = $"https://{Region()}.finalfantasyxiv.com/lodestone/character/{characterId}/class_job/";

      // UTF-8 explicitly, matching FetchProfile: a charset guess is not worth the risk on a page read for exact
      // labels and numbers.
      var bytes = await GetBytesPaced(url).ConfigureAwait(false);
      var html = Encoding.UTF8.GetString(bytes);

      if (ParseFieldOps(html, key, worldName) is not { } progress)
      {
        // The page is about someone else, or its shape changed. Failed, whose contract is "says nothing about the
        // player" — never Ready with null levels, which would assert "no field-op progress" about a page we could
        // not even confirm is theirs.
        SettleFieldOps(key, new FieldOpProgress { State = FieldOpFetchState.Failed }, gen);
        return;
      }

      SettleFieldOps(key, progress, gen);
    }
    catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
    {
      // A 404 on an id resolved moments ago: the sub-page is gone. Its own state, never the face's Missing.
      SettleFieldOps(key, new FieldOpProgress { State = FieldOpFetchState.Gone }, gen);
    }
    catch (Exception ex)
    {
      Plugin.Log.Warning(ex, "Lodestone class_job sub-page failed");
      SettleFieldOps(key, new FieldOpProgress { State = FieldOpFetchState.Failed }, gen);
    }
  }

  /// <summary>
  /// Parses the /class_job/ sub-page's field-op levels, or returns null so the caller settles
  /// <see cref="FieldOpFetchState.Failed"/>.
  ///
  /// THE SENTINEL DIFFERS FROM THE MAIN PAGE. The Race/Clan/Gender block is ABSENT here (verified), so this must
  /// NOT reuse it — the guard is the IDENTITY RE-CHECK alone: the page's own printed name and world must
  /// Ordinal-match the person asked for, exactly as ParseProfile does, or the page is rejected. That is what
  /// keeps the field-op read honest against a sub-page that failed to load into the activity-feed shell.
  ///
  /// THE SIDEBAR TRAP, again: this page carries the same global stranger activity feed. The field-op scrape is
  /// anchored to the main content by slicing off everything from the first ldst__side — the feed's container —
  /// mirroring JobListPattern's scope-then-scan. (Belt-and-braces: a real job's name div carries js__tooltip and
  /// the field-op div does not, so the pattern already cannot catch a job — but the slice matches the discipline
  /// the rest of this file keeps.)
  /// </summary>
  private static FieldOpProgress? ParseFieldOps(string html, WatcherKey key, string worldName)
  {
    var pageName = ProfileNamePattern.Match(html);
    var pageWorld = ProfileWorldPattern.Match(html);
    if (!pageName.Success || !pageWorld.Success)
      return null;

    // Ordinal, never a culture compare — the same rule the face Match and ParseProfile follow.
    var name = WebUtility.HtmlDecode(pageName.Groups[1].Value).Trim();
    if (!string.Equals(name, key.Name, StringComparison.Ordinal))
    {
      Plugin.Log.Warning("Lodestone class_job page name mismatch: page '{Page}', expected '{Expected}'",
        name, key.Name);
      return null;
    }

    var world = WebUtility.HtmlDecode(pageWorld.Groups[1].Value).Trim();
    var bracket = world.IndexOf('[');
    if (bracket > 0)
      world = world[..bracket].Trim();

    if (!string.Equals(world, worldName, StringComparison.Ordinal))
    {
      Plugin.Log.Warning("Lodestone class_job page world mismatch: page '{Page}', expected '{Expected}'",
        world, worldName);
      return null;
    }

    // Slice off the stranger activity feed before scanning; fall back to the whole page if the marker is absent.
    var side = html.IndexOf("ldst__side", StringComparison.Ordinal);
    var main = side > 0 ? html[..side] : html;

    var progress = new FieldOpProgress { State = FieldOpFetchState.Ready };
    foreach (Match m in FieldOpPattern.Matches(main))
    {
      // int.TryParse guards a shape change; the regex already pins g1 to \d+.
      if (!int.TryParse(m.Groups[1].Value, out var level))
        continue;

      switch (m.Groups[2].Value)
      {
        case "Elemental Level": progress.ElementalLevel = level; break;
        case "Resistance Rank": progress.ResistanceRank = level; break;
        case "Knowledge Level": progress.KnowledgeLevel = level; break;
      }
    }

    // Phantom Jobs — Occult Crescent's own job system, read from the SAME sliced main content, so the stranger
    // feed cannot supply one. Name and level are the page's raw strings; MASTERED is present only above the cap.
    var phantoms = new List<PhantomJob>();
    foreach (Match m in PhantomJobPattern.Matches(main))
      phantoms.Add(new PhantomJob(
        WebUtility.HtmlDecode(m.Groups[1].Value).Trim(),
        WebUtility.HtmlDecode(m.Groups[2].Value).Trim(),
        m.Groups[3].Success));
    progress.PhantomJobs = phantoms;

    // A page that passed identity but listed no field-op progress settles Ready with all three null and no
    // phantom jobs — a real "they have done none of this", omitted from the display, not a failure.
    return progress;
  }

  private void SettleFieldOps(WatcherKey key, FieldOpProgress progress, int gen)
  {
    lock (_gate)
    {
      // Disposed at unload, or the session cleared under this fetch at logout (gen moved). Holds no textures, so
      // unlike SettleProfile there is nothing to dispose — just refuse to resurrect a just-ended session's
      // stranger. See _generation.
      if (_disposed || gen != _generation)
        return;

      _fieldOps[key] = progress;
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

      // The character-page cache dies with the session too. It now holds the FC crest textures, so — unlike
      // before — it must be DISPOSED, not merely cleared: the crest layers are ours exactly as the face is, and
      // leaking them past logout is the same fault, one dictionary over.
      foreach (var profile in _profiles.Values)
        DisposeCrest(profile);
      _profiles.Clear();

      // The field-op sub-page cache dies with the session on the same terms. Ints and strings only — no textures
      // — so it only needs clearing.
      _fieldOps.Clear();

      // Past this point, any fetch launched before now belongs to a session that has ended: bumping the
      // generation makes its settle a no-op instead of a resurrection. Under the lock, so a settle cannot read
      // the old value and write between the clear and the bump.
      _generation++;
    }
  }

  public void Dispose()
  {
    lock (_gate)
      _disposed = true;

    Clear();
  }
}
