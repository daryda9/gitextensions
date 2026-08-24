# ROADMAP — dopo la parità: indipendenza dagli strumenti esterni e IA

Documento di lavoro. Nasce da due richieste esplicite:

1. **rendere l'app indipendente da programmi esterni** come kdiff3, offrendoli
   comunque come alternativa;
2. **smettere di guardare solo cosa fa Git Extensions** e guardare il panorama
   dei client git moderni — con attenzione particolare a **dove l'IA aiuta
   davvero** chi vive su git (conflitti in primo luogo).

Quello che segue è **un'analisi di feature, non un confronto fra prodotti**: le
voci sono classificate per *cosa fanno* (§2 feature non-IA per area, §3 feature
IA), non per chi le vende. Dove una feature è ormai uno standard di categoria è
detto così, senza nomi.

> **Regola §4, invariata.** Tutto quello che sta qui sotto è **feature INEDITA**:
> non esiste nell'upstream Windows. Nessuna di queste voci parte di iniziativa
> propria — ognuna va chiesta, e quando viene consegnata va **dichiarata come
> originale**, non come porting. La lista serve a decidere, non ad autorizzare.

Stato dei riferimenti: agosto 2026. Le fonti sono in fondo.

---

## 0. Cosa è già fatto

| | |
|---|---|
| **M207–M210** | **Le altre sei impostazioni** (2026-08-14, quattro unità). La meccanica di M204 esce da `ViewPrefsService` e diventa `JsonSettingsFile<T>` (`418036239`); `app-settings`, `ui-state`, `commit-info`, `favorites`, `scripts` e `hotkeys` ci passano tutti e sei (`df83ed36a`); i punti di scrittura mandano **delta** invece del documento intero, e con essi cade il caso peggiore — la finestra principale che, chiudendosi, riscriveva un `ui-state` vecchio quanto la sessione e annullava tutto quello che le finestre di dialogo avevano salvato nel frattempo (`76d0504c4`); banco di prova `Tests/SettingsStoresRegression` con processi veri e SIGKILL (`c5152dd43`, `PASS: 41 casi`, **non vacuo su entrambe le metà**). Chiude il residuo «Gli altri sei archivi JSON». |
| **M204–M206** | **Chiusura dei residui** (2026-08-14, tre unità). Tre commit: sicurezza fra processi di `ViewPrefsService` con il banco di prova `Tests/ViewPrefsRegression` (`99e1c74b4`, `PASS: 41 casi`, dimostrato non vacuo), `reword`/`squash` che chiedono davvero il messaggio più l'uscita da un `am --3way` in conflitto (`2c5bcdf26`), quattro difetti minori della striscia delle schede (`7b84e9824`). Chiuse così le voci che il giro precedente aveva **misurato e lasciato scritte**: reword/squash, l'ingresso per i conflitti di `am`, l'ellissi in RTL, la forma del path nel tooltip, l'hover del pulsante di chiusura, la priorità dello `Squeeze` e il carica-modifica-salva senza lock delle preferenze di vista. Registrato e **non** corretto allora: gli altri sei archivi JSON avevano lo stesso difetto — chiusi poi in M207–M210. |
| **M196–M203** | **Collaudo dei residui aperti** (2026-08-13/14, otto unità in parallelo, ognuna con repository usa-e-getta e display Xvfb propri, regola sola: **misurare git, non ragionare su git**). Otto commit: barra del sequencer (`7b8372768`), banco di prova della palette (`ea84a9f07`), dialogo di rebase (`409cbc747`), diff di immagini (`004d3610d`), editor della todo (`8659f1868`), rerere nei worktree collegati (`32ef95531`), striscia delle schede (`8cea6b14e`), didascalie del diff (`aebffd9e8`). Diverse voci sono state chiuse **con una misura**, non con una riscrittura: formati immagine che decodificano bene, `--autosquash` che accorpa davvero, verbi esotici della todo che tornano byte per byte, editor di merge oltre il budget. Quello che resta aperto è elencato qui sotto. |
| **M195** | **Palette dei comandi (§2.1).** `Ctrl+Shift+P`. La lista **è il menu vero**, percorso all'apertura, quindi la disponibilità la decide il menu che già la calcola e non c'è un secondo registro da tenere in vita. Recenti salvati per id non tradotto; comandi non disponibili in grigio, non nascosti. **Deviazione dichiarata**: `QuickPull` passa a `Shift+F8`. |
| **M194** | **Ricerca nel contenuto dei commit (§2.1).** Il motore pickaxe c'era **già** (`RevisionFilter.DiffContent` → `git log -S`/`-G`): questa voce della roadmap era **vecchia** su questo punto. Aggiunti l'ingresso dal diff («Search history for …» sulla selezione) e la scelta letterale/regex nella tendina della casella di ricerca; più l'annullamento della camminata precedente. |
| **M181** | **Conflitti non fondibili (§1.3 + §1.5 + §1.6 lato UI).** Selettore del commit per i submodule: mostra cosa c'è *in mezzo* ai due puntatori e sa scegliere anche un **terzo** commit (`update-index --cacheinfo 160000`). Pannello guidato al posto del pulsante grigio: dice in una riga perché il merge a tre vie non si può fare e offre le uscite. rerere a schermo: banner, interruttori, cosa ha già riapplicato, `forget` sotto conferma, finestra della cache. |
| **M180** | **`git rerere` (§1.6).** Servizio con i fatti misurati su git 2.43: attivo anche per sola esistenza di `rr-cache`, `status`/`remaining`/`diff` vuoti dopo un replay mentre l'indice è ancora unmerged, `forget` che si auto-annulla fuori da un merge. |
| **M179** | **Diff di immagini (§1.4).** Affiancate, sovrapposte con opacità, differenza per pixel. Immagine riconosciuta **dai byte**, non dall'estensione. |
| **M178** | **Editor di merge (§1.2 + conflitti banali).** Conflitti di sola spaziatura / fine riga / righe vuote chiusi con un clic, mai all'apertura, reversibili uno per uno. Marcature intra-riga LOCAL↔REMOTE e ogni-lato↔BASE. Rifiuti tipizzati. |
| **M177** | **Diff intra-riga (§1.2).** `InlineDiff`: quali *caratteri* cambiano, in memoria, ~8,6 µs a riga, solo righe visibili. Era il vero divario residuo con kdiff3. |
| **M174** | **Difftool affiancato interno.** «Compare side by side…» nel menu dei file: `git diff --no-index -U0` fa il diff, l'allineamento viene dagli header di hunk, i numeri di riga vengono dall'allineamento e non dal documento. Non serve `diff.tool`. |
| **M173** | **Collaudo del merge su conflitti veri** (5431 righe, UTF-8, CRLF, senza newline finale): due difetti trovati e chiusi. |
| **M172** | **Editor di merge a tre vie interno.** `git merge-file --diff3` fa il merge, `MergeToolService` lo trasforma in chunk tipizzati, `MergeToolWindow` mostra LOCAL / BASE / REMOTE in sola lettura e il risultato modificabile sotto. Con `merge.tool` vuoto i pulsanti esterni sono disabilitati e **Merge funziona lo stesso**. kdiff3 resta dov'era. |

