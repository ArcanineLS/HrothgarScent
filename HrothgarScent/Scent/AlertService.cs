using System;
using System.Collections.Generic;
using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace HrothgarScent.Scent;

/// <summary>
/// What a signal is about. Ordered: LOWER IS MORE URGENT, and that order is the whole of the priority rule.
///
/// Only classes that exist. An enum member for a feature nobody has written is dead code that reads as a
/// promise, and the ladder is only meaningful between things that can actually collide.
/// </summary>
public enum SignalClass : byte
{
  /// <summary>Someone has held you long enough that it stopped being a glance. The most certain fact this
  /// plugin ever has, and the reason it exists.</summary>
  StareEscalation = 0,

  /// <summary>Someone new is targeting you. May be a stranger cycling targets; may be the start of the above.</summary>
  NewWatcher = 1,

  /// <summary>A player you asked to be told about walked into range. Least urgent: you chose them, they did
  /// nothing.</summary>
  FocusArrival = 2,
}

/// <summary>
/// Chat and sound alerts, coalesced onto one cooldown.
///
/// WHAT THIS BUYS, stated honestly, because it is narrower than it sounds: within one cooldown window the
/// MOST URGENT signal wins, rather than whichever one the scanner happened to compute first. A stranger's
/// stare landing two seconds after a friend walked into view now takes the next window instead of vanishing
/// because the friend got there first. It does not mean nothing is ever dropped — a signal that waits too long
/// still expires, because an alert about a moment that has passed is worse than silence.
///
/// The old design was order-as-priority: the scanner called three methods in a fixed sequence and the first
/// one to speak took the cooldown. That works for two classes and rots at three — the loser is dropped with no
/// trace, and a missing alert leaves nothing to diagnose. Which is exactly the shape of the prior art's alert
/// bugs: a proximity alert that fires once per session then never again, a rename alert that repeats six times
/// in four minutes, a world-transfer alert that flip-flops forever. All three still open; the pull request
/// that would have added per-alert cooldowns and dedup died as an unmerged draft.
///
/// ONE cooldown, still, because the user configured one. "Shortest gap between two alerts" is a promise about
/// alerts, not about categories of alert, and a cooldown per class would let a crowd deliver three times what
/// the slider says.
///
/// Framework thread only. The scanner is the sole caller of <see cref="Pump"/> and of every Raise method, and
/// every chat and sound call below is game-side.
/// </summary>
public sealed class AlertService
{
  /// <summary>
  /// UIColor row id for the alert text — a red, so a stare reads differently from ordinary chatter.
  /// Cosmetic only: this row id could not be verified against the live sheet from the shipped assemblies,
  /// and a wrong one costs the wrong colour and nothing else.
  /// </summary>
  private const ushort UiForegroundWatcher = 17;

  /// <summary>
  /// UIColor row id for the arrival line — a blue, so an arrival does not read as a stare. Cosmetic only, on
  /// the same terms as <see cref="UiForegroundWatcher"/>: unverified against the live sheet, and a wrong one
  /// costs the wrong colour and nothing else.
  /// </summary>
  private const ushort UiForegroundFocus = 37;

  /// <summary>Chat SFX ids. Only 1..16 have a <c>&lt;se.N&gt;</c> behind them; outside the range there is no sound.</summary>
  private const int MinSoundId = 1;
  private const int MaxSoundId = 16;

  /// <summary>
  /// The FLOOR on how long a subject may wait for a window before it is thrown away. See
  /// <see cref="TimeToLive"/>, which is what actually decides — this is only its lower bound.
  ///
  /// Waiting is only worth it while the news is still news. "Someone walked into range" eight seconds ago is a
  /// statement about a moment that has passed, and saying it late is worse than not saying it: the user looks
  /// up at nothing.
  /// </summary>
  private const long PendingTtlFloorMs = 6_000;

  /// <summary>
  /// How long a signal may be beaten by more urgent classes before it jumps the queue.
  ///
  /// Without this, strict priority starves: in a zone where someone is always looking at you, a focus arrival
  /// would never once be spoken and the lowest class would be dead code. Overdue signals go first, oldest
  /// first, which bounds the wait rather than merely making it unlikely.
  ///
  /// Below <see cref="TimeToLive"/> by construction — it is one of that sum's own terms — because a signal must
  /// get its turn before it can expire, or the promotion is unreachable and this constant is a lie.
  /// </summary>
  private const long PromoteAfterMs = 3_000;

  /// <summary>
  /// Why a signal that got its window still said nothing.
  ///
  /// Names both outputs because Emit only fails when BOTH are unavailable — the chat line switched off and the
  /// sound call throwing — and a reason naming one would send the user to check a setting that is not the
  /// problem. This is the rarest row in the journal and the only one describing a fault rather than a rule, so
  /// it has to point somewhere useful the first time.
  /// </summary>
  private const string OutputFailedDetail = "chat line off and the sound would not play";

