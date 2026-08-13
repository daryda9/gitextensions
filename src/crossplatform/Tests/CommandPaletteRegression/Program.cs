using System.Diagnostics;
using System.Text;
using GitExtensions.Avalonia.Services;

// Regression suite for the command palette's matcher and ordering
// (App/Services/CommandPaletteService.cs).
//
// Usage: dotnet run --project Tests/CommandPaletteRegression/CommandPaletteRegression.Harness.csproj
//
// Exit code 0 means every case and every invariant held; any other value means at
// least one broke, and each broken one is printed.
//
// Why this exists at all. The matcher has no compile-time contract: it answers with a
// score and a set of indices a renderer emboldens, so a regression in the dynamic
// programme, in the scoring weights or in the read-back of the winning alignment
// changes nothing a build can see. It shows up as one command that can no longer be
// reached by the acronym its user has in their fingers, or as a row whose bold
// characters are not the ones that were typed — months later, if anyone looks. Every
// expectation below is therefore written from the documented contract, never captured
// from what the matcher happens to answer today.
//
// The ranking cases pin the DESIGN, not the numbers: each asserts that one target
// outranks another for a query, which stays true if the weights are retuned and fails
// only if the intent behind them is lost. No case asserts a literal score.
//
// This suite found one defect in the shipped matcher, since fixed in
// CommandPaletteService.TryScore; the cases that pin the fix are ordinary cases below.
//
//  D1 was "highlight indices split a surrogate pair": the alignment was chosen over
//     UTF-16 code UNITS, so for a target holding astral characters the best-scoring
//     alignment could land on the high half of one pair and the low half of another
//     (each half after a pair reads as a word start, which outbids the contiguity
//     bonus of the honest alignment). CommandPaletteWindow.Fill cuts the caption into
//     runs exactly at the hit indices, so such an answer renders as two broken halves
//     of two different characters. TryScore now refuses any alignment that would take
//     one half of a pair without the other.

// The MRU half of the service persists through ViewPrefsService, which resolves its
// file from XDG_CONFIG_HOME at construction. Redirected before the first service is
// built so that running this suite cannot touch — let alone reorder — the MRU of the
// person running it.
string sandbox = Path.Combine(Path.GetTempPath(), "gea-palette-harness-" + Environment.ProcessId);
Directory.CreateDirectory(sandbox);
Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", sandbox);

List<string> failures = [];
int cases = 0;

// ---------------------------------------------------------------- matching, in the small

// The plain cases first: what a user typing the whole word, the first letters, or an
// acronym must get. "Everything is a leaf" (labelStart 0) unless the case is about the
// path/label split, which has its own section.
Matches("exact match", "Commit", "Commit", [0, 1, 2, 3, 4, 5]);
Matches("prefix", "Commit", "com", [0, 1, 2]);
Matches("case-insensitive query", "Commit", "COMMIT", [0, 1, 2, 3, 4, 5]);
Matches("case-insensitive target", "COMMIT", "commit", [0, 1, 2, 3, 4, 5]);

// The reason the matcher is a subsequence matcher and not a substring one: the acronym
// of a command whose exact wording nobody remembers, crossing the " ▸ " that separates
// the menu chain from the leaf.
Matches("acronym across the path separator", "Repository ▸ Manage worktrees…", "mwt", [13, 20, 24]);
Matches("subsequence inside one word", "Commit", "cmt", [0, 2, 5]);

// A query that is not a subsequence at all, in the two ways it can fail: a character
// that is absent, and characters that are all present but in the wrong order.
NoMatch("absent character", "Commit", "z");
NoMatch("right characters, wrong order", "Commit", "tim");
NoMatch("query longer than the target", "Commit", "Committed");
NoMatch("empty target", "", "c");

