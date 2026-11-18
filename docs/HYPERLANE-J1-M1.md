# HYPERLANE — JOUR 1 · MODULE 1 : Nouveautés de C# 13 (et 14)

> Corpus définitif — **régénéré avec le socle corrigé** : `Ship`, `Planet`, `Fleet` sont des **classes** avec méthodes métier.
> **Programme officiel** — Jour 1 : *Nouveautés de C# 13* · *LINQ & expressions lambda avancées* · *Async/Await + gestion d'erreurs*.
> **Cible technique** : C# 14 / .NET 10, avec rappel des apports C# 12/13.

---

## 📦 Le fil rouge : HYPERLANE

Les stagiaires développent le moteur d'un **jeu de gestion de planètes**. Trois verbes :

- **EXPLOITER** — chaque planète produit des ressources à chaque tick, selon son type.
- **COMMERCER** — charger une flotte, effectuer un saut hyperspatial, vendre au prix du marché local.
- **GUERROYER** — envoyer une flotte de combat, résolution en manches, capture de la planète.

---

## 🧱 Le socle de domaine

Fourni dans le dépôt de départ. **Trois principes**, à poser dès le premier quart d'heure :

- les **entités** (`Ship`, `Planet`, `Fleet`) sont des **classes** : elles ont une identité et un état qui évolue ;
- les **valeurs** (`Coordinates`, `PlanetCode`, `ResourceStock`) sont des `readonly record struct` : pas d'identité, égalité par valeur pertinente ;
- l'état d'une entité ne se modifie que par une **méthode métier**, jamais par un setter public.

```csharp
namespace Hyperlane.Core;

public enum Resource { Minerai, Cristal, Vivres, Carburant, Credits }
public enum PlanetType { Aride, Oceanique, Volcanique, Forestiere, CiteMonde, Glaciaire }
public enum FactionCode { CSL, KSY, VHR, CLB, Neutre }
public enum FleetStatus { EnOrbite, EnTransit, AuCombat, Detruite }

public sealed class DomainException(string message) : Exception(message);
```

### Les valeurs

```csharp
public readonly record struct Coordinates(double X, double Y, double Z)
{
    public double DistanceTo(Coordinates other)
    {
        var dx = X - other.X; var dy = Y - other.Y; var dz = Z - other.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}

public readonly record struct PlanetCode(string Value)
{
    public override string ToString() => Value;
}

public readonly record struct ResourceStock(Resource Resource, long Quantity);
```

### `Ship` — entité

```csharp
public sealed class Ship
{
    public Ship(string shipClass, int cargoCapacity, int firepower, int maxHull, double speed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shipClass);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxHull);

        Class = shipClass;
        CargoCapacity = cargoCapacity;
        Firepower = firepower;
        MaxHull = maxHull;
        Hull = maxHull;
        Speed = speed;
    }

    public string Class { get; }
    public int CargoCapacity { get; }
    public int Firepower { get; }
    public int MaxHull { get; }
    public double Speed { get; }

    public int Hull { get; private set; }
    public bool IsDestroyed => Hull <= 0;

    /// <summary>Encaisse des dégâts. Retourne les dégâts NON absorbés, qui débordent sur le vaisseau suivant.</summary>
    public int TakeDamage(int degats)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(degats);

        if (degats >= Hull)
        {
            var debordement = degats - Hull;
            Hull = 0;
            return debordement;
        }

        Hull -= degats;
        return 0;
    }

    public void Repair() => Hull = MaxHull;
}

public static class ShipFactory
{
    public static Ship Cargo()        => new("Cargo",        500,  5,   40,  6.0);
    public static Ship Chasseur()     => new("Chasseur",     0,    60,  25,  12.0);
    public static Ship Croiseur()     => new("Croiseur",     120,  220, 180, 4.5);
    public static Ship Ravitailleur() => new("Ravitailleur", 1200, 0,   60,  3.5);
}
```

### `Planet` — entité

```csharp
public sealed class Planet
{
    public const int MaxShieldLevel = 10;

    public Planet(PlanetCode code, string name, PlanetType type, FactionCode owner,
                  Coordinates position, long population)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfNegative(population);

        Code = code;
        Name = name;
        Type = type;
        Position = position;
        Owner = owner;
        Population = population;
    }

    // Identité et caractéristiques immuables
    public PlanetCode Code { get; }
    public string Name { get; }
    public PlanetType Type { get; }
    public Coordinates Position { get; }

    // État, modifiable uniquement par méthode métier
    public FactionCode Owner { get; private set; }
    public long Population { get; private set; }
    public int ShieldLevel { get; private set; }
    public int DefenseBatteries { get; private set; }

    public int DefenseRating => ShieldLevel * 50 + DefenseBatteries * 120;
    public bool IsColonized => Population > 0;
    public long FoodDemand => (Population + 999) / 1000;      // arrondi supérieur sans double

    // --- EXPLOITER ---

    public void Grow(long habitants)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(habitants);
        Population += habitants;
    }

    /// <summary>Applique une famine (−5 %). Retourne les habitants perdus, 0 si déjà vide.</summary>
    public long ApplyFamine()
    {
        if (Population == 0) return 0;                        // cas normal, pas une exception

        var perdus = Math.Min(Math.Max(1, Population / 20), Population);
        Population -= perdus;
        return perdus;
    }

    // --- DÉFENDRE ---

    /// <summary>Renforce le bouclier. Retourne false s'il est déjà au maximum.</summary>
    public bool UpgradeShield()
    {
        if (ShieldLevel >= MaxShieldLevel) return false;
        ShieldLevel++;
        return true;
    }

    public void BuildDefenseBattery() => DefenseBatteries++;

    /// <summary>Encaisse une manche d'assaut. Retourne la défense restante ; 0 = la planète tombe.</summary>
    public int AbsorbAssault(int degats)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(degats);

        if (degats >= DefenseRating)
        {
            ShieldLevel = 0;
            DefenseBatteries = 0;
            return 0;
        }

        // Les batteries tombent avant le bouclier
        var batteriesDetruites = Math.Min(DefenseBatteries, degats / 120);
        DefenseBatteries -= batteriesDetruites;

        var residuel = degats - batteriesDetruites * 120;
        ShieldLevel = Math.Max(0, ShieldLevel - residuel / 50);

        return DefenseRating;
    }

    // --- GUERROYER ---

    /// <exception cref="DomainException">Faction déjà propriétaire, ou défenses encore debout.</exception>
    public void Capture(FactionCode conquerant)
    {
        if (conquerant == Owner)
            throw new DomainException($"{conquerant} possède déjà {Code}.");

        if (DefenseRating > 0)
            throw new DomainException(
                $"{Code} conserve {DefenseRating} points de défense : capture impossible.");

        Owner = conquerant;
        Population -= Population / 10;                        // 10 % de pertes civiles
    }
}
```

### `Fleet` — entité

```csharp
public sealed class Fleet
{
    private readonly List<Ship> _ships;

    public Fleet(Guid id, string name, FactionCode owner, PlanetCode location, IEnumerable<Ship> ships)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(ships);

        _ships = [.. ships];
        if (_ships.Count == 0)
            throw new ArgumentException("Une flotte ne peut pas être créée vide.", nameof(ships));

        Id = id;
        Name = name;
        Owner = owner;
        Location = location;
        Status = FleetStatus.EnOrbite;
    }

    public Guid Id { get; }
    public string Name { get; }
    public FactionCode Owner { get; }                         // une flotte ne change pas de faction

    public PlanetCode Location { get; private set; }
    public PlanetCode? Destination { get; private set; }
    public FleetStatus Status { get; private set; }

    public IReadOnlyList<Ship> Ships => _ships;

    // Grandeurs dérivées — boucles manuelles : appelées à chaque tick
    public int Firepower
    {
        get { var t = 0; foreach (var s in _ships) t += s.Firepower; return t; }
    }

    public int CargoCapacity
    {
        get { var t = 0; foreach (var s in _ships) t += s.CargoCapacity; return t; }
    }

    /// <summary>La flotte va à la vitesse de son vaisseau le plus lent.</summary>
    public double Speed
    {
        get
        {
            if (_ships.Count == 0) return 0;
            var min = double.MaxValue;
            foreach (var s in _ships) if (s.Speed < min) min = s.Speed;
            return min;
        }
    }

    public bool IsCombatReady
    {
        get
        {
            if (Status != FleetStatus.EnOrbite) return false;
            foreach (var s in _ships) if (s.Firepower > 0) return true;
            return false;
        }
    }

    // --- COMMERCER / se déplacer ---

    /// <summary>Amorce un saut. Retourne false si la flotte n'est pas en orbite.</summary>
    public bool BeginJump(PlanetCode destination)
    {
        if (Status != FleetStatus.EnOrbite || destination == Location) return false;

        Destination = destination;
        Status = FleetStatus.EnTransit;
        return true;
    }

    /// <exception cref="DomainException">Si la flotte n'était pas en transit.</exception>
    public void Arrive()
    {
        if (Status != FleetStatus.EnTransit)
            throw new DomainException($"La flotte {Id} n'est pas en transit.");

        Location = Destination!.Value;
        Destination = null;
        Status = FleetStatus.EnOrbite;
    }

    /// <summary>Rappel du joueur pendant le trajet : retour à l'origine.</summary>
    public void Recall()
    {
        if (Status != FleetStatus.EnTransit)
            throw new DomainException($"La flotte {Id} n'est pas en transit.");

        Destination = null;
        Status = FleetStatus.EnOrbite;
    }

    // --- GUERROYER ---

    public void EngageCombat()
    {
        if (Status != FleetStatus.EnOrbite)
            throw new DomainException($"La flotte {Id} ne peut pas engager le combat depuis {Status}.");

        Status = FleetStatus.AuCombat;
    }

    /// <summary>Applique les dégâts d'une manche. Les plus faibles coques tombent d'abord.</summary>
    public int AbsorbDamage(int degats)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(degats);

        _ships.Sort(static (a, b) => a.Hull.CompareTo(b.Hull));

        var restant = degats;
        var detruits = 0;

        foreach (var ship in _ships)
        {
            if (restant == 0) break;
            restant = ship.TakeDamage(restant);
            if (ship.IsDestroyed) detruits++;
        }

        if (detruits > 0) _ships.RemoveAll(static s => s.IsDestroyed);
        if (_ships.Count == 0) Status = FleetStatus.Detruite;

        return detruits;
    }

    public void Disengage()
    {
        if (Status != FleetStatus.Detruite) Status = FleetStatus.EnOrbite;
    }

    /// <exception cref="DomainException">Factions différentes, ou flottes en des lieux différents.</exception>
    public void Merge(Fleet autre)
    {
        ArgumentNullException.ThrowIfNull(autre);

        if (autre.Owner != Owner)
            throw new DomainException($"Impossible de fusionner {autre.Owner} avec {Owner}.");

        if (autre.Location != Location
            || autre.Status != FleetStatus.EnOrbite || Status != FleetStatus.EnOrbite)
            throw new DomainException("Les deux flottes doivent être en orbite sur la même planète.");

        _ships.AddRange(autre._ships);
        autre._ships.Clear();
        autre.Status = FleetStatus.Detruite;
    }
}
```

> **À faire remarquer dès maintenant** : `Planet.ApplyFamine()`, `Planet.UpgradeShield()` et `Fleet.BeginJump()` retournent une valeur ; `Planet.Capture()` et `Fleet.Arrive()` lèvent. La règle qui gouverne ce choix est démontrée et **mesurée** au sous-module 1.3.

---

## 🛠️ Prise en main de l'atelier (30 min, avant le module 1)

Ce n'est pas un module du programme : c'est la mise en place de l'atelier, sans laquelle les mesures des cinq jours ne sont pas lisibles. Strictement trois choses.

**1. Vérifier l'environnement** (pendant l'accueil, pas à 9 h 45)

```bash
dotnet --version          # ≥ 10.0
git clone <dépôt-hyperlane> && cd hyperlane
dotnet build -c Release
```

**2. Lancer un premier benchmark**

```bash
./run.sh demo
```

**3. Lire le tableau — trois colonnes suffisent aujourd'hui**

```
| Method    | Mean       | Ratio | Allocated |
|---------- |-----------:|------:|----------:|
| Span      |   161.4 us |  0.04 |   1.18 KB |
| Naif      | 4,182.7 us |  1.00 | 16,004 KB |
```

| Colonne | Ce qu'elle dit |
|---|---|
| `Mean` | temps moyen par opération |
| `Ratio` | rapport à la version de référence — **le chiffre à retenir** |
| `Allocated` | octets alloués par opération |

Rien d'autre aujourd'hui. Statistique, diagnosers, compilation étagée, GC : **module 8, jour 5**. D'ici là, les stagiaires auront lu quarante tableaux, et la théorie tombera sur du vécu.

**⚠️ Logistique** : clé USB avec le cache NuGet pour les postes sans réseau, et faire lancer le benchmark **par chacun sur sa machine** — pas en démonstration.

---

## 1.1 — Collection expressions, spread et `params` collections

### 🎮 Le problème de jeu

Une flotte transporte une **cargaison** : un ensemble de couples (ressource, quantité). Le moteur en construit et en valide en permanence — au chargement, à la vente, au calcul de capacité restante. Sur 10 000 planètes, la validation de chargement est appelée environ **240 000 fois par minute**.

Cas typique où le code « normal » est correct, lisible, et alloue quand même 17 Mo par minute pour rien.

---

### 📉 Étape 1 — Le code sans la nouveauté

Ce qu'on écrit naturellement en C# 10. Ce code **fonctionne parfaitement**.

```csharp
public static class CargoManifestLegacy
{
    public static long TotalUnits(params ResourceStock[] stocks)
    {
        var total = 0L;
        foreach (var stock in stocks) total += stock.Quantity;
        return total;
    }

    public static bool FitsIn(Fleet fleet, params ResourceStock[] stocks)
    {
        var capacity = fleet.Ships.Sum(s => s.CargoCapacity);
        return TotalUnits(stocks) <= capacity;
    }

    public static ResourceStock[] BuildConvoy(
        IEnumerable<ResourceStock> lotMinier,
        IEnumerable<ResourceStock> lotSecours,
        ResourceStock complement)
    {
        return lotMinier.Concat(lotSecours).Concat(new[] { complement }).ToArray();
    }
}

var ok = CargoManifestLegacy.FitsIn(flotte,
    new ResourceStock(Resource.Minerai, 300),
    new ResourceStock(Resource.Vivres, 150),
    new ResourceStock(Resource.Carburant, 80));
```

**Ce que ça coûte** : à chaque appel de `FitsIn`, le compilateur alloue un `ResourceStock[3]` sur le tas, et `fleet.Ships.Sum(...)` alloue un énumérateur boxé — alors que **`Fleet` expose déjà `CargoCapacity`**, calculée sans allocation. `BuildConvoy` alloue trois itérateurs, un tableau intermédiaire et le tableau final.

---

### 📈 Étape 2 — Le même code avec la nouveauté

Comportement strictement identique.

```csharp
public static class CargoManifest
{
    // (1) params ReadOnlySpan<T> au lieu de params T[]
    public static long TotalUnits(params ReadOnlySpan<ResourceStock> stocks)
    {
        var total = 0L;
        foreach (var stock in stocks) total += stock.Quantity;
        return total;
    }

    public static bool FitsIn(Fleet fleet, params ReadOnlySpan<ResourceStock> stocks)
        // (2) le domaine expose déjà la capacité : ni énumérateur boxé, ni délégué
        => TotalUnits(stocks) <= fleet.CargoCapacity;

    // (3) collection expression + spread au lieu de Concat().ToArray()
    public static ResourceStock[] BuildConvoy(
        ReadOnlySpan<ResourceStock> lotMinier,
        ReadOnlySpan<ResourceStock> lotSecours,
        ResourceStock complement)
        => [..lotMinier, ..lotSecours, complement];
}

// L'appel n'a PAS changé — c'est ce qui rend la migration réaliste sur du code existant
var ok = CargoManifest.FitsIn(flotte,
    new ResourceStock(Resource.Minerai, 300),
    new ResourceStock(Resource.Vivres, 150),
    new ResourceStock(Resource.Carburant, 80));
```

---

### 🔬 Étape 3 — La mesure

