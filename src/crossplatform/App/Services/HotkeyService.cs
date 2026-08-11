using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace GitExtensions.Avalonia.Services;

/// <summary>
///  The commands the main window can be driven by from the keyboard.
///
///  <para>The names mirror upstream <c>FormBrowse.Command</c> (see
///  <c>src/app/GitUI/CommandsDialogs/FormBrowse.cs</c>) one for one, because the
///  default gestures below are copied from
///  <c>src/app/GitUI/Hotkey/HotkeySettingsManager.cs</c>: keeping the names equal
///  means the two tables can be diffed by eye, and a future settings UI can show
///  the same command list the Windows build shows.</para>
///
///  <para>Two entries have no upstream counterpart in the <c>FormBrowse</c> scope:
///  <see cref="Refresh"/> (upstream reaches it through the revision grid /
///  toolbar) and <see cref="FindInDiff"/> (upstream <c>FileViewer.Command.Find</c>,
///  which in this port has to be reachable from the window because the diff is a
///  panel, not a focused form).</para>
///
///  <para>Commands upstream leaves unassigned (<c>Keys.None</c>: GitGui, GitGitK,
///  GoToSubmodule, GoToSuperproject, OpenCommitsWithDifftool) are simply absent.</para>
/// </summary>
public enum BrowseCommand
{
    AddNotes,
    CheckoutBranch,
    CloseRepository,
    Commit,
    CreateBranch,
    CreateTag,
    EditFile,
    FindFileInSelectedCommit,
    FocusLeftPanel,
    FocusRevisionGrid,
    FocusCommitInfo,
    FocusDiff,
    FocusFileTree,
    FocusGpgInfo,
    FocusGitConsole,
    FocusBuildServerStatus,
    FocusOutputHistoryAndToggleIfPanel,
    FocusNextTab,
    FocusPrevTab,
    FocusFilter,
    GitBash,
    GoToChild,
    GoToParent,
    ManageWorkTrees,
    MergeBranches,
    OpenAsTempFile,
    OpenAsTempFileWith,
    OpenRepo,
    OpenSettings,
    OpenWithDifftool,
    OpenWithDifftoolFirstToLocal,
    OpenWithDifftoolSelectedToLocal,
    PullOrFetch,
    Push,
    QuickFetch,
    QuickPull,
    QuickPullOrFetch,
    QuickPush,
    Rebase,
    Stash,
    StashPop,
    StashStaged,
    ToggleBetweenArtificialAndHeadCommits,
    ToggleLeftPanel,

    /// <summary>Port-specific: F5, upstream's grid/toolbar refresh.</summary>
    Refresh,

    /// <summary>Port-specific: upstream <c>FileViewer.Command.Find</c>, promoted to
    /// a window-level gesture so Ctrl+F works with the focus outside the diff.</summary>
    FindInDiff,
}

/// <summary>
///  A key + modifier combination, in the shape the hotkey table stores and the
///  key handler compares against.
///
///  <para>Its <see cref="ToString"/> / <see cref="TryParse"/> pair is the on-disk
///  format ("Ctrl+Shift+Alt+Up"), deliberately written by hand rather than taken
///  from <see cref="KeyGesture"/>: the round-trip has to be stable and
///  platform-independent, and <c>KeyGesture.ToString()</c> is neither (it renders
///  platform symbols for the modifiers).</para>
/// </summary>
public readonly record struct HotkeyGesture(Key Key, KeyModifiers Modifiers)
{
    public override string ToString()
    {
        List<string> parts = [];
        if (Modifiers.HasFlag(KeyModifiers.Control)) { parts.Add("Ctrl"); }
        if (Modifiers.HasFlag(KeyModifiers.Shift)) { parts.Add("Shift"); }
        if (Modifiers.HasFlag(KeyModifiers.Alt)) { parts.Add("Alt"); }
        if (Modifiers.HasFlag(KeyModifiers.Meta)) { parts.Add("Meta"); }
        parts.Add(Key.ToString());
        return string.Join("+", parts);
    }

    /// <summary>Parses "Ctrl+Shift+F" / "F5" / "Ctrl+OemPeriod"; false on anything else.</summary>
    public static bool TryParse(string? text, out HotkeyGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        KeyModifiers modifiers = KeyModifiers.None;
        Key? key = null;

        foreach (string raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= KeyModifiers.Control; continue;
                case "shift": modifiers |= KeyModifiers.Shift; continue;
                case "alt": modifiers |= KeyModifiers.Alt; continue;
                case "meta" or "win" or "cmd": modifiers |= KeyModifiers.Meta; continue;
            }

            if (key is not null || !Enum.TryParse(raw, ignoreCase: true, out Key parsed))
            {
                return false;
            }

            key = parsed;
        }

        if (key is null)
        {
            return false;
        }

        gesture = new HotkeyGesture(key.Value, modifiers);
        return true;
    }
}

