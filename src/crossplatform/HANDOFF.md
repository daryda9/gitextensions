# HANDOFF — port Linux/Avalonia di Git Extensions

Documento di passaggio per chi (umano o agente) riprende il lavoro.
Fonte di verità dettagliata: **`src/crossplatform/PORTING.md`** (milestone M1–M62,
checklist di parità, metodo del loop). Questo file è il riassunto operativo.

---

## 1. Stato attuale

| | |
|---|---|
| Branch | `linux-avalonia-port` |
| HEAD al momento dell'handoff | `c4b366347` (round 11 iterazione 1 = **M67**) · storico: `3b73b44bc` (… + **round 9 completo: M52–M61** + **M62 fix del tema scuro sulle console**) |
| Build | `Errori: 0` (21 warning pre-esistenti VSTHRD/CS0067) |
| Parità voci UI/funzionali | la vecchia conta 157/160 **non è più la misura giusta**: l'audit del round 9 ha mostrato che contava le *voci*, non la profondità. La misura attuale è la **"Coda round 9"** in `PORTING.md`, area per area |
| Fedeltà UX/visiva | round 1 (T1–T5) + round 2 (M31–M35) + round 3 (M36–M37) + **round 4 rifiniture (M39–M42)** + **round 5 follow-up 1 (M45)** + **round 6 follow-up residui (M46)** + **round 7 feature/GUI (M47–M48)** + M49 fix scroll/selezione grid + **round 8 priorità utente P1–P3 (M50)** + **round 8 pulsanti del pannello inferiore (M51)** |
| Bugfix post-blocco | M43 fetch/pull freeze · M44 `HOME` sbagliato → prompt credenziali a ogni push |
| Packaging | `.deb` self-contained via `packaging/build-deb.sh` |
| Push su remote | **origin NON allineato: 37 commit locali non pushati** al momento della stesura (conta esatta con `git rev-list --count origin/linux-avalonia-port..HEAD`). Il push lo esegue l'utente, mai il loop. Portachiavi: se vuoto, il primo push chiede le credenziali **una volta** (username `daryda9` + PAT), poi `git credential approve` le salva in libsecret |

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
  libera: **M68**), tiene il contatore iterazione.

### Metodo del loop (delega)
- Il loop **non scrive codice a mano**: pianifica e **delega a subagent Claude in
  worktree isolati** (`isolation: worktree`), 2–3 in parallelo, **file disgiunti**.
- **NON usare subagent Codex con worktree** (perde il lavoro).
- **Regola anti-conflitto**: un solo subagent per iterazione tocca ciascun file *hub*:
  `MainWindow`, `MainMenu`, `MainToolbar`, `RepoObjectsTree`, `RevisionGridView`,
  `DiffView`, `StashPanel`, `CommitDialog`, `PushDialog`,
  `GitProcessDialog`, `ConsoleView`.
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

> ### ► ROUND 11 (2026-07-29) — in corso. Prossima milestone libera: **M68**
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

Round 10 (M63–M66) è chiuso: nella "Coda round 9" **non resta nessuna voce `- [ ]`**. Quello che
resta sono i **parziali `[~]`** (4.1, 4.11, 3.2), una voce nuova (palette di syntax highlighting per
il tema chiaro) e due cose da cablare. HEAD alla chiusura del round 10: `757742ce8`. Prompt
riutilizzabile (incollabile in `/loop`):

