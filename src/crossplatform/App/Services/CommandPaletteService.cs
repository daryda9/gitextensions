namespace GitExtensions.Avalonia.Services;

/// <summary>
///  One invocable command as the command palette sees it: a leaf of the live main
///  menu, or a keyboard command that has no menu entry at all.
///
///  <para>Nothing here is a copy of anything: <see cref="Invoke"/> is the very
///  delegate the menu item or the hotkey binding runs, and <see cref="IsEnabled"/> is
///  the gating the menu already computed. That is the whole point of walking the menu
///  instead of keeping a second registry — a registry would have to be taught about
///  every new command, and would be wrong about the gating the day the rules move.</para>
/// </summary>
/// <param name="Id">
///  Stable, language-independent identity, used for the MRU only. It is the XLIFF key
///  (or the English source text) of the menu item, or the <see cref="BrowseCommand"/>
///  name — never the translated caption, which changes under the user's feet when the
///  language does and would strand the whole MRU list.
/// </param>
/// <param name="Path">The parent chain, already translated and mnemonic-free ("Repository").</param>
/// <param name="Label">The leaf caption, likewise.</param>
/// <param name="IconName">The <c>IconLoader</c> name the menu item was built with, if any.</param>
/// <param name="Gesture">The shortcut exactly as the menu prints it, if any.</param>
/// <param name="IsEnabled">
///  The <em>effective</em> enabled state: an item inside a disabled submenu is not
///  reachable from the menu either, so it is not reachable from here.
/// </param>
/// <param name="Invoke">What the menu item / hotkey does. Never called when disabled.</param>
/// <param name="IsChecked">
///  For a command that is a toggle (a View check option, a language radio), whether it
///  is on RIGHT NOW; <see langword="null"/> for the commands that are not toggles.
///  Read from the menu item's own <c>IsChecked</c> — the state is not recomputed here,
///  because the menu already holds the only copy that is kept in step with the grid.
/// </param>
/// <param name="DisabledReason">
///  Why this command is greyed, when — and ONLY when — the gating itself said so at the
///  moment it disabled the item. Null everywhere else, deliberately: a guessed reason
///  ("no repository open" next to something disabled for an unrelated cause) is worse
///  than no reason at all, because the user acts on it.
/// </param>
public sealed record PaletteEntry(
    string Id,
    string Path,
    string Label,
    string? IconName,
    string? Gesture,
    bool IsEnabled,
    Action Invoke,
    bool? IsChecked = null,
    string? DisabledReason = null)
{
    /// <summary>
    ///  What the row shows and what the matcher runs over: "Repository ▸ Manage
    ///  worktrees…". One string for both, so a highlighted character is at the same
    ///  index in the text the user is looking at.
    /// </summary>
    public string Display { get; } =
        Path.Length == 0 ? Label : Path + CommandPaletteService.PathSeparator + Label;

    /// <summary>Where <see cref="Label"/> starts inside <see cref="Display"/>; the
    /// matcher scores a hit past this point higher than one in the path.</summary>
    public int LabelStart => Display.Length - Label.Length;
}

/// <summary>One entry that survived the filter, with its score and the indices of the
/// characters the query matched (so the row can embolden exactly those).</summary>
public sealed record PaletteMatch(PaletteEntry Entry, int Score, IReadOnlyList<int> Hits);

/// <summary>
///  The palette's brain, kept out of the window so it can be reasoned about (and
///  tested) without a UI thread: subsequence matching with a score, and the
///  most-recently-used list that decides the order when the box is empty.
///
///  <para><b>Why a subsequence match and not a substring one.</b> The palette exists to
///  reach a command whose exact wording the user does not remember; "mwt" must find
///  "Repository ▸ Manage worktrees…". The scoring is what stops that generosity from
///  producing noise: a hit at a word start, a run of adjacent hits and a hit in the
///  leaf label all count for more than an incidental letter somewhere in the path.</para>
///
///  <para><b>Why dynamic programming and not a greedy scan.</b> Greedy takes the first
///  occurrence of each query character, which for "gc" over "Tools ▸ Git command log"
///  lands on the 'g' of nothing useful and, worse, highlights the wrong characters.
///  The table below is O(query × display) — a few thousand operations for the whole
///  command list per keystroke — and yields the best-scoring alignment, which is also
///  the one whose highlighting reads correctly.</para>
/// </summary>
public sealed class CommandPaletteService
{
    /// <summary>Between the parent chain and the leaf. A glyph, not "&gt;", so it cannot
    /// be confused with a character the user might be trying to type.</summary>
    public const string PathSeparator = " ▸ ";

    // Impossible-to-reach total, used as "no alignment here" in the table below.
    private const int NoScore = int.MinValue / 4;

    private readonly ViewPrefsService _prefs = new();
    private readonly List<string> _mru;

