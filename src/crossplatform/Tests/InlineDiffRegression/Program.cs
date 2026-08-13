using System.Diagnostics;
using System.Text;
using GitExtensions.Avalonia.Services;

// Regression suite for InlineDiff, the intra-line (word/character) diff engine.
//
// Usage: dotnet run --project Tests/InlineDiffRegression/InlineDiffRegression.Harness.csproj
//
// Exit code 0 means every case and every invariant held; any other value means at
// least one broke, and each broken one is printed with expected against actual.
//
// Why this exists at all: InlineDiff has no compile-time contract to protect it.
// Its output is a set of offsets a renderer underlines, so a regression in the
// tokenizer, in the common-affix trim or — most easily — in the noise heuristic
// changes nothing a build can see. It shows up as one line in one hunk being
// highlighted oddly, months later, if anyone happens to look. Every expectation
// below is therefore written out by hand from the documented contract, never
// captured from whatever the engine returns today.

// This suite once carried two quarantined defects, both since fixed in
// App/Services/InlineDiff.cs; the four cases that pinned their wrong answers are now
// ordinary cases further down, holding the right one. There is no amnesty mechanism
// left on purpose: every invariant violation, hand-written case or fuzz round alike,
// fails the run.
//
//  D1 was "moved text reported as unchanged": Refine() trimmed the common head and
//     tail of the two surviving spans as if they sat at the same place in both lines,
//     so a token that had merely MOVED was trimmed away on both sides and
//     Compare("+Z", "Z+") answered "no spans, Highlight=true". Refine now first
//     checks that the text outside the two spans really is identical and in the same
//     order, and declines to refine when it is not.
//
//  D2 was "span boundary inside a surrogate pair": the tail guards in Refine() and
//     Coarse() tested char.IsHighSurrogate(text[end]) where the head guards tested
//     char.IsLowSurrogate(text[head]), so the one dangerous arrangement — a low
//     surrogate at the boundary whose high half is inside the span — never fired.
//     Both ends now go through one IsCodePointBoundary helper.

List<string> failures = [];
int cases = 0;

// ---------------------------------------------------------------- edits in the small

// A single character inside a word: the word-level pass marks the whole word, the
// character refinement narrows it to the one character that moved.
Case("one character changed", "cat", "car",
    [new(2, 1)], [new(2, 1)], highlight: true);

// A word that grew keeps its old text unmarked on the left: nothing was removed
// there, so the left side must end up with no span at all.
Case("word lengthened", "foo bar", "foo barbaz",
    [], [new(7, 3)], highlight: true);

Case("word shortened", "foo barbaz", "foo bar",
    [new(7, 3)], [], highlight: true);

// Insertions: the inserted word and the whitespace that came with it are one run.
Case("insertion at head", "bar", "foo bar",
    [], [new(0, 4)], highlight: true);

Case("insertion at tail", "foo", "foo bar",
    [], [new(3, 4)], highlight: true);

Case("deletion at head", "foo bar", "bar",
    [new(0, 4)], [], highlight: true);

// Two edits far apart in one line: two spans per side, and — since more than one
// span survives — no character-level refinement, by contract.
Case("two separate word edits", "the quick brown fox", "the slow brown cat",
    [new(4, 5), new(16, 3)], [new(4, 4), new(15, 3)], highlight: true);

// ---------------------------------------------------------------- degenerate inputs

Case("identical lines", "same", "same", [], [], highlight: false);

// A line that only exists on one side: the whole of it is the change.
Case("left empty", "", "added", [], [new(0, 5)], highlight: true);
Case("right empty", "gone", "", [new(0, 4)], [], highlight: true);
Case("both empty", "", "", [], [], highlight: false);

// ---------------------------------------------------------------- the noise heuristic

// Nothing recognisable survives, so marking every other word would be a page of
// boxes: Highlight must drop. The spans themselves are deliberately not asserted —
// the contract here is the flag, and a renderer that respects it never draws them.
HighlightOnly("total rewrite drops Highlight", "alpha beta gamma", "qqq www eee", highlight: false);

// The mirror image, and the reason the heuristic requires BOTH sides to be mostly
// changed: half of a long line is untouched, so the marks still help.
HighlightOnly("mostly-equal long line keeps Highlight",
    "the invoice total is computed from the order lines",
    "the invoice total is derived from the order lines",
    highlight: true);

