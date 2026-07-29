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
