using System;
using System.Collections.Generic;
using System.Threading;

namespace HrothgarScent.Scent;

/// <summary>Why a signal did or did not reach the user.</summary>
public enum SignalOutcome : byte
{
  /// <summary>It was said.</summary>
  Said,

  /// <summary>The user's cooldown was still running, or something more urgent went first. It waits.</summary>
  Waiting,

  /// <summary>
  /// It got its turn and nothing came out — the chat line is off and the sound call failed.
  ///
  /// Its own outcome rather than folded into <see cref="Waiting"/>, because it is the one silence no setting
  /// explains. Without it the signal sits until the TTL and is then reported as <see cref="Expired"/> —
  /// "waited too long" — which is exactly false: it never waited, it got a window on every scan and the output
  /// failed each time. An instrument that blames the wrong rule is worse than no instrument.
  /// </summary>
  OutputFailed,

  /// <summary>A switch is off — the half, the alert, chat and sound both, or the window shut with
  /// RecordWhileClosed off. The user said no.</summary>
  SwitchedOff,

  /// <summary>The subject is party, friend, alliance or ignored, and the user asked not to hear about them.</summary>
  Filtered,

  /// <summary>They stopped watching, or walked off, before it could be said.</summary>
  NoLongerTrue,

  /// <summary>It waited longer than it was worth and was dropped.</summary>
  Expired,
}

/// <summary>
/// One decision, frozen. Immutable and write-once: nothing ever revises a decision after it is recorded, which
/// is what makes this cheaper to publish than <see cref="WatcherEntry"/> and is why there is no ring buffer
/// behind it — see <see cref="SignalJournal"/>.
/// </summary>
public sealed record JournalEntry(
  DateTime When,
  SignalClass Class,
  SignalOutcome Outcome,
  int Subjects,
  string Detail);

/// <summary>
/// The last few things the alert path decided, and why.
///
/// A MISSING ALERT LEAVES NO TRACE, and that is the bug this exists to kill. The prior art's alert tracker is
/// three open issues — a proximity alert that fires once per session then never, a rename alert that repeats
/// six times in four minutes, a world-transfer alert that flip-flops forever — all invisible from outside by
/// construction, so users report "it stopped working" and nobody can reproduce it. Its actual answer to
/// introspection was to ship a raw SQL box in the settings window.
///
/// It also finishes an argument the plugin already makes: the nearby list refuses to let three kinds of
/// nothing look identical, because saying the coast is clear when you merely stopped looking is the one lie it
/// will not tell. Silence from the alert path is that same lie, one level down.
///
/// IN MEMORY, DIES AT LOGOUT, CAP NOT CONFIGURABLE. The cap is a const rather than the slider its neighbour
/// HistoryLimit gets, and the difference is real: HistoryLimit sizes the user's own record OF PEOPLE, which is
/// theirs to size, while this is a readout of the PLUGIN'S OWN DECISIONS — an instrument, whose usefulness
/// depends on a fixed and predictable window. Nothing here is ever exported or persisted, for the same reason
/// the watcher log is not.
///
/// THREADING mirrors <see cref="ScentSnapshot"/>: written on the framework thread (AlertService is the only
/// writer and the scanner is its only caller), read from Draw. There is no ring and no lock — a published
/// immutable list, swapped by one reference write. WatcherLog keeps both a live dictionary and a published
/// clone because its entries are MUTATED after creation; a journal entry never is, so the second copy would be
/// the same data twice.
/// </summary>
public sealed class SignalJournal
{
  /// <summary>
  /// How many decisions are kept.
  ///
  /// Small on purpose. This answers "why did nothing happen just now", which is a question about the last
  /// minute — not a log. Bigger would invite it to be treated as one, and a log is the thing this plugin does
  /// not keep about people.
  /// </summary>
  private const int Cap = 50;

  private IReadOnlyList<JournalEntry> _published = [];

  /// <summary>The decisions, newest first. Safe from any thread: the list is immutable and never mutated after
  /// publication, so a reader gets one whole version or another.</summary>
  public IReadOnlyList<JournalEntry> Snapshot() => Volatile.Read(ref _published);

  /// <summary>
  /// Records one decision. Framework thread only.
  ///
  /// UNCONDITIONAL: this writes what it is given. The rate limiting lives at the caller, in
  /// AlertService.Journal, which drops a decision identical to the one already standing for that class — and it
  /// has to live there, because the repetition is the CALLER's property. Pump runs on every scan, so a class
  /// held by the cooldown offers the same row four times a second; the journal cannot tell that from a caller
  /// that genuinely means it, and a filter here would silently eat the second row of any future one.
  ///
  /// ONE ENTRY PER CLASS PER DECISION, never per subject: a filtered 24-man alliance is one row with a count of
  /// 24, not 24 rows. The bus already coalesces a crowd into one line; the journal has to match it or the cap is
  /// a lie, and the ring turns over in exactly the crowded zone where alerts go missing and the user comes
  /// looking — throwing away the answer they came for at the moment they came for it.
  /// </summary>
  /// <param name="subjects">How many players this decision covered. The count is the payload; see
  /// <paramref name="detail"/>.</param>
  /// <param name="detail">
  /// A short reason, and NEVER a name for a suppressed subject. "Never show or announce them again" is the
  /// oldest promise this plugin makes, and a journal that printed an ignored player's name would put it back on
  /// screen in the same window whose Filters tab is where the user went to erase them — which is the promise
  /// broken by the feature built to catch broken promises. A count says everything the diagnostic needs.
  /// </param>
  public void Record(SignalClass signalClass, SignalOutcome outcome, int subjects, string detail)
  {
    var entry = new JournalEntry(DateTime.Now, signalClass, outcome, subjects, detail);

    var current = _published;
    var size = Math.Min(current.Count + 1, Cap);
    var next = new JournalEntry[size];

    // Newest first: the answer to "why the silence" is always the most recent thing, and a reader should not
    // have to scroll to it. The tail is whatever still fits.
    next[0] = entry;
    for (var i = 1; i < size; i++)
      next[i] = current[i - 1];

    Volatile.Write(ref _published, next);
  }

  /// <summary>
  /// Forgets everything.
  ///
  /// Called wherever the scanner discards its pending signals — logged out, mid-zone, in PvP, or a scan that
  /// threw. The entries describe a world that has ended, and the PvP case is the one that makes this
  /// mandatory rather than tidy: nothing raised before that boundary may survive it, and this window is
  /// readable in PvP because the config window is.
  /// </summary>
  public void Clear()
  {
    if (Snapshot().Count > 0)
      Volatile.Write(ref _published, []);
  }
}