// ---------------------------------------------------------------- span merging

// "ab" and "." are two tokens, both unmatched, and they touch. Two adjacent boxes
// would read as a rendering bug, so the engine must emit one span, not two. The
// long common tail keeps the noise heuristic out of the way.
Case("adjacent changed tokens merge into one span", "ab.cdefghij", "xy?cdefghij",
    [new(0, 3)], [new(0, 3)], highlight: true);

// ---------------------------------------------------------------- whitespace

Case("tab is an ordinary separator", "a\tb", "a\tc",
    [new(2, 1)], [new(2, 1)], highlight: true);

// A run of whitespace is one token, so a run of two tabs replaced by one space is
// one span per side, of different lengths.
Case("whitespace run replaced", "a\t\tb", "a b",
    [new(1, 2)], [new(1, 1)], highlight: true);

Case("indentation retabbed", "    foo", "\tfoo",
    [new(0, 4)], [new(0, 1)], highlight: true);

// ---------------------------------------------------------------- non-ASCII

// Accented Latin is a word character, so "café" stays one token and the refinement
// lands on the single accented character.
Case("accented character", "café au lait", "cafè au lait",
    [new(3, 1)], [new(3, 1)], highlight: true);

// CJK has no spaces: the whole run is one token and only the character refinement
// can say anything useful. One character was dropped, so the right side is clean.
Case("CJK single-character deletion", "日本語のテキスト", "日本語のテスト",
    [new(5, 1)], [], highlight: true);

// An emoji is a surrogate pair. The refinement walks code units and would happily
// stop between the two halves — the engine must back off to the pair boundary, so
// the span is the whole pair, never one unit of it.
Case("emoji replaced keeps the surrogate pair whole", "a 😀 b", "a 😁 b",
    [new(2, 2)], [new(2, 2)], highlight: true);

Case("emoji appended", "hi 😀", "hi 😀😁",
    [], [new(5, 2)], highlight: true);

// ---------------------------------------------------------------- size and cost

// A 100 001-character line with one character changed in the middle. Past the
// tokenizer's length ceiling the engine switches to a character-level affix trim:
// still exactly one span per side, and it must land in negligible time — this runs
// inside a repaint.
{
    string filler = Filler(50_000);
    string left = filler + "alpha" + filler;
    string right = filler + "OMEGA" + filler;

    Timed("long line, one edit in the middle", left, right, budgetMs: 200, result =>
        Expect(result, [new(50_000, 5)], [new(50_000, 5)], highlight: true));
}

// Two lines short enough to tokenise but with far more tokens than the LCS budget
// allows: the engine must bail out to "the whole residue changed" instead of
// running a 600x600 alignment, and must say so instantly.
{
    string left = Words("aaa", 300);
    string right = Words("zzz", 300);

    Timed("two long, wholly different lines hit the cost ceiling", left, right, budgetMs: 100, result =>
    {
        // One run per side, and nothing worth drawing: everything changed.
        if (result.Left.Count != 1 || result.Right.Count != 1 || result.Highlight)
        {
            return $"expected one span per side and highlight=False, actual {Show(result.Left)} / {Show(result.Right)} highlight={result.Highlight}";
        }

        return null;
    });
}

// ---------------------------------------------------------------- moved text (was D1)

// The minimal rotation. "+" moved from the head of the line to its tail: the honest
// answer is that the "+" at 0 went away and a "+" appeared at 1, and the "Z" the two
// lines really do share is what stays unmarked. Refining these two spans against each
// other would trim both to nothing and report two different lines as identical.
Case("moved token is reported where it moved from and to", "+Z", "Z+",
    [new(0, 1)], [new(1, 1)], highlight: true);

// A rotation of a whole run, with accents and CJK in it so the offsets are code-unit
// offsets and not character counts. Everything but the leading "()," moved, so more
// than half of both lines is marked and the noise heuristic — correctly — says the
// boxes would not help. What must never happen is the old answer: "only the trailing
// 'x' was deleted", with Highlight on.
Case("rotated line marks the moved run and drops Highlight", "(),  naïveテストx", "  naïveテスト(),",
    [new(3, 11)], [new(0, 10)], highlight: false);

// ---------------------------------------------------------------- surrogate boundaries (was D2)

