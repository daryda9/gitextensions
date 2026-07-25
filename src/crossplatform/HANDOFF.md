# HANDOFF — port Linux/Avalonia di Git Extensions

Documento di passaggio per chi (umano o agente) riprende il lavoro.
Fonte di verità dettagliata: **`src/crossplatform/PORTING.md`** (milestone M1–M38,
checklist di parità, metodo del loop). Questo file è il riassunto operativo.

---

## 1. Stato attuale

| | |
|---|---|
| Branch | `linux-avalonia-port` |
| HEAD al momento dell'handoff | `b66ac9107` |
| Build | `Errori: 0` (13 warning pre-esistenti VSTHRD/CS0067) |
| Parità voci UI/funzionali | 157/160 = **98,1%** (3 SKIP consapevoli) |
| Fedeltà UX/visiva | round 1 (T1–T5) + round 2 (M31–M35) + round 3 (M36–M37) chiusi |
| Packaging | `.deb` self-contained via `packaging/build-deb.sh` |
| Push su remote | **mai eseguito** (vincolo del lavoro finora) |

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
  inline path-repo (recenti) e branch (checkout), menu `All branches ▾` + casella
  `Filter:` che pilotano la grid.
- **Revision grid**: pill ref *outline* colorate (branch verde / remote rosso / tag
  ambra), branch corrente in grassetto con ▶, date relative, righe artificiali
  "Working directory" + "Commit index" in cima, selezione con barra accento,
  multi-selezione di 2 commit → diff range automatica, quick-search, filtri, scope.
- **Modali**: `CommitDialog` 3-zone (unstaged/staged + diff del file + messaggio e
  bottoni), `PushDialog` di configurazione (remote/branch/force, Pull+Push),
  `GitProcessDialog` stile FormProcess (console beige, `Command to be executed:`,
  **output git live** stdout+stderr, footer Keep-dialog-open/OK/Abort).
- **Pannello inferiore**: Commit · Diff · File tree · GPG · Console · Output ·
  Working directory · Stash · Blame · File history.
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
  libera: **M39**), tiene il contatore iterazione.

### Metodo del loop (delega)
- Il loop **non scrive codice a mano**: pianifica e **delega a subagent Claude in
  worktree isolati** (`isolation: worktree`), 2–3 in parallelo, **file disgiunti**.
- **NON usare subagent Codex con worktree** (perde il lavoro).
- **Regola anti-conflitto**: un solo subagent per iterazione tocca ciascun file *hub*:
  `MainWindow`, `MainMenu`, `MainToolbar`, `RepoObjectsTree`, `RevisionGridView`,
  `DiffView`, `WorkingDirectoryView`, `StashPanel`, `CommitDialog`, `PushDialog`,
  `GitProcessDialog`.
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

### A. Difetti noti / regressioni da chiudere (priorità alta)
1. ✅ **CHIUSO M39/A1** — toolbar overflow `»` (`OverflowPanel` in `MainToolbar.cs`).
2. **Toggle "Split view" ora cosmetico** — dopo U-TABS il diff è in un tab proprio,
   quindi il pulsante non cambia più layout (aggiorna solo lo status). Decidere:
   ridargli significato (es. split dentro il tab Diff) o rimuoverlo dalla toolbar.
   *File*: `App/MainWindow.cs` (`ToggleSplitView`, `_splitHorizontal`), `MainToolbar.cs`.
3. ✅ **CHIUSO M39/A3** — repo recenti filtrati/normalizzati/potati in modo persistente.
   *Residuo minore*: il dropdown mostra ora il path assoluto completo invece di `~/…` →
   riabbreviare con `~` (`App/Views/MainToolbar.cs` o il provider in `MainWindow.cs`).

### B. Fedeltà visiva rimanente (priorità media)
4. ✅ **CHIUSO M39/B4** — toolbar Diff completa (`DiffView.cs` + `Services/DiffTextService.cs`).
5. **U-GRID-SEL** — rifinire ancora la riga selezionata (l'originale la riempie di blu
   pieno su tutta la larghezza, testo bianco). *File*: `App/Views/RevisionGridView.cs`.
6. **Righe artificiali integrate nel grafo** — oggi "Working directory" / "Commit index"
   sono un pannello fisso sopra la lista; nell'originale sono nodi del DAG collegati al
   grafo. *File*: `App/Views/RevisionGridView.cs`.
