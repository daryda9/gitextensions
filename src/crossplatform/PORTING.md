# Git Extensions su Linux — Port cross-platform

Documento di contesto, stato e roadmap del port Linux basato su Avalonia.

---

## 1. Contesto

**Git Extensions** è una GUI per git scritta in **C# / WinForms**, storicamente
**solo Windows**:

- UI in **WinForms** (`net10.0-windows`, `UseWindowsForms=true`).
- Estensione shell nativa C++/ATL, integrazione Explorer / Visual Studio.
- Molte P/Invoke Win32, accesso al registro di Windows.

WinForms su .NET gira **solo su Windows**: non esiste un retarget diretto a
Linux. Le opzioni erano:

| Opzione | Valutazione |
|---|---|
| Wine | Esegue il binario Windows sotto emulazione. Nessun vero port. |
| Port Mono WinForms | .NET 10 WinForms non gira su Mono; downgrade + riscrittura P/Invoke. Rischio altissimo. |
| **Riscrittura UI (Avalonia)** ✅ | Riusa il core git, riscrive solo la UI con un toolkit cross-platform. |

Scelta: **Avalonia**, riusando il core di logica git esistente.

### Mappa del codice (analisi iniziale)

Grafo di dipendenze (foglie in alto):

```
GitExtensions.Extensibility
  └─ GitExtUtils / GitUIPluginInterfaces
       └─ GitCommands            ← tutta la logica git (il "premio")
            └─ ResourceManager
                 └─ GitUI         ← UI WinForms (da riscrivere)
                      └─ GitExtensions (exe) + plugin
```

- **Riutilizzabile** (poca/nessuna dipendenza UI): `GitCommands` (201 file di
  logica git), `GitExtensions.Extensibility`, `GitUIPluginInterfaces`, parte di
  `GitExtUtils`.
- **Da riscrivere** (WinForms puro): `GitUI` (~26 P/Invoke, CsWin32,
  ConEmu/ICSharpCode/NetSpell), `GitExtensions` exe, tutti i plugin (form di
  settings), `BugReporter`.

---

## 2. Cosa è stato fatto

Tutto il lavoro è isolato in **`src/crossplatform/`**. Un solo file esistente
modificato (`AppSettings.cs`), con guardie che non toccano il comportamento
Windows. Il branch è `linux-avalonia-port`.

### Strategia: build isolata + shim di compatibilità

Il core compila come `net10.0-windows` **solo** perché la
`Directory.Build.props` della root forza `UseWindowsForms=true` globalmente.
`src/crossplatform/` è un albero di build **separato**:

1. Ha una propria `Directory.Build.props` che **non importa** quella della root
   → niente WinForms forzato, target `net10.0` puro, nessun simbolo `WINDOWS`.
2. Ricompila gli **stessi file sorgente** del core (via glob) sotto `net10.0`.
3. Fornisce un assembly **shim di compatibilità** (`Compat.WinFormsShims`) che
   dichiara la minima superficie `System.Windows.Forms` / `System.Drawing` (GDI)
   che il core referenzia (`IWin32Window`, `Control`, `MessageBox`, `Font`,
   `Image`, `Application`, ecc.), così i file compilano **senza modifiche**.
   - I primitivi `System.Drawing` (`Point`, `Color`, `Size`, `Rectangle`,
     `SystemColors`) vengono dal runtime, non dallo shim.
4. Esclude le parti realmente WinForms/Win32/GDI di `GitExtUtils/GitUI/`
   (Theming, Interops, helper ToolStrip/DPI), tenendo gli helper di threading
   portabili (`ThreadHelper`, `TaskManager`).
5. Front-end **Avalonia** (`App/`) che pilota il core riusato.

**Perché non tocca la build Windows:** l'albero è separato e auto-contenuto; la
soluzione originale non lo referenzia. Verificato: `git status` mostra solo
`src/crossplatform/` + la modifica guardata ad `AppSettings.cs`.

### Progetti creati

| Progetto | Ruolo |
|---|---|
| `Compat.WinFormsShims` | Stand-in minimi WinForms/GDI per far compilare il core |
| `Core.Extensibility` | = `GitExtensions.Extensibility` sotto net10.0 |
| `Core.GitExtUtils` | parti portabili di `GitExtUtils` (+ bootstrap threading) |
| `Core.GitUIPluginInterfaces` | = `GitUIPluginInterfaces` sotto net10.0 |
| `Core.GitCommands` | = `GitCommands` (tutta la logica git) sotto net10.0 |
| `App/GitExtensions.Avalonia` | UI Avalonia: picker + tab History/Commit/Diff |

Foundation condivisa: `App/GitContext.cs` (`CreateModule(path)`) è l'unico
modo supportato con cui le viste ottengono un `GitModule` del core
pienamente cablato (riusa `ServiceContainerRegistry`).

Viste Avalonia (una `UserControl` self-contained + un service ciascuna,
tutte sul core riusato via `GitContext`):

| Vista | Service | Cosa fa |
|---|---|---|
| `RevisionGridView` | `RevisionService` | log multi-colonna (hash/autore/data/oggetto) + badge ref, via `RevisionReader`/`GetRefs` |
| `DiffView` | `DiffService` | file changed di un commit + diff unificato colorato, via `GetDiffFiles*`/`GetSingleDiffAsync` |
| `WorkingDirectoryView` | `WorkingDirectoryService` | staged/unstaged, stage/unstage, commit (amend), via `GetIndex/WorkTreeFiles`/`StageFiles`/`Commands.Commit` |
| `RepositoryPickerView` | `RecentRepositoriesService` | folder picker nativo Avalonia + MRU via `RepositoryHistoryManager` |

### Unica modifica al sorgente esistente

`GitCommands/Settings/AppSettings.cs`: gli accessi al **registro di Windows**
(lettura/scrittura settings legacy) sono protetti con
`OperatingSystem.IsWindows()`. Su Linux `Registry.CurrentUser` è `null` →
causava `NullReferenceException` all'avvio. Su Windows: nessun cambiamento di
comportamento (le guardie sono `false`).

### Milestone completate

- **M1** — il core portabile compila su Linux (`net10.0`), **0 errori**.
- **M2** — shell Avalonia compila.
- **M3** — slice verticale funzionante: apri repo → branch corrente + log dei
  commit, letti dal core riusato (`Executable` / `GitModule` / `AppSettings`).
  Verificato headless (`--selftest`) e in GUI su Wayland/X11.
- **M4** — nucleo UX quotidiano: factory `GitModule` condivisa + shell a tab
  (History, Commit, Diff) con picker repo + MRU. Le 4 viste ad alta priorità
  (revision grid, diff, working dir/staging, repo picker) sono implementate
  sul core riusato. Sviluppate in parallelo (4 subagent in worktree isolati),
  merge pulito, build 0 errori, GUI verificata headless (xvfb) — la History
  mostra 200 commit con badge ref.
- **M5** — rifiniture alta priorità: grafo DAG nella grid, pannello dettaglio
  commit nel tab Diff, menu contestuali + scorciatoie. Sviluppate in parallelo
  (3 subagent in worktree), merge pulito, build 0 errori, GUI verificata
  headless (xvfb) — grafo + dettaglio + diff colorato renderizzano.
- **M6** — operazioni git (priorità media): tab Branches (crea/checkout/merge/
  rebase/delete branch + tag), Remote (fetch/pull/push + dialog credenziali),
  Stash (save/apply/pop/drop), Blame, File History; azioni commit-targeted
  (checkout/cherry-pick/reset soft·mixed·hard) dal menu contestuale della grid.
  Sviluppate in parallelo (4 subagent in worktree), merge pulito, build 0
  errori, GUI verificata headless (xvfb) — History/Branches(45 branch,179 tag)/
  Stash renderizzano. Fix: warm-up single-thread del core (race su `Lazy`
  condivisa a load concorrente); `ToString` dei DTO per il rendering delle liste.

- **M7** — restyle grafico su layout dell'app originale (FormBrowse): finestra
  unica integrata con **toolbar a icone** (Open/Fetch/Pull/Push/Commit/Stash/
  Refresh/New branch), **albero a sinistra** (`RepoObjectsTree`: branch/remote/
  tag/stash + menu contestuali), **revision grid DAG** al centro, **pannello
  inferiore** a tab (Commit=dettaglio+diff / Working directory / Blame / File
  history), **status bar** (repo · branch · ahead/behind). Riusa le 256 icone
  PNG originali (`IconLoader`, link AvaloniaResource) + tema scuro tipo
  GitExtensions (palette in `App.cs`). Fondazione (icone+tema) + 3 subagent in
  parallelo (albero, toolbar+status, restyle grid/detail/diff), poi
  integrazione del `MainWindow`. Build 0 errori, GUI verificata headless (xvfb).