// U+1F600 and U+1FA00 differ only in their HIGH surrogate; their low halves are both
// DE00. A tail trim that walks code units therefore matches the low half and would
// stop between the halves, leaving a span over a lone high surrogate. The span must
// be the whole pair.
Case("astral characters sharing a low surrogate stay whole", "a😀b", "a🨀b",
    [new(1, 2)], [new(1, 2)], highlight: true);

// The same input past the tokenizer's length ceiling, where the character-level trim
// in Coarse() has its own copy of the boundary guard.
{
    string filler = new('a', 9000);
    Case("astral characters sharing a low surrogate stay whole on the long-line path",
        filler + "😀zz", filler + "🨀zz",
        [new(9000, 2)], [new(9000, 2)], highlight: true);
}

// The mirror arrangement: a shared HIGH surrogate with differing low halves. The head
// trim matches the high half and must back off to the start of the pair.
Case("astral characters sharing a high surrogate stay whole", "a😀b", "a😁b",
    [new(1, 2)], [new(1, 2)], highlight: true);

// ---------------------------------------------------------------- deterministic fuzz

// Cases nobody thinks to write. The seed is fixed so a failure here is reproducible
// and reviewable; only the invariants are checked, because there is no independent
// oracle for what the "right" spans are on a randomly mangled line.
{
    Stopwatch sw = Stopwatch.StartNew();
    int pairs = Fuzz(seed: 20260813, rounds: 8000, allowLoneSurrogates: false, label: "fuzz");
    pairs += Fuzz(seed: 991, rounds: 2000, allowLoneSurrogates: true, label: "fuzz/lone-surrogates");
    sw.Stop();
    Console.WriteLine($"  fuzz: {pairs} pairs, {sw.ElapsedMilliseconds} ms, 0 tolerated violations");
}

// ---------------------------------------------------------------- verdict

Console.WriteLine();
if (failures.Count == 0)
{
    Console.WriteLine($"PASS: {cases} InlineDiff cases, all invariants held");
    return 0;
}

Console.WriteLine($"FAIL: {failures.Count} of {cases} InlineDiff cases broke");
foreach (string failure in failures)
{
    Console.WriteLine("  " + failure);
}

return 1;

// ---------------------------------------------------------------- harness

// Runs one case with its expected spans spelled out, plus the invariants that hold
// for every input.
void Case(string name, string left, string right, InlineSpan[] expectedLeft, InlineSpan[] expectedRight, bool highlight)
    => Record(name, left, right, result => Expect(result, expectedLeft, expectedRight, highlight));

// For cases whose contract is the Highlight verdict rather than the exact spans.
void HighlightOnly(string name, string left, string right, bool highlight)
    => Record(name, left, right, result => result.Highlight == highlight
        ? null
        : $"expected highlight={highlight}, actual highlight={result.Highlight} with {Show(result.Left)} / {Show(result.Right)}");

// Same, with a wall-clock budget: an engine that becomes correct but quadratic is
// still a regression, because the caller is a repaint.
void Timed(string name, string left, string right, long budgetMs, Func<InlineDiffResult, string?> check)
{
    Stopwatch sw = Stopwatch.StartNew();
    InlineDiffResult result = InlineDiff.Compare(left, right);
    sw.Stop();

    Console.WriteLine($"  {name}: {sw.Elapsed.TotalMilliseconds:0.00} ms (budget {budgetMs} ms)");
    Record(name, left, right, _ => check(result), precomputed: result);

    if (sw.ElapsedMilliseconds > budgetMs)
    {
        Fail(name, $"took {sw.ElapsedMilliseconds} ms, budget is {budgetMs} ms");
    }
}

void Record(string name, string left, string right, Func<InlineDiffResult, string?> check, InlineDiffResult? precomputed = null)
{
    cases++;
    InlineDiffResult result = precomputed ?? InlineDiff.Compare(left, right);

    foreach (string violation in Invariants(left, right, result))
    {
        Fail(name, violation);
    }

    if (check(result) is string problem)
    {
        Fail(name, problem);
    }
}

void Fail(string name, string detail) => failures.Add($"{name}: {detail}");

