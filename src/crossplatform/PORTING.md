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
| `App/GitExtensions.Avalonia` | UI Avalonia (apri repo → log dei commit) |

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

### Priorità alta — nucleo UX quotidiano
1. **Revision grid con grafo** — la vista principale: DAG dei commit, branch/tag,
   colonne autore/data/messaggio. Riusare `RevisionReader` / `GitRevision`.
2. **Vista diff** — diff del commit selezionato e file changed list. Riusare la
   logica diff di `GitCommands`.
3. **Working directory / staging** — file modificati, stage/unstage, commit
   (messaggio, amend). Riusare `GitModule` status/commit.
4. **Selezione repo & repository recenti** — dialog nativo Avalonia per aprire un
   repo (oggi il picker WinForms è uno stub che ritorna Cancel); MRU da
   `RecentRepositoryHistory`.

### Priorità media — operazioni git
5. **Branch/tag**: crea, checkout, merge, rebase, delete.
6. **Remote**: fetch, pull, push (con dialog credenziali — oggi
   `CredentialsControl` WinForms è escluso, serve equivalente Avalonia).
7. **Stash**, **cherry-pick**, **reset**.
8. **Blame** e **file history**.

### Priorità bassa — contorno
9. **Pagine settings** in Avalonia (il framework `ISettingControlBinding` è
   WinForms; ridisegnare il binding).
10. **Sistema di plugin**: i plugin espongono form WinForms; ripensare il
    modello UI dei plugin per Avalonia.
11. **Temi**: portare `GitUI/Theming` (oggi GDI) allo stile Avalonia.

### Debito tecnico / pulizia
- **Sostituire gli shim con implementazioni vere** dove un percorso runtime li
  usa davvero (es. dialog, clipboard) — oggi molti shim sono no-op sufficienti a
  compilare ma non a funzionare pienamente.
- **P/Invoke `kernel32` in `ProcessExtensions.cs`** (Ctrl+C ai processi git
  figli): manca un fallback Linux (signal/kill).
- **Warning `NU1903`**: `Tmds.DBus.Protocol` (dep transitiva Avalonia) ha una
  vulnerabilità nota → valutare bump di Avalonia.
- **Localizzazione**: verificare il caricamento delle traduzioni (`ResourceManager`)
  su Linux.
- **Packaging**: `.deb`/AppImage/Flatpak + `dotnet publish` self-contained.

### Nota architetturale
Man mano che si aggiungono viste, valutare se conviene **aggiungere `net10.0` come
target multiplo direttamente nei csproj reali** (invece dell'albero separato),
usando `#if WINDOWS` per isolare il codice WinForms. L'approccio attuale (albero
separato + shim) è a rischio zero per la build Windows ma duplica la lista dei
sorgenti via glob; a un certo punto il multi-target potrebbe essere più pulito.
