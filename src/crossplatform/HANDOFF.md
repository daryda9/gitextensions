# HANDOFF — port Linux/Avalonia di Git Extensions

Documento di passaggio per chi (umano o agente) riprende il lavoro.
Fonte di verità dettagliata: **`src/crossplatform/PORTING.md`** (milestone M1–M72,
checklist di parità, metodo del loop). Questo file è il riassunto operativo.

---

## 1. Stato attuale

| | |
|---|---|
| Branch | `linux-avalonia-port` |
| HEAD al momento dell'handoff | `a1a40c3ce` (**M73: superficie del rebase**) · `963e99119` (round 12, M71–M72) · storico: `6b5dff330` (round 11, M67–M70) · storico: `3b73b44bc` (… + **round 9 completo: M52–M61** + **M62 fix del tema scuro sulle console**) |
| Build | `Errori: 0` (31 warning pre-esistenti VSTHRD/CS; nessuno dal codice del round 12) |
| Parità voci UI/funzionali | la **"Coda round 9"** in `PORTING.md` (la misura buona, area per area) è **ESAURITA**: zero voci `[ ]`, zero `[~]`. Restano solo gli SKIP dichiarati — repository-host GitHub, colonna build status, script utente, le ~35 impostazioni senza consumatore |
| Fedeltà UX/visiva | **round 12 commit dialog + merge (M71–M72)** + **round 11 parziali (M67–M70)** + round 1 (T1–T5) + round 2 (M31–M35) + round 3 (M36–M37) + **round 4 rifiniture (M39–M42)** + **round 5 follow-up 1 (M45)** + **round 6 follow-up residui (M46)** + **round 7 feature/GUI (M47–M48)** + M49 fix scroll/selezione grid + **round 8 priorità utente P1–P3 (M50)** + **round 8 pulsanti del pannello inferiore (M51)** |
| Bugfix post-blocco | M43 fetch/pull freeze · M44 `HOME` sbagliato → prompt credenziali a ogni push |
| Packaging | `.deb` self-contained via `packaging/build-deb.sh` |
| Push su remote | **origin NON allineato: ~75 commit locali non pushati** (`origin/linux-avalonia-port` = `757742ce8`, cioè la chiusura del round 10) al momento della stesura (conta esatta con `git rev-list --count origin/linux-avalonia-port..HEAD`). Il push lo esegue l'utente, mai il loop. Portachiavi: se vuoto, il primo push chiede le credenziali **una volta** (username `daryda9` + PAT), poi `git credential approve` le salva in libsecret |

Tutto il codice del port vive in `src/crossplatform/` (albero separato + shim).
La **build Windows non è toccata**; unica modifica al sorgente condiviso: guardie
`OperatingSystem.IsWindows()` in `AppSettings.cs` e `ProcessExtensions.cs`.

### Come avviare / buildare

```bash
export PATH="$HOME/.dotnet:$PATH"          # SDK .NET 10 in ~/.dotnet
cd src/crossplatform
dotnet build App/GitExtensions.Avalonia.csproj -v q   # → Errori: 0
./run.sh                    # GUI sul repo corrente
./run.sh /path/to/repo      # GUI su un repo specifico
./run.sh --selftest [repo]  # headless: branch + log, senza display
```

---

## 2. Cosa c'è (sintesi delle capacità)

- **Finestra integrata** stile FormBrowse: barra menu (Start · Repository · Navigate ·
  View · Commands · GitHub · Plugins · Tools · Help), toolbar, albero sinistro
  (Branches/Remotes/Tags/Stashes/Submodules/Worktrees), revision grid con grafo DAG,
  pannello inferiore a tab, status bar. Tema chiaro/scuro persistito.
- **Toolbar dinamica**: badge `Push ↑N` / `Pull ↓N`, pulsante Commit colorato per stato
  working dir (verde staged / arancio unstaged / dim pulito) con conteggio, dropdown
  inline path-repo (recenti, abbreviati con `~`) e branch (checkout), menu
  `All branches ▾` + casella `Filter:` che pilotano la grid, **menu overflow `»`** per
  gli item che non entrano in larghezza.
- **Revision grid**: pill ref *outline* colorate (branch verde / remote rosso / tag
  ambra), branch corrente in grassetto con ▶, date relative, "Working directory" e
  "Commit index" come **nodi del DAG** in testa alla lista, selezione blu piena a tutta
  larghezza,
  multi-selezione di 2 commit → diff range automatica, quick-search, filtri, scope.
- **Modali**: `CommitDialog` 3-zone (unstaged/staged + diff del file + messaggio e
  bottoni), `PushDialog` di configurazione (remote/branch/force, Pull+Push),
  `GitProcessDialog` stile FormProcess (console beige, `Command to be executed:`,
  **output git live** stdout+stderr, footer Keep-dialog-open/OK/Abort).
- **Pannello inferiore**: Commit · Diff · File tree · GPG · Console · Output ·
  Stash · Blame · File history. Il tab **Console** è un **terminale PTY realmente
  incorporato** (libc P/Invoke, parser VT100/xterm, alternate screen, job control).
  Non c'è nessuna vista "working directory": le sue funzioni vivono nel `CommitDialog`
  (conflitti, discard, copy path, .gitignore) e nel menu `Commands` (Reset changes…,
  Clean working directory…, Undo last commit…), come nell'originale.
- **Commit detail**: avatar identicon, Author/Date rel+abs, Committer, Parent/Child
  come link che navigano la grid, "Contained in branches/tags", "Derives from tag".
- **Operazioni git**: fetch/pull/push (con credenziali in-app + persistenza nel
  credential helper), branch/tag, stash, reset/discard, revert, archive, patch,
  cherry-pick/reword/squash/fixup, bisect, blame, file history, reflog, submodules,
  worktrees, sparse checkout, maintenance, clone/init, remotes manager, difftool.
- **Modello plugin** Avalonia (loader a reflection, MEF non usabile su Linux) +
  plugin reale BackgroundFetch.

---

## 3. Convenzioni e trappole (LEGGERE PRIMA DI TOCCARE)

### Vincoli di processo
- **NON pushare.** **NON firmare i commit**: `git -c commit.gpgsign=false commit …`.
- Conventional Commits. Nessun trailer/co-author.
- **NON** fare refactor multi-target, **NON** toccare la build Windows: lavorare solo
  in `src/crossplatform/`.
- Ogni iterazione aggiorna `PORTING.md`: spunta le voci, registra la milestone (prossima
  libera: **M74**), tiene il contatore iterazione.

### Metodo del loop (delega)
- Il loop **non scrive codice a mano**: pianifica e **delega a subagent Claude in
  worktree isolati** (`isolation: worktree`), 2–3 in parallelo, **file disgiunti**.
- **NON usare subagent Codex con worktree** (perde il lavoro).
- **Regola anti-conflitto**: un solo subagent per iterazione tocca ciascun file *hub*:
  `MainWindow`, `MainMenu`, `MainToolbar`, `RepoObjectsTree`, `RevisionGridView`,
  `DiffView`, `StashPanel`, `CommitDialog`, `PushDialog`, `PullDialog`,
  `GitProcessDialog`, `ConsoleView`, `RepositoryProgressBanner`, `MergeDialog`,
  `ResolveConflictsDialog`.
- **Istruire ogni subagent a COMMITTARE PRESTO E SPESSO**, un commit per tema, senza aspettare la
  fine dell'unità: un limite di sessione ha ucciso tre agent con ~1100 righe non committate
  ciascuno. Se un agent viene interrotto, **riprenderlo con `SendMessage`** (riparte dal suo
  transcript, worktree intatto) invece di rilanciarlo da zero.