- **M8** — menu + rifiniture UI: **barra menu** (File/Edit/View/Repository/
  Commands/Help) sopra la toolbar, con "Open recent" (MRU), tema chiaro/scuro,
  About, e voci che rispecchiano toolbar + New tag; **`ThemeManager`** con
  switch **chiaro/scuro live** (muta il colore dei brush stabili → repaint
  senza DynamicResource); **scorciatoie globali** (F5 refresh, Ctrl+O apri);
  **About dialog**; polish toolbar (bordo, spaziatura, hover/pressed, tooltip).
  Grafo DAG multi-lane verificato su repo ramificato (lane colorate, merge/
  branch edge). 2 subagent (menu, about+toolbar) + tema/integrazione a mano.
  Build 0 errori, GUI verificata headless (xvfb) in tema scuro e chiaro.

- **M9** — packaging Linux: `packaging/build-deb.sh` produce un `.deb`
  **self-contained** (`dotnet publish -r linux-x64 --self-contained`, nessun
  SDK/runtime richiesto all'utente). Pacchetto `gitextensions_5.0.0-linux1_amd64.deb`
  (~35 MB, payload ~105 MB): payload in `/opt/gitextensions/`, launcher
  `/usr/bin/gitextensions`, `.desktop` in `/usr/share/applications/`, icona
  256px (logo ufficiale) in `hicolor`, `control` con `Depends: git`, `postinst`
  (update-desktop-database/gtk-update-icon-cache, guardati). Script idempotente,
  fail-fast. Verificato: `.deb` costruito + binario pubblicato supera il
  `--selftest` (branch + commit, git core ok). Delegato a subagent in worktree,
  cherry-pick pulito.

- **M10** — debito tecnico (3 subagent claude paralleli in worktree, file
  disgiunti, cherry-pick + build check dopo ognuno, GUI verificata):
  - **Ctrl+C Linux**: `ProcessExtensions.cs` ora invia **SIGINT** (`libc kill`,
    P/Invoke) sul ramo non-Windows con fallback a `Kill(entireProcessTree)`;
    Windows invariato (guardia `OperatingSystem.IsWindows()`). Latente finché
    non esiste un call-site Linux (l'unico è in GitUI WinForms, non compilato).
  - **NU1903**: Avalonia **11.2.3 → 11.3.14** (prima versione che tira una
    `Tmds.DBus.Protocol` patchata); build 0 errori, GUI screenshot ok.
  - **VSTHRD100**: async-void 7→0 (handler → wrapper `void` che delega a
    `async Task` con try/catch), VSTHRD200 5→0. Restano 2 VSTHRD002 in
    `RemoteService` (rimozione richiede rendere async la catena pubblica usata
    da `MainWindow` — rinviato).
  Build 0 errori, GUI verificata headless (xvfb) — menu/toolbar/tree/grid ok.

- **M11** — limiti funzionali + rifinitura grid (2 subagent claude paralleli,
  file disgiunti, cherry-pick + build + GUI ok):
  - **Push sicuro**: `RemoteService.Push` usa ora `ForcePushOptions.ForceWithLease`
    (`--force-with-lease`) invece di `--force`; checkbox UI "Force (with lease)".
  - **Credenziali senza URL injection**: rimosso il rewrite `user:pass@` nell'URL;
    quando servono credenziali (retry post-auth-fail, solo http/https) si passano
    a git via helper per-comando `-c credential.helper='!f(){...}'` che legge
    **variabili d'ambiente** (`GE_AVALONIA_CRED_USER/PASS`) settate solo per quel
    comando e ripristinate in `finally`. La riga di comando (che `Executable`
    logga) contiene solo i *nomi* delle variabili → il segreto non finisce in
    log/reflog/URL. ssh resta key-based. Verificato con `git credential fill`.
  - **Filtro/ricerca grid**: `TextBox` live in cima a `RevisionGridView`
    (autore/messaggio/hash full+short, case-insensitive, Esc/✕ per pulire); con
    filtro attivo la colonna grafo DAG collassa a larghezza 0 (evita edge verso
    righe nascoste), il modello `_allRows` non viene mutato.
  Build 0 errori, GUI verificata headless (xvfb).

- **M12** — rifiniture UI + voci checklist (2 subagent claude paralleli, file
  disgiunti — solo uno tocca `MainWindow`):
  - **Persistenza stato UI**: nuovo `UiStateService` legge/scrive
    `~/.config/GitExtensions.Avalonia/ui-state.json` (dimensioni finestra, larghezza
    albero, ratio degli split info/diff, tema Light/Dark). All'avvio applica tema
    (prima che i brush `App.*` vengano letti) + dimensioni + pannelli; salva su
    `Window.Closing` e al cambio tema. `Sanitize` clampa valori corrotti →
    nessun pannello collassato. Restore verificato in GUI (riapre in Light a
    finestra ridotta).
  - **Create branch/tag dalla grid**: registrati via `AddCommitCommand` come
    checkout/cherry-pick/reset (stesso `RunOp`→`RefreshAll`), riusano
    `BranchTagService.CreateBranch/CreateTag` e il prompt `PromptAsync` esistente.
  - **Undo last commit**: `git reset --soft HEAD~1` dal tab Working directory
    (mantiene le modifiche staged; errore gestito se non c'è parent).
  - **Clean working directory**: `git clean -fd` con **dry-run + conferma modale**
    (lista i file da rimuovere, "This cannot be undone"); niente rimozione senza
    Yes esplicito. Flow distruttivo testato in GUI.
  Build 0 errori, GUI verificata headless (xvfb).

- **M13** — settings + voci checklist (3 subagent claude paralleli, file
  disgiunti — solo uno tocca `MainWindow`/`MainMenu`):
  - **SettingsWindow** Avalonia (nuova): lista categorie a sinistra + pannello a
    destra, Save/Cancel. Impostazioni reali e persistite: **identità git**
    (`user.name`/`user.email` via `GetEffectiveSetting`/`SetSetting`, repo-local
    con fallback global), **default pull action** (merge/rebase/fetch, nuovo
    `SettingsService` → `app-settings.json`), **tema** (Light/Dark via
    `UiStateService`/`ThemeManager`, preview live, Cancel ripristina). Aperta da
    menu Edit ▸ Settings…. Fondazione estendibile, NON il porto del framework
    WinForms `ISettingControlBinding`.
  - **Rename branch** dal context menu del tree (`git branch -m`, solo branch
    locali; prompt + refresh riusati; git rifiuta target esistente → errore
    mostrato).
  - **Stash staged** (`git stash push --staged`, git ≥2.35): stasha solo l'index,
    lascia il working tree. Verificato su repo temporaneo.
  Build 0 errori, GUI verificata headless (xvfb) — SettingsWindow renderizzata.

- **M14** — chiusura voci checklist (3 subagent claude paralleli, file disgiunti
  — solo uno tocca `MainWindow`/`MainMenu`):
  - **Clone + Init**: menu File ▸ "Clone repository…" (dialog URL + folder
    picker) e "Create new repository…"; `CloneInitService` (`git clone`/`git
    init`), apre il repo risultante via `OpenRepository` esistente.
  - **Nodo Submodules** nel tree ("Submodules (N)", con stato not-init/out-of-date
    per voce): `SubmoduleService` list via `GetSubmodulesLocalPaths` + `git
    submodule status`; azioni Update (`--init -- <path>`) e Update all
    (`--init --recursive`). Open rinviato (richiede MainWindow).
  - **Revision grid**: indicatore **git-notes** (pill + tooltip, `git notes list`
    una volta per load), **toggle data** (Commit/Author, Assoluta/Relativa via
    flyout "Date ▾"), **mostra/nascondi colonne** (Commit ID/Author/Date via
    "Columns ▾"). DAG e filtro invariati; header "Hash" → "Commit ID".
  Build 0 errori, GUI verificata headless (xvfb).

- **M15** — chiusura voci checklist (3 subagent claude paralleli, file disgiunti
  — uno tocca `MainWindow`, uno `RepoObjectsTree`, uno `DiffView`):
  - **Revert + Archive** dal menu contestuale della grid (`AddCommitCommand`):
    `git revert --no-edit <hash>` (conflitti gestiti, refresh mostra lo stato) e
    `git archive --format=zip|tar.gz -o <file> <hash>` via `ArchiveDialog`
    (formato + save-file picker). Nuovo `RevertArchiveService`.
  - **Remotes manager** dal nodo Remotes del tree: `RemotesDialog` (lista + Add/
    Edit URL/Rename/Remove) + azioni rapide per-remote; `RemoteService` esteso
    con `git remote add/rename/remove/set-url`.
  - **DiffView**: "Open in external difftool" (`GitModule.OpenWithDifftool`,
    detached/non-blocking, messaggio se nessun tool configurato) e "Compare file
    to working directory" (`git diff <commit> -- <path>`, reso nel pannello diff
    colorato esistente).
  Build 0 errori, GUI verificata headless (xvfb).

- **M16** — chiusura voci checklist (3 subagent claude paralleli, file disgiunti
  — MainWindow/MainMenu, RepoObjectsTree, RevisionGridView):
  - **Menu Repository/Tools/Help**: `ExternalToolService` (Process.Start
    non-bloccante, fallback graceful). Repository ▸ File Explorer + Edit
    .gitignore/.gitattributes/.mailmap/.git-info-exclude (xdg-open, touch se
    mancante). Tools ▸ Git bash (probe x-terminal-emulator/gnome-terminal/…),
    GitK, Git GUI (detached, cwd repo). Help ▸ manual/changelog/report/donate
    (xdg-open URL).
  - **Nodo Worktrees** (`WorktreeService`): "Worktrees (N)" da `git worktree
    list --porcelain`; Add/Remove/Prune (Open rinviato). Più "Checkout tag
    revision…" (detached) sui nodi Tag.
  - **Grid navigate**: parent/first-parent (`ParentHashes[0]`), nearest child,
    go-to-commit per hash (flyout "Go to ▾"); scorciatoie Alt↑ / Alt↓ / Ctrl+G.
    DAG/filtro/note/toggle preservati.
  Build 0 errori, GUI verificata headless (xvfb).

- **M17** — chiusura voci checklist (3 subagent claude paralleli, file disgiunti
  — MainWindow/MainMenu, WorkingDirectoryView, RevisionGridView):
  - **Reflog browser** (`ReflogService`/`ReflogWindow`, View ▸ Show reflog):
    `git reflog --format=…` parsato, copy hash / checkout detached.
  - **Bisect** (`BisectService`): start/good/bad/skip/reset dal menu contestuale
    della grid (auto-start), output ("commit da testare" / "first bad commit")
    in status bar. Verificato su repo temporaneo.
  - **Resolve conflicts** nel Working directory: sezione "Merge conflicts"
    (`git diff --diff-filter=U`), per-file Open-in-mergetool / Take ours / Take
    theirs / Mark resolved; nascosta quando non ci sono conflitti.
  - **Grid scope branch**: "Branches ▾" all / current-only / filtered (stub HEAD);
    reload preserva DAG/filtro/note.
  Build 0 errori, GUI verificata headless (xvfb) — badge git-notes visibile.

- **M18** — chiusura voci checklist (3 subagent claude paralleli, file disgiunti
  — MainWindow/DiffView/DiffService, RepoObjectsTree, WorkingDirectoryView):
  - **Compare commits**: grid context "Select as BASE" / "Compare to BASE"
    (`git diff base other`) / "Compare to working directory" (`git diff commit`),
    reso nel pannello DiffView esistente (file list + diff colorato). Grid
    single-select → coppia BASE+CompareToBASE copre il confronto a due commit.
  - **Tree**: sort ref (nome / data commit, asc/desc, session-local, data
    risolta lazy off-thread) + move up/down (branch) + Copy name / Copy path
    (submodule/worktree) via clipboard Avalonia.
  - **Add to .gitignore**: da file untracked del Working directory — path esatto
    / `*.ext` / `dir/`, append con dedupe + newline, refresh (il file sparisce
    dagli untracked).
  Build 0 errori, GUI verificata headless (xvfb).

- **M19** — chiusura voci checklist (3 subagent claude paralleli, file disgiunti
  — MainWindow, RevisionGridView/RevisionService, StashPanel/StashOpsService):
  - **Commit-edit** (`CommitEditService`, da grid): reword-HEAD (`--amend`),
    reword-older / squash / fixup via `git rebase` non-interattivo con
    `GIT_SEQUENCE_EDITOR`/`GIT_EDITOR` scriptati (awk flippa pick→reword/squash/
    fixup). Guardia dirty-tree + conferma "riscrive la history" + `rebase
    --abort` on-fail. Edge-case root verificati.
  - **Grid "View ▾"**: walk-toggle show remote-branches/tags/stashes (`--branches
    --remotes --tags` + stash hash espliciti) + topo-order; render-only
    non-relatives-gray + highlight-HEAD (reachability sui `ParentHashes` caricati).
  - **Stash**: "Stash…" con messaggio + include-untracked (`git stash push -u
    -m`), + view diff patch colorato (`git stash show -p`). Nota: `StashPanel`
    non ancora agganciato a MainWindow (toolbar Stash chiama StashSave diretto)
    → integrazione futura.
  Build 0 errori, GUI verificata headless (xvfb).

- **M20** — struttura toolbar + dialoghi (3 subagent claude paralleli, file
  disgiunti — MainToolbar/MainWindow, RepoObjectsTree, WorkingDirectoryView):
  - **Toolbar completa**: Split view (stacked ↔ side-by-side), Commit info
    position (below/left/right, ri-hosta il pannello dettaglio), File Explorer
    (`ExternalToolService.OpenPath`), Terminal (`OpenTerminal`). **StashPanel**
    ora agganciato come tab del pannello inferiore (Commit / Working directory /
    Stash / Blame / File history).
  - **Submodules manager dialog** dal nodo tree: update / update-all / sync-all /
    init-all con output.
  - **Reset changes**: discard modifiche tracked, tutte (`git reset --hard HEAD`,
    conferma) o per-file (`git checkout -- <path>`); untracked preservati.
  Build 0 errori, GUI verificata headless (xvfb) — toolbar + tab Stash renderizzati.
  La struttura UI (menu · toolbar · albero · grid DAG · tab inferiori · status
  bar) rispecchia ora `FormBrowse` dell'originale.

- **M21** — chiusura voci checklist (3 subagent claude paralleli, file disgiunti
  — MainWindow/MainMenu, RepoObjectsTree, RevisionGridView):
  - **Favorite repositories** (`FavoritesService`, JSON) + submenu; **Dashboard**
    (`DashboardView`) landing con recent+favorite, close-to-dashboard + refresh;
    **Git maintenance** (`MaintenanceService`/dialog): gc / fsck / delete
    index.lock / edit .git/config; **Repository settings** riusa `SettingsWindow`.
  - **Delete remote branch** (`git push <remote> --delete`) + **merge submodule**
    (`git submodule update --remote --merge`) dal tree.
  - **Quick-search** grid: type-to-jump (F3/Shift+F3/Esc/idle) con pill overlay,
    distinto dal filtro (non nasconde righe).
  Build 0 errori, GUI verificata headless (xvfb) — Dashboard + repo view.

- **M22** — chiusura voci checklist (3 subagent claude paralleli, file disgiunti
  — MainMenu/MainWindow, RepoObjectsTree, WorkingDirectoryView):
  - **Patch** (`PatchService` + `PatchDialogs`): Commands ▸ Format patch
    (`git format-patch base..HEAD -o dir`), Apply patch (`git am`, fallback `git
    apply`), View patch file (viewer read-only colorato, riusa il rendering di
    DiffView).
  - **Manage worktrees dialog** (`WorktreesDialog`) dal nodo Worktrees: list /
    add / remove / prune.
  - **Drag & drop**: trascina file tra le liste staged/unstaged per stage/unstage
    (Avalonia DoDragDrop, same-list guard, riusa Stage/Unstage).
  Build 0 errori, GUI verificata headless (xvfb).

- **M23** — Command log + Compare-to-branch + Sparse (1 subagent) + ricerca
  modello plugin (1 Explore read-only):
  - **Git command log** (Tools): `CommandLogWindow` legge il `CommandLog` statico
    reale del core (`Executable.Start` → `LogProcessStart`) → mostra i comandi git
    effettivamente eseguiti.
  - **Compare to branch**: grid context → picker branch locali → `git diff
    branch..selected` reso via `DiffView.ShowRange` esistente.
  - **Sparse working copy** (`SparseService`/`SparseDialog`): `git sparse-checkout
    list/init --cone/set/disable`.
  - **Piano plugin** documentato (vedi "## Piano modello plugin") per iter. 17-18.
  Build 0 errori.

- **M24** — toolbar split-button + open (1 subagent): "Submodules ▾" (dropdown
  dei submodule + level-up al super-progetto) e "Worktrees ▾" (dropdown), ognuno
  apre il path come repo attivo; "Open" sui nodi submodule/worktree del tree. Il
  toolbar/tree non referenziano MainWindow — usano provider async + evento
  `OpenRepositoryRequested`. Build 0 errori, GUI verificata (xvfb).

- **M25** — modello **plugin** Avalonia (1 subagent, slice verticale):
  - `PluginService` espone `IReadOnlyList<IGitPlugin>`. **MEF (`ManagedExtensibility`)
    è inutilizzabile su Linux**: `Initialise` registra un `AssemblyResolve`
    globale che, caricando un satellite di stringhe d'eccezione, ricorre su sé
    stesso → **StackOverflow** (non catchabile). Quindi **registrazione diretta**
    in-code (`new SampleGreetPlugin()`); il plugin conserva `[Export(typeof(
    IGitPlugin))]` per un futuro loader Linux-safe.
  - `AvaloniaGitUICommands`: `IGitUICommands` minimo (solo `Module` via
    `GitContext`), i metodi `Start*Dialog`/WinForms lanciano `NotSupportedException`.
  - `SampleGreetPlugin` (`GitPluginBase`, 1 `BoolSetting`): `Execute` legge
    `args.GitModule` (branch) e ritorna un messaggio.
  - `PluginSettingsWindow`: renderizza `GetSettings()` per **tipo runtime**
    (`Bool→CheckBox`, `Choice→ComboBox`, `String→TextBox`, `Number→numerico`,
    `Path→TextBox+Browse`), load/save via l'indexer del setting; **ignora**
    `ISettingControlBinding` (WinForms).
  - Menu **Plugins** in `MainMenu` + hook `MainWindow`: run off-thread, `RefreshAll`
    se `Execute→true`, output in status bar.
  Build 0 errori/0 warning. GUI verificata: menu Plugins presente, sample plugin
  eseguito (greeting con branch in status bar).

- **M26** — validazione plugin + avatar (2 subagent):
  - **BackgroundFetch** (plugin reale portato): `Register` avvia un `PeriodicTimer`
    su task di background che esegue `git fetch`/`git fetch --all` all'intervallo
    configurato (settings `FetchIntervalMinutes`/`FetchAllRemotes`); `Unregister`
    cancella; `Execute` fetch immediato. Conferma che il modello plugin regge un
    plugin non banale con stato/timer.
  - **Colonna avatar offline**: `Identicon` (hash FNV-1a stabile dell'email →
    hue HSL + pattern 5x5 simmetrico), `AvatarControl` 18px (ClipToBounds),
    cache per-autore, toggle "Avatar" in "Columns ▾". Nessuna rete (a differenza
    del gravatar dell'originale) — adattamento Linux.
  Build 0 errori, GUI verificata (xvfb).

- **M27** — plugin folder loader Linux-safe + verifica finale (1 subagent):
  `PluginService` ora, oltre ai built-in, scansiona
  `~/.config/GitExtensions.Avalonia/plugins/*.dll` con **reflection pura**
  (`Assembly.LoadFrom` + ricerca tipi `IGitPlugin`, ctor pubblico senza args),
  isola ogni fallimento (assembly/tipo/ctor corrotti loggati e saltati, mai un
  crash), dedupe by `Id` (built-in first). NIENTE MEF (stack-overflow su Linux).
  Testato: cartella assente → 2 built-in; DLL plugin reale → scoperta; DLL
  corrotta → saltata. Verifica finale: build 0 errori, `.deb` ricostruito,
  GUI completa (menu 8 voci / toolbar / avatar / tab).


### Come avviarlo

```bash
cd src/crossplatform
./run.sh                     # GUI, repo corrente (risale alla root)
./run.sh /path/to/repo       # GUI, repo specifico
./run.sh --selftest [repo]   # headless: stampa branch + commit, senza display
```

Richiede il **.NET 10 SDK** (installato in `~/.dotnet`).

---

## 3. Prossime cose da implementare

Il lavoro attuale è **fondamenta + una slice**. Il grosso resta: ogni vista è
oggi una form WinForms in `GitUI`, da ricostruire in Avalonia **sullo stesso
core riusato**. In ordine di priorità suggerito:

### Priorità alta — nucleo UX quotidiano ✅ (M4)
1. ~~**Revision grid**~~ ✅ `RevisionGridView` — log multi-colonna + badge ref
   + **grafo DAG** (lane) aggiunto in M5.
2. ~~**Vista diff**~~ ✅ `DiffView` — file changed + diff unificato colorato.
3. ~~**Working directory / staging**~~ ✅ `WorkingDirectoryView` — stage/unstage,
   commit + amend.
4. ~~**Selezione repo & repository recenti**~~ ✅ `RepositoryPickerView` — folder
   picker nativo + MRU via `RepositoryHistoryManager`.

Rifiniture alta priorità ✅ (M5):
- **Grafo DAG** nella revision grid: `RevisionGraphControl` disegna le lane
  (sweep top-down con indici stabili, nodi + edge branch/merge), colonna
  allineata con header/righe. `RevisionService.BuildGraph`.
- **Dettaglio commit**: `CommitDetailView` (hash/autore/committer/parent +
  messaggio esteso) impilato sopra il diff nel tab Diff (GridSplitter); la
  selezione di una revisione pilota sia dettaglio che diff.
- **Menu contestuali + scorciatoie**: grid (copy hash/subject/author, Ctrl+C),
  diff (copy path / copy diff), working dir (stage/unstage da menu e da
  tastiera Enter/Spazio, Ctrl+Enter = commit).
- Fix: la lista file del diff mostra `Display` (glifo + path).

Nota: grafo multi-lane verificato su repo ramificato in M8 (lane colorate,
merge/branch edge, fork/merge gestiti). ✅

### Priorità media — operazioni git ✅ (M6)
5. ~~**Branch/tag**~~ ✅ `BranchTagPanel`/`BranchTagService` — crea, checkout,
   merge, rebase, delete (branch + tag).
6. ~~**Remote**~~ ✅ `RemotePanel`/`RemoteService` — fetch, pull, push +
   `CredentialsDialog` (retry auth iniettando cred nell'URL, solo http/https;
   ssh resta key-based).
7. ~~**Stash / cherry-pick / reset**~~ ✅ `StashPanel`/`StashOpsService` —
   stash save/apply/pop/drop; cherry-pick e reset (soft/mixed/hard) dal menu
   contestuale della revision grid.
8. ~~**Blame** e **file history**~~ ✅ `BlameView`/`FileHistoryView`
   (+ service); nel tab, input path relativo al repo.

Risolto in M11: push usa `--force-with-lease`; credenziali via git
credential-helper (variabili d'ambiente, non più injection nell'URL, segreto non
loggato). Blame/File-history sono agganciati al menu contestuale della lista file
del diff (`DiffView` → `BlameRequested`/`FileHistoryRequested` → tab in
`MainWindow`) — già dalla UI M7. Resta aperto: blame/history dal menu del
pannello sinistro / grid a livello di commit (meno prioritario).

### UI / look originale ✅ (M7 + M8)
Layout FormBrowse (toolbar + albero + grid DAG + pannello inferiore + status
bar), icone originali, tema scuro/chiaro, **barra menu**, About, scorciatoie.
Vedi milestone M7/M8.
Aperto su questo fronte: ~~filtri/ricerca nella grid~~ ✅ (M11); drag&drop;
scorciatoie più estese; ~~persistenza del tema scelto e delle dimensioni dei
pannelli~~ ✅ (M12, `UiStateService`).

### Priorità bassa — contorno
9. **Pagine settings** in Avalonia — fondazione fatta ✅ (M13, `SettingsWindow`:
   identità git, default pull, tema). Resta: coprire più impostazioni (diff/merge
   tool, tab size, ecc.) sopra questa base; il framework WinForms
   `ISettingControlBinding` NON è stato portato (approccio nativo Avalonia).
10. **Sistema di plugin**: ~~i plugin espongono form WinForms; ripensare il
    modello UI dei plugin~~ ✅ (M25) — modello Avalonia: `IGitPlugin` riusato,
    settings resi per tipo runtime (no `ISettingControlBinding`), loader diretto
    (MEF ko su Linux) + ~~loader Linux-safe da cartella `plugins/`~~ ✅ (M27,
    reflection) + ~~un plugin reale~~ ✅ (M26, BackgroundFetch). Resta: portare
    gli altri plugin reali con UI (Statistics, FindLargeFiles, …).
11. **Temi**: ~~portare `GitUI/Theming`~~ → tema scuro + chiaro Avalonia con
    switch live (`ThemeManager`, M7/M8) + ~~persistenza~~ ✅ (M12). Resta:
    eventuali varianti/accenti aggiuntivi.

### Debito tecnico / pulizia
- **Sostituire gli shim con implementazioni vere** dove un percorso runtime li
  usa davvero (es. dialog, clipboard) — oggi molti shim sono no-op sufficienti a
  compilare ma non a funzionare pienamente.
- ~~**P/Invoke `kernel32` in `ProcessExtensions.cs`** (Ctrl+C ai processi git
  figli): fallback Linux~~ ✅ (M10, SIGINT via `libc kill`; latente, vedi sopra).
- ~~**Warning `NU1903`**: `Tmds.DBus.Protocol`~~ ✅ (M10, bump Avalonia 11.3.14).
- **Localizzazione**: verificare il caricamento delle traduzioni (`ResourceManager`)
  su Linux.
- ~~**Packaging**: `.deb` + `dotnet publish` self-contained~~ ✅ (M9,
  `packaging/build-deb.sh`). Resta opzionale: AppImage / Flatpak.

### Nota architetturale
Man mano che si aggiungono viste, valutare se conviene **aggiungere `net10.0` come
target multiplo direttamente nei csproj reali** (invece dell'albero separato),
usando `#if WINDOWS` per isolare il codice WinForms. L'approccio attuale (albero
separato + shim) è a rischio zero per la build Windows ma duplica la lista dei
sorgenti via glob; a un certo punto il multi-target potrebbe essere più pulito.

---

## Parità con Git Extensions

> **Iterazione: 19 / 20** · parità **98.1%** (157/160 voci `[x]`, invariata:
> loader e verifiche non sono voci checklist). Questo giro (M27): **plugin folder
> loader Linux-safe** (reflection puro, no MEF: scansiona
> `~/.config/GitExtensions.Avalonia/plugins/*.dll`, isola i fallimenti, dedupe by
> Id, built-in first) — testato con DLL reale/corrotta/cartella assente; +
> **verifica finale**: build 0 errori, `.deb` ricostruito (~37 MB), screenshot GUI
> completo (menu 8 voci, toolbar completa, avatar, tab inferiori). Prossimo: iter.
> 20 = riepilogo finale + stop.
> Riferimento originale: `src/app/GitUI` (FormBrowse, RepoObjectsTree,
> RevisionGrid). Stato: `[x]` fatto nel port Avalonia · `[ ]` mancante.

### A. Barra dei menu
- [x] Start ▸ Open repository
- [x] Start ▸ Clone repository
- [x] Start ▸ Create new repository (init)
- [x] Start ▸ Recent repositories (MRU)
- [x] Start ▸ Favorite repositories (add + submenu, persistito)
- [x] Start ▸ Exit
- [x] Dashboard (landing recent+favorite, close-to-dashboard + refresh)
- [x] Repository ▸ Refresh
- [x] Repository ▸ File Explorer (xdg-open)
- [x] Repository ▸ Remote repositories… (Remotes manager dal tree)
- [x] Repository ▸ Manage submodules / update / synchronize (dialog dal tree)
- [x] Repository ▸ Manage worktrees (dialog dal tree)
- [x] Repository ▸ Edit .gitignore / .gitattributes / exclude / mailmap
- [x] Repository ▸ Sparse working copy (git sparse-checkout)
- [x] Repository ▸ Git maintenance (gc / fsck / delete index.lock / edit config)
- [x] Repository ▸ Repository settings (riusa SettingsWindow)
- [x] Commands ▸ Commit
- [x] Commands ▸ Undo last commit (reset --soft HEAD~1, dal tab Working dir)
- [x] Commands ▸ Pull / Fetch
- [x] Commands ▸ Push
- [x] Commands ▸ Manage stashes
- [x] Commands ▸ Reset changes
- [x] Commands ▸ Clean working directory (dry-run + conferma)
- [x] Commands ▸ Create branch
- [x] Commands ▸ Delete branch
- [x] Commands ▸ Checkout branch
- [x] Commands ▸ Merge branches
- [x] Commands ▸ Rebase
- [x] Commands ▸ Solve merge conflicts (mergetool / ours-theirs / mark resolved)
- [x] Commands ▸ Create tag
- [x] Commands ▸ Delete tag
- [x] Commands ▸ Cherry pick
- [x] Commands ▸ Archive revision (git archive zip/tar.gz)
- [x] Commands ▸ Checkout revision
- [x] Commands ▸ Bisect (mark good/bad/skip/reset da grid)
- [x] Commands ▸ Show reflog (ReflogWindow)
- [x] Commands ▸ Format patch
- [x] Commands ▸ Apply patch
- [x] Commands ▸ View patch file
- [ ] Repository hosts (GitHub: fork / view-create PR / add upstream) — **SKIP**: realizzabile come plugin repository-host, fuori scope base
- [x] Plugins menu + Plugins settings (loader diretto + sample plugin + settings render)
- [x] Tools ▸ Git bash / Git GUI / GitK (PuTTY N/A su Linux)
- [x] Tools ▸ Git command log (legge core CommandLog)
- [x] Tools/Edit ▸ Settings (SettingsWindow Avalonia: identità git, pull, tema)
- [x] Help ▸ About
- [x] Help ▸ User manual / Changelog / Report issue / Donate (xdg-open; check-updates/translate/telemetry N/A)
- [x] View ▸ tema chiaro/scuro (toggle live)

### B. Toolbar
- [x] Refresh
- [x] Toggle left panel
- [x] Toggle split-view layout (stacked ↔ side-by-side)
- [x] Commit-info position (below/left/right)
- [x] Level-up / Submodules split button (dropdown + open)
- [x] Worktrees split button (dropdown + open)
- [x] Working directory / recent-repo picker (Open)
- [x] Branch select (checkout)
- [x] Pull (+ merge/rebase/fetch/fetch-all varianti)
- [x] Push
- [x] Commit
- [x] Stash (+ stash staged / pop / manage)
- [x] File Explorer
- [x] User shell selector (terminal in repo dir)
- [x] Settings/Edit button (apertura)
- [x] New branch
- [x] Fetch

### C. Pannello sinistro (RepoObjectsTree)
- [x] Nodo Branches + checkout/create/merge/rebase/reset/rename/delete/filter
- [x] Nodo Remotes + fetch/pull/push/manage
- [x] Nodo Tags + checkout/create-branch/merge/reset/delete
- [x] Nodo Stashes + apply/pop/open/drop/manage
- [x] Nodo Submodules + list/update/update-all/open (commit/reset da fare)
- [x] Nodo Worktrees + add/remove/prune/open
- [x] Ordinamento ref (sort-by name/data + asc/desc) + move up/down (branch)
- [x] Copy to clipboard (nome) / copy path (submodule/worktree)

### D. Revision grid
- [x] Colonna grafo DAG (multi-lane)
- [x] Colonna messaggio/oggetto
- [x] Colonna autore
- [x] Colonna data
- [x] Colonna commit id (SHA)
- [x] Colonna avatar (identicon offline da hash email, no rete — adattamento Linux)
- [ ] Colonna build status (icona/testo) — **SKIP**: richiede integrazione build-server/CI (fuori scope)
- [x] Colonna git notes (indicatore + tooltip, `git notes list` batch)
- [x] Menu contestuale: copy hash/subject/author
- [x] Menu contestuale: checkout commit / cherry-pick / reset (soft·mixed·hard)
- [x] Menu contestuale: revert commit (git revert --no-edit)
- [x] Menu contestuale: create branch/tag here
- [x] Menu contestuale: compare (select BASE / compare to BASE / working dir / difftool; compare-to-branch da fare)
- [x] Menu contestuale: bisect good/bad/skip/stop
- [x] Menu contestuale: navigate (parent/child/go-to; Alt↑/↓, Ctrl+G)
- [x] Filtro/ricerca (autore / messaggio / hash) — barra live in RevisionGridView
- [x] Quick-search da tastiera (type-to-jump, F3/Esc)
- [x] Drag &amp; drop (stage/unstage nel Working directory)
- [x] View toggles: mostra/nascondi colonne + scope branch (all/current/filtered)

### E. Dialoghi / comandi (Form*)
- [x] Blame
- [x] File history
- [x] Commit / staging (working directory)
- [x] Push
- [x] Pull / Fetch
- [x] Create branch
- [x] Checkout branch
- [x] Checkout revision
- [x] Create tag
- [x] Delete branch
- [x] Delete tag
- [x] Merge branch
- [x] Rebase
- [x] Cherry pick
- [x] Stash manager
- [x] Diff viewer
- [x] About
- [x] Credenziali (custom, per push/pull http)
- [x] Clone (CloneDialog: URL + folder picker)
- [x] Init (create new repository)
- [x] Remotes manager (add/edit-url/rename/remove)
- [x] Rename branch
- [x] Delete remote branch (git push --delete)
- [x] Revert commit
- [x] Reset changes (discard tracked, all + per-file, conferma)
- [x] Cleanup repository (git clean, M12)
- [x] Resolve conflicts
- [x] Merge submodule (submodule update --remote --merge)
- [x] Archive
- [x] Format patch / Apply patch / View patch (viewer colorato)
- [x] Add to .gitignore (path / *.ext / dir/, da file untracked)
- [x] Edit .gitignore / .gitattributes / .mailmap (xdg-open, M16)
- [x] Sparse working copy
- [x] Submodules manager (update/update-all/sync/init)
- [x] Reflog browser (copy hash / checkout)
- [x] Compare to branch (git diff branch..selected → DiffView)
- [x] Verify database / recover lost objects (git fsck, da Maintenance)
- [x] Settings (SettingsWindow — identità/pull/tema; fondazione estendibile)
- [x] Command log (CommandLogWindow)
- [x] Bisect UI (da grid)

### F. Operazioni git esposte
- [x] Commit (+ amend)
- [x] Commit: squash / fixup / reword / undo (edit interattivo: reword-older ok)
- [x] Push (`--force-with-lease`, non più `--force`)
- [x] Pull / Fetch (+ fetch all / prune)
- [x] Branch: create / checkout / delete
- [x] Branch: rename (git branch -m, da tree)
- [x] Merge
- [x] Rebase (⚠ non interattivo)
- [x] Cherry-pick
- [x] Revert
- [x] Reset (soft/mixed/hard da grid)
- [x] Clean working directory (git clean -fd, dry-run + conferma)
- [x] Stash: save / apply / pop / drop
- [x] Stash: stash staged (git stash push --staged)
- [x] Tag: create / delete
- [x] Tag: checkout tag revision (detached)
- [x] Bisect (good/bad/skip/reset)
- [x] Submodule ops (list/update/update-all; open da fare)
- [x] Worktree ops (list/add/remove/prune)
- [x] Archive
- [x] Patch (format/apply/view)
- [x] Remotes manage (add/rename/remove/set-url; add-upstream implicito)
- [x] Maintenance (gc / fsck / index.lock / config)
- [x] Clone / Init
- [x] Reflog
- [x] Blame / File history / Diff / Log
- [x] Compare (working dir / difftool / BASE; branch da fare)
- [ ] Repository-host (GitHub) ops — **SKIP**: come sopra (plugin host)

### Packaging / distribuzione
- [x] `.deb` self-contained (linux-x64) + `.desktop` + icona — `packaging/build-deb.sh`

### Debito tecnico noto (non conta ai fini parità UI)
- [x] Fallback Linux Ctrl+C ai git figli (ProcessExtensions → SIGINT via libc
      `kill`, guardato `OperatingSystem.IsWindows()`; latente: call-site solo in
      GitUI WinForms, fuori dal build cross-platform)
- [x] Bump Avalonia per NU1903 (Tmds.DBus) — 11.2.3 → 11.3.14, GUI verificata
- [x] Fix warning VSTHRD100 (async void) — 7→0 (VSTHRD200 5→0; restano 2 VSTHRD002
      in RemoteService, ripple su MainWindow, rinviati)
- [ ] Verifica traduzioni ResourceManager su Linux
- [ ] Shim no-op → implementazioni reali (dialog, clipboard)
- [x] Persistenza tema + dimensioni pannelli tra avvii (UiStateService, JSON in ~/.config)
- [x] Credenziali via credential-helper git (env-var, non injection URL, secret non loggato)

---

## Piano modello plugin (per iter. 17-18)

Ricerca su `GitExtensions.Extensibility` + `src/plugins`. Punti chiave:

- **Contratto già portabile**: `IGitPlugin`/`GitPluginBase` e i setting tipizzati
  (`BoolSetting`/`StringSetting`/`NumberSetting`/`ChoiceSetting`/`PathSetting`)
  **compilano già** sotto net10.0 nel port (`Core.Extensibility` + shim). Solo il
  *rendering* dei setting è WinForms (`ISettingControlBinding.GetControl()`) → va
  ignorato e sostituito con un mapper Avalonia sul tipo runtime del setting (come
  fa già `SettingsWindow`).
- **Discovery/loading**: VS-MEF (`Microsoft.VisualStudio.Composition`) è già
  referenziato e compila (`Core.GitUIPluginInterfaces` → `ManagedExtensibility`).
  Scansione cartella `plugins/` accanto all'eseguibile + `[Export(typeof(IGitPlugin))]`.
  Per il prototipo, preferire `ManagedExtensibility.Initialise(assemblies)`
  in-process (evita fragilità del file-scan / `Application.ExecutablePath` shim).
- **Adapter host**: `IGitUICommands` è enorme e WinForms-bound → implementare un
  `AvaloniaGitUICommands` minimo che espone solo `Module` (via `GitContext`),
  il resto lancia `NotSupportedException`. `GitUIEventArgs.OwnerForm = null`. La
  maggior parte dei plugin portabili usa solo `args.GitModule`.
- **Prototipo (slice minima)**: `PluginService` (porta `PluginRegistry` via
  `GetExports<IGitPlugin>()`), `AvaloniaGitUICommands`, `AvaloniaSettingsContainer`,
  `PluginSettingsWindow` (mapper setting→controllo), un `SampleGreetPlugin`
  built-in, menu **Plugins** in `MainMenu` + hook in `MainWindow` (run off-thread,
  `RefreshAll` se `Execute` → true; settings dialog).
- **Plugin reali classificati**: portabili subito → BackgroundFetch,
  AutoCompileSubmodules, Gource/ProxySwitcher (lanciano processi); da ridisegnare
  (UI WinForms) → Statistics, FindLargeFiles, BuildServerIntegration.
- **Stub/rischi**: `IGitUICommands.Start*Dialog` (throw), `ISettingControlBinding`
  (ignora), `Icon` (System.Drawing shim → icona default), `CredentialsSetting`/
  `PasswordSetting` (esclusi dal build → skip), translations (inglese).

---

## TODO prioritari — feedback visuale & UX (verso porting 1:1)

Obiettivo: **porting 1:1 delle funzionalità di base**. La struttura c'è (98,1%),
ma mancano feedback visuali che nell'originale sono centrali per l'usabilità.
Prossimo blocco di lavoro, in ordine di priorità:

- [x] **T1 — Dialog di esecuzione comando git** (come `FormProcess` dell'originale). ✅ M28
  Ogni azione che lancia un comando git deve aprire una finestra/dialog che mostra
  **il comando avviato** e **l'output in streaming**, con esito (successo/errore) e
  chiusura (auto-close opzionale su successo). Dà feedback tecnico immediato.
  *Dove*: creare un `GitCommandRunner`/`GitProcessDialog` condiviso e farci passare
  le operazioni dei service (RemoteService fetch/pull/push, BranchTag, Stash, ecc.)
  invece dell'esecuzione silenziosa. Il core `CommandLog` (già usato in
  CommandLogWindow, M23) può alimentare l'output.

- [x] **T2 — Toolbar dinamica / stateful** (colpo d'occhio sullo stato): ✅ M29 (render) + M30 (wiring)
  - [x] Push "acceso" con **badge numerico + freccia su** (`Push ↑N` in App.Accent)
    quando ci sono commit ahead; analogo Pull `↓N`. ahead/behind calcolati come in
    StatusBarView (`GetRemoteBranch` + `GetCommitCount`), aggiornati a ogni RefreshAll.
  - [x] Pulsante **Commit** che cambia colore per stato working dir: verde staged /
    arancio unstaged / dim pulito, con conteggio `Commit (N)` (verificato: `Commit (1)`
    arancio con 1 file unstaged).
  - [x] **Indicatore working directory** sulla toolbar: `nome — ~/path (branch)`,
    home collassata a `~`. (Nota residua T6: con molti pulsanti l'indicatore finisce
    a destra fuori vista a 1400px — spostarlo/allineare a destra con DockPanel.)
  *Fatto*: `MainToolbar.UpdateState(ahead,behind,staged,unstaged,repoPath,branch)` +
  hook `RefreshToolbarState()` chiamato da `RefreshAll` e `OpenRepository`.

- [x] **T3 — Commit come modal/dialog, NON tab.** ✅ M30. Nuovo `CommitDialog` modale
  (`ShowAsync(owner,repoPath,onCommitted)`) che ospita una `WorkingDirectoryView`
  (riuso 1:1 di staged/unstaged, stage/unstage, drag&drop, message, amend, commit),
  lanciato dal pulsante Commit di toolbar+menu. Tab "Working directory" mantenuto
  (basso rischio). Verificato in GUI. Oggi il porting apriva il commit
  come tab nel pannello inferiore: **sbagliato**. Nell'originale il Commit è un
  **dialog modale** dedicato. *Dove*: nuovo `CommitDialog` (modale) lanciato dal
  pulsante Commit della toolbar; riusa la logica di `WorkingDirectoryView`/
  `WorkingDirectoryService` (staged/unstaged, stage/unstage, drag&drop, message,
  amend, commit). Valutare se tenere anche il tab "Working directory" o rimuoverlo.

- [x] **T4 — Selezione commit molto più evidente** nella revision grid. ✅ M28. Oggi non si
  distingue bene la riga selezionata. *Dove*: `RevisionGridView` — highlight riga
  intera forte (brush `App.Selection`/accento, testo/contrasto adeguati), bordo o
  barra sul lato, mantenendo leggibilità del grafo DAG e dei badge.

- [x] **T5 — Multi-selezione di due commit → diff nel pannello inferiore.** ✅ M29
  Nell'originale selezionando due commit si vede la diff tra i due sotto. Oggi la
  grid è single-select e il compare è manuale (select BASE → compare). *Dove*:
  `RevisionGridView` selezione multipla (2 righe) → `MainWindow` mostra
  automaticamente `DiffView.ShowRange(a, b)` nel tab Commit/Diff.

- [ ] **T6 — (residue minori note in seguito)** altre differenze meno evidenti da
  raccogliere man mano confrontando con l'originale.

*Nota metodo*: T2/T3/T5 toccano gli "hub" MainWindow/MainToolbar/RevisionGridView —
un solo subagent per hub per iterazione (regola anti-conflitto del loop).

---

## Riepilogo finale (loop 20 iterazioni)

**Parità raggiunta: 157/160 = 98,1%** delle voci UI/funzionali di Git Extensions
(riferimento `src/app/GitUI`: FormBrowse, RepoObjectsTree, RevisionGrid, menu,
toolbar, dialoghi, comandi git). Build cross-platform: **0 errori**. Il `.deb`
self-contained si costruisce e il binario pubblicato supera il self-test.

### Voci residue (3, tutte SKIP consapevoli — restano `[ ]`)
- **Repository hosts (GitHub) ×2** — fork / view-create PR / add upstream. Fuori
  scope base: nel modello originale è un plugin repository-host; ora *realizzabile*
  come plugin Avalonia (l'infrastruttura plugin c'è, M25–M27), ma non incluso.
- **Colonna build status** — icona/testo dallo stato di build: richiede
  integrazione con un build-server/CI, fuori scope.

> ⚠️ **Nota fedeltà UX**: il 98,1% misura le *voci* di parità, ma restano gap di
> feedback visuale rispetto all'originale (dialog comando git, toolbar dinamica,
> commit come modale, evidenza selezione, diff da doppia selezione). Tracciati in
> **"## TODO prioritari — feedback visuale & UX"** — prossimo blocco verso il 1:1.

### Struttura UI (parità con FormBrowse)
Finestra integrata: **barra menu** (File/Edit/View/Repository/Commands/Tools/
Plugins/Help) · **toolbar** (Open/Fetch/Pull/Push/Commit/Stash/Refresh/New branch/
Submodules▾/Worktrees▾/Split view/Commit info▾/File Explorer/Terminal) · **albero
sinistro** (Branches/Remotes/Tags/Stashes/Submodules/Worktrees + menu contestuali)
· **revision grid DAG** (colonne graph/avatar/hash/autore/data/oggetto + git-notes,
filtro, quick-search, Go-to/Branches/View/Date/Columns) · **pannello inferiore a
tab** (Commit=dettaglio+diff / Working directory / Stash / Blame / File history)
· **status bar**. Tema chiaro/scuro con persistenza.

### Milestone
- M1–M8 — fondazione: core portabile net10.0, shell Avalonia, slice verticale,
  4 viste ad alta priorità, grafo DAG, operazioni git (branch/remote/stash/blame),
  restyle FormBrowse (toolbar+albero+grid+tab), barra menu + tema live + About.
- **M9** — packaging `.deb` self-contained (`packaging/build-deb.sh`) + `.desktop`
  + icona.
- **M10** — debito tecnico: Ctrl+C SIGINT su Linux, bump Avalonia 11.3.14 (NU1903),
  fix VSTHRD100 async-void.
- **M11** — push `--force-with-lease`, credenziali via credential-helper (no URL
  injection, segreto non loggato), filtro/ricerca grid.
- **M12** — persistenza tema+dimensioni pannelli, create branch/tag da grid,
  undo last commit, clean working directory.
- **M13** — SettingsWindow (identità git/pull/tema), rename branch, stash staged.
- **M14** — Clone/Init, nodo Submodules, grid git-notes+toggle data+show/hide colonne.
- **M15** — revert+archive commit, remotes manager, difftool+compare-to-working-dir.
- **M16** — menu Repository/Tools/Help (file explorer, editor dotfile, gitk/git-gui,
  link), nodo Worktrees + checkout tag, grid navigate (parent/child/go-to).
- **M17** — reflog browser, bisect, resolve conflicts, grid scope branch.
- **M18** — compare commits (BASE/working-dir), tree sort/copy, add-to-.gitignore.
- **M19** — commit-edit (reword/squash/fixup), grid view-toggles, stash message+diff.
- **M20** — toolbar (split-view/commit-info-position/file-explorer/shell) + stash
  tab, submodules manager dialog, reset changes.
- **M21** — favorite repos + dashboard + git maintenance (gc/fsck/lock/config),
  delete remote branch + merge submodule, grid quick-search.
- **M22** — patch (format/apply/view), manage worktrees dialog, drag&drop stage/unstage.
- **M23** — command log (core CommandLog), compare-to-branch, sparse working copy.
- **M24** — toolbar Submodules/Worktrees split-button + Open come repo attivo.
- **M25** — modello plugin Avalonia (loader diretto, sample plugin, menu Plugins,
  settings render senza ISettingControlBinding).
- **M26** — plugin reale BackgroundFetch (valida il modello) + colonna avatar offline.
- **M27** — plugin folder loader Linux-safe (reflection, no MEF) + verifica finale.

#### Blocco FEDELTÀ UX (loop dedicato, contatore iterazioni)
- **M28** (iter. 1/10) — **T1** GitProcessDialog: runner/dialog modale condiviso
  (comando + output da core CommandLog in polling + esito successo/errore +
  auto-close opzionale), wiring di RemoteService fetch/pull/push in MainWindow
  (`RunRemoteOp`). **T4** highlight riga selezionata forte nella revision grid
  (fill App.Selection + barra accento sinistra App.Accent, testo App.Text ad alto
  contrasto, `:selected:pointerover`), grafo DAG/badge intatti. Verificati in GUI.
- **M29** (iter. 2/10) — **T5** multi-selezione 2 commit nella grid (`SelectionMode.Multiple`,
  evento `RangeSelected(older,newer)`) → MainWindow chiama `DiffView.ShowRange` nel tab
  Commit + hint status bar; verificato in GUI (2 righe con barra accento, tab Commit).
  **T2 (parte rendering)** — `MainToolbar.UpdateState(ahead,behind,staged,unstaged,repoPath,
  branch)`: badge Push ↑N / Pull ↓N in App.Accent, colore Commit (verde staged / arancio
  unstaged / dim pulito), indicatore repo `nome — ~/path (branch)`. API pronta; **wiring su
  RefreshAll da fare in iter 3** (hub MainWindow, insieme a T3).
- **M30** (iter. 3/10) — **T3** `CommitDialog` modale (Window che ospita una
  `WorkingDirectoryView`, riuso 1:1; `ShowAsync`) lanciato da pulsante Commit di
  toolbar+menu al posto della selezione del tab. **T2 wiring** — `RefreshToolbarState()`
  fire-and-forget su `RefreshAll`/`OpenRepository`: calcola ahead/behind (come
  StatusBarView) + staged/unstaged (`WorkingDirectoryService.LoadStatus`) e chiama
  `MainToolbar.UpdateState`. Verificati in GUI: `Commit (1)` arancio, modale con
  staged/unstaged/message/amend/commit.

### Riepilogo blocco FEDELTÀ UX (loop dedicato, 3 iterazioni)
**T1–T5 chiuse e verificate in GUI** (M28–M30, 3 iterazioni, 5 subagent claude in
worktree isolati, file disgiunti, cherry-pick+build+screenshot per pezzo):
- **T1** GitProcessDialog: dialog stile FormProcess (comando + output da core
  CommandLog + esito + auto-close) su fetch/pull/push.
- **T2** Toolbar dinamica: badge Push↑/Pull↓, colore/conteggio Commit per stato
  working dir, indicatore repo `nome — ~/path (branch)`, aggiornati a ogni refresh.
- **T3** Commit MODALE (non più tab), riuso completo di WorkingDirectoryView.
- **T4** Highlight riga selezionata forte (fill Selection + barra accento sinistra).
- **T5** Doppia selezione commit → diff automatica nel tab Commit (`DiffView.ShowRange`).

**Residue (T6, non bloccanti)** — differenze minori da rifinire in futuro:
- Indicatore repo in toolbar può finire fuori vista a destra con larghezza ridotta
  (allineare a destra con DockPanel invece di append allo StackPanel).
- Streaming output di GitProcessDialog è a polling del CommandLog (riga comando +
  output finale), non char-by-char come il ConsoleOutputControl originale.
- Il tab "Working directory" resta accanto al Commit modale (ridondanza tollerata).

## TODO round 2 — fedeltà visiva 1:1 (da screenshot originale Windows)
Confronto con screenshot dell'originale (Documents/process dialog…). Aree scelte
dall'utente: **Modali · Griglia revisioni · Menu+toolbar** (tab inferiori rimandati).

Modali:
- [x] **U-PROC** ✅ M31 — Process dialog stile originale: console beige/tan monospace, header
  "Command to be executed:" + comando git completo, "Current directory:", footer con
  checkbox **Keep dialog open** + **OK** + **Abort**, titolo "Process (path)" con check.
- [x] **U-COMMIT** ✅ M32 — Commit dialog layout 3-zone: unstaged (alto-sx) + staged (basso-sx)
  con pulsanti Stage/Unstage a freccia, **diff del file a destra**, messaggio+bottoni
  in basso (Commit, Commit & push, Amend, Reset all/unstaged, Commit templates,
  Create branch, Options), status bar committer + "Staged x/y Ln Col".
- [x] **U-PUSH** ✅ M32 — Nuovo Push dialog di configurazione: Push to Remote/Url + Manage
  remotes, tab Push branches/tags/multiple, Branch to push → to, Show options
  (force/tags/recursive), bottoni Pull + Push.

Griglia:
- [x] **U-GRID-BADGE** ✅ M31 — Ref come pill *outline* stile originale (bg chiaro, bordo+testo
  colorati: branch verde, remote rosso/rosa, tag, stash) invece dei pill pieni scuri;
  branch corrente **grassetto + ▶** e nodo quadrato pieno.
- [x] **U-GRID-DATE** ✅ M31 — Default date **relative** ("2 hours ago").
- [ ] **U-GRID-TOPROWS** Righe artificiali in cima: **Working directory** + **Commit
  index** con check verde, cliccabili (mostrano il lavoro pendente).
- [ ] **U-GRID-SEL** Selezione riga piena blu forte (già parzialmente fatto T4).

Menu + toolbar:
- [x] **U-MENU** ✅ M31 — Top-level come originale: **Start · Repository · Navigate · View ·
  Commands · GitHub · Plugins · Tools · Help** (rinominare File→Start, aggiungere
  Navigate e GitHub, riparentare Edit/Settings; wiring eventi invariato).
- [ ] **U-TOOLBAR** Dropdown path repo + dropdown branch inline nella toolbar, combo
  All branches / Branches / Filter.

### Milestone round 2 (fedeltà visiva)
- **M31** (iter. 4) — **U-MENU** menu top-level ristrutturato in Start · Repository ·
  Navigate · View · Commands · GitHub · Plugins · Tools · Help (File→Start, Edit rimosso
  e voci riparentate, GitHub placeholder disabilitato). **U-GRID-BADGE** ref come pill
  *outline* (bg chiaro adattivo, bordo+testo colorati: branch verde/remote rosso/tag
  ambra), branch corrente **grassetto + ▶** verde (via `RevisionRow.IsHead`).
  **U-GRID-DATE** default date relative. **U-PROC** process dialog stile originale
  (console beige `#ECE9D8`, "Command to be executed:", footer Keep-dialog-open + OK +
  Abort, ✔ verde su successo). Tutti verificati in GUI (screenshot).

- **M32** (iter. 5) — **U-COMMIT** CommitDialog ricostruito 3-zone (liste unstaged/staged
  + Stage/Unstage/all a sinistra, **diff del file selezionato** a destra via `git diff
  [--cached]`, messaggio+bottoni sotto: Commit, Commit & push, Amend, Reset all/unstaged;
  Stash/templates/Create branch/Options placeholder v1); parla diretto a
  WorkingDirectoryService (tab "Working directory" invariato). **U-PUSH** nuovo PushDialog
  di config (Remote/Url + Manage remotes, tab Push branches/tags/multiple, Branch→to,
  Show options: force-with-lease/tags/recursive, Pull+Push), push/pull via GitProcessDialog;
  MainWindow apre il dialog invece del push immediato. Verificati in GUI.
- **M33** (iter. 6, in corso) — streaming output *vero* nel process dialog.

### Come costruire il pacchetto `.deb`
```bash
cd src/crossplatform
export PATH="$HOME/.dotnet:$PATH"
bash packaging/build-deb.sh
# → packaging/out/gitextensions_5.0.0-linux1_amd64.deb  (self-contained, dip: git)
sudo apt install ./packaging/out/gitextensions_5.0.0-linux1_amd64.deb
```

### Nota sulla build Windows
NON toccata. Tutto il lavoro è in `src/crossplatform/` (albero separato + shim);
la soluzione Windows originale non lo referenzia → rischio zero. Unica modifica al
sorgente condiviso: guardie `OperatingSystem.IsWindows()` in `AppSettings.cs`
(registro) e in `ProcessExtensions.cs` (Ctrl+C → SIGINT), a comportamento Windows
invariato.

### Metodo del loop
20 iterazioni, ognuna: scelta pezzo → delega a subagent Claude in worktree isolati
(2–3 paralleli, file disgiunti) → cherry-pick dei tip + build check → integrazione
minima in MainWindow → verifica GUI headless (xvfb + screenshot) → commit
(Conventional, senza firma) → aggiornamento di questa checklist. Regola anti-conflitto:
un solo subagent per iterazione tocca ciascun file "hub" (MainWindow/MainMenu/
MainToolbar/RepoObjectsTree/RevisionGridView/DiffView/WorkingDirectoryView/StashPanel).
