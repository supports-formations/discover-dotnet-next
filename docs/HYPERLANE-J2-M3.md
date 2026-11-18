# HYPERLANE — JOUR 2 · MODULE 3 : Async / Await + gestion d'erreurs

> **Ce fichier remplace toutes les versions antérieures du module 3.**

## Ce que le module enseigne

Une progression en cinq temps. Chaque notion s'appuie sur la précédente, et chaque exemple tient en quelques lignes.

| § | Notion | La question à laquelle on répond |
|---|---|---|
| 3.1 | `Task` et `async`/`await` | qu'est-ce qu'une `Task`, et que fait le compilateur derrière `await` ? |
| 3.2 | Gagner du temps | pourquoi mon code asynchrone n'est-il pas plus rapide ? |
| 3.3 | `yield` et `ValueTask` | comment rendre la main, et comment éviter une promesse inutile ? |
| 3.4 | `ConfigureAwait`, `WhenAll`, `WhenEach` | où reprend mon code, et comment composer plusieurs opérations ? |
| 3.5 | Annulation, `await using`, erreurs | comment arrêter, libérer, et ne pas perdre une exception ? |

**Format de ce module** : il diffère des modules 1 et 2. L'enjeu ici est de **comprendre le mécanisme**, pas de mesurer une optimisation. Les mesures sont conservées uniquement là où elles font comprendre quelque chose — le gain de temps réel en 3.2, les allocations en 3.3.

**Hors périmètre, et où c'est traité** : `Parallel.ForEachAsync`, `Channel<T>`, verrous et parallélisme borné → **module 8** (multithreading). Retry et circuit breaker → **module 8.9**.

---

## 3.1 — La `Task` : une promesse d'exécution

### Le point de départ

Presque tout le monde sait écrire `await quelqueChose()`. Beaucoup moins de gens savent répondre à : **une `Task`, c'est quoi exactement ?**

Sans cette réponse, tout le reste du module est du rituel.

---

### Une `Task` est une promesse, pas un travail

```csharp
// Ceci N'EST PAS « lancer un travail »
Task<int> promesse = CalculerProductionAsync();

// C'est : « voici un objet qui contiendra un int, plus tard »
```

Une `Task<T>` est un **objet qui représente une opération en cours ou terminée**, et qui contiendra à terme :

- soit un résultat de type `T`,
- soit une exception,
- soit une annulation.

Trois propriétés décrivent son état, et elles s'excluent :

```csharp
Task<int> t = CalculerProductionAsync();

t.IsCompleted;              // terminée, d'une façon ou d'une autre
t.IsCompletedSuccessfully;  // terminée avec un résultat
t.IsFaulted;                // terminée avec une exception
t.IsCanceled;               // annulée — ce n'est PAS un IsFaulted
```

> **Point à faire dire à la salle** : une tâche annulée n'est pas une tâche en erreur. C'est un troisième état.

---

### La promesse est déjà lancée

C'est la confusion la plus fréquente. Comparons deux lignes :

```csharp
// A — la tâche DÉMARRE ici. La promesse existe déjà.
Task<int> promesse = CalculerProductionAsync();

// B — rien ne démarre. On a juste une recette.
Func<Task<int>> recette = () => CalculerProductionAsync();
```

En A, l'opération est **en cours** avant même qu'on écrive `await`. `await` ne démarre rien : il **attend** une promesse déjà lancée.

La démonstration qui fait tilt :

```csharp
Console.WriteLine("avant");

Task t = DireBonjourAsync();      // <- s'exécute JUSQU'AU premier await interne

Console.WriteLine("après création");

await t;

Console.WriteLine("après await");


static async Task DireBonjourAsync()
{
    Console.WriteLine("  début de la méthode");
    await Task.Delay(100);
    Console.WriteLine("  fin de la méthode");
}
```

Sortie :

```
avant
  début de la méthode        <- exécuté à l'APPEL, pas à l'await
après création
  fin de la méthode
après await
```