    public CommandPaletteService() => _mru = [.. _prefs.Load().CommandPaletteMru];

    /// <summary>The remembered command ids, most recent first.</summary>
    public IReadOnlyList<string> Mru => _mru;

    /// <summary>
    ///  Promotes <paramref name="id"/> to the head of the MRU and persists it.
    ///  Read-modify-write through <see cref="ViewPrefsService.Update"/>, like every
    ///  other group in that file, so a preference written by another surface meanwhile
    ///  is not reverted.
    /// </summary>
    public void Remember(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        _mru.Remove(id);
        _mru.Insert(0, id);
        if (_mru.Count > ViewPrefsService.MaxCommandPaletteMru)
        {
            _mru.RemoveRange(
                ViewPrefsService.MaxCommandPaletteMru, _mru.Count - ViewPrefsService.MaxCommandPaletteMru);
        }

        List<string> snapshot = [.. _mru];
        _prefs.Update(p =>
        {
            p.CommandPaletteMru.Clear();
            p.CommandPaletteMru.AddRange(snapshot);
        });
    }

    /// <summary>
    ///  The rows to show for <paramref name="query"/>. An empty query yields every
    ///  entry with the recently used ones floated to the top; otherwise the entries
    ///  that match, best score first, with the MRU breaking ties and a shorter target
    ///  breaking what is left.
    /// </summary>
    public IReadOnlyList<PaletteMatch> Filter(IReadOnlyList<PaletteEntry> entries, string? query)
    {
        string q = (query ?? string.Empty).Trim();
        List<PaletteMatch> rows = new(entries.Count);

        if (q.Length == 0)
        {
            foreach (PaletteEntry entry in entries)
            {
                rows.Add(new PaletteMatch(entry, 0, []));
            }

            // OrderBy is stable, so everything outside the MRU keeps menu order —
            // which is the order the user can already navigate by muscle memory.
            return [.. rows.OrderBy(r => MruRank(r.Entry.Id))];
        }

        foreach (PaletteEntry entry in entries)
        {
            if (TryScore(entry.Display, entry.LabelStart, q, out int score, out int[] hits))
            {
                rows.Add(new PaletteMatch(entry, score, hits));
            }
        }

        return
        [
            .. rows
                .OrderByDescending(r => r.Score)
                .ThenBy(r => MruRank(r.Entry.Id))
                .ThenBy(r => r.Entry.Display.Length)
        ];
    }

    private int MruRank(string id)
    {
        int index = _mru.IndexOf(id);
        return index < 0 ? int.MaxValue : index;
    }

    /// <summary>
    ///  "FocusRevisionGrid" → "Focus revision grid". Derived rather than tabulated on
    ///  purpose: the alternative is a hand-written label for each of the four dozen
    ///  <see cref="BrowseCommand"/> values, and such a table is wrong the first time
    ///  someone adds a command without remembering it exists.
    /// </summary>
    public static string Humanize(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return string.Empty;
        }

        System.Text.StringBuilder text = new(name.Length + 8);
        foreach (char c in name)
        {
            // A capital that is not the first character opens a new word — and the word
            // is lower-cased, because these read as sentences ("Go to parent"), not as
            // titles. Digits ride along with the word they follow ("Focus d0" cannot
            // occur: the enum has no digits), so no special case is needed for them.
            if (char.IsUpper(c) && text.Length > 0)
            {
                text.Append(' ').Append(char.ToLowerInvariant(c));
            }
            else
            {
                text.Append(c);
            }
        }