/// <summary>
///  The main window's keyboard map: command → gesture, plus the actions the host
///  binds to those commands, plus the window-level key handler that dispatches
///  them.
///
///  <para><b>Defaults</b> come from upstream <c>HotkeySettingsManager</c>'s
///  <c>FormBrowse</c> scope, verbatim. <b>Overrides</b> are read from (and written
///  back to) <c>GitExtensions.Avalonia/hotkeys.json</c> next to
///  <c>ui-state.json</c>, as a flat <c>{"Commit": "Ctrl+Space", …}</c> map — so the
///  configuration UI this port does not have yet only needs to edit the dictionary
///  and call <see cref="Save"/>, with no change here.</para>
///
///  <para><b>Dispatch and priority.</b> The handler is installed on the window in
///  the <i>tunnelling</i> phase with <c>handledEventsToo</c>, for the reason the
///  rest of the port already learned the hard way: a bubbling handler never sees
///  the keys an inner control has swallowed (a <c>ListBox</c> eats the arrows, a
///  <c>TabControl</c> eats Ctrl+Tab), so half the map would silently do nothing.
///  Tunnelling reverses that, which means the window would instead <i>steal</i> the
///  gestures the views own — Ctrl+C in the grid, Alt+←/→ history navigation,
///  Ctrl+F/Ctrl+G/F3 in the diff. Upstream solves the same problem in
///  <c>FormBrowse.ProcessHotkey</c> by routing to the focused control first and
///  treating the gesture as global only if nobody claimed it; here the host passes
///  a <c>reserved</c> predicate (see <see cref="Install"/>) which answers "the
///  focused view owns this one", and the handler then does nothing and lets the
///  event continue down to that view.</para>
/// </summary>
public sealed class HotkeyService
{
    /// <summary>
    ///  Upstream's <c>FormBrowse</c> defaults (HotkeySettingsManager.cs:216–265),
    ///  plus the two port-specific entries documented on <see cref="BrowseCommand"/>.
    /// </summary>
    public static IReadOnlyDictionary<BrowseCommand, HotkeyGesture> Defaults { get; }
        = new Dictionary<BrowseCommand, HotkeyGesture>
        {
            [BrowseCommand.AddNotes] = new(Key.N, KeyModifiers.Control | KeyModifiers.Shift),
            [BrowseCommand.CheckoutBranch] = new(Key.OemPeriod, KeyModifiers.Control),
            [BrowseCommand.CloseRepository] = new(Key.W, KeyModifiers.Control),
            [BrowseCommand.Commit] = new(Key.Space, KeyModifiers.Control),
            [BrowseCommand.CreateBranch] = new(Key.B, KeyModifiers.Control),
            [BrowseCommand.CreateTag] = new(Key.T, KeyModifiers.Control),
            [BrowseCommand.EditFile] = new(Key.F4, KeyModifiers.None),
            [BrowseCommand.FindFileInSelectedCommit] = new(Key.F, KeyModifiers.Control | KeyModifiers.Shift),
            [BrowseCommand.FocusLeftPanel] = new(Key.D0, KeyModifiers.Control),
            [BrowseCommand.FocusRevisionGrid] = new(Key.D1, KeyModifiers.Control),
            [BrowseCommand.FocusCommitInfo] = new(Key.D2, KeyModifiers.Control),
            [BrowseCommand.FocusDiff] = new(Key.D3, KeyModifiers.Control),
            [BrowseCommand.FocusFileTree] = new(Key.D4, KeyModifiers.Control),
            [BrowseCommand.FocusGpgInfo] = new(Key.D5, KeyModifiers.Control),
            [BrowseCommand.FocusGitConsole] = new(Key.D6, KeyModifiers.Control),
            [BrowseCommand.FocusBuildServerStatus] = new(Key.D7, KeyModifiers.Control),
            [BrowseCommand.FocusOutputHistoryAndToggleIfPanel] = new(Key.D9, KeyModifiers.Control),
            [BrowseCommand.FocusNextTab] = new(Key.Tab, KeyModifiers.Control),
            [BrowseCommand.FocusPrevTab] = new(Key.Tab, KeyModifiers.Control | KeyModifiers.Shift),
            [BrowseCommand.FocusFilter] = new(Key.E, KeyModifiers.Control),
            [BrowseCommand.GitBash] = new(Key.G, KeyModifiers.Control),
            [BrowseCommand.GoToChild] = new(Key.N, KeyModifiers.Control),
            [BrowseCommand.GoToParent] = new(Key.P, KeyModifiers.Control),
            [BrowseCommand.ManageWorkTrees] = new(Key.W, KeyModifiers.Control | KeyModifiers.Alt),
            [BrowseCommand.MergeBranches] = new(Key.M, KeyModifiers.Control),
            [BrowseCommand.OpenAsTempFile] = new(Key.F3, KeyModifiers.Control),
            [BrowseCommand.OpenAsTempFileWith] = new(Key.F3, KeyModifiers.Control | KeyModifiers.Shift),
            [BrowseCommand.OpenRepo] = new(Key.O, KeyModifiers.Control),
            [BrowseCommand.OpenSettings] = new(Key.OemComma, KeyModifiers.Control),
            [BrowseCommand.OpenWithDifftool] = new(Key.F3, KeyModifiers.None),
            [BrowseCommand.OpenWithDifftoolFirstToLocal] = new(Key.F3, KeyModifiers.Alt),
            [BrowseCommand.OpenWithDifftoolSelectedToLocal] = new(Key.F3, KeyModifiers.Shift | KeyModifiers.Alt),
            [BrowseCommand.PullOrFetch] = new(Key.Down, KeyModifiers.Control),
            [BrowseCommand.Push] = new(Key.Up, KeyModifiers.Control),
            [BrowseCommand.QuickFetch] = new(Key.Down, KeyModifiers.Control | KeyModifiers.Shift),
            [BrowseCommand.QuickPull] = new(Key.P, KeyModifiers.Control | KeyModifiers.Shift),
            [BrowseCommand.QuickPullOrFetch] = new(Key.F8, KeyModifiers.None),
            [BrowseCommand.QuickPush] = new(Key.Up, KeyModifiers.Control | KeyModifiers.Shift),
            [BrowseCommand.Rebase] = new(Key.E, KeyModifiers.Control | KeyModifiers.Shift),
            [BrowseCommand.Stash] = new(Key.Up, KeyModifiers.Control | KeyModifiers.Alt),
            [BrowseCommand.StashPop] = new(Key.Down, KeyModifiers.Control | KeyModifiers.Alt),
            [BrowseCommand.StashStaged] = new(Key.Up, KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt),
            [BrowseCommand.ToggleBetweenArtificialAndHeadCommits] = new(Key.OemBackslash, KeyModifiers.Control),
            [BrowseCommand.ToggleLeftPanel] = new(Key.C, KeyModifiers.Control | KeyModifiers.Alt),
            [BrowseCommand.Refresh] = new(Key.F5, KeyModifiers.None),
            [BrowseCommand.FindInDiff] = new(Key.F, KeyModifiers.Control),
        };

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly Dictionary<BrowseCommand, HotkeyGesture> _bindings;