**Une méthode `async` s'exécute de façon synchrone jusqu'au premier `await` qui porte sur une opération non terminée.** À ce moment-là seulement, elle rend la main à son appelant.

---

### Ce qu'`await` fait vraiment

```csharp
var planete = await _store.FindAsync(code);
Console.WriteLine(planete.Name);
```

Ce que le compilateur produit, en substance :

```csharp
var awaiter = _store.FindAsync(code).GetAwaiter();

if (awaiter.IsCompleted)
{
    // Chemin rapide : le résultat est déjà là, on continue SANS rien suspendre
    var planete = awaiter.GetResult();
    Console.WriteLine(planete.Name);
}
else
{
    // Chemin lent : on enregistre « ce qu'il faudra faire ensuite » et on rend la main
    awaiter.OnCompleted(() =>
    {
        var planete = awaiter.GetResult();
        Console.WriteLine(planete.Name);
    });

    return;     // <- la méthode rend la main à son appelant, ICI
}
```

Trois enseignements immédiats :

**1. `await` teste d'abord si c'est déjà fini.** Si oui, aucune suspension, aucun coût. C'est pour ça qu'un cache chaud ne paie presque rien.

**2. Le code après `await` est une continuation.** Ce n'est plus la suite linéaire d'une méthode : c'est un morceau de code enregistré, exécuté plus tard.

**3. La méthode retourne au milieu d'elle-même.** L'appelant reprend la main alors que l'opération n'est pas finie.

---

### `async` n'est pas du multithreading

Le point le plus important du sous-module, et le plus mal compris.

```csharp
// Combien de threads sont créés ici ?
async Task LireAsync()
{
    var contenu = await File.ReadAllTextAsync("galaxie.csv");
    Console.WriteLine(contenu.Length);
}
```

**Zéro.** Pendant la lecture du fichier, aucun thread n'attend : le système d'exploitation prévient quand les données sont prêtes, et la continuation est alors exécutée — éventuellement sur le même thread qu'avant.

| | Asynchrone | Multithreading |
|---|---|---|
| Sert à | ne pas immobiliser un thread pendant une **attente** | faire plusieurs **calculs** en même temps |
| Cas type | I/O : fichier, réseau, base | CPU : calcul, tri, compression |
| Threads utilisés | souvent aucun de plus | un par unité de travail |

Corollaire immédiat :

```csharp
// ❌ N'apporte RIEN : l'I/O n'occupait déjà aucun thread
var contenu = await Task.Run(() => File.ReadAllTextAsync("galaxie.csv"));

// ✅
var contenu = await File.ReadAllTextAsync("galaxie.csv");
```

`Task.Run` a un seul usage légitime : déplacer un travail **CPU** hors du thread courant.

```csharp
// ✅ Là, c'est justifié : un vrai calcul qui bloquerait l'appelant
var resultat = await Task.Run(() => SimulerDixMilleCombats());
```

---

### Bien manipuler une `Task` : les cinq règles

**Règle 1 — ne jamais bloquer une `Task`.**

```csharp
// ❌ Bloque le thread pendant toute l'opération
var planete = _store.FindAsync(code).Result;
_store.SauvegarderAsync(planete).Wait();

// ✅
var planete = await _store.FindAsync(code);
await _store.SauvegarderAsync(planete);
```

Bloquer, c'est immobiliser un thread pour attendre — exactement ce que l'asynchrone évitait. Sur un serveur, les threads du pool s'épuisent et tout ralentit sans qu'aucune exception n'apparaisse.

**Règle 2 — ne jamais abandonner une `Task`.**

```csharp
// ❌ Si ça lève, personne ne le saura jamais
_ = _journal.EcrireAsync(evenement);

// ✅
await _journal.EcrireAsync(evenement);
```

Une exception dans une tâche abandonnée reste stockée dans l'objet `Task`. Si personne ne l'attend, elle **disparaît** (détail en 3.5).

**Règle 3 — une `Task` s'attend plusieurs fois, sans risque.**

```csharp
Task<Planet> t = _store.FindAsync(code);

var a = await t;
var b = await t;     // ✅ légal : le résultat est mis en cache dans la Task
```

