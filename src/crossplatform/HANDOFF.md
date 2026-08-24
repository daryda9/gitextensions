# HANDOFF — port Linux/Avalonia di Git Extensions

Documento di passaggio per chi (umano o agente) riprende il lavoro.
Fonte di verità dettagliata: **`src/crossplatform/PORTING.md`** (milestone M1–M210,
checklist di parità, metodo del loop). Questo file è il riassunto operativo.
Piano di lavoro e residui aperti: **`src/crossplatform/ROADMAP.md`**.

> Nota sull'ordine: dentro `PORTING.md` le milestone **non** sono in ordine
> numerico dall'inizio alla fine del file — i giri recenti sono inseriti a metà,
> in ordine decrescente dentro il proprio blocco. Si cerca per numero, non per
> posizione.

---

## 1. Stato attuale

| | |
|---|---|
| Branch | `linux-avalonia-port` |
| HEAD al momento dell'handoff | `c18f2e369` — **M218–M220: il selettore di commit arriva agli altri tre campi** (2026-08-24, tre unità, tre commit). Chiuso il residuo di M214: upstream serve `FormChooseCommit` da quattro form (misurato: `FormRebase`, `FormCherryPick`, `FormArchive` su due campi, `FormDiff`), il port lo dava solo al rebase. **M218 (`82db00fcd`)**: i due campi revisione di `ArchiveDialog` hanno il `…` accanto alla casella (che resta libera); il `…` del filtro segue la sua checkbox come upstream (`FormArchive.cs:199-203`). Misurato: revisione B + filtro A → lo zip contiene esattamente `git diff A..B`. **M219 (`71284d732`)**: «Compare to commit…» nel sottomenu Compare della griglia — upstream **non** ha la voce di menu (cercato: il commit arbitrario si sceglie da *dentro* `FormDiff`, `:231-240`), ma la superficie di confronto del port è la `DiffView` condivisa senza chrome propria, quindi il picker entra dal menu; commit scelto = lato vecchio, BASE ricordata come preselezione. **M220 (`c18f2e369`)**: il cherry-pick passa da `Views/CherryPickDialog` (port di `FormCherryPick`) — prima il menu faceva `git cherry-pick` **diretto**: un **merge commit non si poteva prendere** (niente `-m`), `-x` irraggiungibile, output senza console. Ora: pannello del commit, «Choose another revision» via picker, lista dei parent solo su un merge (parent N → `-m N`, rifiuto col messaggio di upstream se nessuno scelto), checkbox nelle stesse chiavi `AppSettings`, esecuzione nel `GitProcessDialog`, conflitti al chiamante via `ConflictFlow`; nel servizio `StashOpsService.CherryPickStreaming` su `GitStreamRunner`. Misurato su git: `-m 1 -x` del merge su un branch fermo alla radice porta esattamente il file del ramo mergiato con «(cherry picked from commit …)». La chiave di routing resta `"Cherry-pick"` (contratto host + prima registrazione di `FileHistoryView`), la caption è quella di upstream. **Trappola (di nuovo M216)**: `find bin -name …dll | head -1` per il run headless ha pescato un harness di dieci giorni prima — il path del dll dell'app va scritto per esteso. **CI: il run su `104a8c861` è VERDE** (build + 7 harness sul runner ospitato; anche i due push precedenti post-fix verdi) — il fix M215 è confermato, e lo stato dei run si legge **da anonimo** via `GET /repos/daryda9/gitextensions/actions/runs` (sono i log grezzi a volere un token). Prossima libera: **M221**. Prima: `104a8c861` — **M217: le voci «diverso dall'originale» del README verificate contro il sorgente upstream** (2026-08-17). Su diciotto, **nove erano sbagliate o mal inquadrate**, e si vedeva solo leggendo `src/app`: upstream **ha** la barra sopra un'operazione ferma (`InteractiveGitActionControl`, con la stessa frase), **ha** un terminale integrato (ospita ConEmu/mintty, dipendenza esterna, solo Windows), **ha** `Edit todo…` (il file grezzo in un editor), **mostra** le immagini (`ViewMode.Image`, una revisione sola), **ha** il selettore di commit (`CommitPickerSmallControl`, su quattro campi), **ha** `FormFileHistory` con le stesse schede, **ha** una suite di test grossa (`tests/app`, `tests/plugins`) che però non compila questo albero, **ha** temi light/dark scelti a mano (ma **non** segue il sistema), **ha** GitHub come plugin (`src/plugins/GitHub3`). Restano nuove e verificate per assenza: schede di repository e chip per checkout, palette dei comandi, editor di merge a tre vie interno, confronto affiancato interno, rerere nel flusso, menu nella barra del titolo, UI size, larghezze di colonna persistite, icone vettoriali colorate per ruolo, impostazioni JSON sotto XDG, `.deb`. **Regola**: una differenza si dichiara solo dopo averla cercata in `src/app`, e la ricerca si scrive accanto alla voce. Prossima libera: **M218**. Prima: `c7a17147a` — **M216: una cartella che si chiama `.git` non è un checkout** (2026-08-17). Trovato **fotografando il README**: i chip da 3 px che colorano ogni scheda per checkout (così due cloni dello stesso progetto, e i loro submodule omonimi, si distinguono con l'occhio invece di leggere due path lunghi che differiscono nel mezzo) non comparivano. `roots=[/tmp] distinct=1`: c'era una **cartella `.git` vuota** in `/tmp`, e `WorkspaceRoot.IsWorkingTree` accettava qualunque voce chiamata `.git`, quindi ogni repository di prova risaliva a `/tmp` e finiva nello stesso checkout — nessun colore, e il tooltip «in checkout:» che nominava una cosa inesistente. Ora una cartella deve contenere `HEAD` e un file `.git` deve cominciare con `gitdir: `. Provato con la striscia: due cloni sotto `/tmp` col `.git` vuoto rimesso, **zero chip prima, quattro dopo**. Il README ha ora una sezione sulle schede con quattro immagini (striscia con anteprima in corsivo, schede duplicate numerate, colori per checkout) e due confronti di icone Modern colorate / monocrome / Classic. Trappola incontrata: una build strumentata non compilava (`Avalonia.Media` → CS0234, serve `global::`) e l'errore era mangiato da `| tail -2`, quindi per un giro ho misurato il dll vecchio. Prossima libera: **M217**. Prima: `1b9d40c66` — **M215: il primo giro di CI trova uno stallo che nessuna macchina di sviluppo mostrava** (2026-08-14). Il primo run del workflow del M211 è rosso: `navigation-snapshot` **appeso** per tutti i 120 s di timeout con il log **vuoto**, su runner ospitato, mentre qui passa in 0,3 s anche strozzato a due core. Difetto **del banco**: teneva un caricamento parcheggiato dentro il factory e apriva il cancello **dopo** l'`await` della generazione successiva — ma dopo il primo `await` il flusso gira su un worker del pool, quindi le sue `Task.Run` finiscono nella coda **locale** di quel worker, e un accodamento locale **non chiede** un altro thread; quando quel worker si parcheggia, la sua coda resta **arenata** e nessuno viene svegliato a rubarla (strumentato: factory della seconda generazione già ritornato, due lavori `pending`, pool all'unico thread parcheggiato). **Alzare i thread minimi non cura niente** — provato e scartato. Ora la sezione che parcheggia ha un **thread proprio** (caricamenti avviati da fuori dal pool → coda **globale**, dove un worker mancante viene chiesto) e aspetta che il primo factory sia dentro prima di invalidare: niente ordinava le due `Task.Run`, e a un core la coda locale è **LIFO**, quindi la generazione 2 si prendeva l'identità «old» e l'asserzione moriva con `'old'` invece di `'new'`. Ogni parcheggio è **limitato**, così il prossimo ciclo nomina il cancello. Misurato: 20 run verdi a 1/2/4/16 core (prima: stallo **ogni volta** a un core), banco al completo verde a un core, **non vacuo** (togliendo lo sfratto in `Invalidate` fallisce a un core e a sedici). Attorno: `run-all.sh` dice quando un log fallito è **vuoto** — appeso, non log perduto — ma solo se il banco è davvero partito; e il workflow raccoglie **file regolari per path relativo**, perché copiando la sandbox in blocco prendeva il `TMPDIR` sandboxato coi socket di `dotnet` e l'artefatto avvisava `ENTRYNOTSUPPORTED` dieci volte. Prossima libera: **M216**. Prima: `0e1753bd9` — **M214: l'intervallo del rebase ha un selettore di commit** (2026-08-14). `Views/ChooseCommitDialog` è la griglia vera in una **terza** istanza (come la storia di un file usa la seconda: la griglia della shell porta il posto della persona nella storia, e l'upstream deve salvare e ripristinare quattro impostazioni globali attorno al suo selettore proprio perché la sua istanza è condivisa), aperta dal `…` accanto a «From (exc.)» e limitata al branch corrente fino al merge base col bersaglio. Il limite è una capacità nuova e non un trucco: `RevisionService.LoadRevisionPage` prende `excludeAncestorsOf` → `HEAD ^<commit>`, e `RevisionGridView.SetWalkBound` lo tiene fuori dalle view options — **il primo tentativo passava `^<hash>` come ref filtrato e `SetRefCatalogue` lo scartava**, quindi solo il **primo** walk era limitato (misurato: riga di stato «filtered (no ref selected → HEAD)» sopra righe ancora limitate). Provato a schermo fino in fondo: scelto «feature commit 2», l'anteprima diventa `rebase --onto <main> "bb7c4556" "feature"` e l'esecuzione rigioca 3 e 4 lasciando fuori 1 e 2 — la semantica **esclusiva** promessa dalla riga del selettore. Prossima libera: **M215**. Prima: `efde6267b` — **M211–M213: i banchi girano da soli, le immagini troncate si dichiarano, e il merge chiede il suo messaggio** (tre unità, 2026-08-14). **M211 (`becadd6f8`)**: i banchi non li lanciava nessuno — ora `GitExtensions.Avalonia.slnx` li compila tutti, `Tests/run-all.sh` li lancia ognuno in una sandbox propria (`XDG_CONFIG_HOME`/`TMPDIR` isolati, `GIT_CONFIG_GLOBAL`/`SYSTEM` silenziati, timeout per banco, directory di lavoro tenuta sui fallimenti perché è la prova) e `.github/workflows/crossplatform-build.yml` fa entrambe le cose con `-warnaserror`; compilare la soluzione ha fatto uscire due avvisi VSTHRD nel banco `Perf`, mai stato in una build. Runner verificato non vacuo con un'asserzione invertita. **M212 (`ca488bb75`)**: Skia decodifica un file troncato in un bitmap **intero** con le righe mancanti in bianco e non lo dice (PNG/GIF/BMP fino al 2% del file, JPEG/WEBP fino a ~70%, misurato) — `ImageIntegrity` chiede a `SKCodec` il codice di risultato, SkiaSharp diventa dipendenza diretta fissata alla **2.88.9** di Avalonia 11.3.14, e la barra apre con «TRUNCATED FILE»; `Tests/ImageIntegrityRegression`, 124 casi, invariante e non tabella, 108 falliscono se la si disabilita. **M213 (`efde6267b`)**: `merge --continue` non fissa più `GIT_EDITOR=true` — editor a script che **rifiuta**, testo di git catturato e mostrato, risposta chiusa con `git commit --cleanup=whitespace` (`whitespace` e non `strip`: la legenda era già via, quindi una riga `#` scritta dalla persona è contenuto); misurato su git 2.43 che l'uscita 1 lascia `MERGE_HEAD` e l'indice intatti, quindi annullare non costa niente. Il meccanismo dell'editor a script è ora `Services/GitScriptedEditor`, uno solo per rebase e merge, col quoting che era stato imparato a caro prezzo. **Annullare è riportato come annullare**: `GitProcessDialog.SettleCancelled` chiude come **Cancelled** invece di un `Failed` rosso sopra la riga «problem with the editor» col path di un temporaneo, su due righe perché la console non manda a capo. Prossima libera: **M214**. Prima: `c5152dd43` — **M207–M210: la difesa scritta una volta, applicata agli altri sei file di impostazioni** (quattro unità, 2026-08-14). M204 aveva messo in sicurezza **un** file e lasciato scritto che gli altri sei avevano lo stesso difetto. **M207 (`418036239`)**: la meccanica (sostituzione atomica, carica-modifica-salva fuso dentro il lock, lock `.lock` di lato) esce da `ViewPrefsService` e diventa `JsonSettingsFile<T>`, che non sa niente di quale documento stia scrivendo — le parti specifiche arrivano come modello; la prova che l'estrazione è fedele è che `Tests/ViewPrefsRegression` passa ancora **invariato**. **M208 (`df83ed36a`)**: `app-settings`, `ui-state`, `commit-info`, `favorites`, `scripts` e `hotkeys` ci passano tutti; tutti e sei usavano `WriteAllText`, che **tronca** prima di riempire, e un file troncato si legge come «nessuna impostazione» — dimensione della finestra, tema, scorciatoie e preferiti **azzerati in silenzio**. Trappola trovata rileggendo: il modello di `HotkeyService` va dichiarato **sopra** `Shared`, perché gli inizializzatori statici girano in ordine testuale e `Shared` costruisce un servizio nel proprio. **M209 (`76d0504c4`)**: i punti di scrittura passano ai **delta**. Il caso peggiore era la finestra principale, che teneva `ui-state` per tutta la sessione e alla chiusura riscriveva tutto — **uscire dal programma annullava** lingua, azione di pull e scelta al push rifiutato salvate ore prima. Due sottigliezze: una spunta che **nega** è sbagliata in una scrittura fusa (la delegata può girare più volte), quindi le sei spunte del pannello ora calcolano il valore e lo **impostano**; e `LeftPanelCategoryOrder` arrivava al file **solo** perché la scrittura totale se lo portava dietro, trovato leggendo tutti gli assegnamenti a `_uiState.*` **prima** di togliere quella scrittura. Verificato a schermo con Xvfb e `XDG_CONFIG_HOME` isolato: la × della barra del titolo lascia un `ui-state.json` completo di 32 campi. **M210 (`c5152dd43`)**: `Tests/SettingsStoresRegression`, 41 casi in ~6 s, con processi veri e SIGKILL; **non vacuo su entrambe le metà** — togliere la rilettura rompe 15 casi su 41, rimettere `WriteAllText` fa vedere 541 letture spezzate su 23365. Prossima libera: **M211**. Prima: `7b84e9824` — **M204–M206: la chiusura dei residui che il collaudo aveva lasciato scritti**. Prossima libera: **M156**. Prima: `bfaee4643` — Prima: `b83238a74` — **M147-M148: `git grep` dalla lista dei file, e le due view morte cablate**. Prima: `174146b3d` — Prima: `44850dc33` — **M136-M145: chiuse dieci voci della coda utente in un blocco solo** (`M136` copy path assoluto col sottomenu; `M137` impostazione «Terminal command» con `{dir}`/`{shell}`, che è il modo per usare Warp; `M138` «Fetch all» e «Fetch and prune all» in toolbar con tre glifi nuovi; `M139`-`M141` il layer `T()` esteso a **tutte** le view rimaste — ~290 stringhe in 21 file, tre subagent in parallelo su file disgiunti; `M142` `TranslationService.TPlural` (due forme, non un «(s)») col bisect come primo chiamante, palette della sintassi Classic ri-solta per la protanopia — peggior coppia da ΔE 6,45 a 24,54 — e titolo di `PushDialog` senza path; `M143` da 2 a 4 revisioni selezionate ora danno **più gruppi di diff sul merge base** come `FileStatusDiffCalculator`, con `FileStatusListView` che sa mostrare N sezioni e il clic su un file che carica la coppia del suo gruppo; `M144` pill delle note tematizzata (`App.RefNote`, viola, ΔE 48 dagli altri ref sotto simulazione) e via i due alias di `CollapseHome`; `M145` **più schede sulla stessa repository** — `RepoTabEntry.Id` GUID, «Duplicate tab» nel menu della scheda, etichette numerate `repo (1)`/`repo (2)`, migrazione dello `ui-state.json` vecchio senza perdere schede). Prossima libera: **M146**. **Tre voci verificate e chiuse senza scrivere codice** (la regola «verificare la premessa» ha evitato tre riscritture): la guardia «nothing staged» esclude già un merge commit legittimo via `MERGE_HEAD`, il discard del dialogo di commit è già multi-selezione, e il titolo è già centrato sulla finestra da M128. **Due view risultate morte**: `BranchTagPanel` e `RemotePanel`, nessuno le istanzia — da decidere se cancellarle. **Residuo aperto della vecchia nota P2**, riscritto e ridotto: manca solo la ricerca `git grep` dalla lista dei file. **Difetto minore noto**: dopo «Duplicate tab» il riquadro Commit info resta vuoto finché non si tocca la selezione. Prima: `62ac7447a` — **M135**. Prima: `a073cd787` — **M134: spinner di caricamento su griglia e albero** (nuovo `Views/BusyOverlay`: velo + arco rotante + didascalia, con **ritardo di 250 ms** perché i ricaricamenti brevi non devono produrre un tremolio; griglia: solo sopra la lista e solo sui restart, mai sugli append; albero: tutto il pannello, nascosto solo quando una passata dipinge davvero, altrimenti il passaggio di consegne fra passate superate farebbe lampeggiare il velo e ripartire il ritardo). Prossima libera: **M135**. Prima: `49841734d` — **M133: la finestra aperta ha l'icona del prodotto** (mancavano due cose, misurate con xprop: `Window.Icon` non era mai assegnata quindi `_NET_WM_ICON` non esisteva e il dock metteva l'ingranaggio, e il `.desktop` non dichiarava `StartupWMClass=GitExtensions.Avalonia`; nuovo `Theming/AppIcon`, installato come style sull'`Application` perché `Window.Icon` è una styled property e così copre ogni finestra; va reinstallato il .deb per la voce .desktop). Prossima libera: **M134**. Prima: `d7c73b0a3` — **M132: schede trascinabili e doppio clic che fissa** (il `DoubleTapped` non partiva mai perché `Sync` faceva Clear+re-add dei figli a ogni attivazione e ri-genitorare azzera lo stato di input: ora i figli si toccano solo se la sequenza cambia e il doppio clic si legge da `ClickCount`; riordino con soglia di 5 px, slot scelto sul punto medio della vicina, puntatore catturato dalla striscia, trascinare fissa la scheda come in VS Code, ordine già persistito dalla lista). Prossima libera: **M133**. Prima: `6bf91a68d` — **M131: più repository in una finestra, schede stile VS Code** (clic singolo su submodule/worktree = anteprima in corsivo che la prossima sostituisce, doppio clic la fissa, ogni altra porta apre fissato; nuovo `Views/RepoTabStrip`, `Theming/RepoTabsOption`, `UiState.OpenRepoTabs`/`ActiveRepoTab`, opzione «Repository tabs» in Appearance con le schede come default; una sola copia delle viste — la scheda porta riga selezionata e tab in basso, catturati da `_loadedTab` e non da `Active` perché la striscia ha già cambiato attivo, e la selezione è registrata con `SelectCommitWhenLoaded` perché la griglia carica in asincrono; `Ctrl+W` chiude la scheda, `Ctrl+PagSu/Giù` scorrono). Prossima libera: **M132**. Prima: `8c30911c7` — **M130: i pulsanti della finestra Stash come quelli del dialogo di commit** (Apply/Pop/Drop e i quattro pulsanti di salvataggio erano gli ultimi con la chrome di Fluent, contorno chiaro compreso: ora `BarButtonStyles.ApplyActions`, riempimento alzato e nessun bordo, solo in Modern; non `Apply`, perché non stanno su una barra). Prossima libera: **M131**. Prima: `c3008bd0b` — **M129 + due correzioni: in modern la scheda selezionata è sottolineata, e il cambio di stile non crasha più** (un template per stile ri-templatizzava i `TabItem` e Avalonia rifiutava di ri-genitorare l'header vivo: ora template unico a tre righe e il modern sposta il marcatore con un setter su `Grid.Row`; raggio degli angoli legato invece che assegnato; e `Grid.SetRow` nel template rendeva la riga un valore locale, quindi la linea restava sopra l'etichetta — ora la riga non si imposta nel template). Prossima libera: **M130**. Prima: `7a33eb988` — **M128: il menu nella barra del titolo, stile VS Code** (su X11 `ExtendClientAreaToDecorationsHint` è un no-op — sondato — mentre `SystemDecorations.None` è onorato: quindi `Views/TitleBar` disegna i pulsanti finestra e `Views/ResizeGrips` restituisce gli otto bordi; overflow «…» misurato da `MainMenu.FitTo`, mai una soglia; opzione «Title bar» in Appearance, default unificata, `UiState.TitleBar`, **indipendente** dallo stile, cambio a caldo). Prossima libera: **M129**. Prima: `3b8ee7ec0` — **M127: il pulsante della shell apre un terminale che sopravvive** (`x-terminal-emulator` qui è Warp, che rifiuta `-e` ed esce con 2 dopo che `Process.Start` è già riuscito: si riportava successo e non si provavano gli altri; ora 700 ms di grazia, uscita non-zero = fallimento e si passa al candidato dopo, lista allargata a kgx/ptyxis/tilix/terminator/mate-terminal/alacritty/kitty/foot/urxvt, lanci esterni fuori dal thread UI). Prossima libera: **M128**. Prima: `0156dd902` — **M126: un dialogo si apre sul pulsante per cui è stato aperto** (`DialogKeys.FocusOnOpen`, usato da Push e Pull; focus e non attivazione, e mai sui dialoghi che aprono su una domanda o su un'azione distruttiva). Prossima libera: **M127**. Prima: `96715fac8` — **M125: markdown nell'evidenziazione della sintassi** (`.md` cadeva in `_ => null`; passata dedicata — fence, titoli, citazioni, marcatori di lista, code span, grassetto/enfasi e la URL dei link — che riusa il bit di blocco degli altri linguaggi). Prossima libera: **M126**. Prima: `76595811c` — **M124: la storia di un file tiene rami e merge** (come l'originale: `git log --name-only --follow` per raccogliere i nomi storici, poi il walk ordinario con quei nomi come pathspec e **senza** `--follow`, così git riscrive i parent — nuovo `Services/FollowedPathService`; riabilitati Branches e View in modalità file-history, scope d'ingresso all-branches; fallback a `--follow` + `ChainFollowedHistory` per cartelle/più path/pathspec > 31000, deciso da `RevisionPage.FollowedWithoutParentRewrite`; la mappa dei nomi storici è un sottoprodotto del passo 1). Prossima libera: **M125**. Prima: `37658f73c` — **M123: pannello del diff virtualizzato su AvaloniaEdit** (nuova dipendenza `Avalonia.AvaloniaEdit` **11.3.0**, fissata alla linea 11.3; `PORTING.md` a file intero, 6559 righe: **1547 ms → 70 ms**, e lo scroll non ridisegna più tutte le righe; nuovo `Views/DiffColorizing.cs`, `DiffSyntaxHighlighter` riusato intatto; ¶ non riscrive più il testo, Ctrl+C rispetta la selezione, via i tetti dell'evidenziazione; `.deb` invariato, verificato con publish self-contained). Prossima libera: **M124**. Prima: `af9f0301a` — **M122: lo stile classico ritrova il bitmap della lente** (`Icons.ClassicNameOf`: «Search» → `Preview.png`, consultato da `GlyphSource.Draw` prima dell'asset loader; l'avviso `[IconLoader] icon 'Search' did not resolve` era il classico che chiedeva un PNG inesistente). Prossima libera: **M123**. Prima: `c8ffa023c` — **M121: anche la toolbar di File History è piatta** (i suoi quattro pulsanti erano l'ultima striscia incorniciata: ora `toolbtn` di `BarButtonStyles`). Prossima libera: **M122**. Prima: `3f6a28c1b` — **M120: la finestra File History disposta come `FormFileHistory`** (toolbar in cima, riga di stato collassata quando non ha nulla da dire, una sola sonda sul blob invece di due, pulsante Git command log, splitter 1*/4/3* con `MinHeight` **sulla riga**, Ctrl+Tab fra le schede saltando quelle disabilitate). Prossima libera: **M121**. Prima: `fc24d6e8e` — **M119: una toolbar sola, un filtro solo** (`ToolStripMain` era già fedele; via la mezza copia del filtro in alto — il menu «All branches» mentiva sull'ambito — e via l'etichetta repo/branch in fondo, entrambe assenti in `FormBrowse`; aggiunte le scorciatoie Pull-merge/Pull-rebase di `InsertFetchPullShortcuts` e i suffissi di hotkey nei tooltip; `Ctrl+E` va alla casella della griglia; la striscia ora entra senza overflow). Prossima libera: **M120**. Prima: `1582e4128` — **M118: il grafo disegnato come nell'originale** (palette `GraphBranch1..7` a sette colori, metriche di `GraphRenderer`, nodo quadrato per le righe con ref e anello su HEAD, diagonali a Bézier, `StraightenLaneShifts` che ricuce le due metà di un arco in una sola diagonale nodo-a-nodo, Author 130 / Commit ID 64). Prossima libera: **M119**. Prima: `c92d4252b` — **M117: la lista dei file cambiati dice di che confronto è** (intestazione «(N)  Diff with A `<sha>`: `<subject>`» come `FileStatusWithDescription.Summary` dell'originale: `SetFiles(rows, summary)` la disegna come gruppo pieghevole sopra gli altri; la revisione è nominata sul thread di background del load via `DiffService.DescribeRevision`/`FirstParentOf`; il commit radice resta senza intestazione). Prossima libera: **M118**. Prima: `2924ccc7e` — **M116: una sola linea nella storia di un file, e il diff di una selezione multipla** (`--follow` NON fa riscrivere i parent da git, quindi il grafo del file era una scaletta di monconi: `RevisionService.ChainFollowedHistory` ri-collega ogni riga a quella sotto, toccando solo `GraphParents`; da tre commit selezionati in su si mostrava un commit solo → `RangeEnds` prende i due estremi, con dedup dell'annuncio perché un Ctrl+clic alza `SelectionChanged` due volte; la finestra File History ora inoltra `RangeSelected` e confronta gli estremi con il suo file preselezionato; la preselezione viaggia col load e non più in un campo). Prossima libera: **M117**. Prima: `4384fbc1c` — **M115: la barra della griglia e le sue tendine parlano la lingua dell'app** (pulsanti della seconda riga piatti come toolbar e dialogo di commit — classe `toolbtn`, contorno barattato con l'hover, compromesso 1.4.11 dichiarato; le sei tendine `Flyout` perdono il `Border` interno e diventano una carta sola sul `FlyoutPresenter` — baseline, modern aggiunge angolo e ombra; i comandi dentro le carte prendono la forma di voce di menu con `BarButtonStyles.ApplyMenus`, installata sull'`Application` perché una pop-up root non vede le styles della vista). Prossima libera: **M116**. Prima: `571b87864` — **M114: colonne della griglia ridimensionabili** (presa da 6px sul bordo sinistro di autore/data/commit id — lo stesso insieme dell'originale; subject = colonna che assorbe, pavimenti 40/120px, righe ri-templatizzate solo al rilascio, larghezze persistite in `ViewPrefs.GridColumns` — cosa che l'originale **non** fa). Prossima libera: **M115**. Prima: `10614aa03` — **M113: la storia di un file è una finestra a sé (`Views/FileHistoryWindow`), come `FormFileHistory`** (griglia del file + le quattro schede Commit/Diff/View/Blame, caricamento pigro, nome storico su rename; nuova `FileContentView`, `DiffView.ShowCommit` con preselezione del file; **la scheda "File history" in basso è stata rimossa** e tutti i punti d'ingresso aprono la finestra). Prossima libera: **M114**. Prima: `0cba58207` — **M112: rotella a tre righe per scatto e scorrimento al passaggio del puntatore sui chevron** (`Theming/MenuScrolling`, attached property messa da una style su tendine, menu contestuali e flyout). Prossima libera: **M113**. Prima: `6926e58fb` — **M111: la tendina si ferma al bordo inferiore della finestra e scorre** (`App.MenuMaxHeight` pubblicata da `MainWindow` e letta dalla carta del menu come dynamic resource). Prossima libera: **M112**. Prima: `769b6fdcd` — **M110: i menu a tendina non si aprono più sopra la barra dei menu** (niente FlipY/SlideY sui popup di `MenuItem`, solo ResizeY: la carta si accorcia e scorre). Prossima libera: **M111**. Prima: `7fcf2204e` — **M109: pulsanti azione pieni (niente contorno) nel dialogo di commit + menu con la forma di VS Code** (pillola arrotondata rientrata, separatori a tutta larghezza, carta del popup arrotondata con ombra; tutto solo in Modern). Prossima libera: **M110**. Prima: `5c5d76c7c` — **M108: allineato a `upstream/master` (merge `736a6ed6d`, 11 commit, zero conflitti) + tre fix riportati a mano** (nome del branch del worktree normalizzato con `-b`, offerta di passare al worktree appena creato, avviso "perdi commit" nel reset di un altro branch via `merge-base --is-ancestor`; più `Theming/MenuText` che raddoppia l'underscore negli header dei menu). Prossima libera: **M109**. Prima: `d5f4aca02` — **M107: via le scatole dal dialogo di commit** (contorni dei pannelli solo in Classic via `StyleDensity.PaneOutline`, liste su `App.Panel`, splitter trasparenti, toolbar dei pannelli con i bottoni piatti di `Theming/BarButtonStyles`: 14139 → 1383 pixel di bordo). Prima: `4099dce58` — **M106: tema di sistema + worktree verde, submodule bicolore, barra inferiore colorata** (`UiState.Theme` accetta **System**, che è anche il nuovo default: risolto e inseguito da `App/Theming/SystemTheme.cs` via `IPlatformSettings`/portal XDG, con seme `SystemThemeSeen` per togliere il flash bianco all'avvio e reconcile a 1s; worktree da ciano a verde; glifo submodule bicolore via la nuova tabella `Icons.Parts` disegnata parte per parte da `GlyphSource`; accenti per tutti gli otto tab della striscia inferiore). Prossima libera: **M107**. Prima: `321e09a3f` — **M105: build a zero warning** (34 → 0, risolti non zittiti: `App/Async.cs` per gli `async void`/lambda async e per l'idioma `ContinueWith`+`t.Result`, remoti letti in modo sincrono, join delle traduzioni senza attesa su task, snapshot chiesti alla cache, `MaintenanceDialog` irraggiungibile eliminato; una modifica al sorgente condiviso in `Executable.cs`). Prossima libera: **M106**. Prima: `56772bcc3` — **M104: le ultime nove icone senza glifo** (i quattro del log dell'utente + i sei verdetti GPG e `FunnelPencil` trovati con un audit statico; più il fix della `Source` in `SetSubmoduleNavigation`). Un run headless in stile Modern non stampa **nessuna** riga `[IconLoader]`. Prossima libera: **M105**. Prima: `7545fd235` — **M103: icone moderne colorate per ruolo, con interruttore in Appearance**. Prima: `9a894c461` — **M102: fascia del chevron nell'albero**. Prima: `90e03397d` — **M101: raggruppamento delle liste del dialogo di commit, overflow della toolbar, icona del branch, messaggio dai submodule**. Prossima libera: **M102**. Prima: `77ed2ebdc` — **M100: struttura interna dei pannelli del dialogo di commit**. Prossima libera: **M101**. Prima: `9e3ed0165` — **M99: icone del dialogo di commit**. Prossima libera: **M100**. Prima: `d8c92123a` — **M98: dialogo di commit allineato a FormCommit** (`479355e1c`, `42c9ac1a8`, `d8c92123a`). Prossima libera: **M99**. Prima: `93ce713b0` — **M97: una sola riga selezionata nell'albero + finestra dello stash** (`c19f74ee7`, `93ce713b0`). Prossima libera: **M98**. Prima di M97: `1a8abcd7c` — **M90: doppio clic Submodules usa il target reale e mostra feedback immediato**. Commit M90: `1e9a0bf5b`, `1a8abcd7c`. M89: `32e981301`…`3b995372d`. Prossima libera: **M97** · `d582225f4` (**M96: densità della chrome, solo Modern**) · `35a98fd3d` (**M95: chrome moderna piatta**) · `7fe3726a8` (**M94: icone complete, App.Link, contorni a 3:1, focus non tagliato**) · `5c5a03a64` (**M93: hover della riga + fine del flash bianco**) · `044ce45dc` (**M92: larghezze dei pannelli**) · `363961635` (**M91: submodule nidificati**). |
| Build | `Errori: 0`, **`Avvisi: 0`** su tutti gli otto progetti da M105 (confermato a M108, anche dopo il merge da upstream) (prima erano 34 fra VSTHRD e CS). Harness navigation snapshot: PASS; hierarchy M87/M91: PASS, 7 nodi, inclusi ciclo e linked worktree. **I progetti sotto `Tests/` sono ora dieci** (i cinque banchi di regressione sono `InlineDiffRegression`, `CommandPaletteRegression` con `PASS: 10037 casi`, `ViewPrefsRegression` con `PASS: 41 casi`, `SettingsStoresRegression` con `PASS: 41 casi` e `ImageIntegrityRegression` con `PASS: 124 casi`). **Da M211 non si lanciano più a mano**: `Tests/run-all.sh` costruisce la soluzione e gira i **sette** deterministici in ~20 s (`ALL GREEN` confermato a M213), e la CI li lancia sui path che toccano il port. Fuori dal runner, con la ragione scritta: `AnimProbe` e `ChromeProbe` (vogliono uno schermo), `Perf` (misura, non verdetto). |
| Parità voci UI/funzionali | la **"Coda round 9"** in `PORTING.md` (la misura buona, area per area) è **ESAURITA**: zero voci `[ ]`, zero `[~]`. Resta **un solo** SKIP dichiarato: la colonna build status. **Il repository-host GitHub NON è più uno SKIP: chiuso in M159.** **I 6 scope hotkey NON sono più uno SKIP: chiusi in M158.** **Gli script utente NON sono più uno SKIP: chiusi in M156.** **Le ~35 impostazioni senza consumatore NON sono più uno SKIP: chiuse in M151–M155** |
| Fedeltà UX/visiva | **round 12 commit dialog + merge (M71–M72)** + **round 11 parziali (M67–M70)** + round 1 (T1–T5) + round 2 (M31–M35) + round 3 (M36–M37) + **round 4 rifiniture (M39–M42)** + **round 5 follow-up 1 (M45)** + **round 6 follow-up residui (M46)** + **round 7 feature/GUI (M47–M48)** + M49 fix scroll/selezione grid + **round 8 priorità utente P1–P3 (M50)** + **round 8 pulsanti del pannello inferiore (M51)** |
| Coda aperta | **PRIORITÀ UTENTE del 31/07/2026 — esaurita.** 13.2 (create branch senza process dialog) e 13.3 (doppio clic = checkout col process dialog) **CHIUSE in M75** su tutti e 10 i call-site; **13.1** (`Create branch…` inerte al primo clic) **CHIUSA in M149bis**, dichiarata risolta dall'utente — M75 non l'aveva mai riprodotta e aveva falsificato con prova diretta due ipotesi su tre, e i due difetti reali del flag `_busy` trovati per strada erano comunque stati corretti allora. Sonda diagnostica storica nel branch locale `diag/13.1-probe` (`16bfc40c7`). Dettaglio in `PORTING.md` → M75, "Coda round 13" e M149bis. **La coda del lavoro corrente non sta più qui**: i residui aperti sono elencati in `ROADMAP.md` → §0 |
| Bugfix post-blocco | M43 fetch/pull freeze · M44 `HOME` sbagliato → prompt credenziali a ogni push |
| Packaging | `.deb` self-contained via `packaging/build-deb.sh` |
| Push su remote | **DA PUSHARE al 24-08-2026**: `origin/linux-avalonia-port` è a `104a8c861`, il locale è avanti dei commit di M218–M220 più i docs (`git rev-list --count origin/linux-avalonia-port..HEAD` li conta). Il push lo esegue l'utente, mai il loop: verificare sempre con quel `rev-list --count`, che deve tornare a 0. Portachiavi: se vuoto, il primo push chiede le credenziali **una volta** (username `daryda9` + PAT), poi `git credential approve` le salva in libsecret. **CI** (`.github/workflows/crossplatform-build.yml`): il run su `104a8c861` è **verde** (verificato il 24-08: build + 7 harness sul runner ospitato; l'unico rosso resta il primissimo, pre-M215). Lo **stato** dei run si legge da anonimo (`curl https://api.github.com/repos/daryda9/gitextensions/actions/runs?branch=linux-avalonia-port`, e `…/runs/<id>/jobs` per i passi); sono i **log grezzi** a volere un token (`403`), e `gh` non è installato |

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
- **Documentazione verso fuori**: `README.md` non è più la nota di una fetta verticale — la prima metà
  racconta cosa cambia rispetto all'originale, con **dodici schermate** di questa build in
  `docs/screenshots/` (finestra principale, schede di repository, colori per checkout, icone Modern
  colorate/monocrome/Classic, palette, dialogo di commit, editor di merge, confronto affiancato,
  confronto immagini, barra del sequencer, editor della todo, storia file con blame, impostazioni,
  terminale). Ogni voce di quella metà è stata verificata contro `src/app` (M217). Le schermate si
  rifanno con un repo usa-e-getta sotto `/tmp`, Xvfb e XTEST: identità finta e path neutri, perché
  finiscono in un README pubblico.

---

## 3. Convenzioni e trappole (LEGGERE PRIMA DI TOCCARE)

### Vincoli di processo
- **NON pushare.** **NON firmare i commit**: `git -c commit.gpgsign=false commit …`.
- Conventional Commits. Nessun trailer/co-author.
- **NON** fare refactor multi-target, **NON** toccare la build Windows: lavorare solo
  in `src/crossplatform/`.
- Ogni iterazione aggiorna `PORTING.md`: spunta le voci, registra la milestone (prossima
  libera: **M77**), tiene il contatore iterazione.

### ⚠️ L'ambiente NON è sempre Linux — verificarlo PRIMA di pianificare (misurato in M75)
Il round 13 è girato su **Windows 11 ARM64** (Git Bash MSYS + PowerShell), non sulla macchina
Linux dei round precedenti. Conseguenze, tutte misurate:
- **Niente Xvfb, ImageMagick, python-Xlib**; la WSL Ubuntu presente **non ha SDK .NET**. Tutta
  l'attrezzatura headless descritta più avanti in questo file **non è applicabile**: non tentarla,
  concordare con l'utente una verifica alternativa.
- **La build funziona su Windows**: `dotnet build App/GitExtensions.Avalonia.csproj -v q` da
  `src/crossplatform` (SDK 10.0.302 in PATH) → `Errori: 0`, 31 warning pre-esistenti.
- **Se l'app è in esecuzione la build fallisce** con `MSB3027`/`MSB3021` "file bloccato da
  GitExtensions.Avalonia": chiudere l'istanza (`taskkill //PID <pid> //F`) — non è un errore di
  compilazione.
- **Le worktree falliscono con "Filename too long"** (i file di test upstream sfondano MAX_PATH):
  serve `git config --global core.longpaths true`, già impostato su questa macchina.
- **Chiedere all'utente su quale piattaforma ha visto il bug**: in M75 il difetto era su Windows e
  questo ha eliminato in partenza l'ipotesi principale (grab X11), che era in cima alla coda.

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

> ### ►► STATO AL 24-08-2026 — nessuna coda utente aperta
> Le tre voci della priorità del 31/07/2026 sono **chiuse** (13.2 e 13.3 in M75, 13.1 dichiarata
> risolta dall'utente in M149bis). Il blocco storico resta qui sotto perché la diagnosi è riusabile,
> non perché sia da fare.
>
> **Aperto adesso, in ordine di costo:**
> 1. ~~Guardare il risultato della CI su `104a8c861`~~ — **fatto il 24-08: verde** (vedi §1; lo stato
>    dei run si legge da anonimo, sono i log grezzi a volere un token).
> 2. **Residui misurati e scritti**, tutti in `ROADMAP.md` → «Residui ancora aperti»: tema **System**
>    mai esercitato contro un portal vero, coda RTL delle schede, rifiuto oltre 16 megapixel non
>    esercitato a schermo, pan/zoom assenti nel confronto di immagini, rerere con cache multi-variante
>    o path non ASCII. ~~Selettore di commit cablato su uno dei quattro campi~~ — **chiuso in
>    M218–M220** (archivio, «Compare to commit…», dialogo del cherry-pick).
> 3. **Due voci grosse di roadmap, mai iniziate** — solo su richiesta, e sono **inedite** rispetto
>    all'originale: **undo timeline sul reflog** (il reflog *è* la timeline, manca la lettura e il
>    raggruppamento; la finestra `ReflogWindow` esiste ed è un elenco crudo — taglia M) e **rebase
>    interattivo per trascinamento** (`RebaseTodoWindow` c'è con su/giù/rimuovi: manca il
>    trascinamento, e manca comporre la todo **prima** di partire — taglia L).
> 4. **Due osservazioni di questo giro, non diagnosticate**: (a) dopo `merge --abort` seguito da un
>    rebase fermo, la barra continuava a dire «Merge is currently in progress» finché non si riavviava
>    l'app — non è confermato che l'F5 fosse arrivato alla finestra, quindi va riprodotto prima di
>    chiamarlo difetto; (b) la finestra di confronto immagini **non si apre** quando l'app gira con un
>    `HOME` finto (nessun errore nel log), mentre con l'`HOME` vero funziona.
> 5. **Fastidio noto, due volte su due**: lanciare l'app dal tree di build **sporca un file tracciato**
>    — `GitExtensions.Avalonia/GitExtensions.settings` si prende i repository recenti e le preferenze
>    di blame. Va ripristinato a mano prima di ogni commit (`git show HEAD:<path> > <path>`). Da
>    decidere: `.gitignore` o spostare quel file fuori dai sorgenti.
>
> **Fuori perimetro, verificato il 2026-08-18: smartphone.** Non è una ricompilazione. Il motore è un
> processo (`GitCommands/Git/Executable.cs` → `Process.Start`; 138 riferimenti nel core, 116 punti di
> chiamata nel port, 20 file del port che avviano processi): su iOS `fork`/`exec` sono **proibiti**, su
> Android non c'è `git` e l'esecuzione di binari da directory scrivibili è bloccata da API 29. E la UI
> è da scrivania: **47** classi `Window`, **101** `ShowDialog`, `Avalonia.Desktop`, codice X11 in 8
> file, più il pty con dieci P/Invoke a `libc`. Sopravviverebbero i pezzi puri (`InlineDiff`,
> `ImageIntegrity`, `JsonSettingsFile`, i parser). Una versione mobile è **un'app diversa** che
> condivide i parser, con lo strato di esecuzione riscritto su libgit2.

> ### ►► PRIORITÀ UTENTE del 31/07/2026 — **CHIUSA** (M75 + M149bis): create branch e checkout dall'albero
> Tre difetti segnalati usando la GUI. **Vengono prima di tutto il resto.** Lista operativa completa,
> con `file:riga` verificati al `22dfc4d1b`, ipotesi di causa e criteri di accettazione:
> `PORTING.md` → **"Coda round 13 — PRIORITÀ UTENTE del 31/07/2026"**.
> 1. **`Create branch…` dal menu contestuale di un branch (albero sinistro) è inerte al primo clic**,
>    funziona al secondo (`RepoObjectsTree.cs:1253`/`:1301` → `DoCreateBranchAsync` `:2004` →
>    `ShowDialog`). Da riprodurre e **misurare**: l'ipotesi principale è il modale aperto mentre il
>    popup del `ContextMenu` è ancora vivo (grab X11 → il WM non mappa la finestra), le altre sono un
>    `Refresh()` che smonta il nodo col menu aperto e la guardia `_busy`. Se è la prima, il rimedio
>    vale per **tutti** gli item del menu dell'albero che aprono un modale.
> 2. **La creazione di un branch non passa dal process dialog** (upstream sì:
>    `FormCreateBranch.cs:163`). Nel port gira in `RunMutation`, che **sul fallimento non fa nulla**.
> 3. **Il doppio clic su un branch deve fare il checkout mostrando il process dialog** (upstream
>    `FormCheckoutBranch.cs:357`). Il cablaggio c'è già (`RepoObjectsTree.cs:244` → `OnActivate`
>    `:1736` → `DoCheckoutAsync` `:1839`) e su tree pulito il dialogo "local changes" viene saltato di
>    proposito: quello che manca è il **feedback**. Verificare prima se il checkout avviene davvero.
>
> **Causa comune a 2 e 3, accertata**: `RepoObjectsTree.RunMutation` (`:2410-2440`) è fire-and-forget
> e **muto** — su `!success` nessun messaggio, nessun refresh; `MainWindow.RunOp` (`:3326`) si ferma a
> una riga di status bar. L'infrastruttura giusta esiste già
> (`GitProcessDialog.RunStreamingAsync:334`, usata per push/merge/commit): manca l'instradamento, su
> **tutti** i call-site — 5 per create branch, 5 per il checkout, tabellati in `PORTING.md`.

> ### ► **M96** (2026-08-05) — **densità della chrome, solo in Modern**
> Richiesta: applicare le raccomandazioni del punto 2 della coda, **solo nello stile Modern**.
> **Il vincolo cambia la forma del lavoro**: sostituire i letterali con token di `Metrics` NON
> rende nulla dipendente dallo stile (un valore sul call-site è un local value e batte ogni
> `Style`); la proprietà va TOLTA dal call-site e assegnata dal blocco che `ModernStyles`
> installa e rimuove in blocco. Il "classico" è ciò che danno i `ControlTheme` di Fluent.
> **Entrato**: padding 12,4 (pulsanti) e 8,4 (input), `MinHeight` 28 (Fluent: 32, che è un
> bersaglio da dito), raggio **4 su tutta la chrome** (i pulsanti erano l'unico 6: a 28px legge
> come pastiglia e in barra apre un cuneo fra vicini), tab header 12,4, riga della griglia **22**,
> pulsanti di barra 4,4 / 8,4, icone 16 con **una** costante (i 42 `, 16)` erano il default del
> parametro stesso).
> **Due cose che nessuno `Style` raggiunge**: l'altezza di riga (la griglia disegna le proprie
> righe → `RebindRows(preserveViewport: true)` su `StyleChanged`, `Post`-ato perché l'evento è
> alzato *dentro* l'installazione del blocco) e i pulsanti di barra (helper che assegnano local
> value → nuovo `Theming/StyleDensity.cs`; `MainToolbar` ricostruisce la striscia, gli altri 5
> call-site prendono il valore alla costruzione successiva — limite dichiarato).
> **Verificato live in entrambe le direzioni** con clic sintetici sulla pagina Appearance: passo
> righe 22 → 20 → 22, nessun rebind fallito. Classic invariato alla cifra.
> **Falso allarme corretto**: i «126 `FontSize` letterali» sono 83 × 12 (= baseline), 21 × 11 e
> 6 × 13 (sulla scala), e i 10 × 10 **non sono testo** — sono i chevron `▾` e il marker `▶`.
> Rinominarli con i token è un refactor a zero pixel. Le ~100 `Thickness` fuori griglia rimaste
> sono margini di pannelli: cambiarle muoverebbe anche Classic. Dettaglio in `PORTING.md` → M96.