  /// <summary>
  /// How long a subject may wait, given the cooldown it is waiting on.
  ///
  /// NEVER SHORTER THAN THE COOLDOWN, and that is the whole reason this is a function rather than the constant
  /// it started as. A fixed 6s TTL against the default 10s cooldown means every one-shot signal raised in the
  /// 4s after an alert is dead before its window ever opens — not a race, an arithmetic certainty, and in a busy
  /// zone it is the common case rather than the corner one. A stare re-raises and survives that; a new watcher
  /// and an arrival are edges the scanner computes once and never again, so for them it is permanent, silent
  /// loss. Which is precisely the failure this whole bus was built to end.
  ///
  /// So a subject always gets at least one window, plus the promotion margin so that window is one it can
  /// actually win. The user's own cooldown therefore decides how late news may be — which is right: someone who
  /// asks to be interrupted once a minute has already said they will hear things a minute late.
  /// </summary>
  private static long TimeToLive(long cooldownMs) => Math.Max(PendingTtlFloorMs, cooldownMs + PromoteAfterMs);

  /// <summary>
  /// Ticks of the last alert, or null if none has fired yet.
  ///
  /// Nullable rather than seeded to 0: Environment.TickCount64 counts from system start, so a plain 0 would
  /// mean "an alert fired at boot" and would swallow the very first watcher for one cooldown's worth of
  /// system uptime. Reachable only on a freshly booted machine, and free to rule out.
  /// </summary>
  private long? _lastAlertTicks;

  /// <summary>
  /// Signals waiting for a window, at most one entry per class.
  ///
  /// Framework-thread-owned, like everything the scan touches — the scanner is the sole caller. Keyed by class
  /// so that a class collapses into one line rather than a queue of them: five people starting to stare at you
  /// inside one window is one piece of news ("5 eyes on you"), not five.
  /// </summary>
  private readonly Dictionary<SignalClass, PendingSignal> _pending = [];

  /// <summary>
  /// The last outcome journalled for each pending class, so an unchanged decision is recorded once instead of
  /// once per scan.
  ///
  /// THE JOURNAL WOULD OTHERWISE BE THE FIREHOSE IT WAS BUILT TO REPLACE. Pump runs on every scan, so a class
  /// held by the cooldown writes four identical rows a second; two classes pending against a ten-second
  /// cooldown is eighty rows through a fifty-entry cap, and the ring turns over inside the window — evicting
  /// the answer in exactly the crowded zone where the user came looking for it. Equality would not save it
  /// either: every row carries a fresh timestamp, which is why the countdown is no longer in the detail.
  ///
  /// Cleared per class the moment that class leaves <see cref="_pending"/>, and that is what keeps this a
  /// filter on repetition rather than on truth: Waiting → Said → Waiting is three genuine transitions and
  /// records three times, but Waiting → Waiting → Waiting is one decision and records once.
  /// </summary>
  private readonly Dictionary<SignalClass, SignalOutcome> _lastJournalled = [];

  /// <summary>One class's waiting signal.</summary>
  private sealed class PendingSignal
  {
    /// <summary>When this class FIRST had something to say, and never refreshed while it waits. Refreshing it
    /// on every re-raise would reset the age of a standing signal every scan, so it could never become overdue
    /// and <see cref="PromoteAfterMs"/> would never fire.</summary>
    public long RaisedTicks { get; init; }

    /// <summary>Subjects, deduped by identity: the same player re-raised inside one window is one subject, not
    /// two. This dictionary IS the dedup the prior art never landed.</summary>
    public Dictionary<WatcherKey, PendingSubject> Subjects { get; } = [];
  }

  /// <summary>One player, and whatever the class needs to say about them.</summary>
  private sealed class PendingSubject
  {
    /// <summary>
    /// When THIS player's news arrived, which is not when the class's did.
    ///
    /// Per subject, because a slot outlives the subject that opened it: someone starting to stare four seconds
    /// after the last person did joins an existing slot, and charging them the slot's age would expire them
    /// before they had waited at all. Preserved across an upsert rather than refreshed — see
    /// <see cref="Offer"/> — or a standing stare, which re-raises every scan, would reset its own clock forever
    /// and become immortal.
    /// </summary>
    public required long RaisedTicks { get; init; }

    public required ScentRow Row { get; init; }

    /// <summary>Escalations only.</summary>
    public StareLevel Level { get; init; }

    /// <summary>Escalations only.</summary>
    public long HeldMs { get; init; }

    /// <summary>Escalations only — the live episode state, so the rung can be spent if and when this is
    /// actually said. Null for every other class. See <see cref="RaiseStareEscalations"/>.</summary>
    public StareState? State { get; init; }
  }