Ce sera faux pour `ValueTask` — on y vient en 3.3.

**Règle 4 — suffixer `Async`, et retourner `Task`, pas `void`.**

```csharp
// ❌ Impossible à attendre, exception non capturable
async void SauvegarderAsync() { await _store.SaveAsync(); }

// ✅
async Task SauvegarderAsync() { await _store.SaveAsync(); }
```

`async void` n'existe que pour les gestionnaires d'événements. Partout ailleurs, c'est une faute.

**Règle 5 — produire une `Task` déjà terminée quand on n'a rien à attendre.**

```csharp
// Implémentation synchrone d'une interface asynchrone
public Task<Planet?> FindAsync(PlanetCode code)
{
    _cache.TryGetValue(code, out var planete);
    return Task.FromResult(planete);            // aucune machine à états générée
}

// Variantes utiles
Task.CompletedTask;                             // Task sans résultat
Task.FromException<Planet>(new DomainException("..."));
Task.FromCanceled<Planet>(ct);
```

---

### 🧪 Micro-atelier — prédire la sortie

Sans exécuter, écris la sortie de ce programme :

```csharp
Console.WriteLine("1");

Task t = TravaillerAsync();

Console.WriteLine("2");

await t;

Console.WriteLine("3");


static async Task TravaillerAsync()
{
    Console.WriteLine("A");
    await Task.Delay(50);
    Console.WriteLine("B");
}
```

Puis modifie une seule chose — remplacer `Task.Delay(50)` par `Task.CompletedTask` — et prédis la nouvelle sortie.

---

### ✅ Correction

**Version 1**, avec `Task.Delay(50)` :

```
1
A        <- la méthode s'exécute jusqu'au premier await incomplet
2        <- elle a rendu la main, l'appelant continue
B
3
```

**Version 2**, avec `await Task.CompletedTask` :

```
1
A
B        <- l'await porte sur une tâche DÉJÀ TERMINÉE : aucune suspension
2
3
```

**Ce que ça démontre** : `await` ne suspend que si nécessaire. Sur une tâche déjà terminée, l'exécution continue en ligne droite, de façon parfaitement synchrone. Le mot-clé `await` ne signifie pas « attendre » — il signifie « **attendre si besoin** ».

C'est cette propriété qui rendra `ValueTask` intéressant en 3.3.

---

### 🎯 Point à retenir

Une `Task` est une **promesse déjà lancée**, pas un travail à démarrer. `await` teste d'abord si elle est terminée, et ne suspend que sinon. Et `async` ne crée aucun thread : il évite d'en immobiliser un pendant une attente.

### ⚠️ Les cinq fautes

- `.Result` / `.Wait()` : on immobilise le thread qu'on voulait libérer.
- `_ = TacheAsync();` : l'exception disparaît.
- `async void` hors gestionnaire d'événement : exception non capturable.
- `Task.Run` autour d'une I/O déjà asynchrone : aucun gain, une allocation.
- Croire qu'`await` démarre l'opération.

---

## 3.2 — Gagner du temps : l'exemple du petit-déjeuner

### Le point de départ

Un développeur convertit tout son code en `async`, mesure, et constate que **rien n'est plus rapide**. C'est normal : l'asynchrone ne rend rien plus rapide par lui-même. Il rend possible de **ne pas attendre inutilement** — encore faut-il l'écrire.

L'exemple canonique est celui de la documentation Microsoft : préparer un petit-déjeuner. On le transpose au jeu.

---

### Le scénario : préparer un convoi commercial

Quatre opérations, avec leurs durées :

| Opération | Durée | Dépend de |
|---|---:|---|
| Charger le minerai | 3 s | — |
| Charger les vivres | 2 s | — |
| Calculer la route | 4 s | — |
| Réserver le créneau de saut | 1 s | la route |

---

### 📉 Version 1 — synchrone