7. **Tab "Working directory" ridondante** con il `CommitDialog` modale: decidere se
   rimuoverlo (l'originale non lo ha) o tenerlo. *File*: `App/MainWindow.cs`.

### C. Funzioni placeholder da completare (priorità media/bassa)
8. **CommitDialog**: `Stash staged changes`, `Commit templates`, `Create branch`,
   `Options` sono presenti ma no-op/disabilitati. *File*: `App/Views/CommitDialog.cs`.
9. **PushDialog**: tab `Push tags` e `Push multiple branches` sono placeholder; pulsante
   `Manage remotes` disabilitato; push per `Url` disabilitato.
   *File*: `App/Views/PushDialog.cs`.
10. **Console tab**: nessun terminale realmente incorporato (solo "Open terminal here").
    Valutare un PTY embedded. *File*: `App/Views/ConsoleView.cs`.

### D. Debito tecnico storico (dal PORTING.md)
11. Verifica traduzioni `ResourceManager` su Linux.
12. Shim no-op → implementazioni reali (dialog, clipboard) in `Compat/`.

### E. Fuori scope (SKIP consapevoli — 3 voci del 98,1%)
- **Repository hosts (GitHub)**: fork / view-create PR / add upstream. Realizzabile come
  plugin repository-host (l'infrastruttura plugin esiste), non incluso.
- **Colonna build status**: richiede integrazione con un build-server/CI.

---

## 5. Prompt pronto per riprendere

Vedi la sezione "Prompt di ripresa" qui sotto: è pensato per essere incollato come
input di `/loop` (self-paced) o come prompt singolo.

```
Continua il port Linux/Avalonia di Git Extensions in src/crossplatform/ — blocco
RIFINITURE (vedi "## 4. Cosa resta da fare" in src/crossplatform/HANDOFF.md e
"## TODO" in PORTING.md). Branch: linux-avalonia-port (HEAD b66ac9107, verificare).
NON push. NON firmare i commit (git -c commit.gpgsign=false). NON refactor multi-target.
NON toccare la build Windows: lavorare solo in src/crossplatform/.

LEGGI PRIMA src/crossplatform/HANDOFF.md (sezioni 3 e 4): contiene convenzioni, trappole
Avalonia, la ricetta di verifica GUI headless e l'elenco dei residui con i file.

LAVORARE in quest'ordine (uno o più item per iterazione, DELEGANDO a subagent):
- A1 Toolbar overflow: MainToolbar è uno StackPanel orizzontale che non fa wrap → combo
  All-branches/Filter e indicatore repo escono dal bordo destro a larghezze piccole.
  Aggiungere wrap / ScrollViewer orizzontale / menu overflow "»" come l'originale.
- A2 Toggle "Split view" ora cosmetico (il diff è in un tab proprio dopo U-TABS):
  ridargli significato (split dentro il tab Diff) oppure rimuoverlo dalla toolbar.
- A3 Dropdown repo recenti: filtrare i path inesistenti e quelli sotto .claude/worktrees
  (oggi elenca vecchi agent-*).
- B4 Toolbar del Diff ricca come l'originale (naviga change su/giù, zoom, ignora
  whitespace, caratteri non stampabili, word-diff, encoding, impostazioni) in DiffView.
- B5 U-GRID-SEL: riga selezionata riempita di blu pieno su tutta la larghezza con testo
  ad alto contrasto (l'originale è molto più marcato), senza rompere grafo e pill.
- B6 Righe "Working directory"/"Commit index" integrate come nodi del grafo DAG invece
  del pannello fisso sopra la lista.
- B7 Decidere il destino del tab "Working directory" (ridondante col CommitDialog modale).
- C8 CommitDialog: completare Stash staged changes / Commit templates / Create branch /
  Options (oggi placeholder).
- C9 PushDialog: completare tab Push tags e Push multiple branches, pulsante Manage
  remotes, push per Url.
- C10 ConsoleView: valutare un terminale realmente incorporato (PTY).

METODO OGNI ITERAZIONE — DELEGA AI SUBAGENT:
- Il loop NON scrive codice a mano: pianifica, poi DELEGA a subagent CLAUDE in worktree
  isolati (isolation: worktree), 2-3 in parallelo, un'unità per subagent, file DISGIUNTI.
  NON usare subagent Codex con worktree. Il loop fa: scelta pezzo, spawn, cherry-pick dei
  tip UNO ALLA VOLTA + build check dopo ognuno, integrazione minima, verifica GUI, commit,
  cleanup worktree+branch.
- REGOLA ANTI-CONFLITTO: un solo subagent per iterazione tocca ciascun file hub
  (MainWindow, MainMenu, MainToolbar, RepoObjectsTree, RevisionGridView, DiffView,
  WorkingDirectoryView, StashPanel, CommitDialog, PushDialog, GitProcessDialog).
- REGOLA subagent: primo step git reset --hard <SHA_HEAD_corrente>; verificare che
  src/crossplatform/App/GitContext.cs esista (altrimenti base sbagliata → fermarsi);
  VIETATO git checkout/switch/branch -f nel repo principale; commit Conventional SENZA
  firma.
- REGOLA loop: dopo ogni integrazione e PRIMA di committare verificare
  `git branch --show-current` == linux-avalonia-port e `git rev-parse HEAD^` == commit
  atteso (in passato una commit è atterrata sul branch sbagliato perdendo lavoro).
- Ambiente: SDK ~/.dotnet (export PATH="$HOME/.dotnet:$PATH"). Build da src/crossplatform:
  dotnet build App/GitExtensions.Avalonia.csproj -v q → Errori: 0.
- Per ogni cambiamento UI: verifica GUI headless con xvfb (+extension XINPUTEXTENSION) +
  mini-WM python-Xlib per i modali + import screenshot, e GUARDARE l'immagine. Non c'è
  xdotool: usare python-Xlib fake_input. Brush App.* da Application.Current.Resources;
  Control custom con ClipToBounds; MenuFlyout popolati PRIMA di ShowAt.
- Confronto visivo con gli screenshot dell'originale Windows in
  /home/dario/Documents/process dialog with terminal command/ (GUI.png, commit dialog.png,
  push dialog.png, process dialog with terminal command.png,
  diff view between two commits.png).
- Ogni iterazione aggiorna PORTING.md: spunta le voci chiuse, registra una milestone
  (prossima libera M39) e tiene un contatore iterazione. Aggiorna HANDOFF.md sezione 4
  togliendo ciò che è stato chiuso.

CONDIZIONE DI STOP (qualunque prima): (a) item A e B tutti chiusi e verificati in GUI;
OPPURE (b) 10 iterazioni totali. Allo stop scrivi un breve riepilogo in PORTING.md,
aggiorna HANDOFF.md e FERMA il loop.
```
