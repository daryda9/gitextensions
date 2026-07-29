# Voce 3.2 — il residuo della persistenza (M69)

Unità: persistere (a) le opzioni del diff viewer, (b) gli switch della file history,
(c) i filtri del left panel, (d) la MRU dei filtri di revisione.

## 0. Verifica della premessa all'HEAD (6aa8ffb4b)

Fatta **prima** di scrivere codice, perché in questo progetto le voci di coda invecchiano.

| Sottotema | Stato reale all'HEAD | Prova |
|---|---|---|
| (a) diff viewer | **NON persistito.** 11 opzioni in due singleton di sessione, senza alcuna lettura da file | `DiffTextService.cs:133` (`DiffDisplayOptions Session`), `DiffViewerOptions.cs:22`; `grep UiState App/Views/DiffView.cs` → 0 hit |
| (b) file history | **NON persistito.** 4 switch in un campo *per-istanza* | `FileHistoryView.cs:67` `private FileHistoryOptions _options = new();`, record a `FileHistoryService.cs:53-57`; `grep UiState FileHistoryView.cs` → 0 hit |
| (c) left panel | **Parzialmente.** Larghezza / collassato / **ordine** delle categorie sì (`UiState.TreeWidth`, `LeftPanelCollapsed`, `LeftPanelCategoryOrder`, scritti da `MainWindow.PersistLayout` `MainWindow.cs:729-730` e dal lambda `MainWindow.cs:1097`). **Visibilità** delle 6 categorie e **ordinamento** dei ref: no, di sessione — commento esplicito a `RepoObjectsTree.cs:48` ("Session-local: the port has no equivalent of AppSettings.RepoObjectsTreeShow*") | campi `_showBranches`…`_showStashes` `RepoObjectsTree.cs:49-55`, `_sortKey`/`_sortOrder` `RepoObjectsTree.cs:112-113` |
| (d) MRU filtri di revisione | **Da distinguere.** La MRU del **quick filter** della griglia era GIÀ persistita (chiavi `filterMru:<rank>:<testo>` dentro `UiState.GridViewOptions`, `RevisionGridView.cs:3364` + `:3457-3474`). Il **filtro avanzato** (`RevisionFilterDialog`) non ha **nessuna** MRU: il dialogo è costruito da zero da `current` ogni volta | `RevisionFilterDialog.cs:214-224` (`AskAsync`), unico chiamante `RevisionGridView.cs:2004-2014` |

Correzione alla voce, quindi: (d) va letta come "MRU del **filtro avanzato**"; quella del
quick filter esiste da prima e **non l'ho toccata** (e non potrei: `RevisionGridView.cs` è
fuori dai miei file).

## 1. La trappola della doppia scrittura su `UiState` — strada scelta

`MainWindow` carica **una** istanza di `UiState` all'avvio (`MainWindow.cs:160`) e
**riserializza l'intero oggetto** alla chiusura da `PersistLayout()`
(`MainWindow.cs:711-744`): una view che scrivesse per conto proprio nello stesso file
verrebbe sovrascritta all'uscita (last-writer-wins).

**Strada scelta: file separato**, `view-prefs.json`, con un servizio nuovo
`App/Services/ViewPrefsService.cs` — cioè **il precedente di `commit-info.json`**
(`CommitInfoSettingsService.cs:50-56` documenta la stessa trappola e la stessa scelta),
copiato nella struttura: `Load`/`Save` tolleranti, `Sanitize`, evento statico `Changed`.

Perché non la strada "instrada sull'host":

1. **Tre dei quattro editor non sono di `MainWindow`.** `DiffView` e `FileHistoryView`
   vengono istanziati una seconda volta dentro le finestre autonome di `CommitDialog`
   (`CommitDialog.cs:1015-1029`), e la MRU del filtro avanzato è scritta da un **modale**
   che è già stato distrutto molto prima che l'host salvi. Passare un callback all'host
   significherebbe cablarlo in ogni host, ramo per ramo.
2. **La scrittura è immediata**, quindi lo stato sopravvive anche a un `kill`, che salta
   `PersistLayout()` per intero.
