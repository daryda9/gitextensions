namespace GitExtensions.Avalonia.Services;

/// <summary>What a highlighted span of a source line is.</summary>
public enum SyntaxTokenKind
{
    /// <summary>A language keyword.</summary>
    Keyword,

    /// <summary>A string or character literal.</summary>
    String,

    /// <summary>A comment (line or block).</summary>
    Comment,

    /// <summary>A numeric literal.</summary>
    Number,

    /// <summary>A preprocessor / pragma line.</summary>
    Preprocessor,
}

/// <summary>A highlighted span inside one line: <paramref name="Start"/> is an index into that line.</summary>
public readonly record struct SyntaxSpan(int Start, int Length, SyntaxTokenKind Kind);

/// <summary>
///  The little bit of state that has to survive from one line to the next: a
///  block comment opened on an earlier line.
/// </summary>
public sealed class SyntaxState
{
    /// <summary>Whether the scanner is inside a <c>/* … */</c>-style comment.</summary>
    public bool InBlockComment { get; set; }

    /// <summary>Forgets everything (a new file, or a re-render).</summary>
    public void Reset() => InBlockComment = false;
}

/// <summary>
///  A language the highlighter knows: its keywords and its comment/string syntax.
/// </summary>
public sealed class SyntaxLanguage
{
    internal SyntaxLanguage(
        string name,
        IReadOnlySet<string> keywords,
        string[] lineComments,
        string? blockStart,
        string? blockEnd,
        char[] quotes,
        bool hashPreprocessor)
    {
        Name = name;
        Keywords = keywords;
        LineComments = lineComments;
        BlockStart = blockStart;
        BlockEnd = blockEnd;
        Quotes = quotes;
        HashPreprocessor = hashPreprocessor;
    }

    /// <summary>Display name (only used for diagnostics/tooltips).</summary>
    public string Name { get; }

    internal IReadOnlySet<string> Keywords { get; }

    internal string[] LineComments { get; }

    internal string? BlockStart { get; }

    internal string? BlockEnd { get; }

    internal char[] Quotes { get; }

    internal bool HashPreprocessor { get; }
}

/// <summary>
///  The port's stand-in for the upstream <c>ShowSyntaxHighlightingInDiff</c>
///  option: a deliberately small, single-pass, line-at-a-time scanner that marks
///  keywords, strings, comments and numbers in the content of a diff line.
///
///  <para>It is not a parser and does not try to be: a diff shows fragments of a
///  file, so any highlighter working on it is approximate by construction (the
///  original has the same limitation — it re-uses the editor's highlighting on a
///  patch). What matters here is that it costs one linear scan per rendered line
///  and allocates nothing per character, because the caller applies it to every
///  visible line of the patch.</para>
///
///  <para>The caller is responsible for the size limit: the diff view only turns
///  this on for patches below its inline-highlighting line cap.</para>
/// </summary>
public static class DiffSyntaxHighlighter
{
    private static readonly string[] NoLineComments = [];
    private static readonly char[] CQuotes = ['"', '\''];
    private static readonly char[] ScriptQuotes = ['"', '\'', '`'];

