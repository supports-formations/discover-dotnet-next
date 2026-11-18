# HYPERLANE — JOUR 1 · MODULE 2 : LINQ & expressions lambda avancées

> Corpus définitif. Fait suite à `HYPERLANE-J1-M1.md` (socle + module 1).
> **Programme officiel** — Jour 1, deuxième module.
> **Ce que le module construit** : le planificateur de routes commerciales et le tableau de bord de l'empire.

**Prérequis** : les six briques du module 1 (`CargoHold`, `HoloNetDecoder`, `PlanetWarehouse`, `FactionTreasury`, `GameRuleExtensions`, `PlanetMarket`) compilent et leurs benchmarks sont au vert.

---

## 2.1 — Lambdas et closures : capture, coût et `static`

### 🎮 Le problème de jeu

Le planificateur de routes filtre la galaxie à chaque tick : « quelles planètes sont atteignables avec mon carburant, appartiennent à une faction non hostile, et ont un stock supérieur à un seuil ? ». Les critères viennent de variables locales — donc les lambdas les **capturent**.

Sur 10 000 planètes et six ticks par minute, ces captures représentent une allocation par appel. Invisible en lecture, mesurable au profileur.

---

### 📉 Étape 1 — Le code sans la nouveauté

```csharp
public sealed class RoutePlannerDepart
{
    private readonly PlanetMarket _market;

    public RoutePlannerDepart(PlanetMarket market) => _market = market;

    public List<Planet> Candidats(
        Planet[] galaxie, Fleet flotte, long carburant, FactionCode joueur, long seuilStock)
    {
        var resultat = new List<Planet>();

        foreach (var origine in galaxie)
        {
            // (1) Quatre variables capturées + `this` (via _market)
            //     -> une classe de closure PAR ITÉRATION de la boucle externe
            var atteignables = galaxie.Where(p =>
                p.Code != origine.Code
                && flotte.JumpCost(origine, p) <= carburant
                && p.Owner != joueur
                && _market.StockOf(Resource.Minerai) >= seuilStock);

            resultat.AddRange(atteignables);
        }

        // (2) Lambda sans capture — mais est-elle gratuite ?
        resultat.Sort((a, b) => a.Population.CompareTo(b.Population));

        // (3) Lambda capturant seulement `this`
        return resultat.Where(p => EstInteressante(p)).ToList();
    }

    private bool EstInteressante(Planet p) => p.Population > 100_000;
}
```

**Ce que ça coûte** : la lambda du point (1) capture `origine`, `flotte`, `carburant`, `joueur`, `seuilStock` **et** `this`. Le compilateur crée une **classe de display** contenant ces six champs, l'instancie à chaque itération de la boucle externe, et crée un délégué qui la référence. Sur 2 000 planètes : 4 000 objets alloués, avant même de compter les itérateurs LINQ.

---

### 📈 Étape 2 — Le même code avec la nouveauté

```csharp
// Le critère devient un TYPE : nommé, testable, et sans capture
public readonly record struct RouteCriteria(
    Fleet Flotte, long Carburant, FactionCode Joueur, long SeuilStock)
{
    public bool Accepte(Planet origine, Planet cible, PlanetMarket market) =>
        cible.Code != origine.Code
        && Flotte.JumpCost(origine, cible) <= Carburant       // extension du module 1.4
        && cible.Owner != Joueur
        && market.StockOf(Resource.Minerai) >= SeuilStock;
}

public sealed class RoutePlanner(PlanetMarket market)
{
    // (2') Délégué de tri mis en cache : alloué UNE fois pour la vie du processus
    private static readonly Comparison<Planet> ParPopulation =
        static (a, b) => a.Population.CompareTo(b.Population);

    // (3') Prédicat sans capture : `static` garantit l'absence de capture accidentelle
    private static readonly Func<Planet, bool> EstInteressante =
        static p => p.Population > 100_000;

    public List<Planet> Candidats(
        Planet[] galaxie, Fleet flotte, long carburant, FactionCode joueur, long seuilStock)
    {
        // (1') Les critères sont regroupés dans un struct : AUCUNE capture
        var criteres = new RouteCriteria(flotte, carburant, joueur, seuilStock);

        var resultat = new List<Planet>(galaxie.Length);      // capacité anticipée

        foreach (var origine in galaxie)
            foreach (var cible in galaxie)
                if (criteres.Accepte(origine, cible, market)) resultat.Add(cible);

        resultat.Sort(ParPopulation);

        // Le délégué est mis en cache : seul l'itérateur Where est alloué
        return [.. resultat.Where(EstInteressante)];
    }
}
```

---

### 🔬 Étape 3 — La mesure

Deux benchmarks : le coût **isolé** d'une capture, puis le planificateur complet.

```csharp
// bench/Hyperlane.Benchmarks/Day1/Module02Benchmarks.cs

// --- Benchmark 1 : le coût d'UNE capture, sans le bruit du reste ---
[MemoryDiagnoser]
public class ClosureAllocationBenchmarks
{
    private readonly Planet[] _galaxie = GalaxyBuilder.Build(100);
    private long _seuil = 100_000;

    [Benchmark(Baseline = true, Description = "Lambda capturant une variable locale")]
    public int AvecCaptureLocale()
    {
        var seuil = _seuil;                                   // capture d'une locale
        return _galaxie.Count(p => p.Population > seuil);
    }

    [Benchmark(Description = "Lambda capturant this")]
    public int CaptureThis() => _galaxie.Count(p => p.Population > _seuil);

    [Benchmark(Description = "Lambda static, sans capture")]
    public int SansCapture() => _galaxie.Count(static p => p.Population > 100_000);
}

// --- Benchmark 2 : le planificateur complet ---
[MemoryDiagnoser]
public class RoutePlannerBenchmarks
{
    private Planet[] _galaxie = null!;
    private Fleet _flotte = null!;
    private PlanetMarket _market = null!;
    private RoutePlannerDepart _depart = null!;
    private RoutePlanner _moderne = null!;

    [Params(500, 2_000)]              // O(n²) : on reste raisonnable sur les tailles
    public int Planetes { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _galaxie = GalaxyBuilder.Build(Planetes);
        _flotte = FleetBuilder.Many(1, shipsPerFleet: 5)[0];
        _market = new PlanetMarket(50_000, 0);
        _depart = new RoutePlannerDepart(_market);
        _moderne = new RoutePlanner(_market);
    }

    [Benchmark(Baseline = true, Description = "Lambdas capturantes")]
    public int Depart() => _depart.Candidats(_galaxie, _flotte, 5_000, FactionCode.CSL, 100).Count;

    [Benchmark(Description = "Critères en struct + délégués cachés")]
    public int Moderne() => _moderne.Candidats(_galaxie, _flotte, 5_000, FactionCode.CSL, 100).Count;
}
```

**❓ Les deux questions**

Avant le premier tableau : « une lambda **sans** capture alloue-t-elle un délégué à chaque appel ? »
Puis : « et une lambda qui capture seulement `this` ? »

**Résultat**

| Méthode (capture isolée) | Mean | Ratio | Allocated |
|---|---:|---:|---:|
| Lambda capturant une locale | 412 ns | 1,00 | **96 B** |
| Lambda capturant `this` | 398 ns | 0,97 | **64 B** |
| Lambda `static`, sans capture | 384 ns | 0,93 | **0 B** |

| Méthode (planificateur) | Planetes | Mean | Ratio | Allocated |
|---|---:|---:|---:|---:|
| Lambdas capturantes | 2 000 | 41,2 ms | 1,00 | **8 420 KB** |
| Critères en struct | 2 000 | 12,8 ms | **0,31** | **1 180 KB** |

---

### 🧭 Étape 4 — Pourquoi, pas à pas

**Ce que génère une lambda capturante**

Pour `p => p.Population > seuil`, le compilateur produit :

```csharp
// Classe de display générée
[CompilerGenerated]
private sealed class <>c__DisplayClass0_0
{
    public long seuil;                                        // le champ capturé

    internal bool <AvecCaptureLocale>b__0(Planet p) => p.Population > seuil;
}

// Le code réécrit
var display = new <>c__DisplayClass0_0();                     // allocation 1 : 24 o + 8 o
display.seuil = _seuil;
return _galaxie.Count(new Func<Planet, bool>(display.<AvecCaptureLocale>b__0));  // allocation 2 : 64 o
```

**96 octets** = 32 (classe de display) + 64 (délégué). Le délégué doit être réalloué à chaque appel puisqu'il pointe vers une instance différente.

**Pour une capture de `this` uniquement**, pas de classe de display : la méthode générée est une méthode d'instance de la classe englobante, et seul le délégué est alloué. **64 octets.**

**Pour une lambda sans capture**, le compilateur applique un **cache statique** :

```csharp
[CompilerGenerated]
private sealed class <>c
{
    public static readonly <>c <>9 = new <>c();
    public static Func<Planet, bool> <>9__2_0;                // le délégué mis en cache

    internal bool <SansCapture>b__2_0(Planet p) => p.Population > 100_000;
}

// Le code réécrit
return _galaxie.Count(<>c.<>9__2_0 ??= new Func<Planet, bool>(<>c.<>9.<SansCapture>b__2_0));
```

Le délégué est créé au premier appel puis réutilisé. **0 octet** ensuite.

> **Ce que fait le mot-clé `static` sur une lambda** : il **interdit** toute capture. Il n'accélère rien par lui-même — il empêche d'en introduire une par accident lors d'une modification future. C'est un garde-fou de compilation, pas une optimisation.

**Décomposition des 8 420 Ko du planificateur, sur 2 000 planètes**

| Poste | Volume | Part |
|---|---:|---:|
| Classe de display + délégué, une par itération externe (2 000 × 96 o) | 192 Ko | 2,3 % |
| Itérateur `Where` par itération externe (2 000 × 88 o) | 176 Ko | 2,1 % |
| `AddRange` sur un `IEnumerable` : énumérateur + réallocations de la liste | 1 840 Ko | 21,8 % |
| `Where(...).ToList()` final : itérateur + délégué + buffer croissant + liste | 6 212 Ko | **73,8 %** |

Le poste dominant n'est **pas** la closure. C'est le `ToList()` final sur un `Where`, qui matérialise deux fois : une fois dans le buffer croissant de `ToList`, une fois dans la liste retournée.

**Pourquoi la version B ne descend qu'à 1 180 Ko et pas à zéro ?** Parce que la liste `resultat` **est** le résultat — environ 74 000 références sur 2 000 planètes. C'est la valeur produite, pas du déchet. Même leçon qu'au 1.2.

---

### 📖 Ce qu'il faut savoir en plus

**Les surcharges à état** : plusieurs API du BCL acceptent un argument d'état pour éviter la capture.

```csharp
// ❌ capture de `facteur` : une closure par appel
_cache.GetOrAdd(code, k => Calculer(k, facteur));

// ✅ l'état est passé en argument : lambda static, zéro allocation
_cache.GetOrAdd(code, static (k, f) => Calculer(k, f), facteur);

// Idem pour Task.Run, Timer, ThreadPool.QueueUserWorkItem, Lazy<T>
ThreadPool.QueueUserWorkItem(static state => Traiter(state), etat, preferLocal: true);
```

**LINQ n'a aucune de ces surcharges.** `Where`, `Select`, `OrderBy` ne prennent pas d'état. C'est structurel : dès qu'un critère LINQ dépend d'une variable, il y a capture. D'où la solution du sous-module — remplacer la lambda par un **type de critère**.

**Le piège de la capture en boucle** : la variable d'un `for` est partagée entre les itérations ; celle d'un `foreach` ne l'est plus depuis C# 5.

```csharp
var actions = new List<Action>();

for (var i = 0; i < 3; i++) actions.Add(() => Console.WriteLine(i));
actions.ForEach(a => a());          // affiche 3, 3, 3 — une SEULE closure partagée

foreach (var p in galaxie) actions.Add(() => Console.WriteLine(p.Code));
                                    // affiche les bons codes — une closure PAR itération
```

**Le coût caché de la capture** : une closure retient tous les objets capturés. Une lambda capturant `this`, stockée dans un cache, maintient l'objet englobant **et tout son graphe** en vie. C'est l'une des fuites mémoire les plus fréquentes en .NET, et elle est invisible en relecture.

---

### 🧪 Atelier — le filtre de flottes du tableau de bord

**Le contexte** : le client de jeu demande la liste des flottes correspondant à des critères choisis par le joueur. La méthode est appelée à chaque rafraîchissement, cinq fois par seconde.

**Le code de départ, fourni** :

```csharp
// exercises/Day1/Ex06_FleetFilter/Depart.cs
public sealed record FleetSummary(Guid Id, string Name, int ShipCount, int Firepower, PlanetCode Location);

public sealed class FleetFilterDepart
{
    public List<FleetSummary> Filtrer(
        Fleet[] flottes, FactionCode joueur, int puissanceMin, int capaciteMin, bool seulementPretes)
    {
        return flottes
            .Where(f => f.Owner == joueur)
            .Where(f => f.Firepower >= puissanceMin)
            .Where(f => f.CargoCapacity >= capaciteMin)
            .Where(f => !seulementPretes || f.Status == FleetStatus.EnOrbite)
            .OrderByDescending(f => f.Firepower)
            .Select(f => new FleetSummary(f.Id, f.Name, f.Ships.Count, f.Firepower, f.Location))
            .ToList();
    }
}
```

**⚠️ Le piège est dans le socle** : `Fleet.Firepower` et `Fleet.CargoCapacity` sont des **propriétés calculées** — chacune parcourt la liste des vaisseaux. Cette chaîne les appelle **trois fois** par flotte (deux filtres, un tri, une projection).

**Ta mission**

1. remplacer les quatre `Where` capturants par un **type de critère** sans allocation ;
2. supprimer les recalculs : `Firepower` et `CargoCapacity` ne doivent être évalués **qu'une fois par flotte** ;
3. les lambdas restantes doivent être `static` ou mises en cache ;
4. conserver strictement le même résultat, **ordre inclus** ;
5. identifier en commentaire le poste d'allocation dominant, et dire si tu l'as traité.

**🎯 Objectif chiffré** (10 000 flottes de 5 vaisseaux)

| Benchmark | Départ | À atteindre |
|---|---:|---:|
| Mean | 8 420 µs | **< 1 800 µs** |
| Allocated | 12 400 KB | **< 3 000 KB** |

---

### ✅ Correction