  /// <summary>
  /// Offers a watcher's escalation to the bus.
  ///
  /// THE RUNG IS SPENT HERE OR IN <see cref="Pump"/>, NEVER WHERE IT IS DETECTED, and the rule is one
  /// sentence: <b>a rung is spent unless it is still waiting for a turn.</b>
  ///
  /// Everything that refuses an escalation — the watcher half switched off, this alert switched off, a player
  /// the user chose not to hear about, the window shut with RecordWhileClosed off — is the user saying "not
  /// this", and re-offering it a second later would be arguing with them. Only the cooldown means "not right
  /// now", and that one leaves the rung armed. Since a standing stare re-raises on every scan, an armed rung
  /// simply arrives again until it is either said or refused.
  ///
  /// Spending at detection instead would make "announces once per episode" a lie in the expensive direction: a
  /// rung dropped on the cooldown would go silent for the rest of the episode, because the level never climbs
  /// to it twice. One other watcher arriving in the wrong ten seconds would permanently mute the stranger who
  /// is actually fixated on you — the one thing this whole feature exists to say.
  /// </summary>
  public void RaiseStareEscalations(
    IReadOnlyList<(ScentRow Row, StareLevel Level, long HeldMs, StareState State)> escalations)
  {
    var config = Plugin.Configuration;

    // RecordWhileClosed belongs here with the rest of the gates. It is not history-only: it promises Hrothgar
    // "only remember what you were there to see", and an escalation is an announcement about a watcher like any
    // other. Without it the arrival stays silent while the escalation talks anyway — a chat line about someone
    // with no row in the history to explain them.
    if (!config.EnableWatchers
        || !config.AlertOnStareEscalation
        || (!config.AlertInChat && !config.AlertWithSound)
        || (!config.RecordWhileClosed && !Plugin.IsMainWindowOpen))
    {
      Spend(escalations);
      Plugin.Journal.Record(SignalClass.StareEscalation, SignalOutcome.SwitchedOff, escalations.Count,
        WhyOff(SignalClass.StareEscalation, config));
      return;
    }

    var marks = Plugin.Marks.Index;
    var filtered = 0;

    foreach (var escalation in escalations)
    {
      var row = escalation.Row;

      // Filter before the bus, never after, for the reason on RaiseNewWatchers: a party member the user chose
      // not to hear about must not occupy the class and swallow the stranger who is actually fixated on them.
      // A refused subject spends its rung — the user does not want this player announced, now or in five
      // seconds.
      if ((row.IsParty && !config.AlertForParty)
          || (row.IsFriend && !config.AlertForFriends)
          || (row.IsAlliance && !config.AlertForAlliance)
          || marks.IsIgnored(row.Key))
      {
        escalation.State.Level = escalation.Level;
        filtered++;
        continue;
      }

      Offer(SignalClass.StareEscalation, row, escalation.Level, escalation.HeldMs, escalation.State);
    }

    if (filtered > 0)
      Plugin.Journal.Record(SignalClass.StareEscalation, SignalOutcome.Filtered, filtered,
        "you asked not to hear about them");
  }

  /// <summary>
  /// Offers watchers who were not watching on the previous scan.
  ///
  /// The watcher half's own output, so the half's toggle suppresses it — here rather than at the call site, so
  /// every alert gate lives in one file. RecordWhileClosed likewise: the scanner still decides whether to
  /// REMEMBER, and this decides whether to SAY.
  /// </summary>
  public void RaiseNewWatchers(IReadOnlyList<ScentRow> fresh)
  {
    var config = Plugin.Configuration;
    if (!config.EnableWatchers
        || (!config.AlertInChat && !config.AlertWithSound)
        || (!config.RecordWhileClosed && !Plugin.IsMainWindowOpen))
    {
      Plugin.Journal.Record(SignalClass.NewWatcher, SignalOutcome.SwitchedOff, fresh.Count,
        WhyOff(SignalClass.NewWatcher, config));
      return;
    }

    var marks = Plugin.Marks.Index;
    var filtered = 0;

    foreach (var row in fresh)
    {
      // Filter first, bus second, and never the reverse. A party member the user chose not to be alerted about
      // would otherwise occupy the class and swallow the stranger who targeted them in the same scan — the one
      // alert that actually mattered.
      if ((row.IsParty && !config.AlertForParty)
          || (row.IsFriend && !config.AlertForFriends)
          || (row.IsAlliance && !config.AlertForAlliance)
          || marks.IsIgnored(row.Key))
      {
        filtered++;
        continue;
      }

      Offer(SignalClass.NewWatcher, row);
    }

    if (filtered > 0)
      Plugin.Journal.Record(SignalClass.NewWatcher, SignalOutcome.Filtered, filtered,
        "you asked not to hear about them");
  }

  /// <summary>
  /// Offers focus-list players who were not in range on the previous scan.
  ///
  /// Ignore beats focus, and the scanner cannot enforce it: it tests the focus flag, not this one. A player
  /// carrying both marks is a user contradiction, and the ignore promise ("never show or announce them again")
  /// is the older and the stronger one.
  /// </summary>
  public void RaiseFocusArrivals(IReadOnlyList<ScentRow> arrived)
  {
    var config = Plugin.Configuration;
    if (!config.AlertOnFocusArrival
        || !config.EnableNearbyList
        || (!config.AlertInChat && !config.AlertWithSound))
    {
      Plugin.Journal.Record(SignalClass.FocusArrival, SignalOutcome.SwitchedOff, arrived.Count,
        WhyOff(SignalClass.FocusArrival, config));
      return;
    }

    var marks = Plugin.Marks.Index;
    var filtered = 0;

    foreach (var row in arrived)
    {
      // AlertForParty/Friends/Alliance are deliberately NOT consulted. Those exist because your party targets
      // you constantly and the watcher alert would be unusable without them; the focus list is hand-built, one
      // name at a time, so a focused friend is a friend the user explicitly asked to be told about.
      if (marks.IsIgnored(row.Key))
      {
        filtered++;
        continue;
      }

      Offer(SignalClass.FocusArrival, row);
    }

    if (filtered > 0)
      Plugin.Journal.Record(SignalClass.FocusArrival, SignalOutcome.Filtered, filtered, "on the ignore list");
  }

