using System;
using System.Collections.Generic;

namespace HrothgarScent.Scent;

/// <summary>What a term looks at. Name is what a bare word searches.</summary>
public enum ScentField : byte
{
  Name,
  World,
  FreeCompany,
  Job,
  Race,
  Note,
}

/// <summary>How a term compares.</summary>
public enum MatchOp : byte
{
  /// <summary>The default, and deliberately so — see <see cref="ScentQuery"/>.</summary>
  Contains,
  StartsWith,
  EndsWith,
  Exact,

  /// <summary>The field has anything in it. <c>note:*</c></summary>
  Present,

  /// <summary>The field is empty. <c>note:!</c></summary>
  Absent,
}

/// <summary>One condition. Terms are ANDed: every one must hold.</summary>
public sealed record QueryTerm(ScentField Field, MatchOp Op, string Value, bool Negated);

/// <summary>
/// A parsed search box.
///
/// THE GRAMMAR:
///   bob smith          name contains "bob smith"
///   world:sarg         home world contains "sarg"
///   fc:free company    FC tag contains "free company"        (values may contain spaces)
///   job:whm            job matches EITHER the abbreviation OR the full name
///   race:lala          race contains "lala"
///   note:griefer       your note about them contains "griefer"
///   note:*             you have written a note about them
///   note:!             you have not
///   =Bob Smith         name is exactly "Bob Smith"
///   sarg*              starts with          *sarg   ends with          *sarg*  contains
///   !bob               name does NOT contain bob     !world:sarg  not on a world containing sarg
///
/// FOUR DECISIONS WORTH KNOWING, each of which is a bug avoided rather than a preference:
///
/// 1. CONTAINS IS THE DEFAULT, and <c>*</c> NARROWS rather than widens. The prior art's grammar makes a bare
///    word mean EXACTLY, so its own documented example — searching notes for "griefer" — cannot match a note
///    reading "this guy is a griefer". Worse, importing that rule here would silently turn today's
///    substring name search into an exact one and hide nearly everybody. <c>=</c> gets its own operator instead.
///
/// 2. VALUES MAY CONTAIN SPACES, because EVERY FFXIV PLAYER NAME DOES. Splitting the query on spaces and ANDing
///    the pieces — which is what the prior art does — turns "Bob Smith" into (contains "Bob" AND contains
///    "Smith"), which also matches "Smithers Bobson", and shatters <c>fc:Free Company</c> outright. So a term's
///    value runs until the next <c>field:</c> boundary, not until the next space.
///
/// 3. AN UNFINISHED TERM MATCHES EVERYTHING. There is no debounce — the box filters on the frame you type — so
///    every prefix of what the user is typing is applied, including <c>note:</c> with nothing after it yet.
///    Treating that as "note contains empty string" would be harmless, but treating it as a failed parse and
///    falling back to a name search would mean searching names for the literal text "note:", which matches
///    nobody. A filter that empties the list mid-keystroke, for a reason the user cannot see, is the thing this
///    window's design refuses above all. So: no operand, no term.
///
/// 4. AN UNKNOWN FIELD IS REPORTED, NOT GUESSED AT. <c>wrld:sarg</c> cannot degrade to a name search — no
///    character name contains a colon, so it would match zero players and look like the plugin broke. It
///    matches everything and says so instead; see <see cref="UnknownFields"/>.
///
/// Immutable once parsed, and parsed on the render thread from the search box. Reads nothing but strings.
/// </summary>
public sealed class ScentQuery
{
  /// <summary>The empty box: every row survives.</summary>
  public static readonly ScentQuery MatchAll = new([], []);

  private readonly QueryTerm[] _terms;

  private ScentQuery(QueryTerm[] terms, string[] unknownFields)
  {
    _terms = terms;
    UnknownFields = unknownFields;

    foreach (var term in terms)
    {
      if (term.Field == ScentField.Note)
      {
        NeedsMark = true;
        break;
      }
    }
  }