    // The six per-control scopes, keyed by scope then by upstream command name. Held
    // apart from _bindings because those are typed by enum and these are not: the
    // window's command list is the port's own, the scopes' are upstream's tables.
    private readonly Dictionary<HotkeyScope, Dictionary<string, HotkeyGesture>> _scoped = [];

    // gesture -> command name, per scope, rebuilt with the bindings. A key press asks
    // this and gets a command back, instead of every view comparing keys inline.
    private readonly Dictionary<HotkeyScope, Dictionary<HotkeyGesture, string>> _scopedByGesture = [];
    private readonly Dictionary<BrowseCommand, Action> _actions = [];
    private readonly Dictionary<HotkeyGesture, BrowseCommand> _byGesture = [];
    private readonly string _path;

    private Func<BrowseCommand, HotkeyGesture, bool>? _reserved;

    public HotkeyService()
    {
        _path = ResolvePath();
        _bindings = Load();
        LoadScoped();
        Reindex();
    }

    /// <summary>
    ///  The instance the whole app shares. The bindings live in ONE file and are read by
    ///  views that are built long before (and far from) the window that owns the service,
    ///  so a second instance would answer from a stale copy of the same file — which is
    ///  exactly what "my hotkey did not take effect until I restarted" looks like.
    /// </summary>
    public static HotkeyService Shared { get; } = new();

