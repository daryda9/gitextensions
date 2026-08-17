# Git Extensions — cross-platform (Linux) port

A ground-up Avalonia UI for Git Extensions on Linux that **reuses the existing
git-logic core** (`GitCommands`, `GitExtUtils`, `GitExtensions.Extensibility`,
`GitUIPluginInterfaces`) instead of the WinForms UI, which is Windows-only.

The engine is still git and the logic is still upstream's. What changed is the
interface — and, in the places listed below, what the application can do without
reaching for another program.

![The main window: revision graph, repository tabs, and the diff pane](docs/screenshots/main-window.png)

> Every screenshot on this page was taken from this build on a throw-away demo
> repository, on a virtual display, with the theme following the desktop's dark
> preference. Nothing is a mock-up.

---

## What is different from the original Git Extensions

### The interface is Avalonia, and Linux is the target

Upstream's UI is WinForms, which runs only on Windows: there is no retarget, so
the shell was rebuilt. The layout, the toolbar, the revision grid, the file
lists, the diff pane and the bottom tabs follow `FormBrowse` closely enough to be
recognisable, and the parts that are *not* recognisable are the ones a modern
Linux desktop expects instead.

![Appearance settings: theme, style, UI size, title bar, repository tabs, coloured icons](docs/screenshots/appearance-settings.png)

Each of those is independent, applied immediately as a preview, and persisted on
OK — reverted on Cancel:

| Setting | What it does |
|---|---|
| **Theme** | Light, Dark, or **System** — the desktop's own preference, followed live through the XDG portal |
| **Style** | **Modern** (vector icon set, neutral palette, flat chrome) or **Classic** (the earlier look) |
| **UI size** | Zooms the whole interface — text, icons, spacing, grid, diff — with no restart |
| **Title bar** | Menu **inside** the window's own title bar (VS Code style, with a `…` overflow measured at runtime) or the desktop's title bar with a separate menu row |
| **Repository tabs** | One window holding several repositories, or one repository at a time as before |

The icon set is drawn as vectors rather than shipped as bitmaps, and in Modern
style it is **coloured by what the command does** — green to create, red to
delete, blue to talk to a remote, purple for the index, cyan for branches and
submodules, amber for stashes and tags. That is a switch, and turning it off
loses nothing.

### One window, several repositories

Submodules, worktrees and any repository you open land on a strip of tabs at the
top of the window. A single click on a submodule or worktree opens it as a
**preview** (italic, replaced by the next one); a double click pins it. Tabs can
be dragged to reorder, `Ctrl+W` closes, `Ctrl+PgUp`/`PgDn` cycle, and the open
set is remembered across restarts. Each tab keeps its own selected commit, so
switching costs a click rather than a reload.

### Diffs, merges and image comparisons are done in-house

Upstream hands conflicts and side-by-side comparison to an external tool —
kdiff3 by default. That still works and is still offered. But **with no external
tool configured at all, none of it is unavailable**: the engine is git itself
(`git merge-file --diff3`, `git diff --no-index`), and the panels are the
application's own.

A real three-way merge editor — LOCAL, BASE and REMOTE read-only above, the
editable result below, per-character highlighting of what each side changed, and
one-click resolutions for whole-side or trivial (whitespace, line-ending,
blank-line) conflicts:

![The internal three-way merge editor](docs/screenshots/merge-editor.png)

A side-by-side comparison of any two revisions of a file, aligned from git's own
hunk headers, with intra-line marks showing which *characters* moved:

![Side-by-side comparison of two revisions](docs/screenshots/side-by-side-diff.png)

And images, which a text diff can only describe as "binary files differ": side by
side, overlaid with adjustable opacity, or as a per-pixel difference, with the
byte sizes and dimensions named underneath. The format is recognised **from the
file's bytes**, not its extension, and a file that decodes only partially is
declared as `TRUNCATED FILE` rather than shown as an image with silently blank
rows.

![Comparing two revisions of an image](docs/screenshots/image-diff.png)

### Conflicts say what they are, and rerere is visible

The conflict list explains in one line what kind of conflict each file has and
which ways out exist — including the cases where a three-way merge is not
possible at all (deleted on one side, a submodule pointer, a symlink). `git
rerere` is not a hidden setting: it can be switched on from here, it reports what
it has already replayed, its cache has a window of its own, and `forget` asks
first.

![The conflict panel](docs/screenshots/conflict-dialog.png)

### An operation that stops has a bar, not a dead end

Merge, rebase, cherry-pick, revert and `git am` all leave the repository in a
state that needs a decision. The bar names the state, the step, the branch and
where it stopped, and offers only the buttons git will actually accept:

![The sequencer bar during an interactive rebase](docs/screenshots/sequencer-bar.png)