  /// <summary>
  /// Puts one player's news on the bus, creating the class's slot if this is the first.
  ///
  /// Upsert by identity: the same player raised again replaces their own entry rather than adding a second —
  /// that dictionary is the dedup. The REPLACEMENT INHERITS THE ORIGINAL'S CLOCK, which is the subtle half: a
  /// standing stare re-raises on every scan, so a subject that stamped itself fresh each time could never age
  /// out, never be promoted, and never expire. What is being replaced is the news, not the wait.
  /// </summary>
  private void Offer(SignalClass signalClass, ScentRow row, StareLevel level = default, long heldMs = 0,
    StareState? state = null)
  {
    var now = Environment.TickCount64;

    if (!_pending.TryGetValue(signalClass, out var signal))
      _pending[signalClass] = signal = new PendingSignal { RaisedTicks = now };

    // Already waiting? Keep the clock it has been waiting on.
    var raised = signal.Subjects.TryGetValue(row.Key, out var existing) ? existing.RaisedTicks : now;

    signal.Subjects[row.Key] = new PendingSubject
    {
      RaisedTicks = raised,
      Row = row,
      Level = level,
      HeldMs = heldMs,
      State = state,
    };
  }

  /// <summary>
  /// Says at most one thing, and decides which.
  ///
  /// Called once per scan, at the end, with the sets the scan already has in hand. Everything the bus does that
  /// the old order-as-priority could not happens here: a signal is re-checked against the world before it is
  /// spoken, an old one is dropped rather than delivered late, and a class that keeps losing eventually wins.
  /// </summary>
  /// <param name="watching">Who is targeting you right now. A subject who has looked away is discarded.</param>
  /// <param name="nearby">Who is in range right now. A subject who has left is discarded.</param>
  public void Pump(IReadOnlyDictionary<WatcherKey, StareState> watching, IReadOnlySet<WatcherKey> nearby)
  {
    if (_pending.Count == 0)
      return;

    var now = Environment.TickCount64;
    var config = Plugin.Configuration;
    var cooldownMs = (long)(Math.Max(0f, config.AlertCooldownSeconds) * 1000f);

    // Before the cooldown check, so a signal that has stopped being true or stopped being wanted is dropped on
    // the scan it stops, rather than sitting in the slot blocking nothing and surfacing whenever the window
    // next opens.
    Prune(now, watching, nearby, config, TimeToLive(cooldownMs));

    if (_pending.Count == 0)
      return;

    // No cooldown means the user asked not to be rate-limited, so there is nothing to coalesce ONTO. Say
    // everything and keep the old behaviour exactly: with the slider at 0 a watcher and an arrival in the same
    // scan have always produced two lines, and quietly collapsing them to one would be this change regressing a
    // setting it has no business touching.
    if (cooldownMs <= 0)
    {
      List<SignalClass>? said = null;
      foreach (var (signalClass, signal) in _pending)
      {
        if (Emit(signalClass, signal, config))
          (said ??= []).Add(signalClass);
        else
          Journal(signalClass, SignalOutcome.OutputFailed, signal.Subjects.Count, OutputFailedDetail);
      }

      // ONLY what actually reached the user, never the whole dictionary. Clearing unconditionally looks
      // harmless — with no cooldown, what could it be waiting for? — and reintroduces the exact bug this bus
      // was built to end. Emit returns false when nothing was output, which is reachable with chat off and a
      // sound call that failed. A stare survives that (it re-raises), but a new watcher and an arrival are
      // edges the scanner computes ONCE: dropped here, they are gone silently and for good.
      //
      // Nothing is stranded by keeping them. Prune drops a class whose outputs are switched off, and the TTL
      // expires anything that cannot be said in time — so this retries only while there is still a reason to.
      if (said is null)
        return;

      _lastAlertTicks = now;

      foreach (var signalClass in said)
      {
        Journal(signalClass, SignalOutcome.Said, _pending[signalClass].Subjects.Count, string.Empty);
        Forget(signalClass);
      }

      return;
    }

    if (_lastAlertTicks is { } last && now - last < cooldownMs)
    {
      // Once per class per DECISION, not per subject and — via Journal — not per scan: this is the entry that
      // answers "why did nothing happen just now", and a row written four times a second is the question
      // burying its own answer. No countdown in the detail: the remaining time is a live value in a record that
      // is frozen the moment it is written, so by the time it is read it is always wrong, and it would make
      // every scan's row textually distinct and defeat the very suppression above.
      foreach (var (signalClass, signal) in _pending)
        Journal(signalClass, SignalOutcome.Waiting, signal.Subjects.Count, "cooldown still running");

      return;
    }

    var winner = Pick(now, out var promoted);
    if (!_pending.TryGetValue(winner, out var winning))
      return;

    // Removed and charged only if it actually reached the user; otherwise it stays pending, keeps its age, and
    // tries again next scan — until it wins, or Prune decides it is no longer worth saying.
    if (!Emit(winner, winning, config))
    {
      // The signal that got its turn and produced nothing. Without this row the class sits, silent and blameless,
      // until the TTL reports it as Expired — "waited too long to still be news" — about a signal that never
      // waited for anything. The journal's whole product is an accurate reason, and a confidently wrong one is
      // worse than the silence it replaced.
      Journal(winner, SignalOutcome.OutputFailed, winning.Subjects.Count, OutputFailedDetail);
      return;
    }

    _lastAlertTicks = now;
    Journal(winner, SignalOutcome.Said, winning.Subjects.Count, string.Empty);
    Forget(winner);

    // The classes that lost this window. Without this the journal would show what WAS said and stay silent
    // about what was not — which is the half the user came for.
    //
    // The reason has to follow Pick's actual branch. Pick is not strict priority: it promotes a class that has
    // waited past PromoteAfterMs ahead of the ladder precisely so the bottom cannot starve, and the winner is
    // then by construction the LESS urgent one. Reporting "something more urgent went first" there would tell a
    // more urgent loser the exact inverse of what happened, in the one window whose only product is the truth
    // about which rule fired.
    foreach (var (signalClass, signal) in _pending)
      Journal(signalClass, SignalOutcome.Waiting, signal.Subjects.Count,
        promoted ? "another signal had waited longer" : "something more urgent went first");
  }