3. **Un solo source of truth** per valori che hanno più editor.

Il file resta uno solo con quattro sezioni annidate, e ogni scrittura passa da
`ViewPrefsService.Update(mutate)` (load → muta → save) così che il gruppo di una
superficie non riverta quello di un'altra: la MRU viene appesa da un dialogo mentre la
toolbar del diff viene toggolata.

Restano dove sono (in `UiState`) larghezza, collasso e ordine delle categorie del pannello
sinistro: sono **layout posseduto dalla finestra**, già scritti dall'host, e spostarli
sarebbe una migrazione senza guadagno.

`UiStateService.Sanitize` (il clamp segnalato nella voce) non c'entra con queste quattro:
nessuna delle nuove chiavi passa da lì. Il mio `Sanitize` fa clamp solo su
`Diff.ContextLines` (0..`MaxContextLines`), `Diff.FontSize` (6..32, la stessa banda di
`DiffView.Zoom`), il nome dell'encoding (deve stare in `DiffTextService.EncodingNames`) e i
due nomi di enum dell'ordinamento.

## 2. (a) Opzioni del diff viewer — fatto

`App/Services/ViewPrefsService.cs` (nuovo) + `App/Services/DiffViewerOptions.cs` +
`App/Views/DiffView.cs`.

Undici opzioni persistite, tutte già esistenti come toggle veri in barra/menu (nessun
pulsante finto aggiunto): `ShowEntireFile`, `IgnoreWhitespace` (`-w`), `ShowNonPrinting`,
`WordDiff`, `IgnoreWhitespaceAtEol`, `IgnoreWhitespaceChange` (`-b`),
`TreatAllFilesAsText` (`--text`), `SyntaxHighlighting`, `EncodingName`, `ContextLines`
(`-U<n>`), `FontSize` (zoom). È lo stesso insieme che upstream tiene in `AppSettings`.

- **Riapplicazione**: `DiffViewerOptions.EnsureRestored()` (`DiffViewerOptions.cs`), chiamata
  come **prima istruzione del corpo del costruttore** di `DiffView` (`DiffView.cs:210+`).
  Perché lì e non in un inizializzatore statico: `DiffView` **aliasa** i due singleton nei
  propri *field initializer* (`DiffView.cs:118` e `:121`), che girano prima del corpo, ma
  **legge i valori** solo nel corpo (font a `:310`, encoding a `:395`, `IsChecked` di ogni
  toggle a `:333-390`). Il corpo è quindi l'ultimo momento sia abbastanza precoce per tutti
  i lettori sia **indipendente dall'ordine** in cui i due singleton vengono toccati.
  Idempotente (flag `_restored` alzato *prima* del load, così un load che eccepisce non fa
  ritentare le view successive riapplicando default sopra toggle vivi).
