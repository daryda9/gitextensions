using System.Collections.Frozen;
using System.Text;
using GitExtensions.Extensibility.Translations;
using GitExtensions.Extensibility.Translations.Xliff;

namespace GitExtensions.Avalonia.Services;

/// <summary>
///  Translation layer for the Avalonia port.
///
///  <para><b>Why not <see cref="Translator.Translate"/> / <c>ITranslate</c>?</b>
///  The core translation engine has two halves. The lower half — discovering
///  <c>Translation/*.xlf</c> next to the assembly and deserializing them into
///  <see cref="TranslationFile"/> — is plain .NET and works fine on Linux, so we
///  reuse it verbatim (<see cref="Translator.GetTranslation"/>,
///  <see cref="Translator.GetAllTranslations"/>). The upper half —
///  <c>ITranslate.TranslateItems</c> as implemented by <c>TranslationUtil</c> —
///  walks a <c>System.Windows.Forms.Control</c> tree by reflection and matches
///  each control's <em>designer field name</em> (<c>commitToolStripMenuItem</c>)
///  against the XLIFF ids. Avalonia controls are not WinForms controls and the
///  port's views have no designer fields at all: every caption is an inline
///  literal. That half is therefore unusable here, and re-creating it would mean
///  rewriting every view to own named, discoverable widgets.</para>
///
///  <para>So this service keeps the core loader and replaces only the matcher.
///  It builds two indexes out of the loaded <see cref="TranslationFile"/>s:</para>
///  <list type="bullet">
///   <item><description>by <b>id</b> — <c>"FormBrowse/commitToolStripMenuItem.Text"</c>,
///     the precise, unambiguous form, used by <see cref="T(string, string)"/>;</description></item>
///   <item><description>by <b>English source text</b> — the <c>&lt;source&gt;</c> of every
///     trans-unit, normalized (accelerators and ellipsis style removed, case-folded),
///     used by <see cref="T(string)"/>. This is what makes the layer callable from
///     views full of inline literals without touching their architecture.</description></item>
///  </list>
///
///  <para>Both lookups fall back to the English text the caller passed, so an
///  untranslated string simply stays English and nothing can ever throw or blank
///  out a caption.</para>
///
///  <para><b>Threading.</b> Loading parses up to ~1 MB of XML with
///  <see cref="System.Xml.Serialization.XmlSerializer"/>; it must never run on the
///  UI thread. Use <see cref="LoadAsync"/> (which does the work in
///  <see cref="Task.Run(Func{Task})"/>) and re-label the UI in its continuation.
///  <see cref="T(string)"/> itself is a dictionary hit and is safe anywhere.</para>
/// </summary>
public static class TranslationService
{
    /// <summary>The pseudo-language meaning "no translation, use the literals".</summary>
    public const string EnglishLanguage = "English";

    // Categories consulted first when the same English source text appears in
    // several XLIFF <file> sections with different targets. FormBrowse is the
    // shell the port's main window is modelled on, so its wording wins.
    private static readonly string[] PreferredCategories =
    [
        "FormBrowse",
        "TranslationString",
        "Strings",
        "FormCommit",
        "FormPush",
        "FormPull",
        "RepoObjectsTree",
        "RevisionGrid",
    ];

    private static Catalog _catalog = Catalog.Empty;

    /// <summary>Raised (on the thread that completed the load) after the active
    /// language changed, so views can re-label themselves.</summary>
    public static event Action? LanguageChanged;

    /// <summary>The active language name, or <see cref="EnglishLanguage"/>.</summary>
    public static string CurrentLanguage => _catalog.Language;

    /// <summary>True when a real (non-English) catalog is loaded.</summary>
    public static bool IsTranslated => _catalog.Count > 0;