string? Expect(InlineDiffResult result, InlineSpan[] left, InlineSpan[] right, bool highlight)
    => Same(left, result.Left) && Same(right, result.Right) && result.Highlight == highlight
        ? null
        : $"expected {Show(left)} / {Show(right)} highlight={highlight}, "
          + $"actual {Show(result.Left)} / {Show(result.Right)} highlight={result.Highlight}";

// The properties every result must have, whatever the input. These are what the
// renderer relies on: it indexes into the strings with these offsets, and it tells
// the reader that everything outside them is unchanged.
static IEnumerable<string> Invariants(string left, string right, InlineDiffResult result)
{
    foreach (string violation in SideInvariants("left", left, result.Left))
    {
        yield return violation;
    }

    foreach (string violation in SideInvariants("right", right, result.Right))
    {
        yield return violation;
    }

    // "Nothing changed anywhere" is never a true answer about two lines that differ,
    // and it is the answer a reader is least able to question: a renderer draws two
    // different lines with no marks at all and the eye is on its own. The engine
    // always knows at least that the whole line changed, so it must say that (with
    // Highlight off, if it judges the marks useless) rather than fall silent. Checked
    // whatever Highlight says, because a caller may reasonably choose to draw the
    // spans anyway.
    if (!string.Equals(left, right, StringComparison.Ordinal)
        && result.Left.Count == 0 && result.Right.Count == 0)
    {
        yield return "no spans at all on two lines that differ";
    }

    // The honesty invariant: if the engine claims these spans are worth drawing,
    // then what it left unmarked on one side must be exactly what it left unmarked
    // on the other. Anything else means a highlight is sitting on text that did not
    // change, or unchanged text is hiding a change.
    if (result.Highlight)
    {
        string outsideLeft = Outside(left, result.Left);
        string outsideRight = Outside(right, result.Right);
        if (!string.Equals(outsideLeft, outsideRight, StringComparison.Ordinal))
        {
            yield return $"unhighlighted remainders differ: '{Escape(outsideLeft)}' vs '{Escape(outsideRight)}'";
        }
    }
}

static IEnumerable<string> SideInvariants(string side, string text, IReadOnlyList<InlineSpan> spans)
{
    int previousEnd = 0;
    for (int i = 0; i < spans.Count; i++)
    {
        InlineSpan span = spans[i];

        if (span.Length <= 0)
        {
            yield return $"{side} span {i} is empty: {Show(spans)}";
            continue;
        }

        if (span.Start < 0 || span.End > text.Length)
        {
            yield return $"{side} span {i} escapes the string (length {text.Length}): {Show(spans)}";
            continue;
        }

        // Sorted and non-overlapping in one test: each span must start at or after
        // the previous one's end.
        if (span.Start < previousEnd)
        {
            yield return $"{side} span {i} overlaps or precedes its predecessor: {Show(spans)}";
        }

        previousEnd = span.End;

        // A boundary inside a surrogate pair hands the text layout half a character
        // to measure and underline.
        if (span.Start > 0 && char.IsLowSurrogate(text[span.Start]) && char.IsHighSurrogate(text[span.Start - 1]))
        {
            yield return $"{side} span {i} starts inside a surrogate pair: {Show(spans)}";
        }

        if (span.End < text.Length && char.IsLowSurrogate(text[span.End]) && char.IsHighSurrogate(text[span.End - 1]))
        {
            yield return $"{side} span {i} ends inside a surrogate pair: {Show(spans)}";
        }
    }
}

// Everything the result does NOT mark, concatenated.
static string Outside(string text, IReadOnlyList<InlineSpan> spans)
{
    StringBuilder builder = new();
    int cursor = 0;
    foreach (InlineSpan span in spans)
    {
        if (span.Start > cursor)
        {
            builder.Append(text, cursor, span.Start - cursor);
        }

        cursor = Math.Max(cursor, span.End);
    }

    if (cursor < text.Length)
    {
        builder.Append(text, cursor, text.Length - cursor);
    }

    return builder.ToString();
}

// Mutates a generated line and checks only the invariants: no oracle exists for the
// spans themselves, but a violated invariant is a bug regardless of the input.
int Fuzz(int seed, int rounds, bool allowLoneSurrogates, string label)
{
    Random random = new(seed);

    for (int round = 0; round < rounds; round++)
    {
        string source = Line(random);
        string mutated = Mutate(random, source, allowLoneSurrogates);

        cases++;
        InlineDiffResult result = InlineDiff.Compare(source, mutated);
        foreach (string violation in Invariants(source, mutated, result))
        {
            // No violation is tolerated. Both sides are escaped so that a fuzz
            // finding can be pasted straight back in as a hand-written case.
            Fail($"{label} seed={seed} round={round}", $"{violation} | left='{Escape(source)}' right='{Escape(mutated)}'");
        }
    }

    return rounds;
}