```csharp
public TradeRun PreparerConvoi()
{
    var minerai = ChargerMinerai();          // 3 s
    var vivres = ChargerVivres();            // 2 s
    var route = CalculerRoute();             // 4 s
    var creneau = ReserverCreneau(route);    // 1 s

    return new TradeRun(minerai, vivres, route, creneau);
}
// Total : 10 secondes
```

Rien à dire : chaque opération attend la précédente. Dix secondes.

---

### 📉 Version 2 — `async` partout, et **toujours 10 secondes**

```csharp
public async Task<TradeRun> PreparerConvoiAsync()
{
    var minerai = await ChargerMineraiAsync();          // 3 s
    var vivres = await ChargerVivresAsync();            // 2 s
    var route = await CalculerRouteAsync();             // 4 s
    var creneau = await ReserverCreneauAsync(route);    // 1 s

    return new TradeRun(minerai, vivres, route, creneau);
}
// Total : toujours 10 secondes
```

**C'est le moment clé du sous-module.** Le code est devenu asynchrone, il ne bloque plus de thread — et il ne va **pas plus vite**.

Pourquoi ? Parce qu'`await` signifie « je m'arrête ici jusqu'à ce que ce soit fini ». Écrire quatre `await` d'affilée, c'est demander quatre attentes successives.

> Le gain de l'asynchrone version 2 est réel, mais il est **pour le serveur**, pas pour l'utilisateur : pendant les 10 secondes, aucun thread n'est immobilisé. Avec 200 joueurs simultanés, la version 1 mobilise 200 threads, la version 2 zéro.

---

### 📈 Version 3 — démarrer d'abord, attendre ensuite

```csharp
public async Task<TradeRun> PreparerConvoiAsync()
{
    // On LANCE les trois opérations indépendantes. Elles démarrent ICI.
    Task<Cargo> tacheMinerai = ChargerMineraiAsync();
    Task<Cargo> tacheVivres = ChargerVivresAsync();
    Task<Route> tacheRoute = CalculerRouteAsync();

    // On attend celles dont on a besoin, quand on en a besoin
    var route = await tacheRoute;                        // 4 s
    var creneau = await ReserverCreneauAsync(route);     // +1 s

    var minerai = await tacheMinerai;                    // déjà fini (3 s < 4 s)
    var vivres = await tacheVivres;                      // déjà fini (2 s < 4 s)

    return new TradeRun(minerai, vivres, route, creneau);
}
// Total : 5 secondes
```

**Dix secondes → cinq secondes.** Et une seule chose a changé : les trois appels indépendants sont **lancés avant d'être attendus**.

---

### Ce qui s'est passé, sur une ligne de temps

```
Version 2 — un await après l'autre
  0s    3s    5s         9s   10s
  |-----|-----|----------|----|
  minerai vivres   route    créneau

Version 3 — lancées ensemble, attendues ensuite
  0s              4s   5s
  |---------------|----|
  minerai  ███              (fini à 3s, on l'attendra plus tard)
  vivres   ██               (fini à 2s)
  route    ████████
  créneau            ██     (dépend de route : forcément après)
```

La durée totale n'est plus la **somme** des durées, mais la durée du **chemin le plus long** : route (4 s) puis créneau (1 s).

---

### La règle, en une phrase

> **`await` au plus tard.** On lance tout ce qui est indépendant, on n'attend qu'au moment où on a réellement besoin du résultat.

Le test à s'appliquer devant deux `await` consécutifs :

```csharp
var a = await OperationA();
var b = await OperationB();
```

**« B a-t-elle besoin de A ? »**

- Oui → l'ordre est correct, on ne peut rien faire.
- Non → on perd du temps. Il faut lancer les deux avant d'attendre.

---

### Deux formulations équivalentes

```csharp
// Forme 1 — lancer, puis attendre individuellement
var t1 = ChargerMineraiAsync();
var t2 = ChargerVivresAsync();

var minerai = await t1;
var vivres = await t2;

// Forme 2 — Task.WhenAll : plus lisible quand on attend tout au même endroit
var t1 = ChargerMineraiAsync();
var t2 = ChargerVivresAsync();

await Task.WhenAll(t1, t2);

var minerai = t1.Result;      // sûr : WhenAll garantit que c'est terminé
var vivres = t2.Result;
```

