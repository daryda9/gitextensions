// Ogni nome di asset che l'app carica esiste davvero — e il banco fallisce dicendo QUALE.
//
// PERCHE' ESISTE. Tre difetti in due giorni, tutti della stessa famiglia e tutti trovati
// dall'uso e non dagli strumenti: l'icona di finestra che nessuna finestra riceveva
// (M223bis), l'icona cercata nella barra sotto il nome "dotnet" (da0a0fb95) e
// l'illustrazione dei dialoghi di merge e rebase spenta dalla rinomina (M224). Il filo
// comune non e' Avalonia: e' che QUI una risorsa mancante non fa rumore. AssetLoader
// risponde "non c'e'", e i chiamanti sono scritti per disegnare niente — il modo giusto
// di comportarsi a schermo, e il modo peggiore per accorgersene.
//
// COME. Nessuna finestra, nessun display, nessun avvio di Avalonia: Avalonia.Platform
// .StandardAssetLoader e' pubblica e risolve gli URI avares: da sola. Quindi questo banco
// gira nel runner insieme agli altri deterministici.
//
// NON VACUO PER COSTRUZIONE. I casi negativi sono dentro il banco e girano sempre: un host
// che non e' nessun assembly caricato e un nome inventato DEVONO risultare assenti. Se un
// giorno Exists dicesse si' a tutto, sarebbero questi a fallire, non le asserzioni positive.
using System.Collections;
using System.Reflection;
using Avalonia.Platform;

int checks = 0;
int failures = 0;
StandardAssetLoader loader = new();
Assembly app = typeof(GitExtensions.Avalonia.App).Assembly;
string assemblyName = app.GetName().Name!;
string expectedBase = $"avares://{assemblyName}/";

void Check(bool ok, string what)
{
    checks++;
    if (!ok)
    {
        failures++;
        Console.WriteLine($"FAIL: {what}");
    }
}

bool Resolves(string relativePath)
{
    try
    {
        return loader.Exists(new Uri(expectedBase + relativePath));
    }
    catch (Exception e)
    {
        Console.WriteLine($"  ({relativePath}: {e.GetType().Name})");
        return false;
    }
}

// ---------------------------------------------------------------- 1. la base
//
// L'host di un URI avares: E' il nome dell'assembly. Scriverlo a mano e' cio' che ha
// spento le illustrazioni: dopo la rinomina a GitNext i letterali indicavano un assembly
// che non esiste piu'. Theming.AssetUri e' l'unico posto che lo compone, ed e' interno,
// quindi lo si legge per reflection: il banco non deve poter divergere dal codice.
Type? assetUri = app.GetType("GitExtensions.Avalonia.Theming.AssetUri");
Check(assetUri is not null, "Theming.AssetUri esiste (il posto unico che compone gli URI avares:)");
string? actualBase = assetUri?.GetProperty("Base", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
    ?.GetValue(null) as string;
Check(actualBase == expectedBase, $"AssetUri.Base e' '{expectedBase}' (letto: '{actualBase ?? "null"}')");

// ---------------------------------------------------------------- 2. il marchio
//
// Un file, tre consumatori: l'icona di ogni finestra (Theming.AppIcon), il logo dell'About
// e la striscia della dashboard. Se manca, tutti e tre tacciono.
Check(Resolves("Assets/Icons/GitNext.png"), "il marchio del prodotto (Assets/Icons/GitNext.png)");

// ---------------------------------------------------------------- 3. i diagrammi di aiuto
//
// Raccolti per reflection da OGNI HelpImageSpec dichiarata nell'app, non da una lista qui:
// un dialogo nuovo con un diagramma nuovo entra in questo banco senza toccarlo.
Type? specType = app.GetType("GitExtensions.Avalonia.Views.HelpImageSpec");
Check(specType is not null, "Views.HelpImageSpec esiste");

// La radice che il pannello usa DAVVERO, non quella che dovrebbe usare. Il difetto del
// M224 stava qui: una base giusta altrove non salva un chiamante che ne tiene una sua.
// Quindi i diagrammi si cercano sotto QUESTA, e questa si confronta con la base attesa.
Type? panel = app.GetType("GitExtensions.Avalonia.Views.HelpImagePanel");
string? panelRoot = panel?.GetField("Root", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null) as string;
Check(panelRoot is not null, "HelpImagePanel.Root e' leggibile");
Check(panelRoot == expectedBase + "Assets/Help/",
    $"HelpImagePanel.Root e' '{expectedBase}Assets/Help/' (letto: '{panelRoot ?? "null"}')");

List<string> diagrams = [];
if (specType is not null)
{
    PropertyInfo? image1 = specType.GetProperty("Image1");
    PropertyInfo? image2 = specType.GetProperty("Image2");

    foreach (Type type in app.GetTypes())
    {
        foreach (FieldInfo field in type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (field.FieldType != specType)
            {
                continue;
            }

            object? spec = field.GetValue(null);
            if (spec is null)
            {
                continue;
            }

            foreach (PropertyInfo? property in new[] { image1, image2 })
            {
                if (property?.GetValue(spec) is string name && name.Length > 0)
                {
                    diagrams.Add(name);
                }
            }
        }
    }
}

Check(diagrams.Count > 0, "almeno una HelpImageSpec trovata nell'app (altrimenti questo banco non prova niente)");
foreach (string name in diagrams.Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal))
{
    // Sotto la radice del pannello: se quella e' sbagliata, e' QUESTA riga a dire quale
    // diagramma l'utente non vedrebbe, con l'URI che il pannello chiederebbe.
    string uri = (panelRoot ?? expectedBase + "Assets/Help/") + name + ".png";
    bool ok;
    try
    {
        ok = loader.Exists(new Uri(uri));
    }
    catch (Exception)
    {
        ok = false;
    }

    Check(ok, $"diagramma di aiuto '{name}' ({uri})");
}