        return text.ToString();
    }

    /// <summary>
    ///  Best-scoring subsequence alignment of <paramref name="query"/> inside
    ///  <paramref name="target"/>, case-insensitive. False when the query is not a
    ///  subsequence at all.
    /// </summary>
    /// <param name="labelStart">Index at which the leaf label begins (see
    /// <see cref="PaletteEntry.LabelStart"/>); hits at or past it score higher.</param>
    /// <param name="score">Total score of the winning alignment.</param>
    /// <param name="hits">Indices in <paramref name="target"/> the query landed on,
    /// ascending — what the row emboldens.</param>
    public static bool TryScore(string target, int labelStart, string query, out int score, out int[] hits)
    {
        score = 0;
        hits = [];

        int n = target.Length;
        int m = query.Length;
        if (m == 0 || m > n)
        {
            return false;
        }

        // best[i, j]: the best total for query[0..i] with query[i] sitting on
        // target[j]; parent[i, j] remembers where query[i-1] sat, for the read-back.
        int[,] best = new int[m, n];
        int[,] parent = new int[m, n];
        for (int i = 0; i < m; i++)
        {
            for (int j = 0; j < n; j++)
            {
                best[i, j] = NoScore;
            }
        }

        for (int j = 0; j < n; j++)
        {
            // A pair's low half can only ever be entered from its high half (see
            // PairedLow), so an alignment may not START on one.
            if (Same(target[j], query[0]) && !PairedLow(target, j))
            {
                best[0, j] = CharScore(target, j, contiguous: false, labelStart);
                parent[0, j] = -1;
            }
        }

        for (int i = 1; i < m; i++)
        {
            // The best predecessor among the columns that are NOT adjacent to j, kept
            // as a running maximum so the whole thing stays O(m × n) instead of
            // O(m × n²). Adjacency is handled separately because only it earns the
            // contiguity bonus.
            int runningBest = NoScore;
            int runningIndex = -1;

            for (int j = 1; j < n; j++)
            {
                // A pair's high half may only be left towards its low half, which is
                // adjacent — so a paired high can never be a LOOSE predecessor, and is
                // kept out of the running maximum entirely (see PairedHigh).
                if (j >= 2 && best[i - 1, j - 2] > runningBest && !PairedHigh(target, j - 2))
                {
                    runningBest = best[i - 1, j - 2];
                    runningIndex = j - 2;
                }

                if (!Same(target[j], query[i]))
                {
                    continue;
                }

                int candidate = NoScore;
                int from = -1;

                if (best[i - 1, j - 1] > NoScore)
                {
                    candidate = best[i - 1, j - 1] + CharScore(target, j, contiguous: true, labelStart);
                    from = j - 1;
                }

                // ...and symmetrically, a paired low may only be ENTERED from j - 1, so
                // the loose transition is not offered for it.
                if (runningBest > NoScore && !PairedLow(target, j))
                {
                    int loose = runningBest + CharScore(target, j, contiguous: false, labelStart);
                    if (loose > candidate)
                    {
                        candidate = loose;
                        from = runningIndex;
                    }
                }

                if (candidate > NoScore)
                {
                    best[i, j] = candidate;
                    parent[i, j] = from;
                }
            }
        }

        int end = -1;
        int total = NoScore;
        for (int j = 0; j < n; j++)
        {
            // Ending on a pair's high half would leave its low half unmatched — the
            // torn-character case again, at the other end of the alignment.
            if (best[m - 1, j] > total && !PairedHigh(target, j))
            {
                total = best[m - 1, j];
                end = j;
            }
        }

        if (end < 0)
        {
            return false;
        }

        int[] positions = new int[m];
        for (int i = m - 1; i >= 0; i--)
        {
            positions[i] = end;
            end = parent[i, end];
        }

        // A shorter target wins a tie: with "push" matching both "Push" and
        // "Push a specific branch…", the terse one is what the user meant. Divided
        // rather than subtracted outright so the penalty cannot swamp a real hit.
        score = total - (n / 8);
        hits = positions;
        return true;
    }

    private static int CharScore(string target, int index, bool contiguous, int labelStart)
    {
        int score = 1;

        if (IsWordStart(target, index))
        {
            score += 8;
        }

        if (contiguous)
        {
            score += 6;
        }

        if (index >= labelStart)
        {
            score += 4;
        }

        return score;
    }

    // A word start is what an acronym query ("mwt") aims at: the first character, one
    // after any non-alphanumeric, or a capital opening a camel-case word.
    private static bool IsWordStart(string target, int index)
        => index == 0
           || !char.IsLetterOrDigit(target[index - 1])
           || (char.IsUpper(target[index]) && !char.IsUpper(target[index - 1]));

    private static bool Same(char a, char b) => char.ToLowerInvariant(a) == char.ToLowerInvariant(b);

    // The two halves of one astral character (an emoji in a repository path, a plugin
    // name, a branch name that reached a menu caption) are two UTF-16 units here, and
    // the table above aligns UNITS. Left alone it happily takes the high half of one
    // pair and the low half of another — each half following a pair reads as a word
    // start, which outbids the honest contiguous alignment — and the row renderer
    // (CommandPaletteWindow.Fill) cuts the caption into runs exactly at the hit indices,
    // so that answer is drawn as two broken halves of two different characters.
    //
    // The rule that prevents it is local and therefore free: a pair's low half may only
    // be reached from its high half, and its high half may only be left towards its low
    // half. Enforced at the four places an alignment can touch a boundary — where it
    // starts, where it ends, and the two kinds of transition — which together mean a
    // pair is either matched whole or not matched at all. A query holding a LONE half
    // consequently matches nothing, which is the honest answer: half a character is not
    // a character.
    private static bool PairedLow(string target, int index)
        => index > 0 && char.IsLowSurrogate(target[index]) && char.IsHighSurrogate(target[index - 1]);

    private static bool PairedHigh(string target, int index)
        => index + 1 < target.Length && char.IsHighSurrogate(target[index]) && char.IsLowSurrogate(target[index + 1]);
}