> `.Result` est interdit partout **sauf ici** : après un `WhenAll`, la tâche est terminée, `.Result` ne bloque rien. C'est le seul usage légitime, et il mérite un commentaire dans le code.

`Task.WhenAll` sera détaillé en 3.4 — ici on ne retient que le principe.

---

### 🔬 La mesure

C'est le seul endroit du module où une mesure de **temps réel** fait comprendre quelque chose.

```csharp
[SimpleJob(RuntimeMoniker.Net10_0, warmupCount: 1, iterationCount: 3)]
public class PetitDejeunerBenchmarks
{
    // Durées réduites d'un facteur 100 pour que le benchmark tienne
    private static Task<int> ChargerMineraiAsync() => Attendre(30);
    private static Task<int> ChargerVivresAsync() => Attendre(20);
    private static Task<int> CalculerRouteAsync() => Attendre(40);
    private static Task<int> ReserverCreneauAsync(int route) => Attendre(10);

    private static async Task<int> Attendre(int ms) { await Task.Delay(ms); return ms; }

    [Benchmark(Baseline = true, Description = "await l'un après l'autre")]
    public async Task<int> Sequentiel()
    {
        var minerai = await ChargerMineraiAsync();
        var vivres = await ChargerVivresAsync();
        var route = await CalculerRouteAsync();
        var creneau = await ReserverCreneauAsync(route);
        return minerai + vivres + route + creneau;
    }

    [Benchmark(Description = "lancer d'abord, attendre ensuite")]
    public async Task<int> Concurrent()
    {
        var tMinerai = ChargerMineraiAsync();
        var tVivres = ChargerVivresAsync();
        var tRoute = CalculerRouteAsync();

        var route = await tRoute;
        var creneau = await ReserverCreneauAsync(route);

        return await tMinerai + await tVivres + route + creneau;
    }
}
```

**❓ La question, avant le tableau**

« Les deux versions sont asynchrones. Laquelle est la plus rapide, et de combien ? »

**Résultat**

| Méthode | Mean | Ratio |
|---|---:|---:|
| `await` l'un après l'autre | 101,4 ms | 1,00 |
| Lancer d'abord, attendre ensuite | **51,2 ms** | **0,50** |

Le total est bien `route + créneau` = 40 + 10 = 50 ms, et non `30 + 20 + 40 + 10` = 100 ms.

---

### Le piège symétrique : composer des opérations dépendantes

L'erreur inverse existe, et elle est plus grave — ce n'est pas une lenteur, c'est un **bug**.

```csharp
// ❌ Le créneau est réservé à partir d'une route qui n'existe pas encore
var tRoute = CalculerRouteAsync();
var tCreneau = ReserverCreneauAsync(tRoute.Result);   // .Result bloque, et c'est le moindre mal

await Task.WhenAll(tRoute, tCreneau);
```

Deux opérations dépendantes **doivent** être séquentielles. Le seul `await` qu'on ne peut pas supprimer est celui qui porte sur une dépendance réelle.

---

### 🧪 Micro-atelier — réordonner un tour de jeu

Voici un tour de jeu. Il met 12 secondes. Réduis-le au minimum théorique, sans rien changer d'autre que l'ordre et le placement des `await`.

```csharp
public async Task<TickResult> JouerTourAsync()
{
    var planetes = await ChargerPlanetesAsync();      // 3 s
    var flottes = await ChargerFlottesAsync();        // 2 s
    var prix = await ChargerPrixAsync();              // 4 s

    var production = await CalculerProductionAsync(planetes);   // 1 s, dépend de planetes
    var routes = await PlanifierRoutesAsync(flottes, prix);     // 2 s, dépend de flottes ET prix

    return new TickResult(production, routes);
}
```

Questions :
1. quelles opérations sont indépendantes ?
2. quel est le chemin le plus long ?
3. quelle durée minimale peut-on atteindre ?

---

### ✅ Correction

**1. Les dépendances**