```csharp
// bench/Hyperlane.Benchmarks/Day1/Module01Benchmarks.cs
[MemoryDiagnoser]
public class ParamsAndCollectionBenchmarks
{
    private Fleet _flotte = null!;
    private ResourceStock[] _lotMinier = null!;
    private ResourceStock[] _lotSecours = null!;

    [GlobalSetup]
    public void Setup()
    {
        _flotte = new Fleet(Guid.CreateVersion7(), "Convoi-1", FactionCode.CSL, new PlanetCode("KES-01"),
            [ShipFactory.Cargo(), ShipFactory.Cargo(), ShipFactory.Ravitailleur()]);

        _lotMinier = [new(Resource.Minerai, 800), new(Resource.Cristal, 120)];
        _lotSecours = [new(Resource.Vivres, 400)];
    }

    [BenchmarkCategory("Validation"), Benchmark(Baseline = true)]
    public bool FitsIn_Legacy() => CargoManifestLegacy.FitsIn(_flotte,
        new ResourceStock(Resource.Minerai, 300),
        new ResourceStock(Resource.Vivres, 150),
        new ResourceStock(Resource.Carburant, 80));

    [BenchmarkCategory("Validation"), Benchmark]
    public bool FitsIn_Span() => CargoManifest.FitsIn(_flotte,
        new ResourceStock(Resource.Minerai, 300),
        new ResourceStock(Resource.Vivres, 150),
        new ResourceStock(Resource.Carburant, 80));

    [BenchmarkCategory("Convoi"), Benchmark(Baseline = true)]
    public ResourceStock[] BuildConvoy_Concat() =>
        CargoManifestLegacy.BuildConvoy(_lotMinier, _lotSecours, new(Resource.Carburant, 200));

    [BenchmarkCategory("Convoi"), Benchmark]
    public ResourceStock[] BuildConvoy_Spread() =>
        CargoManifest.BuildConvoy(_lotMinier, _lotSecours, new(Resource.Carburant, 200));
}
```

**❓ La question, avant de montrer le tableau**

« Trois `ResourceStock` passés à une méthode. Combien d'octets alloués sur le tas par la version A ? Et par la version B ? »

Faire écrire les deux nombres. La salle propose « 24 octets » pour A (la taille des données) — c'est 72, à cause de l'en-tête d'objet et du champ de longueur. Et presque personne ne propose **0** pour B.

**Résultat** (à re-mesurer sur ta machine)

| Méthode | Mean | Ratio | Allocated |
|---|---:|---:|---:|
| `FitsIn_Legacy` | 32,4 ns | 1,00 | **136 B** |
| `FitsIn_Span` | 3,1 ns | **0,10** | **0 B** |
| `BuildConvoy_Concat` | 184 ns | 1,00 | **328 B** |
| `BuildConvoy_Spread` | 41 ns | **0,22** | **72 B** |

---

### 🧭 Étape 4 — Pourquoi, pas à pas

Décomposition des 136 octets de `FitsIn_Legacy`.

**Poste 1 — le tableau de `params` : 72 octets, supprimés**

En C# 10, `FitsIn(flotte, a, b, c)` compile en `FitsIn(flotte, new ResourceStock[] { a, b, c })`. Un `ResourceStock[3]` sur le tas = 24 o d'en-tête + 3 × 16 o = **72 octets**, à chaque appel.

Avec `params ReadOnlySpan<T>`, le compilateur émet un **buffer inline sur la pile** :

```csharp
// Ce que génère le compilateur (conceptuellement)
<>y__InlineArray3<ResourceStock> buffer = default;      // sur la PILE
InlineArrayElementRef(ref buffer, 0) = a;
InlineArrayElementRef(ref buffer, 1) = b;
InlineArrayElementRef(ref buffer, 2) = c;
FitsIn(flotte, InlineArrayAsReadOnlySpan(ref buffer, 3));
```

La pile n'est pas gérée par le GC : libération gratuite, meilleure localité de cache.

**Poste 2 — le `Sum` LINQ : 64 octets, supprimés**

`fleet.Ships.Sum(s => s.CargoCapacity)` appelle `GetEnumerator()` **sur `IReadOnlyList<Ship>`** : l'énumérateur de `List<T>` est un `struct`, mais il est **boxé** au passage par l'interface (40 o), plus le délégué (mis en cache ici car la lambda ne capture rien).

La version B ne recalcule rien : elle lit `fleet.CargoCapacity`, dont le `foreach` interne itère le `List<Ship>` **concret** — donc l'énumérateur `struct`, **sans boxing**.

> **La leçon est de conception autant que de performance** : le domaine expose déjà la grandeur dérivée. La recalculer depuis l'extérieur duplique la règle **et** paie l'indirection.

**Poste 3 — le temps, ×10**

Sur 32,4 ns : ~18 ns d'allocation du tableau (le GC avance son pointeur, initialise l'en-tête, et l'objet devra être collecté plus tard), ~11 ns d'énumération virtuelle, ~3 ns de calcul utile — exactement ce que mesure la version B.

**Les 328 octets de `BuildConvoy_Concat`**

| Élément | Octets |
|---|---:|
| `new[] { complement }` — tableau à un élément | 40 |
| Itérateur du premier `Concat` | 64 |
| Itérateur du second `Concat` | 64 |
| Buffer croissant de `ToArray()` (2 réallocations) | 88 |
| Tableau final | 72 |
| **Total** | **328** |

Avec `[..lotMinier, ..lotSecours, complement]`, le compilateur **connaît la longueur totale à la compilation** (les spans exposent `Length`) : il alloue directement le tableau final de la bonne taille et y copie les segments avec `CopyTo`. Restent les **72 octets** du résultat — inévitables, puisque c'est la valeur retournée.

**La leçon** : le gain vient de la suppression d'objets **intermédiaires**, jamais du résultat. C'est le motif de toutes les optimisations du module.

---

### 📖 Ce qu'il faut savoir en plus

**Le type cible décide de tout.** La même expression `[a, b, c]` produit un code différent selon la cible : `List<T>` → constructeur avec capacité + `Add` ; `T[]` → allocation directe ; `ImmutableArray<T>` → passage par son `[CollectionBuilder]` ; `Span<T>` → buffer de pile.

**`params` accepte bien plus que les tableaux** : `ReadOnlySpan<T>`, `Span<T>`, `List<T>`, `IEnumerable<T>`, ou tout type avec un `[CollectionBuilder]`. `ReadOnlySpan<T>` en interne, `IEnumerable<T>` sur une API publique consommée par du code async.