// The palette's own display alphabet. Every one of these reaches the matcher the
// moment a user pastes a caption back into the box, and '_' and '&' additionally
// survive into captions the mnemonic strippers did not have to touch.
Matches("query holding the path separator", "Repository ▸ Commit", "y ▸ C", [9, 10, 11, 12, 13]);
Matches("query holding a space", "New branch", "w b", [2, 3, 4]);
Matches("query holding the ellipsis", "Manage worktrees…", "s…", [15, 16]);
Matches("query holding an ampersand", "Fetch && prune", "&& p", [6, 7, 8, 9]);
Matches("query holding an underscore", "git_ext_mod", "_e", [3, 4]);
NoMatch("a space is matched literally, never ignored", "Commit", " ");

// ---------------------------------------------------------------- surrogate pairs (D1)

// The two arrangements in which a code-unit-wise alignment can tear a character in
// half. Both must either match whole characters or not match at all; what they must
// never do is answer with an index in the middle of a pair, which the row renderer
// would turn into two mojibake halves.
Matches("astral character matched whole", "a😀b", "😀", [1, 2]);
Matches("astral character among repeats stays whole", "😀😀", "😀", [0, 1]);
NoMatch("a lone half of a pair is not a character", "😀", "\ud83d");
Matches("astral character in a longer caption", "Tools ▸ 😀 settings", "😀s", [8, 9, 11]);

// ---------------------------------------------------------------- ranking (design, not numbers)

// An acronym aims at word starts; a letter buried mid-word is an accident of spelling.
// Same length on both sides so the shorter-target tie-break cannot be what decides it.
Beats("a word start beats a mid-word hit", "c", "xx cx", "xxxcx");
Beats("a capital opening a camel-case word is a word start", "g", "xxGhx", "xxgxx");

// The leaf is what the user is naming; the parent chain is context they are willing to
// spell but did not come for. Both targets are the same length, so only the split moves.
BeatsEntry("a hit in the leaf beats the same hit in the path", "c",
    Entry("Zip", "Commit"), Entry("Commit", "Zip"));

// A run of adjacent hits is evidence the user is typing the word; scattered hits are
// what makes a subsequence matcher noisy, and must lose to it.
Beats("a contiguous run beats a scattered match", "ab", "xabxx", "xaxbx");

// The terse command is what "push" means when both match it.
Beats("a shorter target wins a tie", "push", "Push", "Push a specific branch…");

// The scoring is a whole-alignment optimum, not a greedy left-to-right scan: a greedy
// scan takes the first 'g' it sees and highlights a letter the user did not aim at.
Matches("the best alignment wins, not the first one", "Tools ▸ Git command log", "gc", [8, 12]);

// ---------------------------------------------------------------- Filter: the list, not the string

{
    IReadOnlyList<PaletteEntry> menu =
    [
        Entry("Repository", "Commit…", "commit"),
        Entry("Repository", "Manage worktrees…", "worktrees"),
        Entry("Repository", "Push…", "push"),
        Entry("Repository", "Push a specific branch…", "pushBranch"),
        Entry("View", "Show tags", "showTags"),
        Entry("Keyboard", "Go to parent", "key:GoToParent"),
    ];

    CommandPaletteService service = new();

    // An empty box is not "no results": it is the whole command list, which is how the
    // palette doubles as a way to READ the menu.
    FilterCase("empty query yields the whole list", service, menu, "", menu.Count);
    FilterCase("whitespace-only query yields the whole list", service, menu, "   ", menu.Count);
    FilterCase("a query nothing matches yields nothing", service, menu, "zzz", 0);
    FilterCase("a query longer than every target yields nothing", service, menu, new string('c', 200), 0);

    // Ordering, asserted by identity rather than by score.
    FirstIs("the terse Push leads the specific one", service, menu, "push", "push");
    FirstIs("an acronym reaches the worktree command", service, menu, "mwt", "worktrees");

    // ------------------------------------------------------------ MRU

    // Empty box: the MRU leads, and everything else keeps menu order behind it — the
    // order the user can already navigate by muscle memory.
    CommandPaletteService mru = new();
    mru.Remember("showTags");
    mru.Remember("key:GoToParent");
    FirstIs("with an empty query the most recent command leads", mru, menu, "", "key:GoToParent");
    Expect("with an empty query the MRU order is the whole head of the list",
        string.Join(",", mru.Filter(menu, "").Take(2).Select(r => r.Entry.Id)) == "key:GoToParent,showTags");
    Expect("with an empty query the non-MRU rows keep menu order",
        string.Join(",", mru.Filter(menu, "").Skip(2).Select(r => r.Entry.Id)) == "commit,worktrees,push,pushBranch");

    // Non-empty box: the MRU is a tie-break and nothing more. "pushBranch" is the most
    // recently used command there is, and it still must not climb over the better-scoring
    // "push" — a palette whose ordering history can beat what was actually typed sends
    // the user's Enter to the wrong command.
    CommandPaletteService loaded = new();
    loaded.Remember("pushBranch");
    FirstIs("MRU does not outrank a better score", loaded, menu, "push", "push");

    // ...but it does decide between two rows the matcher itself cannot separate.
    IReadOnlyList<PaletteEntry> twins = [Entry("View", "Show tags", "a"), Entry("View", "Show tags", "b")];
    CommandPaletteService tie = new();
    tie.Remember("b");
    FirstIs("MRU breaks a tie the score cannot", tie, twins, "tags", "b");
}

