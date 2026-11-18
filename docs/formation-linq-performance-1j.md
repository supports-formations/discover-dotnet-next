# LINQ Expert & Performance — Formation 1 journée
### v2 — analyse systématique sur 3 axes

**Public** : développeurs .NET confirmés qui « décrochent » vers Dapper ou des procédures stockées dès que ça devient chaud.
**Objectif** : savoir *quand* LINQ est le bon outil, *comment* écrire du LINQ qui tient la charge, et *où* est la frontière légitime avec le SQL brut.
**Pré-requis** : C# 12+, EF Core 8+ (les nouveautés .NET 9 sont marquées).

---

## La grille d'analyse de la journée

Chaque cas avant/après est décomposé sur **trois axes**, systématiquement, avec l'instrument de mesure associé :

| Axe | Ce qu'on regarde | Instrument |
|---|---|---|
| **① CPU .NET** | appels virtuels, invocations de délégué, complexité algorithmique, coût du pipeline EF | BenchmarkDotNet (`Mean`, `Ratio`) |
| **② Mémoire** | allocations, pression GC, gen0/gen1/gen2, LOH, taille du working set | `[MemoryDiagnoser]`, `[GcServer]`, compteurs `dotnet-counters` |
| **③ Exécution SQL** | logical reads, CPU time serveur, elapsed time, plan (seek vs scan), memory grant, spills tempdb, nombre d'allers-retours | `SET STATISTICS IO/TIME ON`, plan d'exécution réel, `sys.dm_exec_query_stats` |

**Point méthodologique à poser dès l'ouverture, et qui structure la journée :**

Les trois axes ne se compensent pas, et ils n'ont pas la même échelle. Un gain CPU .NET se compte en microsecondes. Un gain SQL se compte en centaines de millisecondes, voire en secondes. **Sur une application connectée à une base, l'axe ③ domine les deux autres d'un à trois ordres de grandeur.**

Corollaire direct, et c'est l'erreur que je vois le plus souvent : optimiser une chaîne LINQ en mémoire (axe ①) pendant qu'une requête fait un table scan (axe ③) est du temps perdu. **L'ordre de travail est toujours ③ → ② → ①.**

Certains cas de la journée n'ont pas les trois axes — un problème de LINQ to Objects n'a pas d'axe SQL. Je le note explicitement plutôt que de remplir la case artificiellement.

---

## Déroulé de la journée

| Créneau | Module | Axe dominant | Type |
|---|---|---|---|
| 09h00 – 09h35 | **M0** — Mesurer sur les trois axes | — | Démo |
| 09h35 – 10h30 | **M1** — Exécution différée & frontière `IEnumerable`/`IQueryable` | ③ puis ② | Théorie + TP |
| 10h30 – 10h45 | Pause | | |
| 10h45 – 11h45 | **M2** — Complexité algorithmique en mémoire | ① et ② | Théorie + TP |
| 11h45 – 12h30 | **M3** — Allocations, closures, opérateurs modernes | ② puis ① | Théorie + TP |
| 12h30 – 13h30 | Déjeuner | | |
| 13h30 – 14h35 | **M4** — Projection, `Include`, N+1, tracking | ③ et ② | Théorie + TP |
| 14h35 – 15h20 | **M5** — Sargabilité : faire travailler les index | ③ | Théorie + TP |
| 15h20 – 15h35 | Pause | | |
| 15h35 – 16h20 | **M6** — Plan cache, paramétrage, `Contains` | ③ (CPU serveur) | Théorie + TP |
| 16h20 – 17h00 | **M7** — Pagination, agrégations, opérations en masse | ③ | Théorie + TP |
| 17h00 – 17h30 | **M8** — Frontière LINQ / Dapper / procédure stockée | — | Atelier collectif |

Ratio : ~2h de magistral, ~4h de manipulation.

---

## Jeu de données du fil rouge

```csharp
public class Customer { public int Id; public string Name; public string Country; public DateTime CreatedAt; public List<Order> Orders; }
public class Order    { public int Id; public int CustomerId; public DateTime PlacedAt; public OrderStatus Status; public decimal Total; public List<OrderLine> Lines; public List<Tag> Tags; public Customer Customer; }
public class OrderLine{ public int Id; public int OrderId; public int ProductId; public int Quantity; public decimal UnitPrice; public Product Product; }
public class Product  { public int Id; public string Sku; public string Name; public int CategoryId; public decimal Price; }
```

50 000 clients · 20 000 produits · 800 000 commandes · **~4,3 M de lignes** · ~1,3 M d'associations tags.
Environ 1,7 Go de données, 0,4 Go d'index.

Volumétrie choisie pour que les trois axes soient visibles simultanément : assez gros pour que le SQL décroche, assez gros pour saturer gen0, assez gros pour que le quadratique soit inacceptable.

**Deux régimes de taille de commande, à dessein** : 98 % des commandes ont 1 à 8 lignes, 2 % en ont 40 à 80. Sur les commandes normales le facteur de duplication cartésienne reste sous 3 et **`AsSplitQuery` ne gagne pas** ; sur les commandes bulk il atteint ~5 et il gagne franchement. Avoir les deux régimes dans le même jeu permet de *démontrer* le seuil de décision de M4.2 au lieu de l'énoncer.

**Le jeu de données et l'application sont fournis** dans `LinqPerfLab/` : schéma, générateur T-SQL déterministe, index commentés module par module, `DbContext` à deux configurations opposées (Naive / Strict), scénarios avant/après exécutables et benchmarks. Voir `LinqPerfLab/README.md` pour l'installation en cinq étapes.

> **Avertissement sur les chiffres de ce support** : toutes les figures qui suivent (logical reads, méga-octets, facteurs) sont des **ordres de grandeur illustratifs**, calculés et non mesurés. Elles servent à situer les échelles relatives entre les trois axes. Les remplacer par les mesures réelles de la machine de formation avant la séance — c'est d'ailleurs l'exercice M0.

---

# M0 — Mesurer sur les trois axes

## ① et ② — BenchmarkDotNet

```csharp
[MemoryDiagnoser]                       // axe ② : Allocated, Gen0/Gen1/Gen2
[SimpleJob(RuntimeMoniker.Net90)]
[HideColumns("Job", "Error", "StdDev")]
public class LinqBench
{
    private readonly List<Order> _orders = DataSet.Orders(800_000);

    [Benchmark(Baseline = true)] public decimal Before() => /* ... */;
    [Benchmark]                  public decimal After()  => /* ... */;
}
```

Colonnes à lire : `Mean` et `Ratio` pour ①, `Allocated`, `Gen0`, `Gen1`, `Gen2` pour ②.

**À expliquer explicitement** : `Gen0` élevé n'est pas grave en soi (collecte peu coûteuse). Ce qui coûte, c'est **`Gen1` et surtout `Gen2` non nuls** : cela signifie que des objets survivent aux collectes, sont promus, et déclenchent à terme des GC bloquants. Une collecte gen2 sur un heap de 2 Go, c'est plusieurs centaines de millisecondes de pause — l'équivalent d'une requête SQL lente, mais qui frappe **toutes** les requêtes en vol.

**Le seuil LOH** : toute allocation de tableau > 85 000 octets part directement sur le Large Object Heap, donc en gen2, et n'est pas compactée par défaut. Un `OrderBy` sur 4,3 M d'éléments alloue un tableau de 4,3 M de références = **~34 Mo sur le LOH**. C'est le lien concret entre « j'ai écrit un `OrderBy` » et « mon service a des pics de latence ».

## ② en conditions réelles — dotnet-counters

BenchmarkDotNet mesure un micro-benchmark isolé. Sous charge réelle :

```bash
dotnet-counters monitor -p <pid> --counters System.Runtime,Microsoft.AspNetCore.Hosting
```

Compteurs à surveiller : `gc-heap-size`, `gen-2-gc-count`, `time-in-gc`, `alloc-rate`, `loh-size`.
Règle de terrain : au-delà de **5 % de `time-in-gc`**, le problème est l'allocation, pas le CPU.

## ③ — Les trois instruments SQL