// ---------------------------------------------------------------- 4. i bitmap dello stile Classic
//
// Icons.ClassicNames mappa i pochi glifi battezzati col nome del comando sul file
// dell'originale. Ogni valore di quella tabella e' un asset che lo stile Classic chiede.
Type? icons = app.GetType("GitExtensions.Avalonia.Theming.Icons");
FieldInfo? classic = icons?.GetField("ClassicNames", BindingFlags.Static | BindingFlags.NonPublic);
Check(classic is not null, "Theming.Icons.ClassicNames esiste");

int classicChecked = 0;
if (classic?.GetValue(null) is IDictionary map)
{
    foreach (DictionaryEntry entry in map)
    {
        if (entry.Value is not string file || file.Length == 0)
        {
            continue;
        }

        string name = Path.GetFileNameWithoutExtension(file);
        Check(Resolves($"Assets/Icons/{name}.png"), $"bitmap Classic di '{entry.Key}' -> {file}");
        classicChecked++;
    }
}

Check(classicChecked > 0, "ClassicNames non e' vuota");

// ---------------------------------------------------------------- 5. il set di icone c'e' tutto
//
// Non un conteggio esatto — cambierebbe a ogni icona aggiunta a monte — ma un pavimento:
// se il glob delle risorse smettesse di prendere le icone dell'originale, qui si vedrebbe.
int icons_count = loader.GetAssets(new Uri(expectedBase + "Assets/Icons/"), null).Count();
Check(icons_count > 200, $"il set di icone e' imbarcato ({icons_count} file sotto Assets/Icons/, atteso > 200)");

// ---------------------------------------------------------------- 5b. i nomi scritti nel codice
//
// Le sezioni sopra leggono strutture: coprono cio' che l'app dichiara come dato. I nomi
// passati a IconLoader sono invece LETTERALI dentro le chiamate, e un letterale sbagliato
// e' precisamente la forma dei tre difetti che hanno motivato questo banco. Quindi il
// sorgente si legge.
//
// La cartella arriva come argomento (il runner la passa) o si deduce dalla posizione del
// binario. Se non si trova, questo banco FALLISCE invece di saltare in silenzio: un banco
// che tace su una meta' di se stesso e' la trappola che il conteggio 39-contro-41 di
// SettingsStoresRegression ha gia' fatto pagare una volta.
string sourceDir = args.Length > 0
    ? args[0]
    : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "App"));

