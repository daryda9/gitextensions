namespace GitExtensions.Avalonia.Services;

/// <summary>
///  A changed stretch of one line, as a character offset and length into that
///  side's string. Offsets are UTF-16 code units — what a text layout indexes by —
///  and never fall inside a surrogate pair.
/// </summary>
public readonly record struct InlineSpan(int Start, int Length)
{
    /// <summary>One past the last character of the span.</summary>
    public int End => Start + Length;
}

/// <summary>
///  What changed inside a pair of lines that the line diff already called
///  "changed": the portions of the left line that were removed or replaced, and
///  the portions of the right line that took their place. Both lists are sorted
///  and non-overlapping.
///
///  <para><see cref="Highlight"/> is the engine's own opinion on whether drawing
///  the spans helps. When the two lines share almost nothing, every second word
///  is marked, and a reader gets less from a page of boxes than from no boxes at
///  all — see the noise heuristic in <see cref="InlineDiff"/>.</para>
/// </summary>
public sealed record InlineDiffResult(
    IReadOnlyList<InlineSpan> Left, IReadOnlyList<InlineSpan> Right, bool Highlight)
{
    /// <summary>Nothing to draw: the two sides are equal, or too far apart to be worth marking.</summary>
    public static InlineDiffResult None { get; } = new([], [], false);
}

/// <summary>
///  Word-level diff <b>inside</b> a single pair of lines.
///
///  <para><b>Why this exists.</b> A line diff says "this line changed"; on a line
///  where two characters moved it makes the reader re-read both versions to find
///  them. kdiff3 and the modern web forges all colour the changed words, and that
///  is the difference between glancing at a hunk and proof-reading it.</para>
///
///  <para><b>Why in memory, and not git.</b> <c>git diff --word-diff</c> answers
///  the same question, but it is a process, and the caller is a repaint: a diff
///  pane scrolling over a few hundred changed lines would fork a few hundred
///  processes per frame. Everything here is pure string work on two strings the
///  caller already holds, with no I/O, no git and no Avalonia, so it can be
///  called from a measure/render pass or from a background pre-pass equally
///  safely.</para>
///
///  <para><b>Shape of the algorithm.</b> Tokenise both sides into words,
///  whitespace runs and single punctuation characters; strip the common leading
///  and trailing tokens, which is O(n) and on real edits removes nearly
///  everything; run an LCS over what is left and report the unmatched tokens.
///  The LCS is quadratic, so it is gated behind an explicit cell budget: past it
///  the honest answer "the middle of the line changed" is returned instantly
///  rather than freezing the UI on a 200 KB minified line.</para>
/// </summary>
public static class InlineDiff
{
    // Budget for the LCS table, in cells (left tokens x right tokens after the
    // common ends are stripped). 256x256 is far more than any human-written line
    // needs; beyond it the two residues have so little in common that the exact
    // word pairing would be noise anyway, so paying for it — including the
    // allocation, on every line of a repaint — buys nothing.
    private const int MaxLcsCells = 256 * 256;

    // Longest line the word engine will look at. Past this the input is not prose
    // or code any more, and the two token lists alone (tens of thousands of
    // entries, allocated per line) would dominate a repaint.
    private const int MaxTokenizedLength = 8 * 1024;

    /// <summary>
    ///  The changed portions of <paramref name="left"/> and <paramref name="right"/>.
    ///  Never throws: empty strings, one empty side, tabs, lone surrogates and
    ///  megabyte-long lines are all ordinary inputs here.
    /// </summary>
    public static InlineDiffResult Compare(string left, string right)
    {
        left ??= string.Empty;
        right ??= string.Empty;

        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return InlineDiffResult.None;
        }

        if (left.Length == 0 || right.Length == 0)
        {
            // A whole line appeared or vanished. There is nothing to align, and
            // marking the side that exists is exactly right.
            return new InlineDiffResult(WholeLine(left), WholeLine(right), Highlight: true);
        }

        List<InlineSpan> leftSpans;
        List<InlineSpan> rightSpans;