    /// <summary>
    ///  Raised after <see cref="ApplyBindings"/> changed the map, on the caller's
    ///  thread. The toolbar and the main menu render the gestures into their captions
    ///  and tooltips, so they need to know when a binding moved; the host subscribes
    ///  and re-labels.
    /// </summary>
    public event Action? Changed;

    /// <summary>The JSON overrides file (for diagnostics and the hotkeys settings page).</summary>
    public string FilePath => _path;

    /// <summary>
    ///  Replaces the bindings of the given commands — a null gesture clears the
    ///  command's binding — then re-indexes, persists and raises <see cref="Changed"/>.
    ///
    ///  <para>This is what the Settings dialog's Hotkeys page calls. It exists rather
    ///  than leaving callers to mutate <see cref="Bindings"/> because the three steps
    ///  have to happen together: a mutation without <see cref="Reindex"/> leaves the
    ///  gesture lookup pointing at the old keys, and one without
    ///  <see cref="Changed"/> leaves stale gestures printed in the menus.</para>
    /// </summary>
    public void ApplyBindings(IReadOnlyDictionary<BrowseCommand, HotkeyGesture?> bindings)
    {
        foreach ((BrowseCommand command, HotkeyGesture? gesture) in bindings)
        {
            if (gesture is { } g)
            {
                _bindings[command] = g;
            }
            else
            {
                _bindings.Remove(command);
            }
        }

        Reindex();
        Save();
        Changed?.Invoke();
    }

    /// <summary>The live command → gesture map. Mutate it, then call
    /// <see cref="Reindex"/> and <see cref="Save"/>.</summary>
    public IDictionary<BrowseCommand, HotkeyGesture> Bindings => _bindings;

    /// <summary>The gesture bound to a scoped command, or null when cleared/unknown.</summary>
    public HotkeyGesture? GestureFor(HotkeyScope scope, string command)
        => _scoped.TryGetValue(scope, out Dictionary<string, HotkeyGesture>? map)
           && map.TryGetValue(command, out HotkeyGesture gesture)
            ? gesture
            : null;

    /// <summary>The gesture as it should read in a menu, for a scoped command.</summary>
    public string? Display(HotkeyScope scope, string command) => GestureFor(scope, command)?.ToString();

    /// <summary>
    ///  Which command of <paramref name="scope"/> the key event stands for, or null.
    ///  This is what replaced the inline key comparisons: a view asks once and switches
    ///  on the answer, so the SAME handler obeys whatever the user configured.
    /// </summary>
    public string? Command(HotkeyScope scope, KeyEventArgs e)
        => _scopedByGesture.TryGetValue(scope, out Dictionary<HotkeyGesture, string>? map)
           && map.TryGetValue(new HotkeyGesture(e.Key, e.KeyModifiers), out string? command)
            ? command
            : null;