### Residui ancora aperti dopo M204–M206

I due giri hanno chiuso molto; questi **non** sono chiusi, e restano scritti per non sparire in silenzio.

| Residuo | Stato |
|---|---|
| **Immagine troncata** (PNG/GIF/WEBP/BMP) | **CHIUSO in M212.** `ImageIntegrity` chiede a `SKCodec` il codice di risultato (`IncompleteInput`), SkiaSharp è ora dipendenza diretta fissata alla 2.88.9 di Avalonia 11.3.14, e la barra informativa apre con «TRUNCATED FILE». `Tests/ImageIntegrityRegression`: 124 casi, 108 falliscono con la verifica disabilitata. Restano fuori, dichiarati: un file a cui manca **solo** il marcatore di fine non viene segnalato, e sopra i 16 megapixel la domanda non si fa. |
| **Gli altri sei archivi JSON** | **CHIUSO in M207–M210.** Tutti e sei passano da `JsonSettingsFile`: sostituzione atomica, lock di lato e `Update()` che manda un delta. Resta fuori la lista dei repository recenti, che è **dello strato condiviso** e non un archivio JSON del port. |
| **Messaggio di `merge --continue`** | **CHIUSO in M213.** Editor a script che rifiuta, testo di git catturato e mostrato, risposta chiusa con `git commit --cleanup=whitespace`; annullare lascia il merge dov'era ed è riportato come **Cancelled**, non come un fallimento. Il meccanismo dell'editor a script è ora uno solo (`Services/GitScriptedEditor`), condiviso col rebase. |
| **Selettore di commit per il campo *From* del rebase** | **CHIUSO in M214.** `Views/ChooseCommitDialog` — la griglia vera in una terza istanza, limitata al branch corrente fino al merge base col bersaglio (`RevisionGridView.SetWalkBound`, nuovo). Il campo resta anche una casella di testo. Fuori: i due link ai genitori dell'upstream. ~~Gli altri campi che upstream serve con lo stesso selettore~~ — **chiusi in M218–M220**: i due campi dell'archivio (M218), «Compare to commit…» nel sottomenu Compare (M219, upstream lo serve da dentro `FormDiff`), e il cherry-pick, che ora ha il **dialogo** (M220, port di `FormCherryPick`: parent `-m N` per i merge, `-x`, process dialog — prima un merge commit non si poteva proprio prendere). |
| **Tema System sulla striscia delle schede** | **Non esercitato**: il portal XDG che serve non esiste sul display di prova. |
| **Coda dell'RTL sulle schede** | Con nome RTL + duplicato + elisione i puntini finiscono, nell'ordine di lettura, **dopo** il `(1)`; due repository le cui etichette si riducono alla stessa coda restano indistinguibili al pavimento (preesistente); artefatto di arrotondamento sub-pixel al confine del tetto dello `Squeeze`. |
| **Rifiuto oltre 16 megapixel, pan col trascinamento, scorciatoie di zoom** (diff immagini) | Restano come erano: non esercitati a schermo il primo, assenti gli altri due. |
| **Cache rerere multi-variante, path non ASCII o con spazi** | Restano non provati: il giro ha coperto worktree collegati, submodule e conflitti misti, non questi. |