| Opération | Dépend de |
|---|---|
| `ChargerPlanetes` (3 s) | — |
| `ChargerFlottes` (2 s) | — |
| `ChargerPrix` (4 s) | — |
| `CalculerProduction` (1 s) | planètes |
| `PlanifierRoutes` (2 s) | flottes **et** prix |

**2. Les deux chemins**

```
Chemin 1 : planètes (3 s) -> production (1 s)          = 4 s
Chemin 2 : max(flottes 2 s, prix 4 s) -> routes (2 s)  = 6 s
```

**3. Le minimum théorique est 6 secondes**, contre 12.

```csharp
public async Task<TickResult> JouerTourAsync()
{
    // Les trois chargements sont indépendants : on les lance ensemble
    var tPlanetes = ChargerPlanetesAsync();
    var tFlottes = ChargerFlottesAsync();
    var tPrix = ChargerPrixAsync();

    // Chemin 1 : dès que les planètes sont là, on calcule la production
    var tProduction = CalculerProductionApresAsync(tPlanetes);

    // Chemin 2 : les routes attendent flottes ET prix
    var tRoutes = PlanifierRoutesApresAsync(tFlottes, tPrix);

    // On attend les deux chemins, qui progressent en parallèle
    await Task.WhenAll(tProduction, tRoutes);

    return new TickResult(tProduction.Result, tRoutes.Result);


    static async Task<Production> CalculerProductionApresAsync(Task<Planet[]> tPlanetes)
        => await CalculerProductionAsync(await tPlanetes);

    static async Task<Route[]> PlanifierRoutesApresAsync(Task<Fleet[]> tFlottes, Task<Prices> tPrix)
        => await PlanifierRoutesAsync(await tFlottes, await tPrix);
}
// Total : 6 secondes
```

**Le point de méthode** : les deux fonctions locales encapsulent « attendre les dépendances, puis faire le travail ». C'est ce qui permet de composer deux **chaînes** indépendantes et de les attendre ensemble.

**L'erreur typique** : attendre les trois chargements d'un coup avec un `WhenAll`, puis enchaîner.

```csharp
// ❌ 4 s + 1 s + 2 s = 7 secondes, pas 6
await Task.WhenAll(tPlanetes, tFlottes, tPrix);       // 4 s (le plus long)
var production = await CalculerProductionAsync(tPlanetes.Result);   // +1 s
var routes = await PlanifierRoutesAsync(tFlottes.Result, tPrix.Result);  // +2 s
```

Ça paraît correct, et ça coûte une seconde de plus : la production n'avait pas besoin d'attendre les prix. **Un `WhenAll` trop large re-synchronise des chemins qui auraient pu avancer séparément.**

---

### 🎯 Point à retenir

L'asynchrone ne rend rien plus rapide. Ce qui accélère, c'est de **lancer d'abord et d'attendre au plus tard**. La durée n'est alors plus la somme des opérations, mais la longueur du **chemin le plus long**.

Et un `WhenAll` trop large est aussi coûteux qu'un `await` de trop.

### 👍 Bonnes pratiques

- Devant deux `await` consécutifs, se demander : le second dépend-il du premier ?
- Lancer les opérations indépendantes **avant** la première attente.
- Dessiner les dépendances avant d'optimiser : le gain se lit sur le graphe, pas dans le code.
- `WhenAll` au plus près de ce qui est réellement nécessaire.

### ⚠️ Points de difficulté

- Convertir en `async` sans réordonner ne gagne **rien** en temps d'exécution.
- Un `WhenAll` trop large re-synchronise des chemins indépendants.
- Lancer en parallèle deux opérations dépendantes est un **bug**, pas une optimisation.
- `.Result` n'est acceptable qu'après un `WhenAll` qui garantit la complétion.

### ❌ Erreurs à ne pas faire

- Enchaîner des `await` sans se demander si les opérations sont liées.
- Utiliser `Task.Run` pour « paralléliser » des I/O.
- Croire que le gain de la version 2 est nul : il est réel, mais il est pour le serveur.