  /// <summary>
  /// Drops what should no longer be said.
  ///
  /// THE RE-CHECK IS THE POINT, and it is not only about existence. A signal is raised from an edge and spoken
  /// later, and in between the world moves AND the user moves: they look away, they walk off, they untick the
  /// half, they right-click Ignore. Every gate the Raise methods applied is re-applied here, because a gate
  /// that only holds at raise time is a gate with a hole exactly as wide as the cooldown.
  ///
  /// The most important of them is Ignore. "Never show or announce them again" is the oldest and strongest
  /// promise this plugin makes, and a user who ignores someone mid-cooldown must not then hear about them —
  /// which is precisely what checking it once, at raise, would do.
  /// </summary>
  private void Prune(long now, IReadOnlyDictionary<WatcherKey, StareState> watching, IReadOnlySet<WatcherKey> nearby,
    Configuration config, long ttlMs)
  {
    var marks = Plugin.Marks.Index;
    List<SignalClass>? doomed = null;

    foreach (var (signalClass, signal) in _pending)
    {
      // The class's own switch, re-read. Turned off while this waited means the user does not want it — which
      // is a refusal, so an escalation's rung is spent rather than left armed to try again the moment they
      // change their mind back.
      if (!IsClassEnabled(signalClass, config))
      {
        Spend(signal);
        Plugin.Journal.Record(signalClass, SignalOutcome.SwitchedOff, signal.Subjects.Count,
          "a switch for this is off");
        (doomed ??= []).Add(signalClass);
        continue;
      }

      List<WatcherKey>? gone = null;

      // Counted per class, never journalled per subject: a filtered 24-man alliance would otherwise be
      // twenty-four rows every scan, which is the whole ring twice a second. The count IS the entry.
      var ignored = 0;
      var expired = 0;
      var stale = 0;

      foreach (var (key, subject) in signal.Subjects)
      {
        // Ignored since it was raised: a refusal, so spend the rung and drop them.
        if (marks.IsIgnored(key))
        {
          SpendOne(subject);
          (gone ??= []).Add(key);
          ignored++;
          continue;
        }

        // Waited too long to still be news. NOT a refusal — nobody said no, the moment simply passed — so the
        // rung stays armed, and a stare that is still going re-raises on the next scan and tries again.
        if (now - subject.RaisedTicks >= ttlMs)
        {
          (gone ??= []).Add(key);
          expired++;
          continue;
        }

        if (!StillTrue(signalClass, key, subject, watching, nearby))
        {
          (gone ??= []).Add(key);
          stale++;
        }
      }

      // Count only, and the ignore branch is exactly why. Naming a suppressed player here would print them
      // back into the same window whose Filters tab is where the user went to erase them — the oldest promise
      // in the plugin, broken by the feature built to catch broken promises. The count answers the question
      // ("something was eaten, and this rule ate it") without reopening the roster.
      if (ignored > 0)
        Plugin.Journal.Record(signalClass, SignalOutcome.Filtered, ignored, "on the ignore list");
      if (expired > 0)
        Plugin.Journal.Record(signalClass, SignalOutcome.Expired, expired, "waited too long to still be news");
      if (stale > 0)
        Plugin.Journal.Record(signalClass, SignalOutcome.NoLongerTrue, stale, "stopped before it could be said");

      if (gone is not null)
        foreach (var key in gone)
          signal.Subjects.Remove(key);

      if (signal.Subjects.Count == 0)
        (doomed ??= []).Add(signalClass);
    }

    if (doomed is null)
      return;

    // Forget, not Remove: the class is leaving, so the outcome standing against it must leave with it or it
    // will swallow the first row of whatever episode comes next.
    foreach (var signalClass in doomed)
      Forget(signalClass);
  }