    /// <summary>Every binding of a scope, in the table's own order (for the Settings page).</summary>
    public IReadOnlyList<(string Command, HotkeyGesture? Gesture)> ScopeBindings(HotkeyScope scope)
    {
        if (!HotkeyScopes.All.TryGetValue(scope, out IReadOnlyDictionary<string, HotkeyGesture>? defaults))
        {
            return [];
        }

        List<(string, HotkeyGesture?)> rows = new(defaults.Count);
        foreach (string command in defaults.Keys)
        {
            rows.Add((command, GestureFor(scope, command)));
        }

        return rows;
    }

    /// <summary>
    ///  Replaces the bindings of one scope — a null gesture clears the command — then
    ///  re-indexes, persists and raises <see cref="Changed"/>, exactly as
    ///  <see cref="ApplyBindings"/> does for the window's own commands.
    /// </summary>
    public void ApplyScopeBindings(HotkeyScope scope, IReadOnlyDictionary<string, HotkeyGesture?> bindings)
    {
        if (!_scoped.TryGetValue(scope, out Dictionary<string, HotkeyGesture>? map))
        {
            map = [];
            _scoped[scope] = map;
        }

        foreach ((string command, HotkeyGesture? gesture) in bindings)
        {
            if (gesture is { } g)
            {
                map[command] = g;
            }
            else
            {
                map.Remove(command);
            }
        }

        Reindex();
        Save();
        Changed?.Invoke();
    }

    /// <summary>The gesture bound to a command, or null if the user cleared it.</summary>
    public HotkeyGesture? GestureFor(BrowseCommand command)
        => _bindings.TryGetValue(command, out HotkeyGesture g) ? g : null;

    /// <summary>The gesture as it should read in a menu ("Ctrl+Space"), or null.</summary>
    public string? Display(BrowseCommand command) => GestureFor(command)?.ToString();

    /// <summary>Attaches the action a command runs. A command with no action is
    /// inert: its gesture falls through to whatever the focused control does with it.</summary>
    public void Bind(BrowseCommand command, Action action) => _actions[command] = action;

    /// <summary>Rebuilds the gesture → command lookup after <see cref="Bindings"/> changed.</summary>
    public void Reindex()
    {
        _byGesture.Clear();
        foreach ((BrowseCommand command, HotkeyGesture gesture) in _bindings)
        {
            // First writer wins, so a user override cannot silently shadow another
            // command; the duplicate is simply never reachable (as upstream does).
            _byGesture.TryAdd(gesture, command);
        }

        _scopedByGesture.Clear();
        foreach ((HotkeyScope scope, Dictionary<string, HotkeyGesture> map) in _scoped)
        {
            Dictionary<HotkeyGesture, string> byGesture = [];
            foreach ((string command, HotkeyGesture gesture) in map)
            {
                byGesture.TryAdd(gesture, command);
            }

            _scopedByGesture[scope] = byGesture;
        }
    }

