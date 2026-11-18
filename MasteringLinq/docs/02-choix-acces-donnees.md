# Choisir son accès aux données : LINQ, EF Core, Dapper, SQL pur, procédures stockées

Synthèse du workshop `MasteringLinq` (SQL Server 2022, 38 M de lignes, .NET 11, EF Core 11, Dapper 2.1.89).
Les chiffres viennent des benchmarks de ce dépôt (`ComplexQuery.cs`, `ComplexQuery2.cs`) ; ce qui n'a pas été mesuré est signalé comme tel.

## Sommaire

1. [Faut-il se passer de LINQ ?](#1-faut-il-se-passer-de-linq-)
2. [ORMs et micro-ORMs](#2-orms-et-micro-orms)
3. [`EF.CompileAsyncQuery`](#3-efcompileasyncquery)
4. [Benchmark 1 : requête complexe, EF vs Dapper](#4-benchmark-1--requête-complexe-ef-vs-dapper)
5. [Benchmark 2 : requête très lourde, 4 approches](#5-benchmark-2--requête-très-lourde-4-approches)
6. [Tableau de décision : 10 cas](#6-tableau-de-décision--10-cas)
7. [`AsAsyncEnumerable()` : streaming des résultats](#7-asasyncenumerable--streaming-des-résultats)
8. [Modèles compilés (démarrage à froid)](#8-modèles-compilés-démarrage-à-froid)
9. [Limites de ces mesures](#9-limites-de-ces-mesures)

---

## 1. Faut-il se passer de LINQ ?

**Non, pas globalement.** Il faut distinguer deux LINQ très différents.

### LINQ to Objects (`IEnumerable<T>`, en mémoire)

Il coûte des allocations (enumerators, délégués, closures, buffers intermédiaires). Il devient gênant dans :

- les **hot paths** : boucles appelées des millions de fois, traitement à fort débit ;
- le code **sensible aux allocations** : `Span<T>`, parsing, sérialisation, jeux ;
- les **chaînes longues** sur de grosses collections (`.Where().Select().OrderBy().ToList()`) ;
- les **closures capturantes** dans un hot path (allocation à chaque appel).

Dans ces cas, une boucle `for` / `foreach` (idéalement sur un `Span<T>` ou un tableau) est souvent 2 à 10 fois plus rapide, avec zéro allocation. Ailleurs, la lisibilité de LINQ l'emporte. **Mesurez d'abord** (BenchmarkDotNet, profiler) : les versions récentes de .NET ont beaucoup optimisé LINQ.

### LINQ to Entities (EF Core, `IQueryable<T>`)

LINQ n'est pas exécuté en C#, il est **traduit en SQL** : son coût propre est marginal. Ce qui ralentit une requête EF, ce sont d'autres choses :

| Problème | Solution |
|---|---|
| Suivi des changements inutile | `AsNoTracking()` en lecture |
| Chargement de l'entité entière | Projection `.Select(x => new Dto {...})` |
| N+1 requêtes | `Include`, ou projection |
| Explosion cartésienne (plusieurs `Include`) | `AsSplitQuery()` |
| Coût de traitement de l'expression à chaque appel | `EF.CompileAsyncQuery` |
| Updates / deletes en masse chargés en mémoire | `ExecuteUpdate` / `ExecuteDelete` |
| Évaluation côté client accidentelle | Vérifier le SQL (`ToQueryString()`, logs) |

Piège classique : repasser en LINQ to Objects sans le vouloir (`.ToList()` trop tôt, `AsEnumerable()` avant un `Where`).

### Peut-on tout faire en LINQ avec EF Core ?

Presque tout, mais pas tout. Les CTE récursives, window functions, hints et `MERGE` sont difficiles ou impossibles à exprimer. Dans EF Core, on peut basculer sur `FromSql`, `Database.SqlQuery<T>`, des vues ou des procédures mappées, pour les quelques requêtes concernées.

---

## 2. ORMs et micro-ORMs

| Outil | Profil |
|---|---|
| **EF Core** | ORM complet : suivi des changements, migrations, LINQ traduit en SQL. |
| **Dapper** | Micro-ORM de référence. SQL écrit à la main, mapping très rapide, proche d'ADO.NET. |
| **Dapper AOT** | Version générée à la compilation : compatible AOT, moins de réflexion. |
| **linq2db** | LINQ vers SQL proche du SQL, sans suivi des changements. Bulk copy, CTE, merge. |
| **RepoDB** | Hybride micro-ORM, avec bulk operations et cache. |
| **ADO.NET brut** | Performance maximale, mais beaucoup de code. |
| **Marten** | PostgreSQL en base documentaire / event sourcing (pas un ORM relationnel). |
| **SqlKata** | Query builder (pas un ORM). |

En lecture avec `AsNoTracking` et projection, EF Core est proche de Dapper pour des requêtes simples. L'écart se creuse sur les gros volumes de lignes, le suivi des changements, les insertions en masse et le démarrage à froid (construction du modèle EF).

---

## 3. `EF.CompileAsyncQuery`

Une requête LINQ est un arbre d'expression. À chaque exécution, EF le parcourt pour extraire les paramètres et calculer une clé de cache, puis retrouve le SQL déjà traduit. `EF.CompileAsyncQuery` (et `EF.CompileQuery` en synchrone) fait ce travail **une seule fois** et renvoie un délégué.

```csharp
public static class Requetes
{
    public static readonly Func<StarWarsContext, int, Task<Good?>> GoodById =
        EF.CompileAsyncQuery((StarWarsContext c, int id) =>
            c.Goods.AsNoTracking().FirstOrDefault(g => g.Id == id));
}

var good = await Requetes.GoodById(db, 42);
```

### Ce que ça fait gagner

EF a un cache interne : après le premier appel, il **ne retraduit pas** en SQL. La requête compilée n'évite que le surcoût CPU par appel (parcours de l'arbre, clé de cache), de l'ordre de dizaines à centaines de microsecondes.

- Sur une requête de 400 ms, le gain est invisible.
- Sur une requête de 0,5 ms appelée des milliers de fois par seconde (lookup par clé), il est visible.

### Contraintes

- Le délégué doit être **`static readonly`** (le construire à chaque appel annule le bénéfice).
- Premier argument : le `DbContext` ; les suivants : les paramètres. Pas de variables capturées.
- Pas de composition dynamique : le résultat n'est pas un `IQueryable`.
- Retour limité à `Task<T>` (valeur unique) ou `IAsyncEnumerable<T>` (plusieurs lignes, lu avec `await foreach`).
- Les types anonymes en retour sont pénibles : prévoir un record nommé.
- Les paramètres de type collection sont mal supportés.

> L'effet de `EF.CompileAsyncQuery` n'a **pas été mesuré** dans ce dépôt.

---

## 4. Benchmark 1 : requête complexe, EF vs Dapper

Fichier : `MasteringLinq/ComplexQuery.cs`. Lancement : `dotnet run -c Release -- --complex`.

**Requête :** pour chaque région, le top 3 des marchandises hors contrebande par chiffre d'affaires de ventes en 2025, avec le nombre d'échanges, le nombre de sociétés distinctes et le prix moyen. Cinq jointures (`Trades → Markets → Planets → StarSystems → Sectors`, plus `Goods`), `GROUP BY` sur deux clés, `COUNT(DISTINCT)`, top-N par groupe.

| | min | médiane | max |
|---|---|---|---|
| EF Core + LINQ (10 itérations) | 381 ms | **401 ms** | 435 ms |
| Dapper (10 itérations) | 370 ms | **381 ms** | 423 ms |

Écart d'environ 5 à 7 % (20 à 30 ms) en régime établi. Les mesures isolées varient beaucoup plus (un premier appel est à 487 ms ou 531 ms contre 393 ms à 446 ms pour Dapper), à cause du cache SQL, du GC et de la première traduction EF.

Le SQL généré par EF est équivalent à celui de Dapper (une seule agrégation). `GroupBy(...).SelectMany(g => g.OrderByDescending().Take(3))` sur un résultat déjà agrégé n'est pas traduisible ; le contournement est un `SelectMany` corrélé sur les régions, que EF traduit en `ROW_NUMBER`.

---

## 5. Benchmark 2 : requête très lourde, 4 approches

Fichier : `MasteringLinq/ComplexQuery2.cs`. Lancement : `dotnet run -c Release -- --complex2` (ajouter `--sql` pour voir le SQL de EF v1).

**Requête :** pour chaque région, le **top 20 des compagnies rentables** (marge = ventes − achats > 0) ayant au moins un vaisseau opérationnel, sur 2023-2025. Pour chaque compagnie : ventes, achats, marge, nb d'échanges, nb de marchés distincts, nb de vaisseaux, capacité de fret, charge en soute, planète du siège, propriétaire et faction. Elle agrège **6 M** de lignes `Trades`, **3 M** de `StarshipCargos` et **2 M** de `Starships`, avec `COUNT(DISTINCT)`, `ROW_NUMBER` et jointures.

Les 4 approches renvoient exactement les mêmes 120 lignes.

| Approche | min | médiane | max | alloc. client |
|---|---|---|---|---|
| EF v1 : LINQ direct (`CROSS APPLY`) | 11 793 ms | **13 915 ms** | 14 904 ms | 0,3 Mo |
| EF v2 : optimisé en 2 phases | 1 156 ms | **1 444 ms** | 1 786 ms | 154,6 Mo |
| SQL pur (ADO.NET) | 1 213 ms | **1 342 ms** | 1 472 ms | 0,07 Mo |
| Dapper | 1 421 ms | **1 663 ms** | 1 704 ms | 0,08 Mo |

### Lecture

- **EF v1 est 10 fois plus lent** : ce n'est pas le surcoût de l'ORM, c'est la forme du SQL. Le `CROSS APPLY` recalcule les trois agrégats pour chaque région. `GroupBy(Region).SelectMany(Take)`, qui devrait donner un `ROW_NUMBER`, lève une exception ici (source avec jointures sur sous-requêtes agrégées).
- **EF v2 rejoint le SQL** :
  - *Phase 1* : classement léger (`Id`, région, marge) de toutes les compagnies rentables, en une seule passe sur `Trades`, sans `COUNT(DISTINCT)`, vaisseaux testés par `EXISTS` ; top 20 par région calculé en mémoire.
  - *Phase 2* : détail des 120 gagnants seulement, par sous-requêtes corrélées qui utilisent les index.
- **Coût de EF v2** : 154,6 Mo alloués côté client (la phase 1 rapatrie plusieurs centaines de milliers de lignes) et environ 60 lignes de code de plus que le SQL.
- **Dapper et SQL pur exécutent le même SQL** : leur écart ici est du bruit. Une mesure antérieure donnait 786 ms et 828 ms ; le SQL Server de la machine varie d'un run à l'autre (EF v1 sature le cache entre deux mesures). Cause non isolée.
- **À retenir** : l'ORM coûte peu sur une requête longue ; la forme du SQL coûte beaucoup. EF v2, SQL pur et Dapper sont dans la même plage ; ne pas en tirer de classement fin.

---

## 6. Tableau de décision : 10 cas

Légende : ✅ bien adapté, 🟡 possible avec réserves, ❌ à éviter. « EF Core optimisé » désigne EF avec `AsNoTracking`, projections, agrégats poussés en SQL et découpage en phases si nécessaire (pas le LINQ direct de EF v1).

| # | Cas | EF Core optimisé | SQL pur | Proc. stockée | Dapper | Pourquoi |
|---|---|---|---|---|---|---|
| 1 | **CRUD classique** (entités, relations) | ✅ | ❌ | ❌ | 🟡 | EF gère le suivi des changements, les relations et les migrations. |
| 2 | **Top-N par groupe, agrégats lourds** (notre `ComplexQuery2`) | ✅ **(mesuré)** | ✅ | ✅ | ✅ | Découpée en 2 phases, la requête EF égale le SQL : 1,4 s contre 1,3 s. Le surcoût est de 155 Mo alloués côté client et de plus de code qu'en SQL. |
| 3 | **Lectures à très fort débit** (lookups par clé, milliers d'appels par seconde) | ✅ *(non mesuré)* | ✅ | ✅ | ✅ | `AsNoTracking`, projection et `EF.CompileAsyncQuery` suppriment l'essentiel du surcoût d'EF. SQL pur et Dapper gardent l'avantage sur les allocations. |
| 4 | **Chargement ou export massif** (millions de lignes) | 🟡 | ✅ | 🟡 | 🟡 | `ExecuteUpdate` / `ExecuteDelete` couvrent les mises à jour de masse. L'insertion en masse passe par `SqlBulkCopy`, donc ADO.NET. |
| 5 | **Logique métier lourde à côté des données** (transaction longue, plusieurs étapes) | ❌ | 🟡 | ✅ | 🟡 | Une procédure s'exécute dans la base, en un seul appel. La logique sort du C#, ce qui la rend plus difficile à tester et à versionner. |
| 6 | **Droits restreints** (l'application n'a que `EXECUTE`) | ❌ | ❌ | ✅ | 🟡 | Seule la procédure évite d'ouvrir les tables. Dapper peut appeler une procédure (`CommandType.StoredProcedure`). |
| 7 | **Rapports en lecture seule**, DTO plats | ✅ | ✅ | ✅ | ✅ | Une projection `Select` vers un record suffit à EF, qui garde la vérification à la compilation. Dapper reste plus simple si le SQL existe déjà. |
| 8 | **Requêtes à tuner finement** (hints, `OPTION (RECOMPILE)`, tables temporaires) | 🟡 | ✅ | ✅ | ✅ | LINQ n'exprime pas ces constructions. EF reste possible via `FromSql` / `SqlQuery<T>` ou un interceptor de commandes. |
| 9 | **95 % de CRUD et quelques requêtes lourdes** | ✅ | 🟡 | 🟡 | ✅ | EF seul peut suffire. Dapper ou `SqlQuery<T>` à côté sert quand le découpage devient pénible. |
| 10 | **Portabilité entre bases** (SQL Server, PostgreSQL, SQLite) | ✅ | ❌ | ❌ | ❌ | EF traduit LINQ pour chaque fournisseur. Les trois autres sont liés à un dialecte SQL. |

### Procédure stockée contre SQL inline

Les performances sont équivalentes une fois le plan en cache. Le vrai critère est organisationnel : sécurité (cas 6), propriété du code par les DBA, déploiement de la logique indépendamment de l'application (cas 5).

---

## 7. `AsAsyncEnumerable()` : streaming des résultats

Fichier : `MasteringLinq/StreamingBenchmark.cs`. Lancement : `dotnet run -c Release -- --stream`.

### À quoi ça sert

`AsAsyncEnumerable()` transforme un `IQueryable<T>` en `IAsyncEnumerable<T>`, consommé avec `await foreach`. Les lignes arrivent **une par une**, au fur et à mesure que `SqlDataReader` les lit. Avec `ToListAsync()`, toutes les lignes sont d'abord matérialisées dans une `List<T>`.

```csharp
await foreach (var t in db.Trades.AsNoTracking()
                          .Where(t => t.TradedAt >= debut)
                          .AsAsyncEnumerable())
{
    await Traiter(t);   // mémoire constante, quel que soit le nombre de lignes
}
```

### Ce qui se passe derrière

**Côté EF**

- **Exécution différée** : appeler `AsAsyncEnumerable()` n'envoie rien. La requête part au premier `MoveNextAsync()`, donc au début du `await foreach`.
- **Même boucle que `ToListAsync()`** : à la première itération, EF ouvre la connexion et lance `ExecuteReaderAsync`. Chaque tour appelle `reader.ReadAsync()` et construit l'objet. `ToListAsync()` fait la même chose en ajoutant chaque objet à une liste.
- **`DisposeAsync`** ferme le lecteur et rend la connexion au pool, y compris après un `break` ou une exception.

**Côté SQL**

- **Le SQL est identique** : même commande, sans curseur serveur (résultat en lecture seule, avant seulement, envoyé en flux TDS).
- **Seule la consommation change** : SQL Server écrit les lignes dans des paquets réseau. Si le client lit lentement, les tampons se remplissent et la requête se met en attente. Dans `sys.dm_exec_requests`, le wait type est **`ASYNC_NETWORK_IO`**.
- **La requête reste active pendant tout le traitement** : session, verrous de lecture selon le niveau d'isolation, *memory grant* et connexion du pool sont tenus. Avec `ToListAsync()`, tout est libéré à la dernière ligne lue.

### Résultats mesurés

Environ 2 M de lignes de `Trades` (2025), `AsNoTracking`, projection vers un record.

| Mesure | `ToListAsync` | `AsAsyncEnumerable` |
|---|---|---|
| Temps total | 3 776 ms | 3 824 ms |
| **Temps au 1er élément** | 3 776 ms | **19 ms** |
| Mémoire allouée au total | 538 Mo | 506 Mo |
| **Mémoire vivante maximale** | **107 Mo** (2 M d'objets) | **≈ 0 Mo** |
| GC de génération 2 | 1 | 0 |

- Le temps total est **identique** : le flux ne va pas plus vite.
- La mémoire **vivante** tombe à presque rien, mais les allocations totales restent à 506 Mo : EF crée un objet par ligne, libéré au fur et à mesure.

**Consommateur lent** (pause de 500 ms toutes les 200 000 lignes, échantillonnage de `sys.dm_exec_requests` toutes les 50 ms depuis une autre connexion) :

| | Durée totale | Requête SQL active | `ASYNC_NETWORK_IO` |
|---|---|---|---|
| `ToListAsync` puis traitement | 8,4 s | ≈ 3,1 s | ≈ 0 s |
| `AsAsyncEnumerable` + traitement | **5,9 s** | ≈ 4,3 s | **≈ 1,4 s** |

SQL Server reste en attente du client pendant 1,4 s : la requête et la connexion restent occupées pendant le traitement. Le flux finit pourtant plus tôt, car le transfert et le traitement se chevauchent.

**Piège du filtre côté client** (`Quantity == 500`) :

| | Temps | Lignes transférées |
|---|---|---|
| `Where` avant `AsAsyncEnumerable()` (traduit en SQL) | **32 ms** | 4 012 |
| Filtre après, en C# | 3 691 ms | **1 994 520** |

**Opérateur bloquant** (temps au premier élément) :

| | 1er élément | Total |
|---|---|---|
| `ORDER BY Id` (index de la clé primaire) | 38 ms | 809 ms |
| `ORDER BY UnitPrice` (tri complet) | 269 ms | 1 579 ms |

Le tri complet repousse le premier élément. L'écart serait plus grand sur un tri qui déborde la mémoire allouée (non mesuré).

### Quand l'utiliser

| Situation | Pourquoi |
|---|---|
| **Export ou ETL de gros volumes** (CSV, fichier, autre base) | Mémoire constante au lieu d'une liste de millions d'objets. |
| **API ASP.NET Core qui renvoie `IAsyncEnumerable<T>`** | `System.Text.Json` sérialise en flux : le client reçoit le début de la réponse tout de suite. |
| **Traitement ligne par ligne léger** (agrégation incrémentale, filtre côté client) | Rien à garder en mémoire. |
| **Producteur / consommateur** avec `Channel<T>` | La lecture alimente un pipeline sans tout charger. |

Lien avec `ComplexQuery2` : la phase 1 d'EF v2 matérialise plusieurs centaines de milliers de lignes (155 Mo alloués). Avec `AsNoTracking().AsAsyncEnumerable()` et un top 20 par région tenu à jour au fil de l'eau (un `PriorityQueue` par région), la mémoire devrait tomber à presque rien. **Non mesuré.**

### Quand c'est contre-productif

| Piège | Pourquoi |
|---|---|
| **Petit résultat** (quelques dizaines de lignes) | Aucun gain mémoire, et chaque ligne passe par un `ReadAsync`. `ToListAsync()` est plus simple. |
| **Filtrer après** : `.AsAsyncEnumerable().Where(...)` | Les opérateurs placés après s'exécutent en C# sur toutes les lignes ramenées (**3 691 ms contre 32 ms** mesurés). `AsAsyncEnumerable()` doit venir en dernier, après tous les `Where`, `Select`, `OrderBy`, `Take`. |
| **Traitement lent à chaque ligne** (appel HTTP, I/O) | La connexion et la requête restent ouvertes pendant tout le traitement (**1,4 s d'attente `ASYNC_NETWORK_IO`** mesurées). Préférer une pagination par clé (`Id > dernierId`, `Take(n)`) avec des connexions courtes. |
| **Autre requête dans la boucle** (N+1) | Sans MARS (`MultipleActiveResultSets=True`), SqlClient lève « There is already an open DataReader ». Le `DbContext` n'est de toute façon pas utilisable en parallèle. |
| **Opérateur bloquant** (`ORDER BY` sans index, `GROUP BY` en hash) | SQL Server produit tout le résultat avant la première ligne : le streaming économise la mémoire client, pas la latence. |
| **Suivi des changements activé** | Chaque entité est gardée par le `ChangeTracker` : la mémoire grossit quand même. Utiliser `AsNoTracking()`. |
| **`break` prématuré sur un gros résultat** | À la fermeture, SqlClient peut devoir vider les lignes restantes. Pour s'arrêter tôt, préférer un `Take(n)` dans la requête. *(non mesuré)* |

À vérifier selon la configuration : EF peut **mettre en mémoire tampon** malgré `AsAsyncEnumerable()` avec une stratégie de réessai (`EnableRetryOnFailure`) ou avec `AsSplitQuery()` sans MARS. *(non testé)*

### Règle pratique

1. **Peu de lignes, ou besoin de plusieurs passes** (compter, trier côté client, revenir en arrière) : `ToListAsync()`.
2. **Gros volume traité une fois, traitement rapide** : `AsNoTracking().AsAsyncEnumerable()`.
3. **Gros volume, traitement lent** : pagination par clé plutôt qu'un flux long.
4. **Toujours** : filtres et projections en LINQ **avant** `AsAsyncEnumerable()`. Les filtres et tris portent sur l'entité, avant le `Select` vers un record (EF ne traduit pas un `Where` ou un `OrderBy` écrit sur un record déjà projeté).

---

## 8. Modèles compilés (démarrage à froid)

Fichiers : `MasteringLinq/ColdStartBenchmark.cs`, `MasteringLinq/CompiledModels/` (généré), `MasteringLinq/Data/DesignTimeFactory.cs`.
Lancement : `dotnet run -c Release -- --coldstart`.

### Principe

Au premier usage d'un `DbContext` dans le processus (première requête, premier `Add`), EF construit son modèle : découverte des entités, conventions, relations, `OnModelCreating`. Créer l'instance seule ne le déclenche pas. Un **modèle compilé** génère ce résultat en code C# à la compilation, et EF le charge au lieu de le reconstruire. La documentation d'EF Core le présente comme utile pour les modèles de plusieurs centaines d'entités ; on le mesure ici sur un modèle de **15 entités**.

```bash
dotnet ef dbcontext optimize --output-dir CompiledModels --namespace MasteringLinq.CompiledModels --context StarWarsContext
```
```csharp
optionsBuilder.UseModel(MasteringLinq.CompiledModels.StarWarsContextModel.Instance);
```

Le modèle compilé ne change **ni la traduction LINQ → SQL, ni l'exécution, ni la matérialisation** : il n'agit que sur le démarrage.

### Résultats mesurés

Chaque mesure tourne dans un **processus séparé** (le modèle n'est construit qu'une fois par processus) : 10 processus par mode, en alternance, plus un échauffement ignoré par mode. Médianes :

| Phase | modèle normal | modèle compilé | écart |
|---|---|---|---|
| **Construction / chargement du modèle** | **404,6 ms** | **173,3 ms** | **−231 ms** |
| Ouverture de la connexion SQL | 86,3 ms | 77,5 ms | −9 ms |
| 1re requête (traduction + exécution) | 160,0 ms | 178,3 ms | +18 ms |
| 2e requête (plan en cache) | 4,2 ms | 4,0 ms | ≈ 0 |
| Processus complet (mur, hors lancement de `dotnet`) | 703,6 ms | 478,6 ms | **−225 ms** |

Plage du temps « modèle » : normal 397–471 ms, compilé 166–188 ms.

### Lecture

- **Gain réel, même sur un petit modèle** : environ 230 ms économisés au premier appel, soit un tiers du démarrage. C'est plus que ce que j'avais anticipé pour 15 entités.
- **173 ms restent incompressibles** : initialisation du conteneur de services interne d'EF et JIT, identiques dans les deux modes.
- **+18 ms sur la 1re requête** avec le modèle compilé : cause non identifiée. Une partie du travail d'initialisation passe peut-être de la phase « modèle » à la première requête. Le bilan global reste en faveur du modèle compilé (−225 ms sur le processus entier).
- **Aucun effet sur le régime établi** : la 2e requête est identique (4 ms). Ce n'est donc **pas** l'explication des 20 à 30 ms d'écart entre EF et Dapper des sections 4 et 5.
- **Le premier appel plus lent** observé en section 4 (531 ms contre 401 ms en médiane) vient en partie de cette construction du modèle, mais ce benchmark ne l'a pas mesuré dans ce contexte précis.

### Quand l'utiliser

| Situation | Intérêt |
|---|---|
| Fonction serverless, conteneur qui démarre souvent, outil en ligne de commande | Fort : le coût du premier appel est payé à chaque démarrage. |
| Service qui tourne des jours et démarre rarement | Faible : 230 ms une seule fois. |
| Modèle de centaines d'entités | Gain attendu plus grand *(non mesuré ici)*. |
| Tests d'intégration lancés en nombreux processus | Réduit le temps cumulé. |

### Pièges rencontrés

- **Découverte automatique** : `dotnet ef dbcontext optimize` génère un fichier `StarWarsContextAssemblyAttributes.cs` contenant `[assembly: DbContextModel(typeof(StarWarsContext), typeof(StarWarsContextModel))]`. Avec ce fichier, **EF utilise le modèle compilé tout seul, sans `UseModel`**. Ma première mesure comparait donc deux fois le modèle compilé (181 ms contre 178 ms, sans écart). Pour avoir un vrai « mode normal », ce fichier est exclu de la compilation dans `MasteringLinq.csproj` (`<Compile Remove="CompiledModels/StarWarsContextAssemblyAttributes.cs" />`). En production, c'est l'inverse qu'on veut : le garder pour activer le modèle compilé partout.
- **Le modèle compilé doit être régénéré** à chaque modification d'entité ou de configuration (sinon le modèle chargé ne correspond plus au code). Il est figé à la compilation.
- **Certaines fonctionnalités ne sont pas supportées** par les modèles compilés (par exemple les filtres globaux de requête, selon la version) : à vérifier dans la documentation d'EF Core avant adoption. *(non testé ici)*
- **Fabrique de design-time** : `dotnet ef` ne peut pas instancier ce `DbContext` (pas de constructeur sans paramètre). `Data/DesignTimeFactory.cs` (`IDesignTimeDbContextFactory`) le résout ; elle n'est utilisée que par l'outil.

---

## 9. Limites de ces mesures

- **Une seule machine, un seul SQL Server** (Docker, 8 Go de RAM alloués) : les valeurs absolues ne se transposent pas, seuls les ordres de grandeur comptent.
- **Variance** : 4 à 10 itérations, médiane retenue. Les écarts inférieurs à environ 20 % entre EF v2, SQL pur et Dapper ne sont pas significatifs.
- **Non mesurés** : l'effet de `EF.CompileAsyncQuery` (cas 3), les procédures stockées, le comportement d'EF v2 quand le nombre de candidats de la phase 1 passe à plusieurs millions (le SQL, qui ne renvoie que 120 lignes, devrait alors l'emporter nettement : extrapolation), les cas 4 à 6 et 8 (fonctionnement connu des outils, pas résultat de ce dépôt).
- **Démarrage à froid (section 8)** : mesuré sur un modèle de 15 entités uniquement, sur une machine locale (disque et cache chauds après l'échauffement). Les valeurs absolues dépendent du processeur ; le gain est de l'ordre de 230 ms ici, pas une constante. La cause du +18 ms sur la 1re requête avec le modèle compilé n'est pas identifiée.
- **Anomalie non expliquée (section 7)** : la même requête de 2 M de lignes prend 809 ms avec `ORDER BY Id` mais 3,8 s sans tri. Hypothèse non vérifiée : un plan parallèle différent côté SQL Server. Les comparaisons `ToListAsync` / `AsAsyncEnumerable` ne sont pas touchées (même SQL), mais les temps absolus des sections 1 et 3 sont sans doute pessimistes.
- **Pistes suivantes** : une procédure stockée dans `ComplexQuery2`, une variante EF avec `Database.SqlQuery<Ligne>(Sql)` pour séparer l'effet de l'ORM de celui de la forme du SQL, et un benchmark BenchmarkDotNet sur un lookup par clé (compilé, EF normal, Dapper, ADO.NET).
