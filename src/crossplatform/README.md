# Git Extensions — cross-platform (Linux) port

A ground-up Avalonia UI for Git Extensions on Linux that **reuses the existing
git-logic core** (`GitCommands`, `GitExtUtils`, `GitExtensions.Extensibility`,
`GitUIPluginInterfaces`) instead of the WinForms UI, which is Windows-only.

## How it works

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
| `App/GitExtensions.Avalonia` | Avalonia UI (open repo → show commit log) |

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

# GUI (needs X11 or Wayland).
dotnet run --project App/GitExtensions.Avalonia.csproj
```

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

## Status / next steps

Done: portable core compiles and runs on Linux; Avalonia shell; vertical slice
(open repo, current branch, commit log).

Not yet ported (large, incremental): the full revision graph, diff/blame views,
commit/stage UI, settings pages, plugins — each currently a WinForms form in
`GitUI`, to be rebuilt in Avalonia on top of the same reused core.
