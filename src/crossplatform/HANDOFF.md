# HANDOFF — port Linux/Avalonia di Git Extensions

Documento di passaggio per chi (umano o agente) riprende il lavoro.
Fonte di verità dettagliata: **`src/crossplatform/PORTING.md`** (milestone M1–M51,
checklist di parità, metodo del loop). Questo file è il riassunto operativo.

---

## 1. Stato attuale

| | |
|---|---|
| Branch | `linux-avalonia-port` |
| HEAD al momento dell'handoff | `84310c5cc` + questo commit di documentazione (… + M45–M49 + **priorità P1–P3 / M50** + **pannello inferiore / M51**) |
| Build | `Errori: 0` (20–21 warning pre-esistenti VSTHRD/CS0067) |
| Parità voci UI/funzionali | 157/160 = **98,1%** (3 SKIP consapevoli) |
| Fedeltà UX/visiva | round 1 (T1–T5) + round 2 (M31–M35) + round 3 (M36–M37) + **round 4 rifiniture (M39–M42)** + **round 5 follow-up 1 (M45)** + **round 6 follow-up residui (M46)** + **round 7 feature/GUI (M47–M48)** + M49 fix scroll/selezione grid + **round 8 priorità utente P1–P3 (M50)** + **round 8 pulsanti del pannello inferiore (M51)** |
| Bugfix post-blocco | M43 fetch/pull freeze · M44 `HOME` sbagliato → prompt credenziali a ogni push |
| Packaging | `.deb` self-contained via `packaging/build-deb.sh` |
| Push su remote | **origin NON allineato: 11 commit locali non pushati** al momento della stesura (conta esatta con `git rev-list --count origin/linux-avalonia-port..HEAD`). Il push lo esegue l'utente, mai il loop. Portachiavi: se vuoto, il primo push chiede le credenziali **una volta** (username `daryda9` + PAT), poi `git credential approve` le salva in libsecret |

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
  libera: **M52**), tiene il contatore iterazione.

### Metodo del loop (delega)
- Il loop **non scrive codice a mano**: pianifica e **delega a subagent Claude in
  worktree isolati** (`isolation: worktree`), 2–3 in parallelo, **file disgiunti**.
- **NON usare subagent Codex con worktree** (perde il lavoro).
- **Regola anti-conflitto**: un solo subagent per iterazione tocca ciascun file *hub*:
  `MainWindow`, `MainMenu`, `MainToolbar`, `RepoObjectsTree`, `RevisionGridView`,
  `DiffView`, `StashPanel`, `CommitDialog`, `PushDialog`,
  `GitProcessDialog`, `ConsoleView`.
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
  `g2_type.py`, `esc.py`, e in `r8/`: `altclick.py`, `ctrlkey.py`).
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

M45–M51 sono chiusi. Il prossimo blocco (round 9) **non parte da una lista scritta a mano**: parte
da un **audit sistematico area per area** contro l'originale Windows, che produce lui la coda di
lavoro. Prompt riutilizzabile (incollabile in `/loop`):