  /// <summary>Field names the user typed that do not exist. Surfaced by the toolbar so a query that matches
  /// everyone says why, rather than looking like it worked.</summary>
  public IReadOnlyList<string> UnknownFields { get; }

  /// <summary>Whether any term reads a mark, so callers can skip the lookup entirely when none does.</summary>
  public bool NeedsMark { get; }

  /// <summary>Whether this filters at all. False for an empty box AND for a box holding only unfinished terms.</summary>
  public bool IsActive => _terms.Length > 0;

  /// <summary>
  /// Whether <paramref name="row"/> survives. <paramref name="mark"/> may be null; only <c>note:</c> reads it,
  /// and <see cref="NeedsMark"/> says whether it is worth looking up.
  /// </summary>
  public bool Matches(ScentRow row, MarkedPlayer? mark)
  {
    foreach (var term in _terms)
    {
      if (!MatchTerm(term, row, mark))
        return false;
    }

    return true;
  }

  private static bool MatchTerm(QueryTerm term, ScentRow row, MarkedPlayer? mark)
  {
    // Job is the one field with two spellings on screen, and it must match EITHER unconditionally. Matching
    // "whichever UseJobAbbreviations renders" would make job:pld find nothing whenever the user has full names
    // switched on — a search silently broken by an unrelated display checkbox in another tab.
    var hit = term.Field == ScentField.Job
      ? Test(term, row.JobAbbreviation) || Test(term, row.JobName)
      : Test(term, Read(term.Field, row, mark));

    return term.Negated ? !hit : hit;
  }

  private static string Read(ScentField field, ScentRow row, MarkedPlayer? mark) => field switch
  {
    ScentField.World => row.HomeWorldName,
    ScentField.FreeCompany => row.CompanyTag,
    ScentField.Race => row.RaceName,
    ScentField.Note => mark?.Note ?? string.Empty,
    _ => row.Name,
  };

  private static bool Test(QueryTerm term, string value) => term.Op switch
  {
    MatchOp.Present => !string.IsNullOrWhiteSpace(value),
    MatchOp.Absent => string.IsNullOrWhiteSpace(value),
    MatchOp.Exact => string.Equals(value, term.Value, StringComparison.OrdinalIgnoreCase),
    MatchOp.StartsWith => value.StartsWith(term.Value, StringComparison.OrdinalIgnoreCase),
    MatchOp.EndsWith => value.EndsWith(term.Value, StringComparison.OrdinalIgnoreCase),
    _ => value.Contains(term.Value, StringComparison.OrdinalIgnoreCase),
  };

  /// <summary>
  /// Parses the box. Never throws and never returns null: the worst case is <see cref="MatchAll"/>, because a
  /// search that hides everyone for an unexplained reason is worse than one that hides nobody.
  /// </summary>
  public static ScentQuery Parse(string raw)
  {
    if (string.IsNullOrWhiteSpace(raw))
      return MatchAll;

    var fields = FindFieldTokens(raw);
    var terms = new List<QueryTerm>();
    List<string>? unknown = null;

    // Everything before the first field: is a name, spaces and all. This is what makes "Bob Smith world:sarg"
    // one name term rather than two words ANDed.
    var nameEnd = fields.Count > 0 ? fields[0].Start : raw.Length;
    AddTerm(terms, ScentField.Name, raw[..nameEnd].Trim(), false, false);

    for (var i = 0; i < fields.Count; i++)
    {
      var token = fields[i];

      // To the next field:, not to the next space — see decision 2 on the class.
      var valueEnd = i + 1 < fields.Count ? fields[i + 1].Start : raw.Length;
      var value = raw[(token.ColonIndex + 1)..valueEnd].Trim();

      if (FieldOf(token.Name) is { } field)
        AddTerm(terms, field, value, token.Negated, true);
      else
        (unknown ??= []).Add(token.Name);
    }

    return terms.Count == 0 && unknown is null
      ? MatchAll
      : new ScentQuery([.. terms], unknown is null ? [] : [.. unknown]);
  }