  /// <summary>
  /// Whether the news is still news.
  ///
  /// A watcher class asks "are they still looking"; an arrival asks "are they still here". Getting those the
  /// wrong way round would make a focus arrival have to be STARING at you to be announced.
  ///
  /// An escalation additionally checks that the episode is the SAME ONE, BY REFERENCE. The same key can hold a
  /// different episode two ways: the player looked away and back, or the user hit Forget and the scanner
  /// re-armed the edge — both make the next scan build a fresh <see cref="StareState"/>. Without this check a
  /// pending "watching you 15 seconds" could be spoken about an episode that has already ended, and its rung
  /// spent on a state object nothing owns any more. Identity, not presence, is the question.
  ///
  /// Nothing is spent when this returns false: the episode is gone, so there is no rung left to guard.
  /// </summary>
  private static bool StillTrue(SignalClass signalClass, WatcherKey key, PendingSubject subject,
    IReadOnlyDictionary<WatcherKey, StareState> watching, IReadOnlySet<WatcherKey> nearby)
  {
    if (signalClass == SignalClass.FocusArrival)
      return nearby.Contains(key);

    if (!watching.TryGetValue(key, out var live))
      return false;

    return subject.State is null || ReferenceEquals(live, subject.State);
  }

  /// <summary>
  /// WHICH switch is off, in the user's words.
  ///
  /// The whole point of the journal is that "a rule you forgot you set ate it" is the common case, so naming
  /// the rule IS the feature — "switched off" alone would only restate the silence. Ordered so the answer is
  /// the one the user is most likely to have forgotten.
  /// </summary>
  private static string WhyOff(SignalClass signalClass, Configuration config)
  {
    if (!config.AlertInChat && !config.AlertWithSound)
      return "both chat and sound are off";

    if (signalClass == SignalClass.FocusArrival)
      return !config.EnableNearbyList ? "the nearby half is off" : "arrival alerts are off";

    if (!config.EnableWatchers)
      return "the watcher half is off";

    if (!config.RecordWhileClosed && !Plugin.IsMainWindowOpen)
      return "the window is shut and 'record while closed' is off";

    return signalClass == SignalClass.StareEscalation ? "escalation alerts are off" : "a switch for this is off";
  }

  /// <summary>
  /// Whether a class is switched on right now, re-reading every gate its Raise method applied.
  ///
  /// One place, so the raise-time check and the pump-time check cannot drift apart — the raise methods each
  /// spell their own out for the Spend decision, and this is what re-asks the same question later.
  /// </summary>
  private static bool IsClassEnabled(SignalClass signalClass, Configuration config)
  {
    if (!config.AlertInChat && !config.AlertWithSound)
      return false;

    return signalClass switch
    {
      SignalClass.StareEscalation => config.EnableWatchers && config.AlertOnStareEscalation
        && (config.RecordWhileClosed || Plugin.IsMainWindowOpen),
      SignalClass.NewWatcher => config.EnableWatchers
        && (config.RecordWhileClosed || Plugin.IsMainWindowOpen),
      _ => config.AlertOnFocusArrival && config.EnableNearbyList,
    };
  }

  /// <summary>
  /// Which class gets this window.
  ///
  /// Most urgent first, EXCEPT that anything which has waited past <see cref="PromoteAfterMs"/> jumps ahead,
  /// oldest first. Strict priority alone starves the bottom of the ladder in exactly the zone where the top of
  /// it never stops firing, and a class that can never be heard is a class that should not exist.
  ///
  /// Callers must have checked <see cref="_pending"/> is non-empty.
  /// </summary>
  /// <param name="promoted">
  /// True when the winner jumped the ladder on age rather than earning the window on urgency.
  ///
  /// Reported rather than re-derived, because only this method knows which branch fired. The caller cannot
  /// recover it: comparing the winner's ordinal against the losers' says nothing — the most urgent class is
  /// ALSO the one that wins on strict priority — so a caller guessing from the outside gets the anti-starvation
  /// case exactly backwards, which is precisely the case the journal exists to explain.
  /// </param>
  private SignalClass Pick(long now, out bool promoted)
  {
    SignalClass? overdue = null;
    var oldest = long.MaxValue;

    foreach (var (signalClass, signal) in _pending)
    {
      if (now - signal.RaisedTicks < PromoteAfterMs)
        continue;

      // Oldest first — but on a TIE, the ladder, not whatever order the dictionary happens to hand back. The
      // tie is the ordinary case rather than a curiosity: TickCount64 moves in ~15.6ms steps and all three
      // Raise calls run microseconds apart inside one scan, so any two classes first raised in the same scan
      // carry byte-identical clocks. Without this the winner is decided by Dictionary entry-slot order, which
      // after a Remove is free-list order — i.e. an allocator detail would outrank the urgency ladder, and a
      // new watcher could lose to an arrival because of where a previous entry happened to be freed.
      if (signal.RaisedTicks > oldest
          || (signal.RaisedTicks == oldest && overdue is { } sameAge && signalClass >= sameAge))
        continue;

      oldest = signal.RaisedTicks;
      overdue = signalClass;
    }

    if (overdue is { } jumped)
    {
      promoted = true;
      return jumped;
    }

    promoted = false;
    var best = SignalClass.FocusArrival;
    var found = false;
    foreach (var signalClass in _pending.Keys)
    {
      if (found && signalClass >= best)
        continue;
      best = signalClass;
      found = true;
    }

    return best;
  }