### Fuori perimetro, misurato: smartphone (2026-08-18)

Domanda arrivata dall'utente, risposta con i numeri del codice e non a intuito. **Non** è una
ricompilazione con un altro target:

- **il motore è un processo.** `GitCommands/Git/Executable.cs` fa `Process.Start`; 138 riferimenti nel
  core, **116** punti di chiamata nel port, **20** file del port che avviano processi (git, mergetool
  esterni, terminali, editor). Su **iOS** `fork`/`exec` sono proibiti dalla piattaforma; su **Android**
  non esiste un `git` a bordo e l'esecuzione di binari da directory scrivibili è bloccata per le app
  che puntano ad API 29+. Servirebbe riscrivere lo strato di esecuzione su **libgit2**, cioè proprio
  quello che questo port riusa di proposito;
- **la UI è da scrivania.** **47** classi `Window`, **101** `ShowDialog`, `Avalonia.Desktop`, tre avvii
  con `StartWithClassicDesktopLifetime`, codice specifico X11 in 8 file (atomi, drag-and-drop, barra del
  titolo senza decorazioni, maniglie di ridimensionamento). Su mobile Avalonia è single-view: niente
  finestre, niente modali;
- **il terminale** è dieci P/Invoke a `libc` più `setsid` e `/bin/sh`.

Sopravvivrebbero le parti pure: `InlineDiff`, `ImageIntegrity` (SkiaSharp gira su mobile),
`JsonSettingsFile`, i parser di revisioni e diff. Su 189 file e ~126.000 righe di `App`, sono la
minoranza. Conclusione: una versione mobile è **un'app diversa** che condivide i parser — non una voce
di questa roadmap.

I due mattoni dell'indipendenza sono posati: conflitti e confronto si fanno in
casa, e in entrambi i casi **il motore resta git** — `merge-file` e `diff`. Sono
anche l'infrastruttura su cui appoggiare buona parte del resto di questo
documento: pannelli allineati, evidenziatori e modello a chunk sono già lì.

---

## 1. Indipendenza dagli strumenti esterni — CHIUSO (M177–M181)

Tutte le voci 1.2–1.6 sono state consegnate, più i **conflitti banali** che non erano
in lista. Conflitti, confronto, immagini e riuso delle risoluzioni si fanno in casa, e
il motore resta git.