```
Continua il port Linux/Avalonia di Git Extensions in src/crossplatform/ — ROUND 9: AUDIT COMPLETO
DI PARITA' + CHIUSURA DELLE DIFFERENZE. Branch: linux-avalonia-port (verificare HEAD all'avvio).
NON push. NON firmare i commit (git -c commit.gpgsign=false). NON refactor multi-target. NON
toccare la build Windows: lavorare SOLO in src/crossplatform/.

LEGGI PRIMA src/crossplatform/HANDOFF.md sezioni 3 e 4 (convenzioni Avalonia, trappole, classe di
bug del core condiviso, ricetta GUI headless, cosa resta) e in PORTING.md i blocchi dei round 8
("Blocco PRIORITA' P1-P3", "Blocco PANNELLO INFERIORE") per non riaprire lavoro gia' fatto.

DIREZIONE DELL'UTENTE: le LINGUE non interessano oltre inglese e italiano (blocco traduzioni
CHIUSO, non aprirne unita'). Contano FEATURE, FEDELTA' all'originale e INTEGRAZIONE nella GUI.

## FASE 1 — AUDIT (prime iterazioni, subagent READ-ONLY in parallelo, NIENTE worktree)
Obiettivo: l'elenco COMPLETO di cio' che ancora differenzia il port dall'originale, area per area.
Un subagent per area, sola lettura, nessun commit. Aree DISGIUNTE:
  A. Barra menu (tutte le voci di FormBrowse.Designer.cs + i menu di FormBrowse.InitMenus*)
  B. Toolbar in alto (pulsanti, split-button, combo, overflow) vs FormBrowse.Designer.cs
  C. Pannello sinistro RepoObjectsTree: nodi, menu contestuali per tipo di nodo, drag&drop
  D. Revision grid: colonne, menu contestuale, filtri, quick search, tastiera, grafo
  E. Pannello inferiore: residui di M51 (Stash, File tree, GPG) + verifica del resto
  F. Dialoghi (Commit, Push, Pull, Checkout, Remotes, Clone, Archive, Patch, Submodules,
     Worktrees, Sparse, Maintenance, Reflog, Bisect, About) vs le Form* corrispondenti
  G. Impostazioni/Settings: pagine di FormSettings vs SettingsWindow del port
  H. Chrome globale: status bar, dashboard/start page, hotkey, persistenza dello stato
Ogni audit consegna: per ogni mancanza -> nome dell'item upstream + file:riga, cosa fa il port
oggi, cosa manca, COSTO (banale/media/alta), se serve un dato/servizio che il port non ha, e se
l'originale NON ha nulla in piu' DIRLO ESPLICITAMENTE (per non inventare lavoro).
Riferimenti visivi: gli screenshot dell'originale in ~/Documents/process dialog with terminal
command/ (GUI.png, commit dialog.png, push dialog.png, diff view between two commits.png,
process dialog with terminal command.png) e ~/Documents/pullu'/ — GUARDARLI col tool Read.
Il loop CONSOLIDA gli audit in una coda unica in PORTING.md ("Coda round 9"), ordinata per
rapporto valore/costo, marcando cosa e' rinviato e perche'.

## FASE 2 — CHIUSURA (iterazioni successive, fino a 20 in totale)
Si lavora la coda dall'alto. Ogni iterazione: 2-3 subagent CLAUDE in worktree isolati
(isolation: worktree), un'unita' per subagent, file DISGIUNTI; mai subagent Codex con worktree.
Il loop: cherry-pick dei tip UNO ALLA VOLTA + build check dopo ognuno, integrazione minima
(il cablaggio in MainWindow lo fa il loop), verifica GUI con screenshot GUARDATI davvero,
commit, cleanup worktree+branch, aggiornamento di PORTING.md.
REGOLA: nessun pulsante finto. Se dietro una voce non c'e' il dato, NON metterla e registrare
perche'. Riusare i componenti che esistono gia' (App/Views/FileStatusListView.cs per le liste
file, lo split-button di MainToolbar.cs, CommitDetailView per i dettagli commit).
NON lavorare su repository-host GitHub ne' colonna build status: SKIP fuori scope.

METODO: il loop NON scrive codice a mano tranne il cablaggio minimo in MainWindow.
REGOLA ANTI-CONFLITTO: un solo subagent per iterazione tocca ciascun file hub (MainWindow,
MainMenu, MainToolbar, RepoObjectsTree, RevisionGridView, DiffView, FileStatusListView,
CommitDetailView, StashPanel, CommitDialog, PushDialog, PullDialog, GitProcessDialog,
ConsoleView, SettingsWindow).
REGOLA subagent: primo step `git reset --hard <SHA_HEAD_corrente>`; verificare che
src/crossplatform/App/GitContext.cs ESISTA (se manca, base sbagliata -> fermarsi);
VIETATO git checkout/switch/branch -f nel repo principale; commit Conventional senza firma.
REGOLA loop: la cwd di Bash PERSISTE fra le chiamate — usare percorsi assoluti e verificare
`git branch --show-current` == linux-avalonia-port e `git rev-parse HEAD^` == commit atteso
PRIMA di ogni commit (un cd in un worktree di subagent ha gia' fatto partire un cherry-pick
sul branch sbagliato).
Ambiente: export PATH="$HOME/.dotnet:$PATH"; da src/crossplatform:
dotnet build App/GitExtensions.Avalonia.csproj -v q -> Errori: 0.
Verifica GUI headless: xvfb-run -n <display privato> --server-args="-screen 0 1400x900x24
+extension XINPUTEXTENSION", XDG_CONFIG_HOME isolato (la dimensione finestra persistita eccede
lo schermo; per forzare stati scrivere $XDG_CONFIG_HOME/GitExtensions.Avalonia/ui-state.json),
mini-WM python-Xlib per i MODALI, import -window root, e GUARDARE davvero l'immagine.
Niente xdotool: python-Xlib fake_input (XTEST). Script pronti in /tmp/loop-verify/ (session.sh,
click.py, rclick.py, esc.py, miniwm.py, g2_type.py e in r8/: altclick.py, ctrlkey.py).
Le sleep di shell vengono UCCISE dall'harness (exit 144): usare
python3 -c "import time;time.sleep(N)". Controllare l'mtime dello screenshot prima di leggerlo.
Repo di prova /tmp/loop-testrepo; operazioni distruttive SOLO su repo in /tmp, mai su git_ext_mod.
Aggiornare PORTING.md (prossima milestone libera: M52) e HANDOFF.md a ogni iterazione, e la
memoria avalonia-port-state.md a fine blocco.
STOP quando la coda e' chiusa, oppure a 20 iterazioni, oppure se una strada si rivela
impraticabile (documentare il vicolo cieco invece di forzare).
```