**Le SQL généré, sans exécuter :**
```csharp
var sql = db.Orders.Where(o => o.Status == OrderStatus.Paid).ToQueryString();
```

**Le coût réel :**
```sql
SET STATISTICS IO ON;
SET STATISTICS TIME ON;
```

Trois chiffres, dans cet ordre de priorité :
1. **`logical reads`** — le nombre de pages de 8 Ko lues. C'est la métrique la plus stable (indépendante du cache, de la charge, de la machine). C'est *la* mesure de référence pour comparer deux requêtes.
2. **`CPU time`** — le temps processeur serveur. Un écart fort entre CPU time et elapsed time indique une attente (I/O, verrou, réseau).
3. **`elapsed time`** — le temps mur.

**Le plan d'exécution réel** (pas estimé). Quatre choses à y chercher :
- **Index Scan** au lieu d'**Index Seek** → problème de sargabilité (M5)
- **Key Lookup** → index non couvrant, coût multiplié par le nombre de lignes remontées (M4)
- **Warning triangle jaune** sur un opérateur Sort ou Hash → **spill en tempdb** : la mémoire accordée était insuffisante, le moteur écrit sur disque. Facteur 10 à 100 sur l'elapsed time.
- **Écart lignes estimées / lignes réelles** > facteur 10 → statistiques obsolètes, ou prédicat que l'optimiseur ne sait pas estimer.

**Le memory grant** — souvent ignoré, et pourtant central. Chaque requête avec un `Sort`, un `Hash Join` ou un `Hash Aggregate` **réserve de la mémoire serveur avant de s'exécuter**. Une requête qui demande 2 Go bloque les autres, même si elle est rapide. Visible dans les propriétés du nœud racine du plan (`MemoryGrantInfo`) et dans `sys.dm_exec_query_memory_grants`.

## ③ côté client — compter les allers-retours

Le coût le plus souvent sous-estimé, parce qu'il n'apparaît sur **aucun** des instruments ci-dessus :

```csharp
options.LogTo(Console.WriteLine, new[] { DbLoggerCategory.Database.Command.Name }, LogLevel.Information);
```

Une requête de 2 ms exécutée 1 000 fois, sur un réseau à 0,8 ms de RTT, coûte **2,8 secondes** dont 0,8 s de SQL et 2 s d'attente réseau pure. Aucun plan d'exécution ne le montrera. C'est tout le sujet du N+1 (M4.1).

## Configuration EF « stricte » pour la formation

```csharp
options.LogTo(Console.WriteLine, LogLevel.Information)
       .EnableSensitiveDataLogging()                    // dev uniquement
       .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
       .ConfigureWarnings(w => w
            .Throw(RelationalEventId.MultipleCollectionIncludeWarning)
            .Throw(CoreEventId.NavigationLazyLoading));
```

Faire **lever** les avertissements plutôt que les logger. EF signale déjà la moitié des problèmes de cette journée ; personne ne lit les logs.

---

# M1 — Exécution différée & frontière IEnumerable / IQueryable

## 1.1 — L'énumération multiple

### ❌ Avant

```csharp
IEnumerable<Order> recent = orders.Where(o => o.PlacedAt > cutoff);

var n     = recent.Count();
var first = recent.First();
foreach (var o in recent) { /* ... */ }
```

### ✅ Après

```csharp
List<Order> recent = orders.Where(o => o.PlacedAt > cutoff).ToList();

var n     = recent.Count;      // propriété, O(1)
var first = recent[0];
foreach (var o in recent) { /* ... */ }
```

### Analyse

| Axe | Avant | Après |
|---|---|---|
| **① CPU .NET** | 3 énumérations complètes de 800 k éléments, prédicat évalué 3 fois → 2,4 M invocations de délégué. `Count()` sur une séquence paresseuse est O(n). | 1 seule énumération, 800 k invocations. `Count` devient un accès à un champ (`ICollection<T>.Count`) : **O(1)**. `First()` devient un accès indexé. |
| **② Mémoire** | Allocation constante (les itérateurs ne bufferisent pas) — **c'est le seul avantage de la version « avant »**. | Buffer de résultat alloué. Si 300 k éléments survivent au filtre : tableau interne de 300 k références ≈ **2,4 Mo → LOH → gen2**. On paie de la mémoire pour économiser du CPU. Arbitrage explicite. |
| **③ SQL** | **Sans objet si la source est en mémoire.** Si la source est un `IQueryable` : **3 allers-retours réseau au lieu d'1** — c'est alors le coût dominant, et de loin. | 1 aller-retour. |

**Le vrai enseignement du cas** : la version « après » n'est pas universellement meilleure. Elle échange l'axe ② contre l'axe ①. La règle de décision est : *matérialiser dès qu'on consomme deux fois ou que la source est distante, streamer sinon*. Sur une séquence consommée une seule fois et volumineuse, `ToList()` est une régression pure sur l'axe ②.

**Détection automatisée** : analyzer `CA1851`.

## 1.2 — La frontière que personne ne voit

### ❌ Avant

```csharp
public IEnumerable<Order> GetOrders() => _db.Orders;      // signature en IEnumerable

var paid = repo.GetOrders()
               .Where(o => o.Status == OrderStatus.Paid)
               .OrderByDescending(o => o.PlacedAt)
               .Take(20)
               .ToList();
```

SQL généré : `SELECT * FROM Orders` — sans `WHERE`, sans `ORDER BY`, sans `TOP`.

### ✅ Après

```csharp
public IQueryable<Order> GetOrders() => _db.Orders.AsNoTracking();
```

SQL généré : `SELECT TOP(20) ... FROM Orders WHERE Status = @p ORDER BY PlacedAt DESC`.

### Analyse