`Continue` answers git's editor for you when a step wants a message (`reword`,
`squash`, a merge commit), and cancelling that question is reported as
**cancelled** — the operation is left exactly where it was, resolutions still
staged.

The todo list of an interactive rebase is rendered rather than dumped into `$EDITOR`:

![The interactive-rebase todo editor](docs/screenshots/rebase-todo.png)

### A command palette over the real menu

`Ctrl+Shift+P` searches the menu itself — walked at open time, so availability is
decided by the same code that greys the menu out, and there is no second registry
to keep in sync. Commands that are unavailable here are shown greyed rather than
hidden, toggles show their state, shortcuts are listed, and recent choices come
back first.

![The command palette](docs/screenshots/command-palette.png)

### A terminal inside the window

The `Console` tab is a real pty running your shell in the repository's directory
— colours, prompt, `less`, `git add -p`, anything. `Ctrl+G` still launches an
external terminal if you prefer one.

![The embedded terminal](docs/screenshots/embedded-terminal.png)

### The commit dialog, rebuilt

Staged and unstaged lists with their own filters, the diff of whatever is
selected on the right, staging by file or by selected lines, message templates,
amend, and the committer identity plus the push target named along the bottom.

![The commit dialog](docs/screenshots/commit-dialog.png)

### File history and blame in a window of their own

Following renames, the graph of a single file with its branches intact (git
rewrites parents for the collected historical names, so the file's history is one
line rather than a ladder of stumps), and blame per line with the commit behind
it:

![File history with blame](docs/screenshots/file-history.png)

### Settings are files, and they survive a crash

Everything the port stores of its own lives in JSON under
`$XDG_CONFIG_HOME/GitExtensions.Avalonia/` — window and view state, hotkeys,
favourites, scripts, commit-info columns. Every write is a **whole-document
atomic replacement under a side lock**, and every update sends a *delta* rather
than the document a window happened to load at start-up. Two copies of the
application running at once, or one of them killed mid-write, cannot leave a
truncated file behind — which would read as "no settings", i.e. a silent reset of
your theme, layout and shortcuts. There are regression harnesses for exactly
this, with real processes and `SIGKILL`.

Git's own configuration is still git's: the settings window writes it through
`git config`, at the level you choose.

### It is tested, and the tests run themselves

Five regression harnesses (inline diff, command palette, view preferences,
settings stores, image integrity) plus the older probes and snapshots.
`Tests/run-all.sh` runs the seven deterministic ones, each in a sandbox of its
own (isolated `XDG_CONFIG_HOME` and `TMPDIR`, global and system git config
silenced, a timeout per harness, the scratch directory kept on failure because it
is the evidence), and `.github/workflows/crossplatform-build.yml` does that plus
a `-warnaserror` build of the whole solution on every push that touches the port.
Excluded from the runner, with the reason written down: the two probes that need
a display, and the performance measurement that is a timing rather than a verdict.

Everything else is verified on screen, and `PORTING.md` records how.

### Smaller things you will notice

- **Translation layer.** Every new string goes through `T(english)`, keyed to
  upstream's catalogue entry where one exists, so a translated build has
  somewhere to put them.
- **Grid columns are resizable**, and their widths persist — upstream's do not.
- **Commit picker** for the rebase range, bounded to the current branch down to
  the merge base with the target.
- **Loading is visible**: a delayed spinner (250 ms, so short reloads do not
  flicker) over the grid and the tree.
- **GitHub**: fork and clone, add the upstream remote, list pull requests and
  create one — without a plugin.
- **A `.deb`**: `packaging/build-deb.sh` produces a self-contained package that
  needs no .NET on the target, installs under `/opt/gitextensions`, and declares
  `git` as its only dependency.

## What is not here

Stated plainly, because a README that only lists wins is a sales page:

- **Plugins**: built-ins only. The shell extension, the Visual Studio
  integration and the Windows-only plugin surface are not ported and are not
  planned.
- **One deliberate deviation** in shortcuts: `QuickPull` moved to `Shift+F8`, to
  free `Ctrl+Shift+P` for the palette.
- Wayland is reached through XWayland; the X11 backend is what is tested.
- The **System** theme has not been exercised against a real desktop portal, only
  against the fallback path.
- The image comparison has no drag-pan or zoom shortcuts, and does not refuse
  images above 16 megapixels on screen (the check exists, it has not been driven
  through the UI).
- Assorted measured-and-recorded gaps: right-to-left tab labels eliding awkwardly,
  the commit picker not yet offered by every field that could use it, rerere
  untested with a multi-variant cache or non-ASCII paths.

`ROADMAP.md` keeps that list current, with what was measured and what was only
reasoned about. `PORTING.md` is the milestone-by-milestone record, `HANDOFF.md`
the working state.