  /// <summary>
  /// Every <c>field:</c> in the query, including misspelled ones — an unknown field has to be SEEN to be
  /// reported, and a field token that went unrecognised would otherwise be swallowed into the name term and
  /// match nobody.
  ///
  /// A token starts at the string's start or after a space, may lead with <c>!</c>, and is letters then a
  /// colon. Letters only, so a note reading "10:30" or an FC tag with a colon in it cannot be mistaken for one.
  /// </summary>
  private static List<(int Start, int ColonIndex, bool Negated, string Name)> FindFieldTokens(string raw)
  {
    var found = new List<(int, int, bool, string)>();

    for (var i = 0; i < raw.Length; i++)
    {
      if (i > 0 && raw[i - 1] != ' ')
        continue;

      var j = i;
      var negated = raw[j] == '!';
      if (negated)
        j++;

      var wordStart = j;
      while (j < raw.Length && char.IsAsciiLetter(raw[j]))
        j++;

      if (j > wordStart && j < raw.Length && raw[j] == ':')
        found.Add((i, j, negated, raw[wordStart..j].ToLowerInvariant()));
    }

    return found;
  }

  private static ScentField? FieldOf(string name) => name switch
  {
    "name" => ScentField.Name,
    "world" => ScentField.World,
    "fc" or "company" => ScentField.FreeCompany,
    "job" or "class" => ScentField.Job,
    "race" => ScentField.Race,
    "note" or "notes" => ScentField.Note,
    _ => null,
  };

  /// <summary>
  /// Turns one value into a term, or into nothing.
  ///
  /// NOTHING is a real answer and the important one: an empty or still-being-typed value adds no term, so the
  /// query matches everything rather than nobody. See decision 3 on the class.
  /// </summary>
  /// <param name="explicitField">
  /// Whether the user actually typed <c>field:</c>. This is what tells a lone <c>!</c> apart from
  /// <c>note:!</c> — the same character, and opposite meanings. With a field it is "this field is empty"; on
  /// its own it is somebody who has typed a negation and not yet the word, and must match everyone rather than
  /// asking for players whose NAME is empty, of whom there are none.
  /// </param>
  private static void AddTerm(List<QueryTerm> terms, ScentField field, string value, bool negated,
    bool explicitField)
  {
    // BEFORE the negation strip below, which would otherwise eat the "!" and leave an empty value — silently
    // turning note:! (the one way to ask who you have NOT written about) into a no-op that matches everybody.
    if (explicitField)
    {
      if (value == "*")
      {
        terms.Add(new QueryTerm(field, MatchOp.Present, string.Empty, negated));
        return;
      }

      if (value == "!")
      {
        terms.Add(new QueryTerm(field, MatchOp.Absent, string.Empty, negated));
        return;
      }
    }

    if (!negated && value.StartsWith('!'))
    {
      negated = true;
      value = value[1..].Trim();
    }

    if (value.Length == 0)
      return;

    // A bare "*" or "!" with no field is mid-thought, not a question. Matching everyone is the only safe read:
    // "name is empty" matches nobody, and emptying the list under a half-typed query is the thing this window
    // refuses to do.
    if (value == "*" || value == "!")
      return;

    if (value[0] == '=')
    {
      var exact = value[1..].Trim();
      if (exact.Length > 0)
        terms.Add(new QueryTerm(field, MatchOp.Exact, exact, negated));
      return;
    }

    var anchoredStart = value.EndsWith('*');
    var anchoredEnd = value.StartsWith('*');
    var core = value.Trim('*').Trim();

    // "*" and "**" already returned above; this catches "* *" and friends rather than searching for nothing.
    if (core.Length == 0)
      return;

    var op = (anchoredEnd, anchoredStart) switch
    {
      (true, true) => MatchOp.Contains,
      (false, true) => MatchOp.StartsWith,
      (true, false) => MatchOp.EndsWith,
      _ => MatchOp.Contains,
    };

    terms.Add(new QueryTerm(field, op, core, negated));
  }
}