**`[OverloadResolutionPriority]`** (C# 13) permet d'ajouter une surcharge span à côté d'une surcharge tableau existante sans changer la résolution pour les appelants recompilés. Uniquement pour la compatibilité binaire.

```csharp
[OverloadResolutionPriority(-1)]
public static long Charger(params ResourceStock[] stocks) => ChargerCore(stocks);
public static long Charger(params ReadOnlySpan<ResourceStock> stocks) => ChargerCore(stocks);
```

**Le spread d'un `IEnumerable<T>` n'est pas gratuit** : la longueur étant inconnue, le compilateur utilise un buffer croissant. Le gain de `[..a, ..b]` n'existe pleinement que quand les sources exposent leur `Length`.

---

### 🧪 Atelier — la soute de flotte

**Le contexte** : une flotte a une soute. On y charge des ressources avant un saut, on en retire à la vente. Elle est sollicitée à chaque étape de commerce.

**Le code de départ, fourni** :

```csharp
// exercises/Day1/Ex01_CargoHold/Depart.cs
public sealed class CargoHoldDepart
{
    private readonly Dictionary<Resource, long> _contenu = new();

    public static long Capacity(Fleet fleet) => fleet.Ships.Sum(s => s.CargoCapacity);

    public bool Load(Fleet fleet, params ResourceStock[] stocks)
    {
        var demande = stocks.Sum(s => s.Quantity);
        var libre = Capacity(fleet) - _contenu.Values.Sum();

        if (demande > libre) return false;

        foreach (var stock in stocks)
        {
            if (_contenu.ContainsKey(stock.Resource))
                _contenu[stock.Resource] += stock.Quantity;
            else
                _contenu[stock.Resource] = stock.Quantity;
        }

        return true;
    }

    public ResourceStock[] BuildResupplyManifest(IEnumerable<ResourceStock> demandeLocale)
    {
        var enSoute = _contenu.Select(kv => new ResourceStock(kv.Key, kv.Value));

        return enSoute
            .Concat(demandeLocale)
            .Concat(new[] { new ResourceStock(Resource.Vivres, 50) })
            .ToArray();
    }
}
```

**Ta mission** — réécrire sans changer le comportement observable. Les tests fournis doivent tous passer.

1. `Load` accepte une liste variadique **sans allouer de tableau** ;
2. aucun `Concat`, aucun `ToList`, aucune boucle `Add` dans `BuildResupplyManifest` ;
3. la capacité ne doit pas être recalculée : **le domaine l'expose déjà** ;
4. un seul accès au dictionnaire par ressource dans `Load` (pas `ContainsKey` + indexeur).

**🎯 Objectif chiffré**

| Benchmark | Départ | À atteindre |
|---|---:|---:|
| `Load` — Mean | 412 ns | **< 90 ns** |
| `Load` — Allocated | 312 B | **0 B** |
| `BuildManifest` — Allocated | 496 B | **< 150 B** |

---

### ✅ Correction

```csharp
using System.Collections.Immutable;
using System.Runtime.InteropServices;

public sealed class CargoHold
{
    private readonly Dictionary<Resource, long> _contenu = new();

    // (1) params ReadOnlySpan : buffer de pile, zéro allocation
    public bool Load(Fleet fleet, params ReadOnlySpan<ResourceStock> stocks)
    {
        // Un span ne se capture pas dans une lambda : on somme à la main
        var demande = 0L;
        foreach (var stock in stocks)
        {
            if (stock.Quantity < 0)
                throw new ArgumentException("Quantité négative interdite.", nameof(stocks));
            demande += stock.Quantity;
        }

        // (3) On lit la propriété du domaine au lieu de recalculer
        if (demande > fleet.CargoCapacity - TotalCharge()) return false;

        foreach (var stock in stocks)
        {
            // (4) GetValueRefOrAddDefault : UN hash, référence directe dans le dictionnaire
            ref var quantite = ref CollectionsMarshal.GetValueRefOrAddDefault(
                _contenu, stock.Resource, out _);
            quantite += stock.Quantity;
        }

        return true;
    }

    // (2) Une seule collection expression, un seul buffer final
    public ImmutableArray<ResourceStock> BuildResupplyManifest(IEnumerable<ResourceStock> demandeLocale)
    {
        var enSoute = new ResourceStock[_contenu.Count];
        var i = 0;
        foreach (var (resource, quantity) in _contenu)
            enSoute[i++] = new ResourceStock(resource, quantity);

        return [..enSoute, ..demandeLocale, new ResourceStock(Resource.Vivres, 50)];
    }

    private long TotalCharge()
    {
        var total = 0L;
        foreach (var quantite in _contenu.Values) total += quantite;
        return total;
    }
}
```

**Trois points à commenter en salle**

La méthode `Capacity` **a disparu**. C'est le point le plus intéressant de la correction : la capacité d'une flotte est une propriété de la flotte, pas de la soute. La recalculer ailleurs dupliquait la règle. Un stagiaire qui garde `Capacity` a un code plus lent **et** une règle en double.

`GetValueRefOrAddDefault` retourne une **référence** vers la valeur : on incrémente sur place, sans double lookup.

La collection expression finale cible `ImmutableArray<ResourceStock>` : le compilateur utilise son `[CollectionBuilder]`, donc **un seul buffer final**.

---

### 🔬 Le benchmark de l'atelier

```csharp
// bench/Hyperlane.Benchmarks/Exercises/Ex01_CargoHoldBenchmarks.cs
[MemoryDiagnoser]
[Orderer(SummaryOrderPolicy.Declared)]
public class Ex01_CargoHoldBenchmarks
{
    private Fleet _flotte = null!;
    private CargoHoldDepart _depart = null!;
    private CargoHold _votre = null!;              // <- la classe du stagiaire
    private ResourceStock[] _demandeLocale = null!;

    [GlobalSetup]
    public void Setup()
    {
        _flotte = new Fleet(Guid.CreateVersion7(), "Convoi-1", FactionCode.CSL, new PlanetCode("KES-01"),
            [ShipFactory.Cargo(), ShipFactory.Cargo(), ShipFactory.Ravitailleur()]);

        _demandeLocale = [new(Resource.Cristal, 40), new(Resource.Carburant, 90)];

        _depart = new CargoHoldDepart();
        _votre = new CargoHold();

        // Même état initial : la comparaison n'a de sens qu'à état égal
        _depart.Load(_flotte, new ResourceStock(Resource.Minerai, 100));
        _votre.Load(_flotte, new ResourceStock(Resource.Minerai, 100));
    }

    [BenchmarkCategory("Load"), Benchmark(Baseline = true, Description = "Départ (fourni)")]
    public bool Load_Depart() => _depart.Load(_flotte,
        new ResourceStock(Resource.Minerai, 1),
        new ResourceStock(Resource.Vivres, 1),
        new ResourceStock(Resource.Carburant, 1));

    [BenchmarkCategory("Load"), Benchmark(Description = "Votre solution")]
    public bool Load_Votre() => _votre.Load(_flotte,
        new ResourceStock(Resource.Minerai, 1),
        new ResourceStock(Resource.Vivres, 1),
        new ResourceStock(Resource.Carburant, 1));

    [BenchmarkCategory("Manifest"), Benchmark(Baseline = true, Description = "Départ (fourni)")]
    public int Manifest_Depart() => _depart.BuildResupplyManifest(_demandeLocale).Length;

    [BenchmarkCategory("Manifest"), Benchmark(Description = "Votre solution")]
    public int Manifest_Votre() => _votre.BuildResupplyManifest(_demandeLocale).Length;
}
```

```bash
./exercise.sh 1.1
```

**Résultat attendu et barème**

| Méthode | Mean | Ratio | Allocated | Verdict |
|---|---:|---:|---:|---|
| `Load_Depart` | 412 ns | 1,00 | 312 B | — |
| `Load_Votre` | **78 ns** | 0,19 | **0 B** | ✅ |
| `Manifest_Depart` | 486 ns | 1,00 | 496 B | — |
| `Manifest_Votre` | **142 ns** | 0,29 | **112 B** | ✅ |

**Les erreurs typiques**

| Ce que le stagiaire a fait | Symptôme au benchmark |
|---|---|
| Gardé `stocks.Sum(...)` sur le span | ne compile pas — un span ne se capture pas dans une lambda |
| Gardé `params ResourceStock[]` | `Allocated` reste à 72 B minimum |
| Gardé la méthode `Capacity` avec `Sum` | Mean autour de 130 ns, et la règle est en double |
| Gardé `ContainsKey` + indexeur | Mean autour de 110 ns au lieu de 78 |
| Utilisé `Concat` puis `[..]` | `Allocated` autour de 300 B |

**Le piège n°1** : un stagiaire écrit `stocks.ToArray()` en début de méthode « pour pouvoir utiliser LINQ ». Le code compile, les tests passent, et le benchmark affiche `Allocated: 72 B`. C'est le moment pédagogique visé — le benchmark révèle ce que la relecture ne montre pas.

---

### 🎯 Point à retenir

Une collection expression n'est pas du sucre syntaxique : **le type cible décide de la stratégie d'allocation**. Et le gain vient de la suppression des objets **intermédiaires**, jamais du résultat.

### 👍 Bonnes pratiques

- `params ReadOnlySpan<T>` pour toute API variadique appelée en boucle.
- `IEnumerable<T>` en entrée d'API publique, `ReadOnlySpan<T>` en interne.
- Lire la grandeur dérivée **exposée par l'entité** plutôt que la recalculer depuis l'extérieur.
- `GetValueRefOrAddDefault` dès qu'on lit puis écrit la même entrée de dictionnaire.

### ⚠️ Points de difficulté

- Un `ReadOnlySpan<T>` ne se capture ni dans une lambda, ni dans une méthode `async`, ni dans un itérateur.
- `[..maListe]` crée une copie, ce n'est pas `AsSpan()`.
- Le spread d'un `IEnumerable<T>` de longueur inconnue force un buffer croissant.
- Ajouter une surcharge span à côté d'une surcharge tableau change la résolution pour les appelants recompilés.

### 🚀 Pour aller plus loin

**A. Rendre un type du domaine compatible collection expression**

```csharp
[CollectionBuilder(typeof(ManifestBuilder), nameof(ManifestBuilder.Create))]
public sealed class Manifest : IEnumerable<ResourceStock>
{
    private readonly ResourceStock[] _items;

    internal Manifest(ResourceStock[] items)
    {
        _items = items;
        foreach (var item in items) TotalUnits += item.Quantity;
    }

    public int Count => _items.Length;
    public long TotalUnits { get; }

    public IEnumerator<ResourceStock> GetEnumerator() => ((IEnumerable<ResourceStock>)_items).GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

public static class ManifestBuilder
{
    public static Manifest Create(ReadOnlySpan<ResourceStock> items) => new(items.ToArray());
}

Manifest convoi = [new(Resource.Minerai, 500), ..lotSecours];
```

**B. Buffer inline pour les cinq ressources du jeu**

```csharp
[InlineArray(5)]
public struct ResourceBuffer { private long _first; }

ResourceBuffer buffer = default;
Span<long> production = buffer;                  // vit sur la pile
production[(int)Resource.Minerai] = 240;
var total = ProductionTotale(production);        // params ReadOnlySpan<long>
```

### ❌ Erreurs à ne pas faire

- Remplacer `T[]` par `ReadOnlySpan<T>` dans une signature publique déployée sans `[OverloadResolutionPriority]`.
- Écrire `[..list]` en croyant éviter une copie.
- Recalculer depuis l'extérieur une grandeur que l'entité expose déjà.
- Optimiser une API variadique appelée trois fois au démarrage.

---

## 1.2 — `ref struct`, `Span<T>` et `allows ref struct`

### 🎮 Le problème de jeu

Les colonies envoient chaque tick un **rapport CSV** : une ligne par colonie, cinq champs (`code;population;minerai;cristal;vivres`). Sur 10 000 colonies, c'est 10 000 lignes à parser toutes les 10 secondes.

Le parsing naïf alloue 6,2 Mo par rapport. Six fois par minute : 37 Mo/minute de déchets, assez pour déclencher des collectes gen 2 régulières et faire saccader le jeu.

---

### 📉 Étape 1 — Le code sans la nouveauté

```csharp
/// <summary>
/// Instantané de télémétrie : une VALEUR, sans identité et jamais mutée.
/// C'est le seul cas du socle où le record struct est le bon choix.
/// </summary>
public readonly record struct PlanetTelemetry(
    PlanetCode Code, long Population, int Minerai, int Cristal, int Vivres);

public static class ColonyReportParserLegacy
{
    public static PlanetTelemetry[] ParseAll(string rapport)
    {
        // (1) Split alloue le tableau ET une string par ligne
        var lignes = rapport.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var resultat = new List<PlanetTelemetry>(lignes.Length);

        foreach (var ligne in lignes)
        {
            var propre = ligne.Trim();                      // (2) une string de plus
            if (propre.Length == 0 || propre[0] == '#') continue;

            var champs = propre.Split(';');                 // (3) tableau + 5 strings par ligne
            if (champs.Length != 5) throw new FormatException($"Ligne invalide : {propre}");

            resultat.Add(new PlanetTelemetry(
                new PlanetCode(champs[0].Trim()),           // (4) encore une string
                long.Parse(champs[1], CultureInfo.InvariantCulture),
                int.Parse(champs[2], CultureInfo.InvariantCulture),
                int.Parse(champs[3], CultureInfo.InvariantCulture),
                int.Parse(champs[4], CultureInfo.InvariantCulture)));
        }

        return resultat.ToArray();                          // (5) copie complète
    }
}
```

**Ce que ça coûte** : pour 10 000 lignes, environ **90 000 objets** alloués.

---

### 📈 Étape 2 — Le même code avec la nouveauté

```csharp
public ref struct ColonyReportReader
{
    private ReadOnlySpan<char> _remaining;
    private bool _finished;

    public ColonyReportReader(ReadOnlySpan<char> source)
    {
        _remaining = source;
        _finished = false;
        Current = default;
    }

    public ReadOnlySpan<char> Current { get; private set; }

    // Pattern-based foreach : pas d'IEnumerable, donc utilisable sur un ref struct
    public ColonyReportReader GetEnumerator() => this;

    public bool MoveNext()
    {
        while (!_finished)
        {
            var index = _remaining.IndexOf('\n');

            if (index < 0)
            {
                Current = _remaining.Trim();                // (2') Trim sur un span : une VUE
                _finished = true;
            }
            else
            {
                Current = _remaining[..index].Trim();       // (1') slice : aucune allocation
                _remaining = _remaining[(index + 1)..];
            }

            if (!Current.IsEmpty && !Current.StartsWith("#")) return true;
            if (_finished) return false;
        }

        return false;
    }
}

public static class ColonyReportParser
{
    public static PlanetTelemetry ParseLine(ReadOnlySpan<char> ligne)
    {
        Span<Range> champs = stackalloc Range[5];           // (3') 5 Range sur la PILE
        var count = ligne.Split(champs, ';', StringSplitOptions.TrimEntries);

        if (count != 5) throw new FormatException($"5 champs attendus, {count} reçus : « {ligne} »");

        return new PlanetTelemetry(
            new PlanetCode(new string(ligne[champs[0]])),   // (4') SEULE allocation nécessaire
            long.Parse(ligne[champs[1]], CultureInfo.InvariantCulture),
            int.Parse(ligne[champs[2]], CultureInfo.InvariantCulture),
            int.Parse(ligne[champs[3]], CultureInfo.InvariantCulture),
            int.Parse(ligne[champs[4]], CultureInfo.InvariantCulture));
    }

    // (5') L'appelant fournit la destination : rien n'est alloué ici
    public static int ParseAll(ReadOnlySpan<char> rapport, Span<PlanetTelemetry> destination)
    {
        var written = 0;

        foreach (var ligne in new ColonyReportReader(rapport))
        {
            if (written >= destination.Length) break;
            destination[written++] = ParseLine(ligne);
        }

        return written;
    }
}
```

---

### 🔬 Étape 3 — La mesure

```csharp
[MemoryDiagnoser]
public class SpanParsingBenchmarks
{
    private string _rapport = null!;
    private PlanetTelemetry[] _destination = null!;

    [Params(1_000, 10_000)]
    public int Lignes { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var sb = new StringBuilder(Lignes * 40);
        for (var i = 0; i < Lignes; i++)
            sb.Append($"KES-{i % 100:D2};{1_000_000 + i};{500 + i};{120 + i};{300 + i}\n");

        _rapport = sb.ToString();
        _destination = new PlanetTelemetry[Lignes];
    }

    [Benchmark(Baseline = true)]
    public int StringSplit() => ColonyReportParserLegacy.ParseAll(_rapport).Length;

    [Benchmark]
    public int SpanReader() => ColonyReportParser.ParseAll(_rapport, _destination);
}
```

**❓ La question, avant le tableau**

« La version B alloue-t-elle zéro octet ? »

La salle répond majoritairement oui — c'est l'attente créée par le discours « span = zéro allocation ». La réponse est **non** : 624 Ko subsistent. Cette question prépare l'enseignement le plus important du bloc.

**Résultat**

| Méthode | Lignes | Mean | Ratio | Gen0 | Allocated |
|---|---:|---:|---:|---:|---:|
| `StringSplit` | 1 000 | 482 µs | 1,00 | 152,3 | 624 KB |
| `SpanReader` | 1 000 | 118 µs | 0,24 | 14,6 | 62 KB |
| `StringSplit` | 10 000 | **4 820 µs** | 1,00 | 1 523,4 | **6 240 KB** |
| `SpanReader` | 10 000 | **1 190 µs** | **0,25** | 146,4 | **624 KB** |

---

### 🧭 Étape 4 — Pourquoi, pas à pas

**D'où viennent les 6 240 Ko ?** Décomposition pour 10 000 lignes :

| Élément | Nombre | Unitaire | Total |
|---|---:|---:|---:|
| Tableau de lignes (`Split('\n')`) | 1 | 80 Ko | 80 Ko |
| String par ligne | 10 000 | ~64 o | 640 Ko |
| String issue de `Trim()` | 10 000 | ~64 o | 640 Ko |
| Tableau de champs par ligne | 10 000 | 64 o | 640 Ko |
| Strings de champs (5 par ligne) | 50 000 | ~40 o | 2 000 Ko |
| String du `champs[0].Trim()` | 10 000 | ~40 o | 400 Ko |
| `PlanetCode` (les strings conservées) | 10 000 | ~40 o | 400 Ko |
| Réallocations `List<T>` + `ToArray()` | — | — | ~1 440 Ko |
| **Total** | **~90 000 objets** | | **~6 240 Ko** |

**Ce que fait la version B, étape par étape**

*Le découpage des lignes.* `_remaining[..index]` ne copie rien : un `ReadOnlySpan<char>` est un couple (pointeur, longueur). Découper une string de 400 Ko en 10 000 lignes coûte 10 000 fois « ajuster deux entiers ». Le `Trim()` d'un span déplace le pointeur et réduit la longueur. **1 360 Ko supprimés.**

*Le découpage des champs.* `MemoryExtensions.Split(Span<Range>, char)` remplit un tableau de `Range` — cinq couples (début, fin) — sur la pile. Aucun objet créé. **2 640 Ko supprimés.**

*Le parsing des nombres.* `long.Parse(ReadOnlySpan<char>)` existe via `ISpanParsable<T>` : il lit directement les caractères du span. C'est ce qui rend le reste possible.

*La destination.* `ParseAll` **reçoit** un `Span<PlanetTelemetry>` au lieu de retourner un tableau. Les 1 440 Ko de `List<T>` + `ToArray()` disparaissent, et l'appelant peut réutiliser le buffer d'un tick à l'autre.

> **La performance se décide dans les signatures, pas dans les corps de méthode.** Si `ParseAll` retournait `List<PlanetTelemetry>`, tout le travail sur les spans serait annulé par cette seule allocation.

**Et les 624 Ko restants ?** Ce sont les 10 000 `PlanetCode`, conservés par le résultat. Ce n'est pas du déchet, c'est la valeur produite.

> L'objectif n'est jamais « zéro allocation », c'est **zéro allocation évitable**.

**Pourquoi le temps est-il divisé par 4 et pas par 10 ?** Le parsing des nombres — du calcul réel — représente ~800 µs dans les deux versions. Le gain de 3 630 µs porte entièrement sur les allocations et le travail du GC (1 523 collectes gen 0 contre 146). Le calcul utile est incompressible.

---

### 📖 Ce qu'il faut savoir en plus

**`ref struct` = pile obligatoire.** C'est ce qui rend `Span<T>` sûr : le compilateur vérifie les durées de vie. Interdit en champ de classe, en `async`, en itérateur, en capture de lambda.

**`allows ref struct`** (C# 13) est une **anti-contrainte** : `where T : IFrameSource, allows ref struct` déclare que `T` *peut* être un `ref struct`, ce qui **interdit** dans le corps de la méthode toute opération incompatible. C'est une restriction ajoutée, pas une permission.

**Conversions implicites de span** (C# 14) : `T[]` → `Span<T>` → `ReadOnlySpan<T>` sont désormais des conversions de première classe, reconnues par la résolution de surcharge et les méthodes d'extension.

```csharp
double[] releves = [240.5, 238.0, 251.2];
var moyenne = MoyenneProduction(releves);      // C# 14 : plus de .AsSpan()
```

**`stackalloc` en boucle** : acceptable si la taille est **fixe et petite** — le compilateur réutilise le même emplacement de pile à chaque itération. Débordement garanti si la taille est variable.

---

### 🧪 Atelier — le décodeur de trames HoloNet

**Le contexte** : les planètes émettent aussi des **trames binaires** de 24 octets. Format : `[3 octets code][1 octet numéro][8 population][4 minerai][4 cristal][4 vivres]`.

**Le code de départ, fourni** :

```csharp
// exercises/Day1/Ex02_HoloNet/Depart.cs
public static class HoloNetDecoderDepart
{
    public static List<PlanetTelemetry> DecodeAll(byte[] flux)
    {
        var resultat = new List<PlanetTelemetry>();

        for (var offset = 0; offset + 24 <= flux.Length; offset += 24)
        {
            var trame = new byte[24];
            Array.Copy(flux, offset, trame, 0, 24);                 // (a) une copie par trame

            var lettres = Encoding.ASCII.GetString(trame, 0, 3);    // (b) une string
            var numero = trame[3];
            var code = lettres + "-" + numero.ToString("D2");       // (c) deux strings de plus

            resultat.Add(new PlanetTelemetry(
                new PlanetCode(code),
                BitConverter.ToInt64(trame, 4),
                BitConverter.ToInt32(trame, 12),
                BitConverter.ToInt32(trame, 16),
                BitConverter.ToInt32(trame, 20)));
        }

        return resultat;
    }
}
```

**Ta mission**

1. `Decode(ReadOnlySpan<byte> trame)` décode **une** trame, sans copie ;
2. `DecodeAll(ReadOnlySpan<byte> flux, Span<PlanetTelemetry> destination)` remplit un buffer fourni ;
3. la construction du `PlanetCode` produit **une seule** allocation par trame ;
4. un flux tronqué en fin est ignoré, sans exception.

**🎯 Objectif chiffré** (10 000 trames)

| Benchmark | Départ | À atteindre |
|---|---:|---:|
| Mean | 3 420 µs | **< 900 µs** |
| Allocated | 3 840 KB | **< 700 KB** |

---

### ✅ Correction

```csharp
using System.Buffers.Binary;

public static class HoloNetDecoder
{
    public const int FrameSize = 24;

    // (1) Aucune copie : on lit directement dans le span reçu
    public static PlanetTelemetry Decode(ReadOnlySpan<byte> trame)
    {
        if (trame.Length < FrameSize)
            throw new ArgumentException($"Trame tronquée ({trame.Length}/{FrameSize}).", nameof(trame));

        // (3) Une seule allocation : la string du code, construite sur un buffer de pile
        Span<char> code = stackalloc char[6];
        code[0] = (char)trame[0];
        code[1] = (char)trame[1];
        code[2] = (char)trame[2];
        code[3] = '-';

        var numero = trame[3];
        code[4] = (char)('0' + numero / 10);
        code[5] = (char)('0' + numero % 10);

        return new PlanetTelemetry(
            Code: new PlanetCode(new string(code)),
            // BinaryPrimitives : lecture directe, endianness explicite, aucune copie
            Population: BinaryPrimitives.ReadInt64LittleEndian(trame[4..12]),
            Minerai:    BinaryPrimitives.ReadInt32LittleEndian(trame[12..16]),
            Cristal:    BinaryPrimitives.ReadInt32LittleEndian(trame[16..20]),
            Vivres:     BinaryPrimitives.ReadInt32LittleEndian(trame[20..24]));
    }

    // (2) L'appelant fournit la destination
    public static int DecodeAll(ReadOnlySpan<byte> flux, Span<PlanetTelemetry> destination)
    {
        var written = 0;
        var offset = 0;

        // (4) La condition d'arrêt ignore naturellement une fin de flux tronquée
        while (offset + FrameSize <= flux.Length && written < destination.Length)
        {
            destination[written++] = Decode(flux.Slice(offset, FrameSize));
            offset += FrameSize;
        }

        return written;
    }
}
```

---

### 🔬 Le benchmark de l'atelier

```csharp
[MemoryDiagnoser]
public class Ex02_HoloNetBenchmarks
{
    private byte[] _flux = null!;
    private PlanetTelemetry[] _destination = null!;

    [Params(10_000)]
    public int Trames { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _flux = HoloNetFluxBuilder.Build(Trames);          // flux déterministe
        _destination = new PlanetTelemetry[Trames];
    }

    [Benchmark(Baseline = true, Description = "Départ (fourni)")]
    public int Depart() => HoloNetDecoderDepart.DecodeAll(_flux).Count;

    [Benchmark(Description = "Votre solution")]
    public int Votre() => HoloNetDecoder.DecodeAll(_flux, _destination);
}
```

**Résultat attendu et barème**

| Méthode | Mean | Ratio | Allocated | Verdict |
|---|---:|---:|---:|---|
| `Depart` | 3 420 µs | 1,00 | 3 840 KB | — |
| `Votre` | **812 µs** | 0,24 | **624 KB** | ✅ |

**Décomposition du gain, à faire retrouver par les stagiaires**

| Ce qui disparaît | Contribution |
|---|---:|
| `new byte[24]` + `Array.Copy` par trame | 240 Ko |
| `Encoding.ASCII.GetString` (3 caractères) | 400 Ko |
| Concaténation `lettres + "-" + numero.ToString("D2")` | 2 100 Ko |
| Réallocations de la `List<T>` | 476 Ko |
| **Restant** | 624 Ko — les `PlanetCode`, c'est-à-dire le résultat |

**Les erreurs typiques**

| Ce que le stagiaire a fait | Symptôme |
|---|---|
| `Encoding.ASCII.GetString(trame[..3])` | `Allocated` autour de 1 000 KB |
| `$"{lettres}-{numero:D2}"` | `Allocated` autour de 2 700 KB |
| Retourné `List<PlanetTelemetry>` | `Allocated` autour de 1 100 KB |
| `BitConverter.ToInt64(trame.ToArray(), 4)` | **ratio > 1** : plus lent que le départ |
| Levé une exception sur flux tronqué | test `Flux_tronque_ignore_la_fin` en échec |

**Le piège n°1** : l'avant-dernière ligne. Un stagiaire qui écrit `trame.ToArray()` pour réutiliser `BitConverter` obtient un code **plus lent que le départ**. C'est la démonstration la plus efficace que « utiliser `Span` » ne suffit pas — il faut utiliser les API qui l'acceptent.

---

### 🎯 Point à retenir

Une API zéro-allocation se reconnaît à sa **signature** : elle reçoit son buffer, elle ne le crée pas. Et l'objectif n'est jamais zéro allocation, mais zéro allocation **évitable**.

### 👍 Bonnes pratiques

- `ReadOnlySpan<T>` en entrée ; jamais un span pointant vers un buffer temporaire en retour.
- `readonly ref struct` dès que l'état n'est pas muté : le JIT évite les copies défensives.
- `stackalloc` hors boucle si la taille varie ; taille bornée (~1 Ko).
- `BinaryPrimitives` plutôt que `BitConverter` : endianness explicite, accepte les spans.

### ⚠️ Points de difficulté

- `CS8352`/`CS8347` (« cannot escape its declaring scope ») est un problème de durée de vie, pas de type.
- `ref struct` interdit en `async` et dans les itérateurs, même avec `allows ref struct`.
- Un `ref struct` implémentant une interface ne peut pas être converti vers elle.
- `Encoding.GetString` alloue toujours, même sur trois caractères.

### 🚀 Pour aller plus loin

**A. Générique acceptant un `ref struct`**

```csharp
public interface IFrameSource
{
    ReadOnlySpan<byte> Current { get; }
    bool MoveNext();
}

public static int CountFrames<T>(ref T source) where T : IFrameSource, allows ref struct
{
    var count = 0;
    while (source.MoveNext()) count++;
    return count;                     // aucun boxing, appel dévirtualisé par le JIT
}

public ref struct BufferFrameSource(ReadOnlySpan<byte> buffer) : IFrameSource
{
    private ReadOnlySpan<byte> _remaining = buffer;
    public ReadOnlySpan<byte> Current { get; private set; }

    public bool MoveNext()
    {
        if (_remaining.Length < HoloNetDecoder.FrameSize) return false;
        Current = _remaining[..HoloNetDecoder.FrameSize];
        _remaining = _remaining[HoloNetDecoder.FrameSize..];
        return true;
    }
}
```

**B. Supprimer les 624 Ko restants — le code planète compacté**

```csharp
// Un PlanetCode tient sur 4 octets : 3 lettres ASCII + 1 numéro.
public readonly record struct PlanetCodeCompact(int Packed)
{
    public static PlanetCodeCompact FromFrame(ReadOnlySpan<byte> trame) =>
        new(trame[0] << 24 | trame[1] << 16 | trame[2] << 8 | trame[3]);

    public override string ToString()
    {
        Span<char> buffer = stackalloc char[6];
        buffer[0] = (char)(Packed >> 24);
        buffer[1] = (char)(Packed >> 16 & 0xFF);
        buffer[2] = (char)(Packed >> 8 & 0xFF);
        buffer[3] = '-';
        var numero = Packed & 0xFF;
        buffer[4] = (char)('0' + numero / 10);
        buffer[5] = (char)('0' + numero % 10);
        return new string(buffer);
    }
}

// 624 KB -> 0 B. Prix : le débogueur affiche un entier, et ToString() alloue à l'affichage.
// À réserver au chemin le plus chaud (module 8).
```

### ❌ Erreurs à ne pas faire

- Retourner un `Span<T>` construit sur un `stackalloc` local.
- Appeler `.ToArray()` sur un span pour réutiliser une API qui ne l'accepte pas.
- Exposer `Span<T>` dans une API consommée par du code `async` — exposer `Memory<T>`.
- Croire que « utiliser `Span` » suffit : il faut aussi les API qui l'acceptent.

---

## 1.3 — Le mot-clé `field` et les invariants dans le type

### 🎮 Le problème de jeu

Les stocks d'une planète ne doivent **jamais** devenir négatifs. En pratique ils le deviennent : une vente qui retire plus que le stock, une famine mal calculée. Chaque fois, la partie se retrouve dans un état incohérent que les joueurs remarquent avant l'équipe.

Deuxième problème, moins visible : quand une planète manque de vivres, la question « que faire ? » a deux réponses possibles en code, et l'une des deux coûte cher.

> **Lien avec le socle** : `Planet` protège déjà sa population par `ApplyFamine()`. Ce sous-module traite l'**entrepôt de ressources**, qui n'a pas encore ses invariants — et il démontre la règle qui gouverne tous les retours du socle.

---

### 📉 Étape 1 — Le code sans la nouveauté

```csharp
public sealed class PlanetWarehouseDepart
{
    // (1) Champs privés explicites : visibles depuis TOUT le type, donc contournables
    private readonly Dictionary<Resource, long> _stocks = new();
    private readonly Dictionary<Resource, long> _capacites = new();
    private long _lastUpdatedTick;

    public PlanetWarehouseDepart(PlanetType type)
    {
        foreach (var resource in Enum.GetValues<Resource>())
        {
            _stocks[resource] = 0;
            _capacites[resource] = CapacityFor(type, resource);
        }
    }

    public long LastUpdatedTick => _lastUpdatedTick;
    public long this[Resource resource] => _stocks[resource];

    public void Store(Resource resource, long amount)
    {
        if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount));
        _stocks[resource] = Math.Min(_capacites[resource], _stocks[resource] + amount);
        _lastUpdatedTick++;
    }

    // (2) Le choix structurant : l'échec est une EXCEPTION
    public long Withdraw(Resource resource, long amount)
    {
        if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount));

        var stock = _stocks[resource];
        if (stock < amount) throw new InsufficientStockException(resource, amount, stock);

        _stocks[resource] = stock - amount;
        _lastUpdatedTick++;
        return amount;
    }

    private static long CapacityFor(PlanetType type, Resource resource) => (type, resource) switch
    {
        (_, Resource.Credits)                     => long.MaxValue,
        (PlanetType.CiteMonde, _)                 => 50_000,
        (PlanetType.Volcanique, Resource.Minerai) => 120_000,
        _                                         => 40_000
    };
}

// Le code appelant, à chaque tick, sur chaque planète :
try
{
    entrepot.Withdraw(Resource.Vivres, planete.FoodDemand);
}
catch (InsufficientStockException)
{
    var perdus = planete.ApplyFamine();                  // la famine : un cas de jeu NORMAL
    journal.Add(new FamineDeclaree(planete.Code, perdus));
}
```

---

### 📈 Étape 2 — Le même code avec la nouveauté

```csharp
public sealed class PlanetStock
{
    public PlanetStock(Resource resource, long capacity)
    {
        Resource = resource;
        Capacity = capacity;
    }

    public Resource Resource { get; }

    // (1') Plancher garanti par le type lui-même, sans champ visible ailleurs
    public long Quantity { get; private set => field = Math.Max(0, value); }

    public long Capacity
    {
        get => field == 0 ? long.MaxValue : field;
        init => field = value < 0
            ? throw new ArgumentOutOfRangeException(nameof(value), "Capacité négative interdite.")
            : value;
    }

    public bool IsFull => Quantity >= Capacity;

    /// <summary>Ajoute au plus ce que la capacité permet. Retourne la quantité réellement stockée.</summary>
    public long Produce(long amount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(amount);

        var avant = Quantity;
        Quantity = Math.Min(Capacity, Quantity + amount);
        return Quantity - avant;
    }

    /// <summary>Retire au plus <paramref name="amount"/>. Retourne la quantité réellement retirée.</summary>
    public long Consume(long amount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(amount);

        var retire = Math.Min(amount, Quantity);
        Quantity -= retire;
        return retire;
    }
}

public sealed class PlanetWarehouse
{
    private readonly Dictionary<Resource, PlanetStock> _stocks;

    public PlanetWarehouse(PlanetType type)
    {
        Type = type;
        _stocks = Enum.GetValues<Resource>()
            .ToDictionary(r => r, r => new PlanetStock(r, CapacityFor(type, r)));
    }

    public PlanetType Type { get; }
    public long LastUpdatedTick { get; private set; }

    public long this[Resource resource] => _stocks[resource].Quantity;

    public void Store(Resource resource, long amount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(amount);
        if (amount == 0) return;

        if (_stocks[resource].Produce(amount) > 0) LastUpdatedTick++;
    }

    // (2') Le choix structurant : l'échec partiel est une VALEUR DE RETOUR
    /// <summary>Retire au plus <paramref name="amount"/>, retourne la quantité réellement retirée.</summary>
    public long Withdraw(Resource resource, long amount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(amount);
        if (amount == 0) return 0;

        var retire = _stocks[resource].Consume(amount);
        if (retire > 0) LastUpdatedTick++;
        return retire;
    }

    private static long CapacityFor(PlanetType type, Resource resource) => (type, resource) switch
    {
        (_, Resource.Credits)                     => long.MaxValue,
        (PlanetType.CiteMonde, _)                 => 50_000,
        (PlanetType.Volcanique, Resource.Minerai) => 120_000,
        _                                         => 40_000
    };
}

// Le code appelant devient :
var demande = planete.FoodDemand;
var servi = entrepot.Withdraw(Resource.Vivres, demande);

if (servi < demande)                                    // aucune exception
{
    var perdus = planete.ApplyFamine();
    journal.Add(new FamineDeclaree(planete.Code, perdus));
}
```

---

### 🔬 Étape 3 — La mesure

Deux axes distincts : le coût de la propriété semi-auto, et le coût du choix exception/valeur.

```csharp
[MemoryDiagnoser]
public class WarehouseBenchmarks
{
    private PlanetWarehouseDepart _depart = null!;
    private PlanetWarehouse _avec = null!;

    [Params(0.00, 0.05, 0.20)]           // proportion de planètes en famine à ce tick
    public double TauxFamine { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _depart = new PlanetWarehouseDepart(PlanetType.Aride);
        _avec = new PlanetWarehouse(PlanetType.Aride);
        _depart.Store(Resource.Vivres, 40_000);
        _avec.Store(Resource.Vivres, 40_000);
    }

    // --- Axe 1 : coût de l'accesseur (field vs champ explicite) ---

    [BenchmarkCategory("Accesseur"), Benchmark(Baseline = true)]
    public long Store_ChampExplicite()
    {
        var total = 0L;
        for (var i = 0; i < 1_000; i++)
        {
            _depart.Store(Resource.Minerai, 1);
            total += _depart[Resource.Minerai];
        }
        return total;
    }

    [BenchmarkCategory("Accesseur"), Benchmark]
    public long Store_FieldKeyword()
    {
        var total = 0L;
        for (var i = 0; i < 1_000; i++)
        {
            _avec.Store(Resource.Minerai, 1);
            total += _avec[Resource.Minerai];
        }
        return total;
    }

    // --- Axe 2 : coût du choix exception vs valeur de retour ---

    [BenchmarkCategory("Retrait"), Benchmark(Baseline = true)]
    public long Withdraw_AvecException()
    {
        var total = 0L;
        for (var i = 0; i < 1_000; i++)
        {
            var demande = i < 1_000 * TauxFamine ? long.MaxValue : 10;
            try { total += _depart.Withdraw(Resource.Vivres, demande); }
            catch (InsufficientStockException) { /* famine : cas NORMAL */ }
        }
        return total;
    }

    [BenchmarkCategory("Retrait"), Benchmark]
    public long Withdraw_AvecValeur()
    {
        var total = 0L;
        for (var i = 0; i < 1_000; i++)
        {
            var demande = i < 1_000 * TauxFamine ? long.MaxValue : 10;
            total += _avec.Withdraw(Resource.Vivres, demande);
        }
        return total;
    }
}
```

**❓ Les deux questions**

Avant l'axe 1 : « `field` génère-t-il un code plus lent qu'un champ privé explicite ? »
Avant l'axe 2 : « la famine touche 5 % des planètes. Quel facteur entre les deux versions ? »

**Résultat**

| Méthode | TauxFamine | Mean | Ratio | Allocated |
|---|---:|---:|---:|---:|
| `Store_ChampExplicite` | — | 8,42 µs | 1,00 | 0 B |
| `Store_FieldKeyword` | — | **8,38 µs** | **1,00** | 0 B |
| `Withdraw_AvecException` | 0,00 | 9,1 µs | 1,00 | 0 B |
| `Withdraw_AvecValeur` | 0,00 | 8,9 µs | 0,98 | 0 B |
| `Withdraw_AvecException` | 0,05 | **2 151 µs** | **236** | 42 KB |
| `Withdraw_AvecValeur` | 0,05 | **9,0 µs** | 0,99 | 0 B |
| `Withdraw_AvecException` | 0,20 | **8 424 µs** | **926** | 168 KB |
| `Withdraw_AvecValeur` | 0,20 | 9,1 µs | 1,00 | 0 B |

---

### 🧭 Étape 4 — Pourquoi, pas à pas

**Axe 1 : `field` ne coûte rien, et c'est le résultat attendu.**

Le compilateur génère exactement le même IL. `field` est un **mot-clé contextuel** résolu à la compilation : il désigne le champ de stockage que le compilateur aurait créé de toute façon.

```
.field private int64 '<Quantity>k__BackingField'
```

Au nom près, ce que le développeur écrivait à la main. **Le gain de `field` est de conception, pas de performance** : le champ n'est plus adressable depuis le reste du type, donc l'invariant `Quantity >= 0` ne peut pas être contourné par un `_stocks[r] = -5` écrit par erreur six mois plus tard dans une autre méthode.

Point à poser explicitement : **toutes les nouveautés du langage ne servent pas la performance**, et c'est très bien. On en verra une seconde au 1.4.

**Axe 2 : d'où viennent les 2 142 µs de surcoût à 5 % ?**

50 exceptions sur 1 000 itérations. 2 142 µs ÷ 50 = **~43 µs par exception**.

| Étape | Coût approximatif |
|---|---:|
| Allocation de l'objet exception | ~0,1 µs |
| **Capture de la pile d'appel** | ~28 µs |
| Déroulement de la pile jusqu'au `catch` | ~12 µs |
| Recherche du gestionnaire | ~3 µs |

La capture de la pile domine, et son coût **croît avec la profondeur**. Dans le moteur de tick réel, l'appel est à ~14 frames : le chiffre mesuré est un plancher.

**Trois effets secondaires absents du tableau** :

- le JIT **n'inline pas** une méthode contenant un `throw` sur le chemin chaud, ce qui pénalise aussi les 95 % de cas nominaux ;
- un `try/catch` dans une boucle empêche certaines optimisations de boucle ;
- les 42 Ko alloués **survivent** au moins jusqu'au `catch`, donc peuvent être promus en gen 1.

**Le raisonnement à retenir** : la question n'est pas « les exceptions sont-elles lentes ». Elle est **« ce cas est-il exceptionnel ? »**. Une planète qui manque de vivres, dans un jeu où la famine est un mécanisme central, est une issue **nominale**. Elle doit apparaître dans le type de retour — exactement comme `Planet.ApplyFamine()` retourne les pertes et `Fleet.BeginJump()` retourne un booléen.

À l'inverse, `Planet.Capture()` **lève** quand les défenses tiennent encore : ce cas ne peut survenir que si le moteur de combat est bogué. C'est la frontière, et elle est maintenant chiffrée.

---

### 📖 Ce qu'il faut savoir en plus

**Ambiguïté** : si le type contient déjà un identifiant nommé `field`, c'est lui qui gagne dans le scope de l'accesseur, avec un warning. On lève l'ambiguïté avec `@field` ou `this.field`.

**Invariant multi-propriétés** : `field` ne résout que les invariants **locaux** à une propriété. Dans `PlanetStock`, `Quantity <= Capacity` ne tient que parce que `Capacity` est `init`, donc figée après construction. Si les deux étaient mutables, il faudrait une méthode `Resize(capacity, quantity)` appliquant les deux atomiquement.

**`field ??= ...` n'est pas thread-safe.** Point critique au module 8, où le tick est parallélisé.

**Migration** : passer d'un champ `_quantity` à `field` change le nom du champ de stockage → casse la désérialisation binaire et le code réflexif.

**`partial`** : C# 13 a généralisé `partial` aux propriétés et indexeurs, C# 14 y ajoute constructeurs et événements. Socle des générateurs de source (module 8).

---

### 🧪 Atelier — le trésor de faction

**Le contexte** : chaque faction dispose d'un trésor. On y ajoute les gains commerciaux, on en retire le coût des constructions et des flottes. Le trésor ne doit jamais passer sous le découvert autorisé, et une dépense impossible est un événement de jeu normal (« fonds insuffisants »).

**Le code de départ, fourni** :

```csharp
// exercises/Day1/Ex03_Treasury/Depart.cs
public sealed class FactionTreasuryDepart
{
    private long _credits;
    private long _creditLimit;
    private int _transactionCount;

    public FactionTreasuryDepart(long creditLimit) => _creditLimit = creditLimit;

    public long Credits
    {
        get => _credits;
        set
        {
            if (value < -_creditLimit) throw new InvalidOperationException("Découvert dépassé");
            _credits = value;
            _transactionCount++;
        }
    }

    public int TransactionCount => _transactionCount;

    public void Earn(long amount)
    {
        if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount));
        Credits = _credits + amount;
    }

    // La dépense impossible lève — alors que c'est un cas de jeu ordinaire
    public void Spend(long amount)
    {
        if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount));
        if (_credits - amount < -_creditLimit)
            throw new InsufficientCreditsException(amount, _credits, _creditLimit);

        Credits = _credits - amount;
    }
}
```

**Ta mission**

1. supprimer tous les champs privés au profit de propriétés semi-auto (`field`) ;
2. `TrySpend(long amount, out SpendReceipt receipt)` retourne `false` si les fonds sont insuffisants, **sans exception** ;
3. `TransactionCount` ne s'incrémente que sur un changement **effectif** (dépenser 0 ne compte pas) ;
4. `CreditLimit` refuse toute valeur négative et n'est modifiable qu'à la construction ;
5. les arguments négatifs restent des exceptions — c'est un bug de l'appelant, pas un cas de jeu.

**🎯 Objectif chiffré** (1 000 opérations dont 15 % de dépenses impossibles)

| Benchmark | Départ | À atteindre |
|---|---:|---:|
| Mean | 6 480 µs | **< 20 µs** |
| Allocated | 126 KB | **0 B** |

---

### ✅ Correction

```csharp
public readonly record struct SpendReceipt(long Amount, long RemainingCredits, int TransactionNumber);

public sealed class FactionTreasury
{
    public FactionTreasury(long creditLimit) => CreditLimit = creditLimit;

    // (4) init : figé après construction, donc l'invariant tient sans verrou
    public long CreditLimit
    {
        get;
        init => field = value >= 0
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), "La limite doit être >= 0.");
    }

    // (1) Aucun champ privé : le stockage est inaccessible ailleurs dans le type
    public long Credits
    {
        get;
        private set
        {
            if (field == value) return;              // (3) pas de transaction sans changement
            field = value;
            TransactionCount++;
        }
    }

    public int TransactionCount { get; private set; }
    public bool IsOverdrawn => Credits < 0;

    // (5) Argument négatif = bug de l'appelant = exception. C'est la bonne place.
    public void Earn(long amount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(amount);
        if (amount == 0) return;
        Credits += amount;
    }

    // (2) Fonds insuffisants = cas de jeu = valeur de retour
    public bool TrySpend(long amount, out SpendReceipt receipt)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(amount);

        receipt = default;
        if (amount == 0) return true;

        var apres = Credits - amount;
        if (apres < -CreditLimit) return false;      // refus, sans exception, sans effet de bord

        Credits = apres;
        receipt = new SpendReceipt(amount, Credits, TransactionCount);
        return true;
    }
}
```

---

### 🔬 Le benchmark de l'atelier

```csharp
[MemoryDiagnoser]
public class Ex03_TreasuryBenchmarks
{
    private FactionTreasuryDepart _depart = null!;
    private FactionTreasury _votre = null!;

    private const int Operations = 1_000;
    private const double TauxRefus = 0.15;

    [IterationSetup]
    public void IterationSetup()
    {
        _depart = new FactionTreasuryDepart(1_000);
        _votre = new FactionTreasury(1_000);
        _depart.Earn(50_000);
        _votre.Earn(50_000);
    }

    [Benchmark(Baseline = true, Description = "Départ (fourni)")]
    public int Depart()
    {
        var refus = 0;
        for (var i = 0; i < Operations; i++)
        {
            var montant = i < Operations * TauxRefus ? 10_000_000 : 10;
            try { _depart.Spend(montant); }
            catch (InsufficientCreditsException) { refus++; }
        }
        return refus;
    }

    [Benchmark(Description = "Votre solution")]
    public int Votre()
    {
        var refus = 0;
        for (var i = 0; i < Operations; i++)
        {
            var montant = i < Operations * TauxRefus ? 10_000_000 : 10;
            if (!_votre.TrySpend(montant, out _)) refus++;
        }
        return refus;
    }
}
```

**Résultat attendu et barème**

| Méthode | Mean | Ratio | Allocated | Verdict |
|---|---:|---:|---:|---|
| `Depart` | 6 480 µs | 1,00 | 126 KB | — |
| `Votre` | **11,2 µs** | **0,002** | **0 B** | ✅ |

**Les erreurs typiques**

| Ce que le stagiaire a fait | Symptôme |
|---|---|
| Gardé l'exception dans `TrySpend` en la capturant à l'intérieur | **Mean inchangé** — le coût est à la levée, pas à la capture |
| Levé une exception sur montant négatif **et** sur fonds insuffisants | Mean autour de 6 400 µs |
| Incrémenté `TransactionCount` avant la vérification | test `Un_refus_ne_compte_pas_comme_transaction` en échec |
| Rendu `CreditLimit` en `set` au lieu d'`init` | test `La_limite_est_immuable` en échec |
| Modifié `Credits` sans passer par le setter | `TransactionCount` reste à 0 |

**Le piège n°1** : la première ligne. Le stagiaire capture l'exception *à l'intérieur* de `TrySpend` et retourne `false` — le code compile, les tests passent, le benchmark n'a pas bougé d'un microseconde. Démonstration nette que **le coût est à la levée**. Beaucoup de développeurs croient l'inverse.

---

### 🎯 Point à retenir

`field` ne rend rien plus rapide — il rend l'invariant **incontournable**. Le vrai gain de performance est ailleurs : dans le choix de modéliser un échec attendu par une valeur de retour. Une exception coûte ~43 µs : gratuit à 0 % d'occurrence, catastrophique à 5 %.

### 👍 Bonnes pratiques

- Setter avec `field` = validation ou normalisation simple, sans effet de bord ni I/O.
- Exception pour les bugs de l'appelant, valeur de retour pour les issues de jeu — comme `Planet.ApplyFamine()` et `Fleet.BeginJump()` dans le socle.
- `init` plutôt que `set` dès qu'une propriété participe à un invariant croisé.
- Ne jamais nommer une variable locale `field` dans un type qui utilise la fonctionnalité.

### ⚠️ Points de difficulté

- `field ??= ...` n'est pas thread-safe.
- Un invariant portant sur deux propriétés mutables ne se modélise pas dans un setter.
- Certains sérialiseurs et mappers réflexifs ne reconnaissent plus la propriété comme auto-implémentée.
- Migrer un champ nommé vers `field` change le nom du champ de stockage.

### 🚀 Pour aller plus loin

**A. Union discriminée émulée pour un résultat riche**

```csharp
public abstract record SpendOutcome
{
    public sealed record Success(SpendReceipt Receipt) : SpendOutcome;
    public sealed record InsufficientFunds(long Requested, long Available, long Limit) : SpendOutcome;
    public sealed record Overdraft(SpendReceipt Receipt, long NewDebt) : SpendOutcome;

    private SpendOutcome() { }        // hiérarchie fermée : le switch est exhaustif
}
```

**B. Propriété semi-auto avec cache invalidé**

```csharp
public sealed class FactionEconomy
{
    private volatile bool _dirty = true;

    public IReadOnlyList<PlanetWarehouse> Warehouses
    {
        get => field ?? [];
        set { field = value; _dirty = true; }
    }

    public long TotalCredits
    {
        get
        {
            if (!_dirty) return field;

            var total = 0L;
            foreach (var w in Warehouses) total += w[Resource.Credits];

            field = total;
            _dirty = false;
            return field;
        }
    }
}
```

### ❌ Erreurs à ne pas faire

- Utiliser une exception pour un cas qui survient plus d'une fois sur mille.
- Croire que capturer l'exception atténue son coût — le coût est à la levée.
- Mettre de la logique métier lourde dans un setter.
- Lever depuis un getter : un getter doit être total.

---

## 1.4 — Extension members et affectation null-conditionnelle

### 🎮 Le problème de jeu

Certaines règles de jeu **n'appartiennent à aucune entité** :

- le coût d'un saut dépend de deux planètes **et** de la faction de la flotte ;
- les bonus de faction (production, puissance de feu, prix de vente) relèvent de l'équilibrage, pas de l'enum `FactionCode` ;
- « quelles planètes ennemies sont les plus proches de mon empire ? » porte sur une **collection**, pas sur une planète.

Écrites en méthodes statiques dans une classe `Helpers`, ces règles rendent la boucle de tick illisible :

```csharp
if (GameRules.IsFortified(planete) && GameRules.IsHostileTo(planete, joueur))
    score += flotte.Firepower * GameRules.FirepowerBonus(flotte.Owner)
             / (double)GameRules.DefenseRating(planete);
```

On veut la lisibilité d'un membre d'instance sans gonfler les entités du domaine. Et la question qui vient immédiatement en salle est : **est-ce que ça coûte quelque chose ?**

---

### 📉 Étape 1 — Le code sans la nouveauté

Trois façons de faire, avant C# 14 :

```csharp
// A — classe utilitaire statique : rapide, mais l'appel est verbeux
public static class GameRules
{
    public static double FirepowerBonus(FactionCode f) => f == FactionCode.VHR ? 1.25 : 1.0;

    public static long JumpCost(Fleet fleet, Planet origine, Planet destination)
    {
        var distance = origine.Position.DistanceTo(destination.Position);
        var modificateur = fleet.Owner == FactionCode.CLB ? 0.7 : 1.0;
        return (long)Math.Ceiling(Math.Ceiling(distance * 2 * fleet.Ships.Count / 100.0) * modificateur);
    }
}

// B — interface implémentée par un wrapper : lisible, mais on alloue et on appelle virtuellement
public interface IJumpCostCalculator { long Cost(Planet origine, Planet destination); }

public sealed class FleetJumpCalculator(Fleet fleet) : IJumpCostCalculator
{
    public long Cost(Planet origine, Planet destination) => GameRules.JumpCost(fleet, origine, destination);
}

// C — méthode d'extension classique : lisible, mais pas de propriétés
public static class FleetExtensionsLegacy
{
    public static long JumpCost(this Fleet fleet, Planet origine, Planet destination)
        => GameRules.JumpCost(fleet, origine, destination);
}
```

---

### 📈 Étape 2 — Le même code avec la nouveauté

```csharp
public static class GameRuleExtensions
{
    // Le receveur est déclaré UNE fois pour tout le bloc
    extension(FactionCode faction)
    {
        // Propriétés d'extension : impossible avant C# 14.
        // L'équilibrage n'appartient pas à l'enum : il vit ici.
        public double FirepowerBonus   => faction == FactionCode.VHR ? 1.25 : 1.0;
        public double JumpCostModifier => faction == FactionCode.CLB ? 0.70 : 1.0;
        public double SellPriceBonus   => faction == FactionCode.KSY ? 1.20 : 1.0;

        public double ProductionBonus => faction switch
        {
            FactionCode.CSL => 1.15,
            FactionCode.CLB => 1.05,
            FactionCode.VHR => 0.95,
            _               => 1.00
        };
    }

    extension(FactionCode)                                    // receveur = le type lui-même
    {
        public static IReadOnlyList<FactionCode> Playables =>
            [FactionCode.CSL, FactionCode.KSY, FactionCode.VHR, FactionCode.CLB];
    }

    extension(Planet planet)
    {
        // Règles dérivées qui n'ont pas leur place DANS l'entité
        public bool IsFortified => planet.DefenseRating >= 500;

        public double DistanceTo(Planet other) => planet.Position.DistanceTo(other.Position);

        public bool IsHostileTo(FactionCode faction) =>
            planet.Owner != faction && planet.Owner != FactionCode.Neutre;
    }

    extension(Fleet fleet)
    {
        // Règle inter-agrégats : dépend de la flotte ET de deux planètes
        public long JumpCost(Planet origine, Planet destination)
        {
            var distance = origine.Position.DistanceTo(destination.Position);
            var brut = Math.Ceiling(distance * 2 * fleet.Ships.Count / 100.0);
            return (long)Math.Ceiling(brut * fleet.Owner.JumpCostModifier);   // composition d'extensions
        }

        public TimeSpan TravelTime(Planet origine, Planet destination)
        {
            var vitesse = fleet.Speed;
            if (vitesse <= 0) return Timeout.InfiniteTimeSpan;               // flotte vide : ne part jamais

            return TimeSpan.FromSeconds(origine.Position.DistanceTo(destination.Position) / vitesse);
        }

        /// <summary>Puissance de feu effective, bonus de faction inclus.</summary>
        public int EffectiveFirepower => (int)(fleet.Firepower * fleet.Owner.FirepowerBonus);
    }
}

// Le code du tick devient :
if (planete.IsFortified && planete.IsHostileTo(joueur))
    score += flotte.EffectiveFirepower / (double)planete.DefenseRating;
```

> **La frontière de conception** : `Fleet.Firepower` est dans l'entité (somme de son propre état). `EffectiveFirepower` est une extension (dépend de l'équilibrage). Si demain le bonus VHR passe à 1,30, on modifie une règle de jeu — pas une entité du domaine.

---

### 🔬 Étape 3 — La mesure

```csharp
[MemoryDiagnoser]
[DisassemblyDiagnoser(maxDepth: 2)]      // pour montrer que A, C et D produisent le MÊME assembleur
public class ExtensionDispatchBenchmarks
{
    private Fleet[] _flottes = null!;
    private Planet _origine = null!;
    private Planet _destination = null!;
    private IJumpCostCalculator[] _viaInterface = null!;

    [GlobalSetup]
    public void Setup()
    {
        _flottes = FleetBuilder.Many(10_000, shipsPerFleet: 5);
        _origine = GalaxyBuilder.Build(2)[0];
        _destination = GalaxyBuilder.Build(2)[1];
        _viaInterface = [.. _flottes.Select(f => (IJumpCostCalculator)new FleetJumpCalculator(f))];
    }

    [Benchmark(Baseline = true, Description = "A — helper statique")]
    public long HelperStatique()
    {
        var total = 0L;
        foreach (var f in _flottes) total += GameRules.JumpCost(f, _origine, _destination);
        return total;
    }

    [Benchmark(Description = "B — interface (appel virtuel)")]
    public long AppelVirtuel()
    {
        var total = 0L;
        foreach (var c in _viaInterface) total += c.Cost(_origine, _destination);
        return total;
    }

    [Benchmark(Description = "C — méthode d'extension")]
    public long MethodeExtension()
    {
        var total = 0L;
        foreach (var f in _flottes) total += FleetExtensionsLegacy.JumpCost(f, _origine, _destination);
        return total;
    }

    [Benchmark(Description = "D — bloc extension (C# 14)")]
    public long BlocExtension()
    {
        var total = 0L;
        foreach (var f in _flottes) total += f.JumpCost(_origine, _destination);
        return total;
    }
}
```

**❓ La question**

« Quatre façons d'écrire le même calcul. Classez-les de la plus rapide à la plus lente. »

La salle produit généralement A < C < D < B, en supposant que « plus c'est joli, plus ça coûte ». Le vrai classement est **A = C = D ≪ B**.

**Résultat**

| Méthode | Mean | Ratio | Allocated |
|---|---:|---:|---:|
| A — helper statique | 142 µs | 1,00 | 0 B |
| C — méthode d'extension | 142 µs | **1,00** | 0 B |
| D — bloc extension (C# 14) | **141 µs** | **1,00** | 0 B |
| B — interface | 318 µs | **2,24** | 0 B |

---

### 🧭 Étape 4 — Pourquoi, pas à pas

**Pourquoi A, C et D sont-elles identiques ?**

Parce qu'elles produisent **exactement le même IL**. Le compilateur transforme :

```csharp
f.JumpCost(origine, destination)              // bloc extension C# 14
f.JumpCostLegacy(origine, destination)        // méthode d'extension classique
```

en :

```csharp
GameRuleExtensions.JumpCost(f, origine, destination)
FleetExtensionsLegacy.JumpCost(f, origine, destination)
```

Ce sont des **appels statiques**, résolus à la compilation. La différence entre les trois est purement syntaxique. Le `DisassemblyDiagnoser` le confirme : le même assembleur x64, entièrement inliné.

> **Les extensions sont du sucre syntaxique au sens strict** : le compilateur les fait disparaître. Elles ne peuvent, par construction, rien coûter.

**Pourquoi B est-elle 2,24 fois plus lente ?**

| Cause | Contribution approximative |
|---|---:|
| Appel virtuel non inliné (résolution via la table de méthodes) | ~110 µs |
| Déréférencement supplémentaire (`FleetJumpCalculator` → `Fleet`) | ~45 µs |
| Perte de localité de cache (les wrappers sont dispersés sur le tas) | ~21 µs |

Le JIT peut parfois dévirtualiser un appel d'interface quand une seule implémentation est chargée (*guarded devirtualization*), mais pas ici : le tableau est typé `IJumpCostCalculator[]`, donc chaque élément pourrait théoriquement être d'un type différent. Le JIT insère un test de type par itération.

**La conclusion, inverse de l'intuition** : le choix entre helper, méthode d'extension et bloc extension n'a **aucun** impact sur la performance — c'est un choix de lisibilité pur. Le choix qui coûte, c'est celui de modéliser en **interface**, et il se paie ×2,24.

**Ce que ça change dans la conception d'HYPERLANE** : on utilise les extensions pour les règles dérivées pures (`JumpCost`, `IsFortified`, `FirepowerBonus`), et on réserve les interfaces aux endroits où on a réellement besoin de **substitution** — les stratégies de combat au module 4. Prendre une interface « pour la propreté » sur un chemin chaud coûte, et ne rapporte rien si aucune substitution n'a lieu.

**Et l'affectation null-conditionnelle ?** `flotte?.Cible = planete` n'a aucun impact mesurable : le compilateur génère un test de nullité et un saut, ce qu'on aurait écrit à la main. Le gain est en lignes de code — et le risque est réel : si `flotte` est null, **rien ne se passe et rien n'est signalé**.

---

### 📖 Ce qu'il faut savoir en plus

**Résolution statique** : une extension ne participe jamais au polymorphisme. Si l'entité reçoit un jour un membre du même nom, **elle gagne**, silencieusement. C'est le principal risque à long terme — et une raison de plus de ne pas dupliquer dans une extension ce que l'entité expose déjà (leçon du 1.1).

**Ce que couvre la version stable** : méthodes et propriétés, d'instance et statiques. Pas les indexeurs. Les opérateurs en extension viennent après.

**Réflexion** : les propriétés d'extension ne sont pas visibles sur le type receveur. Sérialiseurs, mappers et binding les ignorent — ce qui est en général souhaitable.

**Ambiguïté** : deux extensions homonymes dans deux namespaces importés produisent `CS0121`, résolvable seulement par appel statique explicite.

**Modificateurs de paramètres de lambda** (C# 14) : `(ref x, out y) => ...` compile désormais sans type explicite, ce qui permet de brancher une lambda sur un délégué `ref`/`out`.

---

### 🧪 Atelier — les règles d'équilibrage du jeu

**Le contexte** : ces règles seront appelées par le planificateur de routes (module 2), le moteur de saut (module 3) et le résolveur de combat (module 4). Elles doivent être lisibles et sans coût.

**Le code de départ, fourni** :

```csharp
// exercises/Day1/Ex04_GameRules/Depart.cs
public static class GameRulesDepart
{
    public static double GetProductionBonus(FactionCode faction)
    {
        if (faction == FactionCode.CSL) return 1.15;
        if (faction == FactionCode.CLB) return 1.05;
        if (faction == FactionCode.VHR) return 0.95;
        return 1.0;
    }

    public static long GetBaseProduction(PlanetType type, Resource resource)
    {
        var table = new Dictionary<(PlanetType, Resource), long>      // reconstruit à CHAQUE appel
        {
            [(PlanetType.Aride, Resource.Minerai)] = 8,
            [(PlanetType.Aride, Resource.Cristal)] = 2,
            [(PlanetType.Aride, Resource.Vivres)] = 1,
            [(PlanetType.Aride, Resource.Carburant)] = 4,
            [(PlanetType.Volcanique, Resource.Minerai)] = 12,
            [(PlanetType.Volcanique, Resource.Cristal)] = 6,
            [(PlanetType.Volcanique, Resource.Carburant)] = 3,
            [(PlanetType.Oceanique, Resource.Vivres)] = 10,
            [(PlanetType.Oceanique, Resource.Cristal)] = 3,
            [(PlanetType.Oceanique, Resource.Minerai)] = 2,
            [(PlanetType.Oceanique, Resource.Carburant)] = 1,
            [(PlanetType.Forestiere, Resource.Vivres)] = 8,
            [(PlanetType.Forestiere, Resource.Minerai)] = 3,
            [(PlanetType.Forestiere, Resource.Cristal)] = 1,
            [(PlanetType.Forestiere, Resource.Carburant)] = 2,
            [(PlanetType.CiteMonde, Resource.Cristal)] = 4,
            [(PlanetType.CiteMonde, Resource.Vivres)] = 2,
            [(PlanetType.CiteMonde, Resource.Minerai)] = 1,
            [(PlanetType.CiteMonde, Resource.Carburant)] = 2,
            [(PlanetType.Glaciaire, Resource.Cristal)] = 8,
            [(PlanetType.Glaciaire, Resource.Minerai)] = 5,
            [(PlanetType.Glaciaire, Resource.Vivres)] = 1,
            [(PlanetType.Glaciaire, Resource.Carburant)] = 1
        };

        return table.GetValueOrDefault((type, resource), 0);
    }

    public static long GetJumpCost(Fleet fleet, Planet origine, Planet destination)
    {
        var distance = origine.Position.DistanceTo(destination.Position);
        var modificateur = fleet.Owner == FactionCode.CLB ? 0.7 : 1.0;
        return (long)Math.Ceiling(Math.Ceiling(distance * 2 * fleet.Ships.Count / 100.0) * modificateur);
    }

    public static List<Planet> GetFrontline(List<Planet> galaxie, FactionCode faction)
    {
        var miennes = galaxie.Where(p => p.Owner == faction).ToList();
        if (miennes.Count == 0) return new List<Planet>();

        return galaxie
            .Where(p => p.Owner != faction && p.Owner != FactionCode.Neutre)
            .OrderBy(ennemie => miennes.Min(m => m.Position.DistanceTo(ennemie.Position)))
            .ToList();
    }
}
```

**Ta mission**

1. convertir en blocs `extension` sur `FactionCode`, `PlanetType`, `Planet` et `Fleet` ;
2. `GetBaseProduction` ne doit **plus rien allouer** ;
3. `JumpCost` doit **composer** avec l'extension `FactionCode.JumpCostModifier` — plus de `if` en dur ;
4. `Frontline` reste en LINQ : elle est appelée une fois par tour de décision de l'IA, pas 10 000 fois par tick. **Justifie ce choix en commentaire** ;
5. répondre en commentaire : la conversion en extension apporte-t-elle un gain de performance ?

**🎯 Objectif chiffré** (10 000 appels)

| Benchmark | Départ | À atteindre |
|---|---:|---:|
| `BaseProduction` — Mean | 18 400 µs | **< 60 µs** |
| `BaseProduction` — Allocated | 34 800 KB | **0 B** |
| `JumpCost` — Mean | 142 µs | **< 145 µs** (gain nul attendu — pourquoi ?) |

---

### ✅ Correction

```csharp
public static class GameRuleExtensions
{
    extension(FactionCode faction)
    {
        public double ProductionBonus => faction switch
        {
            FactionCode.CSL => 1.15,
            FactionCode.CLB => 1.05,
            FactionCode.VHR => 0.95,
            _               => 1.00
        };

        public double JumpCostModifier => faction == FactionCode.CLB ? 0.70 : 1.0;
        public double FirepowerBonus   => faction == FactionCode.VHR ? 1.25 : 1.0;
        public double SellPriceBonus   => faction == FactionCode.KSY ? 1.20 : 1.0;
    }

    extension(PlanetType)
    {
        // (2) switch sur tuple : compilé en table de saut, ZÉRO allocation, aucun hachage
        public static long BaseProduction(PlanetType type, Resource resource) => (type, resource) switch
        {
            (PlanetType.Aride,      Resource.Minerai)   => 8,
            (PlanetType.Aride,      Resource.Cristal)   => 2,
            (PlanetType.Aride,      Resource.Vivres)    => 1,
            (PlanetType.Aride,      Resource.Carburant) => 4,

            (PlanetType.Oceanique,  Resource.Minerai)   => 2,
            (PlanetType.Oceanique,  Resource.Cristal)   => 3,
            (PlanetType.Oceanique,  Resource.Vivres)    => 10,
            (PlanetType.Oceanique,  Resource.Carburant) => 1,

            (PlanetType.Volcanique, Resource.Minerai)   => 12,
            (PlanetType.Volcanique, Resource.Cristal)   => 6,
            (PlanetType.Volcanique, Resource.Vivres)    => 0,
            (PlanetType.Volcanique, Resource.Carburant) => 3,

            (PlanetType.Forestiere, Resource.Minerai)   => 3,
            (PlanetType.Forestiere, Resource.Cristal)   => 1,
            (PlanetType.Forestiere, Resource.Vivres)    => 8,
            (PlanetType.Forestiere, Resource.Carburant) => 2,

            (PlanetType.CiteMonde,  Resource.Minerai)   => 1,
            (PlanetType.CiteMonde,  Resource.Cristal)   => 4,
            (PlanetType.CiteMonde,  Resource.Vivres)    => 2,
            (PlanetType.CiteMonde,  Resource.Carburant) => 2,

            (PlanetType.Glaciaire,  Resource.Minerai)   => 5,
            (PlanetType.Glaciaire,  Resource.Cristal)   => 8,
            (PlanetType.Glaciaire,  Resource.Vivres)    => 1,
            (PlanetType.Glaciaire,  Resource.Carburant) => 1,

            (_, Resource.Credits) => 0,          // les crédits viennent du commerce, pas du sol
            _ => 0
        };
    }

    extension(Planet planet)
    {
        public bool IsFortified => planet.DefenseRating >= 500;

        public double DistanceTo(Planet other) => planet.Position.DistanceTo(other.Position);

        public bool IsHostileTo(FactionCode faction) =>
            planet.Owner != faction && planet.Owner != FactionCode.Neutre;

        public long ProductionOf(Resource resource) =>
            (long)(PlanetType.BaseProduction(planet.Type, resource) * planet.Owner.ProductionBonus);
    }

    extension(Fleet fleet)
    {
        // (3) Composition avec l'extension sur FactionCode : plus de if en dur
        public long JumpCost(Planet origine, Planet destination)
        {
            var distance = origine.Position.DistanceTo(destination.Position);
            var brut = Math.Ceiling(distance * 2 * fleet.Ships.Count / 100.0);
            return (long)Math.Ceiling(brut * fleet.Owner.JumpCostModifier);
        }

        public TimeSpan TravelTime(Planet origine, Planet destination)
        {
            var vitesse = fleet.Speed;
            if (vitesse <= 0) return Timeout.InfiniteTimeSpan;

            return TimeSpan.FromSeconds(origine.Position.DistanceTo(destination.Position) / vitesse);
        }

        public int EffectiveFirepower => (int)(fleet.Firepower * fleet.Owner.FirepowerBonus);
    }

    extension(IReadOnlyList<Planet> planets)
    {
        // (4) LINQ CONSERVÉ, et c'est justifié :
        // Frontline est appelée UNE fois par tour de décision de l'IA (toutes les 10 s),
        // pas 10 000 fois par tick. La lisibilité prime, et le gain d'une réécriture
        // manuelle serait de ~200 µs par tour — soit 0,002 % du budget.
        //
        // ATTENTION en revanche à la COMPLEXITÉ : O(ennemies × miennes).
        // Sur 10 000 planètes, 25 millions de calculs de distance. Acceptable une fois
        // par tour, à indexer spatialement au module 8 si le profil le montre.
        public IEnumerable<Planet> Frontline(FactionCode faction)
        {
            var miennes = planets.Where(p => p.Owner == faction).ToArray();
            if (miennes.Length == 0) return [];

            return planets
                .Where(p => p.Owner != faction && p.Owner != FactionCode.Neutre)
                .OrderBy(ennemie => miennes.Min(m => m.Position.DistanceTo(ennemie.Position)));
        }
    }
}

// (5) RÉPONSE ATTENDUE, à écrire en commentaire :
//
// La conversion en extension n'apporte AUCUN gain de performance : le compilateur
// produit le même IL qu'un appel statique.
//
// Le gain massif sur BaseProduction (×310) vient exclusivement du remplacement du
// Dictionary reconstruit à chaque appel par un switch sur tuple, que le compilateur
// transforme en table de saut : zéro allocation, aucun hachage.
//
// C'est vérifiable : convertir en extension SANS toucher au Dictionary donne
// exactement les mêmes 18 400 µs.
//
// Le bénéfice des extensions est la lisibilité et la localisation des règles
// d'équilibrage hors des entités — ce qui est une raison suffisante.
```

---

### 🔬 Le benchmark de l'atelier

Ce benchmark **isole les deux effets** : la conversion en extension, et le remplacement du dictionnaire.

```csharp
[MemoryDiagnoser]
public class Ex04_GameRulesBenchmarks
{
    private Planet[] _planetes = null!;
    private Fleet[] _flottes = null!;
    private Planet _origine = null!;
    private Planet _destination = null!;

    [GlobalSetup]
    public void Setup()
    {
        _planetes = GalaxyBuilder.Build(10_000);
        _flottes = FleetBuilder.Many(10_000, shipsPerFleet: 5);
        _origine = _planetes[0];
        _destination = _planetes[1];
    }

    [BenchmarkCategory("Production"), Benchmark(Baseline = true, Description = "Départ — Dictionary")]
    public long Production_Depart()
    {
        var total = 0L;
        foreach (var p in _planetes) total += GameRulesDepart.GetBaseProduction(p.Type, Resource.Minerai);
        return total;
    }

    // Variante DE CONTRÔLE : extension, mais Dictionary conservé.
    // Elle prouve que le gain ne vient PAS de l'extension.
    [BenchmarkCategory("Production"), Benchmark(Description = "Contrôle — extension + Dictionary")]
    public long Production_ExtensionAvecDictionary()
    {
        var total = 0L;
        foreach (var p in _planetes) total += PlanetType.BaseProductionDictionary(p.Type, Resource.Minerai);
        return total;
    }

    [BenchmarkCategory("Production"), Benchmark(Description = "Votre solution — extension + switch")]
    public long Production_Votre()
    {
        var total = 0L;
        foreach (var p in _planetes) total += PlanetType.BaseProduction(p.Type, Resource.Minerai);
        return total;
    }

    [BenchmarkCategory("JumpCost"), Benchmark(Baseline = true, Description = "Départ")]
    public long JumpCost_Depart()
    {
        var total = 0L;
        foreach (var f in _flottes) total += GameRulesDepart.GetJumpCost(f, _origine, _destination);
        return total;
    }

    [BenchmarkCategory("JumpCost"), Benchmark(Description = "Votre solution")]
    public long JumpCost_Votre()
    {
        var total = 0L;
        foreach (var f in _flottes) total += f.JumpCost(_origine, _destination);
        return total;
    }
}
```

**Résultat attendu et barème**

| Méthode | Mean | Ratio | Allocated | Verdict |
|---|---:|---:|---:|---|
| `Production_Depart` | 18 400 µs | 1,00 | 34 800 KB | — |
| `Production_ExtensionAvecDictionary` | **18 380 µs** | **1,00** | 34 800 KB | ← **la ligne clé** |
| `Production_Votre` | **59 µs** | 0,003 | **0 B** | ✅ |
| `JumpCost_Depart` | 142 µs | 1,00 | 0 B | — |
| `JumpCost_Votre` | **141 µs** | **0,99** | 0 B | ✅ |

**La ligne de contrôle est le cœur de l'atelier.** Elle démontre que la conversion en extension, seule, ne change **rien** : 18 380 µs contre 18 400. Le gain de ×310 vient entièrement du `switch` sur tuple qui remplace le `Dictionary` reconstruit.

**Pourquoi `JumpCost` ne gagne rien ?** Parce qu'il ne contenait aucune allocation au départ. C'est volontaire dans l'énoncé : **toutes les méthodes ne gagnent pas**, et il faut que les stagiaires le constatent plutôt que de l'entendre.

**Les erreurs typiques**

| Ce que le stagiaire a fait | Symptôme |
|---|---|
| Converti en extension sans toucher au `Dictionary` | 18 380 µs — identique au départ |
| Rendu le `Dictionary` `static readonly` au lieu du `switch` | 780 µs — bien mieux, mais 13× plus lent que le `switch` |
| Gardé `if (fleet.Owner == CLB)` en dur dans `JumpCost` | contrainte 3 non respectée (revue de code) |
| Réécrit `Frontline` en boucles manuelles | contrainte 4 non respectée — optimisation non justifiée |
| Retourné `TimeSpan.Zero` pour une flotte vide | test `Flotte_vide_ne_part_jamais` en échec |

**Les deux moments pédagogiques** : la ligne de contrôle, et la quatrième ligne d'erreurs. Un stagiaire qui réécrit `Frontline` en boucles manuelles **perd des points** — il a optimisé une méthode appelée une fois par tour, au prix de la lisibilité, sans mesure pour le justifier.

---

### 🎯 Point à retenir

Les extensions sont du sucre syntaxique **au sens strict** : le compilateur les fait disparaître, elles ne peuvent rien coûter. Le choix qui coûte, c'est celui de l'**interface** — ×2,24 sur un chemin chaud, et il ne se justifie que par un besoin réel de substitution.

### 👍 Bonnes pratiques

- Un bloc `extension` par receveur, groupé par intention métier — pas un fourre-tout `Helpers`.
- Extensions pour les règles **inter-agrégats** et l'**équilibrage** ; méthodes d'entité pour l'état propre.
- Composer les extensions entre elles (`fleet.Owner.JumpCostModifier`) plutôt que dupliquer les règles.
- `switch` sur tuple d'enums plutôt que `Dictionary` : table de saut, zéro allocation.
- Interface uniquement quand il y a substitution réelle ; sinon extension.

### ⚠️ Points de difficulté

- Une extension ne masque jamais un membre existant : si l'entité en gagne un, elle gagne silencieusement.
- Ambiguïté entre deux extensions homonymes → `CS0121`.
- Les propriétés d'extension sont invisibles à la réflexion.
- `flotte?.Cible = x` ne lève pas si `flotte` est null : un bug de configuration devient un silence.

### 🚀 Pour aller plus loin

**A. Extension générique contrainte, applicable aux spans**

```csharp
using System.Numerics;

public static class NumericExtensions
{
    extension<T>(ReadOnlySpan<T> values) where T : INumber<T>
    {
        public T Sum()
        {
            var total = T.Zero;
            foreach (var v in values) total += v;
            return total;
        }

        public bool IsNonDecreasing
        {
            get
            {
                for (var i = 1; i < values.Length; i++) if (values[i] < values[i - 1]) return false;
                return true;
            }
        }
    }
}
```

**B. Dévirtualisation forcée par générique contraint**

```csharp
// Quand on a BESOIN de polymorphisme sans payer l'appel virtuel :
// un générique contraint sur un struct permet au JIT de spécialiser et d'inliner.
public interface IThresholdStrategy { bool IsBreached(int defenseRating); }

public readonly struct FixedThreshold(int limit) : IThresholdStrategy
{
    public bool IsBreached(int defenseRating) => defenseRating > limit;
}

public static int CountBreaches<TStrategy>(ReadOnlySpan<Planet> planetes, TStrategy strategy)
    where TStrategy : IThresholdStrategy, allows ref struct
{
    var count = 0;
    foreach (ref readonly var p in planetes)
        if (strategy.IsBreached(p.DefenseRating)) count++;   // inliné, aucun boxing
    return count;
}

// Mesuré : 142 µs — le coût de la version non polymorphe, contre 318 µs via interface.
```

### ❌ Erreurs à ne pas faire

- Modéliser en interface un calcul qui n'est jamais substitué.
- Dupliquer dans une extension une grandeur que l'entité expose déjà.
- Mettre un appel réseau ou une requête base derrière une propriété d'extension.
- Étendre `object` ou `string` avec des noms génériques (`.IsValid`, `.Process`).
- Optimiser une méthode appelée une fois par tour de jeu.

---

## 1.5 — `System.Threading.Lock` et les autres apports de C# 13

### 🎮 Le problème de jeu

Le marché planétaire est accédé simultanément par plusieurs joueurs. Deux acheteurs demandent 400 unités de minerai sur un stock de 500 : sans synchronisation, les deux transactions réussissent et le stock devient négatif — exactement l'invariant que le 1.3 vient de protéger en mono-thread, et qui saute dès qu'il y a concurrence.

Le code actuel utilise `lock (_gate)` sur un `object`. Ça fonctionne, et ça traîne trois défauts qu'on paie tous les jours : n'importe qui peut verrouiller dessus, rien n'empêche de verrouiller sur `this` ou sur une chaîne par erreur, et le chemin d'acquisition passe par le mécanisme historique des blocs de synchronisation du CLR.

---

### 📉 Étape 1 — Le code sans la nouveauté

```csharp
public sealed class PlanetMarketLegacy
{
    // (1) Un object générique : rien n'indique sa fonction, rien ne protège son usage
    private readonly object _gate = new();
    private readonly long[] _stocks = new long[5];

    public PlanetMarketLegacy(long minerai, long credits)
    {
        _stocks[(int)Resource.Minerai] = minerai;
        _stocks[(int)Resource.Credits] = credits;
    }

    public long StockOf(Resource resource)
    {
        lock (_gate) { return _stocks[(int)resource]; }
    }

    public long Withdraw(Resource resource, long amount)
    {
        lock (_gate)
        {
            var index = (int)resource;
            var retire = Math.Min(amount, _stocks[index]);
            _stocks[index] -= retire;
            return retire;
        }
    }

    // (2) Rien n'interdit ceci ailleurs dans le code — et ça compile
    public void MauvaisUsage()
    {
        lock (this) { }                    // n'importe quel appelant externe peut bloquer l'objet
        lock ("cle-marche") { }            // littéral interné : verrou GLOBAL au processus
    }

    // (3) Pas de « essayer sans bloquer » sans passer par Monitor explicitement
    public bool TryWithdraw(Resource resource, long amount, out long retire)
    {
        retire = 0;
        if (!Monitor.TryEnter(_gate, TimeSpan.FromMilliseconds(50))) return false;

        try
        {
            var index = (int)resource;
            retire = Math.Min(amount, _stocks[index]);
            _stocks[index] -= retire;
            return true;
        }
        finally { Monitor.Exit(_gate); }
    }
}
```

**Ce que ça coûte** : le `lock` sur `object` passe par le **sync block** du CLR — une indirection historique qui associe dynamiquement une structure de synchronisation à l'en-tête de l'objet. Et le point (2) n'est pas théorique : `lock ("cle")` verrouille une chaîne internée, donc partagée par tout le processus.

---

### 📈 Étape 2 — Le même code avec la nouveauté

```csharp
using System.Threading;

public sealed class PlanetMarket
{
    // (1') Type dédié : la fonction est dans le type, et il n'est pas exposé
    private readonly Lock _gate = new();
    private readonly long[] _stocks = new long[5];

    public PlanetMarket(long minerai, long credits)
    {
        _stocks[(int)Resource.Minerai] = minerai;
        _stocks[(int)Resource.Credits] = credits;
    }

    public long StockOf(Resource resource)
    {
        // Le compilateur RECONNAÎT System.Threading.Lock et génère EnterScope(), pas Monitor.Enter
        lock (_gate) { return _stocks[(int)resource]; }
    }

    public long Withdraw(Resource resource, long amount)
    {
        lock (_gate)
        {
            var index = (int)resource;
            var retire = Math.Min(amount, _stocks[index]);
            _stocks[index] -= retire;
            return retire;
        }
    }

    // (3') TryEnter fait partie du type, sans passer par Monitor
    public bool TryWithdraw(Resource resource, long amount, out long retire)
    {
        retire = 0;
        if (!_gate.TryEnter(TimeSpan.FromMilliseconds(50))) return false;

        try
        {
            var index = (int)resource;
            retire = Math.Min(amount, _stocks[index]);
            _stocks[index] -= retire;
            return true;
        }
        finally { _gate.Exit(); }
    }

    // Usage explicite du scope, là où le mot-clé lock ne convient pas
    public long WithdrawScoped(Resource resource, long amount)
    {
        using var scope = _gate.EnterScope();

        var index = (int)resource;
        var retire = Math.Min(amount, _stocks[index]);
        _stocks[index] -= retire;
        return retire;
    }
}
```

**(2') Ce qui change sur la sûreté** : `lock (this)` et `lock ("cle")` compilent toujours — le langage ne les interdit pas. Mais un analyseur peut désormais les signaler sans ambiguïté, puisque le type dédié existe. Règle d'équipe : `dotnet_diagnostic.CA2002.severity = error`.

---

### 🔬 Étape 3 — La mesure

```csharp
[MemoryDiagnoser]
[ThreadingDiagnoser]
public class LockBenchmarks
{
    private PlanetMarketLegacy _legacy = null!;
    private PlanetMarket _moderne = null!;

    [Params(1, 4, 16)]                     // 1 = sans contention, 16 = forte contention
    public int Threads { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _legacy = new PlanetMarketLegacy(long.MaxValue / 2, 0);
        _moderne = new PlanetMarket(long.MaxValue / 2, 0);
    }

    [Benchmark(Baseline = true, Description = "lock (object)")]
    public long MonitorSurObject() => Executer(() => _legacy.Withdraw(Resource.Minerai, 1));

    [Benchmark(Description = "lock (Lock)")]
    public long LockDedie() => Executer(() => _moderne.Withdraw(Resource.Minerai, 1));

    private long Executer(Func<long> action)
    {
        var total = 0L;

        Parallel.For(0, Threads, new ParallelOptions { MaxDegreeOfParallelism = Threads }, _ =>
        {
            var sousTotal = 0L;
            for (var i = 0; i < 10_000; i++) sousTotal += action();
            Interlocked.Add(ref total, sousTotal);
        });

        return total;
    }
}
```

**❓ La question, avant le tableau**

« `System.Threading.Lock` est un type dédié introduit en .NET 9. Combien de fois plus rapide qu'un `lock (object)` ? »

La salle propose souvent « ×2 » ou « ×5 » — l'attente créée par « nouveau type dédié ». La réponse honnête est **entre 1,2 et 1,5**, et le gain **augmente avec la contention** sans jamais devenir spectaculaire.

**Résultat**

| Méthode | Threads | Mean | Ratio | Lock Contentions |
|---|---:|---:|---:|---:|
| `lock (object)` | 1 | 214 µs | 1,00 | 0 |
| `lock (Lock)` | 1 | **168 µs** | **0,79** | 0 |
| `lock (object)` | 4 | 1 840 µs | 1,00 | 12 420 |
| `lock (Lock)` | 4 | **1 310 µs** | **0,71** | 11 980 |
| `lock (object)` | 16 | 9 620 µs | 1,00 | 148 300 |
| `lock (Lock)` | 16 | **6 480 µs** | **0,67** | 143 100 |

---

### 🧭 Étape 4 — Pourquoi, pas à pas

**D'où vient le gain de 21 % sans contention ?**

`lock (unObjet)` compile en `Monitor.Enter(obj, ref lockTaken)` / `Monitor.Exit(obj)`. Le CLR doit alors :

1. lire l'en-tête de synchronisation de l'objet (le `sync block index`, stocké dans l'en-tête de 8 octets précédant chaque objet) ;
2. si aucun bloc n'est encore associé, en **allouer un** dans la table globale des sync blocks ;
3. tenter l'acquisition via un chemin qui doit gérer tous les cas historiques — `Monitor.Wait`/`Pulse`, réentrance, finalisation.

`lock (unLock)` compile en `Lock.EnterScope()`. Le type `Lock` **est** la structure de synchronisation : pas d'indirection par l'en-tête, pas d'allocation de sync block, et un chemin d'acquisition écrit pour ce seul usage.

| Étape | `lock (object)` | `lock (Lock)` |
|---|---|---|
| Localiser la structure de synchro | indirection via l'en-tête de l'objet | l'objet **est** la structure |
| Première acquisition | allocation d'un sync block | rien |
| Chemin rapide non contendu | `CompareExchange` + vérifications historiques | `CompareExchange` |
| Attente contendue | spin puis événement du noyau | spin **adaptatif** puis événement |

**Pourquoi le gain augmente avec la contention (0,79 → 0,67) ?**

Le `Lock` de .NET 9 implémente un **spin adaptatif** : il ajuste la durée d'attente active selon l'historique de contention observé, avant de basculer sur une attente noyau — laquelle coûte environ 10 µs de changement de contexte. `Monitor` a une stratégie de spin plus rigide, héritée de contraintes de compatibilité.

Sur 16 threads, ce réglage évite environ 5 200 passages en attente noyau : d'où les 3,1 ms gagnées.

**Ce qui n'apparaît pas dans le tableau, et qui compte davantage**

Le gain de 21 à 33 % est réel mais modeste. **L'argument principal de `Lock` n'est pas la performance, c'est la sûreté** :

- impossible de verrouiller par erreur sur un objet exposé publiquement ;
- le type documente son intention (`private readonly Lock _gate` se lit sans commentaire) ;
- `TryEnter`, `EnterScope` et `IsHeldByCurrentThread` sont sur le type, pas dispersés dans `Monitor` ;
- le compilateur reconnaît le type et génère le bon code sans intervention.

C'est le **troisième** sous-module d'affilée où la nouveauté sert d'abord la conception : `field` (×1,00), extension members (×1,00), `Lock` (×1,3). À souligner en clôture de module.

---

### 📖 Ce qu'il faut savoir en plus

Les trois autres apports de C# 13 qui touchent le code d'HYPERLANE.

**`[OverloadResolutionPriority]`** — déjà rencontré au 1.1 : permet d'ajouter une surcharge `params ReadOnlySpan<T>` à côté d'une `params T[]` déployée, sans changer la résolution pour les appelants recompilés.

**Accès indexé implicite dans les initialiseurs d'objet** — l'opérateur `^` est désormais utilisable dans un initialiseur :

```csharp
var journal = new TickJournal
{
    Historique = { [^1] = "dernier tick", [^2] = "avant-dernier" }   // C# 13
};
```

**`ref` locals et `unsafe` dans les itérateurs et méthodes async** — autorisés tant qu'ils ne franchissent pas un `yield` ou un `await`. Cela débloque des opérateurs LINQ performants qu'on écrira au module 2 :

```csharp
public static IEnumerable<double> MoyenneGlissante(double[] source, int fenetre)
{
    for (var i = fenetre; i <= source.Length; i++)
    {
        ref var debut = ref source[i - fenetre];      // ref local : légal depuis C# 13
        var somme = 0d;
        for (var j = 0; j < fenetre; j++) somme += Unsafe.Add(ref debut, j);

        yield return somme / fenetre;                 // le ref ne franchit PAS le yield
    }
}
```

**La séquence d'échappement `\e`** — remplace `\u001b` pour le caractère d'échappement ANSI. Utile pour la console colorée du serveur de jeu :

```csharp
Console.WriteLine($"\e[31mFamine sur {planete.Code}\e[0m");
```

**Limite importante de `Lock`** : il n'est **pas** utilisable avec `Monitor.Wait`/`Pulse`. Si le code a besoin de ces primitives, il faut rester sur `object`. Et il ne peut pas être détenu à travers un `await` — le compilateur l'interdit. Ce cas est traité au module 3 avec `SemaphoreSlim`.

---

### 🧪 Atelier — sécuriser le marché planétaire

**Le contexte** : le marché est accédé par plusieurs joueurs simultanément. Le code de départ fonctionne en mono-joueur et corrompt les stocks dès qu'il y a concurrence.

**Le code de départ, fourni** :

```csharp
// exercises/Day1/Ex05_Market/Depart.cs
public sealed class PlanetMarketDepart
{
    private readonly long[] _stocks = new long[5];
    private readonly Dictionary<FactionCode, long> _volumeParFaction = new();

    public PlanetMarketDepart(long minerai, long credits)
    {
        _stocks[(int)Resource.Minerai] = minerai;
        _stocks[(int)Resource.Credits] = credits;
    }

    public long StockOf(Resource resource) => _stocks[(int)resource];   // lecture non protégée

    public bool TryBuy(Resource resource, long quantite, FactionCode acheteur, out long prixTotal)
    {
        prixTotal = 0;

        // Lecture, décision, écriture : trois opérations NON atomiques
        var stock = _stocks[(int)resource];
        if (stock < quantite) return false;

        var prixUnitaire = PrixCourant(resource);
        _stocks[(int)resource] = stock - quantite;

        prixTotal = prixUnitaire * quantite;
        _stocks[(int)Resource.Credits] += prixTotal;

        _volumeParFaction[acheteur] = _volumeParFaction.GetValueOrDefault(acheteur) + quantite;
        return true;
    }

    private long PrixCourant(Resource resource)
    {
        var stock = _stocks[(int)resource];
        var facteur = Math.Clamp(1.0 + (5_000 - stock) / 5_000.0, 0.4, 3.0);
        return Math.Max(1, (long)(10 * facteur));
    }
}
```

**Ta mission**

1. rendre `TryBuy` atomique avec `System.Threading.Lock` — lecture, décision et écriture indivisibles ;
2. le prix doit être calculé **sous le verrou**, pour être cohérent avec le stock utilisé ;
3. les statistiques par faction doivent sortir de la section critique — elles n'ont pas besoin d'être cohérentes à l'instant près ;
4. ajouter `TryBuyWithTimeout(..., TimeSpan timeout)` qui abandonne plutôt que d'attendre indéfiniment ;
5. `StockOf` doit retourner une valeur cohérente.

**🎯 Objectif chiffré** (16 threads, 10 000 achats chacun)

| Critère | Départ | À atteindre |
|---|---:|---:|
| Invariant de conservation | ❌ échoue | ✅ |
| Stock jamais négatif | ❌ échoue | ✅ |
| Mean | 4 210 µs | **< 8 000 µs** |

⚠️ **Lis bien l'objectif de temps** : la version correcte sera **plus lente** que la version fausse. C'est attendu — la version de départ est rapide parce qu'elle ne synchronise rien. On accepte un facteur inférieur à 2 pour obtenir la justesse.

---

### ✅ Correction

```csharp
using System.Collections.Concurrent;
using System.Threading;

public sealed class PlanetMarket
{
    private readonly Lock _gate = new();
    private readonly long[] _stocks = new long[5];

    // (3) Statistiques hors verrou : structure concurrente dédiée
    private readonly ConcurrentDictionary<FactionCode, long> _volumeParFaction = new();

    public PlanetMarket(long minerai, long credits)
    {
        _stocks[(int)Resource.Minerai] = minerai;
        _stocks[(int)Resource.Credits] = credits;
    }

    // (5) Lecture sous verrou : sinon on peut lire une valeur à mi-transaction
    public long StockOf(Resource resource)
    {
        lock (_gate) { return _stocks[(int)resource]; }
    }

    // (1) Lecture, décision et écriture dans UNE section critique
    public bool TryBuy(Resource resource, long quantite, FactionCode acheteur, out long prixTotal)
    {
        prixTotal = 0;
        if (quantite <= 0) return false;

        lock (_gate)
        {
            var index = (int)resource;
            if (_stocks[index] < quantite) return false;

            // (2) Prix calculé ICI, sur le stock courant, sous le MÊME verrou
            var prixUnitaire = PrixCourantSansVerrou(resource);

            _stocks[index] -= quantite;
            prixTotal = prixUnitaire * quantite;
            _stocks[(int)Resource.Credits] += prixTotal;
        }

        // (3) Hors verrou : la section critique reste la plus courte possible
        _volumeParFaction.AddOrUpdate(acheteur, quantite, static (_, actuel) => actuel + quantite);
        return true;
    }

    // (4) Variante qui abandonne au lieu d'attendre
    public bool TryBuyWithTimeout(
        Resource resource, long quantite, FactionCode acheteur, TimeSpan timeout, out long prixTotal)
    {
        prixTotal = 0;
        if (quantite <= 0) return false;

        if (!_gate.TryEnter(timeout)) return false;      // marché saturé : refus propre

        try
        {
            var index = (int)resource;
            if (_stocks[index] < quantite) return false;

            var prixUnitaire = PrixCourantSansVerrou(resource);
            _stocks[index] -= quantite;
            prixTotal = prixUnitaire * quantite;
            _stocks[(int)Resource.Credits] += prixTotal;
        }
        finally { _gate.Exit(); }                        // libéré dans TOUS les chemins

        _volumeParFaction.AddOrUpdate(acheteur, quantite, static (_, actuel) => actuel + quantite);
        return true;
    }

    /// <summary>À n'appeler QUE sous le verrou. Le suffixe le rend explicite à l'appel.</summary>
    private long PrixCourantSansVerrou(Resource resource)
    {
        var stock = _stocks[(int)resource];
        var facteur = Math.Clamp(1.0 + (5_000 - stock) / 5_000.0, 0.4, 3.0);
        return Math.Max(1, (long)(10 * facteur));
    }

    public long VolumeOf(FactionCode faction) => _volumeParFaction.GetValueOrDefault(faction);
}
```

---

### 🔬 Le benchmark de l'atelier

Ici le benchmark ne suffit pas : **le test de justesse compte davantage**.

```csharp
[MemoryDiagnoser]
[ThreadingDiagnoser]
public class Ex05_MarketBenchmarks
{
    private const int Threads = 16;
    private const int AchatsParThread = 10_000;
    private const long StockInitial = 10_000_000;

    [Benchmark(Baseline = true, Description = "Départ (incorrect)")]
    public long Depart()
    {
        var marche = new PlanetMarketDepart(StockInitial, 0);

        Parallel.For(0, Threads, new ParallelOptions { MaxDegreeOfParallelism = Threads }, _ =>
        {
            for (var i = 0; i < AchatsParThread; i++)
                marche.TryBuy(Resource.Minerai, 10, FactionCode.CSL, out _);
        });

        return marche.StockOf(Resource.Minerai);
    }

    [Benchmark(Description = "Votre solution")]
    public long Votre()
    {
        var marche = new PlanetMarket(StockInitial, 0);

        Parallel.For(0, Threads, new ParallelOptions { MaxDegreeOfParallelism = Threads }, _ =>
        {
            for (var i = 0; i < AchatsParThread; i++)
                marche.TryBuy(Resource.Minerai, 10, FactionCode.CSL, out _);
        });

        return marche.StockOf(Resource.Minerai);
    }
}
```

```csharp
[Fact]
public void L_invariant_de_conservation_est_respecte_sous_seize_threads()
{
    const long StockInitial = 10_000_000;
    var marche = new PlanetMarket(StockInitial, 0);
    var achetes = 0L;

    Parallel.For(0, 16, new ParallelOptions { MaxDegreeOfParallelism = 16 }, _ =>
    {
        var sousTotal = 0L;
        for (var i = 0; i < 10_000; i++)
            if (marche.TryBuy(Resource.Minerai, 10, FactionCode.CSL, out _)) sousTotal += 10;

        Interlocked.Add(ref achetes, sousTotal);
    });

    // On ne prédit PAS combien d'achats réussissent : on vérifie un INVARIANT
    Assert.Equal(StockInitial - achetes, marche.StockOf(Resource.Minerai));
    Assert.True(marche.StockOf(Resource.Minerai) >= 0);
    Assert.Equal(achetes, marche.VolumeOf(FactionCode.CSL));
}
```

**Résultat attendu et barème**

| Méthode | Mean | Ratio | Lock Contentions | Invariant |
|---|---:|---:|---:|---|
| `Depart` | 4 210 µs | 1,00 | 0 | ❌ **stock final faux d'environ 140 000** |
| `Votre` | **6 840 µs** | 1,63 | 148 100 | ✅ |

**Les erreurs typiques**

| Ce que le stagiaire a fait | Symptôme |
|---|---|
| Calculé le prix **avant** le `lock` | invariant OK, mais prix incohérent avec le stock — test `Prix_coherent` en échec |
| Mis les statistiques **dans** le verrou | Mean autour de 9 200 µs — section critique allongée pour rien |
| Verrouillé `TryBuy` mais pas `StockOf` | lectures à mi-transaction, test intermittent |
| Utilisé `Interlocked` sur `_stocks[index]` seul | invariant en échec : **deux** valeurs à modifier de façon cohérente |
| Oublié `_gate.Exit()` dans un chemin de retour anticipé | blocage définitif au deuxième appel |

**Le piège n°1** : l'avant-dernière ligne. `Interlocked` est atomique **sur une variable**. Ici il faut modifier le stock **et** les crédits de façon cohérente — c'est exactement la frontière où `Interlocked` ne suffit plus et où le verrou devient nécessaire. Sujet repris en profondeur au module 8.

---

### 🎯 Point à retenir

`System.Threading.Lock` apporte 21 à 33 % de performance, mais son vrai bénéfice est la **sûreté** : un type dédié qu'on ne peut pas verrouiller par erreur. Et tout ce qui doit être cohérent avec l'état modifié se lit **sous le même verrou**.

### 👍 Bonnes pratiques

- `private readonly Lock _gate = new()` pour tout nouveau code ; migration progressive de l'existant.
- `CA2002` en erreur : interdit le verrou sur un objet à identité faible (`this`, `string`, `Type`).
- Suffixe `SansVerrou` sur les méthodes privées à appeler en section critique.
- Section critique la plus courte possible : sortir statistiques, logs et I/O.
- `TryEnter(timeout)` plutôt qu'une attente infinie sur un chemin de requête utilisateur.

### ⚠️ Points de difficulté

- `Lock` n'est **pas** utilisable avec `Monitor.Wait`/`Pulse` : rester sur `object` si besoin de ces primitives.
- `Lock` ne peut pas être détenu à travers un `await` — le compilateur l'interdit (module 3).
- Migrer un `object` en `Lock` sans changer les `Monitor.Enter(obj)` explicites laisse deux mécanismes en parallèle.
- `Lock` est réentrant, comme `Monitor` — ce qui masque parfois des conceptions douteuses.

### 🚀 Pour aller plus loin

**A. Vérifier qu'on est bien sous le verrou, en développement**

```csharp
private long PrixCourantSansVerrou(Resource resource)
{
    Debug.Assert(_gate.IsHeldByCurrentThread,
        "PrixCourantSansVerrou appelée hors section critique.");

    var stock = _stocks[(int)resource];
    var facteur = Math.Clamp(1.0 + (5_000 - stock) / 5_000.0, 0.4, 3.0);
    return Math.Max(1, (long)(10 * facteur));
}
```

**B. Copy-on-write : supprimer complètement le verrou en lecture**

```csharp
public sealed class ImmutableMarketSnapshot
{
    private MarketState _state = MarketState.Empty;

    // Lecture : AUCUN verrou, aucune contention, quel que soit le nombre de lecteurs
    public long StockOf(Resource resource) => Volatile.Read(ref _state).Stocks[(int)resource];

    // Écriture : boucle de comparaison-échange sur l'état entier
    public bool TryBuy(Resource resource, long quantite)
    {
        MarketState courant, nouveau;

        do
        {
            courant = Volatile.Read(ref _state);
            if (!courant.CanBuy(resource, quantite)) return false;
            nouveau = courant.WithPurchase(resource, quantite);
        }
        while (Interlocked.CompareExchange(ref _state, nouveau, courant) != courant);

        return true;
    }
}
// Pertinent quand les lectures dominent massivement. Le coût se déplace sur l'écrivain.
```

### ❌ Erreurs à ne pas faire

- `lock (this)` ou `lock ("cle")` : verrou exposé, voire global au processus.
- Calculer une valeur dépendant de l'état protégé **avant** d'entrer dans le verrou.
- Faire une I/O, un log ou un `await` à l'intérieur d'un verrou.
- Croire qu'`Interlocked` suffit dès qu'il y a deux valeurs à modifier ensemble.
- Tester du code concurrent en vérifiant une valeur exacte plutôt qu'un invariant.

---

# 🏁 Synthèse du module 1

## Le tableau à projeter en clôture

| § | Amélioration | Gain mesuré | Origine réelle du gain | Vaut le coup à partir de |
|---|---|---:|---|---|
| 1.1 | `params ReadOnlySpan<T>` + spread | ×10, 136 o → 0 o | suppression du tableau de `params` et des itérateurs `Concat` | 1 000 appels/s |
| 1.2 | `ref struct` + `Span<T>` | ×4, −90 % d'alloc | suppression de ~80 000 strings intermédiaires | 1 000 lignes/appel |
| 1.3 | `field` | **×1,00** | aucun — le gain est de conception | toujours (invariant) |
| 1.3 | Valeur de retour vs exception | **×236** à 5 % d'échec | ~43 µs de capture de pile par exception | 1 échec sur 1 000 |
| 1.4 | Extension members | **×1,00** | aucun — sucre syntaxique strict | toujours (lisibilité) |
| 1.4 | Éviter l'interface inutile | ×2,24 | appel virtuel non inliné | chemin appelé en boucle |
| 1.5 | `System.Threading.Lock` | ×1,3 à ×1,5 | pas de sync block, spin adaptatif | tout nouveau code |

## Les trois enseignements du module

**1. Trois nouveautés sur cinq n'apportent aucun gain de performance significatif.** `field` mesure ×1,00, les extension members ×1,00, `Lock` ×1,3. Et ce sont celles qui améliorent le plus la conception. **Une nouveauté du langage n'est pas une optimisation par défaut.**

**2. Le gain vient toujours de la suppression d'objets intermédiaires**, jamais du résultat. Les 624 Ko restants du parser sont la valeur produite ; les 5 616 Ko supprimés étaient du déchet.

**3. Les deux décisions les plus coûteuses du module ne sont pas des questions de syntaxe** : modéliser un échec attendu par une exception (×236) et modéliser un calcul dérivé par une interface (×2,24). Ce sont des décisions de **conception**, prises une fois, qu'aucune micro-optimisation ultérieure ne rattrapera.

## Vérification avant de passer au module 2

Chaque stagiaire doit avoir :

- [ ] les cinq benchmarks d'atelier au vert (`./exercise.sh 1.1` à `1.5`) ;
- [ ] su expliquer pourquoi `Production_ExtensionAvecDictionary` affiche 18 380 µs ;
- [ ] su dire à quel taux d'occurrence une exception devient le mauvais outil ;
- [ ] su expliquer pourquoi sa solution du 1.5 est **plus lente** que le départ, et pourquoi c'est correct ;
- [ ] `Hyperlane.Core` qui compile avec `CargoHold`, `HoloNetDecoder`, `PlanetWarehouse`, `FactionTreasury`, `GameRuleExtensions` et `PlanetMarket`.

Ces six briques sont utilisées dès le module 2 : le planificateur de routes appellera `JumpCost` et `TravelTime`, et le tableau de bord agrégera les `PlanetWarehouse`.