> ### ► **M95** (2026-08-05) — **chrome moderna piatta**: la toolbar non è più un'altra tinta
> Segnalazione utente: nel dark la toolbar è di un colore diverso dal resto. Misurato sullo
> screenshot: barra dei menu **a due tonalità** (`#1C1D21` fino a x≈748, `#2F3038` a destra — il
> controllo `Menu` dipinge il proprio fondo sopra il contenitore, dove finisce riappare il colore
> del contenitore) e striscia toolbar `#2F3038` contro `#1C1D21` di ogni pannello.
> **Fix**: nelle sole famiglie Modern `App.Toolbar` = `App.Panel`. Un valore, e si appiattiscono
> tutte le 15 barre che leggono quella chiave: cambiare solo `MainMenu`/`MainToolbar` avrebbe reso
> *quelle* le nuove diverse. Precedente già nel file: `App.Control` **è** `App.Panel` dal M77 — una
> superficie si può fondere col fondo se il contorno la delimita, e dal M94 quel contorno misura 3:1.
> Separazione chrome/contenuto = la regola da 1px già in fondo a `MainToolbar`.
> Nessun contrasto decade: App.Toolbar era la superficie **più chiara**, togliendola i minimi
> salgono (`App.TextDim` 4.70 → 5.75:1 dark, 4.67 → 5.29:1 light). Conseguenza da sapere:
> `App.PanelAlt` è ora la più chiara della rampa, quindi le strisce della griglia sono più chiare
> delle barre. **Classic intatta di proposito** (`#333337` è la firma del 2015), verificato a schermo
> su Modern Dark / Modern Light / Classic Dark. Dettaglio in `PORTING.md` → M95.