Quello che è rimasto scoperto **dentro** queste voci, dichiarato e non nascosto:

| Voce | Scoperto |
|---|---|
| §1.2 | ~~Nessuna suite che protegga `InlineDiff` dalle regressioni~~ — **era falso quando è stato scritto**: `Tests/InlineDiffRegression/` esiste, ed è stato il modello dei due banchi arrivati dopo. ~~La modalità intra-riga dell'editor di merge non è persistita fra sessioni~~ — **falso anch'esso**: è persistita da M186 in `view-prefs.json` (`MergeToolPrefs.InlineMode`, letta da `MergeToolWindow.RestoredInlineMode`). Restano scoperti: con più regioni cambiate la granularità resta la parola — `alpha_beta` → `alpha_gamma` marca l'intera parola se accanto c'è un'altra modifica; nessuna opzione «ignora spazi» intra-riga; il pannello MERGE RESULT non ha marcature. |
| §1.3 | Non provati: submodule inizializzato ma con gli oggetti di un lato mancanti, add/add di gitlink senza BASE, liste oltre 200 commit, path con spazi. |
| §1.4 | ~~Firme WEBP/GIF/BMP/ICO implementate ma provate solo su PNG e JPEG~~ — **chiuso in M199**: 26 campioni generati e aperti, `ImageFormats.Detect` li nomina tutti (troncati compresi) e non è stato toccato; BMP RLE4/RLE8, GIF interlacciata, JPEG progressiva e CMYK **decodificano correttamente**, misurate. Sono usciti invece tre difetti (blocco su PNG a 16 bit, contenitore spacciato per il tutto, 16 bit confrontati a 8) e **una nota da correggere**: Skia sceglie la voce ICO **più grande**, non la prima. ~~Restano: **immagine troncata senza avviso** (vedi §0)~~ — **chiuso in M212** (`SKCodec.IncompleteInput`, clausola «TRUNCATED FILE» in testa alla barra informativa, 124 casi in `Tests/ImageIntegrityRegression`). Restano: rifiuto oltre 16 megapixel non esercitato a schermo, niente pan col trascinamento né scorciatoie di zoom. |
| §1.5 | Non provati a schermo: file oltre 20 MB, e il pannello su un repo senza `merge.tool` configurato. |
| §1.6 | Conflitti di rebase provati in M187; **worktree collegati provati in M201, e lì c'era il bug**: `MERGE_RR` è per-worktree ma `rr-cache` vive solo nella common directory, e il port chiedeva `--absolute-git-dir` per entrambe — git riapplicava una risoluzione con l'app che non mostrava niente. Provati e sani nello stesso giro: submodule a metà rebase (`modules/<nome>`) e un merge con conflitti binari, di soli permessi, delete/modify, rename/rename e symlink insieme. Restano non provati **cache multi-variante** e **path non ASCII o con spazi**; il ramo `am` del testo **non è più irraggiungibile** — M205 ha aggiunto l'ingresso, ma solo dove esiste davvero: misurato, un `git am` semplice non lascia niente di unmerged e non coinvolge rerere, solo `am --3way` lo fa, quindi banner e `ApplyPatchDialog` offrono la risoluzione **solo** con l'indice unmerged. `git rerere clear` deliberatamente non esposto: sembra «svuota la cache» e non lo è, e non ha un annulla sicuro. |