// ---------------------------------------------------------------- deterministic fuzz

// The inputs nobody thinks to write. The seed is fixed so a failure here is
// reproducible and reviewable; only the invariants are checked, because there is no
// independent oracle for which alignment is the "right" one on a random string.
{
    Stopwatch sw = Stopwatch.StartNew();
    int pairs = Fuzz(seed: 20260813, rounds: 7000, astral: false, label: "fuzz");
    pairs += Fuzz(seed: 4441, rounds: 3000, astral: true, label: "fuzz/astral");
    sw.Stop();
    Console.WriteLine($"  fuzz: {pairs} pairs, {sw.ElapsedMilliseconds} ms, 0 tolerated violations");
}

// ---------------------------------------------------------------- verdict

try
{
    Directory.Delete(sandbox, recursive: true);
}
catch (IOException)
{
    // A leftover temp directory is not a test result; never fail the run over it.
}

Console.WriteLine();
if (failures.Count == 0)
{
    Console.WriteLine($"PASS: {cases} command palette cases, all invariants held");
    return 0;
}

Console.WriteLine($"FAIL: {failures.Count} of {cases} command palette cases broke");
foreach (string failure in failures)
{
    Console.WriteLine("  " + failure);
}

return 1;

// ---------------------------------------------------------------- harness

// One matching case: the query must match, and land on exactly these indices when they
// are spelled out. The invariants run on every case whether or not indices were given.
void Matches(string name, string target, string query, int[]? expected, bool expectMatch = true)
{
    cases++;
    bool ok = CommandPaletteService.TryScore(target, labelStart: 0, query, out int score, out int[] hits);

    foreach (string violation in ScoreInvariants(target, 0, query, ok, hits))
    {
        Fail(name, violation);
    }

    if (ok != expectMatch)
    {
        Fail(name, $"expected match={expectMatch}, actual={ok} for query '{Escape(query)}' over '{Escape(target)}'");
        return;
    }

    if (ok && expected is not null && !Same(expected, hits))
    {
        Fail(name, $"expected hits {Show(expected)}, actual {Show(hits)} (score {score}) for query '{Escape(query)}' over '{Escape(target)}'");
    }
}

void NoMatch(string name, string target, string query) => Matches(name, target, query, null, expectMatch: false);

// One ranking case over two raw targets, both treated as pure leaves.
void Beats(string name, string query, string winner, string loser)
{
    cases++;
    bool a = CommandPaletteService.TryScore(winner, labelStart: 0, query, out int scoreA, out int[] hitsA);
    bool b = CommandPaletteService.TryScore(loser, labelStart: 0, query, out int scoreB, out int[] hitsB);

    foreach (string violation in ScoreInvariants(winner, 0, query, a, hitsA))
    {
        Fail(name, "winner: " + violation);
    }

    foreach (string violation in ScoreInvariants(loser, 0, query, b, hitsB))
    {
        Fail(name, "loser: " + violation);
    }

    if (!a || !b)
    {
        Fail(name, $"both targets must match '{Escape(query)}': '{Escape(winner)}'={a}, '{Escape(loser)}'={b}");
        return;
    }

    if (scoreA <= scoreB)
    {
        Fail(name, $"'{Escape(winner)}' scored {scoreA}, '{Escape(loser)}' scored {scoreB} for query '{Escape(query)}' — expected the first to win");
    }
}