| Axe | Avant | Après |
|---|---|---|
| **① CPU .NET** | Résolution de surcharge sur `Enumerable.Where` (délégué compilé) au lieu de `Queryable.Where` (arbre d'expression). Tri client sur 800 k éléments : `ArraySortHelper`, O(n log n) ≈ 15,6 M comparaisons **dans le processus applicatif**. Le CPU du serveur d'application est consommé pour un travail qui relève de la base. | Tri délégué au moteur SQL, qui utilise l'index `IX_Orders_PlacedAt`. Coût client : matérialisation de 20 objets. |
| **② Mémoire** | 800 k entités `Order` matérialisées + 800 k entrées de change tracker + snapshots. À ~200 octets par entité : **~320 Mo** alloués, dont un buffer de tri de 6,4 Mo sur le LOH. Collecte gen2 quasi garantie. **799 980 objets alloués pour être immédiatement jetés.** | 20 entités, ~4 Ko. Sans tracking : pas de snapshot. Ratio d'allocation ≈ **1/16 000**. |
| **③ SQL** | `Clustered Index Scan` sur toute la table : ~180 000 logical reads, ~1,4 Go transférés sur le réseau. Aucun index n'est utilisable puisqu'il n'y a aucun prédicat. | `Index Seek` + `TOP` : le moteur s'arrête après 20 lignes. **~25 logical reads**, quelques kilo-octets sur le réseau. Facteur ~7 000 en lectures logiques. |

**Pourquoi ce cas ouvre la journée** : c'est le seul de la journée où les trois axes s'effondrent simultanément, et où le code compile, passe les tests unitaires et ne produit aucun avertissement. C'est le bug de performance le plus coûteux et le plus invisible de l'écosystème EF.

> **Discussion architecture à ne pas éluder** : exposer `IQueryable` fait fuiter la persistance dans le domaine. Deux positions défendables — assumer `IQueryable` en sortie de repository, ou garder `IEnumerable` **mais descendre le filtrage dans le repository** (`GetPaidOrders(int take)`). La troisième voie — `IEnumerable` + filtrage chez l'appelant — est celle qu'on vient de voir, et elle n'est pas défendable.

### 🛠️ TP 1 (30 min) — `dotnet run --project src/LinqPerfLab.Lab -- m1.2`
1. Lire le SQL généré dans les deux versions (`ToQueryString`).
2. Mesurer sur les trois axes : `logical reads` (③), `Allocated` + `Gen2` (②), `Mean` (①). Consigner dans un tableau comparatif.
3. Refactorer un repository fourni exposant 6 méthodes en `IEnumerable`, en justifiant chaque choix sur les trois axes.

---

# M2 — Complexité algorithmique en mémoire

Module purement in-memory : **l'axe ③ est absent**, et c'est important à dire. Ce sont les axes ① et ② qui font ici des facteurs 100 à 1000.

## 2.1 — Le `Any` imbriqué

### ❌ Avant

```csharp
var flagged = orders
    .Where(o => blockedCustomers.Any(c => c.Id == o.CustomerId))
    .ToList();
```

### ✅ Après

```csharp
var blockedIds = blockedCustomers.Select(c => c.Id).ToHashSet();   // hors du Where

var flagged = orders
    .Where(o => blockedIds.Contains(o.CustomerId))
    .ToList();
```

### Analyse

| Axe | Avant | Après |
|---|---|---|
| **① CPU .NET** | Pour chaque commande, `Any` parcourt linéairement les 5 000 clients bloqués. **O(n × m)** = jusqu'à 4×10⁹ comparaisons. Mesuré : ~4,1 s. | Construction du set en O(m), puis lookup O(1) amorti. **O(n + m)** ≈ 805 000 opérations. Mesuré : ~12 ms. **Facteur ~340.** |
| **② Mémoire** | Un itérateur `Any` alloué **par élément source** : 800 000 machines à états de ~48 octets = **~38 Mo de déchets gen0**, plus la closure capturant `o` à chaque itération. Taux d'allocation massif → GC gen0 en rafale. | Un `HashSet<int>` de 5 000 entrées ≈ 60 Ko, alloué **une fois**. Le `Contains` sur `HashSet<int>` n'alloue rien (comparateur par défaut dévirtualisé pour les types primitifs). Allocation divisée par ~600. |
| **③ SQL** | **Sans objet** — traitement en mémoire. ⚠️ Si `orders` était un `IQueryable`, EF traduirait ce prédicat en `EXISTS` corrélé, correct et efficace. Le problème est spécifique à LINQ to Objects. | — |

**Le piège à démontrer en direct** : déplacer `ToHashSet()` **à l'intérieur** du `Where`. On reconstruit un set de 5 000 entrées 800 000 fois : on est alors *pire* que la version initiale sur les deux axes. Le placement de la construction est ce qui fait tout le gain, pas le choix de la structure.

## 2.2 — Le join manuel

### ❌ Avant

```csharp
var report = orders.Select(o => new {
    Order = o,
    Customer = customers.FirstOrDefault(c => c.Id == o.CustomerId)
}).ToList();
```

### ✅ Après

```csharp
var byId = customers.ToDictionary(c => c.Id);

var report = orders.Select(o => new {
    Order = o,
    Customer = byId.GetValueOrDefault(o.CustomerId)
}).ToList();
```

Et pour une relation 1-N :

```csharp
var linesByOrder = lines.ToLookup(l => l.OrderId);    // une seule passe O(n)

var report = orders.Select(o => new { Order = o, Lines = linesByOrder[o.Id] }).ToList();
```

### Analyse

| Axe | Avant | Après |
|---|---|---|
| **① CPU .NET** | `FirstOrDefault` sur 50 000 clients : ~25 000 comparaisons en moyenne par commande → **2×10¹⁰ opérations**. Ne se termine pas sur le jeu de données. | Dictionnaire construit en O(m), lookup O(1). Total O(n + m). |
| **② Mémoire** | Un itérateur `FirstOrDefault` + une closure par commande. 800 k types anonymes (inévitables) **plus** 800 k structures temporaires évitables. | Un `Dictionary` de 50 000 entrées (~2 Mo, alloué une fois, promu gen2 mais **stable et réutilisable**). `ToLookup` renvoie une séquence vide au lieu de `null` pour une clé absente : pas de branche, et pas d'allocation pour le cas vide (instance partagée). |
| **③ SQL** | Sans objet. | — |

**Ce que ça reproduit** : `ToLookup` / `ToDictionary`, c'est exactement un *hash join*. On réimplémente en mémoire ce que le moteur SQL fait depuis quarante ans. Le lien à faire explicitement avec le plan d'exécution vu en M0 : quand SQL Server choisit un `Hash Match`, il fait ceci.

## 2.3 — Compter en une passe

### ❌ Avant

```csharp
foreach (var c in customers)
    c.OrderCount = orders.Count(o => o.CustomerId == c.Id);
```

### ✅ Après

```csharp
var counts = orders.CountBy(o => o.CustomerId).ToDictionary();      // .NET 9

foreach (var c in customers)
    c.OrderCount = counts.GetValueOrDefault(c.Id);
```

### Analyse

| Axe | Avant | Après |
|---|---|---|
| **① CPU .NET** | 50 000 × 800 000 = **4×10¹⁰ comparaisons**. Ne se termine pas — c'est l'exercice de démonstration de la journée. | Une passe sur 800 k éléments. |
| **② Mémoire** | 50 000 itérateurs `Count` + closures. Modéré en volume, mais 50 000 passes complètes sur la liste source détruisent toute localité de cache CPU — le coût réel est un problème de bande passante mémoire autant que de comparaisons. | `CountBy` (.NET 9) ne maintient qu'**un `int` par clé**. L'équivalent pré-.NET 9 (`GroupBy(...).ToDictionary(g => g.Key, g => g.Count())`) alloue un `IGrouping` **et sa liste sous-jacente** par clé — soit 50 000 listes contenant au total 800 000 références (~6,4 Mo) alors qu'on ne veut que des compteurs. `CountBy` supprime cette allocation intermédiaire intégralement. |
| **③ SQL** | Sans objet. ⚠️ Sur `IQueryable`, la bonne réponse est un `GroupBy` traduit en `GROUP BY` — voir M7.2. | — |

### 🛠️ TP 2 (35 min) — `-- m2.1`, puis benchmarks `--filter '*M2*'`
Un rapport de facturation fourni tourne en 90 s.
Objectif : passer sous la seconde **à résultat identique** (test de non-régression fourni).
Livrable : pour chaque transformation, la complexité avant/après (axe ①) **et** le delta d'allocation mesuré (axe ②).

---

# M3 — Allocations, closures, opérateurs modernes

Module à dominante **axe ②**. C'est aussi le module où je serai le plus explicite sur les limites : les gains sont réels mais petits en valeur absolue comparés aux modules SQL.

## 3.1 — Le tri pour rien

### ❌ Avant

```csharp
var cheapest = products.OrderBy(p => p.Price).First();
```

### ✅ Après

```csharp
var cheapest = products.MinBy(p => p.Price);      // .NET 6+
```

### Analyse

| Axe | Avant | Après |
|---|---|---|
| **① CPU .NET** | Tri complet O(n log n) : sur 4,3 M d'éléments, ~95 M de comparaisons, chacune passant par un `Comparer<decimal>`. Puis on jette tout sauf un élément. | Passe unique O(n) : 4,3 M comparaisons, un candidat courant maintenu. **~20× plus rapide.** |
| **② Mémoire** | `OrderBy` **bufferise intégralement la source** : tableau de 4,3 M de références (**~34 Mo**) + tableau de clés `decimal` (4,3 M × 16 octets = **~69 Mo**) + tableau d'index (~17 Mo). Soit **~120 Mo, entièrement sur le LOH**, donc en gen2, non compacté. C'est un pic de working set qui peut à lui seul déclencher une collecte gen2 bloquante. | **Allocation constante** : une variable de candidat. `Allocated: 0 B` au benchmark. |
| **③ SQL** | Sans objet en mémoire. ⚠️ **Sur `IQueryable`, ce cas n'existe pas** : EF traduit déjà `OrderBy(...).First()` en `SELECT TOP(1) ... ORDER BY`, ce qui est optimal. | — |

**À dire clairement** : `MinBy` est un gain **LINQ to Objects uniquement**. Remplacer `OrderBy().First()` par `MinBy()` dans une requête EF ne change rien au SQL — et peut même empêcher la traduction. Ne pas appliquer la recette hors contexte.

## 3.2 — `Count() > 0`

### ❌ Avant
```csharp
if (orders.Where(o => o.Status == OrderStatus.Pending).Count() > 0) { ... }
```

### ✅ Après
```csharp
if (orders.Any(o => o.Status == OrderStatus.Pending)) { ... }
```

### Analyse

| Axe | Avant | Après |
|---|---|---|
| **① CPU .NET** | Énumération complète pour produire un nombre dont on ne garde que le signe. | Court-circuit au premier élément trouvé. Le jeu de données contient 12 % de `Pending` : on s'arrête statistiquement au 8ᵉ élément. |
| **② Mémoire** | Un itérateur `Where` + closure — négligeable. **L'écart est faible sur cet axe.** | Idem. Pas d'argument mémoire ici, et je préfère le dire que l'inventer. |
| **③ SQL** | `SELECT COUNT(*) FROM Orders WHERE Status = @p` : le moteur doit lire **toutes** les lignes correspondantes pour les compter. Sur ~96 000 lignes matchantes : plusieurs centaines de logical reads, et une agrégation complète. | `SELECT CASE WHEN EXISTS(SELECT 1 FROM Orders WHERE Status = @p) THEN 1 ELSE 0 END` : l'opérateur `EXISTS` est marqué en *short-circuit* dans le plan — le moteur **arrête la lecture dès la première ligne trouvée**. ~3 logical reads. **Facteur ~400 sur l'axe ③, contre ~2 sur l'axe ①.** |

**Cas d'école pour la grille** : le vrai gain n'est pas là où l'intuition le place. La plupart des développeurs corrigent `Count() > 0` en pensant au CPU client ; le gain réel est à 99 % côté serveur SQL.

**Symétrique honnête** : `Count()` **est** O(1) quand la source est une `ICollection<T>` (fast-path LINQ). `list.Count() > 0` est bénin ; `list.Where(...).Count() > 0` ne l'est pas.

## 3.3 — Chaînes d'opérateurs

### ❌ Avant

```csharp
var total = lines
    .Where(l => l.Quantity > 0)
    .Select(l => l.Quantity * l.UnitPrice)
    .Where(a => a > 0)
    .Sum();
```

### ✅ Après (a) — fusion, on reste en LINQ

```csharp
var total = lines.Sum(l => l.Quantity > 0 ? l.Quantity * l.UnitPrice : 0m);
```

### ✅ Après (b) — sortie de LINQ, chemin chaud avéré uniquement

```csharp
decimal total = 0m;
foreach (var l in CollectionsMarshal.AsSpan(lines))
{
    if (l.Quantity > 0) total += l.Quantity * l.UnitPrice;
}
```

### Analyse

| Axe | Avant | Après (a) | Après (b) |
|---|---|---|---|
| **① CPU .NET** | 4 itérateurs chaînés : chaque `MoveNext()` final cascade à travers toute la chaîne. Sur 4,3 M d'éléments : **~17 M d'appels virtuels d'interface** + ~13 M d'invocations de délégué. Le passage par `IEnumerable<T>` **boxe le struct enumerator** de `List<T>` → le JIT ne peut plus dévirtualiser ni inliner. | 1 itérateur, 1 délégué : 4,3 M appels. **~3×** plus rapide. | Ni interface, ni délégué, ni itérateur. Le JIT élimine les vérifications de bornes sur le `Span`. **5 à 10×** plus rapide que la version initiale. |
| **② Mémoire** | 4 objets itérateurs + 2 closures + **le boxing de l'enumerator** de `List<T>`. En valeur absolue : quelques centaines d'octets par appel. **Négligeable si la méthode est appelée une fois ; déterminant si elle est dans une boucle** — c'est là que se joue le taux d'allocation et donc la fréquence des GC gen0. | 1 itérateur + 1 closure. | `Allocated: 0 B`. `AsSpan` donne un accès direct au tableau interne, **sans copie ni boxing**. |
| **③ SQL** | Sans objet. ⚠️ Sur `IQueryable`, cette chaîne se traduirait en un unique `SELECT SUM(...) WHERE ...` : le problème n'existe pas. | — | — |

> **Le message pédagogique compte plus que le code.** La version (b) ne s'écrit qu'**après** avoir prouvé par profiling que ce chemin est chaud. Le LINQ chaîné est parfaitement acceptable sur 5 000 éléments une fois par requête HTTP. Il ne l'est plus sur 4 M d'éléments dans une boucle batch. La performance n'est pas une propriété du code, c'est une propriété du **couple code × volumétrie × fréquence**.
>
> Et à remettre en perspective : le gain de (b) sur (a) se compte en **millisecondes**. Un seul `Index Scan` évité en M5 vaut plus que toutes les optimisations de ce module réunies. C'est pour ça que M3 est le module le plus court et le premier candidat à la coupe.

## 3.4 — Rattraper les opérateurs modernes

Beaucoup de code lent réimplémente ce que la BCL fournit déjà, optimisé :

| Écrit à la main | Opérateur | Depuis | Gain principal |
|---|---|---|---|
| `GroupBy(k).Select(g => g.First())` | `DistinctBy(k)` | .NET 6 | ② — pas de matérialisation des groupes |
| `OrderBy(x => x)` | `Order()` | .NET 7 | ① — comparateur dévirtualisé |
| `GroupBy(k).ToDictionary(g => g.Key, g => g.Count())` | `CountBy(k)` | .NET 9 | ② — un `int` par clé au lieu d'une liste |
| `GroupBy(k).ToDictionary(g => g.Key, g => g.Sum(...))` | `AggregateBy(k, seed, f)` | .NET 9 | ② — idem |
| `Select((x, i) => (i, x))` | `Index()` | .NET 9 | ① et ② |
| Découpage manuel par lots | `Chunk(size)` | .NET 6 | ② — **borne la mémoire du traitement** |

`Chunk` mérite un focus, parce qu'il agit sur les trois axes à la fois :

```csharp
foreach (var batch in ids.Chunk(500))
    await ProcessAsync(batch);
```

- ② : 500 éléments en vol au lieu de 4,3 M — la consommation mémoire devient **constante** au lieu de linéaire, et la taille du batch devient un paramètre de réglage.
- ③ : chaque lot produit une requête de taille bornée, donc un plan stable et un memory grant prévisible (voir M6.2).

### 🛠️ TP 3 (25 min) — `-- m3.2`, puis benchmarks `--filter '*M3*'`
8 méthodes d'un service fourni. Pour chacune : mesurer, identifier l'anti-pattern, corriger, re-mesurer.
**Livrable : le tableau à 3 axes rempli, pas le code.** Les cases « sans objet » doivent être justifiées.

---

# M4 — Projection, Include, N+1, tracking

Bascule côté EF. Ici les axes ③ et ② dominent, et les gains se comptent en ordres de grandeur.

## 4.1 — Le N+1

### ❌ Avant

```csharp
var orders = await db.Orders.Where(o => o.PlacedAt > cutoff).ToListAsync();

foreach (var o in orders)
    Console.WriteLine($"{o.Customer.Name} — {o.Lines.Count} lignes");
```

### ✅ Après — projection, pas `Include`

```csharp
var rows = await db.Orders
    .Where(o => o.PlacedAt > cutoff)
    .Select(o => new OrderRow(o.Id, o.Customer.Name, o.Lines.Count))
    .ToListAsync();
```

### Analyse (5 000 commandes concernées)

| Axe | Avant | Après |
|---|---|---|
| **① CPU .NET** | 10 001 exécutions du pipeline EF : construction de commande, ouverture/récupération de connexion, matérialisation, `DataReader`. Le coût de *plomberie* EF, répété 10 001 fois, dépasse le coût du SQL lui-même. | 1 seule exécution du pipeline. Matérialisation directe vers un `record` — **pas de construction d'entité, pas de fixup de navigation**. |
| **② Mémoire** | 5 000 `Order` + 5 000 `Customer` + ~100 000 `OrderLine` matérialisés en entités complètes, **tous suivis par le change tracker avec snapshot des valeurs d'origine** (≈ allocation doublée). Ordre de grandeur : **~60 Mo** pour afficher deux colonnes. Promotion gen1/gen2 quasi certaine. | 5 000 `record` à 3 champs ≈ **400 Ko**. Une projection vers un type non-entité **n'est jamais trackée** : ni snapshot, ni entrée dans le dictionnaire d'identités. **Facteur ~150.** |
| **③ SQL** | **10 001 allers-retours.** Chaque requête est triviale (~2 logical reads) — le plan d'exécution est parfait, et c'est pour ça que le problème est invisible côté DBA. Le coût est la **latence cumulée** : à 1 ms de RTT, **10 secondes d'attente pure**. S'ajoute la pression sur le pool de connexions : 10 001 acquisitions/libérations. | **1 aller-retour.** EF traduit `o.Customer.Name` en `JOIN` et `o.Lines.Count` en sous-requête corrélée `(SELECT COUNT(*) ...)`. Bonus non évident : en ne demandant que 3 colonnes, un **index couvrant** devient possible sur `Orders(PlacedAt) INCLUDE (CustomerId)` → le `Key Lookup` disparaît du plan. |

**Le point qui justifie la grille à 3 axes** : ce cas est invisible sur l'axe ③ tel qu'on le mesure habituellement. `logical reads` est excellent, le plan est optimal, `sys.dm_exec_query_stats` ne signale rien. Seul le **comptage des allers-retours** révèle le problème. C'est pourquoi M0 impose le logging des commandes EF.

**Règle à ancrer** : `Include` sert quand on doit **modifier** le graphe. Pour lire, on projette.

## 4.2 — L'explosion cartésienne

### ❌ Avant

```csharp
// Sur les commandes "bulk" du jeu de données : 40 à 80 lignes, 5 à 8 tags
var orders = await db.Orders
    .Include(o => o.Lines)
    .Include(o => o.Tags)
    .Where(o => bulkIds.Contains(o.Id))
    .ToListAsync();
```

### ✅ Après

```csharp
var orders = await db.Orders
    .AsSplitQuery()
    .Include(o => o.Lines)
    .Include(o => o.Tags)
    .Where(o => bulkIds.Contains(o.Id))
    .ToListAsync();
```

### Analyse (1 500 commandes bulk × ~60 lignes × ~6 tags)

| Axe | Avant | Après |
|---|---|---|
| **① CPU .NET** | ~540 000 lignes traversent le `DataReader`. EF doit **dédupliquer** : pour chaque ligne, chercher si l'entité existe déjà dans l'identity map, et ne créer que les nouvelles. ~540 000 lookups pour créer ~100 000 objets. | 3 lectures de 1 500 / 90 000 / 9 000 lignes = ~100 000 lignes, soit exactement le nombre d'objets à créer. Déduplication marginale. |
| **② Mémoire** | Le `DataReader` reçoit **toutes les colonnes de `Order` répétées ~360 fois par commande** (60 lignes × 6 tags). Les chaînes de caractères sont réallouées à chaque ligne avant déduplication → **beaucoup de déchets gen0 à très haut débit**. Pic de working set nettement supérieur au graphe final. | Chaque colonne d'`Order` lue une seule fois. Déchets d'allocation divisés par ~4. |
| **③ SQL** | `JOIN × JOIN` → **~540 000 lignes produites pour ~100 000 objets utiles**, facteur de duplication ≈ **5,4**. Le volume réseau explose (chaque ligne porte toutes les colonnes d'`Order`). Le `Sort` nécessaire au regroupement demande un **memory grant** proportionnel à 540 000 lignes — risque de **spill tempdb** visible en warning dans le plan. | 3 requêtes, **~100 000 lignes au total**. Memory grant ~5× plus faible, spill évité. |

**Le contre-argument, à donner honnêtement** : `AsSplitQuery` remplace 1 aller-retour par N. Sur une base à forte latence avec un petit résultat, c'est **plus lent sur l'axe ③**. Et les requêtes ne sont pas atomiques par défaut : une écriture concurrente entre les deux peut produire un graphe incohérent.
**Critère de décision chiffré** : si (lignes SQL produites / objets utiles) > ~3, split query gagne. En dessous, single query.

**À faire vérifier par les participants** : refaire l'exercice **sans** le filtre sur les commandes bulk. Sur les commandes normales du jeu de données (1 à 8 lignes, 0 à 3 tags), le facteur de duplication tombe sous 3 et `AsSplitQuery` **ne gagne plus** — voire perd, puisqu'il triple les allers-retours. Le seuil n'est pas une opinion, il se mesure.

## 4.3 — Le tracking inutile

### ❌ Avant
```csharp
var top = await db.Products.OrderByDescending(p => p.Price).Take(100).ToListAsync();
return top.Select(p => new ProductDto(p.Id, p.Name));
```

### ✅ Après
```csharp
var top = await db.Products
    .OrderByDescending(p => p.Price)
    .Take(100)
    .Select(p => new ProductDto(p.Id, p.Name))
    .ToListAsync();
```

### Analyse

| Axe | Avant | Après |
|---|---|---|
| **① CPU .NET** | Pour chaque entité : création du snapshot des valeurs d'origine, calcul de la clé, insertion dans l'identity map. Sur des lectures massives, **le tracking représente typiquement 40 à 60 % du temps de matérialisation**. | Projection vers un type non-entité : **jamais trackée**. Le pipeline de matérialisation se réduit à un constructeur. |
| **② Mémoire** | Entité **+ son snapshot** = allocation doublée. Et surtout : les entités trackées sont **maintenues en vie par le `DbContext`** tant qu'il existe. Sur un contexte à durée de vie longue (ou un scope DI mal calibré), c'est une fuite mémoire de fait — les objets sont promus gen2 et ne sont jamais collectés. | 100 DTO à 2 champs, immédiatement éligibles à la collecte gen0. |
| **③ SQL** | `SELECT *` : toutes les colonnes de `Products` remontent, y compris `Description` (nvarchar(max)). L'index sur `Price` ne peut pas couvrir la requête → **`Index Seek` + 100 `Key Lookup`** dans l'index cluster. | `SELECT Id, Name` : l'index `IX_Products_Price INCLUDE (Name)` **couvre** la requête. Les `Key Lookup` disparaissent du plan. Volume réseau divisé par ~30 si `Description` est volumineuse. |

**Le point non évident, et le plus important du module** : projeter n'améliore pas que le client. **Moins de colonnes demandées = plus de chances qu'un index couvre la requête = suppression des Key Lookup.** La projection est une optimisation de l'axe ③ autant que de l'axe ②.

**À connaître** : `AsNoTrackingWithIdentityResolution()` — pas de tracking, mais les références vers la même entité sont résolues en une instance unique. Sur un graphe à fort partage (un produit référencé par 1 000 lignes), on économise 999 matérialisations sur l'axe ②.

**Réglage global recommandé** :
```csharp
options.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
// puis .AsTracking() explicite sur les rares requêtes d'écriture
```
Inverser le défaut : on opte pour le tracking, on ne le subit pas.

### 🛠️ TP 4 (35 min) — `-- m4.1`, `-- m4.2`, `-- m4.3`
Un endpoint fourni répond en 4,2 s. Trois problèmes empilés (N+1, cartésien, tracking).
Objectif : passer sous 200 ms, **en corrigeant un problème à la fois** et en remplissant le tableau 3 axes à chaque étape. On doit voir que les trois corrections ne jouent pas sur les mêmes axes.

---

# M5 — Sargabilité : faire travailler les index

**Module 100 % axe ③.** C'est le module au plus fort levier de la journée, et celui qui répond le plus directement au réflexe « je passe à Dapper ».

## 5.1 — Le prédicat non traduisible

### ❌ Avant

```csharp
var results = (await db.Orders.ToListAsync())      // toute la table
    .Where(o => IsHighValue(o))
    .ToList();
```

(EF Core 3.0+ lève une exception si on met `IsHighValue` directement dans le `Where` côté serveur. La « correction » ci-dessus est celle que font beaucoup d'équipes, et elle est pire.)

### ✅ Après

```csharp
public static Expression<Func<Order, bool>> HighValue =>
    o => o.Lines.Sum(l => l.Quantity * l.UnitPrice) > 10_000m;

var results = await db.Orders.Where(HighValue).ToListAsync();
```

### Analyse

| Axe | Avant | Après |
|---|---|---|
| **① CPU .NET** | 800 000 entités matérialisées, puis 800 000 évaluations du prédicat côté client pour en garder 300. | 300 entités matérialisées. Le prédicat n'est jamais exécuté en .NET — il est compilé en SQL. |
| **② Mémoire** | ~320 Mo d'entités allouées pour un résultat de 300 objets. Ratio de gaspillage : **2 600 pour 1**. Pic gen2 garanti. | ~60 Ko. |
| **③ SQL** | `SELECT * FROM Orders` — table scan complet, ~180 000 logical reads, 1,4 Go réseau. | `WHERE (SELECT SUM(...) FROM OrderLines WHERE OrderId = o.Id) > 10000`. Le filtrage se fait là où sont les données. **Mais attention : ce n'est pas encore optimal** — la sous-requête agrégée est évaluée par ligne, sans index utilisable sur le résultat. |

**Le palier suivant, à montrer** — colonne calculée persistée et indexée :

```csharp
modelBuilder.Entity<Order>()
    .Property(o => o.TotalAmount)
    .HasComputedColumnSql("...", stored: true);
// + index sur TotalAmount
```

Axe ③ : on passe d'une **sous-requête agrégée par ligne** (~200 000 logical reads) à un **`Index Seek` sur un range** (~30 logical reads). Facteur ~6 000.
Le coût déplacé : l'écriture devient légèrement plus chère, et le calcul est figé dans le schéma. Arbitrage lecture/écriture à assumer.

## 5.2 — La sargabilité : ce que LINQ cache

### ❌ Avant

```csharp
var q1 = db.Orders.Where(o => o.PlacedAt.Year == 2025);
var q2 = db.Customers.Where(c => c.Name.ToUpper() == input.ToUpper());
var q3 = db.Products.Where(p => p.Sku.Substring(0, 3) == "ABC");
```

SQL produit :
```sql
WHERE DATEPART(year, PlacedAt) = 2025
WHERE UPPER(Name) = @p
WHERE SUBSTRING(Sku, 1, 3) = 'ABC'
```

### ✅ Après

```csharp
var start = new DateTime(2025, 1, 1); var end = start.AddYears(1);
var q1 = db.Orders.Where(o => o.PlacedAt >= start && o.PlacedAt < end);

var q2 = db.Customers.Where(c => c.Name == input);     // collation CI de la base

var q3 = db.Products.Where(p => p.Sku.StartsWith("ABC"));   // LIKE 'ABC%'
```

### Analyse

| Axe | Avant | Après |
|---|---|---|
| **① CPU .NET** | **Identique.** Le code C# est de complexité équivalente, la traduction réussit dans les deux cas. | Identique. |
| **② Mémoire** | **Identique côté client** (si le résultat final est le même). Rien à gagner sur cet axe. | Identique. |
| **③ SQL** | Une **fonction appliquée sur la colonne** rend le prédicat *non-sargable* : l'optimiseur devrait appliquer la fonction à chaque entrée d'index pour comparer, donc il ne peut pas l'utiliser. Résultat systématique : **`Index Scan`** sur l'intégralité de la table. Effet secondaire aggravant : l'optimiseur ne sait pas estimer la cardinalité derrière une fonction, il applique une **estimation forfaitaire** → memory grant mal dimensionné → risque de spill tempdb sur les opérateurs en aval. **~4 200 logical reads**, CPU time serveur élevé. | Colonne nue à gauche de l'opérateur → **`Index Seek`** avec bornes de range. Le moteur se positionne et lit une plage contiguë. Les statistiques de l'index donnent une estimation correcte → memory grant juste → pas de spill. **~8 logical reads. Facteur ~500.** |

**Détail par cas** :
- **q1** : intervalle semi-ouvert `>= start && < end`. Range seek. Gère aussi correctement les fractions de seconde, contrairement à un `<= 31/12`.
- **q2** : sur SQL Server, la collation par défaut est *case-insensitive* — `UPPER()` est purement redondant **et** destructeur de performance. Si la collation est CS, la bonne réponse n'est **pas** `ToUpper()` mais une colonne persistée normalisée et indexée.
- **q3** : `LIKE 'ABC%'` **ancré à gauche** est sargable — le moteur borne la recherche. `Contains` (`LIKE '%ABC%'`) ne l'est pas, et c'est irrémédiable côté LINQ : la réponse est un index full-text, pas une réécriture de requête.

> **Le message central de la journée, à poser ici** : sur cet exemple, les axes ① et ② sont strictement inchangés, et l'axe ③ varie d'un facteur 500. LINQ traduit fidèlement ce qu'on écrit. **Un LINQ lent est presque toujours un SQL lent qu'on aurait écrit pareil à la main.** Passer à Dapper avec `WHERE YEAR(PlacedAt) = 2025` ne change strictement rien.
>
> Savoir lire un plan d'exécution est une compétence LINQ.

### 🛠️ TP 5 (30 min) — `-- m5.2`, puis `sql/statistics.sql` dans SSMS
6 requêtes fournies avec leur plan réel capturé.
Pour chacune : repérer le scan, nommer la cause, réécrire, confirmer le passage en seek sur le nouveau plan.
Livrable : tableau `logical reads` / `CPU time` / opérateur du plan, avant et après. **Les colonnes ① et ② doivent rester vides** — c'est l'enseignement de l'exercice.

---

# M6 — Plan cache, paramétrage, Contains

Module à dominante ③, mais avec une nuance : ce qu'on optimise ici est le **CPU du serveur SQL**, pas le temps d'une requête isolée. Les dégâts sont collectifs et invisibles en test.

## 6.1 — Constantes inline vs paramètres

### ❌ Avant
```csharp
var count = await db.Orders.CountAsync(o => o.Status == (OrderStatus)5);
//                                                       ^ constante littérale
```

### ✅ Après
```csharp
var s = status;                                    // variable capturée
var count = await db.Orders.CountAsync(o => o.Status == s);
```

### Analyse

| Axe | Avant | Après |
|---|---|---|
| **① CPU .NET** | EF doit calculer une clé de cache de requête différente pour chaque valeur → **retraduction complète de l'arbre d'expression en SQL** à chaque valeur distincte. Le pipeline EF entier est ré-exécuté. | Une seule traduction, mise en cache, réutilisée. La valeur ne fait que changer la valeur du paramètre. |
| **② Mémoire** | Chaque traduction alloue tout l'arbre intermédiaire du pipeline EF (visiteurs, nœuds SQL). Sur une boucle, taux d'allocation important et inutile. | Allocation limitée au paramètre. |
| **③ SQL** | **L'axe critique.** Chaque valeur produit un texte de requête différent → **un plan d'exécution compilé et mis en cache par valeur**. Conséquences en cascade : (1) CPU serveur consommé en compilations répétées — la compilation d'un plan coûte souvent plus cher que son exécution ; (2) **pollution du plan cache** par des milliers d'entrées à usage unique ; (3) **éviction des plans utiles** des requêtes légitimes, qui doivent à leur tour recompiler. C'est un problème de **charge serveur globale**, invisible en test unitaire, dévastateur en production. | Texte de requête identique quelle que soit la valeur → **un plan unique, compilé une fois, réutilisé**. Zéro recompilation. Vérifiable sur `sys.dm_exec_cached_plans` : 1 entrée avec `usecounts` élevé au lieu de N entrées à `usecounts = 1`. |

**Le revers, à enseigner aussi — le *parameter sniffing*** : un plan compilé pour une valeur très sélective peut être catastrophique pour une valeur peu sélective (le moteur réutilise un plan à `Index Seek + Key Lookup` là où un `Scan` serait meilleur, ou l'inverse). Quand ça arrive, l'inline de la constante devient la bonne réponse :

```csharp
db.Orders.Where(o => o.Status == EF.Constant(s));      // EF Core 8+
```

Les deux outils existent. Le choix dépend de la **distribution des données**, pas d'une règle générale.

## 6.2 — `Contains` sur une grande liste

### ❌ Avant (comportement EF Core ≤ 7)
```csharp
var ids = GetIds();      // 3 000 identifiants
var orders = await db.Orders.Where(o => ids.Contains(o.Id)).ToListAsync();
```
→ `WHERE Id IN (@p0, @p1, ..., @p2999)`

### ✅ Après (a) — EF Core 8+, natif
```csharp
var orders = await db.Orders.Where(o => ids.Contains(o.Id)).ToListAsync();
```
→ `WHERE Id IN (SELECT value FROM OPENJSON(@ids))`

### ✅ Après (b) — au-delà de quelques milliers
```csharp
var results = new List<Order>();
foreach (var batch in ids.Chunk(1000))
    results.AddRange(await db.Orders.Where(o => batch.Contains(o.Id)).ToListAsync());
```

### Analyse

| Axe | Avant | Après (a) |
|---|---|---|
| **① CPU .NET** | Construction de 3 000 objets `SqlParameter`, plus la sérialisation du texte de requête géant. | 1 paramètre, sérialisation JSON de la collection. Beaucoup moins de travail côté ADO.NET. |
| **② Mémoire** | 3 000 `SqlParameter` + un `string` de requête de plusieurs dizaines de Ko, **alloué sur le LOH s'il dépasse 85 Ko**. | 1 paramètre, 1 chaîne JSON compacte. |
| **③ SQL** | Trois problèmes cumulés : (1) **limite de 2 100 paramètres** SQL Server dépassée → exception à l'exécution ; (2) chaque taille de liste distincte produit un texte différent → **un plan par cardinalité**, même pollution du cache qu'en 6.1 ; (3) la **compilation** d'un `IN` à 3 000 termes coûte plus cher que l'exécution — l'optimiseur doit traiter 3 000 prédicats disjoints. | **Texte de requête constant** quelle que soit la taille → **un seul plan réutilisé**. Plus de limite de paramètres. L'optimiseur traite `OPENJSON` comme une table et peut choisir un `Hash Join` ou un `Nested Loops` selon la cardinalité estimée. ⚠️ Contrepartie : l'estimation de cardinalité d'`OPENJSON` est **forfaitaire** (le moteur ne connaît pas le contenu du JSON) — sur de très grands volumes, ça peut mener à un mauvais choix de jointure. C'est précisément le seuil où la TVP devient la bonne réponse (M8). |

## 6.3 — Compiled queries

### ❌ Avant
```csharp
public Task<Customer?> GetAsync(int id) => db.Customers.FirstOrDefaultAsync(c => c.Id == id);
```

### ✅ Après
```csharp
private static readonly Func<AppDbContext, int, Task<Customer?>> _getById =
    EF.CompileAsyncQuery((AppDbContext db, int id) => db.Customers.FirstOrDefault(c => c.Id == id));

public Task<Customer?> GetAsync(int id) => _getById(db, id);
```

### Analyse

| Axe | Avant | Après |
|---|---|---|
| **① CPU .NET** | À chaque appel : parcours de l'arbre d'expression, calcul de la clé de cache, lookup dans le cache de requêtes. Le cache évite la *retraduction SQL*, pas ce travail préalable. **~20 à 30 % du temps total** sur une requête triviale. | Pipeline de traduction exécuté **une seule fois**, au premier appel. Ensuite : invocation directe d'un délégué. |
| **② Mémoire** | Allocation de la clé de cache et des structures de parcours à chaque appel. Faible en valeur unitaire, significatif à 50 000 req/s en taux d'allocation. | Quasi nulle par appel. |
| **③ SQL** | **Strictement identique.** Le SQL généré, le plan, les logical reads sont les mêmes. **Aucun gain sur l'axe ③.** | Identique. |

**Cadrage honnête, et c'est le point du module** : le gain est de quelques dizaines de **microsecondes** par requête, exclusivement sur les axes ① et ②. C'est pertinent sur un chemin appelé des dizaines de milliers de fois par seconde. Sur un CRUD d'admin, c'est de la complexité gratuite. À réserver aux 3–5 requêtes les plus chaudes, **identifiées par profiling**, jamais par principe.

### 🛠️ TP 6 (25 min) — `sql/statistics.sql` section 2 (plan cache)
1. Observer `sys.dm_exec_cached_plans` pendant l'exécution des deux versions du 6.1 : compter les entrées et lire `usecounts`.
2. `Contains` sur 100 / 1 000 / 5 000 ids : tracer temps d'exécution **et** nombre de plans créés.
3. Compiled query : benchmark ①/② et **vérifier que le SQL est identique** (axe ③ inchangé).

---

# M7 — Pagination, agrégations, opérations en masse

## 7.1 — Pagination par offset

### ❌ Avant
```csharp
var page = await db.Orders
    .OrderByDescending(o => o.PlacedAt)
    .Skip(pageIndex * 50).Take(50)
    .ToListAsync();
```

### ✅ Après — pagination par curseur (keyset)
```csharp
var page = await db.Orders
    .Where(o => lastSeen == null
             || o.PlacedAt < lastSeen.PlacedAt
             || (o.PlacedAt == lastSeen.PlacedAt && o.Id < lastSeen.Id))
    .OrderByDescending(o => o.PlacedAt).ThenByDescending(o => o.Id)
    .Take(50)
    .ToListAsync();
```

### Analyse (page 2 000, index composite `(PlacedAt DESC, Id DESC)`)

| Axe | Avant | Après |
|---|---|---|
| **① CPU .NET** | Identique — 50 entités matérialisées dans les deux cas. | Identique. |
| **② Mémoire** | Identique côté client. | Identique. |
| **③ SQL** | `OFFSET 100000 ROWS FETCH NEXT 50` : le moteur doit **produire puis jeter les 100 000 premières lignes** avant de renvoyer les 50 suivantes. Coût **linéaire en profondeur de page** : page 1 ≈ 5 logical reads, page 2 000 ≈ **10 000 logical reads**. Le CPU time serveur croît de même. Défaut fonctionnel additionnel : une insertion concurrente **décale la fenêtre** → doublons ou éléments manqués. | Le `WHERE` devient un **`Index Seek` direct** : le moteur se positionne à l'endroit exact dans l'index et lit 50 lignes contiguës. **Coût constant : ~5 logical reads quelle que soit la page.** La page 2 000 coûte exactement le prix de la page 1. Le curseur est **stable** face aux insertions concurrentes. |

**Le compromis à énoncer** : plus d'accès aléatoire (« aller à la page 47 » devient impossible), on ne peut qu'avancer et reculer. Acceptable pour un scroll infini ou une API ; pas pour un tableau d'admin à pagination numérotée.
**Critère de décision** : la profondeur de pagination réellement atteinte par les utilisateurs. Si 95 % du trafic reste sur les 3 premières pages, l'offset est parfaitement acceptable et keyset est de la complexité gratuite.

## 7.2 — `GroupBy` non traduit

### ❌ Avant
```csharp
var stats = await db.Orders
    .GroupBy(o => o.CustomerId)
    .Select(g => new { g.Key, Orders = g.ToList() })      // matérialise les éléments
    .ToListAsync();
```

### ✅ Après (a) — on veut des agrégats
```csharp
var stats = await db.Orders
    .GroupBy(o => o.CustomerId)
    .Select(g => new CustomerStats(g.Key, g.Count(), g.Sum(o => o.Total), g.Max(o => o.PlacedAt)))
    .ToListAsync();
```

### ✅ Après (b) — on veut vraiment les groupes
```csharp
var stats = (await db.Orders
        .Where(...)
        .Select(o => new { o.CustomerId, o.Id, o.Total })   // colonnes strictement nécessaires
        .ToListAsync())
    .GroupBy(x => x.CustomerId)
    .ToList();
```

### Analyse

| Axe | Avant | Après (a) |
|---|---|---|
| **① CPU .NET** | EF rapatrie **toutes les lignes** et effectue le regroupement **en mémoire** — on croit avoir agrégé côté base, on a fait un `SELECT *` suivi d'un `GroupBy` client sur 800 000 éléments. | Aucun regroupement client. 50 000 `record` construits directement. |
| **② Mémoire** | 800 000 entités + 50 000 `IGrouping` + leurs listes sous-jacentes contenant 800 000 références. **~350 Mo.** | 50 000 `record` à 4 champs ≈ **3 Mo**. **Facteur ~100.** |
| **③ SQL** | `SELECT * FROM Orders ORDER BY CustomerId` — table scan complet, ~180 000 logical reads, 1,4 Go réseau, plus un `Sort` avec memory grant important et **risque de spill tempdb**. | `SELECT CustomerId, COUNT(*), SUM(Total), MAX(PlacedAt) FROM Orders GROUP BY CustomerId`. Le moteur agrège avec un `Stream Aggregate` sur index ou un `Hash Aggregate`, **avec parallélisation possible**. **1 ligne par client au lieu d'1 par commande : 50 000 lignes contre 800 000.** |

**En (b), ce qu'on gagne quand même** : on ne prétend pas. Le regroupement client est **assumé et visible dans le code**, et la projection préalable divise le volume réseau et l'allocation par ~10 en ne remontant que 3 colonnes. La différence entre (b) et « avant » n'est pas l'endroit du regroupement, c'est **la quantité de données déplacée pour le faire**.

## 7.3 — Mise à jour en masse

### ❌ Avant
```csharp
var stale = await db.Orders.Where(o => o.PlacedAt < cutoff).ToListAsync();
foreach (var o in stale) o.Status = OrderStatus.Archived;
await db.SaveChangesAsync();
```

### ✅ Après (EF Core 7+)
```csharp
await db.Orders
    .Where(o => o.PlacedAt < cutoff)
    .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, OrderStatus.Archived));
```

### Analyse (200 000 commandes)

| Axe | Avant | Après |
|---|---|---|
| **① CPU .NET** | Matérialisation de 200 000 entités, snapshot de tracking pour chacune, puis **`DetectChanges` qui compare chaque propriété de chaque entité** à son snapshot au moment du `SaveChanges`. Puis génération de 200 000 instructions `UPDATE`. | Une seule expression traduite. **Zéro entité matérialisée, zéro snapshot, zéro `DetectChanges`.** |
| **② Mémoire** | 200 000 entités + 200 000 snapshots ≈ **80 Mo**, tous maintenus vivants par le `DbContext` jusqu'au `SaveChanges` — donc **promus en gen2**. Le pic de working set est le vrai coût, pas le CPU. | **Allocation constante.** Indépendante du nombre de lignes affectées. |
| **③ SQL** | 1 `SELECT` de 200 000 lignes, **puis 200 000 instructions `UPDATE`** (batchées par lots de ~42 par défaut, soit ~4 800 allers-retours). Chaque batch ouvre sa propre transaction implicite. Volume de log transactionnel élevé, verrous tenus longtemps → **contention sur la table pendant toute la durée**. | **1 seule instruction** : `UPDATE Orders SET Status = @p WHERE PlacedAt < @c`. Un aller-retour. Le moteur applique la mise à jour en une passe sur l'index. Verrouillage plus court (mais potentiellement une escalade en verrou de table à surveiller). Passage typique de **plusieurs minutes à quelques centaines de millisecondes**. |

**Ce qu'on perd, et qu'il faut dire** : `ExecuteUpdate` / `ExecuteDelete` court-circuitent le change tracker — donc pas d'interceptors d'entité, pas d'override de `SaveChanges`, **pas de champs d'audit automatiques**, et le contexte en mémoire devient désynchronisé de la base. C'est un outil de traitement batch, pas un remplacement de `SaveChanges`.

### 🛠️ TP 7 (30 min) — `-- m7.1`
Migrer un job de purge nocturne (22 min actuellement) vers `ExecuteDelete` + `Chunk`, en conservant les contraintes d'audit.
Mesurer : durée (③), pic de working set (②), volume de log transactionnel généré (③). Discuter de ce qui doit rester en `SaveChanges`.

---

# M8 — La frontière : LINQ, Dapper, procédure stockée

Atelier de clôture. Répond directement au point de départ de la formation.

## Grille de décision par axe dominant

| Situation | Outil | Axe où il gagne | Pourquoi |
|---|---|---|---|
| CRUD, requêtes de domaine, écritures avec logique métier | **EF / LINQ** | — | Typage, refactoring sûr, tracking, migrations |
| Lecture massive, projection plate, chemin très chaud | **Dapper** | ① et ② | Pas de pipeline de traduction, matérialisation directe, pas de tracker. Le SQL est le même — **le gain est côté .NET, pas côté base** |
| Requête analytique (window functions, CTE récursives, PIVOT, hints) | **SQL brut / vue** | ③ | LINQ ne traduit pas ces constructions, ou mal |
| Traitement transactionnel lourd, allers-retours multiples | **Procédure stockée** | ③ | Le calcul reste près des données, un seul aller-retour |
| `IN` sur > 5 000 valeurs | **TVP** | ③ | Cardinalité estimée correcte, contrairement à `OPENJSON` |
| Insertion massive (> 10 k lignes) | **`SqlBulkCopy`** | ③ et ② | Chemin d'écriture minimalement journalisé. EF n'est pas conçu pour ça, quel que soit le tuning |

**Le point que je veux qu'ils retiennent de ce tableau** : Dapper gagne sur les axes ① et ②, **presque jamais sur l'axe ③**. Le SQL généré par EF, quand la requête est bien écrite, est équivalent à celui qu'on écrirait à la main. Si le problème est un `Index Scan`, changer d'ORM ne le corrigera pas.

## La voie intermédiaire trop peu connue

Avant de sortir d'EF, il y a un palier : **du SQL brut dans EF**, avec la composabilité et la matérialisation conservées.

```csharp
var orders = await db.Orders
    .FromSql($"SELECT * FROM Orders WITH (INDEX(IX_Orders_Status)) WHERE Status = {status}")
    .Where(o => o.PlacedAt > cutoff)          // composable, ajouté par EF
    .AsNoTracking()
    .ToListAsync();
```

Pour un résultat non-entité (EF Core 8+) :

```csharp
var rows = await db.Database
    .SqlQuery<RevenueByMonth>($"SELECT ... FROM ... GROUP BY ...")
    .ToListAsync();
```

**Ce qu'on garde** : contrôle total du SQL là où il compte (axe ③), interpolation **paramétrée automatiquement** — `{status}` devient un `SqlParameter`, pas une concaténation, donc pas d'injection et un plan réutilisé (M6.1) — matérialisation EF, même `DbContext`, même transaction. Pas besoin d'introduire une seconde couche d'accès aux données pour trois requêtes.

## Question à faire trancher au groupe

> « Sur les cas où vous êtes passés à Dapper ou à une procédure stockée : combien étaient des problèmes d'**axe ③** (SQL réellement mal fichu — M5, M7) et combien étaient des problèmes d'**usage de LINQ** (M1, M4, M6) que vous auriez pu corriger en restant sur EF ? »

C'est le vrai enseignement de la journée. Une grande partie des « LINQ est lent » sont des `IEnumerable` mal typés, des `Include` non projetés et des prédicats non-sargables — trois choses qui ne se corrigent pas en changeant d'ORM.

---

## Ce que les participants repartent avec

- Le repo `LinqPerfLab` : base de données générée par script T-SQL déterministe, `DbContext` à deux configurations (Naive / Strict), scénarios avant/après exécutables, benchmarks BenchmarkDotNet, scripts d'instrumentation SQL
- **La checklist de revue de code LINQ en 12 points, structurée par axe**
- La grille de décision LINQ / Dapper / SP
- Le fichier de configuration EF « strict » (warnings en exception, NoTracking par défaut)
- Le tableau des seuils de décision chiffrés : facteur de duplication > 3 → split query ; > 5 % `time-in-gc` → problème d'allocation ; ratio lignes lues / lignes utiles > 100 → problème de sargabilité

## Prolongements (hors périmètre 1 journée)

- Modélisation et stratégie d'indexation (index couvrants, `INCLUDE`, index filtrés)
- CQRS avec modèle de lecture dédié
- `System.Linq.Expressions` : prédicats dynamiques, specification pattern
- `IAsyncEnumerable` et streaming de gros résultats — le seul vrai moyen de borner l'axe ② sur une lecture massive
