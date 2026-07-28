# HANDOFF — port Linux/Avalonia di Git Extensions

Documento di passaggio per chi (umano o agente) riprende il lavoro.
Fonte di verità dettagliata: **`src/crossplatform/PORTING.md`** (milestone M1–M62,
checklist di parità, metodo del loop). Questo file è il riassunto operativo.

---

## 1. Stato attuale

| | |
|---|---|
| Branch | `linux-avalonia-port` |
| HEAD al momento dell'handoff | `3b73b44bc` (… + **round 9 completo: M52–M61** + **M62 fix del tema scuro sulle console**) |
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
  libera: **M63**), tiene il contatore iterazione.

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
5. **Compat/** — restano no-op solo shim **irraggiungibili** dal port (censiti in M42/D12);
   i file picker richiedono un portal XDG, altrimenti servirebbe `UseManagedSystemDialogs()`.
6. **Clipboard** — verificato solo fino al confine Avalonia: sotto Xvfb il clipboard X11 di
   Avalonia è inerte (controprova con `xclip`: il round-trip nudo funziona, `SetTextAsync`
   no). Va provato su un display reale con "Copy hash" / "Copy file path".

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

Round 9 (M52–M61) e M62 sono chiusi. Quello che resta è **scritto e spuntabile** in `PORTING.md` →
"Coda round 9": dieci difetti *banali* ancora aperti, tre leve architetturali "media/alta" che da
sole chiudono molte voci, e il residuo di toolbar 4.10. Prompt riutilizzabile (incollabile in
`/loop`):

```
Continua il port Linux/Avalonia di Git Extensions in src/crossplatform/ — ROUND 10: CHIUSURA DELLA
CODA. Branch: linux-avalonia-port (verificare HEAD all'avvio: il branch puo' essere molto piu'
avanti di quanto sembri). NON push. NON firmare i commit (git -c commit.gpgsign=false). NON
refactor multi-target. NON toccare la build Windows: lavorare SOLO in src/crossplatform/.

LEGGI PRIMA src/crossplatform/HANDOFF.md sezioni 3 e 4 (convenzioni, trappole, classe di bug del
core condiviso, ricetta GUI headless) e in PORTING.md la sezione "Coda round 9 — audit completo di
parita', 8 aree": e' la fonte di verita' della coda, con file:riga e costi. Le voci `- [ ]` sono
quelle aperte; NON riaprire le `- [x]`. Leggere anche il cappello della coda, che elenca **dove gli
audit hanno stabilito che NON c'e' lavoro** e le ~35 impostazioni che sarebbero pulsanti finti: non
spenderci iterazioni.

DIREZIONE DELL'UTENTE: lingue solo inglese e italiano (traduzioni CHIUSE). Contano feature, fedelta'
all'originale e integrazione nella GUI. NIENTE pulsanti finti: se dietro una voce non c'e' il dato,
non metterla e registrare perche'. Fuori scope: repository-host GitHub, colonna build status.

## PRIORITA', in quest'ordine

1. [BANALI, tutte insieme in una o due iterazioni] 0.17 star del layout salvate come pixel invece
   che come proporzioni; 0.33 "Remotes (n)" conta i branch remoti invece dei remote; 0.34 il repo
   clonato non entra nei recenti; 0.35 Ctrl+W inghiottito dal terminale; 0.36 "Favourite"/"Favorite"
   incoerente; 0.37 URL monco in About; 0.38 Refresh del tab Output apparentemente inerte; 0.39 New
   branch/New tag disabilitati senza selezione (upstream li ancora a HEAD); 1.24 "Filter file in
   grid" dal diff (PathFilter e _pathFilter esistono gia').
2. [LEVA] 4.9 dare a RevisionGridView un entry point CON PATH FILTER: chiude in un colpo grafo,
   decorazioni ref, righe artificiali e multi-selezione nel tab File history, che oggi reimplementa
   una lista nuda. *media/alta*
3. [LEVA] 1.14b righe artificiali, seconda metà: servono le modalita' **index** e **worktree** in
   DiffService (git diff / git diff --cached) piu' un placeholder in Commit details e GPG che nomini
   la riga. Oggi l'host pulisce i tab ma il contenuto vero manca. *media*
4. [LEVA] 4.8 portare GitProcessDialog su PtyProcess/TerminalEmulator (esistono gia', alimentano
   ConsoleView): sblocca output live, barra di progresso dalle righe \r e **prompt interattivi** —
   oggi stdin e' chiuso e GIT_TERMINAL_PROMPT=0, quindi passphrase e host-key yes/no non sono
   rispondibili. *media/alta*
5. [4.10, residuo toolbar] shell-picker come split-button che elenca le shell disponibili, dropdown
   WorkingDir ricco (ricerca, preferiti categorizzati, Open/Close repository, "Configure this
   menu…"), voce "Checkout branch…" in testa al dropdown branch, corpo cliccabile di
   CommitInfoPosition che cicla le 3 posizioni, icona di Commit dai 7 stati upstream del repo.
6. [TEMA] Dopo M62: fare una passata di **leggibilita' in tema CHIARO** su tutte le view (la classe
   di bug era: chiave App.* non registrata -> fallback nero + testo App.Text = illeggibile a riposo).
   Verificare che ogni chiave usata sia in ThemeManager.Keys + Dark + Light; decidere se registrare
   App.ConsoleBackground/App.ConsoleForeground o lasciarle fallback invarianti al tema (oggi:
   fallback voluto). Misurare i colori con python/PIL, non a occhio.
7. [DA FARE SU DISPLAY REALE, non headless] le tre cose che l'attrezzatura non copre: clipboard
   (Copy hash / Copy file path), i file picker Browse… di Open/Clone/Init/Archive (serve un portal
   XDG) e 0.16 WM_DELETE_WINDOW (con un WM vero la "X" non chiude l'app e PersistLayout non gira,
   quindi TUTTO lo stato UI si perde: valutare un intercettatore X11 come si e' fatto per XDND).
   Se non si puo' verificare, DICHIARARLO invece di dedurlo dal codice.

METODO: il loop NON scrive codice a mano tranne il cablaggio minimo in MainWindow. Delega a subagent
CLAUDE in worktree isolati (isolation: worktree), 2-3 in parallelo, un'unita' per subagent, file
DISGIUNTI; mai subagent Codex con worktree. Ogni iterazione: cherry-pick dei tip UNO ALLA VOLTA +
build check dopo ognuno, integrazione minima, verifica GUI con screenshot GUARDATI davvero, commit,
cleanup worktree+branch, spunta della voce in PORTING.md.
REGOLA ANTI-CONFLITTO: un solo subagent per iterazione tocca ciascun file hub (MainWindow, MainMenu,
MainToolbar, RepoObjectsTree, RevisionGridView, DiffView, FileStatusListView, CommitDetailView,
StashPanel, CommitDialog, PushDialog, PullDialog, GitProcessDialog, ConsoleView, SettingsWindow,
DashboardView, ThemeManager).
REGOLA subagent: primo step `git reset --hard <SHA HEAD CORRENTE>` — passargli l'HEAD vero, non uno
vecchio, altrimenti il suo commit non sara' cherry-pickabile; verificare che App/GitContext.cs
ESISTA; VIETATO git checkout/switch/branch -f nel repo principale; **committare presto e spesso**,
non solo a fine unita' (due worktree hanno rischiato ~1100 righe non committate); commit Conventional
senza firma.
REGOLA loop: la cwd di Bash PERSISTE fra le chiamate — usare percorsi assoluti e verificare
`git branch --show-current` == linux-avalonia-port e `git rev-parse HEAD^` == commit atteso PRIMA di
ogni commit.
Ambiente: export PATH="$HOME/.dotnet:$PATH"; da src/crossplatform:
dotnet build App/GitExtensions.Avalonia.csproj -v q -> Errori: 0.
Verifica GUI headless: xvfb-run -n <display privato> --server-args="-screen 0 1400x900x24
+extension XINPUTEXTENSION", XDG_CONFIG_HOME isolato (per forzare stati scrivere
$XDG_CONFIG_HOME/GitExtensions.Avalonia/ui-state.json, chiavi Theme/Language), mini-WM python-Xlib
per i MODALI, import -window root, e GUARDARE davvero l'immagine col tool Read. Niente xdotool:
python-Xlib fake_input (XTEST). Script pronti in /tmp/loop-verify/ (click.py, rclick.py, esc.py,
miniwm.py, g2_type.py; in r8/: altclick.py, ctrlkey.py).
Le sleep di shell vengono UCCISE dall'harness (exit 144): usare python3 -c "import time;time.sleep(N)".
`pkill -f "<pattern>"` uccide la shell che lo lancia se il pattern compare nella propria riga di
comando: usare un pattern auto-escluso (Xvf[b] :151) o kill <PID>. Controllare l'mtime dello
screenshot prima di leggerlo.
Repo di prova /tmp/loop-testrepo; per il grafo costruire una topologia nota in /tmp; operazioni
distruttive SOLO su repo in /tmp, mai su git_ext_mod (in sola lettura va bene).
Aggiornare PORTING.md (prossima milestone libera: M63) e HANDOFF.md a ogni iterazione, e la memoria
avalonia-port-state.md a fine blocco.
STOP quando la coda `- [ ]` e' chiusa, oppure a 20 iterazioni, oppure se una strada si rivela
impraticabile (documentare il vicolo cieco invece di forzare).
```