- Ogni subagent, come **primo step**: `git reset --hard <SHA_HEAD_corrente>` e verifica
  che `src/crossplatform/App/GitContext.cs` **esista** (se manca, il worktree è partito
  da una base sbagliata → fermarsi).
- Il loop poi: cherry-pick dei tip **UNO ALLA VOLTA** + build check dopo ognuno →
  integrazione minima → verifica GUI → commit → cleanup worktree+branch.

### ⚠️ Incidente da non ripetere (perdita di lavoro sfiorata)
Durante il round 3 un subagent ha spostato il branch del **repo principale**
(`git checkout master` → `prova`) mentre il loop integrava: una commit di integrazione
è atterrata su `prova` con base sbagliata, perdendo M28–M35. Recuperato perché il
commit del subagent aveva parent corretto.

**Regole conseguenti:**
1. Vietare ai subagent `git checkout` / `git switch` / `git branch -f` nel repo
   principale (dirlo esplicitamente nel prompt).
2. **Dopo ogni integrazione, PRIMA di committare**, verificare:
   `git branch --show-current` == `linux-avalonia-port` **e** `git rev-parse HEAD^` ==
   commit atteso.

### Verifica GUI headless (indispensabile per ogni cambio UI)
```bash
DLL="$(find bin -name GitExtensions.Avalonia.dll | head -1)"
xvfb-run -a --server-args="-screen 0 1400x900x24 +extension XINPUTEXTENSION" bash -c '
  python3 <scratchpad>/miniwm.py >/dev/null 2>&1 &        # serve per i MODALI
  dotnet "$DLL" /home/dario/git_ext_mod >/tmp/gui.log 2>&1 &
  APP=$!; sleep 13
  python3 <scratchpad>/click.py                            # click sintetici
  import -window root out.png
  kill $APP'
```
- **Non c'è `xdotool`**: usare `python3` + `python-Xlib` (`fake_input` XTEST).
- Avalonia **ignora gli eventi XTEST core senza `+extension XINPUTEXTENSION`**.
- `ShowDialog` **non mappa la finestra senza window manager** → serve il mini-WM
  (~30 righe python-Xlib). Script riutilizzabili: `miniwm.py`, `click2.py` nello
  scratchpad della sessione (ricrearli se assenti).
- **GUARDARE davvero lo screenshot** (tool Read sull'immagine), non fidarsi del build.
- Agenti concorrenti che condividono lo stesso `DISPLAY` si vedono le finestre a
  vicenda: usare display privati (`:97`, `:137`, …) per verifiche affidabili.
- Le `sleep` di shell vengono **uccise dall'harness** (exit 144) anche in background:
  usare `python3 -c "import time;time.sleep(N)"`. Controllare l'**mtime** dello screenshot
  prima di leggerlo, per non guardare l'immagine di un run precedente.
- Script riusabili in `/tmp/loop-verify/` (`miniwm.py`, `click.py`, `ctrlclick.py`,
  `g2_type.py`, `esc.py`, e in `r8/`: `altclick.py`, `ctrlkey.py`; modello di sessione completa
  in `r9/sess2.sh`).
- **`pkill -f "<pattern>"` uccide la shell che lo lancia** se il pattern compare anche nella
  propria riga di comando (successo garantito con `pkill -f "Xvfb :151"` invocato da un comando
  che contiene quella stringa). Usare un pattern auto-escluso (`Xvf[b] :151`) o `kill <PID>`.
- Per chiudere l'app passando dal salvataggio dello stato usare `Start → Exit` **oppure** la "X"
  / un `WM_DELETE_WINDOW` sintetico (funziona da M58); `kill` invece salta sempre `PersistLayout()`.
- **La sessione esporta `WAYLAND_DISPLAY` e `XDG_SESSION_TYPE=wayland`**: un processo figlio Qt
  (kdiff3, mergetool) **gira ma non mappa nessuna finestra** sotto Xvfb, oppure si apre sul
  desktop reale. Lanciare l'app con
  `env -u WAYLAND_DISPLAY -u XDG_SESSION_TYPE QT_QPA_PLATFORM=xcb` (M72).
- **Un'azione distruttiva può avere una conferma in attesa**: una misura "non ha funzionato" può
  essere solo un dialogo non ancora premuto. **Guardare lo screenshot prima di concludere** (M72).
- Repo di prova già pronti: `/tmp/r9repo` (4 commit, remote locale `/tmp/r9remote`, worktree
  `/tmp/r9wt`), `/tmp/r10repo` (branch `side`, per il merge base), `/tmp/g1repo` (rename),
  `/tmp/v4repo` (blame a bande).
- Per verificare il **grafo** serve una topologia nota: costruire un repo minuscolo in
  `/tmp` (es. `A-B-C-D` più un branch che stacca da `B`) invece di ragionare su
  `git_ext_mod`, dove le lane lunghe rendono ambiguo cosa dovrebbe essere grigio. Attenzione:
  `git_ext_mod` è condiviso con altri agent, un auto-refresh ri-ancora l'evidenziazione a HEAD
  e falsa le misure.

### Convenzioni di codice
- Brush tematici SOLO da `Application.Current.Resources` via l'helper locale
  `B(key)` / `Brush(key, fallback)` (`App.Window`, `App.Panel`, `App.PanelAlt`,
  `App.Control`, `App.Text`, `App.TextDim`, `App.Accent`, `App.Selection`,
  `App.Border`, `App.Toolbar`, `App.GraphGreen`).
- Ogni `Control` custom con `Render` override: **`ClipToBounds = true`**.
- Tutto il lavoro git **fuori dal thread UI** (`Task.Run`), mutazioni UI via
  `Dispatcher.UIThread`. Mai lanciare eccezioni dai path di refresh.
- I service (`RemoteService`, `BranchTagService`, …) bloccano su lavoro async: **non
  chiamarli dal thread UI** (deadlock — già capitato in `PushDialog`), pre-caricare in
  `Task.Run` e passare i dati al costruttore.
- **Split-button / MenuFlyout**: popolare gli `Items` **prima** di `ShowAt`, mai mutarli
  dentro l'evento `Opening` (il popup non ri-misura → si vede solo una riga sottile).
- **Riciclo dei container**: un container riciclato riceve solo un nuovo `DataContext`, quindi
  testo e tooltip costruiti a mano diventano stantii allo scroll. Disabilitando il riciclo,
  attenzione: Avalonia re-invoca il template con un item **null** quando svuota un container →
  il costruttore di riga deve tollerare `null` o l'app crasha (M51, `BlameView`).
- **Tasto destro su una `ListBox`**: muove la selezione, quindi fa scattare gli eventi di
  selezione della view (in M51 questo spostava il pannello inferiore sul tab Commit mentre il
  menu si apriva). Se serve, sopprimere la notifica all'host per quel solo dispatch.
- **Style setter vs valore locale sui figli di template**: impostare `Background` su un `TextBox`
  vale solo per lo stato normale. Il `ControlTheme` Fluent, negli stati `:pointerover`/`:focus`/
  `:disabled`, ridipinge il figlio `Border#PART_BorderElement` dalle chiavi `TextControlBackground*`
  e uno style setter batte il valore locale (fondo `#000000` in scuro, `#FFFFFF` in chiaro). Per una
  superficie che deve restare stabile usare `App/Theming/TextBoxSurface.cs` (M62), che popola le
  chiavi nelle `Resources` dell'istanza tenendo i brush **per riferimento**, così il cambio tema a
  caldo continua a funzionare. Non applicarlo agli **input editabili**: lì il riempimento al focus è
  un'affordance voluta.
- **Sottoclassi di `MenuItem` (e di ogni control con `ControlTheme`)**: Avalonia risolve il tema
  **per tipo esatto**, quindi una sottoclasse non trova template e si dispone ad **altezza zero** —
  la voce *scompare* dal menu lasciando solo uno spazio, con la build verde. Serve
  `protected override Type StyleKeyOverride => typeof(MenuItem);` (M66, `CopyPathsMenuItem`).
- **`IconLoader`**: il nome dell'asset è **case-sensitive** (`avares://…/Assets/Icons/<Name>.png`) e
  i file veri sono in maggioranza PascalCase (`Renamed.png`, `RemoteDelete.png`, `DeleteFile.png`),
  con qualche eccezione minuscola (`plugin.png`, `star.png`). Da M66 un nome che non risolve **si
  logga** una volta per nome all'avvio: **leggere quel log** dopo aver aggiunto un'icona. La cache usa
  un comparer `Ordinal` di proposito.
- **Chiavi `App.*` non registrate**: `Brush("App.X", fallback)` restituisce silenziosamente il
  fallback (che non segue il tema) e `B("App.X")` restituisce **null**. Prima di usare una chiave,
  verificare che sia in `ThemeManager.Keys` + `Dark` + `Light` (M62: `App.Control` era letta in ~20
  punti senza esistere; `App.ConsoleBackground`/`App.ConsoleForeground` restano volutamente fallback).
- **Virtualizzazione**: ri-assegnare la *stessa* istanza di lista a `ItemsSource` non
  ricrea i container già realizzati → le righe visibili restano con i visual vecchi e il
  cambio si vede solo su quelle che entrano dopo. Assegnare una **nuova** lista (scoperto
  in M50 su `RebindRows`, colpiva tutti i toggle basati su `RefreshView`).
- Il ripristino dello scroll va riapplicato a `DispatcherPriority.Background`: al primo
  tentativo (`Loaded`) l'extent del pannello è ancora corto e l'offset viene clampato.
- **Fluent dipinge i `Button` con un overlay `ButtonBackground*` traslucido**: su un fondo
  colorato (il banner arancione) il testo può crollare a ~2:1 e diventare invisibile, e un
  `Background` locale **perde** contro gli style setter del `ControlTheme` in `:pointerover`.
  Rimedio: pinnare le chiavi di stato nelle `Resources` dell'istanza (tecnica di
  `TextBoxSurface`, M62) — vedi M72.