---

## How the reuse works

The reusable core targets `net10.0-windows` in the real solution only because the
repo-root `Directory.Build.props` forces `UseWindowsForms=true` globally. This
tree is a **separate, isolated build** (its own `Directory.Build.props` that does
NOT import the repo root) that:

- Compiles the same source files under plain **`net10.0`** (no WinForms).
- Supplies a small **compat shim assembly** (`Compat.WinFormsShims`) that
  declares the minimal `System.Windows.Forms` / `System.Drawing` (GDI) surface the
  core touches, so those files compile unmodified. `System.Drawing` primitives
  (Point/Color/Size/Rectangle/SystemColors) come from the runtime, not the shim.
- Excludes the genuinely WinForms/Win32/GDI parts of `GitExtUtils/GitUI/`
  (Theming, Interops, ToolStrip/DPI helpers) and keeps the portable threading
  helpers (`ThreadHelper`, `TaskManager`).
- Provides an **Avalonia** front-end (`App/`) that drives the reused core.

The only edit to existing repo source is in `GitCommands/Settings/AppSettings.cs`:
Windows-registry reads/writes are guarded with `OperatingSystem.IsWindows()`
(no-ops off Windows). Windows behavior is unchanged.

## Projects

| Project | Purpose |
|---|---|
| `Compat.WinFormsShims` | Minimal WinForms/GDI stand-ins so the core compiles |
| `Core.Extensibility` | = `GitExtensions.Extensibility` under net10.0 |
| `Core.GitExtUtils` | portable parts of `GitExtUtils` (+ threading bootstrap) |
| `Core.GitUIPluginInterfaces` | = `GitUIPluginInterfaces` under net10.0 |
| `Core.GitCommands` | = `GitCommands` (all git logic) under net10.0 |
| `App/GitExtensions.Avalonia` | the Avalonia UI |
| `Tests/*` | ten harness projects; `GitExtensions.Avalonia.slnx` builds them all |

## Build & run

### On Linux

```bash
export DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH"
cd src/crossplatform

# Build the app (builds the whole core chain).
dotnet build App/GitExtensions.Avalonia.csproj

# Headless self-test — proves the reused git core works on Linux (no display).
DLL=$(find bin -name GitExtensions.Avalonia.dll | head -1)
dotnet "$DLL" --selftest /path/to/a/git/repo

# GUI (needs X11, or XWayland under a Wayland session).
./run.sh /path/to/a/git/repo

# Build everything and run the deterministic harnesses.
dotnet build GitExtensions.Avalonia.slnx -c Debug -warnaserror
Tests/run-all.sh --no-build
```

A `.deb` for a machine with no .NET installed: `packaging/build-deb.sh`, then
`sudo apt install ./packaging/out/gitextensions_<version>_amd64.deb`. See
`packaging/README.md`.

### On Windows

Same portable build, nothing extra to install. A `dotnet` on `PATH` is all it
needs — the `DOTNET_ROOT` export above is not a requirement, only an artifact of
the Linux box it was written on having a user-local SDK.

```powershell
cd src\crossplatform

# Build the app (builds the whole core chain).
dotnet build App\GitExtensions.Avalonia.csproj

# Headless self-test — same as above, no window needed.
dotnet bin\GitExtensions.Avalonia\Debug\net10.0\GitExtensions.Avalonia.dll --selftest C:\path\to\a\git\repo

# GUI.
dotnet run --project App\GitExtensions.Avalonia.csproj

# Standalone build for a machine with no .NET installed.
dotnet publish App\GitExtensions.Avalonia.csproj -c Release -r win-x64 --self-contained
```

`dotnet build` already emits a native `GitExtensions.Avalonia.exe` launcher next
to the assemblies, but it needs the .NET 10 shared runtime; the `publish` line
above bundles the runtime instead. It lands in
`dist\<configuration>\<rid>\` (gitignored) — `dist\Release\win-x64\` here, or
`dist\<configuration>\portable\` when no `-r` is given. Pass `-o` to override,
as `packaging/build-deb.sh` does.

Do **not** add `-p:PublishSingleFile=true`. It does not achieve a single file
anyway — Skia, ANGLE and HarfBuzz stay beside the executable as separate native
DLLs — and it empties `Assembly.Location`, which is how the core
`Translator.GetTranslationDir()` finds the `Translation` directory. The path
degrades to a *relative* `"Translation"`, resolved against the working
directory, so every catalogue silently becomes unreachable and the UI falls back
to English with no error.

Piping the self-test into `Select-Object -First N` makes it report exit 255.
That is PowerShell closing the stream early, not a failure — run it unpiped, or
pipe through `Out-String`, and it exits 0.