```
Continua il port Linux/Avalonia di Git Extensions in src/crossplatform/ — ROUND 11: I PARZIALI.
Branch: linux-avalonia-port (verificare l'HEAD VERO all'avvio con git rev-parse HEAD: il branch puo'
essere molto piu' avanti di quanto sembri, e la base dei subagent va allineata a quello). NON push.
NON firmare i commit (git -c commit.gpgsign=false). NON refactor multi-target. NON toccare la build
Windows: lavorare SOLO in src/crossplatform/.

LEGGI PRIMA src/crossplatform/HANDOFF.md sezioni 3 e 4 (convenzioni, trappole, classe di bug del
core condiviso, ricetta GUI headless) e in PORTING.md le milestone M63-M66 piu' la sezione "Coda
round 9". Le voci `- [ ]` sono ZERO: NON riaprirle. Il lavoro di questo round sono le `- [~]` e le
voci nuove elencate sotto. Leggere anche il cappello della coda: elenca dove gli audit hanno
stabilito che NON c'e' lavoro e le ~35 impostazioni che sarebbero pulsanti finti.

DIREZIONE DELL'UTENTE: lingue solo inglese e italiano (traduzioni CHIUSE). Contano feature, fedelta'
all'originale e integrazione nella GUI. NIENTE pulsanti finti: se dietro una voce non c'e' il dato,
non metterla e registrare perche'. Fuori scope: repository-host GitHub, colonna build status.

## PRIORITA', in quest'ordine

1. [LEVA] 4.1 **checkout di rami remoti**, oggi impossibile dalla GUI. NON e' bloccato:
   `Commands.CheckoutBranch(branch, isRemote, localChanges, newBranchMode, newBranchName)` esiste
   gia' nel core (`Commands.cs:10`) mentre `BranchTagService.cs:166` usa solo `Commands.Checkout`.
   Con esso arrivano reset-local-branch, create-with-custom-name e detached. Serve anche togliere
   l'esclusione esplicita di Checkout sui remoti in `RepoObjectsTree` (era `:677`) e completare
   `CheckoutBranchDialog`, che oggi e' solo il gruppo "Local changes". *media/alta*
2. [BANALI, insieme] cablare `CommitDetailView.EditNotes()` / `AddNotesDialog`, che ha **zero
   chiamanti** (finestra irraggiungibile, mai vista renderizzata: la voce 1.10 dava AddNotes
   Ctrl+Shift+N per fatta, RIVERIFICARLA); la pillola **tag** del grafo a 3,25:1 in tema chiaro
   (va rifatta come terna con le altre due pillole ref, non da sola); e un **warm-up di una riga**
   per il difetto del core `ExecutableExtensions.cs:15` (`Lazy<Encoding>(isThreadSafe: false)` →
   le prime due chiamate git CONCORRENTI di un processo lanciano InvalidOperationException).
3. [BLOCCO] 4.11 dialoghi, resto — in ordine di valore, non tutti obbligatori:
   **dialogo bisect** + gating su `InTheMiddleOfBisect` (oggi il port auto-avvia in silenzio alla
   prima marcatura); **macchina a stati `git am`** (Resolved/Skip/Abort, PatchGrid);
   `FormCleanupRepository` ridotto a un confirm inline, quindi la modalita' **solo-ignorati
   `clean -X` e' irraggiungibile**; `FormInit` inesistente (solo folder picker: niente
   `--bare`/`--shared`); `CloneDialog` senza submodule-init, depth, branch picker, preview della
   destinazione; `ArchiveDialog` senza filtro path/revisione; `RemotesDialog` senza il tab "Default
   pull behavior" ne' push URL separata; `FormVerify` ("Recover lost objects") ridotto a un dump di
   `git fsck`; `SparseDialog` su cone mode (niente negazione `!`, upstream pilota il legacy).
4. [I18N MIRATO] il rilevamento del fallimento di autenticazione e' **solo inglese**: con git in
   italiano il PTY stampa `fatal: Autenticazione non riuscita per …`, che non matcha ne'
   `GitProcessDialog.LooksLikeAuthFailure` ne' i marker di `RemoteService`/`PushRefsService`, quindi
   dopo credenziali sbagliate il fallback `CredentialsDialog` NON si apre (con `LC_ALL=C` matcha:
   verificato). Cercare il segnale robusto (exit code + prompt ripetuto, o forzare la locale dei
   figli) invece di aggiungere stringhe tradotte.
5. [PERSISTENZA] 3.2, il residuo: opzioni del **diff viewer**, switch della **file history**, filtri
   del **left panel**, MRU dei **filtri di revisione**. Attenzione: `MainWindow` riserializza la sua
   unica istanza di `UiState` alla chiusura, quindi chi scrive da un'altra view va instradato
   sull'host (o su un file separato, come fece commit-info.json).
6. [ULTIMA, forse fuori scope] palette di **syntax highlighting per il tema chiaro**: 5 tinte
   duplicate in `DiffView:78-82` e `FileTreeView:52-56`, da 1,53:1 a 2,67:1 su fondo chiaro.
   Servono 5 chiavi `App.Token*` x 2 temi, cioe' PROGETTARE un tema di sintassi: valutare se ne vale
   la pena e, se no, registrare il perche' invece di improvvisare tinte.
7. [DA FARE SOLO SU DISPLAY REALE, chiedere prima all'utente] i **file picker** (`Browse…` di
   Open/Clone/Init/Archive): un portal XDG e' attivo e il bus DBus e' ereditato, ma
   `OpenFolderPickerAsync` torna a mani vuote SENZA eccezione e nessuna finestra appare ne' su Xvfb
   ne' sul display reale. Per accertarlo serve lanciare l'app sul display dell'utente (finestre sulla
   sua scrivania): NON farlo senza il suo ok. Il clipboard invece FUNZIONA anche headless (misurato
   in M65 con xclip): non ri-testarlo come se fosse un limite.

METODO: il loop NON scrive codice a mano tranne il cablaggio minimo in MainWindow. Delega a subagent
CLAUDE in worktree isolati (isolation: worktree), 2-3 in parallelo, un'unita' per subagent, file
DISGIUNTI; mai subagent Codex con worktree. Ogni iterazione: cherry-pick dei tip UNO ALLA VOLTA +
build check dopo ognuno, integrazione minima, verifica GUI con screenshot GUARDATI davvero, commit,
cleanup worktree+branch, spunta della voce in PORTING.md.
REGOLA ANTI-CONFLITTO: un solo subagent per iterazione tocca ciascun file hub (MainWindow, MainMenu,
MainToolbar, RepoObjectsTree, RevisionGridView, DiffView, FileStatusListView, CommitDetailView,
StashPanel, CommitDialog, PushDialog, PullDialog, GitProcessDialog, ConsoleView, SettingsWindow,
DashboardView, ThemeManager, Theming/*).
REGOLA subagent: primo step `git reset --hard <SHA HEAD CORRENTE>` — passargli l'HEAD vero;
verificare che App/GitContext.cs ESISTA; VIETATO git checkout/switch/branch -f nel repo principale;
committare presto e spesso; **NOTES.md incrementale nel worktree** con misure e file:riga (in round
10 un subagent e' stato ucciso da un watchdog a lavoro quasi finito e il NOTES ha salvato tutto);
delegare a sub-subagent SOLO ricerche read-only (`Explore`), mai scrittura di codice; commit
Conventional senza firma; NON scrivere fuori da src/crossplatform/ (in round 10 un NOTES.md di
scratch e' finito nella root del repo con un cherry-pick).
REGOLA loop: la cwd di Bash PERSISTE fra le chiamate — usare percorsi assoluti e verificare
`git branch --show-current` == linux-avalonia-port e `git rev-parse HEAD^` == commit atteso PRIMA di
ogni commit. Se un subagent supera ~150k di contesto, farlo chiudere (commit + NOTES) e spawnarne
uno nuovo sul residuo, invece di lasciarlo compattare a metà unità.
Ambiente: export PATH="$HOME/.dotnet:$PATH"; da src/crossplatform:
dotnet build App/GitExtensions.Avalonia.csproj -v q -> Errori: 0.
Verifica GUI headless: Xvfb su display privato (uno per agent, es. :205+) con
"-screen 0 1400x900x24 +extension XINPUTEXTENSION", XDG_CONFIG_HOME isolato (per forzare stati
scrivere $XDG_CONFIG_HOME/GitExtensions.Avalonia/ui-state.json, chiavi Theme/Language), mini-WM
python-Xlib per i MODALI, import -window root, e GUARDARE davvero l'immagine col tool Read; misurare
i colori con python/PIL + formula WCAG, non a occhio. Niente xdotool: python-Xlib fake_input (XTEST).
Script pronti in /tmp/loop-verify/ (click.py, rclick.py, dclick.py, esc.py, miniwm.py, g2_type.py;
in r8/: altclick.py, ctrlkey.py). Avviare Xvfb e app con `nohup … & disown` in chiamate Bash
SEPARATE (xvfb-run muore quando lo script finisce).
Le sleep di shell vengono UCCISE dall'harness (exit 144): usare python3 -c "import time;time.sleep(N)".
`pkill -f "<pattern>"` uccide la shell che lo lancia se il pattern compare nella propria riga di
comando: usare un pattern auto-escluso (Xvf[b] :205) o kill <PID>. Controllare l'mtime dello
screenshot prima di leggerlo.
Repo di prova /tmp/r10loop (con remote locale /tmp/r10remote), /tmp/r9repo, /tmp/g1repo; per il
grafo costruire una topologia nota in /tmp; operazioni distruttive SOLO su repo in /tmp, mai su
git_ext_mod (in sola lettura va bene).
Aggiornare PORTING.md (prossima milestone libera: M67) e HANDOFF.md a ogni iterazione, e la memoria
avalonia-port-state.md a fine blocco.
STOP quando le voci sopra sono chiuse o dichiarate, oppure a 15 iterazioni, oppure se una strada si
rivela impraticabile (documentare il vicolo cieco invece di forzare).

Tre note su come l'ho tarato:
- 4.1 e' prima perche' e' l'ultima azione quotidiana ancora impossibile dalla GUI, ed e' sbloccata da
  un'API del core che esiste gia': costo basso, valore alto.
- Le voci del punto 2 sono feature GIA' SCRITTE e irraggiungibili (AddNotesDialog) o difetti misurati
  in round 10: chiuderle costa poco e toglie debito.
- Il punto 6 potrebbe essere una non-voce: se progettare un tema di sintassi chiaro non vale il
  costo, la risposta giusta e' registrarlo come rinviato con motivo, non inventare cinque tinte.
```

### Prompt storico (round 10, chiuso)

Il prompt del round 10 e' conservato nella storia di questo file (`git log -p src/crossplatform/HANDOFF.md`); non serve piu' come riferimento operativo.