- **`SelectableTextBlock` e `TextBlock` non impaginano lo stesso font allo stesso passo**
  (19,0 vs 17,9 px/riga misurati): affiancarli, come per un gutter di numeri di riga, richiede
  un `LineHeight` esplicito uguale; `VerticalAlignment=Top` non basta (M72).
- **Sottoscrivere `ComboBox.TextProperty` spara subito**: un'eccezione dal costruttore lascia
  un dialogo che **non si apre mai**, con log pulito e finestra X non mappata (M72).
- **`isNewFile` di `PatchManager` significa "nuovo nell'INDICE", non "untracked"**: riscrive
  `--- /dev/null` in `--- a/<name>` e toglie `new file mode`, rendendo il patch inapplicabile a
  un path assente dall'indice. Per il line-staging su untracked serve un'altra strada (M71).
- **`FirstLine()` su output streaming pesca l'header del comando**, non l'errore (M71).
- **Path in `GitArgumentBuilder` vanno quotati** (`.Quote()`): gli argomenti finiscono in
  un'unica command line che git ri-splitta, quindi un path con uno spazio arriva come **due**
  argomenti e l'operazione **fallisce in silenzio** (M72, take-ours sui conflitti).
- Per output git live usare `Services/GitStreamRunner.cs` (Process diretto, stdout+stderr
  async): il core `IExecutable`/`IProcess` **bufferizza stderr**, dove git scrive il
  progress.

### Ambiente / credenziali
- SDK: `~/.dotnet` (`export PATH="$HOME/.dotnet:$PATH"`).
- git su questa macchina usa `~/.local/bin/git-credential-libsecret` (compilato dal
  contrib di git) su gnome-keyring. **Il portachiavi va seminato una volta** con un
  login vero: username `daryda9` + **Personal Access Token** GitHub (`origin` è https).
  Finché non è seminato, il primo push chiede le credenziali nel dialog dell'app (che
  poi le salva da sé via `git credential approve`).

---

## 4. Cosa resta da fare

> ### ► M73 (2026-07-30) — **superficie del rebase**. Prossima milestone libera: **M74**
> Nata da una domanda dell'utente sul rebase fermo in `~/test-avalonia`. `RebaseSessionService` +
> `Continue`/`Skip`/`Abort` (e `Resolve…` quando ci sono conflitti) nel banner, `GIT_EDITOR=true`
> pinnato perché `--continue` su un `edit` altrimenti aspetta `vi` e pianta il process dialog, e i
> quattro entry point del rebase ora chiedono dei conflitti (prima non potevano: non c'era modo di
> finire il rebase). Corretto anche un contrasto: l'inchiostro **derivato** del banner non seguiva il
> cambio tema a caldo (3,52:1 → 5,97:1), difetto che riguardava anche la barra del merge.
> **Restano senza service dietro `--continue`**: cherry-pick e revert (solo suggerimento testuale nel
> banner) e l'editing del todo interattivo (`--edit-todo`), che sarebbe un'unità a sé.

> ### ► ROUND 12 (2026-07-29) — **CHIUSO** (M71–M72)
> Le priorità dell'utente del 29/07/2026 (commit dialog + flusso di merge) sono **tutte chiuse in due
> iterazioni** su dieci concesse, sei subagent Claude in worktree isolati. Dettaglio in `PORTING.md` →
> "ROUND 12". Il flusso degli screenshot è stato percorso end-to-end dal loop: merge → process dialog
> → conferma → resolve → **kdiff3 vero** → `--continue` → banner spento.
>
> **Cosa c'è ora**: `MergeDialog` (port di `FormMergeBranch`, opzioni avanzate tutte cablate a
> `Commands.MergeBranch`), il merge nel `GitProcessDialog`, `ConflictFlow` (port di
> `MergeConflictHandler`: la domanda "solve conflicts now?" su merge/pull/cherry-pick/revert/stash
> apply-pop/`git am`), `ResolveConflictsDialog` + `ConflictService` (sei tipi di conflitto da
> `ls-files -u`, `Open in <merge.tool>` che apre davvero kdiff3), il banner del merge con
> `Resolve…`/`Continue`/`Abort` + `MergeSessionService`, il commit dentro il process dialog (output
> degli hook visibile), `ResetChangesDialog`, il diff dei file untracked, e la chrome del commit dialog
> (status bar upstream, gutter a due colonne, toolbar e filtro per lista).
>
> **Residui registrati, nessuno bloccante**: lo stato "conflitti senza operazione in corso" non è nel
> banner (costerebbe un `git diff` a ogni refresh); rebase/`am`/cherry-pick/revert non hanno pulsanti
> di continue nel banner (nessun service dietro); la scelta fast-forward del `MergeDialog` è ricordata
> globalmente e non per repo; `AvaloniaGitUICommands.StartResolveConflictsDialog` resta
> `NotSupported` (firma sincrona `bool`, decisione semantica); `DontConfirmResolveConflicts` è un flag
> senza UI perché il port non ha la pagina Confirmations; i commenti stantii in
> `ApplyPatchDialog.cs:51` e `PullDialog.cs:718`.