  /// <summary>
  /// Says one class's line, and spends whatever that costs.
  ///
  /// Reports whether it actually reached the user, and NOTHING is spent on the way out if it did not — not the
  /// rung, and not the caller's cooldown window. The output gates were checked when the signal was raised, and
  /// this runs later: the render thread can untick both between the two. Assuming success would then consume a
  /// stare's rung in silence, and because the level never climbs twice, that episode would never be announced
  /// again — the same "spent without being said" trap as spending at detection, one layer further down.
  /// </summary>
  /// <returns>Whether a chat line or a sound actually happened.</returns>
  private static bool Emit(SignalClass signalClass, PendingSignal signal, Configuration config)
  {
    var subjects = new List<PendingSubject>(signal.Subjects.Count);
    foreach (var subject in signal.Subjects.Values)
      subjects.Add(subject);

    // Unreachable while Prune drops empty classes, and cheap to keep honest: an empty line is not a line, and
    // saying so here means no caller can burn a window on one.
    if (subjects.Count == 0)
      return false;

    var said = false;

    if (config.AlertInChat)
    {
      var (text, color) = Render(signalClass, subjects);
      var message = new SeStringBuilder()
        .AddUiForeground(color)
        .AddText(text)
        .AddUiForegroundOff()
        .Build();
      Plugin.ChatGui.Print(message, Plugin.ChatTag, Plugin.ChatTagColor);
      said = true;
    }

    // |=, not =: a sound that failed after a chat line landed must not un-say the chat line.
    if (config.AlertWithSound)
      said |= PlaySound(config.AlertSoundId);

    if (!said)
      return false;

    // Said, so the rung is spent. Only escalations carry a State; the pattern match is the class check.
    foreach (var subject in subjects)
      SpendOne(subject);

    return true;
  }

  /// <summary>
  /// One class's line.
  ///
  /// One line per class no matter how many subjects, because a crowd is one piece of news. The single-subject
  /// forms name the player; the plural forms count them, which is the only thing that scales.
  /// </summary>
  private static (string Text, ushort Color) Render(SignalClass signalClass, List<PendingSubject> subjects)
  {
    switch (signalClass)
    {
      case SignalClass.StareEscalation:
      {
        // The loudest one, not all of them: the worse rung is the one worth the line. Same "of the two facts,
        // this is the one worth the pixels" trade the row tint already makes.
        var worst = subjects[0];
        foreach (var subject in subjects)
          if (subject.Level > worst.Level || (subject.Level == worst.Level && subject.HeldMs > worst.HeldMs))
            worst = subject;

        var held = FormatHeld(worst.HeldMs);
        var text = worst.Level == StareLevel.Fixation
          ? $"Hrothgar smell {worst.Row.FullName} still watching you. {held} now."
          : $"Hrothgar smell {worst.Row.FullName} watching you {held}.";

        return (subjects.Count == 1 ? text : $"{text} And {subjects.Count - 1} more.", UiForegroundWatcher);
      }

      case SignalClass.FocusArrival:
        return (subjects.Count == 1
          ? $"Hrothgar smell {subjects[0].Row.FullName} come close."
          : $"Hrothgar smell {subjects.Count} you watch for come close.", UiForegroundFocus);

      default:
        return (subjects.Count == 1
          ? $"Hrothgar smell {subjects[0].Row.FullName} watching you."
          : $"Hrothgar smell {subjects.Count} eyes on you.", UiForegroundWatcher);
    }
  }

  /// <summary>
  /// Records a decision, but only when it is a different decision than the one already standing for this class.
  ///
  /// Every journalling call in the alert path goes through here rather than at
  /// <see cref="SignalJournal.Record"/> directly, because the repetition is a property of the CALLER — Pump runs
  /// per scan — and pushing the filter down into the journal would make it lie to any future caller that
  /// legitimately wants two identical rows. See <see cref="_lastJournalled"/>.
  /// </summary>
  private void Journal(SignalClass signalClass, SignalOutcome outcome, int subjects, string detail)
  {
    if (_lastJournalled.TryGetValue(signalClass, out var previous) && previous == outcome)
      return;

    _lastJournalled[signalClass] = outcome;
    Plugin.Journal.Record(signalClass, outcome, subjects, detail);
  }