**Stato onesto del collaudo automatico.** Il port ha oggi **sei** banchi di prova di regressione:
`Tests/InlineDiffRegression` (il primo, e il modello degli altri), `Tests/CommandPaletteRegression`
(M197), `Tests/ViewPrefsRegression` (M204), `Tests/SettingsStoresRegression` (M210),
`Tests/ImageIntegrityRegression` (M212) e `Tests/SyntaxTokenizeRegression` (M221, col watchdog che
nomina il caso appeso); accanto restano le sonde e gli snapshot preesistenti
(`NavigationSnapshot`, `SubmoduleHierarchy`, `Perf`, `AnimProbe`, `ChromeProbe`). **Da M211 non si
lanciano più a mano**: `GitExtensions.Avalonia.slnx` li compila tutti, `Tests/run-all.sh` lancia gli otto
deterministici ognuno in una sandbox propria, e `.github/workflows/crossplatform-build.yml` fa
entrambe le cose con `-warnaserror` sui path che toccano il port. Esclusi dal runner, con la ragione
scritta: `AnimProbe` e `ChromeProbe` (vogliono uno schermo), `Perf` (è una misura, non un verdetto).
**Il primo run della CI si è già pagato da solo** (M215): `navigation-snapshot` si appendeva per tutti i
120 s di timeout, con il log vuoto, su un runner ospitato — mai qui, nemmeno strozzato a due core. Era
il banco a parcheggiare un worker del pool aspettando codice che, per girare, aveva bisogno del pool;
adesso quella sezione ha un thread proprio, ogni parcheggio è limitato e il runner dice quando un log
vuoto significa «appeso» e non «log perduto».
Tutto il resto non ha collaudo automatico e viene verificato **a schermo**: il
motore di merge e i suoi chunk, i servizi del diff, `RebaseSessionService` / `MergeSessionService` /
rerere, il diff di immagini, la striscia delle schede e la UI in generale.

Trasversale: le stringhe nuove passano da `T(english)` come le altre, quindi a schermo
restano in inglese finché non esiste un catalogo italiano attivo — è una decisione di
catalogo, non di questi file.

---

## 2. Feature non-IA, per area

Classificate per area funzionale. La colonna **Diffusione** dice quanto la
feature è ormai attesa da chi arriva da un client moderno: *standard* = la danno
praticamente tutti, *comune* = la danno diversi, *rara* = la dà qualcuno e
distingue.

### 2.1 Navigazione e comandi — CHIUSA (M194–M195)

| Voce | Diffusione | Cosa dà | Sforzo |
|---|---|---|---|
| ~~**Palette dei comandi** (Ctrl+Shift+P, ogni azione git raggiungibile da tastiera)~~ — **fatta (M195)** | comune | Consegnata, e **non** come previsto qui: l'elenco non viene dal registro degli hotkey ma dal **menu vero**, percorso all'apertura — `HotkeyService` serve solo per i comandi legati a un tasto e assenti da ogni menu. **Le quattro lacune dichiarate sono state chiuse in M197**: banco di prova `Tests/CommandPaletteRegression` (`PASS: 10037 casi`, fuzz di 10 000 coppie in 137 ms, dimostrato non vacuo), che ha trovato l'allineamento su **unità di codice UTF-16** e le due metà di emoji diverse disegnate come mojibake; spunta come «on»/«off» in colonna propria; voci di lingua offerte (la paura delle didascalie vecchie non ha retto alla prova); motivo accanto alle righe grigie **solo dove è dimostrabile** — due esistono, nessuno dedotto. | S/M |
| ~~**Ricerca nel contenuto dei commit** (pickaxe, `-S`/`-G`) con UI decente~~ — **fatta (M194)** | rara | **Questa voce era già in gran parte realizzata quando è stata scritta**: il pickaxe girava già via `RevisionFilter.DiffContent`, la roadmap era vecchia su questo punto. Non è stata costruita da zero — sono stati aggiunti l'ingresso dal diff, la scelta letterale/regex a schermo e l'annullamento della camminata superata. Scoperto: blame e vista del contenuto non hanno la voce, l'annullamento è osservato solo fra un blocco di output e l'altro. | S |

### 2.2 Ispezione della storia

| Voce | Diffusione | Cosa dà | Sforzo |
|---|---|---|---|
| **Blame incrementale nell'editor** (chi ha toccato questa riga, in linea) | comune | `BlameView` c'è; qui si tratta di portarlo *dentro* il diff. | M |
| **Grafo dei commit ad alte prestazioni su repo enormi** | standard | Il port è nativo e non Electron, quindi parte avvantaggiato; **da misurare prima di ottimizzare**. | ? |

### 2.3 Riscrittura della storia e sicurezza

| Voce | Diffusione | Cosa dà | Sforzo |
|---|---|---|---|
| **Undo timeline** — ogni operazione registrata e reversibile | rara | Toglie la paura. Sul port si può costruire onestamente sopra il **reflog** invece di inventare uno storage: il reflog è già la timeline, manca la lettura. | M |
| **Rebase interattivo per trascinamento** | comune | `git rebase -i` con la todo-list resa a schermo. Il rischio è tutto nella gestione dell'interruzione a metà, che nel port c'è già (`RebaseSessionService`). | L |