> ### ► **M94** (2026-08-05) — **coda di modernizzazione: icone, link, contorni, tab, focus**
> Chiusi gli step 4/3/5/7/6 con quattro subagent in worktree + il loop.
> **Icone**: 23 glifi nuovi, i nomi senza glifo passano da **18 a 1** (`GitForWindows`, marchio,
> lasciato di proposito); ogni path ricamminato aritmeticamente dentro il box 0..24.
> **Link**: `App.Link` da **0 a 10** call site; `App.Accent` mancava il 4,5:1 in 9 delle 16
> combinazioni superficie x famiglia, `App.Link` le passa tutte. La colonna Commit ID è stata
> verificata **non cliccabile** e lasciata.
> **Contorni**: `App.Border` misura 1,08:1 e non può delimitare un controllo (1.4.11 chiede 3:1).
> Alzati su **tre** livelli — chiavi Fluent, nuova chiave di palette `App.BorderStrong`, default di
> `TextBoxSurface` — perché ognuno batte il precedente. Nelle famiglie Classic `App.BorderStrong` **è**
> `App.Border`, così il classico non si muove (verificato a schermo).
> **Tab**: stessa causa di M93; una terza proprietà l'aveva senza che nessuno lo sapesse
> (`PART_SelectedBar`: la barra d'accento lampeggiava bianca a ogni cambio di tab). Rampa vecchia con
> picco 4,6x l'estremo più chiaro, nuova monotona.
> **pressed/focus** fotografati per la prima volta: pressed `#53545B`, anello di focus 2px accento +
> alone 1px. **Difetto trovato qui**: l'anello stava *fuori* dal controllo con un margine negativo ed
> era **tagliato** dal contenitore — un pulsante di toolbar mostrava solo i lati. Ora è dentro i limiti.
> **Da NON riscoprire**: chiave Fluent < valore locale < risorsa pinnata sull'istanza — alzare solo la
> prima non si vede; un nome di icona di upstream può essere semanticamente sbagliato per il suo call
> site e accorgersene solo quando il PNG "giusto per caso" diventa un glifo (`Preview` → occhio dove
> serviva una lente). Dettaglio in `PORTING.md` → M94.

> ### ► **M93** (2026-08-04) — **la riga sotto il puntatore si vede**, e il flash bianco sparisce
> Due segnalazioni con screenshot. (1) L'hover della griglia dipingeva `App.PanelAlt`, che **è** il
> fondo delle righe dispari: invisibile. Tre chiavi nuove in tutte e quattro le famiglie (34 → 37):
> `App.HoverRow` (l'unico fondo di riga con una **tinta**, `App.Panel` verso `#38BDF8`), `App.Hover`,
> `App.Pressed`. Percentuali fissate da `App.TextDim` ≥ 4,5:1, non dal testo pieno.
> (2) Il flash bianco era **`Brushes.Transparent`**, che è `#00FFFFFF` — bianco con alpha 0 — usato
> come valore di riposo di una proprietà **animata** (`ModernStyles.PresenterTransitions`): ogni hover
> interpolava attraverso bianco semi-opaco (misurato: picco `#78787D` su una toolbar `#2F3038`).
> Riposo ora = colore di hover ad alpha 0, e i `toolbtn` escono dal cross-fade; `MenuFlyoutItemBackground`
> passa a `panel`. Hover/pressed della toolbar lasciano `App.PanelAlt`/`App.Panel`, che erano più
> **scuri** della barra.
> **Da NON riscoprire**: `Brushes.Transparent` come punto di partenza di un'animazione = flash chiaro
> su fondo scuro; il valore giusto è *il colore d'arrivo ad alpha 0*. Resta esposto solo il `TabItem`.
> Misurato in tutte e quattro le combinazioni tema × stile. Dettaglio in `PORTING.md` → M93.

> ### ► **M92** (2026-08-03) — **Diff e Stash seguono la larghezza a cui li trascini**
> Segnalazione dell'utente con screenshot. La larghezza iniziale stava sul **figlio** dentro colonne
> `Auto` (`_files.Width = 320`, `listPanel.Width = 340`, `_filesGrid.Width = 320`): il `GridSplitter`
> ridimensiona la **colonna**, il figlio restava alla sua misura e fra il suo bordo e lo splitter si
> apriva una **striscia morta**. Ora la larghezza sta nelle `ColumnDefinition`, con `MinWidth = 120`.
> **Da NON riscoprire**: dentro un `Grid` con splitter la misura appartiene alla **colonna**, mai al
> figlio — `FileTreeView` (`"300,Auto,*"`) era l'unico dei tredici a farlo giusto e l'unico senza il
> difetto. Misurato: colonna dei file di Stash 320 → 401 px, contigua allo splitter, zero pixel morti.
> Dettaglio in `PORTING.md` → M92.

> ### ► **M91** (2026-08-03) — **submodule dei submodule** nell'albero, e il doppio clic che li apre
> Richiesta dell'utente con screenshot dell'originale a confronto. La categoria Submodules era una
> lista **piatta** (`GetSubmodulesLocalPaths(recursive: false)`, `git submodule status` senza
> `--recursive`): un submodule di un submodule non c'era. Ora è una **gerarchia** come
> `SubmoduleTree` di upstream — ogni riga sotto il proprio super-project, un **nodo cartella** per il
> segmento di path che è solo una directory (`core`, `graphs`), etichetta **nome + branch**, path e
> sha nel tooltip. **Doppio clic** apre il submodule come repository attivo (upstream:
> `SubmoduleNode.OnDoubleClick`), tranne quelli non inizializzati.
> **Da NON riscoprire**: (a) `git submodule update -- <path>` accetta **solo** un submodule del repo
> in cui gira, quindi un nidificato va aggiornato dal **suo** super-project — `SubmoduleRow.ParentPath`
> / `PathInParent` esistono per questo, provato con `exit 1` dal top e `exit 0` dal padre; (b) il nodo
> ospite di una riga è la **dirname del path completo**, non il nodo del super-project: appenderlo al
> secondo fa sparire le cartelle intermedie (sbagliato al primo tentativo, visto a schermo);
> (c) il branch si legge dal file `HEAD` risolvendo `gitdir:`, non con un `git` per submodule.
> Dettaglio in `PORTING.md` → M91.

> ### ► ROUND 13 — iterazione 2: **M80** (2026-08-03) — **lo stile è una scelta**
> Richiesta dell'utente subito dopo M79 (*«dammi la possibilità di scegliere dalle impostazioni se
> mantenere il vecchio stile/icone o quello nuovo»*), che **ribalta** la decisione di M79 di sostituire
> l'aspetto senza affiancare una variante classica. Combo **Style** (Modern/Classic) accanto a Theme
> nella pagina Appearance, voci gemelle nel menu View, `Style` in `ui-state.json`. **Cambio a caldo**
> come il tema, non al riavvio.
> Sotto: palette a **quattro** famiglie (34 chiavi ciascuna, valori classici verbatim dal `a38eb4ab4`);
> `GlyphIcon` che conserva il nome e disegna glifo o PNG secondo lo stile; `ModernStyles` reversibile,
> che **rimuove** la chiave Fluent quando prima non c'era invece di indovinarne il valore.
> Verificato a schermo: cambio a caldo senza riavvio, quattro combinazioni, Classic **byte-identico**
> al pre-M79 (bianco puro incluso), persistenza all'avvio. Dettaglio in `PORTING.md` → "ROUND 13 —
> iterazione 2".
> **Da NON riscoprire**: (a) con due dimensioni ortogonali, **nessun call site deve passare un
> letterale per la dimensione che l'utente non ha toccato** — si passa sempre la coppia, letta fresca;
> (b) `StyleChanged` è `static`, quindi la **disiscrizione** in `OnDetachedFromVisualTree` conta più
> della feature: la griglia ricicla i container di continuo; (c) fissare il **contratto di API prima
> di delegare** è ciò che ha reso le due unità parallele invece che sequenziali — quella della UI ha
> chiuso con due errori di compilazione previsti, spariti al cherry-pick dell'altra.

> ### ► ROUND 13 — **GUI moderna**, iterazione 1: **M79** (2026-08-02)
> Direzione dell'utente: struttura e funzioni **invariate**, superficie modernizzata. Deciso anche che
> **non** si affianca una variante "Classic": l'aspetto si sostituisce. Tre subagent in worktree
> isolati, file disgiunti, nessuno dentro `App/Views/`. Dettaglio completo in `PORTING.md` → "ROUND 13".
> - **Icone**: 90 glifi vettoriali monocromatici (Lucide, ISC, path inline) tinti dalle *istanze* dei
>   brush di palette. **API di `IconLoader` invariata** → zero call site toccate dall'unità; i nomi
>   senza glifo cadono sul PNG e si loggano, quindi la copertura è misurabile da un run. Marchi di
>   terzi e famiglia `FileStatus*` restano raster **con motivo**.
> - **Token e stati**: `Metrics` (spazi 4/8/12/16/24, 5 livelli di testo, raggi, durate) + stati e
>   transizioni ottenuti **ridefinendo le chiavi risorsa di Fluent**, non combattendole. Nessun
>   esadecimale: i colori di stato derivano dai brush di palette per riferimento. Griglia esclusa per
>   costruzione. Le view **non** usano ancora `Metrics`: è il lavoro successivo.
> - **Palette**: rampa fredda, via bianco e nero puri, accento contemporaneo, `App.Link` (residuo M74).
>   Tutte le famiglie di inchiostro ri-derivate, tinte di diff **ricomposte** sull'alpha `0x28`.
> - **Due correzioni del loop**: cinque icone di toolbar tornavano raster perché riassegnavano
>   `Image.Source` con un `Bitmap` (nuovo `IconLoader.Retarget`); e la riga selezionata della griglia
>   è scesa a 3,68:1 col nuovo accento → nuova chiave **`App.AccentFill`**, misurata poi a 6,85:1.
> **Da NON riscoprire**: (a) un colore ha **ruoli**, e fondo e inchiostro non si servono con la stessa
> tinta — è già successo con le ref pill in M67 e con `App.Link` in M74; una campagna di misure sul
> solo `ThemeManager` **non vede** gli usi derivati dentro le view. (b) Assegnare `Image.Source` è un
> modo silenzioso di annullare un'icona vettoriale. (c) **Il watchdog uccide un subagent dopo 600 s
> senza progresso e in questa iterazione ha preso tutti e tre, sempre sulla verifica GUI**: riprenderli
> con `SendMessage` (worktree e transcript intatti, mai rilanciarli da zero), imporre *un file scritto
> = un commit*, e tenere la verifica GUI nel loop chiedendo ai subagent misure **calcolate offline**.

> ### ► M78 (2026-08-02) — **le linee del grafo non si spezzano più sui merge**
> Seconda segnalazione dell'utente sullo stesso pannello («a volte le linee risultano spezzate»),
> diagnosticata **misurando i pixel** del suo screenshot: la lane si interrompeva al **centro** della
> riga del merge. In `BuildGraph` i parent extra di un merge finivano **tutti** in `nodeOrigin`, anche
> quando la lane **portava già** quel parent: la metà inferiore veniva ri-sorgentata dal nodo, la metà
> superiore restava un vicolo cieco e il ramo appariva spezzato in due (col frammento sotto che
> prendeva il colore del nodo). Ora quella lane **continua diritta** e l'arco di merge è una diagonale
> **in più** verso di essa (`joinEdges`), nel colore del ramo mergiato. La lane nuova è invariata.
> Frequente su storie con branch di release paralleli. Dettaglio, misure e residuo cosmetico (1 px di
> antialiasing dove i due mezzi segmenti si toccano) in `PORTING.md` → "M78".

> ### ► M77 (2026-08-02) — **il grafo non unisce più branch che non lo sono**
> Segnalazione dell'utente («a volte visualizza come uniti branch che non lo sono, quando mi sposto su
> un branch»), riprodotta. Le righe artificiali non passavano dal layout del DAG: la riga verso HEAD
> era dipinta sopra da `WithHeadConnector`, che forzava un segmento nella lane di HEAD su *ogni* riga
> sopra HEAD. Con HEAD non in cima — cioè dopo il checkout di qualunque branch che in ordine di data
> sta sotto a un altro — quel tratto attraversava lane libere o **già occupate da rami scorrelati**, e
> i due si leggevano come una linea sola. Difetto indipendente che ci si sommava: `ColorLane` era
> l'**indice di lane**, e una lane liberata da una convergenza viene riassegnata più in basso a un ramo
> che non c'entra nulla, che riceveva lo stesso colore nella stessa colonna.
> **Fix**: `RevisionRow.GraphParents` (parenti solo per il layout, `ParentHashes` resta vuoto così la
> navigazione non entra nei nodi artificiali), `BuildDisplayRows` rilancia `BuildRevisionGraph`
> sull'insieme mostrato, e `BuildGraph` traccia un'**identità di arco** parallela alle lane
> (`RevisionRow.NodeColor` + `ColorLane`). Rimossi `WithHeadConnector`, `ArtificialSegments`,
> `_artificialLane`, `_headDisplayIndex`. Dettaglio e fixture in `PORTING.md` → "M77".
> **Da NON riscoprire**: la lane è solo una colonna, viene **riciclata**; qualsiasi cosa la usi come
> identità (colore, continuità visiva) è sbagliata. E niente si disegna nel grafo senza passare dal
> layout: se un arco non è nel DAG, il DAG darà la sua colonna a qualcun altro.

