using System.Text.Json;

namespace GitExtensions.Avalonia.Services;

/// <summary>
///  Persisted UI layout/preferences for the Avalonia port: window size, the
///  three splitter panel sizes, and the chosen light/dark theme.
///
///  <para>Unlike <see cref="RecentRepositoriesService"/> (which reuses the core
///  MRU store), this state is purely presentation-layer for the Linux app, so
///  it lives in its own small JSON file under the user's config directory:
///  <c>$XDG_CONFIG_HOME</c> (or <see cref="Environment.SpecialFolder.ApplicationData"/>,
///  or <c>~/.config</c>) → <c>GitExtensions.Avalonia/ui-state.json</c>.</para>
/// </summary>
public sealed class UiState
{
    /// <summary>Restored window width in device-independent pixels.</summary>
    public double WindowWidth { get; set; } = 1280;

    /// <summary>Restored window height in device-independent pixels.</summary>
    public double WindowHeight { get; set; } = 820;

    /// <summary>Left repository-tree column width (pixels).</summary>
    public double TreeWidth { get; set; } = 260;

    /// <summary>Right area: revision-grid row star weight.</summary>
    public double RevisionsStar { get; set; } = 3;

    /// <summary>Right area: bottom detail-panel row star weight.</summary>
    public double BottomStar { get; set; } = 2;

    /// <summary>Commit-info: detail row star weight (top of the info/diff split).</summary>
    public double DetailStar { get; set; } = 2;

    /// <summary>Commit-info: diff row star weight (bottom of the info/diff split).</summary>
    public double DiffStar { get; set; } = 3;

    /// <summary>"Light" or "Dark".</summary>
    public string Theme { get; set; } = "Dark";
}

/// <summary>Reads/writes <see cref="UiState"/> to a JSON file, tolerating a
/// missing or corrupt file by returning defaults.</summary>
public sealed class UiStateService
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _path;

    public UiStateService() => _path = ResolvePath();

    /// <summary>The resolved JSON file path (for diagnostics/tests).</summary>
    public string FilePath => _path;

    /// <summary>Loads persisted state; returns defaults if absent or unreadable.</summary>
    public UiState Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                string json = File.ReadAllText(_path);
                UiState? state = JsonSerializer.Deserialize<UiState>(json, Options);
                if (state is not null)
                {
                    return Sanitize(state);
                }
            }
        }
        catch
        {
            // Missing/corrupt/unreadable → fall back to defaults below.
        }

        return new UiState();
    }

    /// <summary>Writes the given state; best-effort (never throws).</summary>
    public void Save(UiState state)
    {
        try
        {
            string? dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(_path, JsonSerializer.Serialize(Sanitize(state), Options));
        }
        catch
        {
            // Persistence is best-effort; a failure must not crash the app.
        }
    }

    // Clamp values so a corrupt/zero entry can never collapse a panel or window.
    private static UiState Sanitize(UiState s)
    {
        s.WindowWidth = Clamp(s.WindowWidth, 400, 100000, 1280);
        s.WindowHeight = Clamp(s.WindowHeight, 300, 100000, 820);
        s.TreeWidth = Clamp(s.TreeWidth, 80, 100000, 260);
        s.RevisionsStar = Clamp(s.RevisionsStar, 0.1, 1000, 3);
        s.BottomStar = Clamp(s.BottomStar, 0.1, 1000, 2);
        s.DetailStar = Clamp(s.DetailStar, 0.1, 1000, 2);
        s.DiffStar = Clamp(s.DiffStar, 0.1, 1000, 3);
        s.Theme = s.Theme == "Light" ? "Light" : "Dark";
        return s;
    }

    private static double Clamp(double v, double min, double max, double fallback)
    {
        if (double.IsNaN(v) || double.IsInfinity(v) || v < min || v > max)
        {
            return fallback;
        }

        return v;
    }

    private static string ResolvePath()
    {
        string? baseDir = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(baseDir))
        {
            baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        }

        if (string.IsNullOrWhiteSpace(baseDir))
        {
            baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config");
        }

        return Path.Combine(baseDir, "GitExtensions.Avalonia", "ui-state.json");
    }
}
