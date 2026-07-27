# HANDOFF — port Linux/Avalonia di Git Extensions

Documento di passaggio per chi (umano o agente) riprende il lavoro.
Fonte di verità dettagliata: **`src/crossplatform/PORTING.md`** (milestone M1–M44,
checklist di parità, metodo del loop). Questo file è il riassunto operativo.

---

## 1. Stato attuale

| | |
|---|---|
| Branch | `linux-avalonia-port` |
| HEAD al momento dell'handoff | `dfd0b9fdb` (round 4 + bugfix M43–M44 + follow-up 1 / M45) |
| Build | `Errori: 0` (24 warning pre-esistenti VSTHRD/CS0067) |
| Parità voci UI/funzionali | 157/160 = **98,1%** (3 SKIP consapevoli) |
| Fedeltà UX/visiva | round 1 (T1–T5) + round 2 (M31–M35) + round 3 (M36–M37) + **round 4 rifiniture (M39–M42)** + **round 5 follow-up 1 (M45)** chiusi |
| Bugfix post-blocco | M43 fetch/pull freeze · M44 `HOME` sbagliato → prompt credenziali a ogni push |
| Packaging | `.deb` self-contained via `packaging/build-deb.sh` |
| Push su remote | eseguito dall'utente (origin allineato). Portachiavi **vuoto**: il prossimo push chiede le credenziali **una volta** (username `daryda9` + PAT), poi `git credential approve` le salva in libsecret |

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
  libera: **M45**), tiene il contatore iterazione.

### Metodo del loop (delega)
- Il loop **non scrive codice a mano**: pianifica e **delega a subagent Claude in
  worktree isolati** (`isolation: worktree`), 2–3 in parallelo, **file disgiunti**.
- **NON usare subagent Codex con worktree** (perde il lavoro).
- **Regola anti-conflitto**: un solo subagent per iterazione tocca ciascun file *hub*:
  `MainWindow`, `MainMenu`, `MainToolbar`, `RepoObjectsTree`, `RevisionGridView`,
  `DiffView`, `WorkingDirectoryView`, `StashPanel`, `CommitDialog`, `PushDialog`,
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
2. **Traduzioni** — il port è **inglese per costruzione** (verificato in M42/D11): il motore
   XLIFF del core funziona su Linux, ma nessun `.xlf` viene copiato in output/`.deb`, non
   esiste un layer `ITranslate` (ogni stringa delle view è un letterale) e non c'è selettore
   lingua. Servono: copia MSBuild di `src/app/GitUI/Translation/*.xlf`, layer `ITranslate`
   su tutte le view Avalonia, chooser lingua persistito.
3. ✅ **RISOLTO M44** — `HOME` riscritto dal core: `App/HomeDirectoryFix.cs` semina
   `AppSettings.CustomHomeDir` con la home vera da un `[ModuleInitializer]`. Diagnostica in
   `./run.sh --selftest`: riga `[11]` = HOME per i git figli, `[12]` = `credential.helper`
   risolto. Il difetto di fondo resta **nel core condiviso** (`GetDefaultHomeDir()` legge
   `HOME` dai target `User`/`Machine`, che su Unix sono sempre `null`): se un giorno si
   tocca il core, è lì che va corretto.
4. **Header della revision grid** stampa ancora il path assoluto (non abbreviato con `~`).
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

Il blocco rifiniture è chiuso: non c'è una coda di residui da smaltire. Il prossimo lavoro
sensato sono i **follow-up** della sezione 4. Prompt riutilizzabile (incollabile in `/loop`):

```
Continua il port Linux/Avalonia di Git Extensions in src/crossplatform/. Branch:
linux-avalonia-port (verificare HEAD all'avvio). NON push. NON firmare i commit
(git -c commit.gpgsign=false). NON refactor multi-target. NON toccare la build Windows.

LEGGI PRIMA src/crossplatform/HANDOFF.md sezioni 3 e 4: convenzioni Avalonia, trappole,
ricetta di verifica GUI headless, follow-up aperti.

LAVORARE sui follow-up della sezione 4, in quest'ordine:
1. Traduzioni: copia MSBuild di src/app/GitUI/Translation/*.xlf in $(OutDir)Translation e
   nel .deb, layer ITranslate + Translator.Translate sulle view Avalonia, selettore lingua
   persistito. Verificare in GUI con una lingua non inglese.
2. Header della revision grid: abbreviare il path con ~ come già fa la toolbar.
3. Facoltativa: guardia "Nothing staged to commit." che rifiuta un merge commit legittimo
   (rilevare MERGE_HEAD nel CommitDialog); discard multi-file (liste SelectionMode.Single);
   acceleratori Enter/Space/Ctrl+Enter nel CommitDialog.
NON lavorare su repository-host GitHub né colonna build status: SKIP fuori scope.

METODO: il loop NON scrive codice a mano — DELEGA a subagent CLAUDE in worktree isolati
(isolation: worktree), 2-3 in parallelo, file DISGIUNTI; niente subagent Codex con worktree.
Il loop fa: scelta pezzo, spawn, cherry-pick dei tip UNO ALLA VOLTA + build check dopo
ognuno, integrazione minima, verifica GUI, commit, cleanup worktree+branch.
REGOLA ANTI-CONFLITTO: un solo subagent per iterazione tocca ciascun file hub (MainWindow,
MainMenu, MainToolbar, RepoObjectsTree, RevisionGridView, DiffView, WorkingDirectoryView,
StashPanel, CommitDialog, PushDialog, GitProcessDialog, ConsoleView).
REGOLA subagent: primo step `git reset --hard <SHA_HEAD_corrente>`; verificare che
src/crossplatform/App/GitContext.cs ESISTA (se manca, base sbagliata → fermarsi);
VIETATO git checkout/switch/branch -f nel repo principale; commit Conventional senza firma.
REGOLA loop: prima di ogni commit verificare `git branch --show-current` ==
linux-avalonia-port e `git rev-parse HEAD^` == commit atteso.
Ambiente: export PATH="$HOME/.dotnet:$PATH"; da src/crossplatform:
dotnet build App/GitExtensions.Avalonia.csproj -v q → Errori: 0.
Verifica GUI headless: xvfb-run -n <display privato> --server-args="-screen 0 1400x900x24
+extension XINPUTEXTENSION", XDG_CONFIG_HOME isolato (la dimensione finestra persistita
eccede lo schermo; per forzare stati scrivere $XDG_CONFIG_HOME/GitExtensions.Avalonia/
ui-state.json), mini-WM python-Xlib per i MODALI, import -window root, e GUARDARE davvero
l'immagine. Niente xdotool: python-Xlib fake_input (XTEST).
Aggiornare PORTING.md (prossima milestone libera: M45) e HANDOFF.md a ogni iterazione.
```