    /// <summary>
    ///  Installs the dispatcher on the window. <paramref name="reserved"/> is asked,
    ///  for every recognised gesture, whether the currently focused view owns it;
    ///  when it says yes the event is left alone (see the class remarks for why the
    ///  handler has to tunnel).
    /// </summary>
    public void Install(Window window, Func<BrowseCommand, HotkeyGesture, bool> reserved)
    {
        _reserved = reserved;
        window.AddHandler(InputElement.KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        HotkeyGesture gesture = new(e.Key, e.KeyModifiers);
        if (!_byGesture.TryGetValue(gesture, out BrowseCommand command)
            || !_actions.TryGetValue(command, out Action? action))
        {
            return;
        }

        if (_reserved?.Invoke(command, gesture) == true)
        {
            return;
        }

        e.Handled = true;
        action();
    }

    /// <summary>Writes the current map; best-effort (never throws).</summary>
    public void Save()
    {
        try
        {
            string? dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            Dictionary<string, string> flat = _bindings.ToDictionary(p => p.Key.ToString(), p => p.Value.ToString());

            // The scoped entries share the file, under "Scope:Command" keys. One file,
            // because they are one thing to the user — "my hotkeys" — and because a
            // window command and a grid command can collide, which is only visible if
            // both are in front of whoever reads it.
            foreach ((HotkeyScope scope, IReadOnlyDictionary<string, HotkeyGesture> defaults) in HotkeyScopes.All)
            {
                foreach (string command in defaults.Keys)
                {
                    HotkeyGesture? gesture = GestureFor(scope, command);
                    flat[$"{scope}:{command}"] = gesture?.ToString() ?? string.Empty;
                }
            }

            // A command the user cleared is simply absent from _bindings, and an absent
            // entry means "take the default" on the next Load — so clearing would undo
            // itself at the next start. Write it out explicitly as the empty string,
            // which Load already understands as "cleared".
            foreach (BrowseCommand command in Defaults.Keys)
            {
                if (!_bindings.ContainsKey(command))
                {
                    flat[command.ToString()] = string.Empty;
                }
            }

            File.WriteAllText(_path, JsonSerializer.Serialize(flat, Options));
        }
        catch
        {
            // Persistence is best-effort; a failure must not crash the app.
        }
    }

    // Defaults, with any parseable entry of the JSON file layered on top. An empty
    // string clears a binding (the command keeps existing, unreachable by keyboard).
    private Dictionary<BrowseCommand, HotkeyGesture> Load()
    {
        Dictionary<BrowseCommand, HotkeyGesture> map = new(Defaults);

        try
        {
            if (!File.Exists(_path))
            {
                return map;
            }

            Dictionary<string, string>? flat =
                JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path), Options);
            if (flat is null)
            {
                return map;
            }

            foreach ((string name, string value) in flat)
            {
                if (!Enum.TryParse(name, ignoreCase: true, out BrowseCommand command))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(value))
                {
                    map.Remove(command);
                }
                else if (HotkeyGesture.TryParse(value, out HotkeyGesture gesture))
                {
                    map[command] = gesture;
                }
            }
        }
        catch
        {
            // Missing/corrupt/unreadable → the defaults above.
        }

        return map;
    }

    // The scope tables, with any parseable "Scope:Command" entry of the file on top.
    // Same contract as Load(): an empty string means the user cleared the binding.
    private void LoadScoped()
    {
        foreach ((HotkeyScope scope, IReadOnlyDictionary<string, HotkeyGesture> defaults) in HotkeyScopes.All)
        {
            _scoped[scope] = new Dictionary<string, HotkeyGesture>(defaults, StringComparer.Ordinal);
        }

        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            Dictionary<string, string>? flat =
                JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path), Options);
            if (flat is null)
            {
                return;
            }

            foreach ((string name, string value) in flat)
            {
                int separator = name.IndexOf(':', StringComparison.Ordinal);
                if (separator <= 0
                    || !Enum.TryParse(name[..separator], ignoreCase: true, out HotkeyScope scope)
                    || !_scoped.TryGetValue(scope, out Dictionary<string, HotkeyGesture>? map))
                {
                    continue;
                }

                string command = name[(separator + 1)..];
                if (!map.ContainsKey(command)
                    && !(HotkeyScopes.All.TryGetValue(scope, out IReadOnlyDictionary<string, HotkeyGesture>? defaults)
                         && defaults.ContainsKey(command)))
                {
                    // A command this build does not have (an older/newer file): kept out
                    // rather than resurrected, so it cannot shadow a live gesture.
                    continue;
                }

                if (string.IsNullOrWhiteSpace(value))
                {
                    map.Remove(command);
                }
                else if (HotkeyGesture.TryParse(value, out HotkeyGesture gesture))
                {
                    map[command] = gesture;
                }
            }
        }
        catch
        {
            // Missing/corrupt/unreadable → the defaults above.
        }
    }

    // Same directory as ui-state.json (see UiStateService.ResolvePath).
    private static string ResolvePath()
    {
        string? baseDir = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrEmpty(baseDir))
        {
            baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        }

        if (string.IsNullOrEmpty(baseDir))
        {
            baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config");
        }

        return Path.Combine(baseDir, "GitExtensions.Avalonia", "hotkeys.json");
    }
}