- **Scrittura**: `DiffViewerOptions.Persist()` in 11 punti di mutazione di `DiffView.cs` —
  i 7 callback di `ToggleTool`, la `SelectionChanged` del combo encoding, la voce di menu
  "Treat all files as text" (l'unica che scrive la proprietà diretta), `Zoom` e
  `ChangeContext`. Le altre voci del menu a ingranaggio flippano `IsChecked` del pulsante
  corrispondente, quindi ricadono già nei callback.
- **Non** ho aggiunto la sincronizzazione dello stato `IsChecked` fra istanze `DiffView`
  diverse: i valori erano già condivisi via singleton e i pulsanti di una seconda istanza
  erano già stantii prima di questo lavoro. È preesistente, fuori unità.

### Prova del ciclo (a) — cambia → Start→Exit → riapri

`XDG_CONFIG_HOME=/tmp/p32work/xdg`, repo `/tmp/p32repo`, display `:222`.

- **Prima**: `view-prefs.json` **non esiste** (nella dir solo `GitExtensions.settings`).
- Cambiati 6 valori dalla barra del diff: `-w`, `-b`, `¶`, `{;}`, `U+` ×2, `A+` ×1.
- **Dopo** (`/tmp/p32work/xdg/GitExtensions.Avalonia/view-prefs.json`):
  `IgnoreWhitespace: true`, `IgnoreWhitespaceChange: true`, `ShowNonPrinting: true`,
  `SyntaxHighlighting: true`, `ContextLines: 5`, `FontSize: 13` (gli altri 5 ai default).
- Chiusa con **Start → Exit** (processo terminato; non `kill`).
- Riaperta: i 4 toggle tornano **accesi** (`/tmp/p32work/a2_01_crop.png`) e la riga di
  comando git nella status bar del diff legge
  `… --find-renames -b -w -U5 0eb8c901…` (`/tmp/p32work/a2_01_diff.png`) — cioè le opzioni
  ripristinate arrivano **davvero a git**, non solo ai pulsanti. Caratteri non stampabili
  visibili come interpunti, font a 13 pt.

## 3. (b) Switch della file history — fatto

`App/Views/FileHistoryView.cs`. I quattro switch (`FullHistory`, `SimplifyMerges`,
`FollowRenames`, `ExactRenamesAndCopiesOnly`) passano **tutti** da
`SetOptions(FileHistoryOptions)` (`FileHistoryView.cs:328`), quindi c'è un solo punto di
scrittura: `PersistOptions` chiamata lì.

Ripristino nel *field initializer*: `_options = LoadPersistedOptions()` (era
`new()`), perché il menu costruisce `IsChecked` da `_options` ogni volta che si apre il
flyout (`FileHistoryView.cs:281,289,307,317`), quindi non serve altro per far ripartire la
UI nello stato giusto. Il caricamento è **per istanza** (non un singleton): la view è
istanziata due volte (tab History di `MainWindow` e finestra autonoma di `CommitDialog`,
`CommitDialog.cs:1015-1029`) e leggere il file alla costruzione dà all'istanza nuova lo
stato corrente senza plumbing fra istanze. `LoadPersistedOptions` non può eccepire (un
field initializer che lancia porterebbe giù la view).

## 4. (c) Filtri del left panel — fatto

`App/Views/RepoObjectsTree.cs`. Persistiti **otto** valori: la visibilità delle 6 categorie
(`_showBranches`…`_showStashes`, `RepoObjectsTree.cs:49-55`) e la coppia di ordinamento
(`_sortKey`/`_sortOrder`, `:112-113`), cioè esattamente ciò che i 6 `ToggleButton` della
barra e il menu "Sort by …" cambiano.

- **Ripristino**: `RestoreFilterPrefs()`, prima istruzione del costruttore
  (`RepoObjectsTree.cs:230+`), quindi **prima** di `BuildToolbar()` (`:303`): ogni
  `CategoryToggle` prende `IsChecked` dal campo mentre viene creato (`:363-390`) e il menu
  di ordinamento legge i due campi quando si apre, così la UI riparte nello stato salvato
  senza altro codice.
- **Scrittura**: `PersistFilterPrefs()` in due soli punti — il `Click` di `CategoryToggle`
  (unico handler per tutte e 6 le categorie) e `SetSort` (unico funnel dei 4 item di
  ordinamento).

**Cosa NON ho persistito qui, deliberatamente**:

- il **testo della casella di ricerca** (`_search`/`_filter`, `:45`/`:58`): non è una
  preferenza, è un cursore transitorio sopra l'albero — Escape lo azzera (`:428-431`),
  Enter/F3 ciclano i match (`:439-460`). Ripristinarlo riaprirebbe l'app su un albero
  **potato** senza causa visibile, cioè esattamente il tipo di stato persistito che
  sembra un bug;
- l'insieme dei **nodi espansi** (`_expandedKeys`, `:80`): stato di navigazione, non un
  filtro, e già conservato fra i rebuild della sessione;
- larghezza / collasso / **ordine** delle categorie: già persistiti dall'host in `UiState`
  (vedi §1), li ho lasciati lì.

## 5. (d) MRU dei filtri di revisione (avanzati) — fatto

`App/Views/RevisionFilterDialog.cs` + `ViewPrefsService.PushMru`.

Lista con **tetto 15** (`ViewPrefsService.MaxRevisionFilterMru`), **la più recente in
testa**, **senza duplicati**: `PushMru` fa `RemoveAll(equal)` e poi `Insert(0, …)`, così
riusare un filtro lo **promuove** invece di duplicarlo, e taglia la coda. L'uguaglianza è
`RevisionFilterMruEntry.Equals`, che confronta esattamente i 14 criteri che l'utente
edita. Il filtro **neutro** non entra mai (`IsEmpty`): "nessun filtro" non merita uno slot
ed è ciò che produce "Reset revision filters".

- **Scrittura**: sul click di OK, dopo `Collect()` (`RememberFilter`).
- **Riapplicazione** (il punto che la voce chiede di dimostrare): nuovo pulsante
  **"Recent filters ▾"** nella riga dei bottoni, con `MenuFlyout` popolato **prima** di
  `ShowAt` (trappola HANDOFF §3), una voce per entry con etichetta = riassunto in forma di
  opzioni git (`--grep …  --author …  -S …  --no-merges`, valori elisi a 28 caratteri).
  Cliccando una voce, `ApplyFilter` rimette **tutti** i 14 criteri nei controlli — è
  l'inverso esatto di `Collect()`, aggiungendo `Row.Set` (valore + gate spuntato solo se
  non vuoto, la stessa regola di `AddRow`). Il pulsante è **disabilitato** quando la MRU è
  vuota: nessun pulsante finto, nessun popup vuoto.