  /// <summary>
  /// Drops a class from the pending set and forgets what was last said about it, so its next appearance
  /// journals from a clean slate rather than being mistaken for the tail of the last episode.
  ///
  /// The two dictionaries are only ever wrong together, so they are only ever written together. Removing from
  /// <see cref="_pending"/> alone would leave a stale outcome that silently swallows the first entry of the
  /// next episode — the failure would be a MISSING row in the diagnostic, which is the one bug this feature
  /// cannot have and the one no test notices.
  /// </summary>
  private void Forget(SignalClass signalClass)
  {
    _pending.Remove(signalClass);
    _lastJournalled.Remove(signalClass);
  }

  /// <summary>
  /// Forgets everything waiting, for the states where the world it described is gone: logged out, mid-zone, in
  /// PvP, or a scan that threw.
  ///
  /// Deliberate rather than incidental. A signal raised before a zone is about people who are no longer there,
  /// and Prune's re-check would drop them anyway on the next scan — but "anyway, eventually" is not good enough
  /// for the PvP path, where nothing should survive the boundary at all.
  /// </summary>
  public void ClearPending()
  {
    _pending.Clear();
    _lastJournalled.Clear();

    // The journal goes with them, and this is a PvP requirement rather than tidiness. The config window draws
    // in PvP — it has its own "PvP — hidden" banner — so a journal holding entries raised before the boundary
    // would be readable there, which is precisely what "nothing raised before a PvP boundary may survive it"
    // forbids. The cost is losing the trace across a zone; that is the price of the promise.
    Plugin.Journal.Clear();
  }

  /// <summary>Marks rungs as said-or-refused, so the episode never offers them again. See
  /// <see cref="RaiseStareEscalations"/> for which outcomes spend and which do not.</summary>
  private static void Spend(IReadOnlyList<(ScentRow Row, StareLevel Level, long HeldMs, StareState State)> escalations)
  {
    foreach (var escalation in escalations)
      Climb(escalation.State, escalation.Level);
  }

  /// <summary>Spends every rung waiting in one class's slot. Classes other than escalations carry no state, so
  /// this is a no-op for them.</summary>
  private static void Spend(PendingSignal signal)
  {
    foreach (var subject in signal.Subjects.Values)
      SpendOne(subject);
  }

  private static void SpendOne(PendingSubject subject)
  {
    if (subject.State is { } state)
      Climb(state, subject.Level);
  }

  /// <summary>
  /// Raises a rung, and NEVER lowers one.
  ///
  /// The guard is the whole of what makes <see cref="StareState.Level"/> monotonic, and it is not theoretical:
  /// a subject waiting in the slot carries the rung it was raised with, while the episode behind it keeps
  /// climbing. A pending "Stare" spoken after the state already reached "Fixation" would write Stare back over
  /// it — and the episode would then re-raise Fixation and announce it a second time. The one byte that stops a
  /// long stare repeating itself only works if it cannot go backwards.
  /// </summary>
  private static void Climb(StareState state, StareLevel level)
  {
    if (level > state.Level)
      state.Level = level;
  }

  /// <summary>
  /// How long someone has been staring, in words, for a chat line.
  ///
  /// Deliberately coarse. This is a sentence, not a stopwatch — "watching you 30 seconds" reads as a fact while
  /// "watching you 31.4 seconds" reads as a machine, and the number is only ever accurate to a rescan interval
  /// anyway. The singular is not pedantry either: both thresholds are sliders a user can drag to 1.
  /// </summary>
  private static string FormatHeld(long heldMs)
  {
    var seconds = heldMs / 1000;
    if (seconds < 90)
      return seconds == 1 ? "1 second" : $"{seconds} seconds";

    var minutes = seconds / 60;
    return minutes == 1 ? "over a minute" : $"over {minutes} minutes";
  }

  /// <summary>
  /// Plays the configured chat SFX once, ignoring the cooldown — a Test button that stays silent because
  /// something fired nine seconds ago is not a test.
  ///
  /// Marshalled because it is invoked from Draw and the sound is a game call.
  /// </summary>
  public void TestSound()
  {
    var soundId = Plugin.Configuration.AlertSoundId;
    Plugin.Framework.RunOnFrameworkThread(() => PlaySound(soundId));
  }

  /// <summary>
  /// The one FFXIVClientStructs call in this file, isolated so a game patch that moves the function costs
  /// the alert's sound and nothing else. The chat line still lands; that is the point of catching here
  /// rather than around the whole alert.
  /// </summary>
  /// <returns>
  /// Whether the sound actually played. Load-bearing for the one user who runs sound-only with chat off: after
  /// a patch moved this function, a swallowed failure reported as success would burn the cooldown window and
  /// spend the rung on an alert that made no sound and printed nothing — silence that no setting explains.
  /// </returns>
  private static bool PlaySound(int soundId)
  {
    try
    {
      UIGlobals.PlayChatSoundEffect((uint)Math.Clamp(soundId, MinSoundId, MaxSoundId));
      return true;
    }
    catch (Exception ex)
    {
      Plugin.Log.Warning(ex, "Chat sound effect {SoundId} failed", soundId);
      return false;
    }
  }
}