        if (left.Length > MaxTokenizedLength || right.Length > MaxTokenizedLength)
        {
            // A line this long is machine-written — minified JS, a base64 blob, a
            // one-line JSON document — and word boxes scattered through it would
            // be unreadable even if they were free. Locating the changed stretch
            // with a character-level trim is both what the reader can actually
            // use and O(n), where tokenising alone would cost milliseconds per
            // line in the middle of a repaint.
            Coarse(left, right, out leftSpans, out rightSpans);
            return Judge(left, leftSpans, right, rightSpans);
        }

        List<Token> leftTokens = Tokenize(left);
        List<Token> rightTokens = Tokenize(right);

        // Common ends first: this is the normal case (one word edited in a line of
        // twelve) and it is linear, so the quadratic step below usually sees a
        // handful of tokens or none at all.
        int prefix = CommonPrefix(left, leftTokens, right, rightTokens);
        int suffix = CommonSuffix(left, leftTokens, right, rightTokens, prefix);

        int leftCount = leftTokens.Count - prefix - suffix;
        int rightCount = rightTokens.Count - prefix - suffix;

        if (leftCount == 0 || rightCount == 0)
        {
            // Pure insertion or pure deletion in the middle: one side contributes
            // nothing, so there is no pairing to compute.
            leftSpans = SingleSpan(leftTokens, prefix, leftCount);
            rightSpans = SingleSpan(rightTokens, prefix, rightCount);
        }
        else if ((long)leftCount * rightCount > MaxLcsCells)
        {
            // Over budget. Report the residue as one changed run per side: it is
            // true, it is cheap, and it degrades to what a plain line diff shows.
            leftSpans = SingleSpan(leftTokens, prefix, leftCount);
            rightSpans = SingleSpan(rightTokens, prefix, rightCount);
        }
        else
        {
            Align(
                left, leftTokens, prefix, leftCount,
                right, rightTokens, prefix, rightCount,
                out leftSpans, out rightSpans);
        }