- Le etichette sono **dati** (pattern scritti dall'utente): non passano dalla lookup di
  traduzione e gli `_` sono raddoppiati per l'access-key parser.
- Nel mapping `RevisionFilter` ⇄ entry passano **solo** i criteri del dialogo: i membri
  `FollowRenames`/`ExactRenamesAndCopiesOnly`/`FullHistory`/`SimplifyMerges` di
  `RevisionFilter` appartengono alla modalità file-history (`RevisionGridView.cs:2030-2038`
  li preserva) e **non** devono essere resuscitati da un filtro ricordato.
- **Non** ho toccato la MRU del *quick filter* della griglia: esisteva già persistita
  (§0) e `RevisionGridView.cs` è fuori dai miei file.

---
---

# Unità M70 — i file picker "managed" seguono il tema dell'app

Base: `1affc7341`. File toccati: `App/App.cs` (+6 righe),
`App/ManagedFileChooserTheming.cs` (nuovo). **Nessuna** modifica a `Program.cs`,
`App/Theming/*`, o a qualunque view.

## 0. La premessa era **in parte falsa** — misurata prima di toccare

L'unità dice «fondo nero e icone ambra, **indipendente** dal tema chiaro/scuro
dell'app». Misurato sul binario di base, display `:224`, tema forzato da
`$XDG_CONFIG_HOME/GitExtensions.Avalonia/ui-state.json`:

| superficie del picker | tema scuro (prima) | tema chiaro (prima) |
|---|---|---|
| superficie principale / lista file | `#000000` | `#FFFFFF` |
| sidebar quick-links | `#2B2B2B` | `#F2F2F2` |
| icone cartella | ambra | ambra (identiche) |

Quindi il picker **segue** già la variante Fluent (perché `ThemeManager.Apply`
imposta `app.RequestedThemeVariant`, `App/Theming/ThemeManager.cs:186`): non è
theme-blind. Il difetto vero è che usa le superfici **base di Fluent**
(`#000000`/`#FFFFFF`) invece della palette `App.*` — nello scuro un lastrone nero
contro `App.Window` `#1E1E1E`, cioè il caso che si nota. Solo le **icone** sono
davvero invarianti.

Screenshot: `/tmp/mfc/b_dark_02_picker.png`, `/tmp/mfc/b_light_02_picker.png`.

## 1. Come è fatto il picker (letto, non indovinato)

`Avalonia.Dialogs.dll` 11.3.9 decompilato con `ilspycmd` (serve
`DOTNET_ROOT=$HOME/.dotnet`, altrimenti l'apphost net10 non trova il runtime):

- `ManagedFileChooser` è un **`TemplatedControl`** e `Avalonia.Dialogs.dll` **non
  contiene stili per esso**: le sue uniche risorse embedded sono un font e
  `/AboutAvaloniaDialog.xaml` (`!AvaloniaResourceXamlInfo` elenca solo quella).
- Il suo `ControlTheme` sta in **`Avalonia.Themes.Fluent.dll`**, registrato con la
  chiave `typeof(ManagedFileChooser)` nelle `Resources` del `FluentTheme`.
- Opzioni pubbliche utili: `ManagedFileDialogOptions { AllowDirectorySelection,
  CustomVolumeInfoProvider, ContentRootFactory }`. `UseManagedSystemDialogs()`
  esiste anche in overload generico `<TWindow>`.

**Le chiavi brush usate dal ControlTheme del chooser sono esattamente sei**, tutte
via `DynamicResource` (estratte dal XAML compilato,
`CompiledAvaloniaXaml.!AvaloniaResources`, il `ControlTheme` con
`TargetType == ManagedFileChooser`):

| chiave Fluent | cosa dipinge | mappata a |
|---|---|---|
| `SystemRegionBrush` | `Background` del chooser = tutta la superficie | `App.Window` |
| `SystemControlBackgroundChromeMediumBrush` | sidebar quick-links | `App.PanelAlt` |
| `SystemControlHighlightAltBaseMediumLowBrush` | **3 `Rectangle` alti 1px** (righelli) + `GridSplitter` | `App.Border` |
| `SystemControlBackgroundAltMediumBrush` | quick-link `:pointerover` | `App.PanelAlt` |
| `SystemControlBackgroundAltMediumHighBrush` | quick-link `:selected` | `App.Selection` |
| `SystemControlHighlightAccentBrush` | riga `SelectedLine` | `App.Accent` |

## 2. Strada scelta: ridefinire quelle chiavi in `Application.Resources`

`Application.TryGetResource` cerca in `Resources` **prima** di `Styles` (dov'è il
`FluentTheme`), e sono `DynamicResource`, quindi la ridefinizione vince.
`ManagedFileChooserTheming.Install(app)` è chiamato da `App/App.cs:27` subito dopo
`ThemeManager.Initialize`, e passa i **brush per riferimento**, così il cambio
tema a caldo continua a funzionare.

**Raggio d'azione dello "spill", contato**: su tutto il tema Fluent quelle sei
chiavi hanno 11 usi, 8 dentro il chooser. I 3 fuori sono: `SystemRegionBrush` nei
`ControlTheme` di **`Window`** e **`EmbeddableControlRoot`** (loro `Background` di
default) e `SystemControlHighlightAccentBrush` nel `ControlTheme` di
**`ProgressBar`** (indicatore). Tutti e tre vogliono *esattamente* il valore di
palette che gli ho dato — e le view che impostano il proprio `Background` non sono
toccate (un valore locale batte un setter di `ControlTheme`). **Prova**: la
finestra principale è **pixel-identica** prima/dopo in entrambi i temi (0 pixel
differenti, `PIL.ImageChops`).

### Scartata: `ContentRootFactory` (sarebbe stata più pulita, non è raggiungibile)

`ManagedStorageProvider.PrepareRoot` usa `ManagedFileDialogOptions.ContentRootFactory`
e ripiega su `new Window()` solo se è null; `UseManagedSystemDialogs` legge le
opzioni da `AvaloniaLocator` nel callback `AfterSetup`, e `AppBuilder.SetupUnsafe`
esegue `Instance.Initialize()` **prima** di `AfterSetupCallback` — quindi il timing
da `App.Initialize()` sarebbe andato bene. Blocco reale: in 11.3.9
`AvaloniaLocator.CurrentMutable` e `Bind<T>()` sono **`internal` nella reference
assembly** (`ref/net8.0/Avalonia.Base.dll`), pubblici solo
nell'implementazione → servirebbe reflection su una API privata, oppure una riga in
`Program.cs`. Provato e ritirato (`error CS0117: 'AvaloniaLocator' non contiene una
definizione per 'CurrentMutable'`).

**Cablaggio richiesto all'integratore — solo se si vuole lo scoping.** Nessuno è
necessario: la soluzione committata è completa. Se in futuro si preferisse
limitare le override al solo picker, l'unica riga da aggiungere in
`Program.BuildAvaloniaApp()` sarebbe:

```csharp
.With(new Avalonia.Dialogs.ManagedFileDialogOptions
{
    ContentRootFactory = () => new ManagedFileChooserRoot(),  // Window con le 6 chiavi nelle sue Resources
})
```

## 3. Due trappole trovate

- **`BindingPriority.Template` (2) batte `Style` (3)**: la lista file `PART_Files`
  ha il `Background` assegnato *dentro* il `ControlTemplate`, quindi un setter di
  `Style` su `ManagedFileChooser /template/ ListBox#PART_Files` è **silenziosamente
  morto** (misurato: la lista è rimasta sul background del chooser). L'ho rimosso.
  È il caso *opposto* alla nota HANDOFF su `TextBoxSurface` (là lo style batte un
  valore locale su un figlio di template). Upstream `PART_Files` è `Transparent`,
  quindi mostra già `App.Window`: risultato coerente comunque.
- **Selettore morto in Avalonia**: il setter Fluent per la sidebar seleziona
  `ListBox#QuickLinks` mentre l'elemento si chiama **`PART_QuickLinks`** → non
  matcha mai, e la sidebar prendeva il background dal `ControlTheme` di `ListBox`.
  Perciò `SystemControlBackgroundChromeMediumBrush` da solo non basta: serve uno
  `Style` esplicito su `PART_QuickLinks` (uno `Style` in `Application.Styles` batte
  un setter di `ControlTheme`, e quello **funziona**: `#2B2B2B → #2D2D30`).
  La mappatura della chiave resta comunque, per il giorno che Avalonia corregge il nome.

## 4. Vicolo cieco registrato con la prova: le **icone ambra**

Non sono sovrascrivibili e **non le ho toccate**. I glifi cartella/file/volume
sono `DrawingGroup` a gradiente ambra **hard-coded** dentro le `Resources` del
`ControlTheme` Fluent, sotto la chiave `Icons` — un `ResourceSelectorConverter`
(che *è* un `ResourceDictionary`, `Avalonia.Dialogs.Internal`) — e il template li
raggiunge con **`StaticResource`**. Uno `StaticResource` si risolve sul parent
stack **a build time**, e il dizionario del `ControlTheme` stesso è il primo
elemento di quello stack: nessun dizionario esterno può vincere. Il view model
sceglie la chiave con `ManagedFileChooserItemViewModel.IconKey` →
`"Icon_Folder"` / `"Icon_File"` / `"Icon_Volume"`.

**Alternativa valutata e NON implementata**: replicare il `ControlTheme` del
chooser (≈700 righe di template ricostruite dall'IL compilato, più i 3 gruppi di
`DrawingGroup`), da rifare a ogni bump di Avalonia. Costo alto, beneficio basso:
sono **contenuto non testuale**, quindi la soglia 4,5:1 non li riguarda, e restano
leggibili su entrambi i fondi. Il picker funzionante ma con icone ambra è meglio
di un picker reimplementato.

## 5. Verifica — contrasto WCAG (luminanza relativa, `python3` + PIL)

Fondo = colore modale del box; inchiostro = pixel a massima distanza di luminanza
(nucleo del glifo). Soglia richiesta: **4,5:1**. Script: `/tmp/mfc/wcag.py`.

### Tema scuro (`b_dark_02_picker.png` → `a2_dark_02_picker.png`)

| elemento | prima (fondo → contrasto) | dopo (fondo → contrasto) |
|---|---|---|
| riga file "eng" | `#000000` → 21,00:1 | `#1E1E1E` → **16,67:1** |
| header colonna "Name" | `#000000` → 21,00:1 | `#1E1E1E` → **16,67:1** |
| "Show hidden files" | `#000000` → 21,00:1 | `#1E1E1E` → **16,67:1** |
| voce sidebar "Desktop" | `#2B2B2B` → 14,16:1 | `#2D2D30` → **13,73:1** |
| barra indirizzo | `#000000` → 21,00:1 | `#121212` → **18,73:1** |
| pulsanti OK / Cancel | `#333333` → 12,63:1 | `#4B4B4B` → **8,72:1** |

Fondo dell'app nello **stesso** screenshot: `App.Window` `#1E1E1E`,
`App.PanelAlt` `#2D2D30`, `App.Border` `#3F3F46` → il picker ora **coincide**
esattamente con la palette, dove prima era `#000000`.

### Tema chiaro (`b_light_02_picker.png` → `a2_light_02_picker.png`)

| elemento | prima | dopo |
|---|---|---|
| riga file "eng" | `#FFFFFF` → 21,00:1 | `#F3F3F3` → **18,93:1** |
| header colonna "Name" | `#FFFFFF` → 21,00:1 | `#F3F3F3` → **18,93:1** |
| "Show hidden files" | `#FFFFFF` → 21,00:1 | `#F3F3F3` → **18,93:1** |
| voce sidebar "Desktop" | `#F2F2F2` → 18,76:1 | `#ECECEC` → **17,78:1** |
| barra indirizzo | `#FFFFFF` → 21,00:1 | `#F8F8F8` → **19,77:1** |
| pulsanti OK / Cancel | `#CCCCCC` → 13,08:1 | `#C3C3C3` → **11,91:1** |

Superficie del picker = `App.Window` `#F3F3F3`, identica al fondo della finestra
principale nello stesso screenshot; sidebar = `App.PanelAlt` `#ECECEC`;
righelli = `App.Border` `#C4C4C4`. **Ogni testo supera 4,5:1 in entrambi i temi**
(minimo misurato 8,72:1, i pulsanti nello scuro).

Il contrasto scende leggermente rispetto a prima (21:1 → ~17-19:1) ed è
intenzionale: 21:1 era il sintomo, cioè nero/bianco puri invece delle superfici
dell'app.

## 6. Verifica — nessuna regressione funzionale

- `Ctrl+O` → `Browse…` → digitato `/tmp/mfc/openme` nella barra indirizzo →
  `Return` → `OK`: **l'app ha aperto il repo** (toolbar `/tmp/mfc/openme`, header
  `/tmp/mfc/openme — 5 commits`, status bar `openme — master`).
  `/tmp/mfc/a2_light_03_navigated.png`, `/tmp/mfc/a2_light_04_after_ok.png`.
- Ripetuto in tema **scuro** verso `/tmp/mfc/repo`: aperto.
  `/tmp/mfc/a2_10_dark_after_ok.png`.
- **Cambio tema a caldo**: `View → Appearance → Dark theme` con l'app in chiaro →
  la finestra passa a scuro e il picker aperto **dopo** nasce già
  `App.Window #1E1E1E` / `App.PanelAlt #2D2D30`.
  `/tmp/mfc/a2_08_hotswitch_dark.png`, `/tmp/mfc/a2_09_hotswitch_picker.png`.
- Il dialogo `Ctrl+O` differisce prima/dopo per **618 pixel in un solo bbox**
  `(404,371)-(514,384)`: è la nuova voce `/tmp/mfc/openme` nei "Recent
  repositories", cioè la prova che il picker ha restituito il path.
- Log runtime pulito (nessuna eccezione, nessun asset non risolto).
- Build: `Errori: 0`.

## 7. Cosa NON ho fatto

- Non ho reimplementato il file chooser (vedi §4).
- Non ho ricolorato le icone ambra (non sovrascrivibili, §4).
- Non ho stilizzato `PART_Files` (impossibile via `Style`, §3) né i `TextBox`
  editabili del picker: la barra indirizzo è un **input editabile** e per la nota
  HANDOFF §3 il riempimento al focus è un'affordance voluta — `TextBoxSurface` va
  applicato solo alle superfici di sola lettura. Misura comunque 18,73:1 / 19,77:1.
- Non ho toccato `Program.cs`, `App/Theming/*`, né chiesto nuove chiavi `App.*`:
  tutte e sei le mappature usano chiavi già registrate in `ThemeManager.Keys`.
