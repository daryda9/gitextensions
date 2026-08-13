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
| **M181** | **Conflitti non fondibili (§1.3 + §1.5 + §1.6 lato UI).** Selettore del commit per i submodule: mostra cosa c'è *in mezzo* ai due puntatori e sa scegliere anche un **terzo** commit (`update-index --cacheinfo 160000`). Pannello guidato al posto del pulsante grigio: dice in una riga perché il merge a tre vie non si può fare e offre le uscite. rerere a schermo: banner, interruttori, cosa ha già riapplicato, `forget` sotto conferma, finestra della cache. |
| **M180** | **`git rerere` (§1.6).** Servizio con i fatti misurati su git 2.43: attivo anche per sola esistenza di `rr-cache`, `status`/`remaining`/`diff` vuoti dopo un replay mentre l'indice è ancora unmerged, `forget` che si auto-annulla fuori da un merge. |
| **M179** | **Diff di immagini (§1.4).** Affiancate, sovrapposte con opacità, differenza per pixel. Immagine riconosciuta **dai byte**, non dall'estensione. |
| **M178** | **Editor di merge (§1.2 + conflitti banali).** Conflitti di sola spaziatura / fine riga / righe vuote chiusi con un clic, mai all'apertura, reversibili uno per uno. Marcature intra-riga LOCAL↔REMOTE e ogni-lato↔BASE. Rifiuti tipizzati. |
| **M177** | **Diff intra-riga (§1.2).** `InlineDiff`: quali *caratteri* cambiano, in memoria, ~8,6 µs a riga, solo righe visibili. Era il vero divario residuo con kdiff3. |
| **M174** | **Difftool affiancato interno.** «Compare side by side…» nel menu dei file: `git diff --no-index -U0` fa il diff, l'allineamento viene dagli header di hunk, i numeri di riga vengono dall'allineamento e non dal documento. Non serve `diff.tool`. |
| **M173** | **Collaudo del merge su conflitti veri** (5431 righe, UTF-8, CRLF, senza newline finale): due difetti trovati e chiusi. |
| **M172** | **Editor di merge a tre vie interno.** `git merge-file --diff3` fa il merge, `MergeToolService` lo trasforma in chunk tipizzati, `MergeToolWindow` mostra LOCAL / BASE / REMOTE in sola lettura e il risultato modificabile sotto. Con `merge.tool` vuoto i pulsanti esterni sono disabilitati e **Merge funziona lo stesso**. kdiff3 resta dov'era. |

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
| §1.2 | Nessuna suite che protegga `InlineDiff` dalle regressioni. Con più regioni cambiate la granularità resta la parola: `alpha_beta` → `alpha_gamma` marca l'intera parola se accanto c'è un'altra modifica. Nessuna opzione «ignora spazi» intra-riga. Il pannello MERGE RESULT non ha marcature. La modalità intra-riga dell'editor di merge non è persistita fra sessioni. |
| §1.3 | Non provati: submodule inizializzato ma con gli oggetti di un lato mancanti, add/add di gitlink senza BASE, liste oltre 200 commit, path con spazi. |
| §1.4 | Firme WEBP/GIF/BMP/ICO implementate ma provate solo su PNG e JPEG. Rifiuto oltre 16 megapixel non esercitato a schermo. Niente pan col trascinamento né scorciatoie di zoom. |
| §1.5 | Non provati a schermo: file oltre 20 MB, e il pannello su un repo senza `merge.tool` configurato. |
| §1.6 | Nessuna prova su conflitti di rebase (solo merge), su cache multi-variante, su path non ASCII o con spazi, su worktree collegati. `git rerere clear` deliberatamente non esposto: sembra «svuota la cache» e non lo è, e non ha un annulla sicuro. |

Trasversale: le stringhe nuove passano da `T(english)` come le altre, quindi a schermo
restano in inglese finché non esiste un catalogo italiano attivo — è una decisione di
catalogo, non di questi file.

---

## 2. Feature non-IA, per area

Classificate per area funzionale. La colonna **Diffusione** dice quanto la
feature è ormai attesa da chi arriva da un client moderno: *standard* = la danno
praticamente tutti, *comune* = la danno diversi, *rara* = la dà qualcuno e
distingue.

### 2.1 Navigazione e comandi

| Voce | Diffusione | Cosa dà | Sforzo |
|---|---|---|---|
| **Palette dei comandi** (Ctrl+Shift+P, ogni azione git raggiungibile da tastiera) | comune | È la feature che chi ci lavora cita per prima. Sul port è **quasi gratis**: c'è già il registro degli hotkey (`HotkeyService`, i 6 scope di M158) da cui prendere l'elenco delle azioni. | S/M |
| **Ricerca nel contenuto dei commit** (pickaxe, `-S`/`-G`) con UI decente | rara | `GitGrepService` esiste già; manca l'ingresso dalla griglia. | S |

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

1. ~~**1.1 difftool interno**~~ — **fatto (M174)**.
2. **2.1 palette dei comandi** — costo basso, resa altissima, `HotkeyService` è già lì.
3. **3.1.2 messaggio di commit con IA** — porta dentro tutta l'infrastruttura IA sul caso più innocuo.
4. **1.2 diff intra-riga** — toglie l'ultimo motivo per aprire un tool esterno.
5. **3.1.4 spiega il conflitto** — il primo uso dell'IA dove serve davvero.

---

## Fonti

Rassegna di agosto 2026 sulle feature dei client git da desktop e sulle loro
funzioni IA. I riferimenti servono a datare l'analisi, non a indicare un
prodotto da imitare.

- [Panoramica comparativa dei client git da desktop, 2026](https://jonathansblog.co.uk/best-git-ui-clients-2026)
- [Rassegna dei client git per Mac e Windows, 2026](https://www.git-tower.com/blog/best-git-client)
- [Documentazione sul modello a branch impilati](https://docs.gitbutler.com/features/branch-management/stacked-branches)
- [Documentazione sul modello a branch virtuali](https://docs.gitbutler.com/overview)
