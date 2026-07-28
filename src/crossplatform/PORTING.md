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

- [ ] 0.16 **La finestra non risponde a `WM_DELETE_WINDOW`**: la toplevel Avalonia non espone il
      protocollo in `WM_PROTOCOLS` e ignora il ClientMessage → con un window manager reale la "X"
      della decorazione **non chiude l'app** e `PersistLayout()` non gira, quindi *tutto* lo stato
      UI (geometria, splitter, tab, collasso, pull action) si perde. Oggi solo `Start → Exit` passa
      da `Closing`/`PersistLayout`. Scoperto durante la verifica GUI di M52. **Non è una
      regressione**: è il limite di piattaforma già censito nei blocchi precedenti (Avalonia non
      espone `WM_DELETE_WINDOW` su X11). La novità è la *conseguenza*, che prima non era stata
      collegata: non è solo "la X non chiude", è che **si perde tutto lo stato UI**. Se il
      protocollo non è registrabile dal lato managed, la via è un ricevitore nativo come si è già
      fatto per XDND (`Services/X11DropTarget.cs`). *valore alto*
- [ ] 0.17 `RevisionsStar`/`BottomStar` vengono salvati come valori **simil-pixel** (es. 199 / 525)
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
- [ ] 0.21 Il logo `GitExtensionsLogoWide.png` esiste in `setup/assets/Logo/` ma è **fuori dal
      glob** del csproj (`src/app/GitUI/Resources/Icons/*.png`), quindi non è risolvibile come
      `avares:`. Servono due cose insieme: la riga nel csproj **e** il codice che lo carica; oggi
      la dashboard tiene il wordmark testuale invece di sostituire artwork a caso. *banale*

**Difetti trovati dalla verifica GUI di M53–M56** (sessioni :281–:287, tutti visti a schermo)

- [ ] 0.22 **Il `CommitDialog` non ha alcun pulsante di chiusura**, ignora Esc e ignora
      `WM_DELETE_WINDOW`: una volta aperto non è dismissibile in alcun modo sintetico. Upstream ha
      `Cancel` (`FormCommit.Designer.cs:142-151`). *grave, banale*
- [ ] 0.23 **Nessuno dei tre dialoghi del menu Repository si chiude con Esc** (Remotes,
      Submodules, Worktrees): solo il loro pulsante `Close`. Anche il `WM_DELETE_WINDOW`
      sintetico è ignorato (stessa radice di 0.16). *banale*
- [ ] 0.24 **Selezionare una riga della griglia riporta a forza il pannello inferiore sul tab
      Commit**: non si può tenere aperto Output, Diff o File tree mentre si naviga la storia.
      Upstream aggiorna il tab visibile senza cambiarlo. Il colpevole è la riga
      `_bottom.SelectedItem = _commitInfoTab;` in `OnRevisionSelected`. *banale, alta seccatura*
- [ ] 0.25 **Titolo stantio dopo `Close (go to Dashboard)`**: resta `<repo> (<branch>)` mentre a
      schermo c'è la dashboard (`RefreshToolbarState` non gira più). E in dashboard mode la
      **toolbar non viene neutralizzata**: mostra ancora path, branch e i pulsanti Fetch/Pull/
      Push/Commit di un repo che non è più aperto. *banale*
- [ ] 0.26 **Navigazione da tastiera della dashboard rotta**: dalla casella di ricerca il primo ↓
      evidenzia il *contenitore del gruppo* ("Recent repositories" + prima riga) invece della
      prima voce, e i ↓ successivi non avanzano; il caret resta nella casella, quindi il fuoco non
      si sposta mai davvero. *media*