### 2.4 Modello di lavoro sui branch

| Voce | Diffusione | Nota |
|---|---|---|
| **Branch impilati / PR impilate** | rara | Utile solo con una forge collegata; il port ha già GitHub (M159). Grosso. |
| **Branch virtuali** (più branch nella stessa working copy) | rara | **Sconsigliata.** Dove esiste è costruita su un motore git proprio; reimplementarla sopra git standard significa inventare uno stato che git non ha. I worktree, che il port già mostra, coprono l'80% del bisogno con zero magia. |

### 2.5 Collaborazione

| Voce | Diffusione | Nota |
|---|---|---|
| **Integrazione forge oltre GitHub** (GitLab, Bitbucket, Azure DevOps) | comune | Molto lavoro per utente marginale finché GitHub copre il caso d'uso. |

---

## 3. Feature IA

### 3.1 I casi d'uso, in ordine di utilità reale

| # | Caso | Cosa fa | Perché è il posto giusto |
|---|---|---|---|
| 3.1.1 | **Risoluzione assistita dei conflitti** | Per ogni blocco di conflitto propone una risoluzione **con la spiegazione del perché**, e non applica niente finché non la si accetta. | È il caso in cui l'utente ha meno contesto e più fretta. È anche il caso in cui il modello ha **tutto** il contesto che gli serve già in mano: base, ours, theirs sono tre testi brevi e completi. Dove esiste oggi, le due scelte di progetto ricorrenti sono il **punteggio di confidenza** sulla proposta e il **lavoro in parallelo su più file** — con applicazione solo dopo approvazione. |
| 3.1.2 | **Messaggio di commit dal diff staged** | Genera oggetto e corpo dal diff dell'indice. | Il pezzo più noioso della giornata, ed è ormai **standard di categoria**. Va generato **come proposta modificabile**, mai committato da solo. |
| 3.1.3 | **Spiegami questo commit / questo branch** | Riassunto in linguaggio naturale di cosa cambia un commit o l'insieme dei commit di un branch. | Serve a chi entra in un repo che non ha scritto — cioè quasi sempre. |
| 3.1.4 | **Spiegami questo conflitto** (senza risolverlo) | Dice *cosa* le due parti stavano facendo, e lascia decidere. | Più onesto di 3.1.1 e più utile di quanto sembri: spesso il problema non è scrivere la risoluzione, è capire cosa si sta scegliendo. Complemento naturale del pannello BASE già a schermo. |
| 3.1.5 | **Descrizione della pull request** | Titolo e corpo dai commit del branch. | C'è già il flusso GitHub nel port (M159): è l'aggancio più corto. |
| 3.1.6 | **Ricomposizione della history** (spezza, riordina, accorpa i commit) | Dove esiste è dichiarata sperimentale. | **Ultima della lista.** Riscrive la storia: il rapporto rischio/beneficio è il peggiore del gruppo. |
| 3.1.7 | **Messaggio di stash** | Etichetta uno stash dal suo contenuto. | Marginale, ma quasi gratis una volta che c'è 3.1.2: stesso ingresso, stesso prompt. |

### 3.2 Come va costruita, se si costruisce

Queste sono decisioni di architettura, non di feature, e conviene prenderle
prima della prima riga di codice:

1. **Un'interfaccia sola, provider dietro.** `IAiProvider` con due sole
   operazioni (completamento testo, completamento strutturato) e le
   implementazioni dietro: Anthropic, OpenAI, Gemini, endpoint compatibile
   (Ollama / LM Studio / OpenRouter). È il modello **BYOK**, che nel settore è
   ormai la norma: si fa scegliere il fornitore e si **includono i modelli
   locali**.
2. **Nessun default che parla con la rete.** L'IA parte **spenta**. Nessuna
   chiamata prima che l'utente abbia scelto un fornitore e messo una chiave. Su
   uno strumento che vede codice sorgente privato questa non è una preferenza,
   è il presupposto per poterlo installare in azienda.
3. **La chiave sta dove stanno le credenziali git**, cioè nel keyring
   (libsecret è già configurato in questo ambiente), mai in un file di
   configurazione in chiaro.