```csharp
// (1) Le critère devient un type : nommé, testable, sans capture
public readonly record struct FleetCriteria(
    FactionCode Joueur, int PuissanceMin, int CapaciteMin, bool SeulementPretes)
{
    // Les grandeurs déjà calculées sont PASSÉES en argument : aucun recalcul ici
    public bool Accepte(Fleet flotte, int puissance, int capacite) =>
        flotte.Owner == Joueur
        && puissance >= PuissanceMin
        && capacite >= CapaciteMin
        && (!SeulementPretes || flotte.Status == FleetStatus.EnOrbite);
}

public sealed class FleetFilter
{
    // (3) Comparateur mis en cache, static : alloué une fois pour la vie du processus
    private static readonly Comparison<FleetSummary> ParPuissanceDecroissante =
        static (a, b) => b.Firepower.CompareTo(a.Firepower);

    public List<FleetSummary> Filtrer(
        Fleet[] flottes, FactionCode joueur, int puissanceMin, int capaciteMin, bool seulementPretes)
    {
        var criteres = new FleetCriteria(joueur, puissanceMin, capaciteMin, seulementPretes);

        // Capacité anticipée : on évite les réallocations successives
        var resultat = new List<FleetSummary>(flottes.Length / 4);

        foreach (var flotte in flottes)
        {
            // (2) UNE seule évaluation de chaque propriété calculée, réutilisée partout
            var puissance = flotte.Firepower;
            var capacite = flotte.CargoCapacity;

            if (!criteres.Accepte(flotte, puissance, capacite)) continue;

            resultat.Add(new FleetSummary(
                flotte.Id, flotte.Name, flotte.Ships.Count, puissance, flotte.Location));
        }

        // (4) Tri sur la liste DÉJÀ projetée : la puissance n'est pas recalculée
        resultat.Sort(ParPuissanceDecroissante);
        return resultat;
    }
}

// (5) POSTE DOMINANT ET DÉCISION :
//
// Le poste dominant du départ était le RECALCUL de Fleet.Firepower et Fleet.CargoCapacity.
// Ce sont des propriétés calculées qui parcourent les 5 vaisseaux à chaque accès, et la
// chaîne LINQ les appelait 3 fois par flotte. Sur 10 000 flottes : 150 000 parcours au
// lieu de 20 000, plus 40 000 délégués — soit ~7 900 KB sur les 12 400 KB, c'est-à-dire
// 64 % du problème.
//
// TRAITÉ : les deux grandeurs sont évaluées une fois, stockées en local, réutilisées.
//
// NON TRAITÉ, volontairement :
// - La List<FleetSummary> retournée (~2 400 KB) : c'est le RÉSULTAT, pas un intermédiaire.
// - Les FleetSummary eux-mêmes : ce sont des records, donc une allocation par élément
//   retenu. Les passer en struct ferait gagner ~800 KB mais changerait le contrat public
//   de la méthode. À réévaluer au module 7 si l'API les sérialise directement.
```

---

### 🔬 Le benchmark de l'atelier

```csharp
[MemoryDiagnoser]
public class Ex06_FleetFilterBenchmarks
{
    private Fleet[] _flottes = null!;
    private FleetFilterDepart _depart = null!;
    private FleetFilter _votre = null!;

    [GlobalSetup]
    public void Setup()
    {
        _flottes = FleetBuilder.Many(10_000, shipsPerFleet: 5);
        _depart = new FleetFilterDepart();
        _votre = new FleetFilter();
    }

    [Benchmark(Baseline = true, Description = "Départ (fourni)")]
    public int Depart() => _depart.Filtrer(_flottes, FactionCode.CSL, 100, 200, true).Count;

    [Benchmark(Description = "Votre solution")]
    public int Votre() => _votre.Filtrer(_flottes, FactionCode.CSL, 100, 200, true).Count;
}
```

Et le test qui garantit la contrainte 4 :

```csharp
[Fact]
public void Le_resultat_est_strictement_identique_au_depart()
{
    var flottes = FleetBuilder.Many(1_000, shipsPerFleet: 5);

    var attendu = new FleetFilterDepart().Filtrer(flottes, FactionCode.CSL, 100, 200, true);
    var obtenu = new FleetFilter().Filtrer(flottes, FactionCode.CSL, 100, 200, true);

    Assert.Equal(attendu.Select(f => f.Id), obtenu.Select(f => f.Id));   // ordre inclus
}
```

**Résultat attendu et barème**

| Méthode | Mean | Ratio | Allocated | Verdict |
|---|---:|---:|---:|---|
| `Depart` | 8 420 µs | 1,00 | 12 400 KB | — |
| `Votre` | **1 240 µs** | 0,15 | **2 480 KB** | ✅ |

**Les erreurs typiques**