- [ ] 0.27 **Etichette di menu troncate senza ellissi**: "Toggle between artificial and HEAD
      commi", "Highlight selected branch (until refresh", "Arrange commits by topo order (ances".
      *banale, cosmetico*
- [ ] 0.28 Su un repo **bare** il pannello sinistro mostra l'errore git grezzo
      (`Error: fatal: quest'operazione deve …`) invece di un albero vuoto. *banale*
- [ ] 0.29 Da confermare: lo stack del crash 0.19 è stato visto anche sul percorso
      `OpenRepository → LoadRepository → Reload` (doppio clic su un worktree nell'albero con una
      riga selezionata), intermittente 1 volta su 5. Le prove successive alla build con la guardia
      non l'hanno più riprodotto: **verificare che la guardia copra anche questo stack**.

- [ ] 0.30 `CommitDialog.cs:1830` — "Commit & push" chiama ancora la `PushStreaming` a due stati
      e quindi passa **`-u` cablato**, ri-puntando l'upstream del ramo. Va instradato sullo stesso
      probe `ResolveTrackingAsync` introdotto per il push dialog. *banale*

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
- [ ] 1.14 **Righe artificiali (Working directory / Commit index) non raggiungono il pannello**:
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
- [ ] 1.24 Diff: **"Filter file in grid"** (`RevisionService.PathFilter` e il `_pathFilter` del
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
- [~] 3.2 **Posizione del commit-info** e **ultimo repo aperto** non persistiti (la feature a 3
      posizioni esiste, riparte sempre da `BelowGraph`); più le opzioni del **diff viewer**, gli
      **switch della file history**, i **filtri del left panel** e la **MRU dei filtri di
      revisione**. *banale ciascuno*
- [~] 3.3 **Dashboard: menu contestuale** (Show in folder / Categories ▸ / Remove from list /
      Remove missing projects) — serve `RemoveRecentAsync` in `RecentRepositoriesService`, che oggi
      ha solo Load/Add. Nota: il port **elimina in silenzio** le voci morte
      (`RecentRepositoriesService.cs:35-77`) mentre upstream le evidenzia e chiede: scelta
      difendibile, da *dichiarare*. Più branding (logo/sfondo/palette) e il branch corrente per
      voce. *media*
- [ ] 3.4 **Banner "operazione git in corso"** (rebase / merge / bisect / cherry-pick) ancorato
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

- [~] 4.1 **Checkout di rami remoti impossibile**: `CheckoutBranchDialog` è solo il gruppo "Local
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
- [ ] 4.4 **CommitDialog: lista file senza menu contestuale.** `FileStatusListView` non ha
      `ContextMenu` nel commit dialog e la lista è un `ListBox` di stringhe. Upstream ha ~25 voci
      (reset file to, interactive add, cherry-pick changes, difftool, open/edit, save as/move/
      delete, show in file tree, filter in grid, file history, blame, gitignore/exclude,
      skip-worktree/assume-unchanged, blocco submodule). Più il **filtro regex di selezione**
      (visibile nello screenshot) e la validazione/persistenza del messaggio. *alta*
- [ ] 4.5 **Il box "Filter:" della toolbar non filtra via git**: è un setaccio in memoria sulle
      righe già caricate (`ApplyFilterCore`, `Matches`), mentre upstream applica il filtro **a
      git** su Invio con un dropdown "Filter type" (message/committer/author/diff contains) e una
      MRU di 30 voci. `RevisionFilter` supporta già tutti quei campi: è wiring + persistenza. Più
      il **ref picker** per "Show filtered branches", oggi dichiaratamente uno stub. *media*
- [ ] 4.6 **Blame: evidenziazione di tutte le righe dello stesso commit** su hover/selezione
      (l'affordance più usata upstream), **find/F3/go-to-line** (template già in `DiffView`) e il
      walk accurato "blame previous revision" (upstream mappa la riga nel parent con
      `GitBlameParser`; il port ri-blama e perde la posizione). *media*
- [ ] 4.7 **Linkificazione del commit info**: gli hash dentro il corpo del messaggio non sono link
      (`CommitDataBodyRenderer.cs:44-65`), branch e tag non sono cliccabili (pillole inerti), e
      "Derives from" stampa `v1.0-5-gabc1234` invece di `v1.2.0 + 66 commits`. *media*
- [ ] 4.8 **`GitProcessDialog` su PTY**: passarlo a `PtyProcess`/`TerminalEmulator` (**già
      esistenti**, alimentano `ConsoleView`) sblocca in un colpo output live, barra di progresso
      dalle righe `\r` e **prompt interattivi** — oggi stdin è chiuso e `GIT_TERMINAL_PROMPT=0`,
      quindi passphrase e host-key `yes/no` non sono rispondibili. *media/alta*
- [ ] 4.9 **Leva massima della file history**: dare a `RevisionGridView` un entry point **con path
      filter** (oggi `LoadRepository(string)` è l'unico loader) chiuderebbe in un colpo grafo,
      decorazioni ref, righe artificiali e multi-selezione nel tab File history, che oggi
      reimplementa una lista nuda. *media/alta*
- [~] 4.10 Toolbar, resto: **shell-picker** (upstream `userShell` è uno split-button che elenca le
      shell disponibili, il port ha un "Terminal" secco), dropdown **WorkingDir** ricco (ricerca,
      preferiti categorizzati, Open/Close repository, "Configure this menu…"), voce **"Checkout
      branch…"** in testa al dropdown branch, corpo cliccabile di **CommitInfoPosition** (cicla le
      3 posizioni con icona dinamica), **icona di Commit dallo stato del repo** (7 stati upstream),
      **behind** sul pulsante Push, visibilità condizionale dei Worktrees, filtri **branch** e
      **revision** della seconda toolstrip. *banale→media, molte voci*
- [ ] 4.11 Dialoghi, resto (media ciascuno): `RemotesDialog` senza il tab **"Default pull
      behavior"** né **push URL separata**; `FormVerify` ("Recover lost objects") ridotto a un dump
      di `git fsck`; `FormCleanupRepository` ridotto a un confirm inline (la modalità
      **solo-ignorati `clean -X` è irraggiungibile**); **dialogo bisect** + gating su
      `InTheMiddleOfBisect` (oggi il port auto-avvia in silenzio alla prima marcatura);
      **macchina a stati `git am`** (Resolved/Skip/Abort, `PatchGrid`); `FormInit` inesistente
      (solo folder picker, nessun `--bare`/`--shared`); `CloneDialog` senza submodule-init,
      depth, branch picker, preview della destinazione; `ArchiveDialog` senza filtro path/revisione;
      `SparseDialog` su cone mode (**niente negazione `!`**, upstream pilota il legacy);
      `AboutDialog` senza versione git/build sha né attribuzione icone.

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

**Interruzione**: il limite di sessione ha ucciso tre subagent a metà (verifica GUI di M53, albero
sinistro, File tree+GPG). I due worktree contenevano ~1100 righe **non committate** ciascuno; le
diff sono state salvate in `/tmp/loop-salvage/*.patch` e gli agent sono stati **ripresi dal loro
transcript** invece di ripartire da zero. Lezione: istruire i subagent a **committare presto e
spesso**, non solo a fine unità.

**Nota di metodo** (costata due tentativi): `pkill -f "<pattern>"` negli script di verifica GUI
**uccide la shell che lo lancia** se il pattern compare anche nella propria riga di comando (es.
`pkill -f "Xvfb :151"` invocato da un comando che contiene quella stessa stringa). Usare un pattern
auto-escluso (`Xvf[b] :151`) o `kill <PID>`.

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