> ### ►► Le voci originali della priorità utente (tutte chiuse, tenute per riferimento)
> Lista operativa completa, con `file:riga` verificati al `6b5dff330` e la descrizione degli
> screenshot: `PORTING.md` → **"Coda round 12 — PRIORITÀ UTENTE del 29/07/2026"**.
> Screenshot in `~/Documents/images avalonia/` (letti e verificati: 00–03 merge, commit window,
> resolve conflicts, banner da home, create branch).
>
> **A — dialogo di commit** (`App/Views/CommitDialog.cs`):
> 1. **Nuovo file → diff vuoto.** `PatchStagingService.cs:76-92` fa `git diff -- <path>`, che per
>    un **untracked** non produce nulla: pannello bianco, nessun errore. Upstream mostra il
>    contenuto intero del file.
> 2. **Il commit non passa dal process dialog.** Upstream commita dentro `FormProcess`
>    (`FormCommit.cs:1265`); il port esegue in silenzio (`CommitActionsService.Commit:54-79`,
>    `CommitDialog.DoCommit:2331` → solo `SetStatus`) → hook e messaggi di git invisibili. Il
>    `GitProcessDialog.RunStreamingAsync` è già usato per il push (`CommitDialog.cs:2556`).
> 3. **`Reset all changes` / `Reset unstaged changes` sbagliati.** Upstream instrada entrambi su
>    `FormResetChanges` (`FormCommit.cs:2184-2198`), che decide anche cosa fare degli
>    **untracked**, e li disabilita quando le liste sono vuote (`:831`, `:2806`). Nel port
>    `DoReset` (`:2564-2575`) fa `reset --hard HEAD` dietro una conferma generica e, sul ramo
>    unstaged, `git checkout -- .` **senza conferma** — distruttivo e silenzioso.
> 4. **Chrome lontana da `FormCommit`**: liste `ListBox` nude senza toolbar per lista, **una**
>    sola casella filtro invece di due, nessun **gutter di numeri di riga** nel diff, manca la
>    status bar `Committer · branch → remote · Staged x/y Ln y Col x`. Riusare
>    `Views/FileStatusListView.cs`, non ricostruire. **Niente pulsanti finti.**
>
> **B — flusso di merge, oggi MUTO.** `BranchTagService.MergeBranch:633-647` ha i flag
> **cablati** e i quattro call-site (`RepoObjectsTree.cs:1240`/`:1299`, `BranchTagPanel.cs:283`,
> `RevisionGridView.cs:6018`) lo lanciano in un `RunMutation`: nessun dialogo, nessun process
> dialog, nessuna conferma; in caso di conflitto l'utente lo scopre solo aprendo il commit
> dialog. Nessun port di `FormMergeBranch` (187 righe) né di `FormResolveConflicts` (1571).
> Serve la catena intera: **MergeDialog** (img 00) → **GitProcessDialog** (img 01) → conferma
> **"solve conflicts now?"** (img 02, port di `MergeConflictHandler.cs:9-27`, da agganciare a
> *tutti* i produttori di conflitti: merge/pull/cherry-pick/revert/rebase/stash apply) →
> **ResolveConflictsDialog** (img 03) con **Open in \<mergetool letto da `merge.tool`\>** e
> **Start mergetool** che aprono davvero kdiff3/meld (`WorkingDirectoryService.cs:134-173` sa
> già lanciare `git mergetool --no-prompt --` detached) → **banner con `Resolve…`/`Abort`**
> (`RepositoryProgressBanner.cs:300`/`:335` oggi si limita a *suggerire* `git merge --abort`,
> cioè manda l'utente in terminale).
>
> I dati/API dietro ogni voce esistono già: `Commands.MergeBranch` accetta
> fast-forward/squash/no-commit/strategy/unrelated-histories, e
> `WorkingDirectoryService.ListConflicts:110-118` elenca i conflitti.

> ### ► ROUND 11 (2026-07-29) — **CHIUSO** (M67–M70). Prossima milestone libera: **M71**
> **Esito**: nella "Coda round 9" non resta **nessuna** voce `- [ ]` né `- [~]`. Il round ha chiuso
> 4.1, 4.11 per intero, 3.2 per intero, i tre banali, l'auth-failure indipendente dalla locale, i
> file picker e la palette di sintassi.
> **M70** (iterazione 4) ha aggiunto le cinque chiavi `App.Token*` per **entrambi** i temi (chiaro
> 5,89–10,79 · scuro 5,67–9,01, e la separazione a coppie sale da ΔE 2,4 a ≥17,6 anche in
> simulazione deuteranope/protanope) e ha portato i **managed file picker** sulla palette dell'app
> (`App/ManagedFileChooserTheming.cs`, fondo = `App.Window` misurato identico alla finestra
> principale).
>
> Cose scoperte in M70 da NON riscoprire:
> - **Il tab File tree colora SEMPRE** (`FileTreeView.cs:534`, `highlight: !binary`) e **non ha un
>   toggle**: in tema chiaro l'inchiostro scuro era lo stato di default, non un caso limite.
> - **L'highlighter dipinge sopra una tinta di fondo** (alpha `0x28`): il fondo vero delle righe
>   `+`/`-` è `#2A392C`/`#3C2A2A` in scuro e `#DEECDF`/`#F0DEDE` in chiaro. Misurare il contrasto
>   contro `#FFFFFF`/`#1E1E1E` dà il numero **sbagliato** e nasconde due fallimenti AA in scuro.
> - **La distinguibilità dei token va verificata in simulazione daltonica**: il grappolo
>   verde/oliva/rust collassa in tonalità, quindi la separazione deve venire dalla **luminosità**.
> - **`BindingPriority.Template` (2) batte `Style` (3)**: un setter di stile su un figlio di template
>   può essere **silenziosamente morto** — è il caso *opposto* alla nota su `TextBoxSurface`.
> - **Le icone del `ManagedFileChooser` non sono sovrascrivibili**: sono `DrawingGroup` nelle
>   `Resources` del ControlTheme stesso, raggiunte con `StaticResource` (parent stack a build time,
>   e quel dizionario è il primo elemento). Il resto sì: sei chiavi brush, tutte `DynamicResource`.
>   `ManagedFileDialogOptions.ContentRootFactory` non è una via: `AvaloniaLocator.CurrentMutable` è
>   `internal` in 11.3.9. Selettore morto in Fluent: seleziona `ListBox#QuickLinks` ma l'elemento è
>   `PART_QuickLinks`.
>
> ### ► ROUND 11 iterazione 3 — **M69**
> **M69** (iterazione 3) ha chiuso **4.11** e **3.2**, cioè **le ultime `[~]` della coda round 9: ora
> sono ZERO**. `RemotesDialog` ha il tab "Default pull behavior" + push URL separata; `FormVerify` è
> portato come `App/Views/VerifyDialog.cs` (+ `VerifyService`) con recupero vero in `LOST_FOUND_*`;
> `ArchiveDialog` sceglie la revisione e fa tar semplice; `SparseDialog` è allineato al **legacy** di
> upstream così la **negazione `!` funziona**; `AboutDialog` è completo; e la persistenza residua
> (diff viewer, file history, filtri del left panel, MRU dei filtri avanzati) vive in un
> **`view-prefs.json` separato** (`App/Services/ViewPrefsService.cs`).
> Resta solo la voce nuova **palette di syntax highlighting per il tema chiaro** (da valutare) e la
> nota estetica sui managed dialog che non seguono il tema.
>
> Cose scoperte in M69 da NON riscoprire:
> - **`git fsck` è localizzato** (`commit non raggiungibile`): la regex inglese parsa **zero oggetti
>   uscendo con 0**, indistinguibile da un repo sano. Ogni fsck gira dentro
>   `GitEnvironment.DiagnosticLocaleScope()` — l'infrastruttura di M67 serve anche qui, e servirà a
>   chiunque parsi output di git.
> - **Il setter `TrackingRemote` del core auto-semina `branch.<x>.merge`**: scrivere subito dopo la
>   casella merge (vuota) la **cancella**, lasciando un ramo su cui `git pull` non funziona. Scrivere
>   solo i campi che l'utente ha cambiato.
> - **`Button.Content` come stringa mangia `_` come access key** (`LOST_AND_FOUND` →
>   `LOSTAND_FOUND`): usare un `TextBlock` figlio.
> - **Il cone mode non può esprimere la negazione**: `sparse-checkout set --cone '!x'` fallisce con
>   *"Specify directories rather than patterns"*. Per la parità serve il legacy.
> - **Disabilitare lo sparse nell'ordine di upstream è un no-op silenzioso** su git 2.43.0: con
>   `core.sparsecheckout=false` già scritto, `read-tree -m -u HEAD` non ricalcola `skip-worktree`.
>   E **`.git/config.worktree` batte `.git/config`**, quindi va azzerato anche quello.
> - **Chi scrive stato da una view non posseduta da `MainWindow`** (una seconda istanza di `DiffView`
>   nel `CommitDialog`, un modale già chiuso) deve usare un **file separato** come
>   `view-prefs.json`/`commit-info.json`: l'host riserializza `UiState` alla chiusura e lo
>   sovrascriverebbe.
> - **Ambiente**: gli Xvfb orfani si accumulano fra i round (25 vivi a un certo punto, 31/46 GB
>   occupati → app uccise dall'OOM senza eccezione né exit file). Ripulirli fa parte del metodo. E
>   `import -window root` subito dopo la chiusura di un `MenuFlyout` può restituire un PNG **tutto
>   nero** da ~290 byte con l'app viva: ricontrollare, non concludere che sia crashata.
>
> ### ► ROUND 11 iterazione 2 — **M68**
> **M68** (iterazione 2) ha chiuso il grosso di **4.11**: **bisect** (`App/Views/BisectDialog.cs`,
> gating su `InTheMiddleOfBisect`, banner con conteggi veri, **niente più auto-start silenzioso**),
> la **macchina a stati `git am`** (`AmSessionService` + `ApplyPatchDialog`, PatchGrid,
> Resolved/Skip/Abort) e la verifica end-to-end di **clean/init/clone**, le cui voci di coda erano
> **stantie** (erano già portate: `clean -X` raggiungibile, `FormInit` esistente, il clone completo).
> Restano di 4.11: `RemotesDialog` (tab "Default pull behavior" + push URL), `FormVerify`,
> `ArchiveDialog` (filtro path/revisione), `SparseDialog` (cone mode), `AboutDialog`.
>
> Cose scoperte in M68 da NON riscoprire:
> - **`GitArgumentBuilder` ri-splitta gli argomenti che contengono spazi**: finiscono appiattiti in
>   un'unica `ProcessStartInfo.Arguments`, quindi `--format=%(refname) %(objectname)` arriva a git
>   come **due** argomenti — exit 0 e colonna mancante **in silenzio**. Quotare o evitare gli spazi.
> - **La riga di progresso di `git bisect` è localizzata**: i conteggi vanno presi da
>   `git rev-list --bisect-vars`, non raschiati dall'output.
> - **git ignora `--depth` per i cloni da path locale**: lo shallow si vede solo con un URL `file://`.
> - **`SizeToContent.Height` è una richiesta**, non una garanzia: un WM può ignorarla e lascia una
>   banda non dipinta (era il difetto della finestra di init).
> - **Le voci di coda invecchiano**: tre unità su tre dell'iterazione 2 (clean/init/clone) erano già
>   fatte. **Prima di delegare, verificare la premessa** contro il codice all'HEAD vero.
>
> ### ► ROUND 11 iterazione 1 — **M67**
> **M67** ha chiuso **4.1** (checkout di rami remoti: `App/Views/CheckoutBranchForm.cs`, port completo
> di `FormCheckoutBranch`, su `Commands.CheckoutBranch` del core via `BranchTagService.CheckoutBranch`),
> i tre banali (warm-up del `Lazy<Encoding>` in `Program.cs`; Ctrl+Shift+N che ora **lega** davvero
> `AddNotesDialog`; la **terna** delle pill ref tematizzata, da 3 fallimenti WCAG su 6 a 0) e
> l'**auth-failure indipendente dalla locale**. Restano i `[~]` 4.11 (dialoghi) e 3.2 (persistenza),
> più la palette di syntax highlighting per il tema chiaro.
>
> Cose scoperte in M67 da NON riscoprire:
> - **`LC_ALL` sovrascrive `LC_MESSAGES`**: per avere diagnostiche git in inglese non basta
>   `LC_MESSAGES=C`, va **rimosso `LC_ALL`** (col suo valore travasato in `LC_CTYPE`, per non perdere
>   l'encoding) e azzerato `LANGUAGE`. Fatto in `App/Services/GitEnvironment.cs`, applicato ai git
>   *del port* — **non** alla Console incorporata, dove `PtyProcess.Start` ripristina la locale vera
>   dell'utente.
> - **Sul path PTY l'output di git NON arriva a `onLine`**: va al terminale come byte grezzi, quindi
>   ogni matcher di testo su fetch/pull/push del process dialog era cieco **anche in inglese**. Il
>   segnale robusto sono i **verbi del credential helper** (`get`/`store`/`erase`): `erase` va a
>   *tutti* gli helper (a differenza di `get`, dove un helper `-c` è consultato ultimo), quindi la
>   sonda vede sempre il rifiuto (`App/Services/GitAuthProbe.cs`, verdetto trasportato da
>   `GitAuthSignal` via `AsyncLocal`).
> - **Le pill ref non erano un problema di una tinta**: la riga selezionata scambiava il fondo con un
>   **bianco opaco hard-coded**, e nessuna singola tinta può superare 4,5:1 sia su `#252526` sia su
>   bianco (serve luminanza ≥ 0,254 e ≤ 0,183). Va rimosso il fondo hard-coded, non ritoccata la
>   tinta. Chiavi nuove: `App.RefPillBg`, `App.RefBranch`, `App.RefRemote`, `App.RefTag`.
> - **Voci di parità spuntate su mezze verità**: 1.10 (AddNotes) risultava fatta perché la gesture
>   era *dichiarata* in `HotkeyService` — ma `InstallHotkeys` non la legava. Quando una voce dice
>   "hotkey X fatta", verificare il **binding**, non la dichiarazione.
> - **4.1 era in parte già fatta** da round 10 (voce nell'albero e in testa al dropdown branch): il
>   residuo era il dialogo e il fatto che toolbar/`Ctrl+.` andassero a un picker solo-locale.
>
> ### ► ROUND 10 (2026-07-28) — CHIUSO. Milestone M63–M66
> **M63** ha chiuso le nove voci banali (0.17, 0.33–0.39, 1.24); **M64** le **tre leve**: 1.14b
> (modalità worktree/index in `DiffService` + contenuto nei quattro tab per le righe artificiali),
> 4.8 (`GitProcessDialog` su **PTY**: progress dai `\r`, prompt interattivi rispondibili, Abort che
> fa rimuovere a git il proprio `index.lock` via SIGINT), 4.9 (**file history sulla griglia vera**
> via `LoadFileHistory`, seconda istanza della grid). Nella coda round 9 **non resta nessuna `- [ ]`**:
> solo i `[~]` parziali (4.10 residuo toolbar, 3.2, 3.3, 4.1, 4.11).
>
> Cose scoperte in M63/M64 da NON riscoprire:
> - **1.24 era già cablata** da M56 (voce di coda stantia).
> - **Ctrl+W**: non il PTY, ma l'allowlist di `IsGestureOwnedByFocusedView` (il dispatcher hotkey
>   **tunnela**, vede la gesture prima del terminale).
> - **Sentinel delle righe artificiali invertiti** rispetto al core (`WorkTreeId=1111`,
>   `IndexId=2222`): allineati in M64, ma il cablaggio usa il `kind` dell'evento, non l'hash.
> - **`--follow` è fragile**: con più ref di partenza o `--topo-order` **tronca in silenzio** al
>   rename, e `--skip` oltre quel commit dà una pagina vuota. Un solo commit di partenza, date
>   order, paging per allargamento della finestra.
> - **`ExecutableExtensions.cs:15` del core**: `Lazy<Encoding>(isThreadSafe: false)` → le prime due
>   chiamate git **concorrenti** di un processo lanciano `InvalidOperationException`. ✅ **RISOLTO in
>   M67** con un warm-up di una riga in `Program.Main` (`ExecutableExtensions.GetOutput` con
>   `outputEncoding: null` è l'unico membro pubblico che materializza il Lazy prima di avviare il
>   processo). Misurato: 40/40 fallimenti a freddo → 0/40. Il core non è stato toccato.
> - **Rilevamento auth-failure solo inglese** (`LooksLikeAuthFailure`, marker in `RemoteService`/
>   `PushRefsService`): con git in italiano il fallback `CredentialsDialog` non si apre. ✅ **RISOLTO
>   in M67** (pinning di `LC_MESSAGES` + sonda sui verbi del credential helper; A/B verificato in
>   GUI). Compromesso accettato: le diagnostiche git nella console del process dialog sono ora
>   inglesi anche per un utente italiano.
> - `MainWindow.OpenRepository` → `RecordRecentAsync` non fa attecchire un repo appena clonato nella
>   MRU (coperto lato `CloneDialog`). Aperto, meccanismo dentro il core.
>
> ### ► ROUND 9 (2026-07-27/28) — la lista buona è in `PORTING.md` → **"Coda round 9"**
> Otto subagent READ-ONLY hanno auditato tutta la GUI area per area contro l'upstream. La coda che
> ne è uscita (blocchi 0–4 + **rinviati con motivo**) è la fonte di verità di cosa manca: usarla,
> non gli elenchi storici qui sotto. Milestone chiuse finora: **M52** (correttezza), **M53** (menu
> Navigate/View, griglia, file history, commit dialog, toolbar), **M54** (albero sinistro, File
> tree, GPG). Prossima libera: **M55**.
>
> Tre cose che l'audit ha stabilito e che conviene NON riscoprire:
> - **`FormBrowse` upstream non ha una status bar**: la `StatusBarView` del port è un extra da
>   tenere. Idem il tab inferiore persistito; upstream non persiste larghezza/ordine colonne.
> - Lo scope hotkey `FormBrowse` è in parità **verbatim 43/43**; mancano gli altri sei scope.
> - **~35 impostazioni upstream sarebbero pulsanti finti** nel port (nessun consumatore): avatar
>   provider/cache, `UseGitColoring`, ruler, `OutputHistoryDepth`, font, rendering del grafo.
>
> **RISOLTO in M58 — e la diagnosi che girava da round era sbagliata.** Non è vero che "Avalonia
> non espone `WM_DELETE_WINDOW`": Avalonia lo implementa. È che `X11Atoms.PopulateAtoms` (11.3.14)
> chiama `XInternAtoms` con `only_if_exists: true`, quindi **su Xvfb nudo**, dove nessun client
> precedente li ha creati, tutte e 78 le lookup tornano 0 e la tabella degli atomi resta azzerata
> — muoiono il protocollo di chiusura, tutte le `_NET_WM_*` e la clipboard. Fix:
> `Services/X11AtomPrimer.cs`, un `XInternAtoms(..., only_if_exists: false)` da `Program.Main`.
> **Conseguenza sul metodo**: l'intera attrezzatura headless di questo progetto gira su Xvfb nudo,
> quindi ogni verifica passata di maximize / window type / clipboard ha misurato un ambiente
> storpio. E su un desktop vero la "X" probabilmente **funzionava già**.

**Il blocco RIFINITURE (round 4, M39–M42) è CHIUSO**: tutti i residui A1–C10 e D11–D12
elencati nelle versioni precedenti di questa sezione sono stati risolti e verificati in
GUI. Dettaglio per milestone in `PORTING.md` → "Blocco RIFINITURE (round 4)".

### Follow-up aperti (nessuno bloccante)

> **PRIORITÀ dell'utente del 27/07/2026 — CHIUSE in M50 (round 8)**, tranne un residuo di P2.
> Dettaglio in `PORTING.md` → "Blocco PRIORITÀ P1–P3 (round 8)".
> **P1 — grafo: FATTA.** Relatives = ancora + soli antenati, Alt+clic ri-ancora (clic normale
> no), lane/nodo/testo non-relative in grigio, `_drawNonRelativesGray` default `true` come
> upstream. Limite: la relatività dei segmenti è dedotta dalle lane, quindi una lane riusata da
> un parent di merge può restare colorata.
> **P2 — chrome: FATTA per 2a+2b** (barra pulsanti + casella di ricerca sopra l'albero, icone
> nei nove tab del pannello inferiore). **RESTANO 2c e 2d**: gli altri pulsanti/split-button
> della toolbar in alto, la toolbar ricca della lista file (`FileStatusList.Toolbar.cs`) e le
> opzioni del viewer diff (`FileViewer.Designer.cs:27-48`).
> **P3 — Pull: FATTA.** Split-button (corpo = azione predefinita persistita in
> `UiState.DefaultPullAction`, freccia = menu upstream con il sottomenu "Set default"),
> `PullDialog` su modello `FormPull`, `PullOptions` in `RemoteService` con tag policy/prune/
> autostash + `FetchAll`/`FetchAndPruneAll`. I due `rebase: false` cablati non esistono più;
> corrette anche le hotkey invertite (Ctrl+Down = dialogo, F8 = azione predefinita).

1. ✅ **RISOLTO M45** (round 5, una iterazione) — `WorkingDirectoryView` **non esiste più**.
   Conflitti di merge e menu contestuale per file (discard / copy path / tre voci
   .gitignore) sono nel `CommitDialog`; "Reset changes…", "Clean working directory…" e
   "Undo last commit…" sono nel menu `Commands` negli slot di `FormBrowse.Designer.cs`;
   finestra utility e `Ctrl+Shift+W` rimossi. `App/Services/WorkingDirectoryService.cs`
   **resta** (backend dei nuovi chiamanti). Dettaglio in `PORTING.md` → "Blocco FOLLOW-UP 1
   (round 5)". Code smell residui, tutti minori e registrati lì: discard solo mono-file
   (liste `SelectionMode.Single`), niente drag&drop tra liste (assente anche
   nell'originale), acceleratori Enter/Space/Ctrl+Enter non replicati, e la guardia
   "Nothing staged to commit." che può rifiutare un merge commit legittimo (servirebbe
   rilevare `MERGE_HEAD`).
2. **Traduzioni** — **infrastruttura FATTA in M46/T1**: `.xlf` copiati in output e nel
   `.deb` (66 file), `App/Services/TranslationService.cs` (riusa il loader XLIFF del core,
   sostituisce il matcher WinForms con lookup per id **e** per `<source>` inglese
   normalizzato), selettore **View → Language** persistito in `UiState.Language`, cambio
   lingua senza riavvio. **Resta**: applicare il layer a tutte le view oltre a `MainMenu`.
   Convenzione: `T("<Categoria>/<Item>.<Prop>", "English literal")` con la categoria =
   `<file original>` dell'XLIFF (la form upstream corrispondente: `FormCommit` per
   `CommitDialog`, `FormPush` per `PushDialog`, `RepoObjectsTree`, `RevisionGrid`,
   `FormBrowse` per la chrome); senza equivalente upstream, `T("English literal")`. Le view
   devono ricostruirsi su `TranslationService.LanguageChanged` (pattern in `MainMenu`).
   Minori: flash di ~1 s all'avvio con lingua non inglese; 19 MB di cataloghi filtrabili.
3. ✅ **RISOLTO M44** — `HOME` riscritto dal core: `App/HomeDirectoryFix.cs` semina
   `AppSettings.CustomHomeDir` con la home vera da un `[ModuleInitializer]`. Diagnostica in
   `./run.sh --selftest`: riga `[11]` = HOME per i git figli, `[12]` = `credential.helper`
   risolto. Il difetto di fondo resta **nel core condiviso** (`GetDefaultHomeDir()` legge
   `HOME` dai target `User`/`Machine`, che su Unix sono sempre `null`): se un giorno si
   tocca il core, è lì che va corretto.
4. ✅ **RISOLTO M46/T2** — header della revision grid con path abbreviato `~/…` (più
   ellissi e tooltip). Restano: i due `CollapseHome` duplicati (`MainToolbar.cs` e
   `RevisionGridView.cs`) da unificare, e `PushDialog.cs:95` che stampa ancora il path
   assoluto nel titolo.
5. ✅ **RISOLTO in M67 — i file picker FUNZIONANO**, con `UseManagedSystemDialogs()` in
   `Program.BuildAvaloniaApp`. La diagnosi che girava ("serve un portal XDG") era sbagliata: sulla
   sessione **Wayland/XWayland reale** dell'utente il portal c'è e risponde (`FileChooser` version 3,
   backend `xdg-desktop-portal-gnome` **e** `-gtk` attivi; una chiamata manuale a
   `org.freedesktop.portal.FileChooser.OpenFile` via `gdbus` viene servita e restituisce un request
   handle), ma `dbus-monitor` vede **zero traffico dal processo dell'app** quando si premono i
   `Browse…`: lo `StorageProvider` X11 di Avalonia non arriva mai al portal e
   `OpenFolderPickerAsync` torna lista vuota **senza eccezione**. I dialoghi *managed* girano
   in-process, quindi funzionano sia sul display reale sia su Xvfb — e da ora i picker sono
   **verificabili headless**. Verificato end-to-end: `Ctrl+O` → `Browse…` → il picker elenca `/tmp`
   con i bookmark veri della sidebar → path digitato `/tmp/r9repo` + OK → il repo si apre
   (5 commit, 2 stash, 2 worktree). Nota estetica: i managed dialog **non seguono il tema** dell'app
   (fondo nero, icone ambra), da guardare un giorno. Restano no-op solo shim `Compat/`
   **irraggiungibili** dal port (censiti in M42/D12).
   *Metodo che ha funzionato sul display reale*: XTEST **tastiera** funziona (`set_input_focus` +
   `Ctrl+O`, oppure `_NET_ACTIVE_WINDOW` + `Tab`/`space`), il **puntatore no** — `fake_input`
   MotionNotify viene ignorato/clampato da mutter, quindi niente click sintetici su Wayland. Gli
   screenshot vanno presi **per finestra** (`import -window <id>`), non su root: il root di XWayland
   non mostra le finestre Wayland.
6. ✅ **RISOLTO — il clipboard FUNZIONA anche headless** (verificato in M65 con `xclip`:
   `Copy to clipboard → Commit hash` restituisce l'hash esatto di `git rev-parse`, `Copy file path`
   il path del file). La vecchia misura "clipboard X11 inerte sotto Xvfb" era un altro sintomo della
   tabella atomi azzerata corretta da M58. **Resta** una divergenza di contenuto: upstream copia il
   path **assoluto nativo** da un sottomenu con default in grassetto
   (`CopyPathsToolStripMenuItem.cs:44-50`), il port copia il relativo.

### ⚠️ Classe di bug ricorrente: il core condiviso su Linux
M43 e M44 sono lo stesso genere di difetto — codice del core che assume Windows o un thread
WinForms e si rompe qui. Quando qualcosa "si blocca" o "non persiste", sospettare prima
questo:
- **sync-over-async chiamato dal thread UI** → deadlock totale, spesso *prima* che appaia
  qualsiasi dialog, quindi sembra un freeze inspiegabile (M43: `RemoteService.ListRemotes`).
  Difesa: i service ora usano `RunDetached` (hop sul thread pool), ma i chiamanti devono
  comunque stare fuori dall'UI thread.
- **API .NET che su Unix rispondono `null`/vuoto** dove su Windows hanno un valore (M44:
  `GetEnvironmentVariable(..., Target.User/Machine)`), con fallback silenzioso su un
  percorso plausibile ma sbagliato.
Metodo che ha funzionato: riprodurre headless, verificare se l'UI risponde ancora a un click
(se no → thread UI bloccato), guardare se il processo git è stato **davvero** avviato, e
infine A/B con e senza fix.

### Fuori scope (SKIP consapevoli — le 3 voci mancanti al 100%)
- **Repository hosts (GitHub)**: fork / view-create PR / add upstream. Realizzabile come
  plugin repository-host (l'infrastruttura plugin esiste), non incluso.
- **Colonna build status**: richiede integrazione con un build-server/CI.

---

## 5. Prompt pronto per riprendere

**Round 11 (M67–M70) è chiuso, e con esso la "Coda round 9": non resta nessuna voce `- [ ]` né
`- [~]`.** HEAD alla chiusura: `c11c183a9`, poi `6b5dff330` (drop di NOTES.md).

> **Il round 12 è CHIUSO** (M71–M72): commit dialog e flusso di merge completi, verificati end-to-end
> in GUI. Prossima milestone libera: **M73**. Non c'è una coda aperta: il prossimo round deve prima
> **decidere cosa vale la pena fare**. Materiale noto sotto, più i residui del round 12 in §4.

Materiale noto e già motivato, dietro le priorità:

- **SKIP consapevoli, non riaprire senza una ragione nuova**: repository-host GitHub (fork / PR /
  add upstream — sarebbe un plugin repository-host), colonna **build status** (serve un build server),
  **script utente** (`ScriptsManager`/`ScriptInfo` + hook Before/After: è un sottosistema a sé, la
  voce più grossa fra i rinviati), le **~35 impostazioni** che sarebbero pulsanti finti (censite nel
  cappello della coda round 9), i **6 scope hotkey** oltre a `FormBrowse`.
- **Note estetiche aperte, piccole**: le **icone ambra** del file picker managed non sono
  sovrascrivibili (prova strutturale in M70) e stonano in tema chiaro; la pill **note** del grafo è un
  chip scuro in tema chiaro (5,34:1, passa AA); la coppia scura **string↔comment** della sintassi
  resta la più debole per un protanope (ΔE 2,4 → misurata, non risolta); le stringhe dei conteggi del
  bisect non sono **pluralizzate** ("1 revisions left") — servirebbe un formatter plural-aware in
  `TranslationService`.
- **Debito noto**: `TranslationService` è applicato a `MainMenu` e a parte delle view, non a tutte
  (convenzione in §4); i due `CollapseHome` duplicati; `PushDialog.cs:95` che stampa il path assoluto
  nel titolo; la guardia "Nothing staged to commit." che può rifiutare un merge commit legittimo
  (servirebbe rilevare `MERGE_HEAD`); il discard solo mono-file.
- **Idee di valore vero, se si vuole continuare**: un **giro di collaudo end-to-end** su un repo
  grosso e reale (prestazioni del grafo, paging, memoria) invece di nuove feature; oppure alzare la
  copertura del layer di traduzione; oppure il sottosistema **script utente**, che è l'unica lacuna
  funzionale grossa rimasta.

Prima di aprire un round nuovo, rileggere in `PORTING.md` le milestone **M67–M70** e in questo file
le sezioni **3** e **4**: il round 11 ha aggiunto una dozzina di trappole riusabili (output di git
localizzato, `GitArgumentBuilder` che ri-splitta gli argomenti, priorità `Template` > `Style`,
`.git/config.worktree`, e il fatto che **le voci di coda invecchiano**: nell'iterazione 2 tre unità
su tre erano già state fatte).

Scheletro di prompt per `/loop`, da riempire con le voci scelte:

```
Continua il port Linux/Avalonia di Git Extensions in src/crossplatform/ — ROUND 12: <TEMA>.
Branch: linux-avalonia-port (verificare l'HEAD VERO con git rev-parse HEAD). NON push. NON firmare i
commit (git -c commit.gpgsign=false). NON refactor multi-target. NON toccare la build Windows:
lavorare SOLO in src/crossplatform/.

LEGGI PRIMA src/crossplatform/HANDOFF.md sezioni 3 e 4 e in PORTING.md le milestone M67-M70. La "Coda
round 9" e' ESAURITA: non ci sono piu' voci aperte da pescare. Le voci di questo round sono elencate
sotto; tutto il resto e' SKIP dichiarato (repository-host GitHub, colonna build status, script utente,
le ~35 impostazioni senza consumatore).

DIREZIONE DELL'UTENTE: lingue solo inglese e italiano. Contano feature, fedelta' all'originale e
integrazione nella GUI. NIENTE pulsanti finti: se dietro una voce non c'e' il dato, non metterla e
registrare perche'.

## VOCI DI QUESTO ROUND
1. <voce> — <perche' vale, e dove sta il dato/API che la sblocca>
2. ...

METODO: il loop NON scrive codice a mano tranne il cablaggio minimo in MainWindow/MainMenu. Delega a
subagent CLAUDE in worktree isolati (isolation: worktree), 2-3 in parallelo, un'unita' per subagent,
file DISGIUNTI; mai subagent Codex con worktree. Ogni iterazione: cherry-pick dei tip UNO ALLA VOLTA +
build check dopo ognuno, integrazione minima, verifica GUI con screenshot GUARDATI davvero, commit,
cleanup worktree+branch, spunta della voce in PORTING.md. I commit docs-only dei subagent (solo
NOTES.md) NON vanno cherry-pickati: NOTES.md non entra nel branch.
REGOLA ANTI-CONFLITTO: un solo subagent per iterazione tocca ciascun file hub (MainWindow, MainMenu,
MainToolbar, RepoObjectsTree, RevisionGridView, DiffView, FileStatusListView, CommitDetailView,
StashPanel, CommitDialog, PushDialog, PullDialog, GitProcessDialog, ConsoleView, SettingsWindow,
DashboardView, ThemeManager, Theming/*).
REGOLA subagent: primo step `git reset --hard <SHA HEAD CORRENTE>` — passargli l'HEAD vero; verificare
che App/GitContext.cs ESISTA; VIETATO git checkout/switch/branch -f nel repo principale; committare
presto e spesso; NOTES.md incrementale in src/crossplatform/ con misure e file:riga; delegare a
sub-subagent SOLO ricerche read-only (Explore); commit Conventional senza firma; NON scrivere fuori da
src/crossplatform/; **VERIFICARE LA PREMESSA prima di scrivere codice** (le voci di coda invecchiano:
in round 11 tre unita' su tre di un'iterazione erano gia' fatte); spegnere il proprio Xvfb alla fine.
REGOLA loop: la cwd di Bash PERSISTE — percorsi assoluti, e verificare `git branch --show-current` ==
linux-avalonia-port e `git rev-parse HEAD^` == commit atteso PRIMA di ogni commit.
Ambiente: export PATH="$HOME/.dotnet:$PATH"; da src/crossplatform:
dotnet build App/GitExtensions.Avalonia.csproj -v q -> Errori: 0.
Verifica GUI headless: Xvfb su display privato (uno per agent) con
"-screen 0 1400x900x24 +extension XINPUTEXTENSION", XDG_CONFIG_HOME isolato (Theme/Language in
ui-state.json; opzioni del diff viewer, file history, left panel e MRU dei filtri avanzati in
view-prefs.json), mini-WM python-Xlib per i MODALI, import -window root, e GUARDARE davvero
l'immagine col tool Read; misurare i colori con python/PIL + formula WCAG, non a occhio. Niente
xdotool: python-Xlib fake_input (XTEST). Script in /tmp/loop-verify/ (click.py con coordinate "X,Y",
rclick.py, dclick.py, esc.py, closewin.py, miniwm.py, g2_type.py "c:X,Y t:testo k:Return s:1.5"; in
r8/: altclick.py, ctrlkey.py). Avviare Xvfb e app con `nohup … & disown` in chiamate Bash SEPARATE.
Le sleep di shell vengono UCCISE dall'harness (exit 144): python3 -c "import time;time.sleep(N)".
`pkill -f` uccide la shell che lo lancia: pattern auto-escluso (Xvf[b] :205) o kill <PID>. Controllare
l'mtime dello screenshot. `kill` sull'app salta PersistLayout(): per provare la persistenza chiudere
da Start -> Exit o con closewin.py. Sul display REALE (Wayland/XWayland) XTEST tastiera funziona ma il
puntatore NO, e gli screenshot vanno presi per finestra (import -window <id>).
Repo di prova in /tmp: r11int (+remote r11intrem), r9repo, g1repo, r11bs (bisect), r11am (patch set),
r11a3 (archive/sparse), r11v (oggetti perduti), r11tok (sintassi). Distruttivo SOLO in /tmp.
Aggiornare PORTING.md (prossima milestone libera: M71) e HANDOFF.md a ogni iterazione, e la memoria
avalonia-port-state.md a fine blocco.
STOP quando le voci sono chiuse o dichiarate, oppure a 15 iterazioni, oppure se una strada si rivela
impraticabile (documentare il vicolo cieco invece di forzare).
```

### Prompt storici (round 10 e 11, chiusi)

I prompt dei round 10 e 11 sono conservati nella storia di questo file
(`git log -p src/crossplatform/HANDOFF.md`); non servono più come riferimento operativo.