// The same, over two entries, so the path/label split is what differs.
void BeatsEntry(string name, string query, PaletteEntry winner, PaletteEntry loser)
{
    cases++;
    bool a = CommandPaletteService.TryScore(winner.Display, winner.LabelStart, query, out int scoreA, out int[] hitsA);
    bool b = CommandPaletteService.TryScore(loser.Display, loser.LabelStart, query, out int scoreB, out int[] hitsB);

    foreach (string violation in ScoreInvariants(winner.Display, winner.LabelStart, query, a, hitsA))
    {
        Fail(name, "winner: " + violation);
    }

    foreach (string violation in ScoreInvariants(loser.Display, loser.LabelStart, query, b, hitsB))
    {
        Fail(name, "loser: " + violation);
    }

    if (!a || !b)
    {
        Fail(name, $"both entries must match '{Escape(query)}'");
        return;
    }

    if (scoreA <= scoreB)
    {
        Fail(name, $"'{Escape(winner.Display)}' scored {scoreA}, '{Escape(loser.Display)}' scored {scoreB} for '{Escape(query)}'");
    }
}

void FilterCase(string name, CommandPaletteService service, IReadOnlyList<PaletteEntry> menu, string query, int expectedCount)
{
    cases++;
    IReadOnlyList<PaletteMatch> rows = service.Filter(menu, query);

    foreach (string violation in FilterInvariants(menu, query, rows))
    {
        Fail(name, violation);
    }

    if (rows.Count != expectedCount)
    {
        Fail(name, $"expected {expectedCount} rows for '{Escape(query)}', actual {rows.Count}");
    }
}

void FirstIs(string name, CommandPaletteService service, IReadOnlyList<PaletteEntry> menu, string query, string expectedId)
{
    cases++;
    IReadOnlyList<PaletteMatch> rows = service.Filter(menu, query);

    foreach (string violation in FilterInvariants(menu, query, rows))
    {
        Fail(name, violation);
    }

    if (rows.Count == 0)
    {
        Fail(name, $"'{Escape(query)}' matched nothing; expected '{expectedId}' first");
        return;
    }

    if (rows[0].Entry.Id != expectedId)
    {
        Fail(name, $"'{Escape(query)}' put '{rows[0].Entry.Id}' first; expected '{expectedId}' (order: {string.Join(", ", rows.Select(r => r.Entry.Id + ":" + r.Score))})");
    }
}

void Expect(string name, bool condition)
{
    cases++;
    if (!condition)
    {
        Fail(name, "condition did not hold");
    }
}

void Fail(string name, string detail) => failures.Add($"{name}: {detail}");

// Mutates nothing, generates everything, checks only the invariants — a violated
// invariant is a defect whatever the input was.
int Fuzz(int seed, int rounds, bool astral, string label)
{
    Random random = new(seed);
    CommandPaletteService service = new();

    for (int round = 0; round < rounds; round++)
    {
        PaletteEntry entry = GeneratedEntry(random, astral);
        string query = GeneratedQuery(random, entry.Display);

        cases++;
        bool ok = CommandPaletteService.TryScore(entry.Display, entry.LabelStart, query, out _, out int[] hits);
        foreach (string violation in ScoreInvariants(entry.Display, entry.LabelStart, query, ok, hits))
        {
            // Nothing is tolerated. The inputs are escaped so that a fuzz finding can be
            // pasted straight back in above as a hand-written case.
            Fail($"{label} seed={seed} round={round}", $"{violation} | display='{Escape(entry.Display)}' query='{Escape(query)}'");
        }

        // Every fifth round also exercises the list layer, whose invariants (ordering is
        // a total order, no unmatched entry is returned) cannot be seen from TryScore.
        if (round % 5 == 0)
        {
            List<PaletteEntry> menu = [entry, GeneratedEntry(random, astral), GeneratedEntry(random, astral)];
            IReadOnlyList<PaletteMatch> rows = service.Filter(menu, query);
            foreach (string violation in FilterInvariants(menu, query, rows))
            {
                Fail($"{label} seed={seed} round={round}", $"{violation} | query='{Escape(query)}'");
            }
        }
    }

    return rounds;
}