        Refine(left, leftSpans, right, rightSpans);
        return Judge(left, leftSpans, right, rightSpans);
    }

    /// <summary>
    ///  Wrap the spans in a result, deciding whether they are worth drawing.
    ///
    ///  <para>Highlighting most of both lines is visually identical to
    ///  highlighting nothing, while still costing the reader the work of deciding
    ///  which boxes matter. Both sides must be mostly changed before the flag
    ///  drops: a short line replaced by a long one is still worth marking on the
    ///  side that stayed recognisable.</para>
    /// </summary>
    private static InlineDiffResult Judge(
        string left, List<InlineSpan> leftSpans, string right, List<InlineSpan> rightSpans)
    {
        if (leftSpans.Count == 0 && rightSpans.Count == 0)
        {
            // Safety net, not an expected path: every caller of Judge has already
            // established that the two lines differ, so "nothing changed on either
            // side" is the one answer that cannot be true. Saying it with
            // Highlight=true would be a lie a renderer acts on — it would draw two
            // visibly different lines with no marks at all. If some future pairing
            // rule ever loses track of where the change is, fall back to the honest
            // coarse answer instead: the whole line, not worth boxing.
            return new InlineDiffResult(WholeLine(left), WholeLine(right), Highlight: false);
        }

        bool noisy = TotalLength(leftSpans) * 2 > left.Length
                     && TotalLength(rightSpans) * 2 > right.Length;

        return new InlineDiffResult(leftSpans, rightSpans, Highlight: !noisy);
    }

    /// <summary>
    ///  Whether a span may start or end at <paramref name="index"/> without cutting
    ///  a surrogate pair in half.
    ///
    ///  <para>Only one arrangement is unsafe: a low surrogate at
    ///  <paramref name="index"/> whose high half sits at <c>index - 1</c>, because
    ///  then the boundary runs between the two halves of one character and the
    ///  renderer is handed half of it to measure and underline. Both string ends are
    ///  safe by definition, and a lone surrogate — which the contract admits as
    ///  ordinary input — is a character in its own right, so a boundary next to one
    ///  is safe too. Testing the character <i>after</i> the boundary alone (or the
    ///  one before alone) gets this wrong in one direction or the other; every
    ///  boundary in this file goes through here so the asymmetry cannot be
    ///  re-invented at the next one.</para>
    /// </summary>
    private static bool IsCodePointBoundary(string text, int index)
        => index <= 0
           || index >= text.Length
           || !char.IsLowSurrogate(text[index])
           || !char.IsHighSurrogate(text[index - 1]);

    /// <summary>
    ///  Character-level common-affix trim: one changed stretch per side, no
    ///  tokens and no alignment. Used for lines too long to tokenise.
    /// </summary>
    private static void Coarse(
        string left, string right, out List<InlineSpan> leftSpans, out List<InlineSpan> rightSpans)
    {
        int limit = Math.Min(left.Length, right.Length);

        int head = 0;
        while (head < limit && left[head] == right[head])
        {
            head++;
        }

        // Both sides are tested at every boundary. The head sits at the same index
        // in both strings, but the strings still differ there — one may end in a
        // lone high surrogate exactly where the other carries a whole pair — so a
        // boundary is only safe when it is safe for both.
        while (head > 0 && (!IsCodePointBoundary(left, head) || !IsCodePointBoundary(right, head)))
        {
            head--;
        }

        int tail = 0;
        while (tail < limit - head && left[left.Length - 1 - tail] == right[right.Length - 1 - tail])
        {
            tail++;
        }

        while (tail > 0
               && (!IsCodePointBoundary(left, left.Length - tail)
                   || !IsCodePointBoundary(right, right.Length - tail)))
        {
            tail--;
        }

        leftSpans = Middle(head, left.Length - tail);
        rightSpans = Middle(head, right.Length - tail);
    }

    private static List<InlineSpan> Middle(int start, int end)
        => end > start ? [new InlineSpan(start, end - start)] : [];

    /// <summary>
    ///  Shave characters the two changed regions still share off their ends.
    ///
    ///  <para>Word tokens are the right unit to <i>align</i> on, but they overshoot
    ///  when a word merely grew: <c>foo</c> against <c>foobar</c> aligns as one
    ///  token replaced by another, and marking both whole words tells the reader
    ///  the word changed when what changed is <c>bar</c>. Only the single-region
    ///  case is refined — with several regions there is no reliable pairing
    ///  between them, and guessing one would move a highlight onto text that did
    ///  not change, which is worse than a highlight that is merely wide.</para>
    ///
    ///  <para><b>Why the alignment test below.</b> Shaving a shared head off both
    ///  spans hands that text back to the "unchanged" part of each line, and that is
    ///  only honest when the two spans describe the <i>same place</i> in the two
    ///  lines. A token that merely moved breaks the premise: the two spans then hold
    ///  the same characters at different offsets, the shave eats both of them whole,
    ///  and a rotated line comes back reported as unchanged. The premise spelled out
    ///  is "everything outside the two spans is already identical, and in the same
    ///  order", so it is tested instead of assumed. Testing it is a pair of ordinal
    ///  comparisons — vectorised and linear, on strings the tokenizer has already
    ///  capped — where the alternative, a real character-level diff of the residue,
    ///  is quadratic and would only buy precision on the inputs this test rejects,
    ///  which are exactly the ones where the wide word-level span is the truthful
    ///  answer.</para>
    /// </summary>
    private static void Refine(string left, List<InlineSpan> leftSpans, string right, List<InlineSpan> rightSpans)
    {
        if (leftSpans.Count != 1 || rightSpans.Count != 1)
        {
            return;
        }

        InlineSpan a = leftSpans[0];
        InlineSpan b = rightSpans[0];

        if (!left.AsSpan(0, a.Start).SequenceEqual(right.AsSpan(0, b.Start))
            || !left.AsSpan(a.End).SequenceEqual(right.AsSpan(b.End)))
        {
            // The two spans are not the same hole in the same text: whatever moved,
            // moved across them. Keep the word-level spans, which do cover every
            // character that differs, and stay silent about which characters inside
            // them are "the" change.
            return;
        }

        int limit = Math.Min(a.Length, b.Length);
        int head = 0;
        while (head < limit && left[a.Start + head] == right[b.Start + head])
        {
            head++;
        }

        // Never stop between the halves of a surrogate pair: the caller measures
        // and underlines these offsets. Backing off one code unit at a time is
        // enough — the step lands on the high half, which is a boundary — and the
        // span ends themselves are code-point aligned, so the walk terminates.
        while (head > 0
               && (!IsCodePointBoundary(left, a.Start + head) || !IsCodePointBoundary(right, b.Start + head)))
        {
            head--;
        }

        int tail = 0;
        while (tail < limit - head
               && left[a.Start + a.Length - 1 - tail] == right[b.Start + b.Length - 1 - tail])
        {
            tail++;
        }

        while (tail > 0
               && (!IsCodePointBoundary(left, a.End - tail) || !IsCodePointBoundary(right, b.End - tail)))
        {
            tail--;
        }

        Trim(leftSpans, a, head, tail);
        Trim(rightSpans, b, head, tail);
    }

    private static void Trim(List<InlineSpan> spans, InlineSpan span, int head, int tail)
    {
        int length = span.Length - head - tail;
        if (length <= 0)
        {
            spans.Clear();
        }
        else
        {
            spans[0] = new InlineSpan(span.Start + head, length);
        }
    }

    private static IReadOnlyList<InlineSpan> WholeLine(string text)
        => text.Length == 0 ? [] : [new InlineSpan(0, text.Length)];

    /// <summary>
    ///  The residue between the common prefix and the common suffix, as at most
    ///  one span — the tokens are contiguous by construction, so a run of them is
    ///  a single stretch of characters.
    /// </summary>
    private static List<InlineSpan> SingleSpan(List<Token> tokens, int from, int count)
    {
        if (count <= 0)
        {
            return [];
        }

        int start = tokens[from].Start;
        Token last = tokens[from + count - 1];
        return [new InlineSpan(start, last.Start + last.Length - start)];
    }

    private static int TotalLength(List<InlineSpan> spans)
    {
        int total = 0;
        foreach (InlineSpan span in spans)
        {
            total += span.Length;
        }

        return total;
    }

    /// <summary>How many tokens at the head of both lines are equal.</summary>
    private static int CommonPrefix(string left, List<Token> leftTokens, string right, List<Token> rightTokens)
    {
        int limit = Math.Min(leftTokens.Count, rightTokens.Count);
        int count = 0;
        while (count < limit && Equal(left, leftTokens[count], right, rightTokens[count]))
        {
            count++;
        }

        return count;
    }

    /// <summary>
    ///  How many tokens at the tail of both lines are equal, without ever running
    ///  back into the prefix already claimed.
    /// </summary>
    private static int CommonSuffix(
        string left, List<Token> leftTokens, string right, List<Token> rightTokens, int prefix)
    {
        int limit = Math.Min(leftTokens.Count, rightTokens.Count) - prefix;
        int count = 0;
        while (count < limit
               && Equal(
                   left, leftTokens[leftTokens.Count - 1 - count],
                   right, rightTokens[rightTokens.Count - 1 - count]))
        {
            count++;
        }

        return count;
    }

    private static bool Equal(string left, Token a, string right, Token b)
        => a.Length == b.Length
           && left.AsSpan(a.Start, a.Length).SequenceEqual(right.AsSpan(b.Start, b.Length));

    /// <summary>
    ///  Classic LCS over the residual tokens, then a forward sweep turning every
    ///  unmatched token into a span. Consecutive unmatched tokens merge on their
    ///  own: tokenisation covers every character, so neighbours touch, and two
    ///  touching boxes would only look like a rendering bug.
    /// </summary>
    private static void Align(
        string left, List<Token> leftTokens, int leftFrom, int leftCount,
        string right, List<Token> rightTokens, int rightFrom, int rightCount,
        out List<InlineSpan> leftSpans, out List<InlineSpan> rightSpans)
    {
        int width = rightCount + 1;
        int[] lcs = new int[(leftCount + 1) * width];

        for (int i = leftCount - 1; i >= 0; i--)
        {
            Token a = leftTokens[leftFrom + i];
            for (int j = rightCount - 1; j >= 0; j--)
            {
                lcs[(i * width) + j] = Equal(left, a, right, rightTokens[rightFrom + j])
                    ? lcs[((i + 1) * width) + j + 1] + 1
                    : Math.Max(lcs[((i + 1) * width) + j], lcs[(i * width) + j + 1]);
            }
        }

        bool[] leftMatched = new bool[leftCount];
        bool[] rightMatched = new bool[rightCount];

        for (int i = 0, j = 0; i < leftCount && j < rightCount;)
        {
            if (Equal(left, leftTokens[leftFrom + i], right, rightTokens[rightFrom + j]))
            {
                leftMatched[i] = true;
                rightMatched[j] = true;
                i++;
                j++;
            }
            else if (lcs[((i + 1) * width) + j] >= lcs[(i * width) + j + 1])
            {
                i++;
            }
            else
            {
                j++;
            }
        }

        leftSpans = Collect(leftTokens, leftFrom, leftMatched);
        rightSpans = Collect(rightTokens, rightFrom, rightMatched);
    }

    private static List<InlineSpan> Collect(List<Token> tokens, int from, bool[] matched)
    {
        List<InlineSpan> spans = [];
        int runStart = -1;
        int runEnd = 0;

        for (int i = 0; i < matched.Length; i++)
        {
            Token token = tokens[from + i];
            if (!matched[i])
            {
                if (runStart < 0)
                {
                    runStart = token.Start;
                }

                runEnd = token.Start + token.Length;
            }
            else if (runStart >= 0)
            {
                spans.Add(new InlineSpan(runStart, runEnd - runStart));
                runStart = -1;
            }
        }

        if (runStart >= 0)
        {
            spans.Add(new InlineSpan(runStart, runEnd - runStart));
        }

        return spans;
    }

    /// <summary>A slice of the source line: the unit the diff aligns on.</summary>
    private readonly record struct Token(int Start, int Length);

    /// <summary>
    ///  Split a line into words (letters, digits and underscore — <see
    ///  cref="char.IsLetterOrDigit(string, int)"/> so accented Latin and CJK are
    ///  words too, not a string of single characters), runs of spaces and tabs,
    ///  and one token per remaining character.
    ///
    ///  <para>Words rather than characters because source code reads that way:
    ///  <c>foo</c> becoming <c>foobar</c> should mark a word, not scatter boxes
    ///  over the letters two edits happened to share. Punctuation stays single so
    ///  that <c>)]</c> versus <c>)</c> is one character wide.</para>
    /// </summary>
    private static List<Token> Tokenize(string text)
    {
        List<Token> tokens = [];
        int index = 0;

        while (index < text.Length)
        {
            char c = text[index];
            int start = index;

            if (c is ' ' or '\t')
            {
                while (index < text.Length && text[index] is ' ' or '\t')
                {
                    index++;
                }
            }
            else if (IsWordAt(text, index, out int width))
            {
                index += width;
                while (index < text.Length && IsWordAt(text, index, out int next))
                {
                    index += next;
                }
            }
            else
            {
                // Advance by a whole code point: splitting a surrogate pair would
                // hand the renderer half a character to underline.
                index += width;
            }

            tokens.Add(new Token(start, index - start));
        }

        return tokens;
    }

    /// <summary>
    ///  Whether the code point at <paramref name="index"/> belongs to a word, and
    ///  how many UTF-16 units it occupies. <paramref name="width"/> is filled in
    ///  either way, so callers can use it to step over non-word code points too;
    ///  an unpaired surrogate counts as one unit and as punctuation, which keeps
    ///  malformed text from throwing.
    /// </summary>
    private static bool IsWordAt(string text, int index, out int width)
    {
        char c = text[index];
        if (char.IsHighSurrogate(c) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
        {
            width = 2;
            return char.IsLetterOrDigit(text, index);
        }

        width = 1;
        return c == '_' || char.IsLetterOrDigit(c);
    }
}
