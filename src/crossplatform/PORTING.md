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
10. **Sistema di plugin**: i plugin espongono form WinForms; ripensare il
    modello UI dei plugin per Avalonia.
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

> **Iterazione: 6 / 20** · parità **55.0%** (88/160 voci `[x]`). Questo giro
> (M14): 3 subagent paralleli → Clone + Init repository dal menu File; nodo
> Submodules nel tree (list/update); revision grid — colonna git-notes + toggle
> data (author/commit, relative/assoluta) + mostra/nascondi colonne. Prossimo:
> iter. 7 → Remotes manager, Revert, Compare, Worktrees node, Reflog, Archive,
> menu Repository/Tools; modello UI plugin verso la fine.
> Riferimento originale: `src/app/GitUI` (FormBrowse, RepoObjectsTree,
> RevisionGrid). Stato: `[x]` fatto nel port Avalonia · `[ ]` mancante.

### A. Barra dei menu
- [x] Start ▸ Open repository
- [x] Start ▸ Clone repository
- [x] Start ▸ Create new repository (init)
- [x] Start ▸ Recent repositories (MRU)
- [ ] Start ▸ Favorite repositories
- [x] Start ▸ Exit
- [ ] Dashboard (close-to-dashboard + refresh)
- [x] Repository ▸ Refresh
- [ ] Repository ▸ File Explorer
- [ ] Repository ▸ Remote repositories…
- [ ] Repository ▸ Manage submodules / update / synchronize
- [ ] Repository ▸ Manage worktrees
- [ ] Repository ▸ Edit .gitignore / .gitattributes / exclude / mailmap
- [ ] Repository ▸ Sparse working copy
- [ ] Repository ▸ Git maintenance (gc / fsck / delete index.lock / edit config)
- [ ] Repository ▸ Repository settings
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
- [ ] Commands ▸ Solve merge conflicts
- [x] Commands ▸ Create tag
- [x] Commands ▸ Delete tag
- [x] Commands ▸ Cherry pick
- [ ] Commands ▸ Archive revision
- [x] Commands ▸ Checkout revision
- [ ] Commands ▸ Bisect
- [ ] Commands ▸ Show reflog
- [ ] Commands ▸ Format patch
- [ ] Commands ▸ Apply patch
- [ ] Commands ▸ View patch file
- [ ] Repository hosts (GitHub: fork / view-create PR / add upstream)
- [ ] Plugins menu + Plugins settings
- [ ] Tools ▸ Git bash / Git GUI / GitK / PuTTY
- [ ] Tools ▸ Git command log
- [x] Tools/Edit ▸ Settings (SettingsWindow Avalonia: identità git, pull, tema)
- [x] Help ▸ About
- [ ] Help ▸ User manual / Changelog / Report issue / Check updates / Translate / Donate / Telemetry
- [x] View ▸ tema chiaro/scuro (toggle live)

### B. Toolbar
- [x] Refresh
- [x] Toggle left panel
- [ ] Toggle split-view layout
- [ ] Commit-info position (below/left/right)
- [ ] Level-up / Submodules split button
- [ ] Worktrees split button
- [x] Working directory / recent-repo picker (Open)
- [x] Branch select (checkout)
- [x] Pull (+ merge/rebase/fetch/fetch-all varianti)
- [x] Push
- [x] Commit
- [x] Stash (+ stash staged / pop / manage)
- [ ] File Explorer
- [ ] User shell selector
- [x] Settings/Edit button (apertura)
- [x] New branch
- [x] Fetch

### C. Pannello sinistro (RepoObjectsTree)
- [x] Nodo Branches + checkout/create/merge/rebase/reset/rename/delete/filter
- [x] Nodo Remotes + fetch/pull/push/manage
- [x] Nodo Tags + checkout/create-branch/merge/reset/delete
- [x] Nodo Stashes + apply/pop/open/drop/manage
- [x] Nodo Submodules + list/update/update-all (open/commit/reset da fare)
- [ ] Nodo Worktrees + open/create/delete/prune/manage
- [ ] Ordinamento ref (sort-by / sort-order) + move up/down
- [ ] Copy to clipboard / copy path dai nodi

### D. Revision grid
- [x] Colonna grafo DAG (multi-lane)
- [x] Colonna messaggio/oggetto
- [x] Colonna autore
- [x] Colonna data
- [x] Colonna commit id (SHA)
- [ ] Colonna avatar
- [ ] Colonna build status (icona/testo)
- [x] Colonna git notes (indicatore + tooltip, `git notes list` batch)
- [x] Menu contestuale: copy hash/subject/author
- [x] Menu contestuale: checkout commit / cherry-pick / reset (soft·mixed·hard)
- [ ] Menu contestuale: revert commit
- [x] Menu contestuale: create branch/tag here
- [ ] Menu contestuale: compare (BASE / selected / working dir / branch / difftool)
- [ ] Menu contestuale: bisect good/bad/skip/stop
- [ ] Menu contestuale: navigate (parent/child/ancestor/go-to)
- [x] Filtro/ricerca (autore / messaggio / hash) — barra live in RevisionGridView
- [ ] Quick-search da tastiera
- [ ] Drag &amp; drop
- [ ] View toggles (mostra/nascondi colonne, all/current/filtered branches)

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
- [ ] Remotes manager
- [x] Rename branch
- [ ] Delete remote branch
- [ ] Revert commit
- [ ] Reset changes (dialog dedicato)
- [ ] Cleanup repository (git clean)
- [ ] Resolve conflicts
- [ ] Merge submodule
- [ ] Archive
- [ ] Format patch / Apply patch / View patch
- [ ] Add to .gitignore
- [ ] Edit .gitignore / .gitattributes / .mailmap
- [ ] Sparse working copy
- [ ] Submodules manager
- [ ] Reflog browser
- [ ] Compare to branch
- [ ] Verify database / recover lost objects
- [x] Settings (SettingsWindow — identità/pull/tema; fondazione estendibile)
- [ ] Command log (FormLog)
- [ ] Bisect UI

### F. Operazioni git esposte
- [x] Commit (+ amend)
- [ ] Commit: squash / fixup / reword / edit / undo
- [x] Push (`--force-with-lease`, non più `--force`)
- [x] Pull / Fetch (+ fetch all / prune)
- [x] Branch: create / checkout / delete
- [x] Branch: rename (git branch -m, da tree)
- [x] Merge
- [x] Rebase (⚠ non interattivo)
- [x] Cherry-pick
- [ ] Revert
- [x] Reset (soft/mixed/hard da grid)
- [x] Clean working directory (git clean -fd, dry-run + conferma)
- [x] Stash: save / apply / pop / drop
- [x] Stash: stash staged (git stash push --staged)
- [x] Tag: create / delete
- [ ] Tag: checkout tag revision
- [ ] Bisect
- [ ] Submodule ops
- [ ] Worktree ops
- [ ] Archive
- [ ] Patch (format/apply/view)
- [ ] Remotes manage / add upstream
- [ ] Maintenance (gc / fsck / index.lock / config)
- [ ] Clone / Init
- [ ] Reflog
- [x] Blame / File history / Diff / Log
- [ ] Compare (branch / working dir / difftool)
- [ ] Repository-host (GitHub) ops

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