4. **Niente auto-applicazione.** Ogni risultato è una **proposta**: si vede
   prima, si applica dopo, e c'è sempre il modo di rifiutarla. Vale in
   particolare per 3.1.1, dove l'errore silenzioso di un modello finisce
   direttamente in un commit di merge.
5. **Si dice sempre cosa esce.** Prima della prima chiamata l'utente deve poter
   sapere quali byte partono (il diff? l'intero file? i tre lati?) e va scritto
   nella UI, non solo nella documentazione.
6. **Modello locale come cittadino di prima classe.** Un `merge.tool` interno più
   un modello via Ollama significa un'app che risolve conflitti con l'IA **senza
   che un byte del codice esca dalla macchina**. È la posizione che un prodotto
   commerciale a sottoscrizione non ha interesse a occupare fino in fondo, ed è
   a portata di questo port.

### 3.3 Il primo passo consigliato

**3.1.2 (messaggio di commit)**, non 3.1.1. Motivi: il diff staged è già a
disposizione nel dialogo di commit, l'errore costa una modifica di testo prima
di premere invio, e serve a costruire e collaudare tutta l'infrastruttura del
punto 3.2 — provider, chiave nel keyring, interruttore spento di default,
proposta rivedibile — sul caso meno pericoloso che esista.

Subito dopo, **3.1.4 (spiega il conflitto)** invece di 3.1.1: si aggancia al
pannello BASE dell'editor già costruito, non tocca il testo del risultato, e
prepara il terreno per la risoluzione assistita quando ci sarà fiducia
nell'impianto.

---

## 4. Ordine proposto

Già consegnati, in ordine di consegna:

1. ~~**1.1 difftool interno**~~ — **fatto (M174)**.
2. ~~**1.2 diff intra-riga**~~ — **fatto (M177)**.
3. ~~**2.1 palette dei comandi**~~ — **fatto (M195)**.
4. ~~**2.1 ricerca nel contenuto dei commit**~~ — **fatto (M194)**, sopra un motore che c'era già.

Quello che resta, riordinato di conseguenza:

1. **3.1.2 messaggio di commit con IA** — resta il primo passo consigliato del §3.3: porta dentro
   tutta l'infrastruttura IA sul caso più innocuo.
2. **2.2 blame in linea nel diff** — `BlameView` c'è, si tratta di portarlo *dentro* il diff; è la
   voce rimasta con il rapporto resa/rischio migliore.
3. **3.1.4 spiega il conflitto** — il primo uso dell'IA dove serve davvero, sopra il pannello BASE
   già costruito.
4. **2.3 undo timeline sul reflog** — il reflog *è* già la timeline, manca la lettura; niente storage
   inventato.
5. **2.3 rebase interattivo per trascinamento** — l'interruzione a metà è già gestita
   (`RebaseSessionService`) e `--edit-todo` passa già da un editor scriptato (M191), quindi il
   trascinamento è la parte che manca; resta la voce **grossa** del gruppo. `reword` e `squash`
   **non sono più un prerequisito**: chiedono davvero il messaggio da M205. Resta che nessun passo
   può essere **aggiunto** alla todo.
6. **2.2 prestazioni del grafo su repo enormi** — **da misurare prima di decidere se esiste un
   problema**: oggi non c'è nessuna misura, quindi non c'è nessun lavoro giustificato.
7. **2.4 / 2.5 branch impilati e forge oltre GitHub** — ultimi: molto lavoro, e utili solo a chi sta
   fuori dal caso d'uso GitHub già coperto (M159).

---

## Fonti

Rassegna di agosto 2026 sulle feature dei client git da desktop e sulle loro
funzioni IA. I riferimenti servono a datare l'analisi, non a indicare un
prodotto da imitare.

- [Panoramica comparativa dei client git da desktop, 2026](https://jonathansblog.co.uk/best-git-ui-clients-2026)
- [Rassegna dei client git per Mac e Windows, 2026](https://www.git-tower.com/blog/best-git-client)
- [Documentazione sul modello a branch impilati](https://docs.gitbutler.com/features/branch-management/stacked-branches)
- [Documentazione sul modello a branch virtuali](https://docs.gitbutler.com/overview)