    private static readonly HashSet<string> CSharpKeywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "async", "await", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
        "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else", "enum", "event",
        "explicit", "extern", "false", "finally", "fixed", "float", "for", "foreach", "goto", "if", "implicit",
        "in", "init", "int", "interface", "internal", "is", "lock", "long", "namespace", "new", "null", "object",
        "operator", "out", "override", "params", "private", "protected", "public", "readonly", "record", "ref",
        "return", "sbyte", "sealed", "short", "sizeof", "stackalloc", "static", "string", "struct", "switch",
        "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using",
        "var", "virtual", "void", "volatile", "when", "where", "while", "yield",
    };

    private static readonly HashSet<string> CFamilyKeywords = new(StringComparer.Ordinal)
    {
        "auto", "bool", "break", "case", "catch", "char", "class", "const", "constexpr", "continue", "default",
        "delete", "do", "double", "else", "enum", "explicit", "export", "extern", "false", "final", "float",
        "for", "friend", "goto", "if", "inline", "int", "long", "mutable", "namespace", "new", "noexcept",
        "nullptr", "operator", "override", "private", "protected", "public", "register", "return", "short",
        "signed", "sizeof", "static", "struct", "switch", "template", "this", "throw", "true", "try", "typedef",
        "typename", "union", "unsigned", "using", "virtual", "void", "volatile", "while",
    };

    private static readonly HashSet<string> JavaScriptKeywords = new(StringComparer.Ordinal)
    {
        "as", "async", "await", "break", "case", "catch", "class", "const", "continue", "debugger", "default",
        "delete", "do", "else", "export", "extends", "false", "finally", "for", "from", "function", "get", "if",
        "implements", "import", "in", "instanceof", "interface", "let", "new", "null", "of", "private",
        "protected", "public", "readonly", "return", "set", "static", "super", "switch", "this", "throw",
        "true", "try", "type", "typeof", "undefined", "var", "void", "while", "yield",
    };

    private static readonly HashSet<string> PythonKeywords = new(StringComparer.Ordinal)
    {
        "and", "as", "assert", "async", "await", "break", "class", "continue", "def", "del", "elif", "else",
        "except", "False", "finally", "for", "from", "global", "if", "import", "in", "is", "lambda", "None",
        "nonlocal", "not", "or", "pass", "raise", "return", "self", "True", "try", "while", "with", "yield",
    };

    private static readonly HashSet<string> ShellKeywords = new(StringComparer.Ordinal)
    {
        "case", "do", "done", "elif", "else", "esac", "export", "fi", "for", "function", "if", "in", "local",
        "readonly", "return", "then", "until", "while",
    };

    private static readonly HashSet<string> GoKeywords = new(StringComparer.Ordinal)
    {
        "break", "case", "chan", "const", "continue", "default", "defer", "else", "fallthrough", "for", "func",
        "go", "goto", "if", "import", "interface", "map", "package", "range", "return", "select", "struct",
        "switch", "type", "var",
    };

    private static readonly HashSet<string> RustKeywords = new(StringComparer.Ordinal)
    {
        "as", "async", "await", "break", "const", "continue", "crate", "dyn", "else", "enum", "extern", "false",
        "fn", "for", "if", "impl", "in", "let", "loop", "match", "mod", "move", "mut", "pub", "ref", "return",
        "self", "static", "struct", "super", "trait", "true", "type", "unsafe", "use", "where", "while",
    };

    private static readonly HashSet<string> SqlKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "alter", "and", "as", "asc", "between", "by", "case", "create", "delete", "desc", "distinct", "drop",
        "else", "end", "exists", "from", "group", "having", "in", "index", "inner", "insert", "into", "join",
        "left", "like", "not", "null", "on", "or", "order", "outer", "primary", "right", "select", "set",
        "table", "then", "union", "update", "values", "view", "when", "where",
    };

    private static readonly HashSet<string> Empty = new(StringComparer.Ordinal);

    private static readonly SyntaxLanguage CSharp =
        new("C#", CSharpKeywords, ["//"], "/*", "*/", CQuotes, hashPreprocessor: true);

    private static readonly SyntaxLanguage CFamily =
        new("C/C++", CFamilyKeywords, ["//"], "/*", "*/", CQuotes, hashPreprocessor: true);

    private static readonly SyntaxLanguage JavaScript =
        new("JavaScript", JavaScriptKeywords, ["//"], "/*", "*/", ScriptQuotes, hashPreprocessor: false);

    private static readonly SyntaxLanguage Python =
        new("Python", PythonKeywords, ["#"], null, null, ScriptQuotes, hashPreprocessor: false);

    private static readonly SyntaxLanguage Shell =
        new("Shell", ShellKeywords, ["#"], null, null, ScriptQuotes, hashPreprocessor: false);

    private static readonly SyntaxLanguage Go =
        new("Go", GoKeywords, ["//"], "/*", "*/", ScriptQuotes, hashPreprocessor: false);

    private static readonly SyntaxLanguage Rust =
        new("Rust", RustKeywords, ["//"], "/*", "*/", CQuotes, hashPreprocessor: false);

    private static readonly SyntaxLanguage Sql =
        new("SQL", SqlKeywords, ["--"], "/*", "*/", CQuotes, hashPreprocessor: false);

    private static readonly SyntaxLanguage Markup =
        new("Markup", Empty, NoLineComments, "<!--", "-->", CQuotes, hashPreprocessor: false);

    private static readonly SyntaxLanguage Config =
        new("Config", Empty, ["#"], null, null, ScriptQuotes, hashPreprocessor: false);

    private static readonly SyntaxLanguage Json =
        new("JSON", Empty, NoLineComments, null, null, ['"'], hashPreprocessor: false);

    /// <summary>
    ///  The language for <paramref name="path"/>, or <see langword="null"/> when
    ///  the extension is not one the scanner knows (a plain text file, an image,
    ///  a diff of a binary): the caller then leaves the patch uncoloured rather
    ///  than guessing.
    /// </summary>
    public static SyntaxLanguage? Detect(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        int slash = path.LastIndexOf('/');
        int dot = path.LastIndexOf('.');
        string ext = dot > slash + 1 ? path[(dot + 1)..].ToLowerInvariant() : string.Empty;

        return ext switch
        {
            "cs" or "csx" => CSharp,
            "c" or "h" or "cc" or "cpp" or "cxx" or "hpp" or "hxx" or "m" or "mm" or "java" or "kt" or "kts"
                or "scala" or "swift" => CFamily,
            "js" or "jsx" or "mjs" or "cjs" or "ts" or "tsx" or "php" => JavaScript,
            "py" or "pyi" => Python,
            "sh" or "bash" or "zsh" or "fish" => Shell,
            "go" => Go,
            "rs" => Rust,
            "sql" => Sql,
            "xml" or "html" or "htm" or "xaml" or "axaml" or "csproj" or "props" or "targets" or "svg"
                or "resx" or "xlf" => Markup,
            "yml" or "yaml" or "toml" or "ini" or "cfg" or "conf" or "gitignore" or "dockerfile" or "editorconfig"
                => Config,
            "json" => Json,
            _ => null,
        };
    }

    /// <summary>
    ///  Scans <paramref name="line"/> from <paramref name="from"/> and appends the
    ///  spans it finds to <paramref name="into"/> (cleared first). Spans are
    ///  ordered and never overlap, so a renderer can walk them once.
    /// </summary>
    public static void Tokenize(
        SyntaxLanguage language,
        string line,
        int from,
        SyntaxState state,
        List<SyntaxSpan> into)
    {
        into.Clear();

        if (from >= line.Length)
        {
            return;
        }

        int i = from;

        // A block comment opened on an earlier line runs until its terminator.
        if (state.InBlockComment && language.BlockEnd is not null)
        {
            int end = line.IndexOf(language.BlockEnd, i, StringComparison.Ordinal);
            if (end < 0)
            {
                into.Add(new SyntaxSpan(i, line.Length - i, SyntaxTokenKind.Comment));
                return;
            }

            int stop = end + language.BlockEnd.Length;
            into.Add(new SyntaxSpan(i, stop - i, SyntaxTokenKind.Comment));
            state.InBlockComment = false;
            i = stop;
        }

        // A preprocessor line is coloured whole (# in C/C++/C#, once past the
        // leading whitespace).
        if (language.HashPreprocessor)
        {
            int probe = i;
            while (probe < line.Length && char.IsWhiteSpace(line[probe]))
            {
                probe++;
            }

            if (probe < line.Length && line[probe] == '#')
            {
                into.Add(new SyntaxSpan(probe, line.Length - probe, SyntaxTokenKind.Preprocessor));
                return;
            }
        }

        while (i < line.Length)
        {
            char c = line[i];

            // line comment
            bool matchedLineComment = false;
            foreach (string prefix in language.LineComments)
            {
                if (Matches(line, i, prefix))
                {
                    into.Add(new SyntaxSpan(i, line.Length - i, SyntaxTokenKind.Comment));
                    matchedLineComment = true;
                    break;
                }
            }

            if (matchedLineComment)
            {
                return;
            }

            // block comment
            if (language.BlockStart is not null && language.BlockEnd is not null &&
                Matches(line, i, language.BlockStart))
            {
                int end = line.IndexOf(language.BlockEnd, i + language.BlockStart.Length, StringComparison.Ordinal);
                if (end < 0)
                {
                    into.Add(new SyntaxSpan(i, line.Length - i, SyntaxTokenKind.Comment));
                    state.InBlockComment = true;
                    return;
                }

                int stop = end + language.BlockEnd.Length;
                into.Add(new SyntaxSpan(i, stop - i, SyntaxTokenKind.Comment));
                i = stop;
                continue;
            }

            // string / char literal, ending at the closing quote or at the end of
            // the line (a diff line is a fragment: an unterminated literal is
            // normal and must not swallow the rest of the file).
            if (Array.IndexOf(language.Quotes, c) >= 0)
            {
                int j = i + 1;
                while (j < line.Length)
                {
                    if (line[j] == '\\' && j + 1 < line.Length)
                    {
                        j += 2;
                        continue;
                    }

                    if (line[j] == c)
                    {
                        j++;
                        break;
                    }

                    j++;
                }

                into.Add(new SyntaxSpan(i, Math.Min(j, line.Length) - i, SyntaxTokenKind.String));
                i = j;
                continue;
            }

            // number
            if (char.IsAsciiDigit(c) && (i == from || !IsWordChar(line[i - 1])))
            {
                int j = i;
                while (j < line.Length && (char.IsAsciiLetterOrDigit(line[j]) || line[j] == '.' || line[j] == '_'))
                {
                    j++;
                }

                into.Add(new SyntaxSpan(i, j - i, SyntaxTokenKind.Number));
                i = j;
                continue;
            }

            // identifier / keyword
            if (IsWordStart(c))
            {
                int j = i;
                while (j < line.Length && IsWordChar(line[j]))
                {
                    j++;
                }

                if (language.Keywords.Count > 0 && language.Keywords.Contains(line[i..j]))
                {
                    into.Add(new SyntaxSpan(i, j - i, SyntaxTokenKind.Keyword));
                }

                i = j;
                continue;
            }

            i++;
        }
    }

    private static bool Matches(string line, int at, string text) =>
        at + text.Length <= line.Length &&
        string.CompareOrdinal(line, at, text, 0, text.Length) == 0;

    private static bool IsWordStart(char c) => char.IsLetter(c) || c == '_' || c == '@' || c == '$';

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$';
}