// A line built from the same material real diffs see: identifiers, punctuation,
// runs of whitespace, accents, CJK and astral-plane characters.
static string Line(Random random)
{
    string[] atoms =
    [
        "foo", "bar", "baz", "value", "x", "42", "_id", " ", "  ", "\t", "(", ")", "{", "}",
        ";", ",", ".", "=", "+", "café", "naïve", "日本語", "テスト", "😀", "😁", "🇮🇹", "a", "Z",
    ];

    StringBuilder builder = new();
    int atomCount = random.Next(0, 30);
    for (int i = 0; i < atomCount; i++)
    {
        builder.Append(atoms[random.Next(atoms.Length)]);
    }

    return builder.ToString();
}

// One to six edits, applied at code-point granularity unless the caller asked for
// the nastier variant that is free to cut a surrogate pair in half — the engine
// documents lone surrogates as ordinary input, so the invariants must survive them.
static string Mutate(Random random, string source, bool allowLoneSurrogates)
{
    string current = source;
    int edits = random.Next(1, 7);

    for (int i = 0; i < edits; i++)
    {
        int at = current.Length == 0 ? 0 : random.Next(current.Length + 1);
        if (!allowLoneSurrogates)
        {
            at = Align(current, at);
        }

        switch (random.Next(4))
        {
            case 0: // insert
                current = current[..at] + Line(random) + current[at..];
                break;

            case 1: // delete a stretch
            {
                int end = at + random.Next(1, 8);
                end = Math.Min(end, current.Length);
                if (!allowLoneSurrogates)
                {
                    end = Align(current, end);
                }

                current = current[..at] + current[end..];
                break;
            }

            case 2: // replace a stretch
            {
                int end = at + random.Next(1, 8);
                end = Math.Min(end, current.Length);
                if (!allowLoneSurrogates)
                {
                    end = Align(current, end);
                }

                current = current[..at] + Line(random) + current[end..];
                break;
            }

            default: // swap two halves around the cut, which produces the "same words,
                     // different order" shape that stresses the LCS walk
                current = current[at..] + current[..at];
                break;
        }
    }

    return current;
}

// Nudge an offset off the middle of a surrogate pair.
static int Align(string text, int index)
    => index > 0 && index < text.Length && char.IsLowSurrogate(text[index]) && char.IsHighSurrogate(text[index - 1])
        ? index - 1
        : index;

// 50 000 characters of plausible prose, so the long-line cases are not a run of one
// repeated character that any affix trim would sail through.
static string Filler(int length)
{
    const string chunk = "the quick brown fox jumps over the lazy dog; ";
    StringBuilder builder = new(length + chunk.Length);
    while (builder.Length < length)
    {
        builder.Append(chunk);
    }

    return builder.ToString(0, length);
}

static string Words(string prefix, int count)
    => string.Join(' ', Enumerable.Range(0, count).Select(i => prefix + i));

static bool Same(IReadOnlyList<InlineSpan> expected, IReadOnlyList<InlineSpan> actual)
{
    if (expected.Count != actual.Count)
    {
        return false;
    }

    for (int i = 0; i < expected.Count; i++)
    {
        if (expected[i] != actual[i])
        {
            return false;
        }
    }

    return true;
}

static string Show(IReadOnlyList<InlineSpan> spans)
    => spans.Count == 0 ? "[]" : "[" + string.Join(", ", spans.Select(s => $"({s.Start},{s.Length})")) + "]";

// Keeps a failure line on one line and readable when the input holds tabs or astral
// characters.
static string Escape(string text)
{
    StringBuilder builder = new(text.Length + 8);
    foreach (char c in text)
    {
        builder.Append(c switch
        {
            '\t' => "\\t",
            '\n' => "\\n",
            '\r' => "\\r",
            _ => char.IsControl(c) || char.IsSurrogate(c) ? $"\\u{(int)c:x4}" : c.ToString(),
        });
    }

    return builder.ToString();
}