    /// <summary>
    ///  Enumerates the languages for which an <c>.xlf</c> was found next to the
    ///  executable, English first. Touches the disk — call from a background thread.
    /// </summary>
    public static IReadOnlyList<string> AvailableLanguages()
    {
        List<string> names = [EnglishLanguage];
        try
        {
            names.AddRange(Translator.GetAllTranslations().OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
        }
        catch
        {
            // A missing/unreadable Translation directory just means "English only".
        }

        return names;
    }

    /// <summary>The directory the core engine looks in (diagnostics).</summary>
    public static string TranslationDirectory
    {
        get
        {
            try
            {
                return Translator.GetTranslationDir();
            }
            catch
            {
                return "";
            }
        }
    }

    /// <summary>
    ///  Loads <paramref name="language"/> off the UI thread and installs it as the
    ///  active catalog, then raises <see cref="LanguageChanged"/>. Passing null,
    ///  empty or <see cref="EnglishLanguage"/> clears the catalog (back to the
    ///  inline literals). Never throws.
    /// </summary>
    public static async Task LoadAsync(string? language)
    {
        string name = string.IsNullOrWhiteSpace(language) ? EnglishLanguage : language.Trim();

        Catalog catalog = name.Equals(EnglishLanguage, StringComparison.OrdinalIgnoreCase)
            ? Catalog.Empty
            : await Task.Run(() => Build(name)).ConfigureAwait(true);

        _catalog = catalog;
        LanguageChanged?.Invoke();
    }

    /// <summary>
    ///  Translates by English source text: <c>T("Fetch")</c> → <c>"Recupera"</c>.
    ///  Returns <paramref name="english"/> unchanged when there is no catalog or no
    ///  match. Accelerator style (<c>_Start</c> ↔ <c>&amp;Start</c>) and ellipsis
    ///  style (<c>…</c> ↔ <c>...</c>) of the caller's string are preserved.
    /// </summary>
    public static string T(string english) => T(key: null, english);

    /// <summary>
    ///  Translates by explicit XLIFF id first, falling back to the English source
    ///  text and finally to <paramref name="english"/> itself.
    ///  <paramref name="key"/> is <c>"&lt;Category&gt;/&lt;Item&gt;.&lt;Property&gt;"</c>,
    ///  i.e. the XLIFF <c>&lt;file original="…"&gt;</c> plus the <c>trans-unit</c> id —
    ///  for example <c>"FormBrowse/commitToolStripMenuItem.Text"</c>. This is the
    ///  preferred form for new call sites: it cannot pick up a same-worded string
    ///  from an unrelated dialog.
    /// </summary>
    public static string T(string? key, string english)
    {
        Catalog catalog = _catalog;
        if (catalog.Count == 0 || string.IsNullOrEmpty(english))
        {
            return english;
        }

        string? target = null;

        if (!string.IsNullOrEmpty(key) && catalog.ById.TryGetValue(key, out string? byId))
        {
            target = byId;
        }
        else if (catalog.BySource.TryGetValue(Normalize(english), out string? bySource))
        {
            target = bySource;
        }

        return string.IsNullOrEmpty(target) ? english : Restyle(target, english);
    }

    // ---- catalog -----------------------------------------------------------

    private sealed class Catalog
    {
        public static readonly Catalog Empty = new(EnglishLanguage,
            FrozenDictionary<string, string>.Empty, FrozenDictionary<string, string>.Empty);

        public Catalog(string language, FrozenDictionary<string, string> byId, FrozenDictionary<string, string> bySource)
        {
            Language = language;
            ById = byId;
            BySource = bySource;
        }

        public string Language { get; }
        public FrozenDictionary<string, string> ById { get; }
        public FrozenDictionary<string, string> BySource { get; }
        public int Count => ById.Count;
    }

    private static Catalog Build(string language)
    {
        try
        {
            IDictionary<string, TranslationFile> files = Translator.GetTranslation(language);

            Dictionary<string, string> byId = new(StringComparer.Ordinal);

            // source text -> (target, priority); a lower priority number wins.
            Dictionary<string, (string Target, int Rank)> bySource = new(StringComparer.Ordinal);

            foreach (TranslationFile file in files.Values)
            {
                foreach (TranslationCategory category in file.TranslationCategories)
                {
                    string categoryName = category.Name ?? "";
                    int rank = Array.IndexOf(PreferredCategories, categoryName);
                    rank = rank < 0 ? PreferredCategories.Length : rank;

                    foreach (TranslationItem item in category.Body.TranslationItems)
                    {
                        string? target = item.Value;
                        if (string.IsNullOrWhiteSpace(target))
                        {
                            continue;
                        }

                        string id = $"{categoryName}/{item.Id}";
                        byId[id] = target;

                        if (string.IsNullOrWhiteSpace(item.Source))
                        {
                            continue;
                        }

                        string source = Normalize(item.Source);
                        if (source.Length == 0)
                        {
                            continue;
                        }

                        if (!bySource.TryGetValue(source, out (string Target, int Rank) existing) || rank < existing.Rank)
                        {
                            bySource[source] = (target, rank);
                        }
                    }
                }
            }

            return new Catalog(
                language,
                byId.ToFrozenDictionary(StringComparer.Ordinal),
                bySource.ToDictionary(p => p.Key, p => p.Value.Target, StringComparer.Ordinal)
                        .ToFrozenDictionary(StringComparer.Ordinal));
        }
        catch
        {
            // A corrupt or half-written .xlf must degrade to English, not crash.
            return Catalog.Empty;
        }
    }

    // ---- string shaping ----------------------------------------------------

    /// <summary>
    ///  Folds a caption to its comparable core: accelerator markers dropped
    ///  (WinForms <c>&amp;</c>, Avalonia <c>_</c>), <c>...</c> and <c>…</c> unified,
    ///  surrounding/duplicate whitespace collapsed, case folded. This is what makes
    ///  the port's <c>"Commit…"</c> find the XLIFF's <c>"&amp;Commit..."</c>.
    /// </summary>
    internal static string Normalize(string text)
    {
        StringBuilder sb = new(text.Length);
        bool lastWasSpace = true;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            if (c == '&')
            {
                if (i + 1 < text.Length && text[i + 1] == '&')
                {
                    i++;      // "&&" is a literal ampersand
                    sb.Append('&');
                    lastWasSpace = false;
                }

                continue;     // otherwise an accelerator marker
            }

            if (c == '_' && IsAcceleratorUnderscore(text, i))
            {
                continue;
            }

            if (c == '…')
            {
                sb.Append("...");
                lastWasSpace = false;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace)
                {
                    sb.Append(' ');
                    lastWasSpace = true;
                }

                continue;
            }

            sb.Append(char.ToLowerInvariant(c));
            lastWasSpace = false;
        }