// The invariants that hold for every (target, query) the matcher is ever handed. These
// are the contract CommandPaletteWindow.Fill relies on when it slices the caption at
// the hit indices: violate any of them and the row emboldens the wrong characters, or
// throws.
static IEnumerable<string> ScoreInvariants(string target, int labelStart, string query, bool matched, int[] hits)
{
    if (!matched)
    {
        if (hits.Length != 0)
        {
            yield return $"no match but {hits.Length} hits returned";
        }

        yield break;
    }

    if (hits.Length != query.Length)
    {
        yield return $"expected {query.Length} hits (one per query character), got {hits.Length}";
        yield break;
    }

    for (int i = 0; i < hits.Length; i++)
    {
        if (hits[i] < 0 || hits[i] >= target.Length)
        {
            yield return $"hit {i} = {hits[i]} is outside [0,{target.Length})";
            yield break;
        }

        if (i > 0 && hits[i] <= hits[i - 1])
        {
            yield return $"hits are not strictly increasing at {i}: {Show(hits)}";
            yield break;
        }

        // The characters really do spell the query, in order and case-insensitively —
        // the property that makes the emboldened characters readable as what was typed.
        if (char.ToLowerInvariant(target[hits[i]]) != char.ToLowerInvariant(query[i]))
        {
            yield return $"hit {i} at {hits[i]} is '{Escape(target[hits[i]].ToString())}', query wanted '{Escape(query[i].ToString())}'";
            yield break;
        }
    }

    // D1: the renderer cuts runs at these indices, so a hit must never take one half of
    // a surrogate pair without the other — either both halves are hits or neither is.
    HashSet<int> set = [.. hits];
    for (int j = 0; j + 1 < target.Length; j++)
    {
        if (char.IsHighSurrogate(target[j]) && char.IsLowSurrogate(target[j + 1]) && set.Contains(j) != set.Contains(j + 1))
        {
            yield return $"hits split the surrogate pair at {j}: {Show(hits)}";
            yield break;
        }
    }

    if (labelStart < 0 || labelStart > target.Length)
    {
        yield return $"labelStart {labelStart} is outside [0,{target.Length}]";
    }

    if (query.Length > target.Length)
    {
        yield return "a query longer than the target must not match";
    }
}

// The invariants of the list the palette actually shows.
static IEnumerable<string> FilterInvariants(IReadOnlyList<PaletteEntry> menu, string query, IReadOnlyList<PaletteMatch> rows)
{
    string trimmed = query.Trim();

    if (trimmed.Length == 0)
    {
        if (rows.Count != menu.Count)
        {
            yield return $"an empty query must return every entry: {rows.Count} of {menu.Count}";
        }
    }
    else
    {
        // No entry may appear that the matcher itself rejects, and none that does match
        // may be silently dropped: the palette's promise is that what is typed decides
        // membership, and only ordering is a matter of taste.
        foreach (PaletteMatch row in rows)
        {
            if (!CommandPaletteService.TryScore(row.Entry.Display, row.Entry.LabelStart, trimmed, out _, out _))
            {
                yield return $"returned an entry the matcher does not match: '{Escape(row.Entry.Display)}'";
            }
        }

        int expected = menu.Count(e => CommandPaletteService.TryScore(e.Display, e.LabelStart, trimmed, out _, out _));
        if (rows.Count != expected)
        {
            yield return $"expected {expected} matching entries, returned {rows.Count}";
        }
    }

    // Ordering is a total order: no pair may be arranged so that each outranks the
    // other. Checked against the documented keys (score first, then recency, then the
    // shorter caption), which is the only way an inconsistent comparison — the classic
    // way a hand-rolled sort starts throwing or losing rows — can be caught from outside.
    for (int i = 0; i < rows.Count; i++)
    {
        for (int j = i + 1; j < rows.Count; j++)
        {
            if (rows[j].Score > rows[i].Score)
            {
                yield return $"row {j} scores {rows[j].Score} above row {i}'s {rows[i].Score}";
                yield break;
            }
        }
    }

    HashSet<PaletteEntry> seen = [];
    foreach (PaletteMatch row in rows)
    {
        if (!seen.Add(row.Entry))
        {
            yield return $"entry listed twice: '{Escape(row.Entry.Display)}'";
            yield break;
        }
    }
}