bool haveSource = Directory.Exists(sourceDir) && File.Exists(Path.Combine(sourceDir, "Views", "HelpImagePanel.cs"));
Check(haveSource, $"i sorgenti dell'app sono leggibili ({sourceDir})");

int literals = 0;
int hosts = 0;
if (haveSource)
{
    string[] files = Directory.GetFiles(sourceDir, "*.cs", SearchOption.AllDirectories);
    Check(files.Length > 100, $"il sorgente dell'app e' quello ({files.Length} file .cs)");

    System.Text.RegularExpressions.Regex iconCall = new(
        @"IconLoader\.(?:Image|Load)\(""([A-Za-z0-9_]+)""",
        System.Text.RegularExpressions.RegexOptions.Compiled);
    System.Text.RegularExpressions.Regex avaresLiteral = new(
        @"""avares://([A-Za-z0-9_.]+)/",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    HashSet<string> names = new(StringComparer.Ordinal);
    HashSet<string> literalHosts = new(StringComparer.Ordinal);
    foreach (string file in files)
    {
        string text = File.ReadAllText(file);
        foreach (System.Text.RegularExpressions.Match m in iconCall.Matches(text))
        {
            names.Add(m.Groups[1].Value);
        }

        foreach (System.Text.RegularExpressions.Match m in avaresLiteral.Matches(text))
        {
            literalHosts.Add(m.Groups[1].Value);
        }
    }

    Check(names.Count > 5, $"trovati nomi di icone nel codice ({names.Count})");
    foreach (string name in names.OrderBy(n => n, StringComparer.Ordinal))
    {
        Check(Resolves($"Assets/Icons/{name}.png"), $"IconLoader chiede '{name}' e l'asset c'e'");
        literals++;
    }

    // E l'host di ogni URI avares: SCRITTO A MANO deve nominare un assembly che esiste
    // davvero — questo e' il difetto del M224 come regola, non come aneddoto. Un host di
    // un pacchetto (AvaloniaEdit ha le proprie risorse) e' legittimo: cio' che non lo e'
    // e' un nome che non e' nessun assembly.
    foreach (string host in literalHosts.OrderBy(h => h, StringComparer.Ordinal))
    {
        bool loadable = string.Equals(host, assemblyName, StringComparison.Ordinal);
        if (!loadable)
        {
            try
            {
                loadable = Assembly.Load(new AssemblyName(host)) is not null;
            }
            catch (Exception)
            {
                loadable = false;
            }
        }

        Check(loadable, $"l'host avares: '{host}' scritto nel codice nomina un assembly esistente");
        hosts++;
    }
}

// ---------------------------------------------------------------- 6. i casi negativi
//
// Sempre in esecuzione: sono la prova che le asserzioni sopra possono fallire.
bool wrongHost;
try
{
    wrongHost = loader.Exists(new Uri("avares://GitExtensions.Avalonia/Assets/Icons/GitNext.png"));
}
catch (Exception)
{
    // Un host che non nomina nessun assembly caricato: assente o eccezione, mai "presente".
    wrongHost = false;
}

Check(!wrongHost, "un host che non e' un assembly caricato NON risolve (era il difetto del M224)");
Check(!Resolves("Assets/Icons/QuestaIconaNonEsiste.png"), "un nome inventato NON risolve");
Check(!Resolves("Assets/Help/QuestoDiagrammaNonEsiste.png"), "un diagramma inventato NON risolve");

// ---------------------------------------------------------------- esito
if (failures > 0)
{
    Console.WriteLine($"FAILED: {failures} di {checks} asserzioni");
    return 1;
}

Console.WriteLine(
    $"PASS: {checks} asset-name cases — base {expectedBase}, {diagrams.Distinct(StringComparer.Ordinal).Count()} help diagrams, "
    + $"{classicChecked} classic bitmaps, {literals} names read from the source, {hosts} avares: hosts, "
    + $"{icons_count} icons embedded, 3 negative cases");
return 0;