        return sb.ToString().TrimEnd();
    }

    // An underscore counts as an Avalonia accelerator only when it opens a word
    // and is followed by a letter/digit — so "Show _reflog" yes, "my_file" no.
    private static bool IsAcceleratorUnderscore(string text, int index)
    {
        if (index + 1 >= text.Length || !char.IsLetterOrDigit(text[index + 1]))
        {
            return false;
        }

        return index == 0 || char.IsWhiteSpace(text[index - 1]) || text[index - 1] == '(';
    }

    /// <summary>
    ///  Re-dresses an XLIFF target so it looks like the caller's string: the
    ///  WinForms <c>&amp;</c> accelerator becomes <c>_</c> when the caller used one
    ///  (and is dropped otherwise), and <c>...</c> becomes <c>…</c> when the caller
    ///  wrote the single-character ellipsis.
    /// </summary>
    internal static string Restyle(string target, string english)
    {
        bool wantsUnderscore = english.Contains('_') && HasAcceleratorUnderscore(english);
        bool wantsEllipsis = english.Contains('…');

        StringBuilder sb = new(target.Length + 2);
        bool acceleratorEmitted = false;

        for (int i = 0; i < target.Length; i++)
        {
            char c = target[i];

            if (c == '&')
            {
                if (i + 1 < target.Length && target[i + 1] == '&')
                {
                    i++;
                    sb.Append('&');
                    continue;
                }

                if (wantsUnderscore && !acceleratorEmitted)
                {
                    sb.Append('_');
                    acceleratorEmitted = true;
                }

                continue;
            }

            sb.Append(c);
        }

        string result = sb.ToString();

        if (wantsEllipsis && result.EndsWith("...", StringComparison.Ordinal))
        {
            result = string.Concat(result.AsSpan(0, result.Length - 3), "…");
        }
        else if (!wantsEllipsis && !english.EndsWith("...", StringComparison.Ordinal)
                 && result.EndsWith("...", StringComparison.Ordinal))
        {
            result = result[..^3];
        }

        return result;
    }

    private static bool HasAcceleratorUnderscore(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '_' && IsAcceleratorUnderscore(text, i))
            {
                return true;
            }
        }

        return false;
    }
}