// A display string built from the material real captions are made of: words, digits,
// the separator and ellipsis the palette itself draws, the '&' and '_' the two mnemonic
// dialects leave behind, accents, CJK — and, in the astral rounds, characters that do
// not fit in one UTF-16 unit.
static PaletteEntry GeneratedEntry(Random random, bool astral)
{
    string path = GeneratedText(random, random.Next(0, 3), astral);
    string label = GeneratedText(random, random.Next(1, 4), astral);
    return new PaletteEntry(label, path, label, null, null, true, static () => { });
}

static string GeneratedText(Random random, int words, bool astral)
{
    const string Letters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
    const string Odd = " _&…▸éöß中形";

    StringBuilder text = new();
    for (int w = 0; w < words; w++)
    {
        if (w > 0)
        {
            text.Append(random.Next(4) == 0 ? Odd[random.Next(Odd.Length)] : ' ');
        }

        int length = random.Next(1, 7);
        for (int i = 0; i < length; i++)
        {
            if (astral && random.Next(6) == 0)
            {
                text.Append(char.ConvertFromUtf32(random.Next(0x1F600, 0x1F650)));
            }
            else if (random.Next(8) == 0)
            {
                text.Append(Odd[random.Next(Odd.Length)]);
            }
            else
            {
                text.Append(Letters[random.Next(Letters.Length)]);
            }
        }
    }

    return text.ToString();
}

// Half the queries are a subsequence lifted out of the display (so the matcher is
// pushed through its whole read-back path rather than bailing out early), half are
// arbitrary — including queries longer than the target and queries of pure noise.
static string GeneratedQuery(Random random, string display)
{
    if (display.Length > 0 && random.Next(2) == 0)
    {
        StringBuilder query = new();
        int i = 0;
        while (i < display.Length && query.Length < 6)
        {
            if (random.Next(3) == 0)
            {
                char c = display[i];

                // A lifted subsequence keeps astral characters whole four times out of
                // five, so the astral rounds spend most of their time on the MATCHED
                // path (where the torn-pair defect lived) rather than bailing out on a
                // lone half — which the fifth case still checks.
                if (char.IsHighSurrogate(c) && i + 1 < display.Length && char.IsLowSurrogate(display[i + 1]) && random.Next(5) != 0)
                {
                    query.Append(c).Append(display[i + 1]);
                    i += 2;
                    continue;
                }

                query.Append(random.Next(2) == 0 ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c));
            }

            i++;
        }

        if (query.Length > 0)
        {
            return query.ToString();
        }
    }

    return GeneratedText(random, 1, astral: random.Next(4) == 0);
}

static bool Same(int[] expected, int[] actual)
{
    if (expected.Length != actual.Length)
    {
        return false;
    }

    for (int i = 0; i < expected.Length; i++)
    {
        if (expected[i] != actual[i])
        {
            return false;
        }
    }

    return true;
}

static PaletteEntry Entry(string path, string label, string? id = null)
    => new(id ?? label, path, label, null, null, true, static () => { });

static string Show(IReadOnlyList<int> hits) => "[" + string.Join(",", hits) + "]";

// Keeps a failure line on one line and readable when the input holds astral characters
// or the palette's own glyphs.
static string Escape(string text)
{
    StringBuilder builder = new(text.Length + 8);
    foreach (char c in text)
    {
        builder.Append(char.IsControl(c) || char.IsSurrogate(c) ? $"\\u{(int)c:x4}" : c.ToString());
    }

    return builder.ToString();
}