| Ce que le stagiaire a fait | Symptôme |
|---|---|
| Fusionné les 4 `Where` en un seul, sans stocker `Firepower` | Mean autour de 6 900 µs — 18 % de gain sur 64 % du problème |
| Ajouté `static` aux lambdas sans supprimer les captures | ne compile pas — elles capturent `puissanceMin` |
| Trié **avant** de projeter | `Firepower` recalculé dans le comparateur : Mean autour de 3 400 µs |
| Utilisé `OrderByDescending` au lieu de `List.Sort` | +900 KB (tableau de clés + tableau d'index + liste) |
| Oublié la capacité initiale de la liste | +400 KB de réallocations |
| Changé l'ordre du résultat | test `Le_resultat_est_strictement_identique` en échec |

**Le piège n°1** : la première ligne. Fusionner les `Where` est le réflexe immédiat, il paraît productif, et il traite **2 %** du problème. Le triple recalcul de `Firepower` est invisible parce qu'il est **réparti sur trois lignes différentes** de la chaîne LINQ — et parce que `Firepower` *ressemble* à un champ alors que c'est une boucle.

---

### 🎯 Point à retenir

Une lambda sans capture est **gratuite** (délégué mis en cache). Une lambda capturant `this` coûte 64 octets par appel, une lambda capturant des locales 96. Mais le coût dominant d'une chaîne LINQ n'est presque jamais la closure : c'est le **recalcul réparti sur plusieurs maillons**, que la syntaxe rend invisible.

### 👍 Bonnes pratiques

- `static` sur toute lambda qui ne doit pas capturer : garde-fou de compilation.
- Délégués sans capture en `static readonly` quand ils sont réutilisés.
- Surcharges à état (`GetOrAdd(key, factory, arg)`) partout où elles existent.
- Un **type de critère** plutôt qu'une chaîne de `Where` capturants : nommé, testable, sans allocation.
- Évaluer une propriété calculée **une fois** et la stocker, surtout quand la chaîne LINQ y accède plusieurs fois.

### ⚠️ Points de difficulté

- La variable d'un `for` est partagée entre les itérations ; celle d'un `foreach` ne l'est pas.
- Une lambda capturant `this` stockée durablement maintient tout le graphe de l'objet en vie.
- `static` sur une lambda n'accélère rien par lui-même : il **interdit** la capture.
- LINQ n'a aucune surcharge à état : la capture y est structurelle.
- Une propriété calculée ressemble à un champ à la lecture : c'est ce qui rend le recalcul invisible.

### 🚀 Pour aller plus loin

**A. Prédicat générique en struct : dévirtualisé et inliné par le JIT**

```csharp
public interface IFleetPredicate { bool Accepte(Fleet fleet, int puissance, int capacite); }

public readonly struct CombatReadyPredicate(int seuil) : IFleetPredicate
{
    public bool Accepte(Fleet fleet, int puissance, int capacite) =>
        puissance >= seuil && fleet.Status == FleetStatus.EnOrbite;
}

// TPredicate est un struct : le JIT spécialise la méthode et inline l'appel
public static int Compter<TPredicate>(Fleet[] flottes, TPredicate predicat)
    where TPredicate : IFleetPredicate, allows ref struct
{
    var count = 0;
    foreach (var f in flottes)
        if (predicat.Accepte(f, f.Firepower, f.CargoCapacity)) count++;
    return count;
}
```

**B. Détecter les captures involontaires en CI**

```xml
<!-- À activer sur les projets du chemin chaud uniquement -->
<PackageReference Include="ClrHeapAllocationAnalyzer" Version="*" PrivateAssets="all" />
```

```ini
# .editorconfig
dotnet_diagnostic.HAA0301.severity = warning   # Closure allocation
dotnet_diagnostic.HAA0302.severity = warning   # Display class allocation
dotnet_diagnostic.HAA0603.severity = warning   # Delegate allocation from a method group
```

### ❌ Erreurs à ne pas faire

- Optimiser les closures avant d'avoir classé les postes d'allocation.
- Stocker durablement une lambda capturant `this` (fuite du graphe complet).
- Enchaîner plusieurs `Where` dont chacun réévalue la même propriété calculée.
- `OrderByDescending(...).ToList()` quand `List.Sort` suffit.
- Croire que `static` sur une lambda accélère quelque chose.

---

## 2.2 — `Func<T>` vs `Expression<Func<T>>` : arbres d'expression

### 🎮 Le problème de jeu

L'écran « Carte galactique » propose une recherche multicritère : type de planète, faction propriétaire, population minimale, préfixe de code, distance maximale. Chaque critère est optionnel.

Aujourd'hui la galaxie tient en mémoire. Au module 4, elle passera en base de données. **Le même code de filtre doit fonctionner dans les deux cas** — sinon il faudra tout réécrire.

---

### 📉 Étape 1 — Le code sans la nouveauté

```csharp
public sealed record GalaxyFilter(
    FactionCode? Owner,
    PlanetType? Type,
    long? MinPopulation,
    string? CodePrefix,
    Coordinates? Center,
    double? MaxDistance);

public sealed class GalaxySearchDepart
{
    // Le filtre est un Func : du code exécutable, rien de plus
    public Func<Planet, bool> ConstruireFiltre(GalaxyFilter filtre)
    {
        return planet =>
        {
            if (filtre.Owner is { } owner && planet.Owner != owner) return false;
            if (filtre.Type is { } type && planet.Type != type) return false;
            if (filtre.MinPopulation is { } min && planet.Population < min) return false;

            if (!string.IsNullOrEmpty(filtre.CodePrefix)
                && !planet.Code.Value.StartsWith(filtre.CodePrefix)) return false;

            if (filtre is { Center: { } centre, MaxDistance: { } distanceMax })
                if (planet.Position.DistanceTo(centre) > distanceMax) return false;

            return true;
        };
    }

    public Planet[] Rechercher(Planet[] galaxie, GalaxyFilter filtre) =>
        [.. galaxie.Where(ConstruireFiltre(filtre))];
}
```

**Ce que ça coûte** : rien, tant que la galaxie est en mémoire. Le problème apparaît au module 4 :

```csharp
// Ce code COMPILE et FONCTIONNE. Il charge aussi les 10 000 planètes en RAM.
var resultat = db.Planets.Where(searchService.ConstruireFiltre(filtre)).ToList();
```

`IQueryable<T>` hérite d'`IEnumerable<T>`. Passer un `Func` sélectionne la surcharge `Enumerable.Where`, qui **matérialise toute la table** avant de filtrer. Aucun avertissement, aucune erreur.

---

### 📈 Étape 2 — Le même code avec la nouveauté

```csharp
public static class GalaxyQueryBuilder
{
    // Le filtre est une Expression : un ARBRE DE DONNÉES décrivant le code
    public static Expression<Func<Planet, bool>> Construire(GalaxyFilter filtre)
    {
        var predicat = PredicateBuilder.True<Planet>();

        if (filtre.Owner is { } owner)
            predicat = predicat.And(p => p.Owner == owner);       // capture locale SCALAIRE

        if (filtre.Type is { } type)
            predicat = predicat.And(p => p.Type == type);

        if (filtre.MinPopulation is { } min)
            predicat = predicat.And(p => p.Population >= min);

        if (!string.IsNullOrWhiteSpace(filtre.CodePrefix))
        {
            var prefixe = filtre.CodePrefix;                      // le string, pas le record entier
            predicat = predicat.And(p => p.Code.Value.StartsWith(prefixe));
        }

        if (filtre is { Center: { } centre, MaxDistance: { } distanceMax })
        {
            // DistanceTo n'est pas traduisible : on écrit la formule à plat,
            // et on compare le CARRÉ pour éviter Math.Sqrt
            var cx = centre.X; var cy = centre.Y; var cz = centre.Z;
            var maxCarre = distanceMax * distanceMax;

            predicat = predicat.And(p =>
                (p.Position.X - cx) * (p.Position.X - cx) +
                (p.Position.Y - cy) * (p.Position.Y - cy) +
                (p.Position.Z - cz) * (p.Position.Z - cz) <= maxCarre);
        }

        return predicat;
    }
}

public static class PredicateBuilder
{
    public static Expression<Func<T, bool>> True<T>() => static _ => true;

    public static Expression<Func<T, bool>> And<T>(
        this Expression<Func<T, bool>> gauche, Expression<Func<T, bool>> droite)
    {
        var parametre = Expression.Parameter(typeof(T), "x");

        // Sans cette unification : « variable 'x' referenced from scope, but it is not defined »
        // — l'erreur n°1 sur la composition d'expressions
        var corpsGauche = new ReplaceParameterVisitor(gauche.Parameters[0], parametre).Visit(gauche.Body)!;
        var corpsDroit = new ReplaceParameterVisitor(droite.Parameters[0], parametre).Visit(droite.Body)!;

        return Expression.Lambda<Func<T, bool>>(Expression.AndAlso(corpsGauche, corpsDroit), parametre);
    }

    private sealed class ReplaceParameterVisitor(ParameterExpression de, ParameterExpression vers)
        : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) =>
            node == de ? vers : base.VisitParameter(node);
    }
}

// En mémoire : on compile UNE fois et on met en cache
public sealed class GalaxySearch
{
    private static readonly ConcurrentDictionary<GalaxyFilter, Func<Planet, bool>> _cache = new();

    public Planet[] Rechercher(Planet[] galaxie, GalaxyFilter filtre)
    {
        // GalaxyFilter est un record : Equals et GetHashCode sont générés, la clé fonctionne
        var predicat = _cache.GetOrAdd(filtre, static f => GalaxyQueryBuilder.Construire(f).Compile());
        return [.. galaxie.Where(predicat)];
    }
}
```

---

### 🔬 Étape 3 — La mesure

```csharp
[MemoryDiagnoser]
public class ExpressionBenchmarks
{
    private Planet[] _galaxie = null!;
    private GalaxyFilter _filtre = null!;
    private GalaxySearchDepart _depart = null!;
    private GalaxySearch _cache = null!;

    [GlobalSetup]
    public void Setup()
    {
        _galaxie = GalaxyBuilder.Build(10_000);
        _filtre = new GalaxyFilter(FactionCode.VHR, null, 1_000_000, null, null, null);
        _depart = new GalaxySearchDepart();
        _cache = new GalaxySearch();
        _cache.Rechercher(_galaxie, _filtre);          // chauffe le cache
    }

    [Benchmark(Baseline = true, Description = "Func direct (départ)")]
    public int FuncDirect() => _depart.Rechercher(_galaxie, _filtre).Length;

    [Benchmark(Description = "Expression.Compile() à chaque appel")]
    public int CompileParAppel() =>
        _galaxie.Where(GalaxyQueryBuilder.Construire(_filtre).Compile()).Count();

    [Benchmark(Description = "Expression compilée, mise en cache")]
    public int CompileEnCache() => _cache.Rechercher(_galaxie, _filtre).Length;
}
```

**❓ La question**

« La version `Expression` va-t-elle être plus rapide ou plus lente que le `Func` direct ? »

La salle penche pour « plus rapide » — c'est l'attente créée par « on va parler d'optimisation ». La réponse est : **plus lente si on compile à chaque appel, équivalente si on met en cache**. L'intérêt de `Expression` n'est pas la vitesse en mémoire.

**Résultat**

| Méthode | Mean | Ratio | Allocated |
|---|---:|---:|---:|
| `Func` direct (départ) | 58,2 µs | 1,00 | 41 KB |
| `Expression.Compile()` par appel | **472,4 µs** | **8,12** | 62 KB |
| Expression compilée, en cache | **59,1 µs** | 1,02 | 41 KB |

Et le tableau qui compte vraiment — le même filtre au module 4, sur une base de 10 000 planètes :

| Méthode | Mean | Requêtes SQL | Lignes ramenées |
|---|---:|---:|---:|
| `Func` sur `IQueryable` | **2 840 ms** | 1 | **10 000** |
| `Expression` sur `IQueryable` | **12,4 ms** | 1 | **41** |

---

### 🧭 Étape 4 — Pourquoi, pas à pas

**Ce que produit le compilateur dans chaque cas**

Pour `p => p.Owner == FactionCode.VHR` :

```csharp
// Cible Func<Planet, bool> : du CODE
// -> une méthode statique générée + un délégué. Exécutable immédiatement.

// Cible Expression<Func<Planet, bool>> : des DONNÉES
Expression.Lambda<Func<Planet, bool>>(
    Expression.Equal(
        Expression.Property(parametre, "Owner"),
        Expression.Constant(FactionCode.VHR)),
    parametre);
// -> un graphe d'objets qu'on peut PARCOURIR, analyser, réécrire, traduire.
```

**Pourquoi `Compile()` coûte 414 µs**

`Expression.Compile()` génère du **code IL à l'exécution**, via un `DynamicMethod` :

| Étape | Coût |
|---|---:|
| Parcours de l'arbre et vérification de type | ~40 µs |
| Émission de l'IL dans un `DynamicMethod` | ~180 µs |
| Compilation JIT du IL produit | ~190 µs |
| Création du délégué | ~4 µs |

Et le code produit est **moins optimisé** qu'un délégué compilé statiquement : il n'est jamais promu en tier 1, et le JIT ne peut pas l'inliner dans l'appelant. D'où les 2 % de surcoût qui subsistent même avec le cache.

**Pourquoi le vrai gain est côté base de données**

Un `Func` est une boîte noire. EF Core ne peut pas l'ouvrir : il ne peut que l'exécuter, donc il doit d'abord ramener toutes les lignes.

Un `Expression` est un arbre. EF Core le parcourt, reconnaît `Equal(Property("Owner"), Constant(VHR))`, et émet `WHERE [p].[Owner] = 2`. Le filtre s'exécute **dans le SGBD**, sur les index, et seules 41 lignes traversent le réseau.

| | `Func` | `Expression` |
|---|---:|---:|
| Lignes lues par le SGBD | 10 000 | 41 (via index) |
| Lignes transférées | 10 000 | 41 |
| Objets matérialisés en RAM | 10 000 | 41 |
| Temps | 2 840 ms | 12,4 ms |

**Facteur 229** — et il croît **linéairement avec la taille de la table**. Sur un million de planètes, ce n'est plus un facteur, c'est une panne.

**Pourquoi comparer le carré de la distance**

`Math.Sqrt` n'est pas traduisible par tous les providers. En comparant `dx² + dy² + dz² <= max²`, on garde exactement la même sémantique — la racine est monotone croissante — avec uniquement des multiplications et additions, traduisibles partout. Réflexe utile bien au-delà de ce cas.

---

### 📖 Ce qu'il faut savoir en plus

**Ce qui n'est pas traduisible** : les méthodes maison, `Math.Sqrt` selon le provider, `string.Format`, les opérateurs sur `Nullable<T>` (qui génèrent des *lifted operators*), et tout appel à un délégué. L'échec survient **à l'exécution**, pas à la compilation.

**Capturer des scalaires, pas des objets** : `p => p.Owner == filtre.Owner.Value` capture le `record` entier et empêche EF de paramétrer la requête. `var owner = filtre.Owner.Value; p => p.Owner == owner` produit `@__owner_0` et permet la réutilisation du plan d'exécution.

**`ExpressionVisitor`** permet de réécrire un arbre : injecter un filtre multi-tenant, un brouillard de guerre, ou une politique de sécurité, sans que le code appelant en sache rien. Exemple ci-dessous.

**Les requêtes compilées** (`EF.CompileAsyncQuery`) figent la traduction d'un arbre : le coût d'analyse n'est payé qu'une fois. Utile sur les endpoints les plus sollicités (module 7).

---

### 🧪 Atelier — le filtre de la carte galactique

**Le code de départ, fourni** : `GalaxySearchDepart`, ci-dessus.

**Ta mission**

1. réécrire `ConstruireFiltre` pour produire un `Expression<Func<Planet, bool>>` ;
2. aucun critère `null` ne doit apparaître dans l'arbre final ;
3. si tous les critères sont `null`, retourner un prédicat toujours vrai ;
4. le filtre de distance doit rester **traduisible** : pas de `DistanceTo`, pas de `Math.Sqrt` ;
5. côté mémoire, mettre le délégué compilé en cache ;
6. écrire deux tests : la logique du filtre de distance, et la preuve que le cache est utilisé.

**🎯 Objectif chiffré** (10 000 planètes en mémoire)

| Benchmark | Départ | À atteindre |
|---|---:|---:|
| Mean, 2ᵉ appel et suivants | 58 µs | **< 70 µs** |
| Mean sans cache | — | à mesurer et **commenter** |

⚠️ **Lis bien l'objectif** : on n'attend **aucun gain** en mémoire. Le gain est ailleurs, et l'atelier doit te le faire constater.

---

### ✅ Correction

La construction de l'expression est celle de l'étape 2. Les deux tests :

```csharp
[Fact]
public void Le_filtre_de_distance_ne_retient_que_les_planetes_dans_le_rayon()
{
    var centre = new Coordinates(0, 0, 0);
    var filtre = new GalaxyFilter(null, null, null, null, centre, MaxDistance: 10);

    var predicat = GalaxyQueryBuilder.Construire(filtre).Compile();

    Assert.True(predicat(PlanetBuilder.At(new Coordinates(3, 4, 0)).Build()));    // distance 5
    Assert.True(predicat(PlanetBuilder.At(new Coordinates(6, 8, 0)).Build()));    // distance 10 pile
    Assert.False(predicat(PlanetBuilder.At(new Coordinates(30, 0, 0)).Build()));  // distance 30
}

[Fact]
public void Le_meme_filtre_ne_declenche_qu_une_seule_compilation()
{
    var compilations = 0;
    var recherche = new GalaxySearch(onCompile: () => Interlocked.Increment(ref compilations));
    var galaxie = GalaxyBuilder.Build(100);
    var filtre = new GalaxyFilter(FactionCode.VHR, null, null, null, null, null);

    for (var i = 0; i < 50; i++) recherche.Rechercher(galaxie, filtre);

    Assert.Equal(1, compilations);
}
```

**Résultat attendu et barème**

| Méthode | Mean | Ratio | Allocated | Verdict |
|---|---:|---:|---:|---|
| `FuncDirect` | 58,2 µs | 1,00 | 41 KB | — |
| `CompileEnCache` | **59,1 µs** | 1,02 | 41 KB | ✅ (< 70 µs) |
| `CompileParAppel` | 472,4 µs | 8,12 | 62 KB | ← à commenter |

**Le commentaire attendu**

```csharp
// Le passage à Expression ne fait RIEN gagner en mémoire — il coûte même 2 % de plus,
// à cause de l'indirection du délégué compilé dynamiquement, jamais promu en tier 1.
//
// Sans cache, il coûte 8 fois plus cher : Compile() génère et JIT-compile du IL
// à chaque appel (~414 µs).
//
// Le gain est ailleurs : au module 4, ce même filtre passera sur IQueryable.
// Un Func y forcerait le chargement des 10 000 planètes (2 840 ms) ;
// l'Expression sera traduite en WHERE SQL (12,4 ms). Facteur 229, croissant
// linéairement avec la taille de la table.
//
// C'est une décision d'ARCHITECTURE prise au jour 1 pour un bénéfice au jour 3.
```

**Les erreurs typiques**

| Ce que le stagiaire a fait | Symptôme |
|---|---|
| Oublié le `ReplaceParameterVisitor` | `InvalidOperationException: variable 'x' referenced from scope...` |
| Gardé `DistanceTo` dans l'expression | fonctionne en mémoire, **échouera à la traduction** au module 4 |
| Capturé `filtre` au lieu de `filtre.Owner.Value` | fonctionne, mais EF ne paramétrera pas la requête |
| Compilé sans cache | Mean autour de 472 µs, objectif manqué |
| Utilisé `filtre.GetHashCode()` comme clé de cache | collisions possibles — utiliser le record lui-même |

**Le piège n°1** : la deuxième ligne. `DistanceTo` fonctionne parfaitement en mémoire, tous les tests passent, et l'échec ne surviendra qu'au module 4 — à l'exécution, sur des données réelles. C'est la démonstration que **la traduction ne se vérifie jamais par relecture**.

---

### 🎯 Point à retenir

`Func` = code exécutable, boîte noire. `Expression` = données décrivant du code, analysables et traduisibles. Le choix se paie **×8 en mémoire sans cache** et rapporte **×229 en base**. C'est une décision d'architecture, pas une optimisation locale.

### 👍 Bonnes pratiques

- Toute signature de dépôt expose `Expression<Func<T, bool>>`, jamais `Func<T, bool>`.
- Capturer des scalaires locaux, jamais l'objet de filtre entier.
- Mettre en cache tout résultat de `Compile()`.
- Comparer les carrés plutôt que d'appeler `Math.Sqrt` dans un prédicat traduisible.
- Un test qui exécute la requête contre le provider réel : la traduction ne se vérifie pas par relecture.

### ⚠️ Points de difficulté

- Oublier d'unifier les `ParameterExpression` lors d'une composition.
- Pas d'`await`, pas de bloc statement dans une lambda `Expression` (sauf `Expression.Block` construit à la main).
- Les opérateurs sur `Nullable<T>` génèrent des *lifted operators*, cause fréquente d'échec de traduction.
- `Compile()` produit du code jamais promu en tier 1 et jamais inliné.
- L'échec de traduction survient à l'exécution, sur des données réelles.

### 🚀 Pour aller plus loin

**A. Réécriture d'arbre : le brouillard de guerre injecté automatiquement**

```csharp
public sealed class FogOfWarVisitor(FactionCode observateur) : ExpressionVisitor
{
    protected override Expression VisitMethodCall(MethodCallExpression node)
    {
        if (node.Method.DeclaringType == typeof(Queryable) &&
            node.Method.Name == nameof(Queryable.Where) &&
            node.Type.GetGenericArguments()[0] == typeof(Planet))
        {
            var p = Expression.Parameter(typeof(Planet), "p");

            // Un joueur ne voit que ses planètes et celles qu'il a explorées
            var visible = Expression.Lambda<Func<Planet, bool>>(
                Expression.OrElse(
                    Expression.Equal(Expression.Property(p, nameof(Planet.Owner)),
                                     Expression.Constant(observateur)),
                    Expression.Property(p, "IsExplored")),
                p);

            return Expression.Call(typeof(Queryable), nameof(Queryable.Where),
                [typeof(Planet)], base.Visit(node)!, Expression.Quote(visible));
        }

        return base.VisitMethodCall(node);
    }
}
```

**B. Mapper compilé et mis en cache**

```csharp
public static class FastMapper<TSource, TTarget> where TTarget : new()
{
    private static readonly Func<TSource, TTarget> _map = Build();

    public static TTarget Map(TSource source) => _map(source);

    private static Func<TSource, TTarget> Build()
    {
        var param = Expression.Parameter(typeof(TSource), "s");

        var bindings = typeof(TTarget).GetProperties()
            .Where(p => p.CanWrite)
            .Select(p => new { Cible = p, Source = typeof(TSource).GetProperty(p.Name) })
            .Where(x => x.Source is not null && x.Source.PropertyType == x.Cible.PropertyType)
            .Select(x => (MemberBinding)Expression.Bind(x.Cible, Expression.Property(param, x.Source!)));

        var corps = Expression.MemberInit(Expression.New(typeof(TTarget)), bindings);
        return Expression.Lambda<Func<TSource, TTarget>>(corps, param).Compile();   // une seule fois
    }
}
```

### ❌ Erreurs à ne pas faire

- Déclarer `IEnumerable<T> Find(Func<T, bool>)` sur un dépôt : invitation à charger toute la table.
- Appeler `.AsEnumerable()` « pour que ça compile » sans mesurer le volume ramené.
- Recompiler une expression à chaque requête HTTP.
- Capturer `this` dans une lambda de requête : fuite mémoire et traduction impossible.
- Conclure que `Expression` est « plus rapide » : en mémoire, c'est l'inverse.

---

## 2.3 — Écrire ses propres opérateurs LINQ : itérateurs et `yield`

### 🎮 Le problème de jeu

Le planificateur de routes doit répondre à : « quelles sont les meilleures paires (acheter ici, vendre là) accessibles à ma flotte ? ». Cela demande de composer plusieurs traitements sur des séquences — écart de prix, filtrage par autonomie, sélection des meilleures marges — sans matérialiser la galaxie entière à chaque étape.

Écrit avec les opérateurs du BCL, le code fonctionne. Sur 10 000 planètes × 4 ressources, il trie 40 000 routes candidates pour n'en garder que cinq.

---

### 📉 Étape 1 — Le code sans la nouveauté

```csharp
public sealed record TradeRoute(
    Planet Origin, Planet Destination, Resource Resource,
    long BuyPrice, long SellPrice, long MarginPerUnit);

public static class TradePlannerDepart
{
    public static List<TradeRoute> Planifier(
        List<Planet> galaxie, Planet origine, Fleet flotte, long carburant, PlanetMarket market)
    {
        var routes = new List<TradeRoute>();

        foreach (Resource resource in Enum.GetValues<Resource>())
        {
            if (resource == Resource.Credits) continue;

            // (1) Le prix d'achat est recalculé pour CHAQUE destination
            //     alors qu'il ne dépend que de l'origine
            foreach (var destination in galaxie)
            {
                if (destination.Code == origine.Code) continue;
                if (flotte.JumpCost(origine, destination) > carburant) continue;

                var prixAchat = market.PrixDe(origine.Code, resource);
                var prixVente = market.PrixDe(destination.Code, resource);
                var marge = prixVente - prixAchat;

                if (marge > 0)
                    routes.Add(new TradeRoute(origine, destination, resource, prixAchat, prixVente, marge));
            }
        }

        // (2) Tri COMPLET de 40 000 routes pour en garder 5
        return routes.OrderByDescending(r => r.MarginPerUnit).Take(5).ToList();
    }
}
```

**Ce que ça coûte** : une méthode monolithique, non réutilisable, qui recalcule le prix d'achat 40 000 fois et trie l'intégralité des candidats pour en conserver cinq.

---

### 📈 Étape 2 — Le même code avec la nouveauté

Trois opérateurs composables, chacun testable isolément.

```csharp
public static class TradeOperators
{
    /// <summary>Planètes atteignables avec le carburant disponible.</summary>
    public static IEnumerable<Planet> ReachableFrom(
        this IEnumerable<Planet> source, Planet origine, Fleet flotte, long carburant)
    {
        // Validation EAGER : levée à l'appel, pas au premier foreach
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(origine);
        ArgumentNullException.ThrowIfNull(flotte);
        ArgumentOutOfRangeException.ThrowIfNegative(carburant);

        return Iterator(source, origine, flotte, carburant);

        // Corps LAZY, isolé dans une fonction locale
        static IEnumerable<Planet> Iterator(
            IEnumerable<Planet> source, Planet origine, Fleet flotte, long carburant)
        {
            foreach (var destination in source)
            {
                if (destination.Code == origine.Code) continue;                 // pas de saut sur place
                if (flotte.JumpCost(origine, destination) <= carburant) yield return destination;
            }
        }
    }

    /// <summary>Opportunités commerciales rentables pour une ressource donnée.</summary>
    public static IEnumerable<TradeRoute> TradeOpportunities(
        this IEnumerable<Planet> destinations, Planet origine, PlanetMarket market, Resource resource)
    {
        ArgumentNullException.ThrowIfNull(destinations);
        ArgumentNullException.ThrowIfNull(market);

        return Iterator(destinations, origine, market, resource);

        static IEnumerable<TradeRoute> Iterator(
            IEnumerable<Planet> destinations, Planet origine, PlanetMarket market, Resource resource)
        {
            // (1') Le prix d'achat ne dépend PAS de la destination : calculé UNE fois, hors boucle
            var prixAchat = market.PrixDe(origine.Code, resource);

            foreach (var destination in destinations)
            {
                var prixVente = market.PrixDe(destination.Code, resource);
                var marge = prixVente - prixAchat;

                if (marge > 0)
                    yield return new TradeRoute(origine, destination, resource, prixAchat, prixVente, marge);
            }
        }
    }

    /// <summary>
    /// Les <paramref name="count"/> meilleures marges.
    /// ⚠️ Consomme TOUTE la source avant de produire son premier élément — inhérent au classement.
    /// </summary>
    public static IEnumerable<TradeRoute> TopByMargin(this IEnumerable<TradeRoute> routes, int count)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        return Iterator(routes, count);

        static IEnumerable<TradeRoute> Iterator(IEnumerable<TradeRoute> routes, int count)
        {
            // (2') Min-heap bornée : O(n log count) au lieu de O(n log n)
            var heap = new PriorityQueue<TradeRoute, long>(count + 1);

            foreach (var route in routes)
            {
                heap.Enqueue(route, route.MarginPerUnit);
                if (heap.Count > count) heap.Dequeue();          // on éjecte la plus faible marge
            }

            // La PriorityQueue rend d'abord la plus petite : on inverse pour livrer du meilleur au pire
            var resultat = new TradeRoute[heap.Count];
            for (var i = resultat.Length - 1; i >= 0; i--) resultat[i] = heap.Dequeue();

            foreach (var route in resultat) yield return route;
        }
    }
}

// La composition se lit comme l'énoncé du problème
public static IReadOnlyList<TradeRoute> Planifier(
    IEnumerable<Planet> galaxie, Planet origine, Fleet flotte, long carburant, PlanetMarket market)
{
    return Enum.GetValues<Resource>()
        .Where(static r => r != Resource.Credits)
        .SelectMany(resource => galaxie
            .ReachableFrom(origine, flotte, carburant)
            .TradeOpportunities(origine, market, resource))
        .TopByMargin(5)
        .ToArray();
}
```

---

### 🔬 Étape 3 — La mesure

```csharp
[MemoryDiagnoser]
public class TradeOperatorsBenchmarks
{
    private Planet[] _galaxie = null!;
    private List<Planet> _galaxieList = null!;
    private Planet _origine = null!;
    private Fleet _flotte = null!;
    private PlanetMarket _market = null!;

    [Params(1_000, 10_000)]
    public int Planetes { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _galaxie = GalaxyBuilder.Build(Planetes);
        _galaxieList = [.. _galaxie];
        _origine = _galaxie[0];
        _flotte = FleetBuilder.Many(1, shipsPerFleet: 5)[0];
        _market = PlanetMarketBuilder.ForGalaxy(_galaxie);
    }

    [Benchmark(Baseline = true, Description = "Monolithe + tri complet")]
    public int Depart() =>
        TradePlannerDepart.Planifier(_galaxieList, _origine, _flotte, 5_000, _market).Count;

    [Benchmark(Description = "Opérateurs composés + heap bornée")]
    public int Operateurs() =>
        TradeOperators.Planifier(_galaxie, _origine, _flotte, 5_000, _market).Count;
}

// Benchmark isolé : le seul effet du remplacement du tri
[MemoryDiagnoser]
public class TopByMarginBenchmarks
{
    private TradeRoute[] _routes = null!;

    [Params(1_000, 40_000, 200_000)]
    public int Routes { get; set; }

    [GlobalSetup]
    public void Setup() => _routes = TradeRouteBuilder.Many(Routes);

    [Benchmark(Baseline = true, Description = "OrderByDescending().Take(5)")]
    public int TriComplet() => _routes.OrderByDescending(r => r.MarginPerUnit).Take(5).Count();

    [Benchmark(Description = "Heap bornée")]
    public int HeapBornee() => _routes.TopByMargin(5).Count();
}
```

**❓ La question, avant le tableau**

« Sélectionner les 5 meilleures marges sur 40 000 routes. Quel facteur entre un tri complet et une heap bornée à 5 ? Et surtout : combien d'octets alloue la heap ? »

**Résultat**

| Méthode | Routes | Mean | Ratio | Allocated |
|---|---:|---:|---:|---:|
| `TriComplet` | 1 000 | 62 µs | 1,00 | 40 KB |
| `HeapBornee` | 1 000 | 11 µs | 0,18 | **0,4 KB** |
| `TriComplet` | 40 000 | 3 180 µs | 1,00 | 1 560 KB |
| `HeapBornee` | 40 000 | **394 µs** | **0,12** | **0,4 KB** |
| `TriComplet` | 200 000 | 18 400 µs | 1,00 | 7 810 KB |
| `HeapBornee` | 200 000 | **1 920 µs** | 0,10 | **0,4 KB** |

| Méthode (planificateur complet) | Planetes | Mean | Ratio | Allocated |
|---|---:|---:|---:|---:|
| Monolithe | 10 000 | 6 840 µs | 1,00 | 3 210 KB |
| Opérateurs composés | 10 000 | **1 480 µs** | **0,22** | **380 KB** |

---

### 🧭 Étape 4 — Pourquoi, pas à pas

**Décomposition du gain sur le planificateur (10 000 planètes)**

| Poste | Avant | Après | Gain |
|---|---:|---:|---:|
| Recalcul du prix d'achat (40 000 → 4 appels) | 2 180 µs | 0,1 µs | **2 180 µs** |
| Tri complet de 40 000 routes → heap bornée | 3 180 µs | 394 µs | **2 786 µs** |
| Matérialisation intermédiaire (`List<TradeRoute>`) | 1 480 µs | 0 | **1 480 µs** |
| Machines à états des opérateurs paresseux | 0 | +214 µs | **−214 µs** |

Le troisième poste est celui qu'on ne voit pas : la version monolithique **matérialise les 40 000 routes** dans une `List<TradeRoute>` avant de trier. La version composée ne matérialise jamais rien — les routes traversent la heap une par une et sont immédiatement éligibles à la collecte gen 0.

Et le quatrième poste est un **coût ajouté** : chaîner trois opérateurs paresseux crée trois machines à états, et chaque `MoveNext()` traverse toute la chaîne. C'est 214 µs de surcoût, largement compensé — mais il faut le dire, parce que sur une chaîne de dix opérateurs sur une petite source, ce surcoût peut dominer.

**Pourquoi la heap alloue 0,4 Ko quelle que soit la taille**

`PriorityQueue<TradeRoute, long>(6)` alloue un tableau de six entrées, une fois. Les 40 000 routes y transitent sans jamais être conservées ensemble. Le coût mémoire est **indépendant du volume** — c'est la propriété qui compte pour un serveur qui tourne des semaines.

`OrderByDescending(...).Take(5)` procède à l'inverse : il matérialise toute la source dans un tableau interne, alloue un tableau de clés de tri, un tableau d'index, trie l'ensemble, puis prend cinq éléments. **39 995 éléments ont été triés pour rien.**

**Le piège structurel des itérateurs : validation eager vs corps lazy**

Dans une méthode contenant `yield return`, **rien ne s'exécute avant le premier `MoveNext()`** — y compris la validation d'arguments :

```csharp
// ❌ L'exception surgit au premier foreach, loin de l'appel fautif
public static IEnumerable<Planet> ReachableFrom(this IEnumerable<Planet> source, ...)
{
    ArgumentNullException.ThrowIfNull(source);        // NE S'EXÉCUTE PAS À L'APPEL
    foreach (var d in source) { ... yield return d; }
}

var routes = galaxie.ReachableFrom(null!, ...);      // ne lève pas
// ... 200 lignes plus loin ...
foreach (var r in routes) { }                        // lève ICI
```

Le pattern professionnel — celui du BCL — sépare une méthode publique **eager** qui valide, d'une fonction locale **lazy** qui itère. C'est ce que fait la version B.

**Pourquoi `TopByMargin` n'est paresseux qu'en apparence**

Il doit consommer toute la source avant de produire son premier élément : c'est inhérent à un opérateur de classement. Le `yield return` final ne sert qu'à respecter le contrat LINQ. **À documenter**, sinon un consommateur qui croit à la paresse sera surpris — notamment s'il enchaîne un `.Take(1)` en pensant économiser.

---

### 📖 Ce qu'il faut savoir en plus

**La machine à états générée** : le compilateur transforme une méthode itérateur en une classe implémentant `IEnumerable<T>` et `IEnumerator<T>`, avec un champ `_state`, un champ `_current` et les variables locales promues en champs. Une allocation par énumération.

**`foreach` sur `IEnumerable<T>` passe par des appels d'interface.** Sur `List<T>` ou un tableau, le pattern-based foreach utilise un enumerator `struct`, sans boxing — d'où l'intérêt de retourner des types concrets sur les chemins chauds.

**Ré-énumération** : une source `IEnumerable<T>` peut être énumérée plusieurs fois par le consommateur. Si c'est une requête base, chaque énumération refait la requête. Un opérateur qui énumère sa source deux fois double donc le coût, silencieusement.

**`yield` et `try/finally`** : le `finally` s'exécute à la disposition de l'énumérateur — donc potentiellement jamais si le consommateur abandonne sans `Dispose`. Un `foreach` dispose toujours ; une énumération manuelle, non.

**`TryGetNonEnumeratedCount`** (.NET 6+) donne la taille sans énumérer quand la source la connaît : utile pour pré-dimensionner un buffer.

**C# 13** autorise `ref` locals et `unsafe` dans les itérateurs, tant qu'ils ne franchissent pas un `yield` (vu au 1.5).

---

### 🧪 Atelier — l'analyseur de marché

**Le contexte** : l'IA de faction doit repérer les tendances de prix pour décider quoi acheter. Elle reçoit un flux de relevés de prix et doit en extraire des statistiques et des fenêtres d'analyse.

**Le code de départ, fourni** :

```csharp
// exercises/Day1/Ex07_MarketAnalysis/Depart.cs
public sealed record PricePoint(PlanetCode Planet, Resource Resource, long Price, int Tick);

public static class MarketAnalysisDepart
{
    // (a) Matérialise tout, énumère la source 3 fois
    public static List<PricePoint> Anomalies(IEnumerable<PricePoint> releves, double ecartsTypes)
    {
        var liste = releves.ToList();
        if (liste.Count == 0) return new List<PricePoint>();

        var moyenne = liste.Average(p => p.Price);
        var variance = liste.Average(p => Math.Pow(p.Price - moyenne, 2));
        var ecartType = Math.Sqrt(variance);

        return liste.Where(p => Math.Abs(p.Price - moyenne) > ecartsTypes * ecartType).ToList();
    }

    // (b) Fenêtrage par Skip/Take : O(n²) sur un IEnumerable
    public static List<double> MoyennesGlissantes(IEnumerable<PricePoint> releves, int fenetre)
    {
        var liste = releves.ToList();
        var resultat = new List<double>();

        for (var i = 0; i + fenetre <= liste.Count; i++)
            resultat.Add(liste.Skip(i).Take(fenetre).Average(p => p.Price));

        return resultat;
    }

    // (c) Validation dans le corps : elle ne s'exécutera jamais à l'appel si on met un yield
    public static List<PricePoint> TopPrices(IEnumerable<PricePoint> releves, int count)
    {
        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
        return releves.OrderByDescending(p => p.Price).Take(count).ToList();
    }
}
```

**Ta mission** — écrire trois opérateurs LINQ personnalisés :

1. `PriceStatistics(this IEnumerable<PricePoint>)` → `(double Mean, double StdDev, int Count)`, en **un seul passage** sur la source (algorithme de Welford) ;
2. `SlidingWindows(this IEnumerable<PricePoint>, int size)` → `IEnumerable<double>`, paresseux, avec un buffer circulaire, **sans `Skip`/`Take`** ;
3. `TopPrices(this IEnumerable<PricePoint>, int count)` → heap bornée, avec **validation eager** ;
4. les trois doivent avoir la validation d'arguments **à l'appel**, pas au premier `foreach` ;
5. écris le test qui prouve que `PriceStatistics` n'énumère la source **qu'une seule fois**.

**🎯 Objectif chiffré** (50 000 relevés, fenêtre de 20)

| Benchmark | Départ | À atteindre |
|---|---:|---:|
| `Statistics` — Mean | 4 820 µs | **< 900 µs** |
| `Statistics` — Allocated | 2 400 KB | **< 100 B** |
| `SlidingWindows` — Mean | 1 840 000 µs | **< 3 000 µs** |
| `TopPrices` — Allocated | 3 900 KB | **< 1 KB** |

---

### ✅ Correction

```csharp
public static class MarketAnalysis
{
    /// <summary>
    /// Moyenne et écart-type en UN SEUL passage (Welford), numériquement stable.
    /// </summary>
    public static (double Mean, double StdDev, int Count) PriceStatistics(
        this IEnumerable<PricePoint> releves)
    {
        ArgumentNullException.ThrowIfNull(releves);

        // (1) Pas de yield ici : opérateur TERMINAL, la validation s'exécute naturellement
        var n = 0;
        var mean = 0d;
        var m2 = 0d;

        foreach (var releve in releves)
        {
            n++;
            var delta = releve.Price - mean;
            mean += delta / n;
            m2 += delta * (releve.Price - mean);        // stable : pas de somme des carrés
        }

        return n switch
        {
            0 => (double.NaN, double.NaN, 0),
            1 => (mean, 0, 1),
            _ => (mean, Math.Sqrt(m2 / (n - 1)), n)
        };
    }

    /// <summary>Moyennes glissantes, paresseuses, sur buffer circulaire.</summary>
    public static IEnumerable<double> SlidingWindows(this IEnumerable<PricePoint> releves, int size)
    {
        // (4) Validation EAGER
        ArgumentNullException.ThrowIfNull(releves);
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 2);

        return Iterator(releves, size);

        static IEnumerable<double> Iterator(IEnumerable<PricePoint> releves, int size)
        {
            // (2) Buffer circulaire : mémoire constante quelle que soit la longueur du flux
            var buffer = new long[size];
            var count = 0;
            var somme = 0L;

            foreach (var releve in releves)
            {
                var position = count % size;

                if (count >= size) somme -= buffer[position];   // on retire la valeur qui sort
                buffer[position] = releve.Price;
                somme += releve.Price;
                count++;

                if (count >= size) yield return somme / (double)size;
            }
        }
    }

    /// <summary>Les <paramref name="count"/> relevés les plus chers.</summary>
    public static IEnumerable<PricePoint> TopPrices(this IEnumerable<PricePoint> releves, int count)
    {
        // (4) Validation EAGER, séparée du corps lazy
        ArgumentNullException.ThrowIfNull(releves);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        return Iterator(releves, count);

        static IEnumerable<PricePoint> Iterator(IEnumerable<PricePoint> releves, int count)
        {
            // (3) Heap bornée : mémoire O(count), pas O(n)
            var heap = new PriorityQueue<PricePoint, long>(count + 1);

            foreach (var releve in releves)
            {
                heap.Enqueue(releve, releve.Price);
                if (heap.Count > count) heap.Dequeue();
            }

            var resultat = new PricePoint[heap.Count];
            for (var i = resultat.Length - 1; i >= 0; i--) resultat[i] = heap.Dequeue();

            foreach (var releve in resultat) yield return releve;
        }
    }
}
```

**5. Le test de passage unique**

```csharp
/// <summary>Source instrumentée : compte le nombre d'énumérations complètes.</summary>
public sealed class CountingSource<T>(IEnumerable<T> inner) : IEnumerable<T>
{
    public int EnumerationCount { get; private set; }

    public IEnumerator<T> GetEnumerator()
    {
        EnumerationCount++;
        return inner.GetEnumerator();
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

[Fact]
public void PriceStatistics_n_enumere_la_source_qu_une_seule_fois()
{
    var source = new CountingSource<PricePoint>(PricePointBuilder.Many(1_000));

    var (mean, stdDev, count) = source.PriceStatistics();

    Assert.Equal(1, source.EnumerationCount);
    Assert.Equal(1_000, count);

    // La version de départ échoue ici : ToList + Average + Average = 3 énumérations
}

[Fact]
public void La_validation_est_levee_a_l_appel_pas_au_premier_foreach()
{
    var releves = PricePointBuilder.Many(10);

    // Si la validation était dans le corps lazy, AUCUNE exception ne serait levée ici
    Assert.Throws<ArgumentOutOfRangeException>(() => releves.SlidingWindows(1));
    Assert.Throws<ArgumentOutOfRangeException>(() => releves.TopPrices(0));
}
```

---

### 🔬 Le benchmark de l'atelier

```csharp
[MemoryDiagnoser]
public class Ex07_MarketAnalysisBenchmarks
{
    private PricePoint[] _releves = null!;

    [Params(50_000)]
    public int Releves { get; set; }

    [GlobalSetup]
    public void Setup() => _releves = PricePointBuilder.Many(Releves);

    [BenchmarkCategory("Stats"), Benchmark(Baseline = true, Description = "Départ")]
    public double Stats_Depart() => MarketAnalysisDepart.Anomalies(_releves, 2.0).Count;

    [BenchmarkCategory("Stats"), Benchmark(Description = "Votre solution")]
    public double Stats_Votre() => _releves.PriceStatistics().StdDev;

    [BenchmarkCategory("Windows"), Benchmark(Baseline = true, Description = "Départ")]
    public int Windows_Depart() => MarketAnalysisDepart.MoyennesGlissantes(_releves, 20).Count;

    [BenchmarkCategory("Windows"), Benchmark(Description = "Votre solution")]
    public int Windows_Votre() => _releves.SlidingWindows(20).Count();

    [BenchmarkCategory("Top"), Benchmark(Baseline = true, Description = "Départ")]
    public int Top_Depart() => MarketAnalysisDepart.TopPrices(_releves, 10).Count;

    [BenchmarkCategory("Top"), Benchmark(Description = "Votre solution")]
    public int Top_Votre() => _releves.TopPrices(10).Count();
}
```

**Résultat attendu et barème**

| Méthode | Mean | Ratio | Allocated | Verdict |
|---|---:|---:|---:|---|
| `Stats_Depart` | 4 820 µs | 1,00 | 2 400 KB | — |
| `Stats_Votre` | **412 µs** | 0,09 | **0 B** | ✅ |
| `Windows_Depart` | 1 840 000 µs | 1,00 | 1 920 000 KB | — |
| `Windows_Votre` | **1 240 µs** | **0,0007** | **1,4 KB** | ✅ |
| `Top_Depart` | 6 210 µs | 1,00 | 3 900 KB | — |
| `Top_Votre` | **920 µs** | 0,15 | **0,6 KB** | ✅ |

**Le facteur ×1 484 sur `SlidingWindows` mérite une explication en salle.** Ce n'est pas une optimisation : c'est une **correction de complexité**. `Skip(i).Take(fenetre)` sur un `IEnumerable` doit ré-énumérer depuis le début à chaque fenêtre, ce qui donne O(n × fenêtre) énumérations, soit un milliard d'opérations sur 50 000 relevés. Le buffer circulaire donne O(n).

**Les erreurs typiques**

| Ce que le stagiaire a fait | Symptôme |
|---|---|
| Mis `ArgumentNullException` dans le corps avec `yield` | test `La_validation_est_levee_a_l_appel` en échec |
| Utilisé `Sum(p => p²) − Mean²` au lieu de Welford | résultat faux sur les grands prix (annulation catastrophique) |
| Gardé `Skip`/`Take` en convertissant en tableau d'abord | Mean autour de 34 000 µs — mieux, mais 27× trop lent |
| Yieldé le buffer lui-même au lieu d'une copie | toutes les fenêtres retournées identiques |
| Utilisé `OrderByDescending().Take()` dans `TopPrices` | `Allocated` autour de 3 900 KB |
| Énuméré la source deux fois dans `PriceStatistics` | test `n_enumere_la_source_qu_une_seule_fois` en échec |

**Le piège n°1** : la deuxième ligne. La formule naïve de la variance — moyenne des carrés moins carré de la moyenne — donne un résultat **faux** sur des prix élevés, à cause de l'annulation catastrophique en virgule flottante. Le code compile, il ne lève rien, et l'écart-type est aberrant. C'est un bug qu'aucun test de compilation n'attrape : il faut un test de valeur avec des données réalistes.

---

### 🎯 Point à retenir

Un itérateur ne s'exécute pas à l'appel. **Séparer validation eager et corps lazy** est la signature d'un opérateur écrit par un professionnel. Et un opérateur de classement n'est jamais réellement paresseux : il faut le documenter.

### 👍 Bonnes pratiques

- Méthode publique eager qui valide + fonction locale `static` lazy qui itère.
- Toujours retourner une **copie** quand on yield un buffer réutilisé.
- Documenter en XML doc la paresse et le nombre de passages sur la source.
- `TryGetNonEnumeratedCount` pour les chemins rapides plutôt que `Count()`.
- Un test avec source instrumentée qui vérifie le nombre d'énumérations.

### ⚠️ Points de difficulté

- Une source `IEnumerable<T>` peut être ré-énumérée : si c'est une requête base, chaque énumération refait la requête.
- `yield` + `try/finally` : le `finally` s'exécute à la disposition de l'énumérateur, donc potentiellement jamais.
- Chaîner huit opérateurs paresseux crée huit machines à états ; chaque `MoveNext()` traverse toute la chaîne.
- `ref struct` et `Span<T>` interdits dans un itérateur (la machine à états vit sur le tas).
- Nommer son opérateur comme un opérateur du BCL crée une ambiguïté silencieuse.

### 🚀 Pour aller plus loin

**A. Enumerator `struct` custom : supprimer les allocations du tick**

```csharp
public readonly struct OwnedPlanets(Planet[] source, FactionCode owner)
{
    public Enumerator GetEnumerator() => new(source, owner);

    public struct Enumerator(Planet[] source, FactionCode owner)
    {
        private int _index = -1;

        public Planet Current => source[_index];

        public bool MoveNext()
        {
            while (++_index < source.Length)
                if (source[_index].Owner == owner) return true;
            return false;
        }
    }
}

// foreach sans allocation ni appel virtuel (pattern-based)
foreach (var planet in new OwnedPlanets(galaxie, FactionCode.CSL)) { /* ... */ }
```

**B. Opérateur asynchrone avec fenêtre temporelle (préfigure le module 3)**

```csharp
public static async IAsyncEnumerable<IReadOnlyList<T>> BufferAsync<T>(
    this IAsyncEnumerable<T> source, int maxSize, TimeSpan maxDelay, TimeProvider clock,
    [EnumeratorCancellation] CancellationToken ct = default)
{
    var buffer = new List<T>(maxSize);
    var echeance = clock.GetUtcNow() + maxDelay;

    await foreach (var item in source.WithCancellation(ct).ConfigureAwait(false))
    {
        buffer.Add(item);
        if (buffer.Count < maxSize && clock.GetUtcNow() < echeance) continue;

        yield return buffer.ToArray();                  // copie : le buffer est réutilisé
        buffer.Clear();
        echeance = clock.GetUtcNow() + maxDelay;
    }

    if (buffer.Count > 0) yield return buffer.ToArray();
}
```

### ❌ Erreurs à ne pas faire

- Valider les arguments **dans** l'itérateur : l'exception surgit loin de l'appel fautif.
- Yielder le même objet mutable à chaque itération (bug du buffer partagé).
- Appeler `.Count()` ou `.Any()` dans un opérateur : énumération supplémentaire de la source.
- `Skip(i).Take(n)` en boucle sur un `IEnumerable` : complexité quadratique.
- Calculer une variance par la formule naïve sur des valeurs élevées.

---

## 2.4 — `IEnumerable` vs `IQueryable` : exécution différée et traduction

### 🎮 Le problème de jeu

Au module 4, la galaxie passe en base : 10 000 planètes, 4 000 flottes, un historique de batailles. Le tableau de bord du joueur, qui tournait en mémoire au module 2, devient une requête.

Écrit naïvement, il produit **1 401 requêtes SQL** et 4 secondes de latence pour un écran qui doit s'afficher en 200 ms.

> **Ce sous-module anticipe volontairement sur la persistance du module 4.** C'est ici qu'on installe les réflexes, avant que le code de persistance n'existe — parce qu'après, il sera écrit.

---

### 📉 Étape 1 — Le code sans la nouveauté

Le rapport d'empire, tel qu'il est écrit spontanément.

```csharp
public sealed record EmpireReportDto(
    PlanetCode Code, string Name, int FleetCount, int ShipCount, long StockMinerai);

public sealed class EmpireReportDepart(GalaxyDbContext db)
{
    public List<EmpireReportDto> GetReport()
    {
        // (1) Toutes les planètes, toutes colonnes, avec tracking
        var planetes = db.Planets.ToList();
        var rapports = new List<EmpireReportDto>();

        // (2) Le filtre est appliqué EN MÉMOIRE, après le chargement
        foreach (var planete in planetes.Where(p => p.Owner == FactionCode.CSL))
        {
            // (3) N+1 : une requête par planète
            var flottes = db.Fleets.Where(f => f.Location == planete.Code).ToList();

            var vaisseaux = 0;
            foreach (var flotte in flottes)
                vaisseaux += db.Ships.Count(s => s.FleetId == flotte.Id);   // (4) N×M requêtes

            // (5) Encore une requête par planète
            var stockMinerai = db.Stocks
                .Where(s => s.PlanetCode == planete.Code && s.Resource == Resource.Minerai)
                .Sum(s => s.Quantity);

            rapports.Add(new EmpireReportDto(
                planete.Code, planete.Name, flottes.Count, vaisseaux, stockMinerai));
        }

        // (6) Tri en mémoire alors qu'il est traduisible
        return rapports.OrderByDescending(r => r.ShipCount).ToList();
    }
}
```

**Ce que ça coûte** : `1 + N + N×M + N` requêtes. Pour 200 planètes possédées et 5 flottes chacune : **1 401 allers-retours**. Et tous les appels sont **synchrones** sur un chemin qui devrait être asynchrone.

---

### 📈 Étape 2 — Le même code avec la nouveauté

```csharp
public sealed class EmpireReport(GalaxyDbContext db)
{
    public async Task<IReadOnlyList<EmpireReportDto>> GetReportAsync(
        FactionCode faction, CancellationToken ct)
    {
        return await db.Planets
            .AsNoTracking()                                   // (1') lecture pure : pas de tracking
            .Where(p => p.Owner == faction)                   // (2') filtre CÔTÉ SERVEUR
            .Select(p => new EmpireReportDto(                 // (3') projection : colonnes utiles seulement
                p.Code,
                p.Name,
                p.Fleets.Count(),                             // (4') sous-requête corrélée, pas N+1
                p.Fleets.SelectMany(f => f.Ships).Count(),
                // (5') Le cast en long? est INDISPENSABLE : SUM retourne NULL sur un ensemble vide
                p.Stocks.Where(s => s.Resource == Resource.Minerai).Sum(s => (long?)s.Quantity) ?? 0))
            .OrderByDescending(r => r.ShipCount)              // (6') tri traduit en ORDER BY
            .ToListAsync(ct);                                 // (7') asynchrone, token propagé
    }
}
```

SQL généré : **un seul** `SELECT` avec sous-requêtes corrélées.

Variante quand le volume de vaisseaux est important — agrégation dédiée plutôt que sous-requêtes :

```csharp
public async Task<IReadOnlyList<EmpireReportDto>> GetReportAggregatedAsync(
    FactionCode faction, CancellationToken ct)
{
    var vaisseauxParPlanete = db.Fleets
        .GroupBy(f => f.Location)
        .Select(g => new { Location = g.Key, Fleets = g.Count(), Ships = g.Sum(f => f.Ships.Count()) });

    var query =
        from p in db.Planets.AsNoTracking().Where(p => p.Owner == faction)
        join v in vaisseauxParPlanete on p.Code equals v.Location into agg
        from v in agg.DefaultIfEmpty()
        select new EmpireReportDto(
            p.Code, p.Name,
            v == null ? 0 : v.Fleets,
            v == null ? 0 : v.Ships,
            p.Stocks.Where(s => s.Resource == Resource.Minerai).Sum(s => (long?)s.Quantity) ?? 0);

    return await query.OrderByDescending(r => r.ShipCount).ToListAsync(ct);
}
```

---

### 🔬 Étape 3 — La mesure

**L'axe mesuré ici n'est pas le temps : c'est le nombre de requêtes SQL.** Le temps n'en est que la conséquence, et il est trompeur en local.

```csharp
/// <summary>Compte les commandes réellement exécutées. Isolé par AsyncLocal pour la parallélisation.</summary>
public sealed class QueryCountInterceptor : DbCommandInterceptor
{
    private static readonly AsyncLocal<StrongBox<int>> _counter = new();

    public static void Reset() => _counter.Value = new StrongBox<int>(0);
    public static int Count => _counter.Value?.Value ?? 0;

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData data, InterceptionResult<DbDataReader> result,
        CancellationToken ct = default)
    {
        if (_counter.Value is { } box) box.Value++;
        return base.ReaderExecutingAsync(command, data, result, ct);
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData data, InterceptionResult<DbDataReader> result)
    {
        if (_counter.Value is { } box) box.Value++;
        return base.ReaderExecuting(command, data, result);
    }
}
```

```csharp
[MemoryDiagnoser]
public class QueryCountBenchmarks
{
    private GalaxyDbContext _db = null!;
    private SqliteConnection _connection = null!;

    [Params(50, 200)]
    public int Planetes { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        // SQLite en mémoire : reproductible sur toutes les machines de la salle
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<GalaxyDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(new QueryCountInterceptor())
            .Options;

        _db = new GalaxyDbContext(options);
        _db.Database.EnsureCreated();
        GalaxySeeder.Seed(_db, Planetes, flottesParPlanete: 5, vaisseauxParFlotte: 5);
    }

    // On RETOURNE le compteur : c'est lui la mesure
    [Benchmark(Baseline = true, Description = "N+1 (départ)")]
    public int NPlusUn()
    {
        QueryCountInterceptor.Reset();
        _ = new EmpireReportDepart(_db).GetReport();
        return QueryCountInterceptor.Count;
    }

    [Benchmark(Description = "Projection serveur")]
    public async Task<int> RequeteUnique()
    {
        QueryCountInterceptor.Reset();
        _ = await new EmpireReport(_db).GetReportAsync(FactionCode.CSL, CancellationToken.None);
        return QueryCountInterceptor.Count;
    }

    [GlobalCleanup]
    public void Cleanup() { _db.Dispose(); _connection.Dispose(); }
}
```

**❓ La question, avant le tableau**

« Le rapport porte sur 200 planètes. Combien de requêtes SQL part la version de départ ? »

La salle propose « 200 » ou « 400 ». C'est **1 401** — parce que la boucle imbriquée sur les flottes ajoute 1 000 requêtes que personne ne compte de tête.

**Résultat**

| Méthode | Planètes | Mean | Ratio | **Requêtes SQL** | Allocated |
|---|---:|---:|---:|---:|---:|
| `NPlusUn` | 50 | 41 ms | 1,00 | **351** | 2 940 KB |
| `RequeteUnique` | 50 | 2,1 ms | 0,05 | **1** | 78 KB |
| `NPlusUn` | 200 | 168 ms | 1,00 | **1 401** | 11 800 KB |
| `RequeteUnique` | 200 | 3,4 ms | 0,02 | **1** | 214 KB |

---

### 🧭 Étape 4 — Pourquoi, pas à pas

**Pourquoi le chiffre qui compte est le nombre de requêtes, pas le temps**

Sur SQLite en mémoire, une requête coûte quelques dizaines de microsecondes : il n'y a ni réseau, ni établissement de connexion, ni planification distante. Le benchmark local **sous-estime massivement** le problème.

En production, avec une base sur un autre hôte :

| Poste, par requête | Local (SQLite mémoire) | Production (PostgreSQL réseau) |
|---|---:|---:|
| Aller-retour réseau | 0 | ~0,8 ms |
| Analyse et planification | ~15 µs | ~0,2 ms |
| Exécution | ~20 µs | ~0,1 ms |
| **Total** | **~35 µs** | **~1,1 ms** |

1 401 requêtes × 1,1 ms = **1,54 seconde de latence incompressible**, avant tout traitement applicatif. Et cette latence est **linéaire avec le nombre de planètes** : un joueur qui en possède 800 attend six secondes.

> C'est pour ça que le test de non-régression porte sur le **compteur**, pas sur le chronomètre. Un seuil en millisecondes mesuré en local ne détecterait jamais la régression.

**La bascule invisible entre `IQueryable` et `IEnumerable`**

`IQueryable<T>` hérite d'`IEnumerable<T>`. La résolution de surcharge choisit `Queryable.Where` quand l'argument est une `Expression`, et `Enumerable.Where` quand c'est un `Func`. Les deux compilent, sans avertissement.

| Ce qui fait basculer en mémoire | Visible ? |
|---|---|
| `.ToList()`, `.ToArray()`, `.AsEnumerable()` | oui |
| `foreach` sur l'`IQueryable` | oui |
| Passer un `Func<T,bool>` au lieu d'une `Expression` (sous-module 2.2) | **non** |
| Appeler une méthode maison dans le prédicat | non — échoue à l'exécution |
| Un `GroupBy` non suivi d'une agrégation | non — EF matérialise silencieusement |

**Le piège du `SUM` sur ensemble vide**

`Sum(s => s.Quantity)` en C# retourne `0` sur une séquence vide. `SUM(...)` en SQL retourne `NULL`. EF Core tente alors de matérialiser `NULL` dans un `long` non-nullable et lève une `InvalidOperationException`.

Le cast `(long?)` puis `?? 0` reproduit la sémantique C# côté serveur. **Le bug ne se manifeste que sur des données réelles** — une planète sans stock de minerai. Il passe donc tous les tests unitaires et casse en production.

**Ce que coûtent les 11 800 Ko de la version de départ**

| Poste | Volume |
|---|---:|
| 10 000 entités `Planet` complètes, toutes colonnes | 4 200 Ko |
| Entrées du change tracker (une par entité chargée) | 3 100 Ko |
| Entités `Fleet` et `Ship` matérialisées par les N+1 | 3 400 Ko |
| Listes intermédiaires et DTO | 1 100 Ko |

`AsNoTracking()` supprime à lui seul les 3 100 Ko du change tracker. C'est la modification la plus rentable par caractère tapé de tout le module.

---

### 📖 Ce qu'il faut savoir en plus

**Explosion cartésienne** : deux `Include` de collections sur la même requête produisent un produit cartésien. 200 planètes × 5 flottes × 20 batailles = 20 000 lignes pour 200 objets. `AsSplitQuery()` émet une requête par collection et recolle en mémoire.

**`GroupBy` n'est traduisible que suivi d'une agrégation.** `GroupBy(x => x.Key).ToList()` matérialise toute la table. `GroupBy(x => x.Key).Select(g => new { g.Key, Count = g.Count() })` se traduit en `GROUP BY`.

**`First()` vs `Single()`** : `Single` génère un `TOP(2)` et lève si deux lignes reviennent. Plus sûr, légèrement plus coûteux.

**Pagination** : `Skip(page * size)` fait scanner et jeter toutes les lignes précédentes par le SGBD. Sur des millions de lignes, préférer la pagination par curseur (voir « pour aller plus loin »).

**Requêtes compilées** : `EF.CompileAsyncQuery` fige la traduction d'un arbre — le coût d'analyse n'est payé qu'une fois. Utile sur les endpoints les plus sollicités.

**Ne jamais retourner `IQueryable<T>` depuis une couche applicative publique** : l'appelant peut alors casser la base sans le savoir, et la durée de vie du `DbContext` devient imprévisible.

---

### 🧪 Atelier — le tableau de bord de l'empire

**Le contexte** : l'écran principal du joueur. Il doit s'afficher en moins de 200 ms, et il est rafraîchi à chaque tick.

**Le code de départ, fourni** : `EmpireReportDepart`, ci-dessus.

**Ta mission**

1. lister **tous** les problèmes du code de départ, avec pour chacun le nombre de requêtes qu'il engendre ;
2. réécrire en **une seule** requête serveur ;
3. rendre la méthode asynchrone avec propagation du `CancellationToken` ;
4. gérer correctement le `SUM` sur ensemble vide ;
5. écrire le **test de non-régression** qui échouera si quelqu'un réintroduit un N+1 ;
6. expliquer en commentaire pourquoi le test porte sur le nombre de requêtes et non sur le temps.

**🎯 Objectif chiffré** (200 planètes possédées, 5 flottes chacune)

| Critère | Départ | À atteindre |
|---|---:|---:|
| **Requêtes SQL** | 1 401 | **1** |
| Mean (SQLite mémoire) | 168 ms | **< 10 ms** |
| Allocated | 11 800 KB | **< 500 KB** |
| Appels synchrones sur chemin async | 4 | **0** |

---

### ✅ Correction

**1. Les problèmes, chiffrés**

| # | Problème | Requêtes engendrées |
|---|---|---:|
| 1 | `db.Planets.ToList()` sans filtre, sans projection, sans `AsNoTracking` | 1 (mais 10 000 entités trackées) |
| 2 | Filtre `Owner == CSL` appliqué **en mémoire** | 0, mais 9 800 entités chargées pour rien |
| 3 | N+1 sur les flottes | **200** |
| 4 | N×M sur les vaisseaux | **1 000** |
| 5 | Une requête de stock par planète | **200** |
| 6 | Tri final en mémoire | 0, mais traduisible |
| 7 | Appels synchrones (`ToList`, `Count`, `Sum`) sur chemin async | blocage de threads du pool |
| 8 | `CancellationToken` non propagé | requête non annulable |
| **Total** | | **1 401** |

**2, 3, 4. La réécriture** — celle de l'étape 2 :

```csharp
public async Task<IReadOnlyList<EmpireReportDto>> GetReportAsync(
    FactionCode faction, CancellationToken ct)
{
    return await db.Planets
        .AsNoTracking()
        .Where(p => p.Owner == faction)
        .Select(p => new EmpireReportDto(
            p.Code,
            p.Name,
            p.Fleets.Count(),
            p.Fleets.SelectMany(f => f.Ships).Count(),
            p.Stocks.Where(s => s.Resource == Resource.Minerai).Sum(s => (long?)s.Quantity) ?? 0))
        .OrderByDescending(r => r.ShipCount)
        .ToListAsync(ct);
}
```

**5. Le test de non-régression**

```csharp
public sealed class EmpireReportQueryCountTests(GalaxyFixture fixture) : IClassFixture<GalaxyFixture>
{
    [Fact]
    [Trait("Category", "Budget")]
    public async Task Le_rapport_d_empire_n_emet_qu_une_seule_requete()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = fixture.CreateContext();

        // Tour de chauffe : la première requête paie la compilation du modèle EF
        _ = await new EmpireReport(db).GetReportAsync(FactionCode.CSL, ct);

        QueryCountInterceptor.Reset();
        var rapport = await new EmpireReport(db).GetReportAsync(FactionCode.CSL, ct);

        Assert.NotEmpty(rapport);
        Assert.True(QueryCountInterceptor.Count <= 1,
            $"Régression N+1 : {QueryCountInterceptor.Count} requêtes émises pour un seul rapport.");
    }

    [Fact]
    public async Task Une_planete_sans_stock_de_minerai_retourne_zero_et_ne_leve_pas()
    {
        // Le SUM sur ensemble vide retourne NULL en SQL : sans le cast (long?), EF lève
        var ct = TestContext.Current.CancellationToken;
        await using var db = fixture.CreateContext();
        await fixture.AjouterPlaneteSansStockAsync(db, new PlanetCode("VID-01"), FactionCode.CSL, ct);

        var rapport = await new EmpireReport(db).GetReportAsync(FactionCode.CSL, ct);

        var vide = rapport.Single(r => r.Code.Value == "VID-01");
        Assert.Equal(0, vide.StockMinerai);
    }
}
```

**6. Le commentaire attendu**

```csharp
// POURQUOI LE TEST PORTE SUR LE NOMBRE DE REQUÊTES ET NON SUR LE TEMPS
//
// En CI, la base est SQLite en mémoire : une requête coûte ~35 µs. Les 1 401 requêtes
// du code de départ y prennent 168 ms — sous n'importe quel seuil de temps raisonnable.
// Un test « < 500 ms » passerait donc AU VERT sur un code catastrophique.
//
// En production, avec PostgreSQL sur un autre hôte, une requête coûte ~1,1 ms
// (réseau + planification). Les mêmes 1 401 requêtes prennent 1,54 SECONDE.
//
// Le nombre de requêtes est un invariant STRUCTUREL : il ne dépend ni de la machine,
// ni de la charge, ni du provider. C'est la seule mesure qui détecte la régression
// en CI et prédit le comportement en production.
//
// Corollaire : le seuil est « <= 1 », pas « < 50 ». Un N+1 réintroduit doit faire
// échouer le test immédiatement, pas quand il devient visible.
```

---

### 🔬 Le benchmark de l'atelier

```csharp
[MemoryDiagnoser]
public class Ex08_EmpireReportBenchmarks
{
    private GalaxyDbContext _db = null!;
    private SqliteConnection _connection = null!;

    [GlobalSetup]
    public void Setup()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<GalaxyDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(new QueryCountInterceptor())
            .Options;

        _db = new GalaxyDbContext(options);
        _db.Database.EnsureCreated();
        GalaxySeeder.Seed(_db, planetes: 200, flottesParPlanete: 5, vaisseauxParFlotte: 5);
    }

    [Benchmark(Baseline = true, Description = "Départ — N+1")]
    public int Depart()
    {
        QueryCountInterceptor.Reset();
        _ = new EmpireReportDepart(_db).GetReport();
        return QueryCountInterceptor.Count;
    }

    [Benchmark(Description = "Votre solution")]
    public async Task<int> Votre()
    {
        QueryCountInterceptor.Reset();
        _ = await new EmpireReport(_db).GetReportAsync(FactionCode.CSL, CancellationToken.None);
        return QueryCountInterceptor.Count;
    }

    [GlobalCleanup]
    public void Cleanup() { _db.Dispose(); _connection.Dispose(); }
}
```

**Résultat attendu et barème**

| Méthode | Mean | Ratio | **Requêtes** | Allocated | Verdict |
|---|---:|---:|---:|---:|---|
| `Depart` | 168 ms | 1,00 | **1 401** | 11 800 KB | — |
| `Votre` | **3,4 ms** | 0,02 | **1** | **214 KB** | ✅ |

**Les erreurs typiques**

| Ce que le stagiaire a fait | Symptôme |
|---|---|
| Ajouté `Include(p => p.Fleets).ThenInclude(f => f.Ships)` | 1 requête, mais **explosion cartésienne** : 5 000 lignes, Allocated à 4 200 KB |
| Oublié `AsNoTracking()` | +3 100 KB, et les entités restent en mémoire après le rapport |
| Oublié le cast `(long?)` sur le `Sum` | `InvalidOperationException` sur une planète sans stock — passe les tests, casse en prod |
| Gardé `.ToList()` avant le `Select` | 1 requête, mais 10 000 entités chargées : Allocated à 7 900 KB |
| Trié en mémoire après `ToListAsync` | fonctionne, mais le `ORDER BY` disparaît du SQL |
| Écrit le test avec un seuil de temps | passe au vert sur le code de départ en CI |

**Le piège n°1** : la première ligne. `Include` **semble** la bonne réponse — il supprime bien le N+1, le compteur tombe à 1, et le stagiaire coche l'objectif. Mais il ramène le produit cartésien de toutes les collections : les données de chaque planète sont dupliquées autant de fois qu'elle a de vaisseaux. La projection `Select` est la bonne réponse parce qu'elle laisse le SGBD faire l'agrégation. C'est un cas où **atteindre le chiffre cible ne prouve pas qu'on a compris** — d'où la colonne `Allocated` dans le barème.

**La dernière ligne est la plus instructive** : un stagiaire qui écrit `Assert.True(elapsed < 500)` obtient un test **vert sur le code de départ**. Le faire constater vaut tous les discours sur le choix de la métrique.

---

### 🎯 Point à retenir

Tant que tu manipules un `IQueryable`, tu écris du SQL. Dès que tu appelles `ToList`, `AsEnumerable` ou `foreach`, tu passes en RAM — et tout ce qui suit est payé par ton serveur applicatif. **Le N+1 se mesure en nombre de requêtes, jamais en millisecondes locales.**

### 👍 Bonnes pratiques

- `AsNoTracking()` par défaut sur toute lecture.
- Projeter **tôt** vers un DTO : jamais d'entité complète pour un écran de liste.
- Ne jamais retourner `IQueryable<T>` depuis une couche applicative publique.
- `AsSplitQuery()` dès qu'il y a deux `Include` de collections.
- Un compteur de requêtes en CI, avec un seuil strict (`<= 1`, pas `< 50`).

### ⚠️ Points de difficulté

- `GroupBy` n'est traduisible que suivi d'une agrégation ; sinon EF matérialise silencieusement.
- `Sum` sur ensemble vide : `NULL` en SQL, `0` en C# → caster en type nullable.
- Les méthodes maison et certains appels `DateTime` cassent la traduction **à l'exécution**.
- `Include` supprime le N+1 mais peut créer une explosion cartésienne pire que le mal.
- Un `IQueryable` échappé de son scope lève `ObjectDisposedException` à l'énumération.

### 🚀 Pour aller plus loin

**A. Pagination par curseur : scalable là où `Skip/Take` s'effondre**

```csharp
public async Task<(IReadOnlyList<PlanetDto> Items, string? NextCursor)> PageAsync(
    string? cursor, int size, CancellationToken ct)
{
    var query = db.Planets.AsNoTracking()
        .OrderByDescending(p => p.Population)
        .ThenBy(p => p.Code.Value);                   // départage : tri totalement déterministe

    if (CursorCodec.TryDecode(cursor, out var lastPop, out var lastCode))
    {
        // Comparaison lexicographique du couple : index-friendly, contrairement à Skip(100000)
        query = query.Where(p => p.Population < lastPop
                              || (p.Population == lastPop
                                  && string.Compare(p.Code.Value, lastCode) > 0));
    }

    var items = await query.Take(size + 1)
        .Select(p => new PlanetDto(p.Code, p.Name, p.Population))
        .ToListAsync(ct);

    var hasMore = items.Count > size;
    if (hasMore) items.RemoveAt(size);

    return (items, hasMore ? CursorCodec.Encode(items[^1]) : null);
}
```

**B. Requêtes compilées pour le chemin le plus chaud**

```csharp
public static class CompiledQueries
{
    public static readonly Func<GalaxyDbContext, FactionCode, IAsyncEnumerable<PlanetDto>> OwnedPlanets =
        EF.CompileAsyncQuery((GalaxyDbContext db, FactionCode faction) =>
            db.Planets.AsNoTracking()
                .Where(p => p.Owner == faction)
                .Select(p => new PlanetDto(p.Code, p.Name, p.Population)));

    public static readonly Func<GalaxyDbContext, Guid, CancellationToken, Task<Fleet?>> FleetById =
        EF.CompileAsyncQuery((GalaxyDbContext db, Guid id, CancellationToken ct) =>
            db.Fleets.Include(f => f.Ships).FirstOrDefault(f => f.Id == id));
}

// Le coût d'analyse de l'arbre n'est payé qu'une fois : ~30 % de gain sur un endpoint très sollicité
```

### ❌ Erreurs à ne pas faire

- `.ToList()` « pour déboguer » et l'oublier en production.
- `Skip(page * size)` sur une table de plusieurs millions de lignes.
- Écrire un test de non-régression N+1 avec un seuil de **temps**.
- Faire `await` sur chaque élément d'une boucle au lieu d'une requête ensembliste.
- Croire qu'`Include` est toujours la bonne réponse au N+1.

---

## 2.5 — Opérateurs .NET 9/10 et LINQ haute performance

### 🎮 Le problème de jeu

Le tableau de bord temps réel est recalculé **cinq fois par seconde** sur l'état complet de la galaxie : planètes par faction, population totale par faction, planètes en famine, top 10 des plus peuplées, population moyenne.

Écrit naïvement, il alloue 16 Mo par rafraîchissement — soit 80 Mo par seconde, assez pour mettre le GC à genoux.

---

### 📉 Étape 1 — Le code sans la nouveauté

```csharp
public sealed record DashboardDto(
    IReadOnlyDictionary<FactionCode, int> PlanetesParFaction,
    IReadOnlyDictionary<FactionCode, long> PopulationParFaction,
    int Famines,
    IReadOnlyList<Planet> Top10,
    double PopulationMoyenne);

public static class DashboardBuilderDepart
{
    public static DashboardDto Build(List<Planet> planetes, PlanetWarehouse[] entrepots)
    {
        // (1) GroupBy matérialise un groupe + une liste interne par faction
        var parFaction = planetes
            .GroupBy(p => p.Owner)
            .ToDictionary(g => g.Key, g => g.Count());

        // (2) Deuxième passage, deuxième GroupBy identique
        var populationParFaction = planetes
            .GroupBy(p => p.Owner)
            .ToDictionary(g => g.Key, g => g.Sum(p => p.Population));

        // (3) Troisième passage
        var famines = planetes
            .Where((p, i) => entrepots[i][Resource.Vivres] < p.FoodDemand)
            .Count();

        // (4) Quatrième passage : tri COMPLET pour 10 éléments
        var top10 = planetes.OrderByDescending(p => p.Population).Take(10).ToList();

        // (5) Cinquième passage
        var moyenne = planetes.Select(p => p.Population).Average();

        return new DashboardDto(parFaction, populationParFaction, famines, top10, moyenne);
    }
}
```

**Ce que ça coûte** : **cinq passages** sur la collection, deux `GroupBy` identiques, et un tri complet de 10 000 éléments pour en garder dix.

---

### 📈 Étape 2 — Le même code avec la nouveauté

Deux versions, parce que le choix entre les deux est **le** livrable du sous-module.

**Version intermédiaire — les nouveaux opérateurs du BCL**

```csharp
public static class DashboardBuilderAggregateBy
{
    public static DashboardDto Build(List<Planet> planetes, PlanetWarehouse[] entrepots)
    {
        // (1') CountBy (.NET 9) : aucun groupe matérialisé
        var parFaction = planetes.CountBy(static p => p.Owner)
            .ToDictionary(static kv => kv.Key, static kv => kv.Value);

        // (2') AggregateBy (.NET 9) : agrégation par clé, sans matérialiser les groupes
        var populationParFaction = planetes
            .AggregateBy(
                keySelector: static p => p.Owner,
                seed: 0L,
                func: static (total, p) => total + p.Population)
            .ToDictionary(static kv => kv.Key, static kv => kv.Value);

        // (3') Index() (.NET 9) : plus lisible que Where((p, i) => ...)
        var famines = planetes.Index()
            .Count(x => entrepots[x.Index][Resource.Vivres] < x.Item.FoodDemand);

        var top10 = planetes.OrderByDescending(static p => p.Population).Take(10).ToList();
        var moyenne = planetes.Average(static p => p.Population);

        return new DashboardDto(parFaction, populationParFaction, famines, top10, moyenne);
    }
}
```

**Version maximale — un seul passage sur un span**

```csharp
using System.Runtime.InteropServices;

public static class DashboardBuilderSpan
{
    public static DashboardDto Build(List<Planet> planetes, PlanetWarehouse[] entrepots)
    {
        // Vue sans copie sur la List<T>
        var span = CollectionsMarshal.AsSpan(planetes);

        var parFaction = new Dictionary<FactionCode, int>(5);
        var populationParFaction = new Dictionary<FactionCode, long>(5);
        var famines = 0;
        var populationTotale = 0L;

        // Min-heap bornée : O(n log 10) au lieu de O(n log n)
        var top = new PriorityQueue<Planet, long>(11);

        for (var i = 0; i < span.Length; i++)
        {
            var planete = span[i];

            populationTotale += planete.Population;
            if (entrepots[i][Resource.Vivres] < planete.FoodDemand) famines++;

            // GetValueRefOrAddDefault : un seul hash, pas TryGetValue + indexeur
            ref var nb = ref CollectionsMarshal.GetValueRefOrAddDefault(parFaction, planete.Owner, out _);
            nb++;

            ref var pop = ref CollectionsMarshal.GetValueRefOrAddDefault(
                populationParFaction, planete.Owner, out _);
            pop += planete.Population;

            top.Enqueue(planete, planete.Population);
            if (top.Count > 10) top.Dequeue();                // on éjecte la moins peuplée
        }

        // La PriorityQueue rend d'abord la plus petite : on remplit à l'envers
        var top10 = new Planet[top.Count];
        for (var i = top10.Length - 1; i >= 0; i--) top10[i] = top.Dequeue();

        var moyenne = span.Length == 0 ? double.NaN : (double)populationTotale / span.Length;

        return new DashboardDto(parFaction, populationParFaction, famines, top10, moyenne);
    }
}
```

---

### 🔬 Étape 3 — La mesure

```csharp
[MemoryDiagnoser]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class DashboardBenchmarks
{
    private List<Planet> _planetes = null!;
    private PlanetWarehouse[] _entrepots = null!;

    [Params(100, 1_000, 10_000, 100_000)]
    public int Taille { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _planetes = [.. GalaxyBuilder.Build(Taille)];
        _entrepots = [.. _planetes.Select(p => new PlanetWarehouse(p.Type))];
    }

    [Benchmark(Baseline = true, Description = "LINQ naïf — 5 passages")]
    public DashboardDto Naif() => DashboardBuilderDepart.Build(_planetes, _entrepots);

    [Benchmark(Description = "CountBy / AggregateBy")]
    public DashboardDto AggregateBy() => DashboardBuilderAggregateBy.Build(_planetes, _entrepots);

    [Benchmark(Description = "Span — 1 passage")]
    public DashboardDto Span() => DashboardBuilderSpan.Build(_planetes, _entrepots);
}
```

**❓ Le vote, avant le tableau**

Deux questions, dans cet ordre :

1. « Entre le LINQ naïf et `AggregateBy`, quel facteur ? » — la salle propose ×2. C'est **×7**.
2. « Entre `AggregateBy` et la version span, quel facteur ? » — la salle propose ×5 après avoir été surprise. C'est **×3,7**.

Le double vote est le dispositif : la première surprise pousse à surestimer la seconde.

**Résultat**

| Méthode | Taille | Mean | Ratio | Gen0 | Allocated |
|---|---:|---:|---:|---:|---:|
| Naïf | 1 000 | 412 µs | 1,00 | 158 | 1 620 KB |
| AggregateBy | 1 000 | 58 µs | 0,14 | 9 | 92 KB |
| Span | 1 000 | 19 µs | 0,05 | 0 | 1,2 KB |
| Naïf | 10 000 | 4 180 µs | 1,00 | 1 601 | 16 400 KB |
| AggregateBy | 10 000 | **601 µs** | 0,14 | 92 | 940 KB |
| Span | 10 000 | **161 µs** | **0,04** | 0 | **1,2 KB** |
| Naïf | 100 000 | 52 100 µs | 1,00 | 16 400 | 168 000 KB |
| AggregateBy | 100 000 | 7 240 µs | 0,14 | 956 | 9 800 KB |
| Span | 100 000 | 1 690 µs | 0,03 | 0 | 1,2 KB |

---

### 🧭 Étape 4 — Pourquoi, pas à pas

**Ce que coûte un `GroupBy` (les 16 400 Ko de la version naïve, à 10 000 planètes)**

| Poste | Volume | Part |
|---|---:|---:|
| Deux `GroupBy` : buffer interne + un `Grouping` par faction + une liste par groupe | 8 900 Ko | 54 % |
| `OrderByDescending().Take(10)` : buffer + tableau de clés + tableau d'index + liste | 5 200 Ko | 32 % |
| Machines à états des cinq chaînes LINQ | 1 400 Ko | 8,5 % |
| Délégués et dictionnaires résultats | 900 Ko | 5,5 % |

`GroupBy` doit **matérialiser tous les groupes** avant de pouvoir en compter un seul : il ne peut pas savoir qu'une faction n'aura plus d'éléments avant d'avoir tout parcouru. `CountBy` et `AggregateBy` n'ont pas cette contrainte — ils tiennent un dictionnaire d'accumulateurs et ne conservent jamais les éléments.

**Ce que fait la version span en plus**

| Optimisation | Gain |
|---|---:|
| 5 passages → 1 passage | ~180 µs |
| `PriorityQueue` bornée au lieu du tri complet | ~210 µs |
| `GetValueRefOrAddDefault` : 1 hash au lieu de 2, deux fois par élément | ~35 µs |
| `CollectionsMarshal.AsSpan` : indexation directe sans appel virtuel | ~15 µs |

**Le tableau de décision — c'est lui le livrable**

| Fréquence d'appel | Taille | Version | Justification |
|---|---|---|---|
| Une fois par chargement de partie | toutes | **Naïf** | 4 ms, lisibilité maximale |
| Une fois par tick (10 s) | ≤ 10 000 | **AggregateBy** | 601 µs sur 10 s = 0,006 % du budget |
| Une fois par tick | 100 000 | **AggregateBy** | 7,2 ms sur 10 s = 0,07 % du budget |
| 5 fois par seconde | ≥ 10 000 | **Span** | le seul qui tienne, et zéro allocation |
| Chemin critique d'API | toutes | **Span** | on ne paie pas de GC sur le chemin de requête |

**`AggregateBy` gagne dans trois cas sur cinq.** La version span n'est justifiée que sur le tableau de bord temps réel — un seul endroit du code. Un développeur qui optimiserait partout aurait dégradé la lisibilité de 80 % du code pour un gain nul.

C'est la conclusion du module, et elle est plus importante que les chiffres : **la question n'est pas « quelle version est la plus rapide », c'est « quelle version à quel endroit »**.

---

### 📖 Ce qu'il faut savoir en plus

**Les opérateurs récents et ce qu'ils remplacent**

| Opérateur | Version | Remplace |
|---|---|---|
| `CountBy` | .NET 9 | `GroupBy(k).Select(g => (g.Key, g.Count()))` |
| `AggregateBy` | .NET 9 | `GroupBy(k).Select(g => (g.Key, g.Aggregate(...)))` |
| `Index()` | .NET 9 | `Select((x, i) => (i, x))` |
| `LeftJoin` / `RightJoin` | .NET 10 | `GroupJoin` + `SelectMany` + `DefaultIfEmpty` |
| `Shuffle` | .NET 10 | `OrderBy(_ => Random.Shared.Next())` |

```csharp
// LeftJoin : les planètes sans flotte en orbite sont conservées
var couverture = planetes.LeftJoin(
    flottes,
    static p => p.Code,
    static f => f.Location,
    static (planete, flotte) => new
    {
        planete.Code,
        Defenseur = flotte?.Name,
        EstDecouverte = flotte is null
    });

// Shuffle : ordre de résolution des combats, pour éviter un biais systématique
foreach (var bataille in bataillesEnAttente.Shuffle()) Resoudre(bataille);
```

**⚠️ `Shuffle` utilise un RNG non déterministe.** Dans un jeu, cela rend les parties non reproductibles et les bugs impossibles à rejouer. À isoler derrière une abstraction — sujet traité au module 6.4.

**`SearchValues<T>`** : recherche vectorisée d'un ensemble de valeurs. Construction coûteuse, usage très rapide → toujours en `static readonly`.

```csharp
private static readonly SearchValues<string> PrefixesStrategiques =
    SearchValues.Create(["VHR-", "KES-", "COR-"], StringComparison.Ordinal);

public static bool EstStrategique(ReadOnlySpan<char> code) => code.StartsWithAny(PrefixesStrategiques);
```

**`MinBy`/`MaxBy`** plutôt que `OrderBy().First()` : un seul passage au lieu d'un tri complet.

**`CollectionsMarshal.AsSpan` devient invalide** si la liste est modifiée pendant l'itération : un redimensionnement alloue un nouveau tableau interne, et le span pointe alors vers l'ancien.

---

### 🧪 Atelier — le tableau de bord temps réel

**Le contexte** : rafraîchi cinq fois par seconde sur 10 000 planètes.

**Le code de départ, fourni** : `DashboardBuilderDepart`, ci-dessus.

**Ta mission**

1. écrire **les deux** versions : `AggregateBy` et span en un passage ;
2. produire le **tableau de décision** : quelle version à quelle fréquence d'appel, justifié par tes mesures ;
3. la version span doit allouer **moins de 2 Ko** quelle que soit la taille ;
4. les deux versions doivent produire un résultat **strictement identique** au départ — écris le test ;
5. répondre en commentaire : laquelle des deux mettrais-tu en production, et pourquoi ?

**🎯 Objectif chiffré** (10 000 planètes)

| Benchmark | Départ | AggregateBy | Span |
|---|---:|---:|---:|
| Mean | 4 180 µs | **< 900 µs** | **< 250 µs** |
| Allocated | 16 400 KB | **< 1 500 KB** | **< 2 KB** |

---

### ✅ Correction

Les deux implémentations sont celles de l'étape 2. Le test d'équivalence :

```csharp
[Fact]
public void Les_trois_versions_produisent_un_resultat_identique()
{
    var planetes = (List<Planet>)[.. GalaxyBuilder.Build(1_000)];
    var entrepots = planetes.Select(p => new PlanetWarehouse(p.Type)).ToArray();

    var naif = DashboardBuilderDepart.Build(planetes, entrepots);
    var aggregateBy = DashboardBuilderAggregateBy.Build(planetes, entrepots);
    var span = DashboardBuilderSpan.Build(planetes, entrepots);

    foreach (var obtenu in new[] { aggregateBy, span })
    {
        Assert.Equal(naif.PlanetesParFaction.OrderBy(kv => kv.Key), 
                     obtenu.PlanetesParFaction.OrderBy(kv => kv.Key));
        Assert.Equal(naif.PopulationParFaction.OrderBy(kv => kv.Key),
                     obtenu.PopulationParFaction.OrderBy(kv => kv.Key));
        Assert.Equal(naif.Famines, obtenu.Famines);
        Assert.Equal(naif.PopulationMoyenne, obtenu.PopulationMoyenne, precision: 10);

        // Ordre du top 10 inclus : c'est là que les implémentations divergent le plus souvent
        Assert.Equal(naif.Top10.Select(p => p.Code), obtenu.Top10.Select(p => p.Code));
    }
}
```

**5. La réponse attendue**

```csharp
// QUELLE VERSION EN PRODUCTION ?
//
// AggregateBy pour le rafraîchissement par tick (toutes les 10 s) : 601 µs représentent
// 0,006 % du budget du tick, et le code reste lisible par toute l'équipe.
//
// Span pour le flux temps réel (5 rafraîchissements par seconde) : 5 × 601 µs = 3 ms/s
// d'allocation continue avec AggregateBy, soit 4,7 Mo/s qui remplissent gen 0.
// La version span alloue 6 Ko/s. C'est le SEUL endroit du code où l'écart justifie
// la perte de lisibilité.
//
// Autrement dit : les DEUX partent en production, à deux endroits différents.
// Ce n'est pas « la plus rapide gagne », c'est « la bonne au bon endroit ».
```

---

### 🔬 Le benchmark de l'atelier

Celui de l'étape 3, exécuté sur la solution du stagiaire.

**Résultat attendu et barème**

| Méthode | Mean | Ratio | Allocated | Verdict |
|---|---:|---:|---:|---|
| `Naif` | 4 180 µs | 1,00 | 16 400 KB | — |
| `AggregateBy_Votre` | **601 µs** | 0,14 | **940 KB** | ✅ |
| `Span_Votre` | **161 µs** | 0,04 | **1,2 KB** | ✅ |

**Les erreurs typiques**

| Ce que le stagiaire a fait | Symptôme |
|---|---|
| Gardé `OrderByDescending().Take(10)` dans la version span | Allocated à 5 200 KB — objectif manqué de loin |
| Utilisé `foreach` sur la `List<T>` au lieu du span, mais gardé le reste | 198 µs — proche, mais l'indexation directe manque |
| Alimenté la `PriorityQueue` puis appelé `.Take(10)` dessus | ordre faux : l'énumération d'une `PriorityQueue` n'est **pas** ordonnée |
| Oublié d'inverser le remplissage du top 10 | test d'équivalence en échec sur l'ordre |
| Modifié la `List<Planet>` pendant l'itération du span | comportement indéfini, parfois silencieux |
| Livré uniquement la version span | contrainte 1 non respectée, et pas de tableau de décision |

**Le piège n°1** : la troisième ligne. Une `PriorityQueue<T,TPriority>` **n'est pas énumérable dans l'ordre** — sa représentation interne est un tas binaire, pas une liste triée. `foreach` dessus retourne les éléments dans l'ordre du tableau sous-jacent. Il faut `Dequeue()` en boucle. Le bug est silencieux : le top 10 contient les bons éléments, dans le mauvais ordre. Seul le test d'équivalence l'attrape.

**Le dernier point du barème est pédagogique** : un stagiaire qui ne livre que la version span a « gagné » le benchmark mais raté l'exercice. L'objectif est le **tableau de décision**, pas le meilleur chiffre.

---

### 🎯 Point à retenir

`CountBy`, `AggregateBy` et `Index` suppriment les allocations de `GroupBy` sans sacrifier la lisibilité : **premier réflexe**. La boucle sur span est le second, et seulement là où la fréquence l'exige. Le livrable d'un benchmark n'est pas un chiffre, c'est une **décision documentée sur quelle version utiliser où**.

### 👍 Bonnes pratiques

- Un seul passage quand plusieurs agrégats sont calculés sur la même source.
- Pré-dimensionner les dictionnaires quand l'ordre de grandeur est connu (5 factions).
- `SearchValues` et `Regex` en `static readonly` : construction coûteuse, usage vectorisé.
- `MinBy`/`MaxBy` plutôt que `OrderBy().First()`.
- Écrire le tableau de décision **avant** de choisir, pas après.

### ⚠️ Points de difficulté

- `CollectionsMarshal.AsSpan` devient invalide si la liste est modifiée pendant l'itération.
- L'énumération d'une `PriorityQueue` n'est **pas** ordonnée : il faut `Dequeue`.
- `Shuffle` utilise un RNG non déterministe : à isoler pour un jeu (module 6.4).
- `AggregateBy` avec `seedSelector` évalue le seed par clé, pas une fois pour toutes.
- Une agrégation en un passage devient vite illisible : la documenter, ou la garder pour les vrais chemins chauds.

### 🚀 Pour aller plus loin

**A. Agrégation vectorisée de la production galactique**

```csharp
using System.Numerics.Tensors;

public static (double Max, double Somme, int AuDessusDuSeuil) Statistiques(
    ReadOnlySpan<double> productions, double seuil)
{
    if (productions.IsEmpty) return (double.NaN, 0, 0);

    var max = TensorPrimitives.Max(productions);        // SIMD selon le processeur
    var somme = TensorPrimitives.Sum(productions);      // SIMD

    var count = 0;
    foreach (var p in productions) if (p > seuil) count++;

    return (max, somme, count);
}
```

**B. Agrégation incrémentale sur un flux asynchrone (préfigure le module 3)**

```csharp
public static async Task<IReadOnlyDictionary<FactionCode, (int Planetes, long Population)>>
    AgregerFluxAsync(IAsyncEnumerable<Planet> flux, CancellationToken ct)
{
    var acc = new Dictionary<FactionCode, (int Planetes, long Population)>(5);

    await foreach (var planete in flux.WithCancellation(ct).ConfigureAwait(false))
    {
        ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(acc, planete.Owner, out var existait);

        slot = existait
            ? (slot.Planetes + 1, slot.Population + planete.Population)
            : (1, planete.Population);
    }

    return acc;
}
```

### ❌ Erreurs à ne pas faire

- Micro-optimiser sans benchmark : la moitié des « optimisations » LINQ manuelles sont plus lentes.
- `OrderBy().First()` au lieu de `MinBy()` (tri complet pour un seul élément).
- `.Where().Count()` au lieu de `.Count(predicate)`.
- Recréer un `SearchValues` à chaque appel.
- Remplacer tout LINQ par des boucles « parce que c'est plus rapide » : illisible, non testé, et souvent identique après JIT.

---

# 🏁 Synthèse du module 2

## Le tableau à projeter

| § | Amélioration | Gain mesuré | Axe | Vaut le coup à partir de |
|---|---|---:|---|---|
| 2.1 | Type de critère au lieu de lambdas capturantes | ×3,2 | temps + alloc | chaîne LINQ dans une boucle |
| 2.1 | Lambda `static` sans capture | 96 o → 0 o | alloc | 1 000 appels/s |
| 2.2 | `Expression` mise en cache vs `Compile()` par appel | ×8,0 | temps | filtre réutilisé ≥ 2 fois |
| 2.2 | `Expression` vs `Func` **en mémoire** | **×1,02 — plus lent** | temps | jamais (le gain est en base) |
| 2.2 | `Expression` vs `Func` **sur `IQueryable`** | ×229 | temps + lignes | toujours |
| 2.3 | Heap bornée vs tri complet | ×8 | temps + alloc | 500 éléments, N < 5 % |
| 2.3 | Buffer circulaire vs `Skip`/`Take` | ×1 484 | **complexité** | toujours |
| 2.4 | Projection serveur vs N+1 | 1 401 → **1 requête** | **requêtes SQL** | toujours |
| 2.5 | `CountBy`/`AggregateBy` vs `GroupBy` | ×7 | temps + alloc | 1 000 éléments |
| 2.5 | Span en un passage vs `AggregateBy` | ×3,7 | temps + alloc | 5 appels/s |

## Les trois enseignements du module

**1. Le poste dominant d'une chaîne LINQ n'est presque jamais celui qu'on regarde.** En 2.1, les closures pesaient 2,3 % et le `ToList()` final 73,8 %. En 2.4, le nombre de requêtes comptait, pas les millisecondes. Classer avant d'optimiser n'est pas un conseil de prudence : c'est la seule façon de ne pas perdre son temps.

**2. Deux gains du module ne sont pas des optimisations, ce sont des corrections.** Le ×1 484 de `SlidingWindows` corrige une complexité quadratique. Le passage de 1 401 à 1 requête corrige une erreur de conception. Les confondre avec du réglage fin fait rater leur nature.

**3. Le livrable d'un benchmark est une décision, pas un chiffre.** Le tableau de décision du 2.5 met **deux** versions en production, à deux endroits différents. `AggregateBy` gagne dans trois cas sur cinq — optimiser partout aurait dégradé 80 % du code pour rien.

## Vérification avant de passer au module 3

- [ ] les cinq benchmarks d'atelier au vert (`./exercise.sh 2.1` à `2.5`) ;
- [ ] su expliquer pourquoi `Expression` est **plus lente** en mémoire et ×229 plus rapide en base ;
- [ ] su dire pourquoi le test N+1 porte sur le compteur et non sur le temps ;
- [ ] su produire le tableau de décision du 2.5 sans le relire ;
- [ ] `Hyperlane.Core` qui compile avec `RoutePlanner`, `GalaxyQueryBuilder`, `TradeOperators`, `MarketAnalysis`, `EmpireReport` et les trois `DashboardBuilder`.

Ces briques sont reprises dès le module 3 : le moteur de saut appellera `TradeOperators`, et le moteur de tick agrégera le tableau de bord.
