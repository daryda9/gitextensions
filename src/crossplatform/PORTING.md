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
- ~~Streaming output non char-by-char~~ → RISOLTO in M33 (streaming stdout+stderr live).
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
- [x] **U-GRID-TOPROWS** ✅ M34 — Righe artificiali in cima: **Working directory** + **Commit
  index** con check verde, cliccabili (mostrano il lavoro pendente).
- [x] **U-GRID-SEL** ✅ M40 — Selezione riga piena blu forte a tutta larghezza, testo
  bianco, pill e grafo DAG resi leggibili sopra il fill.

Menu + toolbar:
- [x] **U-MENU** ✅ M31 — Top-level come originale: **Start · Repository · Navigate · View ·
  Commands · GitHub · Plugins · Tools · Help** (rinominare File→Start, aggiungere
  Navigate e GitHub, riparentare Edit/Settings; wiring eventi invariato).
- [x] **U-TOOLBAR** ✅ M35 — Dropdown path repo + dropdown branch inline nella toolbar, combo
  All branches / Branches / Filter.

### Credenziali / push (fix post-round-3)
- **M38** — push dalla GUI chiedeva il login **sul terminale** di avvio e falliva.
  Cause: `GitStreamRunner` eseguiva git ereditando il tty senza disabilitare i prompt,
  e nessun retry credenziali nel path GUI. Fix: (a) git strettamente non-interattivo
  (`GIT_TERMINAL_PROMPT=0`, `GCM_INTERACTIVE=never`, stdin chiuso) + askpass ereditato
  neutralizzato (`GIT_ASKPASS=""`, `SSH_ASKPASS=""`, `SSH_ASKPASS_REQUIRE=never`, la
  sessione desktop impostava `SSH_ASKPASS=ssh-askpass` inesistente → "cannot run
  ssh-askpass"); (b) su auth-failure il process dialog si auto-chiude
  (`closeOnAuthFailure`) e appare il `CredentialsDialog` in-app, che ritenta l'op con
  le credenziali via credential-helper transitorio (push/pull in PushDialog,
  fetch/pull in MainWindow); (c) **persistenza**: dopo un'op riuscita con credenziali
  inserite nel dialog, `RemoteService.ApproveCredentials` le passa a
  `git credential approve` (stdin, mai in command line) → finiscono nell'helper reale
  configurato dall'utente, così i push successivi sono silenziosi come con GCM su Windows.
  *Nota ambiente*: su Linux git non ha helper di default (su Windows Git-for-Windows
  installa GCM) → configurato `git-credential-libsecret` (compilato dal contrib di git,
  in `~/.local/bin`) su gnome-keyring, testato con round-trip approve→fill.

### Priorità aperte (P1–P3) e coda di lavoro

> Dettaglio operativo dei punti elencati in `HANDOFF.md` sezione 5. **P1–P3 sono priorità
> indicate dall'utente il 27/07/2026**, confrontando la GUI con gli screenshot dell'originale
> Windows in `~/Documents/process dialog with terminal command/GUI.png` e
> `~/Documents/pullù/` (`button.png`, `menu.png`, `pull dialog.png`).
> I riferimenti `file:riga` valgono per l'albero al momento della stesura: verificarli.
>
> **STATO al 27/07/2026 (round 8, M50)**: **P1 CHIUSA**, **P3 CHIUSA**, **P2 chiusa per 2a+2b**
> (barra pulsanti + ricerca nella colonna sinistra, icone nei tab); **restano aperti 2c e 2d**
> (altri pulsanti/split-button della toolbar in alto, toolbar ricca della lista file, opzioni del
> viewer diff). Dettaglio in "Blocco PRIORITÀ P1–P3 (round 8)". I punti 4–6 qui sotto sono ancora
> aperti.

1. [PRIORITA' UTENTE] GRAFO: nell'originale, evidenziando un commit restano colorate solo le
   lane del percorso che porta a quel commit, il resto diventa GRIGIO. Due meccanismi separati:
   - HighlightSelectedBranch() (RevisionGridControl.cs:3062) si attiva su ALT+CLIC
     (OnGridViewMouseClick:1900) o dal comando ToggleHighlightSelectedBranch, NON sulla
     selezione semplice; chiama MakeRelative() che risale i PARENT marcando IsRelative.
   - il disegno usa GetBrushForLaneInfo(laneInfo, isRelative, drawStyle)
     (Graph/Rendering/GraphRenderer.cs:64): lane non-relative in grigio; a parte,
     RevisionDataGridView.cs:352 ingrigisce il TESTO se AppSettings
     .RevisionGraphDrawNonRelativesGray e' attiva.
   Nel port: _drawNonRelativesGray esiste (RevisionGridView.cs:2705) ma calcola i parenti
   rispetto a HEAD invece che al commit selezionato, e ingrigisce SOLO il testo: le lane
   restano sempre colorate. Da fare: relatives dalla selezione + lane grigie + Alt+clic.
   DECISO DALL'UTENTE (27/07/2026): FEDELE all'originale. Quindi:
   - all'apertura il riferimento e' HEAD (upstream: ApplyFlags(isCheckedOut: HeadId == ...)
     marca HEAD e AddParent propaga agli antenati; il setting
     RevisionGraphDrawNonRelativesGray e' true di default, quindi il grigio si vede subito);
   - ALT+CLIC su una riga sposta il riferimento a quel commit; il clic NORMALE non cambia
     nulla e NON deve ri-ancorare l'ingrigimento;
   - da ingrigire sono le LANE del grafo oltre al testo (oggi il port fa solo il testo).

2. [PRIORITA' UTENTE] CHROME MANCANTE rispetto all'originale:
   a) COLONNA SINISTRA: l'originale ha una barra di 5 pulsanti icona sopra l'albero e una
      CASELLA DI RICERCA con lente; RepoObjectsTree.cs non ha ne' l'una ne' l'altra.
   b) TAB del pannello inferiore: nell'originale hanno le ICONE (Commit/Diff/File tree/GPG/
      Console/Output); nel port sono solo testo (MainWindow.cs:48-56, IconLoader gia' esiste).
   c) TOOLBAR in alto: l'originale ha piu' pulsanti e split-button di quelli portati.
   d) Gia' noti dall'audit e ancora aperti: toolbar ricca della lista file (raggruppamento
      per path/estensione/stato, casella di ricerca, toggle ignorati/skip-worktree/untracked
      - FileStatusList.Toolbar.cs) e opzioni del viewer (evidenziazione sintattica, copia
      versione nuova/vecchia - FileViewer.Designer.cs:27-48).

3. [PRIORITA' UTENTE, 27/07/2026] PULL: split-button + menu + dialogo dedicato.
   Screenshot dell'originale in ~/Documents/pull/ (button.png, menu.png, pull dialog.png)
   -- la cartella reale si chiama "pullù".
   Nell'originale `toolStripButtonPull` e' un ToolStripSplitButton
   (FormBrowse.Designer.cs:50): il corpo esegue l'AZIONE PREDEFINITA (tooltip
   "Pull - merge (F8)"), la freccia apre un menu con:
     Open pull dialog... (Ctrl+Down) | Pull - merge | Pull - rebase | Fetch | Fetch all |
     Fetch and prune all | --- | Set default Pull button action > (sottomenu)
   L'azione predefinita e' persistita in AppSettings.DefaultPullAction
   (AppSettings.cs:1008, enum GitPullAction in
   GitExtensions.Extensibility/Git/GitPullAction.cs: None/Merge/Rebase/Fetch/FetchAll/
   FetchPruneAll/Default; default = Merge).
   Il "pull dialog" (FormPull) contiene: Pull from (Remote con combo + Manage remotes /
   URL con Browse), Branch (Local branch in sola lettura + Remote branch), Merge options
   (merge / rebase / solo fetch), Tag options (follow tagopt / no tag / all tags),
   Prune remote branches, Prune remote branches and tags, e in fondo
   Solve conflicts | Stash changes | Auto stash | Pull, piu' un pannello di aiuto con lo
   schema visuale (l'illustrazione si puo' omettere).
   NEL PORT: il Pull e' un pulsante semplice senza freccia (MainToolbar.cs:258) e chiama
   PullStreaming(..., rebase: false, ...) con il flag CABLATO A FALSE in due punti
   (MainWindow.cs:964 dal toolbar e :1082 dal menu), quindi non esiste ne' il dialogo, ne'
   la scelta merge/rebase/fetch, ne' un'azione predefinita configurabile. Nel MainToolbar
   non esiste ancora nessuno split-button: la struttura va creata (attenzione alla trappola
   nota: gli Items del MenuFlyout vanno popolati PRIMA di ShowAt).
   Nota: il punto 5 elencava gia' "dialogo Pull con prune/autostash/tag policy" come minore;
   e' assorbito qui e promosso.

4. Code da M48: push del branch dal menu grid, edit commit e rebase interattivo (serve un
   harness GIT_SEQUENCE_EDITOR), SelectRefInLeftPanelRequested da cablare, e le gesture
   Ctrl+M merge / Ctrl+Shift+E rebase / Ctrl+Alt+W worktrees (i service esistono, manca
   l'entry point in MainWindow).

5. Difetti noti aperti (la perdita di scroll/selezione al refresh e' RISOLTA in M49):
   CheckoutBranchDialog e' misto italiano/inglese; Avalonia non espone WM_DELETE_WINDOW
   (finestra non chiudibile dal WM); il "salva come" del diff non e' verificabile headless
   (serve portal XDG).

6. Minori: opzioni di merge/cherry-pick, continue/skip/abort per rebase, filtrare i
   cataloghi .xlf a inglese + italiano per togliere ~19 MB dal .deb.

**Resto della coda** (dettaglio in `HANDOFF.md` sezione 5, punti 4–6): code lasciate da M48
(push del branch dal menu grid, edit commit e rebase interattivo — servirebbe un harness
`GIT_SEQUENCE_EDITOR` che il port non ha, `SelectRefInLeftPanelRequested` da cablare, gesture
`Ctrl+M`/`Ctrl+Shift+E`/`Ctrl+Alt+W` senza entry point in `MainWindow`); difetti noti aperti;
minori (opzioni merge/cherry-pick, continue/skip/abort per rebase, filtro dei cataloghi
`.xlf` a inglese + italiano per togliere ~19 MB dal `.deb`).

### Coda round 9 — audit completo di parità, 8 aree

> **Iterazione 1 (FASE 1).** Otto subagent READ-ONLY in parallelo su aree disgiunte
> (A barra menu, B toolbar, C RepoObjectsTree, D revision grid, E pannello inferiore,
> F dialoghi, G impostazioni, H chrome globale), ognuno contro i file upstream corrispondenti
> con `file:riga`, costo e verifica che il dato/servizio esista già nel port. Base:
> `a3aae6d0b`, build Errori 0. Fuori scope per direzione dell'utente: lingue/traduzioni,
> repository-host GitHub, colonna build status.
>
> **Dove gli audit hanno stabilito che NON c'è lavoro** (per non inventarlo): la **status bar**
> — `FormBrowse` upstream *non ha* una status bar (`FormBrowse.Designer.cs:1354`
> `BottomToolStripPanelVisible = false`), quindi la `StatusBarView` del port è un extra da
> tenere; il **drag&drop di cartella**, già coperto a livello finestra; lo scope hotkey
> **`FormBrowse`**, in parità *verbatim* 43/43 gesture più la priorità focused-first; il
> **tab inferiore persistito** (upstream non lo persiste); **larghezza/ordine delle colonne**
> della griglia e **testo dei filtri** (upstream non li persiste); **drag&drop e inline rename**
> nell'albero sinistro (non esistono upstream); il **menu Plugins** (fedele, manca solo il
> placeholder "Loading…"); la **Console** (confermata la tesi di M51: nessuna toolbar upstream);
> le 4 azioni del **MaintenanceDialog** (1:1 col submenu `&Git maintenance`; non esiste una
> feature `git maintenance` upstream, solo `gc` + `fsck`); il **dialogo dei filtri di revisione**
> (completo, con un extra `-S`/`-G`); la **funnel icon a 2 stati** con summary del filtro.
>
> **~35 impostazioni upstream sarebbero pulsanti finti** se portate così come sono (nessun
> consumatore nel port): blocco avatar-provider/cache, `UseGitColoring`/ruler/continuous-scroll/
> all-parents, `OutputHistoryDepth`, i font (finché restano hard-coded), i flag di rendering del
> grafo, e tutto ciò che dipende da feature assenti (stash count, ahead/behind per ref, submodule
> status, script utente, revision links). Registrato per non spenderci iterazioni.

**BLOCCO 0 — correttezza** (fanno *la cosa sbagliata*, non sono controlli mancanti; costo basso,
priorità massima)

- [x] 0.1 `PatchService.cs:70-84` esegue **sempre** `git am` e sul fallimento lancia
      `git am --abort` → **distrugge una sessione `am` altrui in corso**. Upstream sniffa il file
      (`FormApplyPatch.cs:216-229 IsDiffFile`) e scegle tra `git apply` e `git am`. *banale*
- [x] 0.2 `GitProcessDialog.cs:132` — **Abort chiude solo la finestra**: git continua a girare e
      l'`index.lock` resta orfano. Upstream: `KillCommandProcess()` + `UnlockIndex(includeSubmodules)`
      (`FormStatus.cs:260-272`). *media*
- [x] 0.3 `FileHistoryView.cs:399` — **"Save as" salva il blob sbagliato** (o fallisce) per ogni
      commit anteriore a un rename: `--follow` è attivo (`FileHistoryService.cs:104`) ma il path
      usato è quello *attuale*. Upstream: `GetFileNameForRevision` (`FormFileHistory.cs:225-235`).
      Serve un campo nome-file in `FileHistoryRow`. *media*
- [x] 0.4 `PushDialog.cs:198-207` — la combo **"Remote branch" è popolata con i rami LOCALI** e
      non si riaggiorna al cambio remote → crea rami remoti sbagliati in silenzio. Upstream:
      rami remoti del remote selezionato (`FormPush.cs:756-774`). *media*
- [x] 0.5 `ConsoleView.RepoPath` **non è mai assegnato** da nessuno: dopo aver aperto un altro
      repo la shell resta nel repo d'avvio, e nemmeno "Restart shell" la sposta. Upstream fa
      `cd` sulla shell viva a ogni cambio working dir (`FormBrowse.cs:2777-2785`, da `:1732`). *banale*
- [x] 0.6 La combo **"Default pull action" di Settings è un pulsante finto**: scrive
      `AppPreferences.DefaultPullAction` (`SettingsWindow.cs:373`) mentre lo split-button Pull
      legge `UiState.DefaultPullAction`. Unificare su `UiState` ed esporre anche
      FetchAll/FetchPruneAll. *banale*
- [x] 0.7 **Left panel collassato: la larghezza si perde.** `ToggleLeftPanel` porta `_treeCol.Width`
      a 0, alla chiusura si salva 0 e `Sanitize` (`UiStateService.cs:161`) lo clampa a 260 → al
      riavvio il pannello riappare con larghezza di default. Serve un flag "collapsed" separato
      dalla larghezza. *banale*
- [x] 0.8 `PushDialog.cs:823-826` force-pusha i **tag** con `--force-with-lease`, che i tag non
      supportano (upstream usa `--force` puro, `FormPush.cs:496`). Dipende dal force a 3 stati. *banale*
- [x] 0.9 `WorktreesDialog.cs:127` offre **Remove sul worktree corrente/principale** (esclude solo
      i bare) e git fallisce. Upstream gating a `FormManageWorktree.cs:126-149`. *banale*
- [x] 0.10 `RemotesDialog.cs:211-231` **inghiotte tutti gli errori git** (tiene solo `Success`).
      Upstream mostra `result.UserMessage` (`FormRemotes.cs:531-535,603-607`). *banale*
- [x] 0.11 `OutputView` — **il log non è live**: `CommandLog.CommandsChanged` non è sottoscritto da
      nessuna parte (0 hit in `App/`), quindi un comando in volo resta `running` senza durata
      finché non si clicca Refresh. Upstream si iscrive e disiscrive (`FormGitCommandLog.cs:38-58`).
      `GitCommands.Logging.CommandLog` è già referenziato. *banale*
- [x] 0.12 `CommitActionsService.cs:50-63` scrive `COMMIT_EDITMSG` **sempre UTF-8** → messaggi
      corrotti con `i18n.commitEncoding` non-UTF8. Upstream usa `Module.CommitEncoding`. *banale*
- [x] 0.13 `SubmodulesDialog` — il pulsante **"Init all" chiama `UpdateAll`**: duplicato di
      "Update all" con etichetta fuorviante (upstream non ha "Init all"). *banale*
- [x] 0.14 `BlameView.ShowBlame` (`:274-301`) — **nessuna cancellazione del blame in volo**
      (`Task.Run` nudo, `CancellationToken.None` a `BlameService.cs:76`): due cambi di file rapidi
      possono lasciare le righe di A sotto lo status di B. *banale*
- [x] 0.15 Mancano **tutte le conferme distruttive** dei dialoghi: amend "rewrite history"
      (`FormCommit.cs:1098-1111`), merge-commit vuoto (`:1113-1123`), detached-HEAD (`:1191-1231`),
      branch nuovo sul remote (`FormPush.cs:291-310`), nome remote duplicato
      (`FormRemotes.cs:464-483`). *banale ciascuna*

- [x] 0.16 **La finestra non risponde a `WM_DELETE_WINDOW`**: la toplevel Avalonia non espone il
      protocollo in `WM_PROTOCOLS` e ignora il ClientMessage → con un window manager reale la "X"
      della decorazione **non chiude l'app** e `PersistLayout()` non gira, quindi *tutto* lo stato
      UI (geometria, splitter, tab, collasso, pull action) si perde. Oggi solo `Start → Exit` passa
      da `Closing`/`PersistLayout`. Scoperto durante la verifica GUI di M52. **Non è una
      regressione**: è il limite di piattaforma già censito nei blocchi precedenti (Avalonia non
      espone `WM_DELETE_WINDOW` su X11). La novità è la *conseguenza*, che prima non era stata
      collegata: non è solo "la X non chiude", è che **si perde tutto lo stato UI**. Se il
      protocollo non è registrabile dal lato managed, la via è un ricevitore nativo come si è già
      fatto per XDND (`Services/X11DropTarget.cs`). *valore alto*
      > **RISOLTO in M58, e la diagnosi storica era SBAGLIATA.** Avalonia implementa benissimo il
      > protocollo di chiusura. Il difetto è un argomento in `Avalonia.X11.X11Atoms.PopulateAtoms`
      > (11.3.14): `XInternAtoms(..., only_if_exists: true, ...)`, cioè "solo se qualcuno li ha già
      > creati". Su un server dove nessun client precedente l'ha fatto — **cioè su Xvfb nudo** —
      > tutte e 78 le lookup tornano 0 e **l'intera tabella degli atomi resta azzerata**:
      > `XSetWMProtocols` pubblica l'atomo `0` (osservato: `WM_PROTOCOLS raw=[0]`) e il lato
      > ricezione è morto perché il primo test dell'handler è `message_type != Atoms.WM_PROTOCOLS`.
      > Ne cadono anche tutte le `_NET_WM_*` e gli atomi della clipboard.
      > **Conseguenza sul metodo, non solo sul codice**: tutte le sessioni headless di questo
      > progetto girano su Xvfb nudo, quindi **ogni verifica passata di maximize, window type o
      > clipboard ha misurato un ambiente storpio**, non il comportamento su un desktop vero.
      > Corollario: su un desktop reale, dove altri client hanno già creato quegli atomi, la "X"
      > molto probabilmente **funzionava già**.
- [x] 0.17 `RevisionsStar`/`BottomStar` vengono salvati come valori **simil-pixel** (es. 199 / 525)
      invece di rapporti star normalizzati: passano il `Clamp(0.1, 1000)` di `Sanitize` quindi non
      rompono nulla, ma il ripristino dipende dalla dimensione della finestra. *banale*
- [x] 0.18 Nel tab **File tree** il clic (singolo e doppio) su un file **non carica il Blame**
      (resta "No file loaded"); il Blame si raggiunge solo dal tab Diff col tasto destro. Coerente
      con 2.1: il File tree è una lista piatta di stringhe. *si chiude con 2.1*

- [x] 0.19 **CRASH da re-entrancy nella griglia** (regressione introdotta in M53). Cablare
      "Filter file in grid" a `ApplyRevisionFilter` fa esplodere
      `InvalidOperationException: Cannot change source while update is in progress`, con lo stack
      tutto interno: `ApplyRevisionFilter → Reload → ItemsSource → SelectionChanged →
      UpdateAuthorHighlight → RefreshView → RebindRows → ItemsSource`. Causa: l'autore in grassetto
      reagisce a `SelectionChanged` e ri-assegna `ItemsSource` mentre l'assegnazione precedente è
      in corso; rinviare al dispatcher **non** basta. Serve una guardia di re-entrancy vera.
      *Finché non è chiuso, la voce 1.24 resta scablata.*
- [x] 0.20 **Sospetto**: un path filter applicato dal dialogo "Filter…" lascerebbe la griglia con
      **tutti** i commit. Osservato di sfuggita mentre si indagava 0.19, non confermato: da
      verificare contro `git log` reale prima di trattarlo come difetto.
- [x] 0.21 Il logo `GitExtensionsLogoWide.png` esiste in `setup/assets/Logo/` ma è **fuori dal
      glob** del csproj (`src/app/GitUI/Resources/Icons/*.png`), quindi non è risolvibile come
      `avares:`. Servono due cose insieme: la riga nel csproj **e** il codice che lo carica; oggi
      la dashboard tiene il wordmark testuale invece di sostituire artwork a caso. *banale*

**Difetti trovati dalla verifica GUI di M53–M56** (sessioni :281–:287, tutti visti a schermo)

- [x] 0.22 **Il `CommitDialog` non ha alcun pulsante di chiusura**, ignora Esc e ignora
      `WM_DELETE_WINDOW`: una volta aperto non è dismissibile in alcun modo sintetico. Upstream ha
      `Cancel` (`FormCommit.Designer.cs:142-151`). *grave, banale*
- [x] 0.23 **Nessuno dei tre dialoghi del menu Repository si chiude con Esc** (Remotes,
      Submodules, Worktrees): solo il loro pulsante `Close`. Anche il `WM_DELETE_WINDOW`
      sintetico è ignorato (stessa radice di 0.16). *banale*
- [x] 0.24 **Selezionare una riga della griglia riporta a forza il pannello inferiore sul tab
      Commit**: non si può tenere aperto Output, Diff o File tree mentre si naviga la storia.
      Upstream aggiorna il tab visibile senza cambiarlo. Il colpevole è la riga
      `_bottom.SelectedItem = _commitInfoTab;` in `OnRevisionSelected`. *banale, alta seccatura*
- [x] 0.25 **Titolo stantio dopo `Close (go to Dashboard)`**: resta `<repo> (<branch>)` mentre a
      schermo c'è la dashboard (`RefreshToolbarState` non gira più). E in dashboard mode la
      **toolbar non viene neutralizzata**: mostra ancora path, branch e i pulsanti Fetch/Pull/
      Push/Commit di un repo che non è più aperto. *banale*
- [x] 0.26 **Navigazione da tastiera della dashboard rotta**: dalla casella di ricerca il primo ↓
      evidenzia il *contenitore del gruppo* ("Recent repositories" + prima riga) invece della
      prima voce, e i ↓ successivi non avanzano; il caret resta nella casella, quindi il fuoco non
      si sposta mai davvero. *media*
- [x] 0.27 **Etichette di menu troncate senza ellissi**: "Toggle between artificial and HEAD
      commi", "Highlight selected branch (until refresh", "Arrange commits by topo order (ances".
      *banale, cosmetico*
- [x] 0.28 Su un repo **bare** il pannello sinistro mostra l'errore git grezzo
      (`Error: fatal: quest'operazione deve …`) invece di un albero vuoto. *banale*
- [x] 0.29 Da confermare: lo stack del crash 0.19 è stato visto anche sul percorso
      `OpenRepository → LoadRepository → Reload` (doppio clic su un worktree nell'albero con una
      riga selezionata), intermittente 1 volta su 5. Le prove successive alla build con la guardia
      non l'hanno più riprodotto: **verificare che la guardia copra anche questo stack**.

- [x] 0.30 `CommitDialog.cs:1830` — "Commit & push" chiama ancora la `PushStreaming` a due stati
      e quindi passa **`-u` cablato**, ri-puntando l'upstream del ramo. Va instradato sullo stesso
      probe `ResolveTrackingAsync` introdotto per il push dialog. *banale*

- [x] 0.31 **CRASH aprendo un repository dalla Dashboard** (clic su una tile o Invio):
      `NullReferenceException` in `RevisionGridView.IsArtificial` (`:984`, `row` è **null**) da
      `BuildRow` (`:3873`) via `FuncDataTemplate`. È la trappola già nota: col **riciclo dei
      container disabilitato** Avalonia re-invoca il template con item `null` quando svuota un
      container (stesso difetto che causò il crash di `BlameView` in M51). Verificato
      **preesistente** ricompilando con la baseline: non introdotto dalle correzioni della
      dashboard. Assegnato al proprietario di `RevisionGridView.cs`. *grave, banale*

- [x] 1.14b **Righe artificiali, seconda metà.** La griglia ora alza
      `ArtificialRevisionSelected` con un contratto scritto, e l'host pulisce File tree e GPG e
      marca stantii tutti i tab — quindi **il contenuto del commit precedente non resta più lì**.
      Manca il contenuto vero: **Diff** (`git diff` per Working directory, `git diff --cached` per
      Commit index) e **File tree** su worktree/index richiedono le modalità index/worktree in
      `DiffService`, che non esistono (già censite come "alta" nell'audit E/D1); **Commit details**
      e **GPG** non hanno un oggetto commit, quindi vogliono un placeholder che nomini la riga —
      e nessuna delle due view espone oggi un'API per mostrarlo. *media*
- [x] 0.32 Con la finestra stretta il box "Filter:" finisce nel menu di overflow `»`, e lì il
      mirror **non riceve i caratteri digitati** (il `MenuItem` li mangia). Preesistente, serve un
      fix del fuoco dentro un controllo ospitato in un `MenuItem`. *banale/media*

**Esito della verifica GUI finale del round 9** (display :401, repo costruito ad hoc con merge,
tag, sottocartelle e date distinte). **Nessuna eccezione nel log in tutta la sessione.** Tutte le
regressioni cercate sono risultate negative: griglia/grafo/selezione/menu a piena altezza,
Alt+clic che ri-ancora l'evidenziazione, i nove tab, l'albero che conserva espansione e selezione
dopo un refresh, l'apertura dalla dashboard (crash chiuso), il tab che non salta più su Commit, il
`cd` della console al cambio repo, e la chiusura con la "X" che riscrive `ui-state.json` (61 →
1472 byte). Misurato: una selezione di revisione produce **9 comandi in una sola catena**, nessuna
moltiplicazione ×4; i toggle della griglia tornano come lasciati dopo un riavvio.

*Difetti minori registrati e NON corretti* (costo basso, nessuno bloccante):

- [x] 0.33 Il nodo **"Remotes (n)"** dell'albero conta i **branch remoti**, non i remote: con un
      solo `origin` mostra `Remotes (4)` mentre il figlio è `origin (4)`. *banale*
- [x] 0.34 Dopo un **clone**, il repo clonato **non entra nei recenti** della dashboard (finisce
      solo in `LastRepoPath`). *banale*
- [x] 0.35 **Ctrl+W** viene inghiottito quando il fuoco è nel terminale del tab Console: funziona
      solo dopo aver dato il fuoco alla griglia. Coerente con la regola "la console riceve tutto",
      ma Ctrl+W non è un carattere di controllo utile lì. *banale*
- [x] 0.36 Incoerenza lessicale **"Favourite" / "Favorite"** fra dropdown WorkingDir, menu Start e
      dashboard. *banale*
- [x] 0.37 In About l'URL dell'attribuzione icone è reso **monco** (`p.yusukekamiyamane.com`).
      *banale*
- [x] 0.38 Il bottone **Refresh del tab Output** sembra inerte subito dopo una selezione fatta
      mentre Output è visibile — conseguenza del caricamento pigro, ma dà l'impressione di un
      pulsante rotto. *banale*
- [x] 0.39 `Commands → New branch… / New tag…` risultano disabilitati quando **nessuna riga è
      selezionata** (es. dopo un refresh che perde la selezione): upstream li àncora a HEAD.
      *banale*

*Non verificabile con l'attrezzatura headless — dichiarato, non dedotto dal codice*: i **file
picker** (`Browse…` di Open/Clone/Init/Archive) non si materializzano senza portal XDG, quindi la
loro resa resta non provata (tutti i percorsi sono stati digitati a mano); il **depth** del clone
(git ignora `--depth` su un clone locale); **"Clean submodules"** e **"Initialize all submodules"**
(nessun submodule nei repo di prova usati in quella sessione — il secondo era però stato provato
dal suo autore su un repo con submodule); il **cambio effettivo di shell** dal picker; i **tooltip
per tile** della dashboard (l'hover sintetico non li fa scattare nemmeno su controlli preesistenti).

**BLOCCO 1 — menu, toolbar e chrome: alto valore, costo banale** (le funzioni **esistono già**
nel port, manca il punto d'accesso)

- [x] 1.1 **Titolo finestra** fisso a "Git Extensions (Avalonia / Linux)" (`MainWindow.cs:133`),
      mai aggiornato. Upstream: `"{pathFilter}{repo} ({branch}) - Git Extensions"`
      (`AppTitleGenerator.cs:38-59`). *banale, valore alto* (alt-tab con più istanze)
- [x] 1.2 **Menu Navigate**: 15 voci upstream (`RevisionGridMenuCommands.cs:91-198`), il port ne
      ha 4 e nessuna delle upstream. Le azioni esistono già nel flyout "Go to" e nel menu
      contestuale della griglia. *banale* (eccetto merge-base, vedi 4.x)
- [x] 1.3 **Menu View**: 30 voci upstream con group header ("Branches", "Commits", "Grid labels",
      "Grid info", "Columns", "Sorting", "Settings persistence") e voci checkable; il port ne ha 2
      + tema/lingua. La maggior parte è già implementata nei flyout dell'header della griglia
      (View/Branches/Columns/Date): serve esporla nel menu come voci checkable. *banale*
- [x] 1.4 **Menu Repository — tre dialoghi esistenti e non raggiungibili**: `RemotesDialog`
      (oggi solo da `PullDialog.cs:651`), `SubmodulesDialog` (solo da `RepoObjectsTree.cs:1264`),
      `WorktreesDialog` (solo da `:1336`); più "Update all submodules" e "Synchronize all
      submodules" (`SubmoduleService.UpdateAll`/`SynchronizeAll` già presenti) e "Refresh" in
      testa. *banale*
- [x] 1.5 **Menu Dashboard** assente del tutto (`FormBrowse.Designer.cs:1295-1301`): top-level
      visibile solo in dashboard mode, con "Refresh". *banale*
- [x] 1.6 **Nessuno shortcut mostrato nei menu**: `MainMenu.Item()` non imposta mai
      `InputGesture`. Il dato c'è (`HotkeyService`). Attenzione: `MainToolbar.cs:865-874` legge
      `HotkeyService.Defaults`, non le binding effettive → **con override attivi le etichette
      mentono**: passare l'istanza reale. *banale/media*
- [x] 1.7 **Nessuna logica enable/disable** in tutto il menu: upstream nasconde interi menu senza
      repo valido (`FormBrowse.cs:926-929`), disabilita 13 voci su repo **bare** (`:1014-1034`) e
      gating per selezione in `CommandsToolStripMenuItem_DropDownOpening` (`:2330-2366`). Serve la
      nozione di bare repo (assente nei service) + un handler `Opening`. *media*
- [x] 1.8 **Ordine colonne della griglia invertito** rispetto all'originale: upstream è
      graph → **Message** → notes → avatar → author → date → **CommitId ultimo**
      (`RevisionGridControl.cs:342-351`); il port ha hash in seconda e subject in ultima. È la
      divergenza visiva di primo impatto (cfr. `GUI.png`). *banale*
- [x] 1.9 **Batch hotkey della griglia** su azioni **già esistenti**: Ctrl+I, Ctrl+Shift+I,
      Ctrl+Shift+C, Ctrl+Shift+G, Ctrl+P/Ctrl+N, Ctrl+Shift+A/U/T, Ctrl+Shift+B, Ctrl+Alt+T,
      Ctrl+Shift+R. Più tre **divergenze** da sanare: Alt+↑/↓ (upstream = quick-search prev/next,
      parent/child sono Ctrl+P/Ctrl+N), Ctrl+G (upstream `GoToCommit` è Ctrl+Shift+G e Ctrl+G è
      GitBash → oggi conflitto reale), F3. *banale*
- [x] 1.10 **Binding registrati ma inerti** già chiudibili: `AddNotes` Ctrl+Shift+N
      (`CommitDetailView.EditNotes` e `AddNotesDialog` esistono), `ManageWorkTrees` Ctrl+Alt+W
      (`WorktreesDialog` esiste), `GoToChild` Ctrl+N. *banale*
- [x] 1.11 **Toolbar**: manca il pulsante **Settings** (`Designer.cs:529-536`) e il **toggle del
      pannello sinistro** (`:247-254`, azione già esistente come sola hotkey). *banale*
- [x] 1.12 **Split-button Stash**: il port ha un pulsante secco che fa `stash save "WIP"`. Upstream
      (`Designer.cs:348-405`): corpo = dialogo stash, freccia = Stash / Stash staged / Stash pop /
      — / Manage stashes… / Create a stash…, e il testo porta il **contatore `(n)`**. *media*
- [x] 1.13 **Caricamento non lazy del pannello inferiore**: `MainWindow.OnRevisionSelected`
      (`:1565-1568`) lancia *sempre* `ShowCommit` su Commit+Diff+File tree+GPG → a ogni movimento
      di selezione partono 4 catene di git (incluso `--show-signature` e `ls-tree -r`) di cui 3
      invisibili. Upstream carica solo il tab selezionato (`FormBrowse.cs:1240,1251,1306`). *banale*
- [x] 1.14 **Righe artificiali (Working directory / Commit index) non raggiungono il pannello**:
      `RevisionGridView.cs:513-531` esce prima di emettere l'evento e `ArtificialRowActivated`
      non ha subscriber → i tab restano sul commit precedente, cioè mostrano contenuto **stantio**
      (il caso peggiore). *banale per Commit, media per Diff/File tree*
- [x] 1.15 `MaxGraphLanes` 8 → 40 come upstream (`RevisionGraph.cs:20`): oltre l'ottava lane il
      grafo non disegna. *banale*
- [x] 1.16 Quick-search: cercare anche **nomi dei ref** e **prefisso hash** come
      `GitRevisionTester.cs:97-109`, più Ctrl+V nel buffer. *banale*
- [x] 1.17 Autore della revisione selezionata in **bold** su tutte le righe
      (`AuthorRevisionHighlighting`). *banale*
- [x] 1.18 `CommitDetailView` — la **data di commit spare quando autore == committer**: upstream
      mostra la riga extra se le *date* differiscono (`CommitDataHeaderRenderer.cs:87-111`), il
      port se differisce la *persona* → su rebase/amend/cherry-pick la data di commit si perde. *banale*
- [x] 1.19 Blame: **gutter a bande** (upstream collassa hash+autore per righe consecutive dello
      stesso commit, `BlameControl.cs:402-405`; il port ripete l'hash su ogni riga) e la **data
      d'autore già calcolata e mai mostrata** (`BlameService.cs:86` la riempie, `BuildRow` la
      ignora). *banale entrambi*
- [x] 1.20 GPG: **icone di stato della firma** (`CommitSignatureOk/Warning/Error`) e il port
      **mostra troppo** — `--pretty=medium` stampa anche il corpo del commit, upstream solo il
      messaggio di verifica. Le 7 icone sono **già linkate** dal glob `Assets/Icons/`. *banale*
- [x] 1.21 Stash: checkbox **"Keep index"** (`StashOpsService.cs:65` cabla `keepIndex: false`) +
      riquadro del messaggio dello stash + hotkey next/prev stash. *banale*
- [x] 1.22 File history: pulsante **Reload** e aggancio al refresh globale (`MainWindow` non
      richiama mai `ShowHistory`); **doppio clic** su una riga (idem in Blame). *banale*
- [x] 1.23 Dashboard: **casella di ricerca** con filtro incrementale (Enter apre il primo, ↓ passa
      alla lista) e i link **Clone / Create** oggi solo nel menu. *banale*
- [x] 1.24 Diff: **"Filter file in grid"** (`RevisionService.PathFilter` e il `_pathFilter` del
      dialogo esistono già: basta spingerci il path e refreshare). *banale*
- [x] 1.25 RepoObjectsTree — voci di menu su **servizi già esistenti**: **tag** (Merge, Rebase,
      Create branch, Reset current branch to here, doppio clic — oggi solo 3 voci), **remote
      branch** (Checkout è *esplicitamente escluso* a `:677`, più Create branch e Reset), **root
      Remotes** (Fetch all / Fetch and prune all: `FetchAllStreaming`/`FetchAndPruneAllStreaming`
      esistono), **root Stashes** (nessun menu: Stash / Stash staged / Manage stashes…), **branch
      locale** (Create branch, Reset current branch to here, gating sul branch corrente),
      **worktree** (gating current/deleted + Show in folder). *banale in blocco*

**BLOCCO 2 — residui M51: costo più basso della stima originale**

- [x] 2.1 **File tree** — oggi è una `ListBox` di stringhe da `ls-tree -r --name-only`, piatta,
      senza menu né anteprima. Due appigli abbattono il costo: `IGitModule.GetTreeFiles(oid,
      full: true)` (`IGitModule.cs:359`, **già linkato**, restituisce `GitItemStatus` mappabile
      1:1 su `DiffFileRow`) e `DiffTextService.GetFileBytesAsync` (`:279`, già fa `git show
      <rev>:<path>`) per l'anteprima del contenuto. Consumare `FileStatusListView`. *banale/media*
- [x] 2.2 **GPG firma del tag** — sezione separata + 4 icone `TagOk/Error/Many/Warning`, layout
      50/50 vs 100/0. `GitCommands.Git.Gpg.GitGpgController` **non dipende da GitUI** → il port
      può istanziarlo direttamente (`CommitStatus`, `TagStatus`, `GetTagVerifyMessage`). *banale/media*
- [x] 2.3 **Stash: lista file dello stash** → sblocca **"Stash selected changes"**. Serve un
      metodo in `StashOpsService` che dia la lista file di uno stash (`git stash show
      --name-status`); l'anteprima per-file è già coperta da `DiffTextService`/`DiffService`, e il
      consumatore (`FileStatusListView`) è pronto. Più la voce "Current working directory changes"
      in testa alla lista, che upstream usa come gate di "Stash selected". *media*

**BLOCCO 3 — persistenza e dashboard (media, alto valore d'uso)**

- [x] 3.1 **Toggle e colonne della griglia non persistiti**: visibilità colonne, ShowAuthorDate,
      RelativeDate, topo order, remote branches, tags, stashes, hide merges, first parent, current
      branch only, non-relatives gray, page size — tutti session-local
      (`RevisionGridView.cs:210-238`). È il gruppo più visibile: l'utente ri-configura la griglia a
      ogni avvio. *media*
- [x] 3.2 **Persistenza** — **CHIUSA in M69** per il residuo: opzioni del **diff viewer** (11),
      **switch della file history** (4), **filtri e ordinamento del left panel** (8) e **MRU del
      dialogo dei filtri avanzati** (cap 15) vivono in un `view-prefs.json` separato
      (`App/Services/ViewPrefsService.cs`), sul precedente di `commit-info.json`, perché
      `MainWindow` riscrive la sua istanza di `UiState` alla chiusura. La MRU del **quick filter**
      era già persistita. Testo di ricerca e nodi espansi del left panel volutamente fuori (stato di
      navigazione, non filtri).
- [x] 3.3 **Dashboard: menu contestuale** (Show in folder / Categories ▸ / Remove from list /
      Remove missing projects) — serve `RemoveRecentAsync` in `RecentRepositoriesService`, che oggi
      ha solo Load/Add. Nota: il port **elimina in silenzio** le voci morte
      (`RecentRepositoriesService.cs:35-77`) mentre upstream le evidenzia e chiede: scelta
      difendibile, da *dichiarare*. Più branding (logo/sfondo/palette) e il branch corrente per
      voce. *media*
- [x] 3.4 **Banner "operazione git in corso"** (rebase / merge / bisect / cherry-pick) ancorato
      sopra la griglia (`FormBrowse.Designer.cs:650-668`): oggi nessun indizio visivo che un
      bisect o un merge è in corso. Serve un rilevatore di `rebase-merge/`, `MERGE_HEAD`,
      `BISECT_LOG`, `CHERRY_PICK_HEAD`. *media*
- [x] 3.5 **UI delle hotkey**: il backend è **completo** (default, parse, `Save()`, `hotkeys.json`)
      e manca solo la finestra; oggi gli override si scrivono a mano e il duplicato vince "primo
      che scrive". *media*
- [x] 3.6 **Pagina Git config advanced**: 8 chiavi (`pull.rebase`, `fetch.prune`,
      `merge.autostash`, `rebase.autostash`/`autosquash`/`updaterefs`, `rerere.enabled`/
      `autoupdate`) — il consumatore è **git**, zero plumbing nel port. Più i 3 flag blame
      (`IgnoreWhitespaceOnBlame`, `DetectCopy*`) che cambiano davvero l'output di `git blame`
      (oggi `module.Blame` è chiamato senza alcun flag). Più il selettore **Local/Global** per
      `user.name`/`user.email`: oggi si scrive sempre `--local`, quindi **l'identità globale non è
      impostabile**. *media, ottimo rapporto valore/costo*

**BLOCCO 4 — media/alta, da valutare dopo i primi tre**

- [x] 4.1 **Checkout di rami remoti impossibile** — CHIUSA in **M67**: `CheckoutBranchForm` è il
      port completo di `FormCheckoutBranch` (tre modalità new-branch + Local changes) su
      `Commands.CheckoutBranch`; toolbar, `Ctrl+.` e nodi remoti dell'albero ci passano tutti.
      Nota: l'esclusione in `RepoObjectsTree` non esisteva più. Testo originale della voce:
      `CheckoutBranchDialog` è solo il gruppo "Local
      changes" e `RepoObjectsTree` omette Checkout sui remoti. **Non bloccato**:
      `Commands.CheckoutBranch(branch, isRemote, localChanges, newBranchMode, newBranchName)`
      esiste già nel core (`Commands.cs:10`), `BranchTagService.cs:166` usa solo `Commands.Checkout`.
      Con esso arrivano reset-local-branch, create-with-custom-name e detached. *media/alta*
- [x] 4.2 **Flusso "push rejected"**: upstream rileva `! [rejected]`, offre pull default/rebase/
      merge/force-with-lease e fa `Retry()` **in place** (`FormPush.cs:509-693`); il port ha solo
      il retry sulle credenziali. Più il **force a 3 stati** (`bool force` → `ForcePushOptions`,
      che sblocca anche 0.8) e la risoluzione della destinazione via `push.default`/
      `remote.pushDefault`/`branch.<x>.merge`. *alta*
- [x] 4.3 **RepoObjectsTree perde stato expand/collapse e selezione a ogni refresh**: `BuildTree`
      ricostruisce da zero e riassegna `ItemsSource`, quindi ogni checkout/merge/stash richiude
      tutto. Upstream salva/ripristina lo stato e ri-seleziona (`Tree.cs:163-183`). Più la
      **gerarchia a cartelle** per branch/tag (upstream `BranchPathNode`/`BasePathNode`, visibile
      nello screenshot come `docs/`, `feature/`). *media*
- [x] 4.4 **CommitDialog: lista file senza menu contestuale.** `FileStatusListView` non ha
      `ContextMenu` nel commit dialog e la lista è un `ListBox` di stringhe. Upstream ha ~25 voci
      (reset file to, interactive add, cherry-pick changes, difftool, open/edit, save as/move/
      delete, show in file tree, filter in grid, file history, blame, gitignore/exclude,
      skip-worktree/assume-unchanged, blocco submodule). Più il **filtro regex di selezione**
      (visibile nello screenshot) e la validazione/persistenza del messaggio. *alta*
- [x] 4.5 **Il box "Filter:" della toolbar non filtra via git**: è un setaccio in memoria sulle
      righe già caricate (`ApplyFilterCore`, `Matches`), mentre upstream applica il filtro **a
      git** su Invio con un dropdown "Filter type" (message/committer/author/diff contains) e una
      MRU di 30 voci. `RevisionFilter` supporta già tutti quei campi: è wiring + persistenza. Più
      il **ref picker** per "Show filtered branches", oggi dichiaratamente uno stub. *media*
- [x] 4.6 **Blame: evidenziazione di tutte le righe dello stesso commit** su hover/selezione
      (l'affordance più usata upstream), **find/F3/go-to-line** (template già in `DiffView`) e il
      walk accurato "blame previous revision" (upstream mappa la riga nel parent con
      `GitBlameParser`; il port ri-blama e perde la posizione). *media*
- [x] 4.7 **Linkificazione del commit info**: gli hash dentro il corpo del messaggio non sono link
      (`CommitDataBodyRenderer.cs:44-65`), branch e tag non sono cliccabili (pillole inerti), e
      "Derives from" stampa `v1.0-5-gabc1234` invece di `v1.2.0 + 66 commits`. *media*
- [x] 4.8 **`GitProcessDialog` su PTY**: passarlo a `PtyProcess`/`TerminalEmulator` (**già
      esistenti**, alimentano `ConsoleView`) sblocca in un colpo output live, barra di progresso
      dalle righe `\r` e **prompt interattivi** — oggi stdin è chiuso e `GIT_TERMINAL_PROMPT=0`,
      quindi passphrase e host-key `yes/no` non sono rispondibili. *media/alta*
- [x] 4.9 **Leva massima della file history**: dare a `RevisionGridView` un entry point **con path
      filter** (oggi `LoadRepository(string)` è l'unico loader) chiuderebbe in un colpo grafo,
      decorazioni ref, righe artificiali e multi-selezione nel tab File history, che oggi
      reimplementa una lista nuda. *media/alta*
- [x] 4.10 Toolbar, resto: **shell-picker** (upstream `userShell` è uno split-button che elenca le
      shell disponibili, il port ha un "Terminal" secco), dropdown **WorkingDir** ricco (ricerca,
      preferiti categorizzati, Open/Close repository, "Configure this menu…"), voce **"Checkout
      branch…"** in testa al dropdown branch, corpo cliccabile di **CommitInfoPosition** (cicla le
      3 posizioni con icona dinamica), **icona di Commit dallo stato del repo** (7 stati upstream),
      **behind** sul pulsante Push, visibilità condizionale dei Worktrees, filtri **branch** e
      **revision** della seconda toolstrip. *banale→media, molte voci*
- [x] 4.11 Dialoghi, resto — **CHIUSA**: il grosso in **M68**, la coda in **M69**.
      ✅ **dialogo bisect + gating su `InTheMiddleOfBisect`** (M68: `BisectDialog`, l'auto-start
      silenzioso non c'è più, banner con conteggi veri da `--bisect-vars`);
      ✅ **macchina a stati `git am`** (M68: `AmSessionService` + `ApplyPatchDialog`, PatchGrid,
      Resolved/Skip/Abort con le regole di abilitazione di upstream);
      ✅ `FormCleanupRepository` — la premessa era stantia, `clean -X` **era già** raggiungibile:
      in M68 verificato end-to-end (tre modi → 2/2/4 voci, Preview == dry-run) e chiusi i residui;
      ✅ `FormInit` — **esisteva già**; verificato bare + `core.sharedRepository`. Upstream non ha
      un controllo `--shared` separato, quindi non è stato aggiunto;
      ✅ `CloneDialog` — le quattro cose (submodule-init, depth, branch picker, preview) **c'erano
      già**; verificate in M68 (ramo scelto checkoutato, shallow, submodule) e corretta la semantica
      della preview.
      ✅ **CHIUSA in M69** anche la coda: `RemotesDialog` ha il tab **"Default pull behavior"** e la
      **push URL separata**; `FormVerify` è portato come `VerifyDialog` con recupero vero degli
      oggetti perduti; `ArchiveDialog` ha la scelta della revisione e il tar semplice (il filtro
      path/revisione c'era già); `SparseDialog` è allineato al **legacy** di upstream, quindi la
      **negazione `!` funziona** (il cone mode non può esprimerla); `AboutDialog` mostra versione,
      build sha, versione git, copyright e attribuzione icone.

**Rinviati con motivo** (registrati per non riaprirli a ogni round)

- **Script utente** (`ScriptsManager`/`ScriptInfo`, toolstrip Scripts, voci "Run script" nel menu
  contestuale della griglia e dell'albero, hook Before/After Commit/Push/Checkout, pagina
  Scripts): **nessun equivalente in `App/Services/`**, è un sottosistema a sé. *alta*
- **External links / bug tracker** ("Related links:" nel commit info): servono storage, parser e
  pagina di impostazioni; zero hit in `src/crossplatform`. *alta*
- **Superproject refs** (3 toggle View + label nel grid): manca un equivalente di
  `SuperProjectInfo`. *alta*
- **Avatar remoti** (provider/cache/template): il port disegna identicon offline **per scelta**;
  l'intero blocco di impostazioni sarebbe pulsante finto. Restano sensati i soli due toggle
  `Show…`.
- **`FormResolveConflicts` dedicato**: il path per-file nel `CommitDialog` (ours/theirs/mergetool/
  mark resolved) copre i casi comuni; `AvaloniaGitUICommands.cs:137` lancia ancora `NotSupported`.
- **Lato scrittura del tab Diff** (righe artificiali + stage/unstage/reset dal Diff, e i comandi
  distruttivi reset-to/move/delete/stop-tracking): serve la modalità index/worktree in
  `DiffService` e nessuna API git a livello di file esiste in `App/Services/`. Nota: il patching
  **per righe** esiste già (`PatchStagingService`), usato solo dal `CommitDialog`. *alta*
- **Gutter dei numeri di riga** nel diff: il diff è reso come un unico `TextBlock`, serve un
  modello per-riga con numero left/right. *media/alta*
- **Colori, font e temi configurabili**: i font sono hard-coded in ≥8 file, il tema è 2 palette
  fisse; prima centralizzare, poi la UI.
- **Opzioni di stile del grafo** (diagonali, straighten, merge lanes a parent comune),
  **ref label a forme** point/notch con coppia locale+remoto annidata, **tooltip del grafo con
  LaneInfo** (i `RevisionGraphSegment` del port non portano l'origine del segmento).
- **Ahead/behind per ref** nel grid e nell'albero: manca un provider (`RemoteService` non lo espone).
- **Stato submodule dettagliato** (11 icone upstream): `SubmoduleService` dà solo
  Initialized/NotInitialized/OutOfDate.
- **Enable/disable dei remoti** e gruppo "Inactive": serve l'equivalente di
  `IConfigFileRemoteSettingsManager` (`remote.<name>.gitextensions-disabled`).
- **Personalizzazione della toolbar** (i 6 pulsanti-scorciatoia fetch/pull nascosti + menu
  contestuale checkable + persistenza per pulsante). *alta, valore basso*
- **SKIP Windows-only**: ConEmu/console style, shell extension/registry, Plink/Pageant/Puttygen,
  `LinuxToolsDir`, path di git configurabile, updater/telemetria/Visual Studio, `FormFixHome`
  (già coperto non-UI da `HomeDirectoryFix`).
- **Fuori scope per direzione dell'utente**: lingue oltre inglese/italiano, repository-host
  GitHub (fork/PR/upstream), colonna e integrazione build status.

**M52** (2026-07-27) — **BLOCCO 0 chiuso** per 13 voci su 15 (0.3 e il resto di 0.15 sono
nell'iterazione successiva). Iterazione 2, tre subagent in worktree su file disgiunti, più il
cablaggio minimo in `MainWindow` fatto dal loop. Nove commit.

- **Patch, process dialog, encoding** (`29d05133a`, `d326c7501`, `0a739b55f`) — `ApplyPatch` ora
  **sniffa il file** (porting di `IsDiffFile`: prima riga `diff `/`Index: `) e sceglie `git apply`
  per un diff nudo, `git am` per una mailbox; prima di partire con `am` controlla
  `GitModule.InTheMiddleOfPatch()` (raggiungibile dal port; fallback su `.git/rebase-apply`) e
  **rifiuta** invece di toccare una sessione che non ha creato lei — `am --abort` viene emesso solo
  se la sessione esiste *dopo* il fallimento del nostro stesso `am`. Nessun fallback su `apply` per
  le mailbox (perderebbe autore e messaggio in silenzio).
  Il **process dialog** ha un nuovo `GitProcessScope` in `GitStreamRunner` che raccoglie i `Process`
  avviati e li lega al flusso logico dell'operazione con un `AsyncLocal`, così **nessun chiamante è
  cambiato**: Abort ora fa `Kill(entireProcessTree: true)` su ogni processo vivo (e su quelli
  avviati più tardi nello stesso scope), poi `UnlockIndex(includeSubmodules: true)` **solo se un
  kill è davvero avvenuto** (così il lock di un git vivo non viene mai cancellato), scrive
  "Aborted" e restituisce l'esito di abort; OK resta disabilitato fino al termine e chiudere la
  finestra in anticipo non finge più un successo. Abort è nascosto sul path non-streaming, dove il
  core `Executable` non dà un handle killabile — come upstream nasconde il pulsante senza callback.
  `COMMIT_EDITMSG` è scritto con `module.CommitEncoding`; due extra trovati testando: `cp1251` (che
  git accetta ma la tabella .NET no) cadeva in silenzio su UTF-8 → ora si mappa per codepage, e
  `i18n.commitEncoding=utf-8` risolveva a un'istanza **con BOM** che infilava un `U+FEFF` nel
  messaggio → il caso UTF-8 usa ora `UTF8Encoding(false)`.
  *Verificato con git reale*: mailbox → `am` con autore e subject preservati; diff nudo → `apply`
  senza lasciare `rebase-apply`; con una sessione `am` conflittuale già aperta la chiamata rifiuta,
  la sessione **sopravvive**, HEAD è invariato e l'utente può ancora fare `am --skip`; il nostro
  `am` conflittuale viene abortito e HEAD ripristinato. Abort: `git commit` bloccato da un hook
  `pre-commit` da 60 s → 3 processi prima, 0 dopo, git esce 137 in 0,7 s, un comando avviato dopo
  l'abort muore all'arrivo, `UnlockIndex` rimuove `index.lock`. *Non verificato*: il wiring UI del
  dialogo (visibilità pulsanti, auto-chiusura) è solo compile-verified; `index.lock` nel test era
  creato sinteticamente, non da una scrittura git interrotta.
- **Dialoghi push / worktree / remotes / submodules** (`3b29ba7e3`, `d90904e88`, `ebf007a35`,
  `d71b91e4f`) — la combo "Remote branch" del push non elenca più i rami **locali**: nuovo
  `UpdateRemoteBranchCombo` la ricostruisce dai rami che esistono davvero sul remote selezionato
  (nuovo `PushRefsService.LoadRemoteBranches`, da `for-each-ref refs/remotes`, caricato dentro il
  `Task.Run` di `ShowAsync` — bug M43 rispettato), agganciata al cambio di remote e di ramo locale;
  resta editabile, e un nome che non esiste sul remote fa scattare una **conferma** (tab singolo e
  multiplo). Nuovo `enum PushForceMode { None, WithLease, Force }` in `PushRefsService`/
  `RemoteService`: i **tag** usano `--force` puro, i branch `--force-with-lease`; le firme
  `bool force` storiche restano come overload che delegano, quindi i chiamanti compilano invariati.
  In UI una seconda checkbox "Force push" mutuamente esclusiva, con la domanda upstream "usare il
  più sicuro?". `WorktreeService` parsa ora `prunable` (con la ragione di git) più `IsMain` e
  `IsSamePath`: **Remove** è abilitato solo se non-main, non-bare, non-prunable e non è il worktree
  aperto; **Prune** solo se c'è qualcosa di prunable; i cancellati sono barrati e in `App.TextDim`.
  `RemotesDialog` mostra l'output di git quando un'operazione fallisce (prima teneva solo
  `Success`) e rifiuta i nomi duplicati su Add e Rename. "Init all" dei submodule esegue davvero
  `git submodule init` invece di duplicare "Update all".
  *Verificato con git reale*: `push --force-with-lease` su un tag spostato viene **rifiutato**
  (`! [rejected] v1 -> v1 (stale info)`, tag remoto invariato) mentre `--force` passa
  (`+ d06af8d...9802e42 (forced update)`) — il bug era reale; `LoadRemoteBranches` eseguito verbatim
  su un repo con due remoti a set disgiunti dà i soli rami remoti; il parser worktree distingue
  main/prunable/aperto su git 2.43; `submodule init` scrive `submodule.sub.active/url` lasciando la
  directory vuota, quindi non è un doppione di `update --init`. *Nota*: `prunable` in `--porcelain`
  esiste da git ≥ 2.36; su git più vecchi il gating degrada senza regressioni.
- **Console, log, blame, settings, stato** (`b756e0653` + cablaggio `f16610488`) —
  `ConsoleView.RepoPath` è un vero setter: con shell viva digita `` + `cd '<path>'` sul
  PTY (gli stessi due control char di `MinttyShellRunner.ChangeWorkingDirectory`, per pulire una
  riga a metà), altrimenti registra la directory, così anche "Restart shell" atterra nel repo
  nuovo. `OutputView` e `CommandLogWindow` si iscrivono a `CommandLog.CommandsChanged` con
  **throttle 300 ms** trailing-edge e si disiscrivono su detach/close; la finestra staccata
  auto-scrolla solo se era già in coda, altrimenti conserva caret e offset. Il blame ha un
  `CancellationTokenSource` per richiesta che cancella il precedente, token passato fino a
  `BlameService`, e guardia di staleness prima di postare; più il **gutter a bande** (hash/autore/
  data solo sulla prima riga di una serie dello stesso commit) e la nuova colonna **Author date**,
  che il servizio già calcolava e nessuno mostrava. La combo "Default pull action" scrive ora
  `UiState.DefaultPullAction` (e offre tutti e cinque i valori), con callback verso l'host perché
  `MainWindow` riserializza l'intera istanza alla chiusura; l'identità ha un selettore **Settings
  source Local/Global** (prima era una nota fissa "Local" e si scriveva sempre `--local`, quindi
  **l'identità globale non era impostabile**). `UiState` guadagna `LeftPanelCollapsed` (distinto da
  `TreeWidth`, che quindi non viene più mangiato dal clamp di `Sanitize`), `CommitInfoPosition` e
  `LastRepoPath`; il loop ha cablato restore/save, il `cd` su `OpenRepository`, il callback della
  pull action e la precedenza CLI > cwd > ultimo repo > dashboard.
  *Verificato a schermo dal loop*: log **live** — 23 → 44 comandi loggati dopo il solo Refresh
  della toolbar, senza toccare il Refresh del pannello. *Limite dichiarato*: le scritture
  `--global` non sono mai state eseguite (la sandbox rifiuta l'override di `HOME` e non si tocca il
  `~/.gitconfig` reale); verificata la lettura del livello Global e la mappatura a `--global`.
  Cambio di comportamento voluto: i campi identità mostrano il valore del **livello selezionato**,
  non quello effettivo, quindi su un repo senza identità locale la pagina Local è vuota dove prima
  mostrava il valore globale ereditato.

**M53** (2026-07-28) — **BLOCCO 0 chiuso del tutto** (0.3 e 0.15) e **grosso taglio nel BLOCCO 1**.
Iterazione 3, tre subagent in worktree su file disgiunti più il cablaggio del loop. Undici commit
(`0dcd51534`…`c480d1a43`).

- **Menu Navigate/View + parità della griglia** (`0dcd51534`, `ef97b94a2`, `3af4b121a`,
  `b49578edd`, cablaggio `a18bccdc1`) — le colonne tornano nell'ordine upstream
  (`graph, Subject(*), avatar, author, date, hash`, **SHA-1 ultimo**, Subject come colonna che
  riempie); `MaxGraphLanes` 8 → 40; l'autore della revisione selezionata è in **bold** su tutte le
  righe che lo condividono. La quick-search cerca anche i **nomi dei ref** e il **prefisso hash**
  (> 2 caratteri) e accetta Ctrl+V. Nuova superficie pubblica della griglia chiavata sui
  `MenuCommand.Name` upstream (`ExecuteMenuCommand`, `ViewOptions`, `ViewOptionsChanged`, 20 metodi
  `Toggle*`/`Set*`), su cui il menu **Navigate** (15 voci nell'ordine upstream) e **View** (group
  header Branches/Commits/Grid labels/Grid info/Columns/Sorting, voci checkable) sono un
  *mirror*: **una sola fonte di verità**, i flyout dell'header e il menu si aggiornano a vicenda e
  nessun flyout viene ricostruito mentre è aperto. Nuovo `Services/MergeBaseService.cs` (`git
  merge-base`) e `--author-date-order` in `RevisionService`. Hotkey riallineate a
  `HotkeySettingsManager`: Ctrl+P/Ctrl+N parent/child, Alt+↑/↓ quick-search, Ctrl+Shift+G go-to,
  Ctrl+Shift+K merge base, Ctrl+I / Ctrl+Shift+I filtro, Ctrl+Shift+A/U/T scope, Ctrl+Shift+B,
  Ctrl+Shift+R, Ctrl+Alt+T; **Ctrl+G e F3 liberati** (Ctrl+G era in conflitto reale con GitBash).
  *Due difetti trovati e corretti dal subagent stesso*: la cella del subject era uno `StackPanel`,
  che misura su larghezza infinita → il testo veniva dipinto **sopra** il nome dell'autore (ora
  `DockPanel` con clipping ed ellissi); e lo swap di `ItemsSource` in `RebindRows` ri-annunciava
  `RevisionSelected`/`RangeSelected` → ogni clic sparava l'evento due volte (flag `_rebinding`).
  *Verificato a schermo*: menu con group header, "Show SHA-1 column" dal menu che nasconde la
  colonna **e** toglie la spunta nel flyout Columns, Ctrl+Shift+K che atterra esattamente su
  `git merge-base side HEAD`, Ctrl+G non più catturato.
- **File history e commit dialog** (`771f803dc`, `46fb91463`, `594da0f69`, `4d2b79513`, cablaggio
  `8690b59e7`) — **il bug 0.3 è chiuso**: `FileHistoryRow` porta ora il nome che il file aveva *in
  quella revisione* (una passata `git log --format="????%H" --name-only --diff-merges=separate`
  parsata come `RevisionGridControl.ParseFileNames`, con encoding lossless), e "Save as" lo usa.
  *Prova su repo reale con `git mv`*: per i commit anteriori al rename il servizio restituisce
  `src/old.txt` e il contenuto salvato è **byte-identico** a `git show <sha>:src/old.txt`, dove il
  codice precedente falliva del tutto. Aggiunti il marcatore "file non identificato in questa
  revisione" (via `GetFileBlobHash`), il pulsante Load/Reload con `Reload()` pubblico agganciato al
  refresh globale, il doppio clic (`RevisionActivated`) e la colonna **data d'autore**.
  Nel `CommitDialog`: le tre conferme distruttive di upstream (amend che riscrive la storia, merge
  commit vuoto — distinguendo il caso legittimo via `MERGE_HEAD` —, HEAD staccato, saltata durante
  un rebase), e il **filtro regex di selezione** con throttle 250 ms, contatore `n/m`, bordo rosso
  **sul contatore** (Fluent lo maschera sul TextBox) e caption che diventano "Stage filtered"/
  "Unstage filtered" agendo solo sui match. *Difetto corretto strada facendo*: la riselezione
  programmatica del toggle della colonna data faceva saltare il pannello inferiore sul tab Commit.
- **Toolbar** (`155f55fd2`, `c1e3cee8d`, `ac2aa9a89`, cablaggio `c480d1a43`) — tutti gli otto punti
  T1–T8. Split-button **Stash** (corpo = tab Stash, freccia coi sei comandi upstream e le gesture
  reali, caption `Stash (n)`); pulsanti **Settings** e **toggle del pannello sinistro** con stato
  checked che segue anche la hotkey; **commit-info** diventa uno split il cui corpo cicla le tre
  posizioni cambiando icona, col radio sulla posizione attiva; **Push** mostra il *behind* e scambia
  l'icona con `Unstage` quando behind > 0; **Commit** prende le icone `RepoState*` (7 stati);
  dropdown branch con **"Checkout branch… Ctrl+."** in testa e tasto destro che apre il picker;
  **Worktrees** con il corrente spuntato e inerte e i prunable in grigio; **"Fetch all" nascosto**
  con un solo remote. Nuovo `Services/ToolbarStateService.cs` per ciò che la toolbar non può
  calcolare (conteggio stash, stato repo, tracking) — senza il probe i display **degradano a
  "sconosciuto" invece di mentire**. *Verificato a schermo*, incluse due prove di controllo:
  "Fetch all" sparisce con un remote e **ricompare** aggiungendone un secondo, e a 1150px di
  larghezza i pulsanti nuovi finiscono nel menu di overflow `»` restando funzionanti.

**M54** (2026-07-28) — **albero sinistro** e **residui M51 di File tree e GPG**. Due subagent
ripresi dal transcript dopo l'interruzione (vedi sotto), quattro commit più due di cablaggio.

- **RepoObjectsTree** (`3a610a7c6`, `2905f238d`, cablaggio `c670ad199`) — l'albero **non perde più
  stato**: espansione e selezione sopravvivono a ogni rebuild (verificato su **tre** percorsi
  diversi: stash creato dal menu, Refresh da toolbar, checkout remoto). Gerarchia a **cartelle
  ricorsive** per branch locali, tag e sotto-path dentro il gruppo remoto (`feature/nested/deep`),
  col menu del path node (Create branch prefissato, Delete All con conferma che elenca cosa
  cancella). Menu contestuali completati per tag (Merge/Rebase/Create branch/Reset + doppio clic),
  remote branch, root Remotes (Fetch all / Fetch and prune all), root Stashes (Stash / Stash staged
  / Manage stashes… / Open stash), branch locale (Create branch / Reset current branch to here) e
  worktree (Show in folder, tooltip upstream, gating), più Expand/Collapse nel menu e Del/F2.
  **Gating fedele all'upstream**: sul branch corrente restano attivi solo Create branch e Rename;
  su worktree aperto Open e Delete sono disabilitati. `Move Up/Down` spostati dalle foglie alle
  **categorie** come upstream, con l'ordine persistito in `UiState.LeftPanelCategoryOrder`.
  *Bonus necessario*: `git checkout origin/x` **detacha sempre la HEAD**, quindi il "Checkout" su
  un ramo remoto non poteva funzionare → nuovo `BranchTagService.CheckoutRemoteBranch` che replica
  `StartCheckoutRemoteBranch` (branch locale esistente → checkout normale, altrimenti
  `checkout -b <branch> --track <remote>/<branch>`), con lo stash estratto in `StashLocalChanges`
  condiviso. Verificato: HEAD finisce sul branch **locale** con upstream corretto. Chiude in
  anticipo buona parte di 4.1.
  *Rinviato con motivo*: le cinque combo "Fetch &&…" sul singolo ramo remoto — `RemoteService` sa
  fare fetch solo per remote intero, e il fetch per singolo refspec con dialogo streaming e retry
  credenziali è un'unità di servizio a sé. Nessuna voce morta al loro posto.
- **File tree e GPG** (`7cbbe61c0`, `1259f8027`, cablaggio `84e6205cb`) — il File tree non è più
  una lista piatta di stringhe: consuma `FileStatusListView` in modalità albero (toolbar nascosta e
  glifi di stato spenti **come upstream in file-tree mode**, non per pigrizia) e mostra il
  **contenuto** del blob al commit via `DiffTextService.GetFileBytesAsync`, evidenziato con
  `DiffSyntaxHighlighter` e con i binari riconosciuti dal sniff del NUL e *dichiarati* invece che
  riversati. Menu contestuale dell'albero upstream (Collapse all / Expand all / Collapse root
  folders) più open/save as/copy path/file history/blame, doppio clic = File history.
  `FileStatusListView` esteso (opzioni per istanza, `ShowToolbar`, `ShowStatusGlyphs`, comandi di
  collasso) **senza cambiare** il comportamento dei suoi consumatori. Nuovo
  `DiffService.GetTreeFiles` su `IGitModule.GetTreeFiles`. Chiusi tre difetti: il clic singolo ora
  carica il file (0.18), il tasto destro sposta la selezione sotto il puntatore, e il tasto destro
  su una cartella non la chiude più.
  Il GPG istanzia direttamente `GitGpgController` (confermato: nessuna dipendenza da GitUI) →
  stessi comandi di upstream, **niente più corpo del commit**, icone `CommitSignatureOk/Warning/
  Error`, sezione tag separata con `TagOk/Error/Many/Warning` e layout 50/50 → 100/0 senza tag.
  *Verificato con firme GPG **reali*** create nella sandbox (chiave EDDSA in un `GNUPGHOME`
  dedicato, commit firmato + tag firmato + tag annotato non firmato): nessun ramo è "verificato
  solo per costruzione". Verificata anche l'assenza di regressioni in Diff e commit dialog.
  *Rinviato con motivo*: "Select all" del menu albero (la lista è a selezione singola e nessun
  comando del port consuma una multiselezione → sarebbe un pulsante finto), la toolbar di
  raggruppamento (upstream la nasconde in file-tree mode), le icone per estensione (manca la mappa)
  e il toggle `ShowGpgInformation` (non esiste nel port).

**M55** (2026-07-28) — **BLOCCO 1 quasi chiuso e BLOCCO 2 chiuso del tutto**. Iterazione 5, tre
subagent più due voci fatte direttamente nel `MainWindow` (che nessun subagent può toccare).

- **Barra dei menu** (`608a6d673`, `c33178651`, `48e6098f2`, `9280e6544`, cablaggio `08965953d`) —
  chiuse 1.4–1.7. Menu **Repository** nell'ordine upstream con i tre dialoghi che esistevano ma
  erano irraggiungibili (`Remote repositories…`, `Manage submodules…`, `Manage worktrees…`), più
  Update/Synchronize all submodules, Refresh in testa, `Close (go to Dashboard)` spostato qui da
  Start, Fetch/Pull/Push spostati in **Commands** come upstream, e il submenu **Git maintenance**
  a quattro voci. Menu **Dashboard** (Refresh, visibile solo in dashboard mode). **Scorciatoie
  nelle voci**, lette dall'istanza reale di `HotkeyService`. **Gating** per stato: menu nascosti
  senza repo, insieme upstream disabilitato su repo **bare**, voci di Commands gated sulla
  selezione via il nuovo `RevisionGridView.SelectionSummary` (la griglia non pubblicava la
  selezione, quindi il gating non aveva nulla da leggere) e nuovo
  `Services/RepositoryStateService.cs` per `--is-bare-repository`.
  *Verificato a schermo su un `git init --bare` vero*: in grigio esattamente l'insieme upstream.
  *Scelte contro il pulsante finto*: Exit / File Explorer / Git command log **non** mostrano
  scorciatoia perché nel port non esiste un `BrowseCommand` che le dispatchi (l'etichetta
  mentirebbe); "Recover lost objects…" punta al `MaintenanceDialog` esistente, che esegue lo
  stesso `git fsck`, invece di fingere il `FormVerify` upstream con il restore per oggetto.
- **Tab Stash** (`60da263fc`, `38bc11724`, cablaggio `fff7d3725`) — **ultimo residuo di M51**.
  Lista file **per stash** (inclusi i file untracked, che vivono in `stash@{0}^3` e il cui diff
  rende `--- /dev/null`: è il caso che sparirebbe in silenzio diffando contro `^..ref`), voce
  "current working directory changes" con i due gruppi **Commit index** / **Working directory**,
  **"Stash selected changes"** abilitato come upstream solo su quella voce, **Keep index**
  (prima `keepIndex: false` era cablato) e riquadro del messaggio.
  *Verificato con git reale*: la lista è byte-identica a `git stash show --name-status`, e lo
  stash parziale ha contenuto **solo** i due file selezionati (`git status` da tre voci a una).
  Il conflitto Ctrl+N/Ctrl+P con `GoToChild`/`GoToParent` (che upstream non ha, perché `FormStash`
  è una finestra separata) è risolto cedendo le due gesture al tab quando ha il fuoco.
  *Rinviata*: la multi-selezione **incrociata** fra i due gruppi — upstream li tiene in un'unica
  lista, qui servono due istanze perché `FileStatusListView` deriva le intestazioni dal group mode
  e non accetta etichette arbitrarie.
- **Dashboard e due difetti isolati** (`8f3a7901a`, `eeb94cbf6`, `e3439f46d`, `dc228491a`,
  `ff23f2280`, `1be53d570`) — dashboard con **casella di ricerca** (filtro incrementale, ↓ nella
  lista, ↑ ritorno, Invio apre il primo), **menu contestuale** (Show in folder / Remove / Remove
  missing projects, quest'ultima visibile solo se serve), link **Create** e **Clone**, nome del
  **branch corrente** per riga (letto fuori dal thread UI), apertura a clic singolo e F5.
  **Scelta dichiarata**: la potatura *silenziosa* dei repo mancanti è stata **rimossa** — era
  perdita di dati e rendeva irraggiungibile "Remove missing projects", perché nulla di mancante
  sopravviveva al caricamento; ora restano, marcati con icona d'errore, e li rimuove l'utente.
  Restano potati in silenzio solo i duplicati e i checkout effimeri in `.claude/worktrees`.
  **1.18 chiuso**: la riga della data di commit dipende ora dalle **date**, non dall'identità —
  prima su rebase/amend/cherry-pick (stessa persona, date diverse: il caso comune) la data di
  commit spariva. Aggiunti i link `mailto:`.
- **Nel `MainWindow`** (`b77234d3f`) — **caricamento pigro** del pannello inferiore: prima ogni
  selezione lanciava quattro catene git (Commit, Diff, File tree, GPG), tre delle quali invisibili,
  incluse `--show-signature` e un `ls-tree -r` sull'intero albero; ora si carica solo il tab
  visibile e gli altri restano marcati stantii. E il **titolo della finestra** nel formato upstream
  `<repo> (<branch>) - Git Extensions`, prima fisso.
- **Da verificare a schermo** (integrati ma non ancora provati): titolo via `WM_NAME` e lazy load
  (una selezione deve produrre **una** catena di comandi nel tab Output, non quattro).

**M56** (2026-07-28) — **la regressione di M53 chiusa** e **BLOCCO 3 quasi finito**. Due subagent.

- **Griglia: crash da re-entrancy e persistenza** (`48c96ad98`, `ae7de82d4`, cablaggio
  `558bf50fc`) — la causa era **più a monte** di quanto registrato in 0.19: non solo
  `UpdateAuthorHighlight`, ma il fatto che `Reload()` sganciava `ItemsSource` **senza alzare la
  guardia**, e Avalonia ripunta il suo `SelectionModel` *dentro* il setter, emettendo
  `SelectionChanged` a metà del batch update. Ora `SetListItems` è l'**unico scrittore** di
  `ItemsSource` e alza la guardia (le chiamate annidate ripristinano il flag che hanno trovato), e
  `RebindRows` **coalesce** le richieste rientranti in una sola passata a
  `DispatcherPriority.Background`. Verificato prima/dopo con una sonda temporanea che riproduce
  esattamente il cablaggio di "Filter file in grid": prima il processo moriva, dopo la griglia si
  restringe correttamente ai 6 commit del path.
  **0.20 era lo STESSO difetto, non un secondo bug**: il path filter dal dialogo sembrava non
  filtrare perché `ApplyRevisionFilter → Reload` lanciava la stessa eccezione *prima* che
  `LoadPage` girasse, e sul percorso non-posted l'eccezione veniva **ingoiata** — così la funnel e
  la "×" si aggiornavano mentre la griglia teneva tutti i commit. Riprodotto (12 commit) e
  risolto (6). Nessun fix separato necessario.
  Con la guardia in piedi **1.24 è cablata**. Verificato che non siano regrediti l'autore in
  grassetto (M53) e l'Alt+clic sul grafo (M50).
  **Persistenza (3.1)**: `UiState` guadagna `GridViewOptions` (mappa chiavata sugli stessi id con
  cui griglia e menu si rispecchiano, tollerante allo skew di versione) e `GridPageSize`; la view
  **non scrive mai il file** — espone stato ed evento e l'host li piega nella sua unica istanza,
  stesso contratto di `RepoObjectsTree.CategoryOrder`. Verificato il giro completo: toggle →
  `Start → Exit` → `ui-state.json` → riavvio, con le spunte del flyout coerenti.
  *Lasciata fuori con motivo*: "Save current view settings as default" — nel port quel salvataggio
  è già automatico all'uscita, quindi la voce sarebbe un **no-op**, cioè il pulsante finto che le
  convenzioni vietano.
- **Impostazioni con effetto reale** (`017a60cec`, `7247c13c5`, `a23263597`, `06f4144fa`,
  `4751e73b8`, `1b3b4822a`, cablaggio `e0a1e5cf7`) — pagina **Git config advanced** tri-state per
  le otto chiavi upstream: lo stato "non impostata" fa un **unset vero**, verificato con
  `git config --local --list` (la chiave sparisce, non diventa `false`); livello **Global**
  provato con un `GIT_CONFIG_GLOBAL` isolato, con l'md5 del `~/.gitconfig` reale **invariato**.
  I **tre flag del blame** (`-w`, `-M`, `-C`) ora arrivano davvero a `git blame`: su un file
  re-indentato la riga 1 passa da **B** ad **A** attivando "ignore whitespace", con re-blame
  immediato dal menu contestuale. Esposti in Settings `AutoRefresh`,
  `DefaultCheckoutLocalChangesAction` e i toggle del commit-info, che il port **già consumava**
  senza offrirli. Nuova **pagina Hotkeys**: cattura del gesto, rilevamento dei duplicati con riga
  rossa, Clear, Reset all, salvataggio via il `Save()` che esisteva già.
  *Cablaggio in più fatto dal loop*: senza, cambiando una gesture la toolbar e il menu avrebbero
  continuato ad annunciare quella vecchia fino al riavvio — cioè avrebbero mentito. Ora un cambio
  li ricostruisce e **ri-applica lo stato che la ricostruzione azzera** (toggle, posizione del
  commit-info, gating, view options).
  *Escluse come pulsanti finti*: tutte le ~35 senza consumatore, più la metà *display* della
  pagina blame upstream (autore/data/numeri di riga), che qui la griglia del blame rende comunque.

**M57** (2026-07-28) — **push rifiutato, blame, linkificazione e otto difetti di GUI**.

- **Push rifiutato e destinazione** (`95b883274`…`65deff70d`, nessun cablaggio necessario) — il
  port ora riconosce `! [rejected]` e offre le quattro scelte upstream (pull default / rebase /
  merge / force-with-lease) con "don't show again" persistito, e soprattutto fa il **retry in
  place** senza chiudere il dialogo (nuovo `GitProcessDialog.Retry()`). *Prova per reflog*:
  `pull --rebase … (finish): returning to refs/heads/main` seguito da `a5d3a64..33f3720 main ->
  main`, con la finestra mai chiusa fra i due tentativi. Il force-with-lease ha **correttamente
  rifiutato** un lease stantio e ri-offerto le scelte.
  **Due correzioni al brief del loop, verificate dal subagent**: (a) upstream **non legge**
  `push.default` né `remote.pushDefault` (zero occorrenze); la catena vera è `remote.<n>.push` →
  `branch.<x>.merge` (solo se `branch.<x>.remote` è il remote di destinazione) → `remote.<n>.prefix`
  → nome del branch, ed è quella portata; (b) il difetto del `-u` era **invertito**: non "mai sul
  fast path" ma `track: true` **cablato sempre**, e il tab multi-branch lo passava anche lì,
  ri-puntando in silenzio l'upstream di ogni ramo selezionato.
  *Bug latente trovato provando davvero l'opzione*: senza `--no-rebase` **ogni pull di tipo merge
  era fatale** su git ≥ 2.27 ("devi specificare come riconciliare i rami divergenti").
- **Blame e commit info** (`005a0463e`…`6f40dc81c`, cablaggio `9b5873d3a`) — evidenziazione di
  **tutte** le righe dello stesso commit su hover e selezione, find/F3 con contatore e
  scroll-into-view, go-to-line, riga iniziale, encoding, doppio clic per blamare quella revisione;
  e nel commit info gli **hash dentro il messaggio sono link** che navigano, branch e tag
  cliccabili, "Derives from tag: **v1.0** + 2 commits" al posto della stampa grezza di
  `git describe`. Il cablaggio cede Ctrl+F/Ctrl+G/F3 al tab Blame quando ha il fuoco.
  *Validazione del mapping riga→parent contro git reale*: 27/27 su un caso sintetico e
  **3066/3066 su cinque commit reali**, zero discrepanze. Nel farlo è emerso che **l'espressione
  di upstream è off-by-one** quando un hunk `-U0` ha range vuoto (`@@ -0,0 +1,5 @@`, dove `,0`
  indica la riga *prima* del buco): il port tratta ogni hunk come le sue due range, e la
  divergenza è documentata nel codice — meglio divergere con una ragione che replicare un difetto.
  *Rinviata*: colorazione del gutter per fascia d'età (la palette ha 10 chiavi e nessun gradiente:
  servirebbero voci nuove in `ThemeManager`, e cablare una rampa a mano violerebbe la convenzione
  dei brush).
- **Otto difetti trovati dalla verifica GUI** (`059a441cd`…`f1dadfac5`, più `dc0e3dd21` fatto dal
  loop) — il **commit dialog ora è dismissibile** (Cancel + Esc: prima non c'era *alcun* modo di
  chiuderlo); Esc chiude i tre dialoghi del menu Repository **conservando il flag `Changed`**
  (verificato rimuovendo un remote e uscendo con Esc: l'albero è passato a `Remotes (0)`); su repo
  **bare** l'albero si popola invece di mostrare l'errore git (colpevoli `git stash list` e
  `git submodule status`, che rifiutano di girare senza working tree, ora saltati); etichette di
  menu non più troncate (era il cap Fluent `FlyoutThemeMaxWidth`, rialzato solo per il MainMenu);
  navigazione ↓/↑ della dashboard che entra sulle **voci** e non sul contenitore del gruppo.
  Dal loop: il pannello inferiore **non salta più sul tab Commit** a ogni selezione (si aggiorna il
  tab visibile, come upstream; il salto resta sul doppio clic), e in dashboard **titolo e toolbar
  vengono azzerati** invece di continuare ad annunciare un repo non più aperto.
  **0.29 chiuso**: il crash intermittente sul doppio clic di un worktree **non si è riprodotto in
  15 tentativi** — la guardia di M56 copre anche quello stack.

**M58** (2026-07-28) — **la finestra si chiude davvero, e la diagnosi storica era sbagliata**.

- **Chiusura della finestra** (`fd54beaab`, nessun cablaggio) — vedi il riquadro in 0.16: non
  serviva alcun ricevitore nativo, serviva un `XInternAtoms` con `only_if_exists: false` prima che
  Avalonia costruisca la sua tabella (`Services/X11AtomPrimer.cs`, chiamato da `Program.Main`).
  Una sola chiamata, nessuna seconda connessione, nessun event thread, nessuna finestra proxy.
  *A/B a schermo su display vergini, finestra pre-dimensionata a 1111×777 e `WM_DELETE_WINDOW`
  sintetico*: **prima** `WM_PROTOCOLS = [<UNSET/0>]`, app viva, `ui-state.json` **assente** (stato
  perso); **dopo** `WM_PROTOCOLS = [WM_DELETE_WINDOW, _NET_WM_SYNC_REQUEST]`, app chiusa, e lo
  stato **scritto** con `WindowWidth 1111, WindowHeight 777`. Vale per **tutte** le finestre, che
  condividono la stessa tabella. *Correzione a una mia assunzione*: **Esc non c'entra** con questa
  radice — è un binding managed, quindi la sua correzione per-dialogo (M57) era comunque
  necessaria. Limite dichiarato: la lista di atomi è accoppiata ad Avalonia 11.3.14; un nome che
  Avalonia togliesse costerebbe un atomo inutilizzato, uno che aggiungesse resterebbe non
  pre-creato — nessuno dei due è un guasto.
- **Banner "operazione git in corso"** (`701af7887`, cablaggio `92dbde20e`) —
  `RepositoryStateService.GetProgress()` legge i marcatori nella git dir (`rebase-merge/` +
  `interactive`, `rebase-apply/`, `MERGE_HEAD`, `BISECT_LOG`/`BISECT_START`, `CHERRY_PICK_HEAD`,
  `REVERT_HEAD`) più il contatore di step e il branch di destinazione; `RepositoryProgressBanner`
  rispecchia le due barre upstream e collassa a nulla quando il repo è a riposo.
  *Verificati a schermo tutti e quattro gli stati* — rebase interattivo fermo su conflitto ("Step
  1 of 1. Branch: topic"), merge in conflitto, bisect avviato con Good/Bad/Skip/Stop, cherry-pick
  in conflitto — e la sparizione dopo l'abort; **"Stop bisect" dal vivo** ha rimosso i marcatori,
  riportato HEAD su `main` e collassato il banner **senza riavviare l'app**.
  *Rinviato con motivo*: nessun pulsante continue/abort/skip per rebase, merge, cherry-pick,
  revert e `git am` — nel port **non esiste un servizio** dietro nessuno di essi (le uniche due
  chiamate `--abort` in `App/Services` sono percorsi privati di pulizia dentro `CommitEditService`
  e `PatchService`, non un'API). Il banner mostra lo stato e **nomina il comando git**, invece di
  esibire un pulsante morto. Serve un `ConflictOpsService`; si lega a `FormResolveConflicts`, che
  resta non portato (`AvaloniaGitUICommands.cs:137` lancia `NotSupported`).

**M59** (2026-07-28) — **il filtro della toolbar diventa un filtro git**, ref picker vero, righe
artificiali segnalate, e il crash della dashboard chiuso.

- **Box "Filter:" → filtro git** (`3120052ac`) — era un **setaccio in memoria** sulle sole righe
  già caricate; ora applica a **git su Invio**, con dropdown del tipo di filtro (message /
  committer / author / diff contains) e **MRU di 30 voci persistita**. *Confronto con `git log`
  reale su un repo di 79 commit con page size 50 e i termini messi apposta nei 4 commit più
  vecchi, cioè **fuori da ciò che la griglia aveva caricato***: message `ZEBRAWORD` → 1 =
  `--grep`; author `Bob` → 1 = `--author`; diff `PICKAXETOKEN` → 1 = `-S`; committer `alice` → 78
  = `--committer` (dopo il paging). **Il caso decisivo, catturato in due screenshot**: prima di
  premere Invio la griglia dice `0 of 50+ commits` — il vecchio setaccio non poteva vederlo —
  e dopo diventa `1 commits — git filter: message: ZEBRAWORD`.
  *Rinviato con motivo*: il filtro **multi-campo** di upstream (più caselle spuntabili) — git le
  mette in AND, quindi lo stesso testo in message AND author è quasi sempre vuoto e **si legge
  come un filtro rotto**; qui è un gruppo radio, e combinare criteri resta possibile ed esplicito
  nel dialogo dei filtri.
- **Ref picker per "Filtered branches"** (`c95323979`) — non è più uno stub che si dichiarava
  tale nella UI: casella che restringe + toggle Local/Remote/Tags + lista a checkbox, con
  **multi-ref**, che upstream (combo singola) non ha. Verificato: `wtbranch` → 4 commit =
  `git log wtbranch`; aggiungendo `origin/master` → 5 = `git log wtbranch origin/master`.
  La scelta sopravvive al riavvio (`filteredRef:` in `ui-state.json`).
- **Righe artificiali** (`2eb3bd700`, cablaggio `096466647`) — la griglia alza ora
  `ArtificialRevisionSelected` con un **contratto scritto nel doc comment** (Diff = `git diff` /
  `git diff --cached`; File tree = worktree / index; Commit details e GPG **non hanno un oggetto
  commit**, quindi vogliono un placeholder, mai il contenuto del commit precedente). L'host pulisce
  File tree e GPG e marca stantii tutti i tab: **il contenuto del commit precedente non resta più
  lì**, che era il difetto vero. Il resto è registrato come **1.14b**, non spacciato per fatto.
- **Crash aprendo un repo dalla Dashboard** (`033bc81f9`) — chiuso: item `null` nel template di
  riga (la trappola del riciclo disabilitato, la stessa di `BlameView` in M51). Verificato su
  **10 aperture** (5 col clic, 5 con ↓+Invio), zero eccezioni, stesso PID.
- *Segnalato invece di lasciarlo rotto in silenzio* (voce **0.32**): con la finestra stretta il box
  "Filter:" finisce nel menu di overflow `»` e lì **non riceve i caratteri digitati** — il
  `MenuItem` li mangia. Preesistente, indipendente dall'Invio.

**M60** (2026-07-28) — **file list del commit dialog, cinque dialoghi, toolbar e logo**. Tre
subagent, l'ultima ondata del round.

- **Commit dialog** (`c96958824`…`538083ece`, nessun cablaggio) — 15 voci nuove nel menu
  contestuale delle due liste file: reset a HEAD, difftool (index→worktree per unstaged,
  HEAD→index per staged), open/edit del file, show in folder, save as, **rename/move** (`git mv`),
  **delete** (`git rm -f` se tracciato, unlink se no), file history, blame, `.git/info/exclude`,
  **skip-worktree** e **assume-unchanged** — più una voce che upstream non ha e qui serve:
  **Restore skipped / assumed-unchanged**, perché i due bit fanno sparire il file da entrambe le
  liste e senza di essa sarebbe una porta a senso unico.
  *Verificato con git reale*: `H a.txt` → `S a.txt` e ritorno dopo Restore; reset a HEAD su un file
  **staged** ripristina il contenuto e toglie anche lo staged (a differenza di Discard);
  `git mv` produce `RM a.txt -> moved/a2.txt`; delete distingue tracciato (`D  sub/c.txt`) da
  untracked (rimozione dal disco), con conferme diverse.
  **0.30 chiuso**: "Commit & push" non ri-punta più l'upstream. Command line effettiva dal tab del
  processo: ramo **già tracciante** → `git push --progress origin main:refs/heads/main`, **nessun
  `-u`**; ramo nuovo + "No" → nessun `-u` e upstream non scritto; ramo nuovo + "Sì" → `-u` e
  `origin/feature2`. Più quattro opzioni del dialogo ora persistite.
  *Omesse con motivo* (non voci morte): reset-chunk e `add -p` (il primo è già coperto dal pannello
  diff, il secondo vuole un terminale interattivo); tutto ciò che richiede **revisioni** (cherry-pick
  changes, i quattro difftool fra commit, "open this revision") perché queste liste contengono
  worktree e index, non commit; *Show in file tree* e *Filter in grid*, che agiscono sulla finestra
  principale mentre il dialogo è **modale** — il risultato sarebbe invisibile fino alla chiusura;
  il blocco submodule (manca reset/stash/commit dentro il submodule in `SubmoduleService`).
- **Cinque dialoghi** (`8477ddb76`…`1ef143cca`, cablaggio `c3d40054f`) — **`git clean -X` è
  finalmente raggiungibile**: nuovo `CleanupDialog` con i tre modi, directories, submodules,
  filtri include/exclude e un **dry-run ripetibile mostrato prima di cancellare**. *Prova*: su un
  repo con untracked e ignorati, "solo ignorati" ha elencato `build/` e `debug.log` nel preview e
  dopo l'esecuzione `git status --short --ignored` mostrava gli untracked ancora lì e gli ignorati
  spariti. Nuovo `InitDialog` col tipo **Central** (`--bare --shared=all`, verificato:
  `core.bare=true`, `core.sharedRepository=2`). `CloneDialog`: subdirectory editabile, preview della
  destinazione, **init dei submodule** (prima non avvenivano mai: verificato `libs/sub/subfile.txt`
  presente), depth, bare, e la combo dei branch da `ls-remote`. `ArchiveDialog`: pannello della
  revisione e i due filtri — l'archivio con "solo i file cambiati da un'altra revisione" conteneva
  esattamente `b.txt` e `sub/d.txt`, con l'invariato e il cancellato correttamente assenti.
  `AboutDialog`: versione, `Build <sha> (Dirty)`, versione di git, copyright e **l'attribuzione
  delle icone a Yusuke Kamiyamane (CCA3)**, che è un obbligo di licenza.
  Il vecchio confirm inline del clean e i suoi helper sono stati rimossi dal `MainWindow`.
  *Rinviati*: il commit picker dell'archive (nel port non esiste un picker riusabile → la revisione
  si digita e viene validata con `rev-parse`) e lo storico From/To del clone.
- **Toolbar e logo** (`6c3bd1c78`, `61309ecc3`, `0f355029f`, cablaggio `17aa4b960`) — **shell
  picker** che elenca solo le shell realmente installate (qui Bash/Dash/Sh; `command -v` conferma
  che zsh e fish non ci sono), con la scelta persistita su disco; **dropdown WorkingDir** con
  ricerca dal vivo, preferiti, Open/Close repository e **Ctrl+click che apre una seconda istanza
  vera**; e il **logo** della dashboard, che richiedeva sia la riga nel csproj sia il codice che lo
  carica.
  **0.32 smentita**: il box "Filter:" non finisce mai nel menu di overflow — il pulsante è secondo
  da sinistra e l'`OverflowPanel` scarta da destra, quindi il difetto non lo tocca.
  *Dichiarato non visto*: i tooltip delle tile della dashboard sono compilati ma l'hover sintetico
  non li fa scattare **nemmeno su pulsanti preesistenti** — limite dell'attrezzatura, non verifica.

**M61** (2026-07-28) — **i due difetti della verifica finale**, entrambi con la causa vera diversa
da quella che sembrava.

- **Il TAB perso negli argomenti git** (`3b8b2bb3b`) — «Contained in no branch» compariva per
  **ogni** commit. Non era il parser: `ArgumentBuilder` concatena gli argomenti in **una** command
  line, quindi il TAB dentro `--format=%(HEAD)\t%(refname:short)` **spezzava l'argomento in due**.
  Catturato con uno shim su PATH che logga ogni voce di argv:
  prima `<--format=%(HEAD)>` `<%(refname:short)>`, dopo `<--format=%(HEAD)%09%(refname:short)>`.
  Due occorrenze in `CommitDetailService` (anche `LoadRefHashes`, che rendeva i **pill dei ref link
  morti**). *A schermo dopo il fix*: "Contained in branches: master, feature/alpha, release-2" —
  identico a `git branch --contains`, col branch corrente per primo — e i pill navigano (clic su
  `master` e sul tag `v1.0` spostano la selezione della griglia; prima non facevano nulla).
  **Scansione dell'intero `App/`** per la stessa classe di bug: restano 5 occorrenze di spazi in
  argomenti git, tutte **deliberate e verificate benigne** (due argomenti impacchettati di
  proposito con valori quotati in `FileHistoryService`, le command line del credential helper, e
  una stringa di sola anteprima mai eseguita). Gli usi corretti dell'idioma erano già lì
  (`%x09`, `%x20`, `%09`, e `GitService` che aggira il problema con ASCII 0x1F): i due punti
  corretti erano gli unici veri.
- **Esc che non chiudeva i dialoghi-finestra** (`e6e611558`, `5d6b87fae`, `3869ee1ef`, più
  `cfa035555`) — la causa **non era un handler mancante**: l'input focus X era già sulla finestra
  del dialogo, ma Avalonia instrada `KeyDown` dall'**elemento** focalizzato, e quelle finestre non
  ne avevano alcuno — il che spiega anche perché `Button.IsCancel` non funzionava, viaggiando sullo
  stesso evento. I dialoghi pieni di caselle di testo sembravano a posto solo perché un campo
  prendeva il fuoco da sé. Nuovo helper `Views/DialogKeys.cs` esteso a **25 finestre**, con
  `EnsureFocusRoute` per le 9 che un handler ce l'avevano già e una **overload di veto** perché Esc
  non possa abbandonare un processo git in corso (`GitProcessDialog`) o un'applicazione sparse.
  *Trovato strada facendo*: anche l'**Esc-come-Cancel del commit dialog (M57) era morto** —
  `CommitDialog.cs` è a zero righe di differenza dalla base, quindi l'handler era sempre stato
  corretto: non riceveva mai il tasto. Ora funziona: **ripristinato, non rotto**.
  *Nota di metodo dal subagent*: `FocusManager` in Avalonia 11 è **a livello di applicazione**,
  quindi `GetFocusedElement()` continua a restituire un controllo della finestra principale mentre
  un dialogo è aperto; un primo tentativo basato su un null check non funzionava, e la verifica
  buona confronta i **visual root**.
  Il loop ha poi spostato l'installazione dell'Esc del picker dal self-install nella view al suo
  chiamante, dov'è il posto giusto.
  *Verificate a schermo 5 finestre* (Open, About, Commit, Create branch, Cleanup) più le
  non-regressioni; le altre 20 sono coperte dall'helper condiviso e dalla build, **non aperte una
  per una**.

**Interruzione**: il limite di sessione ha ucciso tre subagent a metà (verifica GUI di M53, albero
sinistro, File tree+GPG). I due worktree contenevano ~1100 righe **non committate** ciascuno; le
diff sono state salvate in `/tmp/loop-salvage/*.patch` e gli agent sono stati **ripresi dal loro
transcript** invece di ripartire da zero. Lezione: istruire i subagent a **committare presto e
spesso**, non solo a fine unità.

**Nota di metodo** (costata due tentativi): `pkill -f "<pattern>"` negli script di verifica GUI
**uccide la shell che lo lancia** se il pattern compare anche nella propria riga di comando (es.
`pkill -f "Xvfb :151"` invocato da un comando che contiene quella stessa stringa). Usare un pattern
auto-escluso (`Xvf[b] :151`) o `kill <PID>`.

**M62** (2026-07-28) — **il tema scuro che anneriva le console al clic**, segnalato dall'utente con
uno screenshot del dialogo `Process — Push`: cliccando il testo la console diventava tutta nera.

- **Causa reale** (`0e5a91b49`): non i colori locali ma la **precedenza degli style setter sui figli
  di template**. Il `ControlTheme` Fluent del `TextBox` (Avalonia 11.3.14, verificato decompilando
  `Avalonia.Themes.Fluent.dll`) negli stati `:pointerover`/`:focus`/`:disabled` ridipinge **non il
  `TextBox`** ma il figlio `Border#PART_BorderElement`, con `TextControlBackground{PointerOver,
  Focused,Disabled}` → `Border.Background` (più le analoghe per bordo e foreground) — e uno style
  setter **batte il valore locale**. Quindi `Background = #ECE9D8` valeva solo nello stato normale:
  al clic il fondo passava al colore del tema, **misurato `#000000` in scuro e `#FFFFFF` in chiaro**,
  mentre il foreground restava `#101010`. Nota: `TextControlSelectionHighlightColor`, malgrado il
  nome, alimenta `TextBox.SelectionBrush` ed è tipizzata **`IBrush`**, non `Color`.
- **Soluzione**: nuovo `App/Theming/TextBoxSurface.cs`, che popola le chiavi consumate dal template
  nelle `Resources` **dell'istanza** (background/foreground/bordo/placeholder × 4 stati + selezione +
  spessore del bordo) tenendo i brush **per riferimento**, così il cambio tema a caldo continua a
  funzionare (`ThemeManager` muta i colori in place). Console misurata identica (`#DEDBCB`) prima del
  clic, dopo il clic e dopo un drag di selezione; selezione ora `#007ACC` con inchiostro bianco.
- **Estensione** (`b8b57f991`) a nove superfici di testo **read-only**: i log di `CleanupDialog` e
  `CloneDialog`, `OutputView._detail`, gli output di `SubmodulesDialog`/`WorktreesDialog`/
  `MaintenanceDialog`, i due box di `GpgView` e il branch locale read-only di `PullDialog`. I due log
  erano il vero problema di leggibilità: in tema chiaro il focus portava il fondo a bianco col testo
  `#D0D0D0`, **da 7,42:1 a ~1,5:1**. Lasciati fuori di proposito **tutti gli input editabili** (le
  caselle di ricerca, il filtro, i box dei dialoghi): lì il riempimento al focus è un'affordance
  reale e resta leggibile; fissarlo cancellerebbe l'indizio di focus.
- **Difetto accoppiato scoperto e chiuso** (`3b73b44bc`): la chiave `App.Control` era letta in ~20
  punti ma **mai registrata** in `ThemeManager`. `Brush("App.Control", Brushes.Black)` restituiva
  quindi il fallback nero, che non segue il tema → in tema **chiaro** quei box erano testo quasi nero
  su nero, **illeggibili a riposo** e resi leggibili solo dal riempimento bianco del focus; e
  `B("App.Control")` restituiva **null**. Registrata con i valori di `App.Panel` (`#252526` scuro,
  `#FFFFFF` chiaro): nessuna tinta nuova. Verificato in GUI a tema chiaro che la casella di ricerca
  dell'albero è ora bianca con testo scuro.
  ⚠️ Restano **non registrate** `App.ConsoleBackground` e `App.ConsoleForeground` (`CleanupDialog`,
  `CloneDialog`): lì il fallback è un terminale scuro invariante al tema, coerente col beige fisso del
  process dialog, e fissarlo è stabile per costruzione — ma se un giorno servono theme-driven, vanno
  registrate.
- *Non verificati in GUI*: `CleanupDialog._log`, `SubmodulesDialog._output`,
  `MaintenanceDialog._output`, `PullDialog._localBranchBox` (stessa modifica di una riga e stessa
  coppia di colori di un fratello verificato) e i due box di `GpgView`, che rendono il `TextBox` solo
  per un commit **firmato**, assente nei repo di prova.
- **Nota di metodo**: il primo tentativo di estensione era stato scritto su una base vecchia
  (`a3aae6d0b`) e **non era cherry-pickabile** — i file erano cambiati troppo nel frattempo. È stato
  riapplicato da zero sull'HEAD corrente. Prima di delegare, allineare la base del subagent all'HEAD
  vero: il branch può essere avanzato di molti commit rispetto a quello che il loop ha in mano.

## ROUND 12 — commit dialog e flusso di merge — **CHIUSO** (M71–M72)

> **Esito del round**: la "Coda round 12" non ha più **nessuna** voce `- [ ]`. Tutte e nove le voci
> chiuse in **due iterazioni** invece delle dieci concesse, con sei subagent Claude in worktree
> isolati. Il flusso degli screenshot dell'utente esiste ora per intero e **è stato percorso dal
> loop dall'inizio alla fine**: merge → process dialog → conferma → resolve → kdiff3 → commit →
> banner spento.
>
> **Le premesse erano vere in tutti i casi tranne una** (nessuna voce stantia come nel round 11, dove
> tre unità su tre di un'iterazione erano già fatte): la sola correzione è che `PullDialog.ShowAsync`
> aveva **già** il parametro `solveConflicts`, mai passato da nessuno.

> **Iterazione 1 / 10.** Tre subagent Claude in worktree isolati su file disgiunti (A commit dialog,
> B MergeDialog, C ResolveConflictsDialog), più il cablaggio dei call-site fatto dal loop. Base
> `c9a9d2ec0`, build `Errori: 0` dopo ognuno dei 7 cherry-pick integrati finora.

**M71** (2026-07-29) — **12.A.1, 12.A.2, 12.A.3, 12.B.1 e 12.B.2 chiuse**. Unità A (4 commit) e B
(3 commit) integrate, più il cablaggio `0f671da88`.

- **Il diff di un file nuovo non è più vuoto** (`805af7125`, `237a2baa0`). Premessa **vera e
  misurata**: `git diff -- newfile.sh` su un untracked restituisce **0 byte con exit 0**, quindi il
  pannello restava bianco senza errore. Ora il file nuovo rende un patch vero.
  *Riverificato dal loop* sull'HEAD integrato: `newfile.sh` mostra `new file mode 100644`,
  `--- /dev/null`, `+++ b/newfile.sh` e le tre righe `+`; `blob.bin` dà
  `Binary files /dev/null and b/blob.bin differ` invece di byte grezzi; `big.txt` (20000 righe)
  rende il patch con troncatura.
  **Trappola nuova e riusabile**: `isNewFile` di `PatchManager` significa "nuovo **nell'indice**",
  non "untracked" — riscrive `--- /dev/null` in `--- a/<name>` e toglie `new file mode`, rendendo il
  patch inapplicabile a un path assente dall'indice (upstream per gli untracked usa un builder
  diverso che scrive un blob). Per questo il line-staging su untracked ha richiesto un secondo
  commit. Seconda trappola: `FirstLine()` su output **streaming** pesca l'header del comando, non
  l'errore.
- **Il commit passa dal process dialog** (`bb89784e4`). Premessa **vera**: l'output di un hook reale
  non compariva da nessuna parte. Ora il commit gira in `GitProcessDialog` come `FormProcess`
  (`FormCommit.cs:1265`).
  *Riverificato dal loop* con un `pre-commit` che stampa una riga: il dialogo mostra
  `Command to be executed: git commit -F "/tmp/tmp….tmp"`, **la riga dell'hook**
  (`PRE-COMMIT HOOK RAN: checking staged files`), il riepilogo `[master a8bee488] commit via process
  dialog / 1 file changed, 1 insertion(+)`, stato **Success**, footer `Keep dialog open`/`OK`/`Abort`
  e la casella `Reply:` del path PTY. Il subagent aveva già dimostrato l'hook che **rifiuta**: output
  visibile, messaggio di commit **non** svuotato, `git log` invariato.
- **I due reset passano da un `ResetChangesDialog`** (`efe3b4ed7`). Premessa **vera**: il ramo
  unstaged non aveva **alcuna** conferma e `ResetChanges` non eseguiva mai `clean`, con i due
  pulsanti mai disabilitati.
  *Riverificato dal loop*: il dialogo dichiara i conteggi **reali** ("Unstaged changes in 1 tracked
  file(s) will be reverted; staged changes are kept." + "3 untracked file(s) are also present") e
  offre la checkbox `Also delete new files and/or directories`; spuntandola e premendo Reset,
  `tracked.txt` torna a `base`, i **tre** untracked sono cancellati e `git status --porcelain` è
  **vuoto**. Deviazioni: path espliciti invece di `git checkout -- .` (escludendo i path unmerged che
  git rifiuterebbe), e unstage/reset **per riga** su un untracked risponde con un messaggio esplicito
  invece di un errore criptico di git.
- **`MergeDialog`, e il merge non è più muto** (`56005776a`, `ca21a5b52`, `43304dcd4`, cablaggio
  `0f671da88`). Premessa **vera e misurata**: `BranchTagService.cs:633-647` passava
  `allowFastForward: true, squash: false, noCommit: false, strategy: "", allowUnrelatedHistories:
  false` — tutti cablati, nessun overload — e i quattro chiamanti lo lanciavano dentro `RunMutation`;
  `grep MergeDialog|FormMergeBranch` dava **zero** occorrenze.
  Nuova API: `record MergeOptions(...)` + `MergeBranch(repo, name, options)` +
  `MergeBranchStreaming(...)` + `MergeDialog.ShowAsync(owner, repo, defaultBranch, execute)` che
  ritorna `MergeDialogResult(Branch, Options, Executed, Success, Output)` — dove `Success == false` è
  il segnale di conflitto per l'unità D. Il vecchio `MergeBranch(repo, name)` è rimasto, così i
  call-site compilavano prima del cablaggio.
  Il loop ha instradato i quattro call-site **togliendo** il wrapper `RunMutation`/`RunRefOp`: il
  dialogo esegue già il merge, quindi il wrapper avrebbe lanciato git **due volte**.
  `RevisionGridView` perde anche la conferma nuda "Merge 'x' into 'y'?", superata dal dialogo.
  *Verificato in GUI dal loop* su `/tmp/r12int`: tasto destro su `branch1` → `Merge into current
  branch…` → il dialogo si apre con `branch1` preselezionato e `Into current branch **master**` →
  `Merge` ⇒ process dialog con `git merge --no-edit branch1`,
  `CONFLICT (content): Merge conflict in README.md`, stato **Failed**, e sul disco un merge
  conflittuale vero (`MERGE_HEAD` presente, `README.md` in `UU`). Il subagent aveva già coperto
  fast-forward (0 merge commit), `--no-ff` (merge commit `717bffc`), `--no-commit` (`MERGE_HEAD` +
  staged), `--strategy=resolve --log=20` con messaggio custom, e `--squash` (`SQUASH_MSG`): le
  opzioni avanzate **non sono finte**.
  Deviazioni registrate: ~~nessun pannello illustrativo e nessun link `Hide help` (precedente M50/P3 —
  il link da solo non farebbe nulla)~~ **CHIUSA da M74**: pannello, link `Hide help`, pulsante
  `Show help`, stato persistito e swap fast-forward su hover sono portati
  (`App/Views/HelpImagePanel.cs`, misure in `NOTES.md`); nessun pulsante commit-picker (`FormChooseCommit` non è
  portato, M69) quindi la combo è **editabile**; messaggio di merge su file temporaneo invece di
  `.git/MERGE_MSG`; nessun hook script `BeforeMerge`/`AfterMerge` (il port non ha motore di script).
  Difetto trovato per strada: sottoscrivere `ComboBox.TextProperty` **spara subito**, e l'eccezione
  dal costruttore faceva sì che il dialogo **non si aprisse mai** con log pulito e finestra X non
  mappata.
- **Nota sul rumore di build**: 31 warning al netto (VSTHRD/CS pre-esistenti). Nessuno viene dai file
  nuovi né dal cablaggio: verificato elencando i `file:riga` di ogni warning.

> **Iterazione 2 / 10.** Tre subagent Claude in worktree isolati su file disgiunti (D `ConflictFlow`,
> E banner del merge, F chrome del commit dialog), più tutto il cablaggio fatto dal loop. Base
> `efe3b4ed7`/`69daf4295`, build `Errori: 0` dopo ognuno dei 9 cherry-pick.

**M72** (2026-07-29) — **12.A.4, 12.B.3, 12.B.4 e 12.B.5 chiuse: il round è finito**. Nove commit
dei subagent più tre di integrazione (`54f04843d`, `dbae1f842`, `963e99119`).

- **`ResolveConflictsDialog` + `ConflictService`** (`27f5f4234`, `aa1b250d9`, `4cf888eb4`) portano
  `FormResolveConflicts`. Distingue **sei** tipi di conflitto letti da `git ls-files --unmerged -z`
  e **mai** dai messaggi di git (che qui sono in italiano): both-modified, both-added,
  deleted-by-them, deleted-by-us più **added-by-us/added-by-them**, che upstream non copre — il suo
  `switch` cade in fondo e lascia in schermo il testo del file precedente.
  Il pulsante `Open in <tool>` prende il nome da `merge.tool` (dinamico, "Open in kdiff3" qui) e
  lancia `git mergetool --no-prompt -- <path>`, non la sostituzione manuale di `mergetool.<tool>.cmd`
  di upstream, che deriva l'eseguibile spezzando `cmd` su `.exe` — Windows-only, come dice il
  commento di upstream stesso.
  *kdiff3 VERO verificato due volte*: dal subagent (tre pane, scelta di B, salvataggio, chiusura, e
  il dialogo che si ri-scansiona da sé facendo sparire il file dalla lista) e **dal loop**
  (`README.md (Base) <-> (Local) <-> (Remote) - KDiff3`, pannello risultato su
  `<Conflitto di fusione>`, "conflitti non ancora risolti: 1").
- **Il banner del merge ha i pulsanti veri** (`7b6fccdc5`, `9267e096e`, `2b3187f00`, cablaggio
  `dbae1f842`). Premessa **vera**: `RepositoryProgressBanner.cs:335-336` era un `TextBlock` dim che
  *consigliava* `git merge --abort`, cioè mandava l'utente in terminale, e `RepositoryProgress` non
  portava alcun dato sui conflitti. Ora i due stati di upstream sono distinti — arancione *"Merge is
  currently in progress with merge conflicts."* con `Resolve…`/`Abort`, grigio *"Merge is currently
  in progress."* con `Continue`/`Abort` — e `MergeSessionService` sta dietro `--abort`/`--continue`.
  **Difetto trovato e corretto**: Fluent dipinge la faccia dei `Button` da un overlay `ButtonBackground*`
  **traslucido**, quindi sull'arancione `Abort` misurava **1,99:1** ed era invisibile. Risolto pinnando
  le chiavi di stato sulle `Resources` dell'istanza (la tecnica di `TextBoxSurface`, M62), perché un
  `Background` locale perde contro gli style setter del `ControlTheme` in hover. Dopo: 7,70:1 (scuro) e
  5,97:1 (chiaro); il fondo dà 10,56:1 / 5,88:1 con l'inchiostro derivato dal fondo in un solo punto.
  Deviazioni: `Abort` chiede conferma (upstream no, ma riscrive il working tree senza reflog);
  `--continue` gira con `GIT_EDITOR=true`, perché il port non ha un editor cablato a git e un `vi`
  ereditato pianterebbe il process dialog per sempre.
- **`ConflictFlow`: la conferma di upstream, e la prima modale Sì/No riusabile del port**
  (`1316c258b`, `74a139d74`, cablaggio `963e99119`). Premessa **vera**:
  `grep MergeConflictHandler|DontConfirmResolveConflicts` dava 0 hit, e
  `ResolveConflictsDialog.ShowAsync`/`HasConflicts` esistevano con **zero call-site** — il dialogo era
  raggiungibile da niente. Il subagent ha censito i punti di cablaggio uno per uno verificando se il
  conflitto è davvero possibile lì; il loop ha cablato: i tre call-site del merge, il pull da toolbar
  (`RunRemoteOp`), il `PullDialog`, cherry-pick e stash pop (`RunOp`), revert, `stash apply/pop` dal
  pannello e dall'albero, e `git am` (`ApplyPatchDialog`, dove `_state.InConflictedMerge` era già
  calcolato e usato solo per abilitare i pulsanti).
  **Correzione alla premessa**: `PullDialog.ShowAsync` aveva **già** il parametro `solveConflicts`
  (`:341-346`) e l'unico chiamante lo ometteva, quindi "Solve conflicts" ricadeva su un mergetool
  nudo: il gancio era a costo zero.
  Non cablati, con motivo: il **rebase** (il conflitto è possibilissimo, ma il port non ha né dialogo
  di rebase né un solo `git rebase --continue` — la domanda da sola lascerebbe l'utente col rebase a
  metà e nessun pulsante), i **merge di submodulo** (il conflitto vive dentro il submodulo, l'indice
  del super-repo resta pulito), `StashDrop` e `PatchService` (non lasciano conflitti), e
  `AvaloniaGitUICommands.StartResolveConflictsDialog`, che è un punto reale ma con firma sincrona
  `bool` e nessun riferimento a una `Window`: decisione semantica, non un gancio.
  `DontConfirmResolveConflicts` esiste come flag ma **non** ha UI: il port non ha la pagina
  Confirmations (nessuna delle 17 checkbox di upstream è portata).
- **La chrome del commit dialog** (`91c58cd89`, `3195ed6bf`, `1ef18ad35`, `219056c39`): tutte e quattro
  le divergenze chiuse. Status bar con `Committer` dai valori effettivi (col filler
  `/user.name not configured/` di upstream), `branch → push target` con le regole esatte di
  `FormCommit.UpdateBranchNameDisplayAsync` (upstream configurato → `origin/branch (untracked)` →
  `(remote not configured)` → niente se HEAD non è su un branch locale: **nessuna stringa inventata**),
  `Staged x/y` reale e `Ln/Col`. Gutter a **due colonne** parsato dagli header `@@ -a,b +c,d @@`
  (contesto entrambi, `+` solo nuovo, `-` solo vecchio; diff combinato `@@@` → gutter **vuoto**
  anziché numeri sbagliati). Toolbar e casella filtro regex **per lista**.
  Scoperte: upstream prende `Ln/Col` dal caret del **messaggio** (`FormCommit.cs:2428-2429`), non dal
  diff — il port segue entrambi, vince l'ultimo mosso; e `SelectableTextBlock` e `TextBlock` impaginano
  lo stesso font a **passi diversi** (19,0 vs 17,9 px/riga), quindi i numeri scivolavano di una riga
  intera e `VerticalAlignment=Top` non bastava: serve un `LineHeight` esplicito uguale.
  **Il line-staging su untracked è stato riverificato due volte** (dopo il gutter e dopo le toolbar):
  nell'indice finisce solo la riga scelta. Contrasti: status bar 11,17:1 / 16,67:1, numeri del gutter
  5,51:1 / 5,41:1.
  `FileStatusListView` **non** riusato, con misura: la sua superficie è tutta su `DiffFileRow`, mentre
  il line-staging dipende da `WorkingDirFileRow.Status`/`IsStaged` — la conversione sarebbe stata
  lossy proprio lì. Escluso per non fare pulsanti finti: albero/lista piatta, `git grep` find-in-files,
  righe skip-worktree/assume-unchanged, file ignorati (nel port nulla fa `git status --ignored`).
- **Difetto trovato dall'unità C in un file di un'altra unità, corretto dal loop** (`54f04843d`): i
  path dei conflitti arrivavano a `GitArgumentBuilder` **senza quote**, quindi take-ours / take-theirs /
  mark-resolved del `CommitDialog` **fallivano in silenzio** su un nome con lo spazio. *Verificato in
  GUI* su un merge che conflitta su `a file.txt`: prima il clic non faceva nulla, dopo il file resta col
  contenuto nostro e sparisce dagli unmerged.
- **Collaudo end-to-end del loop** (screenshot in `/tmp/r12e2e-*.png`, tutti guardati): merge di
  `branch1` → dialogo con `branch1` preselezionato e `Into current branch master` → process dialog con
  `git merge --no-edit branch1` e `CONFLICT (content): Merge conflict in README.md` → **la domanda**
  *"There are unresolved merge conflicts, solve conflicts now?"* con Sì/No → `ResolveConflictsDialog`
  → `Choose local/current (ours)` → il dialogo **si chiude da sé** e il banner **passa da solo** allo
  stato grigio con `Continue` → `git merge --continue` ⇒ `[master 3c2da511] Merge branch 'branch1'` →
  banner **spento**, merge visibile nel grafo, tree pulito, nessun `MERGE_HEAD`.

**Trappole nuove di questo round, da NON riscoprire**
- **`isNewFile` di `PatchManager` significa "nuovo nell'INDICE", non "untracked"**: riscrive
  `--- /dev/null` in `--- a/<name>` e toglie `new file mode`, rendendo il patch inapplicabile a un path
  assente dall'indice (upstream per gli untracked usa un builder diverso che scrive un blob).
- **`FirstLine()` su output streaming pesca l'header del comando**, non l'errore.
- **Sottoscrivere `ComboBox.TextProperty` spara subito**: l'eccezione dal costruttore faceva sì che il
  `MergeDialog` **non si aprisse mai**, con log pulito e finestra X non mappata.
- **Fluent dipinge i `Button` con un overlay traslucido**: su un fondo colorato il testo può crollare a
  2:1 e sparire, e un `Background` locale non basta (serve pinnare le chiavi di stato).
- **`SelectableTextBlock` e `TextBlock` non impaginano allo stesso passo** con lo stesso font.
- **Ambiente**: la sessione esporta `WAYLAND_DISPLAY` **e** `XDG_SESSION_TYPE=wayland`, quindi un figlio
  Qt come kdiff3 gira ma **non mappa nessuna finestra** sotto Xvfb: lanciare l'app con
  `env -u WAYLAND_DISPLAY -u XDG_SESSION_TYPE QT_QPA_PLATFORM=xcb`. Non è un difetto del port, che
  lancia solo `git mergetool`.
- **Un'azione distruttiva può avere una conferma in attesa**: una misura "non ha funzionato" era in
  realtà un dialogo di conferma non ancora premuto. Guardare lo screenshot **prima** di concludere.

**Residui aperti registrati** (nessuno bloccante): lo stato "conflitti senza operazione in corso"
(dopo uno `stash pop` conflittuale) non è nel banner, perché rilevarlo costerebbe un `git diff` a ogni
refresh anche su repo inerti — servirebbe una cache dello stato dell'indice; rebase/`am`/cherry-pick/
revert non hanno pulsanti nel banner (nessun service dietro il loro `--continue`/`--skip`); la scelta
fast-forward del `MergeDialog` è ricordata **globalmente** e non per repository
(`GetEffectiveSettings().Detached()` è uno store condiviso); "Open/Save `<side>` as" e la file history
del dialogo dei conflitti non sono portati; la persistenza di sort-key e toggle untracked delle nuove
toolbar richiederebbe campi in `AppPreferences`; i due commenti ora stantii in `ApplyPatchDialog.cs:51`
e `PullDialog.cs:718` dicono ancora che il port non ha `FormResolveConflicts`.

## M73 (2026-07-30) — la superficie del rebase, il residuo del round 12

> Unità singola, un subagent Claude in worktree isolato + il cablaggio del loop. Base `4a34cd5b8`,
> build `Errori: 0` dopo ognuno dei 4 cherry-pick. **Nata da una domanda dell'utente**: perché il
> rebase fermo nel suo `~/test-avalonia` non si potesse chiudere dalla GUI.

Chiude il residuo che M72 aveva registrato con il suo motivo: *"rebase non cablato a `ConflictFlow`
perché il port non ha modo di continuarlo — la domanda da sola lascerebbe l'utente col rebase a metà
e nessun pulsante"*. Ora il modo c'è, quindi la domanda è stata cablata.

- **`App/Services/RebaseSessionService.cs`** (nuovo, `a0ce516f2`): `Read(repoPath)` →
  `RebaseSessionState` (`InProgress`, `Interactive`, `HasUnresolvedConflicts`, `Step`/`TotalSteps`,
  `HeadName`, `Onto`, `StoppedSha`, più le regole di upstream come `CanContinue`/`CanSkip`/`CanAbort`)
  e `Continue`/`Skip`/`Abort(repoPath, emit)`. Stato letto **strutturalmente** — `GetRebaseDir()`,
  `InTheMiddleOfRebase()` e i file marker `done`/`git-rebase-todo`/`msgnum`/`end`/`head-name`/`onto`/
  `stopped-sha` — mai parsando i messaggi di git, che qui parla italiano.
- **I pulsanti nel banner** (`79ef436ee`): `Continue`/`Skip`/`Abort`, più `Resolve…` quando il rebase
  è fermo su conflitti, riusando l'evento `ResolveConflictsRequested` che il banner **aveva già** (il
  loop lo cabla già a `ResolveConflictsDialog`: **zero righe nuove in `MainWindow`**). `Abort` dietro
  conferma, come il merge. `GIT_EDITOR=true` pinnato: senza, `git rebase --continue` su un `edit`
  aspetterebbe `vi` e **pianterebbe il process dialog per sempre**.
- **Due stati distinti, e il testo dice la verità**: fermo **senza** conflitti →
  *"Interactive rebase is paused — no conflicts to resolve. Step N of M."*; fermo **con** conflitti →
  banner arancione *"…is currently in progress with merge conflicts."*. Non dice "risolvi i conflitti"
  quando non ce ne sono — era il difetto del suggerimento testuale che c'era prima.
- **Cablaggio del loop** (`a1a40c3ce`): i quattro entry point del rebase (`BranchTagPanel`,
  `RepoObjectsTree` ×2, `RevisionGridView`, tutti via `BranchTagService.RebaseOnto`) girano ora
  **fuori** dai wrapper fire-and-forget `RunMutation`/`RunRefOp`, così l'attesa può esserci e
  `ConflictFlow.HandleAsync` gira **dopo** che git si è fermato. `HandleAsync` chiede solo su
  `ConflictedMerge`, quindi un rebase fermo su un `edit` **non** viene interrogato: corretto.
- **Difetto di contrasto trovato misurando** (`68af816d2`): l'inchiostro del banner è **derivato** dal
  fondo, non una chiave tematica, quindi un cambio tema **a caldo** lasciava inchiostro nero sul rust
  del tema chiaro a **3,52:1** fino al refresh successivo. Risolto agganciandosi a
  `ActualThemeVariantChanged`; rimisurato **5,97:1** senza refresh in mezzo. Correggeva anche la barra
  del merge di M72.
- Deviazione scelta dal subagent: `Continue` resta in riga e **si spegne** mentre l'indice è
  conflittuale, con `Resolve…` accanto, invece dello scambio di visibilità di upstream — con quattro
  pulsanti lo scambio farebbe ballare gli altri sotto il puntatore. La **regola** è quella di
  upstream. Conseguenza registrata: lo stato merge nella stessa barra scambia ancora (M72, lasciato
  com'era), quindi i due stati della barra non sono coerenti fra loro.
- *Verificato in GUI dal loop* su due fixture: rebase fermo su `edit` (indice pulito, il caso
  dell'utente) ⇒ `Continue` porta a *"Successfully rebased and updated refs/heads/master"*, banner
  spento, nessun `.git/rebase-merge`; rebase fermo su **conflitto** ⇒ banner arancione con `Resolve…`
  e `Continue` spento, il dialogo dei conflitti mostra i lati **invertiti** (`Local/current (theirs)`
  / `Remote/incoming (ours)`) — corretto, in rebase `ours`/`theirs` di git sono scambiati — e dopo la
  risoluzione `Continue` chiude il rebase.
- **Non fatto, con motivo**: editing del todo interattivo (`git rebase --edit-todo`) — servirebbe una
  griglia del todo più uno shim `GIT_SEQUENCE_EDITOR` puntato al port: è un'unità a sé e **non è
  promessa da nessun controllo** nella UI. Cherry-pick e revert restano senza service dietro
  `--continue`, quindi nel banner hanno ancora solo il suggerimento testuale.

## M75 (2026-08-01) — le mutazioni di ref passano dal process dialog; diagnosi di 13.1

Chiude **13.2** e **13.3** della coda round 13; **13.1 resta aperta** con una diagnosi parziale
(dettaglio e verdetto nella voce stessa, qui sotto).

**Il difetto di fondo**: nel port creazione branch e checkout giravano dentro wrapper
fire-and-forget e **muti** — `RepoObjectsTree.RunMutation` su fallimento non faceva *nulla*, né
messaggio né output né refresh. Upstream esegue entrambe le operazioni dentro `FormProcess`.

- `3196e8f04` — `BranchTagService`: `CreateBranchStreaming`, `CheckoutStreaming`,
  `CheckoutBranchStreaming`. Costruiscono le **stesse** stringhe di argomenti dei gemelli esistenti
  (`Commands.Branch`/`CreateOrphan`/`Checkout`/`CheckoutBranch`, più il pre-step di stash) e le
  fanno passare da `GitStreamRunner` — obbligatorio, perché il core `IExecutable`/`IProcess`
  bufferizza stderr, dove git scrive il progress. Le vecchie firme sono intatte.
- `2930ffbe4` — `App/Views/RefProcessRunner.cs`: rotta unica "operazione → `GitProcessDialog`
  → esito". Ritorna `true` **solo** se git esce 0 **e** l'utente non ha premuto Abort. `owner` può
  essere null (fallback alla main window, mai un'eccezione).
- `ca222145d` · `92c548216` · `dd7fa502e` · `4b504d234` — i **10 call-site** cablati:
  `RepoObjectsTree` (create, checkout, `-B`/remoto), `BranchTagPanel` (create, checkout),
  `MainWindow` (dropdown toolbar, "Create branch here…", menu Commands), `CommitDialog`,
  `RevisionGridView`.
- `3fafaa1f9` — irrobustimento delle guardie `_busy` dell'albero (vedi 13.1).

**Trappole registrate durante il cablaggio:**
- Il vecchio wrapper va **rimosso** attorno alla chiamata, non lasciato: altrimenti git gira **due
  volte** (`DoMergeAsync` già avvertiva di questo). `RunMutation` resta però in vita per
  Reset/Rename/Delete.
- **Refresh e flag busy restano al call-site.** In `MainWindow` il cablaggio **non** è stato una
  riga sola: `RunOp` possedeva anche la sospensione del watcher, la status line, `RefreshAll()` e
  `ConflictFlow.HandleAsync` — conservati in `RunRefProcessAsync`, stessa forma senza `Task.Run`.
- In `RepoObjectsTree` `_busy` va azzerato **prima** del refresh: `Refresh()` (`:610`) è esso stesso
  guardato da `_busy` e sarebbe stato un no-op silenzioso.
- **Refresh anche su `false`/Abort**, in tutti i punti: un checkout interrotto può aver già mosso
  HEAD, e mostrare uno stato stantio è il fallimento peggiore.
- Niente `Task.Run` attorno all'helper: apre un modale e fa da sé il threading.

## M76 (2026-08-01) — riscontro dell'utente su M75: `Keep dialog open` e `Delete branch`

Due difetti trovati dall'utente **provando la build di M75 sul repo vero** (i branch `test`, `test1`,
`prova` nel reflog sono le sue prove; il checkout funziona).

**`Keep dialog open` non ricordava la scelta e non chiudeva il dialogo** (`955d57e64`). Unica causa
per entrambi i sintomi: un `IsChecked = true` cablato nel costruttore. Poiché `Settle()` legge la
casella quando il run finisce — e un checkout finisce in poche centinaia di ms, prima che l'utente
ci arrivi — il ramo di auto-chiusura non veniva praticamente mai preso.
**Verdetto sull'originale, contro l'ipotesi dell'utente ("un booleano per tipologia di comando")**:
upstream ha **un solo flag GLOBALE** condiviso da tutti i process dialog —
`AppSettings.CloseProcessDialog`, chiave piatta `"closeprocessdialog"`
(`GitCommands/Settings/AppSettings.cs:1336-1340`), letto a `FormStatus.cs:50` e riscritto a `:276`,
editabile anche da Impostazioni → Generale (`GeneralSettingsPage.cs:90,114`). L'auto-chiusura scatta
**solo su successo** e senza ritardo (`FormStatus.cs:190`); l'unica modulazione per call-site è
`useDialogSettings: false`, che **nasconde** la casella e disabilita l'auto-chiusura — tutto o
niente, non una memoria per comando. **L'utente, messo davanti alla misura, ha scelto la fedeltà a
upstream**: flag globale. Persistito in `view-prefs.json` (non in `ui-state.json`: il dialogo è
modale e sparisce prima che `MainWindow.PersistLayout()` riserializzi quel file, che lo
sovrascriverebbe — stessa ragione di `HelpPanels`). Differenza pre-esistente lasciata: l'auto-chiusura
del port aspetta 800 ms (`Settle()`), upstream chiude subito.

**`Delete branch` non cancellava nulla e non mostrava niente** (`79a638e21`, `116a88d0d`).
**NON è una regressione di M75**, misurato: `git diff 22dfc4d1b..HEAD` filtrato sul percorso del
delete dà **zero hunk**. Due difetti sovrapposti e preesistenti:
1. `RepoObjectsTree.cs:2259` cancellava con **`force: false`** → `git branch --delete "x"`, che su un
   branch non mergiato esce con `error: the branch 'x' is not fully merged` (riprodotto in un repo
   scratch). Upstream passa **sempre `force: true`** (`FormDeleteBranch.cs:118`) **dopo aver
   avvisato** (`:90-116`).
2. `RunMutation` è muto sul fallimento → nessun messaggio, nessun dialogo: il click sembrava inerte.
Fix: `DeleteBranchStreaming` + `IsBranchMerged` (`git merge-base --is-ancestor <b> HEAD`; detached
HEAD ⇒ `false`, come upstream) + `DeleteRemoteBranchStreaming` (`git push <remote> --delete`), e i
tre call-site (albero locale, albero remoto, `BranchTagPanel`) portati sul process dialog con
l'avviso "not fully merged" che promuove a `--force` — rispondendo **No** non parte nulla.
`RunMutation` resta per reset/rename/tag/remoti e per il "Delete All" del folder node (upstream lì
cancella solo i merged, quindi lasciato com'è).

**Aperto, registrato qui e non risolto**: su Windows il port tenta la PTY di Linux e la console
mostra `<pty: Unable to load DLL 'libc'…>` seguito da `<no pseudo-terminal available; falling back to
non-interactive git>`. Il comando gira lo stesso, ma la casella `Reply:` del process dialog è di
fatto inerte su Windows (niente prompt interattivi di git). Non segnalato come priorità dall'utente.

## M77 (2026-08-02) — il grafo non unisce più branch che non lo sono

> Unità singola, scritta e verificata dal loop. Base `22dfc4d1b`, build `Errori: 0`, nessun warning
> nuovo. **Nata da una segnalazione dell'utente**: «a volte si incasina sul grafo dei branch e
> visualizza come uniti branch che non lo sono, ad esempio quando mi sposto su un branch».
> Riprodotta, non era un'impressione.

**Il difetto.** Le righe artificiali "Working directory"/"Commit index" non passavano dal
layout del DAG: la loro riga verso HEAD era **dipinta sopra** dopo il fatto, da
`RevisionGridView.WithHeadConnector`, che aggiungeva un segmento nella **lane di HEAD** su *ogni*
riga sopra HEAD. Il layout non ne sapeva niente. Quando HEAD non era la riga in cima — cioè dopo
il checkout di **qualsiasi** branch che in ordine di data sta sotto a un altro — quel tratto
attraversava righe la cui lane era libera oppure, peggio, già occupata da un ramo scorrelato: i due
si leggevano come **una linea sola**.

Ci si sommava un secondo difetto indipendente: il colore del segmento era `ColorLane = indice di
lane`. Una lane viene **liberata** quando due rami convergono (`BuildGraph`, `lanes[i] = null`) e
`FirstFree` la riassegna più in basso a un ramo che non c'entra nulla — che quindi riceveva lo
**stesso colore nella stessa colonna**. Riprodotto in `/tmp/graphrecy` (due side branch mergiati in
punti diversi di main): con il vecchio codice `sideA` e `sideB` erano entrambi blu in colonna 1,
separati da una riga sola, e si leggevano come un unico ramo lungo.

**La correzione.**
- `RevisionRow.GraphParents` (nuovo, nullable): parenti **solo per il layout**. Le righe artificiali
  lo usano per dichiarare l'arco working directory → commit index → HEAD tenendo `ParentHashes`
  vuoto, così la navigazione del DAG continua a non entrarci né uscirne. È vuoto anche quando HEAD è
  fuori dalla finestra caricata: il nodo resta isolato invece di puntare a un commit che non è quello
  checkoutato.
- `BuildDisplayRows` ora antepone le righe artificiali e **rilancia `RevisionService.BuildRevisionGraph`
  sull'insieme mostrato**: l'arco verso HEAD ottiene una lane propria, instradata come tutte le altre.
  `WithHeadConnector`, `ArtificialSegments`, `_artificialLane` e `_headDisplayIndex` sono spariti.
- `BuildGraph` traccia un'**identità di arco** parallela alle lane (`colors`, allocata quando una lane
  viene occupata ex novo, ereditata quando prosegue) e la mette in `RevisionGraphSegment.ColorLane` e
  nel nuovo `RevisionRow.NodeColor`. Una colonna riciclata cambia colore.
- `RevisionGraphControl` prende `nodeColor` (default `-1` = vecchio comportamento su colonna); le
  righe artificiali ricevono i flag relative/gray come le altre, perché ora possono portarsi dietro le
  lane di rami terzi.

**Verificato in GUI** (Xvfb, screenshot guardati) su tre fixture: `featX` checkoutato con altri due
rami più recenti sopra (prima: un tratto unico da "Working directory" fino a `X2` attraverso `featY`,
`main5`, `merge featX`, `main3`; dopo: colonna propria, topologia corretta), `main` con working dir
**e** index sporchi (catena working directory → commit index → `main5`, `featY` e `featX` in lane
distinte), e il riciclo delle lane (`sideA` arancione / `sideB` blu nella stessa colonna 1). Nessuna
regressione con working dir pulito (riga artificiale assente, grafo identico) né sul repo vero con
`Load 500 more commits` a 1000+ commit.

**Non fatto**: nulla di quanto sopra tocca il caso con **quick filter** attivo, dove le righe mostrate
sono un sottoinsieme non contiguo e la colonna del grafo resta collassata — comportamento invariato e
già dichiarato.

## M78 (2026-08-02) — le linee del grafo non si spezzano più sui merge

> Unità singola, base `bf8dfec51`, build `Errori: 0`. **Segnalazione dell'utente** con screenshot:
> «come mai a volte le linee risultano spezzate?». Diagnosticata **misurando i pixel** dello
> screenshot prima di toccare il codice: la lane verde si interrompeva esattamente a `y=62`, cioè al
> **centro** della riga del merge.

**Il difetto.** In `BuildGraph`, i parent extra di un merge finivano tutti in `nodeOrigin`:

```csharp
int existing = IndexOf(lanes, parents[p]);
int pl = existing >= 0 ? existing : FirstFree(lanes);
SetLane(pl, parents[p], …);
nodeOrigin.Add(pl);          // <-- anche quando la lane era GIÀ occupata da quel parent
```

`nodeOrigin` decide da dove parte la metà inferiore della lane
(`source = nodeOrigin.Contains(i) ? nodeLane : i`). Quando la lane **portava già** quel parent —
cioè un altro ramo ci stava scendendo sopra da righe precedenti — la sua metà inferiore veniva
**ri-sorgentata dal nodo**: la metà superiore restava un **vicolo cieco** a metà riga e il ramo
appariva **spezzato in due**, col frammento sotto che prendeva pure il colore del nodo invece del
suo. Succede su ogni merge la cui seconda parent è già raggiunta da un ramo elencato più in alto —
frequentissimo su una storia con branch di release paralleli, che è esattamente lo screenshot
dell'utente (`Merge branch 'master' into release/6.0`).

**La correzione.** Se la lane è già occupata da quel parent, **continua diritta** e l'arco di merge
diventa una diagonale **in più** dal nodo verso quella lane al bordo inferiore della riga
(`joinEdges`), nel colore del ramo mergiato — non un rimpiazzo del passaggio. La lane nuova
(`existing < 0`) resta invariata: `FirstFree` + colore nuovo + `nodeOrigin`.

**Verificato in GUI** su una fixture minima (`branchB` che scende verso `a1`, merge di `branchA` in
`main` la cui seconda parent è `a1`): prima **4 righe di pixel vuote** nella lane 0 al centro della
riga del merge, dopo **zero**. Poi sulla **storia vera** dello screenshot dell'utente (clone con i
ref potati attorno a `e048b4a94`/`069d8b778`, tag `v6.0.4`): entrambe le lane continue, la diagonale
del merge che si innesta. Nessuna regressione sul caso comune (merge la cui seconda parent prende una
lane nuova: `sideA`/`sideB` in `/tmp/graphrecy`, identico).

**Residuo cosmetico misurato, non corretto**: dove due mezzi segmenti si toccano al centro riga
l'antialiasing lascia **un pixel** di allargamento laterale (misurato: colonne `31,32` che diventano
`31,32,33` alle sole y dei centri riga). Si toglierebbe emettendo **un unico** segmento a tutta
altezza per le lane che passano dritte, ma `ComputeGraphRelatives` distingue le metà con
`bottomHalf = seg.FromY >= 0.5` per propagare i flag relative/gray: andrebbe cambiato in
`seg.ToY >= 1.0`, e non vale il rischio per un pixel.

## ROUND 14 — iterazione 10: M91 (2026-08-04) — attivazione dei parent senza toggle concorrente

Follow-up al doppio clic M90: passando da un submodule profondo al repository
parent, il ramo lampeggiava chiudendosi e riaprendosi e la navigazione poteva non
partire. La causa era il workaround M88: ogni pressione sull'header raggiungeva
il class handler nativo di `TreeViewItem`, che alternava l'espansione, mentre un
callback asincrono tentava poi di ripristinarla. Sul parent espanso il visual
poteva quindi essere smontato prima di `DoubleTapped`.

- Il tunnel di `PointerPressed` intercetta ora soltanto il tasto sinistro sulle
  righe, seleziona/focalizza subito il `TreeViewItem` esatto e impedisce il toggle
  nativo concorrente.
- `ClickCount == 2` attiva una sola volta il nodo risolto. Il terzo clic non
  riattiva; folder e categorie senza target restano inerti.
- Il chevron (`ToggleButton`) bypassa il tunnel e conserva il toggle nativo.
  Menu contestuale, Enter e navigazione tastiera restano invariati.
- Rimossi callback/generation del vecchio ripristino differito, incluso un
  riferimento residuo rilevato dalla prima build d'integrazione.

Commit applicativi: `36b9fc9af`, `b1b525c91`. Review indipendente: nessun
finding. Build finale: 0 errori, 34 warning preesistenti. Harness navigation
snapshot: PASS; hierarchy multilivello: PASS, 7 nodi. La verifica del gesto
pointer reale resta manuale in questa sessione Windows/DPI.

## ROUND 14 — iterazione 9: M90 (2026-08-04) — doppio clic submodule deterministico e con feedback

Regressione residua: il doppio clic su una riga Submodules era intermittente,
senza feedback e talvolta lento. Causa: il routed event `DoubleTapped` ignorava
`e.Source` e attivava `_tree.SelectedItem`; durante il cambio selezione poteva
quindi usare il nodo precedente o nessun nodo.

- Il doppio clic risolve ora il `TreeViewItem` dall'ancestor visuale del target
  reale (testo/icona/header). Enter continua volutamente a usare la selezione.
- Il chevron/`ToggleButton` è escluso: il suo doppio clic fa soltanto toggle e
  non naviga. Folder e categorie restano inerti.
- La status bar riconosce subito il gesto prima di warm-up/discovery; missing e
  non inizializzati mostrano un messaggio esplicito. Il current conserva la
  parità upstream “Open in new instance” ma ora dà feedback immediato.
- Richieste duplicate sullo stesso target vengono coalesciate; un target diverso
  successivo mantiene la semantica last-wins già protetta da epoch/path.

Commit: `1e9a0bf5b`, `1a8abcd7c`. Build: 0 errori, 34 warning
preesistenti. Review indipendente: nessun finding residuo. La verifica pointer
reale resta manuale su questa sessione Windows/DPI.

## ROUND 14 — iterazioni 5-8: M89 (2026-08-04) — switch e menu repository senza attese duplicate

Tre regressioni segnalate dopo M88: switch submodule lento/inaffidabile,
scrollbar orizzontale centrata sul current e dropdown Submodules/Worktrees aperti
solo dopo alcuni secondi.

Diagnosi misurata sulla fixture profonda: `submodule status --recursive` richiede
656–898 ms (picco 1.584 ms); una `DiscoverHierarchy` avvia circa 24 processi Git
e costa 1,8–2,3 s. Ogni switch eseguiva due discovery concorrenti e ogni click
dropdown una terza. Il tree serializzava inoltre il nuovo repository dietro il
refresh obsoleto. `BringIntoView` rendeva visibile l'intera label lunga, spostando
anche X. `StashPanel` poteva scartare un nuovo load quando busy e applicare il
risultato del repository precedente.

- Nuovo `RepositoryNavigationSnapshotService`: snapshot immutabile hierarchy +
  worktree, single-flight per path normalizzato, invalidazione, retry dopo errori
  e isolamento delle generazioni stale. Harness concorrente: PASS (10 caller =
  una factory, invalidazione/retry/stale isolation).
- `MainWindow`, `RepoObjectsTree` e toolbar condividono una sola snapshot per
  switch. Il nuovo repository parte subito e scarta il vecchio epoch; il warm-up
  core resta serializzato ma gira una sola volta in background; solo l'ultimo
  switch avvia i pannelli. Callback toolbar hanno guardie task/epoch/path e marshal
  esplicito sul dispatcher UI.
- Dropdown Submodules/Go-to-superproject e Worktrees mostrano subito dati cached;
  se il prefetch è ancora pending mostrano immediatamente `Loading…` e non
  attendono Git prima di `ShowAt`. Il click successivo usa la cache; un task
  fallito viene riacquisito single-flight.
- `RepoObjectsTree` forza X=0 dopo `BringIntoView` su vero cambio repository e
  ricerca, preservando Y; callback Loaded/Background sono protette da epoch/path.
- `StashPanel` separa repository generation e load epoch: il nuovo repository
  vince sui load vecchi, i refresh same-repo non invalidano mutazioni, e ogni
  mutazione è vincolata a repo/generation attraverso i dialoghi asincroni.

Commit M89: `32e981301`, `9e4aaea5e`, `a07c918c4`, `e226e5f97`,
`ce8c82d2a`, `d969f028c`, `893e14590`, `05b265fd0`, `6096e07be`,
`375dc0bf0`, `3b995372d`. Build: 0 errori. Harness navigation: PASS;
harness gerarchia: PASS, 7 nodi. Review indipendente finale: nessun finding.
Verifica manuale Windows richiesta per la sensazione reale dello switch e i click,
perché l'automazione pointer della sessione DPI rimane inaffidabile.

## ROUND 14 — iterazione 4: M88 (2026-08-04) — il click di riga non collassa più i submodule

Regressione segnalata dopo M87: cliccare l'header di una cartella o di un
submodule richiudeva il ramo. Diagnosi: il class handler Avalonia di
`TreeViewItem` usa il click sull'intero header come toggle; il port lasciava
passare il click sinistro e non era coinvolto alcun refresh. Inoltre la prima
auto-espansione M87 cercava gli ancestor prima che `_nodeParent` fosse popolato.

- `RepoObjectsTree` conserva `IsExpanded` sui normali click di riga e lascia il
  toggle nativo soltanto al `ToggleButton`/chevron.
- L'undo è confinato alla stessa operazione input con generation per nodo e
  `DispatcherPriority.Input`: click successivi e tastiera Left/Right/Space
  invalidano callback obsolete, evitando di annullare un toggle intenzionale.
- La catena current usa una mappa parent locale costruita insieme ai nodi,
  comprese le cartelle intermedie; refresh successivi continuano a usare
  `HarvestState`/`RestoreState` per rispettare collassi volontari.
- Commit: `257cc3fb5`, `268daf261`. Review indipendente: nessun finding residuo.
  Build: 0 errori, 31 warning preesistenti. Harness gerarchia: PASS, 7 nodi.
- Limite: la sessione Windows/DPI non offre un'automazione click affidabile; il
  gesto reale resta da confermare manualmente nell'app aggiornata.

## ROUND 14 — iterazioni 1-3: M87 (2026-08-04) — parità gerarchia Submodules dal super-project

Richiesta: quando il repository attivo è un submodule, mantenere nel
`RepoObjectsTree` l'intera gerarchia dalla radice, con parent, sibling e figli;
allineare inoltre il pulsante toolbar icon-only a `Go to superproject`.

- Confrontata integralmente la semantica upstream (`GitModule.GetTopModule`,
  `SubmoduleStatusProvider`, `SubmoduleTree`, `SubmoduleNode`) prima del porting.
- `SubmoduleService.DiscoverHierarchy` trova la catena completa dei super-project
  tramite Git, normalizza i path assoluti e costruisce dalla radice una traversal
  ricorsiva controllata. I nodi conservano repository dichiarante, path configurato,
  nome config, stato, branch, esistenza e marker current. Missing/non inizializzati
  restano rappresentati. Identità `--absolute-git-dir`, visited set e limite 128
  evitano ricorsioni infinite e coprono `.git` file e linked worktree.
- `RepoObjectsTree` usa lo snapshot rooted nel `Task.Run` già protetto da epoch:
  nodo root esplicito, cartelle intermedie, sibling/discendenti, catena current
  espansa e current in accent/grassetto con `▶`. Apertura e azioni usano path
  assoluti e repository dichiarante; i nodi mancanti non sono apribili. Il nodo
  current mantiene la parità upstream con `Open in new instance` reale.
- Toolbar senza label `Submodules`: alla radice mantiene `SubmodulesManage`; in un
  submodule mostra `NavigateUp`, tooltip `Go to superproject` e il body apre il
  parent immediato. Stato ripristinato dopo `Rebuild` e azzerato in Dashboard.
- Commit integrati, in ordine: `44a4b3b6c`, `e59bd6110`, `05cacd532`,
  `a88847b34`, `15ce1af15`, `d05017780`. Tutti confinati a
  `src/crossplatform/`; nessun push.

Prove automatiche:

- `dotnet build App/GitExtensions.Avalonia.csproj -v q --no-restore`: **0 errori**,
  31 warning preesistenti.
- `dotnet run --project Tests/SubmoduleHierarchy.Harness.csproj --no-restore`:
  **PASS, 7 nodi**. Fixture reale con due sibling, catena ≥4, foglia corrente,
  missing/deinit, nome config diverso dal path, config incompleta, alias ciclico
  con timeout e linked worktree.

Verifica GUI Windows (niente Xvfb disponibile): fixture persistente in
`C:\tmp\ge-submodule-gui`, manifest `states.json`, screenshot in
`C:\tmp\ge-submodule-shots`. Cinque catture reali confermano pannello mai vuoto,
nodo root esplicito, marker current sulla root, icona manage alla radice, cerchio
blu/freccia su nei submodule e label assente. Limite ambientale dichiarato: il
desktop remoto è 1453×775 e Avalonia/DPI rende inaffidabili click sintetici e
scroll; i figli profondi restano sotto il clipping. Catena/current profondo,
missing visibile e interazioni toolbar/doppio clic/menu richiedono quindi conferma
manuale Windows. Non sono dichiarati verificati sulla sola build.

## ROUND 13 — iterazione 8: M86 (2026-08-04) — lo zoom vero, due livelli, e il transform torna senza le sue cause

> Decisione dell'utente sulle tre opzioni di M85: **(b)**, cioè `OverlayPopups = true` più un layout
> transform costruito dalla finestra, **accettando** che i popup restino confinati nei bordi della
> finestra. Base `4ae181d4a`. `Errori: 0`. Commit `c78d9fbc3` (meccanismo), `09ad0ecd7` (il
> `VisualLayerManager`, senza cui i popup non scalano).

### La scoperta che ha cambiato l'implementazione: `OverlayPopups` **da solo non basta**
Il brief dava per scontato che rendere i popup non-nativi bastasse a farli entrare nel transform.
**Non basta, ed è misurato, non dedotto.** L'`OverlayLayer` di una finestra vive nel **template della
finestra**, come *fratello* del `ContentPresenter` che contiene il nostro host: quindi un popup overlay
raggiunge la `Window` **scavalcando** il transform esattamente come faceva quello nativo. La catena
misurata in headless (che usa già gli overlay popup) è quella che M83 aveva registrato, verbatim:

```
ComboBoxItem < … < ContentPresenter < VisualLayerManager < LayoutTransformControl
             < OverlayPopupHost < OverlayLayer < VisualLayerManager < Panel < ZoomWindow
```

**La correzione**: l'host porta **un `VisualLayerManager` proprio**, e il contenuto della finestra va
dentro *quello*. `OverlayLayer.GetOverlayLayer` risolve al **più vicino** manager sopra il controllo che
apre il popup, che adesso è il nostro, dentro il transform:

```
… < OverlayPopupHost < OverlayLayer < VisualLayerManager < LayoutTransformControl(nostro)
  < ContentPresenter < VisualLayerManager < Panel < ZoomWindow
```

Senza questa aggiunta l'opzione (b) sarebbe stata **inutile**: transform sulla finestra, popup al 100%,
cioè il difetto di M83 di nuovo.

### Le due cause strutturali, rimosse — non tollerate
| causa (M82/M83) | perché non c'è più |
|---|---|
| il wrapper era installato da una `Style` app-wide, cioè da una **callback di styling**, e mutare `Application.Styles` (apre Settings) ri-stila tutto e la richiamava su una finestra già avvolta | **nessuno stile e nessuna callback**: l'host lo installa la finestra, dal **costruttore** di `Theming/ZoomWindow`. Niente ri-entra quando `Application.Styles` cambia |
| il crash `The Control already has a parent`: il presenter teneva già il contenuto quando il wrapper provava a prenderlo | nel costruttore la finestra **non ha ancora né `Content` né `ContentPresenter`**: l'host entra con figlio `null` e non c'è niente da staccare. Su quel percorso il crash **non è raggiungibile** |

`Install` resta **idempotente** per finestra (`HostProperty`), **rifiuta invece di lanciare**, e non lascia
mai il contenuto senza genitore. Il trucco `Presenter.UpdateChild()` di M82 è **conservato**, perché
serve ancora alle scritture di `Content` *successive* (finestra riempita dopo `Show`, contenuto
sostituito a caldo).

### I due livelli
| livello | fattore | perché |
|---|---|---|
| `Standard (like Git Extensions)` | **1.0**, **nessun transform** | è una *misura*, non una comodità: M81 aveva già corretto il chrome da 14 (Fluent) a **12** (upstream, Segoe UI 9pt), e le metriche del port sono prese da upstream (toolbar 25px, riga griglia 24px, bottoni 23x22 con icone 16px). A 1.0 il port **è già** alla scala di upstream: non resta niente da correggere con un fattore |
| `Large (125%)` | **1.25** | primo passo convenzionale su Windows e GNOME, quindi il fattore che «più zoomata» più probabilmente significa; 110% sarebbe nel rumore di una modifica di font, cioè il meccanismo appena rifiutato. **150% scartato**: Git Extensions è denso e il suo valore è quanta storia sta a schermo, a 150% la griglia perde circa un terzo delle righe visibili. A 1.25 il chrome cade su 15px, che è la dimensione che M84 già spediva come passo massimo, quindi la leggibilità non è un'ipotesi |

Uno zoom **non** può promettere pixel interi come poteva una dimensione di font: a 1.25 una toolbar da
25px misura 31,25px e il compositor arrotonda. È inerente a qualsiasi scala non intera e non viene
nascosto.

**Vivo, non al riavvio.** Assegnare `LayoutTransform` invalida la misura dell'host, quindi ogni finestra
aperta si ri-dispone al passo di layout successivo senza ricostruire nessuna view. Non c'è niente di
mezzo-applicato da spiegare e nessun riavvio da chiedere: il requisito 6 è soddisfatto dal lato buono.

### Rimosso
La **scala** delle tre chiavi font di M84. Il **baseline 12px resta**, come scrittura fissa e
indipendente dal livello: `UiScaling.Apply(UiSize.Normal)` in `App.Initialize` diventa
`UiScaling.InstallChromeBaseline()`. Era la parte di M84 che aveva risolto la lamentela originale
dell'utente, e sopravvive intatta; quella che spariva era il *knob*, perché due controlli di dimensione
in concorrenza danno un prodotto che nessuno ha scelto.

### Migrazione del valore persistito
`UiSize` passa da quattro membri a due, e `UiSizes.Parse` **migra** invece di ripiegare:
`Small`/`Normal` → `Standard`, `Large`/`VeryLarge` → `Large`. Il caso che conta è **`VeryLarge`**: se
cadesse nel fallback, un utente che aveva scelto il passo **più grande** si troverebbe sul livello **più
piccolo** dopo l'aggiornamento. La migrazione atterra su disco gratis, attraverso il round-trip di
normalizzazione che `UiStateService` faceva già in lettura: il nome vecchio viene letto una volta e
riscritto col nuovo. Stesso store, stesso posto nella pagina Appearance, stesso comportamento di
Cancel (revert immediato, coerente perché l'applicazione è viva).

### Onestà nella UI: la nota è stata riscritta perché era diventata una bugia
La riga di M84 diceva che griglia, diff e liste file **non** seguono l'opzione. Con un transform la
seguono, quindi quella frase andava rimossa, non ritoccata. Adesso dice cosa fa e **qual è il costo**:
*«Zooms the whole interface — text, icons, spacing, toolbars, the revision grid, the diff and the file
lists together. Applied immediately, no restart needed. Because menus and drop-downs are drawn inside
the window so that they scale with it, they cannot extend past its edges: in a small dialog they open
into less room than before.»* Letterale inglese, come M80/M81/M84. **Debito di traduzione**: due nuove
etichette (`Standard (like Git Extensions)`, `Large (125%)`) e questa nota non hanno id XLIFF.

Il costo vale a **entrambi** i livelli, non solo a Large, perché `OverlayPopups` è process-wide: la nota
infatti non lo lega a un livello. Per la stessa ragione «Standard non installa nessun transform» **non**
significa «Standard è identico a una build senza la feature», e il codice lo dice: host e
`VisualLayerManager` sono nell'albero a entrambi i livelli, perché costruirli o toglierli al cambio di
livello vorrebbe dire mutare l'albero del contenuto **proprio** nel momento che tutto questo design
esiste per evitare. Sono pass-through di layout.

### I popup: cosa si rompe, verificato
Niente nel port dipende dai popup **nativi**: zero `new Popup`, zero override di `ShouldUseOverlayLayer`,
nessun handle di finestra preso da un popup. I tipi in uso sono 4 `ContextMenu` (di cui due con
`PlacementMode.Pointer`), 3 `ContextFlyout`, 6 `Flyout` (fra cui il pulsante MRU della griglia e
l'overflow `»` della toolbar, che è la stessa forma) e i dropdown di `ComboBox`. **Asserito dentro
l'host, a entrambi i livelli**: item realizzato di un dropdown aperto, `MenuItem` figlio di un submenu
aperto, `ContextMenu` con `PlacementMode.Pointer`, e contenuto di un `Flyout`. Il costo misurato: in una
finestra **320x200** a Large, un dropdown da 40 voci apre un `OverlayPopupHost` di **105x160**, cioè
tagliato all'altezza disponibile dentro la finestra invece di sfondarne il bordo — è esattamente ciò che
la nota dichiara.

### Verificato / non verificato
**273 asserzioni, 0 fallimenti**, harness headless fuori dall'albero. Oltre ai popup di cui sopra:
baseline 12 su tutte e tre le chiavi a **entrambi** i livelli (cioè il knob font è davvero morto); i 10
casi di `Parse` inclusi `VeryLarge`, il trim, il case e i nomi ignoti; **tutti** i percorsi che scrivono
`Content` — initializer, corpo del costruttore, finestra mostrata **vuota** e riempita dopo `Show`,
contenuto **sostituito a caldo**, contenuto non-`Control` (stringa), e `Install` chiamata **due volte**
— ciascuno con contenuto ancora *il controllo originale*, attaccato, radice visuale giusta, bounds non
collassati e transform giusto; `MainWindow` reale con `SettingsWindow` aperta sopra e poi chiusa; i
quattro switch Classic/Modern × Light/Dark; e il cambio di livello a finestre aperte in entrambe le
direzioni. Che lo zoom sia **reale** e l'asserzione non vacua: in una finestra fissa 1000x800 il body
misura **1000** DIP a Standard e **800** a Large, rapporto **1,25** esatto.

**Non verificato, e solo l'utente può confermarlo**: *niente a schermo*. La verifica GUI headless non
funziona su questa macchina, quindi non è stata fatta **nessuna** misura di pixel renderizzati, di
nitidezza del testo a 1.25, né di come stia effettivamente la griglia a 125% su un monitor vero. Tutte
le asserzioni qui sopra sono sull'**albero visuale e sui bounds di layout**, non sul disegno. In
particolare l'headless usa gli overlay popup **per costruzione**: che `OverlayPopups = true` abbia
l'effetto atteso sul backend **Win32** non è misurato qui — è la ragione per cui l'opzione viene
impostata esplicitamente su entrambi gli option object invece di essere data per acquisita. Da chiedere
all'utente: se a `Large` la UI cresce davvero **tutta** (griglia e diff compresi, non solo il testo), e
se il taglio dei dropdown nelle dialog piccole è accettabile nell'uso reale.

## ROUND 13 — iterazione 7: M85 (2026-08-03) — lo zoom vero: la strada esiste su X11, **non** su Win32

> Richiesta dell'utente dopo M84, che rifiuta l'approccio a soli font: *«cambiando la dimensione cambia
> solo il font e non la UI. […] lavorerei affinché zoommi tutta l'UI (e quindi di conseguenza anche il
> testo), e imposterei due livelli di zoom, uno che rispecchia l'attuale zoom di git extensions e una
> versione più zoomata.»* Base `004428869`. **Nessuna modifica di codice: è un'indagine che si è
> fermata su un fatto misurato.** Il mandato era esplicito: se la strada
> ambiente/platform-options non esiste, fermarsi e riportare, **senza** ripiegare sul
> `LayoutTransform` per finestra rimosso in M84.

### Il fatto: il knob di scala process-wide di Avalonia 11.3.14 è **solo del backend X11**
Misurato sugli assembly che il port referenzia davvero (`~/.nuget/packages/*/11.3.14/lib/net8.0`),
non sulla documentazione:

| dove | cosa c'è | verdetto |
|---|---|---|
| `Avalonia.X11.dll` | `AVALONIA_GLOBAL_SCALE_FACTOR`, `AVALONIA_SCREEN_SCALE_FACTORS`, `AVALONIA_USE_PHYSICAL_DPI`, `QT_SCALE_FACTOR` + i tipi `IScalingProvider`, `UserConfiguredScalingProvider`, `PostMultiplyScalingProvider`, `XrdbScalingProvider`, `PhysicalDpiScalingProvider` | **la strada c'è** |
| `Avalonia.Win32.dll` | **zero** stringhe `AVALONIA_*` | **la strada non c'è** |
| `Avalonia.Win32PlatformOptions` | 9 proprietà pubbliche: `OverlayPopups`, `RenderingMode`, `CompositionMode`, `WinUICompositionBackdropCornerRadius`, `ShouldRenderOnUIThread`, `WglProfiles`, `CustomPlatformGraphics`, `DpiAwareness`, `GraphicsAdapterSelectionCallback` — **nessun fattore di scala** | idem |
| `Avalonia.X11PlatformOptions` | 15 proprietà pubbliche, **nessun fattore di scala** (su X11 il knob è **solo** l'env var) | idem |
| `Avalonia.Win32DpiAwareness` | `Unaware` / `SystemDpiAware` / `PerMonitorDpiAware` | **non è uno zoom** — vedi sotto |
| `Avalonia.Controls.WindowBase.DesktopScalingOverride` | esiste, ma è `FamANDAssem` (`private protected`) **e per-istanza** | inaccessibile, e sarebbe di nuovo per-finestra |

Semantica dell'env var, confermata sul sorgente upstream (`src/Avalonia.X11/Screens/X11Screens.Scaling.cs`,
gli stessi nomi di classe presenti nell'assembly 11.3.14 pinnato): `if (global != 1) provider = new
PostMultiplyScalingProvider(provider, global)`, e `GetScaling(screen, index) => _inner.GetScaling(...) *
_factor`. È **esattamente** il meccanismo giusto: moltiplica la scala di **tutti** gli schermi, quindi
cambia il DPI in cui crede l'intero toolkit — layout, rendering e popup nativi insieme — e non tocca
**nessun** albero visuale, quindi non può orfanare contenuto come M82/M83.

**`DpiAwareness.Unaware` non è un sostituto su Windows.** Fa credere al processo 96 DPI e lascia che
l'OS stiri la finestra come bitmap: il fattore è quello del monitor, **non** una scelta dell'utente
(su un display al 100% lo zoom è 1.0, cioè l'opzione «Large» non farebbe nulla), e il risultato è
sfocato. Non può esprimere «Standard vs Large».

### Perché questo blocca la richiesta invece di risolverla a metà
`App/Program.cs:180` usa `.UsePlatformDetect()`, quindi **la piattaforma decide il backend**. Sulla
macchina dell'utente — Windows 11 ARM64, la stessa dove gira `GitExtensions.Avalonia.exe` e dove sono
state viste tutte le regressioni di questo round (cfr. M75) — il backend è **Win32**, dove la strada
non esiste. Implementare l'env var darebbe uno zoom reale **sul target Linux del port** e
**un'opzione inerte sulla piattaforma su cui l'utente lo sta valutando**: è precisamente la
«fake option» che il vincolo vieta. Da qui lo stop.

### Non tentato, per divieto esplicito
Ripiegare sul `LayoutTransformControl` per finestra. Resta strutturalmente pericoloso (M82/M83).

> **RISOLTO IN M86.** L'utente ha scelto l'opzione (b) qui sotto, e il transform è tornato — ma
> **non** «così com'era»: le due cause strutturali sono state rimosse (installazione dal costruttore
> invece che da una `Style`, quindi nessuna callback di styling e nessun presenter da svuotare). La
> frase «resta strutturalmente pericoloso» era vera del **meccanismo di installazione di M81**, non
> del transform in sé, e M86 lo dimostra. Va corretta anche l'aspettativa espressa nel paragrafo
> seguente: `OverlayPopups` **da solo non basta** a far scalare i popup — serve anche un
> `VisualLayerManager` dentro l'host, perché l'`OverlayLayer` della finestra sta nel *template* della
> finestra, fuori dal transform. Misurato in M86.

### Informazione nuova per la decisione: `OverlayPopups` esiste su **entrambi** i backend
`Win32PlatformOptions.OverlayPopups` e `X11PlatformOptions.OverlayPopups` sono entrambe pubbliche in
lettura/scrittura (misurato). Con i popup resi **non nativi** cadrebbe una delle due obiezioni al
transform — quella misurata in M83, «il transform non raggiunge i popup» — perché il contenuto dei
popup tornerebbe nello stesso visual root della finestra. **Non cadrebbe la seconda**, la mutazione
dell'albero del contenuto; va però detto che quella era una conseguenza dell'*implementazione*
(installare il wrapper da una `Style` app-wide, cioè da una callback di styling), non del transform in
sé: un wrapper creato dalla finestra **alla costruzione**, nel codice della finestra, non passa da
nessuna callback di styling. È un'opzione diversa, con un costo diverso (i popup overlay non escono
dai bordi della finestra), e va portata all'utente insieme allo stop qui sopra — non decisa qui.

### Verificato / non verificato
**Verificato**: la presenza/assenza dei simboli e delle stringhe negli assembly 11.3.14 pinnati
(scansione dei byte in UTF-16 e ASCII + dump dei metadati via `System.Reflection.Metadata`:
visibilità, staticità e presenza del setter), e la semantica dell'env var sul sorgente upstream.
**Nota sul metodo**: la prima scansione, fatta con `strings`, aveva dato *zero* risultati su tutti gli
assembly — `strings` **non esiste** su questa macchina e il comando falliva silenziosamente. Il
negativo su `Avalonia.Win32.dll` vale solo perché lo stesso metodo, corretto, trova 9 stringhe
`AVALONIA_*` in `Avalonia.X11.dll`.
**Non verificato**: che `AVALONIA_GLOBAL_SCALE_FACTOR` produca davvero lo zoom atteso a schermo. Non è
verificabile qui — richiede il backend X11, e questa macchina è Windows (l'headless non usa né X11 né
Win32). Nessuna misura è stata fatta su come apparirebbe la UI a un fattore diverso da 1.

## ROUND 13 — iterazione 6: M84 (2026-08-03) — il meccanismo sbagliato, sostituito

> **SUPERATO DA M86**, su richiesta dell'utente: la scala del font è stata **rimossa** perché muoveva
> il testo e lasciava la UI dov'era. Di M84 **sopravvive il baseline 12px** (fisso, non più funzione
> della size). Due affermazioni di questa milestone non valgono più e sono corrette sul posto qui
> sotto: le quattro size e la tabella «cosa non segue la dimensione».

> Decisione del coordinatore dopo M83, portata all'utente: **sostituire il transform per-finestra con
> la scala del font**. Le due ragioni che l'hanno decisa sono quelle misurate in M83 — il transform
> **non raggiunge i popup**, quindi la coerenza che lo giustificava non esiste, e muta l'albero del
> contenuto da dentro una callback di styling, cosa che ha prodotto **tre difetti di fila** (crash di
> ogni finestra, finestra principale bianca, transform perso sulle finestre riempite dopo `Show`).
> Base `73224da6f`, `Errori: 0`.

### Rimosso, non deprecato
`ScaledProperty`, `HostProperty`, l'host `LayoutTransformControl` e la sua lista debole, `Attach`,
`TryReparent`, `AsControl`, `Transform`, `CurrentScale`, l'evento `SizeChanged` (mai sottoscritto) e
la `Style` app-wide su `Window` in `App.Initialize`. **Nessun percorso dell'app tocca più l'albero del
contenuto di una finestra per applicare una scelta di aspetto.**

### Cosa scrive adesso l'opzione: tre chiavi di risorsa, e nient'altro
| chiave | perché |
|---|---|
| `ControlContentThemeFontSize` | la legge **ogni** `ControlTheme` di Fluent, via dynamic resource |
| `ToolTipContentThemeFontSize` | Fluent tiene i tooltip su una chiave propria (default 12) |
| `TabItemHeaderFontSize` | la sua di Fluent è **24**; il port l'ha sempre sovrascritta |

Il setter `FontSize` su `TabItem` in `ModernStyles.BuildBaseline` è **cancellato**: misurato, era lui a
sovrascrivere la scelta dell'utente — con la chiave chrome a 15 **ogni** controllo riportava 15 e il
solo `TabItem` riportava ancora 12. L'override ora è la risorsa, che legge il template di Fluent
stesso. E la proprietà del 12px di baseline si sposta da `ModernStyles`
(`InstallChromeFontSize`, cancellata) a `UiScaling`, chiamata da `App.Initialize` come
`Apply(UiSize.Normal)`: due proprietari per un numero erano tollerabili finché il numero era fisso,
non appena diventa un'opzione.

### Pixel interi, e le percentuali non si dichiarano più
90/100/110/125% di 12 fa 10,8 / 12 / 13,2 / 15: due valori frazionari, e una dimensione di chrome
frazionaria si propaga in altezze di controllo e origini del testo frazionarie, che si leggono
**morbide** — l'opposto di quello che chiede chi cambia la dimensione. Arrotondato a pixel interi:
**11 / 12 / 13 / 15**, cioè rapporti reali **92 / 100 / 108 / 125%**. I mezzi pixel sono stati
considerati e scartati (10,8 → 11,0 non è più vicino al nominale, e 13,2 → 13,5 è di nuovo
frazionario). Le etichette del combo stampano quindi **i pixel** («Small (11px text)») e non le
percentuali nominali: l'interfaccia non può dichiarare un rapporto che l'app non disegna.

### Onesto nella UI, non solo nel report
Sotto il controllo c'è **una riga**: *"Changes the interface text: buttons, labels, menus, tabs and
list rows. The revision grid, the diff and the file lists keep their own text size, and some control
heights are fixed."* Letterale inglese, come `Style` e come le etichette delle size (M80/M81: upstream
non ha questa impostazione, non c'è id XLIFF da riusare). E la descrizione della categoria non dice
più che le size *«scale the whole window, text and spacing together»*, che non è più vero.

### Cosa segue la dimensione e cosa no — misurato, non dedotto
> **CORRETTO DA M86 per quanto riguarda il «non segue».** Questa tabella è vera **della scala del
> font**, che è il meccanismo rimosso. Con il transform di M86 i 137 `FontSize` letterali e le altezze
> minime fisse di Fluent **scalano tutti**, perché un layout transform scala il risultato *misurato e
> disegnato* e non gli importa se una dimensione venga da una risorsa o da un `const`. L'alternativa
> citata qui sotto — trasformare 137 assegnazioni in binding — **non è servita**.

**Segue** (chiave a 12 e a 15, `FontSize` effettiva letta dai controlli): `Button`, `TextBox`,
`CheckBox`, `ComboBox`, `TreeView`, `ListBox`, `TextBlock` nudo, `ListBoxItem`, `TreeViewItem`,
`ComboBoxItem`, `MenuItem` **a entrambi i livelli**, e `TabItem` dopo la modifica di cui sopra.
**Non segue**: i 137 `FontSize` letterali delle view (`Metrics.Text.*` sono `const`, letti una volta
quando la view viene costruita — griglia, diff, liste file), e le altezze minime fisse di Fluent (un
`TextBox` misura 32px sia a font 12 sia a 15; un `Button` invece cresce, 23 → 25). Farli seguire
significherebbe trasformare 137 assegnazioni in binding, non cambiare la tabella `Metrics`.

### Verificato / non verificato
111 asserzioni, `MainWindow` reale: baseline 12 su tutte e tre le chiavi all'avvio; su una finestra
**già costruita**, `Button`/`TabItem`/`MenuItem`/`ComboBox` riportano 11/12/13/15 alle quattro size;
**dentro i popup aperti** — un menu aperto e un dropdown aperto, con gli item verificati *realizzati*
nel visual tree perché l'asserzione non sia vacua — `MenuItem` figlio e `ComboBoxItem` riportano la
dimensione scelta: **i popup ora seguono l'opzione**, che è esattamente ciò che il transform non
faceva. Poi: Settings aperta sopra `MainWindow` a ogni size, cambio size **da dentro** la dialog in
ogni direzione (arriva anche alla finestra dietro), contenuto della finestra ancora popolato dopo la
chiusura, e i quattro switch Classic/Modern × Light/Dark. Il contenuto di `MainWindow` è di nuovo il
suo `DockPanel`, senza wrapper.

**Non verificato**: niente a schermo (verifica GUI headless non funzionante su questa macchina), e i
popup **nativi** di Windows — l'headless usa gli overlay popup. Qui però l'argomento è più forte che
per il transform: la risoluzione di una risorsa non passa dal visual root ma dall'albero logico e
dalle risorse d'applicazione, quindi un popup nativo legge la stessa chiave. Non è una misura, è un
ragionamento, e va segnato come tale.

## ROUND 13 — iterazione 5: M83 (2026-08-03) — lo stesso meccanismo, il terzo difetto

> Segnalazione dell'utente subito dopo M82: *«appena clicco su settings, la schermata lampeggia, si
> apre il dialog di settings ma la GUI dietro il dialog diventa bianca. Se chiudo il dialog rimane
> bianca.»* Base `44e434651`, `Errori: 0`.

**Riprodotta headless sulla `MainWindow` vera, asserendo sull'albero** e non sull'assenza di
eccezioni — questo guasto è **silenzioso**:

```
dopo Show:      LayoutTransformControl[1440x740] > DockPanel[1440x740]
                body parent=LayoutTransformControl visualRoot=MainWindow attached=True
dialog aperto:  LayoutTransformControl[1440x740] > LayoutTransformControl[1440x740]
                body parent=<null>                visualRoot=<null>      attached=False
```

Il contenuto reale non era figlio di nulla. Una finestra vuota dipinge il suo sfondo: **bianco**.

### Causa: `Attach` non era idempotente, e il setter di stile passa più di una volta
Tracciato:

```
Scaled True -> False  prio=Unset      (ValueStore.EndStyling / ApplyStyling)
Scaled False -> True  prio=Style
```

Aprire Settings **modifica `Application.Styles`**, e mutare quella collezione ri-stila **ogni**
elemento: il setter viene disapplicato al default e riapplicato. `Attach` girava quindi una seconda
volta su una finestra già avvolta, costruiva un **secondo host**, e il gestore di swap del primo si
azzuffava col secondo per `Window.Content` finché il contenuto vero non restava figlio di nessuno.
L'avvio sopravviveva perché durante l'avvio nulla ri-stila: **di nuovo** "sopravvissuto per tempismo",
e di nuovo per la stessa ragione di fondo — **questo codice muta l'albero del contenuto da dentro una
callback di styling**.

La finestra ora ricorda il suo unico host in un attached property privato (`HostProperty`): `Attach`
esce subito su una finestra già avvolta, quindi un secondo host non può esistere. `TryReparent`
rifiuta inoltre se il contenuto proposto è un **antenato** dell'host, così nessun percorso può
annidare l'host nel proprio sottoalbero.

### Il limite del meccanismo, misurato: i popup non scalano
> Questa misura è ciò che ha deciso M84: il meccanismo è stato sostituito, e con la scala del font i
> popup **seguono** l'opzione (verificato sugli item dentro un menu e un dropdown aperti).
>
> **M86: questa misura era giusta e resta giusta — la sua causa però era un'altra.** M86 l'ha
> riprodotta verbatim, e non dipendeva dal fatto che i popup fossero finestre native: dipende dal fatto
> che l'`OverlayLayer` sta nel **template della finestra**, fratello del `ContentPresenter`, quindi
> fuori dall'host. Ecco perché `OverlayPopups = true` da solo non risolveva niente. Con un
> `VisualLayerManager` **dentro** l'host la catena passa dal transform e i popup scalano: asserito su
> dropdown, submenu, `ContextMenu` con `PlacementMode.Pointer` e `Flyout`, a entrambi i livelli. La
> nota qui sotto sull'uso dell'identità di riferimento invece del tipo resta valida e M86 la segue.
Verificato con identità di riferimento (non per tipo — `OverlayPopupHost` ha un
`LayoutTransformControl` **suo**, che darebbe un falso positivo): il contenuto di un dropdown di
`ComboBox` e di un `MenuItem` aperto **non è discendente del nostro host**. La catena è
`ContentPresenter < VisualLayerManager < LayoutTransformControl(di Avalonia) < OverlayPopupHost <
OverlayLayer < VisualLayerManager < Panel < Window`, cioè arriva alla `Window` **scavalcando** il
wrapper. Su Windows i popup sono per default finestre native, quindi visual root separati: a maggior
ragione fuori. **Menu, dropdown, tooltip e menu contestuali restano al 100%** mentre la finestra sta
a 90/110/125%. È esattamente il pezzo che un knob sul **font** invece raggiungerebbe, perché
`ControlContentThemeFontSize` è una risorsa d'applicazione che i popup leggono come tutti.

### Verificato / non verificato
Tutte e quattro le size, `MainWindow` reale: contenuto ancora il **body originale**, parent logico
l'host, attaccato al visual tree, bounds non collassati, scala giusta — dopo `Show`, con il dialog
aperto, dopo la chiusura, e attraverso tutti e quattro gli switch Classic/Modern × Light/Dark (lo
stesso trigger su `Application.Styles`, raggiungibile dai combo di Settings). Il body misura
`1600x823` al 90% e `1152x592` al 125% dentro la stessa finestra `1440x740`, quindi la scala è reale
e l'asserzione non è vacua. Le 64 asserzioni della suite precedente continuano a passare. **Non**
verificato: nulla a schermo (verifica GUI headless non funzionante su questa macchina), e i popup
**nativi** di Windows — testati solo nella variante overlay che usa l'headless.

## ROUND 13 — iterazione 4: M82 (2026-08-03) — non è la `Window` a fare da parent

> Regressione segnalata dall'utente sull'app in esecuzione, subito dopo M81: *«quando provo ad aprire i
> settings, cliccando tools → settings il programma crasha e si chiude da solo»*. Base `d041e22e5`,
> worktree isolato, `Errori: 0`.

**Riprodotta prima di toccare qualsiasi cosa**, con un harness headless fuori dall'albero che avvia
`App` e apre la `SettingsWindow` come fa `MainWindow.OpenSettingsAsync`:

```
System.InvalidOperationException: The Control already has a parent.
   at Avalonia.Controls.Decorator.set_Child(Control value)
   at GitExtensions.Avalonia.Theming.UiScaling.Attach(Window window)  UiScaling.cs:138
   ...
   at Avalonia.StyledElement.ApplyStyling()
   at Avalonia.Controls.WindowBase.MeasureCore(Size availableSize)
```

### La causa: la correzione di M81 era puntata sull'oggetto sbagliato
A fare da parent del contenuto **non è la `Window`**: è il suo `ContentPresenter`, che raccoglie o
rilascia il figlio **solo al layout successivo**. Azzerare `Window.Content` quindi **non stacca niente
subito**, e la riga dopo — che passa il controllo a `LayoutTransformControl.Child` — lancia.
L'avvio sopravviveva per **tempismo**, non per correttezza; Settings no. E la coda dello stack dice
perché non c'era un "layout successivo" da aspettare: il setter di stile che installa lo scaler scatta
**dentro la prima misura della finestra**, quando il presenter tiene già il contenuto.
`Window.Presenter.UpdateChild()` forza la riconciliazione **subito**, ed è ciò che stacca davvero.

### Il contratto: se non si può scalare, non si scala — non si lancia
`TryReparent` è ora l'unico punto che re-parenta (primo attach **e** sostituzione successiva del
`Content`), **verifica** che il controllo sia libero, e se non lo è **rimette a posto il contenuto e
lascia quella finestra non scalata**. Una preferenza di aspetto non vale un crash del processo. Il
gestore del cambio `Content` ha una guardia di rientranza, perché il ramo di ripristino scrive
`Content` a sua volta.

### Secondo difetto, e correzione di quanto M81 aveva scritto qui sopra
`LayoutTransformControl` **riscrive la propria `LayoutTransform`** durante il layout (una
`ScaleTransform` assegnata torna come `MatrixTransform` equivalente) e la **azzera** quando fa layout
**senza figlio**. Le finestre mostrate vuote e riempite dopo — `MainWindow` al cambio lingua, e ogni
dialog che costruisce il corpo dopo `Show` — venivano quindi disegnate **non scalate** a
Small/Large/VeryLarge. M81 dichiarava questo caso verificato: lo era nel probe, dove il wrapper aveva
già un figlio, non sul percorso reale. Il transform viene **ri-asserito** dopo l'assegnazione del
figlio.

### Verificato / non verificato
56 asserzioni headless, tutte e quattro le size: Settings si apre ed è avvolta al fattore giusto; il
cambio di size **dall'interno della Settings aperta** funziona in ogni direzione; finestre con
`Content` nel costruttore, assegnato dopo `Show`, sostituito a caldo, e non-`Control`; `ShowDialog`
modale; `GitProcessDialog`, `CommitDialog`, `AboutDialog`. **Non** istanziati `PushDialog`/`PullDialog`:
costruttori privati alimentati da letture git asincrone — passano per lo stesso unico `Attach`.
Nessuno screenshot: su questa macchina la verifica GUI headless non funziona.

## ROUND 13 — iterazione 3: M81 (2026-08-03) — la UI era davvero più grande: 14 contro 12

> Osservazione dell'utente confrontando il port con l'originale WinForms affiancati: *«in generale
> sembra che la UI del porting sia leggermente più zoommata, magari potremmo aggiungere una opzione nel
> menu di appearance in cui decidere la dimensione dell'ui»*. **Prima misurato, poi corretto, poi
> l'opzione** — in quest'ordine, perché uno slider che compensa un default sbagliato non è una scelta
> dell'utente, è una toppa. Base `b14bc03d1`, build `Errori: 0`.

### La diagnosi, misurata (non dedotta dai commenti nel codice)
Le chiavi risorsa dei font **non compaiono** come stringhe nella dll di Fluent 11.3.14 (a differenza di
`ButtonBackground` & co. lette da M79 con `strings -el`): stanno nel blob `!AvaloniaResources`. Quindi
la misura è stata fatta con un **probe headless** che istanzia Fluent e legge i valori effettivi.

| | port, prima di M81 | upstream WinForms | delta |
|---|---|---|---|
| `Button`/`TextBox`/`CheckBox`/`ComboBox`/`MenuItem`/`TreeViewItem` | **14** px | 12 px | **+17%** |
| `TextBlock` nudo | 14 px, riportato a 13 da uno stile app-wide | 12 px | +8% |
| header dei `TabItem` | 13 px | 12 px | +8% |
| righe della griglia revisioni | 12 px (`RowFontSize`), riga ~20-22 px | 12 px, riga **24** px (`_rowHeight = MeasureString("By") + Scale(9)`) | il port è **più stretto** |
| literal `FontSize` nelle view | 12 in **77** punti, 11 in 17, 10 in 10 | — | già alla misura di upstream |

- **`ControlContentThemeFontSize` = 14** è la causa: Fluent la imposta a 14, e **ogni** template di
  controllo la legge. Upstream disegna la sua chrome in `SystemFonts.MessageBoxFont` — Segoe UI 9pt =
  **12 px** a 100% DPI (`AppSettings.Font`, `GitCommands/Settings/AppSettings.cs:1550`).
- Quindi il "leggermente più zoommata" **non era DPI scaling** e **non era la griglia**: era la chrome
  ereditata a 14 attorno a un contenuto che era già a 12. La griglia, se mai, è più densa
  dell'originale (riga 24 px contro ~20-22).
- Le altezze fisse di Fluent (`TextBox`/`CheckBox`/`TreeViewItem` misurano **32 px** con font 12 come
  con font 15) sono la parte **non** spiegata dal font: sono minimi del `ControlTheme`, e non si
  muovono cambiando la tipografia.

### (a) Prima la baseline — una chiave, non una patch per controllo
`ModernStyles.InstallChromeFontSize` scrive `ControlContentThemeFontSize = Metrics.Text.Body` (12).
Verificato col probe: con la chiave a 12, `Button`/`TextBox`/`CheckBox`/`TreeViewItem` riportano tutti
`FontSize = 12`, e **anche un `TextBlock` non stilato** — motivo per cui lo stile app-wide
`TextBlock → 13` di M77 è stato **rimosso**: faceva lo stesso lavoro con un numero diverso, e tenerlo
avrebbe rialzato la prosa a 13 mentre bottoni e menu attorno stavano a 12. Un solo posto decide la
dimensione. Lo `TabItem` scende a `Body` per la stessa ragione.
Installata **una volta e mai rimossa**, fuori dallo `Snapshot`: la dimensione del testo non è una
questione moderno-contro-classico — il riferimento del classico *è* upstream, che sta a 12. Restituire
la chiave passando a Classic renderebbe Classic **17% più grande** dell'aspetto che è definito a
riprodurre.

### (b) Poi l'opzione — perché un transform e non una scala di font
> **SUPERATO DA M84.** Questo paragrafo argomenta la scelta del transform, e l'argomento **non ha
> tenuto**: il transform non raggiunge i popup (menu, dropdown, tooltip — misurato in M83), quindi la
> "coerenza" che lo giustificava non c'era, e in compenso mutava l'albero del contenuto da dentro una
> callback di styling, producendo tre difetti di fila. M84 ha sostituito il meccanismo con la scala
> del font, cioè con l'alternativa che qui sotto viene scartata. La parte **vera** di questo
> paragrafo resta vera — i 137 letterali e i minimi fissi di Fluent **non** si muovono — ed è per
> questo che ora la pagina Appearance lo **dice** invece di lasciarlo credere.

Scalare il font di default e lasciare che i controlli seguano sarebbe stata un'**opzione finta**: le
view assegnano `FontSize` letterale in **137 punti** (la griglia, il diff, le liste file — esattamente
il contenuto denso per cui l'opzione viene chiesta) e le altezze di Fluent sono minimi fissi che il
font non muove. Quelle parti sarebbero rimaste ferme mentre le etichette attorno si spostavano.
Quindi: un `LayoutTransformControl` sopra il contenuto di **ogni** finestra, che scala l'albero
*misurato* con un unico fattore — font letterali, minimi fissi, box delle icone e il DAG disegnato a
mano si muovono insieme.
- **Come arriva a tutte le 34 finestre senza toccarne nessuna**: una `Style` app-wide su `Window` che
  imposta una attached property la cui `Changed` fa il wrap. Prende anche le finestre che apre Avalonia
  (il file chooser gestito) — che è il punto: nessuna classe da ricordare.
- **Il prezzo, dichiarato**: il transform scala anche il rendering, quindi le PNG 16px del Classic
  vengono ricampionate fuori dal 100% e perdono un po' di nitidezza (testo e glifi vettoriali sono
  geometria e restano nitidi). È il costo di un'opzione **vera** su tutta la UI invece che solo sulle
  parti che leggono un font.
- **A `Normal` non viene installato alcun transform** (`LayoutTransform = null`): il default è
  pixel-identico a una build senza l'opzione.
- Quattro passi chiusi — `Small 90` / `Normal 100` / `Large 110` / `VeryLarge 125` — e non uno slider:
  un valore libero non ha un nome da scrivere in `ui-state.json` o in una segnalazione.
- **La size non è un terzo argomento di `ThemeManager.Apply`.** La regola di M80 (nessun call site
  passa un letterale per la dimensione che l'utente non ha toccato) costa di più a ogni dimensione
  aggiunta alla stessa chiamata. La size non condivide niente con la palette — è un transform, non un
  brush — quindi ha un owner suo, `UiScaling.Apply(UiSize)` a un argomento, e i call site di
  theme/style restano **esattamente** come li ha lasciati M80.

### (c) Persistenza e ciclo del dialogo
`UiSize` in **`ui-state.json`** accanto a `Theme` e `Style` (non `view-prefs.json`), normalizzato in
`Sanitize` con lo stesso giro dell'enum, quindi un nome corrotto atterra su `Normal` invece di
raggiungere il transform. Applicato **a caldo** come tema e stile: `PreviewUiSize` sulla
`SelectionChanged`, ripristino su Cancel, spostamento della baseline su Apply/OK. E `OpenSettingsAsync`
ri-sincronizza `UiSize` dal file insieme a `Theme`/`Style`, altrimenti la riserializzazione completa
di `_uiState` alla chiusura della finestra principale disfaceva la scelta.
`MainWindow` applica la size **prima** che la finestra venga stilata: il setter che installa il
transform scatta durante lo styling e legge `UiScaling.CurrentScale`, quindi la finestra **nasce** alla
dimensione ricordata invece di aprirsi al 100% e saltare.

### Due difetti trovati dal probe, non dallo schermo
1. **`InvalidOperationException: The Control already has a parent`** all'apertura di ogni finestra: nel
   wrap il contenuto veniva passato a `Child` mentre era ancora figlio della `Window`. Va **staccato
   prima** (`window.Content = null`). Sbagliato al primo colpo, e avrebbe fatto crashare l'avvio.
   **Ma la correzione era mezza sbagliata, e M82 l'ha rifatta**: a fare da parent non è la `Window`
   ma il suo `ContentPresenter`, che aggiorna il figlio solo al layout successivo — azzerare
   `Content` non stacca niente subito. L'avvio sopravviveva per puro tempismo; Tools → Settings
   crashava. Vedi M82.
2. Una `InvalidateMeasure()` aggiunta "per sicurezza" dopo il cambio di transform è **inutile**:
   `LayoutTransformControl` invalida da sé. Rimossa dopo averlo verificato togliendola e rimisurando —
   il commento che la giustificava era falso, ed è il tipo di riga che sopravvive per anni perché
   nessuno la ricontrolla.

### Verificato headless compilando i file veri nel probe (non una copia)
> **Storico: descrive il meccanismo rimosso in M84.** Due delle voci qui sotto erano anche
> **sbagliate**, e sono state smentite dai difetti successivi: il caso «`Content` sostituito dopo lo
> styling» passava nel probe solo perché lì il wrapper aveva già un figlio (M83), e nel percorso reale
> il transform veniva perso; e il wrap "verificato" al primo colpo crashava all'apertura di Settings
> (M82). La lezione, registrata: un probe che costruisce lo scenario **a modo suo** può confermare una
> proprietà che il percorso reale non ha.

Non screenshot: su questa macchina la verifica GUI headless non funziona, quindi la prova è sulle
**dimensioni misurate**. `UiScaling.cs`/`UiSize.cs` compilati dentro il probe insieme a Fluent:
- baseline: `Button.FontSize = 12`, contenuto avvolto in `LayoutTransformControl`;
- **ridimensionamento a caldo** di una finestra **già aperta**: altezza radice 30 / 33 / 37 / 42 px per
  90/100/110/125% — rapporti 0,909 / 1,000 / 1,121 / 1,273;
- finestra **aperta** a 125%: `98x42` contro `78x33` al 100%;
- `Content` **sostituito** dopo lo styling (il caso del cambio lingua): il wrapper resta, il transform
  sopravvive, il nuovo contenuto finisce dentro, e il ritorno a `Normal` rimette `LayoutTransform` a
  null;
- round-trip di persistenza, `"garbage"`/`""` → `Normal`.

### Localizzazione: dichiarata, non finta
`UI size` e le quattro etichette sono **letterali inglesi**, con la stessa scelta che M80 fece per
`Style`: il port non ha un overlay proprio per le stringhe che inventa, `TranslationService.T` risolve
per id XLIFF di upstream o per testo sorgente, e upstream **non ha questa impostazione** (il suo unico
controllo di scala è la checkbox `chkEnableAutoScale`, "Auto scale user interface when high DPI is
used"). Non c'è quindi id da riusare né target italiano da ereditare: cercato in `Italian.xlf`, l'unico
`Size` disponibile è `sizeColumnHeader.Text` → *"Simensioni"* (refuso di upstream compreso), che non
c'entra. Aggiungere un dizionario locale solo per queste cinque stringhe sarebbe un meccanismo nuovo
accanto a quello esistente: **non fatto**, e registrato qui come debito.

### Non toccato di proposito
La voce nel menu **View**: M80 mise lì tema e stile perché sono due voci a testa, mentre quattro
dimensioni allungherebbero il menu senza aggiungere una scelta che la pagina Appearance non offra già.
Le **altezze minime di Fluent** (32 px) restano: al 100% sono più generose dei ~23 px di upstream — è
la parte del divario che il font non spiegava, e ridurla è un lavoro sui `ControlTheme` a sé stante.
*(Diceva «scalano col transform»: dopo M84 non scalano affatto — un `TextBox` misura 32 px sia a font
12 sia a 15, mentre un `Button` cresce 23 → 25. È una delle cose che la riga in pagina Appearance
dichiara.)*

## ROUND 13 — iterazione 2: M80 (2026-08-03) — lo stile è una scelta, non un fatto compiuto

> Richiesta dell'utente subito dopo M79: *«dammi la possibilità di scegliere dalle impostazioni se
> mantenere il vecchio stile/icone o quello nuovo»*. **Ribalta la decisione presa in M79** di
> sostituire l'aspetto senza affiancare una variante classica. Base `1e884a9b8`, due subagent in
> worktree isolati, file disgiunti, build `Errori: 0`.

**Deciso che il cambio è a caldo**, come già il tema, non "al riavvio": una scelta di stile che
obbliga a riaprire l'app sarebbe incoerente col combo Theme che sta tre righe sopra nella stessa
pagina. Costa poco perché nessuna delle tre parti ha bisogno di ricostruire le view.

### Il contratto, fissato PRIMA di delegare
```csharp
public enum AppStyle { Classic, Modern }
ThemeManager.CurrentStyle / CurrentVariant
ThemeManager.StyleChanged                                  // event Action?
ThemeManager.Apply(ThemeVariant variant, AppStyle style)
```
Averlo scritto in anticipo è ciò che ha permesso ai due subagent di lavorare **in parallelo** invece
che in sequenza: quello della UI ha compilato contro una firma che nel suo worktree non esisteva
ancora e ha chiuso con esattamente due errori "AppStyle not found" — previsti, e spariti al
cherry-pick del motore.

### U6 — il motore (`Theming/AppStyle.cs`, `ThemeManager.cs`, `IconLoader.cs`, `ModernStyles.cs`, `App.cs`)
- **Palette a quattro famiglie**: `ClassicDark`/`ClassicLight`/`ModernDark`/`ModernLight`, **34 chiavi
  ciascuna** (contate, non stimate). I valori classici sono recuperati **verbatim** dal
  `a38eb4ab4`, commenti di merito inclusi: sono il ragionamento di M67/M70 e restano leggibili
  accanto ai moderni.
- Le due chiavi nate dopo quel commit — `App.Link` e `App.AccentFill` — nel classico **non
  esistevano** e sono state inventate misurando. Vincolo dato al subagent: *"classico" vuol dire
  l'aspetto di prima, non ereditarne un fallimento AA* — quindi `App.Link` classico **non** riusa il
  vecchio `App.Accent`, che stava a 3,70:1 scuro / 4,06:1 chiaro.
- **Icone commutabili a caldo senza ricostruire le view**: `GlyphIcon` conserva anche il **nome** e al
  disegno sceglie glifo tinto (Modern) o `Bitmap` (Classic). Sottoscrizione a `StyleChanged` in
  `OnAttachedToVisualTree` e **disiscrizione in `OnDetachedFromVisualTree`** (`IconLoader.cs:261`/
  `:278`) — punto dichiarato al subagent come *più importante della feature stessa*: `StyleChanged` è
  `static` e la griglia ricicla i container di continuo, quindi un controllo staccato che resta
  sottoscritto fa crescere la lista senza limite.
- Il log "has no vector glyph…" **non spara** in Classic: lì il PNG è la scelta, non un ripiego.
- **`ModernStyles` reso reversibile**: per ogni chiave Fluent sovrascritta fotografa il valore
  precedente e in ripristino lo rimette, o **rimuove la chiave** se prima non c'era — che è ciò che
  restituisce il valore del `ControlTheme`. I valori di Fluent non si indovinano riscrivendoli. Gli
  `Style` aggiunti vengono **tolti** dalla collezione, non neutralizzati. Restano attivi in entrambi
  gli stili i due stili `TabItem`/`TextBlock` che esistevano anche prima di M79.

### U7 — la scelta (`SettingsWindow.cs`, `MainMenu.cs`, `MainWindow.cs`, `UiStateService.cs`)
Combo **Style** accanto a Theme nella pagina Appearance (voci `Modern`/`Classic`), voci
`Classic style`/`Modern style` nel menu **View** accanto a quelle del tema, e `Style` persistito in
`ui-state.json` con la stessa normalizzazione di `Theme`.
Il punto delicato era il ciclo anteprima/OK/Cancel con **due** dimensioni invece di una, e la regola
adottata è netta: **nessun call site passa mai un letterale per la dimensione che l'utente non ha
toccato** — ogni `Apply` riceve la *coppia*, letta fresca da chi la possiede in quel momento.
`PreviewTheme()` è diventato `PreviewAppearance()` su entrambi i combo; `LoadValues()` restituisce la
coppia da cui Cancel torna indietro; `ApplyAndSave()` sposta **entrambe** le dimensioni sulla nuova
baseline, così "Apply poi Cancel" non disfa nulla. Trovato per strada: `OpenSettingsAsync` doveva
ri-sincronizzare `Style` dal file insieme a `Theme`, o la riserializzazione alla chiusura annullava la
scelta.

### Correzione del loop in integrazione
**Il pulsante Commit restava vettoriale in Classic.** M79 gli aveva tolto le **sette** bitmap per-stato
di upstream a favore di un glifo tinto — giusto per il moderno, sbagliato per il classico, dove lo
stato è detto dall'**icona** e non dalla tinta. E `Commit` è **l'unico dei 90 glifi senza PNG proprio**
(verificato incrociando `Icons.cs` con `Resources/Icons/`: 89 su 90 hanno il raster), quindi il
fallback sul nome del glifo non poteva funzionare. `GlyphSource` porta ora un **nome classico
opzionale** accanto al proprio: basta il meccanismo live già esistente, senza ricostruire view né
sottoscrivere niente nella toolbar.

### Verificato in GUI dal loop (screenshot guardati, pixel misurati)
- **Cambio a caldo** da Modern a Classic dalla pagina Appearance, **senza riavvio**: le icone tornano
  raster nella stessa sessione.
- **Le quattro combinazioni** rese e misurate. Classic è **byte-identico** al pre-M79: scuro
  `#1E1E1E`/`#252526`/`#333337`, chiaro `#F3F3F3`/`#FFFFFF`/`#E4E4E4` — **bianco puro incluso**.
  Modern scuro `#141518`/`#1C1D21`/`#2F3038`.
- **Persistenza**: `Style` riletto da `ui-state.json` all'avvio.
- Icona del branch in Classic: **32 px scuri, 0 chiari** — è davvero il PNG, non il glifo.

### Proprietà nota di Classic, non un difetto da correggere
Parecchie PNG di upstream (`Branch.png` fra queste) sono **line art nera** su 16×16, quindi sul tema
scuro classico sono quasi invisibili. Era già così **prima di M79** — M79 le aveva sostituite con un
glifo tinto, ed è il motivo per cui il moderno non ha il problema. Correggerlo renderebbe Classic
non-classico: la sua definizione è *l'aspetto di prima*, e lì dentro c'è anche questo.

### Nota di integrazione
Durante l'iterazione sono entrati nel branch **due merge da `origin`** con lavoro fatto altrove, che
ha rinumerato le milestone: le M75/M76 di questa sessione sono diventate **M77/M78** e l'iterazione 1
**M79**. Tutti i commit di questa sessione sono sopravvissuti ai merge (verificati uno per uno) e la
build resta a `Errori: 0` dopo l'unione.

## M131 (2026-08-08, `6bf91a68d`) — più repository in una finestra, stile VS Code

> Dall'utente: «voglio poter scorrere tra le repo in stile vscode, quindi con delle tab che mi
> consentono di tenere aperte più repo o submodules contemporaneamente nella stessa finestra […] se
> clicco un nuovo submodule si sostituisce a quello attivo, mentre se faccio doppio clic si fissa».
> Con quattro scelte fatte da lui prima di scrivere una riga: **qualsiasi repo** può stare in una
> scheda (non solo i submodule), stato **leggero con ripristino**, schede **persistite**, striscia
> **sotto la toolbar**.

### Cos'è una scheda

Non una seconda copia dell'area di lavoro. `MainWindow` ha **una sola** copia delle viste e da sempre
cambia repository con `OpenRepository` + guardia epoch; una scheda è un segnalibro più il poco stato
che vale la pena ricordare — la riga su cui si era e il pannello in basso che si stava leggendo. È la
scelta esplicita dell'utente fra le tre offerte: la variante «viste vive per scheda» avrebbe voluto
`MainWindow` (4800 righe) spezzata in un `RepoTabView`, N watcher e N insiemi di loader accesi, per
guadagnare uno switch istantaneo su un'operazione che già costa qualche centinaio di ms.

### Anteprima e fissaggio

La regola di VS Code, presa alla lettera:

- **clic singolo** su un submodule o un worktree nell'albero → la repo si apre nella scheda di
  *anteprima*, in corsivo. Il clic singolo dopo **sostituisce** quella scheda, non ne aggiunge una;
- **doppio clic** → la scheda si fissa, il corsivo sparisce e da lì in poi ogni nuova anteprima
  nasce accanto;
- ogni altra porta (picker, dashboard, recenti, clone, cartella trascinata, riga di comando) apre
  **fissato**: sono atti deliberati, non sguardi.

Il clic singolo è nuovo nell'albero (`PreviewRepositoryRequested`) e risponde solo per i due tipi di
nodo che aprono una repository: un branch continua a richiedere il doppio clic per il checkout. Con
l'opzione spenta l'evento non viene ascoltato, quindi l'albero si comporta esattamente come prima.

Due gesti esistenti hanno dovuto cedere il passo, ed entrambe le eccezioni sono strette il più
possibile:

- il worktree **già aperto** non veniva annunciato affatto (`OnActivate` lo saltava): il doppio clic
  sulla repo che il clic singolo aveva appena messo in anteprima non arrivava a nessuno e non
  fissava niente. Ora l'annuncio parte comunque; senza schede l'host risponde «Repository is already
  open», che è più di quanto dicesse prima (silenzio indistinguibile da un clic mancato);
- il submodule che **è** la repo corrente chiede una seconda istanza (come `SubmoduleNode.
  OnDoubleClick` dell'originale). Resta così, tranne quando quella repo è la scheda di anteprima
  attiva: lì il doppio clic è il fissaggio, non la richiesta di un'altra finestra.

### Lo stato che una scheda si porta dietro

`ShowRepoTab` è l'unico punto in cui una scheda va in scena, e quindi l'unico in cui quella che si
lascia viene catturata. Il campo che serve è `_loadedTab`, **non** `_repoTabs.Active`: quando la
striscia ci avverte ha già spostato la propria scheda attiva, quindi «quella che sto lasciando» è
l'ultima che questa finestra ha caricato. Con `Active` la selezione si perdeva a ogni clic sulla
striscia — trovato a schermo, non a mente.

Il commit selezionato non può essere applicato subito: la griglia carica in modo asincrono.
`RevisionGridView.SelectCommitWhenLoaded` **registra e basta** la richiesta, che viene onorata
quando la prima pagina atterra. Anche qui la prima stesura era sbagliata in un modo che solo la
prova mostra: provava la selezione immediatamente, cioè contro le righe della repo che si stava
**lasciando**, bruciando l'unico colpo su una storia che quel commit non poteva contenere.

### Contorno

- `Ctrl+W` (già `BrowseCommand.CloseRepository`) chiude **questa** scheda, non tutte; chiusa
  l'ultima si torna alla dashboard. `Ctrl+PagGiù`/`PagSu` scorrono le schede, in tunnel perché una
  lista con il fuoco non le mangi, e ad anello.
- Un clic sulla scheda **già attiva** normalmente non fa nulla; la striscia lo annuncia lo stesso
  (`Picked`) per l'unico caso in cui significa qualcosa: la dashboard aperta dal menu sopra l'area
  di lavoro, dove quel clic vuol dire «torna a questa repo».
- Le schede sono persistite in `ui-state.json` (`OpenRepoTabs`, `ActiveRepoTab`, tetto di 30) e
  ripristinate all'avvio; un percorso sparito viene semplicemente saltato. Un path sulla **riga di
  comando** le scavalca: è un'istruzione esplicita e diventa una scheda in più.
- Opzione in Appearance, «Repository tabs», con le schede come **default**; indipendente da
  Modern/Classic e dalla barra del titolo, anteprima dal vivo e ripristino su Annulla.

### Verifica

Su Xvfb, con tre worktree reali: anteprima in corsivo, sostituzione in loco al clic singolo
successivo, fissaggio al doppio clic, terza scheda al clic su un altro worktree, ritorno alla prima
scheda con la riga selezionata ripristinata, `Ctrl+W`, `Ctrl+PagGiù`, chiusura e riapertura con le
due schede e i loro stati, opzione su «Single repository» (striscia via all'istante) e Annulla
(striscia di nuovo lì). Build `Avvisi: 0 / Errori: 0`.

## M132 (2026-08-08, `d7c73b0a3`) — schede trascinabili, e il doppio clic sulla scheda fissa davvero

> Dall'utente: «aggiungi il riordino delle tab con drag e che quando faccio doppio click anche sulla
> tab "corsiva" questa si fissi».

### Perché il doppio clic sulla scheda non fissava

Il codice per farlo c'era già (`root.DoubleTapped → Pin`) e non è mai partito. Colpa di `Sync`, che a
ogni chiamata faceva `Children.Clear()` e ri-aggiungeva gli stessi controlli: il **primo** clic attiva
la scheda, l'attivazione chiama `Sync`, e ri-genitorare un controllo azzera lo stato di input che
Avalonia tiene per lui — così il secondo clic arrivava a un controllo che il primo non l'aveva mai
visto. Ricostruire una lista di controlli identici sembra innocuo; non lo è per nessun gesto che viva
su più eventi, e il drag sarebbe morto allo stesso modo.

Due cambi, non uno:

- `Sync` tocca i figli **solo** se la sequenza è davvero diversa (confronto per riferimento); una
  semplice attivazione ora ridipinge e basta;
- il doppio clic si legge da `e.ClickCount == 2` nel `PointerPressed`, come fa l'albero, così il
  gesto sopravvive a qualunque cosa il primo clic abbia fatto alla striscia.

### Riordino

Premuto un tab, il drag **inizia solo dopo 5 px** di spostamento: un clic normale — e il doppio clic
qui sopra — non riordina niente per sbaglio. Da lì la scheda si sposta viva sotto il puntatore,
prendendo lo slot della prima scheda il cui **punto medio** sta alla sua destra (così due schede si
scambiano appena si supera metà della vicina, non tutta). Il puntatore viene catturato dalla striscia:
un drag che scivola giù nell'albero altrimenti non finirebbe mai e il clic dopo lo riprenderebbe.

Trascinare **fissa** la scheda, come in VS Code: una scheda che stai mettendo in un posto preciso è
una che vuoi tenere, e lasciarla in anteprima permetterebbe al clic singolo successivo di cancellare
la disposizione appena fatta. Feedback: la scheda trascinata al 60% di opacità — il riordino è già
visibile di suo, non serve altro.

L'ordine è quello della lista salvata, quindi la persistenza era già scritta: verificato chiudendo e
riaprendo (`wt-alpha`, `git_ext_mod` nell'ordine trascinato).

## M130 (2026-08-08) — i pulsanti dello stash come quelli del commit

> Dall'utente, con lo screenshot della finestra Stash: «rendi coerenti anche i pulsanti con i bordi
> bianchi della finestra di stash con quelli della finestra di commit».

Sette pulsanti — Apply / Pop / Drop sotto la lista degli stash e Save stash / Stash… / Stash staged /
Stash selected changes in fondo — erano gli ultimi rimasti con la chrome di default di Fluent: fondo
piatto e **contorno chiaro**, cioè sette rettangoli pallidi in una finestra dove tutto il resto è
piatto. È lo stesso difetto che M109 aveva già risolto nel dialogo di commit, e la cura è quella già
scritta: `BarButtonStyles.ApplyActions`, riempimento alzato di un passo (`App.PanelAlt`) e nessun
bordo.

Due dettagli deliberati:

- **Non** `Apply` (i `toolbtn`): quelli sono i pulsanti *su una barra*, che a riposo non hanno né
  fondo né bordo. Questi stanno su un pannello, dove ciò che dice «pulsante» è il riempimento. La
  toolbar della lista file, in mezzo alla finestra, è già piatta perché la disegna `FileStatusListView`.
- **Solo in Modern**, come nel dialogo di commit: il pulsante incorniciato *è* l'aspetto classico. Il
  controllo si fa nel costruttore del pannello, che è ricostruito insieme alla sua finestra a ogni
  apertura, quindi legge lo stile corrente e non uno vecchio.

I dialoghi interni al pannello (conferme Sì/No, OK/Annulla) restano com'erano: sono finestre a sé, e
lì la coppia incorniciata è la convenzione.

## M129 (2026-08-08, `764a0d250`) — la scheda selezionata, in modern, è sottolineata

> Dall'utente, con lo screenshot del pannello di VS Code: «per quanto riguarda le schede di sotto,
> cambia lo stile della selezione del tab del modern mettendolo simile a questo».

La striscia in basso è un insieme di **viste della stessa selezione**, non una pila di pagine, e il
riferimento la legge così: OUTPUT / DEBUG CONSOLE / TERMINAL / PORTS sono etichette, una delle quali
**sottolineata**. Il port disegnava invece una scheda rialzata, piena e contornata.

Solo nel blocco **modern**: il classico tiene la sua scheda piena, l'aspetto che ha sempre avuto. Il
template è ora condiviso e prende il bordo del marcatore come parametro — **sopra** nel classico, dove
legge come il cappello della scheda, **sotto** nel modern, dove sottolinea l'etichetta. In entrambi i
casi il marcatore mantiene una **riga di layout propria**, che è la ragione per cui questo template
esiste (Fluent disegna la sua pipe sopra la cella dell'etichetta, e a questa densità finisce sul
testo).

**Il conto dei segnali, dichiarato.** Il classico ne porta quattro (riempimento, bordo, barra, peso);
questo ne porta due, ed entrambi sono misurati: la sottolineatura accento contro la striscia sta allo
stesso 3.95:1 (chiaro) / 3.72:1 (scuro) a cui era tenuto il bordo, e il salto d'inchiostro da
`App.TextDim` a `App.Text` è un secondo indizio che non dipende dal vedere un filo da 2px. Cambiano
**posizione** (un bordo che viene disegnato) e **luminosità**: la selezione non è mai solo colore.

**Trovato per strada.** Il colore a riposo del marcatore era **assegnato dentro il template**, quindi
era un *valore locale* — e in Avalonia un valore locale batte un setter di stile: la regola
«`:selected` → accento» non aveva **mai** dipinto nulla. Ora entrambi gli stati sono stili, e la
striscia classica si prende la barra superiore che avrebbe sempre dovuto disegnare.

**Verificato** su Xvfb: in modern «Commit» ha 91px di sottolineatura accento (misurata a schermo,
2px), niente riempimento né contorno, le altre schede in inchiostro smorzato; in classic la scheda
piena con la barra in alto. Build `Avvisi: 0 / Errori: 0`, harness navigation snapshot PASS.

### Correzione immediata (`13d3d1b68`) — il cambio di stile faceva crashare la striscia

> Dall'utente: «ora appena switcho da modern a classic crasha».

La prima stesura dava al modern un **template proprio** per spostare il marcatore in basso. Passare
Modern → Classic ri-templatizzava quindi ogni `TabItem`, e alla passata di layout successiva Avalonia
lanciava:

```
System.InvalidOperationException: The control StackPanel already has a visual parent
ContentPresenter (Name = PART_ContentPresenter) while trying to add it as a child of
ContentPresenter (Name = PART_ContentPresenter, Host = TabItem)
```

Il contenuto dell'intestazione — il pannello icona+etichetta con cui la scheda è costruita — è ancora
tenuto dal presenter del template **uscente** quando quello entrante misura e prova ad adottarlo.
Scambiare il template di un controllo il cui `Header` è un controllo vivo non è sicuro.

Quindi torna un **template solo**, con tre righe: marcatore, etichetta, marcatore. Il marcatore è un
`Border` unico in riga 0, e il blocco modern lo sposta in riga 2 con un setter sulla proprietà
allegata `Grid.Row` — un cambio di **proprietà**, non un albero visuale nuovo. La riga d'estremità
inutilizzata è `Auto` e collassa a nulla. Anche il raggio degli angoli passa da valore assegnato nel
template a valore **legato** alla scheda, così lo stile modern può squadrarlo: assegnare è un valore
locale, e un valore locale batte un setter di stile — la stessa trappola che aveva già impedito al
marcatore di dipingere.

**Verificato**: Modern → Classic → Modern dal dialogo, a caldo, senza crash, con la sottolineatura di
nuovo al suo posto (91px) al ritorno.

### Seconda correzione (`c3008bd0b`) — la linea andava ancora sopra l'etichetta

> Dall'utente, con lo screenshot: «la linea blu va sotto la scritta».

Il template chiamava `Grid.SetRow(bar, 0)`: la riga diventava così un **valore locale**
sull'elemento, e — di nuovo — un valore locale batte un setter di stile, quindi lo spostamento a riga
2 del blocco modern non faceva nulla. Stessa trappola del colore del marcatore due commit prima,
nello stesso template, una proprietà più in là. Ora la riga **non viene impostata affatto** nel
template: `Grid.Row` vale già 0 di suo, che è dove la vuole il classico, e lasciarla non impostata è
ciò che la tiene raggiungibile da uno stile.

**Verificato**: in modern la linea sta sotto «File tree»; in classic la scheda resta piena con la
barra sopra.

## M128 (2026-08-08, `7a33eb988`) — il menu nella barra del titolo, come VS Code

> Dall'utente: «tutta la toolbar (start, repository, navigate…) deve essere nella barra di sopra, la
> stessa dove ci sono le icone per chiudere e mettere in barra … se c'è poco spazio compaiono i "…"»,
> poi «la modalità deve poter essere selezionata nelle impostazioni di appearance (di default ci deve
> essere la modalità nuova)» e infine «non quindi in base allo stile modern o classic».

Lavoro delegato a un subagent in worktree, integrato con cherry-pick.

### Cosa dice X11 (sondato, non dedotto)
Sonda dentro l'app vera, sotto una sessione **GNOME Shell (mutter)** su Xvfb:

- `ExtendClientAreaToDecorationsHint = true` — con **ogni** valore di `ExtendClientAreaChromeHints` —
  è un **no-op** sul backend X11 di Avalonia 11.3.14: `IsExtendedIntoWindowDecorations` resta `false`,
  i margini restano a zero e mutter continua a disegnare la sua cornice da 37px. Non esiste una
  "client area estesa" in cui infilare il menu.
- `SystemDecorations.None` **è** onorato: la cornice sparisce del tutto (`BorderOnly` si comporta
  identico sotto mutter).
- È tutto-o-niente: niente cornice significa niente pulsanti di sistema, niente maniglia di
  trascinamento e **niente bordo di ridimensionamento**.

Quindi i pulsanti se li disegna l'app — minimizza, massimizza/ripristina (il glifo segue
`WindowState`) e chiudi, con i riempimenti di hover della chrome e il rosso sulla chiusura — e
`Views/ResizeGrips` restituisce gli otto bordi via `BeginResizeDrag` (`_NET_WM_MOVERESIZE`), nascosti
mentre la finestra è massimizzata.

### Cosa c'è
`App/Views/TitleBar.cs`: menu · titolo · pulsanti finestra su una riga, con `BeginMoveDrag` sull'area
vuota e doppio clic che massimizza. `App/Theming/WindowChrome.cs` tiene la modalità viva e la mappa
(assente o ignota → **unificata**). L'opzione sta in **Impostazioni → Appearance** come combo
«Title bar» («Menu in the title bar», default, / «Separate menu bar»), persistita in
`UiState.TitleBar` accanto a `Theme` e `Style`, con anteprima dal vivo e ripristino su Annulla. Come
chiesto, **non** dipende dallo stile: Modern e Classic prendono entrambi le due modalità, e la barra
unificata pesca dalla palette, quindi in Classic esce con la superficie classica.

**L'overflow è una misura.** La barra riserva i pulsanti più `min(larghezza del titolo, 220)` e passa
il resto a `MainMenu.FitTo`, che misura il menu senza vincoli, tiene in cache la larghezza naturale di
ogni voce, le percorre da sinistra contro il budget (riservando lo spazio per il «…») e rimanda il
re-parenting fuori dalla passata di misura. Nessuna soglia scritta a mano.

**Due bug veri trovati durante la verifica.** Il cambio di stile ri-templatizza le voci, quindi quelle
parcheggiate nel «…» conservavano larghezze vecchie e restavano bloccate lì (Classic→Modern perdeva
«Help»). E `InvalidateMeasure()` sul solo menu non arriva mai all'ospite: Avalonia rimisura con il
vincolo precedente e la dimensione desiderata non cambia — va invalidata anche la misura dell'ospite.

**Verificato** (screenshot, GNOME Shell/mutter su Xvfb): barra larga e a 800/480px con il «…»; il
flyout del «…» che elenca Commands/GitHub/Plugins/Tools/Help nell'ordine; un menu aperto dalla barra;
navigazione con le frecce fino al «…» e Alt+S che apre Start con le sottolineature dei tasti di
accesso; spostamento della finestra col trascinamento (+100/+100 esatti), doppio clic che massimizza e
ripristina, minimizza (→ `Iconic`), hover rosso su chiudi; ridimensionamento dal bordo destro, dal
bordo alto e dall'angolo in basso a destra; Light↔Dark che segue la palette viva; la pagina Appearance
con l'opzione; la modalità standard; e la barra unificata in **Classic** (`App.Toolbar` #333337, ink
#DCDCDC). **Cambio a caldo**: Modern↔Classic e unificata↔standard rifanno la finestra dal vivo (mutter
la incornicia e la scornicia al volo), anche dall'anteprima del dialogo — nessun riavvio, quindi il
dialogo non porta avvisi.

Build `Avvisi: 0 / Errori: 0`, harness navigation snapshot PASS.

## M127 (2026-08-08, `3b8ee7ec0`) — il pulsante della shell apre un terminale che sopravvive

> Dall'utente: «ho notato che il tasto bash, almeno in linux non fa niente».

Il cablaggio era a posto (`MainToolbar.LaunchCurrentShell` → `OpenShellRequested` →
`ExternalToolService.OpenTerminal`). A sbagliare era il **giudizio sul lancio**. Su questa macchina
`x-terminal-emulator` — la scelta di Debian, primo candidato della lista — è un collegamento a
**Warp**, la cui CLI rifiuta `-e`:

```
$ x-terminal-emulator -e bash
error: unexpected argument '-e' found
  tip: to pass '-e' as a value, use '-- -e'
```

Warp stampa il suo usage ed esce con 2 — ma `Process.Start` era già riuscito, quindi il launcher
riportava «Opened terminal in …», **smetteva di provare gli altri candidati** e nessun terminale
compariva. Il pulsante non faceva niente e la barra di stato diceva il contrario.

Ora un terminale ha una **finestra di grazia** (700 ms) per dimostrare di essere sopravvissuto: ancora
in esecuzione, oppure uscito con 0 (il client di gnome-terminal passa il lavoro al suo server e
ritorna), vale come aperto; un'uscita **diversa da zero** è un fallimento e si passa al candidato
successivo. Se falliscono tutti, il messaggio riporta la lamentela del programma — «unexpected
argument '-e'» — perché è la riga che dice il perché.

La lista dei candidati cresce dai cinque di prima: `kgx`, `ptyxis`, `tilix`, `terminator`,
`mate-terminal`, `alacritty`, `kitty`, `foot`, `urxvt`. `ExecArg` è diventato nullable per i due che
prendono il programma come **argomento finale nudo** (kitty, foot) invece che dietro una bandiera.

I lanci di strumenti esterni passano **fuori dal thread UI** (`MainWindow.WithRepo` via `Async.OffUi`):
la finestra di grazia è un'attesa, e sul thread UI un'attesa è una finestra congelata.

**Verificato**: `x-terminal-emulator -e bash` esce non-zero e viene saltato; il candidato successivo
(`gnome-terminal --working-directory <repo> -- bash`) parte e la barra di stato dice «Opened terminal
in /home/dario/git_ext_mod». Nota sul rig headless: gnome-terminal è client/server, quindi la finestra
si apre sul display dove gira **il server**, non su quello Xvfb del test. Build `Avvisi: 0 /
Errori: 0`, harness navigation snapshot PASS.

**Resta fuori**: Warp non viene usato affatto: il suo `-e` non esiste e non ha (per quanto visto) un
modo documentato di aprire una directory da riga di comando. Se lo si vuole come terminale del port,
serve un'impostazione «comando del terminale» dove l'utente scrive la riga esatta — non è stata
aggiunta qui.

## M126 (2026-08-08, `0156dd902`) — un dialogo si apre sul pulsante per cui è stato aperto

> Dall'utente: «quando si apre la finestra di push, devo cliccare per forza il pulsante, puoi lasciarmi
> il focus direttamente sul pulsante di push?».

Upstream il problema non ce l'ha: una form WinForms ha un `AcceptButton` e l'ordine di tabulazione
parte da lì. Una finestra Avalonia si mostra **senza nulla di focalizzato**, quindi aprire Push dalla
toolbar e poi dover tornare al mouse per premere Push è un viaggio di troppo.

`DialogKeys.FocusOnOpen(window, primary)` mette la tastiera sul pulsante principale appena la finestra
è su; lo usano **Push** e **Pull**. Focus, non attivazione: nulla parte finché l'utente non preme
Spazio o Invio. Non è applicato ai dialoghi che si aprono su una **domanda** — lì la tastiera
appartiene al campo da compilare — né a quelli distruttivi, dove un tasto di conferma non deve mai
essere lo stato a riposo.

Un dialogo che durante `Opened` mette il caret in un campo suo se lo tiene: questa è la riserva per una
finestra che nessuno ha reclamato, non un sovrascritto. Con il pulsante focalizzato la finestra ha
anche una rotta per i tasti, quindi Escape viene instradato — per questi due `EnsureFocusRoute` diventa
ridondante.

**Verificato** su Xvfb: la finestra di push si apre con l'anello di focus intorno a **Push**, e Invio
lo preme. Build `Avvisi: 0 / Errori: 0`.

## M125 (2026-08-08, `96715fac8`) — markdown nell'evidenziazione della sintassi

> Dall'utente, su un diff di `HANDOFF.md`: «mi sembra si sia rotta la syntax highlighting».

Non era rotta — verificato subito dopo M123 su `DiffView.cs` (i `using` blu) e su un `.csproj` (le
stringhe arancioni). Il markdown però non è **mai** stato fra i linguaggi riconosciuti: `Detect`
copriva cs, famiglia C, js/ts, py, sh, go, rs, sql, markup, config e json, e `.md` cadeva in
`_ => null`. Ed è il formato dei due file che in quel pannello si guardano più spesso.

Il markdown non entra nello scanner a parole chiave: parole chiave non ne ha, e il significato lo
porta la **forma della riga**. Ha una passata sua, sei marchi decisi in ordine:

1. una riga di fence (``` o ~~~) commuta il bit di blocco — **lo stesso** che ogni altro linguaggio usa
   per un commento a blocchi, quindi la macchina a stati per riga del renderer non cambia — e tutto
   ciò che sta dentro è code span;
2. un titolo (`#`…`######` seguito da spazio) è colorato per intero — `#tag` e i righelli di cancelletti
   più lunghi di sei non lo sono;
3. una citazione tiene il suo `>`, un elemento di lista tiene il suo marcatore (`-`, `*`, `+`, `12.`) —
   solo il marcatore: il testo è prosa e vuole ancora il colore della riga di diff su cui sta;
4. nella prosa: i code span fra backtick, `**grassetto**` / `*enfasi*`, e la **URL** di un link
   `[testo](url)` — la URL e non l'etichetta, perché l'etichetta è la prosa che si legge.

Nient'altro viene toccato: prosa che si legge come prosa è il punto. Approssimato come tutto il resto
del file: un diff mostra frammenti, quindi un fence aperto sopra l'hunk mostrato è invisibile; dentro
un patch lo stato è giusto, perché il renderer ripercorre le righe dall'alto.

**Verificato** su Xvfb, diff di `HANDOFF.md` e `PORTING.md`: code span arancioni, `**grassetto**` nel
colore delle parole chiave, e un blocco fence colorato per intero mantenendo il verde della riga
aggiunta. Build `Avvisi: 0 / Errori: 0`, harness navigation snapshot PASS.

## M124 (2026-08-08, `76595811c`) — la storia di un file tiene i suoi rami e i suoi merge

> Dall'utente, con le due finestre affiancate: «sulla sinistra ho cliccato su *filter file in grid*,
> a destra è lo stesso file su *file history* e non visualizza vari branch del file, ma fa vedere tutto
> in un unico ramo».

Stesso controllo (`RevisionGridView`) in entrambi i pannelli: diversa è la **domanda fatta a git**. Il
filtro sulla griglia emette `--parents` con semplificazione, quindi git **riscrive** i link ai parent e
il DAG resta connesso *e ramificato*. La finestra File History aggiungeva `--follow`, che la
riscrittura la sopprime del tutto (misurato, git 2.51). M116 aveva tamponato collegando ogni riga a
quella sotto: via la scaletta di monconi, ma rami e merge appiattiti in una linea. In più `--follow` è
fragile con più commit di partenza, quindi il walk era forzato su HEAD — «93 commits (current branch)»
contro i «97 (all branches)» della griglia.

**La soluzione è quella dell'originale**, che al walk del grafo `--follow` non lo passa affatto
(`RevisionGridControl`, commento «git log --follow is not working as expected»): due passi.

1. `git log --format=… --name-only --follow <opzioni rename> -- <path>` per raccogliere **tutti i nomi
   storici** del file;
2. il walk ordinario con quei nomi come pathspec, **senza** `--follow`.

Nuovo `App/Services/FollowedPathService.cs`. La seconda passata su tutti i nomi con `--all` parte
**solo se il file è stato davvero rinominato** (più di un nome), così un file mai rinominato costa una
chiamata sola; il risultato è in cache per `(repo, path, exactOnly)` e la prima pagina del walk la
rinfresca, quindi la paginazione paga l'espansione una volta.

Misurato su un repo di prova (rename + ramo laterale + merge con conflitto):

```
PRIMA  git log --parents --follow … -- sub/new.txt
  0cd34fa^508bd8c     508bd8c NON è nel risultato
  6d04cd1^3c7c1e1     3c7c1e1 NON è nel risultato
  … 6 righe, nessun merge, nessun ramo
DOPO   git log --parents HEAD --branches --remotes --tags -- "sub/new.txt" "sub/old.txt"
  508bd8c^446a78c 6d04cd1   ← il merge, con entrambi i parent presenti
  … 7 righe, ogni parent nel risultato
```

Su questo repository, `RevisionGridView.cs`: 65 righe con parent non riscritti → **67 righe, tutti i
rami, parent riscritti**.

**Caduto `--follow`, cadono i suoi vincoli**: scope, remoti/tag/stash, `--topo-order` /
`--author-date-order` e lo `--skip` vero tornano a valere. Riabilitati in modalità file-history:
**Branches** (all/current/filtered con il selettore dei ref) e **View** (remote branches, tag, stash,
note, ordine, commit per pagina, evidenziazioni); lo scope d'ingresso è ora **all branches**, e solo
all'ingresso, così una scelta successiva dell'utente sopravvive al refresh. Restano nascosti il
dialogo **Filter…** con la sua **✕** (il suo campo path litigherebbe con quello della scheda) e la voce
**Artificial commits** (il log di un file non contiene mai working directory e index).

**Il fallback resta**: cartella, più path, pathspec oltre 31000 caratteri (l'originale lì mostra un
errore), espansione fallita → si torna a `--follow` + `ChainFollowedHistory` di M116, e la pagina lo
dichiara con `RevisionPage.FollowedWithoutParentRewrite` — il concatenamento ora è deciso da **come è
andata la pagina**, non dal filtro richiesto. In quel caso lo scope torna a CurrentBranch, così
l'etichetta non mente.

La mappa dei **nomi storici** (`FileHistoryView._pathByHash`, che regge titolo, schede Diff/View/Blame
e «Save as») non si costruisce più con una chiamata git tutta sua: è un sottoprodotto del passo 1,
consegnato dal nuovo evento `FileHistoryPathsResolved`. Una sonda in meno per file.

**Verificato** su Xvfb: nel repo di prova la finestra mostra 7 commit «all branches» con la corsia
`feature` e il merge; selezionando il commit precedente al rename la riga di stato dice «In this
revision the file is named sub/old.txt» e il Diff gira su quel nome; Topo-order ora riordina davvero il
walk (prima era ignorato). Su questo repository, `HANDOFF.md`: 98 righe «all branches» e i due
`Merge branch 'linux-avalonia-port'` disegnati con le loro due corsie. Build `Avvisi: 0 / Errori: 0`,
harness navigation snapshot PASS.

**Da sapere** (non è una regressione, ed è identico all'originale): con *Show full history* attivo e
*Simplify merges* spento, `--full-history --parents` fa tenere a git **tutti** i merge — 3604 commit
per un file qui contro 71 senza `--parents`. `FilterInfo` di upstream emette la stessa coppia; prima il
`--follow` forzato mascherava la cosa.

## M123 (2026-08-08, `37658f73c`) — il pannello del diff virtualizzato su AvaloniaEdit

> Dall'utente: «questa finestra (quella di testo quando apro un file da Diff), se la scorro, a volte è
> lentissima, comincia a laggare … a volte è anche lentissima a caricare il diff del file».

### Misura, prima di toccare
Il pannello era **un solo blocco di testo** (`SelectableTextBlock` dentro uno `ScrollViewer`) con un
`Run` per riga, più altri `Run` per gli span di sintassi e le occorrenze della ricerca. Niente di
virtualizzato. Su un patch di **498 righe**: 119 ms di costruzione + **363 ms di layout**; con
l'evidenziazione forzata a zero ancora 61 + 238 ms — cioè **~0,5 ms per riga è il pavimento**, e
l'evidenziazione pesa solo il ~35%. Con «Show entire file» (≡) attivo git emette il file intero
(`-U1000000`) e ogni fotogramma di scroll ridisegnava **tutte** le righe, non solo quelle a schermo.

### La scelta
L'originale non si è mai scritto un renderer di testo: `FileViewerInternal` ospita l'editor di
ICSharpCode. Il port fa lo stesso con **AvaloniaEdit** (`Avalonia.AvaloniaEdit` **11.3.0**, fissato
alla linea 11.3: il pacchetto esce una build per minor di Avalonia e la 11.4.x compila contro API di
Avalonia 11.4 che sulla nostra 11.3.14 fallirebbero solo a run time — il floor `>= 11.0.0` del nuspec
non è una dichiarazione di compatibilità). Virtualizza per riga visibile, quindi layout, disegno **e**
colorazione smettono di scalare con la dimensione del file.

| caso | prima (build/totale) | dopo |
|---|---|---|
| patch di 30 righe | 0 / 4 ms | 0 / 8 ms |
| patch C# di 146 righe, sintassi on | 17 / 38 ms | 4 / 17 ms |
| file C# di 1382 righe, ≡ on | 32 / 241 ms | 1 / 4 ms |
| file di 997 righe, ≡ on | 14 / 671 ms | 13 / 40 ms |
| **`PORTING.md`, 6559 righe, ≡ on** | **95 / 1547 ms** | **65 / 70 ms** |

Il caso da 30 righe è rumore: l'editor ha un piccolo costo fisso di setup che il vecchio percorso non
aveva, ed è l'unico caso in cui il vecchio renderer reggeva il confronto.

### Com'è fatto
`RenderDiff` ora costruisce un `TextDocument` e fa due passate lineari (intestazioni di hunk,
occorrenze) invece di decine di migliaia di `Run`. Il nuovo `App/Views/DiffColorizing.cs` porta
`DiffLineClassifier` (le regole di prefisso, condivise fra il raccoglitore di hunk e il colorizer),
`DiffPalette` (i colori, ancora risolti pigramente come **istanze** di brush della palette, così la
mutazione in place di `ThemeManager` li raggiunge), `DiffLineColorizer` e `DiffSearchColorizer`.
`DiffSyntaxHighlighter` è riusato **intatto**: la virtualizzazione toglie lo stato progressivo dello
scanner, quindi il colorizer tiene un `bool[]` a crescita pigra e in avanti — «la riga n comincia
dentro un commento a blocchi» — con una scansione per riga solo per le righe davvero raggiunte.

### Cosa passa all'editor e cosa resta nostro
**Dell'editor**: selezione e caret (selezione libera col mouse verificata, drag e Ctrl+C compresi),
tutto l'indirizzamento per riga — «vai alla riga» e la navigazione ▲/▼ fra hunk **stimavano** la y di
una riga come `altezzaBlocco / numeroRighe`, ora è esatta — i segni ¶ (`ShowSpaces/ShowTabs/ShowEndOfLine`),
scrolling e virtualizzazione. **Nostri**: la barra di ricerca (UI tradotta, contatore, ▲/▼/F3/Esc/Enter,
casella «vai alla riga»): il `SearchPanel` di AvaloniaEdit ha una UI fissa non tradotta e nessun
«vai alla riga», quindi non è installato; la raccolta delle occorrenze e il lavaggio ambra, la
colorazione del diff e la sintassi, la raccolta degli hunk, la toolbar, il menu a ingranaggio, lo zoom,
il menu contestuale, la codifica e tutti gli interruttori dei flag di git.

### Tre cambi di comportamento, dichiarati
1. **¶ non riscrive più il testo.** Prima sostituiva spazi/tab/CR con `·`/`→   `/`␍` **nel documento**,
   quindi una selezione copiava il testo alterato e i tab si disallineavano; ora l'editor disegna i
   segni sopra i caratteri veri. Conseguenza: sparisce il `␍` esplicito per un CR isolato (l'editor
   disegna un marcatore di fine riga senza distinguere CRLF). È l'unica cosa davvero non conservata.
2. **Ctrl+C** nel diff ora scende all'editor quando c'è una selezione viva, e copia l'intero patch solo
   quando non c'è nulla di selezionato: intercettarlo sempre avrebbe reso inutile la selezione col mouse.
3. **Spariti i tetti dell'evidenziazione della ricerca** (`MaxHighlightLines` 20 000 /
   `MaxHighlightMatches` 2 000): esistevano perché ogni occorrenza costava un `Run`. Resta
   `MaxSearchMatches` (20 000), il cui tooltip ora dice «solo le prime N occorrenze sono elencate»
   invece di «troppe da evidenziare», che non era più vero.

### Pacchetto `.deb`
Nessuna modifica a `packaging/build-deb.sh`. Verificato con un `dotnet publish -c Release -r linux-x64
--self-contained true` vero: `AvaloniaEdit.dll` finisce nella publish, il launcher nativo e i 66
`Translation/*.xlf` ci sono ancora, e il `cp -a "$PUBLISH_DIR/."` dello script porta il nuovo assembly
in `/opt/gitextensions` da solo. AvaloniaEdit è puro managed senza payload nativo, quindi il
`Depends: git` del control resta valido.

**Verificato** su Xvfb: tinte +/− e colori dei token, `@@` e intestazioni, barra di ricerca
(«1 di 1», «4 di 112», lavaggio per occorrenza e ambra forte sulla corrente), F3, vai alla riga 900 su
1382, ▲/▼, ¶ on/off, A+/A−, menu contestuale a quattro voci, selezione col trascinamento e uno
switch Dark→Light a caldo. Build `Avvisi: 0 / Errori: 0`, harness navigation snapshot PASS.

## M122 (2026-08-08, `af9f0301a`) — lo stile classico ritrova il bitmap della lente

> Dall'utente, dalla console: `[IconLoader] icon 'Search' did not resolve (…/Search.png): no such asset`.

Il port chiama quel glifo con il nome di ciò che il pulsante **fa** (percorrere le occorrenze del
filtro dell'albero), mentre il set 2015 spedisce la stessa lente come `Preview.png`. Il nome del file
lo legge **solo** lo stile classico (`GlyphSource.Draw`): chiedeva un PNG che non esiste, stampava
l'avviso, ripiegava sul vettore — giusto a schermo, rumoroso nel log — e il bitmap che invece esiste,
sotto l'altro nome, non veniva mai disegnato.

`Icons.ClassicNameOf` mappa i pochi glifi battezzati col nome del comando sul file dell'originale, e
`GlyphSource.Draw` lo consulta prima dell'asset loader. Il modern non cambia: lì il vettore **è**
l'icona e nessun nome di file entra in gioco.

**Verificato**: avviato in Classic (`ui-state.json` → `Style: Classic`, poi ripristinato), **zero**
righe `[IconLoader]` nel log e la lente del 2015 disegnata accanto alla casella di ricerca dell'albero.
Build `Avvisi: 0 / Errori: 0`.

## M121 (2026-08-08, `c8ffa023c`) — anche la toolbar di File History è piatta

> Dall'utente: «allinea questi tasti di file history allo stile di quelli in commit».

I quattro pulsanti di `FileHistoryView` (Load file history, Detect and follow renames ▾,
Show Full History ▾, il log dei comandi) erano l'**ultima** striscia incorniciata rimasta: una fila di
scatole quattro righe sopra la barra piatta della griglia, in una finestra che vive accanto al dialogo
di commit, piatto da M107. Ora portano la classe `toolbtn` di `Theming.BarButtonStyles` come ogni
altra barra: niente bordo, nessun riempimento a riposo, `App.Hover` sotto il puntatore, `App.Pressed`
premuto. Stesso compromesso dichiarato a M115 — un pulsante di barra a riposo non ha contrasto
proprio, quindi etichetta più riempimento all'hover sostituiscono il contorno, in cambio di **una**
grafica di barra in tutta l'app. **Verificato** su Xvfb: striscia piatta e «Load file history» che si
accende al passaggio. Build `Avvisi: 0 / Errori: 0`.

## M120 (2026-08-08, `3f6a28c1b`) — la finestra File History disposta come l'originale

Confronto con `FormFileHistory.cs` / `.Designer.cs`. Lavoro delegato a un subagent in worktree,
integrato con cherry-pick.

- **La toolbar sta in cima.** Upstream ancora `ToolStripFilters` sopra `splitContainer1`; il port
  metteva la riga del path **sopra** gli interruttori, così la finestra si apriva su una stringa che la
  status line della griglia ripete identica due righe più sotto. Ora prima la toolbar, poi la riga.
- **La riga di stato è collassata quando non ha nulla da dire.** Il suo unico messaggio permanente è
  il nome che il file aveva nella revisione selezionata, e solo quando `--follow` ha attraversato un
  rename (upstream mette lo stesso fatto fra parentesi nel titolo; il port lo fa già). Resta la
  superficie per i messaggi di Save as / revert, con un unico setter che la mostra e la nasconde.
- **Una sola sonda sul blob.** Il marcatore «Git could not identify the file» era calcolato **due
  volte**, nella view e nella finestra; upstream lo calcola una volta e lo mette nella didascalia
  della scheda Commit — che è ciò che la finestra fa già da M113. Via il doppione, cioè via un
  `git cat-file` a ogni freccia.
- **Manca(va) un pulsante**: il **Git command log** (`gitcommandLogToolStripMenuItem`), aggiunto solo
  icona come upstream, aperto non modale perché questa finestra vive accanto alla principale. I due
  pulsanti con didascalia ora portano le immagini dell'originale (`ReloadRevisions`, `FileHistory`).
- **Proporzioni dello splitter**: upstream `SplitterDistance 101/419`, cioè un quarto alla griglia; il
  port apriva a 40/60. Ora `1*,4,3*` con un `MinHeight` **sulla riga** di 240 — pavimento che upstream
  non gli serve perché i suoi 101px sono 101px di righe (la sua toolbar è fuori dallo split), mentre il
  riquadro del port porta anche la striscia degli interruttori, la barra della griglia e la sua status
  line. Il pavimento **deve** stare sulla `RowDefinition`: una riga a stella non rilegge il `MinHeight`
  del figlio, e al primo tentativo la griglia sbordava dipingendo sopra le schede (visto a schermo,
  poi corretto).
- **Ctrl+Tab / Ctrl+Shift+Tab** ciclano le quattro schede saltando quelle disabilitate (il `TabControl`
  di WinForms lo fa gratis, quello di Avalonia no; upstream le stacca, quindi il suo ciclo non può
  fermarcisi). Registrati in tunneling, così gli editor e la navigazione del focus non se li mangiano.

**Verificato** su Xvfb: toolbar in cima con i quattro pulsanti e nessuna riga del path duplicata, le
due tendine con i loro stati, «Load file history» che ricarica, griglia a un quarto e schede a tre
quarti, Ctrl+Tab che percorre Diff→View→Blame (ognuna caricata pigramente all'arrivo) e Ctrl+Shift+Tab
che torna a Commit, il pulsante del log che mostra le chiamate `git log --follow` / `blame` di questa
finestra. Su un file davvero rinominato la riga dice «In this revision the file is named …» e la
scheda Diff carica sotto il nome storico (M113 intatto); Shift su quattro righe dà il diff di
intervallo con `HANDOFF.md` selezionato e l'intestazione «(9) Diff with A 9604da5a: …» (M116 e M117
intatti). Build `Avvisi: 0 / Errori: 0`, harness PASS.

**Non portato, con motivo.** «Load history on show» / «Load blame on show»: esistono perché upstream
lancia un **processo** separato il cui primo log può essere lento — qui la finestra è in-process e già
carica pigramente per scheda — e persisterli vuol dire un campo nuovo in `ViewPrefsService`, fuori dal
perimetro dell'unità. La tendina «Blame options» (11 interruttori): il port ha già
ignore-whitespace / detect-copy-in-file / detect-copy-in-all nella barra di `BlameView`, accanto alla
blame che riplasmano; duplicarli su una striscia che di solito guarda un diff darebbe due fonti di
verità. Difftool e «Difftool selected ↔ local» (F3): appartengono al pannello diff. Dimensioni:
tenute quelle del port (1100×700, min 640×420) invece dei 748×444 di upstream, che precedono il grafo
e il set di colonne che questa griglia disegna. Le righe artificiali: la griglia della storia di un
file non ne mostra, quindi non c'è nulla da staccare.

## M119 (2026-08-08, `fc24d6e8e`) — una toolbar sola, un filtro solo, come in `FormBrowse`

Confronto voce per voce fra `ToolStripMain` (`FormBrowse.Designer.cs`, righe 205–224),
`FormBrowse.InitMenusAndToolbars.cs`, `FilterToolBar.Designer.cs` e la barra del port. Lavoro
delegato a un subagent in worktree, integrato con cherry-pick.

**La striscia principale era già fedele**: tutte e 19 le voci dell'originale, nello stesso ordine, con
i separatori al posto giusto, più due aggiunte del port (Apri repository, Nuovo branch). Le
divergenze vere erano altrove.

- **Superficie di filtro duplicata, rimossa dalla toolbar.** `ToolStripFilters` era ripetuto **due
  volte** nel port: una mezza copia in alto e quella completa sulla barra della griglia (M115). Il menu
  «All branches ▾» in alto per giunta scriveva sempre «All branches» anche con la griglia sul solo
  branch corrente — non ridondante, **falso**. Via il menu e via la coppia «Filter:» + casella:
  la barra della griglia porta già tutto (filtro rapido, tipo di campo, MRU, ambito dei branch con il
  selettore dei ref, il dialogo `Filter…`, la `✕` di reset). `Ctrl+E` ora va dritto alla casella della
  griglia.
- **Indicatore «repo — path (branch)» in fondo a destra, rimosso**: nessun corrispettivo in
  `ToolStripMain`, terza copia dello stesso dato, ed era la prima cosa che finiva nell'overflow «»`.
- **Aggiunte le due scorciatoie di `InsertFetchPullShortcuts`** che mancavano: Pull - merge e
  Pull - rebase, solo icona, che alzano `PullActionRequested` (già instradato da
  `MainWindow.RunPullAction`).
- **Suffissi di hotkey nei tooltip** dove upstream li annota in `RefreshShortcutKeys`: Open, Refresh
  (F5), Fetch, Commit, selettore di branch, shell.
- Eliminata la macchineria di overflow ormai morta (`OverflowKind.Menu/Filter/Text/Skip`,
  `MakeMenuButton`, `SubItems/TextSource/FilterBox`).

**Non fatto, con motivo.** «Fetch all» e «Fetch and prune all»: upstream li distingue con tre PNG
diversi, mentre `Icons.cs` mappa `PullFetch`, `PullFetchAll` e `PullFetchPruneAll` sullo **stesso**
glifo — solo icona sarebbero tre pulsanti identici. Restano nel menu a tendina di Pull; servono tre
glifi nuovi. Il sesto clone upstream, «Pull» semplice, fa esattamente ciò che fa il corpo dello split
button accanto, con la stessa icona. `ToolStripScripts` non ha nulla da mostrare: il port non ha gli
script utente (SKIP dichiarato). La **barra di stato** resta: upstream non ne ha
(`toolPanel.BottomToolStripPanelVisible = false`), ma è l'unico canale di feedback delle operazioni
del port — e dopo queste rimozioni non duplica più niente della toolbar.

**Verificato** su Xvfb a 1600×1000: la striscia ora **entra tutta**, nessun overflow «»` (prima
traboccava, nascondendo proprio la casella di filtro e l'etichetta del repo); gruppo remoto
Fetch ↓ · Pull-merge · Pull-rebase · Pull ▾ · Push ↑ · Commit · Stash ▾; **una** sola casella di
filtro a schermo. Build `Avvisi: 0 / Errori: 0`, harness navigation snapshot PASS.

## M118 (2026-08-08, `1582e4128`) — il grafo delle revisioni disegnato come nell'originale

> Dall'utente, con gli screenshot a confronto: «l'interfaccia è parecchio differente».

Prima cosa che salta all'occhio in un confronto affiancato: il **grafo**. Lavoro delegato a un
subagent in worktree, integrato qui con cherry-pick.

- **Palette delle corsie** — via gli otto colori inventati dal port, dentro i sette
  `AppColorDefaults.GraphBranch1..7` dell'originale (`#F064A0`, `#78B4E6`, `#24C221`, `#A078F0`,
  `#DD3228`, `#1AC6A6`, `#E7B00F`), nello stesso ordine. `GraphBranch8` upstream è `Color.Empty`,
  quindi il ciclo è di **sette**, come lo calcola `RevisionGraphLaneColor`.
- **Metriche** — `LaneWidth` 14 → 16, nodo 8 → 10, tratto della corsia 2: i valori di
  `GraphRenderer.LaneWidth/NodeDimension/LaneLineWidth` al 100%.
- **Forma del nodo e marcatore di HEAD** — nodo **quadrato** quando la riga porta dei ref, cerchio
  altrimenti, più un anello di 2px sulla riga di HEAD: sono esattamente le regole `square`/`hasOutline`
  di `GraphRenderer.DrawItem`. L'anello usa `App.Text` (upstream `SystemColors.WindowText`) per
  sopravvivere alla palette scura.
- **Segmenti** — un cambio di corsia è ora una Bézier cubica che esce e rientra **verticale** dal nodo
  e si adagia sulla diagonale (`RenderGraphWithDiagonals`, attivo di default upstream); le verticali
  pure restano linee dritte e nitide.
- **`StraightenLaneShifts`** in `BuildGraph` — i segmenti sono memorizzati **spezzati sul nodo** (metà
  sopra, metà sotto), quindi un arco di branch/merge da una corsia faceva tutto lo spostamento dentro
  una metà e scendeva dritto nell'altra: pendenza doppia rispetto all'originale, con un gomito sul
  bordo riga. Il passo fa incontrare le due metà a metà dello spostamento, ottenendo un'unica diagonale
  da nodo a nodo come `GraphRenderer`. Scatta solo dove il collegamento non è ambiguo (esattamente una
  metà per lato del bordo, in quella corsia), quindi un merge che entra in una corsia che prosegue
  dritta resta com'è. `FromLane`/`ToLane` logici non si toccano: `ComputeGraphRelatives` e il conteggio
  delle corsie sono intatti (M114 e M116 salvi).
- **Larghezze di colonna** — Author 170 → 130, Commit ID 90 → 64 (upstream 60, più lo spazio per lo
  short hash a 8 caratteri che questo port mostra sempre): ~70px restituiti alla colonna Subject.
  L'**ordine** era già quello dell'originale; era sbagliato solo il commento di classe che diceva
  altro.

**Verificato** su Xvfb a quattro posizioni di scorrimento: corsia rosa nella palette upstream, nodo di
HEAD quadrato con anello, righe con ref quadrate e commit semplici tondi, l'arco del branch verso HEAD
come **una** diagonale piena (niente gomito), 3–4 corsie concorrenti con la riga `Merge tag 'v4.2.1'`
che emette il suo arco ambra. Build `Avvisi: 0 / Errori: 0`, harness navigation snapshot PASS.

**Non portato, con motivo.** Il nodo delle righe artificiali resta il quadrato vuoto del port
(l'originale le disegna tonde e le distingue con icone nella colonna del messaggio, che qui non ci
sono: toglierlo perderebbe l'indizio senza guadagnare quello upstream). Il colore delle non-relative
resta `App.TextDim` invece del `Color.LightGray` letterale, invisibile sul fondo scuro; il
*comportamento* già coincideva. `StraightenGraphDiagonals`, `MergeGraphLanesHavingCommonParent` e
`ReduceGraphCrossings` non sono portati: lavorano sul modello a segmenti/`LaneSharing` di upstream,
che lo sweep di corsie del port non ha, e agiscono sull'**assegnazione** delle corsie, non sull'aspetto
di un layout dato.

## M117 (2026-08-08, `c92d4252b`) — la lista dei file dice di che confronto è

> Dall'utente, con gli screenshot dell'originale a confronto: nella lista dei file cambiati
> l'originale scrive «(5) Diff with A 8c37814a: …», il port mostrava solo le righe dei file.

L'originale intesta **ogni** lista di file cambiati con il confronto da cui viene — è
`FileStatusWithDescription.Summary`, composto in `FileStatusDiffCalculator` come
`TranslatedStrings.DiffWithParent + DescribeRevision(a)`. Senza quell'intestazione il pannello non
dice mai **quale sia il lato «A»**: per un commit singolo è il suo primo parent, per una selezione
multipla è l'estremo più vecchio, e dalle sole righe dei file i due casi sono indistinguibili.

`FileStatusListView.SetFiles(rows, summary)` disegna il riepilogo come riga di intestazione sopra i
gruppi che il builder ha già prodotto. È un `FileListGroupNode` normale: piegarlo piega tutta la
lista e nient'altro nel controllo deve sapere che è speciale. Una lista che **non** è un confronto
(l'albero dei file, le liste staged/unstaged del dialogo di commit, che si presentano da sole con le
proprie didascalie) non passa alcun riepilogo e resta identica a prima.

Nominare una revisione costa una chiamata a git (`git log -1 --format=%s`), quindi
`DiffService.DescribeRevision` / `FirstParentOf` vengono invocate sul **thread di background** del
load, accanto al diff stesso: il riepilogo viaggia col load, come la preselezione da M116. Il commit
radice è confrontato con l'albero vuoto — non c'è revisione da nominare, e la lista resta senza
intestazione invece di scriverne una che non nomina nulla.

**Verificato** su Xvfb: commit singolo → `(5)  Diff with A 94525c1d: docs(crossplatform): reco…`;
Ctrl su un secondo commit → `(9)  Diff with A 932d478e: fix(crossplatform): the gr…`, cioè l'estremo
più vecchio. Build `Avvisi: 0 / Errori: 0`.

**Differenza che resta.** Con 2–4 revisioni selezionate e `ShowDiffForAllParents` attivo, l'originale
mostra **più gruppi** (il merge base e i due lati); il port ne mostra uno, il diff fra gli estremi.
Non è un caso d'uso segnalato dall'utente e costa tre confronti in più per selezione.

## M116 (2026-08-07, `2924ccc7e`) — una sola linea nella storia di un file, e il diff di una selezione multipla

> Dall'utente, sulla finestra File History: «la linea relativa ai commit è duplicata per ogni commit,
> dovrebbe seguire il normale flusso/collegamento tra un commit e l'altro» e «il diff … non funziona
> nel caso di selezione di due commit (ctrl) o di un gruppo di commit (shift)».

### Il grafo: `--follow` non riscrive i parent
Ogni walk ristretto ottiene da git la **riscrittura dei link ai parent** (`--parents` + la
semplificazione della storia: `%P` nomina allora l'antenato più vicino **sopravvissuto**), ed è quella
che tiene il DAG connesso attraverso i commit che il filtro ha tolto. `--follow` è l'eccezione —
misurato, git 2.51:

```
git log --parents --follow --format=%h^%p -- src/crossplatform/HANDOFF.md
  94525c1d0^4384fbc1c   <- NON è nel risultato
git log --parents        --format=%h^%p -- src/crossplatform/HANDOFF.md
  94525c1d0^b292aa32d   <- riscritto, come ovunque
```

Quindi seguendo i rename ogni riga puntava a un parent che non è a schermo: il passo delle corsie non
trovava nessuna corsia in attesa, ne apriva una nuova per ogni riga e la chiudeva una riga dopo. Da
qui la scaletta di monconi al posto di una linea sola. `RevisionService.ChainFollowedHistory`
ri-collega ogni riga a quella **sotto**, che è esattamente ciò che la riscrittura avrebbe nominato
(`--follow` produce una storia lineare per costruzione: git lo rifiuta con più path e il walk è
forzato su un solo commit di partenza e sull'ordine di default). Tocca **solo**
`RevisionRow.GraphParents`: navigazione del DAG, menu dei parent e diff continuano a usare la
parentela vera del commit. L'ultima riga caricata resta senza arco verso il basso — il walk continua
lì sotto, e puntare a un commit che non c'è è proprio il difetto che questo risolve.

### Selezione di due o più commit
Due righe selezionate diffavano già il range; **da tre in su** si cadeva nel ramo del commit singolo e
il pannello mostrava il diff di qualunque riga fosse `SelectedItem`: la selezione diceva «confronta
questi» e la risposta era un commit solo. Ora `RangeEnds` prende i due **estremi** della selezione
(salta le righe artificiali: una riga "working directory" presa dentro uno Shift non è un commit e
non può essere un capo del range), così Ctrl su due commit e Shift su un intervallo rispondono allo
stesso modo — `git diff piùVecchio piùNuovo`.

Un Ctrl+clic alza `SelectionChanged` **due volte** (rimozione e aggiunta sono riportate separatamente)
e ogni annuncio costa un `git diff` all'host: il range viene annunciato solo quando cambia davvero.

### La finestra File History ignorava del tutto i range
Era iscritta al solo `RevisionSelected`, quindi una selezione multipla lasciava le schede sull'ultimo
commit singolo. Ora `FileHistoryView` inoltra `RangeSelected` e la finestra confronta i due estremi,
con la scheda **Diff** che atterra sul file di cui la finestra parla (`DiffView.ShowRange` ha un
overload con `preselectPath`); le altre tre restano sull'estremo più nuovo, perché un blob e una
blame appartengono a **una** revisione. La chiave della cache delle schede è il **confronto**
(`base..hash`), non la revisione: passare da due commit selezionati al solo più nuovo lascia `_hash`
invariato e deve comunque ricaricare il Diff.

### Il bug della preselezione, trovato per strada
La preselezione viaggiava in un campo della view. Con **due** load in volo per un solo gesto (vedi il
doppio `SelectionChanged`), il secondo consumava quello che il primo aveva già usato e la lista
ripiegava sulla prima riga: si vedeva il diff di `MainWindow.cs` con `HANDOFF.md` decimo in elenco.
Ora la preselezione viaggia **con** il load. Diagnosticato con un log temporaneo che stampava
chiamante, preselezione e righe della lista.

### Verifica (Xvfb, repo di lavoro)
Finestra File History su `src/crossplatform/HANDOFF.md`, 86 commit: **una** linea verticale con i
nodi, non più la scaletta. Ctrl su due commit (M111 + M114) → `diff 653229ce…b292aa32`, 11 file, con
`HANDOFF.md` selezionato e il suo diff a schermo; Shift su cinque commit → `diff 653229ce…94525c1d`,
12 file. Nella finestra principale, Shift su cinque commit → `diff b04038cb…4384fbc1`, 4 file (prima:
il diff di un commit solo). Build `Avvisi: 0 / Errori: 0`, harness navigation snapshot PASS.

### Resta aperto
L'utente segnala anche che «l'interfaccia è parecchio differente» dall'originale: non ancora
affrontato, serve sapere **quali** differenze contano (vedi la domanda posta a fine sessione). Nota
nota: nell'originale una selezione multipla produce **più gruppi** nella lista dei file — «(N) Diff
with A `<sha>`», un gruppo per ogni revisione selezionata confrontata con la prima — mentre il port
mostra un gruppo solo, il diff fra i due estremi.

## M115 (2026-08-07, `932d478ee`, `4384fbc1c`) — la barra della griglia e le sue tendine parlano la lingua dell'app

> Dall'utente: «allinea la grafica di questi tasti a quella della schermata di commit» (la seconda
> riga di barra della griglia) e poi «rendi consistenti anche i pulsanti di questi menu sopra, inoltre
> sistema i bordi di tutte quelle tendine (goto, branches, commit message …) perché sono grossi e
> strani».

### I pulsanti della barra della griglia (`932d478ee`)
`Go to`, `Branches`, `View`, `Date`, `Columns`, `Filter…`, il `⌄` delle ricerche recenti e la `✕`
del filtro erano gli ultimi pulsanti incorniciati dell'app: una fila di scatole sotto una toolbar
principale piatta (M77) e accanto a un dialogo di commit piatto (M107). `MakeBarButton` ora è
piatto e porta la classe `toolbtn`, e la vista installa `Theming.BarButtonStyles.Apply(Styles)`:
niente bordo, nessun riempimento a riposo, `App.Hover` sotto il puntatore, `App.Pressed` premuto.
La casella di ricerca tiene il suo contorno perché è un **input**, non un comando.

**Compromesso dichiarato.** Il contorno era l'affordance: un pulsante di barra a riposo non ha
contrasto proprio contro la striscia, quindi toglierlo perde il 3:1 di WCAG 1.4.11 sul confine.
Restano l'etichetta e il riempimento all'hover — esattamente ciò che regge la toolbar principale da
M77. È una scelta di coerenza: o piatte tutte le barre, o incorniciate tutte, non una sola diversa.

### Le tendine della stessa barra (`4384fbc1c`)
Quelle sei tendine non sono `MenuFlyout`: sono `Flyout` con dentro controlli veri (radio, checkbox,
una casella per l'hash), perché mescolano opzioni e comandi. Due difetti, tutti e due visibili nello
screenshot dell'utente:

1. **Bordo doppio.** Ogni tendina avvolgeva il proprio pannello in un `Border` (`App.Panel`, padding
   2) *sopra* il `FlyoutPresenter` di Fluent, che è già una scatola piena, contornata e con un
   padding generoso. Due cornici con in mezzo una striscia di un terzo colore: ecco il bordo «grosso
   e strano». Ora la carta è il presenter e basta — superficie della palette, un capello di
   `App.Border`, padding proprio a zero (il margine ce l'ha già il pannello di ogni carta), minimi di
   Fluent (96×40, pensati per la prosa) azzerati — e i `Border` interni sono spariti. Sta nel blocco
   **baseline**, perché la cornice doppia è un difetto in entrambi gli stili; il blocco modern
   aggiunge l'angolo arrotondato e l'ombra che gli altri menu hanno già da M109.
2. **Comandi a scatola.** `MakeMenuButton` produceva pulsanti Fluent — rettangolo pieno e
   contornato ciascuno — quindi una carta di quattro comandi si leggeva come quattro scatole
   impilate dentro una cornice, mentre ogni menu contestuale accanto disegna righe piatte. Ora
   portano la classe `menubtn` di `BarButtonStyles.ApplyMenus`: piatti a riposo, pillola arrotondata
   sotto il puntatore, cioè la stessa forma che `ModernStyles.MenuStyles` dà a un `MenuItem` vero.

**Perché `ApplyMenus` va sull'`Application` e non sulla vista.** Il contenuto di un flyout vive in
una **pop-up root** propria: le `Styles` dichiarate sul controllo che possiede il flyout non lo
raggiungono mai, solo quelle dell'applicazione. Per questo non si poteva riusare `Apply`, che ogni
barra installa su sé stessa; l'installazione avviene in `BuildBaseline`.

### Verifica
Build `Avvisi: 0 / Errori: 0`, harness navigation snapshot PASS. In GUI su Xvfb: la carta di `Go to`
ha **una** cornice, le quattro voci sono righe piatte, il puntatore su «Backward» accende la pillola,
la casella dell'hash tiene il suo contorno; stessa cosa per `View` (la carta lunga con checkbox,
radio e il comando in fondo) e per la tendina del tipo di filtro. Angoli tondi e ombra non si vedono
sotto Xvfb — manca il compositore, degrado noto e documentato su `PopupShadowRoom`.

## M114 (2026-08-07, `571b87864`) — colonne della griglia ridimensionabili

> Dall'utente: «rendi le colonne della home screen di subject, author e date ridimensionabili (vedi
> se lo sono anche le altre nel programma originale)».

### Com'è in originale (verificato)
La griglia è una `DataGridView`, quindi il ridimensionamento arriva gratis: `AllowUserToResizeColumns`
**non è impostato da nessuna parte** e resta al default `true`; sono i singoli provider a tirarsi
fuori.

| Colonna | Ridimensionabile | Larghezza di default |
|---|---|---|
| Grafo | **no** (`TriState.False`) | calcolata dalle corsie |
| Subject/messaggio | sì — ed è la colonna **Fill** | 500 |
| Note | sì | 50 |
| Avatar | **no** | forzata all'altezza della riga a ogni paint |
| Autore | sì | 130 |
| Data | sì | 130 (o la misura di una data completa) |
| Commit ID | sì (`TriState.True` esplicito) | 60, con tetto = larghezza di uno SHA intero |
| Build status | solo in modalità testo | 150 / 16 |

Minimi 25px (32 per l'id). **Le larghezze NON sono persistite**: a ogni avvio le colonne sono
ricreate ai default. L'ultima colonna visibile ridimensionabile viene inoltre resa *non*
ridimensionabile perché si allunghi con la griglia.

### Cosa abbiamo fatto
Il port disegna la propria intestazione, quindi i divisori vanno disegnati anche loro: una **striscia
di presa da 6px** a cavallo del bordo **sinistro** delle colonne autore, data e commit id — lo stesso
insieme che l'originale lascia trascinare, grafo e avatar esclusi. Il **subject non ha una larghezza
propria** né qui né là: è la colonna che dà e prende lo spazio (l'unica `*`), ed è per questo che i
divisori stanno dal lato che le dà le spalle e che trascinarne uno **è** ridimensionare il subject.

Due pavimenti: **40px** per una colonna trascinata e **120px** per il subject, il cui margine di
manovra è il tetto di ogni trascinamento. Senza il secondo, una tirata decisa rendeva i messaggi
illeggibili.

Durante il trascinamento **seguono anche le righe**, senza essere ricostruite: ri-templatizzarle a
ogni movimento del puntatore sarebbe una presentazione a diapositive, ma le righe che *esistono* sono
quelle **realizzate** — una schermata — e le loro definizioni di colonna si spostano come quelle
dell'intestazione. Una riga realizzata durante il trascinamento è già giusta, perché `MakeColumns`
legge gli stessi campi. (Il primo tentativo muoveva solo l'intestazione e rifaceva le righe al
rilascio; l'utente ha chiesto di farle scorrere insieme, ed era giusto: costa una schermata di grid,
non la storia intera, e sparisce anche il lampeggio al rilascio.)

**Le larghezze si ricordano** (`ViewPrefs.GridColumns`). L'originale no — ricrea le colonne ai default
a ogni avvio — ed è una differenza voluta: una larghezza trascinata è una decisione. La griglia della
finestra File history legge le stesse preferenze, quindi si apre come l'hai lasciata.

### Seguito immediato (`fix`, stesso giorno)
Le prese erano **invisibili** — 6px di Border trasparente e un cursore che parla solo quando ci sei
già sopra — e la prima domanda dell'utente è stata «non ho capito cosa hai fatto resizable»,
guardando proprio quella griglia. Ogni presa ora porta un **filo da 1px** in `App.Border`, così il
confine è disegnato dove si può afferrare, e il filo diventa `App.Accent` **al passaggio del
puntatore**: il segno dice «qui c'è un divisore», l'accensione dice «questo è vivo».

Poi, sempre su segnalazione: col filo disegnato, etichetta e valori partivano **attaccati** ad esso
(`|Author`, `|daryda9`), che si legge come un bordo attorno al testo invece che come un confine fra
colonne. Ora **6px di margine** su ogni cella delle tre colonne che hanno un divisore a sinistra,
messi dentro `AddCell` e non ai punti di chiamata, così intestazione e righe non possono
disallinearsi: passano tutte di lì. Grafo e subject restano attaccati al bordo della griglia, dove non
c'è niente da cui scostarsi.

### Verifica
Su Xvfb: trascinando il divisore dell'autore di 90px a sinistra la colonna si allarga e il subject si
stringe **sia nell'intestazione sia nelle righe**; trascinando quello del commit id molto a sinistra
ci si ferma col subject al suo pavimento invece di schiacciarlo; le larghezze **sopravvivono al
riavvio** (stanno in `view-prefs.json`). Build `Avvisi: 0`, harness PASS.

## M113 (2026-08-07, `10614aa03`) — la storia di un file in una finestra sua, come in originale

> Dall'utente: «la scheda "file history" in basso, nel programma originale è una schermata a parte,
> controlla quando e come viene avviata e che struttura ha nel progetto originale, quindi sviluppa
> anche nella nostra dopo aver analizzato accuratamente».

### Cosa fa l'originale (analisi)
`StartFileHistoryDialog` (`IGitUICommands.cs:75`) **non costruisce la form**: lancia un **processo
separato** di Git Extensions con una riga di comando `filehistory`/`blamehistory`
(`GitUICommands.cs:1278-1308`). I chiamanti sono il menu contestuale di `FileStatusList`
("File &history", "Blame"), il doppio clic sulla lista file, `RevisionDiffControl`,
`FormResolveConflicts` e l'estensione shell di Windows; le scorciatoie **H** e **B** appartengono a
`RevisionDiffControl`, non alla form.

`FormFileHistory` (748×444, `Designer.cs`) è: barra dei filtri in alto, poi uno split orizzontale con
**la griglia delle revisioni del file** sopra e sotto un tab control di **quattro** schede —
**Commit** (`CommitDiff`), **Diff** (`FileViewer` limitato al file), **View** (il blob a quella
revisione), **Blame** (`BlameControl`). Nessuna barra di stato. `UpdateSelectedFileViewers` carica
**solo la scheda visibile** e ricarica al cambio di selezione *o* di scheda. Il titolo è
`File History - <path> [(<nome storico>)] - <working dir>`.

Nota trovata nell'analisi: dal default `UseBrowseForFileHistory = true`, l'app moderna apre in realtà
`FormBrowse` in modalità file-history; `FormFileHistory` è il percorso legacy — ed è quello che
descrive la *schermata a parte* chiesta.

### Cosa abbiamo fatto
Nuova `Views/FileHistoryWindow`: sopra la `FileHistoryView` esistente (griglia, "Detect and follow
renames", "Show Full History" — invariata), sotto le **quattro schede** di upstream su
`CommitDetailView`, `DiffView`, la nuova `FileContentView` e `BlameView`. Caricamento **pigro per
scheda e per revisione** come `UpdateSelectedFileViewers`. Non modale, Escape chiude, titolo composto
come `SetTitle`.

**I rename sono il motivo per cui il nome storico gira ovunque**: chiedere a git il path di oggi in un
commit vecchio non restituisce niente. Ogni viewer riceve `GetFileNameForRevision`, e il titolo mette
il vecchio nome tra parentesi quando se ne attraversa uno.

Due pezzi di supporto (scritti da due subagent in parallelo, su file distinti):
- `DiffView.ShowCommit(repo, hash, preselectPath)` — la scheda Diff si apre **sul file** della
  finestra e non sulla prima modifica del commit (quella di upstream è file-scoped);
- `Views/FileContentView` — il blob alla revisione, letto con `DiffTextService.GetFileBytesAsync` e
  la stessa codifica del pannello diff, con gutter dei numeri di riga, guardia sul binario e guardia
  di generazione contro un caricamento vecchio che atterra sopra uno nuovo.

**La scheda "File history" in basso non c'è più**: la aprono la finestra tutti i punti d'ingresso —
menu del pannello diff, menu e **doppio clic** dell'albero dei file, Ctrl+Shift+F, e le voci
File history / Blame del dialogo di commit. Un `ui-state.json` vecchio che contiene `"History"`
ripiega su Commit. Le due finestrelle ad hoc del dialogo di commit collassano in questa — che è poi
quello che fa upstream: `filehistory` e `blamehistory` sono **la stessa form** aperta su una scheda
diversa. La griglia della finestra riceve i comandi di commit e il gate del bisect della griglia
principale attraverso una lista che l'host ora registra, perché una griglia può nascere **dopo** che
il menu è stato costruito.

### Verifica
Su Xvfb, repo di prova con un rename (`a.txt` → `b.txt`): la finestra si apre dal menu del diff e dal
doppio clic nell'albero; il titolo è `File History - b.txt - /tmp/m113/repo` e diventa
`b.txt (a.txt)` su un commit **precedente** al rename; su quel commit **Diff** si apre con `a.txt`
selezionato e la sua patch, **View** mostra le quattro righe di `a.txt` a quella revisione, **Blame**
fa blame di `a.txt`, **Commit** descrive il commit. Nessuna eccezione, nessuna riga `[Async]`.
Harness PASS, build `Avvisi: 0`.

**Non verificato**, da guardare quando capita: il caso "il file non esiste in questa revisione"
(schede Diff/View/Blame disabilitate e suffisso di upstream sulla scheda Commit) — la storia di prova
non ha un commit in cui il file manchi.

## M112 (2026-08-07, `0cba58207`) — il menu che scorre risponde alla rotella e al puntatore

> Dall'utente: «non funziona bene lo scroll, ad esempio con il touchpad funziona male se vado su o
> giù, inoltre se rimango con il cursore sulla freccia di sotto non scorre, devo per forza cliccare».

Due difetti veri del menu reso scorrevole in M111.

**La rotella** muoveva **una riga per scatto**. Col mouse è lento in un menu da trenta voci; col
touchpad, che manda **frazioni** di scatto, si legge come un menu che non si muove e poi salta. Ora
sono **84px — tre righe — per scatto**, e una frazione di scatto muove una frazione di quello: che è
esattamente lo scorrimento morbido che mancava al touchpad.

**Restare sul chevron** non faceva nulla: Fluent li disegna come `RepeatButton`, che agiscono solo
mentre sono **premuti**, quindi leggere il menu voleva dire un clic per riga. Ora il puntatore fermo
sopra scorre, **6px ogni 16ms**, finché non se ne va.

Entrambi stanno in `Theming/MenuScrolling`, agganciati da una **attached property** che una style
mette sullo `ScrollViewer` dentro il popup: nessuna view ne sa niente e gli handler muoiono con lo
ScrollViewer. Applicato a tutti e tre gli ospiti che possono produrre una lista più lunga dello
schermo: tendina della barra, menu contestuale, flyout di uno split button. I due chevron si
distinguono **per posizione** e non per nome: i nomi sono del template di Fluent.

**Verifica** su Xvfb: uno scatto muove tre righe (misurato sulla voce in cima alla carta), il
puntatore fermo sul chevron in basso porta il menu Vista fino a Refresh in fondo, quello in alto lo
riporta a Branches, e nel log non resta niente.

## M111 (2026-08-07, `6926e58fb`) — il menu si ferma al bordo inferiore della finestra

> Dall'utente, con screenshot: «ora va bene il margine superiore, ma esce dal bordo sotto della
> finestra. deve fermarsi prima e mettere la freccia per scorrere come prima».

M110 aveva impedito alla tendina di **salire** sopra la propria barra, ma poteva ancora **sforare in
basso**: un popup è una finestra a sé, quindi il posizionatore lo limita allo **schermo**, e su una
finestra più piccola dello schermo — il caso normale — il menu Vista finiva sul desktop sotto l'app.

Nessuna style può conoscere quell'altezza: è una proprietà **della finestra** e di dove è finita la
sua barra dei menu. Quindi la finestra la misura e la pubblica come **`App.MenuMaxHeight`**
(`MainWindow.PublishMenuMaxHeight`, ripubblicata al ridimensionamento, al layout della barra e
all'apertura) e la carta del menu la legge come **dynamic resource**. Sotto il tetto il menu
**scorre**, che è ciò che serviva comunque alle voci sotto la piega.

Un pavimento di 160px tiene il menu usabile su una finestra ridotta a niente: meglio un moncone che
scorre e sborda, che una carta alta due voci.

**Verifica** su Xvfb con finestra 1280×820 dentro uno schermo 1600×1000 (il caso segnalato): la
tendina finisce **dentro** la finestra con il chevron di scorrimento sotto l'ultima voce visibile, e
scorrendo si arriva al blocco Appearance e a Refresh in fondo. **Noto e lasciato così**: un menu
aperto da un **dialogo** legge lo stesso tetto, quindi è limitato dalla finestra principale e non dal
dialogo — può sbordare da un dialogo piccolo, mai dallo schermo.

## M110 (2026-08-07, `769b6fdcd`) — un menu non si apre più sopra la propria barra

> Dall'utente: «la toolbar di "view" ha molte voci, quando la apro va a coprire la toolbar, fai in
> modo che questo non accada e si apra a partire da sotto la toolbar».

Il menu Vista ha una trentina di voci e **non ci sta** sotto la barra. La risposta predefinita del
posizionatore dei popup è **far scorrere in su** tutta la carta finché non ci sta: il menu si apriva
quindi **sopra la propria voce**, coprendo barra dei menu e toolbar — la voce a cui appartiene
finiva nascosta dietro l'elenco che aveva aperto.

Si tolgono le vie di fuga verticali e resta quella onesta: **niente FlipY, niente SlideY, ResizeY**.
La carta viene accorciata allo spazio disponibile sotto la barra e **scorre**, come fa qualunque
menu troppo lungo. Gli aggiustamenti orizzontali restano: un **sottomenu** si apre di lato e deve
poter ribaltarsi dall'altra parte del genitore quando finisce lo schermo.

Sta nelle styles **baseline**, non nel blocco moderno: un menu che copre la propria barra è un
difetto in entrambi gli stili.

**Verifica** su Xvfb: il menu Vista parte subito sotto la barra, scorre fino al blocco Appearance in
fondo, e il sottomenu Language si apre ancora a destra. **Non coperto, di proposito**: un flyout
aperto da uno split button della toolbar è ospitato dal flyout stesso e non dal template di
`MenuItem`, quindi questa style non lo raggiunge — al momento nessuno di quei flyout è abbastanza
alto da scorrere in su.

## M109 (2026-08-07, `7fcf2204e`) — pulsanti pieni e menu con la forma di VS Code

> Dall'utente: «1) ci sono ancora dei pulsanti con i bordi bianchi all'interno della finestra di
> commit; 2) allinea lo stile della toolbar e relativi menu a tendina (nello stile modern) con quello
> di questa immagine … gli spaziatori arrivano fino ai lati del menu, le selezioni sono arrotondate
> con del padding, il bordo della finestra sembra più spesso e quasi sfumato». Con screenshot di VS Code.

Entrambe le cose sono **solo Modern**: lo stile Classic tiene la cornice, che è ciò che deve essere.

### 1. I pulsanti azione del dialogo di commit
Portavano ancora il contorno a 3:1 che la tavolozza moderna assegna a un bottone **sopra una
toolbar**, dove riempimento e fondo sono lo stesso colore e il bordo è l'unica cosa che dice dov'è il
bottone. Su un dialogo non è così: il riempimento **già** differisce dal fondo, quindi il bordo era
cinque rettangoli pallidi in colonna e nient'altro. `BarButtonStyles` guadagna il ramo `actionbtn` —
niente bordo, riempimento alzato di un gradino a `App.PanelAlt`, `App.Hover`/`App.Pressed` sugli
stati — e il dialogo lo mette sulle **proprie** azioni (non sui bottoni piatti della striscia
stage/unstage, che sono un'altra cosa). Misurato: **1970** pixel di grigio-bordo in quella colonna
prima, **0** dopo.

### 2. I menu
Tre regole che dipendono l'una dall'altra:
- l'evidenziazione è una **pillola arrotondata rientrata** dal bordo del popup, non una fascia a
  tutta larghezza;
- i **separatori annullano** quel rientro e sono l'unica cosa che arriva ai due lati;
- il popup è una **carta arrotondata** con un'ombra morbida sotto.

La carta del menu a tendina di un `Menu` sta dentro `PART_Popup` del template di `MenuItem` e **non ha
nome**: si seleziona come «il Border di un MenuItem che *non* è `PART_LayoutRoot`», l'unico altro
bordo di quel template. L'ombra ha bisogno di spazio in cui disegnarsi — la superficie di un popup è
dimensionata sul contenuto e la taglierebbe — e il margine che glielo dà sposta la carta di 4px dal
suo ancoraggio, che è poi quello che fa anche il riferimento.

### Verifica
Su Xvfb in **modern scuro**, **modern chiaro** e **classic**: pillola e separatori a tutta larghezza
ci sono sia nelle tendine della barra dei menu sia nei flyout della toolbar, in entrambe le
tavolozze; il classic è **invariato** (voci quadrate a tutta larghezza, separatori rientrati, bottoni
incorniciati). **Non verificabile headless**: ombra e angoli arrotondati richiedono un compositor, che
Xvfb non ha — senza, semplicemente non compaiono (degradazione documentata su `PopupShadowRoom`).
Harness PASS, build `Avvisi: 0`.

## M108 (2026-08-07, `736a6ed6d` + `5c5d76c7c`) — allineamento con `upstream/master` e fix portati

> Dall'utente: «vedi cosa hanno fatto nei commit del master e sincronizzalo a quello nostro,
> assicurandoti poi di apportare i fix in maniera portabile al nostro codice».

### Il merge (`736a6ed6d`)
Undici commit di `upstream/master`, **zero conflitti**. Quello che arriva al port attraverso il core
condiviso che compiliamo (`src/app/GitCommands`):

- **`8cbe7c9f2`** cross-platform: `GetHomeDir()` ripiega su `UserProfile` invece che su `Personal`
  (che su Linux è `~/Documents`), il path della cache degli avatar perde il backslash incorporato e
  `meld` è cercato senza `.exe` fuori da Windows. **Il nostro `HomeDirectoryFix` resta**: semina
  `AppSettings.CustomHomeDir` col vero `$HOME` in un `ModuleInitializer`, cioè in un punto **prima**
  della catena rispetto al fallback (vedi M44).
- **`6f71c08fc`** `LocalRepositoryManager` non butta più i repository *anchored* quando taglia la
  cronologia recente — è la lista che legge la dashboard.
- **`e3206275a`** elimina `Commands.PushLocal`, il cui unico chiamante era il dialogo di reset.

Il resto è UI WinForms (`GitUI` non lo compiliamo), test o CI.

### I fix riportati a mano (`5c5d76c7c`)
1. **Nome del branch del worktree normalizzato** (da `6c302d839`). Il port non ha un campo "nuovo
   branch": senza ref, git chiama il branch come l'**ultimo segmento del path così com'è**, quindi un
   worktree in `~/lavoro/my feature` **falliva l'add**. Ora `WorktreeService` deriva il nome e lo
   passa dal normalizzatore `check-ref-format` del core (`-b my_feature`), raggiunto via `GitContext`.
2. **Dopo la creazione, offre di passarci** (stesso commit), da entrambi i punti d'ingresso: l'albero
   alza `OpenRepositoryRequested`, il dialogo espone `RepositoryToOpen` e si chiude — sotto una
   modale il repository non si può cambiare.
3. **"Reset another branch" avvisa se si perdono commit** (da `e3206275a`), con
   `git merge-base --is-ancestor <branch> <target>` fuori dal thread UI. Il port **non aveva alcun
   avviso**; si riusa la stringa di upstream con la sua chiave, così una build tradotta la dice nella
   lingua giusta.

**Non portabili**, messi a verbale perché il prossimo giro non li riapra: `a2154c42f` (il port già
rimette l'icona `Push` quando non è indietro), `5d6fd56e3` (nessun completamento del messaggio di
commit nel port), `c20bb8464` (scelta git Windows/WSL), `f8b5d7d7a` (DI WinForms), più CI e test.

### Un difetto trovato mentre si verificava
Un underscore in un `Header` di `MenuItem` è il **marcatore del tasto di accesso** di Avalonia: ogni
voce di menu che cita un ref mostrava `myfeature` per `my_feature`. Sbagliato da sempre, ma i nomi
normalizzati del punto 1 rendono l'underscore comune. `Theming/MenuText` lo raddoppia in **un** posto,
che è anche quello che ora usano le due `Replace("_", "__")` sparse in `MainToolbar`.

### Verifica
Su Xvfb, repo di prova in `/tmp/m108`: `my feature` crea il worktree sul branch `my_feature` (prima:
`fatal: invalid reference`) e il prompt di switch lo apre davvero; spostare `main` all'indietro mostra
l'avviso di perdita, spostare `side` in avanti (fast-forward) **no**; il menu contestuale scrive
`my_feature` per intero. Build `Avvisi: 0`, harness entrambi PASS.

## M107 (2026-08-07, `d5f4aca02`) — via le scatole dal dialogo di commit

> Dall'utente: «migliora la grafica della finestra di commit (ci sono troppi bordi bianchi)».

Il dialogo incorniciava tutto: un box `App.Border` da 1px attorno a ogni lista file e attorno al diff,
una **fascia da 4px** dello stesso colore dipinta sui tre splitter, e dodici bottoni Fluent di default
nelle due toolbar dei pannelli, ognuno col suo contorno. Contati sullo schermo: **14139 pixel** del
colore del bordo nel corpo del dialogo. La finestra principale, che non disegna **nessun**
`App.Border`, era lo standard che il dialogo non rispettava.

- I contorni dei pannelli passano da `StyleDensity.PaneOutline`: **niente** in Modern, **1px** in
  Classic — i pannelli incorniciati *sono* l'aspetto del 2015. Un pannello che perde il contorno
  prende `App.Panel`: resta una superficie su una finestra più scura invece di diventarci un buco.
- Le due liste file erano le uniche superfici del dialogo **fuori dalla tavolozza**: senza
  `Background` prendevano il `#2B2B2B` di Fluent. Ora nominano `App.Panel` come ogni altra lista.
- Gli splitter non dipingono più la fascia chiara (trasparente, così la presa riceve ancora il
  puntatore) — quelli di `MainWindow` non l'hanno mai disegnata.
- Le toolbar dei pannelli usano i bottoni piatti della toolbar principale, che escono da `MainToolbar`
  e diventano `Theming/BarButtonStyles`: **una** definizione invece di due. Guadagna il ramo
  `ToggleButton` con lo stato `:checked`, che serve ai toggle di raggruppamento — il bottone agganciato
  dice quale raggruppamento è attivo, e un riempimento piatto a riposo lo nasconderebbe.

Dopo: **1383** pixel di bordo (casella del filtro, riga della gutter del diff, contatore — tutti
separano qualcosa) e **0** pixel fuori tavolozza. Verificato su Xvfb in **entrambi** gli stili; la
toolbar principale è identica pixel per pixel dopo l'estrazione, a parte il contatore dei commit.

## M106 (2026-08-07, `4099dce58`) — tema di sistema e altre due famiglie di icone colorate

> Dall'utente, come «prossimi step»: «1) sincronizzare il tema a quello di sistema (sempre
> selezionabile dalle impostazioni); 2) il colore delle icone dei worktree deve essere verde, il
> colore dei submodules deve essere giallo per il quadratino in alto, verde per quei due in basso;
> 3) colora anche tutte le icone della barra di sotto (dove c'è commit, diff, file tree, gpg ecc.)».

### 1. Tema "System", e diventa il default
`UiState.Theme` ha un **terzo** valore. `App/Theming/SystemTheme.cs` lo risolve in chiaro/scuro
leggendo la preferenza del desktop e **continua a seguirla** mentre l'app è aperta: su Linux è la
chiave del portal XDG `org.freedesktop.appearance color-scheme` (quella che GNOME scrive quando
`org.gnome.desktop.interface color-scheme` è `prefer-dark`), su Windows e macOS l'API nativa — tutto
attraverso `IPlatformSettings` di Avalonia, così il port ha **una** strada e non tre.

Resta selezionabile come prima, e in due posti: Impostazioni ▸ Appearance ▸ Theme e Vista ▸
Appearance elencano **System / Dark / Light**. Un Dark o Light esplicito **disarma** l'inseguimento:
una risposta esplicita non deve muoversi da sola.

Perché non un quarto `ThemeVariant`: "System" non è una tavolozza. `ThemeManager` applica sempre e
solo una variante concreta; quello che la classe aggiunge è la **sottoscrizione**, che è stato che la
tavolozza non deve possedere.

**Il flash bianco all'avvio, e come è stato eliminato.** La risposta del portal arriva su DBus in modo
asincrono e, finché non arriva, Avalonia risponde col proprio default (Light) — la prima finestra è
già costruita da un pezzo. Quindi `UiState` registra anche **cosa preferiva il desktop all'uscita**
(`SystemThemeSeen`: un'osservazione, non un'impostazione, e non compare in nessuna UI) e la prima
finestra parte da lì. La risposta del portal è l'autorità; un **reconcile a un secondo** copre il caso
in cui quella risposta coincide col default di Avalonia e quindi `ColorValuesChanged` **non scatta
affatto** — che è l'unico modo in cui un seme stantio sopravviverebbe a una preferenza cambiata mentre
l'app era chiusa.

### 2. Il worktree è verde
Era ciano come i branch. È l'unico ref strutturale che è anche **una directory con un checkout suo**,
e il verde è ciò che lo distingue a colpo d'occhio dal branch su cui punta (che resta ciano).

### 3. Il submodule è bicolore
Tre quadrati di un solo colore erano tre scatole pari: nessuna contenenza. Ora **ambra il quadrato
padre**, **verde i due figli**, neutri i connettori. `Icons` guadagna una tabella `Parts` accanto ad
`Accents` — il path è spezzato come **dati**, cioè la concatenazione delle parti *è* il glifo — e
`GlyphSource` disegna parte per parte con **la stessa penna**, sotto lo stesso predicato che concede
un accento singolo. Quindi "Colora le icone" spento e le PNG classiche non cambiano di una virgola.

### 4. Tutta la barra inferiore colorata per contenuto
Nessuna coppia **adiacente** condivide la tinta, così la striscia si legge come una fila di cose
distinte: Commit verde, Diff blu, File tree ambra, GPG viola, Console ciano, Output blu, Blame ambra,
File history viola.

### Verifica
Su Xvfb: config vergine → parte scuro su desktop `prefer-dark` **senza flash**; `gsettings set
… color-scheme default` → l'intera finestra si ridipinge chiara **in posto** (log `[Theme] the desktop
switched to Light`) e torna scura al ripristino; il combo delle impostazioni previewa Light e
**Cancel** riporta a System **riarmando** l'inseguimento; le voci del menu Vista scrivono `Theme`
corretto in `ui-state.json`; icone verificate a crop (worktree verde, submodule ambra+verde+neutro,
otto tab tutte colorate) in **entrambe** le varianti. Harness: PASS entrambi. Build: `Avvisi: 0`,
`Errori: 0`.

## M105 (2026-08-07, `321e09a3f`) — build a zero warning

> Dall'utente, guardando l'output di `./run.sh`: «noto tutti questi warning, come mai» — 34 righe a
> ogni avvio, perché `dotnet run` **ricompila** e `-v q` non nasconde le diagnostiche del compilatore.
> Direzione: «fixa i warning, non zittirli, DEVI RISOLVERLI (passata vera) tutti».

Risolti tutti: app + i due harness compilano con **`Avvisi: 0`**. Trentadue delle trentaquattro righe
venivano da due cause **strutturali**, ognuna con un guasto vero dietro:

1. **`async void` e lambda `async` passate a `EventHandler`** (13). Un'eccezione che sfugge da lì viene
   alzata senza nessuno che la prenda e **il processo muore**: il dialogo di commit non deve poter
   uccidere l'app perché git ha risposto qualcosa di inatteso. Nuovo `App/Async.cs`: `Async.Run` avvia
   il lavoro da un contesto void, riporta l'eccezione e tiene l'app in piedi.
2. **`Task.Run(work).ContinueWith(t => … t.Result …)`** (12). Leggere `Result` **blocca** se il task
   non è finito e la continuazione **inghiotte** i fault: un errore di git diventava un dialogo che
   non si aggiornava mai. `Async.OffUi` fa `await`: stesso threading, nessuna lettura bloccante,
   fault riportati.

Gli altri, uno per uno (nessuno "sistemato" con un `NoWarn`):
- **`RemoteService`** non blocca più su `GetRemotesAsync` del core con un salto sul thread pool: legge
  `git remote -v` in modo **sincrono** (`GitExecutable.Execute`, come fa `GetRemoteNames`), che è ciò
  che i suoi ~12 chiamanti sincroni volevano. Il parse tiene il TAB come separatore e ancora la
  direzione a fine riga, così un URL con spazi non spezza la colonna.
- **`TranslationService`**: il join d'avvio non aspetta più il **task** di pre-load dal thread che sta
  per costruire la prima finestra (che è l'UI thread — bloccarlo su un task è come si deadlocka
  un'app, timeout o no). Ora pre-load e joiner prendono lo **stesso gate** e fanno lo stesso parse:
  se il pool ha finito il catalogo è già dentro, se è a metà il gate dura quanto il parse, se non è
  mai partito lo fa il joiner. Il timeout resta e continua a spedire inglese se sfora.
- **`MainWindow`**: `RefreshSubmoduleNavigationAsync` non riceve più il task dello snapshot ma lo
  chiede **alla cache** (stessa istanza, niente scoperto due volte; l'identità serve ancora al
  controllo di staleness, quindi resta in una **locale**), e l'osservatore del warm-up è una
  `ContinueWith` invece di un `await` su un task altrui.
- **`CleanupDialog`** tiene la domanda in sospeso come **la callback che la risponde**
  (`Action<bool>`), con il `TaskCompletionSource` **locale** al metodo che chiede.
- **`MainToolbar`**: il task dei repo-link già completato viene **materializzato** (valore o fault)
  invece di essere passato avanti; `LoadShellsAsync` → `LoadShells` (ritorna void);
  **`CommitDetailView`** osserva il task di `xdg-open`.
- **`CommitActionsService`** normalizza una volta il messaggio dello stash (CS8604).
- **`MainMenu.GitMaintenanceRequested` cancellato** (CS0067): non veniva **mai alzato**, quindi il
  `MaintenanceDialog` — un extra del port, non di upstream — era **irraggiungibile**. Tutti e cinque i
  suoi pulsanti esistono già come voci di menu (compress, recover lost objects, delete index.lock,
  edit .git/config), quindi il dialogo è stato **eliminato** invece di ricablato.

**Una modifica al sorgente condiviso** (`src/app/GitCommands/Git/Executable.cs`, compilato anche dalla
build Windows — è la seconda dopo le guardie `OperatingSystem.IsWindows()`): la copia di stderr resta
fire-and-forget, ma un fault ora arriva al **command log** invece di essere scartato in silenzio (un
task fallito e non osservato viene buttato via senza un rumore).

**L'unica esenzione, argomentata sul posto**: l'harness `NavigationSnapshot` tiene `VSTHRD003` in
`NoWarn` nel **proprio** csproj — tenere un task che il servizio ha consegnato e attenderlo dopo *è*
ciò che quell'harness verifica, e un harness console non ha synchronization context su cui deadlockare.

### Verifica
`Avvisi: 0 / Errori: 0` su tutti gli otto progetti (`-t:Rebuild`). Entrambi gli harness **PASS**. In
GUI su Xvfb, percorso per percorso su ciò che è stato toccato: tendine di branch e repository, dialogo
**Remotes** (due remote con le URL giuste, uguali a `git remote -v`), dialogo di commit con i menu
"Commit message" e "Commit templates", **stage e unstage** di un file vero (indice riportato allo stato
iniziale), e **entrambe** le risposte alla conferma di clean su un repo di prova in `/tmp` (Cancel
lascia i file, Delete li rimuove). Nessuna riga `[Async]` e nessuna eccezione in nessun run.
Falso allarme escluso con una **misura**: con `Language=Italian` i menu restano inglesi, ma lo erano
**anche prima** della modifica (verificato ricompilando con `TranslationService.cs` stashato) — è il
debito noto del layer di traduzione, non una regressione. Prossima libera: **M106**.

## M104 (2026-08-07, `56772bcc3`) — le ultime nove icone senza glifo

> Dall'utente, il log di `./run.sh`: quattro righe `[IconLoader] icon '…' has no vector glyph, drawing
> the 2015 PNG instead` (`GitForWindows`, `RepoStateClean`, `CommitTemplates`, `NavigateUp`).

Il log è la misura che `IconLoader.NoteRasterFallback` esiste per dare, e va letto per **nome**: quattro
nomi si erano dichiarati all'avvio, ma il conto vero è più alto — un nome si segnala solo quando la sua
superficie viene raggiunta. Un audit statico (nomi di asset PNG citati come stringa nel codice ∖ chiavi
di `Icons.Data`, scartando i falsi positivi che sono testo e non icone: `Date`, `Message`, `Save`,
`Appearance`) ne ha trovati **cinque in più**: i sei verdetti di firma del tab GPG (`CommitSignatureOk`
/`Warning`/`Error`, `TagOk`/`TagWarning`/`TagError`, di cui `TagMany` era già mappato) e `FunnelPencil`,
la casella di filtro parcheggiata nel menu di overflow della toolbar.

### Le scelte, tutte per significato
- **`GitForWindows`** (menu Tools ▸ Git bash) = `Terminal`. Il logo di Git-for-Windows è sbagliato su
  Linux e non si traduce in una penna sola; quel comando apre una **shell**.
- **`RepoStateClean`** = `Commit`. Il dialogo di commit chiede il nome dello *stato*, dove la toolbar
  chiede `"Commit"` e passa lo stato come nome **classico**; solo lo stato clean viene mai chiesto come
  nome, quindi solo quello è mappato — e in stile Classic carica ancora `RepoStateClean.png`, perché il
  nome viaggia col glifo.
- **`CommitTemplates`** = `FileLines`, pagina con righe: il template *è* un file su disco, e la piega
  d'angolo è ciò per cui la famiglia dei file si riconosce a 16 px (`Log`, che non ce l'ha, resta il
  registro dei comandi).
- **`NavigateUp`** = `ArrowUp`, la stessa freccia dei comandi di riordino: è lo stesso gesto, un passo
  su. Accento **ciano** come `SubmodulesManage`, con cui la toolbar lo alterna.
- **`FunnelPencil`** = `Filter`. È lo stesso comando di `EditFilter`, quindi lo stesso imbuto: la matita
  a 16 px è un secondo segno per niente.
- **I sei verdetti GPG** = uno **scudo** («è stato verificato») con dentro il segno del risultato:
  spunta, punto esclamativo, croce. Le stesse tre forme servono la riga del commit e quella del tag —
  il verdetto è ciò che l'icona porta, e *quale oggetto* sia lo dice già l'etichetta della riga. Una
  forma per verdetto batte sei forme quasi identiche a 16 px. Ruoli: verde afferma, ambra avverte,
  rosso è il verdetto negativo (lo stesso ruolo di `BisectBad`: non distrugge niente, ma la risposta è
  no).

### Un difetto trovato per strada
`MainToolbar.SetSubmoduleNavigation` passava al controllo vivo la `Source` di un'icona **appena
costruita** — la trappola di M77 al contrario: non un `Bitmap` sopra un glifo, ma un `GlyphSource` che
nessuno ha risolto e che non segue più il cambio di stile. Ora passa da `IconLoader.Retarget`, che
scambia la geometria in place e conserva tinta e sottoscrizioni.

### Verifica
Build `Errori: 0`. App avviata headless su `:151` con `XDG_CONFIG_HOME` isolato (stile Modern di
default): il log **non ha più nessuna riga `[IconLoader]`**, né di fallback né di parse fallito, né
all'avvio né dopo aver aperto il dialogo di commit (dove si vede il nuovo glifo di «Commit templates ▾»).
I glifi nuovi sono stati **rasterizzati offline** con inkscape a 16 e 64 px e guardati: a 16 px scudo +
spunta/`!`/croce restano distinguibili, e comunque le righe del GPG portano il testo del verdetto.
Metodo riusabile: per giudicare un glifo non serve la GUI, basta comporre l'SVG dai path di `Icons.cs`
con stroke 2 su griglia 24.

## M103 (2026-08-06) — le icone moderne hanno un colore, e si può spegnere

> «per quanto riguarda lo stile modern, le icone mi piacciono ma sono tutte bianche, preferirei che si
> aggiungano dei colori (sempre con la possibilità di scegliere dalle impostazioni in appearance se
> mantenere o meno il colore delle icone)».

Il set moderno era monocromatico **per costruzione**: linea disegnata con una sola penna, tinta
`App.Text`. È ciò che lo faceva leggere come una famiglia — ed è anche ciò che, in una toolbar da venti
icone, lo faceva leggere come un muro di segni grigi identici: a 16 px la forma è l'unica informazione
disponibile.

### Il colore è un ruolo, non un disegno
Sei ruoli, assegnati per **quello che il comando fa**, mai per l'aspetto del glifo — perciò Push, Pull,
Fetch e Clone condividono una tinta pur essendo quattro forme, e il cestino è rosso sia sotto «elimina
branch» sia sotto «elimina tag»:

| Ruolo | Chiave | Cosa copre |
|---|---|---|
| verde | `App.IconGreen` | creare, aggiungere, affermare (create repo/branch, checkout, commit, bisect good, file aggiunto) |
| rosso | `App.IconRed` | distruggere (cestino ovunque, ogni reset, file rimosso, bisect bad) |
| blu | `App.IconBlue` | parlare con un remote (push/pull/fetch/clone/remote) e i file modificati o rinominati |
| ambra | `App.IconAmber` | contenitori e marcatori (cartelle, tag, stash, preferiti) e gli stati «guardami» |
| viola | `App.IconPurple` | l'indice e le riscritture (stage/unstage — upstream le disegna viola — merge, rebase, bisect) |
| ciano | `App.IconCyan` | i ref strutturali: branch locali, submodule, worktree |

Un'icona **senza ruolo resta `App.Text`**: la chrome (impostazioni, navigazione, pannelli, ricerca,
copia) non deve competere con le icone che significano qualcosa.

### Cosa il colore non fa mai
La tinta chiesta dal call site **vince** quando è essa stessa informazione: `App.TextDim` per un glifo
attenuato, `App.Accent` su una superficie accentata e la famiglia `App.RepoState*` con cui il pulsante
Commit *dice* lo stato del repository non vengono mai ridipinte. Solo la tinta di default `App.Text` è
disponibile.

E il colore non porta mai da solo un significato: ogni coppia che condivide una tinta differisce di
forma, e i due ruoli che un daltonico collassa — verde e rosso — sono il più contro il meno, il check
contro il cestino. Spegnere tutto non perde nulla.

### Contrasto
Le sei tinte sono registrate in **tutte e quattro** le palette (trappola M62: una chiave non registrata
si tiene il colore che l'altra palette ha lasciato nel brush). Un marcatore non testuale deve 3:1;
ognuna di queste supera **4.5:1** sul peggiore fra `App.Window` / `App.Panel` / `App.PanelAlt`, e verde
e rosso sono separati anche in luminanza (6.77:1 contro 4.63:1).

### L'interruttore
`Appearance ▸ "Colour the icons"` (`UiState.ColoredIcons`, default acceso). Anteprima **dal vivo** come
tema e stile, con revert su Cancel: `ThemeManager.SetColoredIcons` alza `StyleChanged`, che ogni glifo a
schermo già ascolta — le icone si ridipingono sul posto, nessuna vista viene ricostruita. Lo stile
classico non è toccato: disegna i PNG del 2015, coi colori dentro il bitmap.

Fuori scopo ma nato qui: **Commit & push** portava `ArrowUp`, il glifo di chrome che i pulsanti di
riordino usano; ora porta `Push`, la forma con cui la toolbar spinge davvero — ed è anche ciò che gli
fa guadagnare la tinta di trasferimento.

Verificato in GUI su `/tmp/m97/super`: albero e toolbar coi ruoli attesi (campionati a pixel:
push `#5B9CFF`, stage/unstage `#B197E1`, stash `#E0A73C`, reset `#E06C6C`), spegnimento dal vivo che
riporta tutto a `App.Text`, Cancel che ripristina, e il tema chiaro con le tinte scurite.

## M102 (2026-08-06) — il chevron dell'albero ha una fascia sua

> Segnalazione dell'utente con immagine annotata: «a volte non si capisce se facciamo doppio clic
> sulla freccia o sulla scritta, vorrei separare le zone»; le due linee rosse delimitano una **colonna**
> attorno al chevron, alta quanto la riga.

Prima il discrimine era `IsExpandToggle(e.Source)`, cioè *aver colpito il `ToggleButton`*: un bersaglio
di 12 px. Mancarlo non era neutro — al secondo clic partiva l'**attivazione della riga**, che per un
submodule o un worktree cambia repository e per un branch fa checkout.

Ora il chevron possiede la fetta orizzontale di riga in cui sta (la sua larghezza + 3 px di gioco), a
qualunque altezza: dentro la fascia il press **piega solo il nodo** e non tocca la selezione, come il
riquadro +/- di un tree control; fuori è etichetta, quindi seleziona e al secondo press attiva. Un nodo
senza figli non ha chevron e quindi non ha fascia: tutta la riga è etichetta.

Da NON riscoprire: il chevron dell'item si cerca **senza scendere nei `TreeViewItem` annidati** — i loro
chevron stanno nello stesso albero visuale e una discesa ingenua restituisce quello di una riga figlia.

## M101 (2026-08-06) — le ultime differenze del dialogo di commit: raggruppamento, overflow, submodule

> «implementa tutte le parti rimanenti della finestra del commit che hai trovato»: le quattro voci
> lasciate dichiarate in M98–M100. Base `6f7f23f22`.

### Raggruppamento delle liste (la voce grossa)
La toolbar del pannello aveva 3 pulsanti dove upstream ne ha 6, e i tre mancanti sono il
**raggruppamento** attorno a cui `FileStatusList` è costruita (`btnByPath` / `btnByExtension` /
`btnByStatus`, sopra `btnAsTree` e `btnCollapseGroups`). Il port usava le stesse tre chiavi come
**ordinamento**: la stessa informazione senza le intestazioni che rendono leggibile una lista lunga.

Ora le liste costruiscono nodi di gruppo: **albero vero di cartelle** per il raggruppamento per path
(le righe file portano solo il nome, la cartella sta nell'intestazione sopra), un'intestazione per
chiave per estensione e stato, ognuna col conteggio e il chevron, che si chiude al clic e sopravvive a
un refresh perché la chiave è il path della cartella. Cliccare il toggle attivo **spegne** il
raggruppamento, come fa il pulsante checkable di upstream. I nodi di gruppo non entrano mai nella
selezione da cui lavorano stage/unstage/diff (`SelectedRows` filtra per tipo).

Dettaglio non ovvio: l'indentazione e il "solo nome" **non stanno in `WorkingDirFileRow`** — è il tipo
del servizio, non sa nulla di liste — ma in una `ConditionalWeakTable` riempita da `BuildItems`, così
niente resta vivo dopo un reload.

### Le altre tre
- **Overflow**: `OverflowPanel` era una classe annidata privata di `MainToolbar`; estratta in
  `Views/OverflowPanel.cs`, la toolbar del messaggio adesso parcheggia dietro «»» ciò che non entra
  invece di andare a capo.
- **Icona del branch** nella status bar, davanti al nome (upstream `toolStripStatusBranchIcon`).
- **"Generate list of changes in submodules"** nel menu del messaggio: compone
  `Submodules … updated` dai bump staged, leggendo il log di ciascun submodule fra i due
  `Subproject commit`; con nessun submodule staged lo dice nella status line invece di non fare nulla
  (upstream esce in silenzio). Trappola nota riapplicata: `--pretty=format:` con **`%x20`**, mai spazi
  letterali, perché `GitArgumentBuilder` concatena tutto in una sola command line.

Misura estetica: il glifo di `CollapseAll` erano due chevron che si piegano, che a 16 px **si legge
come una ✕** (= chiudi); ora è il meno in un riquadro dei tree control.

## M100 (2026-08-06) — confronto 1:1 col dialogo originale: la struttura dentro i pannelli

> Terza passata: «controlla bene questa immagine e fai il confronto preciso 1:1 di ciò che cambia
> rispetto a quello che abbiamo in avalonia, quindi implementa le differenze di struttura».
> Base `63ca3261e`, screenshot `~/Pictures/Schermate/Schermata del 2026-08-06 14-59-02.png`.

Dopo M98 la divisione generale era giusta: le differenze rimaste erano **dentro** i pannelli.

| Originale | Port prima | Ora |
|---|---|---|
| toolbar di icone e **sotto** il filtro a tutta larghezza | tutto su una riga, filtro ridotto a un moncone | due righe |
| filtro con **▾** (è una `ToolStripComboBox`) | nessuna cronologia | ▾ con i pattern già usati nel pannello |
| pannello vuoto = solo la riga in corsivo, **filtro nascosto** | filtro sempre visibile, testo centrato | `SetFileStatusListVisibility`: filtro nascosto, testo in alto a sinistra |
| strip unica: coppia unstage a sinistra, coppia stage a destra, "all" **solo icona** | quattro pulsanti incorniciati che andavano a capo | una riga, piatta, "all" con la didascalia nel tooltip |
| path con **cartella in grigio** e nome file pieno | path monocromo | due toni |
| pulsanti con icona **ancorata a sinistra** (`ImageAlign = MiddleLeft`) e testo centrato | icona e testo centrati insieme | come upstream; Amend a sinistra |
| `toolbarCommit` piatta | pulsanti incorniciati | piatti |

I quattro glifi stage/unstage erano **due forme per quattro pulsanti** (`ListPlus`/`ListMinus`
ripetuti): ora sono freccia singola e doppia, verso una linea e via da una linea — la direzione che
upstream disegna con le sue frecce viola. Regola: **a 16 px due varianti della stessa forma non si
distinguono**, serve un asse diverso (qui il verso, e il raddoppio per "all").

Restano dichiarate, non fatte: la toolbar del pannello ha 3 pulsanti invece dei 6 di upstream (gli
altri tre sono i **raggruppamenti** per path/estensione/stato, che il dialogo di commit del port non
ha perché le sue liste sono piatte); la riga della toolbar del messaggio **va a capo** su finestra
stretta invece di finire in un overflow; la riga "Committer" del port sta a sinistra nella status bar.

## M99 (2026-08-06) — le icone del dialogo di commit

> Seconda passata sulla stessa segnalazione: «ci sono ancora differenze nel commit dialog (ad esempio
> le icone che mancano)». Base `8ff9c4fe5`.

Ogni pulsante di `FormCommit` ha una `Image` nel designer (`:331-799`: `Stage`, `Unstage`, `StageAll`,
`UnstageAll`, `RepoStateClean`, `ArrowUp`, `stash`, `ResetWorkingDirChanges`, `WorkingDirChanges`,
`CommitTemplates`, `BranchCreate`) e le righe dei file mostrano l'icona di stato; il port disegnava
tutto a testo. Ora passano da `IconText.Header` con i **nomi asset di upstream**, i due pulsanti "all"
scambiano l'icona con la variante `…Filtered` quando un filtro è attivo (upstream fa lo stesso: è
l'unico segnale che "all" significa "i match"), e le righe disegnano l'icona di stato con la lettera
colorata come fallback.

Le liste vuote dicono **"There are no unstaged/staged changes"** in corsivo — `FileStatusList.NoFiles`.

### Il set moderno era spaiato
`Unstage` e `FileStatusModified` avevano un glifo vettoriale, i loro compagni no: nella stessa striscia
un vettore stava accanto a una bitmap del 2015. Aggiunti `ListPlus` (`Stage`/`StageAll`) e i quattro
stati mancanti (`FileStatusAdded`/`Removed`/`Renamed`/`Copied`/`Unknown`), disegnati dalle forme già
presenti. Regola generale: **upstream distingue gli stati per COLORE** (più verde, meno rosso), il set
moderno è monocromatico, quindi il segno sulla pagina deve portare il significato.

## M98 (2026-08-06) — il dialogo di commit allineato a `FormCommit`

> Segnalazione dell'utente con screenshot dell'originale Windows: «nel commit dialog ci sono ancora
> delle differenze rispetto all'originale, allinealo». Base `8c37814a9`.

### La struttura era la differenza vera
Upstream annida tre `SplitContainer` (`FormCommit.Designer.cs:31-52`): `splitMain` mette **le due
liste di file a sinistra** e tutto il resto a destra; `splitRight` impila il **diff** sopra un
`tableLayoutPanel1` la cui prima colonna è un flow **dall'alto in basso** dei pulsanti di commit, con
la `toolbarCommit` sopra il box del messaggio nella seconda. Il port aveva invece una **fascia a
tutta larghezza** sotto entrambe le colonne, messaggio sopra e pulsanti a capo sotto: le liste
perdevano altezza e niente si allineava.

Ora: colonna sinistra a tutta altezza col suo splitter, strip stage/unstage in testa al pannello
staged (upstream `toolbarStaged`), banner dei conflitti e regione di commit **dentro** la colonna
destra, pulsanti in colonna verticale (`MinWidth` 171 come `flowCommitButtons`). Tolti i due titoli
in grassetto sopra le liste e il pulsante **Cancel**: upstream non ha né gli uni né l'altro (il suo
`Cancel` è un pulsante coperto dalla lista, e Escape chiude già il dialogo).

### Le righe dei file
Stampavano lo stato **a parole** davanti al path (`new  docs/x.md`) dentro il padding di default di
Fluent: alte il doppio di una riga upstream, otto file riempivano un pannello dove upstream ne mostra
venti. Ora portano la **lettera colorata** che `FileStatusListView` già usa (A/M/D/R/C/U) e le sue
metriche di riga. In più il **primo file è selezionato** alla prima riempitura, così il pannello del
diff non resta bianco finché non si clicca.

### "Commit message ▾"
La `toolbarCommit` upstream ha quattro voci, il port ne aveva tre. Aggiunta la prima:
`commitMessageToolStripMenuItem`, l'elenco dei messaggi degli ultimi commit (etichetta = prima riga
tagliata a 72 caratteri, clic = sostituisce il messaggio) più il filtro **"Show only my messages"**
sull'identità del committer. Legge `GitModule.GetPreviousCommitMessages`, già linkato.

Da NON riscoprire: un `DockPanel` serve i figli **nell'ordine in cui sono aggiunti** — con il gruppo
di sinistra aggiunto per primo si prendeva tutta la larghezza e `Options` restava una scheggia sul
bordo; va aggiunto **prima** il figlio ancorato a destra.

Non portato (dichiarato, non taciuto): la voce **"Generate list of changes in submodules"** del
menu del messaggio, che compone un messaggio dai bump di submodule presenti nell'indice.

## M97 (2026-08-06) — una sola riga selezionata nell'albero, e lo stash torna una finestra

> Due segnalazioni dell'utente con screenshot: (a) «switcho submodules e clicco sulle cartelle,
> queste rimangono buggate selezionate»; (b) «la funzione di stash è nel menu in basso mentre nel
> programma originale è in una schermata a parte, controlla». Base `74a347d6c`.

### (a) Le righe restavano selezionate perché il modello di selezione non le vedeva mai

`TreeViewItem.IsSelected` arriva al selection model del `TreeView` **solo dopo che il container è
stato realizzato sotto il suo parent**. Il port assegnava `IsSelected = true` a mano in tre punti —
il click (tunneling in `OnTreePointerPressed`), il ciclo dei match della ricerca e il ripristino
della selezione dopo un rebuild — e nel sottoalbero appena aperto di un submodule quel flag veniva
scritto **dietro le spalle del modello**. La selezione successiva deselezionava solo il nodo che il
modello conosceva: tutti gli altri restavano col fondo blu. Da qui la catena
`pluma_orchestrator › ai-server › core › graphs` tutta accesa dello screenshot.

Ora ogni selezione passa da `SelectOnly(node)`, che azzera il flag su tutti gli altri nodi indicizzati
prima di accenderlo sul suo (l'iterazione lavora su uno snapshot delle chiavi: assegnare `IsSelected`
alza `SelectionChanged`, e un handler che ricostruisse l'albero invaliderebbe il dizionario).

**Misura** su un repo `super › lib1 › lib2` costruito apposta (`/tmp/m97`), quattro click sulle righe
di un submodule espanso, banda blu letta a x=125: **prima** una banda continua 413–540 px (quattro
righe accese insieme), **dopo** una banda di 32 px alla volta, cioè una riga sola.

### (b) Lo stash non è un tab: upstream è una finestra

`FormBrowse` upstream ha quattro tab in designer (Commit · Diff · File tree · GPG) più quelli che
aggiunge a runtime (Console · Output · Blame · File history): **lo stash non è fra questi**. Tutte le
sue superfici — lo split button `toolStripSplitStash`, il menu Commands, e nell'albero
`mnubtnOpenStash` / `mnubtnManageStashFromRootNode` — chiamano `UICommands.StartStashDialog`, cioè un
`FormStash` **modale**. Il port aveva messo lo stesso pannello in un nono tab, che schiacciava lista +
liste file + diff nella striscia in basso.

Fatto: nuovo `App/Views/StashWindow.cs` (una `ZoomWindow` attorno a `StashPanel`, Esc chiude via
`DialogKeys`), tab Stash rimosso dalla striscia, e un unico ingresso `MainWindow.ShowStashDialogAsync`
per tutte le superfici. I due argomenti di `StartStashDialog` esistono davvero adesso:
`StashPanel.ManageStashes` (prima riempitura sullo stash più recente invece che sulla riga della
working directory, poi il flag si spegne come upstream) e `StashPanel.SelectStashOnLoad("stash@{2}")`,
così `Open stash` e il doppio clic su un nodo stash aprono **su quello stash**. `RepoObjectsTree`
espone `StashDialogRequested(string?)` al posto di `BottomTabRequested`, che non aveva altri utenti.

Verificato in GUI su un repo con quattro stash: la striscia in basso non ha più Stash; il corpo dello
split button apre la finestra su `stash@{0}`; il doppio clic su `stash@{2}` la apre su `stash@{2}` col
diff giusto; «Create a stash…» apre la finestra sulla riga della working directory con il prompt sopra;
alla chiusura dopo uno stash creato la finestra principale si aggiorna (Stash (3) → Stash (4), riga
Working directory sparita).

Da NON riscoprire: un `BottomTab` "Stash" salvato da una versione precedente non corrisponde più a
nulla e ricade sul tab Commit — `RestoreBottomTab` lo gestisce già col suo `_ =>` di default.

## M96 (2026-08-05) — densità della chrome, e SOLO nello stile Modern

> Richiesta dell'utente: applicare le raccomandazioni date sulle sei scelte del punto 2
> della coda di modernizzazione, con un vincolo esplicito: «queste modifiche di stile devono
> applicarsi solo quando viene selezionato lo stile "Modern" … quindi NON nello stile classic».
> Base `bf1a86903`. Le scelte applicate: densità invariata ma allineata alla griglia base-4,
> riga della griglia 22, icone 16 con una costante unica, valore fisso (non una preferenza
> utente), raggio 4 su pulsanti e input, ambito finestra principale + dialoghi frequenti.

### Il vincolo cambia la forma del lavoro, non solo i numeri
Il piano ovvio — «sostituire i 126 `FontSize` e le 671 `Thickness` letterali con i token di
`Metrics`» — **non produce nulla di dipendente dallo stile**: un valore scritto sul call-site è
un *local value* e batte qualunque `Style`, quindi una view che scrive
`Padding = Metrics.Density.ButtonPadding` ha solo spostato il letterale. La forma corretta è
l'opposto: la proprietà va **togliata dal call-site** e assegnata dal blocco Modern, che
`ModernStyles` installa e rimuove in blocco — ed è quel meccanismo, non una seconda tabella di
numeri classici, a rendere la densità Modern-only. Il "classico" è ciò che danno i `ControlTheme`
di Fluent, cioè esattamente l'aspetto che l'app aveva prima che il blocco esistesse.

### Cosa è entrato
| dove | Modern | Classic |
|---|---|---|
| `Button`/`ToggleButton` padding | 12,4 | Fluent (11,5,11,6) |
| `TextBox`/`ComboBox` padding | 8,4 | Fluent |
| altezza minima dei controlli | 28 | Fluent (32) |
| raggio pulsanti e input | 4 | Fluent (0 / 4) |
| padding header dei tab | 12,4 | 12,6 (baseline, entrambi) |
| riga della griglia revisioni | 22 | 20 |
| pulsanti di barra dell'app | 4,4 / 8,4 | 4,2 / 8,3 |
| dimensione icone | 16, **stessa in entrambi** — costante unica | 16 |

`ControlMinHeight` a 28 e non a 32: 32 è un bersaglio da dito, questa è un'app da mouse, e 28 è
il multiplo di 4 più grande che tiene una riga di pulsanti sotto quella di upstream. Raggio 4 e non
6 sui pulsanti: a 28px di altezza un angolo da 6 legge come pastiglia, e i pulsanti dell'app stanno
spalla a spalla in barra, dove 6px aprono un cuneo visibile di fondo fra vicini. Ora tutta la
chrome ha **un solo angolo**.

### Due cose che nessuno `Style` può raggiungere
1. **La riga della griglia**: la griglia disegna le proprie righe, l'altezza sta su `MinHeight` del
   `Grid` di riga. `RowMinHeight` la prende dallo stile corrente, e il cambio di stile passa da
   `RebindRows(preserveViewport: true)` — l'utente ha cambiato aspetto, non ha chiesto un altro
   *insieme* di righe, e perdere lo scroll per quello è il caso che `RebindRows` documenta come
   inaccettabile. Il rebind è `Post`-ato: `StyleChanged` viene alzato **dentro** l'installazione del
   blocco di stile.
2. **I pulsanti di barra**: costruiti da helper che assegnano `Padding` come local value. Nuovo
   `Theming/StyleDensity.cs`, due valori per stile. `MainToolbar` ricostruisce la striscia su
   `StyleChanged` (come già fa per la lingua), gli altri 5 call-site prendono il valore alla
   costruzione successiva — limite dichiarato, non un difetto da pagare con un hook per view.

### Verifica
Modern/Classic misurati a schermo, e il **cambio live in entrambe le direzioni** guidato con
clic sintetici attraverso la pagina Appearance: passo delle righe **22 → 20 → 22**, nessun
rebind fallito, nessuna eccezione nel log. Classic invariato alla cifra (`#333337` sulla fascia,
`#252526`/`#2D2D30` sotto, righe da 20).

### Quello che NON è stato fatto, e perché
- **I 42 `, 16)` passati a `IconLoader.Image`** erano il valore di default del parametro stesso:
  la definizione di "quanto è grande un'icona" era scritta 43 volte. Ora il default legge
  `Metrics.Density.IconSize` e l'argomento è sparito dai call-site (resta esplicito l'unico caso
  diverso, il logo da 48 dell'About).
- **La "coda" dei letterali di testo era in gran parte un falso allarme.** Ricontati: 83 `FontSize = 12`
  (= il baseline dell'app, ridondanti ma corretti), 21 × 11 (caption, sulla scala), 6 × 13
  (subtitle, sulla scala), e i **10 `FontSize = 10` non sono testo**: sono i chevron `▾` e il
  marker `▶`, cioè icone disegnate come carattere. Non esiste testo a 10px nell'app, e la frase
  «quattro dimensioni in una banda di 3px, che è rumore» in testa a `Metrics` va letta con questa
  correzione. Rinominare i letterali con i token resta un refactor a **zero pixel** e a zero
  dipendenza dallo stile: non è ciò che rende la UI più moderna.
- **Le ~100 `Thickness` fuori griglia rimaste** (`0,0,6,0`, `0,10,0,0`, `6`, `10`) sono margini di
  pannelli specifici, non token di densità: cambiarle muoverebbe **anche Classic**, che è ciò che
  questa milestone ha il divieto di fare, e nessuno `Style` può possedere il margine di un
  `StackPanel` arbitrario. Restano fuori per costruzione, non per stanchezza.

## M95 (2026-08-05) — la chrome moderna è piatta: la toolbar non è più una fascia di un altro colore

> Richiesta dell'utente (screenshot della fascia superiore): «Di default la toolbar è di colore
> diverso rispetto al resto nella modalità dark, fixa». Base `1b9a53aa0`.

**Misura prima del fix**, campionando lo screenshot dell'utente: la barra dei menu era **a due
tonalità** — `#1C1D21` (App.Panel) fino a x≈748, `#2F3038` (App.Toolbar) da lì a destra — e la
striscia della toolbar sotto era `#2F3038` per tutta la larghezza, contro `#1C1D21` di ogni
pannello del contenuto. Due difetti, una causa: **App.Toolbar era un gradino a sé** nella rampa.
Il taglio verticale nella barra dei menu è il controllo `Menu` che dipinge il proprio fondo
(`MenuFlyoutItemBackground` = panel dal M93) sopra il contenitore `MainMenu`, che dipingeva
App.Toolbar: dove il `Menu` finisce, riappare il colore del contenitore.

**Fix**: nelle sole famiglie **Modern**, `App.Toolbar` = `App.Panel` (`#1C1D21` dark, `#FDFDFD`
light). Un solo valore, e ogni barra dell'app si appiattisce insieme — non solo menu e toolbar
principale, ma anche i 15 call-site che leggono `App.Toolbar` (RevisionGridView, DiffView,
BlameView, FileTreeView, RepoObjectsTree, CommitDetailView, FileHistoryView, ConsoleView,
CommitDialog). Correggere solo `MainMenu`/`MainToolbar` avrebbe reso *quelle* barre le nuove
diverse. Precedente esatto già nel file: `App.Control` (le superfici di input) **è** `App.Panel`
dal M77, e gli input si vedono per il contorno, non per il fondo — dal M94 quel contorno misura
3:1 (`App.BorderStrong`), quindi la condizione era già soddisfatta prima di appiattire.
La separazione fra chrome e contenuto resta la **regola da 1px** già presente in fondo a
`MainToolbar` (`BorderThickness = 0,0,0,1`).

Nessun numero di contrasto del M67/M70/M77/M94 decade: `App.Toolbar` era la superficie **più
chiara** del tema dark e la peggiore per ogni inchiostro, quindi togliendola i minimi **salgono**
— `App.TextDim` 4.70 → 5.75:1, `App.Border` 1.23 → 1.58:1. In light idem (`App.TextDim`
4.67 → 5.29:1). L'unica conseguenza da registrare è che `App.PanelAlt` (`#26272D`) ora è la
superficie più chiara della rampa: le strisce alternate della griglia sono più chiare delle barre,
non più il contrario.

**Classic resta intatta di proposito**: `#333337` sotto la barra dei menu è la firma del 2015, e
lo stile classico esiste per riprodurla. Verificato a schermo su tre configurazioni con Xvfb —
Modern Dark `#1C1D21` uniforme su menu + toolbar + barra filtri + tab strip, Modern Light
`#FDFDFD` uniforme, Classic Dark ancora `#333337` sulla fascia e `#252526`/`#2D2D30` sotto.

## M94 (2026-08-05) — icone complete, link con il loro inchiostro, contorni che misurano, focus che non si taglia

> Richiesta dell'utente: chiudere gli step 4, 3, 5, 7 e 6 della coda di modernizzazione. Quattro
> subagent in worktree isolati su file disgiunti + il loop. Base `64e2522f9`.

### Step 4 — copertura icone: da 18 nomi sul PNG a **uno**
`Icons.cs` guadagna 23 glifi nuovi. Le forme non sono riempitivi: `CleanupRepo` è una **scopa**
(`git clean` spazza gli untracked, non distrugge i tracked, quindi non il cestino), `DeleteIndexLock` è
un **lucchetto aperto** (rilascia il repo), `CompressGitDatabase` è la scatola dell'object store
schiacciata (volutamente diversa dalla scopa), `RemoteDelete` è la **nuvola dei remote sbarrata** e non
un cestino (scollega, non cancella sul server), i quattro `Bisect*` si distinguono **fra loro** perché
l'utente li vede in fila. `plugin` è minuscolo: il dizionario è `Ordinal` e la chiave maiuscola
sarebbe caduta sul PNG in silenzio.
Verificato sull'app: il log `[IconLoader] icon '…' has no vector glyph` passa da **18 nomi a 1**, e
l'unico rimasto è **`GitForWindows`**, lasciato di proposito — è un marchio di prodotto, e nessuna
line art 24x24 dice quello che dice il logo.
Ogni path nuovo è stato **ricamminato aritmeticamente** (M/L/h/v/H/V/a/q/z, archi e quadratiche
campionate): tutti dentro il box 0..24 su entrambi gli assi, i più estesi `Wrench` (x 3..22, y 2..21) e
`Compress` (y 2,5..21,5), quindi con tratto 2 nessuno tocca il clip.

**Difetto trovato dalla verifica a schermo, non dal codice**: il pulsante di ricerca dell'albero
chiedeva l'icona di upstream chiamata **`Preview`**, il cui PNG del 2015 era *per caso* una lente.
Appena quel nome ha avuto un glifo vero, il pulsante è diventato un **occhio**. Il call site ora chiede
`Search`, e la lente — che aveva il `const` ma **nessuna chiave** nel dizionario — è registrata.

### Step 3 — `App.Link` ha finalmente dei call site
Erano **zero**: tutti i link leggevano `App.Accent`, che è tarato come riempimento. Ora 10 call site
(`CommitDetailView` x5, `HelpImagePanel`, `ResolveConflictsDialog`, `DashboardView` x3). Il criterio è
*cliccabile*, non *blu*: restano su `App.Accent` i bordi delle pill, gli `@@` degli hunk (enfasi, non
link), i badge ahead/behind, il caret del terminale, i riempimenti dei banner.
**La colonna Commit ID è stata verificata e lasciata**: `AddCell` imposta solo il foreground, non c'è
handler di click né hit-test — è codice colorato, non un link.

| superficie | Classic dark | Classic light | Modern dark | Modern light |
|---|---|---|---|---|
| App.Window | 3,70 → **6,30** | 4,06 → **5,32** | 4,96 → **6,65** | 6,05 → **6,41** |
| App.Panel | 3,40 → **5,79** | 4,51 → **5,90** | 4,58 → **6,13** | 6,59 → **6,98** |
| App.PanelAlt | 3,04 → **5,19** | 3,82 → **4,99** | 4,05 → **5,42** | 5,64 → **5,97** |
| App.Toolbar | 2,79 → **4,75** | 3,55 → **4,64** | 3,57 → **4,78** | 5,19 → **5,50** |

`App.Accent` mancava il 4,5:1 in **9 delle 16** combinazioni; `App.Link` le passa tutte.

### Step 5 — un contorno che può delimitare un controllo da solo
`App.Border` misura **1,08:1** (Modern dark) e 1,32 (light) contro le superfici su cui un controllo
sta: va bene come separatore, non come contorno (WCAG 1.4.11 chiede 3:1).
Fatto su tre livelli, perché ognuno può out-shoutare il precedente:
1. **chiavi Fluent** in `ModernStyles`: bordo a riposo su `borderStrong` (0,45 → 3,30:1 / 3,32:1) e
   hover su un nuovo `borderHover` (0,65 → 4,96:1 / 5,52:1). 0,45 è il **pavimento**: a 0,40 sono
   2,92 / 2,97;
2. **chiave di palette `App.BorderStrong`** (nuova, tutte e quattro le famiglie) perché ~39 call site
   disegnano la propria chrome e non possono raggiungere un brush interno a quel file;
3. **`TextBoxSurface`**, che pinna il bordo nelle `Resources` dell'**istanza** — cioè esattamente ciò
   che batte le chiavi Fluent — e il cui default passa a `App.BorderStrong`.

Nelle famiglie **Classic** `App.BorderStrong` **è** `App.Border`: il classico è definito come
l'aspetto di prima, e un contorno nitido attorno a ogni input sarebbe un aspetto nuovo. Verificato a
schermo: Classic dark mostra ancora `#3F3F46` su `#252526`.
Convertiti i contorni di controllo in 11 file (filtri e caselle di ricerca di albero, dashboard,
toolbar e sua copia in overflow; griglia delle patch; input di encoding/find; picker di blame; il box
del messaggio di merge, che la checkbox rende editabile; la lista dei conflitti; il pulsante di aiuto).
**Restano su `App.Border`**, con ragione: filetti da 1px, `GridSplitter`, separatori dentro gli split
button, cornici di raggruppamento (`HeaderedContentControl`, i `Border` dei radio group), pannelli
informativi non interattivi, superfici console opache e **tutti** gli stati `*Disabled` — 1.4.11
esclude i componenti inattivi, e un controllo spento con un contorno nitido si legge come un controllo
attivo disegnato male.

### Step 7 — il `TabItem` non può più lampeggiare
Stessa causa di M93: `Brushes.Transparent` è `#00FFFFFF`. Tre proprietà ce l'avevano, e una è stata
scoperta leggendo — il selettore `OfType<TabItem>().Template().OfType<Border>()` prende **anche**
`PART_SelectedBar`, quindi la barra d'accento lampeggiava bianca a ogni cambio di tab. Ogni riposo è
ora il colore d'arrivo ad alpha 0 (helper `Faded()`, che segue la palette).
Misurato sulla luminanza relativa, Modern dark (`#141518` → `#252629`): la vecchia rampa arrivava a
**0,0885** contro un estremo massimo di 0,0194 — **4,6x** — la nuova sale monotona da 0,0086 a 0,0177 e
non esce mai dall'intervallo. A schermo: `#141518` → `#252629` senza picchi.

### Step 6 — pressed e focus, fotografati per la prima volta
- **pressed**: pulsante di toolbar `#2F3038` → hover `#41424A` → **`#53545B`**; un `Button` normale
  fa hover `#41424A` e pressed `#53545B`. Coincidono alla cifra con `surfaceHover`/`surfacePressed` di
  `ModernStyles`, che è la conferma che le chiavi nuove di M93 hanno la stessa derivazione del resto.
- **focus**: l'anello si vede — 2px `#3B82F6` (3,57:1 sulla toolbar) con l'alone `#E4E4E7` da 1px.
- **Difetto trovato qui, e corretto**: l'anello aveva un **margine negativo** per stare *fuori* dal
  bordo del controllo, e la parte esterna era **tagliata** da chi impacchetta il controllo. Misurato:
  un pulsante di toolbar focalizzato mostrava l'anello a sinistra e a destra e **niente sopra e sotto**,
  perché la barra è alta esattamente quanto il pulsante. *Un indicatore di focus che dipende dallo
  spazio libero del contenitore non è un indicatore.* Disegnato dentro i limiti è intero su tutti i
  controlli provati (pulsante icona di toolbar, tab del pannello inferiore, riga della griglia).

### Da NON riscoprire
- **Tre livelli possono sovrascriversi**: chiave Fluent < valore locale sul call site < risorsa pinnata
  sull'istanza. Alzare solo il primo non basta e **non si vede** — è quello che è successo con i
  `TextBox` di `TextBoxSurface` e con i 39 call site.
- Un nome di icona di upstream può essere **semanticamente sbagliato** per il suo call site e non
  accorgersene finché il PNG casualmente giusto viene sostituito da un glifo (`Preview` → lente →
  occhio).
- Il watchdog dei 600 s ha ucciso **cinque volte** i quattro subagent, sempre per analisi lunga prima
  del primo commit. Ciò che ha funzionato: dire *un file finito = un commit immediato*, dare un tetto
  di due minuti per file con l'obbligo di dichiarare "non deciso", e vietare di avviare Xvfb (la
  verifica GUI resta nel loop). Due volte il lavoro utile era **non committato** nel worktree: si
  recupera con `git -C <worktree> diff` e `git apply`, senza rilanciare nulla.

## M93 (2026-08-04) — la riga sotto il puntatore si vede, e il flash bianco della toolbar sparisce

> Due segnalazioni dell'utente con screenshot: *«il colore della riga su cui sono con il cursore è
> uguale a quello delle righe scure normalmente presenti, preferirei un colore diverso, tipo
> celestino»* e *«nel tema scuro, quando scorro sui pulsanti della toolbar e/o sui menu a tendina, fa
> per un istante un hover del tasto completamente bianco, poi scompare la selezione di hover»*.

### 1. L'hover della griglia era il colore della zebra
`RevisionRowView.Sync` dipingeva l'hover con **`App.PanelAlt`**, che è *esattamente* il fondo delle
righe dispari: sulle righe dispari l'hover non cambiava nulla, sulle pari sembrava la zebra.
Tre chiavi nuove in tutte e quattro le famiglie (34 → 37 chiavi): `App.HoverRow`, `App.Hover`,
`App.Pressed`.
`App.HoverRow` è **l'unico fondo di riga con una tinta** — `App.Panel` tirato verso `#38BDF8` — quindi
nessuna zebra può somigliargli. Misure (contrasto WCAG con le due inchiostrazioni che la riga porta e
col marker verde dei ref):

| famiglia | HoverRow | Text | TextDim | marker | vs Panel | vs PanelAlt |
|---|---|---|---|---|---|---|
| Modern dark | `#20333F` (14%) | 10,30 | **4,68** | 8,30 | 1,29 | 1,14 |
| Modern light | `#D2EFFC` (22%) | 14,30 | **5,03** | 4,46 | 1,18 | 1,01 |
| Classic dark | `#27343B` (10%) | 9,33 | **4,61** | 8,13 | 1,20 | 1,07 |
| Classic light | `#D3F0FD` (22%) | 14,01 | **4,55** | 4,50 | 1,19 | 1,01 |

Il vincolo che ha fissato le percentuali è `App.TextDim` (≥ 4,5:1), non il testo pieno; il marker
verde resta ai valori che aveva già sulla zebra (4,51 → 4,46 chiaro, 9,45 → 8,30 scuro).

### 2. Il flash bianco: `Brushes.Transparent` è bianco
`Brushes.Transparent` **non** è "niente": è `#00FFFFFF`, cioè **bianco** con alpha 0. La superficie
moderna fa il cross-fade del `Background` del `ContentPresenter`
(`ModernStyles.PresenterTransitions`), quindi ogni hover interpolava *da bianco trasparente* al
riempimento di hover, passando per **bianco semi-opaco**. Misurato sul pulsante `Commit info` in
Modern dark: riposo `#2F3038` → **picco `#78787D` in 40 ms** → discesa al valore finale. Identico
sulle voci dei menu a tendina, dove `MenuFlyoutItemBackground` era anch'esso `Brushes.Transparent`.

Correzioni:
- riposo dei `toolbtn` = **il colore di hover ad alpha 0** (`Fade(hover)`), così il fade è una pura
  rampa di opacità e nessun terzo colore compare mai. Partendo invece dal colore *della barra* ad
  alpha 0, il tema chiaro scendeva a `#BEBEC3` prima di risalire — misurato, e scartato;
- i `toolbtn` **escono dal cross-fade** (`Transitions` vuote nello stile locale, che vince perché è
  dichiarato sul controllo e non sull'`Application`): la transizione era ciò che rendeva visibile
  l'artefatto, e una barra di pulsanti piccoli sotto un puntatore che si muove legge meglio se lo
  stato scatta;
- `MenuFlyoutItemBackground` / `…Disabled` = `panel`, che è esattamente ciò con cui è dipinto il
  presenter del flyout: identico a riposo, senza bianco da attraversare.

### 3. L'hover della toolbar era più scuro della toolbar
`hover = App.PanelAlt`, `pressed = App.Panel`: **entrambi più scuri** di `App.Toolbar`, quindi "sotto
il puntatore" si leggeva come un buco. Ora `App.Hover` / `App.Pressed` — la barra tirata al 10% e al
20% verso l'inchiostro, la stessa regola che `ModernStyles` usa per ogni altro controllo, e infatti in
Modern dark il valore coincide alla cifra con `surfaceHover` (`#41424A`).

### Verifica (Xvfb `:219`, campionamento dei pixel ogni 15–20 ms durante l'hover)
| combinazione | riga sotto il puntatore | pulsante di toolbar |
|---|---|---|
| Modern dark | `#26272D` → `#20333F` | `#2F3038` → `#41424A`, **un solo passo** |
| Modern light | `#EBEBEF` → `#D2EFFC` | `#E2E2E8` → `#CECED4`, un solo passo |
| Classic dark | `#252526` → `#27343B` | `#333337` → `#444448` |
| Classic light | `#FFFFFF` → `#D3F0FD` | `#E4E4E4` → `#D0D0D0` |

Voce di menu a tendina, Modern dark: `#1C1D21` → `#2A2B2F` **monotona**, nessun picco chiaro (prima
il picco era a `#78787D`).

### Da NON riscoprire
`Brushes.Transparent` è bianco trasparente: come **valore di partenza di un'animazione** introduce un
flash chiaro su qualunque fondo scuro. Se una proprietà animata parte da "invisibile", il valore
giusto è *il colore di arrivo ad alpha 0*, non `Transparent`. Restano con `Brushes.Transparent` a
riposo il `TabItem` (`ModernStyles`, fondo dietro non garantito) e le righe della griglia (il cui
presenter non è animato): il primo è l'unico punto ancora esposto allo stesso artefatto.

## M92 (2026-08-03) — i pannelli di Diff e Stash seguono la larghezza a cui li trascini

> Segnalazione dell'utente con due screenshot: *«sia in diff che in stash, se ridimensiono le sezioni
> di sotto, queste non si stickano alla larghezza del contenitore»*.

**Causa**: in `DiffView` e `StashPanel` la larghezza iniziale stava sul **figlio** dentro colonne
`Auto` — `_files.Width = 320`, `listPanel.Width = 340`, `_filesGrid.Width = 320`. Il `GridSplitter`
ridimensiona la **colonna**: il figlio con `Width` propria restava alla sua misura e fra il suo bordo
destro e lo splitter si apriva una **striscia morta**. Le colonne erano `Auto`, quindi la colonna
seguiva lo splitter mentre il contenuto no.

**Fix**: le larghezze passano alle `ColumnDefinition` (`320px`/`340px`), con `MinWidth = 120` così un
pannello non si può trascinare a zero. Nessuna `Width` sui figli, che ora si stirano (default
`Stretch`). `FileTreeView` faceva già così (`ColumnDefinitions("300,Auto,*")`) e non aveva il difetto:
è la conferma che la differenza è quella e non altro.

### Misure (Xvfb `:218`, scansione dei pixel su una riga del pannello)
| | prima del drag | dopo il drag |
|---|---|---|
| Stash, colonna dei file | `x 901–1220` (320 px), splitter a `1221` | `x 901–1301` (401 px), splitter a `1302` |
| Diff, lista dei file | 320 px | ~512 px, contigua allo splitter |

Zero pixel fra il bordo del contenuto e lo splitter in tutti i casi — la striscia morta degli
screenshot non c'è più. Cercati altri `GridSplitter` con lo stesso schema: solo questi due (gli altri
undici usano colonne in pixel o in star).

## M91 (2026-08-03) — i submodule dei submodule, e il doppio clic che li apre

> Richiesta dell'utente con screenshot dell'originale a confronto: *«nella versione originale si
> possono vedere anche i submodules dei submodules, a noi no, inoltre vorrei che quando faccio
> doppio click sui submodules mi porta appunto sui submodules che ho selezionato»*. Fatto in diretta,
> senza subagent: due file, `App/Services/SubmoduleService.cs` e `App/Views/RepoObjectsTree.cs`.

### Cos'era rotto
`ListSubmodules` chiamava `GetSubmodulesLocalPaths(recursive: **false**)` e `git submodule status`
senza `--recursive`: la categoria era una lista **piatta** dei soli submodule del repo di primo
livello. Un submodule di un submodule non c'era, e non c'era neanche un modo di arrivarci.

### Le tre parti
1. **Elenco ricorsivo** — `recursive: true` + `git submodule status --recursive`. Entrambi riportano
   il path **completo dal repo di primo livello** (`pluma_orchestrator/core/graphs/tasks`), quindi le
   chiavi delle due fonti combaciano senza normalizzazioni.
2. **Gerarchia nell'albero** (`AddSubmodulesWithFolders`) — ogni riga si appende al nodo del proprio
   super-project, e un segmento di path che è **solo una directory** (`core`, `graphs`) diventa un
   nodo cartella intermedio. È la forma di `SubmoduleTree.AddTopAndNodesToTree` di upstream, che
   crea un `SubmoduleFolderNode` per ogni parte del path che non è essa stessa un submodule.
   Etichetta = **nome + branch** (`tasks (no branch)`), come `SubmoduleNode.DisplayText`; il path
   completo e lo sha sono passati al **tooltip**, perché in una catena di quattro livelli lo sha su
   ogni riga era solo rumore.
3. **Doppio clic** — `OnActivate` apre il submodule come repository attivo, la stessa rotta della
   voce «Open» del suo menu. Un submodule **non inizializzato** è escluso: la sua directory è vuota,
   aprirla sposterebbe la finestra su un non-repo.

### Il difetto che il nesting porta con sé, corretto nello stesso giro
`git submodule update -- <path>` accetta **solo** un submodule del repository in cui gira. Con i
nidificati visibili, l'«Update» del menu li avrebbe passati al repo di primo livello e fallito.
Provato in chiaro sulla fixture:
```
$ git -C top          submodule update --init -- pluma_orchestrator/core/graphs/tasks   → exit 1
  error: pathspec '...' did not match any file known to git
$ git -C top/pluma_orchestrator submodule update --init -- core/graphs/tasks            → exit 0
```
Quindi `SubmoduleRow` porta ora `ParentPath` (il repo che **dichiara** il submodule) e
`PathInParent`, e `Update`/`UpdateMerge` hanno un overload che prende la riga e gira nel posto
giusto.

### Il branch senza un processo per submodule
Il branch mostrato **non** viene da un `git` per submodule (una catena profonda ne pagherebbe uno per
livello, sopra ai due che il servizio già lancia): si legge il file `HEAD`. Il `.git` di un submodule
è normalmente un **file** `gitdir: ../../.git/modules/<nome>`, quindi si risolve quello e si legge
`ref: refs/heads/<branch>`; niente ref = detached, che l'albero mostra come `(no branch)` esattamente
come `SubmoduleNode.BranchText`.

### Verifica GUI (Xvfb `:217`, fixture `/tmp/nsub` a tre livelli reali)
- `Submodules (3)` con dentro `pluma-UI (develop)` e `pluma_orchestrator (develop)`, e sotto
  quest'ultimo la catena `core` → `graphs` → `tasks (no branch)`: **la stessa forma dello screenshot
  dell'originale**.
- Doppio clic su `tasks` → titolo finestra `tasks - Git Extensions`, path bar
  `/tmp/nsub/top/pluma_orchestrator/core/graphs/tasks`, statusline `(detached HEAD)`, 1 commit.
- Primo tentativo **sbagliato e corretto misurando**: appendevo al nodo del super-project
  (`Host(row.ParentPath)`), e `core`/`graphs` **spariva**no — `tasks` compariva direttamente sotto
  `pluma_orchestrator`. L'host giusto è la **dirname del path completo**.

## ROUND 13 — GUI moderna — iterazione 1: M79 (2026-08-02)

> Direzione dell'utente: *«mantenere la struttura e le funzioni attuali, rendendo però più moderna la
> gui»*. Quindi **nessuna** voce di menu, view, flusso o comportamento cambia: si tocca solo il
> livello di superficie. Deciso esplicitamente anche che **non** si affianca una variante "Classic":
> l'aspetto si sostituisce.
>
> Base `a38eb4ab4`. Tre subagent Claude in worktree isolati, file **disgiunti**, nessuno dentro
> `App/Views/`. Build `Errori: 0` dopo ogni cherry-pick.

### La diagnosi, misurata prima di toccare qualcosa
| | stato al `a38eb4ab4` |
|---|---|
| Icone | PNG raster 16px di Git Extensions 2015, multicolore, contorno nero, densità disomogenee |
| Palette | VS Code 2015: `#1E1E1E`/`#252526`/`#2D2D30`/`#333337`, accento `#007ACC`, e in chiaro `App.Panel` **`#FFFFFF`** puro |
| Angoli | **6** `CornerRadius` in tutto il codice |
| Tipografia | nessuna scala: `FontSize = 12` in **81** punti, `11` in 20, `10` in 10, `13` in 6 |
| Spaziature | `16`/`12`/`8`/`4` mescolati a `6` e `10`, fuori da qualunque scala |
| Stati e moto | **zero** file con `Transitions`/`Animation`; hover, focus e pressed sono i default Fluent |

### U1 — icone monocromatiche vettoriali (`Theming/Icons.cs`, `Theming/IconLoader.cs`)
**90 glifi** line-art su griglia 24×24, tratto 2, trascritti da Lucide (ISC, licenza e provenienza per
glifo in testa al file). Tinti dalle **istanze** dei brush di palette (`App.Text`/`App.TextDim`/
`App.Accent`), quindi seguono il cambio tema; `GlyphIcon` si sottoscrive al `PropertyChanged` del
brush perché nulla invaliderebbe il controllo su una proprietà che non è sua.
**L'API di `IconLoader` non cambia**: nessuna delle 54 call site è stata toccata dall'unità, e i nomi
senza glifo cadono sul PNG loggando una riga per nome — la copertura si misura dallo stdout di un run.
`GlyphIcon` deriva da `Image` **perché** due call site fanno pattern match su `Image` e altre
riassegnano `.Source`.
Lasciati raster **con motivo**: i marchi di terzi (`github`, `BitBucket`, `VisualStudio*`, `putty`,
`Gitk`…), che un ridisegno monocromatico travisa; e la famiglia `FileStatus*`, che col colore codifica
*anche* da quale lato del diff viene un file — informazione che una tinta sola non porta.

### U2 — token di stile e stati (`Theming/Metrics.cs`, `Theming/ModernStyles.cs`, `App.cs`)
`Space` 4/8/12/16/24 (con `6` e `10` documentati come da ritirare), `Text` a **5 livelli**
(Caption 11 / Body 12 / Subtitle 13 / Title 16 / Display 20) con costanti di peso e colore perché la
gerarchia venga da quelli prima che dalla dimensione, `Radius` 4/6/10, `Motion` 120/140/160 ms.
**Le view non sono convertite**: questo giro costruisce solo il vocabolario.
Gli stati **non** sono ottenuti gridando più forte di Fluent ma **ridefinendo le sue chiavi risorsa**
in `Application.Resources` — la tecnica di `TextBoxSurface`/`ManagedFileChooserTheming`. Le chiavi
sono state lette dalla dll Fluent 11.3.14 compilata (`strings -el`, sono UTF-16). Famiglie coperte:
`Button*`, `ToggleButton*`, `TextControl*`, `ComboBox*`/`ComboBoxItem*`, `MenuFlyoutItem*`,
`TabItemHeader*`, `ControlCornerRadius`/`OverlayCornerRadius`, `SystemControlFocusVisual*`.
**Nessun esadecimale negli stati**: `Derived()` si sottoscrive ai brush di palette e ri-mescola, quindi
la rampa nuova è ereditata e il cambio tema a caldo continua a funzionare.
Il calcolo dei contrasti ha trovato **tre fallimenti veri prima dello schermo**: bordo hover a 2,98:1,
bordo pressed accentato a 2,05:1 (tolto — il riempimento porta già il pressed), e il bordo interno del
focus ring su un pulsante in hover a 2,72:1 (risolto con un alone `App.Text` da 1px dentro i 2px di
accento, così misurano entrambi i lati). Peggior testo **5,65:1** scuro / **5,08:1** chiaro su 28
combinazioni di stato; peggior non-testo 3,30 / 3,32:1.
**Revision grid esclusa per costruzione**, verificato leggendo: ogni selettore di transizione è
`OfType<X>().Template()`, e il sottoalbero di una riga (`ListBoxItem` → `RevisionRowView`) non contiene
**nessun** controllo templato.

### U3 — rampa di neutri e `App.Link` (`Theming/ThemeManager.cs`)
Scuro `#141518`/`#1C1D21`/`#26272D`/`#2F3038`, bordo `#3C3E47`; chiaro `#F3F3F6`/`#FDFDFD`/`#EBEBEF`/
`#E2E2E8`, bordo `#C2C2CB` — **via il bianco puro**. Ogni superficie porta una leggera dominante
fredda (canale blu 3–9 sopra il rosso).
**La prima stesura è stata scartata dalla misura**: i valori indicativi del brief (window `#17181B`,
bordo `#2E3037`) davano separazioni fra superfici adiacenti di 1,066/1,075/1,084 e un bordo a 1,26 sul
pannello — le superfici collassavano in una massa piatta. La rampa finale tiene le separazioni **al di
sopra** delle vecchie (1,084/1,131/1,135) e i bordi da 1,71 a 1,23.
Ri-derivate **tutte** le famiglie di inchiostro, perché cambiare la rampa invalida ogni numero di
M67/M70: le due tinte di diff sono state **ricomposte** (alpha `0x28` sulla superficie nuova →
`#213127`/`#342325` invece di `#2A392C`/`#3C2A2A`) e i 5 token verificati contro **quelle**, non
contro `App.Window`. `App.TextDim` passa da 4,39 (falliva AA sulle tinte di diff) a 4,70:1;
`App.DiffRemoved` da 4,20 a 4,62:1 senza muoversi, solo perché la finestra è più scura.
Sei accenti RepoState ricalcolati sulla toolbar nuova: i due che non passavano AA in scuro
(`Clean` 3,64 · `UntrackedOnly` 2,87) sono ora **5,19** e **5,24**.
Aggiunta **`App.Link`** (`#5B9CFF` / `#1A4FC4`), il residuo aperto da M74: 6,13:1 sul pannello scuro
contro i 3,70:1 di `App.Accent`.

### Correzioni del loop in integrazione (file hub, non dei subagent)
1. **Cinque icone di toolbar tornavano raster.** Commit-info, shell, azione di pull predefinita,
   direzione di push e stato del repo cambiano con lo stato e lo facevano assegnando un `Bitmap`
   direttamente a `Image.Source`: funziona, ed è il problema — il bitmap vince sul glifo, quindi al
   primo refresh quelle cinque tornavano al PNG 2015 mentre tutte le altre restavano vettoriali. U1
   l'aveva **segnalato senza correggerlo**, come da regola. Nuovo `IconLoader.Retarget`, che ri-punta
   un'icona già costruita scambiando la geometria in place e conservando la tinta risolta. Il pulsante
   Commit abbandona le **sette** bitmap per-stato di upstream a favore di **un** glifo tinto con la
   chiave di stato — che è già quello che fa la sua caption, e a differenza di sette bitmap cotte
   sopravvive al cambio tema.
2. **La riga selezionata della griglia è scesa sotto AA.** `RevisionGridView` dipinge la selezione con
   `App.Accent` pieno e ci mette sopra testo bianco: con l'accento nuovo il soggetto misurava
   **3,68:1** (col vecchio `#007ACC` faceva 4,51). È un uso **derivato dentro una view**, fuori da
   `ThemeManager`, quindi la campagna di misure di U3 non poteva vederlo. Nessun blu serve entrambi i
   ruoli — chi porta testo bianco a 4,5:1 scende sotto 4,5:1 come inchiostro su pannello, e viceversa
   (`#3B82F6`: 4,58 inchiostro / 3,68 fondo; `#2563EB`: 3,26 / 5,17). Stessa separazione che la
   palette fa già per `App.Link`: nuova chiave **`App.AccentFill`** = `#215BDD` scuro / `#1D4ED8`
   chiaro, il valore più chiaro che fa passare tutti e tre gli inchiostri che una riga selezionata può
   portare (bianco 5,82 · `#DFECFA` 4,86 · marker `#9CF0B8` 4,32). **Misurato a schermo dopo: 6,85:1.**

### Verificato in GUI dal loop (screenshot guardati davvero, colori misurati con PIL+WCAG)
Toolbar, tab strip, albero sinistro e griglia in **entrambi i temi**; ref pill anche su **riga
selezionata**; tab File tree con i cinque token di sintassi; hover su un pulsante di toolbar
(`#26272D` contro toolbar `#2F3038`, stato visibile e preso dalla palette). Copertura icone: **26**
nomi cadono ancora sul PNG in un run tipico, tutti fra quelli lasciati raster di proposito più una
coda di nomi minori.

### Aperto, con motivo
- Le **view non usano ancora `Metrics`**: 81 `FontSize = 12` e le `Thickness` a `6`/`10` sono ancora
  letterali. È il lavoro successivo, ed è la ragione per cui il vocabolario esiste già.
- Le call site dei link usano ancora `App.Accent`, non `App.Link`. Con l'accento nuovo il difetto di
  M74 è **rientrato da solo** (4,58:1 contro i 3,70 di prima), quindi non è più urgente, ma la chiave
  giusta è `App.Link`.
- `Unstage` e i 26 nomi in coda non hanno glifo.
- `App.Border` misura **1,23:1** scuro / 1,37 chiaro contro `App.Toolbar` (segnalato da U2): non è una
  violazione per come è usato oggi, ma è troppo debole per reggere da solo il contorno di un controllo.
- Nessuna verifica su schermo di **pressed** e **focus** (calcolati, non fotografati).

### Trappola di metodo nuova, da NON riscoprire
**Il watchdog dell'harness uccide un subagent dopo 600 s senza progresso, e in questa iterazione ha
preso tutti e tre.** Ciò che li bloccava era sempre la **verifica GUI** (avvio Xvfb, attese, mini-WM).
Due erano stati uccisi *prima di committare*. Rimedi che hanno funzionato: riprenderli con
`SendMessage` (il worktree è intatto e il transcript pure — **non** rilanciarli da zero), imporre
*un file scritto = un commit*, e **spostare la verifica GUI nel loop**, che è comunque dove il metodo
la mette. Corollario: a un subagent conviene chiedere misure **calcolate offline**, non misurate a
schermo.

## Coda round 13 — PRIORITÀ UTENTE del 31/07/2026: create branch e checkout dall'albero — **13.2 e 13.3 CHIUSE (M75), 13.1 APERTA**

> Tre difetti segnalati dall'utente usando la GUI del port sul suo repo. **Hanno precedenza su tutto
> il resto** (le "idee di valore" e i residui del round 12 in `HANDOFF.md` §4 restano dietro).
> Prossima milestone libera: **M79** (M75–M78 usate; vedi la riga HEAD in `HANDOFF.md` per la rinumerazione del merge).
>
> **Causa comune a 13.2 e 13.3, accertata leggendo il codice**: nel port le mutazioni di ref girano
> dentro wrapper **fire-and-forget e muti**. `RepoObjectsTree.RunMutation` (`:2410-2440`) fa
> `Task.Run(work)` e, in caso di **fallimento, non fa assolutamente nulla** — nessun messaggio,
> nessun output, nessun refresh: l'utente vede una GUI immobile e non sa se git è stato lanciato.
> `MainWindow.RunOp` (`:3326-3372`) è appena meno cieco: scrive una riga nella status bar e, sul
> fallimento, *"— see the panel output"*. Upstream invece esegue **entrambe** le operazioni dentro
> `FormProcess`: creazione branch a `FormCreateBranch.cs:163`, checkout a
> `FormCheckoutBranch.cs:357` (`StartCommandLineProcessDialog`). Il port ha già la superficie
> giusta — `GitProcessDialog.RunStreamingAsync` (`:334-349`, console + `Keep dialog open` + `Abort`),
> usata per push/merge/commit — quindi qui manca **solo l'instradamento**, non l'infrastruttura.
>
> **Call-site censiti (da cablare tutti, non solo quello dell'albero):**
>
> | Operazione | Call-site | Wrapper oggi |
> |---|---|---|
> | Create branch | `Views/RepoObjectsTree.cs:2022` | `RunMutation` (muto) |
> | Create branch | `Views/BranchTagPanel.cs:230` | `RunMutation` (muto) |
> | Create branch | `MainWindow.cs:1827` (grid, "Create branch here…") | `RunOp` (status bar) |
> | Create branch | `MainWindow.cs:3456` (menu Commands) | `RunOp` (status bar) |
> | Create branch | `Views/CommitDialog.cs:3070` | `_actions.CreateBranch` diretto |
> | Checkout | `Views/RepoObjectsTree.cs:1868` (doppio clic + menu) | `RunMutation` (muto) |
> | Checkout | `Views/BranchTagPanel.cs:198` | `RunMutation` (muto) |
> | Checkout | `Views/RevisionGridView.cs:6000` | `RunRefOp` |
> | Checkout | `MainWindow.cs:1807` (dropdown branch della toolbar) | `RunOp` (status bar) |
> | Checkout `-B`/remoto | `Views/RepoObjectsTree.cs:1912` | `RunMutation` (muto) |

- [~] **13.1 — `Create branch…` dal menu contestuale di un branch (albero sinistro) è INERTE al
      primo clic: serve riaprire il menu e cliccare una seconda volta.** ⚠️ **M75: NON RIPRODOTTO,
      due ipotesi su tre FALSIFICATE, causa residua a certezza MEDIA.** Vedi il verdetto in fondo
      alla voce. Percorso: `RepoObjectsTree.cs:1253` (branch locale) e `:1301`
      (branch remoto) → `DoCreateBranchAsync` (`:2004-2028`) → `CreateBranchDialog.AskAsync`
      (`CheckoutBranchDialog.cs:412-436`) → `ShowDialog(owner)`.
      **Ipotesi in ordine di probabilità, tutte falsificabili con una sonda di log:**
      1. **Il modale viene mostrato mentre il popup del `ContextMenu` è ancora aperto.** `ShowDialog`
         parte dentro `MenuItem.Click` (`MenuItem()` `:1527-1538`), quindi la finestra nasce mentre
         la popup X11 (override-redirect, con grab del puntatore) non è ancora smontata; il WM può
         non mapparla/attivarla, e il secondo tentativo — a popup chiuso — funziona. Combacia col
         sintomo *"la seconda volta va"* ed è coerente con quanto già registrato in `HANDOFF.md` §3
         (`ShowDialog` che non mappa senza WM). Rimedio standard: rimandare l'apertura con
         `Dispatcher.UIThread.Post(..., DispatcherPriority.Background)` dopo la chiusura del menu.
      2. **Un `Refresh()` dell'albero smonta il nodo che possiede il menu** mentre è aperto: i
         `ContextMenu` sono ricreati per nodo a ogni ricostruzione (`:701`, `:734`, `:919`), quindi
         il click sull'item può non essere mai consegnato. Il codice già si difende da un effetto
         collaterale del tasto destro (`OnTreePointerPressed` `:1720-1729`, che sopprime la notifica
         di selezione per un solo tick a `Background`): verificare se il refresh arriva **dopo** quel
         tick.
      3. **La guardia `_busy`** (`:2008`) o `_repoPath` vuoto fanno uscire in silenzio; `_busy` è
         condiviso da tutte le mutazioni dell'albero e viene azzerato solo da un `Post` sull'UI
         thread (`:2432`).
      Fix atteso: il **primo** clic apre il dialogo. Se la causa è la (1), lo stesso rimedio va
      applicato a **tutti** gli item del menu dell'albero che aprono un modale (Merge, Rebase,
      Create tag, Rename, Delete…), non solo a Create branch. Aggiungere log diagnostico
      *definitivo* è preferibile a una correzione a tentoni: qui una guardia che esce in silenzio e
      una finestra che non mappa hanno lo **stesso** sintomo.

      **VERDETTO M75 (2026-08-01).** Premessa cambiata: l'utente ha chiarito di aver osservato il
      difetto **su Windows**, non su Linux → **l'ipotesi (1) è fuori questione** (non c'è nessun
      grab X11 su Win32). Percorso strumentato con una sonda su file e app guidata sul desktop con
      input sintetico vero (`user32!SetCursorPos` + `mouse_event`, elementi localizzati via UI
      Automation) su un repo di prova. **Il sintomo non si è riprodotto.** Risultati:
      - **Ipotesi (2) FALSIFICATA**: nessun `Refresh()`/rebuild parte dalla selezione o dal
        pointer-pressed. `OnSelectionChanged` (`:1707`) emette solo `RefSelected`; l'unico
        sottoscrittore è `MainWindow.OnRevisionSelected` (`MainWindow.cs:1869`), che non tocca
        l'albero; l'unico `_tree.LoadRepository` è in `RefreshAll` (`MainWindow.cs:2865`).
        Forzando il file watcher a menu aperto, il `BuildTree` è arrivato ~700 ms **dopo**
        l'apertura del dialogo senza impedirla.
      - **Ipotesi "target letto dalla selezione" FALSIFICATA per costruzione**: `BranchMenu`
        (`:1231`) cattura `row.Name` nella closure a `:1253`/`:1301`, e `DoCreateBranchAsync`
        (`:2004`) non legge mai `_tree.SelectedItem`. Log su un nodo **mai selezionato**: target
        corretto e dialogo aperto al **primo** click.
      - Scartate anche: owner nullo (`owner=MainWindow isVisible=True isActive=True`), eccezione
        silenziata (il `catch` di `:2024` non è mai stato colpito), `async void` (è `async Task`).
      - **Causa residua: (3), la guardia `_busy`** — l'unico `return` muto rimasto sul percorso.
        **Certezza MEDIA, per esclusione**: non si è riusciti a fabbricare una finestra `_busy=true`
        abbastanza lunga su un repo piccolo.
      Corretti comunque i **due difetti reali e dimostrati** del flag (commit `3fafaa1f9`), che
      producono esattamente il sintomo riferito: (a) nei wrapper `Run*` `_busy` era azzerato **solo
      dopo** il ritorno di `work()` via `Post` finale → un git bloccato (credenziali, lock, rete)
      lo lasciava `true` **per sempre** e da lì **ogni** voce del menu falliva in silenzio; ora c'è
      un `finally` **dentro** il `Task.Run`. (b) le guardie mute di `DoCheckoutAsync`,
      `DoMergeAsync`, `DoRebaseAsync`, `DoCreateBranchAsync`, `DoCreateTagAsync` e dei 5 wrapper ora
      notificano il rifiuto (`NotifyBusy`/`NotifyBusyAsync`, `:2776`, con `_busyNoticeOpen` per non
      impilare modali). `Refresh()` mantiene di proposito il bail-out muto: è chiamato
      programmaticamente, non da un'azione utente. **Nessun fix a tentoni per il sintomo "primo
      clic"**: non è dimostrato. **Sonda diagnostica conservata** nel branch locale
      `diag/13.1-probe` (`16bfc40c7`, `App/DiagProbe.cs`): se il sintomo si ripresenta, riapplicarla
      e leggere `%TEMP%\ge_13_1_probe.log` — la comparsa di `DoCreateBranchAsync GUARD-EXIT` rende
      certa l'ipotesi (3) in un colpo solo. **Voce da RIVERIFICARE con l'utente**, non chiusa.
- [x] **13.2 — la creazione di un branch non mostra il process dialog.** Confermato nel codice:
      `RepoObjectsTree.cs:2022` → `RunMutation` → `BranchTagService.CreateBranch` (`:488-501`),
      che esegue `Commands.Branch(name, objectId, checkout)` e restituisce un `BranchTagResult`
      **buttato via** quando fallisce. Upstream mostra `FormProcess` con la riga di comando e
      l'output (`FormCreateBranch.cs:163`), e con `Checkout after create` un secondo processo se il
      branch è orphan (`:167`). Fix: instradare su `GitProcessDialog.RunStreamingAsync` come già
      fatto per commit/merge/push, su **tutti** i call-site della tabella sopra, così anche un nome
      rifiutato da `check-ref-format` o un branch già esistente diventa **visibile** invece di
      sparire. Nota: `CreateBranch` risolve lo start point con `module.RevParse` **prima** di git —
      quel fallimento non ha output git, va riportato come messaggio.
      ✅ **M75**: fatto su **tutti e 5** i call-site di creazione. `BranchTagService` ha ora
      `CreateBranchStreaming` (stessa riga di comando dei gemelli, orphan+clear incluso come
      upstream `:167`) che passa da `GitStreamRunner`; l'instradamento è incapsulato in
      `App/Views/RefProcessRunner.cs`. Il fallimento che **non arriva mai a git** (`rev-parse` a
      vuoto, nome vuoto) viene scritto nella console del dialogo come `error: unknown revision 'x'`,
      come richiesto dalla nota.
- [x] **13.3 — il doppio clic su un branch nell'albero deve fare il checkout mostrando il process
      dialog.** Il cablaggio **esiste già**: `_tree.DoubleTapped` (`:244`) → `OnActivate`
      (`:1736-1760`) → `DoCheckout(row)` → `DoCheckoutAsync` (`:1839-1874`), e con working tree
      **pulito** `CheckoutBranchDialog.AskAsync` (`CheckoutBranchDialog.cs:196-223`) ritorna
      `DontChange` **senza mostrare nulla** → `RunMutation` esegue `git checkout` in silenzio. Quindi
      il difetto percepito ("non succede niente") è **mancanza di feedback**, e su un checkout che
      *fallisce* è mancanza totale di diagnostica (`RunMutation` ignora `!success`).
      **Da misurare per primo**: se il checkout avviene davvero (confrontare `git branch --show-current`
      prima/dopo) — se non avviene, la causa è a monte del feedback e va trovata lì.
      Fix: process dialog sul checkout (upstream `FormCheckoutBranch.cs:357`), sia sul percorso
      pulito sia dopo la scelta del dialogo "local changes". Da valutare in più, per fedeltà:
      upstream chiede conferma prima del checkout da doppio clic
      (`LeftPanel/LocalBranchNode.cs:29-31,54-57` → `MessageBoxes.ConfirmBranchCheckout`), dietro
      un'impostazione della pagina **Confirmations** che il port non ha — se si porta la conferma,
      registrare che il flag non ha UI (come già fatto per `DontConfirmResolveConflicts`).
      ✅ **M75**: fatto su **tutti e 5** i call-site di checkout. Il process dialog viene aperto
      **dopo** la risposta di `CheckoutBranchDialog.AskAsync`, quindi compare su **entrambi** i
      percorsi: tree pulito (dove `AskAsync` risponde `DontChange` senza mostrare nulla) e dopo la
      scelta del dialogo "local changes". `CheckoutStreaming`/`CheckoutBranchStreaming` coprono
      anche il `-B` e il branch remoto/detached, con il pre-step di stash in streaming.
      **NON portata** la conferma upstream sul doppio clic (`MessageBoxes.ConfirmBranchCheckout`):
      dipende da un flag della pagina Confirmations che il port non ha, e aggiungerla senza UI
      significherebbe un comportamento non disattivabile dall'utente. Resta valutabile.

**Verifica GUI del blocco — NON ESEGUITA, e perché.** Il metodo di `HANDOFF.md` §3 (Xvfb + mini-WM
python-Xlib + `import -window root`) **non è applicabile sulla macchina di questo round**: Windows 11
ARM64, senza Xvfb, ImageMagick, python-Xlib, e con una WSL Ubuntu priva di SDK .NET. L'utente ha
scelto esplicitamente "solo codice + build, verifica manuale a mano". Verifica sostitutiva eseguita:
build `Errori: 0` con i 31 warning pre-esistenti e nessuno nuovo, più rilettura dei call-site. La
diagnosi di 13.1 ha invece usato input sintetico Win32 reale (vedi verdetto sopra). **Checklist
manuale consegnata all'utente** con i 5 criteri di accettazione (primo clic su `Create branch…`;
process dialog sulla creazione con branch nuovo visibile nell'albero; doppio clic → process dialog
con `git checkout` e branch corrente cambiato in albero e toolbar; casi di **fallimento** leggibili
— nome duplicato/rifiutato da `check-ref-format`, checkout bloccato da file locale con tree sporco;
nessuna regressione su Merge/Rebase/Create tag/Rename/Delete e sul checkout da toolbar e grid).
## Coda round 12 — PRIORITÀ UTENTE del 29/07/2026: commit dialog e flusso di merge

> Voci indicate dall'utente confrontando la GUI del port con l'originale Windows. **Hanno
> precedenza su tutto il resto** (le idee di valore elencate in `HANDOFF.md` §5 restano dietro).
> Screenshot di riferimento in `~/Documents/images avalonia/` — **letti e verificati**, contenuto
> descritto qui sotto perché i file possono spostarsi:
>
> | File | Cosa mostra |
> |---|---|
> | `commit window dialog.png` | `FormCommit` reale: due liste con **toolbar di icone propria** + casella "Filter files using a regular expression…", pulsanti **Unstage ⬆ / Stage ⬇** sulla barra fra le due liste, diff a destra con **gutter a due colonne di numeri di riga** (old/new) e fondo verde sulle righe aggiunte, colonna pulsanti Commit / Commit & push / **checkbox Amend commit** / Stash staged changes / Reset all changes / Reset unstaged changes, riga superiore `Commit message ▾ · Commit templates ▾ · Options ▾` con overflow `»`, status bar `Committer <nome> <mail>` · `branch → origin/branch` · **`Staged 1/4 Ln 0 Col 0`** |
> | `00_merge window.png` | `FormMergeBranch`: titolo "Merge branches", link **Hide help** + pannello illustrativo a sinistra, `Merge branch` (combo + pulsante picker commit), `Into current branch **master**` in sola lettura, radio **Keep a single branch line if possible (fast forward)** / **Always create a new merge commit**, checkbox **Do not commit** e **Show advanced options**, pulsante **Merge** in basso a destra |
> | `01_merge windows with process and conflict.png` | il `Process` dialog **sopra** la merge window, con la riga di comando (`git.exe merge --no-commit branch1`), output `CONFLICT (content): Merge conflict in README.md` / `Automatic merge failed…` / `Done`, footer `Keep dialog open` + `OK` + `Abort` |
> | `02_merge windows with process and conflict confirmation dialog.png` | modale **"Merge conflicts"** con icona `?`: *"There are unresolved merge conflicts, solve conflicts now?"* + Sì/No |
> | `03_resolve merge conflict window dialog.png` | `FormResolveConflicts`: lista **Unresolved merge conflicts** (colonna Filename), colonna pulsanti **Open in kdiff3** (nome del mergetool configurato, dinamico) / **Start mergetool** / **Rescan merge conflicts** / **Reset**, riquadro informativo *"The file has been changed both locally (ours) and remotely (theirs). Merge the changes."* + pulsante **Merge**, e le tre righe `Local/current (ours)` · `Base` · `Remote/incoming (theirs)` con il nome file per lato, link **Help** in basso |
> | `unresolved merge conflict UI from home.png` | la finestra principale durante il merge: **banner arancione** *"Merge is currently in progress with merge conflicts."* con i pulsanti **Resolve…** e **Abort** a destra |
> | `create branch window dialog.png` | `FormCreateBranch` (riferimento collaterale, non richiesto): revisione + **Checkout after create**, riquadro col commit risolto (autore/data/**Branch(es)** evidenziati/Tag(s)), gruppo **Orphan** con `Create orphan` + `Clear working directory and index` |

**BLOCCO 12.A — dialogo di commit** (`App/Views/CommitDialog.cs`, 3341 righe; upstream
`src/app/GitUI/CommandsDialogs/FormCommit.cs` + `FormCommit.Designer.cs`)

- [x] **12.A.1 — un nuovo file selezionato mostra il pannello diff VUOTO.** ✅ M71 Causa accertata:
      `PatchStagingService.cs:76-92` esegue `git diff [--cached] -- <path>` e per un file
      **untracked** git non produce nulla (non è nell'index, non c'è niente da diffare) →
      `stdout` vuoto, il pannello resta bianco senza errore. Upstream mostra il **contenuto
      intero** del file nuovo (`FileViewer` cade sul file grezzo). Fix: per una riga untracked
      caricare il contenuto del file (o `git diff --no-index -- /dev/null <path>`, che dà un
      patch vero con header `--- /dev/null` — coerente con `_diffFileIsNew`, già usato a
      `CommitDialog.cs:1429`/`:1849` per il line-staging) e non l'output vuoto di `git diff`.
      Attenzione a binari e file grandi: upstream tronca.
- [x] **12.A.2 — il commit non passa dal process dialog.** ✅ M71 Upstream esegue il commit dentro
      `FormProcess.ShowDialog` (`FormCommit.cs:1265`) e l'utente vede comando+output+hook.
      Il port lo esegue in silenzio: `CommitActionsService.Commit` (`:54-79`) chiama
      `module.GitExecutable.Execute` e `CommitDialog.DoCommit` (`:2331`) riporta solo una riga
      di status (`SetStatus`), quindi hook pre-commit, warning e messaggi di git sono
      **invisibili**. Fix: instradare il commit su `GitProcessDialog` (il port ha già
      `GitProcessDialog.RunStreamingAsync`, usato per il push a `CommitDialog.cs:2556`) —
      stessa superficie del push, con `Keep dialog open`.
- [x] **12.A.3 — `Reset all changes` / `Reset unstaged changes` fanno la cosa sbagliata.** ✅ M71
      Upstream (`FormCommit.cs:2184-2198`) instrada **entrambi** su
      `StartResetChangesDialog(..., onlyWorkTree)` = `FormResetChanges`, che chiede conferma
      **e** decide cosa fare degli **untracked**; poi disabilita i pulsanti quando non c'è
      niente da resettare (`:831`, `:2806`: abilitati solo se le liste non sono vuote).
      Nel port `CommitDialog.DoReset` (`:2564-2575`): il ramo `includeStaged: true` fa un
      `reset --hard HEAD` dietro una `ConfirmThen` generica, il ramo unstaged fa
      `git checkout -- .` **senza alcuna conferma** — distruttivo e silenzioso — e nessuno dei
      due tocca gli untracked né viene mai disabilitato
      (`WorkingDirectoryService.ResetChanges`, `:377-395`). Fix: portare `FormResetChanges`
      (conferma + scelta sugli untracked), applicare alle **righe della lista** come upstream,
      e gestire l'abilitazione.
- [x] **12.A.4 — la chrome del dialogo è ancora lontana da `FormCommit`.** Divergenze misurate ✅ M72
      contro `commit window dialog.png`: le due liste sono `ListBox` nude
      (`CommitDialog.cs:37-38`, `MakeList()` `:3292`) **senza la toolbar di icone per lista**
      e con **una sola** casella filtro (`:689`) invece di una per lista; **nessun gutter di
      numeri di riga** nel pannello diff (è un `SelectableTextBlock`, `:261`); **manca la status
      bar** in stile upstream (committer + `branch → remote` + **`Staged x/y Ln y Col x`**): oggi
      c'è solo un `TextBlock` di status (`:548`). Nota: la toolbar ricca della lista file è la
      voce **2d** già aperta (`FileStatusList.Toolbar.cs`) e il port ha già un
      `Views/FileStatusListView.cs` da riusare invece di ricostruirla.
      **Vincolo: niente pulsanti finti** — ogni icona aggiunta deve avere dietro un dato reale.

**BLOCCO 12.B — flusso di merge completo** (oggi il merge è **muto**)

Stato accertato al `6b5dff330`: `BranchTagService.MergeBranch` (`:633-647`) esegue
`Commands.MergeBranch(... allowFastForward: true, squash: false, noCommit: false ...)` con i
flag **cablati**, e tutti e quattro i chiamanti lo lanciano dentro un `RunMutation`
(`RepoObjectsTree.cs:1240` e `:1299`, `BranchTagPanel.cs:283`, `RevisionGridView.cs:6018`):
nessun dialogo, nessun process dialog, nessuna conferma, e in caso di conflitto l'utente
scopre lo stato solo aprendo il commit dialog. Non esiste alcun port di `FormMergeBranch`
(187 righe) né di `FormResolveConflicts` (1571 righe).

- [x] **12.B.1 — `MergeDialog` (port di `FormMergeBranch`)** ✅ M71: combo del branch da mergiare,
      `Into current branch <x>` in sola lettura, i due radio fast-forward / always-new-commit,
      checkbox **Do not commit**, **Show advanced options** (squash / strategy /
      allow-unrelated-histories: sono esattamente i parametri già presenti nella firma di
      `Commands.MergeBranch`, quindi **non** sono pulsanti finti), link Hide help. Il pannello
      illustrativo è ✅ **portato in M74** (`HelpImagePanel`, riusabile anche per Pull/Rebase:
      le sette PNG di `Resources/Help` sono già linkate come risorse Avalonia).
      I quattro call-site vanno instradati qui; `MergeBranch` deve accettare le opzioni invece
      dei flag cablati.
- [x] **12.B.2 — il merge passa dal `GitProcessDialog`** ✅ M71 (img 01), come già fanno
      fetch/pull/push: comando, output live, `Keep dialog open`, `OK`/`Abort`.
- [x] **12.B.3 — conferma "Merge conflicts" dopo un merge fallito** (img 02): port di ✅ M72
      `MergeConflictHandler.HandleMergeConflicts`
      (`src/app/GitUI/CommandsDialogs/MergeConflictHandler.cs:9-27`) — se
      `module.InTheMiddleOfConflictedMerge()` chiedere *"There are unresolved merge conflicts,
      solve conflicts now?"* e su Sì aprire il dialogo di risoluzione. Upstream ha anche il
      bypass `AppSettings.DontConfirmResolveConflicts`. Il gancio va messo su **tutti** i
      chiamanti che possono generare conflitti (merge, pull, cherry-pick, revert, rebase,
      stash apply), non solo sul merge.
- [x] **12.B.4 — `ResolveConflictsDialog` (port di `FormResolveConflicts`)** (img 03): lista ✅ M72
      dei conflitti (`WorkingDirectoryService.ListConflicts`, `:110-118`, esiste già:
      `git diff --name-only --diff-filter=U`), pulsanti **Open in \<mergetool\>** con il nome
      del tool **letto da `merge.tool`** (dinamico: "Open in kdiff3" nello screenshot) e
      **Start mergetool**, **Rescan merge conflicts**, **Reset**, il riquadro che descrive il
      tipo di conflitto (both-modified / deleted-by-us / deleted-by-them: sono gli stati che
      `--diff-filter=U` + `ls-files -u` distinguono davvero) e le tre righe
      ours/base/theirs. `WorkingDirectoryService.cs:134-173` sa già lanciare
      `git mergetool --no-prompt -- <path>` detached, quindi il pulsante apre **davvero** il
      tool configurato (kdiff3, meld, …) come chiesto.
- [x] **12.B.5 — banner del merge con `Resolve…` e `Abort`** (img `unresolved merge conflict UI ✅ M72
      from home.png`): `RepositoryProgressBanner.cs:300`/`:335` oggi mostra il testo
      *"A merge is in progress."* e come *suggerimento testuale* "…or run: git merge --abort",
      cioè **dice all'utente di andare in terminale**. Vanno aggiunti i due pulsanti veri
      (Resolve… → 12.B.4, Abort → `git merge --abort`), come già fatto per bisect e `git am` in
      M68.

**Trappole già note che valgono per questo round** (dettaglio in `HANDOFF.md` §3): i service
bloccano su lavoro async → pre-caricare in `Task.Run` e non chiamarli dal thread UI (il
deadlock di `PushDialog`); `MenuFlyout.Items` popolati **prima** di `ShowAt`; brush solo da
`Application.Current.Resources`; nomi degli asset `IconLoader` **case-sensitive** con log
all'avvio da leggere; **verificare la premessa** contro il codice all'HEAD vero prima di
scrivere (i riferimenti `file:riga` qui sopra sono presi al `6b5dff330`).

## ROUND 11 — i parziali — **CHIUSO** (M67–M70)

> **Esito del round**: la "Coda round 9" non ha più **nessuna** voce `- [ ]` né `- [~]`. Chiuse 4.1,
> 4.11 (tutta), 3.2 (tutta), i tre banali, l'i18n mirato dell'auth-failure, i file picker (il vicolo
> cieco storico era una diagnosi sbagliata) e la voce nuova della palette di sintassi. Restano solo
> gli SKIP consapevoli dichiarati (repository-host GitHub, colonna build status, script utente) e le
> note estetiche registrate qui sotto.


> **Iterazione 1 / 15.** Tre subagent Claude in worktree isolati su file disgiunti (A checkout
> remoti, B tre banali, C auth-failure indipendente dalla locale), più il cablaggio in `MainWindow`
> fatto dal loop. Base `537990dc6`, build `Errori: 0` dopo ognuno degli 11 cherry-pick.

**M67** (2026-07-29) — **4.1 chiusa, i tre banali del punto 2 chiusi, l'i18n mirato del punto 4
chiuso**. Undici commit (`f0c451eba`…`c4b366347`).

- **4.1 — checkout di rami remoti, ora possibile dalla GUI** (`d38d64427`, `77f8c2e9e`,
  `4f7a5fe65`, `c4b366347`). `App/Views/CheckoutBranchForm.cs` è il port completo di
  `FormCheckoutBranch`: radio Local/Remote, casella branch con autocompletamento, contatore
  ahead/behind, le **tre modalità new-branch** (create-with-custom-name / reset-local-branch /
  detached) e il gruppo "Local changes" mostrato **solo** su tree sporco.
  `BranchTagService.CheckoutBranch` passa per `Commands.CheckoutBranch` del core
  (`src/app/GitCommands/Git/Commands.cs`) con `LocalChangesAction` + `CheckoutNewBranchMode`;
  `Stash` fa un pre-stash perché il builder del core non ha il flag.
  **Correzione alla voce di coda**: l'esclusione di Checkout sui nodi remoti in `RepoObjectsTree`
  *non esisteva più* (round 10 l'aveva già rimossa) e `MainToolbar` aveva già `Checkout branch...`
  in testa al dropdown: il residuo vero era il **dialogo**, più il fatto che il picker della
  toolbar e `Ctrl+.` andavano a un picker **solo-locale** (`MainWindow.CheckoutBranchPickerAsync`),
  ora instradati sul form con la conferma upstream sul reset non-fast-forward.
  *Verificato in GUI* (display privato, repo `/tmp/r11int` con un ramo `solo-remoto` presente solo
  sul remote): dropdown → `Checkout branch…` → Remote branch → autocompletamento →
  `(+0-1)` e l'etichetta che passa a "Create local branch with same name: 'solo-remoto'" → Checkout
  ⇒ `* solo-remoto 93fbed3 [origin/solo-remoto]`, **branch locale tracciante, non detached**, albero
  e status bar aggiornati (`↑0↓0`). Il subagent aveva già dimostrato le altre due modalità e il
  warning di reset su merge base.
- **Punto 2a — warm-up del `Lazy<Encoding>` del core** (`f0c451eba`). Il difetto è in
  `src/app/GitCommands/Git/ExecutableExtensions.cs:15` (`isThreadSafe: false`), dereferenziato a
  `:97` e `:291` come **prima** istruzione: le prime due chiamate git concorrenti di un processo
  lanciano `InvalidOperationException: ValueFactory attempted to access the Value property`.
  Misurato con una sonda a `Barrier`: **40/40 fallimenti a freddo, 0/40 col warm-up** — deterministico,
  non flaky. Il warm-up è una riga in `App/Program.cs` che chiama
  `ExecutableExtensions.GetOutput` con `outputEncoding: null` (unico membro pubblico che materializza
  il Lazy prima di avviare il processo; `git --version` è solo la scusa più economica). Il core
  **non è stato toccato**. Ipotesi scartata con misura: `SystemEncodingReader.cs:41` passa
  `Encoding.UTF8` esplicito, quindi non rientra nel Lazy — il difetto è puramente cross-thread, ed
  è per questo che un warm-up single-thread basta.
- **Punto 2b — `AddNotesDialog` raggiungibile** (`29766eb3e`). La voce di parità 1.10 era spuntata
  su una mezza verità: `CommitDetailView.cs:184` chiamava già `EditNotes()` dal menu contestuale del
  commit-info, ma `HotkeyService.cs:180` **dichiarava** Ctrl+Shift+N senza che `InstallHotkeys` la
  legasse — gesture pubblicizzata (e rimappabile in Settings) e inerte. Ora è cablata come upstream
  (`FormBrowse.AddNotes` + l'etichetta di `CommitInfo.cs:113`). *Verificato in GUI*: la finestra si
  è renderizzata **per la prima volta** e la nota fa round-trip
  (`git notes show 93fbed3b…` → `nota round 11 integrazione`). NON aggiunta al menu contestuale
  della griglia: upstream non ce l'ha.
- **Punto 2c — la terna delle pill ref, non la sola pillola tag** (`a97ef36cd`). A fallire erano
  **tre casi su sei**, non uno: tag in chiaro 3,25 e **branch/remote in scuro 2,99 / 2,82**. Il
  blocco strutturale era la riga selezionata, che scambiava il fondo con un **bianco opaco
  hard-coded**: per superare 4,5:1 serve luminanza ≥ 0,254 su `#252526` e ≤ 0,183 su bianco, quindi
  nessuna singola tinta poteva servire i due fondi finché quel bianco c'era. Rimosso, non
  ri-tematizzato. Quattro chiavi nuove in `ThemeManager` (`Keys`+`Dark`+`Light`):
  `App.RefPillBg`, `App.RefBranch`, `App.RefRemote`, `App.RefTag`.
  Contrasti prima → dopo: branch chiaro 5,13→6,53 · remote chiaro 5,44→6,67 · **tag chiaro
  3,25→6,40** · **branch scuro 2,99→6,67** · **remote scuro 2,82→6,56** · tag scuro 4,71→6,67.
  Tutte e sei in 6,40–6,67, nessuna è più l'anello debole. *Ricontrollato dal loop* sui pixel di uno
  screenshot indipendente in tema chiaro: branch 6,53 e remote 6,67, coincidenti.
  Effetto collaterale voluto: passando i brush **per riferimento** le pill seguono ora il cambio
  tema a caldo (le vecchie copie `SolidColorBrush` no). Lasciata fuori la pill **note**
  (`BuildNotesBadge`, misurata 5,34, passa AA; resta un chip scuro in tema chiaro, incoerenza
  estetica da guardare un giorno).
- **Punto 7 — i file picker: vicolo cieco CHIUSO, non registrato** (`6db901748`). La diagnosi
  storica ("`OpenFolderPickerAsync` torna vuoto perché serve un portal XDG") era **sbagliata**.
  Misurato sulla sessione **Wayland/XWayland reale** dell'utente (con il suo ok): il portal c'è e
  risponde — `FileChooser` version 3, backend `xdg-desktop-portal-gnome` *e* `-gtk` attivi, e una
  `org.freedesktop.portal.FileChooser.OpenFile` invocata a mano con `gdbus` viene servita e
  restituisce un request handle. Ma `dbus-monitor` sul bus di sessione vede **zero traffico dal
  processo dell'app** quando si premono i `Browse…`: è lo `StorageProvider` X11 di Avalonia che non
  arriva mai al portal e torna lista vuota senza eccezione. Fix: `UseManagedSystemDialogs()` in
  `Program.BuildAvaloniaApp` (+ `using Avalonia.Dialogs`), che fa girare il picker **in-process**.
  *Verificato end-to-end*, sia sul display reale sia headless: `Ctrl+O` → `Browse…` → il picker
  elenca `/tmp` con i bookmark veri (Desktop/Documents/…/volumi) → path digitato `/tmp/r9repo` + OK
  ⇒ il repo si apre (5 commit, 2 stash, 2 worktree). **Conseguenza sul metodo**: i picker sono ora
  verificabili headless. Nota estetica registrata: i managed dialog non seguono il tema dell'app
  (fondo nero, icone ambra).
  *Metodo sul display reale*: XTEST **tastiera** funziona (`set_input_focus` + `Ctrl+O`, o
  `_NET_ACTIVE_WINDOW` + `Tab`/`space`); il **puntatore no**, `fake_input` MotionNotify viene
  ignorato/clampato da mutter. Screenshot **per finestra** (`import -window <id>`): il root di
  XWayland non mostra le finestre Wayland.

- **Punto 4 — auth-failure indipendente dalla locale** (`deac4ae2d`, `b9155207a`, `66e0a8bb5`,
  `0a26785b7`). Scelte **entrambe** le strade, in quest'ordine:
  1. *Pinning della locale dei figli*: `App/Services/GitEnvironment.cs` imposta `LC_MESSAGES=C`,
     **rimuove `LC_ALL`** portandone il valore in `LC_CTYPE` e azzera `LANGUAGE`. La rimozione di
     `LC_ALL` non è opzionale — misurato: `LC_ALL=it_IT.UTF-8 LC_MESSAGES=C` stampa **ancora**
     italiano, perché `LC_ALL` sovrascrive la categoria. Scartato `LC_ALL=C`/`C.UTF-8` per non
     toccare l'encoding. Applicato al path pipe e a quello PTY, a `PushRefsService.Capture`, a
     `ApproveCredentials` e — a livello di processo e temporaneamente — attorno a
     `module.GitExecutable.Execute`, che è core condiviso e non offre hook di env per comando.
     **NON** applicato alla Console incorporata: `PtyProcess.Start` ripristina esplicitamente la
     locale vera dell'utente, perché quella shell è sua.
  2. *Segnale strutturale*: `App/Services/GitAuthProbe.cs` registra i **verbi del credential
     helper** (`get`/`store`/`erase`), che sono token di protocollo e non messaggi. `erase` = il
     server ha rifiutato le credenziali; `get` + exit ≠ 0 = il comando serviva credenziali ed è
     fallito. Misura chiave: un helper `-c` è consultato **ultimo** per `get`, ma **`erase` va a
     tutti gli helper**, quindi la sonda vede sempre il rifiuto. `GitAuthSignal` (holder
     `AsyncLocal`) porta il verdetto fino al dialogo senza cablaggio.
  **Difetto più profondo trovato per strada**: sul path PTY interattivo — quello che usano *tutti*
  fetch/pull/push del process dialog — l'output di git non arriva mai a `onLine`, va al terminale
  come byte grezzi, quindi i matcher di testo ricevevano una stringa vuota ed erano ciechi
  **anche in inglese**. È la sonda che sistema davvero quel path.
  *A/B verificato in GUI* con un server locale che risponde sempre `401` e un credential helper che
  fornisce credenziali sbagliate, app e git in italiano: **prima** console
  `fatal: Autenticazione non riuscita per 'http://127.0.0.1:8791/x.git/'`, stato Failed, nessun
  `CredentialsDialog` nemmeno 8 s dopo; **dopo** il process dialog si chiude e il
  `CredentialsDialog` si apre, e il retry stampa `fatal: Authentication failed for …` in inglese
  mentre l'app resta in italiano. Console tab: `LC_MESSAGES="it_IT.UTF-8"`, `git status` italiano,
  accenti corretti, ramo `perché-àèìòù-日本` reso bene.
  Già sicuri e lasciati stare: `! [rejected]` (la tabella di stato di push non è tradotta),
  `%(upstream:track)`/`gone`, il prompt `Username for '…'` (non tradotto in git 2.43) e i prompt di
  ssh (non localizzati). **Compromesso accettato e registrato**: le diagnostiche git nella console
  del process dialog sono ora inglesi anche per un utente italiano. Nessuna stringa tradotta
  aggiunta, come chiesto. Fuori dall'unità e ancora inglese: `CleanupDialog.cs:509` (prefisso
  `fatal:`).

> **Iterazione 2 / 15.** Tre subagent Claude in worktree isolati su file disgiunti (D bisect,
> E macchina a stati `git am`, F clean/init/clone), più il cablaggio in `MainWindow` fatto dal loop.
> Base `c4b366347`, build `Errori: 0` dopo ognuno dei 10 cherry-pick.

**M68** (2026-07-29) — **il grosso di 4.11**: bisect con gating, `git am` come macchina a stati,
e i tre dialoghi clean/init/clone chiusi contro upstream. Dieci commit
(`efacf8d9d`…`d553a318f`) più il cablaggio `17b0987bb`.

- **Bisect: il port non avvia più una sessione dietro le spalle** (`46a40f255`, `12275d46d`,
  `31cc7ab01`, `da44268fe`, `d8b2f84ab`, `d553a318f`). Il difetto era in `MainWindow.cs:1509-1516`:
  `git bisect start` partiva **in silenzio** ogni volta che non c'era sessione, e la griglia
  abilitava le marcature senza altra condizione che `ctx.SingleCommit` — un clic sbagliato
  staccava HEAD. Ora l'auto-start non c'è più, le quattro voci in-sessione richiedono una sessione
  aperta (come upstream, `RevisionGridControl.cs:2256-2261`), e avviare è un atto esplicito nel
  nuovo `App/Views/BisectDialog.cs` (port di `FormBisect`, gating come `FormBisect.cs:27-35`). Il
  `RepositoryProgressBanner` mostra lo stato reale con i pulsanti Good/Bad/Skip/Stop/More di
  upstream.
  *Verificato in GUI dal loop, oltre alla sessione completa del subagent*: senza sessione il
  sottomenu `Other actions` ha **solo** `Start bisect…` attivo e le quattro marcature spente;
  aperta la sessione, esattamente **invertito**. Sessione: `Mark bad` su c09 → banner
  *"Bisecting — a bad commit is known; mark a good one to bound the search."* con pill `bisect/bad`
  sul commit → `Mark revision as good` su c02 → banner **"3 revisions left to test, roughly 2 more
  steps."** e `git bisect log` coerente → `Stop bisect` ⇒ ritorno a `main`, nessun `BISECT_START`.
  Il subagent aveva già portato una sessione intera a convergere sul colpevole piantato
  (`11e5f254`), confermato da `git bisect log`.
  **I conteggi sono veri, non raschiati dall'output**: vengono da `git rev-list --bisect-vars`,
  perché la riga di progresso di git è **localizzata** (qui git parla italiano). Nota: le stringhe
  non sono pluralizzate ("1 revisions left"); servirebbe un formatter plural-aware in
  `TranslationService`.
  **Trappola nuova e riusabile**: gli argomenti di `GitArgumentBuilder` finiscono appiattiti in
  un'unica stringa `ProcessStartInfo.Arguments`, quindi **un argomento che contiene uno spazio viene
  ri-splittato**. `--format=%(refname) %(objectname)` arrivava a git come due argomenti: exit 0 e
  colonna hash **assente in silenzio**. Trovata solo guardando lo screenshot e notando che il bisect
  finito non nominava ciò che aveva trovato.
  Non fatti, con motivo: `git bisect run`, `bisect terms`, barra di progresso determinata (nessun
  dato/servizio dietro).
- **`git am` come macchina a stati** (`efacf8d9d`, cablaggio `17b0987bb`).
  `App/Services/AmSessionService.cs` + `App/Views/ApplyPatchDialog.cs` portano `FormApplyPatch`:
  scelta file/directory, **PatchGrid** con lo stato per patch, e Resolved / Skip / Abort con le
  regole di abilitazione di upstream. Stato letto dalle API vere del core
  (`GitModule.InTheMiddleOfPatch()` `:1975`, `InTheMiddleOfConflictedMerge()` `:511`,
  `GetRebaseDir()` `:1639`); griglia dalle copie numerate in `.git/rebase-apply` + `next`, cioè il
  port di `PatchGrid.GetRebasePatchFiles()` (`:199-320`) con la precedenza di `PatchFile.Status`
  intatta; argomenti byte-identici a `Commands.Arguments.cs` (tutti con `--3way`).
  `GetInteractivePatchesFromFolder`/`FromReverseFolder` **non esistono** in questo albero.
  Il vecchio corpo di `MainWindow.ApplyPatchAsync` era un file picker + un apply, quindi una
  sessione `am` fermata non aveva **nessuna** superficie.
  *Verificato in GUI dal loop, dopo l'integrazione*: directory `/tmp/r11am/pset` su un repo
  divergente → conflitto su 0002, il `GitProcessDialog` mostra
  `CONFLICT (content) … Patch failed at 0002`, la griglia mostra `0001 Applied / 0002 Applying… /
  0003`, Apply spento, **Conflicts resolved spento** perché l'indice è conflittato, Stage all /
  Skip / Abort accesi, e il banner del repository dice *"A patch series is being applied (git am).
  Step 2 of 3."* → `Abort` ⇒ HEAD torna indietro, tree pulito, `.git/rebase-apply` inesistente.
  Il subagent aveva già coperto anche Resolved (con `git add -A` dalla GUI) e Skip (griglia con i
  tre stati insieme).
  Deviazioni registrate: la directory di patch passa come **argomenti** e non su stdin (il path
  dell'output live chiude stdin), ordinata per nome; riga corrente evidenziata con la chiave
  tematica `App.RepoStateDirty` invece del `OrangeRed` hard-coded di upstream. Non fatti: modalità
  rebase interattivo della griglia (è di `FormRebase`), "Solve conflicts"/"Add files" come dialoghi
  (`FormResolveConflicts`/`FormAddFiles` non esistono nel port → nessun pulsante finto, "Add files"
  è un onesto `git add -A`).
- **clean / init / clone: la premessa era stantia** (`f54d81c79`, `688822486`, `c4623e3fa`). Alla
  base assegnata tutte e tre le unità erano **già portate** (`f7bede515`, `8090d2a19` e seguiti):
  `clean -X` **era** raggiungibile, `FormInit` **esisteva**, e il clone aveva già le quattro cose.
  Il lavoro utile è stato verificarle end-to-end e chiudere i residui contro upstream.
  *Prove*: i tre modi di clean danno tre risultati **diversi e corretti** sulla fixture
  tracked/untracked/ignored/untracked-dir — `-X -d` → 2 voci, default `-d` → 2, `-x -d` → 4 (il
  tracciato resta sempre); il Preview coincide **esattamente** col dry-run della CLI (riverificato
  dal loop: `build/` + `debug.log`, "2 entries would be removed"); Clean fa sempre dry-run e chiede
  conferma, Cancel non cancella nulla. Init: Central → `is-bare-repository=true` +
  `core.sharedRepository=2`; Personal → `false`. Clone: branch picker popolato da `ls-remote`
  **fuori dal thread UI**, ramo scelto davvero checkoutato (riverificato dal loop:
  `## feature...origin/feature`), shallow provato con `is-shallow-repository=true`, submodule
  inizializzato.
  Residui chiusi: modo preselezionato + wording dei radio + i due picker "Add a path…" mancanti +
  la riga di stato che diceva "Previewing…" sotto un prompt di cancellazione vero; la **banda bianca
  non dipinta** della finestra di init (`SizeToContent.Height` è solo una *richiesta* che un WM può
  ignorare); la preview della destinazione del clone con la semantica di upstream ("already exists"
  solo se la directory è **non vuota** — clonare in una directory esistente e vuota è normale).
  Non fatti perché **upstream non li ha**: campo numerico per il depth e controllo `--shared`
  separato (un solo radio pilota entrambi i flag); `LoadSSHKey` (solo PuTTY, upstream lo nasconde).
  **Trappola**: git **ignora `--depth` per i cloni da path locale**, lo shallow si vede solo con un
  URL `file://`.

> **Iterazione 3 / 15.** Tre subagent Claude in worktree isolati su file disgiunti (G RemotesDialog +
> FormVerify, H Archive/Sparse/About, I persistenza 3.2), più il cablaggio fatto dal loop. Base
> `6aa8ffb4b`, build `Errori: 0` dopo ognuno dei 14 cherry-pick.

**M69** (2026-07-29) — **4.11 chiusa** e **3.2 chiusa**. Sedici commit
(`68a961498`…`2ec65b7f3`).

- **`RemotesDialog`: tab "Default pull behavior" e push URL separata** (`0b69d94ee`, `565e8e901`,
  `68e6366a5`). Qui la premessa **non** era stantia: `RemotesDialog.cs` non aveva nulla di
  `pushurl`/`branch.`/default-pull. Due dettagli del prompt erano invece sbagliati: la chiave PuTTY
  è `remote.<name>.puttykeyfile` (non `puttysshkey`) e upstream la nasconde quando
  `GitSshHelpers.IsPlink` è false — **non** "fuori da Windows". Su Linux è sempre false, quindi
  ometterla **è** il comportamento di upstream.
  *Bug trovato e corretto*: il setter `TrackingRemote` del core **auto-semina `branch.<x>.merge`**,
  e la riga successiva riassegnava la casella merge ancora vuota, **cancellandola**. Misurato: dopo
  "scegli origin + Save" esisteva solo `branch.main.remote`, cioè un ramo su cui `git pull` non
  funziona. Ora si scrivono solo i campi che l'utente ha cambiato (semantica per-`Validated` di
  upstream) e le due chiavi restano coerenti (`.merge` = `refs/heads/main`).
  Verificato con `git config --get-regexp`: `remote.origin.pushurl` scritta, rimossa quando si
  toglie la spunta **e** quando la si imposta uguale alla fetch URL (regola di ridondanza
  case-insensitive di upstream: la checkbox si stoglie da sé).
- **`FormVerify` → `VerifyDialog`** (`8b3e7b0b5`, `f55a19cc8`, `41e018370`, cablaggio `2ec65b7f3`).
  `MaintenanceService.cs:39` era letteralmente `new GitArgumentBuilder("fsck")`. Ora
  `App/Services/VerifyService.cs` + `App/Views/VerifyDialog.cs` portano il dialogo vero: opzioni
  `--unreachable` / `--full` / `--no-reflogs`, filtri commit-e-tag / blob-e-tree, lista con
  Date/Type/Subject/Author/Hash/Parent, pannello di preview, e le azioni di upstream (recover in
  `LOST_FOUND_*`, create tag/branch, "Save objects to .git/lost-found", "Delete all LOST_AND_FOUND
  tags", prune dietro conferma).
  *Difetto trovato che la build non può vedere*: **l'output di `git fsck` è localizzato** — git
  italiano dice `commit non raggiungibile`, quindi la regex inglese di upstream parsa **zero
  oggetti uscendo con 0**, indistinguibile da un repo sano. Ogni chiamata fsck gira ora dentro
  `GitEnvironment.DiagnosticLocaleScope()` (l'infrastruttura di M67 che torna utile una seconda
  volta). Secondo difetto: **`Button.Content` come stringa mangia `_` come access key**, quindi
  "LOST_AND_FOUND" si leggeva "LOSTAND_FOUND"; risolto con un `TextBlock` figlio.
  *Verificato in GUI dal loop*, con app e git in **italiano**, su un repo con oggetti realmente
  irraggiungibili (branch cancellato + `reflog expire --expire=now --all`): la griglia elenca
  `dangling commit — lost commit 2` (quindi il parsing regge l'italiano), la spunta +
  "Recover selected objects" crea `LOST_FOUND_1`, e **`git log LOST_FOUND_1` mostra entrambi i
  commit perduti** mentre `git fsck --no-reflogs` non riporta più nulla: il recupero **rende
  davvero raggiungibile** l'oggetto. Il menu `Repository → Git maintenance → Recover lost objects…`
  ora apre questo dialogo (prima ricadeva su `MaintenanceDialog`).
- **`ArchiveDialog`: revisione, tar semplice, filtri mutuamente esclusivi** (`68a961498`,
  `262250266`, `f70d76faf`). Premessa **stantia**: il filtro path e quello per revisione c'erano
  già. Aggiunti la scelta della **revisione** (casella "Choose another revision" + Load, risolta con
  `rev-parse`) e il formato **tar** semplice, e resi i due filtri mutuamente esclusivi come upstream.
  Upstream **non espone `--prefix`**, quindi non è stato inventato; e non ha un commit picker
  riusabile nel port (`FormChooseCommit` non è portato), quindi la revisione si digita.
  *Verificato dal loop*: retarget su `1813d997` (c1) + formato `tar` ⇒ `tar -tf` elenca **esattamente**
  i tre file di c1 (`docs/d.txt`, `gamma/g.txt`, `src/a.txt`), senza `src/b.txt` di c2 né
  `docs/c.txt` di c3. Cablato anche il **difetto di rendicontazione**: `MainWindow` costruiva lo
  stato post-archive dall'hash della **riga di griglia**, quindi scegliendo un'altra revisione
  diceva "Archived <riga>" mentre l'archivio conteneva un altro commit; ora `ArchivedRevision`
  riporta il commit vero (verificato: status bar `Archived 1813d997 → /tmp/r11a3out.tar`).
  *Difetto che solo lo screenshot ha rivelato*: la riga della revisione era costruita ma **mai
  aggiunta al pannello** — build verde, controllo assente.
- **`SparseDialog`: allineato al legacy di upstream, la negazione `!` funziona** (`782bc0f4f`,
  `75ffe19aa`). Decisione: **allinearsi a upstream**, perché il cone mode **non può esprimere la
  negazione** — `git sparse-checkout set --cone '!gamma'` fallisce con *"Specify directories rather
  than patterns"*. Il legacy è ora il default, il cone resta opt-in (rimuovere una feature che
  funziona sarebbe una regressione).
  *Verificato dal loop*: regole `/*` + `!gamma/` ⇒ `gamma/` sparisce dal working tree, `docs` e `src`
  restano, `core.sparsecheckout=true`; Disable ⇒ `gamma` torna, `core.sparsecheckout` sparisce,
  **zero** voci `skip-worktree` in `git ls-files -v`.
  *Due difetti misurati e corretti*: (1) **il Disable era un no-op silenzioso** — nell'ordine di
  upstream, con `core.sparsecheckout=false` già scritto, `read-tree -m -u HEAD` non ricalcola
  `skip-worktree`, e su git 2.43.0 `ls-files -v` mostrava ancora `S gamma/g.txt` dopo un "successo";
  ora il refresh avviene mentre il flag è ancora attivo. (2) **`.git/config.worktree` batte
  `.git/config`**: dopo un uso del cone mode il dialogo dichiarava "enabled" su un tree ripristinato;
  ora viene azzerato anche quello.
- **`AboutDialog` completo** (`af6fcca0b`, `363ebe4ad`). Premessa **stantia**: versione git, build
  sha e attribuzione Kamiyamane c'erano già (il build sha via il target `StampBuildProvenance` del
  csproj, perché il pacchetto `GitInfo` di upstream non è restorabile offline). Aggiunti copyright e
  la clausola di non-garanzia. *Difetto pre-esistente trovato leggendo il log delle icone come
  prescrive l'HANDOFF*: **il logo dell'About non risolveva** e si renderizzava vuoto in silenzio.
  *Verificato dal loop*: logo presente, `Version 5.0.0-linux1`, `Build 363ebe4ad0 (Dirty)` che
  segue l'HEAD vero, `Git 2.43.0` rilevato a runtime.
- **3.2 CHIUSA: la persistenza residua vive in `view-prefs.json`** (`21215958a`, `f42edc609`,
  `676e55172`, `5209c2616`, `8ab02f41c`). Strada scelta per il conflitto di scrittura su `UiState`:
  **file separato**, il precedente di `commit-info.json`, e non l'instradamento sull'host. Motivi:
  tre dei quattro editor **non sono posseduti da `MainWindow`** (`DiffView` e `FileHistoryView` hanno
  una seconda istanza dentro le finestre autonome del `CommitDialog`; la MRU la scrive un modale che
  non esiste più quando l'host salva), le scritture sono **immediate** quindi lo stato sopravvive
  anche a un kill che salta `PersistLayout()`, e ogni scrittura passa da `Update(mutate)` così un
  gruppo non annulla quello di un'altra superficie. Il layout (larghezza/collapse/ordine) resta in
  `UiState`.
  Correzione alla voce: la MRU del **quick filter** era **già** persistita
  (`filterMru:<rank>:<text>` in `UiState.GridViewOptions`, `RevisionGridView.cs:3364`); quella che
  non esisteva era la MRU del **dialogo dei filtri avanzati**, ed è quella che è stata fatta (cap 15,
  più recente in testa, senza duplicati, pulsante "Recent filters ▾" **disabilitato quando è vuota**
  — nessun pulsante finto). Il left panel era parziale: larghezza/collapse/*ordine* delle categorie
  erano già persistiti dall'host, la **visibilità** e l'**ordinamento** no.
  *Prove del ciclo cambia → Start→Exit (exit code 0, non `kill`) → riapri*: 11 opzioni del diff
  viewer (riverificato dal loop: `-b -w` accesi e riga di comando `--find-renames -b -w -U5` **dopo**
  la riapertura, cioè le opzioni ripristinate arrivano davvero a git); 4 switch della file history;
  8 valori del left panel; la MRU avanzata che dopo il ciclo elenca due voci e ricompila tutti i
  criteri, e che ri-confermando una voce resta a 2 promuovendola in testa.
  Non fatti, con motivo: testo della casella di ricerca e insieme dei nodi espansi (stato di
  navigazione, non filtri: ripristinare la ricerca riaprirebbe l'app su un albero potato senza causa
  visibile), sync di `IsChecked` fra istanze di `DiffView` (pre-esistente, i valori erano già
  condivisi via singleton).

> **Iterazione 4 / 15 — ultima.** Due subagent Claude in worktree isolati su file disgiunti
> (J palette dei token, K tema dei managed dialog). Base `1affc7341`, build `Errori: 0` dopo ognuno
> dei 3 cherry-pick.

**M70** (2026-07-29) — **la palette di syntax highlighting a due temi e i file picker tematizzati**.
Tre commit (`80c8a3170`, `e64344fb1`, `c11c183a9`).

- **Palette di syntax highlighting: il lavoro ANDAVA fatto, e il presupposto era più forte del
  previsto** (`80c8a3170`). La via d'uscita "forse è un percorso morto" è **misurabilmente falsa**:
  `FileTreeView.cs:534` chiama `RenderContent(text, path, highlight: !binary)` e il tab **File tree
  non ha alcun toggle** per la sintassi — colora ogni file di testo che apre. Quindi in tema chiaro
  l'inchiostro pensato per il fondo scuro era lo **stato di default**, senza modo di spegnerlo.
  Seconda scoperta della rimisura: l'highlighter ridipinge le righe `+`/`-` sopra una **tinta** di
  fondo (alpha `0x28`), quindi il fondo vero è `#2A392C`/`#3C2A2A` in scuro e `#DEECDF`/`#F0DEDE` in
  chiaro — e contro quelli **anche il tema scuro** falliva AA (commento 4,55; preprocessor 4,45).
  Il vincolo che lega i valori chiari è `#F0DEDE`, non `#FFFFFF`.
  Cinque chiavi nuove in `ThemeManager` (`Keys`+`Dark`+`Light`):
  `App.TokenKeyword`, `App.TokenString`, `App.TokenComment`, `App.TokenNumber`,
  `App.TokenPreprocessor`. I valori chiari **conservano la tinta scura** (±5° di tonalità) e
  scuriscono; in scuro keyword/string/number sono intatti, comment e preprocessor alzati in modo
  impercettibile (ΔE*ab 4,8 e 3,1) per superare AA sulle tinte. Le due view **cachano l'istanza** del
  brush di risorsa, quindi la mutazione in-place del `Color` da `ThemeManager` ridipinge a caldo.
  Contrasti letti sui pixel (nucleo del glifo): **tab Diff scuro** min 4,45 FAIL → **4,76**; **tab
  Diff chiaro** min 1,31 → **4,64**; **tab File tree chiaro** min 1,70 → **6,01**.
  *Riverificato dal loop in modo indipendente*, su un `.cs` con tutti e cinque i tipi di token:
  chiaro 5,89–10,79 (keyword 7,09 · string 5,89 · comment 6,24 · number 10,79 · preprocessor 6,93),
  scuro 5,67–9,01 — tutti sopra 4,5:1, e le cinque tinte restano distinte a vista in entrambi i temi.
  **La distinguibilità è stata ottimizzata, non assunta**: la distanza CIE L*a*b* a coppie, simulando
  anche deuteranopia e protanopia, è **ΔE ≥ 17,6** per la famiglia chiara, contro **2,4** della
  vecchia coppia string↔comment in scuro. È per questo che *number* è il più scuro dei cinque: il
  grappolo verde/oliva/rust collassa in tonalità per chi non distingue rosso e verde, quindi la
  separazione deve venire dalla **luminosità** (che conserva anche l'identità: number era il token a
  contrasto più alto anche in scuro).
  Non fatto, con motivo: nessun ridisegno della famiglia scura (l'ottimizzazione libera spingeva
  *string* verso il bianco `#F0E4DB` — passa AA ma una stringa che sembra testo normale perde la sua
  identità); la coppia scura string↔comment resta la più debole per un protanope, **registrata col
  suo numero** invece di lasciata in silenzio; nessun toggle della sintassi aggiunto al tab File tree
  (upstream non ce l'ha e ora l'inchiostro è leggibile in entrambi i temi).
- **I managed dialog seguono la palette dell'app** (`e64344fb1`, `c11c183a9`). Anche qui la premessa
  era **in parte falsa**: il picker **non** è cieco al tema, perché `ThemeManager.Apply` imposta
  `app.RequestedThemeVariant` (`App/Theming/ThemeManager.cs:186`) — misurato prima di toccare, il
  fondo era `#000000` in scuro ma `#FFFFFF` in chiaro. Il difetto vero è che usa le **superfici base
  di Fluent** invece della palette `App.*`: in scuro un lastrone nero contro `App.Window #1E1E1E`.
  *Prova strutturale* (`ilspycmd`, richiede `DOTNET_ROOT=$HOME/.dotnet`): `Avalonia.Dialogs.dll`
  **non contiene stili** per `ManagedFileChooser` (uniche risorse: un font e
  `AboutAvaloniaDialog.xaml`); il suo `ControlTheme` sta in `Avalonia.Themes.Fluent.dll` sotto la
  chiave `typeof(ManagedFileChooser)` e usa **esattamente sei** chiavi brush, tutte
  `DynamicResource` → ridefinibili. Fix in `App/ManagedFileChooserTheming.cs` (nuovo) + 6 righe in
  `App/App.cs`: nessuna riga in `Program.cs`, nessuna chiave `App.*` nuova.
  Contrasti (soglia 4,5:1): riga file scuro `#000000` → `App.Window #1E1E1E` **16,67**; chiaro
  `#FFFFFF` → `#F3F3F3` **18,93**; sidebar → `App.PanelAlt`; OK/Cancel min **8,72**. Il calo da 21:1
  è **voluto**: 21:1 *era* il sintomo (nero e bianco puri). *Riverificato dal loop*: il fondo della
  lista del picker misura `#1E1E1E`, **identico** al fondo della finestra principale nello stesso
  screenshot, testo a 16,67:1; e il picker **funziona ancora** (`Ctrl+O` → `Browse…` → path digitato
  → OK ⇒ `/tmp/r11int` aperto, 2 commit). Finestra principale **pixel-identica** prima/dopo nei due
  temi (0 pixel differenti), quindi lo spill di `SystemRegionBrush` è innocuo.
  **Vicolo cieco circoscritto e documentato: le icone ambra.** Sono `DrawingGroup` hard-coded nelle
  `Resources` **del ControlTheme stesso** sotto la chiave `Icons`, raggiunte con `StaticResource`,
  che si risolve sul parent stack a build time e quel dizionario è il primo elemento: **nessun
  dizionario esterno può vincere**. L'alternativa (replicare ~700 righe di ControlTheme ricostruite
  dall'IL, da rifare a ogni bump di Avalonia) è stata valutata e **non** implementata. Sono contenuto
  non testuale, la soglia 4,5:1 non li riguarda.
  Nota: la via più pulita (`ManagedFileDialogOptions.ContentRootFactory`) è bloccata perché
  `AvaloniaLocator.CurrentMutable`/`Bind<T>` sono `internal` nella reference assembly di 11.3.9.

## ROUND 10 — chiusura della coda

> **Iterazione 1 / 20.** Tre subagent Claude in worktree isolati su file disgiunti (A layout +
> path filter, B albero/menu/toolbar, C clone/console/About/Output), più il cablaggio in
> `MainWindow` fatto dal loop. Base `f01142202`, build Errori: 0 dopo ogni cherry-pick.

**M63** (2026-07-28) — **le nove voci banali della coda round 9 chiuse in una iterazione**: 0.17,
0.33, 0.34, 0.35, 0.36, 0.37, 0.38, 0.39, 1.24. Undici commit
(`22ba751b0`…`55832a7cf`).

- **0.17 — star del layout come proporzioni** (`00938b9c5`). La causa non era il salvataggio in sé:
  il `GridSplitter` di Avalonia **riscrive una star trascinata con la sua estensione in pixel**
  (`3*`/`2*` → `199*`/`525*`), e il rapporto restava corretto. Il danno era il `Clamp(0.1, 1000)`
  **per singolo valore** di `Sanitize`: superati i 1000 su una finestra grande, **un solo lato**
  tornava al default mentre il partner conservava il pixel (`1100`/`400` → `3`/`400`, cioè un
  pannello invisibile). Ora ogni split è una coppia di proporzioni che somma a 1, normalizzata
  **a coppie** in load e save. Migrazione senza stamp di versione e senza euristica "valore > 10 =
  pixel": normalizzare una coppia recupera lo split esatto sia dalle proporzioni (no-op), sia dai
  pesi in pixel legacy, sia dai letterali `3`/`2` originali. *Verificato*: coppia legacy
  `1100`/`400` → `0.7333`/`0.2667` con la griglia al 73% (438 px su 600); drag + chiusura via
  `WM_DELETE_WINDOW` → somma esattamente `1.0`; lo stesso file riaperto a **1350x860** invece di
  1000x700 restituisce il 40,5% (306 px su 756), cioè **indipendenza dalla dimensione**, e
  round-trip byte-identico senza drag. Non verificata a schermo la coppia `DetailStar`/`DiffStar`
  (split view del tab Commit, mai attivata): stesso codice di migrazione.
- **1.24 — "Filter file in grid": era GIÀ cablata** (`6e6e40ac2`). Contrariamente alla voce di coda,
  la voce di menu, `FilterSelectedFileInGrid`, l'evento `FilterFileInGridRequested` **e** la
  sottoscrizione in `MainWindow.cs:1091-1092` esistono da M56, e la guardia di re-entrancy chiusa
  con 0.19 (`_rebinding`/`_rebindQueued`, `SetListItems` unico writer di `ItemsSource`) copre
  questo path **senza estensioni**. Rimossi i commenti che dicevano ancora "scablata perché
  crasha". *Verificato* su un repo costruito ad hoc (6 commit con tocchi per-file noti): il filtro
  su `alpha.txt` riduce la griglia a c5/c3/c1 = esattamente `git log -- alpha.txt`, su
  `sub/gamma.txt` al solo c4, e la ✕ ripristina tutti e 6 senza alcuna
  `InvalidOperationException` nel log.
- **0.33 — "Remotes (n)" conta i remote** (`22ba751b0`): il nodo radice passava `remote.Count`,
  cioè i **branch** remoti. Auditati anche gli altri nodi radice (Branches/Tags/Stashes/Submodules/
  Worktrees): contano già la propria specie, lasciati intatti. *Verificato* su un repo con un solo
  `origin` e 4 branch remoti: `Remotes (1)` con figlio `origin (4)`.
- **0.36 — "Favourite"/"Favorite"** (`cf7cc5107`): upstream è **diviso di proposito** — gli
  identificatori e la chiave persistita sono britannici (`KeyFavouriteHistory = "history-favourite"`,
  173 occorrenze britanniche contro 6 americane), ma l'**unica stringa visibile** è americana
  (`tsmiFavouriteRepositories.Text = "&Favorite repositories"`,
  `StartToolStripMenuItem.Designer.cs:71`), riusata verbatim dallo split-button WorkingDir
  (`WorkingDirectoryToolStripSplitButton.cs:131`). Allineato il testo su **Favorite**; identificatori
  e `favorites.json` **non** rinominati, quindi la compatibilità di lettura è intatta per
  costruzione.
- **0.39 — New branch/New tag senza selezione** (`4c1243005`, `6d04d4b10`): il menu li gatava su
  `_selectedCount == 1`, e lo stato a **zero selezioni** è quello subito dopo l'apertura di un repo.
  Ora una selezione vuota li abilita (una riga **artificiale** li disabilita ancora, e la regola
  bare-repo resta). *Secondo difetto, trovato dal subagent e chiuso dal loop*: `MainWindow` passava
  `"HEAD"` **anche con una revisione selezionata**, quindi "New branch…" su un commit vecchio
  ramificava in silenzio dal tip; upstream usa l'`ObjectId` selezionato
  (`GitUICommands.cs:562`). Nuovo `StartPointForRefCreation()` + flag `_artificialRowSelected`
  (sulle righe artificiali `_lastSelectedHash` conserva il commit reale precedente, che sarebbe
  stantio). *Verificato a schermo*: con `commit two` selezionato il dialogo mostra
  `24bfaafde0b31c8…` e `git log` dopo la creazione dà `24bfaaf (HEAD -> frombtwo)`; senza selezione
  il dialogo di New tag mostra `HEAD`.
- **0.34 — clone nei recenti** (`95d799044`): `CloneDialog.CloneAsync` registra la destinazione
  nella MRU come già fa `InitDialog`. *Verificato con un clone vero* da un bare locale: il repo
  compare in testa a "Recent repositories" e nel file di history; con la hunk rimossa e ricompilato
  (**baseline**) il path era assente sia a runtime sia dopo Start → Exit. ⚠️ Difetto sotto:
  `MainWindow.OpenRepository` chiama `RecordRecentAsync` e **non** basta a far attecchire un repo
  appena clonato — meccanismo dentro il `RepositoryHistoryManager` del core, non inseguito; la
  registrazione lato dialogo lo copre, la scrittura sottostante resta lossy.
- **0.35 — Ctrl+W nel terminale** (`55832a7cf`): la causa **non era il terminale** ma
  `MainWindow.IsGestureOwnedByFocusedView`, che riserva *ogni* gesture alla console mentre
  `_console.IsKeyboardFocusWithin`, e `HotkeyService` installa il proprio handler in **tunnel**:
  il dispatcher vede Ctrl+W per primo, lo declina, e solo dopo arriva al PTY. Aggiunto
  `BrowseCommand.CloseRepository` all'allowlist globale. Il subagent aveva anche provato la strada
  sbagliata (lasciar passare Ctrl+W da `TerminalControl`) e l'ha **revertata**: da sola rendeva
  Ctrl+W un tasto morto (né werase né azione host). *Verificato a schermo*: col caret nella shell
  viva Ctrl+W chiude il repo e riporta alla dashboard (toolbar neutralizzata, menu Dashboard),
  mentre Ctrl+U cancella la riga digitata e Ctrl+C resta al PTY. Zero eccezioni nel log.
- **0.37 — URL in About** (`6f440380d`, `da0e59c38`): nessun troncamento né bug di linkificazione,
  il letterale **mancava lo schema**. Ora `http://p.yusukekamiyamane.com/` (stringa upstream), su
  riga separata dal credito e con `NoWrap`, perché su una sola riga Avalonia mandava a capo subito
  dopo `http://`.
- **0.38 — Refresh del tab Output** (`5109c9951`): scarta il reload throttled pendente, rilegge
  `CommandLog`, ri-renderizza le righe, **rilegge il `Detail` della voce selezionata** (cresce
  mentre il processo gira) e riporta l'ora. *Verificato*: `32 command(s) logged.` →
  `32 command(s) logged — refreshed at 12:49:22.`; il path live resta intatto (32 → 61 comandi
  senza toccare il pulsante) e `_detail.Text` è assegnato solo se cambia, quindi il pannello non
  perde più lo scroll.

*Difetti registrati e non corretti in M63*: la scrittura MRU del core sopra descritta; il singolo
clic su una riga artificiale apre l'intero Commit dialog (upstream vuole il doppio clic) — da
decidere se è una scelta del port; "1 commits" nella status line della griglia (`CommitsNoun` è di
proposito **una sola** chiave di catalogo per non litigare con le traduzioni: servirebbe una chiave
singolare separata); nessun clear **specifico** del path filter (ogni affordance azzera tutti i
filtri, upstream lo tiene dismissibile a parte).

*Non verificato*: la creazione effettiva di un tag; un repository **bare** (la metà bare del gating
di New branch resta verificata solo per ispezione); il flusso Init → recenti (solo letto);
`Ctrl+W` con la sola modifica del subagent (mai spedita).

**M64** (2026-07-28) — **le tre leve architetturali della coda, chiuse in una iterazione**: 1.14b
(righe artificiali), 4.8 (process dialog su PTY), 4.9 (file history sulla griglia vera). Tre
subagent in worktree su file disgiunti + il cablaggio del loop. Quindici commit
(`e66fe23d6`…`4a56e0fc1`).

- **1.14b — contenuto vero per le righe artificiali** (`e66fe23d6`…`155f76bb9`, cablaggio
  `27e7abe87`). `DiffService` ha ora le modalità **worktree** (`git diff`) e **index**
  (`git diff --cached --find-renames`), lista file e testo per-file; l'untracked passa da
  `git diff --no-index -- /dev/null <path>`. `DiffView`/`FileTreeView` hanno `ShowArtificial`,
  `CommitDetailView`/`GpgView` un placeholder che **nomina la riga** ("Commit index is not a commit:
  there is no signature to verify until it is committed."). Il loop ha cablato la dispatch sullo
  **stesso path lazy per-tab** del commit (voce 1.13), quindi carica solo il tab visibile.
  *Verificato dal subagent contro git a mano*: worktree = `D gone.txt, M mod.txt, A untracked`,
  index = `R ren-src→ren-dst, M staged.txt`, patch byte-identiche; su un repo **senza HEAD** git
  2.43 risolve `git diff --cached` da sé (nessun empty-tree fallback: verificato, non assunto).
  *Verificato a schermo dal loop*: riga Working directory → `git diff -- one.txt` con `M one.txt` +
  `A loose.txt`; riga Commit index → `--cached -- newstaged.txt`; File tree su index mostra
  `newstaged.txt @ Commit index` col contenuto **staged** di un file cancellato sul disco;
  placeholder leggibili; zero eccezioni.
  - ⚠️ **Difetto trovato dal cablaggio: i sentinel erano invertiti** (`2e658b153`).
    `RevisionGridView.WorkTreeHash` era `2222…` e `IndexHash` `1111…`, mentre il core ha
    `ObjectId.WorkTreeId = 1111…` / `IndexId = 2222…` (`ObjectId.cs:33,38`) — e il commento sopra le
    due costanti **affermava** che erano identiche byte per byte. Il nuovo `DiffService` deriva dal
    core, quindi mappare per hash mostrava il diff **staged** sulla riga working-directory: visto a
    schermo. Il cablaggio usa ora il `kind` dell'evento, e le due costanti sono state allineate al
    core (dentro la view sono confrontate solo simbolicamente).
  - **Cambio di interazione** (`4a56e0fc1`): il clic **singolo** su una riga artificiale non apre più
    il commit dialog — apriva una finestra sopra il contenuto appena caricato. Upstream non lo fa
    (`FormBrowse` riempie i tab e il dialogo si raggiunge dal pulsante Commit). Ora singolo =
    seleziona, **doppio = dialogo**. Verificati entrambi a schermo.
  - *Divergenza dichiarata*: il File tree della riga working-directory elenca i file **sul disco**,
    non l'index come upstream, perché `GetTreeFiles(ObjectId.WorkTreeId)` del core emette
    `git ls-files --no-cached` — opzione che git **ignora** senza altri selettori, restituendo di
    nuovo l'index (verificato su git 2.43).
  - *Landmine registrata nel core*: `ExecutableExtensions.cs:15` costruisce
    `Lazy<Encoding>(… , isThreadSafe: false)`, quindi **le prime due chiamate git concorrenti** di un
    processo lanciano `InvalidOperationException: ValueFactory attempted to access the Value
    property`. Riprodotto dal subagent con un harness che partiva a freddo. Nell'app non morde (git
    gira sempre prima che una riga sia cliccabile), ma un warm-up di una riga in `Program.Main` la
    chiuderebbe per sempre.
- **4.8 — process dialog su PTY** (`44f95787c`…`ef1f2c2d8`). `PtyProcess.StartCommand` (additivo)
  più `GitStreamRunner.EnterPtyHost`/`IGitPtyHost`: sul flusso interattivo git gira su un PTY, con
  fallback a pipe se il PTY manca. Nuovo `PtyTextBuffer` **line-oriented** (non `TerminalEmulator`:
  una griglia cols×rows fissa avrebbe wrappato/troncato le righe di git — divergenza dichiarata in
  codice), che consuma i `
`, strippa ANSI/CSI/OSC ed estrae la percentuale. Il dialogo ha console
  live, **barra di progresso** e **casella Reply** (mascherata per i segreti).
  *Verificato con run reali*: clone locale `--no-local` di un repo da 900 file → **273 aggiornamenti
  di percentuale durante il run** (primo a 54 ms), console finale **10 righe**, una sola riga
  "Ricezione degli oggetti"; A/B sul path a pipe, stesso clone: **0 aggiornamenti**. Host-key
  `yes/no` (fake ssh su `/dev/tty`) rispondibile dalla casella → fetch completato; **passphrase**
  OpenSSH vera consumata e **mai** comparsa in console (echo tty off, `grep` = 0); credenziali
  HTTPS username+password con Basic auth effettivamente inviata. **Abort**: hook `pre-commit` da
  60 s → 3 processi, `KillAll` in 1 ms, 0 residui, exit 130; con un clean filter lento che tiene
  davvero `index.lock`, **SIGINT lascia git rimuovere il proprio lock** (contro-prova: `kill -9` lo
  lascia lì), e su uno scope senza processi vivi `KillAll` torna **false**, quindi l'unlock resta
  gated. Console tab e path non-streaming senza regressioni. *Verificato a schermo dal loop*: Fetch
  apre il dialogo PTY con comando, esito Success, Reply+Send, Abort disabilitato e **nessuna barra
  finta** quando non c'è progresso da mostrare.
  - **Cambio voluto**: `GIT_TERMINAL_PROMPT=1` (più `SSH_ASKPASS_REQUIRE=never`, `GIT_PAGER=cat`)
    **solo sul path PTY**; il path a pipe resta a `0`. Conseguenza: un'operazione streaming può ora
    **attendere un umano**, visibilmente e con Abort a portata.
  - *Difetto registrato, non corretto*: il rilevamento del fallimento di autenticazione è
    **solo inglese**. Con git in italiano il PTY stampa `fatal: Autenticazione non riuscita per …`,
    che non matcha né `GitProcessDialog.LooksLikeAuthFailure` né i marker di `RemoteService`/
    `PushRefsService`, quindi il fallback al `CredentialsDialog` non si apre (con `LC_ALL=C` matcha:
    verificati entrambi i casi). Preesistente ma **più esposto**: il path vecchio falliva con il
    messaggio non tradotto `could not read Username … terminal prompts disabled`, che matchava.
- **4.9 — file history sulla griglia vera** (`c46dedc23`…`7aaa97f6d`). Nuovo
  `RevisionGridView.LoadFileHistory(repo, path, options)`, fratello con path filter di
  `LoadRepository`; `RevisionFilter` guadagna `FollowRenames`/`ExactRenamesAndCopiesOnly`/
  `FullHistory`/`SimplifyMerges`. Il tab File history è ora un consumatore di una **seconda istanza**
  della griglia (scelta (a)): l'istanza principale porta stato che la file history corromperebbe —
  posizione nella storia, quick filter e `RevisionFilter`, scope dei branch, righe artificiali via
  `SetWorkingState`, view options persistite, cablaggio ai tab inferiori. Arrivano quindi grafo,
  pillole dei ref, multi-selezione e il menu di riga completo.
  - **Scoperta da non riscoprire: `--follow` è fragile, non solo limitato** (git 2.43, misurato). Con
    più ref di partenza (`HEAD --branches --remotes --tags`) o sotto `--topo-order` **tronca in
    silenzio** al commit del rename (3 righe invece di 6), e `--skip` oltre quel commit restituisce
    una **pagina vuota**. Una storia troncata è indistinguibile da una completa, quindi il servizio
    forza un solo commit di partenza in date order e pagina allargando la finestra e scartando la
    testa in memoria invece di usare `--skip`.
  - *Verificato dal subagent* su un repo con merge, branch, rename in sottocartella e path con
    spazi, ogni volta contro `git log`: 6 righe identiche a `git log --follow` con grafo e pillole
    `main`/`side`/`v1`; Shift+clic → range di 4, Ctrl+clic → selezione discontinua di 5; follow
    off → 3 righe (git dice 3), full-history off/on → 4/5; **voce 0.3 intatta** ("Copy path" dà
    `sub/old.txt` prima del rename e `sub/new.txt` dopo); paging attraverso il rename 2 → 4 → 6.
    *Verificato a schermo dal loop*: `one.txt — /tmp/r10loop — 1 commits (current branch)` con una
    riga, uguale a `git log --follow --oneline -- one.txt`.
  - *Disattivati di proposito in questa modalità* (non lasciati come decorazione): scope Branches,
    menu View, Filter avanzato + reset ✕ (il suo campo path litigherebbe con questo), righe
    artificiali.
  - *Difetto lasciato*: con `--follow` git non riscrive i link ai parent, quindi il parent della riga
    del rename è assente e la sua lane esce dal fondo mentre i commit pre-rename aprono una lane
    nuova — discontinuità visibile nel grafo. È l'output di git; upstream ha la stessa lacuna e
    nasconderla vorrebbe dire inventare archi.

*Non verificato in M64*: il file picker "Save as" della file history (serve un portal XDG);
revert/cherry-pick dal menu della file history (mutano il repo, stessi handler di prima); remote di
rete veri e operazioni ricorsive sui submodule per il PTY; `CloneDialog`, che chiama
`GitStreamRunner.Run` diretto e resta sul path a pipe.

**M65** (2026-07-28) — **residuo toolbar 4.10 + passata di leggibilità in tema CHIARO**, più i tre
punti "da display reale" provati davvero. Iterazione 3, due subagent in worktree (uno **ucciso da un
watchdog** a lavoro quasi finito: i cinque commit e il suo `NOTES.md` sono stati recuperati intatti —
la disciplina del NOTES incrementale ha pagato). Nove commit (`e09a4c5b3`…`cbe6df507`).

- **4.10 — quattro priorità su cinque erano GIÀ in base** (M60: `56f36b4c6` shell picker,
  `8ea4081a4` dropdown WorkingDir, `d40fccae6`), e `ToolbarStateService.Classify` porta **tutti e 7**
  gli stati upstream, combaciando riga per riga con `RepoStateVisualiser.Invoke`. Il buco vero era
  uno: i **preferiti categorizzati** (`e09a4c5b3`, `81443dbab`). `FavoritesService` ha ora
  `FavoriteRepo(Path, Category)` + `LoadEntries`/`AssignCategory`/`Categories`/`CategoryOf` con JSON
  **tollerante** (stringa nuda = preferito senza categoria, oggetto = categorizzato; le firme
  `Load`/`Add`/`Remove`/`Contains` non cambiano, le usano `DashboardView` e `MainWindow`), e il
  dropdown WorkingDir raggruppa in un sottomenu per categoria con numerazione che riparte.
  Contratto preso da upstream: **una categoria vuota RIMUOVE il preferito**, perché lì la categoria
  *è* il flag di preferito.
  *Verificato a schermo dal loop*: `1: r9repo` senza categoria in testa, poi ⭐ Experiments e
  ⭐ Work, con i repo dentro. *Verificato dal subagent*: shell picker con le **3 shell realmente
  presenti** (Bash in grassetto, Dash, Sh), corpo di CommitInfoPosition che cicla le 3 posizioni
  **spostando davvero il layout**, "Checkout branch… Ctrl+." in testa al dropdown branch,
  **6 stati su 7** del pulsante Commit da repo veri (incluso dirty-submodules da un repo con
  submodule), Push `1↑ 2↓`, visibilità condizionale dei Worktrees, filtri che arrivano a git.
  Ogni split-button e dropdown è stato **aperto** in uno screenshot (la trappola "Items dentro
  Opening" si vede solo così). *Non verificato*: `RepoState.Unknown`, non provocabile headless.
  *Escluso di proposito*: "Configure this menu…" (upstream `FormRecentReposSettings` riguarda i
  **recenti**, non i preferiti; il port non ha quella pagina → niente pulsante finto) e la UI di
  assegnazione categoria, che upstream mette nel menu contestuale della dashboard.
- **Tema chiaro — la classe di bug M62 era davvero una classe.** Censimento: delle **23** chiavi
  `App.*` lette nel port, **quattro erano lette e mai registrate** (`App.Foreground`,
  `App.PanelBackground`, `App.DiffAdded`, `App.DiffRemoved`) più le sei `App.RepoState*`. Effetto
  misurato in tema chiaro: testo `#DCDCDC` su finestra `#F3F3F3` = **1,24:1** (illeggibile) in tutto
  il `CommitDialog`, righe `+` del diff **1,91:1**, righe `-` **3,10:1**. Registrate con valori
  affini (nessuna tinta nuova): dark identico a quello che le view già dipingevano, light scurito
  come fa `App.GraphGreen`. Dopo: **15,02:1**, **4,58:1**, **5,39:1**.
  - **Decisione richiesta su `App.ConsoleBackground`/`App.ConsoleForeground`: REGISTRARE** (rovescia
    la scelta di M62). I due presupposti di allora non reggono, verificati in codice: il process
    dialog **non legge** quelle chiavi (codifica a mano `#ECE9D8`/`#101010`), e la console del tab
    Console è **già** theme-driven (`App.Text`/`App.Panel`). Delle nove superfici read-only fissate
    da `TextBoxSurface`, sette leggono già le chiavi di tema, una è il beige voluto, e solo queste
    due erano `#111111` fisso. Il contrasto testo-su-fondo **non** discriminava (12,24:1 il
    fallback), ha deciso il resto: il beige dista **1,10:1** dalla finestra chiara → si fonde, ed è
    per questo che lì il fisso non stona; `#111111` distava **17,02:1** → era esattamente la
    "lastra nera in un dialogo chiaro" che la passata doveva cacciare. Ora `#ECECEC`/`#1E1E1E` in
    chiaro (14,11:1) e `#2D2D30`/`#DCDCDC` in scuro (10,01:1); l'identità "terminale" resta portata
    da font monospace e bordo, come già per `OutputView`.
  - **Nove difetti corretti e misurati** (`5419be6d5`, `1c556813b`, `52bcf288e`, `ab420b0b7`,
    `04ffda3cd`): righe diff slavate nel tab Diff (1,88/2,90 → 4,58/5,39); console del Cleanup
    lastra nera (fondo → `#ECECEC`, 14,11:1, e **invariata dopo il clic**: il pinning di M62 regge);
    barra di conferma "will be deleted permanently" a 1,17:1; "Success" del process dialog in
    LimeGreen a 1,91:1; otto `TextBlock` del `CommitDialog` in Gainsboro a 1,24:1; label
    "No repository loaded." in `Brushes.Gray` (3,95:1 → 5,41:1) in quattro view; inchiostri diff
    **duplicati** in `StashPanel` e `PatchDialogs`; stati Aborting/Aborted/Failed in `OrangeRed`
    (3,10:1 → 5,39:1); separatore dell'header blame da `Brushes.Gray` ad `App.Border`.
  - **Sei accenti del pulsante Commit** (`cbe6df507`, fatto dal loop perché cadeva fra i due
    subagent): `MainToolbar` leggeva `App.RepoState*` "con fallback ai valori upstream", ma nessun
    tema le registrava → vinceva **sempre** il fallback, che è tarato sulla toolbar chiara di
    WinForms mentre qui è il **foreground del testo** della caption. Misurati su toolbar chiara
    `#E4E4E4`: da **1,35:1** (Staged) a **3,44:1** (UntrackedOnly), quattro su sei sotto perfino
    3:1. Ora dark = valori upstream, light = stessa tinta scurita a poco più di 4,6:1. *Misurato a
    schermo*: caption `#366887` a **4,74:1** in chiaro, ancora `#87CEFA` a **7,33:1** in scuro.
  - *Coperto e sano* (nessun difetto): chrome e menu aperti, albero, griglia, righe artificiali di
    M64, tutti e nove i tab inferiori (compresa la file history-griglia di M64), e i dialoghi
    Remotes/Submodules/Worktrees/Settings/About/Pull/Push/Cleanup più il process dialog aperto da un
    Pull vero.
  - *Misurati e NON corretti, con motivo* (richiederebbero tinte nuove o di ridisegnare un gruppo,
    fuori mandato): la **palette di syntax highlighting** (5 tinte duplicate in `DiffView` e
    `FileTreeView`, da **1,53:1** a 2,67:1 in chiaro → servono 5 chiavi `App.Token*` × 2 temi, cioè
    progettare un tema di sintassi chiaro: **voce di coda a sé**); la pillola **tag** (3,25:1: fa
    parte della terna delle pillole ref, va rifatta come gruppo); il marcatore ▶ del branch corrente
    e ✔/✚ delle righe artificiali (2,83:1 e 2,67:1, appena sotto la soglia grafica, e
    l'informazione non è veicolata dal solo colore); la **lane arancione** del grafo (2,33:1: è una
    palette categorica di 8 tinte, cambiarne una rompe la distinguibilità che è il suo scopo);
    l'ink **ambra** del prompt PTY (`Goldenrod`, 2,02:1: non esiste una chiave ambra affine da
    riusare); `App.Accent` `#007ACC` su finestra chiara = **4,06:1** (è la tinta d'accento di base,
    usata anche come *fondo*: ridisegnarla è fuori mandato). La palette **ANSI** di
    `TerminalControl` è theme-invariant **per correttezza**: sono i colori SGR che chiede la shell.
- **I tre punti "da display reale", provati e non dedotti.**
  - **Clipboard: FUNZIONA headless**, contrariamente al follow-up storico. `Copy to clipboard →
    Commit hash` mette `a93f54ab201b549b70b59c7cc8d9451857239165`, identico a `git rev-parse`;
    `Copy file path` mette `one.txt`. La vecchia misura "clipboard X11 inerte sotto Xvfb" era un
    altro sintomo della **tabella atomi azzerata** che M58 ha corretto: da rimuovere dalle liste di
    limiti noti. Divergenza trovata di conseguenza: upstream copia il **path assoluto nativo** da un
    **sottomenu** con default in grassetto (`CopyPathsToolStripMenuItem.cs:44-50`), il port copia il
    relativo.
  - **File picker: non si materializza, e ora sappiamo di più.** Un portal XDG è attivo nella
    sessione (`xdg-desktop-portal-gnome`/`-gtk`) e l'app headless eredita
    `DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/1000/bus`, quindi la chiamata è possibile. Cliccando
    `Browse…`: nessuna finestra su `:195`, **nessuna finestra sul display reale `:1`** (ispezionato
    l'albero X), e `RepositoryPickerView.BrowseAsync` non scrive alcun `Error:` — quindi
    `OpenFolderPickerAsync` **è tornata a mani vuote senza eccezione**. Resta non provato che il
    picker funzioni su un desktop vero: servirebbe lanciare l'app sul display dell'utente.
  - **0.16 / `WM_DELETE_WINDOW`**: già chiuso in M58, riconfermato in questa sessione (la "X"
    riscrive `ui-state.json`).
- **Trappola registrata**: `IconLoader.Load` costruisce un URI `avares://` **case-sensitive** e
  restituisce **null in silenzio** → un nome con la capitalizzazione sbagliata non mostra nulla e non
  segnala niente.

**M66** (2026-07-28) — **i due buchi lasciati aperti perché cadevano fra i file di due subagent**:
la parità di "Copy file path" e la UI di assegnazione categoria; più l'audit `IconLoader` e il
residuo di tema chiaro su dashboard e dialoghi minori. Iterazione 4, due subagent in worktree.
Dieci commit (`25ff86f68`…`9ba0a368a`).

- **Copy file path in parità** (`25ff86f68`, `116960ac4`, `68920b030`). Il port copiava il path
  **relativo**; upstream copia il **path assoluto nativo** da un **sottomenu** con il default in
  grassetto (`CopyPathsToolStripMenuItem.cs:44-50`, e `GetFilePaths` unisce le selezioni multiple con
  `Environment.NewLine`). Nuovo `App/Views/CopyPathsMenuItem.cs`: **Copy full path(s) - native**
  (grassetto, `Ctrl+C`, default), *Copy relative path(s) - native*, *Copy file name(s)*.
  *Escluse con motivo*: **WSL/Cygwin**, perché `PathUtil.ToMountPath` riscrive solo una lettera di
  drive Windows e su Linux tornerebbero il path invariato, duplicando il default; e la coppia
  upstream *relative native* / *relative POSIX*, che su Linux è **byte-identica** (il separatore
  nativo *è* `/`) → collassate in una. Correzione alla richiesta del loop: upstream **non ha** la
  variante "solo nome file"; è un'aggiunta, dichiarata come tale.
  *Verificato col round-trip vero* (`xclip -selection clipboard -o`, azzerato prima di ogni prova),
  su un file in sottocartella e su un path **con spazi**: `/tmp/cp1repo/sub/two.txt`,
  `sub/two.txt`, `two.txt`, `/tmp/cp1repo/dir with spaces/three four.txt`, e `Ctrl+C` sulla lista.
  Riverificato dal loop dopo l'integrazione: `/tmp/r10loop/three.txt`.
  - ⚠️ **Trappola nuova, trovata solo guardando lo screenshot**: la voce era **sparita** dal menu
    lasciando uno spazio più alto. Avalonia risolve il `ControlTheme` **per tipo esatto**, quindi una
    **sottoclasse di `MenuItem`** non trova template e si dispone ad **altezza zero**. Serve
    `StyleKeyOverride => typeof(MenuItem)`. La build era verde e il codice sembrava giusto.
- **`IconLoader` non è più silenzioso** (`116960ac4`) e l'audit ha trovato **7 chiamate rotte**
  (`6055d7d63` ne corregge cinque): `Plugins` → `plugin`, `BranchRename` → `Renamed`, il Remove del
  worktree → `DeleteFile`, il Remove del remote → `RemoteDelete`, `ViewFilter` → `FunnelPencil`; la
  voce "User manual" perde l'icona (non esiste alcun asset di help) e si allinea a "Report an issue",
  che già non l'aveva. `GitExtensions` (il logo in About) resta non risolvibile: dipende dalla voce
  0.21, il codice degrada già di proposito. Cambiato anche il comparer della cache da
  `OrdinalIgnoreCase` a `Ordinal`: con ignore-case `"star"` e `"Star"` condividevano una entry
  mentre l'URI li distingue, quindi un futuro errore di maiuscole si sarebbe risolto **o no** a
  seconda dell'ordine di caricamento.
  *La diagnostica si è ripagata subito*: il primo giro di correzioni del loop usava nomi minuscoli
  presi da un listing ripiegato con `tr A-Z a-z`, e il log li ha respinti tutti e tre all'avvio
  (`renamed`, `remotedelete`, `deletefile` → i file veri sono `Renamed.png`, `RemoteDelete.png`,
  `DeleteFile.png`). Senza il log sarebbero state tre icone invisibili in silenzio.
- **Categorie dei preferiti dalla dashboard** (`2e89a1db5`, cablaggio `9ba0a368a`). Sottomenu
  `Categories ▸` nell'ordine upstream (Show in folder / — / Categories / — / Remove / Remove
  missing): le categorie in uso (quella corrente **disabilitata**, non spuntata — upstream non usa
  `Checked` in quel file), `Add new…` con rifiuto inline di nomi vuoti e duplicati, e la voce di
  rimozione. Aggiunto anche il **raggruppamento per categoria nella dashboard**, altrimenti
  assegnare una categoria non produceva alcun feedback sulla pagina stessa che la assegna.
  - **La trappola di upstream è stata rinominata invece che riprodotta**: `AssignCategory(path, null)`
    **cancella il preferito** (`FavoritesService.cs:191-194`), come
    `LocalRepositoryManager.AssignCategoryAsync`, perché la categoria *è* il flag di preferito.
    Upstream lo espone come `(none)`, che non dice cosa fa. Qui la voce è **"Remove from favorites"**,
    ultima e dietro un separatore, e appare solo se la riga è davvero un preferito: stessa
    chiamata, nome che descrive l'effetto.
  - *Escluse*: Rename/Delete category e Clear all recent, che upstream appende al menu contestuale
    dell'**header di gruppo** — qui gli header sono `TextBlock` non selezionabili senza menu, quindi
    è un'unità a sé, non costo zero. Le tre voci `[~]` della 3.3 (Show in folder / Remove project /
    Remove missing) **erano già in base** (`DashboardView.cs:130-143`).
  - *Verificato a schermo dal loop*, tema chiaro: menu e sottomenu leggibili, `Work` disabilitata
    perché corrente, clic su `Experiments` → la riga **si sposta** nel gruppo Experiments,
    `favorites.json` riscritto, status "Filed under …", e la voce Favorite del menu Start non è più
    stantia (il cablaggio `FavoritesChanged`, che il subagent non poteva scrivere).
- **Tema chiaro, residuo** (`26a3a3644`, `f22dfd5ce`, più `bad114ef1`): il **wordmark della
  dashboard** era `#FFFFFF` su `#ECECEC` = **1,18:1** (invisibile) → 14,11:1; la **riga selezionata**
  aveva nome `#007ACC` su `#92C2E8` = **2,39:1** e path 2,86:1 → **8,82:1** entrambi; le ultime due
  superfici read-only (`CommandLogWindow`, `SparseDialog`) non erano fissate e al clic andavano a
  `#000000` → ora stabili. Misurati **sani** e non toccati: Clone, Init, Archive, Sparse, Reflog,
  CommandLog, Maintenance, PluginSettings, RepositoryPicker (testo abilitato 4,87–18,93:1). Trovato
  di passaggio e corretto un difetto non di contrasto: il **quarto pulsante** del
  `MaintenanceDialog` ("Edit .git/config") era **tagliato oltre il bordo e non cliccabile**.
  - *Misurati e non corretti*: placeholder/watermark 3,23–3,93:1 (è
    `TextControlPlaceholderForeground` di Fluent, non una chiave `App.*`, su input **editabili** dove
    M62 ha deliberatamente lasciato le affordance di focus); controlli **disabilitati** (WCAG 1.4.3
    li esenta); l'antialiasing del testo a 11px (pixel più scuro reale 3,95:1 contro i 4,58:1
    nominali: artefatto di rasterizzazione, il brush nominale passa).
- **`AddNotesDialog` è irraggiungibile, e non solo headless**: il suo unico punto di costruzione è
  `CommitDetailView.EditNotes()`, che è `public` ma ha **zero chiamanti** (il commento in loco dice
  "unwired is harmless"). Dichiarato, non dedotto: la finestra non è mai stata vista renderizzata.
  Va cablata (la voce 1.10 dava `AddNotes` Ctrl+Shift+N per fatta: da riverificare).

### Blocco PANNELLO INFERIORE (round 8) — i pulsanti che mancavano nei tab

> **Iterazione 2.** Nata da un'osservazione dell'utente ("mancano i tasti delle sezioni inferiori,
> per esempio dentro Diff") e da un **audit di parità tab per tab** contro i controlli upstream.
> Scoperta metodologica: `RevisionFileTreeControl` **non esiste** in questo repo — il tab "File tree"
> di `FormBrowse` usa la *stessa* classe del tab Diff (`RevisionDiffControl`, `FormBrowse.Designer.cs:76`,
> bindata in modalità albero a `FormBrowse.cs:331`) e anche `FormStash` usa un `FileStatusList`
> (`FormStash.Designer.cs:38`): la toolbar della lista file è **un solo componente per tre posti**.

**M51** (2026-07-27) — sei commit.

- **Diff** (`84310c5cc`) — nuovo `App/Views/FileStatusListView.cs` **riusabile** (più
  `Services/DiffFileListBuilder.cs`, `DiffSyntaxHighlighter.cs`, `DiffViewerOptions.cs`): toolbar della
  lista file con filtro **regex** (watermark, clear, contatore `n/m`, debounce 220 ms; pattern non
  valido → match literal, contatore rosso `⚠ n/m` e messaggio del parser nel tooltip), raggruppamento
  **per path / estensione / stato** (i primi come albero di cartelle vero), split-button flat/tree,
  collapse/expand all, refresh reale. Nel viewer: **evidenziazione della sintassi** (scanner per riga,
  ~15 famiglie di linguaggi, stesso tetto di 20 000 righe della ricerca; con l'evidenziazione attiva le
  righe +/- prendono una tinta di fondo per non perdere l'identità), **copia versione nuova/vecchia**
  (lato vecchio = `<sha>^`/`BASE`/commit per i confronti col working tree, quindi corretta anche sotto
  `-w`/`--word-diff`) e i tre toggle `--ignore-space-at-eol` / `-b` / `--text`.
  Due difetti reali trovati durante la verifica GUI e corretti: la barra del viewer sbordava a destra
  (ora `WrapPanel`) e il bordo rosso del regex non valido era mascherato dal focus border di Fluent
  (spostato sul contatore). **Omessi con motivo**: toggle ignorati/skip-worktree/assume-unchanged/
  untracked (il `DiffService` cabla `untrackedFilesMode: No` e `excludeSkipWorktreeFiles: true`,
  nessun dato dietro → nessun pulsante finto), filtri `!`/`A`/`B`/`=` (il port non ha i dati del diff
  combinato A/B), blocco `git grep`, dropdown MRU del filtro (la ComboBox di Avalonia non è editabile),
  `tsmiDenseTree`/`ShowGroupNodesInFlatList`, scroll continuo e difftastic.
  *Non verificato*: il click-through di "Copy new version" (il path di lettura è lo stesso di "Save as").
- **Commit** (`1d63c9fc0`) — menu contestuale che mancava del tutto: **Copy commit info**, **Copy link**
  (visibile solo col puntatore su un hash parent/child, header dinamico con lo short hash), i **sei
  toggle** di visibilità con gli id e i default upstream (local branches / tags / messaggi dei tag
  annotati / derives-from attivi, i due remote no), applicati live e persistiti, e **Add notes** che
  funziona davvero senza editor esterno (dialogo multiriga in-app + `git notes add -f -F -` da stdin;
  testo vuoto rimuove la nota). L'originale **non ha una toolbar** in questo tab: non è stata inventata.
  *Deviazione consapevole*: i toggle vivono in un **`commit-info.json` separato**, non in `UiState`,
  perché `MainWindow` tiene una sola istanza di `UiState` e la riserializza interamente alla chiusura →
  una scrittura da una view che non condivide quell'istanza verrebbe annullata all'uscita. Sostituibile
  passando l'istanza condivisa alla view. `CommitDetailView.EditNotes()` è pubblico per la hotkey
  `AddNotes`, non ancora cablata.
- **Blame** (`a0b88da0d`, cablaggio `d79c26344`) — menu contestuale completo (Blame this revision,
  Blame previous revision con **header dinamico**, Show changes, Copy to clipboard ▸ hash/message/all
  info), **pannello dettagli** del commit sopra la griglia che riusa `CommitDetailView` senza
  modificarla (splitter, come lo `splitContainer1` upstream), e **tooltip per riga** col testo di
  `GitBlameCommit.ToString()`. Il tasto destro seleziona anche la riga sotto il puntatore (`PointerPressed`
  in tunneling con `handledEventsToo`), altrimenti la `ListBox` agiva sulla selezione precedente.
  `BlameService` guadagna `CommitHash`/`Summary`/`Details`/`OriginFileName` **dalla stessa passata** di
  `git blame --porcelain` (gli unici comandi in più sono un `rev-parse` per load e uno per il parent).
  `ShowChangesRequested` e `CommitNavigated` cablati in `MainWindow`. *Bug corretto strada facendo*: il
  template di riga riciclava i container (un container riciclato riceve solo un nuovo DataContext →
  testo e tooltip stantii allo scroll); disabilitando il riciclo è emerso un **crash** perché Avalonia
  re-invoca il template con item **null** quando svuota un container. Limite: l'header "previous
  **visible** revision" è un'approssimazione, la view non ha una grid da interrogare.
- **File history** (`89fe2d8e3`, cablaggio `9d0664090`) — menu contestuale (Copy to clipboard ▸ con
  anteprima del valore negli header come il `CopyContextMenuItem` upstream, Save as, Manipulate commit ▸
  Revert / Cherry pick) e i quattro switch: **Detect and follow renames**, la variante **exact renames
  and copies only**, e il dropdown **Show Full History** con **Simplify merges** (disabilitato mentre
  full history è spento, come upstream). Il nuovo record `FileHistoryOptions` li traduce nei **soli
  flag di `git log`** (`--follow`, `--find-renames --find-copies` o la variante `"100%"`,
  `--full-history`, `--simplify-merges`) e un toggle ricarica; verificate tutte e sei le combinazioni
  contro `git log` reale. Revert e cherry-pick riusano `RevertArchiveService.Revert` e
  `StashOpsService.CherryPick` e ora passano da `RunOp`, quindi prendono la sospensione del watcher e il
  refresh. Upstream li persiste in `AppSettings.*InFileHistory`, qui restano session-local come gli
  altri toggle di view. *Bug corretto*: il tasto destro su una riga muoveva la selezione → `RevisionSelected`
  → `MainWindow` spostava il pannello sul tab Commit, e il menu si apriva su una vista che gli sfuggiva
  da sotto; ora la notifica all'host è soppressa per quel solo dispatch.
- **Output** (`ca1725e4a`) — da blob di testo a **lista di comandi selezionabile** con pannello
  dell'output completo sotto (splitter, come il `LogOutput` di `FormGitCommandLog`), menu contestuale
  **Save to file / Copy full command line / Clear** e toggle **Word wrap**; un refresh mantiene
  selezionata l'ultima riga se l'utente non ne ha scelta un'altra. Omessi: tab "Command cache" (serve la
  cache del core, non esposta), `chkAlwaysOnTop` (è un tab, non una finestra), `chkCaptureCallStacks`.

**Dove l'audit ha stabilito che NON c'è lavoro** (per non inventarlo): **Console** — upstream non ha
toolbar né menu propri, il contenuto è il controllo di ConEmu, e il port è già più ricco (restart shell,
apri terminale); **GPG** — `RevisionGpgInfoControl.Designer.cs:31-35` non ha **nessun** pulsante, solo
due textbox; differiscono soltanto layout (sezione separata per la firma del tag) e icone di stato;
**Commit** e **Blame** — nessuna toolbar nell'originale, solo menu contestuali (le "Blame options"
appartengono a `FormFileHistory`, non al tab Blame).

**Restano aperti dal pannello inferiore**: **Stash** (checkbox "Keep index" banale; lista file dello
stash → prerequisito di "Stash selected changes"), **File tree** (albero vero + anteprima del contenuto:
deve consumare `FileStatusListView`), le icone di stato firma e la firma del tag nel GPG, e i rinviati
che richiedono infrastruttura assente (`git grep` nei file del commit, le 11 "Blame options", tab
"Command cache", `FilterToolBar` completa, i 4 tab interni della file history).
`FileStatusListView` è la superficie che `FileTreeView`/`StashPanel` devono consumare (`SetFiles`,
`SelectedFile`/`SelectedFileChanged`, `RefreshRequested`, `List.ContextMenu`, `AddToolbarItem`); le sue
opzioni di raggruppamento stanno in `FileStatusListOptions.Session`, non persistite.

### Blocco PRIORITÀ P1–P3 (round 8) — grafo, chrome, Pull

> **Iterazione 1.** Le tre priorità indicate dall'utente il 27/07/2026 sono **CHIUSE** nella
> portata concordata (P2 limitata a barra pulsanti + ricerca e icone dei tab; i punti 2c/2d
> restano aperti). Tre subagent in parallelo su file disgiunti, più un giro di fix su P1.

**M50** (2026-07-27) — sei commit.

- **P1 grafo** (`7dc5cc943`, fix `cea46f2d9`) — evidenziazione **fedele all'originale** in
  `RevisionGridView.cs`. I "relatives" ora sono **l'ancora + i suoi soli antenati** (upstream
  `HighlightBranch` → `MakeRelative`, che risale i parent): il walk sui *children* che il port
  faceva è stato rimosso. Nuovo `_highlightAnchor` (null = HEAD): all'avvio l'ancora è HEAD e
  `_drawNonRelativesGray` è ora `true` come upstream, quindi il grigio si vede subito; **Alt+clic**
  ri-ancora sulla riga sotto il puntatore (righe artificiali → HEAD), il **clic normale non
  cambia nulla**. Handler in `Tunnel` con `handledEventsToo` che **non** marca l'evento handled,
  quindi selezione/multi-selezione/menu contestuale/doppio clic sono intatti; la riga si risolve
  da qualunque punto (`GetSelfAndVisualAncestors` fino a `_list`, fallback per hit-test della Y su
  `GetRealizedContainers()`, scrollbar esclusa). **Ora sono grigie anche le LANE**, non solo il
  testo: `ComputeGraphRelatives()` produce per ogni riga un flag di nodo più un flag per segmento
  (nell'ordine in cui `WithHeadConnector`/`ArtificialSegments` li costruiscono), portando il flag
  *giù* per le lane; `RevisionGraphControl` riceve `relativeSegments`/`relativeNode`/
  `nonRelativeBrush` (`B("App.TextDim")`, nessun colore hardcodato) e disegna prima le grigie, così
  le colorate vincono le sovrapposizioni (come `GraphRenderer` con il suo `OrderBy(IsRelative)`).
  Extra: voce "Highlight current branch's history" nel flyout View che ri-ancora a HEAD.
  Due fix collaterali necessari a renderlo visibile: `RebindRows` assegna una **nuova** lista
  invece di `_rows` (con la stessa istanza il pannello virtualizzante teneva i container già
  realizzati → le righe visibili restavano con i visual vecchi; colpiva anche gli altri toggle
  basati su `RefreshView`), e il ripristino dello scroll è riapplicato a
  `DispatcherPriority.Background` (al primo tentativo l'extent è ancora corto e viene clampato).
  Verifica GUI su repo di topologia nota (`A-B-C-D` + `S` che stacca da `B`): all'avvio `S` grigio
  e la linea di HEAD colorata; Alt+clic su `B` → `B` e `A` colorati, `D`/`S`/`C` grigi in **lane,
  nodo, hash e subject**. *Limite*: la relatività dei segmenti è dedotta dal bookkeeping delle lane
  (i `RevisionGraphSegment` del port non portano hash), quindi per un parent di merge che **riusa**
  una lane già aperta i due flag vengono OR-ati e quella lane può restare colorata dove l'upstream
  disegnerebbe due segmenti distinti; lane a figlio singolo esatte. Avalonia non muove la selezione
  su Alt+clic (Alt non è un modificatore di selezione) mentre la griglia Windows seleziona e
  ri-ancora in un colpo: lasciato così per non disturbare multi-selezione e range-diff.
- **P2 chrome** (`5e1bd9524`, `57b2627f9`) — `RepoObjectsTree` non è più il solo `TreeView`: griglia
  a 3 righe con **barra pulsanti** (collapse-all + un toggle per categoria nell'ordine del
  `leftPanelToolStrip` upstream: local/remote/worktrees/tag/submodule/stash, icone
  `CollapseAll`/`LocalBranchRoot`/`RemoteBranchRoot`/`WorkTree`/`TagHorizontal`/`FolderSubmodule`/
  `stash`, tutte già presenti; `WrapPanel` su bordo `App.Toolbar` così la colonna stretta va a capo
  invece di tagliare) e **casella di ricerca** con la lente upstream (`Images.Preview`): filtro
  incrementale case-insensitive che pota ai match più i loro antenati (espansi), un nodo che matcha
  tiene tutto il sottoalbero, Escape pulisce, Enter e la lente ciclano la selezione fra i match come
  la coda rotante upstream; le foglie ref matchano sul **nome completo** (`FullPath`), quindi
  `avalonia` trova `origin/linux-avalonia-port` anche se l'etichetta è accorciata dal gruppo remoto.
  Key handling in tunneling con `handledEventsToo` perché il `TextBox` non mangi Enter/Escape.
  I nove **tab del pannello inferiore** hanno le icone con lo stesso mapping di
  `FormBrowse.InitCommitDetails` (Commit→`CommitSummary`, Diff→`Diff`, File tree→`FileTree`,
  GPG→`Key`, Console→`Console`, Output→`GitCommandLog`; i tre tab solo-port su
  `stash`/`Blame`/`FileHistory`), costruite dal nuovo `App/Views/IconText.cs` con fallback al
  caption nudo se l'icona manca. Nessun nuovo evento da cablare: Enter-per-selezionare passa dal
  `RefSelected` esistente. *Limiti*: stato dei toggle e testo del filtro **session-local** (il port
  non ha l'equivalente di `AppSettings.RepoObjectsTreeShow*`); i conteggi di categoria restano
  quelli non filtrati; nessun autocomplete sotto la casella; divergenza voluta dall'upstream, che
  tinge i match senza nascondere nulla, mentre qui si filtra.
- **P3 Pull** (`ece3f9810`, `11813e897`, cablaggio `735d29ace`) — il pulsante secco è diventato uno
  **split-button**: il corpo esegue l'azione predefinita persistita, la freccia apre il menu
  upstream (`Open pull dialog… Ctrl+Down` | Pull - merge | Pull - rebase | Fetch | Fetch all |
  Fetch and prune all | --- | `Set default Pull button action ▸` con i radio sull'attuale). Enum
  riusato dal core (`GitExtensions.Extensibility.Git.GitPullAction`), default persistito in
  `UiState.DefaultPullAction` (default `Merge`, sanificato al load) — verificato: scelta "Pull -
  rebase" → `"DefaultPullAction": "Rebase"` nello stato e icona rebase sul corpo dopo riavvio.
  Nuovo `App/Views/PullDialog.cs` su modello `FormPull`: Pull from (Remote + Manage remotes | URL +
  Browse), Branch (local read-only + remote), Merge options (merge / rebase / solo fetch), Tag
  options (follow tagopt / no tag / all tags), Prune remote branches, Prune remote branches and
  tags, e in fondo Solve conflicts | Stash changes | Auto stash | Pull; l'illustrazione upstream è
  omessa. Remote e branch caricati in `Task.Run` e passati al costruttore (i service bloccano
  sync-over-async: chiamarli dall'UI thread congela tutto — bug M43). `RemoteService` ha ora
  `PullOptions` (azione, remote o URL, branch remoto, tag policy, prune, prune-tags, autostash,
  unshallow), `PullStreaming(repo, PullOptions, …)`, `FetchAllStreaming`,
  `FetchAndPruneAllStreaming`; la vecchia firma `PullStreaming(…, bool rebase, …)` resta e delega,
  così i chiamanti storici compilano. **I due `rebase: false` cablati sono spariti**: il corpo del
  toolbar passa da `RunPullAction(action)`, `Commands → Pull` e `Ctrl+Down` aprono il dialogo.
  Corretto anche l'inverso rispetto all'upstream nelle hotkey: `PullOrFetch` (Ctrl+Down) apre il
  dialogo (`DoPull(isSilent: false)`), `QuickPullOrFetch` (F8) è il clic del pulsante, cioè
  l'azione predefinita, `QuickPull` è un merge silenzioso. *Limiti*: l'autostash usa `--autostash`
  di git invece dello stash/pop separato dell'upstream; il local branch è read-only quindi non si
  genera mai un refspec locale; "Solve conflicts" non ha un `FormResolveConflicts` portato e lancia
  il merge tool configurato per ogni path in conflitto; `Unshallow` esiste in `PullOptions` ma non
  ha checkbox; Avalonia rende la gesture come "Ctrl+Down Arrow"; le hotkey mostrate vengono dai
  default, gli override utente non sono riflessi.

**Restano aperti di P2** (fuori portata di questa iterazione, erano i punti 2c/2d): toolbar in alto
con gli altri pulsanti/split-button dell'originale, toolbar ricca della lista file (raggruppamento
per path/estensione/stato, ricerca, toggle ignorati/skip-worktree/untracked —
`FileStatusList.Toolbar.cs`) e opzioni del viewer diff (evidenziazione sintattica, copia versione
nuova/vecchia — `FileViewer.Designer.cs:27-48`).

### Blocco FEATURE E INTEGRAZIONE GUI (round 7)
> **Iterazioni 1–3.** Direzione data dall'utente: **le lingue non interessano oltre inglese
> e italiano** (blocco traduzioni chiuso), contano **feature e integrazione nella GUI**.
> Le unità nascono da un audit di parità *funzionale* (non di voci di menu) fra
> `src/app/GitUI` e il port: la checklist di PORTING contava le voci, non la profondità.

**M47** (2026-07-27) — sette commit. Le lacune chiuse erano tutte "la voce c'è ma fa meno":

- **F1** (`34ae54d0b`) — **la storia non si fermava più a 200 commit**. `RevisionService`
  aveva `maxCount = 200` come default e nessun chiamante lo cambiava. Ora
  `LoadRevisionPage(repo, skip, maxCount)` → `RevisionPage(Rows, HasMore)` senza geometria,
  con `BuildRevisionGraph` sull'intera lista accumulata e metadati (HEAD, ref, note) messi
  in cache per non rifare `for-each-ref` a ogni append; pagine da 500, append su scroll +
  pulsante "Load 500 more", dimensione pagina nel menu View. Su git_ext_mod (17 862 commit):
  prima pagina 57 ms, append 27–34 ms, e page(0,500)+page(500,500) è byte-identica a una
  singola camminata da 1000. In più: **doppio clic/Enter sulla revisione** (eventi
  `RevisionActivated`/`ArtificialRowActivated`) e **cronologia di navigazione** `Alt+←/→`.
  *Bug pre-esistente scoperto e corretto*: il key handler della grid era in **bubbling**,
  dove la `ListBox` aveva già consumato le frecce → `Alt+↑/↓` si comportavano da frecce
  semplici e il salto a parent/child **non funzionava affatto**; ora è tunnelling con
  `handledEventsToo`.
- **F2** (`ccaf63a40`) — **auto-refresh**: `RepositoryWatcherService` modellato su
  `GitStatusMonitor`, due `FileSystemWatcher` (work tree + git-dir separata solo quando sta
  fuori dal work tree), git-dir risolta anche per i worktree collegati (`gitdir:` +
  `commondir`), debounce 1 s ricaricato a ogni evento con **tetto a 4 s**, pavimento di 5 s
  fra refresh e rete di sicurezza ogni 5 minuti; rumore ignorato (`*.lock`, `objects/**`,
  `COMMIT_EDITMSG`, …). **Due guardie anti-loop**: ogni comando git dell'app gira dentro
  `Suspend()`, e `RefreshAll()` chiude con `NotifyRefreshed()` — così nemmeno un refresh che
  tocca l'index può innescare il successivo. Verificato: commit da shell → GUI aggiornata in
  ~1 s **senza F5**; `checkout` di un branch da 1500 file → **un solo** refresh. Esaurimento
  inotify provato davvero (occupate tutte le 8085 istanze): niente crash, messaggio di
  degrado e timer a 60 s. Più drag&drop di una cartella e persistenza di posizione/
  massimizzato/tab attivo con **clamp allo schermo**.
  ⚠️ **Avalonia 11.3 non implementa XDND su X11**: non interna nemmeno l'atomo `XdndAware`,
  quindi `DragDrop.DropEvent` non può scattare su Linux (risolto upstream in 12.1, non
  backportato). Serve `App/Services/X11DropTarget.cs`, ricevitore nativo che pubblica
  `XdndAware`/`XdndProxy` e risponde al protocollo su una connessione X propria.
- **F3** (`925be2385`) — nel diff: **ricerca `Ctrl+F`** incrementale con evidenziazione a
  `Run` e contatore (F3/Shift+F3), soppressa oltre 20 000 righe o 2 000 hit con la
  navigazione ancora attiva; `Ctrl+G` vai-a-riga; righe di contesto `-U<n>` e "file intero";
  menu del file da 5 a 10 voci (apri nell'editor, apri questa revisione, mostra nella
  cartella, salva come, copia patch). Editor risolto come fa git (`GIT_EDITOR` → `core.editor`
  → `$VISUAL` → `$EDITOR`, editor console avvolto in un terminale), cartella via
  `org.freedesktop.FileManager1.ShowItems` con fallback `xdg-open`.
- **F4** (`dff0598c9`) — **checkout che non ignora più le modifiche locali**: prima
  `LocalChangesAction.DontChange` era hard-coded e con l'albero sporco il checkout falliva
  con l'errore grezzo di git. Ora albero pulito → nessun dialogo; albero sporco →
  `CheckoutBranchDialog` con *Don't change / Merge / Reset / Stash*, spiegazione di cosa
  succede alle modifiche, default persistito in `SettingsService` (Reset mai memorizzato,
  come upstream). Più `CreateBranchDialog` (checkout-after-create) e `CreateTagDialog`
  (annotato/firmato/force/push).
- **G1** (`2e9d981eb`) — **staging per hunk e per righe**, la lacuna singola più pesante:
  senza, il dialogo non sostituiva `git add -p`. `PatchManager` del core **riusato tal quale**
  (è .NET puro e già referenziato: contiene sub-chunk, ricalcolo dei contatori `@@`,
  `\ No newline at end of file`, header dei file nuovi/rinominati); il nuovo
  `PatchStagingService` è solo raccordo. Selezione a granularità di riga con **due sistemi di
  coordinate** (render vs sorgente, perché il `\r` sparisce a video ma deve restare nella
  patch). Ogni operazione verificata byte-esatta contro `git diff --cached`/`git diff`/il file
  su disco, CRLF compreso. *Trappole Avalonia scoperte*: `SelectableTextBlock` ingoia il tasto
  destro (`ContextRequested` non scatta mai → menu aperto da `PointerPressed` in tunneling) e
  un clic semplice non muove il caret (→ hit-test sul `TextLayout`). Non supportati e
  dichiarati: staging parziale di una cancellazione (git lo rifiuta, come upstream), file
  untracked, discard dal lato staged.
- **G2** (`7899fde12`) — **il filtro lo fa git**, non più una substring in memoria su quattro
  campi. `RevisionFilter` → argomenti `git log` (`--author`/`--committer`/`--grep`, `-S`/`-G`,
  `--since`/`--until`, path **dopo `--`**, `--no-merges`, `--first-parent`, case/regex, limite
  applicato attraverso le pagine), nuovo `RevisionFilterDialog`, paginazione filtro-consapevole,
  indicatore in barra di stato e reset a un clic. **`--parents` è il dettaglio che tiene in
  piedi il grafo**: riscrive `%P` verso gli antenati sopravvissuti, così il DAG filtrato resta
  connesso. Correttezza provata su **dieci** casi GUI vs `git log --oneline … | wc -l`, tutti
  esatti (autore 179, path 62, `-S` 50, nessun filtro 17461). La casella `Filter:` rapida
  resta **in memoria di proposito**: la sua semantica è un OR fra autore/subject/hash che git
  non esprime in una invocazione, e filtrare a ogni tasto significherebbe un processo git per
  carattere. *Bug corretto in corsa*: il DAG filtrato arrivava a ~30 lane e spingeva le colonne
  fuori dal pannello → lane limitate a 8.
- **Integrazione del loop** (`1450e0d65`, `a335216b5`) — cablati i punti che le unità non
  potevano toccare: doppio clic della grid → tab dettagli; i tre percorsi di checkout ancora
  nudi (dropdown toolbar, "Checkout this commit", `ReflogWindow`) → dialogo F4; i quattro
  punti di creazione branch/tag → dialoghi F4; voci **Visualizza → Filtro avanzato / Reset dei
  filtri di revisione**. Nota: in `MainWindow` `LocalChangesAction` va qualificato
  (`GitCommands.LocalChangesAction`) perché `CommitInfoPosition` esiste sia nel core sia nelle
  view del port.

**M48** (2026-07-27, iterazione 3 del round 7) — le due lacune di interazione rimaste:

- **H1** (`66a5ae1c8`) — **da 2 scorciatoie a ~30**. Nuovo `Services/HotkeyService.cs`: enum
  `BrowseCommand` speculare a `FormBrowse.Command`, tabella di default **copiata da
  `HotkeySettingsManager.cs:216-265`** (non inventata), override da
  `$XDG_CONFIG_HOME/GitExtensions.Avalonia/hotkeys.json`, `Bind`/`Display`/`Save` pronti per
  una futura UI di configurazione. Wired: Ctrl+Space commit · Ctrl+↑/↓ push/pull ·
  Ctrl+Shift+↓ e F8 fetch · Ctrl+. checkout · Ctrl+B branch · Ctrl+T tag · Ctrl+W chiudi ·
  Ctrl+, impostazioni · Ctrl+E filtro · Ctrl+0…6 e Ctrl+9 focus pannelli · Ctrl+Tab tab ·
  Ctrl+Alt+↑/↓ stash/pop · Ctrl+Shift+F find file · Ctrl+Alt+C toggle pannello sinistro ·
  Ctrl+F ricerca nel diff **da qualunque focus** (era locale alla view).
  *Correzioni rispetto all'elenco che avevo dato io*: i focus pannelli sono Ctrl+0…7 **più
  Ctrl+9**, Ctrl+7 è FocusBuildServerStatus che qui non esiste; `Ctrl+.`/`Ctrl+,` sono
  `OemPeriod`/`OemComma`. La tabella upstream ha vinto sul brief, giustamente.
  **Priorità contro le view**: dispatcher in tunneling con `handledEventsToo` + predicato di
  riserva che replica l'ordine di `FormBrowse.ProcessHotkey` — grid col focus tiene
  Ctrl+C/Alt+frecce/F3, il diff tiene Ctrl+F/Ctrl+G/F3, la console PTY tiene tutto tranne F5 e
  i comandi di focus (altrimenti non se ne esce da tastiera).
  *Due bug trovati dagli screenshot e corretti*: `FocusInto` prendeva il primo controllo
  focalizzabile, che era un **Button** di intestazione (Ctrl+1 poi Ctrl+Space premeva
  "Filter…"); e la `ListBox` di Avalonia **non è focalizzabile** (lo sono i container), quindi
  Ctrl+1 finiva nella casella di ricerca.
- **H2** (`9c8e18bc8`) — **menu contestuale della grid** da lista piatta sempre abilitata a
  menu con sotto-menu e predicati: `Copia nella clipboard ▸` · checkout/merge/rename/delete
  per ref (3 slot fissi per tipo, caption riscritta) · `Reset del branch corrente ▸` ·
  `Reset di un altro branch a questo punto…` · create branch/tag · revert/cherry-pick/archive ·
  `Avanzato ▸` (reword/squash/fixup) · `Confronta ▸` · `Naviga ▸` · `Other actions ▸`.
  Predicati su: riga artificiale vs commit, selezione singola vs multipla, `IsHead`, branch
  corrente, e **tipo di ref da una mappa nome→tipo aggiornata fuori thread**, non da euristiche
  sul testo. Vincolo rispettato: Items creati una volta, in `Opening` solo
  `IsVisible`/`IsEnabled`/`Header`; i sotto-menu senza figli visibili si nascondono e i
  separatori collassano come in `UpdateSeparators()`. Otto operazioni nuove funzionanti
  (merge, rebase su commit, reset di un altro branch, rename/delete branch, delete tag,
  checkout branch, confronto di due commit), verificate da CLI: tag `v0.9 v1.0`→`v0.9`,
  `topic df5b5cc`→`73515be`, merge con due parent, rename `feature`→`featuremaster`, delete con
  doppia conferma se git dice "not fully merged". Un merge è **fallito correttamente** perché
  git l'ha rifiutato: l'app non ha forzato nulla. `AddCommitCommand` resta compatibile (le voci
  note vanno nei sotto-menu, le altre in "Other actions").
  Lasciate fuori con motivo: push del branch (credenziali in `MainWindow`), edit commit e
  rebase interattivo (serve un harness `GIT_SEQUENCE_EDITOR` che non esiste), apply/pop/drop
  stash (la grid non cammina i commit di stash, le voci non avrebbero bersaglio).

**M49** (2026-07-27) — **bug segnalato dall'utente**: «scorrendo nella lista dei commit, a un
certo punto la lista si refresha da sola e torna in cima». Corretto in `be80f3dec`
(`RevisionGridView.cs`). Causa **principale**: `MainWindow.RefreshAll()` chiama
`LoadRepository` sul repository *già aperto*, che eseguiva `Reload()` — bump di generazione,
`_loaded = []`, `_scroll = null`, walk ripartito da pagina 1: quindi non solo perdeva la
posizione, **buttava via tutte le pagine** caricate scorrendo. Da M47/F2 il watcher lo
scatena a ogni modifica di file. Colpevole **secondario**: `ApplyFilterCore` ribindava
`ItemsSource` senza preservare viewport e selezione (esce presto se i contatori non
cambiano, quindi da solo non spiegava il sintomo). **Terzo, mai notato**: il timer di 5 s che
pulisce il messaggio di stato ribindava anch'esso.
Correzione: un `LoadRepository` sullo stesso repo è ora un **refresh a pari profondità**
(niente unbind, niente pagine perse) e tutti i rebind passano da `RebindRows(preserveViewport)`.
Rebind classificati: legittimi (cambio repo/scope/page size/filtro esplicito dell'utente →
tornare in cima è corretto), non legittimi (`SetWorkingState`, cambio lingua, append,
flash di stato, `RefreshView` per modalità data e colonne → preservano). Corretto anche un
off-by-one esposto dal refresh a pari profondità: `HasMore` significa "la pagina è tornata
piena", quindi chiedere esattamente la profondità caricata faceva riapparire un footer
"Load 500 more" fantasma → si chiede profondità+1 e si taglia.
Verificato headless riproducendo il bug **prima** (lista in cima, selezione persa, dettagli
stantii) e mostrandolo risolto **dopo**, anche nel caso duro: due pagine caricate, selezione
oltre il confine dei 500, e transizione pulito→sporco che *inserisce* la riga artificiale.

**Difetti noti raccolti in questo blocco, non ancora chiusi**: un refresh in background
(`SetWorkingState` → `ApplyFilterCore`) ribinda `ItemsSource` e **perde la selezione**
dell'utente pochi secondi dopo l'avvio; `CheckoutBranchDialog` è misto italiano/inglese (le
descrizioni delle opzioni non hanno `trans-unit`); Avalonia non espone `WM_DELETE_WINDOW`,
quindi la finestra non è chiudibile dal window manager; il "salva come" del diff non è
verificabile headless (serve un portal XDG — follow-up 5); il pannello dettagli non si
aggiorna dopo un cambio di ref dal menu della grid (mostra per un attimo "Contained in tags"
stantio); `SelectRefInLeftPanelRequested` è esposto ma non ancora cablato; il commit dialog
non si chiude con Escape. *(Il `Ctrl+F` locale alla view è stato risolto da M48/H1.)*

### Blocco FOLLOW-UP RESIDUI (round 6) — traduzioni, header grid, strascichi M45
> **Iterazione: 1 / 15.** Tre unità in parallelo su file disgiunti (T1 traduzioni,
> T2 header grid, T3 CommitDialog).

**M46** (2026-07-27) — tre commit di feature:

- **T1** (`56023619a`) — **infrastruttura di traduzione**, il pezzo che mancava da D11.
  Scelta architetturale: del motore del core si riusa **solo la metà bassa**
  (`Translator.GetTranslation`, `TranslationSerializer.Deserialize`, .NET puro, funziona
  su Linux); la metà alta (`ITranslate.TranslateItems` → `TranslationUtil`) riflette su un
  albero di `System.Windows.Forms.Control` e matcha i **nomi dei campi del designer**, cioè
  è inservibile per view Avalonia fatte di letterali inline. Il nuovo
  `App/Services/TranslationService.cs` sostituisce quindi solo il matcher: indicizza ogni
  `trans-unit` **due volte** — per id (`FormBrowse/commitToolStripMenuItem.Text`) e per
  `<source>` inglese normalizzato (acceleratori `&`↔`_`, `...`↔`…`, spazi, case) — ed
  espone `T(key, english)` / `T(english)` con fallback all'inglese del chiamante.
  Gli `.xlf` arrivano in output con un `<None Include="..\..\app\GitUI\Translation\*.xlf"
  Link="Translation\…" CopyToOutputDirectory/CopyToPublishDirectory="PreserveNewest">`,
  che soddisfa esattamente `Translator.GetTranslationDir()` (= directory di
  `GitExtensions.Extensibility.dll` + `Translation`): **66 file, ~19 MB**; `build-deb.sh`
  ora fallisce se mancano. Selettore in **View → Language** (radio, accanto a Light/Dark
  perché è lì che il port espone le preferenze di aspetto), persistito come
  `UiState.Language` in `ui-state.json`, **senza riavvio**. Dimostrazione su `MainMenu`:
  tutte e nove le intestazioni + ~45 voci si traducono (in italiano: Avvia · Repository ·
  Naviga · Visualizza · Comandi · Plugin · Strumenti · Aiuto; Comandi → Commit… · Annulla
  ultimo commit… · Stash · Reset delle modifiche… · Pulisci cartella di lavoro… · Crea
  ramo/tag/patch…). Le voci senza equivalente upstream restano inglesi per fallback.
  **Bug latente scoperto e corretto**: il parser degli access-key di Avalonia mangiava gli
  underscore negli header *di dati* (`fa_IR` → "faIR", e ogni path recente con `_` come
  `git_ext_mod`) → ora sono escapati con `__`.
  *Convenzione per le view rimanenti*: `T("<Categoria>/<Item>.<Prop>", "English literal")`
  dove la categoria è il `<file original>` dell'XLIFF, cioè la form upstream di cui la view
  è il corrispettivo (`FormCommit` per `CommitDialog`, `FormPush` per `PushDialog`,
  `RepoObjectsTree`, `RevisionGrid`, `FormBrowse` per la chrome); dove non esiste un item
  upstream, `T("English literal")`. Le view si ricostruiscono su
  `TranslationService.LanguageChanged` (pattern in `MainMenu`).
- **T2** (`5c956647f`) — header della revision grid con path abbreviato `~/…` come la
  toolbar, più `TextTrimming.CharacterEllipsis` e tooltip col testo completo (prima un path
  profondo spingeva conteggio e scope fuori vista senza ellissi). L'helper `CollapseHome` è
  **duplicato** da `MainToolbar.cs` perché quel file era assegnato a un altro subagent
  nella stessa iterazione: da unificare.
- **T3** (`45d103fa0`) — chiusi i tre strascichi di M45 nel `CommitDialog`:
  **(a)** il merge commit legittimo non viene più rifiutato — lo stato di merge è rilevato
  cercando `MERGE_HEAD` nella git-dir **risolta** (`GitModule.WorkingDirGitDir`, che scioglie
  l'indirezione `gitdir:` dei worktree collegati, con fallback a `git rev-parse --git-dir`),
  la guardia `staged == 0` è saltata a merge pendente e `MERGE_MSG` pre-popola il messaggio
  senza mai sovrascrivere quanto digitato; verificato end-to-end (commit con 0 file staged →
  `Merge branch 'feature'` con `HEAD^2` esistente). **(b)** liste `SelectionMode.Multiple`:
  stage/unstage/discard/copy-path agiscono su tutta la selezione con i conteggi nelle voci di
  menu e nella conferma; le tre .gitignore e mergetool restano solo su selezione singola.
  **(c)** acceleratori Enter/Space (stage/unstage) e Ctrl+Enter (commit), quest'ultimo
  intercettato in fase di tunneling così funziona anche dalla casella messaggio, dove Enter
  continua ad andare a capo. Corretto anche il diff stantio dopo un `Reload` che perde la
  selezione.

Verifica GUI del loop sull'albero integrato (screenshot guardati): menu bar e menu Comandi
in italiano; header grid `~/tmp-gridtest-repo — 3 commits (all branches)`; CommitDialog con
`MERGE_MSG` pre-popolato e status `merge in progress`.

**Residui aperti dopo M46**: applicare il layer di traduzione a tutte le altre view (T1 copre
solo `MainMenu`); unificare i due `CollapseHome`; `PushDialog.cs:95` stampa ancora il path
assoluto nel titolo; flash di ~1 s all'avvio con lingua non inglese (il menu nasce in inglese
e viene rietichettato, perché il parsing XLIFF sta fuori dall'UI thread e `_menu` è
inizializzato prima della lettura di `UiState`); i 19 MB di cataloghi potrebbero essere
filtrati alle sole lingue offerte.

### Blocco FOLLOW-UP 1 (round 5) — fine della ridondanza `WorkingDirectoryView`
> **Iterazione: 1 / 15 — BLOCCO CHIUSO** in una sola iterazione (stop per condizione (a):
> W1–W5 tutte integrate e verificate in GUI, `App/Views/WorkingDirectoryView.cs`
> cancellata, nessuna funzione rimasta irraggiungibile). Metodo invariato: delega a
> subagent Claude in worktree isolati su file disgiunti, cherry-pick uno alla volta +
> build check, verifica GUI headless del loop su albero integrato.

**M45** (2026-07-27) — le funzioni esclusive della vecchia finestra utility "Working
directory" sono state spostate dove le mette l'originale Windows, e la view è stata
cancellata (−1203 righe). Cinque unità, cinque commit di feature:

- **W1** (`3b2232ec4`) — **risoluzione conflitti nel `CommitDialog`**, come `FormCommit`:
  i file unmerged compaiono nella lista unstaged con stato `U conflict`, con context menu
  condizionale *Open in mergetool · Take ours · Take theirs · Mark resolved* (Items
  statici, in `Opening` si tocca solo `IsEnabled`), banner accent in testa finché
  esistono path non risolti, commit rifiutato con la dicitura originale ("There are
  unresolved merge conflicts, solve merge conflicts before committing."), doppio clic su
  una riga in conflitto = mergetool. Take ours/theirs passano da `ConfirmThen`.
- **W2** (`e70f339e9`) — **menu contestuale per file completo**: `Discard changes`
  (solo riga tracciata non in conflitto → `WorkingDirectoryService.ResetFile`, con
  conferma), `Copy path` su entrambe le liste, e le tre voci .gitignore (`Add to
  .gitignore` / `Ignore by extension` / `Ignore in folder`, abilitate solo per un
  **singolo file untracked**, stessa semantica della vecchia view). La lista staged, che
  non aveva menu, ora ha `Unstage` + `Copy path`.
- **W3** (`967941dda`) — **`Commands → Reset changes…` e `Clean working directory…`**
  nello slot esatto di `FormBrowse.Designer.cs` (dopo Stash). Clean: entrambe le preview
  dry-run (`git clean -nd` con e senza `-x`) pre-calcolate in un solo `Task.Run`, così la
  checkbox "include ignored" scambia solo stringhe già caricate; preview vuota → "Nothing
  to clean" e nessuna esecuzione; conferma → esecuzione via `GitProcessDialog` +
  `GitStreamRunner` con output live, poi `RefreshAll()`.
- **W4** (`989597e2a`) — **`Commands → Undo last commit…`** subito dopo Commit, come nel
  Designer originale (`undoLastCommitToolStripMenuItem`, icona `ResetFileTo`). Conferma
  esplicita che spiega che è `git reset --soft HEAD~1` (commit rimosso, modifiche
  conservate), caso limite HEAD senza parent gestito con dialog informativo e nessuna
  esecuzione.
- **W5** (`dfd0b9fdb`) — rimossi voce `Commands → "Working directory…"`, binding
  `Ctrl+Shift+W`, `OpenWorkingDirectoryWindow`, campo `_workingDir` e i refresh morti in
  `MainWindow`; `git rm App/Views/WorkingDirectoryView.cs`. `grep -rn WorkingDirectoryView
  --include=*.cs` → zero. **`App/Services/WorkingDirectoryService.cs` resta**: è il backend
  di tutti i nuovi chiamanti.

Build `Errori: 0` dopo ogni cherry-pick. Verifica GUI headless del loop (xvfb `:141`,
mini-WM, XTEST, repo di prova `/tmp/loop-testrepo` con conflitto reale + file untracked,
screenshot guardati): banner + riga `U conflict`; menu contestuale nei tre stati (file
tracciato modificato / file in conflitto / nessuna selezione) con le abilitazioni giuste e
diff `--cc` corretto; menu Commands finale = Commit… · Undo last commit… · Stash · Reset
changes… · Clean working directory… · New branch/tag · patch, **senza** "Working
directory…" e senza separatore orfano.

**Gap residui accettati** (registrati, non bloccanti): discard **multi-file** — le liste
del `CommitDialog` sono `SelectionMode.Single`, la vecchia view aveva "Discard changes (N
files)"; **drag&drop** tra unstaged/staged — non esiste nemmeno in `FormCommit`, scartato;
**acceleratori da tastiera** della vecchia view (Enter/Space = stage/unstage, Ctrl+Enter =
commit) non replicati, le azioni restano raggiungibili da bottoni e menu.
**Difetto noto emerso in W1**: dopo aver risolto tutti i conflitti, un merge commit
legittimo che non lascia diff in index può essere rifiutato dalla guardia "Nothing staged
to commit." — servirebbe rilevare `MERGE_HEAD`; unità futura.

### Blocco RIFINITURE (round 4) — chiusura residui A/B/C
> **Iterazione rifiniture: 4 / 20 — BLOCCO CHIUSO** (stop anticipato: tutti i residui
> A1–C10 + D11/D12 esauriti). Metodo invariato (delega a subagent Claude in worktree
> isolati, file disgiunti, cherry-pick uno alla volta + build check).

**Riepilogo del blocco rifiniture (round 4, iter. 1–4, M39–M42).** Chiusi tutti e dodici
i residui elencati nell'HANDOFF: A1 toolbar overflow, A2 split view, A3 repo recenti,
B4 toolbar diff, B5 selezione grid, B6 nodi DAG, B7 tab Working directory, C8 CommitDialog,
C9 PushDialog, C10 terminale PTY, D11 traduzioni (verificate → debito documentato, non
implementabile in piccolo), D12 shim Compat. 12 commit di feature + 4 di documentazione,
build sempre `Errori: 0`, ogni cambiamento verificato con screenshot GUI headless.
**Follow-up noti, non eseguiti**: (1) spostare risoluzione conflitti / clean / discard per
file / .gitignore / undo-last-commit da `WorkingDirectoryView` dentro `CommitDialog` +
`MainMenu`, poi cancellare la finestra utility e la view; (2) traduzioni = copia MSBuild
degli `.xlf` + layer `ITranslate` sulle view + selettore lingua; (3) il core condiviso
riscrive `HOME` all'avvio (`SetEnvironmentVariables` → `~/Documents`), quindi i processi
git figli possono ereditare un `HOME` sbagliato — fix non fatto perché tocca il codice
condiviso con la build Windows. **SKIP confermati fuori scope**: repository-host GitHub,
colonna build status.

- **M44** (bugfix post-blocco, 2026-07-27) — **il push chiedeva le credenziali ogni volta**.
  Il core riscrive `HOME` per l'intero processo a ogni costruzione di `Executable`
  (`EnvironmentConfiguration.SetEnvironmentVariables`), e su Linux il suo
  `GetDefaultHomeDir()` è sbagliato: legge `HOME` dai target `User`/`Machine`
  dell'environment, che .NET supporta **solo su Windows** — su Unix tornano entrambi
  `null`, quindi cade su `SpecialFolder.Personal` = `~/Documents`. I git figli cercavano
  `~/Documents/.gitconfig`, non trovavano nessun `credential.helper` → prompt a ogni push
  **e** il `git credential approve` di M38 non salvava nulla (parlava con un git senza
  helper), motivo per cui il portachiavi era rimasto vuoto.
  Fix in `App/HomeDirectoryFix.cs`, **senza toccare il core condiviso**: un
  `[ModuleInitializer]` (gira prima di `Main` e prima che qualsiasi tipo del core venga
  toccato) cattura l'`HOME` vero e lo scrive in `AppSettings.CustomHomeDir`, che è il
  **primo** ramo di `ComputeHomeLocation()` → ogni ricalcolo successivo atterra sulla home
  giusta. Non sovrascrive una home impostata deliberatamente dall'utente.
  Aggiunte due diagnostiche permanenti a `--selftest`: `[11]` HOME effettivo per i git
  figli, `[12]` `credential.helper` risolto. Prova A/B con quelle:
  senza fix → `HOME=/home/dario/Documents`, helper `<none>`; con fix →
  `HOME=/home/dario`, helper `git-credential-libsecret`. Commit `f1caa6512`.

- **M43** (bugfix post-blocco, 2026-07-27) — **Fetch/Pull bloccavano la GUI**.
  `RemoteService.ListRemotes` fa sync-over-async (`GetRemotesAsync().GetAwaiter().GetResult()`)
  e `MainWindow.RunRemoteOp` lo chiamava **sul thread UI**: la continuazione veniva postata
  sul thread già bloccato → hang totale *prima* di avviare git, quindi nemmeno il process
  dialog compariva (finestra congelata, non lenta). Stesso difetto in
  `RepoObjectsTree.DoEditRemoteUrlAsync` → `FindRemoteUrl`. Fix a due livelli: helper
  `RemoteService.RunDetached` che fa hop sul thread pool (nessun chiamante può più
  deadlockare — degrada a blocco breve), e le due chiamate spostate in `Task.Run`.
  Push era immune perché `PushDialog` pre-carica già fuori dall'UI thread. Verificato in
  GUI headless: il dialog appare e il fetch completa con `Success`.

- **M42** (iter. 4) — C10 + D11 + D12, chiusura del blocco:
  - **C10 terminale PTY realmente embedded** (nuovi `Services/PtyProcess.cs`,
    `Services/TerminalEmulator.cs`, `Views/TerminalControl.cs` + `Views/ConsoleView.cs`,
    `001c505b1`): niente NuGet, solo P/Invoke su libc
    (`posix_openpt`/`grantpt`/`unlockpt`/`ptsname_r`/`read`/`write`/`ioctl(TIOCSWINSZ)`).
    Nessun `fork()` dal runtime .NET (pericoloso con molti thread): il figlio è
    `setsid -w /bin/sh -c 'exec 0</dev/pts/N 1>/dev/pts/N 2>&1; exec $SHELL -i'`, così il
    pts diventa il terminale **di controllo** della shell → job control, `isatty()`, colori.
    Parser VT100/xterm con SGR completo (16/bright/256/truecolor), CUP/ED/EL/IL/DL/ICH/DCH/
    SU/SD/DECSTBM, save/restore cursore, autowrap, DECCKM, bracketed paste, **alternate
    screen** (`less`, `top`), OSC 0/1/2, scrollback 5000 righe; tastiera completa (frecce,
    Home/End/Del/PgUp/PgDn, F1–F4, Ctrl+/Alt+lettera, Backspace 0x7f) e resize via
    `TIOCSWINSZ`. **Bug non ovvio trovato**: la shell ereditava `SigIgn` con SIGINT/SIGQUIT/
    SIGPIPE ignorati dal processo GUI (le disposizioni "ignora" sopravvivono a `execve`) →
    **Ctrl+C non uccideva nulla**; risolto rimettendo `SIG_DFL` attorno a `Process.Start`.
    Verificato in GUI: `ls --color`, `git log --decorate` a colori, Ctrl+C su `sleep 100`,
    history con frecce, Tab completion, `less` e `top` sull'alternate screen, `stty size`
    coerente dopo resize, nessun processo zombie alla chiusura.
  - **D11 traduzioni — verificate, NON funzionanti per costruzione** (`e1d8fee09` per la
    parte di fix): il motore XLIFF del core gira benissimo su Linux (32 lingue caricate,
    `GetTranslation("Italian")` → 146 categorie, nessun problema di case-sensitivity), ma
    (a) nessun csproj crossplatform copia `src/app/GitUI/Translation/*.xlf` in output o nel
    `.deb`, (b) non esiste un'implementazione `ITranslate` né una sola chiamata
    `Translator.Translate` nelle view Avalonia — ogni stringa è un letterale inglese —, e
    (c) non c'è selettore lingua né chiave persistita. Impostare `translation=Italian` non
    cambia nulla: **il port è inglese per costruzione**. Registrato come debito; non è un
    fix piccolo. Nel commit è invece incluso il fix del **path repo abbreviato con `~`**:
    la causa non era l'abbreviazione (esisteva già) ma il core che riscrive `HOME` a
    `~/Documents` da un thread di background mentre la shell si costruisce → risolto con
    uno snapshot della home in `[ModuleInitializer]` (gira prima di `Main`), collasso solo
    su confine di directory reale.
  - **D12 shim `Compat/` reali** (nuovi `Compat/AvaloniaHost.cs`, `MessageBoxWindow.cs`,
    `ClipboardShim.cs`, `FileDialogs.cs`, + `SystemWindowsFormsShims.cs` e il csproj,
    `8b84ce91`): censiti tutti i no-op e implementati quelli **davvero raggiunti**.
    L'unico no-op vivo era `MessageBox.Show` (raggiunto da `GitVersion.CurrentVersion` per
    la versione di git non supportata e da `ConfigFile.Save` → `ExceptionUtils.ShowException`):
    ora è un modale Avalonia a tema con glifi, ordine bottoni WinForms, default-button e
    `DialogResult` fedeli, Ctrl+C che copia il messaggio, Escape → Cancel. Aggiunti anche
    clipboard reale (`IClipboard` via `TopLevel`) e file/folder picker (`IStorageProvider`).
    Il bridge async→sync (`AvaloniaHost.Run`) non blocca mai l'UI thread: sull'UI thread
    pompa un `DispatcherFrame` annidato (come fa il modal loop di WinForms), fuori posta
    sull'UI thread e blocca solo il chiamante. Lasciati no-op, con motivazione, quelli
    irraggiungibili (`Icon.ExtractAssociatedIcon`, `TextRenderer.MeasureText`,
    `Graphics.MeasureString`, i type-filler WinForms, i path Registry già OS-guarded).
    *Limite noto*: sotto Xvfb il clipboard X11 di Avalonia è inerte (verificato con
    controprova `xclip`), quindi headless si verifica solo fino al confine Avalonia; i
    file picker richiedono un portal XDG (senza, servirebbe `UseManagedSystemDialogs()`,
    scelta che spetta all'app).
  - Verifica finale sull'albero integrato: `--selftest` exit 0, GUI senza eccezioni,
    terminale embedded funzionante, toolbar `~/git_ext_mod`. Build `Errori: 0`.

- **M41** (iter. 3) — B6 + B7 + C9:
  - **B6 righe artificiali come nodi del DAG** (`RevisionGridView.cs`, `e9e5a49a5`): eliminato
    il pannello fisso `_topRows`; **Working directory** e **Commit index** sono ora veri
    `RevisionRow` in testa alla stessa `ListBox`, con hash sentinella `2222…`/`1111…`
    (come `WorkTreeId`/`IndexId` del core), parent vuoti e data vuota. Il grafo li ancora
    nella lane di HEAD (`ArtificialSegments` + `WithHeadConnector`) così la lane arriva
    ininterrotta al nodo HEAD, e `RevisionGraphControl` disegna per loro un **quadrato
    vuoto** invece del pallino. Non partecipano al range-diff né emettono
    `RevisionSelected` (il CommitDialog si apre su click esplicito o da menu contestuale,
    così scorrere con le frecce non apre più un modale); nascosti quando un filtro
    testuale è attivo; compaiono solo con conteggi > 0.
  - **B7 tab "Working directory" rimosso** (`MainWindow.cs`, `3c31fad94`): l'originale
    FormBrowse non ha quel tab e il nucleo stage/unstage/diff/commit è duplicato dal
    `CommitDialog`. **Ma** il pannello non era pura duplicazione: è l'unico posto del port
    con risoluzione conflitti (mergetool / take ours / take theirs / mark resolved),
    `git clean` con preview, "Discard changes" per file, le tre voci .gitignore, "Copy
    path" e "Undo last commit". Quindi la view sopravvive come **finestra utility
    on-demand** (non modale) aperta da **Commands → "Working directory…"** e
    **Ctrl+Shift+W**; la voce di menu è agganciata a runtime da `MainWindow` cercando il
    `MenuItem` `_Commands` (nessuna modifica a `MainMenu.cs`). `LoadRepository` per quel
    pannello gira solo a finestra aperta (un `git status` in meno per refresh).
    *Follow-up*: spostare conflitti / clean / discard+gitignore dentro `CommitDialog` e
    aggiungere "Reset changes…" / "Clean working directory…" a `MainMenu` (dove stanno
    nell'originale); solo allora la utility window e `WorkingDirectoryView` si possono
    cancellare del tutto.
  - **C9 PushDialog completo** (`PushDialog.cs` + nuovo `Services/PushRefsService.cs`,
    `1f0163425`): tab **Push tags** (lista tag con checkbox e OID breve, select all/none,
    `--tags`, force-with-lease), tab **Push multiple branches** (griglia con checkbox,
    branch di destinazione editabile, colonna ahead/behind da `%(upstream:track)`, push
    multiplo in un solo `git push` multi-refspec), **Manage remotes** che apre il
    `RemotesDialog` esistente e ricarica le combo fuori dall'UI thread, **push per Url**
    con combo pre-riempita dai push-URL dei remote + Browse…. Tutto passa dal path
    esistente `GitProcessDialog.RunStreamingAsync` + retry `CredentialsDialog`.
    Due bug trovati in verifica: `TargetIsUrl()` veniva valutato **dentro** la lambda di
    background (accesso a un control fuori dall'UI thread → eccezione e console vuota;
    ora i valori sono snapshottati sull'UI thread), e i branch senza upstream risultavano
    "up to date" (`%(upstream:track)` vuoto letto come 0/0 → ora "new"). Verificato su
    remote bare locale, nessun remote reale toccato.
  - Verifica GUI di integrazione: tab strip senza "Working directory"; su repo sporco le
    due righe artificiali compaiono in cima con nodo quadrato e lane continua fino a HEAD.
    Build `Errori: 0`.

- **M40** (iter. 2) — altri tre residui chiusi in parallelo:
  - **A2 Split view reale** (`MainWindow.cs`, `MainToolbar.cs`, +1 proprietà in
    `Services/UiStateService.cs`, `7865c8df8`): il toggle torna a cambiare davvero il
    layout. ON → il tab **Commit** ospita commit-detail | `GridSplitter` trascinabile |
    diff completo (file list + testo + toolbar diff), e il tab Diff sparisce finché dura
    (un control ha un solo parent visuale); OFF → detail da solo e tab Diff reinserito
    nella sua posizione. Posizione dello splitter salvata in `DetailStar`/`DiffStar`,
    toggle persistito come `UiState.SplitView` e ripristinato all'avvio; caption
    "Split view ✓" in accent, coerente anche nella voce del menu overflow `»`. I 4 punti
    che forzavano il tab Diff passano ora da `FocusDiff()`.
  - **B5 selezione riga** (`RevisionGridView.cs`, `59d5e9d63`): il background della riga
    era dipinto dal `Grid` interno (opaco, con margine) e copriva il fill di selezione →
    si vedeva solo la barra accento. Ora la radice riga è un `RevisionRowView : Border`
    a piena larghezza che osserva `IsSelected`/`IsFocused`/`IsPointerOver` del
    `ListBoxItem` (template ridotto a trasparente). Selezione = **`App.Accent` pieno da
    bordo a bordo**, testo bianco; selezione inattiva virata verso `App.Selection`; riga
    focus dentro una multi-selezione con rettangolo bianco 1px. Leggibilità sopra il blu:
    pill con sfondo bianco e colore-tipo su bordo+testo, ▶ verde chiaro, lane del DAG
    schiarite del 55% verso il bianco e nodo con anello bianco.
  - **C8 CommitDialog completo** (`CommitDialog.cs` + nuovo `Services/CommitActionsService.cs`,
    `b471524b0`): **Stash staged changes** = `git stash push --staged` con fallback
    manuale per git < 2.35 (`write-tree`/`commit-tree`/`apply --reverse --index`/
    `stash store`, con la fase distruttiva dopo lo store); **Commit templates** = union di
    `commit.template` e scansione repo (`.gitmessage*`, `.github/*TEMPLATE*`, …), la voce
    scelta riempie il messaggio; **Create branch** = `check-ref-format` +
    `show-ref --verify` poi `checkout -b`/`branch`; **Options** = amend / `--signoff` /
    `--no-verify` / `--reset-author` / chiudi-dopo-commit, composti nel `git commit … -F`
    realmente eseguito e mostrati nella status line.
  - Verifica GUI di integrazione a 1400px: riga selezionata blu pieno con pill e grafo
    leggibili; split ON mostra detail+diff affiancati con la toolbar B4. Build `Errori: 0`.

- **M39** (iter. 1) — tre residui chiusi in parallelo su file disgiunti:
  - **A1 toolbar overflow** (`MainToolbar.cs`, `008075276`): lo `StackPanel` orizzontale è
    sostituito da un `OverflowPanel : Panel` che misura ogni item a larghezza infinita,
    tiene quelli che entrano e parcheggia gli altri fuori schermo (`ClipToBounds`), senza
    toccare `IsVisible` dal measure. Pulsante **`»`** ancorato a destra, visibile solo
    quando serve; flyout ricostruito da `HiddenItems` **prima** di `ShowAt`. I dropdown
    con provider (Repository/Branch/Submodules/Worktrees) sono `LazyMenu` e riaprono il
    proprio flyout ancorato a `»`; la casella Filter nel menu è un TextBox mirror che
    riscrive nel reale. Verificato a 1400px (layout inline invariato) e 1200px (niente
    oltre il bordo, tutti gli item raggiungibili dal menu).
  - **A3 repo recenti** (`RecentRepositoriesService.cs` + `RecentReposProvider`, `4c7eab2b1`):
    normalizzazione path (`GetFullPath`, trailing separator), dedup ordinale MRU-safe,
    scarto dei path inesistenti o senza `.git`, scarto dei worktree effimeri
    (`.claude/worktrees`, anche in `AddAsync`). La potatura è **persistita**
    (`SaveRecentHistoryAsync`), non solo nascosta a display; tutto dentro `Task.Run`
    (nessuno `stat` sul thread UI). Verificato in GUI con lista seminata di voci morte.
  - **B4 toolbar del Diff** (`DiffView.cs` + nuovo `Services/DiffTextService.cs`, `229fd8143`):
    prev/next change (▲▼ con scroll all'hunk e "Change N of M"), zoom `A+`/`A−`/reset
    (6–32pt), toggle **`-w`**, **`¶`** caratteri non stampabili (`·`/`→`/`␍`, render-side),
    **`<div>`** `--word-diff=plain`, selettore encoding con default **Unicode (UTF-8)**
    (git letto come byte grezzi, decodifica lato client), menu **`⚙`**. I toggle
    ri-eseguono davvero git e persistono nella sessione (`DiffTextService.Session`); la
    status line mostra il comando git che ha prodotto la patch visibile. Git sempre
    off-UI-thread.
  - Verifica GUI di integrazione a 1400px con config isolata: `»` presente, nessun
    elemento oltre il bordo destro, app senza eccezioni. Build `Errori: 0`.
  - *Residuo minore introdotto*: il dropdown repo in toolbar ora mostra il path assoluto
    completo invece di `~/…` (effetto della normalizzazione A3) → riabbreviare con `~`.

### Milestone round 3 (fedeltà visiva, aree: tab inferiori + commit detail + filtri)
- **M37** (iter. 10) — **U-FILTER** toolbar: menu **All branches ▾** (All/Current/Filtered)
  + casella **Filter:** che pilotano la grid (`RevisionGridView.SetBranchScope`/`ApplyFilter`,
  header radios rifattorizzati per condividere il path). Wiring **CommitNavigated**: link
  parent/child del commit detail → `SelectCommit(hash)`+OnRevisionSelected (naviga la grid).
  Console tab già collegato a OpenTerminal. Verificato in GUI (filtro "U-TABS" → 2/200 commit).

**Round 3 COMPLETO** (M36–M37): tab inferiori Commit/Diff/File tree/GPG/Console/Output,
commit detail ricco (child/parent link, contained-in, describe), combo scope+filtro in
toolbar. Le 3 aree round-3 scelte dall'utente sono chiuse.
Residuo noto (chiuso in **M39/A1**: overflow `»`): le combo toolbar + repo indicator
finivano oltre il bordo destro a larghezze piccole.
- **M36** (iter. 9) — **U-DETAIL** commit detail arricchito (avatar identicon grande,
  Author/Date rel+abs, Committer se diverso, hash, Parent/Child come link → evento
  CommitNavigated, "Contained in branches/tags" a pill, "Derives from tag" via
  `git describe`). **U-TABS** pannello inferiore ristrutturato in tab
  **Commit · Diff · File tree · GPG · Console · Output** (+ Working directory · Stash ·
  Blame · File history): nuove view FileTreeView (`ls-tree`), GpgView (`--show-signature`),
  OutputView (core CommandLog), ConsoleView (apri terminale); diff scorporato dal tab
  Commit in tab Diff proprio (toggle Split-view ora cosmetico). Verificati in GUI.
  Nota: durante l'integrazione il branch del repo principale era stato spostato da un
  subagent (checkout master/prova) e la prima commit U-TABS era atterrata su `prova`
  con base sbagliata → recuperato reintegrando `05bf206d7` su HEAD corretto.

### Milestone round 2 (fedeltà visiva)
- **M35** (iter. 8) — **U-TOOLBAR** dropdown inline nella toolbar: repo-path
  (`~/path ▾`, flyout recenti → OpenRepository) + branch corrente (`branch ▾`, flyout
  branch locali, corrente in grassetto → checkout via BranchTagService), subito dopo
  Open come nell'originale; popola-prima-di-aprire (no sliver vuoto). `BranchesProvider`
  /`BranchCheckoutRequested`/`RecentReposProvider` + caption aggiornate in UpdateState.
  Verificato in GUI.

**Round 2 COMPLETO** per le aree scelte (Modali · Griglia · Menu+toolbar): M31–M35.
Resta fuori scope (non scelto): tab inferiori originali Console/Output/File tree/GPG,
combo All-branches/Filter in toolbar (la grid ha già filtro+scope), righe artificiali
integrate nel grafo DAG (ora sono un pannello sopra la lista).
- **M34** (iter. 7) — **U-GRID-TOPROWS** righe artificiali "Working directory" +
  "Commit index" in cima alla grid (pannello fisso docked Top sopra la ListBox, non
  tocca RevisionRow/ItemsSource): ✔ verde+conteggi quando ci sono modifiche, dim se
  pulito; click apre CommitDialog. `RevisionGridView.SetWorkingState(unstaged,staged)`
  + eventi WorkingDirectorySelected/CommitIndexSelected, alimentati da MainWindow
  (RefreshToolbarState). Verificato in GUI.
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
- **M33** (iter. 6) — **streaming output VERO** nel process dialog. Nuovo
  `Services/GitStreamRunner.cs` (System.Diagnostics.Process, stdout+stderr redirette e
  lette async con OutputDataReceived/ErrorDataReceived → callback riga-per-riga; il core
  bufferizza stderr, quindi git eseguito diretto). `RemoteService.Fetch/Pull/PushStreaming`
  + `GitProcessDialog.RunStreamingAsync`; fetch/pull (MainWindow) + push/pull (PushDialog)
  + Commit & push (CommitDialog) rewired. Verificato live in GUI: progress fetch stderr
  ("Ricezione oggetti 1%→21%…") scorre incrementale mentre lo status è "Running…".
  Rimane il residuo T6 aggiornato: streaming ora live (non più solo a fine comando).

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