> ### ► M76 (2026-08-01) — **`Keep dialog open` persistito + `Delete branch` riparato**
> Riscontro dell'utente sulla build di M75: il flag `Keep dialog open` non ricordava la scelta (un
> `IsChecked = true` nel costruttore) ed è ora un flag **globale** come upstream, persistito in
> `view-prefs.json`; `Delete branch` era muto e cancellava con `force: false` (difetto **preesistente**,
> non una regressione di M75). Dettaglio in `PORTING.md` → "M76".

> ### ► M75 (2026-08-01) — **le mutazioni di ref passano dal process dialog** (13.2 e 13.3 chiuse)
> `BranchTagService.*Streaming` + `App/Views/RefProcessRunner.cs` su tutti e 10 i call-site di create
> branch e checkout; refresh e guardie `_busy` restano al call-site. Dettaglio in `PORTING.md` → "M75".

> ### ► M74 (2026-07-30) — **pannello illustrativo del merge**
> Ultima differenza visibile fra il `MergeDialog` del port e lo screenshot dell'originale: chiusa.
> `App/Views/HelpImagePanel.cs` (riusabile) + le **7** PNG di `src/app/GitUI/Resources/Help/` linkate
> come `AvaloniaResource` sotto `Assets/Help/` (quindi Pull e Rebase potranno usarlo: servono solo lo
> spec e la larghezza, il cablaggio no). Link `Hide help` / pulsante `Show help`, stato persistito in
> `ViewPrefs.HelpPanels` (**non** in `UiState`, che l'host riserializza alla chiusura), swap su hover
> verso l'immagine fast-forward con la condizione di upstream.
> **Il tema scuro è stato risolto misurando**: `AdaptLightness` di upstream non esiste nel port, ed è
> stato reimplementato in `HelpImagePanel` come remap della lightness percepita su
> `[L(App.Text), L(App.Window)]` conservando tinta e saturazione, applicato **solo** in tema scuro. Il
> lastrone bianco passa da **16,67:1 a 1,00:1** contro il pannello e il testo dentro l'immagine resta
> ≥ **4,57:1**. Il passo per-tinta **non è decorativo**: con una semplice inversione HSL l'etichetta
> bianca sul nodo blu crollava a **1,89:1** (il blu puro sta a L=0,50, quindi invertire scurisce il
> nodo *e* la sua lettera). Il tema chiaro è lasciato **intatto** con misura: trasformarlo avrebbe
> abbassato le etichette bianche sui nodi da 8,59–10,95 a 4,32–6,33.
> **Residuo nuovo, segnalato dall'unità e non risolvibile da lei**: il link `Hide help` usa
> `App.Accent` (#007ACC) come tutti i link del port (stessa convenzione di
> `ResolveConflictsDialog.cs:290`) e misura **3,70:1 in scuro / 4,06:1 in chiaro**, sotto AA 4,5:1.
> Serve una chiave **`App.Link`** nuova in `ThemeManager` (`Keys`+`Dark`+`Light`) e la sostituzione nei
> call-site: è un difetto **pre-esistente e diffuso**, non introdotto qui.
> Non fatti: `AppSettings.DontShowHelpImages` (nel port non esiste nulla da leggere) e il ricolore al
> cambio tema **con il modale già aperto** (la correzione è calcolata all'apertura e `ThemeManager` non
> espone un evento di cambio).

> ### ► M73 (2026-07-30) — **superficie del rebase**
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
> **Residui registrati, nessuno bloccante** (lista completa, allineata a M73):
> - lo stato "conflitti senza operazione in corso" (dopo uno `stash pop` conflittuale) non è nel
>   banner: rilevarlo costerebbe un `git diff` a ogni refresh anche su repo inerti — servirebbe una
>   cache dello stato dell'indice;
> - **cherry-pick e revert** non hanno un service dietro `--continue`, quindi nel banner restano col
>   solo suggerimento testuale. Il rebase invece **ce l'ha da M73**, e `git am` ha già la sua
>   macchina a stati in `ApplyPatchDialog` (M68);
> - l'**editing del todo interattivo** (`git rebase --edit-todo`, la griglia per riordinare i commit)
>   non è portato: servirebbe una griglia del todo più uno shim `GIT_SEQUENCE_EDITOR` puntato al port.
>   Non è promesso da nessun controllo nella UI;
> - i **due stati della barra non sono coerenti fra loro**: il merge **scambia** i pulsanti per
>   visibilità (M72), il rebase li **spegne** restando in riga (M73, per non far ballare quattro
>   pulsanti sotto il puntatore). Va uniformato, in un verso o nell'altro;
> - la scelta fast-forward del `MergeDialog` è ricordata **globalmente** e non per repository
>   (`GetEffectiveSettings().Detached()` è uno store condiviso);
> - `AvaloniaGitUICommands.StartResolveConflictsDialog` resta `NotSupported`: firma sincrona `bool`
>   con `IWin32Window?` e nessun riferimento a una `Window` — è una decisione semantica, non un gancio;
> - `DontConfirmResolveConflicts` è un flag **senza UI**, perché il port non ha la pagina
>   Confirmations (nessuna delle 17 checkbox di upstream è portata);
> - nel dialogo dei conflitti mancano **"Open/Save `<side>` as"** (servirebbe un checkout su file
>   temporaneo più un sostituto Linux di `OpenAs_RunDLL`) e la **file history** (serve instradamento
>   dall'host);
> - la persistenza di **sort-key e toggle untracked** delle nuove toolbar del commit dialog
>   richiederebbe campi in `AppPreferences`, che è condiviso;
> - i commenti ora stantii in `ApplyPatchDialog.cs:51` e `PullDialog.cs:718`, che dicono ancora che il
>   port non ha `FormResolveConflicts`.

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
> **P2 — chrome: CHIUSA. Riverificata il 2026-08-10** — la nota «restano 2c e 2d» era
> invecchiata: M115-M121 hanno rifatto tutte e tre le strisce che nominava.
> 2a+2b erano già fatte (barra pulsanti + casella di ricerca sopra l'albero, icone nei tab
> del pannello inferiore). **2c**: `ToolStripMain` è stata rifatta in M119 (le sei scorciatoie
> di `InsertFetchPullShortcuts`, di cui le ultime due aggiunte in M138) e appiattita in
> M115/M121; lo split-button del Pull è di M50/P3. La lista dei file ha oggi la sua casella
> di filtro e il raggruppamento pieghevole (M117 per l'intestazione di confronto, e il flyout
> group-by). **2d**: le opzioni del viewer diff ci sono — ignore-whitespace nelle tre varianti,
> righe di contesto +/-/intero file, evidenziazione della sintassi, caratteri non stampabili,
> «vai alla riga», «prossima modifica».
> Il residuo che questa riverifica aveva isolato — la ricerca `git grep` dalla lista dei
> file — è **CHIUSO in M148**: nuovo `Services/GitGrepService` sul `GetGrepFilesStatus` del
> core, casella inline, Match case / Match whole word persistiti in `view-prefs.json`,
> risultati come sezione in più. Restano fuori, con motivo, il dialogo separato
> `FormFindInCommitFilesGitGrep` e gli argomenti liberi `GitGrepUserArguments`.
> **P2 non ha più nulla di aperto.**
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
   (round 5)". **Due dei code smell registrati qui non esistono più — riverificati il
   2026-08-10 con prova a schermo**: le liste sono `SelectionMode.Multiple` e il discard
   agisce sulla selezione («Discard changes (2 files)»), e la guardia "Nothing staged"
   esclude già un merge commit legittimo tramite `MERGE_HEAD` (`_mergeInProgress`, letto
   dalla git-dir *risolta*, quindi valido anche nei worktree). Restano: niente drag&drop fra
   le liste (assente anche nell'originale) e acceleratori Enter/Space/Ctrl+Enter non
   replicati.
2. **Traduzioni** — **infrastruttura FATTA in M46/T1**: `.xlf` copiati in output e nel
   `.deb` (66 file), `App/Services/TranslationService.cs` (riusa il loader XLIFF del core,
   sostituisce il matcher WinForms con lookup per id **e** per `<source>` inglese
   normalizzato), selettore **View → Language** persistito in `UiState.Language`, cambio
   lingua senza riavvio. **CHIUSA in M139-M141** (2026-08-10): il layer copre tutte le view,
   ~290 stringhe in 21 file, con ricostruzione su `LanguageChanged`. Restano in inglese solo
   le stringhe che nessuna trans-unit copre — elencate nelle milestone, senza inventare id.
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

### Fuori scope (SKIP consapevoli)
- ~~**Repository hosts (GitHub)**~~: **chiuso in M159** — fork / create-view PR / add upstream /
  link "View in GitHub", come servizio contro il REST v3 invece che come plugin.
- **Colonna build status**: richiede integrazione con un build-server/CI. **È l'ultima voce.**

### Sviluppi futuri **su richiesta** — feature INEDITE (non esistono in upstream)

Roba che il port potrebbe fare e che **l'originale Windows non fa**. Non sono lacune di parità:
sono aggiunte. Nessuna va iniziata di propria iniziativa — si fanno **quando l'utente le chiede**, e
il fatto che siano inedite va detto quando si consegnano, perché non c'è un comportamento upstream a
cui appellarsi se la scelta di design risulta sbagliata.

- **Fondere il submodule dal superprogetto** *(proposta nata da M165, 11/08/2026)*. Oggi un conflitto
  di puntatore si risolve **scegliendo una delle due parti** (M165), che è tutto ciò che fa anche
  l'originale. Manca la terza risposta: *«non voglio né X né Y, voglio X mergiato con Y»*. Nell'originale
  si esce dall'app, si apre il submodule come repository a sé, si fa il merge lì dentro e si torna a
  fare `git add sub`.
  Forma minima proposta: dal dialogo dei conflitti, **«apri questo submodule in una scheda»** — il port
  ha già le schede multi-repository (M131/M145) e il submodule è già navigabile dall'albero, quindi il
  pezzo mancante è solo il ponte dal conflitto alla scheda. Forma piena: dentro quella scheda, un
  invito a mergiare i due commit in conflitto (che il superprogetto conosce: stage 2 e stage 3), e al
  ritorno registrare il risultato con lo stesso `update-index --cacheinfo` di M165.
  **Da valutare prima di iniziare**: cosa succede se il merge dentro il submodule a sua volta va in
  conflitto (annidamento del dialogo), e cosa registrare se l'utente chiude la scheda a metà. Nessuna
  delle due ha una risposta upstream da copiare.

---

## 5. Prompt pronto per riprendere

**Stato al 24-08-2026 (sera).** `HEAD` locale avanti di M218–M220 + docs su `origin` (che è a
`104a8c861`): **il push spetta all'utente**. Nessuna coda utente aperta. Prossima milestone libera:
**M221**. Ultimo giro: CI su `104a8c861` **verificata verde** (fix M215 confermato), e il selettore
di commit portato agli altri tre campi — i due dell'archivio (M218), «Compare to commit…» (M219) e
il **dialogo del cherry-pick** (M220, port di `FormCherryPick`: prima un merge commit non si poteva
prendere).

Prima di decidere qualsiasi cosa, leggere **§3** (convenzioni e trappole) e **§4** (cosa resta), in
quest'ordine. Le regole che non cambiano: si lavora **solo** in `src/crossplatform/`; **niente push**
(lo fa l'utente); commit **senza firma e senza trailer**; mai `git checkout`/`switch`/`reset` nel repo
principale; una differenza dall'originale si dichiara **solo** dopo averla cercata in `src/app` (M217:
nove su diciotto erano sbagliate); una build si guarda, **non** si tronca con `| tail` (M216: ho
misurato un dll vecchio per un giro intero).

Le due cose che un giro nuovo può prendere, in ordine di costo:

1. **Un residuo misurato** dalla lista di `ROADMAP.md` → «Residui ancora aperti»: tema System contro
   un portal vero, coda RTL delle schede, 16 megapixel a schermo, pan/zoom nel confronto immagini,
   rerere multi-variante o path non ASCII. Nessuno è cablatura pura: ognuno chiede prima una misura.
2. **Una delle due voci grosse, INEDITE, e solo se l'utente le chiede**: **undo timeline sul reflog**
   (taglia M — `ReflogService`/`ReflogWindow` esistono e sono un elenco crudo: manca raggruppare le
   righe in operazioni, nominarle in umano, leggere i reflog **per-ref** e aggiungere l'annullamento,
   che è a sua volta annullabile) oppure **rebase interattivo per trascinamento** (taglia L —
   `RebaseTodoWindow` ha già verbi, su/giù e rimuovi; manca il trascinamento, che il codice della
   striscia delle schede ha già collaudato, e manca comporre la todo **prima** di partire).

**Smartphone: fuori perimetro, verificato.** Vedi §4 per i numeri. Non è una ricompilazione.

Prompt da incollare in una chat nuova:

```
Riprendi il port Linux/Avalonia di Git Extensions in /home/dario/git_ext_mod/src/crossplatform.

PRIMA DI TUTTO, in quest'ordine:
1. git -C /home/dario/git_ext_mod rev-parse --short HEAD  (atteso c18f2e369 o piu' avanti; se diverso,
   fidati del repo e non di questo prompt)
2. leggi src/crossplatform/HANDOFF.md sezioni 1, 3, 4 e 5 (§3 sono le trappole, §4 e' cosa resta)
3. leggi src/crossplatform/ROADMAP.md, la tabella "Residui ancora aperti"

REGOLE, non negoziabili:
- si lavora SOLO dentro src/crossplatform/; non toccare la build Windows
- NIENTE push: lo fa l'utente. Verifica con
  git rev-list --count origin/linux-avalonia-port..HEAD
- commit in Conventional Commits, SENZA firma e SENZA trailer Co-Authored-By
- mai git checkout/switch/reset nel repo principale; esperimenti distruttivi solo in /tmp
- verifica il branch (linux-avalonia-port) prima di OGNI commit
- una differenza dall'originale si dichiara solo dopo averla cercata in src/app, e la ricerca si
  scrive accanto alla voce (M217: nove affermazioni su diciotto erano sbagliate)
- una build si guarda per intero: non troncarla con | tail (M216: un errore mangiato ha fatto
  misurare il dll vecchio per un giro)
- per un run headless il dll dell'app e' bin/GitExtensions.Avalonia/Debug/net10.0/…: MAI
  "find bin | head -1", che pesca il dll di un harness vecchio (successo in M218-M220)
- misurare git, non ragionare su git: se il dubbio e' su cosa fa git, si esegue e si guarda
- niente pulsanti finti: se dietro una voce non c'e' il dato, non si mette e si scrive perche'

AMBIENTE:
- export DOTNET_ROOT=$HOME/.dotnet PATH=$HOME/.dotnet:$PATH  (altrimenti "dotnet: comando non trovato")
- build: dotnet build GitExtensions.Avalonia.slnx -c Debug -warnaserror   (deve dare zero avvisi)
- banchi: Tests/run-all.sh --no-build   (deve dire ALL GREEN: 7 harnesses)
- GUI: Xvfb + clic/tasti via python3-xlib (xdotool NON e' installato); cattura con
  import -window <id>. Le finestre si posizionano/ridimensionano da Xlib perche' non c'e' un WM.
  Per i sottomenu in fondo alla finestra: schermo Xvfb piu' alto (1400x1250) + resize della finestra.
- lo stato dei run CI si legge da anonimo: curl api.github.com/repos/daryda9/gitextensions/actions/runs
  (i log grezzi invece vogliono un token)
- lanciando l'app dal tree, GitExtensions.Avalonia/GitExtensions.settings (TRACCIATO) si sporca:
  ripristinalo con git show HEAD:<path> > <path> prima di committare

COSA FARE, scegli in questo ordine e dillo prima di partire:
1. se origin/linux-avalonia-port e' avanzato da 104a8c861, guarda l'esito CI del push nuovo
   (quello su 104a8c861 e' gia' verificato verde)
2. un residuo dalla lista di ROADMAP.md — restano: tema System contro un portal vero, coda RTL
   delle schede, 16 megapixel a schermo, pan/zoom nel confronto immagini, rerere multi-variante
   o path non ASCII. Ognuno chiede prima una misura, nessuno e' cablatura pura.
3. NON iniziare di tua iniziativa le due voci inedite (undo timeline sul reflog, rebase interattivo
   per trascinamento): sono grosse e vanno chieste dall'utente

Quando consegni: aggiorna PORTING.md (nuova milestone, la prossima libera e' M221), ROADMAP.md se
chiudi un residuo, e HANDOFF.md sezioni 1/4/5. Le schermate del README stanno in
src/crossplatform/docs/screenshots.
```
