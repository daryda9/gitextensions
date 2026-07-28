# Round 10 — passata di leggibilità in tema CHIARO

Base: `6979841a8`. Build di partenza: Errori 0.
Classe di bug (M62): chiave `App.*` letta ma **non registrata** in `ThemeManager` →
`Brush("App.X", fallback)` restituisce il fallback (che NON segue il tema), `B("App.X")` → null.

## 1. Censimento delle chiavi `App.*`

Lette (23, via `grep -rhoE '"App\.[A-Za-z0-9_]+"'` su `App/**/*.cs`):

| Chiave | Registrata prima? | Fallback ai punti di lettura | Esito |
|---|---|---|---|
| App.Window | sì | — | ok |
| App.Panel | sì | — | ok |
| App.PanelAlt | sì | — | ok |
| App.Toolbar | sì | — | ok |
| App.Border | sì | — | ok |
| App.Text | sì | — | ok |
| App.TextDim | sì | — | ok |
| App.Accent | sì | — | ok (vedi residuo §4) |
| App.Selection | sì | — | ok |
| App.GraphGreen | sì | — | ok |
| App.Control | sì (da M62) | — | ok |
| **App.Foreground** | **NO** | `Brushes.Gainsboro` #DCDCDC | **BUG → registrata** = App.Text |
| **App.PanelBackground** | **NO** | `#2A2A2E` | **BUG → registrata** = App.PanelAlt |
| **App.DiffAdded** | **NO** | `Brushes.LimeGreen` #32CD32 | **BUG → registrata** |
| **App.DiffRemoved** | **NO** | `Brushes.OrangeRed` #FF4500 / `#CE5C5C` | **BUG → registrata** |
| App.ConsoleBackground | NO (voluto) | `#111111` | decisione §2 |
| App.ConsoleForeground | NO (voluto) | `#D0D0D0` | decisione §2 |
| App.RepoStateClean | **NO** | `#8A8A8A` | file NON mio (MainToolbar) → misurato §5 |
| App.RepoStateDirty | **NO** | `#FFA07A` | file NON mio → misurato §5 |
| App.RepoStateDirtySubmodules | **NO** | `#FFA500` | file NON mio → misurato §5 |
| App.RepoStateMixed | **NO** | `#E6A700` | file NON mio → misurato §5 |
| App.RepoStateStaged | **NO** | `#87CEFA` | file NON mio → misurato §5 |
| App.RepoStateUntrackedOnly | **NO** | `#8A63D2` | file NON mio → misurato §5 |

### Valori registrati (commit 1)

| Chiave | Dark | Light | Perché |
|---|---|---|---|
| App.Foreground | `#DCDCDC` | `#1E1E1E` | identici ad App.Text; il dark resta pixel-identico al fallback Gainsboro |
| App.PanelBackground | `#2D2D30` | `#ECECEC` | identici ad App.PanelAlt; #2D2D30 vs #2A2A2E = 1,04:1 (invisibile) |
| App.DiffAdded | `#6AC776` | `#1E7D5A` | dark = tinta che DiffView già usa; light = valore già registrato per App.GraphGreen |
| App.DiffRemoved | `#E06C6C` | `#B03A3A` | dark = tinta di DiffView; light = stessa gamma brick-red, scurita (nessun rosso in palette; #CE5C5C fa solo 3,95:1) |

### Misure — prima / dopo

| Punto | Tema | Prima | Dopo |
|---|---|---|---|
| CommitDialog, testo diff normale su App.Window | chiaro | #DCDCDC su #F3F3F3 = **1,24:1** | #1E1E1E su #F3F3F3 = **15,02:1** |
| CommitDialog, `_conflictText` / `_conflictHint` / 6 altri TextBlock | chiaro | #DCDCDC su fondo chiaro = **1,24–1,37:1** | **15,02:1** |
| CommitDialog, righe `+` del diff | chiaro | #32CD32 su #F3F3F3 = **1,91:1** | #1E7D5A = **4,58:1** |
| CommitDialog, righe `-` del diff | chiaro | #FF4500 su #F3F3F3 = **3,10:1** | #B03A3A = **5,39:1** |
| CommitDialog, righe `+`/`-` | scuro | #32CD32 / #FF4500 (fuori palette) | #6AC776 = 7,97:1 / #E06C6C = 5,18:1 |
| CleanupDialog `_confirmBar` + `_confirmText` | chiaro | #1E1E1E su #2A2A2E = **1,17:1** | #1E1E1E su #ECECEC = **14,11:1** |
| SettingsWindow `_hotkeyWarning` / conflitti | chiaro | #CE5C5C su fondo chiaro = 3,95:1 | #B03A3A = **5,98:1** |

## 2. Decisione su App.ConsoleBackground / App.ConsoleForeground

TODO (misure in corso).

## 3. Passata a schermo in tema chiaro

TODO.

## 4. Residui misurati, non corretti (palette di base, fuori mandato)

- `App.Accent` #007ACC su `App.Window` chiaro #F3F3F3 = **4,06:1** (< 4,5). Usato per gli header
  di hunk `@@` nel diff del CommitDialog. Non toccato: è la tinta d'accento di base, usata anche
  come *fondo* (banner conflitti), e il mandato vieta di ridisegnare la palette.
- `DiffView` codifica a mano #6AC776 / #E06C6C (non via chiavi `App.*`): su fondo chiaro
  **2,09:1** e **3,22:1**. Vedi §3 per l'esito della verifica a schermo.

## 5. Difetti nei file non miei (misurati, NON corretti)

`App/Views/MainToolbar.cs:990-998` (`CommitStateBrush`) — le sei chiavi `App.RepoState*` non sono
registrate. Il commento in loco dice che i colori "sono offerti come chiavi di tema *prima*, con
fallback ai valori upstream": intenzione giusta, ma poiché nessun tema le registra, il risultato è
sempre il colore upstream (pensato per la toolbar chiara di Windows Forms) e non segue il tema.
Il brush è il **foreground del testo** `_commitCaption` (riga 969) → testo normale, soglia 4,5:1.
Misure su `App.Toolbar` chiaro `#E4E4E4`:

| Stato | Colore | Contrasto su #E4E4E4 | Verdetto |
|---|---|---|---|
| Staged | #87CEFA | **1,35:1** | grave |
| DirtySubmodules | #FFA500 | **1,55:1** | grave |
| Dirty | #FFA07A | **1,56:1** | grave |
| Mixed | #E6A700 | **1,67:1** | grave |
| Clean | #8A8A8A | **2,72:1** | insufficiente |
| UntrackedOnly | #8A63D2 | **3,44:1** | insufficiente per testo normale |

Nessuno raggiunge 4,5:1; quattro su sei non raggiungono nemmeno 3:1. Il fix corretto è registrare
le sei chiavi con una variante scura per il tema chiaro (come fatto qui per App.DiffAdded/Removed).
**Non corretto**: `MainToolbar.cs` è assegnato a un altro subagent.
